# DIY 内容开发：Lua API

更新：2026-10-28。核对基线：`3a398b86a1a5433714dd5dc4a9fc55aa4a68213a` 及本次工作区生产源码。

本文面向独立编写 DIY 规则的内容作者，配套 [JSON 编写文档](DIY内容开发-JSON编写.md)。参考 [Lua效果系统API初稿](Lua效果系统API初稿.md) 的分层思路，但**接口名称、参数、返回形状以当前宿主、编译器和执行器为准**。初稿中的设想不能直接当成可调用 API。

本文是源码合同说明，不是正常对局、UI 或联机验收报告。只使用本文示例也不代表已经完成内容包装配；运行入口、资源、文案和能力声明见配套文档。

## 1. 使用范围与剔除项

依据 [待移除与未完成事项](../../prompt/企业家内容实施规划-20261019/04-高风险语义与回归清单.md)，本版执行以下排除规则。该表只用于迁移检查，不属于可用接口表。

| 清单项 | 新内容规则 | 当前事实及替代方向 |
| --- | --- | --- |
| REMOVE-01 | 禁用 `Effect.Repeat` | 次数式构造器仍有内核、编译器和表现残留。固定次数直接展开有限 Effect 数组；依赖上一步结果时使用完成回调。收尾来源 `repeatPolicy = once/unbounded` 是另一机制，继续保留 |
| REMOVE-02 | 不接入无卡面候选的旧多选显示链路 | 保留卡牌多选、玩家多选、普通执行选择。多个合法效果的组合可编码为一个选项，以 `effect.choice.execute` 做 1 选 1；不能借此取消真实规则中的多选约束 |
| REMOVE-03 | 禁用 `Effect.LoadCardMarkers` 及旧名/旧类型包装 | API 已正式退役。使用当前 `MoveItem` 转移协议及真实来源授权；不能造一个同义包装恢复旧固定装载语义 |
| REMOVE-04 | 不复制未证实有消费者的企业家文案 | 按真实引用和可见消费者登记文案；不能仅因静态搜索不到就删除动态键或用户手改文案 |
| REMOVE-05 | 不提供插入结算按钮的悬浮文字入口 | 保留 hover 视觉、点击、拖动、禁用态、详情和无障碍名称；不是禁用全局提示或全部 `Text` API |

此外，旧草稿中的 `Effect.TransferResource`、`Effect.PayCardMarkers`、`Effect.ResolveCharacterCard`、任意注册 CLR 执行器和运行时自由订阅事件，不是本版公开构造器。资源转移应根据真实语义组合失去/支付和获得；牌上标记支付使用来源声明与统一支付机制。

本次只更新文档，没有删除上述代码残留、测试、文案或界面资产。

## 2. 最小脚本与执行模型

普通效果脚本返回一个 `handler(ctx)` 函数，函数返回从 1 开始、无空洞的 Effect 数组。无效果返回 `{}`，不能返回 `nil`、单个 Effect 或 C# 对象。

```lua
return function(ctx)
    return {
        Effect.GainResource({
            recipient = ctx.playerId,
            resourceType = 'originium',
            amount = 2
        })
    }
end
```

构造器只创建声明；调用 `Effect.GainResource(...)` 时资源尚未变化。Host 在 Lua 返回后校验、编译并依序执行。返回的普通兄弟节点之间没有自动“前者失败则后者取消”关系；需要支付成功才给奖励时必须用 `Condition`。等待选择、响应或子节点时，Host 保存可恢复状态；不能保存 Lua 闭包、VM 或协程来等待玩家。

同一次 Lua 调用内查询得到的是同一版本快照。不能返回一个变更后立刻在同一次调用中查询其结果；应从新的 `EffectCompleted` 回调继续。

### 2.1 `ctx`

| 字段 | 含义 |
| --- | --- |
| `eventId`, `eventType` | 当前真实事件身份和类型 |
| `playerId` | 当前执行玩家键；直接传给接受玩家参数的接口，不自行改编号 |
| `stateRevision` | 本次快照修订号 |
| `definitionVersion` | 当前处理器版本 |
| `goldVoucherCount`, `availableGoldVoucherCount`, `reservedGoldVoucherCount` | 当前玩家金券总量、可用量、已预留量 |
| `payload` | 当前入口授权的只读数据；形状随入口而变 |

`payload` 不是任意 GameState。角色、设施、事件牌、纯查询和收尾来源的字段各有合同；不能把一个脚本的 payload 原样假定为所有入口都有。规则查询和结算查询还有专门的返回合同，见第 7 节。

### 2.2 引用、数组、数值

- 定义 ID、物品实例 ID、区域实例 ID、能力 ID、处理器 ID 是不同身份。引用实际物品时不能拿 `definitionId` 替代 `itemId`。
- `GameData.GetPlayers()` 返回玩家引用，引用含 `playerId`。给其他玩家发资源/分数时优先传宿主返回的玩家引用，不能随意拼一个字符串绕过目标校验。
- Lua 数组下标从 1 开始；payload 中的棋盘槽位、选项索引沿宿主合同，不能一律加减 1。例如事件牌 `optionIndex` 从 0 开始。
- 整数不接受小数、无穷大或 NaN。各接口范围独立，不能把同一个 `amount` 上限套给所有接口。
- API 使用点调用，例如 `PlayerData.GetScore(ctx.playerId)`；冒号调用会多传一个参数。

### 2.3 沙箱与预算

可使用普通 Lua 运算、字符串、table、`pairs/ipairs` 等已启用基础模块。禁止文件/系统/网络/Unity/CLR 访问；`os`、`io`、`debug`、`package`、`coroutine`、`math`、`CS`、`System`、`UnityEngine` 和 `require/load/loadfile/dofile` 不可用。**整个 `math` 模块均未启用**；随机结果交给 `Effect.RollDice` 等 Host 流程。

默认预算为：脚本 32 KiB UTF-8、10,000 条 VM 指令、顶层返回最多 16 个 Effect、返回表深度 8、单表条目 128。嵌套 Effect 数组也有 16 项限制，不能通过套表无限展开。权威规则入口不以机器墙钟耗时判定规则成败；表现入口仍有时间保护。预算可由宿主配置，内容不能修改。

共享函数通过 `pack.json` 的 `luaModules/scriptDependencies` 冻结为 `Shared.<alias>`，不是运行期 `require`，详见 JSON 文档。

## 3. 只读查询

返回值及其子表都是只读快照。字段缺失、隐藏信息和查询未装配是不同情况：单个 ID 不存在通常返回 `nil`，集合无匹配通常返回空数组；缺少相应快照会报错，不能全部当作空集合继续。

### 3.1 `GameData` 与 `PlayerData`

| 调用 | 返回 |
| --- | --- |
| `GameData.Get()` | `eventId/eventType/stateRevision/playerCount/definitionVersion/gameId/phaseId/round/maxRounds/actionRound/startPlayerId/currentPlayerId/contentVersion` |
| `GameData.GetRound()` / `GetActionRound()` | 大回合/行动轮整数 |
| `GameData.GetPhase()` | 阶段 ID |
| `GameData.GetPlayers()` / `GetTurnOrder()` | 玩家引用数组；后者表达行动顺序 |
| `GameData.GetStartPlayer()` / `GetCurrentPlayer()` | 玩家引用或空值 |
| `GameData.GetEnabledDlcIds()` | 本局启用扩展 ID 数组 |
| `GameData.IsDlcEnabled(id)` | 布尔值；缺配置快照时拒绝查询 |
| `PlayerData.Get(player)` | 玩家公开字段、可见事实；私有字段依调用权限 |
| `PlayerData.GetScore(player)` | 分数 |
| `PlayerData.GetResources(player)` | `originium/originiumShard/iron/pureOriginium/goldVoucher` 资源表 |
| `PlayerData.GetCityLocation(player)` | 城市位置 ID |
| `PlayerData.GetHand(player)` / `GetDiscard(player)` | 卡牌 ID 数组；其他玩家读取受权限限制 |
| `PlayerData.GetBuiltFacilities(player)` | 已建设施快照数组；全层信息用下表的设施查询 |

建议始终显式传 `ctx.playerId` 或真实玩家引用，不依赖省略参数的兼容行为。资源 Effect 推荐使用 `originium`、`originium_shard`、`iron`、`pure_originium`、`gold_voucher`；查询表字段使用上表的驼峰拼写，不能混用。

### 3.2 `Global`

| 调用 | 参数与结果 |
| --- | --- |
| `Global.Map.GetMap()` | 当前授权地图快照 |
| `GetLocation(locationId)` / `GetRoute(routeId)` / `GetRegion(regionId)` | 单个地图对象；均位于 `Global.Map` |
| `GetConnectedLocations(locationId)` / `GetRoutesBetween(fromId, toId)` | 相邻位置/两位置之间路线数组 |
| `GetInfluenceSlot(slotId)` / `GetResourcePoint(id)` | 影响力槽位/资源点对象 |
| `GetCitiesAt(locationId)` / `IsRoad(routeId)` | 所在城市数组/是否道路 |
| `Global.Map.FindInfluences(filter?)` | 影响力数组；筛选键见下文 |
| `Global.Content.GetDefinition(id)` / `GetInstance(id)` | 内容定义/物品实例快照 |
| `Global.Content.FindZones(filter?)` / `FindInstances(filter?)` | 区域/物品实例数组 |
| `Global.Facilities.GetCityLayers(playerId)` | 城市各格的完整设施层级 |
| `Global.Facilities.GetTowerAdjacency(playerId)` | 核心塔相邻关系 |
| `Global.Facilities.GetSupplyLayers()` | 供应各槽完整层级 |
| `Global.Facilities.GetFactsDigest(playerId)` | 设施事实摘要，用于发现版本变化 |
| `Global.Scoring.GetFinalControl()` | 当前已提供的终局控制权投影；其他时点不保证可用 |

设施查询的 `playerId` 参数要求字符串，不要传玩家 table。

过滤器接受零个参数或一个 table，未知键报错：

| 查询 | 允许的筛选键 |
| --- | --- |
| `FindInfluences` | `influenceId, ownerPlayerId, owner, locationId, routeId, slotId, slotIds, limit` |
| `FindZones` | `id, templateId, localZoneId, parentId, owner, includeDescendants, limit` |
| `FindInstances` | `id, definitionId, typeId, contentType, zoneId, owner, controller, slotId, tag, includeDescendants, limit` |

`includeDescendants` 是布尔值，区域查询必须同时给 `parentId`，实例查询必须同时给 `zoneId`。`slotIds` 是至多 128 项的无重复字符串数组。其余身份过滤项为字符串，最长 1024 字符，`limit` 为 1～1024 整数；显式限量得到的是截断结果，不能据此断言全部候选已列完。

### 3.3 `SourceData`：当前来源权限

| 调用 | 专用筛选键 |
| --- | --- |
| `SourceData.GetCurrentSources(filter?)` | `definitionId, abilityId, itemId, activeFaceId, sourceKey` |
| `SourceData.GetGrantSources(filter?)` | 同上 |
| `SourceData.GetCallableActions(filter?)` | `itemId, sourceKey, actionId, callPolicyRef` |
| `SourceData.GetTransferableTokens(filter?)` | `itemId, typeId, zoneId, carrierItemId, localZoneId` |

四者共同接受 `cursor`（从 0 开始）、`expectedStateRevision`、`expectedContentHash`。每页至多 4 项，返回分页对象，含 `items, totalCount, nextCursor, protocolVersion, stateRevision, contentHash, authorizedPlayerId, ruleOwnerSourceKey, ruleOwnerItemId`；`nextCursor == -1` 表示结束。可调用行动页还含 `enabledCount/payableMarkerCount/transferableTokenCount`。

这些 API 只在宿主提供真实来源事实时可用；没有来源报 `source_facts_unavailable`，版本过期报 `source_facts_scope_stale`。不能通过手填 payload、来源键或授权字符串授予自己权限。

## 4. 常用 Effect 合同

下文 `?` 表示可选。所有构造器都只接收一个参数 table。除明确列出的接口外，不要一律添加 `apiVersion`、`completionHandlerId` 或 `presentation`，未知字段会被拒绝。

### 4.1 资源、分数与支付

| 构造器 | 新内容使用的参数与语义 |
| --- | --- |
| `Effect.GainResource` | `recipient, resourceType, amount, reasonId?`；amount 为 1～100 整数 |
| `Effect.GainScore` | `recipient, amount, reasonId?`；amount 为 1～100 整数 |
| `Effect.PayResource` | 单资源：`payer, resourceType, amount, allowSubstitution?`；amount 为 1～100。复合费用：`player, cost, allowSubstitution?`；足额支付，不等于失去资源 |
| `Effect.LoseResource` | 必填 `apiVersion=1, player, targetPlayer, resourceType, amount`；可带 `completionHandlerId`。amount 为 0～Int32.MaxValue；不足时只失去现有可用量，后续读实际结果 |
| `Effect.LoseScore` | `player, amount, minimumScore?`；amount 为正整数，省略 minimumScore 不自动保底 0；有底线时显式填写。不能使用负数 GainScore |
| `Effect.ChooseResources` | `player, amount`；走既有资源数量选择流程 |
| `Effect.Collect` | `apiVersion=1, player, scope='extra'`；当前仅支持该额外采集范围 |
| `Effect.SellResource` | 批量：`apiVersion=2, player, saleMode='batch', pricePolicy='core.resource_sale', interactionType?, promptKey?`；指定玩家全部某资源：`apiVersion=1, player, targetPlayer, resourceType, saleMode='all', unitPrice`，单价 1～99 |

复合 `cost` 由资源枚举名映射到非负整数，例如 `{ GoldVoucher = 2, Iron = 1 }`。总量费用另有 `apiVersion=2, player, resourceType='total', amount, allowedResourceTypes, cost?, allowSubstitution?` 合同，amount 为 0～99，allowedResourceTypes 是非空、无重复的枚举名数组，如 `{'Originium','Iron'}`；大小写必须与 `ResourceType` 一致，不能沿用普通 GainResource 的下划线拼写。cost 为额外固定费用。高级支付入口参见 [ResourcePaymentDemand.cs](../../Assets/YC/Domain/Effects/ResourcePaymentDemand.cs)。

支付换分数的完整脚本：

```lua
return function(ctx)
    return {
        Effect.Condition({
            leftEffects = {
                Effect.PayResource({ payer = ctx.playerId,
                    resourceType = 'gold_voucher', amount = 2 })
            },
            rightEffects = {
                Effect.GainScore({ recipient = ctx.playerId, amount = 1 })
            }
        })
    }
end
```

### 4.2 `Condition`

参数为 `leftEffects, rightEffects`，可加 `ruleCondition, allowDecline, decisionPlayer, declinePromptKey, apiVersion`。左右都是 Effect 数组。默认 `ruleCondition=true`；左侧依序结算并按条件合同判断后，才执行右侧。可选支付在确需玩家拒绝时声明 `allowDecline` 和对应决策玩家/文案。条件失败不会自动回滚已经提交的左侧效果。

不要用 `if PlayerData.GetResources(...).goldVoucher >= 2` 代替支付：预留、替代支付和执行期变化仍须由 Host 检查。

### 4.3 `Choice` 三种互斥写法

| 写法 | 参数 | 完成值 |
| --- | --- | --- |
| 候选选择 | `player, options={字符串ID...}, minSelections?, maxSelections?, promptKey?, interactionType?` | 单选字符串、多选数组，或可选选择的空值 |
| 分支执行 | `player, branches={{id=字符串, effects={...}}, ...}, promptKey?, interactionType?` | 只创建被选分支的子节点；1～16 个分支、ID 唯一、固定单选 |
| 数量选择 | `player, amountRange={minAmount=整数,maxAmount=整数,sourcePlayer?=玩家引用,resourceType?=资源名}` | 所选整数；可用资源约束由 Host 重算 |

共同可使用 `resultContext` 和 `completionHandlerId`。提供 `resultContext` 后，完成结果变为 `{ value=原完成值, context=原resultContext }`；没有该字段时返回原值。context 只能存普通可序列化数据，不能藏 Effect 或函数。

数量选择的 `sourcePlayer/resourceType` 必须成对出现；范围非负且 max 不小于 min。数量式不能再加 `options/branches/minSelections/maxSelections/promptKey/interactionType`。分支式不能再加 `options/amountRange/minSelections/maxSelections`。

普通执行选项使用 `interactionType='effect.choice.execute'`，语义为 1 选 1。例如把“两枚标记的合法组合”作为一个候选 ID；选择后仍逐枚校验真实实例与规则，不能把任意组合都放进去。玩家选择使用正式玩家候选（现有内容形如 `player:1`）；实际卡牌多选保留卡牌页面。自定义字符串的多选不得依赖待清理的无卡面多选页。

### 4.4 完成回调：可复制示例

JSON 的该能力必须声明 `completionHandlerId = 'diy.demo.strategy.done'`，脚本会在首次能力事件和完成事件中被调用。对应 JSON 在配套文档第 4 节。

```lua
return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        if ctx.payload.effectTypeId ~= 'effect.flow.choice' then return {} end
        local result = ctx.payload.normalizedResult
        if result.context.stage ~= 'resource' then
            error('diy_demo_choice_stage_invalid')
        end
        local resource = result.value
        if resource ~= 'originium' and resource ~= 'iron' then
            error('diy_demo_resource_invalid')
        end
        return { Effect.GainResource({ recipient = ctx.playerId,
            resourceType = resource, amount = 2 }) }
    end
    return {
        Effect.WithPresentation(
            Effect.Choice({ player = ctx.playerId,
                options = { 'originium', 'iron' },
                interactionType = 'effect.choice.execute',
                minSelections = 1, maxSelections = 1,
                resultContext = { stage = 'resource' } }),
            { interaction = {
                prompt = Text.Ref('core:diy.demo.choose_resource'),
                options = {
                    { candidateId = 'originium',
                      label = Text.Ref('core:diy.demo.choose_originium') },
                    { candidateId = 'iron',
                      label = Text.Ref('core:diy.demo.choose_iron') }
                }
            } })
    }
end
```

`completionHandlerId` 必须是装配时注册的处理器，不是 Lua 全局函数名。按 `eventType/effectTypeId/context.stage` 分流，并先判断 `outcome`。后续选择若需要再次续接，按入口合同显式设置已注册的 completion ID；不能凭名称调用一个未登记处理器。

`LoseResource` 完成结果包含实际 `amount`、`targetPlayer`、`resourceType` 等；转给自己时按实际 amount 创建 GainResource，amount 为 0 就不创建 GainResource。不能按原请求量发资源，也不能使用负数 GainResource。

## 5. 其他公开构造器速查

本节列出当前公开名称及主要参数，不将每个可选字段或兼容分支都当成推荐写法。**有来源、操作域、候选、层级摘要或阶段要求的接口必须在相应真实入口调用**。表中源码合同是高级接口的组成部分；在未理解其嵌套结构前，不按名称猜参数。所有表中 API 均保留 Host 规则校验，不提供状态注入能力。

### 5.1 城市、影响力、建设与主行动

| API | 参数与限制 |
| --- | --- |
| `Effect.MoveCity` | `player`；常用 `waiveBaseCost, consumeMainAction, targetPolicy, allowDecline`。定向移动还接受 `targetLocationId/targetLocation` 及 movement/cost/clear/source 策略；只使用 `CityMoveArgumentParser` 支持的组合 |
| `Effect.PlaceInfluence` | `executingPlayer, influenceSource, ownerSubject`，目标用 `targetSlotId` 或对应候选范围；可用 `targetLocationId, itemTypeId, subjectFilter, excludedSubjectKeys, causeKind, replaceEffectId` |
| `Effect.RemoveInfluence` | 严格批量写法：`apiVersion=2, executingPlayer, targets={实例ID...}, requiredCount, reasonId?`，requiredCount 1～16；不能混入 `targetInfluence/candidateScope`。旧单目标写法另有目标/范围合同，不能冒充严格批量 |
| `Effect.MoveInfluence` | `executingPlayer, targetInfluence, targetSlotId, causeKind?`，可筛选主体及排除主体 |
| `Effect.ReplaceInfluence` | `executingPlayer, targetInfluence, replacementSource, replacementOwner`，可加 `itemTypeId, subjectFilter, excludedSubjectKeys, placementFailurePolicy, causeKind` |
| `Effect.SelectInfluence` | `player, operation='remove'/'replace'/'move'`；`ownerFilter/subjectFilter, count/requiredCount, distinctPolicy, candidateScope/candidateIds, requireCompleteSelection, allowDecline, promptKey, nextPromptKey, interactionType` 按选择器合同配套，不等于直接提交 |
| `Effect.DepartureBatch` | `effects`；组织已确定的离场子效果，不能拿来绕过移除资格 |
| `Effect.StoreRemovedInfluence` | `apiVersion, player, cardInstanceId, localZoneId, influenceItemId`；仅在合法离场响应中暂存真实离场实例 |
| `Effect.PlaceRoad` | `apiVersion=1, player, routeId`；路线、道路库存及权限由 Host 验证 |
| `Effect.SetLocationsOpen` | `locations` 字符串数组、`isOpen` 布尔值、`reasonId?`；位置数最多 16 |
| `Effect.Build` | `player, facilityId/facilityInstanceId, cityBoardSlotIndex` 及 `paymentMode, reserveForFree, recipient, sourceId/sourceKind/sourceKey, expectedSourceZoneId, consumeMainAction`；使用真实建设候选 |
| `Effect.ChooseBuild` | `player`，可带 `sourceZone, sourceKey, expectedSourceZoneId, consumeMainAction, effectId, paymentMode, allowDecline, maxGoldVoucherCost, includeStoredFacilities, excludeRainbow` |
| `Effect.SelectFacilityPanel` | `apiVersion=1, player, allowDecline, operationProfile, emptyIsFailure, promptKey, itemIds?`；operationProfile 为 `city_top` 或 `effect.facility.retire` |
| `Effect.RetireFacility` | `apiVersion=1, player, facilityItemId, expectedCellZoneId, expectedOrderDigest`；以当前真实顶层、格子和顺序摘要提交 |
| `Effect.ActivateFacilityEntry` | `player, instanceId`；激活真实设施入场效果 |
| `Effect.Explore` / `Effect.ChooseExplore` | 进入既有探索执行/选择流程；不接受随意填写的地图奖励。详细字段以 `ExplorationEffectExecutor` / `ExplorationSelectionEffectExecutor` 为准 |
| `Effect.GrantMainActions` | `player, amount, lockCharacterCard?, allowedActionTypes?`；额度仍走主行动规则 |
| `Effect.ExecuteMainAction` | `player` 与既有主行动候选策略；实际执行由主链完成 |
| `Effect.OverrideNextStartPlayer` | `player`；覆盖下一起始玩家 |

影响力来源、目标、主体与候选范围都有类型和权限约束；特别是离场实例、普通玩家影响力、卡牌标记不能因为都表现为方块就混用。

### 5.2 物品与角色生命周期

| API | 参数与限制 |
| --- | --- |
| `Effect.MoveItem` | 当前统一转移入口；包含 `apiVersion, player, operationId, transfers, expectedOrders, itemRefs/itemIds, expectedSource, destination, insertion, operationProfileId, requiredCount, carrierPolicy` 等按版本互斥字段。不得把字段全集塞进一次调用 |
| `Effect.MoveFacilityCard` / `MoveCharacterCard` | 接入同一转移执行器，也保留有限旧 `sourceZone/destinationZone` 流程；新跨区规则按 `MoveItem` 的版本、区域与操作域合同写 |
| `Effect.ShuffleDeck` / `ShuffleFacilityDeck` | `apiVersion, player, deckZone`；随机化由 Host 执行 |
| `Effect.RefreshFacilitySupply` / `RefillFacilitySlot` | `apiVersion, player, slotZoneId`；供应更新/单槽补牌 |
| `Effect.CoverCharacterCard` | `apiVersion, player` 和真实 `cardInstanceId` 或合法 `candidateSource/count`；`coverMode, blockedActionRoundSequence, acceptedTypeId, reusable, recycleDiscardWhenEmpty` 属各自盖放合同 |
| `Effect.TransferCharacterCard` | `apiVersion=1, player, cardInstanceId, recipientPlayer` |
| `Effect.SetCharacterDoubleUseRule` | `apiVersion, player, scope, useId, sourceItemId, expiresActionRoundSequence`；仅合法角色使用窗口 |
| `Effect.ConsumeCharacterDoubleUseSource` | `apiVersion=1, player, useId, sourceItemId`；消费已证明的来源 |
| `Effect.RetainCharacter` | `apiVersion=1, player`；当前角色生命周期声明，不是任意 ID 移牌 |
| `Effect.CreateCityInfluenceLock` | `apiVersion=1, player, definitionId`；定义必须属于已登记组件与合法来源 |
| `Effect.OperatePlayerMarker` | `markerOwner, operation, sourceZone, destinationZone, count, targetSlot, executingPlayer, markerId` 按标记操作合同配套 |
| `Effect.OperateToken` | `apiVersion=1, player, tokenItemId, operation, destinationLocationId, operationProfileId` |
| `Effect.ReturnCityStyleMarker` | `apiVersion=1, player`；使用现有样式标记返还流程 |

`MoveItem` 嵌套结构、版本分支和权限以 [ItemTransferEffectExecutor](../../Assets/YC/Domain/Effects/ItemTransferEffectExecutor.cs) 及其调用的转移服务为准。卡牌携带物、数量、先后顺序、源区和目的区必须分别声明；成功移动物品不等于获得任意“收益”。当前可转移 Token 从 `SourceData.GetTransferableTokens` 取事实；不存在一个替代已退役装载 API 的无限造标记接口。

为迁移旧装载写法，新内容需要理解以下三个协议，而不是只改函数名：

| 版本 | 形状与用途 |
| --- | --- |
| v1 | 单组 `itemRefs={{itemId=真实ID}}, expectedSource={zoneId=源区}, destination={zoneId=目的区}, insertion, operationProfileId` 等；执行既有卡牌/区域转移合同 |
| v2 | `operationId, transfers={转移段...}, expectedOrders={顺序期望...}`；每段分别给 itemRefs、源/目标、插入与操作域，用于多段和有序区域，不与 v3 混用 |
| v3 | `itemIds={真实ID...}, expectedSource={zoneId=源区}, destination={zoneId=目的区}, insertion={kind='unordered'}, operationProfileId, requiredCount, carrierPolicy?`；Token/Cube 明确实例移动。requiredCount 为 1～99；carrierPolicy 如给只可 preserve |

v3 返还原供应区可将 destination 写为 `{kind='original_supply'}`。牌上方块常见操作域是 `core.cube_storage`，但仍须有真实牌区、匹配类型和本来源的精确载物权限；它不是所有内容通用的豁免授权。无序区域不能填 top/bottom。迁移固定数量装载时先通过合法选择确定数量，再重读供应实例，对每枚生成 `requiredCount=1` 的独立转移；某枚缺货应得到真实失败，不撤回已经成功的其他枚，也不虚构实例 ID。足额费用则按其真实支付语义处理，不能照搬逐枚尽量移动。

### 5.3 企业、奖励、来源行动

以下企业 API 需 `apiVersion=1, player`；玩家参数由宿主归一化，企业参数使用实例 ID。

| API | 其余参数 |
| --- | --- |
| `Effect.UpgradeEnterprise` | `enterpriseItemId` 或 `enterpriseItemIds`，`levels` |
| `Effect.ChangeEnterpriseLevel` | `enterpriseItemId, levelDelta, resolveArrivalReward` |
| `Effect.SwitchDepartment` | `enterpriseItemId, departmentItemId` |
| `Effect.ActivateEnterpriseSpecial` | `unlockedByPlayer, minLevel, maxLevel, enterpriseItemIds, specialId, candidateIds` 按目标选择方式取用 |
| `Flow.UpgradeDistinctEnterprises` | `count, levels` |
| `Flow.UpgradeThenActivateEnterpriseSpecial` | `levels, enterpriseItemIds` |
| `Flow.ActivateAllEnterpriseSpecials` | `unlockedByPlayer, minLevel, maxLevel, enterpriseItemIds` |
| `Flow.UpgradeEnterpriseForAllPlayers` | `enterpriseItemId` 或 `enterpriseItemIds`，`levels` |

下表为其他公开入口：

| API | 参数与限制 |
| --- | --- |
| `Effect.InvokeAction` | `apiVersion=1, player, itemId, actionId, callPolicyRef`；须有 JSON 调用策略和真实来源授权，不能直接点名任意行动免付成本 |
| `Effect.ScheduleCleanupObligation` | `apiVersion=1, player, handlerRef, dueRound, context`；context 是普通对象，dueRound 为正整数；需要本来源 `cleanup.defer:<handlerRef>` 授权 |
| `Effect.PaymentSubstitution` | `apiVersion, player, paymentOwnerEffectId, sourceZoneId, substitutionBudget, sourceKey, ruleId, paymentGeneration, draftRevision`；只在现有付款草稿与授权来源内有效 |
| `Effect.GrantReward` | `apiVersion=1, player, definitionId, typeId, count, recipientPlayer, destination, operationId`；count 1～16，需精确 `reward.grant` 权限及 `content_granted` 奖励定义 |
| `Flow.AcquireReward` | `apiVersion=1, player, poolRole='awards'/'contracts'`；从正式池获得 |
| `Flow.ClearRouteAndPlaceRoad` | `apiVersion=1, player, scoreRemovedPlayers, scoreExecutor`；分数为 0～99 |
| `Flow.DispatchOperator` | `apiVersion=1, player, drawCount, excludedReturnItemIds?`；真实抽牌与返牌流程 |
| `Flow.ReplaceHandWithOperator` | `apiVersion=1, player` |
| `Effect.RollDice` | `apiVersion=1, player, count=1, sides=6`；当前只支持单枚六面骰。多枚用多个有序 Effect；完成结果为 `{diceResult=1到6}`，不能脚本自选点数 |
| `Effect.RevealEventCard` | `player, eventDeck, placeResourcePointIndicator, resourcePoint, postResolveDestination, allowedOptions, eventColor, targetLocation/targetLocationId, primaryInfluenceSlot` 按事件入口合同配套 |

### 5.4 需绑定真实调用来源的事务

以下均要求 `apiVersion=1, player`，并由 `ContentTransactionEffectExecutor.BoundSource` 核对真实调用祖先、能力与来源。只注册名称或填写 `sourceItemId` 不会产生资格。

| API | 其余字段 |
| --- | --- |
| `Effect.RecycleCharacterCover` | `coveredItemIds`（至多 16 个、无重复），`sourceItemId` |
| `Effect.BindCharacterToFacility` | `characterItemId, facilityItemId` |
| `Effect.ReleaseFacilityBinding` | `bindingId` |
| `Effect.CreateMapToken` | `definitionId, sourceItemId, destinationLocationId, operationProfileId` |
| `Effect.MoveCharacterCardToZone` | `cardItemId, destinationZoneId, reveal?` |
| `Effect.StorePlayerMarker` / `ReturnPlayerMarker` | `markerItemId, cardItemId, localZoneId` |
| `Effect.ActivateCityStyleSpecial` | `specialActionId, declarationMarkerId, activationSource` |
| `Effect.UpgradeEnterpriseForPlayer` | `enterpriseItemId, targetPlayer, levels`（1～16） |

## 6. 文案与表现

`Text.Ref(key, arguments?)` 引用已登记文案；arguments 是具名表。实体参数用 `Text.Player(id)`、`Text.Item(id)`、`Text.Definition(id)`、`Text.Resource(id)`、`Text.Location(id)`。`Text.Player(1)` 会形成 `p1` 引用；不要把它当成可写玩家对象。

`Effect.WithPresentation(spec, presentation)` 为已有 Effect 添加表现信息，返回同一业务声明的副本。常用 `interaction.prompt` 和 `interaction.options`，见第 4.4 节；具体字段遵循 `EffectPresentationDefinition`。它不能改变合法候选、费用、效果结果或权限。

`Effect.WithContext(spec, context)` 为可恢复效果附加受校验的续接数据；特殊键 `requiredOutcome`、`executionBinding` 有封闭合同，不能自造任意结构。它不同于 `Choice.resultContext`，也不是全局可写状态。

静态界面标题、按钮名称、布局和样式继续以场景/预制体为准。JSON 文案用于已有内容展示合同，不允许 Lua 直接找 UI 对象改字、改布局或创建弹窗。

## 7. 专门入口：不要与普通效果返回混用

| 入口 | 返回合同与可用范围 |
| --- | --- |
| 普通规则处理器 | 连续 Effect 数组；第 2～6 节 |
| 候选集修正 | `Candidate.Add/Remove/Intersect({ ids={...}, reasonCode=... })` 的数组；仅宿主以 `candidatePatches` 合同调用时使用，不在普通 handler 中混入 Effect |
| 纯规则查询 | `{ queryId=..., basisDigest=..., items={...} }`，严格只有这三个字段；依据宿主给出的 queryId/digest 回传，items 结构由各 queryKind 决定。该入口未安装 Effect/Flow |
| 影响力附加改写 | `influencePlaceAttaching` 专门合同；只能按 `InfluencePlaceRewriteLua` 的字段修改允许部分 |
| 结算来源报价 | `Settlement.Offer(...)`；仅 `settlementOffers` 入口，按 `SettlementLuaHost` 的 OfferFields、窗口和来源资格返回 |
| 结算可用性 | `settlementAvailability` 专门返回；不能返回 Effect，不能用规则查询执行支付 |
| 段表现 | `Settlement.WithPresentation(spec, metadata)`；附加结算段信息，不能扩大插入窗口权限 |
| 纯显示脚本 | `Present` 入口及 `GameplayPresentationResult`，只使用展示上下文和 Text，不能执行规则 |

旧初稿中的事件总表不是自由订阅白名单。JSON 的 `abilities[].observedEvents` 与 `abilitySources[].observedEvents` 有不同验证范围，见配套文档。没有正式入口、快照或 queryKind 的能力属于引擎扩展需求，不能只靠增加 JSON 字符串完成。

## 8. 隔离开发检查顺序

1. 在独立内容工作目录编写，不覆盖正式 core、场景或预制体。固定基线提交、规则版本和所需能力。
2. 核对所有调用名属于本文；搜索被剔除 API、旧无卡面多选和插入按钮悬浮文字依赖。
3. 检查 JSON → 处理器 → Lua → 完成回调 → 文案的引用闭环，未知字段不能靠宽松 JSON 解析蒙混通过。
4. 在隔离装配包上验证解析、Lua 语法、Effect 归一化及领域执行，覆盖资源不足、空候选、拒绝、目标变化、来源失效、断点恢复和版本不一致。这属于隔离规则验证。
5. 正常界面验收从可见独立程序菜单、建房、开局和合法操作进入；不得注入牌、资源或强制刷新来代替。随机未出现记录为未覆盖，继续本局。
6. 按版本/内容哈希、GameId 和实际证据累计多局覆盖。夹具预期不同但仍能合法推进时记录首次差异并继续；崩溃、不同步、必答请求/权限丢失或无合法下一步则保现场中断受影响部分。

## 9. 源码索引与维护

| 合同 | 当前来源 |
| --- | --- |
| 构造器、白名单、归一化、只读查询 | [MoonSharpLuaRuntimeHost.cs](../../Assets/YC/Infrastructure/Lua/MoonSharpLuaRuntimeHost.cs)、[查询过滤](../../Assets/YC/Infrastructure/Lua/MoonSharpLuaRuntimeHost.Queries.cs) |
| 编译、权限与续接 | [LuaEffectSpecCompiler.cs](../../Assets/YC/Infrastructure/Lua/LuaEffectSpecCompiler.cs)、[LuaEffectEventDispatcher.cs](../../Assets/YC/Infrastructure/Lua/LuaEffectEventDispatcher.cs) |
| 沙箱预算与返回类型 | [LuaRuntimeContracts.cs](../../Assets/YC/Infrastructure/Lua/LuaRuntimeContracts.cs) |
| 当前来源分页 | [MoonSharpSourceFactsApi.cs](../../Assets/YC/Infrastructure/Lua/MoonSharpSourceFactsApi.cs) |
| Choice 与主行动 | [GenericLuaEffectExecutor.cs](../../Assets/YC/Domain/Effects/GenericLuaEffectExecutor.cs) |
| 资源损失 | [ResourceLossEffectExecutor.cs](../../Assets/YC/Domain/Effects/ResourceLossEffectExecutor.cs) |
| 内容原语/事务 | [ContentPrimitiveEffectExecutor.cs](../../Assets/YC/Domain/Effects/ContentPrimitiveEffectExecutor.cs)、[ContentTransactionEffectExecutor.cs](../../Assets/YC/Domain/Effects/ContentTransactionEffectExecutor.cs) |
| 企业/设施 | [EnterpriseEffectExecutor.cs](../../Assets/YC/Domain/Enterprises/EnterpriseEffectExecutor.cs)、[FacilitySelectionEffectExecutor.cs](../../Assets/YC/Domain/Effects/FacilitySelectionEffectExecutor.cs) |
| 文案/结算/纯查询 | [GameplayPresentationLuaHost.cs](../../Assets/YC/Infrastructure/Lua/GameplayPresentationLuaHost.cs)、[SettlementLuaHost.cs](../../Assets/YC/Infrastructure/Lua/SettlementLuaHost.cs)、[AbilityQueryLuaHost.cs](../../Assets/YC/Infrastructure/Lua/AbilityQueryLuaHost.cs) |

升级引擎时先对照构造器注册、字段白名单、领域参数验证和注册入口，再更新文档与示例。源码仍有残留不等于允许新内容使用；剔除表优先于从源码自动枚举出的名字。

本版检查记录：65 个未剔除的 Effect 构造器和 8 个 Flow 构造器均已列入文档；三个完整 Lua 示例通过当前 Domain/Application/Lua 源码编译出的隔离宿主归一化与 Effect 编译，选择完成/失败两条回调分别产生 1/0 个效果。此处使用独立 .NET 检查程序，没有启动 Unity、Player 或进行正常对局验收，也没有验证所有高级 API 的运行组合。
