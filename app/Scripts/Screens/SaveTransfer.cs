using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Saves in and out of the game (Yunke 11-23: "需要加一个导入存档的功能，能从json导入存档，安卓最好能选择本地文件。还可以添加一个
// 从电脑通过eos传送存档给手机apk版的功能"):
//   - export a save to a .json file, and import one (a file this game or the html version wrote: the rules core's save);
//     the file is picked with the system's own file window (on Android its document picker);
//   - send a save from this computer to a phone over EOS: the computer opens a small room and shows its code, the phone
//     enters it under 从电脑接收, the save travels over the direct connection and is imported there as a new save.
public static class SaveTransfer
{
    // the words name the other device (Yunke 11-23: "按钮文字改成按平台显示"): both ends can send and receive, so on a phone
    // it is 发送到电脑 / 从电脑接收, on a computer 发送到手机 / 从手机接收. QA: --qa-phone-labels shows the phone's words.
    public static bool OnPhone => OS.GetName() is "Android" or "iOS" || OS.GetCmdlineUserArgs().Contains("--qa-phone-labels");
    public static string Other => OnPhone ? "电脑" : "手机";
    public static string This => OnPhone ? "手机" : "电脑";

    static Services S => App.I.S!;

    // ---- files ----------------------------------------------------------------------------------------------------------

    static FileDialog Dialog(FileDialog.FileModeEnum mode, string title)
    {
        var d = new FileDialog
        {
            FileMode = mode, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = title,
            Filters = new[] { "*.json ; 存档文件" }, Size = new Vector2I(1200, 760),
        };
        App.I.Overlay.AddChild(d);
        d.Canceled += () => d.QueueFree();
        return d;
    }

    public static void Export(SavedGameSummary g, string title)
    {
        var d = Dialog(FileDialog.FileModeEnum.SaveFile, "导出存档");
        d.CurrentFile = Safe(title) + ".json";
        d.FileSelected += path => { d.QueueFree(); _ = ExportTo(g, path); };
        d.PopupCentered();
    }

    public static async Task<bool> ExportTo(SavedGameSummary g, string path)
    {
        try
        {
            var st = await S.Backend.Open(Guid.Parse(g.Id));
            var save = WithGameFiles(S.Backend.SavedDocument(st), g.Id);
            using (var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
            {
                if (f is null) throw new InvalidOperationException("无法写入：" + Godot.FileAccess.GetOpenError());
                f.StoreString(save.GetRawText());
            }
            GD.Print("SAVE_EXPORTED " + path);
            App.I.Toast("已导出存档：" + System.IO.Path.GetFileName(path), false, 3.5);
            return true;
        }
        catch (Exception e) { App.I.Toast("导出失败：" + e.Message, true, 5); GD.Print("SAVE_EXPORT_FAIL " + e.Message); return false; }
    }

    public static void Import(Action done)
    {
        var d = Dialog(FileDialog.FileModeEnum.OpenFile, "导入存档");
        d.FileSelected += path => { d.QueueFree(); _ = ImportFrom(path, done); };
        d.PopupCentered();
    }

    public static async Task<Guid?> ImportFrom(string path, Action? done = null)
    {
        try
        {
            // Godot's own file access also opens what Android's document picker hands back
            string text = Godot.FileAccess.FileExists(path) || path.StartsWith("content://") ? Godot.FileAccess.GetFileAsString(path) : "";
            if (text.Length == 0) throw new InvalidOperationException("文件是空的或无法读取");
            string name = System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/').Last());
            var id = await ImportText(text, name.Length > 0 ? name : "导入的存档");
            done?.Invoke();
            return id;
        }
        catch (Exception e) { App.I.Toast("导入失败：" + e.Message, true, 6); GD.Print("SAVE_IMPORT_FAIL " + e.Message); return null; }
    }

    // The saves a file holds (Yunke 11-23: "从json导入和导出存档，类似旧版的导出调试信息"). Every one is the rules core's own
    // save ({gameId, saveFormat, snapshot}); the files around it:
    //   - this game's 导出 (继续调查): the save itself;
    //   - this game's 导出调试信息 (the table's menu, ExportDebug): the save under "save", with the view beside it;
    //   - the old version's 导出调试信息 / 本机存档 (arkham-native-save-*.json, kind arkham-local-save): the save as a string
    //     under "saveJson"; its diagnostic file (kind arkham-native-debug) holds one of those under "snapshot";
    //   - the old version's personal backup: several arkham-local-save under "games".
    // The old version's online server export (campaignData) is the server's own game record, not a rules-core save.
    static List<(JsonElement Save, string Title)> SavesIn(JsonElement root, string title)
    {
        var found = new List<(JsonElement, string)>();
        void Local(JsonElement item, string fallback)
        {
            string json = J.String(item, "saveJson");
            if (json.Length == 0) throw new InvalidOperationException("旧版存档里没有存档内容");
            using var d = JsonDocument.Parse(json);
            found.Add((d.RootElement.Clone(), fallback));
        }
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("不是存档文件");
        string kind = J.String(root, "kind");
        if (root.TryGetProperty("snapshot", out var snap) && root.TryGetProperty("saveFormat", out _)) found.Add((root, title));   // the save itself
        else if (kind == "arkham-local-save") Local(root, title);
        else if (kind == "arkham-native-debug" && J.String(snap, "kind") == "arkham-local-save") Local(snap, title);
        else if (root.TryGetProperty("save", out var inner) && inner.ValueKind == JsonValueKind.Object) found.Add((inner, title));
        else if (J.Get(root, "games") is { ValueKind: JsonValueKind.Array } games)
        {
            int n = 0;
            foreach (var g in games.EnumerateArray()) if (J.String(g, "kind") == "arkham-local-save") { n++; Local(g, title + " · " + n); }
        }
        else if (root.TryGetProperty("campaignData", out _))
            throw new InvalidOperationException("这是旧版联网服务器导出的战役记录，不是本机存档，新版不能导入。请在旧版本机游戏里用“导出调试信息”导出本机存档。");
        else if (kind == "arkham-client-diagnostic" || kind == "arkham-ui-diagnostic")
            throw new InvalidOperationException("这份调试信息只有画面内容（联机客人导出），没有存档，不能导入");
        if (found.Count == 0) throw new InvalidOperationException("文件里没有找到存档");
        return found;
    }

    // ---- the game's own files beside the save (Yunke 11-23: "把存档导出也带上侧卡组和升级记录") ------------------------------
    // setups/<id>…: the seats' decks, each seat's upgraded deck (-upgrade-N), its build metadata (-upgrade-meta-N), the side deck
    // (-upgrade-side-N), experience left over (-xp-carry-N), the upgrade notes, the story read, the trauma notes. The rules core
    // keeps none of these, so an export carries them under one extra field beside the core's save; the import takes the field
    // off before the core sees the save and writes the files back under the new save's id. A file without the field (the html
    // version's, an older export) imports as before.
    const string GameFilesKey = "arkhamuiFiles";
    static string SetupsDir => System.IO.Path.Combine(App.I.DataRoot, "setups");
    static readonly System.Text.RegularExpressions.Regex GameFileSuffix = new(@"^(\.json|-[a-z0-9-]{1,40}\.json)$");

    static JsonElement WithGameFiles(JsonElement save, string gameId)
    {
        try
        {
            if (save.ValueKind != JsonValueKind.Object || !System.IO.Directory.Exists(SetupsDir)) return save;
            var files = new System.Text.Json.Nodes.JsonObject();
            foreach (var f in System.IO.Directory.GetFiles(SetupsDir, gameId + "*").OrderBy(f => f, StringComparer.Ordinal))
            {
                string suffix = System.IO.Path.GetFileName(f)[gameId.Length..];
                if (GameFileSuffix.IsMatch(suffix)) files[suffix] = System.IO.File.ReadAllText(f);
            }
            if (files.Count == 0) return save;
            var o = System.Text.Json.Nodes.JsonNode.Parse(save.GetRawText())!.AsObject();
            o[GameFilesKey] = files;
            GD.Print("SAVE_FILES_OUT " + files.Count + " " + string.Join(",", files.Select(p => p.Key)));
            return JsonSerializer.SerializeToElement(o);
        }
        catch (Exception e) { GD.PushWarning("save files: " + e.Message); return save; }
    }

    // the files an export carries (suffix → text), and the text without them (what the core is given)
    static (string Text, Dictionary<string, string> Files) TakeGameFiles(string text)
    {
        var files = new Dictionary<string, string>();
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(text) is not System.Text.Json.Nodes.JsonObject o || o[GameFilesKey] is not System.Text.Json.Nodes.JsonObject fo) return (text, files);
            foreach (var (suffix, v) in fo)
                if (GameFileSuffix.IsMatch(suffix) && v is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<string>(out var content)) files[suffix] = content;
            o.Remove(GameFilesKey);
            return (o.ToJsonString(), files);
        }
        catch { return (text, files); }
    }

    static void PutGameFiles(Guid id, Dictionary<string, string> files)
    {
        if (files.Count == 0) return;
        try
        {
            System.IO.Directory.CreateDirectory(SetupsDir);
            foreach (var (suffix, content) in files) System.IO.File.WriteAllText(System.IO.Path.Combine(SetupsDir, id.ToString("D") + suffix), content);
            GD.Print("SAVE_FILES_IN " + id + " " + files.Count + " " + string.Join(",", files.Keys));
        }
        catch (Exception e) { GD.PushWarning("save files: " + e.Message); }
    }

    // the rules core checks each save like any import; each becomes a new save in the list
    static async Task<Guid> ImportText(string text, string title)
    {
        if (!S.HasCore) throw new InvalidOperationException("这台设备没有规则核心，不能导入存档");
        (text, var gameFiles) = TakeGameFiles(text);
        using var doc = JsonDocument.Parse(text);
        var saves = SavesIn(doc.RootElement, title);
        Guid last = Guid.Empty; int ok = 0; string? failed = null;
        foreach (var (save, name) in saves)
        {
            try
            {
                last = await S.Backend.ImportCrafted(save.Clone(), name); ok++;
                GD.Print("SAVE_IMPORTED " + last + " " + name);
                if (saves.Count == 1) PutGameFiles(last, gameFiles);
            }
            catch (Exception e) { failed ??= e.Message; GD.Print("SAVE_IMPORT_FAIL " + name + " " + e.Message); if (saves.Count == 1) throw new InvalidOperationException(CoreWords(e.Message)); }
        }
        if (ok == 0) throw new InvalidOperationException(CoreWords(failed ?? ""));
        App.I.Toast(saves.Count == 1 ? "已导入存档：" + title : "已导入 " + ok + " / " + saves.Count + " 个存档" + (failed is null ? "" : "（其余失败：" + CoreWords(failed) + "）"), false, 4);
        return last;
    }

    // the core's refusal in words a player can act on
    static string CoreWords(string m) =>
        m.Contains("Invalid import envelope") ? "存档格式不对（不是规则核心的存档）"
        : m.Contains("source", StringComparison.OrdinalIgnoreCase) && m.Contains("mismatch", StringComparison.OrdinalIgnoreCase) ? "存档来自不兼容的规则核心版本：" + m
        : m;

    // ---- 导出调试信息 (the table's menu) ----------------------------------------------------------------------------------
    // The old version's 导出调试信息: the game as it is now, to send with a bug report or to carry to another computer. The
    // file holds the rules core's save (importable, as above) and the table's view of it; an online guest has no save of its
    // own (the host keeps it), so its file is the view alone.
    public static void ExportDebug(TableState st, IGameBackend backend, string title)
    {
        var d = Dialog(FileDialog.FileModeEnum.SaveFile, "导出调试信息");
        d.CurrentFile = Safe(title) + " 调试信息.json";
        d.FileSelected += path => { d.QueueFree(); ExportDebugTo(st, backend, title, path); };
        d.PopupCentered();
    }

    public static bool ExportDebugTo(TableState st, IGameBackend backend, string title, string path)
    {
        try
        {
            JsonElement? save = backend is PrototypeBackend pb && !OnlineGame.IsGuest ? pb.SavedDocument(st) : null;
            var doc = new System.Text.Json.Nodes.JsonObject
            {
                ["formatVersion"] = 1,
                ["kind"] = save is null ? "arkham-ui-diagnostic" : "arkham-ui-debug",
                ["title"] = title,
                ["gameId"] = st.Id.ToString("D"),
                ["sequence"] = st.Sequence,
                ["coreRevision"] = st.Revision,
                ["app"] = Updater.VersionTag,
                ["platform"] = OS.GetName(),
                ["exportedAt"] = DateTime.UtcNow.ToString("o"),
                ["online"] = OnlineGame.Active ? (OnlineGame.IsHost ? "host" : "guest") : "local",
            };
            if (save is { } s) doc["save"] = System.Text.Json.Nodes.JsonNode.Parse(s.GetRawText());
            doc["view"] = System.Text.Json.Nodes.JsonNode.Parse(st.View.GetRawText());
            using (var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
            {
                if (f is null) throw new InvalidOperationException("无法写入：" + Godot.FileAccess.GetOpenError());
                f.StoreString(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            GD.Print("DEBUG_EXPORTED " + (save is null ? "view-only " : "") + path);
            App.I.Toast("已导出调试信息：" + System.IO.Path.GetFileName(path) + (save is null ? "（联机客人只含画面）" : ""), false, 4);
            return true;
        }
        catch (Exception e) { App.I.Toast("导出失败：" + e.Message, true, 5); GD.Print("DEBUG_EXPORT_FAIL " + e.Message); return false; }
    }

    static string Safe(string s) => string.Concat(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c == '·' ? ' ' : c)).Trim();

    // ---- computer → phone over EOS --------------------------------------------------------------------------------------

    static JsonElement sending; static string sendingTitle = ""; static Control? sendScrim; static Label? sendStatus;
    static readonly HashSet<string> sentTo = new();

    // the computer: a room just for this save; whoever joins gets the save
    public static async void Send(SavedGameSummary g, string title)
    {
        if (Online.Service is not { } s || OnlineGame.Link is null) { App.I.Toast("联机未就绪，不能发送", true); return; }
        try
        {
            var st = await S.Backend.Open(Guid.Parse(g.Id));
            sending = WithGameFiles(S.Backend.SavedDocument(st), g.Id).Clone(); sendingTitle = title; sentTo.Clear();
        }
        catch (Exception e) { App.I.Toast("读取存档失败：" + e.Message, true, 5); return; }
        OnlineGame.End(null);
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(820, 0) }; col.AddThemeConstantOverride("separation", 22);
        col.AddChild(Style.Title("发送存档到" + Other, 44));
        col.AddChild(Style.Text(title, 28, Style.Brass, Style.Kai));
        var code = Style.Text("正在建房…", 60, Style.Amber, Style.Sans); col.AddChild(code);
        sendStatus = Style.Text("在" + Other + "上打开 继续调查 → 从" + This + "接收，输入上面的房间码", 26, Style.Paper, Style.Kai);
        sendStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart; sendStatus.CustomMinimumSize = new Vector2(760, 0); col.AddChild(sendStatus);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; col.AddChild(row);
        Control scrim = null!;
        Action? watch = null;
        void Close() { if (watch is not null) s.RoomChanged -= watch; sending = default; sendStatus = null; sendScrim = null; if (GodotObject.IsInstanceValid(scrim)) App.I.CloseModal(scrim); _ = s.LeaveAsync(); }
        var close = Ui.Button("关闭", Close, 26); close.CustomMinimumSize = new Vector2(180, 70); row.AddChild(close);
        scrim = App.I.Modal(Ui.Panel(col, 44), dismissOnBackdrop: false); sendScrim = scrim;
        try { code.Text = await s.HostAsync(null, 2, "存档传送"); }
        catch (Exception e) { code.Text = "建房失败"; sendStatus.Text = e.Message; return; }
        // a phone in the room: send it the save (again if it comes back)
        watch = () =>
        {
            if (s.Room is not { } r || sending.ValueKind == JsonValueKind.Undefined) return;
            foreach (var m in r.Members.Where(m => m.Id != s.LocalId && !sentTo.Contains(m.Id)))
            {
                sentTo.Add(m.Id);
                if (sendStatus is not null) sendStatus.Text = m.Name + " 已进入，正在发送存档…";
                OnlineGame.Link?.Send(m.Id, OnlineGame.Envelope("evt", 0, "savefile", JsonSerializer.SerializeToElement(new { title = sendingTitle, save = sending })));
                GD.Print("SAVE_SENT to " + m.Id);
            }
        };
        s.RoomChanged += watch;
    }

    // the phone said it has the save
    public static void Acked(string from, JsonElement a)
    {
        bool ok = J.Get(a, "ok").ValueKind == JsonValueKind.True;
        GD.Print("SAVE_ACK " + from + " ok=" + ok);
        if (sendStatus is not null && GodotObject.IsInstanceValid(sendStatus))
            sendStatus.Text = ok ? Other + "已收到并导入存档，可以关闭了。" : Other + "导入失败：" + J.String(a, "error");
    }

    // ---- the phone ---------------------------------------------------------------------------------------------------

    static Action? onReceived; static Label? recvStatus;

    public static void Receive(Action refresh)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(820, 0) }; col.AddThemeConstantOverride("separation", 24);
        col.AddChild(Style.Title("从" + Other + "接收存档", 44));
        col.AddChild(Style.Text("在" + Other + "上 继续调查 → 发送到" + This + "，输入" + Other + "上显示的房间码", 26, Style.Paper, Style.Kai));
        var code = new LineEdit { PlaceholderText = "房间码", MaxLength = 8, CustomMinimumSize = new Vector2(0, 96), Alignment = HorizontalAlignment.Center };
        code.AddThemeFontSizeOverride("font_size", 48); if (Style.Sans is not null) code.AddThemeFontOverride("font", Style.Sans);
        code.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.4f, false)); code.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.55f, true));
        col.AddChild(code);
        recvStatus = Style.Text(Online.Available ? "" : Online.Reason, 24, Style.PaperDim, Style.Kai); recvStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart; recvStatus.CustomMinimumSize = new Vector2(760, 0); col.AddChild(recvStatus);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; row.AddThemeConstantOverride("separation", 16); col.AddChild(row);
        Control scrim = null!;
        void Close() { onReceived = null; recvStatus = null; if (GodotObject.IsInstanceValid(scrim)) App.I.CloseModal(scrim); _ = Online.Service?.LeaveAsync(); }
        var cancel = Ui.Button("关闭", Close, 26); cancel.CustomMinimumSize = new Vector2(180, 80); row.AddChild(cancel);
        Button go = null!;
        go = Ui.Button("接收", async () =>
        {
            if (Online.Service is not { } s || code.Text.Trim().Length == 0 || go.Disabled) return;
            go.Disabled = true; if (recvStatus is not null) recvStatus.Text = "正在进入房间…";
            OnlineGame.End(null);
            onReceived = () => { refresh(); };
            try { await s.JoinAsync(code.Text.Trim().ToUpperInvariant()); if (recvStatus is not null) recvStatus.Text = "已进入，正在接收存档…"; }
            catch (Exception e) { if (recvStatus is not null) recvStatus.Text = "进入失败：" + e.Message; go.Disabled = false; }
        }, 28, true);
        go.CustomMinimumSize = new Vector2(220, 80); Online.Gate(go); row.AddChild(go);
        scrim = App.I.Modal(Ui.Panel(col, 44), dismissOnBackdrop: false);
    }

    // the save arrived: import it, tell the computer, leave the room
    public static async void Received(string from, JsonElement a)
    {
        string title = J.String(a, "title"); if (title.Length == 0) title = "从" + Other + "接收的存档";
        GD.Print("SAVE_RECEIVED from " + from);
        string? error = null;
        try { await ImportText(J.Get(a, "save").GetRawText(), title); }
        catch (Exception e) { error = e.Message; }
        OnlineGame.Link?.Send(from, OnlineGame.Envelope("evt", 0, "savefile-ok", JsonSerializer.SerializeToElement(new { ok = error is null, error = error ?? "" })));
        if (recvStatus is not null && GodotObject.IsInstanceValid(recvStatus)) recvStatus.Text = error is null ? "已收到并导入：" + title : "导入失败：" + error;
        if (error is not null) App.I.Toast("导入失败：" + error, true, 6);
        onReceived?.Invoke();
        _ = Online.Service?.LeaveAsync();
    }
}
