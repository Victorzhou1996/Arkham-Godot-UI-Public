# 美术需求：文生图（2026-11-18）

来源：Arkham UI 新版前端（Yunke 委托）。
交付位置：`G:\Python Project\Arkham UI\inbox\art\`（仅此目录）。
生成工具：img2（由 Yunke 手动调用）。

## 当前状态（2026-11-18）
- `selection-dunwich.png`：已由 Yunke 提供，已接入游戏。
- `campaign-zealot.png`、`campaign-dunwich.png`：已不再紧急。Yunke 已为基础版和敦威治重新绘制群像，两张战役卡都已改用这些群像。若之后要做专用封面，再另行提供，目前无需生成。
- `selection-core.png`（基础版新群像）：已由 Yunke 绘制，无需生成。

## 统一画风
参照游戏现有两张图：
- `app\assets\bg\exterior.png`：夜色中的维多利亚宅邸
- `app\assets\bg\selection.png`：基础版五人群像

画风：1920 年代美国新英格兰，写实偏概念插画、厚涂质感，暖色灯光配冷色阴影，整体偏暗，适合压暗后做背景。画面里不要文字、不要 logo、不要卡牌边框。

## 需求清单

### 1. `campaign-zealot.png`
- 用途：基础战役"狂热之夜"的封面背景
- 尺寸：约 1600×1600（正方形，下方 40% 会压暗叠字）
- Prompt：
```
Moody 1920s New England town of Arkham at night, a lone Victorian house with warm lit windows at the edge of dark woods, cultists' torches faintly visible among the trees, full moon behind clouds, wet cobblestones, painterly concept art, cinematic lighting, warm lamplight and cool blue shadows, dark lower third for text, no text, no logo.
```

### 2. `campaign-dunwich.png`
- 用途：第一循环"敦威治遗产"的封面背景
- 尺寸：约 1600×1600（正方形，下方 40% 会压暗叠字）
- Prompt：
```
Desolate hills of Dunwich, Massachusetts in the 1920s at dusk, rolling overgrown hills with ancient standing stones on a hilltop, a decaying farmhouse and a crooked wooden bridge over a dark river, low mist in the valleys, ominous sky with a pale moon, whippoorwills over the fields, painterly concept art, cinematic lighting, muted greens and cold blues with a faint sickly glow on the hilltop, dark lower third for text, no text, no logo.
```

### 3. `selection-dunwich.png`（已由 Yunke 提供并接入游戏，无需生成）
- 用途：敦威治五名调查员的群像（选人画面，之后做人物色块蒙版）
- 尺寸：1672×941（16:9）
- 构图：五人从左到右站成一排，彼此稍有间隔、不要互相大面积遮挡，全身或至少到膝盖，脸部清楚；背景是敦威治乡间夜晚的农舍门廊或谷仓前，不要太亮，人物要突出。
- Prompt：
```
Group portrait of five 1920s investigators standing side by side in front of an old Dunwich farmhouse porch at night, lantern light, painterly concept art, cinematic, each figure clearly separated with a little space between them, full body. From left to right: (1) a tough woman cook in a stained apron with rolled sleeves, holding a kitchen knife, a small crucifix necklace, determined face; (2) a lean newspaper reporter with round glasses and a mustache, waistcoat and rolled shirt sleeves, a pocket watch, thoughtful pose with hand at his chin; (3) a glamorous wealthy flapper in a dark blue beaded dress, wide-brimmed hat and long pearl necklace, holding a pistol in each gloved hand; (4) a jazz trumpeter in a pinstripe suit and fedora, holding a brass trumpet, calm and melancholic; (5) a rugged drifter in a cowboy hat and worn jacket with an acoustic guitar, a large brown dog sitting at his feet. Warm lamplight on faces, cool blue night around them, no text, no logo.
```

## 交付要求
1. 三张图分别保存为上表的文件名，放入 `inbox\art\`。不要改文件名，不要放入其他目录。
2. 每张图生成后，在 `docs/review/` 写一份交付说明（文件名 `YYYYMMDD-HHMM-美术交付.md`），列出：文件名、实际尺寸、是否符合上面的构图与画风要求、是否有文字或 logo。不符合的请说明，不要自行改图。
3. 不符合要求时，只在交付说明里标注，不要覆盖已交付的文件。
4. 不修改 `app\assets\`、`docs\PROGRESS.md` 或其他任何文件。
