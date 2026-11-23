using Godot;

namespace ArkhamUI;

// The online screens (Online.cs): join by room code, and the room where the players gather before a game starts or a
// saved game goes on. They open only once online play is available; QA can show them with sample players (online-demo).
// The room is live (Yunke 11-22: "电脑版的这里看不到联机码"): opening it as host creates the EOS room, its code shows at
// once, and the seats redraw whenever someone joins, sits down, picks a deck, gets ready or leaves.
// Sized for a phone too (Yunke 11-22: "字太小了，而且每个位置要有背景色，方便手机操作"): large text, every seat a panel of
// its own, big buttons. For a new game every player picks their own deck from their own library ("客人不能选自己的卡组").
public static class OnlineDialogs
{
    // a short line under a title saying why online play cannot be used yet
    static Label Why()
    {
        var l = Style.Text(Online.Reason, 24, Style.PaperDim, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(720, 0);
        l.Visible = !Online.Available; return l;
    }

    static Button Big(string text, Action onPress, int size = 30, bool primary = false, float minWidth = 200)
    {
        var b = Ui.Button(text, onPress, size, primary); b.CustomMinimumSize = new Vector2(minWidth, 80); return b;
    }

    public static void Join()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(820, 0) }; col.AddThemeConstantOverride("separation", 24);
        col.AddChild(Style.Title("加入联机游戏", 46));
        col.AddChild(Style.Text("输入主持人给你的房间码", 30, Style.Paper, Style.Kai));
        var code = new LineEdit { PlaceholderText = "房间码", MaxLength = 8, CustomMinimumSize = new Vector2(0, 96), Alignment = HorizontalAlignment.Center };
        code.AddThemeFontSizeOverride("font_size", 48); if (Style.Sans is not null) code.AddThemeFontOverride("font", Style.Sans);   // (the symbols font turns letters into icons)
        code.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.4f, false)); code.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.55f, true));
        code.Editable = Online.Available; col.AddChild(code);
        col.AddChild(Why());
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; row.AddThemeConstantOverride("separation", 16); col.AddChild(row);
        Control scrim = null!;
        row.AddChild(Big("取消", () => App.I.CloseModal(scrim)));
        Button go = null!;
        go = Big("加入", async () =>
        {
            if (Online.Service is not { } s || code.Text.Trim().Length == 0 || go.Disabled) return;
            go.Disabled = true; go.Text = "正在加入…";
            OnlineGame.End(null);   // an earlier online game is over
            try { await s.JoinAsync(code.Text.Trim().ToUpperInvariant()); App.I.CloseModal(scrim); if (s.Room is not null) Live(); }
            catch (Exception e) { App.I.Toast("加入失败：" + e.Message, true, 4); go.Disabled = false; go.Text = "加入"; }
        }, 30, true, 260);
        Online.Gate(go); row.AddChild(go);
        code.TextSubmitted += _ => go.EmitSignal(BaseButton.SignalName.Pressed);
        scrim = App.I.Modal(Ui.Panel(col, 40));
    }

    // Host a room for a new game (gameId null, up to four seats, each player brings a deck) or a saved one (its seats as
    // saved): the room opens at once with "正在建房…" in place of the code, then fills in when EOS answers.
    public static async void Host(string title, Guid? gameId, int seats, string[]? seatCodes = null)
    {
        if (Online.Service is not { } s) return;
        // a game played online earlier in this run is over once a new room opens (Yunke 11-22: after a new game, a room to
        // go on with a save stayed at "正在建立直连…" — the old game still counted as running, so the host never checked)
        OnlineGame.End(null);
        OnlineGame.SeatCodes = seatCodes ?? Array.Empty<string>();
        OnlineGame.ClearRoomDecks();
        var (scrim, _) = Show(new OnlineRoom("", gameId, title, seats, Array.Empty<OnlineMember>()), waiting: true);
        try
        {
            await s.HostAsync(gameId, seats, title);
            if (s.Room is not null && s.Room.Members.All(m => m.Seat != 0)) await s.ClaimSeatAsync(0, "", "");   // the host sits first
            OnlineGame.PlanDecks.Clear();
            // a saved online game: who controlled the seats nobody sits in last time (大家一起, or a player who may join again)
            if (gameId is { } g)
                foreach (var (seat, owner) in OnlineGame.SavedOwners(g))
                    if (seat > 0 && owner.Length > 0 && owner != OnlineGame.Ai) await s.SetSeatPlanAsync(seat, "", "", owner);
        }
        catch (Exception e)
        {
            if (GodotObject.IsInstanceValid(scrim)) App.I.CloseModal(scrim);
            App.I.Toast("建房失败：" + e.Message, true, 5);
            return;
        }
        if (!GodotObject.IsInstanceValid(scrim)) { _ = s.LeaveAsync(); return; }   // closed while the room was being made
        App.I.CloseModal(scrim);
        Live();
    }

    // the room this player is in, redrawn on every change; closing it leaves the room
    static void Live()
    {
        if (Online.Service is not { } s || s.Room is not { } r) return;
        var (scrim, body) = Show(r, waiting: false);
        void Redraw()
        {
            if (!GodotObject.IsInstanceValid(body)) return;
            if (s.Room is not { } now) { App.I.CloseModal(scrim); App.I.Toast("已离开房间（房间已关闭或连接中断）", true, 4); return; }
            foreach (var c in body.GetChildren()) c.QueueFree();
            Fill(body, now, demo: false, waiting: false, scrim);
        }
        Action handler = () => Callable.From(Redraw).CallDeferred();
        s.RoomChanged += handler;
        // the host checks the direct connection to everyone every few seconds (OnlineGame.PingRoom)
        var pinger = new Godot.Timer { WaitTime = 2.5, Autostart = true };
        pinger.Timeout += OnlineGame.PingRoom; scrim.AddChild(pinger);
        OnlineGame.PingRoom();
        OnlineGame.RoomDecksChanged += handler;
        scrim.TreeExiting += () => { s.RoomChanged -= handler; OnlineGame.RoomDecksChanged -= handler; };
    }

    // The room: its code to share, the seats with who sits there and their investigator, ready marks; the host starts.
    public static void Room(OnlineRoom room, bool demo = false) => Show(room, waiting: false, demo);

    static (Control Scrim, VBoxContainer Body) Show(OnlineRoom room, bool waiting, bool demo = false)
    {
        var body = new VBoxContainer { CustomMinimumSize = new Vector2(1500, 0) }; body.AddThemeConstantOverride("separation", 26);
        Control scrim = App.I.Modal(Ui.Panel(body, 44), dismissOnBackdrop: false);
        Fill(body, room, demo, waiting, scrim);
        return (scrim, body);
    }

    static void Fill(VBoxContainer col, OnlineRoom room, bool demo, bool waiting, Control scrim)
    {
        var s = Online.Service;
        string myId = s?.LocalId ?? "";
        var me = room.Members.FirstOrDefault(m => m.Id.Length > 0 && m.Id == myId);
        bool iHost = me?.Host == true || (waiting && !demo);
        bool newGame = room.GameId is null;
        var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 24); col.AddChild(head);
        var titles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; titles.AddThemeConstantOverride("separation", 6); head.AddChild(titles);
        titles.AddChild(Style.Title("联机房间", 52));
        titles.AddChild(Style.Text(newGame ? room.Title + " · 新的调查" : room.Title + " · 继续存档", 30, Style.Brass, Style.Kai));
        // the room code, large, with a copy button
        var codeBox = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; codeBox.AddThemeConstantOverride("separation", 4); head.AddChild(codeBox);
        codeBox.AddChild(Style.Text("房间码", 26, Style.PaperDim, Style.Kai));
        var codeRow = new HBoxContainer(); codeRow.AddThemeConstantOverride("separation", 16); codeBox.AddChild(codeRow);
        if (waiting) codeRow.AddChild(Style.Text("正在建房…", 40, Style.PaperDim, Style.Kai));
        else
        {
            codeRow.AddChild(Style.Text(room.Code, 60, Style.Amber, Style.Sans));
            var copy = Big("复制", () => { DisplayServer.ClipboardSet(room.Code); App.I.Toast("已复制房间码"); }, 26, false, 120);
            copy.Disabled = room.Code.Length == 0; copy.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; codeRow.AddChild(copy);
        }
        // the seats: each its own panel; an empty one can be taken
        var seats = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; seats.AddThemeConstantOverride("separation", 22); col.AddChild(seats);
        for (int i = 0; i < Math.Max(1, room.Seats); i++)
        {
            int seat = i;
            var m = room.Members.FirstOrDefault(x => x.Seat == i);
            bool mine = m is not null && m.Id == myId;
            var cell = new PanelContainer { CustomMinimumSize = new Vector2(330, 470) };
            var box = Ui.SoftPanel(m is null ? 0.55f : 0.8f, 18, 18);
            box.BgColor = m is null ? new Color(0.13f, 0.11f, 0.09f, 0.92f) : mine ? new Color(0.24f, 0.17f, 0.08f, 0.96f) : new Color(0.17f, 0.14f, 0.11f, 0.96f);
            box.BorderColor = mine ? Style.Amber : new Color(Style.Brass, m is null ? 0.25f : 0.55f);
            box.SetBorderWidthAll(mine ? 3 : 2); box.SetCornerRadiusAll(16);
            cell.AddThemeStyleboxOverride("panel", box);
            var c = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; c.AddThemeConstantOverride("separation", 12); cell.AddChild(c);
            Label Line(string text, int size, Color color) { var l = Style.Text(text, size, color, Style.Kai); l.HorizontalAlignment = HorizontalAlignment.Center; l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(290, 0); c.AddChild(l); return l; }
            Line("座位 " + (i + 1), 26, new Color(Style.PaperDim, 0.85f));
            var plan = room.Plan(i);
            string code = m?.InvestigatorCode.Length > 0 ? m.InvestigatorCode : !newGame && i < OnlineGame.SeatCodes.Length ? OnlineGame.SeatCodes[i] : newGame && plan is { } pl0 ? pl0.InvestigatorCode : "";
            if (code.Length > 0) { var pc = CardView.PortraitCard(code, 200); pc.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter; c.AddChild(pc); }
            // a seat nobody sits in that will be played (a new game: the host added an investigator; a saved game: its seat):
            // whoever the host gives it to controls it (Yunke 11-23: "联机和一人多控结合")
            bool played = m is null && (newGame ? plan is { InvestigatorCode.Length: > 0 } : i < OnlineGame.SeatCodes.Length);
            if (played)
            {
                if (newGame) Line(plan!.DeckName.Length > 0 ? plan.DeckName : "建房者的牌组", 24, Style.Brass);
                string ctrl = plan?.Controller ?? "";
                string hostName = room.Members.FirstOrDefault(x => x.Host)?.Name ?? "建房者";
                string who = ctrl == OnlineGame.Everyone ? "大家一起" : ctrl == OnlineGame.Ai ? "AI" : ctrl.Length == 0 ? hostName : room.Members.FirstOrDefault(x => x.Id == ctrl)?.Name ?? "（不在房间）";
                if (iHost || demo) c.AddChild(ControlPick(s, room, i, plan, demo || waiting));
                else Line("由 " + who + " 控制", 26, Style.Paper);
                if (newGame && (iHost || demo) && s is not null && !demo)
                {
                    var row2 = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; row2.AddThemeConstantOverride("separation", 10); c.AddChild(row2);
                    row2.AddChild(Big("更换", () => PickPlanned(s, seat), 24, false, 120));
                    row2.AddChild(Big("移除", async () =>
                    {
                        OnlineGame.PlanDecks.Remove(seat);
                        try { await s.SetSeatPlanAsync(seat, "", "", ""); } catch (Exception e) { App.I.Toast("操作失败：" + e.Message, true, 4); }
                    }, 24, false, 120));
                }
                // a saved game: a player may still sit here and play it themselves
                if (!newGame && !demo && !waiting && me is not null && s is not null)
                {
                    var sit = Big(me.Seat < 0 ? "坐这里" : "换到这里", async () =>
                    {
                        try { await s.ClaimSeatAsync(seat, me.InvestigatorCode, me.DeckName); } catch (Exception e) { App.I.Toast("入座失败：" + e.Message, true, 4); }
                    }, 24, false, 200);
                    sit.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter; c.AddChild(sit);
                }
            }
            else if (m is null)
            {
                Line("等待加入", 30, new Color(Style.PaperDim, 0.8f));
                // the host: an investigator for this seat from the host's own decks, played by whoever the host chooses
                if (newGame && iHost && !demo && !waiting && s is not null)
                {
                    var add = Big("添加调查员（托管）", () => PickPlanned(s, seat), 24, false, 260);
                    add.TooltipText = "没人坐的座位也可以上场：由建房者选调查员和牌组，再交给房间里任何一位玩家，或大家一起控制";
                    add.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter; c.AddChild(add);
                }
                if (!demo && !waiting && me is not null && s is not null)
                {
                    var sit = Big(me.Seat < 0 ? "坐这里" : "换到这里", async () =>
                    {
                        try { await s.ClaimSeatAsync(seat, me.InvestigatorCode, me.DeckName); } catch (Exception e) { App.I.Toast("入座失败：" + e.Message, true, 4); }
                    }, 28, false, 220);
                    sit.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter; c.AddChild(sit);
                }
            }
            else
            {
                Line(m.Name + (m.Host ? "（主持）" : "") + (mine ? "（你）" : ""), 30, Style.Paper);
                if (newGame) Line(m.DeckName.Length > 0 ? m.DeckName : "还没选调查员", 24, m.DeckName.Length > 0 ? Style.Brass : Style.PaperDim);
                if (!m.Host) Line(m.Ready ? "已准备" : "未准备", 26, m.Ready ? Style.Good : Style.PaperDim);
                // the direct connection the game will use: the host sees it per player, a player sees it to the host
                if (!demo && (iHost ? !m.Host : m.Host))
                {
                    bool ok = OnlineGame.Linked(m.Id);
                    // and how it travels: directly (same network or through the routers) or through Epic's relay (EosP2P.Path)
#if ARKHAM_EOS
                    string path = EosP2P.PathText(m.Id);
#else
                    string path = "";   // built without the EOS SDK: no relay information
#endif
                    Line(ok ? (path.Length > 0 ? path : "直连正常") : "正在建立直连…", 22, ok ? Style.Good : Style.Brass);
                }
                if (mine && newGame && !demo && s is not null)
                {
                    var pick = Big(m.DeckName.Length > 0 ? "更换调查员" : "选择调查员", () => PickDeck(s, m), 28, m.DeckName.Length == 0, 220);
                    pick.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter; c.AddChild(pick);
                }
            }
            seats.AddChild(cell);
        }
        // players in the room without a seat yet
        var standing = room.Members.Where(m => m.Seat < 0 || m.Seat >= room.Seats).Select(m => m.Name + (m.Id == myId ? "（你）" : "")).ToList();
        if (standing.Count > 0)
        {
            var st = Style.Text("还没入座：" + string.Join("、", standing), 26, Style.PaperDim, Style.Kai); st.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(st);
        }
        if (demo) { var d = Style.Text("（示意：联机尚未接通，玩家为示例）", 24, Style.PaperDim, Style.Kai); d.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(d); }
        var bottom = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; bottom.AddThemeConstantOverride("separation", 16); col.AddChild(bottom);
        string hint = iHost ? (OnlineGame.CanStart(room, out var whyNot) || waiting ? "" : whyNot) : "等主持人开始";
        var note = Style.Text(hint, 26, Style.PaperDim, Style.Kai); note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; note.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; bottom.AddChild(note);
        bottom.AddChild(Big("离开房间", () => { App.I.CloseModal(scrim); _ = Online.Service?.LeaveAsync(); }));
        if (!iHost)
        {
            bool ready = me?.Ready == true;
            var readyB = Big(ready ? "取消准备" : "准备", async () =>
            {
                try
                {
                    if (s is null) return;
                    if (!ready && newGame && me is not null && me.DeckName.Length == 0) { App.I.Toast("先选择调查员和牌组", true); return; }
                    if (!ready) OnlineGame.ResendDeck();   // the host must have this player's deck before the start
                    await s.SetReadyAsync(!ready);
                }
                catch (Exception e) { App.I.Toast("操作失败：" + e.Message, true, 4); }
            }, 30, !ready);
            readyB.Disabled = waiting || me is null; Online.Gate(readyB); bottom.AddChild(readyB);
        }
        if (iHost || demo)
        {
            var start = Big(newGame ? "开始调查" : "继续调查", () => OnlineGame.HostStart(room), 32, true, 280);
            bool can = OnlineGame.CanStart(room, out var why);
            start.Disabled = waiting || !can;
            start.TooltipText = start.Disabled && !waiting ? why : "";
            Online.Gate(start); bottom.AddChild(start);
        }
    }

    // this player's own investigator and deck for a new online game: the same group picture as playing alone (Yunke 11-22:
    // "多人的牌组选择界面也要是人物群像界面的选择"), with the investigators others took unavailable; the deck is chosen in
    // its notebook, from this player's own library
    static void PickDeck(IOnlineService s, OnlineMember me) => OpenStage(s, me, SelectionStage.Core);

    static void OpenStage(IOnlineService s, OnlineMember me, SelectionStage.Scene scene)
    {
        var S = App.I.S; if (S is null) return;
        var taken = (s.Room?.Members ?? Array.Empty<OnlineMember>()).Where(x => x.Id != me.Id && x.InvestigatorCode.Length > 0).Select(x => x.InvestigatorCode).ToList();
        SelectionStage stage = null!;
        stage = new SelectionStage(taken, async (code, deckKey) =>
        {
            string key = deckKey.Length > 0 ? deckKey : S.Decks.For(code).Select(d => d.Key).FirstOrDefault() ?? "";
            if (key.Length == 0 || !S.Decks.Has(key)) { App.I.Toast("这名调查员还没有牌组：先在牌组工坊里组一套", true, 4); return; }
            var deck = S.Decks.Get(key);
            string name = J.String(deck, "name"); if (name.Length == 0) name = S.Name(code);
            string inv = J.String(deck, "investigator_code"); if (inv.Length == 0) inv = code;
            OnlineGame.ChooseOwnDeck(key, deck);
            try { await s.ClaimSeatAsync(Math.Max(0, me.Seat), inv, name); } catch (Exception e) { App.I.Toast("选择失败：" + e.Message, true, 4); }
        }, scene);
        stage.SwitchTo = other => { stage.QueueFree(); OpenStage(s, me, other); };
        App.I.Overlay.AddChild(stage); stage.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    public static void QaPickDeck(IOnlineService s) => PickDeck(s, new OnlineMember("你", false, false, 0, ""));

    // who controls a seat nobody sits in: the host, any player in the room, everyone, or (later) the AI
    static Control ControlPick(IOnlineService? s, OnlineRoom room, int seat, OnlineSeatPlan? plan, bool locked)
    {
        var box = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; box.AddThemeConstantOverride("separation", 8);
        var l = Style.Text("控制", 24, Style.PaperDim, Style.Kai); l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; box.AddChild(l);
        var pick = new OptionButton { FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(210, 60) }; Ui.Soften(pick); pick.AddThemeFontSizeOverride("font_size", 22);
        var options = new List<(string Id, string Name)>();
        var host = room.Members.FirstOrDefault(x => x.Host);
        options.Add(("", (host?.Name ?? "建房者") + "（建房者）"));
        foreach (var m in room.Members.Where(x => !x.Host)) options.Add((m.Id, m.Name));
        options.Add((OnlineGame.Everyone, "大家一起"));
        options.Add((OnlineGame.Ai, "AI 控制（开发中）"));
        string now = plan?.Controller ?? "";
        if (!options.Any(o => o.Id == now)) options.Insert(1, (now, "（已离开的玩家）"));
        for (int k = 0; k < options.Count; k++) { pick.AddItem(options[k].Name); if (options[k].Id == OnlineGame.Ai) pick.SetItemDisabled(k, true); }
        pick.Selected = Math.Max(0, options.FindIndex(o => o.Id == now));
        pick.Disabled = locked || s is null;
        pick.ItemSelected += async idx =>
        {
            if (s is null) return;
            try { await s.SetSeatPlanAsync(seat, plan?.InvestigatorCode ?? "", plan?.DeckName ?? "", options[(int)idx].Id); }
            catch (Exception e) { App.I.Toast("操作失败：" + e.Message, true, 4); }
        };
        box.AddChild(pick);
        return box;
    }

    // the host picks the investigator and deck of a seat nobody sits in, from the host's own library
    static void PickPlanned(IOnlineService s, int seat, SelectionStage.Scene? scene = null)
    {
        var S = App.I.S; if (S is null || s.Room is not { } room) return;
        var taken = room.Members.Where(x => x.InvestigatorCode.Length > 0).Select(x => x.InvestigatorCode)
            .Concat((room.Plans ?? Array.Empty<OnlineSeatPlan>()).Where(p => p.Seat != seat && p.InvestigatorCode.Length > 0).Select(p => p.InvestigatorCode)).ToList();
        SelectionStage stage = null!;
        stage = new SelectionStage(taken, async (code, deckKey) =>
        {
            string key = deckKey.Length > 0 ? deckKey : S.Decks.For(code).Select(d => d.Key).FirstOrDefault() ?? "";
            if (key.Length == 0 || !S.Decks.Has(key)) { App.I.Toast("这名调查员还没有牌组：先在牌组工坊里组一套", true, 4); return; }
            var deck = S.Decks.Get(key);
            string name = J.String(deck, "name"); if (name.Length == 0) name = S.Name(code);
            string inv = J.String(deck, "investigator_code"); if (inv.Length == 0) inv = code;
            OnlineGame.PlanDecks[seat] = key;
            try { await s.SetSeatPlanAsync(seat, inv, name, room.Plan(seat)?.Controller ?? ""); } catch (Exception e) { App.I.Toast("选择失败：" + e.Message, true, 4); }
        }, scene ?? SelectionStage.Core);
        stage.SwitchTo = other => { stage.QueueFree(); PickPlanned(s, seat, other); };
        App.I.Overlay.AddChild(stage); stage.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    // QA: the room with sample players, to see the screen before online play works
    public static void Demo() => Room(new OnlineRoom("K7Q2MX", null, "敦威治遗产", 4, new[]
    {
        new OnlineMember("Yunke", true, true, 0, "01002", "h", "罗兰的牌组"),
        new OnlineMember("玩家二", false, false, 1, "03001", "b", "马克的牌组"),
        new OnlineMember("玩家三", false, false, 2, "", "c"),
    }, new[] { new OnlineSeatPlan(3, "01004", "阿格妮丝的牌组", OnlineGame.Everyone) }), demo: true);
}
