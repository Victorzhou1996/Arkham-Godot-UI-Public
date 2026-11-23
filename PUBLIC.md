# 公开版说明

这是私有仓库 Arkham-Godot-UI 在提交 6724f19（2026-11-23）时的代码快照，历史从这里重新开始。

与私有版相比：
- 删除了 `tools/qqbot/data/`（QQ 机器人运行数据，含玩家标识）。
- 热更新服务器的地址已打码：`<DOMESTIC_SERVER_IP>`、`<OVERSEAS_SERVER_IP>`、`<OVERSEAS_SERVER_HOST>`。
- 不含任何联机密钥。EOS 配置从 `~/.arkham/eos-config.local.json` 读取，不在仓库里。

大体积美术素材（`app/assets/bg`、`cards`、`ref`、`art-portrait-wide`、`art-fallback`）不放在 git 里，作为 Release 附件 `media-assets.zip` 提供，解压到仓库根目录即可。

卡牌图片、卡牌文字等游戏内容的版权归其权利人（Fantasy Flight Games）所有。本仓库是爱好者项目，与官方无关。

## 文档

公开版只保留了以下文档（开发过程中的进度快照、评审记录、bug 分诊、试玩记录、美术/音频提示词等已移除）：

- `README.md`：项目说明
- `MAC-REBUILD.md`：Mac 构建说明
- `native/README-core-b25aded6.md`：规则核心的编译与身份核对
- `docs/STYLE-SPEC.md`、`docs/FRONTEND-DESIGN-NOTES.md`、`docs/DOOM-ANIMATION-RULES.md`：界面风格与动效规范
- `docs/VIEW-SCHEMA.md`：规则核心视图数据结构
- `docs/CAMPAIGN-STORY-LOGIC.md`：战役剧情逻辑
- `docs/ONLINE-SYNC-DESIGN.md`、`docs/ONLINE-MULTISEAT-DESIGN.md`：联机设计
- `docs/HOT-UPDATE-DESIGN.md`：热更新设计
- `docs/MOBILE-BUILD-PLAN-20261122.md`、`docs/MOBILE-TOUCH-20261122.md`：手机版构建与触控
- `tools/eos-probe/README.md`：EOS 联机探针
