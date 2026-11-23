# 战役封面重绘需求（给 GPT 图像模型）· 2026-11-18

## 用在哪里

选战役画面有两张卡，左边是狂热之夜，右边是敦威治遗产。每张卡的上部约三分之二放封面图，下沿渐隐到暗底，下面写文字。

现在用的是从 html 版战役盒封面里裁出来的插画（`app/assets/art-cover/01.webp`、`02.webp`）。原图只有 773×289 和 773×333，放大后发糊，所以需要重绘一版高清的。

## 交付要求（两张都适用）

- **参考图**：把下面写的原图作为参考图一起发给 GPT，要求**保持原图的构图、主体和氛围**，只是重画成高清版本，不要换成别的画面。
- **尺寸**：横版 3:2，至少 2400×1600，PNG。卡片在 1920×1080 下大约是 800×520 显示，左右两边会被裁掉一点，所以主体放在中间 80% 的范围内。
- **画风**：厚涂油画质感，1920 年代新英格兰，夜晚，偏暗、低饱和，带一点克苏鲁式的不安；和游戏里已有的两张选人群像（书房、敦威治）是同一种画法。
- **下沿**：画面最下面约 20% 会渐隐进黑色，这里不要放重要的东西，暗一些更好。
- **不要**：文字、字母、标题、标志、边框、水印、界面元素。
- **文件名**：放进 `inbox/art/`，分别叫 `campaign-cover-01.png`（狂热之夜）和 `campaign-cover-02.png`（敦威治遗产）。我负责裁切、接入游戏，原来的两张保留不删。

## 1. `campaign-cover-01.png`：狂热之夜

- **参考原图**：`G:/Python Project/ArkhamHorrorLocal-WinDeploy-20260712/outputs/ArkhamHorror-Server-Linux-amd64-1.0.35-20261116/game/frontend/dist/img/arkham/boxes/01.jpg`，只看盒子中间那条插画带（项目里裁好的是 `app/assets/art-cover/01.webp`）。
- **给 GPT 的话**：

```
Redraw the attached reference illustration as a high-resolution painting, keeping the same composition, subjects, lighting and mood. Do not add or remove story elements. 3:2 landscape, at least 2400x1600. Painterly oil painting, 1920s New England at night, dark muted palette with warm amber lamp light, subtle eldritch unease. Keep the main subjects within the central 80% of the width; the bottom 20% should be darker and quiet because it fades to black in the game. No text, no letters, no title, no logo, no border, no watermark, no UI.
```

## 2. `campaign-cover-02.png`：敦威治遗产

- **参考原图**：`G:/Python Project/ArkhamHorrorLocal-WinDeploy-20260712/outputs/ArkhamHorror-Server-Linux-amd64-1.0.35-20261116/game/frontend/dist/img/arkham/boxes/02.jpg`，同样只看中间的插画带（裁好的是 `app/assets/art-cover/02.webp`）。
- **给 GPT 的话**：

```
Redraw the attached reference illustration as a high-resolution painting, keeping the same composition, subjects, lighting and mood. Do not add or remove story elements. 3:2 landscape, at least 2400x1600. Painterly oil painting, rural 1920s New England hills at night, dark muted palette, cold moonlight with a few warm lights, subtle eldritch unease. Keep the main subjects within the central 80% of the width; the bottom 20% should be darker and quiet because it fades to black in the game. No text, no letters, no title, no logo, no border, no watermark, no UI.
```

## 交付后我这边做什么

- 裁成卡片需要的比例，替换选战役画面两张卡的封面；
- 原来的 `01.webp`、`02.webp` 保留，新图用新文件名接入。
