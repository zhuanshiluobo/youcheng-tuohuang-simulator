# 外部内容包

本目录是桌面版的运行时内容来源。编辑器读取 `Assets/StreamingAssets/Content/core`；构建后读取 `<游戏名>_Data/StreamingAssets/Content/core`。修改 JSON、Lua 或图片后重启游戏/Play Mode，无需重建旧 ScriptableObject 目录。当前不提供对局中热重载。

- `pack.json`：启用定义清单与共用背面/城市板贴图。
- `character/*.json`：五张角色牌；`abilities` 指定策略/计谋脚本及收尾脚本。
- `lua/characters/*.lua`：十项能力和雷蛇收尾。全部组合通用 Effect，没有角色专用 C# executor。
- `facility_inventory.json`、`event/*.json`、`city_style/*.json`：设施数量清单、事件和城市样式静态数据。设施和样式的 `data.effectScript` 指向外部 Lua；事件效果由 JSON 数据生成。
- `artwork/`：实际 PNG/JPEG 文件。`artwork` 路径相对本目录，与 Unity GUID 无关。
- `content.schema.json`：编辑器可参考的共同 JSON 格式；运行时还校验跨文件引用、ID、脚本语法等。
- `examples/`：企业板、企业家能力牌与回合卡的未启用模板，不在 `pack.json` 中。玩法尚未接入，不可设为 `enabled: true` 混入正式包。

## 修改角色

改 `character/elysium.json` 的 `displayName` 修改名称，改 `artwork` 更换图片，改 `abilities[].script` 选择 Lua 文件。例如 `lua/characters/elysium.strategy.lua` 中的 `amount = 4` 控制获得量。旧角色 `data.strategyEffect/tacticEffect` 仅保留兼容枚举；新角色可使用空 `data: {}`，实际规则来自 `abilities`。

新增角色时复制一份角色 JSON 与两个 Lua 脚本，使用唯一 `definitionId`（角色 ID 不含点）、`abilityId`、`subscriptionId`，并把 JSON 路径加入 `pack.json.definitions`。不需要添加 C# 角色枚举；初始手牌按清单中角色的顺序生成。

## 效果与 UI

Lua 只能读取快照并返回 Effect，不能访问文件、Unity UI 或直接改玩家状态。条件式必须先完整执行 `leftEffects`，再生成 `rightEffects`。

本轮新增/闭合的组合：

- `Effect.SellResource`：统一价格的自由资源出售，允许全零。
- `Effect.MoveFacilityCard`：当前支持 `sourceZone='supply', destinationZone='deck_bottom'`，选择后放到牌堆底并补回原供应槽。
- `Effect.MoveCharacterCard`：当前支持 `sourceZone='discard', destinationZone='hand'`，一次提交全部弃牌回手。
- `Effect.SelectInfluence`：选择并执行 `remove/replace/move`；`ownerFilter='self/opponent/any'`；移动可用 `count` 和 `distinctPolicy='instance'`。每步实际提交后重新生成候选。
- `Effect.Choice({ branches = { { id='...', effects={...} }, ... } })`：等待选择后才创建选中分支；未选分支不执行。当前为单分支选择，与旧 `options` 字符串选择二选一使用；不能混传 `minSelections/maxSelections`。

`interactionType` 和 `promptKey` 只选择已有的 UI 展示适配，不执行卡面规则。没有专用提示的内容可用默认 `lua.choice`。

## 版本与边界

内容包在加载时固定 JSON、脚本和贴图快照并计算哈希。存档绑定哈希，内容改动后不能继续使用旧包的未完成对局；恢复请放回原包。脚本 continuation 同时验证版本与脚本哈希。

所有静态卡牌定义均已改从外部读取，但设施行为和部分城市样式生命周期仍包含 C# 适配；这不等于其所有规则都已经纯 Lua 化。外部目录已解除固定数量与 ID 限制；新规则仍须由已实现的通用 Effect 表达。企业与企业家仅提供格式模板，未实现玩法。

贴图原文件分辨率决定现有卡面点击区域的展示尺度；替换事件牌图片应保留原版式。布局和点击区域本轮未改。


## 技能类型、扩展归属与替换声明（schemaVersion 2）

所有内容定义，包括角色、设施、事件、城市样式、企业板、企业家能力牌及回合卡，必须显式填写：

```json
"expansionId": "core",
"replaces": null
```

`expansionId` 是规则内容所属扩展的稳定 ID，与物理文件包 `packId` 分开；核心现有内容填 `core`，未启用示例填 `example`。不替换填 `null`；替换同类资源时填写目标扩展、类型及定义 ID，例如：

```json
"expansionId": "my_expansion",
"replaces": {
  "expansionId": "core",
  "contentType": "character",
  "definitionId": "elysium"
}
```

替换者必须有自己的唯一 definitionId；不能以重复 ID 或加载顺序覆盖。加载器检查同类、自我引用、包内目标的扩展归属及循环。未启用定义可保留尚未加载的外部目标声明；启用的替换目标必须存在并同时启用。最初元数据版本未执行替换；现已接入下文的内容装配，启用的替换必须解析成功。

每项角色能力（策略/计谋各自填写）新增 `skillType`：

| 值 | 类型 |
| --- | --- |
| `normal` | 普通 |
| `persistent` | 永续 |
| `one_shot` | 一次性 |

永续技能必须明确选择 `hasSpecialZone: true/false`。有特殊区域时，`specialZoneId` 引用本定义 `playerMarkerZones` 中已经声明的 zoneId；无特殊区域时省略该 ID 或填空字符串。普通/一次性技能不能声明特殊区域。例：

```json
"skillType": "persistent",
"hasSpecialZone": true,
"specialZoneId": "persistent-area"
```

并在同卡定义中添加：

```json
"playerMarkerZones": [
  { "zoneId": "persistent-area", "capacity": 3 }
]
```

这里的容量仅为写法示例，不代表任何正式卡面规则。现有十项技能均显式标为普通，延迟到清理时执行不等于永续。类型和区域作为定义元数据保存/校验；本次不自动新增永续可选行动入口、特殊区域 UI 或一次性消耗状态。实现生命周期时须按 API 初稿 0.3 的可用状态设计，不挂长期不完成的 Effect 冒充永续。

`pack.json.schemaVersion` 已升为 2：旧格式必须补字段，加载器不会将缺失类型静默猜成普通。

## 设施数量的口径（模板拆分前记录）

当前 `facility/` 是 46 份完整实体牌 JSON，按名称和 effectId 分别去重均为 19 种。同名牌仍各有 definitionId、成本/分数/效果数据、贴图引用，例如 `building_001`～`building_003` 都是城邦行政区。它不是“19 个模板 + 46 个实例引用”的压缩定义；同效果共用 Lua 模块也不代表 JSON 已按模板去重。本次仅补充元数据，保持既有牌组组成与 ID。


## 内容装配与设施模板（当前可执行实现）

`pack.json.enabledExpansionIds` 控制启用哪些扩展，核心包显式为 `["core"]`。新增扩展文件仍须加入同一 `definitions` 清单，并以当前包根目录为相对路径基准；本轮没有实现独立 ZIP/多个目录自动扫描或菜单扩展选择器。

```json
"enabledExpansionIds": ["core", "my_expansion"]
```

1. 加载器先校验原始定义、设施模板、脚本与贴图，再选择启用扩展中的 enabled 内容。
2. 一个替换项的目标必须存在且同时启用。缺失目标、未启用目标扩展、同一目标被两个启用内容替换均报错；不依赖清单顺序。链式 A → B → C 最终仅装配 C。
3. 原根定义的 ID 是稳定运行时槽位 `RuntimeId`，最终卡面保持自身 `definitionId` 和扩展归属。初始手牌/牌堆按原槽位顺序创建，每个槽位只有一张有效定义，使用最终名称、数据、脚本和图片。被替换的旧 Lua 能力不再注册。图片与脚本查询支持原槽位及替换链 ID。
4. `Definitions` 提供所有校验后的定义，`ActiveDefinitions` 提供替换后的有效定义。Bootstrap、角色 Lua 注册和贴图读取只使用有效集合。清单、模板、脚本及图片均纳入哈希，改动配置后旧对局不能静默恢复。
5. 设施/事件/城市样式仍受现有目录的数量、颜色和行为族等合同约束；替换不会取消这些约束。城市样式沿用原特殊行动 ID，设施仍按 effectId 注册，共用同 effectId 的实例须引用相同脚本。不支持通过单张替换偷偷给共用行为族设置冲突脚本；新的行为族仍需完成通用内核迁移。
6. 永续/一次性的生命周期、特殊区域 UI 按用户要求留待具体扩展卡实现；企业与企业家玩法仍不能启用。

设施现已变为 **19 份共用数据模板 + 46 份实体牌引用**：

- `templates/facilities/*.json`：同名设施共用费用、分数、效果描述与 Lua 脚本路径等字段。
- `facility/*.json`：稳定实体 ID、名称、贴图、扩展/替换声明，以及 `dataTemplate` 和 `data` 差异。异色等差异保留在实体 data 中。
- 加载时模板 data 为底，再递归合并实体 data；数组整体替换，显式 null 覆盖原值。只支持一层设施模板，不递归继承，引用受包内路径限制。
- 修改模板影响所有引用实例；修改实例 data 只覆盖该牌。运行时仍产出原来的 46 项完整设施目录，供应/储备及 UI 不变。

例如 `facility/building_001.json` 的 `dataTemplate` 指向 `templates/facilities/unique_only.json`。模板本身不作为实体牌加入 pack.definitions。46 张展开后的 data 已逐项与迁移前快照比较一致。

## 设施颜色数量与动态目录（2026-09-20）

正式设施来源改为 `pack.json.facilityInventory` 指向的 `facility_inventory.json`。清单包含 19 组模板引用，按 `variants[].color/count` 展开 46 张实体；其中初始供应 41 张、储备 4 张、企业办事处 1 张。旧 `facility/*.json` 文件保留参考，已不在正式 definitions 清单中，修改它们不会改变当前牌组。

每组填写 `idPrefix`、共用 `definition` 和颜色 `variants`。每种颜色的 count 是实际张数；现有 `instances` 显式保留历史 ID 和各自图片。未填写别名的序号生成 `<idPrefix>_<color>_<三位序号>`，增加 count 不改变原有 ID。例如前两张自动 ID 为 `custom_blue_001`、`custom_blue_002`，增至三张只追加 `custom_blue_003`。显式别名数量不能大于 count；减少已有核心数量时须同步调整对应别名。

颜色限 blue/yellow/red/rainbow，单颜色 count 为 0～128 的整数，清单总生成上限 512。重复颜色、重复 ID、非法数量在加载时拒绝。组内 definition 引用 dataTemplate；实例颜色和 ID 由展开器填写，模板仍按递归对象合并、数组整体覆盖处理。`data.defaultSupply` 的 0/1 只表示实体是否进入初始供应，不表示张数；`reserveOnly` 标识储备。

外部设施、事件和城市样式目录已使用 `InitializeExternal`，不再要求固定 46/22/6 张或固定 ID。设施新 effectId 从对应 Lua 注册；事件按实际颜色池登记及抽取；城市样式遍历有效定义登记 Lua，特殊行动也不再要求固定五个 ID。旧内置目录 Initialize 保留严格快照校验。新内容仍必须使用已实现的 Effect、合法字段和必要基础开局规则，企业及企业家玩法尚未启用。

城市样式可配置 `data.luaSubscriptionId`、`luaAbilityId`、`luaCompletionHandlerId`、`luaVersion`；设施 continuation 用 `data.completionHandlerId`。共用同一设施 effectId 的实例必须引用一致脚本。替换仍保留原运行时槽位，不绕过内容哈希和版本验证。

## 2026-09-21 相邻设施入场复制迁移

附属能源设施的相邻筛选与复制编排已改为 `lua/facilities/copy_adjacent_entry.lua`：读取公开设施快照，筛选己方十字相邻、非彩色且有入场效果的设施，再用 Choice 生成单次选择。无合法邻居时正常完成，未选中的分支不结算。复用原 facility.entry.choice 弹窗，不修改布局。

新增 `Effect.ActivateFacilityEntry({ player = ctx.playerId, instanceId = '设施实例ID' })`。宿主复验实际实例仍存在、归属于执行玩家且具备入场效果，再挂现有设施入场节点；保存后从已挂子节点继续，不重复触发。它不包含相邻或颜色规则，这些限制属于卡牌 Lua。公开设施快照新增 color、hasEntryEffect，只读构造快照，不在查询时修改 GameState。

原 ReplayAdjacentEntry 专用分支已撤下，设施入口宿主版本升至 1.2.0。旧版本未完成节点不能静默套用新代码恢复。其他旧设施适配仍在：额外建设、扩展枢纽、收尾标记，以及载具仓库等。

本轮审查另发现载具仓库旧适配缺少“移除后调度”分支，探索选项也尚未完成合法候选交互。该问题已登记，当前未修复，不能把该设施记作完成。下一步应补齐通用探索交互后迁移完整 Choice 两分支，避免继续使用预选目的地和专用结算逻辑。NMC-015 仍不可结案。

## 2026-09-21 基础设施后续迁移（当前状态）

载具仓库、额外建设、延伸枢纽与联邦理事处已迁为 Lua 组合及通用原语。14 种入场模板全部脱离旧 facility_entry 适配，旧设施 Behavior 与城市样式 ScriptStep 已撤下。此前记录的载具仓库缺失分支现已修复。建设不再默认空位或错误扣行动次数；探索逐段选择路线、落点及收费方；起始玩家覆盖值在回合边界消费并清除。

统一请求编号修复了同一 Effect 连续同类交互时 ID 重复的问题，设施展示也已接入统一路由。建设颜色折扣改为外部 costReductionPerDistinctBuiltColor 数据。接口、版本与剩余 UI 边界详见《基础内容迁移与UI接入边界》。NMC-015 仍不能结案，不把基础内容迁移等同旧 UI、存档和多人发布验收完成。
