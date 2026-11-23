using Godot;

namespace ArkhamUI;

// Title screen: one of the ten homepage paintings with its moving layers (HomeBackground: random each time, the last save's
// scenario's own painting when it has one), the dark fade on the left, the menu. (The moonlit street with tentacles and
// investigators is retired, Yunke 11-20; backup/MainMenu.cs.before-home-bg-20261120.)
public partial class MainMenu : Control
{
    Label status = null!;
    VBoxContainer menu = null!;

    // built while the previous screen (a table, perhaps) is still the current one: remember which save was played
    public MainMenu() { HomeBackground.NoteFrom(App.I.Current); }

    // The deck workshop reads its decks from the build engine first: the dip to black stays until they are in, with a line of
    // text, so the hitch and the empty frame are not seen (Yunke 11-20)
    static void OpenWorkshop()
    {
        App.I.WorkshopReturn = null;   // from the main menu: its back is the main menu
        var ready = Task.WhenAll(BuildDecks.List(), TabooChip.Sets());
        App.I.HoldNextDip(ready, "正在整理牌组…");
        App.I.Go(new DeckLibraryScreen());
    }

    HomeBackground? homeBg;
    bool swapping;
    // "换一张壁纸" (Yunke 11-20): another of the homepage paintings, from all of them, cross-faded in over the one showing
    void NextWallpaper()
    {
        if (swapping || homeBg is null || !IsInstanceValid(homeBg)) return;
        swapping = true;
        HomeBackground.anyNext = true;
        var old = homeBg; var next = new HomeBackground();
        AddChild(next); MoveChild(next, old.GetIndex() + 1); next.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); homeBg = next;
        if (App.I.QaRunning) GD.Print("QA_WALLPAPER " + old.SceneId + " -> " + next.SceneId);
        if (App.I.S?.Settings.ReducedMotion == true) { old.QueueFree(); swapping = false; return; }
        next.Modulate = new Color(1, 1, 1, 0);
        var tw = next.CreateTween(); tw.TweenProperty(next, "modulate:a", 1f, 0.7f).SetTrans(Tween.TransitionType.Sine);
        tw.TweenCallback(Callable.From(() => { if (IsInstanceValid(old)) old.QueueFree(); swapping = false; }));
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        var ink = new ColorRect { Color = Style.Ink, MouseFilter = MouseFilterEnum.Ignore }; AddChild(ink); ink.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new HomeBackground(); AddChild(bg); bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); homeBg = bg;
        // the menu always sits on the left: the painting there is softly out of focus, clear again past the middle (Yunke 11-20)
        var blur = new ColorRect { MouseFilter = MouseFilterEnum.Ignore, Material = new ShaderMaterial { Shader = LeftBlur } };
        AddChild(blur); blur.AnchorBottom = 1; blur.AnchorRight = 0.5f;
        // the homepage paintings keep their left third dark and quiet for the menu; a light fade is enough
        var shade = new TextureRect { Texture = Gradient(new Color(0, 0, 0, 0.75f), new Color(0, 0, 0, 0)), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(shade); shade.AnchorBottom = 1; shade.OffsetRight = 900;
        // the change-the-painting button, top-right corner (Yunke 11-20); built once with the menu, above the painting
        var wp = LineIcon.Button("wallpaper", "换一张壁纸", NextWallpaper, 46);
        AddChild(wp); wp.AnchorLeft = wp.AnchorRight = 1; wp.OffsetLeft = -46 - 28; wp.OffsetRight = -28; wp.OffsetTop = 24; wp.OffsetBottom = 24 + 46; wp.ZIndex = 5;
        QaScript.Register("wallpaper", _ => NextWallpaper());
        UpdateUi.Tag(this);   // 有新版本 / 正在下载 / 已准备好 (Scripts/Update)

        var col = new VBoxContainer { Position = new Vector2(110, 90) };
        col.AddThemeConstantOverride("separation", 14);
        AddChild(col);
        var mark = Ui.Image(Ui.Ref("art/title-wordmark-v1/wordmark"), new Vector2(560, 186));
        col.AddChild(mark);
        // (the 狂热之夜 · 敦威治遗产 line under the wordmark is gone, Yunke 11-20: it read like the last save's name)
        col.AddChild(Ui.Spacer(0, 100));
        menu = new VBoxContainer(); menu.AddThemeConstantOverride("separation", 16); col.AddChild(menu);
        status = Style.Text("", 22, Style.PaperDim);
        AddChild(status); status.AnchorTop = status.AnchorBottom = 1; status.OffsetLeft = 110; status.OffsetTop = -70;
        // the rules core actually in runtime/ (its manifest), not a fixed name (11-22: it still said green-1.0.29 after the swap)
        string core = "";
        try { using var cj = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(App.I.Root, "runtime", "core.json"))); core = cj.RootElement.TryGetProperty("sourceRelease", out var rel) ? rel.GetString() ?? "" : ""; } catch { }
        var ver = Style.Text("诡镇奇谈 · 新版原生前端" + (core.Length > 0 ? " · 规则核心 " + core : ""), 18, new Color(Style.PaperDim, 0.7f));
        AddChild(ver); ver.AnchorLeft = ver.AnchorRight = ver.AnchorTop = ver.AnchorBottom = 1; ver.OffsetLeft = -640; ver.OffsetTop = -44;
    }

    // a disc blur of what is drawn below (the painting), strongest at the left edge and gone by `to` of the screen width
    static Shader? leftBlur;
    static Shader LeftBlur => leftBlur ??= new Shader { Code = @"shader_type canvas_item;
uniform sampler2D screen : hint_screen_texture, filter_linear;
uniform float radius = 9.0;    // screen pixels at full strength (at 1080p; scales with the height)
uniform float full = 0.14;     // blur at full strength left of this (fraction of the screen width)
uniform float to = 0.42;       // and none right of this (the H08 moon at 0.36 stays nearly sharp)
void fragment() {
    float w = 1.0 - smoothstep(full, to, SCREEN_UV.x);
    vec2 px = vec2(SCREEN_PIXEL_SIZE.x / SCREEN_PIXEL_SIZE.y, 1.0) * (radius / 1080.0) * w;
    vec3 acc = texture(screen, SCREEN_UV).rgb; float n = 1.0;
    if (w > 0.01) {
        for (int i = 0; i < 32; i++) {
            float r = sqrt((float(i) + 0.5) / 32.0), th = float(i) * 2.39996;
            acc += texture(screen, SCREEN_UV + vec2(cos(th), sin(th)) * r * px).rgb; n += 1.0;
        }
    }
    COLOR = vec4(acc / n, 1.0);
}" };

    static GradientTexture2D Gradient(Color from, Color to)
    {
        var g = new Gradient(); g.SetColor(0, from); g.SetColor(1, to);
        return new GradientTexture2D { Gradient = g, FillFrom = new Vector2(0, 0.5f), FillTo = new Vector2(1, 0.5f), Width = 256, Height = 8 };
    }

    public void SetStatus(string text, bool error = false)
    {
        if (status is null) return;
        status.Text = text; status.AddThemeColorOverride("font_color", error ? Style.Danger : Style.PaperDim);
    }

    public new void Ready()
    {
        Ui.Clear(menu);
        BuildEngine.Warm();   // arkham.build's rules load in the background while the menu is up
        QaScript.Register("build-test", async a =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = await BuildEngine.I.Ready; GD.Print($"QA_BUILD ready {n} cards, init {BuildEngine.I.InitMs} ms, waited {sw.ElapsedMilliseconds} ms");
            var deck = System.IO.File.ReadAllText(System.IO.Path.Combine(App.I.Root, a.Length > 0 ? a : "tools/build-engine/test-deck.json"));
            sw.Restart(); var v = await BuildEngine.I.Call("validate", deck); GD.Print($"QA_BUILD validate {sw.ElapsedMilliseconds} ms :: {v}");
            sw.Restart(); var acc = await BuildEngine.I.Call("access", deck, "slots"); GD.Print($"QA_BUILD access {sw.ElapsedMilliseconds} ms :: {acc.Length} chars");
            sw.Restart(); var inv = await BuildEngine.I.Call("investigators"); GD.Print($"QA_BUILD investigators {sw.ElapsedMilliseconds} ms :: {inv.Length} chars");
        });
        QaScript.Register("build-compare", async _ =>
        {
            await BuildEngine.I.Ready;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(App.I.DataRoot, "decks.json")));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                System.Text.Json.JsonElement r;
                try { r = await BuildEngine.I.CallJson("validate", p.Value.GetRawText()); } catch { GD.Print("QA_DECK " + p.Name + " error"); continue; }
                bool ok = r.GetProperty("valid").GetBoolean();
                GD.Print("QA_DECK " + p.Name + " " + (ok ? "valid" : "invalid " + string.Join(",", r.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("type").GetString()))));
            }
        });
        // 继续调查 first, then 开始新的调查 (Yunke 11-20)
        var hasSaves = App.I.S!.Backend.Games().Length > 0;
        var cont = Item("继续调查", () => ContinueDialog.Open()); cont.Disabled = !hasSaves;
        // the first time: the 新手教程 is offered (Yunke 11-23: "在第一次首页点击进入新冒险的时候触发")
        Item("开始新的调查", () => Tutorial.OfferOr(() => App.I.Go(new CycleScreen())));
        if (Guide.On) Guide.End();   // back at the menu: the guided screens are over
        QaScript.Register("tutorial", a => Tutorial.Start());          // QA: straight to the tutorial table
        QaScript.Register("tutorial-guide", _ => Guide.Begin());       // QA: the guided screens
        // online play (Yunke 11-22): join someone's room by its code; shown but disabled until EOS is connected (Online.cs)
        var join = Item("加入联机游戏", OnlineDialogs.Join); Online.Gate(join);
        Item("牌组工坊", OpenWorkshop);
        Item("新手教程", Guide.Begin);   // Scripts/Tutorial: from the campaign choice to the table, step by step (under 牌组工坊, Yunke 11-23)
        Item("设置", () => SettingsDialog.Open());
        Item("退出游戏", () => GetTree().Quit());
        // QA only: rewind a saved game (in a QA data folder) to a step and open its table there
        // QA: home-bg:<H01…H10 or a scenario code> picks the next menus' painting ("-" back to the last save's); menu-again rebuilds the menu
        QaScript.Register("online-demo", a => { if (a == "join") OnlineDialogs.Join(); else OnlineDialogs.Demo(); });   // QA: the online screens
        QaScript.Register("home-bg", a => HomeBackground.qaPick = a == "-" ? null : a);
        QaScript.Register("menu-again", _ => { var m = new MainMenu(); App.I.Go(m); m.Ready(); });
        QaScript.Register("midshot", a => TableScreen.qaMidShot = a);
        QaScript.Register("dipshot", a => App.qaDipShot = a);
        QaScript.Register("shufshot", a => TableScreen.qaShuffleShot = a);   // armed before open-at (the table's first steps run inside it)
        QaScript.Register("open-at", async a =>
        {
            var parts = a.Split(','); var id = Guid.Parse(parts[0]); long seq = long.Parse(parts[1]);
            var st = await App.I.S!.Backend.Open(id);
            await App.I.S!.Backend.RevertTo(st, seq);
            App.I.Go(new TableScreen(id));
        });
        // QA only: import a crafted save (path under the project root) as a new game and open it
        QaScript.Register("qa-import", async path =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(App.I.Root, path)));
                var id = await App.I.S!.Backend.ImportCrafted(doc.RootElement.Clone(), "测试存档 · " + System.IO.Path.GetFileNameWithoutExtension(path));
                GD.Print("QA_IMPORT_OK " + id); App.I.Go(new TableScreen(id));
            }
            catch (Exception e) { GD.Print("QA_IMPORT_FAIL " + e.Message); }
        });
        QaScript.Register("menu", a => { switch (a) { case "new": App.I.Go(new PrepareScreen()); break; case "cycle": App.I.Go(new CycleScreen()); break; case "dunwich": App.I.Go(new PrepareScreen(Campaigns.Dunwich)); break; case "carcosa": App.I.Go(new PrepareScreen(Campaigns.Carcosa)); break; case "continue": ContinueDialog.Open(); break; case "workshop": OpenWorkshop(); break; case "settings": SettingsDialog.Open(); break; default: if (a.StartsWith("camp:") && Campaigns.All.FirstOrDefault(c => c.Id == a[5..]) is { } qc) App.I.Go(new PrepareScreen(qc)); break; } });   // QA: menu:camp:<id>
        int i = 0;
        foreach (var c in menu.GetChildren())
            if (c is Control b) { b.Modulate = new Color(1, 1, 1, 0); var tw = CreateTween(); tw.TweenInterval(0.08 * i++); tw.TweenProperty(b, "modulate:a", 1f, 0.35f); }
    }

    Button Item(string text, Action a)
    {
        // text list on the left dark fade; hover darkens a little and draws the amber hairline
        // a bright glowing bar behind the line under the pointer (Yunke 11-18 r6 #1); the same line serves every menu (Ui.MenuItem)
        var b = Ui.MenuItem(text, a, 34);   // a size up from 30 (Yunke 11-20)
        menu.AddChild(b); return b;
    }

}
