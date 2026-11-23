using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using Gathering;

namespace ArkhamUI;

// The Circle Undone's tarot reading (PickDestiny, later cycles 11-22), as the html version draws it (PickDestiny.vue): one
// tarot card per scenario, its official name and the scenario it is drawn for; a click turns a card upside down; "完成" is
// offered when half of them (rounded up) are reversed. The answer is the core's PickDestinyAnswer with each card's facing.
// Official words: pickDestiny.title / instructions, tarot.<arcana>, destiny.<scenario> (html locale); pictures: the html
// version's tarot scans (app/assets/ui/tarot).
public partial class TableScreen
{
    void DestinyDialog()
    {
        var m = model!;
        var q = ClientQuestions.Unwrap(m.State.Question);
        var drawings = J.Items(J.Get(q, "drawings")).Select(d => (Scenario: J.String(d, "scenario"), Arcana: J.String(J.Get(d, "tarot"), "arcana"), Reversed: J.String(J.Get(d, "tarot"), "facing") == "Reversed")).ToList();
        if (drawings.Count == 0) return;
        int need = (drawings.Count + 1) / 2;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(Math.Min(1500, GetViewportRect().Size.X - 160), 0) }; col.AddThemeConstantOverride("separation", 16);
        col.AddChild(Style.Title(Word("pickDestiny.title", "选择命运"), 38));
        var count = Style.Text("", 21, Style.PaperDim, Style.Kai); col.AddChild(count);
        var grid = new GridContainer { Columns = Math.Min(4, drawings.Count) }; grid.AddThemeConstantOverride("h_separation", 26); grid.AddThemeConstantOverride("v_separation", 18); col.AddChild(grid);
        Button ok = null!;
        var pics = new List<TextureRect>();
        void Refresh()
        {
            int rev = drawings.Count(d => d.Reversed);
            count.Text = Word("pickDestiny.instructions", "翻转一半的卡牌（向上取整）。") + "  已翻转 " + rev + " / " + need;
            ok.Disabled = rev != need;
            for (int i = 0; i < pics.Count; i++) pics[i].FlipV = pics[i].FlipH = drawings[i].Reversed;
        }
        for (int i = 0; i < drawings.Count; i++)
        {
            var d = drawings[i]; int idx = i;
            var cell = new VBoxContainer(); cell.AddThemeConstantOverride("separation", 6); grid.AddChild(cell);
            var name = Style.Text(DestinyName(d.Scenario), 22, Style.Paper, Style.Kai); name.HorizontalAlignment = HorizontalAlignment.Center; cell.AddChild(name);
            var pic = new TextureRect { Texture = Ui.Tex("res://assets/ui/tarot/" + TarotFile(d.Arcana)), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(180, 300), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "点击翻转（正位 / 逆位）" };
            pic.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { drawings[idx] = drawings[idx] with { Reversed = !drawings[idx].Reversed }; Refresh(); } };
            pics.Add(pic); cell.AddChild(pic);
            var arcana = Style.Text(Word("tarot." + d.Arcana, d.Arcana), 19, Style.Brass, Style.Kai); arcana.HorizontalAlignment = HorizontalAlignment.Center; cell.AddChild(arcana);
        }
        ok = Ui.Button(Word("label.done", "完成"), async () => { if (questionScrim is not null) App.I.CloseModal(questionScrim); await Do("选择命运", () => B.Answer(state!, DestinyAnswer(q, drawings.Select(x => x.Reversed).ToArray()))); await Advance(); }, 26, true);
        ok.CustomMinimumSize = new Vector2(320, 64); ok.SizeFlagsHorizontal = SizeFlags.ShrinkCenter; col.AddChild(ok);
        Refresh();
        questionScrim = App.I.Modal(Ui.Panel(col, 36), false);
        // QA: reverse the first half and answer
        QaScript.Register("destiny-ok", _ => { for (int i = 0; i < drawings.Count; i++) drawings[i] = drawings[i] with { Reversed = i < need }; Refresh(); ok.EmitSignal(BaseButton.SignalName.Pressed); });
    }

    // the core's own answer: every drawing as it came, with the facing the player chose
    static JsonElement DestinyAnswer(JsonElement q, bool[] reversed)
    {
        var rows = J.Items(J.Get(q, "drawings")).ToArray();
        return JsonSerializer.SerializeToElement(new
        {
            tag = "PickDestinyAnswer",
            contents = rows.Select((r, i) => new { scenario = J.Get(r, "scenario"), tarot = new { arcana = J.String(J.Get(r, "tarot"), "arcana"), facing = reversed[i] ? "Reversed" : "Upright" } }).ToArray(),
        });
    }

    string Word(string key, string fallback) => S.Locale.TryTemplate(key, out var t) && t.Length > 0 ? S.Locale.Plain(t) : fallback;

    // destiny.<scenario>: the core's scope may carry an apostrophe and other casing (atDeath'sDoorstep / atDeathsDoorStep)
    string DestinyName(string scenario)
    {
        if (S.Locale.TryTemplate("destiny." + scenario, out var t) && t.Length > 0) return S.Locale.Plain(t);
        string want = Regex.Replace(scenario, "[^A-Za-z]", "").ToLowerInvariant();
        foreach (var key in new[] { "theWitchingHour", "atDeathsDoorStep", "theSecretName", "theWagesOfSin", "forTheGreaterGood", "unionAndDisillusion", "inTheClutchesOfChaos", "beforeTheBlackThrone" })
            if (key.ToLowerInvariant() == want && S.Locale.TryTemplate("destiny." + key, out var t2)) return S.Locale.Plain(t2);
        return scenario;
    }

    // the html version's file for an arcana: tarot-<number>.jpg, the number being the card's own (TheFool0, TemperanceXIV)
    static string TarotFile(string arcana)
    {
        var m = Regex.Match(arcana, "(0|[IVXL]+)$");
        if (!m.Success) return "back.jpg";
        if (m.Value == "0") return "tarot-0.jpg";
        int n = 0, prev = 0;
        foreach (char ch in m.Value.Reverse()) { int v = ch switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, _ => 0 }; n += v < prev ? -v : v; prev = Math.Max(prev, v); }
        return "tarot-" + n + ".jpg";
    }
}
