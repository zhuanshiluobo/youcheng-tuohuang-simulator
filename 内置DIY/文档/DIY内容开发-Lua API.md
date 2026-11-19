# DIY 内容开发：Lua API

本文说明当前内容宿主实际开放的 Lua 合同，适用于 core `2.2.2` 对应实现。API 名称、字段大小写和版本均有校验；未列出的字段不能靠猜测添加。`apiVersion` 是某个接口的输入协议版本，不是内容包版本。

配套文档：[JSON 编写](DIY内容开发-JSON编写.md)、[Event 与处理器](DIY内容开发-Event.md)。

## 1. 入口、数据和执行规则

### 1.1 普通入口

```lua
return function(ctx)
    return {
        Effect.GainResource({
            recipient = ctx.playerId,
            resourceType = 'originium',
            amount = 1
        })
    }
end
```

`Effect.*` 和 `Flow.*` 创建声明；构造时不执行、不显示页面。Host 接收返回值后按效果树结算，遇到必答请求才暂停等待玩家。数组中的两个效果不会自动构成“支付成功才获得”；需要此关系时使用 `Effect.Condition`。

普通返回值有两种，不能混合：

| 返回形式 | 含义 |
| --- | --- |
| `{ effect1, effect2 }` | 连续效果数组；`{}` 表示没有效果 |
| `{ effects={...}, memory={...} }` | 效果数组加本 Lua 入口的持久数据；`effects` 必填，包装只接受这两个键 |

回调、事件订阅和纯查询的装配方式见 Event 文档。没有 `UI.Open`、`Event.Publish`、运行时自由订阅、文件读写或直接修改游戏状态的公开接口。

### 1.2 `ctx` 字段

| 字段 | 类型及内容 | 用途 |
| --- | --- | --- |
| `eventId` | 本次宿主事件的稳定字符串 ID | 区分具体调用事实；不是效果 ID |
| `eventType` | 事件名字符串 | 同一入口内区分初次调用、完成续接等 |
| `playerId` | 宿主玩家键，例如 `p1` | 当前调用上下文中的玩家；观察其他玩家的事件时，不应凭此猜受益人 |
| `stateRevision` | 非负整数 | 本次只读快照的版本；不是锁定未来状态 |
| `definitionVersion` | 已装配处理器版本字符串 | 内容一致性检查 |
| `goldVoucherCount` | 非负整数 | 当前上下文玩家金券总量 |
| `availableGoldVoucherCount` | 非负整数 | 扣除预留后的可用金券 |
| `reservedGoldVoucherCount` | 非负整数 | 已预留金券 |
| `payload` | 只读 table；结构随事件变化 | 事件事实、真实来源和完成结果，详见 Event 文档 |
| `memory` | 本入口持久数据的独立可写副本 | 本次修改后显式返回才保存 |

查询结果是只读快照；不要把给返回对象赋值当作修改游戏。目标必须在实际执行时重新合法，先查询成功不保证稍后一定成功。

事件牌选择入口额外提供 `ctx.payload.choiceId`：它是宿主验证后的卡内稳定选择 ID，零选项立即入口为空字符串。每个选择直接绑定自己的脚本；不要用 `optionIndex` 推断新合同的奖励。完成续接仍读取该完成事件实际提供的 payload，不能假设初次调用的所有字段都会自动带入。

### 1.3 ID、整数和容器

| 值 | 写法及限制 |
| --- | --- |
| 玩家 | 优先用 `ctx.playerId` 或查询返回的真实玩家引用；不要混用玩家键、主体键和候选 ID。常见 `p1`、`player:1` 分别用于不同协议 |
| 物品实例 | 实际 `itemId`，标识某一张牌／某一枚标记；不等于 `definitionId` |
| 内容定义 | `definitionId`，标识卡面规则／物品定义；不能用它冒充实例 |
| 区域 | 真实 `zoneId`；`localZoneId` 仅是某个载体内部的区域名，`templateId` 是区域类型 |
| 地图 | `locationId`、`routeId`、`slotId` 分别是地点、路线、槽位，不能互换 |
| 资源名 | 普通资源参数推荐 `originium / originium_shard / iron / pure_originium / gold_voucher`；费用表键使用 `Originium / OriginiumShard / Iron / PureOriginium / GoldVoucher` |
| Lua 数组 | 从 1 开始、连续、无空洞；不可混用字符串键。宿主格子编号、`optionIndex` 等可能从 0 开始，以字段说明为准 |
| 整数 | 不允许小数；接口范围仍受普通值序列化范围约束。普通规范化整数范围为 ±1,000,000,000 |
| 普通对象 | 字符串键 table；不能含函数、协程、循环引用、元表或隐藏 Effect 的对象 |

单份 Lua 源码最多 32 KiB，调用指令预算 10,000；普通返回值深度上限 8、元素预算 128，效果数预算 16。嵌套业务不会解除这些限制。Lua 沙盒没有任意 `require`、`io`、`os`、网络或可自行控制的随机源；随机使用宿主效果。共享模块必须已由内容包正式登记和冻结。

### 1.4 `ctx.memory`

memory 按“本局＋内容包中的脚本入口身份＋实际调用函数入口”保存，跨多次发动保留。**不按单张牌、单个能力或玩家自动分开。** A、B 共用同一个已登记 Lua 入口时可以共用数据；如果需要按玩家区分，在 table 内自行以玩家键分层。

```lua
return function(ctx)
    local memory = ctx.memory
    local times = memory[ctx.playerId] or 0
    memory[ctx.playerId] = times + 1
    return { effects = {}, memory = memory }
end
```

| 行为／限制 | 规则 |
| --- | --- |
| 不返回 `memory` 或返回 `nil` | 保留上次数据；只改 `ctx.memory` 不会自动保存 |
| 显式返回 `memory={}` | 清空本入口数据；单独 `return {}` 只表示无效果，不清空 memory |
| 返回 table | 整份覆盖，不做递归合并；保存后下次调用重新复制到 `ctx.memory` |
| 可存值 | 布尔、整数、字符串、连续数组、字符串键对象；不存宿主引用对象、Effect、函数或循环引用 |
| 大小 | 深度最多 8，总值数最多 128，字符串／键最多 1024 UTF-8 字节；本局入口槽位上限 4096 |
| 存档／恢复 | 进入存档，入口和源码哈希必须匹配；不能用同路径的新代码静默解释旧状态 |
| 提交时机 | 与本次 Lua 调用结果一起校验、提交；调用或编译失败不覆盖。随后独立子效果失败不会自动撤销已提交 memory |
| 纯查询／预览 | 可读取对应入口数据，但不能借这些入口写入 memory |

## 2. 只读查询

本节所有接口均无 UI、不执行业务。不存在的单个对象通常为 `nil`，无匹配集合通常为空数组；未装配快照、越权或版本不符会报错，不能一律当作空集合。

### 2.1 `GameData`、`PlayerData`

| 调用 | 参数 | 返回与用途 |
| --- | --- | --- |
| `GameData.Get()` | 无 | `eventId/eventType/stateRevision/playerCount/definitionVersion/gameId/phaseId/round/maxRounds/actionRound/startPlayerId/currentPlayerId/contentVersion` |
| `GameData.GetRound()` | 无 | 当前大回合整数 |
| `GameData.GetActionRound()` | 无 | 当前行动轮整数 |
| `GameData.GetPhase()` | 无 | 阶段 ID |
| `GameData.GetPlayers()` | 无 | 本局玩家引用数组 |
| `GameData.GetTurnOrder()` | 无 | 当前行动顺序的玩家引用数组 |
| `GameData.GetStartPlayer()` / `GameData.GetCurrentPlayer()` | 无 | 起始／当前玩家引用，时点无对应玩家时为空 |
| `GameData.GetEnabledDlcIds()` | 无 | 启用的扩展 ID 数组 |
| `GameData.IsDlcEnabled(id)` | 已登记扩展 ID 字符串 | 布尔值 |
| `PlayerData.Get(player)` | 玩家引用或玩家键 | 授权可见的玩家事实；不是完整可写状态 |
| `PlayerData.GetScore(player)` | 同上 | 当前分数 |
| `PlayerData.GetResources(player)` | 同上 | `originium/originiumShard/iron/pureOriginium/goldVoucher`，均为整数 |
| `PlayerData.GetCityLocation(player)` | 同上 | 城市所在地点 ID |
| `PlayerData.GetHand(player)` / `PlayerData.GetDiscard(player)` | 同上 | 实际卡牌 ID 数组；其他玩家私有信息受调用权限限制 |
| `PlayerData.GetBuiltFacilities(player)` | 同上 | 已建设施快照；需要叠层时使用设施专门查询 |

### 2.2 `Global`

| 调用 | 参数与返回 |
| --- | --- |
| `Global.Map.GetMap()` | 无参数；授权地图快照 |
| `Global.Map.GetLocation(locationId)` | 真实地点 ID → 地点对象 |
| `Global.Map.GetRoute(routeId)` | 真实路线 ID → 路线对象 |
| `Global.Map.GetRegion(regionId)` | 真实地区 ID → 地区对象 |
| `Global.Map.GetConnectedLocations(locationId)` | 真实地点 ID → 相邻地点数组 |
| `Global.Map.GetRoutesBetween(fromId,toId)` | 两个地点 ID → 连接它们的路线数组 |
| `Global.Map.GetInfluenceSlot(slotId)` | 真实槽位 ID → 槽位对象 |
| `Global.Map.GetResourcePoint(id)` | 真实资源点 ID → 资源点对象 |
| `Global.Map.GetCitiesAt(locationId)` | 地点 ID → 此处城市数组 |
| `Global.Map.IsRoad(routeId)` | 路线 ID → 是否为道路 |
| `Global.Map.FindInfluences(filter?)` | 下表过滤器 → 影响力实例数组 |
| `Global.Content.GetDefinition(id)` | 定义 ID → 内容定义快照 |
| `Global.Content.GetInstance(id)` | 物品实例 ID → 实例快照 |
| `Global.Content.FindZones(filter?)` | 下表过滤器 → 区域数组 |
| `Global.Content.FindInstances(filter?)` | 下表过滤器 → 实例数组 |
| `Global.Facilities.GetCityLayers(playerId)` | 玩家字符串键，如 `p1` → 每格 `slotIndex/cellZoneId/topItemId/coveredZoneId/coveredItemIds/allItemIds/orderDigest/orderRevision` |
| `Global.Facilities.GetTowerAdjacency(playerId)` | 同上 → `towerItemId/slotIndex/adjacentTopItemIds` 数组 |
| `Global.Facilities.GetSupplyLayers()` | 无参数 → 每槽 `slotIndex/slotZoneId/topItemId/itemIds/orderDigest/orderRevision` |
| `Global.Facilities.GetFactsDigest(playerId)` | 玩家字符串键 → 当前设施事实摘要；用于检测变动 |
| `Global.Scoring.GetFinalControl()` | 无参数 → 当前已提供的终局控制权投影；不是任意时点都存在 |

过滤器可以省略，或给一个 table。未列出的键拒绝；多个条件取交集。

| 字段 | 适用查询 | 类型／作用 |
| --- | --- | --- |
| `id` | 区域、实例 | 精确区域／实例 ID |
| `influenceId` | 影响力 | 精确影响力实例 ID |
| `ownerPlayerId` | 影响力 | 所有者玩家 ID 字符串 |
| `owner` | 三者 | 快照中的所有者主体键；不是控制者 |
| `controller` | 实例 | 控制者主体键 |
| `locationId` / `routeId` | 影响力 | 限制地图地点／路线 |
| `slotId` | 影响力、实例 | 限制槽位 |
| `slotIds` | 影响力 | 无重复槽位 ID 数组，最多 128 项 |
| `templateId` | 区域 | 区域模板 ID |
| `localZoneId` | 区域 | 载体内部区域名 |
| `parentId` | 区域 | 父区域／载体身份 |
| `definitionId` / `typeId` / `contentType` | 实例 | 定义、物品类型、内容分类过滤 |
| `zoneId` | 实例 | 实际所在区域 |
| `tag` | 实例 | 已登记标签 |
| `includeDescendants` | 区域、实例 | 布尔，默认 false；区域须同时给 `parentId`，实例须同时给 `zoneId` |
| `limit` | 三者 | 1～1024 整数；结果截断上限，不代表已经查询所有候选 |

除布尔、数组、整数外，上表筛选值均为不超过 1024 字符的字符串；使用查询得到的真实键，不自行拼出隐藏区域或实例。

`FindZones` 可以查询牌堆区域的 ID、类型和数量等元数据，包括干员牌堆。隐藏牌堆的 `topItemId/orderRevision/orderDigest` 为 nil，`FindInstances` 不因此公开堆内牌面或顺序；取得区域 ID 不代表获得任意读取或移动其中卡牌的权限。

### 2.3 `SourceData`

| 调用 | 专用过滤字段 | 作用 |
| --- | --- | --- |
| `SourceData.GetCurrentSources(filter?)` | `definitionId/abilityId/itemId/activeFaceId/sourceKey` | 当前生效来源 |
| `SourceData.GetGrantSources(filter?)` | 同上 | 本来源获授予的来源事实 |
| `SourceData.GetCallableActions(filter?)` | `itemId/sourceKey/actionId` | 当前玩家真实来源上的行动及其来源、次数、占用等资格；实际费用与行动额度由调用参数决定并复验 |
| `SourceData.GetTransferableTokens(filter?)` | `itemId/typeId/zoneId/carrierItemId/localZoneId` | 当前真实权限内可转移物品 |
| `SourceData.GetTransferReceiptPage({receiptId,offset,limit})` | `receiptId` 非空收据 ID；`offset` 非负整数；`limit` 1～8 | 当前真实来源／完成续接可读的 MoveItem v4 收据分页；返回 `items/nextOffset/total` |

前四者专用字段均为身份字符串，还允许 `cursor`（非负整数，默认从 0 开始）、`expectedStateRevision`（期望整数版本）、`expectedContentHash`（期望内容哈希）。每页最多 4 项，返回 `items/totalCount/nextCursor/protocolVersion/stateRevision/contentHash/authorizedPlayerId/ruleOwnerSourceKey/ruleOwnerItemId`；`nextCursor=-1` 表示结束。行动页另有 `enabledCount/payableMarkerCount/transferableTokenCount`。

必须来自 Host 装配的真实来源。没有来源时报 `source_facts_unavailable`，过期时报 `source_facts_scope_stale`；手填 `sourceKey`、`sourceItemId` 或 payload 不能授予权限。收据分页不能跨来源、跨局读隐藏信息。

`GetCallableActions` 不接受 `callPolicyRef` 过滤器，也不返回调用策略。每项字段如下：

| 字段 | 类型／作用 |
| --- | --- |
| `optionId` | 字符串，本次查询的行动选项身份；不是 `InvokeAction` 的输入字段 |
| `itemId` | 字符串，目标行动的真实载体实例 ID；传给 `InvokeAction.itemId` |
| `sourceKey` | 字符串，当前目标能力来源键，可用于进一步筛选 |
| `actionId` | 字符串，目标定义中已登记的行动 ID；传给 `InvokeAction.actionId` |
| `enabled` | 布尔，来源、次数、占用等当前资格是否满足；不表示入口费用已经支付或有足够行动额度 |
| `disabledReason` | 字符串，当前不可用原因；可用时为空 |
| `trackBindingId` | 字符串，当前使用轨道绑定身份；无绑定时可为空 |
| `factsDigest` | 字符串，本次候选事实摘要；用于识别事实变化，不作为支付收据 |
| `handlerHash` | 字符串，目标行动正文处理器的内容哈希 |

查询时不以入口费用、主要行动额度筛掉可供效果调用的目标；真正执行时，`InvokeAction` 根据三个 `omit` 参数重新检查并结算。需要完整候选集时，持续使用上一页的 `nextCursor` 查询，直到返回 `-1`，不能只读取第一页或把 `enabled=true` 当作一定能成功发动。调用参数见第 9.3.1 节。

## 3. 只唤起选择 UI、返回答案的接口

这些接口会检查候选资格、保存交互状态和答案，但**不会支付、移动、出售、建设或发奖**。纯 UI 不等于任意造一个候选列表都能显示：必须使用当前已有的页面和候选语义。

当前没有独立 `UI` 命名空间。纯选择入口只有 `Choice.options`、`Choice.amountRange` 和 `SelectFacilityPanel` 这三种合同。`Choice.branches`、`AllocateEffects`、`ChooseResources`、`SellResource`、`Build/Explore` 的选择模式及 `Flow.Choose*` 含业务，分别在后文说明。

### 3.1 `Effect.Choice`：候选选择

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `player` | 必填 | 实际回答玩家 |
| `options` | 必填 | 连续字符串候选 ID 数组；来自真实卡牌、玩家或已登记地图／设施候选协议 |
| `minSelections` | 默认 1 | 非负整数；0 表示允许不选／放弃 |
| `maxSelections` | 默认等于 minSelections | 至少 1、不小于最少值且不超过候选数量的整数；大于 1 才是多选。minSelections=0 时应显式给 maxSelections=1 或合法多选上限 |
| `promptKey` | 默认 `lua.choice.select` | 已登记提示文案键；不决定业务授权 |
| `interactionType` | 默认 `lua.choice` | 已登记交互类型；见页面表 |
| `resultContext` | 可选 | 普通对象；用于区分续接阶段，不能含 Effect／函数 |
| `completionHandlerId` | 可选 | 已注册的完成处理器 ID；不是 Lua 函数变量 |

| 参数／候选语义 | 预期 UI 操作页面顺序 |
| --- | --- |
| 真实卡牌候选 | 卡牌页 → 选中卡牌 → 确认；多选保留多选确认 |
| 真实玩家候选 | 玩家选择页 → 选中玩家 → 确认；可直接用 `player:1` 等真实候选，或用 `p1` 并为该项 label 提供唯一 `Text.Player(...)` 参数来证明玩家身份。最多 4 名，回答保留原候选 ID |
| `facility.supply.select`＋真实供应设施 | 供应设施卡牌页 → 选卡 → 确认；不执行回底／建设 |
| `effect.influence.place.target / effect.influence.remove.target / effect.influence.move.target` | 对应地图槽位／影响力高亮 → 选中 → 确认；options 必须是对应的真实槽位／影响力候选 |
| `effect.route.choose` | 地图路线高亮 → 选路线 → 确认；只返回原候选 ID |
| `lua.choice` 且 promptKey 为 `effect.influence.replace.target` 或 `effect.influence.replace.choose_target` | 地图替换目标高亮 → 选中 → 确认；不执行替换 |
| `effect.map.location.target` 且提示为 `core:interaction.token.initial.choose / core:interaction.token.adjacent.choose` | 地图地点选择 → 确认；要求正式真实地点候选，后续创建／移动 Token 另行执行 |
| `lua.choice` 且 promptKey 为 `effect.resource.choose_type` | 已登记的资源种类页 → 选择一种资源；只返回对应资源候选，不获得／出售资源 |
| `reward.choose`＋正式奖赏卡候选 | 奖赏卡牌页 → 选卡确认；只用 Choice 时仍不自动取得奖赏，完整取得使用 Flow.AcquireReward |
| 其他已登记业务专页 | 必须同时满足其请求身份、提示、候选及附加事实合同；仅改 interactionType 不保证能够显示，不能把内部请求类型当通用页面 API |
| 空候选且 `minSelections=0` | 直接结束，无页面 |
| 任意纯文本 ID／无语义菜单 | 不再有普通 `Option Row Template` 兜底，拒绝进入普通文字选项页 |

供应设施专页当前只支持 `minSelections=maxSelections=1`，候选必须全部仍是实际供应顶牌。正常完成值：单选字符串、多选数组，或允许不选时的空结果。指定 `resultContext` 后包装为 `{value=原值, context=原对象}`。只有选中的 ID，没有该 ID 对应业务已完成的含义。

不要把 `interactionType='effect.choice.execute'` 填给任意值菜单来绕过页面限制；业务选项必须使用真实 `branches.effects`。卡牌缺图不会退化成普通文字条目，而是保留卡牌语义并报告缺失。提示键和 `WithPresentation` 都不能强行改变页面类别。

### 3.2 `Effect.Choice`：单个数量选择

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `player` | 必填 | 回答玩家 |
| `amountRange.minAmount` | 必填 | 非负整数，最小数量 |
| `amountRange.maxAmount` | 必填 | 不小于最小值的整数；接口校验上限 Int32.MaxValue，同时受普通值范围限制 |
| `amountRange.sourcePlayer` | 可选，与下一项成对 | 用于计算当前可用库存的真实玩家 |
| `amountRange.resourceType` | 可选，与上一项成对 | 普通资源类型；Host 据当前可用量缩小上限，目标变化时刷新请求 |
| `resultContext` | 可选 | 普通续接对象 |
| `completionHandlerId` | 可选 | 已登记完成处理器 |

不能同时填 `options/branches/minSelections/maxSelections/promptKey/interactionType`。

UI：单数量调节页 → 调整整数 → 确认。完成值为整数；提供 `resultContext` 时为 `{value=整数,context=...}`。它不扣资源、不提供三资源分配、不产生出售收益。

```lua
Effect.Choice({
    player = ctx.playerId,
    amountRange = { minAmount = 0, maxAmount = 3 },
    resultContext = { stage = 'choose_amount' },
    completionHandlerId = 'diy.example.complete'
})
```

此例要求 JSON 已登记 `diy.example.complete`。回调必须先检查 `payload.outcome`，再从 `payload.normalizedResult.value` 取数量，重新提交真实业务效果。

### 3.3 `Effect.SelectFacilityPanel`

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `apiVersion` | 必填 | `1` |
| `player` | 必填 | 城市所属及回答玩家 |
| `allowDecline` | 必填 | 布尔；允许放弃时，放弃结果为失败 `player_declined` |
| `operationProfile` | 必填 | `city_top`：本人城市顶层设施；`effect.facility.retire`：再应用退役／绑定资格检查 |
| `emptyIsFailure` | 必填 | true：空集失败；false：完成并返回 `outcome='no_legal_target'` |
| `promptKey` | 必填 | 非空已登记提示键 |
| `itemIds` | 可选 | 实例 ID 数组，与实际合法顶层候选取交集；省略不额外限范围 |

UI：城市设施格选择页 → 选中合法顶层设施 → 确认。返回 `slotIndex`（0 起）、`facilityItemId`、`cellZoneId`、`orderDigest`。候选改变时重新验证／刷新，不默默换成同名设施。调用后尚未退役；需要以这些事实构造 `RetireFacility`。本接口没有任意 `completionHandlerId` 字段，续接来自已装配入口。

## 4. 条件、逻辑选择与次数分配

### 4.1 `Effect.Condition`

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `leftEffects` | 必填 | 左式 Effect 数组，依序执行；用于费用／条件动作 |
| `rightEffects` | 必填 | 左式满足才执行的 Effect 数组 |
| `ruleCondition` | 默认 true | 是否为规则条件式；影响前置执行／放弃入口 |
| `allowDecline` | 默认 false；规则式另由左式形状确定 | 布尔；非规则式可显式开放拒绝 |
| `decisionPlayer` | 可选 | 决策玩家；普通规则式由执行玩家决策 |
| `declinePromptKey` | 可选 | 执行／放弃入口文案键 |
| `apiVersion` | 可选 | 仅允许 `1`；新脚本通常省略 |

UI：规则式且左式唯一效果为 `PayResource`，直接由付款页提供付款／放弃；其他规则左式先显示执行／放弃，再执行左式 UI，成功后展开右式。`ruleCondition=false` 且不允许拒绝时直接执行左式；允许拒绝时先出现执行／放弃页。

左式失败不执行右式，**不会撤销已经成功提交的左式前缀**。对必须足量移除的费用，用 `RemoveInfluence` v2 或 v3 完整选择合同；不能依赖多个松散单枚移除自然组成原子费用。

```lua
Effect.Condition({
    leftEffects = {
        Effect.PayResource({payer=ctx.playerId, resourceType='gold_voucher', amount=2})
    },
    rightEffects = {
        Effect.GainScore({recipient=ctx.playerId, amount=1})
    }
})
```

### 4.2 `Effect.Choice`：逻辑分支

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `player` | 必填 | 选择玩家 |
| `branches` | 必填 | 1～16 项数组，每项见下两行 |
| `branches[].id` | 必填 | 唯一非空字符串；对应选项身份和表现绑定 |
| `branches[].effects` | 必填 | 真实业务 Effect 数组，不能用空数组／NoOp 伪装执行菜单 |
| `minSelections` | 默认 1 | 只允许 0 或 1；0 允许放弃，不需要额外“空分支” |
| `maxSelections` | 默认 1 | 只能为 1 |
| `promptKey` | 可选 | 提示文案键 |
| `interactionType` | 可选 | 省略或 `lua.choice` 归一为 `effect.choice.execute`；真实卡牌／玩家路由可显式使用其正式类型 |
| `resultContext` / `completionHandlerId` | 可选 | 与纯候选式相同，前者包装结果，后者指向已登记续接 |

不能混入 `options` 或 `amountRange`。UI：逻辑选择页 → 点击某项即提交 → 执行该分支实际业务及其所有必要子页面。此页不是已移除的普通文字选项模板。明确采用真实卡牌路由时保留卡牌页及确认流程。

此接口等待所选分支子树，传播真实失败；“点击过选项”不代表支付、移动或奖励成功。卡面必须选择其他玩家、再执行业务时，可以使用纯玩家选择续接，也可将真实业务绑定到合法玩家分支。

单卡双去向的专门页面：`interactionType='character.card.destination'`，branches 必须按 `hand/discard` 顺序提供两条真实移动分支，`minSelections=maxSelections=1`、不可放弃；用 WithPresentation 的 `interaction.prompt=Text.Ref('core:interaction.card.destination.choose',{card=Text.Item(真实实例ID)})` 绑定可见的 `core.generated_character_card`。页面只显示该实例一次，点击手牌／弃牌动作按钮后执行对应分支。此合同用于缪尔赛思流形，不是任意卡／任意去向的通用 UI 参数。

### 4.3 `Effect.AllocateEffects`

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `apiVersion` | 必填 | `1` |
| `player` | 必填 | 分配和提交玩家 |
| `options` | 必填 | 1～16 项选项数组 |
| `options[].id` | 必填 | 唯一 1～64 字符 ID，只含英文字母、数字、`_ . : -` |
| `options[].text` | 必填 | `Text.Ref(...)`，该效果式的转译文案 |
| `options[].maxExecutions` | 必填 | 0～99 整数，本项最多执行次数；0 表示不可分配 |
| `options[].effects` | 必填 | 本项每执行一次所展开的真实 Effect 数组 |
| `totalExecutions` | 必填 | 1～99 整数；整次分配上限 |
| `allowEndAction` | 必填 | 布尔；true 可通过结束行动提交尚未分满的当前分配；false 必须分满，且各项容量合计须足够 |
| `completionHandlerId` | 可选 | 已登记完成处理器 |

UI：通用数量页的效果分配模式 → 每行显示该效果文本 → 每次加号在结算区域最右追加一个不可点击按钮 → 每次减号删除该项最右侧按钮 → 分满确认，或在允许时以结束行动提交当前分配。按钮从左到右就是执行顺序，不按选项分组重排。

确认前只有草稿；确认后 Host 冻结顺序，逐次等待该项完整子树。首个普通业务失败使队列停止，保留已提交前缀，技术故障则保留故障现场。普通失败停止时外层仍以完成事件交付队列结果，必须检查内层 `outcome='stopped_on_failure'`，不能只看外层完成。

`payload.normalizedResult` 是摘要：`version='allocation_result_v1',resultRef,resultDigest,submitKind,allocatedCount,attemptedCount,completedCount,failedOrdinal,failureReason,outcome`。只有真实绑定的完成续接另可读 `payload.allocationResult` 完整只读视图，含 `sequence/allocatedCounts/executions/unexecutedFrom` 等；不要把它重新复制进普通 128 值 table。`submitKind` 为 `confirm/end_action`；`failedOrdinal` 从 1 开始、无失败为 0，`unexecutedFrom` 为尝试次数加 1。林的策略使用此类业务分配。

## 5. 资源、分数、支付和出售

本节各 API 的未列出字段均不应填写；特别是不要一律给无版本接口添加 `apiVersion`。

### 5.1 直接数值变化

| API | 字段：类型、范围、默认及作用 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.GainResource` | 必填 `recipient` 玩家、`resourceType` 普通资源名、`amount` 整数 1～100；可选 `reasonId` 原因字符串 | 无独立页面，增加资源 |
| `Effect.GainResource` 资格式 | 上述字段＋必须成对给 `apiVersion=2`、`qualification={kind='minimum_balance',resourceTypes={...}}`；集合 2～5 种普通资源名，无重复且含本次获得类型。执行前比较这些资源当前库存，允许并列最少 | 无独立页面；不再满足最少资格时失败、不发奖 |
| `Effect.GainScore` | 必填 `recipient`、`amount` 整数 1～100；可选 `reasonId` 原因字符串 | 无独立页面，增加分数 |
| `Effect.LoseResource` | 必填 `apiVersion=1`、`player` 发起者、`targetPlayer` 扣减对象、`resourceType`、`amount` 非负整数（执行器上限 Int32.MaxValue）；可选 `completionHandlerId` | 无独立页面；最多扣实际可用量，完成结果 `amount` 是实际值。0 不开数量页 |
| `Effect.LoseScore` | 必填 `player`、正整数 `amount`；可选整数 `minimumScore`，是扣分下限；省略不自动保底 0 | 无独立页面，直接扣分 |

`LoseResource` 不是足额付款。转移资源时只能按其实际完成量发给收款人；数量 0 不构造 `GainResource(amount=0)`。

### 5.2 `Effect.PayResource`

以下三种输入互斥；`allowSubstitution` 为可选布尔，默认允许正式来源提供的替代。

| 写法 | 字段 | 作用 |
| --- | --- | --- |
| 固定单资源 | `payer` 玩家、`resourceType` 普通资源名、`amount` 1～100、`allowSubstitution?` | 足额支付一种资源 |
| 固定复合费用 | `player` 玩家、`cost` 费用对象、`allowSubstitution?` | 一笔支付所有固定资源 |
| 总量分配 | `apiVersion=2`、`player`、`resourceType='total'`、`amount` 0～99、`allowedResourceTypes`、`cost?`、`allowSubstitution?` | 在允许资源之间分配合计 amount，再加固定 cost |

固定复合 `cost` 必须为非空对象，使用 `Originium/OriginiumShard/Iron/PureOriginium/GoldVoucher` 键，值为非负整数，例如 `{GoldVoucher=2,Iron=1}`。总量写法的附加 cost 可以省略。`allowedResourceTypes` 是这些枚举名组成的非空无重复数组，大小写不能改成普通资源参数的下划线拼写。

UI：付款前响应 → 必要付款确认 → 提交。固定费用且没有替代来源／预备行动上下文时可以直接支付；作为规则 Condition 唯一左式时由付款页提供放弃。总量 `amount>0` 显示资源数量分配页；`amount=0` 回到固定费用流程。

有真实替代来源时：原付款页 → 插入来源 → 替代数量分配／必要实例选择 → 返回同一付款草稿 → 最终确认。关闭替代不等于跳过其他确认。付款失败不会产生 Condition 右式收益。

### 5.3 `Effect.PaymentSubstitution`

只能在当前付款草稿的真实授权来源内使用；不是任意减免费用。

| 字段 | v1 | v2 |
| --- | --- | --- |
| `apiVersion` | `1` | `2` |
| `player` | 当前付款玩家 | 同左 |
| `paymentOwnerEffectId` | 真实付款所有者效果 ID | 同左 |
| `sourceZoneId` | 已授权存储区域 ID | 不填 |
| `substitutionBudget` | 1～2 整数，可抵扣预算 | 不填 |
| `sourceKey` | 不填 | 当前窗口提供的来源键 |
| `ruleId` | 不填 | 当前来源提供的替代规则 ID |
| `paymentGeneration` | 不填 | 当前付款代数，使用宿主事实，不自行递增 |
| `draftRevision` | 不填 | 当前草稿版本，使用宿主事实 |

UI：替代分配页 → 必要时选择真实消耗实例 → 确认／放弃配置 → 返回原付款页。此时只改草稿；实际扣款和物品消费在最终付款提交时发生。

### 5.4 获得分配、出售与采集

| API | 字段：类型、范围、默认及作用 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.ChooseResources` | 必填 `player`、`amount` 整数 1～99；没有资源种类自定义参数 | 三种基础资源数量分配页 → 合计恰好 amount → 确认 → 真正获得资源；不含至纯源石／金券 |
| `Effect.SellResource` 批量 | 必填 `apiVersion=2,player,saleMode='batch',pricePolicy='core.resource_sale'`；可选 `interactionType`（默认 `lua.choice`）、`promptKey`（省略为 `effect.resource.sell`；当前正式内容统一显式用 `core:interaction.sale.batch`） | 统一出售数量页 → 调整源岩／源石碎片／异铁／至纯源石四行、查看单价及预计收益 → 确认本批；全 0 结束 |
| `Effect.SellResource` 全部某资源 | 必填 `apiVersion=1,player,targetPlayer,resourceType,saleMode='all',unitPrice`；类型限三种基础资源，单价 1～99 | 无独立页面，读取指定玩家该资源钱包总量，清零并增加金券；不是按扣除预留后的可用量结算 |
| `Effect.Collect` | 必填 `apiVersion=1,player,scope='extra'`；目前无其他 scope | 地图额外采集选点／选路 → 航道费用及收款方小窗 → 核对总费用 → 确认 → 采集及响应 |

出售 v2 的 Host 语义是 `resource_sale_batch`，不是把“资源×数量”字符串送给纯 Choice。一次调用结算一次提交，结果含 `soldCount/revenue/finished`；需要连续交易时按完成结果续接。贸易街区、坎诺特和蒂亚戈共用此页面及提示合同。`ChooseResources` 则会发放资源，不能当作纯 UI 使用。

## 6. 城市、设施、探索和主要行动

### 6.1 `Effect.Build`

当前推荐 v3，建设执行定义版本为 `2.3.0`。它既负责选设施，也负责真实付款、取牌、落位和入场，不能用普通 `MoveItem` 免费搬进城市来代替建设。存档中的建设节点必须匹配当前执行定义版本，不自动把旧节点改写为新的付款流程。

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `apiVersion` | 必填 | `3` |
| `player` | 必填 | 建设玩家 |
| `targetMode` | 必填 | `select`：内部选择设施；`explicit`：已确定具体设施 |
| `sourceKind` | 必填 | `supply` 供应、`deck` 牌堆、`reserve` 备用区、`authorized` 当前来源授予的建设区域 |
| `paymentMode` | 必填 | `auto` 在建设面板选择资源或金券支付，select／explicit 均由玩家选择；`resources` 仅资源费用、`gold` 仅金券费用、`free` 免建设费用。资源费用计算建设折扣及合法付款替代；金券费用使用设施定义的金券价格。免费仍检查来源／位置 |
| `consumeMainAction` | 必填 | 布尔；是否消费本次主行动额度 |
| `sourceKey` | authorized 必填，其他来源不填 | 正式授权来源键；不能自行制造权限 |
| `expectedSourceZoneId` | 可选 | 期望真实源区域，用于防止目标已移动 |
| `allowDecline` | select 可选，默认 false | 是否允许准备阶段放弃；explicit 不能为 true，已付款后不能放弃 |
| `effectId` | select 可选 | 本次范围构造所用的实际效果身份；不要借用其他效果 ID |
| `includeStoredFacilities` | 仅 supply/select 可选 | 布尔，默认 false；纳入有权限的存储设施 |
| `excludeRainbow` | select 可选 | 布尔，默认 false；排除彩虹设施 |
| `maxGoldVoucherCost` | 可选 | 非负整数，金券费用上限；执行器上限 Int32.MaxValue |
| `facilityId` | explicit 必填，select 不填 | 真实设施实例 ID，不是定义 ID |
| `cityBoardSlotIndex` | explicit 可选 | `-1` 或省略由流程处理位置；`0～11` 指定城市格，仍检查占用／叠建规则 |

`sourceKind='reserve'` 必须 `paymentMode='free'` 且 `consumeMainAction=false`。explicit 不能带 `effectId/includeStoredFacilities/excludeRainbow`，select 不能带 `facilityId/cityBoardSlotIndex`。

| 模式 | 预期 UI 操作页面顺序 |
| --- | --- |
| select | 设施卡牌候选 → 付款准备／确认（免费跳过）→ 取牌及必要补供应 → 城市格／叠建选择 → 来源响应及入场子页面 |
| explicit + auto | 跳过选牌，建设面板中间仅显示指定实例对应的设施牌，并自动选中 → 玩家选择资源或金券支付 → 付款准备／必要响应与确认 → 取牌及必要补供应 → 城市格／叠建选择 → 来源响应及入场子页面；落位阶段中间仍只展示该牌，已指定合法格子时跳过位置选择 |
| explicit + resources／gold | 跳过选牌与支付方式二选一；按指定方式付款及处理必要响应 → 取牌及必要补供应 → 未指定格子时选择位置 → 来源响应及入场子页面 |
| explicit + free | 跳过选牌和建设付款 → 取牌及必要补供应 → 未指定格子时选择位置 → 来源响应及入场子页面 |

`explicit + auto` 的付款候选固定为 `facilityId`，不能换成其他牌；改选支付方式和保存／恢复不会改变指定实例或 `cityBoardSlotIndex`。来源无效、位置非法或两种支付方式都付不起时失败，不自动换牌或改用其他城市格。面板按实际支付能力启用两个按钮；选择支付方式前不会扣费。无版本旧直接建设的 `auto` 仍按旧规则自动优先资源、其次金券，新内容使用上述 v3 合同。

完成结果含 `outcome/builtItemId/definitionId/buildEffectId`；只有实际建成且必要响应完成才是 `built`。放弃／无候选不会返回可当作建成凭据的实例 ID。

仍接受原合法无版本直接建设输入：`player`（或原归一化的 `recipient`）、`facilityId`／`facilityInstanceId`、`cityBoardSlotIndex`、`paymentMode`、`reserveForFree`、`sourceId`、`sourceKind`、`sourceKey`、`expectedSourceZoneId`、`consumeMainAction`。位置、费用和来源字段含义同上；reserveForFree 默认为 false，true 使用备用免费建设；consumeMainAction 默认 true，备用免费路径不扣主行动；sourceId 为来源标识。旧路径在实例未命中时仍允许唯一供应顶牌的定义 ID 解析，因此新内容必须使用 v3 和实际实例，避免依赖此兼容规则。缺目标不自动开选择。

### 6.2 `Effect.Explore`

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `apiVersion` | 必填 | `3` |
| `player` | 必填 | 探索玩家 |
| `targetMode` | 必填 | `select` 或 `explicit` |
| `consumeMainAction` | 必填 | 布尔；赠送探索可设 false，但仍支付真实路费 |
| `targetLocationId` | explicit 必填；select 可选 | 真实目标地点；select 填写时限制目标 |
| `routeIds` | explicit 必填 | 无重复真实路线 ID 数组，按本次路线意图提供 |
| `recipients` | explicit 必填 | 数组，每项严格 `{routeId=路线ID,subject=收款主体键}`；路线须在 routeIds 中，同一路线不能重复指定 |

select 不接受 `routeIds/recipients`。UI：select 走地图选目标／选路／费用确认；explicit 复验外部已经确定的意图，不再替换成任意新目标。随后封存路线 → 预留路费 → 事件及必要响应 → 交付 → 按声明消费主行动。结果区分 `completed/cancelled/no_legal_target/failed`，不能自行编造支付收据。

### 6.3 `Effect.MoveCity`

| 字段 | 默认／允许值 | 作用 |
| --- | --- | --- |
| `player` | 当前调用玩家 | 城市移动执行上下文；不以此绕过当前玩家权限 |
| `targetLocationId` | 可选真实地点 ID | 已确定目标；兼容字段 `targetLocation` 可接同一地点引用，二者不应同时填写 |
| `targetPolicy` | `standard` | 可取 `standard / explored / explored_own_influence`，依次为通常合法范围、已探索地点、已有本人影响力的已探索地点 |
| `waiveBaseCost` | false | 布尔，免基础移动费用 |
| `costPolicy` | 可选 | `waived` 等价开启免基础费用；其他值没有额外策略效果，新内容优先用上一个布尔字段 |
| `consumeMainAction` | true | 布尔，是否消费主行动 |
| `clearInfluencePolicy` | `remove` | `remove` 移除相关影响力；`move` 按规则调遣它们 |
| `allowDecline` | false | 布尔，是否有前置执行／放弃 |
| `decisionPlayer` | 当前调用玩家 | 前置决策玩家 |
| `declinePromptKey` | 空 | 前置执行／放弃提示键 |

UI：可选执行／放弃 → 移动前响应 → 未指定目标时地图选点 → 基础费用付款（免除时跳过）→ 影响力离场及响应 → 城市移动 → 入场／事件子效果。`clearInfluencePolicy='move'` 时还需逐枚选择合法目的槽。`movementMode/candidateScope/sourceInfluencePolicy` 虽仍在构造白名单，但当前归一化不保留、不控制执行；不要填写它们期待产生另一条流程。

### 6.4 设施操作与主行动

| API | 字段：类型、范围、默认及作用 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.RetireFacility` | `apiVersion=2,player,facilityItemId,expectedCellZoneId,expectedOrderDigest`；实例、城市格和摘要来自当前选择／查询。v1 仍接受，但不强制后两项；新内容用 v2 防止叠层改变 | 无选设施页 → 复验当前顶层及摘要 → 退役／绑定牌离场／必要响应 |
| `Effect.ActivateFacilityEntry` | `player`、`instanceId`，指本人实际设施实例 | 无选设施页 → 执行该设施入场正文，页面由正文决定 |
| `Effect.GrantMainActions` | `player`、整数 `amount`（1～1,000,000,000，受普通规范化整数范围限制，累计额度也不得溢出）；`lockCharacterCard` 可选布尔，默认 true；`allowedActionTypes` 可省略／空表示不限制，非空时为 `deploy/dispatch/explore/move_city/build/special` 中任意无重复组合 | 无独立页面；增加本行动轮指定范围的额度，立即继续后续效果，不等待额度用完 |
| `Effect.ExecuteMainAction` | `player,executionMode='immediate',allowedActionTypes`；候选数组非空、无重复，元素来自 `build/explore/move_city/deploy/dispatch/special`；不接受 amount 或 lockCharacterCard | 逻辑主行动选择页（单项也选择）→ 所选实际行动及必要响应 → 完成 → 后续效果 |
| `Effect.OverrideNextStartPlayer` | `player` 发起上下文、必填 `targetPlayer` 实际目标玩家；`scope` 默认且只支持 `current_round` | 无页面；记录本轮产生的下一大回合起始玩家覆盖。只填 player 不足以指定目标 |

`ExecuteMainAction` 不赠送额度，结果为 `outcome/executed/actionType/actionEffectId/actionResult`；只有子树实际完成且 `outcome='completed'` 才有 `executed=true`。`grant_budget/facility_entry` 模式不支持；额度用 `GrantMainActions`，设施入场用 `ActivateFacilityEntry`。

`GrantMainActions.allowedActionTypes` 中，`deploy` 是部署，`dispatch` 是调度，`explore` 是探索，`move_city` 是移动城市，`build` 是建设，`special` 是特殊行动。列表只控制新授予的这批额度，不改变此前额度；未知类型、重复项和非字符串元素会被拒绝。`lockCharacterCard=true` 会同时锁定本玩家行动轮的角色牌使用，填 false 则不因本次授予新增该锁定，不会解除已有锁定。

每批额度分别保存剩余次数和允许类型。执行行动时优先消耗允许范围最窄的匹配批次；范围大小相同时按授予顺序消费，无限制批次排在有限制批次之后。没有匹配的剩余额度就不能发动该行动，即使总剩余次数大于 0。正常换到新的玩家行动轮时重置；未用完就结束行动轮也会清空。

例如，额外获得一次只能用于部署或调度的主要行动：

```lua
Effect.GrantMainActions({
    player = ctx.playerId,
    amount = 1,
    allowedActionTypes = { 'deploy', 'dispatch' }
})
```

## 7. 影响力与地图

### 7.1 确定目标的基础影响力合同

下面的 `executingPlayer` 为真实玩家键或 `{playerId=...}`。`targetInfluence` 可为真实实例 ID 或 `{influenceId=...}`；`targetSlotId` 可为槽位 ID 或 `{slotId=...}`。引用 table 只填该引用字段。

| 公共字段 | 允许值与用途 |
| --- | --- |
| `subjectFilter` | 可选 `self/player/opponent/any`；限制本人、玩家主体、其他玩家、任意允许主体，不能扩大业务权限 |
| `excludedSubjectKeys` | 可选无重复主体键数组，如 `player:1` 或正式公司键 `company:stonehoove_security` |
| `causeKind` | 可选原因字符串；通常放置／移动／移除默认 `direct`，替换默认 `replace`。不能通过改原因取得权限 |
| `influenceSource` / `replacementSource` | 真实供应来源 ID；也可 `{kind='player_supply'或'company_supply',sourceId=真实来源ID}`，宿主复验类型和供应 |
| `ownerSubject` / `replacementOwner` | 真实玩家主体／公司主体；例如 `{kind='player',playerId=ctx.playerId}`，公司只允许已登记的 `stonehoove_security`。归属不是执行玩家的同义词 |

| API／写法 | 字段和限制 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.PlaceInfluence` | 必填 `executingPlayer/influenceSource/ownerSubject`；可选 `targetSlotId`、`candidateScope`（槽位 ID 数组）、`targetLocationId`、`itemTypeId`（已登记影响力物品类型）、公共筛选／原因；`replaceEffectId` 只接真实替换父效果身份 | 显式合法槽位时直接放置；省略时地图选择合法槽 → 确认 → 放置及附着／入场响应 |
| `Effect.RemoveInfluence` 单枚 | `executingPlayer`；`targetInfluence` 或非空 `candidateScope`（影响力 ID 数组）；`destination` 默认且只允许 `player_supply`；`reasonId?`；公共筛选／原因；可省略版本或填 1 | 指定实例直接移除 → 离场响应；仅给候选范围时地图选择一枚 → 确认 → 移除 |
| `Effect.RemoveInfluence` 严格批量 | 必填 `apiVersion=2,executingPlayer,targets,requiredCount`；targets 为无重复实例 ID 数组，条数不超过 requiredCount；requiredCount 为 1～16；可选 `reasonId/subjectFilter/excludedSubjectKeys` | 无选择页；整组复验、足量预检 → 移除 → 离场响应。不足则不进行部分扣除 |
| `Effect.MoveInfluence` | `executingPlayer,targetInfluence`；`targetSlotId?`；公共筛选／原因 | 来源和目标都指定则直接移动；缺目标槽则地图选目的槽 → 确认 → 移动及响应 |
| `Effect.ReplaceInfluence` | `executingPlayer,targetInfluence,replacementSource,replacementOwner`；可选 `placementFailurePolicy='keep_removal'`、`itemTypeId`、公共筛选／原因 | 无目标选择页 → 旧影响力移除及响应 → 原槽放置替代影响力及响应 |

严格 v2 不接受 `targetInfluence/candidateScope/destination/causeKind`。选择数量和当前合法数量都必须满足要求才提交；它可用于条件左式，也可独立使用。`ReplaceInfluence` 不是同样的足量原子费用：放置失败保留已发生的移除，没有整组替换回滚。

公开 Lua 的 `MoveInfluence` 不接受执行器内部 `moves` 数组。需要多步用下述 v3 或多个明确效果，不把内部 DTO 当作公开字段。

### 7.2 `RemoveInfluence / MoveInfluence / ReplaceInfluence` v3 选择模式

三个构造器使用同一组字段，不能再混入上一节的显式目标、替代来源或目的槽字段。

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `apiVersion` | 必填 | `3` |
| `targetMode` | 必填 | `select` |
| `executingPlayer` | 必填 | 实际执行玩家 |
| `subjectFilter` | `self` | `self/player/opponent/any` |
| `excludedSubjectKeys` | 空数组 | 无重复正式主体键 |
| `selectionScope` | 空字符串 | 空：一般合法范围；`resource_points`：资源点；`routes`：路线 |
| `distinctPolicy` | `none` | `none` 不增加去重条件；`instance` 不重复实例；`subject` 不重复主体 |
| `count` | 1 | 1～16，最多处理次数／完整选择数量 |
| `requiredCount` | 0 | 0～count，至少完成数量 |
| `requireCompleteSelection` | false | true 时先选齐再执行；只支持移除／替换，且 requiredCount 必须等于 count |
| `allowDecline` | false | 布尔；开放本操作的放弃入口 |
| `candidateIds` | 不填 | 仅移除、count=requiredCount=1 且完整选择时可给无重复真实候选 ID，用于收窄范围 |
| `promptKey` | 不填 | 首次选择提示键 |
| `nextPromptKey` | 不填 | 后续选择提示键 |
| `interactionType` | 不填 | 移动只接受空、`lua.choice`、`character.ability.choice.move`；不用于借用无关页面 |

UI：普通模式逐次在地图选择，执行该次操作并等待响应后刷新候选；移动还要选择对应目的槽。完整模式在地图多选，显示已选项，选足后确认；严格移除复用 v2 足量提交。替换仍逐项按 `keep_removal` 执行，没有“全部替换失败则撤回移除”的保证。

已确定目标、不需要任何选择的影响力效果通常只产生简要结算和地图变化；其事件响应仍可能展开别的页面。因此“该操作无选择页”和“整棵响应树没有交互”是两件事。

### 7.3 其他地图接口

| API | 字段：类型、范围、默认及作用 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.StoreRemovedInfluence` | 必填 `apiVersion=1` 或 `2`、`player,cardInstanceId,localZoneId,influenceItemId`；v1 要求目标区域 entryPolicyId 为 `core.removed_owned_influence`；v2 走当前来源的 carrier.store 权限和类型校验；均只能在真实离场去向响应中接管该枚方块 | 无选择页；暂存已确定的离场实例，保留离场去向协议 |
| `Effect.PlaceRoad` | 必填 `apiVersion=1,player,routeId`；真实合法路线与道路库存 | 无选择页，放置道路 |
| `Effect.SetLocationsOpen` | 必填 `locations` 地点 ID 数组（最多 16）、`isOpen` 布尔；`reasonId?` 原因字符串 | 无选择页，改变地点开放状态 |
| `Effect.OperateToken` | 必填 `apiVersion=1,player,tokenItemId,operation='move',destinationLocationId,operationProfileId='core.adjacent_location_token_move'` | 无选 Token／选地点页，明确目标后移动及响应 |
| `Effect.CreateCityInfluenceLock` | 必填 `apiVersion=1,player,definitionId`；定义须为已登记锁组件且来源合法 | 无选择页；建立来源绑定锁，跟随和到期由规则处理 |
| `Effect.RevealEventCard` | 实际生效参数：`targetLocationId`（兼容 `resourcePoint`）为真实地点、`eventColor` 可取 `green/yellow/red`、默认 green；`primaryInfluenceSlot?` 为真实槽位引用；`player` 使用当前调用上下文 | 不先选地图地点；若已有资源点标记跳过，否则翻牌 → 有选项时事件牌选项页 → 分支付款／放置影响力等子页面 |

`RevealEventCard` 的 `eventDeck/placeResourcePointIndicator/postResolveDestination/allowedOptions/targetLocation` 仍在构造白名单，但不进入当前实际归一化，不控制流程。不能用它们定制翻牌。Event 文档说明事件牌调用与规则事件的区别。

## 8. 物品移动和牌堆

### 8.1 `Effect.MoveItem` v4：单组转移

本接口只提交已经确定的移动。`selector.mode='select'` 拒绝；它不打开选择、数量分配或执行确认。合法业务响应／在途角色依赖可能需要等待，但不是移动节点自行选目标。

```lua
Effect.MoveItem({
    apiVersion=4, player=ctx.playerId, operation='transfer',
    selector={ mode='explicit', itemIds={actualItemId},
        expectedSource={zoneId=actualSourceZoneId} },
    destination={zoneId=actualDestinationZoneId},
    insertion={kind='unordered'},
    operationProfileId='core.player_marker_storage', requiredCount=1
})
```

上例变量必须从真实授权查询／已确认事实取得。

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `apiVersion` | 必填 | `4` |
| `player` | 必填 | 执行玩家；权限依据真实来源和实际物品状态 |
| `operation` | 必填 | 本节为 `transfer`；其他模式见下文 |
| `selector` | 必填 | `explicit` 或 `top`，结构见下表 |
| `destination` | 必填 | `{zoneId=真实区域ID,mustBeEmpty?=布尔}`，默认不额外要求全区为空；或 Token 使用 `{kind='original_supply'}` 回各自真实原供应 |
| `insertion` | 必填 | `{kind='unordered'}` 无序区、`{kind='top'}` 顶、`{kind='bottom'}` 底、`{kind='slot',slotId=真实槽位}`；必须匹配区域结构。original_supply 只允许 unordered |
| `operationProfileId` | 必填 | 下表转移 profile 之一；决定物品类型和来源／目的域规则，不是任意权限开关 |
| `requiredCount` | 必填 | 0～99；本笔必须满足的最低数量，top 不得超过 count。正数不足整笔失败；0 允许合法零变化 |
| `carrierPolicy` | 默认 `preserve` | 仅此值；载物仍遵守载体生命周期自动离场策略 |
| `reveal` | 默认 false | 仅角色牌 profile 可用；真实移动后公开展示事实并等待响应，不是额外确认 |

| selector 形式 | 每个字段 |
| --- | --- |
| explicit | `mode='explicit'`；`itemIds` 无重复真实实例 ID 数组，最多 99；`expectedSource={zoneId=非空真实源区,slotId?=非空原槽位}`，逐项复验来源 |
| top | `mode='top'`；`zoneId` 真实有序源区；`count` 1～99；`itemTypeId` 真实可接受物品类型。按执行时正式顶部取物，不允许读隐藏牌序后挑选 |

| profile | 适用功能 |
| --- | --- |
| `core.facility_card_transfer` | 设施牌转移；不自动执行建设、城市叠层或入场奖励 |
| `core.character_card_transfer` | 角色牌转移、交接和载物退出；原所有权不因此随控制权改变 |
| `core.reward_card_transfer` | 已存在奖励牌的合法移动；不等价生成／获得新奖励 |
| `core.cube_storage` | 合法方块存取；需精确载体来源授权 |
| `core.player_marker_storage` | 玩家标记存取／轨道移动；同步当前实际权限，遵守互斥 |
| `core.token_storage` | 其他已登记 Token 的合法库存转移 |
| `core.mobile_city_move` | 移动城市实例在合法区域间转移；遵守来源、目标、容量及载体规则。仅移动实例，不自动执行城市移动行动、路费、入场效果或城市移动事件；城市有活动绑定标记时直接转移会拒绝，须通过城市移动流程处理标记跟随 |

企业同一实例中，同一玩家跨合作等级／子区最多一枚标记；第一条宣告与其 usage 区共享互斥域。允许零枚；企业无合作标记时等级状态是 `-1`，没有升级／特效资格。直接移动不补发到达奖励、不自动宣告计分、不重置已使用次数。供应区仍检查原所有者／原供应资格。

结果含 `outcome/changed/movedCount/movedItemIds/receiptId/reasonCode`。必要时通过 `SourceData.GetTransferReceiptPage` 读完整收据；不要把简要列表当作可写状态。目标过期时失败，不重新补选；多个独立单枚步骤不会自动合并成一笔原子移动。

### 8.2 批量及专门共同操作

| `operation` | 允许字段和含义 | UI／执行边界 |
| --- | --- | --- |
| `transfer_batch` | 顶层仅 `apiVersion=4,player,operation,transfers,expectedOrders`。`transfers` 非空，每项为上一节单组的 `selector/destination/insertion/operationProfileId/requiredCount/carrierPolicy?`；只允许 explicit，同一批 profile 一致，所有实例全局无重复且合计最多 99 | 无页面；按整个计划预检、提交，不等价多个独立根效果 |
| `move_map_token` | `apiVersion=4,player,operation,selector,destinationLocationId,operationProfileId`；selector 为单枚 explicit，profile 只能 `core.adjacent_location_token_move` | 无页面；按正式地图邻接移动规则处理 |
| `return_city_style_marker` | `apiVersion=4,player,operation,selector`；selector 为单枚 explicit | 无页面；返还本人符合条件的二级样式标记至真实原供应，不能自定 destination |
| `cover_character` | `apiVersion=4,player,operation,selector,cover`；selector 为单枚 explicit；cover 见下一段 | 无页面；实际盖放及生命周期，不提供任意免费主链盖牌能力 |

`expectedOrders` 必填数组，可以为空；每项严格为 `zoneId` 非空区域、`digest` 非空当前顺序摘要、`orderRevision` 非负整数、`expectedTopItemId` 字符串（空堆可为空）。区域不得重复，事实必须来自当前授权快照，不能自行算一个可绕过校验的摘要。

`cover` 必填字段：`mode='additional'`、`acceptedTypeId='core.character_card'` 或 `'core.generated_character_card'`、`reusable` 布尔、`blockedActionRoundSequence` 非负整数。可复用盖放只接本人手牌且阻塞序号为 0；不可复用可接本人手牌／弃牌，阻塞序号必须是当前真实行动轮序列。附加盖放须绑定真实来源，不冒发正常主链盖放事件。`mode='main'` 虽为共同执行器内部模式，但仅 Host 正常主链任务可使用，DIY 不能自行获得此权限。

### 8.3 专门移动包装

| API | 当前字段 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.MoveFacilityCard` 普通转移 | `apiVersion=2,player,itemRefs,expectedSource,destination,insertion,operationProfileId='core.facility_card_transfer'`；`requiredCount?` 非负整数、`carrierPolicy?='preserve'`。itemRefs 每项 `{itemId=真实ID}`；源 `{zoneId,slotId?}`；目的 `{zoneId}`；insertion 同前 | 无选择页；复验后移动 |
| `Effect.MoveFacilityCard` 回底补槽 | `apiVersion=4,player,operation='cycle_supply',selector={mode='explicit',itemIds={单枚ID},expectedSource={zoneId,slotId?}}` | 无选择页；供应顶牌回底 → 仅原槽实际为空时补牌；下层仍在不补 |
| `Effect.MoveCharacterCard` | `apiVersion=2,player,itemRefs,expectedSource,destination,insertion={kind='unordered'},operationProfileId='core.character_card_transfer'`；`requiredCount?`、`carrierPolicy?='preserve'`、`reveal?=false`。引用结构同上 | 无选牌／选玩家页；等待真实在途角色与载物退出 → 转移 → 必要交接／展示响应 |
| `Effect.ReturnCityStyleMarker` | `apiVersion=2,player,selector={mode='explicit',itemIds={单枚真实标记ID},expectedSource={zoneId,slotId?}}` | 无选择页；明确返还；结果 `changed/markerItemId` |
| `Effect.OperatePlayerMarker` | `apiVersion=1,player,operation='transfer',selector,destination,insertion,requiredCount`；selector 仅 explicit；其他结构同 v4 单组，`carrierPolicy?='preserve'`；包装固定玩家标记 profile | 无选择页；明确移动并同步当前资格；结果 `changed/movedCount/movedItemIds` |

设施回底前的选择使用真实供应卡牌页；城市样式返还前的选择使用后文 `Flow.ChooseCityStyleMarkerReturn`。不让包装偷偷创建选择请求。

仍接受的通用 `MoveItem` 有限旧输入：v1 使用上述 `itemRefs/expectedSource/destination/insertion/operationProfileId/requiredCount?/carrierPolicy?/reveal?` 单组；v2 使用 `player,operationId,transfers,expectedOrders`，transfers 各项使用 itemRefs 且固定设施 profile、requiredCount 等于项内枚数；v3 使用 `player,itemIds,expectedSource,destination,insertion={kind='unordered'},operationProfileId,requiredCount,carrierPolicy?`，只用于明确 Token，requiredCount 为 1～99。它们只在新输入处规范化为共同 v4，不表示旧执行节点可恢复；新内容统一使用 v4。

### 8.4 补牌与洗牌

| API | 字段 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.RefillFacilitySlot` | 必填 `apiVersion=1,player,slotZoneId`；slotZoneId 为真实供应槽区域 | 无页面；当时堆顶补入空槽。槽已占用失败，空堆合法零变化；不洗牌 |
| `Effect.RefreshFacilitySupply` | 必填 `apiVersion=1,player`；不接受 slotZoneId | 无页面；原供应逐张回底 → 洗设施堆 → 按槽顺序逐步从当时顶部补牌 |
| `Effect.ShuffleDeck` | 必填 `apiVersion=1,player,deckZone={zoneId=真实牌堆}` | 无页面；在权限内随机化该堆顺序 |
| `Effect.ShuffleFacilityDeck` | 同上，但 zoneId 必须为实际设施牌堆 | 无页面；洗设施堆 |

补牌使用共同移动能力，洗牌是独立随机操作。恢复已完成洗牌不重复消耗随机。不能先读未来隐藏牌序再声称按顶补牌。

## 9. 企业、角色规则和来源事务

### 9.1 企业

本节默认必填 `apiVersion=1,player`。企业参数用实例 ID，所有目标仍受合作标记与来源资格检查。

| API | 其他字段：取值及作用 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.UpgradeEnterprise` | `enterpriseItemId` 单实例或 `enterpriseItemIds` 无重复候选数组，二选一；`levels` 正整数。v2 可另给 `targetPlayer`（默认 player），指定实际被升级玩家 | 显式目标直接逐级升级／每级到达奖励；列表多项先企业选择，单项自动定目标，空集结束。v2 由发起者选企业，目标玩家处理自己的奖励 |
| `Effect.ChangeEnterpriseLevel` | 必填 `enterpriseItemId`、非零整数 `levelDelta`、布尔 `resolveArrivalReward`；true 结算到达奖励，false 只改等级 | 无选企业页；改级 → 必要到达奖励子页面 |
| `Effect.SwitchDepartment` | 必填 `enterpriseItemId`；可选 `departmentItemId` 为具体科室板实例 | 明确科室直接切；省略则科室板选择页（单项也确认）→ 新科室当前等级奖励；无合法项结束 |
| `Effect.ActivateEnterpriseSpecial` | 必填 `unlockedByPlayer` 为解锁资格玩家；`minLevel?=1,maxLevel?=5` 均 1～5 且前者≤后者；`enterpriseItemIds?` 无重复企业范围；`candidateIds?` 无重复正式特效候选 ID；`specialId?` 为指定特效 | 指定 specialId 时必须同时给恰好一家 enterpriseItemIds，跳过选择直接发动；否则特效选择页（单项也选择）→ 正文及响应 |

`levels` 不限于 1 的接口可以逐级升级；`Flow.UpgradeEnterpriseForAllPlayers` 单独仅支持 1。升级封顶、缺合作标记或来源变化须读实际结果，不凭请求的 levels 发额外奖励。v2 升级结果区分 `initiatingPlayerId/targetPlayerId` 及真实升级收据；移动企业标记不等于调用升级。

### 9.2 角色生命周期

| API | 字段：取值及作用 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.CoverCharacterCard` | 必填 `apiVersion=3,player,cardInstanceId,coverMode='additional',blockedActionRoundSequence,acceptedTypeId,reusable`；后三项含义同 MoveItem.cover，真实来源绑定 | 无选牌页；明确角色附加盖放，返回 `coveredItemId/coverSlotId` |
| `Effect.SetCharacterDoubleUseRule` 当前使用 | `apiVersion=1,player,scope='current_use',useId,sourceItemId`；useId 和 sourceItemId 必须为真实使用窗口和来源；不填过期序号 | 无页面；登记本次使用规则，后续方式／顺序 UI 属于主流程 |
| `Effect.SetCharacterDoubleUseRule` 行动轮 | `apiVersion=1,player,scope='action_round',expiresActionRoundSequence`；序号必须是当前真实正整数行动轮序列；不填 useId/sourceItemId | 无页面；登记本行动轮期限规则 |
| `Effect.ConsumeCharacterDoubleUseSource` | 必填 `apiVersion=1,player,useId,sourceItemId`，均为真实消费证明对应身份 | 无页面；消费明确来源及等待载物退出；不自行授予双用资格 |
| `Effect.RecycleCharacterCover` | 必填 `apiVersion=1,player,coveredItemIds,sourceItemId`；ID 数组最多 16、无重复 | 无选牌页；回收指定盖牌及等待必要依赖 |
| `Effect.BindCharacterToFacility` | 必填 `apiVersion=1,player,characterItemId,facilityItemId`；真实明确两张牌 | 无选择页；建立绑定；真实离场自动按生命周期解绑 |

普通转移不能改写使用历史、创建角色使用窗口或重置次数。`CoverCharacterCard` 无版本主链签名只属于 Host，不是 DIY 入口。

### 9.3 来源调用、延迟义务和奖励

| API | 字段：取值及作用 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Effect.InvokeAction` | 必填 `apiVersion=1,player,itemId,actionId`；可选布尔 `omitEntryCost/omitEntryAfterEffects/omitMainAction` 均默认 false；精确调用当前玩家真实来源上的已登记行动，无需额外调用许可，逐字段说明见第 9.3.1 节 | 无选来源页 → 未省略的入口费用／条件及其页面 → 正常次数／占用结算 → 行动正文及其页面 → 未省略的入口后置效果 → 必要的主要行动额度消费；页面由实际执行的效果决定 |
| `Effect.ScheduleCleanupObligation` | 必填 `apiVersion=1,player,handlerRef,dueRound,context`；handlerRef 是本来源已登记处理器，dueRound 正整数，context 普通对象；要求 `cleanup.defer:<handlerRef>` 权限 | 登记时无页面；到指定回合该玩家收尾窗口 → 执行处理器返回的业务页面 |
| `Effect.GrantReward` | 必填 `apiVersion=1,player,definitionId,typeId,count,recipientPlayer,destination,operationId`；count 1～16；typeId=`core.award_card`；destination=`core.zone.player_rewards`；definitionId/typeId/operationId 为非空≤128字符字符串，operationId 为本次稳定操作键 | 无选择页；生成指定奖励交付目标玩家 → 获得奖励响应 |
| `Effect.CreateMapToken` | 必填 `apiVersion=1,player,definitionId,sourceItemId,destinationLocationId,operationProfileId='core.adjacent_location_token_move'`；definitionId 为已登记组件，sourceItemId 须匹配真实来源，地点须已开放且存在资源点标记 | 无选择页；创建合法地图 Token；同一来源已存在对应 Token 时返回该 tokenItemId，不重复生成 |
| `Effect.ActivateCityStyleSpecial` | 必填 `apiVersion=1,player,specialActionId,declarationMarkerId,activationSource`；实际样式行动和宣告标记；activationSource 只接受 `public_style/immediate`，分别对应合法公共样式调用／即时发动合同 | 无选样式页 → 入口条件／付款 → 特殊行动正文；不凭填写来源免印刷费用 |
| `Effect.RollDice` | 必填 `apiVersion=1,player,count=1,sides=6`；当前只支持单枚六面骰 | 无操作页；Host 投骰，返回 `{diceResult=1到6}`；后续按点数创建的页面属于续接效果 |

`GrantReward` 要求真实来源声明精确 `reward.grant:<definitionId>:core.award_card:<最大数1～16>:<self/other/any>:core.zone.player_rewards`，并且奖励定义 acquisitionPolicy 为 `content_granted`。`RecycleCharacterCover/BindCharacterToFacility/CreateMapToken/ActivateCityStyleSpecial` 等会核对真实调用祖先；填写 sourceItemId 或复制 payload 都不能伪造资格。

### 9.3.1 `Effect.InvokeAction`：效果内调用真实行动

由正在执行的效果调用当前玩家可用的行动，目标通常从 `SourceData.GetCallableActions` 取得。目标须有真实生效来源及 `actions[]` 中的行动定义；接口不会自动选择载体或创建目标行动，也不要求调用方与目标另行登记许可。

| 字段 | 必填／默认 | 能填什么、做什么 |
| --- | --- | --- |
| `apiVersion` | 必填 | 整数 `1` |
| `player` | 必填 | 当前执行玩家；目标必须是该玩家按实际来源资格可调用的行动 |
| `itemId` | 必填 | 非空字符串，目标行动的真实载体实例 ID；不是定义 ID |
| `actionId` | 必填 | 非空字符串，该载体定义中已登记的行动 ID，须与 itemId 对应 |
| `omitEntryCost` | false | 布尔；true 时省略目标 `entryCostRef` 产生的入口费用／条件子树；false 时正常检查、支付并等待完成。不省略行动正文内部另行定义的费用 |
| `omitEntryAfterEffects` | false | 布尔；true 时省略目标 `entryAfterEffectsRef` 产生的入口后置效果；不跳过正文或其他必要事件响应 |
| `omitMainAction` | false | 布尔；true 时本次调用不消费主要行动额度；false 时目标为 special 行动则正常检查并消费匹配额度，quick 行动不会因此变成需要主行动额度的行动。不授予额外额度 |

三个参数必须使用 Lua 布尔值，不能填字符串或 0/1。默认调用保留费用、入口后置效果和目标原本需要的行动消费；只有内容规则明确豁免时才把对应参数设为 true。它们都不豁免使用次数、生命周期、实际来源、占用和角色使用互斥等其他条件。

下面是已从正式候选取得并选定 `action` 后的调用片段，示例规则明确省略三项包装：

```lua
Effect.InvokeAction({
    apiVersion = 1,
    player = ctx.playerId,
    itemId = action.itemId,
    actionId = action.actionId,
    omitEntryCost = true,
    omitEntryAfterEffects = true,
    omitMainAction = true
})
```

正常付费调用可直接省略三个 `omit` 字段。`omitEntryCost=true` 不显示被省略费用子树的付款／条件页面；`omitEntryAfterEffects=true` 不显示该后置子树的页面；`omitMainAction` 本身不新增页面。无论如何组合，行动正文自己的交互仍正常执行。

当前接口不接受 `callPolicyRef`，JSON 不再声明 `actionCallPolicies/directCallPolicy` 或 `action.invoke:*`。`allowedOperations` 中其他机制所需的声明仍按各自合同处理，不参与本接口的调用许可判断。

本次调用的三个布尔参数及真实付款、激活、行动消费收据会保存到存档，恢复时沿已提交阶段继续，不重复扣费或扣次数。旧合同的调用记录（包括已完成的历史记录）不会自动补写新参数；应按存档匹配的内容和执行器版本验证恢复，不能把旧 `callPolicyRef` 记录当作当前合同执行。

## 10. Flow：包含业务的组合流程

当前公开 12 个 Flow。它们不是纯 UI 别名；会组织真实子效果、等待响应并报告结果。除 `DispatchOperator` 使用 `apiVersion=2` 外，均必填 `apiVersion=1,player`；下面列出其余字段。

### 10.1 企业和地图流程

| API | 字段：取值及作用 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Flow.UpgradeDistinctEnterprises` | 必填 `count` 1～16、`levels` 正整数；分别是不同企业数量和每家升级级数 | 选择一家企业 → 升级及到达奖励 → 再选尚未选择企业，直到 count；候选不足失败，单项也保留该轮选择 |
| `Flow.UpgradeThenActivateEnterpriseSpecial` | 必填 `levels` 正整数，可为 2，表示升级两级；`enterpriseItemIds?` 无重复企业实例范围 | 按升级后资格选择一个企业特效 → 该企业逐级升级及奖励 → 发动先前选定特效 |
| `Flow.ActivateAllEnterpriseSpecials` | 必填 `unlockedByPlayer`；`minLevel?=1,maxLevel?=5` 均 1～5，范围不能倒置；`enterpriseItemIds?` 无重复企业范围 | 多家时先选一家 → 该家多项特效时选择发动顺序 → 逐项执行；只有一家／一项时跳过相应选择。“全部”是选中企业的冻结集合，不是遍历所有企业 |
| `Flow.UpgradeEnterpriseForAllPlayers` | `enterpriseItemId` 或无重复候选 `enterpriseItemIds`，二选一；必填 `levels=1` | 必要企业选择（显式／单项跳过）→ 按冻结玩家顺序各升级一次及各自奖励；无合作标记者不升级 |
| `Flow.ClearRouteAndPlaceRoad` | 必填 `scoreRemovedPlayers`、`scoreExecutor`，各为 0～99；分别给被清理玩家和执行者的分数 | 地图选合法路线 → 逐项影响力离场及响应 → 放道路 → 计分；无合法路线结束 |

### 10.2 卡牌、奖励和兑换流程

| API | 字段：取值及作用 | 预期 UI 操作页面顺序 |
| --- | --- | --- |
| `Flow.DispatchOperator` | 必填 `apiVersion=2,player,deckZoneId`；`deckZoneId` 为实际干员牌堆区域 ID；`excludedReturnItemIds?` 为不能用于返还的卡牌实例 ID 数组。基础抽牌数读取本局最终配方 `operatorDrawCount`，不接受 drawCount 参数 | 前置事件收集纯数量补丁 → 从指定牌堆抽候选 → 干员卡牌页选择／放弃 → 接受时获得、其余回原堆洗牌 → 合法手牌／弃牌页选一张移至退役区；放弃时候选全回原堆洗牌并得 1 分。牌堆为空时直接得 1 分，无选择页 |
| `Flow.ReplaceHandWithOperator` | 无其他字段 | 干员卡牌候选 → 选一张 → 本人手牌候选 → 选被替换牌 → 提交交换；空池／空手结束 |
| `Flow.AcquireReward` | 必填 `poolRole='awards'` 或 `'contracts'` | awards：正式奖励卡页 → 选卡确认 → 取得及响应；contracts：自动取合法合同 Token，无卡牌选择；上限／池空时结束 |
| `Flow.ExchangeResource` | 必填 `targetPlayer` 实际兑换者、`resourceType` 支出资源、`receiveResourceType` 获得资源（两种须不同）、`amount='all'`、`rate` 1～100；`reasonId?` 原因字符串 | 自身无选择页；执行到目标玩家时读取实际可用库存，按真实付款 → 获得资源逐批执行；付款前响应可展开页面 |

干员特派 v2 的 `deckZoneId` 必须是有权操作的真实 `core.zone.card_deck`，现有牌须为干员牌；未知区域、手牌区、设施牌堆或越权牌堆拒绝，不按“空堆”发分。牌堆不足时只抽现有数量，不补造卡牌。`excludedReturnItemIds` 只过滤返还候选，不过滤抽牌候选；返还区域由业务规定，Lua 不开放内部 `allowedReturnZoneIds`。

基础数量读取已合并并冻结的 `SetupRecipe.OperatorDrawCount`，范围 1～99；不在特派内再次判断感染者或人数。内置配方默认 2、感染者覆盖为 3、双人局最低 3，扩展配方修改会实际生效。首次执行通过 `OperatorDispatchStarting` 专门入口收集纯数量补丁，合并后保存本次数量，再抽牌。调整只影响本次特派，不改整局配方；不创建调整数量的 Effect、普通事件响应子树或交互请求。最终数量随存档保留，恢复不重复调用已完成的前置查询、抽牌或得分。

```lua
Flow.DispatchOperator({
    apiVersion=2,
    player=ctx.playerId,
    deckZoneId=Global.Content.FindZones({localZoneId='operator_deck'})[1].id,
    excludedReturnItemIds={}
})
```

本例使用正式共享干员牌堆；DIY 有多个牌堆时应按自己的区域身份确定目标，不使用任意首项。完成结果包含 `accepted/changed/selectedOperatorItemId/returnItemId/reasonCode/deckZoneId/drawCount/scoreAwarded`。空牌堆为正常完成：`accepted=false,changed=true,reasonCode='empty_deck',scoreAwarded=1`；主动放弃为 `reasonCode='player_declined',scoreAwarded=1`；无合法返还牌为 `reasonCode='no_candidates',changed=false,scoreAwarded=0`。v1 的 drawCount 签名不再接受。

数量修正的返回值、字段和作用域见 [12.6 干员特派数量前置修正](#126-干员特派数量前置修正)。

`ExchangeResource` 禁用替代支付，每批最多付 50 且收益不超过 100；失败不生成该批收益，已完成前批不撤回；合法 0 库存可以完成。结果 `paidAmount/receivedAmount/changed/targetPlayer` 均是实际事实。

### 10.3 移动之外的业务选择

`Flow.ChooseTransferCount`：

| 字段 | 必填／默认 | 含义 |
| --- | --- | --- |
| `itemIds` | 必填 | 1～99 个无重复真实 Token ID；确定顺序，选择 K 即使用前 K 个 |
| `sourceZoneId` | 必填 | 当前真实源区 |
| `destinationZoneId` | 必填 | 真实目的区，不能等于源区 |
| `operationProfileId` | 必填 | 合法 Token profile：`core.cube_storage/core.player_marker_storage/core.token_storage` |
| `maximumCount` | 必填 | 1～itemIds 数量，最多 99 |
| `unitCost` | 可选 | 费用枚举名到非负整数的对象；每项费用乘 K，总额不能溢出 Int32；省略不收该费用 |
| `allowDecline` | 默认 false | 布尔，允许通过当前选择放弃 |

UI：1～maximumCount 的真实逻辑选择，超过 16 项分层呈现 → 如有费用先正式付款 Condition → 依既定顺序逐枚明确 MoveItem。每层最多 16，不截断候选。返回 `selectedCount/declined/changed/movedCount/failedCount`；移动失败不重试、不撤销已成功前缀；Flow 不额外授予转移权限。

`Flow.ChooseCarrierTokenPairReturn`：必填 `carrierItemId` 真实载体、`localZoneId` 卡内方块储区、`operationProfileId` 合法 Token profile。必须是本人当前控制、当前来源获授权的 `card_cube_storage`。

UI：按类型、原所有者、控制者、原供应及本地状态分组构造真实返还组合；能唯一确定时直接确定，组合数≤16 时平铺逻辑选择，更多时分阶段选择 → 最终一笔足量 MoveItem 将两枚返还各自原供应。开放离场实例不能互相当作等价项折叠。它有真实返还业务，不是只返回两个 ID。

`Flow.ChooseCityStyleMarkerReturn`：没有其他字段。UI：本人合法二级样式返还逻辑选择（单项仍选择；超过 16 项分层）→ 执行明确 `ReturnCityStyleMarker`。空池失败，真实返还失败传播；不会让 Condition 右式在返还未成功时发奖。

## 11. 文案、效果表现与续接数据

这些接口本身都不打开页面，只给已有业务和已有 UI 提供数据。

### 11.1 `Text`

| 调用 | 参数和作用 |
| --- | --- |
| `Text.Ref(key,arguments?)` | key 为已登记文案键；arguments 为具名对象，对应模板参数；参数可用整数、文本及下列类型化引用 |
| `Text.Player(id)` | 真实玩家身份；整数 1 会形成 p1 文案引用，不是可写玩家对象 |
| `Text.Item(id)` | 真实物品实例 ID，按可见性显示其名称 |
| `Text.Definition(id)` | 内容定义 ID |
| `Text.Resource(id)` | 正式资源 ID |
| `Text.Location(id)` | 正式地点 ID |

文本引用只替换模板参数，不选择玩家、不移动物品、不打开详情。可见性不足时不能借文案取隐藏卡面。

### 11.2 `Effect.WithPresentation(spec,presentation)`

返回带表现的原效果声明副本。`spec` 是实际构造器结果；`presentation` 可用字段如下，均为可选，不填保留宿主默认表现。

| 字段 | 类型和作用 |
| --- | --- |
| `started / waiting / succeeded / failed / stopped` | `Text.Ref`，对应结算阶段文案 |
| `interaction.prompt` | 当前既有交互的提示 `Text.Ref` |
| `interaction.progress` | 当前进度 `Text.Ref` |
| `interaction.options` | 候选表现数组，逐项结构见下表 |
| `interaction.quantityRows` | 数量行表现数组，逐项结构见下表 |
| `interaction.candidateIntegerLabel` | 整数候选模板 `{key=文案键,arguments={{name=参数名,multiplier=整数},...}}`；参数由实际候选数值乘 multiplier（默认 1）生成 |
| `presentationScript` | 已登记纯表现脚本路径；不执行规则 |

| 子项 | 字段 |
| --- | --- |
| `options[]` | `candidateId` 真实候选 ID；`label/description/disabledReason` 可选 Text.Ref；`iconKey` 可选已登记图标键 |
| `quantityRows[]` | `rowId` 真实数量行 ID；`label/description/unit` 可选 Text.Ref；`iconKey` 可选已登记图标键 |

例：为真实分支附加文本，不制造空业务选项：

```lua
Effect.WithPresentation(
    Effect.Choice({player=ctx.playerId,branches={
        {id='gain',effects={Effect.GainScore({recipient=ctx.playerId,amount=1})}}
    }}),
    {interaction={options={
        {candidateId='gain',label=Text.Ref('diy:choice.gain_score')}
    }}}
)
```

表现不能改合法候选、费用、结果或权限，也不能把卡牌／逻辑执行／数量页面换成普通文字条目。静态标题、按钮名称、布局、缩放和样式由现有场景／预制体决定，Lua 不直接修改 Unity 对象。

### 11.3 `Effect.WithContext(spec,context)`

`spec` 为真实效果声明；`context` 为可序列化普通对象，提供完成续接上下文。使用自定义阶段键，例如 `{stage='after_move'}`；回调从 `payload.continuationContext` 读。

`requiredOutcome`、`executionBinding` 是宿主封闭合同的保留键；允许按下表使用，但不能作为任意业务数据或自造证明：

| 保留对象 | 字段和作用 |
| --- | --- |
| `requiredOutcome` | 严格 `{protocolVersion=1,resultField=非空顶层结果字段名,expectedValue=非负整数}`，resultField 不含点。表示已承担的必做义务；要求实际完成、不可放弃且结果整数恰等于期望。不满足则 `required_outcome_unfulfilled`，保留已提交事实并暂停后续，不能用作普通可失败费用 |
| `executionBinding` | 严格 `{protocolVersion=1,parentBuildId=真实建设ID,choiceInteractionId=真实已回答选择ID,playerId=所选玩家正整数}`。仅来源已有 `execution.bind` 权限、建设及必做响应已完成、选择确属该树并选中该玩家时成立；用于受证的其他玩家正文执行，不授予任意跨玩家权限 |

这两个对象提取为执行元数据，不作为普通 continuationContext 原样传回。WithContext 不是 memory，不在不同发动之间自动共享；也不等同于仅包装 Choice 返回值的 `resultContext`。

## 12. 专门入口合同

这些入口必须由 JSON 声明和 Host 以对应 responseKind 调用；普通效果处理器不能混合返回它们。它们都不自行弹独立页面。

### 12.1 候选集修正

`Candidate.Add({ids,reasonCode})`、`Candidate.Remove({ids,reasonCode})`、`Candidate.Intersect({ids,reasonCode})` 返回候选补丁。`ids` 为该次候选协议中的真实 ID 数组；`reasonCode` 为可诊断原因字符串。返回补丁数组，不返回 Effect。分别表示添加允许候选、去除候选、与声明集合取交集；添加仍受宿主原始池及授权限制。

只在 `CandidateSetBuilding` 的 `candidatePatches` 入口中可用，当前角色持续候选修正针对正式设施建设候选。它不是打开选择页：Host 完成候选计算后由原业务显示原选择页。

### 12.2 纯规则查询和影响力附着

纯规则查询入口返回严格 `{queryId=当前查询ID,basisDigest=当前事实摘要,items={...}}`。三字段均必填，ID／摘要必须原样回传；items 的具体字段由声明的 `queryKind` 决定，见 Event 文档的查询合同部分。此入口不安装 Effect／Flow，不能以查询为名支付、选择或发奖。

`influencePlaceAttaching` 是放置附着改写入口，只允许返回规定的改写描述；不是普通规则查询，也不能任意返回 Effect 来改放置流程。其字段及来源限制见 [Event 与处理器](DIY内容开发-Event.md)。

### 12.3 `Settlement.Offer`

只在 `settlementOffers` 入口使用，返回 Offer 数组。上下文为 `ctx.playerId/stateRevision/window/facts`；window 提供 `id/kind/ownerEffectId/playerId/targetUseId/triggerId/stateRevision/paymentId/paymentCost`（费用事实按窗口提供），facts 是本次来源授权事实。

| 字段 | 能填什么、做什么 |
| --- | --- |
| `apiVersion` | 必填 `1` |
| `localId` | 非空≤128字符，须以当前 abilityId 加 `.` 开头；同窗口来源内唯一 |
| `player` | 当前窗口玩家 |
| `sourceItemId` | 真实来源实例 |
| `windowId` | 当前 ctx.window.id |
| `ownerEffectId` | 当前 ctx.window.ownerEffectId |
| `maxUses` | 必填 `1` |
| `visibility` | 必填 `owner` |
| `availabilityHandlerId` | 已登记可用性处理器 ID |
| `presentation` | 下述结算表现对象 |
| `effects` | 非空真实效果声明数组；获准插入时才实例化 |

字符串身份字段不能超过 128 字符。Offer 只把合法来源提供给已有结算窗口，不能凭任意 windowId 创建新窗口。可用性处理器返回 `{enabled=布尔,disabledReasonKey?=统一文案键}`，不能返回效果。

结算 `presentation` 字段：`label/description/ariaLabel` 是 Text.Ref；`iconKey` 是已登记图标键；`contentMode` 为 `text/icon_text/icon`，默认 text。不会改变窗口的实际使用权限。

### 12.4 `Settlement.WithPresentation(spec,metadata)`

给已有结算段加元数据，不开放新插入权限。metadata 字段：

| 字段 | 取值和作用 |
| --- | --- |
| `entryKind` | 必填 `cleanup_order` |
| `localId` | 必填非空稳定段 ID |
| `wholeEffectPolicy` | 必填 `optional/required/unspecified`，声明整段可选／必做／未指定 |
| `sourceItemId` | 必填真实来源实例 |
| `sourceEffectId` | 可选真实来源效果 ID |
| `abilityId` | 必填真实能力 ID |
| `presentation` | 上一节结算表现对象 |

身份字符串最多 128 字符。元数据只对应已经合法存在的段，不能借 optional 跳过规则要求的付款或必答请求。

### 12.5 纯表现入口

宿主以 `Present` 等登记入口调用纯展示脚本，返回 `resolution`（Text.Ref）、`interaction`（11.2 的交互表现结构）、`details`（Text.Ref 数组）。字段按实际展示用途填写，不返回 Effect 或业务查询结果。只能更新已有展示内容，不能作为独立 UI 调用。

### 12.6 干员特派数量前置修正

`OperatorDispatchStarting` 使用专门的 `operatorDispatchPatches` 返回合同，与候选集修正同属纯数据前置入口。没有 `Effect.ModifyOperatorDispatchDrawCount`，也不通过其他 Effect 修改该数量。

```lua
return function(ctx)
    return { drawCountDelta = 1 }
end
```

| 返回字段 | 能填什么 | 作用 |
| --- | --- | --- |
| `drawCountDelta` | 可选，整数 -99～99；省略等于 0 | 正数增加本次抽牌数，负数减少；不是总数 |

返回 `{}` 表示不修正。多个当前有效来源均读取相同基础数量，Host 累加全部增减值，再统一校验最终值为 1～99；与来源调用顺序无关。非法返回或最终越界使本次特派失败，不抽牌、不按空堆发分。不存在 set 覆盖或按最后一个响应取值的规则。

`ctx.payload` 提供 `dispatchEffectId`（本次效果 ID）、`playerId`（接受特派的玩家整数 ID）、`deckZoneId`（指定牌堆）、`baseDrawCount`（本局配置的基础数量）。角色持续来源另有 `cardInstanceRef/sourceItemId`；通用能力来源有 `sourceItemId`。`ctx.playerId` 是当前响应来源的执行玩家，不一定是接受特派的玩家。

入口不安装 Effect、Flow、Settlement，不接受效果数组、交互、完成续接或 memory 覆写；可读取 `ctx.memory`。角色订阅仍须是当前激活的 persistent 能力，`observerSubjectPolicy='self'`，只修正本人的特派，不能配置该事件的 `eventResponseAttachments`。通用能力来源按实际生效区域和授权查询；DIY 模式／扩展用 `ruleBindings` 声明事件，能力标识为 `diy.rule_bindings.dispatch.v1`，不能填写 completion。该查询不消耗一次性使用次数。完整登记方式见 [Event 文档](DIY内容开发-Event.md#46-干员特派前置事件)。

## 13. 不开放的名称与编写检查

当前没有以下公共执行入口：`Effect.Repeat`、`Effect.LoadCardMarkers`、`Effect.SelectInfluence`、`Effect.DepartureBatch`、`Effect.ChooseBuild`、`Effect.ChooseExplore`、`Effect.TransferCharacterCard`、`Effect.MoveCharacterCardToZone`、`Effect.RetainCharacter`、`Effect.StorePlayerMarker`、`Effect.ReturnPlayerMarker`、`Effect.UpgradeEnterpriseForPlayer`、`Effect.ReleaseFacilityBinding`。旧名、旧类型包装或自行构造 effectTypeId 都不能绕过退役校验。

也不能使用移动 selector.select、MoveFacilityCard v3 内置选择、ReturnCityStyleMarker v1 内置选择、CoverCharacterCard v2、无版本角色／设施移动及无版本设施洗牌、OperatePlayerMarker 的公开 round_cleanup、ExecuteMainAction 的 grant_budget/facility_entry。这不是需要填写另一组提示键的页面问题。

编写时依次核对：

1. JSON 注册的脚本、处理器、文案、来源和调用策略完整，命名空间及输入版本在本文内。
2. 每次调用只使用该模式允许字段，ID 来自真实查询／事件，区分定义、实例、区域、主体与候选 ID。
3. 需要纯选择时用第 3 节；点击后执行规则的业务菜单用真实逻辑分支；移动前完成选择，不回流普通文字模板。
4. 条件成本按足量合同提交，续接先检查结果再发收益，区分完成、无变化、放弃和失败。
5. 检查目标变化、来源失效、空候选、库存不足、取消、存档恢复和重复回答；隔离测试不代表正常页面验收。
6. 正常页面验收从独立程序菜单、建房和合法行动进入，按版本／内容哈希和 GameId 累积覆盖；未出现的随机内容记录为未覆盖。
