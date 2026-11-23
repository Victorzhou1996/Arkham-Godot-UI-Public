using System.Text.Json;
using Gathering;

namespace ArkhamUI;

// The campaigns this front end presents. Names are the official ones (html version locale: campaignNames, scenarioNames);
// Key is the campaign's locale section (nightOfTheZealot.*, theDunwichLegacy.*), each chapter's Key its scenario section.
public sealed record Chapter(string Code, string Key, string Name);
public sealed record CampaignDef(string Id, string Key, string Name, string Cycle, Chapter[] Chapters, string[] Investigators, string[] IntroKeys)
{
    static readonly string[] Numerals = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十", "十一", "十二", "十三", "十四", "十五" };
    public Chapter? Find(string code) => Chapters.FirstOrDefault(c => c.Code == code);
    public int Index(string code) => Array.FindIndex(Chapters, c => c.Code == code);
    // "第二章 · 午夜假面"
    public string Label(string code) => Index(code) is var i && i >= 0 ? "第" + Numerals[i] + "章 · " + Chapters[i].Name : code;
    public string ShortLabel(string code) => Index(code) is var i && i >= 0 ? "第" + Numerals[i] + "关 · " + Chapters[i].Name : code;
    public Chapter Last => Chapters[^1];
    // The route as the prepare screen shows it (official step titles from the html locale) and a line on how it runs.
    // A campaign is always played from its start, one chapter after another: the rules core chooses each intro and
    // ending from the campaign log (docs/CAMPAIGN-STORY-LOGIC.md).
    public string PrologueName { get; init; } = "序章";
    public string EpilogueName { get; init; } = "";
    public string RouteNote { get; init; } = "";
    // Later cycles, return-to, side stories and fan-made scenarios (11-22, app/assets/data/campaigns-catalog.json):
    // Kind "campaign" | "return" | "side" | "fanCampaign" | "fanScenario". A side or fan scenario is started on its own
    // (Standalone = its scenario id); a return-to campaign shares its original's entrance (Return / ReturnOf).
    public string Kind { get; init; } = "campaign";
    public bool Beta { get; init; }
    public string Cover { get; init; } = "";
    public CampaignDef? Return { get; set; }
    public string ReturnOf { get; init; } = "";
    public string Standalone { get; init; } = "";
    public string NeedsInvestigator { get; init; } = "";
    public string[] Difficulties { get; init; } = { "Easy", "Standard", "Hard", "Expert" };
    public Dictionary<string, string[]> Bags { get; init; } = new();
    // whether the rules core opens it (StartsProbe): false with the core's reason
    public bool Starts { get; init; } = true;
    public string Reason { get; init; } = "";
    public string Designer { get; init; } = "";
    // the rules core opens it, but this front end has no screen yet for one of its questions (11-22 sweep)
    public string UiGap { get; init; } = "";
    public bool IsScenario => Standalone.Length > 0;
}

public static class Campaigns
{
    static readonly CampaignDef zealot0 = new("01", "nightOfTheZealot", "狂热之夜", "基础游戏",
        new Chapter[] { new("01104", "theGathering", "聚集于此"), new("01120", "theMidnightMasks", "午夜假面"), new("01142", "theDevourerBelow", "地底噬魔") },
        new[] { "01001", "01002", "01003", "01004", "01005" }, Array.Empty<string>())
    {
        RouteNote = "三章依次进行，不能单独选关。每章的引言随前一章的结局而变（例如房子是否被烧毁、丽塔的去向、多少异教徒逃走），结局写进战役日志，影响后面的章节。",
    };

    // The Dunwich Legacy: the first two chapters may be played in either order (the core asks).
    static readonly CampaignDef dunwich0 = new("02", "theDunwichLegacy", "敦威治遗产", "第一循环",
        new Chapter[] {
            new("02041", "extracurricularActivity", "课外活动"), new("02062", "theHouseAlwaysWins", "百赌百胜"),
            new("02118", "theMiskatonicMuseum", "博物馆之夜"), new("02159", "theEssexCountyExpress", "疯狂列车"),
            new("02195", "bloodOnTheAltar", "血色祭坛"), new("02236", "undimensionedAndUnseen", "无人知晓"),
            new("02274", "whereDoomAwaits", "毁灭之地"), new("02311", "lostInTimeAndSpace", "迷失时空"),
        },
        new[] { "02001", "02002", "02003", "02004", "02005" }, new[] { "theDunwichLegacy.prologue.body" })
    {
        PrologueName = "引导故事", EpilogueName = "尾声故事",
        RouteNote = "八章依次进行，不能单独选关。引导故事里决定先打课外活动还是百赌百胜，两章打完后进入幕间故事 I；血色祭坛有结局时进入幕间故事 II。每章的引言和设置随战役日志而变。",
    };

    // The Path to Carcosa (cycle 2, 11-20): eight chapters in order; official step titles from the html locale
    // (thePathToCarcosa.prologue.title 序幕, interludes 现实之影 / 失魂落魄, epilogue.title 尾声)
    static readonly CampaignDef carcosa0 = new("03", "thePathToCarcosa", "卡尔克萨之路", "第二循环",
        new Chapter[] {
            new("03043", "curtainCall", "谢幕"), new("03061", "theLastKing", "最后的王者"),
            new("03120", "echoesOfThePast", "往事回声"), new("03159", "theUnspeakableOath", "邪秽誓约"),
            new("03200", "aPhantomOfTruth", "真相幻影"), new("03240", "thePallidMask", "苍白面具"),
            new("03274", "blackStarsRise", "黑星升起"), new("03316", "dimCarcosa", "卡城幽影"),
        },
        new[] { "03001", "03002", "03003", "03004", "03005", "03006" }, new[] { "thePathToCarcosa.prologue.body" })
    {
        PrologueName = "序幕", EpilogueName = "尾声",
        RouteNote = "八章依次进行，不能单独选关。第二章之后是幕间故事 I（现实之影），第四章之后是幕间故事 II（失魂落魄）。每章的引言和设置随战役日志而变；有的章节结局会改变后面章节的设置。",
    };

    // every campaign and scenario this front end offers, in picker order; return-to versions are listed too (ById / ForGame)
    // but reached through their original's entrance
    public static readonly CampaignDef[] All = Build();
    // the first three, as the catalog completed them (return-to version, chaos bags…)
    public static CampaignDef Zealot => All.FirstOrDefault(c => c.Id == "01") ?? zealot0;
    public static CampaignDef Dunwich => All.FirstOrDefault(c => c.Id == "02") ?? dunwich0;
    public static CampaignDef Carcosa => All.FirstOrDefault(c => c.Id == "03") ?? carcosa0;
    public static IEnumerable<CampaignDef> Official => All.Where(c => c.Kind == "campaign");
    public static IEnumerable<CampaignDef> Side => All.Where(c => c.Kind == "side");
    public static IEnumerable<CampaignDef> Fan => All.Where(c => c.Kind is "fanCampaign" or "fanScenario");

    static Dictionary<string, string[]> BagsOf(JsonElement b) => J.Props(b).ToDictionary(p => p.Name, p => J.Items(p.Value).Select(x => x.GetString() ?? "").ToArray());
    static bool Yes(JsonElement e, string k) => J.Get(e, k).ValueKind == JsonValueKind.True;
    static bool NotNo(JsonElement e, string k) => J.Get(e, k).ValueKind != JsonValueKind.False;

    // what the front end cannot show yet, by id (offscreen sweep 11-22, docs/LATER-CYCLES-20261122.md)
    static Dictionary<string, string> UiGaps => new()   // a property: Build() runs from the static initializer of All, above it
    {
        ["90054"] = "开局要组建“灵魂牌组”，本前端还没有这个界面",
    };

    static CampaignDef[] Build()
    {
        var list = new List<CampaignDef>();
        JsonElement cat = default;
        try
        {
            const string path = "res://assets/data/campaigns-catalog.json";
            string text = Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : "";
            if (text.Length > 0) { using var doc = JsonDocument.Parse(text); cat = doc.RootElement.Clone(); }
        }
        catch (Exception e) { Godot.GD.PushWarning("战役目录读取失败：" + e.Message); }
        var four = new[] { "Easy", "Standard", "Hard", "Expert" };
        foreach (var c in J.Items(J.Get(cat, "campaigns")))
        {
            string id = J.String(c, "id");
            var chapters = J.Items(J.Get(c, "chapters")).Select(ch => new Chapter(J.String(ch, "code"), J.String(ch, "key"), J.String(ch, "name"))).ToArray();
            CampaignDef def = id switch
            {
                "01" => zealot0, "02" => dunwich0, "03" => carcosa0,
                _ => new CampaignDef(id, J.String(c, "key"), J.String(c, "name"), J.String(c, "cycle"), chapters,
                    J.Items(J.Get(c, "investigators")).Select(x => x.GetString() ?? "").OrderBy(x => x).ToArray(), Array.Empty<string>())
                {
                    PrologueName = J.String(c, "prologue"), EpilogueName = J.String(c, "epilogue"),
                    RouteNote = chapters.Length + " 章依次进行，不能单独选关。每章的引言、结局和幕间故事由规则核心按战役日志给出。",
                },
            };
            def = def with
            {
                Kind = "campaign", Cover = id, Beta = Yes(c, "beta") || !NotNo(c, "hasStoryTexts"),   // 卡尔克萨之路: no longer BETA (Yunke 11-23)
                Bags = BagsOf(J.Get(c, "bags")), Starts = NotNo(c, "starts"), Reason = J.String(c, "reason"),
            };
            list.Add(def);
            var ret = J.Get(c, "returnTo");
            if (ret.ValueKind == JsonValueKind.Object && J.String(ret, "id").Length > 0)
            {
                var rch = J.Items(J.Get(c, "chapters")).Where(ch => J.String(ch, "returnTo").Length > 0)
                    .Select(ch => new Chapter(J.String(ch, "returnTo"), J.String(ch, "key"), J.String(ch, "returnName") is { Length: > 0 } rn ? rn : J.String(ch, "name"))).ToArray();
                var r = def with
                {
                    Id = J.String(ret, "id"), Name = J.String(ret, "name"), Chapters = rch.Length > 0 ? rch : def.Chapters, Kind = "return", ReturnOf = id, Cover = J.String(ret, "id"),
                    Bags = BagsOf(J.Get(ret, "bags")), Starts = NotNo(ret, "starts"), Reason = J.String(ret, "reason"), Return = null,
                    RouteNote = "重返版：同一战役加入新的遭遇组和替换卡牌。" + def.RouteNote,
                };
                def.Return = r; list.Add(r);
            }
        }
        if (list.Count == 0) list.AddRange(new[] { zealot0, dunwich0, carcosa0 });   // no catalog: the three built in
        foreach (var s in J.Items(J.Get(cat, "sideStories")))
        {
            string id = J.String(s, "id"); bool challenge = J.String(s, "needsInvestigator").Length > 0 || id.StartsWith("90");
            var diffs = J.Items(J.Get(s, "difficulties")).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
            list.Add(new CampaignDef(id, "standalone", J.String(s, "name"), challenge ? "挑战剧本" : "旁支剧本",
                new[] { new Chapter(id, J.String(s, "key"), J.String(s, "name")) }, Array.Empty<string>(), Array.Empty<string>())
            {
                Kind = "side", Standalone = id, Cover = id, Beta = Yes(s, "beta"), Difficulties = diffs.Length > 0 ? diffs : four,
                Bags = BagsOf(J.Get(s, "bags")), NeedsInvestigator = J.String(s, "needsInvestigator"),
                Starts = NotNo(s, "starts") || J.String(s, "needsInvestigator").Length > 0, Reason = J.String(s, "reason"),
                PrologueName = "", RouteNote = "单独一个剧本，不属于战役：打完即结束，不记经验，也不写战役日志。",
            });
        }
        foreach (var f in J.Items(J.Get(cat, "fanCampaigns")))
        {
            var chapters = J.Items(J.Get(f, "chapters")).Select(ch => new Chapter(J.String(ch, "code"), J.String(ch, "key"), J.String(ch, "name"))).ToArray();
            var bags = BagsOf(J.Get(f, "bags"));
            list.Add(new CampaignDef(J.String(f, "id"), J.String(f, "key"), J.String(f, "name"), "玩家自制战役", chapters, Array.Empty<string>(), Array.Empty<string>())
            {
                Kind = "fanCampaign", Cover = FanCover(J.String(f, "id")), Beta = true, Bags = bags, Difficulties = bags.Count > 0 ? bags.Keys.ToArray() : four, Designer = J.String(f, "designer"),
                Starts = NotNo(f, "starts"), Reason = J.String(f, "reason"), PrologueName = "",
                RouteNote = chapters.Length + " 章依次进行（玩家自制内容，规则由规则核心执行；剧本名和剧情有官方中文，卡牌只有英文，html 版也是如此）。",
            });
        }
        foreach (var f in J.Items(J.Get(cat, "fanScenarios")))
        {
            string id = J.String(f, "id");
            list.Add(new CampaignDef(id, J.String(f, "key"), J.String(f, "name"), "玩家自制剧本", new[] { new Chapter(id, "", J.String(f, "name")) }, Array.Empty<string>(), Array.Empty<string>())
            {
                Kind = "fanScenario", Standalone = id, Cover = FanCover(id), Beta = true, Starts = NotNo(f, "starts"), Reason = J.String(f, "reason"),
                PrologueName = "", RouteNote = "单独一个剧本（玩家自制内容，规则由规则核心执行；剧本名和剧情有官方中文，卡牌只有英文，html 版也是如此）。",
            });
        }
        return list.Select(d => UiGaps.TryGetValue(d.Id, out var gap) ? d with { UiGap = gap } : d).ToArray();
    }

    // a fan-made entry's cover file (art-cover/fan-<homebrew id>[-<box>].webp): the html version's homebrew/<id>/boxes/<box>.jpg
    static string FanCover(string id) => "fan-" + id.Trim(':').Replace(':', '-');

    public static CampaignDef ById(string id) => All.FirstOrDefault(c => c.Id == id) ?? Zealot;
    public static CampaignDef ForScenario(string code) => All.FirstOrDefault(c => c.Find(CoreCatalog.Normalize(code)) is not null)
        ?? All.FirstOrDefault(c => c.Kind == "campaign" && CoreCatalog.Normalize(code).StartsWith(c.Id)) ?? Zealot;
    // the campaign a game belongs to: the core's campaign id, else the scenario it is in
    public static CampaignDef ForGame(JsonElement game)
    {
        var camp = J.Get(J.Get(game, "mode"), "This");
        string id = J.String(camp, "id");
        if (id.Length > 0) return ById(id);
        string sid = J.String(J.Get(J.Get(game, "mode"), "That"), "id");
        return sid.Length > 0 ? ForScenario(sid) : Zealot;
    }
    // locale section of a scenario: "<campaign>.<scenario>"
    public static string ScenarioSection(string code) { var c = ForScenario(code); return c.Key + "." + (c.Find(CoreCatalog.Normalize(code))?.Key is { Length: > 0 } k ? k : c.Chapters[0].Key); }
}
