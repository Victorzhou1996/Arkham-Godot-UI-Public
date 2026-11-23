using Godot;
using Gathering;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArkhamUI;

// The story the rules core actually read in this game. A chapter's intro is not fixed: the core picks its parts from the
// campaign log (The Midnight Masks reads intro 1 or intro 2, then intro 3; the Miskatonic Museum part 1 or part 2…), so
// the table keeps what was read, per chapter, and the goal popup shows that (docs/CAMPAIGN-STORY-LOGIC.md).
public partial class TableScreen
{
    Dictionary<string, List<string>>? storyRead;
    string StoryFile => System.IO.Path.Combine(App.I.DataRoot, "setups", gameId.ToString("D") + "-story.json");

    Dictionary<string, List<string>> StoryRead()
    {
        if (storyRead is not null) return storyRead;
        try { storyRead = System.IO.File.Exists(StoryFile) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(System.IO.File.ReadAllText(StoryFile)) : null; } catch { storyRead = null; }
        return storyRead ??= new();
    }

    // A Read question whose story keys are a chapter's intro ("<campaign>.<chapter>.intro…"): keep its text for that chapter.
    readonly Dictionary<string, int> readResolution = new();
    void RememberStory(TableModel m)
    {
        if (m.QuestionTag != "Read") return;
        var raw = ClientQuestions.Unwrap(m.State.Question).GetRawText();
        // the resolution the story actually went to (迷失时空: the core records "no resolution" while its defeat text sends
        // you to resolution 4) — the end screens name it beside the core's record
        foreach (Match rm in Regex.Matches(raw, @"""key"":""[A-Za-z]+\.(?<chap>[A-Za-z]+)\.resolutions\.resolution(?<n>\d)\.body"))
            if (Campaigns.All.SelectMany(c => c.Chapters).FirstOrDefault(c => c.Key == rm.Groups["chap"].Value) is { } rc)
                readResolution[rc.Code] = int.Parse(rm.Groups["n"].Value);
        var key = Regex.Match(raw, @"""key"":""(?<camp>[A-Za-z]+)\.(?<chap>[A-Za-z]+)\.intro");
        if (!key.Success) return;
        var camp = Campaigns.All.FirstOrDefault(c => c.Key == key.Groups["camp"].Value);
        var chapter = camp?.Chapters.FirstOrDefault(c => c.Key == key.Groups["chap"].Value);
        if (chapter is null) return;
        string text = m.Question.Body.Trim();
        if (text.Length == 0) return;
        var all = StoryRead();
        if (!all.TryGetValue(chapter.Code, out var parts)) all[chapter.Code] = parts = new();
        if (parts.Contains(text)) return;
        parts.Add(text);
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StoryFile)!);
            System.IO.File.WriteAllText(StoryFile, JsonSerializer.Serialize(all));
        }
        catch (Exception e) { GD.PushWarning("story: " + e.Message); }
    }

    // "本局剧情线：引言 2 → 引言 3": the chapter's numbered intro parts (locale <campaign>.<chapter>.introN) read in this game,
    // in reading order. Null for a chapter with a single intro.
    Control? StoryLine(string code)
    {
        code = CoreCatalog.Normalize(code);
        var camp = Campaigns.ForScenario(code); var ch = camp.Find(code); if (ch is null) return null;
        var parts = new List<int>();
        for (int n = 1; n <= 9; n++) if (S.Locale.TryTemplate(camp.Key + "." + ch.Key + ".intro" + n, out _)) parts.Add(n);
        if (parts.Count < 2 || !StoryRead().TryGetValue(code, out var read) || read.Count == 0) return null;
        var order = new List<int>();
        foreach (var text in read)
            foreach (Match mt in Regex.Matches(text, @"引言\s*(\d)"))
                if (int.TryParse(mt.Groups[1].Value, out int n) && parts.Contains(n) && !order.Contains(n) && Regex.IsMatch(text.TrimStart(), @"^[_*]*引言\s*" + n)) order.Add(n);
        if (order.Count == 0) return null;
        var l = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(460, 0) };
        l.AddThemeFontSizeOverride("normal_font_size", 18); if (Style.Kai is not null) l.AddThemeFontOverride("normal_font", Style.Kai);
        l.AddThemeColorOverride("default_color", Style.Amber);
        // only what this game triggered (Yunke 11-20: the parts not read are left out, no struck-through list)
        l.Text = "本局剧情线：" + string.Join(" → ", order.Select(n => "引言 " + n));
        return l;
    }

    // The intro read for this chapter in this game, its parts in order; the lines the core marked as not applying and
    // the "read on" pointers between parts are left out. Empty when the intro was not read here (a game opened mid-way).
    string ChapterIntro(string code)
    {
        if (!StoryRead().TryGetValue(CoreCatalog.Normalize(code), out var parts)) return "";
        var lines = parts.SelectMany(p => p.Split('\n')).Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("<dim>"))
            // the part titles ("冒险I: 聚集于此", "第二部分：午夜假面") repeat the chapter name: the popup has its own heading
            .Where(l => !(l.Length <= 24 && Campaigns.ForScenario(code).Find(CoreCatalog.Normalize(code)) is { } ch && l.Contains(ch.Name)))
            // reading-order instructions ("继续 引言 3.", "检查剧本日志，如果……阅读 引言 1.", "否则，跳转至 引言 2.")
            .Where(l => !Regex.IsMatch(l, @"^(继续|否则|检查(剧本|战役)日志).{0,60}(引言|部分|结局)\s*\d"))
            .Distinct();
        return string.Join("\n", lines);
    }
}
