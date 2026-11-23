using Godot;
using Gathering;
using System.Text.Json;
using Path = System.IO.Path;
using File = System.IO.File;

namespace ArkhamUI;

// 新手教程 (docs/TUTORIAL-PLAN.md, Yunke 11-23): 狂热之夜 第一章「聚集于此」 with two fixed decks, both investigators played
// by the player (to teach switching between them). Roland Banks carries 黑帮打手 (an enemy weakness, for the fight), Daisy
// Walker 恶灵附身. The opening is arranged through the rules core's own debug operations (TableScreen.Tutorial); tips appear
// the first time something happens. The game is an ordinary save named 新手教程 and can go on as a normal campaign.
public static class Tutorial
{
    public const string Roland = "01001", Daisy = "01002";
    public const string RolandWeakness = "01101", DaisyWeakness = "01098";   // 黑帮打手, 恶灵附身
    // the opening with the tutorial's seed (66): each starting hand, the cards the player is led to swap, and the hand after the
    // swap (checked offscreen 11-23, evidence/p0/seed66.log). ArrangeHand at the first window then has nothing left to change;
    // it stays as a safety net (a different core build could shuffle differently).
    public static readonly string[] RolandSwap = { "01033", "01030" };            // 米兰·克里斯托弗博士 放大镜 (探求者的牌，交给黛西用)
    public static readonly string[] DaisySwap = { "01037", "01032", "01089" };    // 专业的直觉 研究馆员 胆识
    public static readonly string[] RolandHand = { "01039", "01020", "01006", "01025", "01091" };   // 推理 大砍刀 .38特制手枪 致命打击 压制
    public static readonly string[] DaisyHand = { "01039", "01033", "01032", "01031", "01030" };    // 推理 超感知 研究馆员 智慧古书 放大镜

    public sealed class Meta
    {
        public string GameId { get; set; } = "";
        public List<string> HandsDone { get; set; } = new();
        public bool DecksDone { get; set; }
        public int Tests { get; set; }               // skill tests seen while the opening is arranged
        public string LastTest { get; set; } = "";
        public bool FightForced { get; set; }
        public bool OpeningOver { get; set; }        // the first act advanced: no more arranging
        public List<string> Seen { get; set; } = new();
        public bool Skipped { get; set; }
        public bool Offered { get; set; }            // the first-start question was asked
    }

    static Meta? meta;
    static string MetaFile => Path.Combine(App.I.DataRoot, "tutorial.json");
    public static Meta M
    {
        get
        {
            if (meta is not null) return meta;
            try { meta = File.Exists(MetaFile) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaFile)) : null; } catch { meta = null; }
            return meta ??= new Meta();
        }
    }
    public static void Save() => SafeFile.WriteAllText(MetaFile, JsonSerializer.Serialize(M, new JsonSerializerOptions { WriteIndented = true }));
    public static bool Is(Guid id) => M.GameId == id.ToString("D");

    // the tutorial game's seed: the same shuffles every time, so the opening hands, the cards to swap and what is drawn next
    // are the same for everyone (Yunke 11-23: "要保证整个新手教程是固定流程")
    public static int Seed = int.TryParse(System.Environment.GetEnvironmentVariable("ARKHAM_TUTORIAL_SEED"), out int ts) && ts > 0 ? ts : 66;

    public static string DeckKey(string code) => "tutorial-" + code;

    // the two decks: the core set's example lists with the fixed weakness in place of the random one
    public static async Task<string[]> EnsureDecks()
    {
        var S = App.I.S!;
        var keys = new List<string>();
        foreach (var (code, weak) in new[] { (Roland, RolandWeakness), (Daisy, DaisyWeakness) })
        {
            string key = DeckKey(code);
            var schema = await S.Backend.BuildSchema(code);
            var starter = S.Catalog.Starter(code, schema);
            var slots = new Dictionary<string, int>();
            foreach (var p in J.Props(J.Get(starter, "slots"))) if (p.Name != StandaloneDecks.Placeholder) slots[p.Name] = p.Value.GetInt32();
            slots[weak] = 1;
            var deck = JsonSerializer.SerializeToElement(new { name = "新手教程 · " + S.Catalog.Name(code), investigator_code = code, slots, sideSlots = new Dictionary<string, int>() });
            S.Decks.Put(key, deck);
            keys.Add(key);
        }
        S.Decks.Save();
        return keys.ToArray();
    }

    // the tutorial game: 狂热之夜 from its start, 简单, Roland then Daisy with their tutorial decks
    public static async Task<Guid?> StartGame()
    {
        var S = App.I.S!;
        try
        {
            var keys = new[] { DeckKey(Roland), DeckKey(Daisy) };
            var state = await S.Backend.Create(2, "Easy", Campaigns.Zealot.Id, null, new Gathering.GameVariants(false, Array.Empty<string>(), Seed));
            SetupStore.Save(state.Id, keys);
            meta = new Meta { GameId = state.Id.ToString("D"), Offered = true };   // a new tutorial shows every tip again
            Save();
            GD.Print("TUTORIAL_START " + state.Id);
            return state.Id;
        }
        catch (Exception e) { App.I.Toast("新手教程开局失败：" + e.Message, true, 5); GD.PushError(e.ToString()); return null; }
    }

    // QA: straight to the table, without the guided screens
    public static async Task Start()
    {
        await EnsureDecks();
        if (await StartGame() is { } id) App.I.Go(new TableScreen(id));
    }

    // the first 开始新的调查 (Yunke 11-23): offer the tutorial once; otherwise go on as asked
    public static void OfferOr(Action normal)
    {
        if (M.Offered || App.I.QaRunning) { normal(); return; }
        M.Offered = true; Save();
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(600, 0) }; col.AddThemeConstantOverride("separation", 18);
        col.AddChild(Style.Title("要先玩一下新手教程吗？", 32));
        var t = Style.Text("一步步带你选战役、选调查员，再用两副固定的牌玩一遍狂热之夜第一章「聚集于此」：行动、技能检定、敌人、遭遇牌，以及在两名调查员之间切换。教程存档会保留，打完可以接着玩第二章。以后也可以在主菜单的“新手教程”重新开始。", 21, Style.Paper);
        t.AutowrapMode = TextServer.AutowrapMode.WordSmart; t.CustomMinimumSize = new Vector2(560, 0); col.AddChild(t);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; row.AddThemeConstantOverride("separation", 14); col.AddChild(row);
        Control scrim = null!;
        var skip = Ui.Button("不用，直接开始", () => { App.I.CloseModal(scrim); normal(); }, 22); skip.CustomMinimumSize = new Vector2(190, 54); row.AddChild(skip);
        var go = Ui.Button("开始教程", () => { App.I.CloseModal(scrim); Guide.Begin(); }, 22, true); go.CustomMinimumSize = new Vector2(150, 54); row.AddChild(go);
        scrim = App.I.Modal(Ui.Panel(col, 36));
    }
}
