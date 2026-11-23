# 音效 / 音乐生成提示词（交给 Yunke，2026-11-19）

用途：诡镇奇谈新版缺的音效和音乐。如果选择"自己生成"而不是下载 Kenney CC0 包，按下面的提示词生成。
生成后放到 `app/assets/audio/sfx/`（随仓库）或 `userdata/audio/sfx/`（只本机），文件名照表；格式 `.ogg` 优先（`.wav`、`.mp3` 也能读）。
整体风格：1920 年代新英格兰、克苏鲁恐怖、桌游实物质感（纸、木桌、黄铜、皮革），**安静克制**，不要电子游戏式的"叮咚"。
音量统一：峰值约 -3 dBFS，首尾不留静音（开头 < 10ms），单声道或立体声都可，44.1kHz。

## 音效（每个最好 2–3 个变体，文件名加 -1/-2/-3）

| 文件名 | 时长 | 英文提示词 |
|---|---|---|
| `click.ogg` | 0.05–0.12s | Soft tactile click of a small brass switch on an old wooden desk, muted, close-mic, no reverb, subtle |
| `draw.ogg` | 0.3–0.5s | Single playing card slid off the top of a deck across a felt table, dry paper swish, close and quiet |
| `play-card.ogg` | 0.3–0.5s | A thick linen card placed firmly onto a wooden table, soft paper slap with a faint wood knock |
| `shuffle.ogg` | 1.0–1.5s | Riffle shuffle of a deck of thick cards, then a light tap to square the deck, intimate room |
| `encounter.ogg` | 0.8–1.2s | Card flipped over on a table followed by a low eerie swell, distant whisper texture, ominous but short |
| `chaos.ogg` | 0.4–0.7s | Small bone or wooden token drawn from a cloth bag and set on a table, muffled rattle then a soft clack |
| `test-success.ogg` | 0.6–1.0s | Subtle warm low piano chord with a soft chime, restrained relief, 1920s parlour feel, no fanfare |
| `test-fail.ogg` | 0.6–1.0s | Dull low cello stab with a dissonant fading tail, restrained dread, no jump scare |
| `horror.ogg` | 0.6–1.0s | Short unsettling breathy whisper swell with a faint high string tremolo, quiet, psychological dread |
| `doom.ogg` | 0.6–1.0s | Single heavy distant church bell toll, muffled, with a low rumble tail, ominous |
| `act.ogg` | 1.0–1.5s | Old book page turned decisively followed by a low hopeful string note, investigative progress |
| `agenda.ogg` | 1.0–1.5s | Deep cosmic low drone swell with a muffled bell, sense of creeping doom advancing |
| `phase.ogg` | 0.6–1.0s | Soft low gong or tam-tam brushed lightly, short decay, marks a new phase without startling |

现有可用（不用生成）：`impact-heavy.ogg`（伤害，Kenney CC0）、`turn.ogg`（轮到你，0712 自制）、`rain.ogg`（雨声氛围，CC0）。

## 音乐（只有在 0712 现有曲目来源无法确认时才需要）

| 文件名 | 时长 | 英文提示词 |
|---|---|---|
| `menu.ogg` | 2–3 分钟，可循环 | Slow dark ambient piano and strings, 1920s New England, foggy harbor night, mysterious and melancholic, sparse, seamless loop, no vocals |
| `table.ogg` | 3–4 分钟，可循环 | Low tension investigation underscore, muted strings, sparse piano, distant creaks, subtle pulse, unobtrusive background for a card game, seamless loop, no vocals |
| `ending-won.ogg` | 1–2 分钟 | Quiet bittersweet resolution, solo cello and piano, dawn after a long night, restrained hope, no vocals |
| `ending-lost.ogg` | 1–2 分钟 | Dark mournful drone with low choir pad and distant bells, cosmic dread, slow fade, no vocals |

生成后告诉我文件位置，我改 `app/assets/audio/audio.json` 的映射即可，不用改代码。
