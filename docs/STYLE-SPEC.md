# 风格规范（取自参考程序 0.1.1-preview 的主题脚本与素材）

来源：`board_theme.gdc`、`typography.gdc`、`material_frame_style.gdc` 解码出的常量；素材见 `app/assets/ref/`。

## 色板
| 名称 | 值 | 用途 |
|---|---|---|
| INK | #131512 | 最深底色 |
| PANEL | #171713 (α .96) | 面板底 |
| PAPER | #EADBBD | 主要文字（暖象牙） |
| MUTED | #B6AA91 | 次要文字 |
| AMBER | #C3A366 | 黄铜强调、边框 |
| RED | #D58B72 | 伤害、失败、警示 |
| GREEN | #A6B99B | 成功、可执行 |
| 面板描边 | #B3975C | |
| 按钮 normal/hover/pressed/disabled | #23221D / #3A3526 / #454130 / #25231E；描边 #A58A54；悬停字色 #FFF1D5；禁用字色 #8B8472 | |
| 技能色 | 意志 #8FB6CB、智力 #BA9EC9、战斗 #D78E79、敏捷 #93B8A0 | |

## 字体
| 角色 | 字体 |
|---|---|
| 界面（按钮、标签、数字） | 思源黑体 Medium |
| 标题、剧情 | 霞鹜文楷 Medium |
| 正文（规则、阅读器） | 思源宋体 Regular |
| 卡名、牌标题 | 马善政（加粗 0.3） |
| 诡镇符号 | arkham.ttf：意志 A、智力 F、战斗 D、敏捷 S、万能 G、行动 I、快速 u、反应 y、守护者 Q、探求者 E、流浪者 T、神秘学家 W、生存者 R、远古印记 X、自动失败 Z |
| 混沌符号 | arkhamicons.ttf：骷髅 U+E910、异教徒 U+E911、石板 U+E912、古神 U+E913 |
默认字号 20（1080p），新版提高到 22 起步，最小 18。

## 材质
- 深色面板：`ui/board-v6-materials/panels/charcoal-brass-panel`（九宫格，源角 150px → 屏幕 64–75px）
- 浅色按钮/纸面：`ivory-paper-button`
- 标题牌匾：`tags/brass-plaque`、`hand-v7/title-ribbon`
- 头像框：`portrait/portrait-frame`；圆形徽章：`hand-v7/brass-medallion-ring`
- 牌堆背面：`deck-backs-v16/personal`（玩家）、`encounter`（遭遇）
- 首页：`art/title-screen-v1/background + tentacles + investigators`、`title-wordmark-v1/wordmark`

## 新版的取舍
- 卡面用原版卡图（`app/assets/cards/*.webp`），不用参考版的空卡框重绘。
- 混沌 token 用网页版素材（`app/assets/tokens/ct_*.png`），外圈可加黄铜圆环。
- 场景背景用 Yunke 提供的纯场景图（`assets-source/chatgpt-20261117/g1-*`）。
