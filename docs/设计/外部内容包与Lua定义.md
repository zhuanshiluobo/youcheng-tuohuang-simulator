# 外部内容包与 Lua 定义

## 实施结果

本设计落实《Lua效果系统API初稿》2.2～2.5。当前正式启动从 `Assets/StreamingAssets/Content/core/pack.json` 读取 79 项定义：5 张角色、46 张设施、22 张事件牌、6 张城市样式。84 张卡面、背面和城市板图片按包内相对路径绑定，不依赖 Unity GUID。

JSON 保存静态数据、版本、名称、贴图、标记区与能力脚本引用；Lua 保存运行阶段的 Effect 组合。没有把 Lua VM/table/闭包放进 GameState。角色的十项能力全部来自外部 Lua；新增角色不再要求新增 C# 枚举或专用执行器。

## 层级与启动

1. Presentation 的 `ExternalContentRuntime` 确定 `Application.streamingAssetsPath/Content/core`，并负责 Unity Texture2D 解码、缓存与释放。
2. Infrastructure 的 `ExternalContentPack` 读取、校验并固定 JSON/Lua/图片字节，形成内容快照与哈希。它不依赖 Unity，不执行对局状态变更。
3. 各 Bootstrap 把类型化定义注入 Domain 目录。旧 ScriptableObject 继续保留作编辑器旧工具与兼容测试快照，但正式启动不再以它们的牌面数据为准。
4. 外部角色能力按 `abilityId/subscriptionId` 注册到本局 EffectRegistry；注册关系按 Registry 隔离，不使用可被另一局覆盖的全局能力路由。
5. 运行时仍为命令 → 激活 Effect → Event → Lua → 通用 Effect → InteractionRequest → 玩家回答 → 状态提交。UI 不读取 Lua、不决定卡面费用和奖励。

## 角色迁移

- 雷蛇：策略两个 PlaceInfluence 与收尾 Remove；计谋 Condition(Pay, SelectInfluence replace)。
- 极境：策略按最低资源生成 Choice 和奖励 continuation；计谋 Condition(Pay, 普通 MoveCity 候选修正)。
- 德克萨斯：策略 GainResource → MoveFacilityCard；计谋 Condition(Pay, SelectInfluence remove → SelectInfluence move count=2)。
- 坎诺特：策略 SellResource；计谋一次 Choice 后按各玩家资源量生成支付和补偿。
- 锡人：策略 GainScore 与两个独立可选 Condition；计谋 MoveCharacterCard 先回收全部，再按回收数生成有子 Effect 分支的 Choice。

上述旧五项角色专用 executor 方法已经移除。`CharacterCardService` 等旧无 Registry 兼容路径仍存在，其删除需要另行收敛生产调用方，不作为本次外部内容规则来源。

新增通用原语目前支持的范围必须明确：SellResource 使用游戏统一出售价格；MoveFacilityCard 支持供应区选牌→牌堆底并补供应槽；MoveCharacterCard 支持全部弃牌→手牌；SelectInfluence 支持移除/替换/移动、所有权过滤、次数与已移动实例排除。不能将这些首批实现解释为所有规划卡区和移动模式均已完成。

## 其他内容边界

设施和城市样式的静态定义、费用数据、贴图与当前脚本已外部化。设施多种行为仍通过 `facility_entry` 通用入口转向旧行为适配；城市样式声明、标记、费用生命周期还有 C# 编排。事件牌以外部 JSON 的选项/奖励生成现有 Lua 执行内容。此处不把数据外部化等同于所有内容效果已经纯 Lua 化。

企业板、企业家能力牌、企业家回合卡采用同一外壳（contentType、artwork、playerMarkerZones、abilities、data），已给出未启用模板。当前工程没有这三类的完整运行时玩法，加载器拒绝启用，以免空实现被误认为有效内容。模板不列入正式包清单，也没有虚构正式卡面规则。

## 版本、文件与恢复

- 所有路径限定于内容包根目录；禁止绝对路径和 `..` 越界。缺图、缺脚本、重复 ID、错误脚本语法会明确失败。
- 内容包哈希涵盖实际读取的清单、定义、脚本与贴图；GameState 及公开投影携带 ContentPackHash。EffectTreeExecutor 在绑定了内容包的 Registry 下拒绝哈希不同的存档。
- 单局使用已加载快照，不支持对局中热重载。修改文件后重启。恢复原对局须使用原内容包。
- Lua continuation 沿用版本/内容哈希校验；角色宿主版本升为 1.4.0。旧未完成专用能力节点明确停止，不能静默改成新的执行顺序。
- 尚未绑定 ContentPackHash 的旧存档只会在首次使用新 Registry 时绑定；完整历史存档转换及联机内容包分发/下载不是本轮已实现范围。
- 外部贴图归加载器所有；调用方只借用。缓存会识别已失效的 Unity Texture2D 并重建；UI 布局未调整。

编辑步骤和参数示例见 `Assets/StreamingAssets/Content/core/README.md`；验证记录见 `docs/测试/2026-09-20_外部内容包迁移回归.md`。


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


## 内容装配与设施模板（数量清单迁移前阶段）

`pack.json.enabledExpansionIds` 控制启用哪些扩展，核心包显式为 `["core"]`。新增扩展文件仍须加入同一 `definitions` 清单，并以当前包根目录为相对路径基准；本轮没有实现独立 ZIP/多个目录自动扫描或菜单扩展选择器。

```json
"enabledExpansionIds": ["core", "my_expansion"]
```

1. 加载器先校验原始定义、设施模板、脚本与贴图，再选择启用扩展中的 enabled 内容。
2. 一个替换项的目标必须存在且同时启用。缺失目标、未启用目标扩展、同一目标被两个启用内容替换均报错；不依赖清单顺序。链式 A → B → C 最终仅装配 C。
3. 原根定义的 ID 是稳定运行时槽位 `RuntimeId`，最终卡面保持自身 `definitionId` 和扩展归属。初始手牌/牌堆按原槽位顺序创建，每个槽位只有一张有效定义，使用最终名称、数据、脚本和图片。被替换的旧 Lua 能力不再注册。图片与脚本查询支持原槽位及替换链 ID。
4. `Definitions` 提供所有校验后的定义，`ActiveDefinitions` 提供替换后的有效定义。Bootstrap、角色 Lua 注册和贴图读取只使用有效集合。清单、模板、脚本及图片均纳入哈希，改动配置后旧对局不能静默恢复。
5. 本阶段设施/事件/城市样式仍受目录数量等合同约束；后续已由文末动态目录迁移解除固定数量和 ID 限制。城市样式沿用原特殊行动 ID，设施仍按 effectId 注册，共用同 effectId 的实例须引用相同脚本。不支持通过单张替换偷偷给共用行为族设置冲突脚本；新的行为族仍需完成通用内核迁移。
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
