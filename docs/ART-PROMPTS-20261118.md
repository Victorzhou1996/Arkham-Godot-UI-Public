# 图片素材需求（给文生图 AI 的 prompt）· 2026-11-18

用途：诡镇奇谈新版前端。选战役画面的封面已经改为从 html 版封面重绘（不需要出图）；这里只列还缺的地图（战役背景已定为 html 封面重绘，Yunke 11-18）。图做好后放进 `inbox/art/`，文件名按下面每节写的，我负责裁切、做区域蒙版、接入游戏。

## 共同要求（每张图都适用）

- **画风**：和已经用上的两张地图一致（基础版的房子剖面、敦威治的大学俯瞰）：厚涂油画质感，1920 年代新英格兰，暮色或夜晚，窗里透出暖黄灯光，整体偏暗、低饱和，带一点克苏鲁式的不安。
- **尺寸**：横版 16:9，至少 1672×941，最好 2560×1440，PNG。
- **不要**：文字、字母、招牌上的字、人物、怪物、界面元素、边框、水印。
- **地点要分得开**：每个地点是一块轮廓清楚的建筑、房间或空地；不相邻的地点之间留出草地、街道或虚空，并画出连接它们的路（游戏会沿路画发光曲线）。同一剖面里相邻的房间可以直接贴着。
- **构图**：主要地点不要贴着画面边缘（四边会渐隐约 5%）。

通用英文后缀（加在每条 prompt 末尾）：

```
painterly oil painting, 1920s New England, dusk to night, warm amber window light, muted dark palette, subtle eldritch unease, highly detailed, 16:9, no text, no letters, no people, no creatures, no UI, no border, no watermark
```

## 桌布（准备画面背景，Yunke 11-18）

**`tablecloth-pattern.png`**：一块深色、纯色的桌布，暗纹是克苏鲁风格的花纹（触手、远古印记、星形符号组成的锦缎暗纹，颜色只比底色略亮），要能无缝平铺。现在暂时用 html 版的墨绿毛毡（`assets/ui/tablecloth-felt.png`），图到了放进 `inbox/art/`，我替换成 `assets/ui/tablecloth-pattern.png`。

```
Seamless tileable texture, dark solid-coloured tablecloth (deep green or oxblood) with a subtle damask pattern of Cthulhu motifs — curling tentacles, elder signs, star sigils — woven tone-on-tone, only slightly lighter than the cloth, soft fabric texture, flat top-down, even lighting, 2048x2048, no text
```

## 剧本地图（9 张，每个剧本一张整图）

已经有：房子（聚集于此）、大学（课外活动，新图已收到）。还缺下面 9 张。每节的地点名来自卡牌数据，画里要能清楚找到每一个地点。

### 1. `map-arkham.png`：狂热之夜 · 第二章《午夜假面》

- 地点：圣玛丽医院（St. Mary's Hospital）、米斯卡塔尼克大学（Miskatonic University）、河岸城（Rivertown）、你的房子（Your House）、南区（Southside）、商业区（Downtown）、东城区（Easttown）、墓地（Graveyard）、北区（Northside）
- 画面：阿卡姆城区的夜景俯瞰，像一张画出来的城镇地图：九个街区和建筑各自成团、彼此分开，中间用街道、桥和河相连（米斯卡托尼克河穿城而过，河镇在河边）。墓地在城边，医院和大学各是一组醒目的建筑，你的家是一栋独立的小屋。

```
Bird's-eye painted map of a 1920s New England town at night: nine separate districts and landmarks spread across the picture - a small lone house, a riverside warehouse district by a dark river with bridges, a busy downtown square, a poor east side, a south side with a historical society, a hospital complex, a university campus with ivy brick halls, a quiet north side, and a cemetery at the edge of town. Clear gaps of streets, bridges and lawns between every district; cobbled streets glowing under gas lamps.
```

### 2. `map-arkham-woods.png`：狂热之夜 · 第三章《地底噬魔》

- 地点：主道路（Main Path）、阿卡姆密林（Arkham Woods）、仪式地（Ritual Site）
- 画面：阿卡姆森林夜景俯瞰：中央一条林间主路，四周四片彼此分开的林地空地，每片要有自己的特征（沼泽、怪石、倒木、古树）；最深处是一个石圈祭祀场。林地之间用细小的小路连到主路。

```
Top-down painted map of a dark forest at night: a central dirt main path, four distinct clearings around it (a misty bog, a ring of strange standing rocks, fallen giant trees, an ancient gnarled oak), and at the far end a stone ritual circle lit by torches. Narrow trails link each clearing to the main path; the clearings are clearly separated by dense trees.
```

### 3. `map-clover-club.png`：敦威治遗产 · 第二章《百赌百胜》

- 地点：月下美人（La Bella Luna）、俱乐部大厅（Clover Club Lounge）、俱乐部酒吧（Clover Club Bar）、俱乐部卡牌室（Clover Club Cardroom）、黑暗走廊（Darkened Hall）、画廊（Art Gallery）、VIP区（VIP Area）、后巷（Back Alley）
- 画面：像房子那张一样的剖面图：一栋两三层的楼，临街是意大利餐厅，里面藏着地下赌场三叶草俱乐部（休息厅、吧台、牌室、昏暗走廊、画廊、贵宾区），楼外一侧是后巷。房间相邻就表示相通，不需要画路。

```
Cut-away side view of a 1920s building at night, like a dollhouse cross-section: a street-front Italian restaurant with a lit sign shape (no letters), behind it a hidden speakeasy casino - a plush lounge, a long bar, a card room with green tables, a dim hallway, an art gallery, a velvet VIP room upstairs - and a rain-wet back alley beside the building. Each room clearly framed by walls and floors.
```

### 4. `map-museum.png`：敦威治遗产 · 第三章《博物馆之夜》

- 地点：博物馆大门（Museum Entrance）、博物馆大厅（Museum Halls）、保安室（Security Office）、管理办公室（Administration Office）、展厅（Exhibit Hall）
- 画面：米斯卡托尼克博物馆夜里的剖面图：正门大厅、展馆走廊、保安室、行政办公室、几间展厅（其中一间放着古籍）。房间相邻即相通。

```
Cut-away view of a grand stone museum at night: a columned entrance lobby, long exhibit halls with display cases and statues, a small security office, an administration office with filing cabinets, and several side exhibit rooms (one with an ancient book under glass). Moonlight through tall windows; rooms clearly separated by walls.
```

### 5. `map-train.png`：敦威治遗产 · 第四章《疯狂列车》

- 地点：客车（Passenger Car）、卧车（Sleeping Car）、餐车（Dining Car）、特等车（Parlor Car）、车头（Engine Car）
- 画面：一列夜行蒸汽列车的侧面剖面，一字排开：车头在一端，后面依次是客车厢、卧铺车厢、餐车、特等车厢等，最多 8 节。每节车厢之间要有明显的连接处。背景是山间夜色。

```
Side cut-away of a 1920s night express steam train crossing mountains, cars in one straight row from left to right: up to eight cars - the steam engine at one end, then passenger cars with seats, a sleeping car with bunks, a dining car with tables, a luxury parlor car. Each car's interior visible, clear couplings between cars, starry mountain night outside.
```

### 6. `map-dunwich-village.png`：敦威治遗产 · 第五章《血色祭坛》

- 地点：公共空地（Village Commons）、毕晓普河（Bishop's Brook）、火灾废墟（Burned Ruins）、奥斯本杂货店（Osborn's General Store）、自治教堂（Congregational Church）、芦苇屋（House in the Reeds）、学校（Schoolhouse）、隐藏密室（The Hidden Chamber）
- 画面：敦威治村夜景俯瞰：村中空地、溪流、烧毁的废墟、杂货店、教堂、芦苇丛中的小屋、校舍，以及一处隐藏密室的入口。破败的新英格兰山村，地点之间用泥路和溪流相连。

```
Bird's-eye painted map of a decaying New England hill village at night: a muddy village common, a creek with a small bridge, burned-down ruins, a general store, a white wooden church with a steeple, a lonely house among tall reeds by the water, an old one-room schoolhouse, and a dark cellar entrance. Places spread apart and connected by dirt roads and the creek.
```

### 7. `map-dunwich-hills.png`：敦威治遗产 · 第六章《无人知晓》

- 地点：敦威治村（Dunwich Village）、冷泉幽谷（Cold Spring Glen）、十里牧场（Ten-Acre Meadow）、憎恶荒野（Blasted Heath）、沃特雷家的废墟（Whateley Ruins）、恶魔花园（Devil's Hop Yard）
- 画面：敦威治周围的山野夜景俯瞰：村子、冷泉幽谷、草地、焦枯的荒野、一处老农舍的废墟、立着石头的荒芜山顶。地点之间相隔较远，用蜿蜒的山路连接。

```
Bird's-eye painted map of wild hills around a small village at night: the village at one side, a deep glen with a cold spring, a wide meadow, a blasted grey heath where nothing grows, the burnt ruins of an old farmhouse, and a barren hilltop yard with standing stones. Long winding country roads link them; strong sense of distance.
```

### 8. `map-sentinel-hill.png`：敦威治遗产 · 第七章《毁灭之地》

- 地点：山脚（Base of the Hill）、上山道路（Ascending Path）、哨兵峰（Sentinel Peak）、屠杀之森（Slaughtered Woods）、诡异空地（Eerie Glade）、被毁的道路（Destroyed Path）、冷冻泉（Frozen Spring）、次元裂隙（Dimensional Gap）、道路裂口（A Tear in the Path）、漂浮之森（Uprooted Woods）、回忆迷宫（Lost Memories）
- 画面：哨兵峰：从画面下方的山脚往上，一条上山的路分成几条岔路，穿过被屠戮的林子、诡异的林间空地、冻住的泉水、裂开的小路等，最上方是山顶的石祭坛。方向要清楚：山脚在下，山顶在上。

```
Painted view of a tall wooded hill at night seen from the side and slightly above: the base of the hill at the bottom, an ascending path that splits into several branching trails climbing upward through slaughtered trees, an eerie glade, a frozen spring, a torn and broken path, and at the very top a stone altar on the bare peak under a sickly sky. The climb from bottom to top must read clearly.
```

### 9. `map-other-dimension.png`：敦威治遗产 · 第八章《迷失时空》

- 地点：另一个次元（Another Dimension）、宇宙的边缘（The Edge of the Universe）、时间缝隙（Tear Through Time）、空间裂缝（Tear Through Space）、菱柱瀑布（Prismatic Cascade）、无尽之桥（Endless Bridge）、伊哈格尔的阶梯（Steps of Y'hagharl）、次元之门（Dimensional Doorway）
- 画面：异次元：漂浮在虚空中的碎片岛屿。中央是一块平台，四周是彼此分离的岛：宇宙的边缘、时间裂隙、空间裂隙、棱镜瀑布、无尽之桥、通往黑暗的巨大台阶、维度之门。岛屿之间用细细的发光桥连接。

```
Surreal painted map of floating fragments in a cosmic void: a central rocky platform, around it separate floating islands - the ragged edge of the universe, a crack in time with clock-like debris, a rip in space showing stars, a cascading prismatic waterfall of light, an endless narrow bridge, colossal alien steps leading up into darkness, and a glowing dimensional doorway. Islands clearly apart, linked by thin glowing bridges.
```

## 交付后我这边做什么

- 每张地图：标出各地点的区域蒙版（自动分割，不准的手画）；需要连线的地图沿路标点，画发光曲线；画面上已经相邻的房间不画线。
