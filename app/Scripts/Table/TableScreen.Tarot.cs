using System.Text.Json;
using Godot;
using Gathering;

namespace ArkhamUI;

// 使用塔罗牌解牌 (the web version's tarot readings, PrepareScreen.Options; Yunke 11-23): at each scenario's start the lead
// picks how to read (Game/Runner.hs PerformTarotReading: 混乱 one card, 平衡 one upright and one reversed, 选择 three cards of
// which two are turned); the cards drawn lie on the table for the whole scenario. As the html version draws them:
//   the reading type: a small dialog with the core's own question ("选择塔罗解读类型") and its three labels;
//   选择: the three focused cards as pictures (Question.vue tarotLabels), a click turns one ("选择两张卡牌旋转");
//   on the table: each card beside the act and the agenda (Scenario.vue .tarot-cards), upside down when reversed; hovering
//   shows it large (CardOverlay .tarot), and a click uses its ability when the core offers one (TarotSource).
// Pictures: the html version's Chinese tarot scans (app/assets/ui/tarot, TableScreen.Destiny TarotFile).
public partial class TableScreen
{
    static string RawLabel(TableModel m) => J.Tag(m.State.Question) == "QuestionLabel" ? J.String(m.State.Question, "label") : "";
    static bool IsTarotReadingChoice(TableModel m) => RawLabel(m) == "$label.chooseTarotReadingType";
    static bool HasTarotLabels(TableModel m) => m.Question.Choices.Any(c => c.Tag == "TarotLabel");

    void TarotReadingDialog()
    {
        var m = model!;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) }; col.AddThemeConstantOverride("separation", 20);
        var t = Style.Text(Word("label.chooseTarotReadingType", "选择塔罗解读类型"), 34, Style.HoverText, Style.Kai); t.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(t);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 18); col.AddChild(row);
        foreach (var c in m.Question.Choices.Where(c => c.Enabled))
        {
            var cc = c;
            var b = Ui.Button(c.Title, () => _ = Choose2Async(cc), 26, true); b.CustomMinimumSize = new Vector2(170, 64); row.AddChild(b);
        }
        // the back of the deck under the choice: the reading is about to be dealt
        var back = new TextureRect { Texture = Ui.Tex("res://assets/ui/tarot/back.jpg"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(0, 220), MouseFilter = MouseFilterEnum.Ignore };
        col.AddChild(back);
        questionScrim = App.I.Modal(Ui.Panel(col, 34), false);
    }

    // 选择: the focused cards; those the core still offers can be turned
    void TarotPickDialog()
    {
        var m = model!;
        var offered = m.Question.Choices.Where(c => c.Tag == "TarotLabel" && c.Enabled).ToList();
        var shown = J.Items(J.Get(m.Game, "focusedTarotCards")).Select(Card).ToList();
        if (shown.Count == 0) shown = offered.Select(c => Card(J.Get(c.Raw, "tarotCard"))).ToList();
        var now = TableTarots(m).ToDictionary(x => x.Arcana, x => x.Reversed);   // turned ones already lie reversed in the scenario
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 18);
        var title = RawLabel(m).TrimStart('$');
        var t = Style.Text(Word(title, "选择两张卡牌旋转"), 34, Style.HoverText, Style.Kai); t.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(t);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 30); col.AddChild(row);
        foreach (var card in shown)
        {
            var choice = offered.FirstOrDefault(c => J.String(J.Get(c.Raw, "tarotCard"), "arcana") == card.Arcana);
            bool reversed = now.TryGetValue(card.Arcana, out var r) ? r : card.Reversed;
            var cell = new VBoxContainer(); cell.AddThemeConstantOverride("separation", 8); row.AddChild(cell);
            var pic = TarotPicture(card.Arcana, reversed, new Vector2(220, 380));
            if (choice is not null)
            {
                pic.MouseFilter = MouseFilterEnum.Stop; pic.MouseDefaultCursorShape = CursorShape.PointingHand;
                var cc = choice;
                pic.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _ = Choose2Async(cc); };
                TarotBreathe(pic);
            }
            else pic.Modulate = new Color(1, 1, 1, 0.75f);
            cell.AddChild(pic);
            var name = Style.Text(Word("tarot." + card.Arcana, card.Arcana) + (reversed ? " · 逆位" : ""), 21, choice is null ? Style.PaperDim : Style.Brass, Style.Kai); name.HorizontalAlignment = HorizontalAlignment.Center; cell.AddChild(name);
        }
        questionScrim = App.I.Modal(Ui.Panel(col, 34), false);
        QaScript.Register("tarot-pick", a => { if (int.TryParse(a, out int i) && i >= 0 && i < offered.Count) _ = Choose2Async(offered[i]); });
    }

    // what can be used breathes softly (memory: effects sparingly — a breathing glow, no ripple)
    static void TarotBreathe(Control c)
    {
        var tw = c.CreateTween().SetLoops();
        tw.TweenProperty(c, "modulate", new Color(1.25f, 1.15f, 0.95f), 0.9f).SetTrans(Tween.TransitionType.Sine);
        tw.TweenProperty(c, "modulate", Colors.White, 0.9f).SetTrans(Tween.TransitionType.Sine);
    }

    static (string Arcana, bool Reversed) Card(JsonElement c) => (J.String(c, "arcana"), J.String(c, "facing") == "Reversed");

    // the scenario's tarot cards: GlobalTarot ones, and those placed on an investigator (scope InvestigatorTarot)
    static IEnumerable<(string Arcana, bool Reversed, string Owner)> TableTarots(TableModel m)
    {
        var raw = J.Get(m.Scenario, "tarotCards");
        if (raw.ValueKind == JsonValueKind.Undefined) raw = J.Get(m.Scenario, "scenarioTarotCards");
        // Map TarotCardScope [TarotCard]: an array of [scope, cards] pairs, or an object keyed by the scope
        if (raw.ValueKind == JsonValueKind.Array)
            foreach (var pair in raw.EnumerateArray())
            {
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
                {
                    string owner = J.Tag(pair[0]) == "InvestigatorTarot" ? J.String(pair[0], "contents") : "";
                    foreach (var c in J.Items(pair[1])) yield return (J.String(c, "arcana"), J.String(c, "facing") == "Reversed", owner);
                }
                else if (pair.ValueKind == JsonValueKind.Object)   // already flat: {scope, arcana, facing}
                    yield return (J.String(pair, "arcana"), J.String(pair, "facing") == "Reversed", J.Tag(J.Get(pair, "scope")) == "InvestigatorTarot" ? J.String(J.Get(pair, "scope"), "contents") : "");
            }
        else if (raw.ValueKind == JsonValueKind.Object)
            foreach (var p in raw.EnumerateObject())
                foreach (var c in J.Items(p.Value)) yield return (J.String(c, "arcana"), J.String(c, "facing") == "Reversed", p.Name.Contains("InvestigatorTarot") ? p.Name : "");
    }

    static TextureRect TarotPicture(string arcana, bool reversed, Vector2 size) => new()
    {
        Texture = Ui.Tex("res://assets/ui/tarot/" + TarotFile(arcana)), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        CustomMinimumSize = size, FlipH = reversed, FlipV = reversed, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmaps,
    };

    // beside the act and the agenda: one small card per tarot in play
    void TarotBeside(HBoxContainer plates)
    {
        var m = model!;
        if (App.I.QaRunning) { var raw = J.Get(m.Scenario, "tarotCards"); GD.Print("QA_TAROTS " + string.Join(",", TableTarots(m).Select(x => x.Arcana + (x.Reversed ? "/R" : "") + (x.Owner.Length > 0 ? "@" + x.Owner : ""))) + " raw=" + (raw.ValueKind == JsonValueKind.Undefined ? "(none)" : raw.GetRawText()[..Math.Min(300, raw.GetRawText().Length)])); }
        foreach (var (arcana, reversed, owner) in TableTarots(m))
        {
            var pic = TarotPicture(arcana, reversed, new Vector2(46, 80));
            pic.SizeFlagsVertical = SizeFlags.ShrinkCenter; pic.MouseFilter = MouseFilterEnum.Stop;
            string who = owner.Length > 0 && m.Investigators.FirstOrDefault(i => i.Id == owner || "c" + i.Code == owner || i.Code == owner) is { } inv ? m.Name(inv) + " · " : "";
            string name = who + Word("tarot." + arcana, arcana) + (reversed ? " · 逆位" : " · 正位");
            // its ability, when the core offers one now (the web table: a click on the tarot card)
            var use = m.Question.Choices.FirstOrDefault(c => c.Enabled && J.Tag(J.Get(J.Get(c.Raw, "ability"), "source")) == "TarotSource" && J.String(J.Get(J.Get(J.Get(c.Raw, "ability"), "source"), "contents"), "arcana") == arcana);
            if (use is not null)
            {
                pic.MouseDefaultCursorShape = CursorShape.PointingHand; TarotBreathe(pic);
                pic.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _ = Choose2Async(use); };
            }
            Control? big = null;
            pic.MouseEntered += () =>
            {
                big?.QueueFree();
                var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 200 }; box.AddThemeConstantOverride("separation", 6);
                box.AddChild(TarotPicture(arcana, reversed, new Vector2(300, 520)));
                var l = Style.Text(name, 22, Style.HoverText, Style.Kai); l.HorizontalAlignment = HorizontalAlignment.Center; box.AddChild(l);
                App.I.Overlay.AddChild(box);
                var at = pic.GetGlobalRect();
                box.Position = new Vector2(Math.Clamp(at.Position.X - 127, 10, GetViewportRect().Size.X - 320), at.End.Y + 10);
                big = box;
            };
            pic.MouseExited += () => { big?.QueueFree(); big = null; };
            pic.TreeExiting += () => { if (big is not null && IsInstanceValid(big)) big.QueueFree(); };
            plates.AddChild(pic);
        }
    }
}
