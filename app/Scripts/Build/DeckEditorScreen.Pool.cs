using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The card pool's cards, made only near the view (Yunke 11-21: "滚动浏览的卡牌界面会随着滚动越来越卡 … 同一时间只加载一部分
// 卡牌"). Every card of the filtered list has its place in the grid (an empty slot of the card's size and its count plaque);
// the card itself (a CardView with its printed picture) is made when its slot comes within about a screen of the view, a few
// per frame, nearest first, and is freed again when it is several screens away — so the number of live cards stays small
// however far one scrolls, and their pictures can leave the picture cache (ArtStore keeps a bounded LRU).
//
// Dragging (Yunke 11-21: "从已有列表里拖出卡牌的逻辑没做，拖入卡牌时，卡牌要在进入卡组列表的时候变为条形"): what follows the
// pointer is a ghost on the screen — the card while it is over the pool, the deck list's row (条形) once it is over the deck
// list. A pool card dropped on the deck list goes in; a deck row dropped outside the deck list comes out.
public partial class DeckEditorScreen
{
    readonly List<(string Code, JsonElement Card, Control Slot)> slots = new();
    readonly List<(string Code, JsonElement Card, Control Slot)> realizeQueue = new();
    int cullIn;

    CardView? PoolCard(int i) => i >= 0 && i < slots.Count && IsInstanceValid(slots[i].Slot) && slots[i].Slot.GetChildCount() > 0 ? slots[i].Slot.GetChild(0) as CardView : null;

    // ───────── the pictures ahead of the cards (Yunke 11-22: "小卡模式滚动时会有点卡顿，可能需要一些预加载"): each card's
    // picture is read on worker threads while its slot is still a few screens away, and a card is made only once its picture
    // is in (or after a short wait), so making it costs no file reading on the main thread.
    readonly HashSet<string> warm = new(), warming = new();
    readonly Dictionary<string, int> waited = new();
    const double RealizeBudgetMs = 6;

    IEnumerable<string> PicturePaths(string code, out bool store)
    {
        string n = CoreCatalog.Normalize(code); store = false;
        var list = new List<string>();
        if (poolMode != PoolMode.Large && Ui.ArtPath(n) is { } art) { list.Add(art); return list; }
        foreach (var c in new[] { n, n.TrimEnd('a', 'b') })
            if (ResourceLoader.Exists("res://assets/cards/" + c + ".webp")) { list.Add("res://assets/cards/" + c + ".webp"); return list; }
        store = true; return list;
    }

    void Warm(string code)
    {
        if (code == RandomWeakness || warm.Contains(code) || !warming.Add(code)) return;
        _ = WarmAsync(code);
    }
    async Task WarmAsync(string code)
    {
        try
        {
            var paths = PicturePaths(code, out bool store).ToList();
            if (paths.Count > 0) await Ui.Preload(paths);
            if (store) { string n = CoreCatalog.Normalize(code); await ArtStore.Prefetch(ArtStore.Has(n) ? n : n.TrimEnd('a', 'b')); }
        }
        catch (Exception e) { GD.PushWarning("pool warm " + code + ": " + e.Message); }
        warming.Remove(code); warm.Add(code);
    }

    public override void _Process(double delta)
    {
        if (cullIn > 0 && --cullIn == 0) Cull();
        long frame0 = System.Diagnostics.Stopwatch.GetTimestamp(); int made = 0;
        for (int k = 0; k < realizeQueue.Count; )
        {
            if (made > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(frame0).TotalMilliseconds > RealizeBudgetMs) break;
            var (code, card, slot) = realizeQueue[k];
            if (!IsInstanceValid(slot) || slot.GetChildCount() > 0) { realizeQueue.RemoveAt(k); continue; }
            // its picture still coming: the next one first (but not for long: after about half a second it is made anyway)
            if (code != RandomWeakness && !warm.Contains(code))
            {
                Warm(code);
                waited[code] = waited.GetValueOrDefault(code) + 1;
                if (waited[code] < 30) { k++; continue; }
            }
            realizeQueue.RemoveAt(k); made++;
            if (!App.I.QaRunning) { Realize(code, card, slot); continue; }
            double l0 = Ui.LoadMs; long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); Realize(code, card, slot);
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (ms > 6) GD.Print($"QA_REALIZE {code} ms={ms:F1} load={Ui.LoadMs - l0:F1} warm={warm.Contains(code)}");
        }
        qaProcMs += System.Diagnostics.Stopwatch.GetElapsedTime(frame0).TotalMilliseconds;
        if (dragCode is not null)
        {
            bool over = deckPanel.GetGlobalRect().HasPoint(dragPointer);
            if (over != dragOver) { dragOver = over; Ghost(); }
            MoveGhost();
        }
    }

    // which cards to make (near the view, nearest first) and which to free (far from it)
    public double qaCullMs, qaProcMs;
    void Cull()
    {
        long qc = System.Diagnostics.Stopwatch.GetTimestamp(); try { CullNow(); } finally { qaCullMs += System.Diagnostics.Stopwatch.GetElapsedTime(qc).TotalMilliseconds; }
    }
    void CullNow()
    {
        if (!IsInstanceValid(libScroll) || slots.Count == 0) return;
        var view = libScroll.GetGlobalRect(); float h = Math.Max(200, view.Size.Y);
        var near = new Rect2(view.Position.X - 50, view.Position.Y - h * 0.75f, view.Size.X + 100, h * 2.5f);
        var far = new Rect2(view.Position.X - 50, view.Position.Y - h * 2.5f, view.Size.X + 100, h * 6f);
        var seen = new Rect2(view.Position.X - 50, view.Position.Y - 260, view.Size.X + 100, view.Size.Y + 520);   // on screen, with a margin
        var ahead = new Rect2(view.Position.X - 50, view.Position.Y - h * 1.5f, view.Size.X + 100, h * 5.5f);   // pictures read early
        float mid = view.GetCenter().Y;
        realizeQueue.Clear();
        foreach (var (code, card, slot) in slots)
        {
            if (!IsInstanceValid(slot)) continue;
            var r = slot.GetGlobalRect(); bool has = slot.GetChildCount() > 0;
            // a made card out of sight is hidden: the cards kept for scrolling back cost nothing to draw or move (Yunke 11-22)
            if (has && slot.GetChild(0) is CanvasItem ci) { bool vis = r.Intersects(seen); if (ci.Visible != vis) ci.Visible = vis; }
            if (!has && r.Intersects(ahead)) Warm(code);
            if (!has && r.Intersects(near)) realizeQueue.Add((code, card, slot));
            else if (has && !r.Intersects(far) && dragCode != code) Unrealize(code, slot);
        }
        realizeQueue.Sort((a, b) => Math.Abs(a.Slot.GetGlobalRect().GetCenter().Y - mid).CompareTo(Math.Abs(b.Slot.GetGlobalRect().GetCenter().Y - mid)));
    }

    void Realize(string code, JsonElement card, Control slot)
    {
        if (poolMode == PoolMode.Text) { RealizeThumb(code, card, slot); return; }   // a text row's thumbnail (DeckEditorScreen.Modes.cs)
        var cv = poolMode == PoolMode.Small ? SmallPoolCard(code, card)   // our own small card (DeckEditorScreen.Modes.cs)
            : new CardView { Code = code, CustomMinimumSize = CardSize, Size = CardSize, Actionable = false, Draggable = true, HoverZoom = false, ZoomHeight = 640, Redrawn = false };
        cv.OnClick = () => _ = Add(code, cv.GetGlobalRect());
        cv.DragChanged = on => { if (on) { cv.Modulate = new Color(1, 1, 1, 0); StartDrag(code, card, fromDeck: false); } else EndDrag(); };
        cv.OnDragRelease = pos =>
        {
            if (deckPanel.GetGlobalRect().Grow(10).HasPoint(pos))
            {
                _ = Add(code, null);
                Callable.From(() => Remake(code)).CallDeferred();   // the card in its place again (its own drop tween fades it out where it was let go)
                return true;
            }
            RefreshPlaque(code);   // back into its place, seen again
            return false;
        };
        cv.GuiInput += e =>
        {
            if (e is InputEventMouseMotion mm) dragPointer = mm.GlobalPosition;   // where the pointer is, from the events the card gets while held
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) { cv.AcceptEvent(); LibraryCardDialog(code); }
        };
        cv.PivotOffset = CardSize / 2;
        cv.MouseEntered += () => { if (!Calm && dragCode is null) cv.CreateTween().TweenProperty(cv, "scale", new Vector2(1.04f, 1.04f), 0.12f); };
        cv.MouseExited += () => { if (!Calm && dragCode is null) cv.CreateTween().TweenProperty(cv, "scale", Vector2.One, 0.12f); };
        slot.AddChild(cv); pageCards[code] = cv; if (!cv.Redrawn) RoundCorners(cv); else SmallPips(cv, card);
        RefreshPlaque(code);
    }

    void Unrealize(string code, Control slot)
    {
        foreach (var ch in slot.GetChildren()) { if (pageCards.TryGetValue(code, out var cv) && cv == ch) pageCards.Remove(code); slot.RemoveChild(ch); ch.QueueFree(); }
    }

    void Remake(string code)
    {
        foreach (var (c, card, slot) in slots)
            if (c == code && IsInstanceValid(slot)) { Unrealize(c, slot); Realize(c, card, slot); return; }
    }

    void RefreshPlaque(string code) { if (plaques.TryGetValue(code, out var p)) Plaque(code, p.Card, p.Count, p.Plaque); }

    // ───────── the drag ghost
    string? dragCode; JsonElement dragCard; bool dragFromDeck, dragOver; Control? dragGhost; Vector2 dragPointer;

    void StartDrag(string code, JsonElement card, bool fromDeck)
    {
        dragCode = code; dragCard = card; dragFromDeck = fromDeck;
        dragOver = deckPanel.GetGlobalRect().HasPoint(dragPointer);
        Ghost();
        if (App.I.QaRunning) GD.Print("QA_DRAG start " + code + (fromDeck ? " deck" : " pool"));
    }

    // the card over the pool, the deck list's row over the deck list
    void Ghost()
    {
        if (dragGhost is not null && IsInstanceValid(dragGhost)) dragGhost.QueueFree();
        Control g;
        if (dragOver)
        {
            g = Tile(dragCard, true, ghost: true);
            float w = Math.Max(280, deckList.Size.X);
            g.CustomMinimumSize = new Vector2(w, 48); g.Size = new Vector2(w, 48);
        }
        else
        {
            var size = dragFromDeck ? CardSize * 0.62f : CardSize;
            g = !dragFromDeck && poolMode == PoolMode.Small ? SmallPoolCard(dragCode!, dragCard)
                : new CardView { Code = dragCode!, CustomMinimumSize = size, Size = size, HoverZoom = false, Redrawn = false, ShowCost = false, MouseFilter = MouseFilterEnum.Ignore };
        }
        g.ZIndex = 170; g.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(g);
        if (g is CardView cv && !cv.Redrawn) RoundCorners(cv);
        g.PivotOffset = g.Size / 2;
        if (!Calm) { g.Modulate = new Color(1.25f, 1.15f, 1f, 0); g.Scale = new Vector2(0.92f, 0.92f); var tw = g.CreateTween().SetParallel(); tw.TweenProperty(g, "modulate", new Color(1.1f, 1.05f, 1f, 1f), 0.1f); tw.TweenProperty(g, "scale", Vector2.One, 0.1f); }
        dragGhost = g; MoveGhost();
        if (App.I.QaRunning) GD.Print("QA_DRAG ghost " + (dragOver ? "strip" : "card") + " " + g.Size);
    }

    void MoveGhost() { if (dragGhost is not null && IsInstanceValid(dragGhost)) dragGhost.GlobalPosition = dragPointer - dragGhost.Size / 2; }

    void EndDrag()
    {
        if (dragGhost is not null && IsInstanceValid(dragGhost)) dragGhost.QueueFree();
        dragGhost = null; dragCode = null;
    }
}
