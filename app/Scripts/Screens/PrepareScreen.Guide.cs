using Godot;

namespace ArkhamUI;

// 新手教程 on the prepare screen (Tutorial/Guide): the steps in order — what this screen is, pick Roland Banks, pick Daisy
// Walker, the decks, start. Each step's tip points at the thing to use; the screen's other choices are fixed or hidden
// (PrepareScreen: difficulty 简单, no online host, no workshop, no deck switch or 移出).
public partial class PrepareScreen
{
    readonly List<Button> diffButtons = new();
    Button? addButton;
    bool guideIntroSeen, guideDecksSeen, guidePicking;

    void GuideStep()
    {
        if (!Guide.On || guidePicking || !IsInsideTree()) return;
        var S = App.I.S!;
        bool hasRoland = seats.Any(s => s.Code == Tutorial.Roland), hasDaisy = seats.Any(s => s.Code == Tutorial.Daisy);
        if (!guideIntroSeen)
        {
            Guide.Show("第二步：准备调查。｜上面是「狂热之夜」的战役路线：三章依次进行，前一章的结局会影响后面。｜右上角是难度，右边是这个难度下的混沌袋——检定时就从这里抽标记。教程固定用“简单”难度。",
                () => diffButtons.FirstOrDefault(), () => { guideIntroSeen = true; GuideStep(); });
            return;
        }
        if (!hasRoland)
        {
            Guide.Show("接下来选调查员。点击发光的“＋ 选择调查员”，进入调查员群像。", () => addButton);
            return;
        }
        if (!hasDaisy)
        {
            Guide.Show("罗兰·班克斯加入了。一局可以有 1–4 名调查员，教程用两名。｜再点发光的“＋ 选择调查员”，这次选黛西·沃克。", () => addButton);
            return;
        }
        if (!guideDecksSeen)
        {
            Guide.Show("两人到齐。每名调查员带一副牌组：教程已经为两人配好了固定牌组，各带一张弱点（罗兰：「黑帮打手」；黛西：「恶灵附身」）。｜平时可以在这里的下拉框换牌组，或去牌组工坊自己组牌。",
                () => slots, () => { guideDecksSeen = true; GuideStep(); });
            return;
        }
        Guide.Show("准备好了。点击右下角发光的“开始调查”，进入「聚集于此」。", () => start);
    }

    // in the group picture: only the one the tutorial asks for. The tip points at the person (a glow and a ripple on them),
    // and once their notebook is open, at its "选择" button (Yunke 11-23)
    void GuidePicker(SelectionStage stage)
    {
        var S = App.I.S!;
        string want = seats.Any(s => s.Code == Tutorial.Roland) ? Tutorial.Daisy : Tutorial.Roland;
        SelectionStage.Guide = code => code == want ? null : "新手教程这一步请选择" + S.Name(want);
        SelectionStage.GuideDeck = code => code is Tutorial.Roland or Tutorial.Daisy ? Tutorial.DeckKey(code) : null;
        guidePicking = true;
        string who = want == Tutorial.Roland
            ? "这是基础游戏的调查员群像。把指针移到人物身上能看到名字，点击就会拉近并打开他的资料。｜找到发光的、戴帽子的罗兰·班克斯（守卫者，擅长战斗），点击他。"
            : "再找到发光的黛西·沃克（探求者，擅长调查），点击她。";
        bool onButton = false;
        void Step()
        {
            if (!IsInstanceValid(stage)) return;
            bool open = stage.Picked == want && stage.ChooseButton is not null;
            if (open == onButton && Guide.On) return;
            onButton = open;
            if (open) Guide.ShowAt("这是" + S.Name(want) + "的资料和牌组（教程已配好）。点击下面发光的“选择 " + S.Name(want) + "”。", () => stage.ChooseButton?.GetGlobalRect());
            else Guide.ShowAt(who, () => stage.PersonRect(want));
        }
        onButton = true; Step();
        var timer = new Godot.Timer { WaitTime = 0.25, Autostart = true }; timer.Timeout += Step; stage.AddChild(timer);
        stage.TreeExited += () => { guidePicking = false; if (IsInstanceValid(this)) Callable.From(GuideStep).CallDeferred(); };
        QaScript.Register("guide-pick", _ => stage.Focus(want));   // QA: click the person the tutorial asks for
    }
}
