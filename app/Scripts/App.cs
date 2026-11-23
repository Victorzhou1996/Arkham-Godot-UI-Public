using Godot;
using System.Text.Json;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace ArkhamUI;

// Root node: owns services, the current screen, global overlays and QA hooks.
public partial class App : Control
{
    public static App I { get; private set; } = null!;
    public string Root { get; private set; } = "";
    public string Assets { get; private set; } = "";
    public string DataRoot { get; private set; } = "";
    public static readonly bool ExportedWindows = OS.HasFeature("template") && OS.GetName() == "Windows";   // an exported build, not the editor
    public Services? S { get; private set; }
    public static Action<MainMenu>? CoreMissing;   // a build without the rules core shows its own menu here (the phone's guest menu)
    Control screenLayer = null!, overlayLayer = null!, toastLayer = null!;
    Control? current;
    string capturePath = ""; double captureDelay = 2.5; bool quitAfterCapture;
    readonly List<string> scriptSteps = new();
    public bool QaRunning => scriptSteps.Count > 0;   // automated runs: story screens continue on their own

    void MoveAway(int x) { GetWindow().Position = new Vector2I(x, 0); }

    public override void _Ready()
    {
        I = this;
        PerfProbe.AttachIfAsked(this);   // QA only (--perf-probe)
        MemProbe.AttachIfAsked(this);    // QA only (--mem-probe): memory, CPU and hitches
        OnlineBoot.Start(this);          // EOS online play (Scripts/Online); no-op without the SDK
        // every button and drop-down without its own look gets the soft-edged backdrop (Yunke 11-18: all buttons, every screen)
        var theme = new Theme();
        foreach (var type in new[] { "Button", "OptionButton", "MenuButton" })
        {
            theme.SetStylebox("normal", type, Ui.SoftBox(0.38f, false)); theme.SetStylebox("hover", type, Ui.SoftBox(0.62f, true));
            theme.SetStylebox("pressed", type, Ui.SoftBox(0.72f, true)); theme.SetStylebox("hover_pressed", type, Ui.SoftBox(0.72f, true));
            theme.SetStylebox("disabled", type, Ui.SoftBox(0.2f, false)); theme.SetStylebox("focus", type, new StyleBoxEmpty());
        }
        GetTree().Root.Theme = theme;
        Root = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        Assets = Path.Combine(Root, "app", "assets");
        DataRoot = Path.Combine(Root, "userdata");
        // Exported Windows build (11-22 install package): res:// is the .pck inside the exe, so the files read from disk sit in
        // game\ next to the exe (game\runtime, game\artstore, game\assets, game\ai — see AiHostGodot), and the player's
        // settings and saves go to %APPDATA%\ArkhamUI-New\userdata (writable, never the developer's userdata/). Editor and QA
        // runs are unchanged.
        if (ExportedWindows)
        {
            Root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(OS.GetExecutablePath()))!, "game");
            Assets = Path.Combine(Root, "assets");
            DataRoot = Path.GetFullPath(Path.Combine(OS.GetUserDataDir(), "userdata"));
        }
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--data-root=")) DataRoot = Path.GetFullPath(Path.Combine(Root, arg[12..]));
            else if (arg.StartsWith("--capture=")) capturePath = Path.GetFullPath(Path.Combine(Root, arg[10..]));
            else if (arg.StartsWith("--capture-delay=")) double.TryParse(arg[16..], out captureDelay);
            else if (arg == "--quit-after-capture") quitAfterCapture = true;
            // QA runs in the background (Yunke 11-18: the test window must not jump in front of what they are doing):
            // the window never takes focus and sits beyond the right edge of every screen; captures still work
            else if (arg == "--background")
            {
                GetWindow().Unfocusable = true;
                int right = 0; for (int i = 0; i < DisplayServer.GetScreenCount(); i++) right = Math.Max(right, DisplayServer.ScreenGetPosition(i).X + DisplayServer.ScreenGetSize(i).X);
                CallDeferred(nameof(MoveAway), right + 200);
            }
            else if (arg.StartsWith("--window="))
            {
                var d = arg[9..].Split('x');
                if (d.Length == 2 && int.TryParse(d[0], out int w) && int.TryParse(d[1], out int h))
                { GetWindow().Mode = Window.ModeEnum.Windowed; GetWindow().Size = new Vector2I(w, h); }
            }
        }
        Directory.CreateDirectory(DataRoot);
        // in-game updates (Scripts/Update, Yunke 11-23): a staged update is switched in, or a failing one rolled back, before
        // anything opens a game file; the patch packs are loaded here too. When the update helper must take over, quit now.
        if (Updater.Boot(Root, DataRoot)) { GetTree().Quit(); return; }
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Theme = Style.Build();
        screenLayer = Layer(); overlayLayer = Layer(); toastLayer = Layer();
        // Layers must not swallow input: a full-rect PASS control blocks its siblings underneath.
        screenLayer.MouseFilter = MouseFilterEnum.Ignore; overlayLayer.MouseFilter = MouseFilterEnum.Ignore; toastLayer.MouseFilter = MouseFilterEnum.Ignore; overlayLayer.ZIndex = 200; toastLayer.ZIndex = 300;
        PhoneFeedback();
        Boot();
        if (capturePath.Length > 0) GetTree().CreateTimer(captureDelay).Timeout += () => Capture(capturePath, quitAfterCapture);
        GD.Print("APP_READY data=" + DataRoot);
    }

    // The phone draws the screen as the desktop does: no safe-area shrinking (Yunke 11-23: tried, "太丑了，还是回到之前的UI吧，
    // 不要缩小效果"). What it keeps is a visible press on every button ("但是apk按键效果还是要有（可以换成现在win版首页的指针选中的
    // 金色效果）"): while a finger is down a button shows the title screen's golden bar, the look a menu line has under the pointer
    // on the desktop. QA: --touch-feedback-test turns it on on Windows.
    void PhoneFeedback()
    {
        if (OS.GetName() != "Android" && !OS.GetCmdlineUserArgs().Contains("--touch-feedback-test")) return;
        GetTree().NodeAdded += n => { if (n is Button b) Press(b); };
        foreach (var b in FindChildren("*", "Button", true, false).OfType<Button>()) Press(b);
        // QA: hold a button down (by its text) to see the press
        QaScript.Register("press-down", t => { if (FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(x => x.IsVisibleInTree() && x.Text.Contains(t)) is { } hit) hit.EmitSignal(BaseButton.SignalName.ButtonDown); else GD.PrintErr("QA no button " + t); });
    }

    static readonly string[] PressBoxes = { "normal", "hover", "pressed", "hover_pressed" };
    static readonly string[] PressFonts = { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" };

    static void Press(Button b)
    {
        if (b.HasMeta("pressFx")) return;
        b.SetMeta("pressFx", true);
        bool lit = false;
        var savedBoxes = new Dictionary<string, StyleBox?>(); var savedFonts = new Dictionary<string, Color?>();
        void On()
        {
            if (lit || b.Disabled || !IsInstanceValid(b)) return;
            lit = true;
            // the golden bar keeps the button's own padding, so nothing moves
            var cur = b.GetThemeStylebox("normal");
            var glow = Ui.GlowBar((int)cur.ContentMarginLeft, (int)cur.ContentMarginRight, (int)Math.Max(cur.ContentMarginTop, cur.ContentMarginBottom));
            foreach (var k in PressBoxes) { savedBoxes[k] = b.HasThemeStyleboxOverride(k) ? b.GetThemeStylebox(k) : null; b.AddThemeStyleboxOverride(k, glow); }
            var ink = new Color(0.12f, 0.08f, 0.04f);
            foreach (var k in PressFonts) { savedFonts[k] = b.HasThemeColorOverride(k) ? b.GetThemeColor(k) : null; b.AddThemeColorOverride(k, ink); }
        }
        void Off()
        {
            if (!lit || !IsInstanceValid(b)) return;
            lit = false;
            foreach (var (k, v) in savedBoxes) { if (v is null) b.RemoveThemeStyleboxOverride(k); else b.AddThemeStyleboxOverride(k, v); }
            foreach (var (k, v) in savedFonts) { if (v is { } c) b.AddThemeColorOverride(k, c); else b.RemoveThemeColorOverride(k); }
        }
        b.ButtonDown += On;
        // held a moment after the finger lifts, so a quick tap is still seen
        b.ButtonUp += () => { if (IsInstanceValid(b)) b.GetTree().CreateTimer(0.12).Timeout += Off; };
        b.MouseExited += Off;
    }

    Control Layer()
    {
        var c = new Control { MouseFilter = MouseFilterEnum.Pass };
        AddChild(c); c.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return c;
    }

    async void Boot()
    {
        Sfx.Attach(this);
        var menu = new MainMenu(); Go(menu); menu.SetStatus("正在唤醒规则核心…");
        try
        {
            Gathering.PrototypeBackend.Trace = s => GD.Print(s);
            S = await Services.Start(Root, DataRoot);
            if (!S.HasCore)
            {
                // no rules core here (a phone, 11-22): the cards and texts still load, so this player can join an online room
                ApplySettings();
                menu.SetStatus("启动失败：规则核心不可用（" + S.CoreError + "）", true);
                CoreMissing?.Invoke(menu);
                UpdateUi.Startup();
                await RunScript();
                return;
            }
            ApplySettings();
            menu.SetStatus("");
            menu.Ready();
            // QA: a start that never reaches the menu (the update's roll-back test)
            if (OS.GetCmdlineUserArgs().Contains("--qa-fail-before-menu")) { GD.Print("QA_FAIL_BEFORE_MENU"); GetTree().Quit(3); return; }
            UpdateUi.Startup();
            await RunScript();
        }
        catch (Exception e) { menu.SetStatus("启动失败：" + e.Message, true); GD.PushError(e.ToString()); }
    }

    public void ApplySettings()
    {
        if (S is null) return;
        GetWindow().Mode = S.Settings.Fullscreen ? Window.ModeEnum.Fullscreen : (GetWindow().Mode == Window.ModeEnum.Fullscreen ? Window.ModeEnum.Maximized : GetWindow().Mode);
        GetWindow().ContentScaleFactor = WantedScale;   // FitScale (each frame) takes it down where the screen would not fit
        if (current is TableScreen ts) ts.RefreshView();   // a hover-mode switch shows at once (the test's cards: small / printed)
    }

    // The interface scale (bug #24, a phone at 115 %: the prepare page's 开始调查 and the settings' 保存 fell off the bottom of
    // the screen, and the scale could not be set back). The layouts are made for a 1920 x 1080 view; a larger scale makes the
    // view smaller than that. The chosen scale is a ceiling: where the current screen's layout would not fit, the scale comes
    // down (never below 100 %) until it does, so every button stays on screen. A saved value outside App.ScaleMin..ScaleMax is 100 %.
    // 11-23 (Yunke): a free slider (ScaleMin..ScaleMax %) instead of four choices; while it is dragged the settings preview it
    float WantedScale => previewScale ?? (S?.Settings.TextScale is int t && t >= ScaleMin && t <= ScaleMax ? t / 100f : 1f);
    public const int ScaleMin = 75, ScaleMax = 150;
    float? previewScale;
    // the settings' slider shows a scale before it is saved (null: back to the saved one); the next frame applies it
    public void PreviewUiScale(float? scale) { previewScale = scale; fitFor = null; }
    Control? fitFor; Vector2I fitWin;
    public override void _Process(double delta) { FitScale(); ApplyZoom(); }

    // 缩放 (Yunke 11-23: "用来调整整个显示框占据屏幕的比例，就像视频播放软件双指调整大小一样"): the whole picture, laid out as
    // usual, is drawn smaller (black round it) or larger (its edges cut off) about the middle of the screen. It is the root
    // viewport's canvas transform, so the layout, the input and every global rect stay as they are; only the drawing moves.
    public const int ZoomMin = 50, ZoomMax = 150;
    float? previewZoom;
    public float Zoom => previewZoom ?? (S?.Settings.FrameZoom is int z && z >= ZoomMin && z <= ZoomMax ? z / 100f : 1f);
    public void PreviewZoom(float? zoom) => previewZoom = zoom;
    float zoomDone = 1f; Vector2 zoomView;
    void ApplyZoom()
    {
        var vp = GetViewport(); float z = Zoom; var view = vp.GetVisibleRect().Size;
        if (Math.Abs(z - zoomDone) < 0.0001f && view == zoomView) return;
        zoomDone = z; zoomView = view;
        vp.CanvasTransform = new Transform2D(0f, new Vector2(z, z), 0f, view * (1f - z) / 2f);
        if (QaRunning) GD.Print("QA_ZOOM " + z + " view=" + view);
    }
    void FitScale()
    {
        if (S is null || current is null) return;
        var win = GetWindow(); float want = WantedScale;
        // a new screen or a resized window starts again from the chosen scale; otherwise the scale only ever comes down
        if (current != fitFor || win.Size != fitWin) { fitFor = current; fitWin = win.Size; if (Math.Abs(win.ContentScaleFactor - want) > 0.001f) win.ContentScaleFactor = want; return; }
        if (want <= 1f || win.Size.X <= 0 || win.Size.Y <= 0) return;
        var view = GetViewport().GetVisibleRect().Size;
        var need = current.GetCombinedMinimumSize();
        // a screen is a plain Control holding containers: full-screen ones (its margins, columns) need their content plus
        // their margins; ones placed from the top-left corner (the title screen's list) need their place plus their content
        static float Extent(float a0, float a1, float o0, float o1, float min) => a0 <= 0f && a1 >= 1f ? min + o0 - o1 : a1 <= 0f ? o0 + min : 0f;
        foreach (var c in current.GetChildren())
            if (c is Container k && k.Visible)
            {
                var m = k.GetCombinedMinimumSize();
                need = new Vector2(Math.Max(need.X, Extent(k.AnchorLeft, k.AnchorRight, k.OffsetLeft, k.OffsetRight, m.X)), Math.Max(need.Y, Extent(k.AnchorTop, k.AnchorBottom, k.OffsetTop, k.OffsetBottom, m.Y)));
            }
        if (need.X <= view.X + 1 && need.Y <= view.Y + 1) return;
        // canvas_items / expand from 1920 x 1080: at factor f the view is window / (min(w/1920, h/1080) * f)
        float baseScale = Math.Min(win.Size.X / 1920f, win.Size.Y / 1080f);
        float fit = want;
        if (need.X > 0) fit = Math.Min(fit, win.Size.X / (baseScale * need.X));
        if (need.Y > 0) fit = Math.Min(fit, win.Size.Y / (baseScale * need.Y));
        fit = Math.Max(1f, MathF.Floor(fit * 100f) / 100f);
        if (fit < win.ContentScaleFactor - 0.001f)
        {
            win.ContentScaleFactor = fit;
            if (QaRunning) GD.Print("QA_FIT_SCALE " + current.GetType().Name + " want=" + want + " fit=" + fit + " need=" + need + " view=" + view);
        }
    }

    public void Go(Control screen)
    {
        // a dip to black between screens (Yunke 11-20 #6): the old screen, frozen, darkens; the new one rises out of the dark
        if (current is not null) Dip();
        CloseAllModals();
        if (current is not null) { screenLayer.RemoveChild(current); current.QueueFree(); }
        current = screen; screenLayer.AddChild(screen); screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }
    public Control? Current => current;

    // Dip to black and back over whatever changes underneath now: a still of the last frame fades to black (0.22 s), then the
    // black lifts (0.38 s) off the new state. The still also hides a slow first frame of what is being built.
    Control? curtain;
    public static string qaDipShot = "";   // QA: dipshot:<path> captures the next dip at full black (the script runs too slowly to catch it)
    // A screen that needs a moment to load (the deck workshop asks the build engine for its decks) can ask the next dip to stay
    // black until it is ready (Yunke 11-20: the workshop showed a hitch and an empty frame): set before Go.
    Task? dipHold; string dipHoldText = "";
    public void HoldNextDip(Task until, string text) { dipHold = until; dipHoldText = text; }
    // Where the workshop's back button leads when it was opened from somewhere other than the main menu (Yunke 11-22: from the
    // investigator picker's + it went back to the main menu). Taken once by the workshop's back (Build/DeckLibraryScreen).
    public Action? WorkshopReturn;
    public bool TakeWorkshopReturn() { var r = WorkshopReturn; WorkshopReturn = null; if (r is null) return false; r(); return true; }

    public void Dip(float down = 0.22f, float up = 0.38f)
    {
        if (QaRunning) GD.Print("QA_DIP t=" + Time.GetTicksMsec() + " reduced=" + (S?.Settings.ReducedMotion == true) + " busy=" + (curtain is not null && IsInstanceValid(curtain)));
        if (S?.Settings.ReducedMotion == true || (curtain is not null && IsInstanceValid(curtain))) return;
        Texture2D? still = null;
        try { still = ImageTexture.CreateFromImage(GetViewport().GetTexture().GetImage()); } catch { }
        var shot = new TextureRect { Texture = still, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Stop, ZIndex = 250 };
        if (Math.Abs(zoomDone - 1f) > 0.0001f)
        {
            // 缩放 is on: the still already shows the zoomed picture, so it goes on a layer the zoom does not touch
            var layer = new CanvasLayer { Layer = 50 }; AddChild(layer); layer.AddChild(shot);
            shot.TreeExited += () => { if (IsInstanceValid(layer)) layer.QueueFree(); };
        }
        else AddChild(shot);
        shot.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var black = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        shot.AddChild(black); black.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        curtain = shot;
        _ = DipRun(shot, black, down, up);
    }

    // The new screen's first frames can take long to build; a tween started now would jump through in one of them and show no
    // fade at all (seen in QA 11-20). So the still holds until frames come at a normal pace again, and only then fades.
    async Task DipRun(TextureRect shot, ColorRect black, float down, float up)
    {
        for (int i = 0; i < 30; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!IsInstanceValid(shot)) return;
            if (i >= 1 && GetProcessDeltaTime() < 0.05) break;
        }
        if (QaRunning) GD.Print("QA_DIP_FADE t=" + Time.GetTicksMsec() + " delta=" + GetProcessDeltaTime().ToString("0.000") + " z=" + shot.ZIndex + " vis=" + shot.IsVisibleInTree());
        var hold = dipHold; string holdText = dipHoldText; dipHold = null; dipHoldText = "";
        var tw = shot.CreateTween();
        tw.TweenProperty(black, "modulate:a", 1f, down).SetTrans(Tween.TransitionType.Sine);
        tw.TweenCallback(Callable.From(() => { shot.Texture = null; if (QaRunning && qaDipShot.Length > 0) { var at = Path.GetFullPath(Path.Combine(Root, qaDipShot)); qaDipShot = ""; GetTree().CreateTimer(up * 0.35).Timeout += () => Capture(at); } }));
        if (hold is not null && !hold.IsCompleted)
        {
            // fully black: wait there for the new screen to be ready (at most 4 s), a line of text breathing softly meanwhile
            await ToSignal(tw, Tween.SignalName.Finished);
            if (!IsInstanceValid(shot)) return;
            Label? note = null;
            if (holdText.Length > 0)
            {
                note = Style.Text(holdText, 24, new Color(Style.Amber, 0.85f), Style.Kai); note.HorizontalAlignment = HorizontalAlignment.Center; note.VerticalAlignment = VerticalAlignment.Center;
                note.MouseFilter = MouseFilterEnum.Ignore; shot.AddChild(note); note.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); note.Modulate = new Color(1, 1, 1, 0);
                var nt = note.CreateTween().SetLoops(); nt.TweenProperty(note, "modulate:a", 1f, 0.6f).SetTrans(Tween.TransitionType.Sine); nt.TweenProperty(note, "modulate:a", 0.35f, 0.6f).SetTrans(Tween.TransitionType.Sine);
            }
            // a turning ring under the text (Yunke 11-21: something moves while it loads)
            var spin = new Spinner { Size = new Vector2(60, 60) };
            shot.AddChild(spin); spin.SetAnchorsAndOffsetsPreset(LayoutPreset.Center); spin.Position = GetViewport().GetVisibleRect().Size / 2 + new Vector2(-30, holdText.Length > 0 ? 62 : -30);   // well under the line of text (Yunke 11-22: they overlapped)
            await Task.WhenAny(hold, Task.Delay(4000));
            // the new screen builds in the next frames (a long one, for a big list): the fade out waits until frames come at a
            // normal pace again, or it would jump straight to the end and the screen would just appear (Yunke 11-21)
            int smooth = 0;
            for (int i = 0; i < 90 && smooth < 3; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!IsInstanceValid(shot)) return;
                smooth = GetProcessDeltaTime() < 0.05 ? smooth + 1 : 0;
            }
            if (!IsInstanceValid(shot)) return;
            if (IsInstanceValid(spin)) spin.QueueFree();
            if (QaRunning) GD.Print("QA_DIP_HELD t=" + Time.GetTicksMsec() + " ready=" + hold.IsCompleted);
            if (note is not null && IsInstanceValid(note)) note.QueueFree();
            tw = shot.CreateTween();
        }
        tw.TweenProperty(shot, "modulate:a", 0f, up).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tw.TweenCallback(Callable.From(() => { if (QaRunning) GD.Print("QA_DIP_DONE t=" + Time.GetTicksMsec()); shot.QueueFree(); if (curtain == shot) curtain = null; }));
    }

    // Modal dialog with dimmed backdrop. Returns the backdrop so callers can close it.
    public Control Modal(Control content, bool dismissOnBackdrop = true, float dim = 0.62f)
    {
        Peek.Hide();   // a hover text box never stays on top of a dialog
        var scrim = new ColorRect { Color = new Color(0, 0, 0, dim), MouseFilter = MouseFilterEnum.Stop };
        overlayLayer.AddChild(scrim); scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // If the overlay is busy notifying its children (a dialog opened from inside another dialog's event), the add is
        // refused; add it on the next idle moment instead, or the dialog would never appear and its caller would wait forever.
        if (scrim.GetParent() is null) overlayLayer.CallDeferred(Node.MethodName.AddChild, scrim);
        scrim.SetMeta("dismissible", dismissOnBackdrop);
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        scrim.AddChild(center); center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // a dialog taller (or wider) than the screen scrolls instead of running off it (bug #24: at 115 % on a phone the
        // settings' buttons were out of reach); while it fits, the scroll box is exactly the dialog's size
        var fitBox = new ScrollContainer { MouseFilter = MouseFilterEnum.Stop, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        center.AddChild(fitBox); fitBox.AddChild(content);
        void Fit()
        {
            if (!IsInstanceValid(fitBox) || !IsInstanceValid(content)) return;
            var view = GetViewport().GetVisibleRect().Size; var m = content.GetCombinedMinimumSize();
            bool tall = m.Y > view.Y + 0.5f, wide = m.X > view.X + 0.5f;   // a full-screen backdrop (exactly the view) does not scroll
            fitBox.VerticalScrollMode = tall ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.Disabled;
            fitBox.HorizontalScrollMode = wide ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.Disabled;
            fitBox.CustomMinimumSize = new Vector2(wide ? view.X : 0, tall ? view.Y : 0);
        }
        Action onFrame = Fit; GetTree().ProcessFrame += onFrame;
        fitBox.TreeExiting += () => GetTree().ProcessFrame -= onFrame;
        if (dismissOnBackdrop) scrim.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right }) CloseModal(scrim); };
        content.MouseFilter = MouseFilterEnum.Pass;   // clicks on it stop at its scroll box (not the backdrop); a drag on it scrolls a dialog that does not fit
        scrim.Modulate = new Color(1, 1, 1, 0); CreateTween().TweenProperty(scrim, "modulate:a", 1f, 0.14f);
        return scrim;
    }
    // A modal can be closed twice (the reveal's button and its QA timer both call this): only remove it while it is still on the overlay.
    // It fades out as it faded in (Yunke 11-22: dialogs vanished at once); from the first moment it no longer counts as open
    // and lets clicks through, so what comes next is not held up by it.
    public void CloseModal(Control scrim)
    {
        if (!IsInstanceValid(scrim) || scrim.GetParent() != overlayLayer || scrim.IsQueuedForDeletion() || scrim.HasMeta("closing")) return;
        scrim.SetMeta("closing", true); scrim.RemoveMeta("dismissible"); scrim.MouseFilter = MouseFilterEnum.Ignore;
        foreach (var c in scrim.FindChildren("*", "Control", true, false).OfType<Control>()) c.MouseFilter = MouseFilterEnum.Ignore;
        if (S?.Settings.ReducedMotion == true) { overlayLayer.RemoveChild(scrim); scrim.QueueFree(); return; }
        ulong t0 = Time.GetTicksMsec();
        var tw = scrim.CreateTween(); tw.TweenProperty(scrim, "modulate:a", 0f, 0.14f).SetTrans(Tween.TransitionType.Sine);
        tw.TweenCallback(Callable.From(() => { if (QaRunning) GD.Print("QA_MODAL_FADED ms=" + (Time.GetTicksMsec() - t0)); if (IsInstanceValid(scrim)) { if (scrim.GetParent() == overlayLayer) overlayLayer.RemoveChild(scrim); scrim.QueueFree(); } }));
    }
    public void CloseAllModals() { foreach (var c in overlayLayer.GetChildren()) { overlayLayer.RemoveChild(c); c.QueueFree(); } }
    public bool HasModal => overlayLayer.GetChildren().Any(c => c is Control k && k.HasMeta("dismissible") && !k.IsQueuedForDeletion());
    Control? TopModal => overlayLayer.GetChildren().OfType<Control>().LastOrDefault(k => k.HasMeta("dismissible") && !k.IsQueuedForDeletion());
    public Control Overlay => overlayLayer;

    public void Toast(string text, bool error = false, double seconds = 2.6)
    {
        var l = Style.Text(text, 22, error ? Style.Danger : Style.Paper);
        var p = new PanelContainer(); p.AddThemeStyleboxOverride("panel", Style.Box(new Color(0.07f, 0.065f, 0.06f, 0.95f), error ? Style.Danger : Style.Brass, 1, 8, 22, 10));
        p.AddChild(l); toastLayer.AddChild(p);
        p.ResetSize(); p.Position = new Vector2((GetViewportRect().Size.X - p.Size.X) / 2, 96);
        var tw = CreateTween(); tw.TweenInterval(seconds); tw.TweenProperty(p, "modulate:a", 0f, 0.4f); tw.TweenCallback(Callable.From(() => p.QueueFree()));
    }

    // QA script: --script=step1;step2 drives the UI for automated captures.
    async Task RunScript()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--script=")) scriptSteps.AddRange(arg[9..].Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var step in scriptSteps)
        {
            await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
            await QaScript.Run(this, step);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest && S is not null) { _ = S.DisposeAsync(); }
    }

    public void Capture(string path, bool quit = false)
    {
        // draw this frame now: with the screen locked or the window hidden the viewport texture otherwise stays stale
        try { RenderingServer.ForceDraw(false); } catch (Exception e) { GD.PushWarning("force draw: " + e.Message); }
        var image = GetViewport().GetTexture().GetImage();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        image.SavePng(path);
        GD.Print("CAPTURED t=" + Time.GetTicksMsec() + " " + path + " " + image.GetWidth() + "x" + image.GetHeight());
        if (quit) GetTree().Quit();
    }

    static readonly bool TraceInput = OS.GetCmdlineUserArgs().Contains("--trace-input") || OS.GetCmdlineArgs().Contains("--trace-input");
    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && HasModal)
        {
            var top = TopModal;
            if (top is not null && top.GetMeta("dismissible").AsBool()) CloseModal(top);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (TraceInput && e is InputEventMouseButton { Pressed: true } mb)
        {
            var hovered = GetViewport().GuiGetHoveredControl();
            GD.Print("MOUSE " + mb.ButtonIndex + " at " + mb.Position + " -> " + (hovered is null ? "null" : hovered.GetType().Name + ":" + hovered.Name + (hovered is Button b ? "[" + b.Text + "]" : "")));
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.F12 })
            Capture(Path.Combine(ExportedWindows ? DataRoot : Root, "evidence", "manual", DateTime.Now.ToString("HHmmss") + ".png"));   // the install folder may be read-only
    }
}
