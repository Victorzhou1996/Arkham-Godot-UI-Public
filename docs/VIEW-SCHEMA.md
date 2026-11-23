# 规则核心视图结构速查（来自真实探针输出）

样本：`evidence/probe-20261116-184020/`（`1p-investigation-view.json`、`1p-skilltest-*.json`、`victory-step-*.json`）。
视图顶层：`{ game, settingsSchema, deckSchema, interactionSchema, ... }`；前端主要读 `game`。

| 位置 | 关键字段 |
|---|---|
| `game.investigators[id]` | `cardCode`(c01001)、`name.title`、`playerId`、`placement{tag:AtLocation?,contents:地点id}`、`health/sanity`、`assignedHealthDamage/assignedSanityDamage`、`tokens[[Resource,5],[Clue,n],[Damage,n],[Horror,n]]`、`willpower/intellect/combat/agility`、`remainingActions`、`hand[{tag:PlayerCard,contents:{id,cardCode}}]`、`deckSize`、`discard`、`assets[id]`、`engagedEnemies`、`treacheries`、`class` |
| `game.locations[id]` | `cardCode`、`label`(study/hallway/...，对应布局网格)、`revealed`、`shroud{tag:Static,contents}`、`tokens[[Clue,n]]`、`investigators[]`、`enemies[]`、`connectedLocations[]` |
| `game.mode.That`（场景） | `name.title`、`difficulty`、`locationLayout`(网格字符串数组)、`chaosBag.chaosTokens[{chaosTokenFace,...}]`、`victoryDisplay`、`encounterDeck`、`discard` |
| `game.acts[id]` / `game.agendas[id]` | 场景：`id`(c01108)、`sequence`、`advanceCost`；密谋：`doom`、`doomThreshold{contents}`、`sequence.agendaSequenceStep/Side` |
| `game.enemies[id]` | `cardCode`、`placement{tag:InThreatArea|AtLocation,contents}`、`fight/evade/health{contents:{contents:n}}`、`tokens[[Damage,n]]`、`healthDamage/sanityDamage`、`exhausted`、`engagedInvestigators` |
| `game.assets[id]` | `cardCode`、`controller`、`exhausted`、`tokens`、`slots`、`health/sanity`、`assignedHealthDamage` |
| `game.treacheries[id]` | `cardCode`、`placement`、`tokens` |
| `game.skillTest` | `investigator`、`action`、`skills[]`、`modifiedSkillValue`、`modifiedDifficulty`、`committedCards{iid:[card]}`、`step`、`result{tag:Unrun|SucceededBy|FailedBy}`、`revealedChaosTokens` |
| `game.skillTestChaosTokens` | `[{chaosTokenFace:Skull,...}]` |
| `game.question[playerId]` | 由 `engine/Protocol/ClientQuestions.cs` 解析；`QuestionPresenter` 生成中文选项 |
| `game.phase` / `gameState` / `outcome` | 阶段、`IsOver`、结局 |

卡牌中文数据：`app/assets/data/core-cards.json`（name/text/flavor/traits/type_name/faction_name，184 张核心盒+第一关）。
