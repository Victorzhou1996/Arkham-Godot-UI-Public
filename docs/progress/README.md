# 进度快照与文件交接

本目录用于每 60 分钟一次的进度交接，给 Orca 中的 Codex 审核。

## 流程
1. **快照**：每小时由计划任务生成 `docs/progress/YYYYMMDD-HHMM-快照.md`，内容来自 `docs/PROGRESS.md`、`jobs/runner-alive.txt`、`docs/review/` 的新文件。快照只汇总，不改写任何已有文件。
2. **审核**：Codex 读取最新快照与 `docs/PROGRESS.md`，把意见写成 `docs/review/YYYYMMDD-HHMM-<主题>.md`，格式见 `docs/SUPERVISION.md` 第 3 节。
3. **处理**：实现者每个任务开始前读取 `docs/review/`，处理后在原文件末尾"处理结果"下追加说明。

## 快照格式
- 时间戳、当前里程碑、本小时新增或变更的任务（沿用 `PROGRESS.md` 的状态词：未做 / 进行中 / 已做未验证 / 待你验收）
- 红线状态（见 `docs/SUPERVISION.md` 第 4 节）
- 夜间助手心跳时间
- 未处理的 review 意见清单

## 边界
- 快照任务不写 `docs/PROGRESS.md`、不写 `docs/review/`、不碰 `jobs/inbox/`、`runtime/`、`userdata/`、`qa-data/`。
- 状态词里不出现"已验证"（只有 Yunke 能记）。
