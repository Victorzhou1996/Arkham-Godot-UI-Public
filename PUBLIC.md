# 公开版说明

这是私有仓库 Arkham-Godot-UI 在提交 6724f19（2026-11-23）时的代码快照，历史从这里重新开始。

与私有版相比：
- 删除了 `tools/qqbot/data/`（QQ 机器人运行数据，含玩家标识）。
- 热更新服务器的地址已打码：`<DOMESTIC_SERVER_IP>`、`<OVERSEAS_SERVER_IP>`、`<OVERSEAS_SERVER_HOST>`。
- 不含任何联机密钥。EOS 配置从 `~/.arkham/eos-config.local.json` 读取，不在仓库里。

大体积美术素材（`app/assets/bg`、`cards`、`ref`、`art-portrait-wide`、`art-fallback`）不放在 git 里，作为 Release 附件 `media-assets.zip` 提供，解压到仓库根目录即可。

卡牌图片、卡牌文字等游戏内容的版权归其权利人（Fantasy Flight Games）所有。本仓库是爱好者项目，与官方无关。
