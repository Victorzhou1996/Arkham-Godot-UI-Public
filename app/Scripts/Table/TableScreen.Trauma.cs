using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using Gathering;

namespace ArkhamUI;

// Trauma bought with the deck (危险处境: "在你购买危险处境时，承受总共2点肉体和/或精神创伤"), asked before the campaign starts.
// As in the 0712 web version (Yunke 11-22): each investigator's small card with a heart (肉体创伤) and a brain (精神创伤) under it,
// left click adds a point, right click takes one back, one 提交 — nothing else (Yunke 11-22: no extra text). One investigator
// at a time: when several decks hold such a card the core asks them one after another, and each gets this window in turn.
public partial class TableScreen
{
    static readonly Regex TraumaAsk = new(@"^Suffer (\d+) total physical and/or mental trauma$");

    static bool IsTraumaAsk(JsonElement question, out int total)
    {
        total = 0;
        if (J.Tag(question) != "ChooseAmounts") return false;
        var mt = TraumaAsk.Match(J.String(question, "label"));
        return mt.Success && int.TryParse(mt.Groups[1].Value, out total);
    }

    void TraumaDialog(int total)
    {
        var m = model!; var st = state!;
        // only the investigator asked now (Yunke 11-22: each one splits their own in turn; the core asks the next seat after)
        var who = new List<(Guid Player, Entity Inv)>();
        if (m.Investigators.FirstOrDefault(x => J.String(x.Raw, "playerId") == st.Player.ToString("D")) is { } me) who.Add((st.Player, me));
        var phys = who.ToDictionary(w => w.Player, _ => 0); var ment = who.ToDictionary(w => w.Player, _ => 0);

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(Math.Max(680, who.Count * 230), 0) }; col.AddThemeConstantOverride("separation", 16);
        col.AddChild(Style.Title("承受总共" + total + "点肉体和/或精神创伤", 30));
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 40); col.AddChild(row);
        var refreshers = new List<Action>();
        Button ok = null!;
        void RefreshAll() { foreach (var r in refreshers) r(); ok.Disabled = who.Any(w => phys[w.Player] + ment[w.Player] != total); }

        foreach (var (player, inv) in who)
        {
            var c = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; c.AddThemeConstantOverride("separation", 10); row.AddChild(c);
            var cv = new CardView { Code = inv.Code, Picture = Ui.Mini(inv.Code), CustomMinimumSize = new Vector2(150, 232), ShowCost = false, HoverZoom = false, MouseFilter = MouseFilterEnum.Ignore };
            cv.SizeFlagsHorizontal = SizeFlags.ShrinkCenter; c.AddChild(cv);
            var tokens = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; tokens.AddThemeConstantOverride("separation", 18); c.AddChild(tokens);
            foreach (var (kind, name, map) in new[] { ("health", "肉体创伤", phys), ("horror", "精神创伤", ment) })
            {
                var hold = new Control { CustomMinimumSize = new Vector2(80, 80), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = name + "：左键 +1，右键 −1" };
                tokens.AddChild(hold);
                refreshers.Add(() =>
                {
                    foreach (var ch in hold.GetChildren()) ch.QueueFree();
                    var tok = Ui.TokenNumber(kind, map[player], 80); tok.MouseFilter = MouseFilterEnum.Ignore; hold.AddChild(tok);
                    tok.Position = new Vector2((hold.CustomMinimumSize.X - tok.CustomMinimumSize.X) / 2, 0);
                });
                hold.GuiInput += e =>
                {
                    if (e is not InputEventMouseButton { Pressed: true } mb) return;
                    if (mb.ButtonIndex == MouseButton.Left && phys[player] + ment[player] < total) map[player]++;
                    else if (mb.ButtonIndex == MouseButton.Right && map[player] > 0) map[player]--;
                    else return;
                    hold.AcceptEvent(); RefreshAll();
                };
            }
        }
        ok = Ui.Button("提交", async () =>
        {
            if (questionScrim is not null) App.I.CloseModal(questionScrim);
            var q = ClientQuestions.Unwrap(state!.Question);
            if (IsTraumaAsk(q, out _) && who.Count > 0)
            {
                var player = who[0].Player;
                var values = ClientQuestions.AmountRows(q).ToDictionary(r => r.Id, r => r.Label == "$physical" ? phys[player] : r.Label == "$mental" ? ment[player] : r.Minimum);
                GD.Print("QA_TRAUMA " + who[0].Inv.Code + " physical=" + phys[player] + " mental=" + ment[player]);
                await Do("承受创伤", () => B.Amounts(state!, values));
            }
            await Advance();
        }, 26, true);
        ok.CustomMinimumSize = new Vector2(320, 64); ok.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        col.AddChild(ok);
        RefreshAll();
        QaScript.Register("trauma", a =>
        {
            // QA: trauma:<physical>,<mental> for the investigator in the window, then 提交
            var p = a.Split(','); foreach (var w in who) { phys[w.Player] = int.Parse(p[0]); ment[w.Player] = int.Parse(p[1]); }
            RefreshAll(); ok.EmitSignal(BaseButton.SignalName.Pressed);
        });
        questionScrim = App.I.Modal(Ui.Panel(col, 36), false);
    }
}
