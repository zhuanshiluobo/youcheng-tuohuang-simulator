# DIY 内容开发：Event 与处理器

本文说明 core `2.2.2` 对应实现中的事件入口、订阅、payload、完成续接及专门返回协议。它与 [Lua API](DIY内容开发-Lua%20API.md)、[JSON 编写](DIY内容开发-JSON编写.md) 配合使用。

这里的 Event 是 Host 发布的规则事实，不等于地图上翻开的“事件牌”。Lua 没有自由发布事件、运行时注册监听或订阅任意字符串的接口；在 JSON 登记处理器后，由 Host 在合法时点调用。

## 1. 从触发到完成

通常顺序为：业务效果执行到触发点 → Host 生成事件 → 筛选当前有效来源及处理器 → Lua 读取 ctx → 返回效果或对应专门协议 → Host 挂接响应 → 等待必要响应 → 原流程继续。

| 概念 | 作用 |
| --- | --- |
| `eventType` | 事件类别，如 `InfluenceRemoved`；决定匹配哪些已登记入口 |
| `eventId` | 本次具体事件身份；同类事件可以发生多次 |
| `sourceEffectId` | 产生事实的效果，不一定是当前来源响应包装 |
| `ownerNodeId` | 内核管理该事件响应依赖的节点 |
| `routeKey` | 已装配路由筛选键，如 abilityId；不是 Lua 任意填写的参数 |
| `responseKind` | 宿主内部约定该次调用接受什么返回值；Lua 不能改变它 |
| `definitionVersion/contentHash` | 绑定处理器和执行定义，供存档恢复及一致性校验 |
| `DispatchReceipt` | Host 保存的分发收据，防止同一事件／订阅重复挂接 |

这些是内核概念，**不是都直接出现在 ctx 顶层**。普通 Lua 顶层可读 `eventId/eventType/playerId/stateRevision/definitionVersion/payload/memory` 及资源快照字段，具体见 Lua API 文档。原始事件中的玩家可能为 `-1`（全局阶段）；不能据此构造对“玩家 -1”的业务。

`ResponseKind=None` 只保留事件事实，内核不分发响应。`OperatorDispatchPatches` 表示特派专门前置查询已收集的纯数量补丁，内核保留事件事实，不再分发普通效果响应。`Effects` 接受普通效果返回；候选补丁、纯查询、附着改写及展示属于单独装配的入口，不能混用。

## 2. JSON 里在哪里登记

### 2.1 角色 `abilities[]`

| 字段 | 允许值与作用 |
| --- | --- |
| `abilityId` | 非空、唯一能力 ID |
| `subscriptionId` | 非空、唯一初次调用订阅 ID |
| `script` | 内容包内已登记 Lua 文件路径 |
| `version` | 非空处理器版本 |
| `mode` | 角色的 `strategy` 或 `tactic`；角色定义要求各有一个 |
| `skillType` | `normal/persistent/one_shot`；observedEvents 非空时必须 persistent |
| `observedEvents` | 无重复事件名数组；仅允许本节白名单 |
| `observerSubjectPolicy` | 有订阅时必填 `self` 或 `departed_owner`；无订阅时为空。self 按本人来源／事件资格处理，departed_owner 按离场原主体相关资格处理 |
| `candidateKindFilter` | 观察 CandidateSetBuilding 时固定 `facility.build.card`；其他时候为空 |
| `eventResponseAttachments` | 事件名到 `RequiredChild`／`DeferredPlayerResponse` 的对象；事件必须也在 observedEvents 中 |
| `completionHandlerId` | 可选已登记完成续接 ID；装配时关联同脚本，不是 Lua 函数名 |
| `cleanupScript` | 可选包内脚本路径；在符合资格的玩家收尾时调用 |
| `hasSpecialZone` | persistent 必须明确布尔值；不是“是否保留本牌”的运行时 API |
| `specialZoneId` | hasSpecialZone=true 时引用本卡真实区域定义；否则为空 |

角色 observedEvents 白名单恰好为：

`InfluenceRemoved`、`InfluenceReplaced`、`CharacterCardCovered`、`CharacterEffectCompleted`、`CharacterUseFinished`、`CollectionStarted`、`OperatorDispatchStarting`、`CandidateSetBuilding`。

`CandidateSetBuilding` 要求 `observerSubjectPolicy='self'`，当前角色接的是设施建设候选，而不是任意候选页。`DeferredPlayerResponse` 只允许离场两事件且必须 departed_owner；默认是 RequiredChild。这些字段的完整包体位置和资源引用见 JSON 文档。

### 2.2 通用 `abilitySources[]` 与 `abilityHandlers[]`

能力来源负责“哪件物品在什么区域、由谁获得哪种能力”，处理器负责“这个能力调用哪个 Lua”。不要把两者混成一个仅按卡名触发的监听。

| abilitySources 字段 | 必填／默认与作用 |
| --- | --- |
| `protocolVersion` | 必填 `1` |
| `abilityId` | 必填唯一能力 ID |
| `definitionId` | 所属内容定义；可由装配补入，但若填写必须与所属定义相同 |
| `kind` | 必填 `event/cleanup/quick/special/ruleQuery/setup`；事件响应使用 event，查询使用 ruleQuery |
| `handlerRef` | 必填，同内容已登记处理器 ID |
| `declarationVersion` | 必填非空声明版本 |
| `authorizationPolicy` | 必填：`controller/holder/owner/character_covered/character_subscription/active_selection/public_declaration`，使用相应真实来源资格 |
| `effectiveZones` | 必填非空无重复区域模板 ID 数组；不是某次实例 zoneId |
| `activeFaceId` | 可选，非空时限制真实活动面 |
| `presentationRef` | 必填，引用所属定义、能力或已登记玩家详情能力的表现身份 |
| `observedEvents` | 默认空数组；事件白名单见下节 |
| `completedEffectTypes` | 默认空数组；观察 EffectCompleted 必须给非空无重复效果类型 ID，如 `effect.city.move` |
| `completionPolicy` | 默认 `succeeded`；`terminal` 也接正常失败终态。不是任意运行时状态过滤器 |
| `queryKinds` | 默认空数组；只声明已登记查询类别 |
| `allowedOperations` | 默认空数组；载体转移、奖励创建、计分、延迟义务等机制的精确字符串集合，按相应合同填写；不再用于行动调用许可或声明响应挂载类型 |
| `responseKinds` | 默认空数组；非空时仅用于 kind=event，取值为 `response.destination/response.self/response.optional_effect/response.batch/response.required_effect`，不可重复；声明离场响应调度方式，不是行动调用许可 |
| `actions` | 默认空数组；quick／special 等可调用行动定义，参见 JSON 文档；单纯 event 来源通常为空 |
| `repeatPolicy` | 默认 `once`；`unbounded` 只用于符合收尾可选来源合同的 cleanup，且 observedEvents 必须恰为 PlayerCleanupStarted |
| `cleanupInjectionPolicy` | 默认 `always`；`when_available` 只用于 cleanup 且 repeatPolicy=once |

`completedEffectTypes` 每项是非空、不超过 256 字符的内部公开效果类型身份，只允许字母、数字、点、下划线、连字符；不是 `Effect.Build` 这样的 Lua 构造器名。不能订阅来源包装自身制造无限完成响应。必须同时满足：观察 EffectCompleted ⇔ completedEffectTypes 非空；使用完成过滤时 kind 必须为 event。

| abilityHandlers 字段 | 取值与作用 |
| --- | --- |
| `handlerId` | 非空稳定 ID；来源通过 handlerRef 引用 |
| `script` | 内容包内正式加载的 Lua 路径 |
| `version` | 非空处理器版本 |
| `completionHandlerId` | 可选，登记该处理器的完成续接；仍指向已冻结代码 |

权限策略表示从哪里发现资格，不替代业务校验。例如 owner 与 controller 可能不是同一玩家；携带物权限还会校验载体、局部区域和物品类型，升级还会校验合作标记。

## 3. 通用来源的事件白名单与实际触发

通用 abilitySources 接受下表 16 个事件名。**允许声明与实际可响应是两层检查。** 只有存在正式生产者、当前来源有效且该事件允许响应时才调用。

| 事件名 | 时点／事实 | 入口和限制 |
| --- | --- | --- |
| `PlayerCleanupStarted` | 指定玩家的收尾开始 | event／cleanup 及角色 cleanupScript；来源与执行顺序按正式收尾窗口 |
| `RoundStarted` | 新大回合开始节点 | 通用来源事件 |
| `RoundEnded` | 大回合结束节点 | 通用来源事件；不自行清空使用记录 |
| `CharacterCardCovered` | 正常主链完成一位玩家盖牌 | 角色／通用来源；附加盖放不冒发此事件 |
| `CharacterCoverCompleted` | 全体正常盖牌任务收口 | 通用来源；不同于单人盖牌 |
| `CharacterEffectCompleted` | 单项角色能力结束的记录 | 当前生产者发为 `ResponseKind=None`，内核直接跳过分发；名称在白名单不代表可靠它获得 Lua 响应 |
| `CharacterUseFinished` | 整次角色使用结束 | 角色／通用来源；与一次策略／计谋完成不同 |
| `EffectCompleted` | 某个具体效果提交完成／正常失败事实 | 通用来源须 completedEffectTypes；完成处理器走精确续接绑定；见第 5 节 |
| `CollectionStarted` | 正常主链采集阶段开始 | 角色／通用来源；不是每次额外采集都重复发布的承诺 |
| `OperatorDispatchStarting` | 特派读取配置后、抽牌前；此时尚未移动任何候选牌 | 角色持续订阅、通用来源 event、DIY 模式／扩展 ruleBindings；专门返回 `{drawCountDelta=整数}`，无效果子节点或页面 |
| `CityMoveCompleted` | 城市移动及其前置移动子流程收口 | 通用来源；后续入场／事件响应仍是正式子树 |
| `InfluenceRemoved` | 真实影响力离场 | 角色／通用来源；必须按离场身份及协议处理 |
| `InfluenceReplaced` | 真实替换相关事实 | 角色／通用来源；规范离场协议避免对同一离场重复领取 |
| `FacilityPlaced` | 设施真实落位，具有建设事实 | 通用来源；选择根不是第二次落位，不能重复奖励 |
| `FinalScoringStarted` | 末轮终局计分流程开始 | 通用来源；不得用不受约束的监听重复改写终局结果 |
| `CandidateSetBuilding` | Host 正在计算正式候选 | 专门候选补丁入口，不返回普通 Effect，不等于“某页面已经打开” |

不要在 JSON 自由填 `InfluencePlaced`、`InfluenceMoved`、`ResourcePaid`、`ActionRoundStarted` 等名字就期待通用订阅生效。内核可能有此类事件，不代表当前外部来源白名单开放它们。所需时机没有公开入口时，需要先扩展引擎合同。

## 4. payload 的读取方式

事件牌选择入口以正式选择合同装配，`ctx.payload.choiceId` 为宿主确认的卡内稳定 ID；零选项立即入口使用空字符串。`cardId`、目标地点及玩家由宿主提供，脚本不应按 `optionIndex` 推断新选择的奖励。选择脚本与完成处理器分别采用各自登记的版本和源码身份，完成处理器的 payload 以完成事件合同为准。

### 4.1 通用来源附加的身份事实

通用来源处理器在原触发事实之外可能提供下列字段；只有 Host 实际提供时才能读取。

| 字段 | 类型／含义 |
| --- | --- |
| `sourceItemId` | 当前能力载体实例 ID |
| `rewardItemId` | 当前通用适配中的来源实例别名；不应仅据此判断来源一定是奖励卡 |
| `authorizedPlayerId` | 整数，当前来源获授权的玩家 |
| `abilityId` / `actionId` | 能力／当前行动 ID；无行动时 actionId 可为空 |
| `authorizationRef` | 本次真实授权引用，不能复制到其他调用取得资格 |
| `activeFaceId` | 当前生效面身份 |
| `grantingSourceRefs` | 授予该来源的来源键数组 |
| `arguments` | 该真实行动调用的参数对象 |
| `declarationMarkerCount` | 相关实际样式宣告事实数量 |
| `influenceSupply` | 授权玩家当前影响力供应数量 |
| `candidateIds` | 当前适配提供的实际候选，不是可任意改写候选池 |
| `triggerEventId` / `triggerEventType` | 原始触发事件身份／类型 |
| `triggeringPlayerId` | 原触发玩家整数，与 authorizedPlayerId 可能不同 |
| `triggerOwnerEffectId` / `triggerSourceEffectId` | 原触发所有者／产生事实的效果 |
| `callableActions` | 至多 4 项的小集合调用事实；每项 `optionId/itemId/actionId/enabled/disabledReason`；enabled 表示来源、次数和占用等当前资格，不代表已支付费用或有足够行动额度 |
| `callableActionsRequirePaging` | 布尔；true 时必须用 SourceData 分页，空 callableActions 不表示没有行动 |
| `cleanup` | 收尾时提供 `sourceItemId/invocationId/windowId/sequence` |

通用来源包装时 `ctx.playerId` 是获授权玩家，原事件玩家请读 triggeringPlayerId；例如其他玩家触发某件本人物品的响应时，二者不相同。身份事实是只读证据，不是业务 API 的通行证。

行动事实不包含 `callPolicyRef`。需要完整集合时使用 `SourceData.GetCallableActions`，只用 `itemId/sourceKey/actionId` 等当前过滤字段，按 `nextCursor` 分页至 -1。分页项还提供 sourceKey、轨道绑定和事实摘要等字段，见 [Lua API 第 2.3 节](DIY内容开发-Lua%20API.md#23-sourcedata)。实际调用由 `Effect.InvokeAction` 的 `omitEntryCost/omitEntryAfterEffects/omitMainAction` 控制是否省略入口费用、入口后置效果和主要行动消费，均默认 false；来源、次数和占用仍须复验，无需额外调用许可。

### 4.2 大回合及阶段 payload

`RoundStarted/RoundEnded/PlayerCleanupStarted/CollectionStarted/FinalScoringStarted` 的正常主链生产者提供：

| 字段 | 内容 |
| --- | --- |
| `roundExecutionId` | 本次回合主链执行身份 |
| `roundNumber` | 大回合整数 |
| `nodeId` | 真实阶段节点 ID |
| `playerId` | 阶段绑定玩家整数；全局节点可为 -1 |
| `playerOrder` | 冻结玩家引用数组 |
| `startPlayerId` | 本轮起始玩家稳定引用 |
| `markerIds` | 玩家收尾事件额外提供的样式标记事实数组；仍以当前业务复验为准 |

`CharacterCoverCompleted` 使用专门 payload：`coverNodeId`、`coveredPlayerIds`（玩家引用数组）、`roundExecutionId`、`roundNumber`。

### 4.3 角色相关 payload

| 事件／入口 | 字段和注意事项 |
| --- | --- |
| `CharacterEffectActivated` | `abilityId/cardInstanceRef/mode/effectOrder/useId`；正式角色初次能力入口，由角色装配自动登记，不写入 observedEvents 来重复注册 |
| `CharacterCardCovered` 公开记录 | `allPlayersCovered` 布尔、`cardInstanceRef` 隐藏引用、`coverIndex` 0 起顺序、`playerId` 整数 |
| `CharacterCardCovered` Lua 授权载荷 | 当前分派优先使用 Host 私有载荷，包含真实 `cardInstanceRef/coverIndex`；不能假定公开记录所有字段也合并进来 |
| `CharacterEffectCompleted` 记录 | `abilityId/cardInstanceRef` 及具体生产者附加结果；当前不分发响应 |
| `CharacterUseFinished` | `useId`，对应整次使用记录；不等于可再次使用该卡 |

普通 Lua 收到的稳定引用按宿主投影读取，不要强制假定所有引用都和原始 JSON 的字段格式一样。完成续接可用查询确认当前卡位置；已经离场的卡不能仅凭旧引用继续使用。

### 4.4 城市与设施

`CityMoveCompleted`：`playerId` 是移动玩家整数，`sourceLocationId/targetLocationId` 是起终点 ID。

此事件及 `BeforeCityMove` 属于正式城市移动流程。`MoveItem` 使用 `core.mobile_city_move` 直接转移城市物品时，两者都不会发布，也不执行城市入场正文；不能用订阅 `CityMoveCompleted` 的脚本推断所有城市位置变更。需要观察该物品转移的结果时，使用对应 `MoveItem` 的完成续接／收据。

`FacilityPlaced` 的实际建设事实，以及对应实际 Build 完成载荷附加的 `buildFacts`：

| 字段 | 含义 |
| --- | --- |
| `protocolVersion` | 当前为 1 |
| `buildId` | 实际建设效果 ID；不是外层选择根 |
| `itemId` / `definitionId` | 实际建成实例／定义 |
| `builderId` | 建设玩家整数 |
| `sourceZoneId` | 取牌前真实源区 |
| `slotIndex` | 城市格，0～11 |
| `previousTopItemId` | 先前顶牌 ID，原空格为空 |
| `overlay` | 是否叠建 |
| `overlayAuthorization` | 真实叠建来源事实；没有则为空 |
| `removalPrecondition` | 本次建设要求的移除前置事实，不是任意由 Lua 补填的免检字段 |
| `entryRequired` | 是否有必须完成的入场正文 |
| `entryEffectId` | 对应入场效果 ID，无正文时为空 |

通用完成观察会排除 Build 的 v3 select 外层、Explore 的 v3外层，避免把同一业务包装当作额外完成奖励。其精确完成回调仍按自身合同返回；不能把外层 UI 提交当作真实建设完成。

### 4.5 影响力离场与批次

离场载荷描述“哪次操作让哪枚真实物品离场，以及去向尚如何处理”。常用结果字段包括 `changed/operationKind/affectedItemIds/removedItemIds/replacedItemIds/originalSubjectKeys/positionChanges/originalPosition/destination/outcome/failureCode/influenceId/playerId/slotId`，以及 `causeKind/executingPlayerId/parentOperationEffectId`。位置对象用真实 zone／slot，不能仅按颜色猜所有者。

旧单次事实可另含：移除的 `removeEffectId/influenceRefBeforeRemoval/reasonId/fromSlot`，替换的 `replaceEffectId/placeEffectId/placedInfluenceRef/removedInfluenceRef/slot`。规范离场使用 `eventProtocolVersion=2` 与真实 `departureId` 等保管事实；应按本次协议读取，不把不同生产路径的可选字段全部当必填。

Host 自动为真实成功离场建立批次，并封存实际生产步骤。`Effect.DepartureBatch` 不是公开 API；把多个 Effect 放在同一数组也不等于同批。

事件响应返回的子效果挂到真实响应树／延迟响应根，保留事件因果和依赖关系，**不自动继承原离场批次作为新的生产步骤**。因此子效果再移除一枚时会按其自己的业务形成离场事实，不会无限塞回原批。

批次聚合响应提供 `batchId/departureCount`，小集合可含 `departureIds/sourceEventIds`；大型集合使用 `batchFactsProtocolVersion=2/batchFactsDigest` 的事实引用，不同时伪造完整数组。不要用数组缺失判定“没有离场”，也不要扩大普通 Lua 值预算来复制内部集合。

### 4.6 干员特派前置事件

`OperatorDispatchStarting` 是纯数据前置查询。每次特派读取配置后、抽牌前收集一次补丁，空牌堆也经过该查询；全部有效来源的补丁合并并冻结后，才抽牌或空堆得 1 分。它不使用普通 Effects 响应，不生成效果子节点、选择请求或完成续接。

| payload 字段 | 含义 |
| --- | --- |
| `dispatchEffectId` | 本次特派效果 ID，仅作为查询事实，不需要返回 |
| `playerId` | 接受特派的玩家整数 ID |
| `deckZoneId` | 指定干员牌堆的真实区域 ID |
| `baseDrawCount` | 从本局最终配方读取的基础数量；所有来源读取同一值 |
| `sourceItemId` | 仅角色持续／通用能力来源提供，当前响应来源物品 ID |
| `cardInstanceRef` | 仅角色持续来源提供，当前激活角色实例 ID |

返回 table 仅允许一个可选字段 `drawCountDelta`：整数 -99～99，正数增加、负数减少。`{}` 或 `{drawCountDelta=0}` 表示不修正。各补丁相加后统一校验最终数量 1～99，不允许 set 覆盖；单个中间和不作为最终合法性判据。非法数据使本次特派失败，不抽牌、不发空堆分。

```lua
return function(ctx)
    if ctx.eventType == 'OperatorDispatchStarting' then
        return { drawCountDelta = 1 }
    end
    return {} -- 同一入口用于其他事件时，按各自的返回合同处理
end
```

此入口不安装 Effect、Flow、Settlement，也不接受手写效果描述、候选补丁、effects 包装或 memory 写回。ctx.payload 只读；修改数量只通过返回 table。ctx.memory 可读取。已冻结的最终数量保存到本次特派节点，存档恢复不重跑已经完成的查询。

登记方式：

- 角色 `abilities[].observedEvents` 包含该事件，仍要求 persistent、`observerSubjectPolicy='self'`；只有本人当前激活的实例参与，离场后不再参与。不能为该事件填写 `eventResponseAttachments`。其他事件或主动发动可以继续使用同一 Lua，但须按事件区分返回合同。
- 通用 `abilitySources` 使用 `kind='event'` 并在 observedEvents 中登记，按当前生效区域和授权筛选来源，专门调用对应 handler 返回补丁。没有 `effect.ability.source.resolve` 响应树，也不消耗使用次数。ctx.playerId 是响应来源玩家，payload.playerId 才是接受特派的玩家。
- DIY 模式／扩展 `ruleBindings` 指定该事件，使用 `diy.rule_bindings.dispatch.v1`。不接受 candidateKind、completedEffectTypes、completionPolicy 或 completion。

```json
{
  "bindingId": "diy.example.dispatch_count",
  "eventType": "OperatorDispatchStarting",
  "script": "lua/dispatch_count.lua",
  "version": "1.0.0"
}
```

## 5. `EffectCompleted` 和完成续接

### 5.1 两种不同接法

| 方式 | 适用情况 | 绑定 |
| --- | --- | --- |
| 当前处理器的完成续接 | 当前效果完成后按结果继续，例如先选玩家再执行正文 | JSON completionHandlerId，及具体 API 支持的显式 completionHandlerId／上下文；只续接真实所属节点 |
| 通用来源完成观察 | 当前生效来源对某类真实操作完成作出反应 | abilitySources：kind=event、observedEvents 含 EffectCompleted、completedEffectTypes 精确过滤、completionPolicy |

不能把完成处理器名写成临时 Lua 全局函数，也不能用复制其他节点的 effectId 获得续接。完成绑定、源码哈希、作用域和处理器版本会进入存档；不匹配时拒绝恢复。

### 5.2 完成 payload

| 字段 | 类型／意义 |
| --- | --- |
| `effectId` | 刚产生完成事实的真实效果 ID |
| `effectTypeId` | 内核效果类型 ID，例如 `effect.flow.choice` |
| `outcome` | `completed` 或 `failed`，先判断该项 |
| `failureReason` | 失败原因字符串，成功通常为空 |
| `normalizedResult` | 该接口自己的结果；可以是字符串、整数、对象或空值 |
| `continuationContext` | WithContext 绑定的对象，仅真实续接存在时提供 |
| `interactionId` | Choice 完成且确有有效回答时补入，指向最后一个实际完成请求；旧回答不充当本次来源，空候选直接完成没有该字段 |
| `buildFacts` | 实际建设节点的建设事实；选择外层不重复补入 |
| `logicalOperation` | 有正式逻辑放置映射时的投影；不能自造该对象冒充原放置 |
| `allocationResult` | 仅 AllocateEffects 的真实绑定完成续接可读的完整只读队列视图；normalizedResult 只含摘要，详细字段见 Lua API |

外层 `outcome='completed'` 不等于“业务产生变化”。还要读取接口结果，例如 `changed=false`、建设 `outcome!='built'`、即时主行动 `executed=false`、条件 `condition_not_met`、次数分配 `outcome='stopped_on_failure'` 都不能按成功发奖。实际需要事务条件时使用 Condition／足量合同，不靠回调事件名推断。

### 5.3 一个完整的纯选择续接

以下是角色能力 JSON 片段，省略卡牌其他必需字段；两个 ID 和脚本路径需在包内唯一：

```json
{
  "abilityId": "diy.example.strategy",
  "subscriptionId": "diy.example.strategy.entry",
  "version": "1.0.0",
  "script": "lua/example.lua",
  "completionHandlerId": "diy.example.strategy.complete",
  "mode": "strategy",
  "skillType": "normal"
}
```

同一个 `lua/example.lua`：

```lua
return function(ctx)
    local p = ctx.payload
    if ctx.eventType == 'CharacterEffectActivated' then
        return {
            Effect.Choice({
                player = ctx.playerId,
                amountRange = {minAmount=0,maxAmount=3},
                resultContext = {stage='gain_score'},
                completionHandlerId='diy.example.strategy.complete'
            })
        }
    end
    if ctx.eventType == 'EffectCompleted' then
        if p.effectTypeId ~= 'effect.flow.choice' or p.outcome ~= 'completed' then
            return {}
        end
        local r = p.normalizedResult
        if r == nil or r.context == nil or r.context.stage ~= 'gain_score' then
            return {}
        end
        if r.value > 0 then
            return {Effect.GainScore({recipient=ctx.playerId,amount=r.value})}
        end
    end
    return {}
end
```

此例展示返回协议，不代表任何卡牌天然有免费得分能力。实际卡面约束由自己的初次入口实现。对 GainScore 完成的回调会被 effectTypeId 分流挡住，避免重复发奖。

### 5.4 memory 与事件

memory 按同一 Lua 入口保存，不按事件类型、卡面能力或玩家自动分开。事件 A、B 使用同一已登记脚本入口可以读写同一份数据；需要分玩家、分阶段、按 useId 去重时由作者在 table 中分层。

只有普通可写调用显式返回 `{effects={...},memory=...}` 才覆盖。查询／候选／展示入口不用于写 memory；失败调用不提交，后续业务失败也不自动撤回此前已经提交的 memory。内核分发去重和脚本业务去重是不同职责，不能用“我记过 eventId”跳过真实付款／库存验证。

## 6. 必做子响应、延迟响应和来源失效

`RequiredChild` 返回的响应挂为原节点必要子树，原流程等它收口。`DeferredPlayerResponse` 用于正式离场目的地协议：保管真实离场物，登记谁可以回答、哪个处理器决定去向，其他依赖在需要该物品时等待。它不是允许任意异步发奖的通用开关。

通用来源的 `response.*` 权限由精确来源声明控制；不能任意为新事件加同名前缀。当前带此类权限的离场来源仅允许 InfluenceRemoved／InfluenceReplaced，并处理规范 v2 去重：已由真实移除离场路由处理的替换，不再按替换事件重复领取同一响应。

载体失效、被转移、活动面改变、授权玩家改变时，后续发现及接受来源都重新检查当前状态。已经正式成立的 `ScheduleCleanupObligation` 另有不可依赖未来来源继续存在的义务收据；不能把普通持续监听和既成义务混为一谈。

## 7. 候选集入口

`CandidateSetBuilding` payload：

| 字段 | 含义 |
| --- | --- |
| `candidateSetId` | 该次正式候选集 ID |
| `candidateKind` | 正式候选类别 |
| `sourceEffectId` | 拥有该候选计算的实际业务效果 |
| `executingPlayerId` | 真正执行业务的玩家整数 |
| `candidatePoolIds` | 该协议授权可用的完整候选池 |
| `defaultCandidateIds` | 基础规则候选 |
| `currentIds` | 此次补丁调用前的当前候选 |
| `context` | 对应查询用途的事实，例如目标政策、起点、版本、地点映射 |
| `cardInstanceRef` | 持续角色候选调用附加的真实载体身份；并非所有查询都有 |

返回连续补丁数组：

```lua
return {Candidate.Remove({ids=idsToExclude,reasonCode='diy.rule.excluded'})}
```

`ids` 只填真实池中候选，不能用文案或定义 ID 冒充候选；`reasonCode` 给诊断原因。`Add/Remove/Intersect` 分别增加、移除、取交集，仍受协议规则约束。硬禁入不被后续普通 Add 复活。

通用 `queryKinds` 中的 `city_destination.v1` 是一个特殊路由：实际以 CandidateSetBuilding 的候选补丁协议调用来源，只允许 `Candidate.Remove` 作硬排除，**不返回 `{queryId,basisDigest,items}`**。它与一般设施候选修正共享候选语法，但权能不同。

DIY 模式／扩展也可通过 `ruleBindings` 订阅 `CandidateSetBuilding`，当前 `candidateKind` 只开放 `city_destination` 和 `facility.build.card`，采用普通候选补丁合同。此绑定与上述 `queryKinds` 硬排除路由不同，登记字段见 [包体作者指南](DIY包体作者指南.md#规则绑定)。两种入口均为只读，不挂接 Effect 或写入 memory。

城市目的地 payload 中，`context.startLocationId` 是出发地点，`context.destinations` 每项含 `candidateId/locationId` 映射；应向 `Candidate.*.ids` 回传其中的 **candidateId**，不是直接填 locationId。当前 `MoveCity` 传入的池仅含相邻地点；向补丁添加池外地点会拒绝整个候选查询，不能借此实现非相邻移动。普通补丁完成后还会应用硬排除、目标政策及正式移动校验。

## 8. 纯查询和附着改写

### 8.1 纯查询公共字段

普通 `abilityRuleQuery` 载荷在来源身份之外提供 `queryId/queryKind/basisDigest/facts/purpose/subjectPlayerId`。queryId 是当前查询 ID，basisDigest 绑定当前事实，facts 是对应类别的只读输入；subjectPlayerId 是被查询对象，可能与当前来源授权玩家不同。

返回严格三字段：

```lua
return {
    queryId=ctx.payload.queryId,
    basisDigest=ctx.payload.basisDigest,
    items={}
}
```

`items` 必须是有限数组；空表表示没有贡献。Effect／Flow 不在此入口安装，不能发业务或弹窗。声明了 queryKind 但消费方没有登记合同会报 `ability_query_kind_not_connected`，不应按“空结果”处理。

### 8.2 当前查询类别

| queryKind | facts／返回项 | 边界 |
| --- | --- | --- |
| `collection_free_locations.v1` | facts：`purpose` 为 `normal/extra`；`sourceEffectId/selectionSessionId` 为本次来源／会话；`startLocationId` 为城市地点，城市离图时为 `""`；`legalLocationIds` 为原合法采集地点数组。items 每项 `{kind='free_location',locationId=真实地点}` | 只与原合法采集集合取交集，免路费不授予额外采集目标；无城市时仍调用此查询，只允许采集交集中的免费点 |
| `built_facilities.v1` | facts：`player/baseItemIds/allCityItemIds/stateRevision`；items 为真实设施实例 ID 数组 | 必须确在该玩家城市，不能计入其他玩家／库存设施 |
| `facility.build.sources.v1` | facts 提供 `requestedSource/candidatePoolIds` 等当前来源请求；items 为允许建设的真实实例 ID | 限于当前来源的正式候选池，不能凭返回任意 ID 扩大建设权限 |
| `facility.build.slot.v1` | facts：`player/newItemId/definitionId/newScore/cells`；每格含 `slotIndex/cellZoneId/topItemId/topScore/orderDigest`；返回叠建项见下文 | Host 复验层序、旧顶牌、叠建授权和必做响应 |
| `scoringModifiers.v1` | facts 提供当前 pass、区域或玩家等计分事实；items 见 8.3 | 只贡献当前查询阶段，不直接加分 |
| `resource_payment_substitution.v1` | facts 提供当前付款玩家、用途、buildIdentity、grossCost；items 为 8.4 的替代规则 | 配置合法资源替代，不表示已付款 |
| `resource_payment_demand.v1` | facts 提供当前真实行动身份及费用；items 为正式付款需求对象，如 PayResource v2 总量字段 | 只描述该行动费用，不返回 Effect.PayResource 构造结果；冲突／越权需求拒绝 |
| `city_destination.v1` | 特殊候选 Remove 协议，见第 7 节 | 不是普通三字段返回 |
| `influence_place_attaching.v1` | 专门 keep／replace 协议，见 8.5 | 不是普通三字段返回 |
| `payment_substitution.v1` | 仍列于来源声明允许名称中 | 当前正式资源替代使用 resource_payment_substitution.v1；不要仅凭该旧名在白名单就按新协议编写，未接通消费方会拒绝 |

免费采集按当前起点及查询来源计算：城市所在地点若本来合法，默认免费；城市离图时没有这项默认贡献，但来源仍可为其他合法点提供免费资格。无城市的采集不接受需走路线的目标或已确认航道，可确认空采集。脚本应容许 `facts.startLocationId == ''`，不要把空起点当成整个查询无效；探索不采用该例外。

叠建返回项：`slotIndex` 为 0～11；`expectedTopItemId` 为本次 cells 对应真实旧顶牌；`orderDigest` 原样回传；`removalPrecondition={operation='remove',ownerFilter='self',requiredCount=1}` 声明实际必须完成的移除；`requiredResponseAbilityId` 为已登记的必做响应能力。它们描述候选资格及后续义务，不是立即移除／立即建设。

### 8.3 计分返回项

所有 kind 必须与当前 `purpose`／查询 pass 对应；regionId 必须为正在查询的区域，playerId 必须符合来源授权及该次对象。单一来源不能返回重复同类目标项。

| kind | 每个字段 |
| --- | --- |
| `influence_delta` | `regionId` 真实当前区域；`playerId` 授权玩家整数；`delta` 整数，影响力计数贡献 |
| `tie_claim` | `regionId/playerId` 同上；`ruleId` 合法稳定规则 ID，提供平局主张 |
| `region_value_delta` | `regionId`；`delta` 整数；`reasonRef='region'` 或 `claim:<ruleId>`，后者须对应本来源真实控制主张 |
| `resource_profile` | `playerId`；`profileId` 合法稳定方案 ID；`entries` 数组，每项 `resourceType` 为 PascalCase 资源枚举、`units` 正整数、`points` 正整数；资源不重复 |

resource_profile 的空 entries 是空方案，不等于字段缺失；同一玩家多个冲突方案不能随意叠加。所有返回仍受有限值预算限制。

### 8.4 资源替代规则返回项

| 字段 | 允许值和作用 |
| --- | --- |
| `ruleId` | 非空规则 ID，同一来源中稳定 |
| `purposeKind` | `payment/build/pay/condition/city_move`，限制适用付款用途；payment 为一般付款用途 |
| `allowedResourceTypes` | 非空无重复 PascalCase 资源枚举数组，允许抵扣的资源 |
| `maxUnits` | 1～99，本来源最多替代单位 |
| `quotaGroupId` | 非空配额组 ID |
| `quotaLimit` | 1～99，配额组上限 |
| `quotaScope` | `payment` 同笔付款，或 `build` 同次真实建设 |
| `unitResourceCost` | PascalCase 资源键到非负整数的费用表，每替代一单位实际要支付的资源，允许空对象 |
| `itemCost` | 可选，按单位消费真实物品，结构见下段 |
| `insertion` | 可选，正式插入入口 `{apiVersion=1,maxUses=1,presentation=结算表现对象}` |

itemCost：`localZoneId` 为真实来源载体内区域；`countPerUnit` 为 1～99；`destinationKind` 为 `original_supply` 或 `retired`；`operationProfileId` 可选合法 Token profile，默认 `core.cube_storage`；`selectionPolicy` 为 `equivalent`（默认，事实完全等价时自动定实例）或 `explicit`（需要明确选实例）。载体由真实来源绑定，不接受任意 cardInstanceId 来消费别人的标记。

报价、分配草稿、选择消耗实例、最终付款是不同阶段。只有正式提交才扣资源／移动消耗物，配额与 current draft、付款代数一起复验。

### 8.5 放置附着改写

声明 `queryKinds=['influence_place_attaching.v1']` 的真实来源，由 Host 以 `InfluencePlaceAttaching`、`responseKind='influencePlaceAttaching'` 调用，payload 的 facts 描述原始放置。合法返回只有：

```lua
return {operation='keep'}
```

或：

```lua
return {
    operation='replace',
    effect=oneRealEffect,
    resultContract='place_result',
    permissionContract='inherit_original'
}
```

`keep` 不能带其他字段；`replace` 必须恰好一个真实效果根，不能返回任意数组或旁路副作用。resultContract 要求保留放置结果合同，permissionContract 继承原操作权限；不能扩大目标或授权。此处可以构造合法替代效果，但不把普通 ruleQuery 入口变成通用业务入口。

## 9. 事件牌与自动装配入口

事件牌是内容对象，Host 在 `RevealEventCard`／探索等流程中翻开并处理。事件牌选项页由真实卡牌定义和其选项生成，Lua 不通过伪造 RuleEvent 打开它。

角色初次激活、设施入场、企业到达／特效、城市样式特殊行动等都有各自装配入口，不能把它们的内部事件名复制进 observedEvents 当作万能监听。入口 payload 只保证该类型正式提供的字段；例如设施入场的 `facilityId` 与城市建设事实中的 `itemId`／`definitionId` 必须按各自语义区分。

是否显示页面取决于返回效果，而不是“发生了 Event”。纯资源收益可以直接简要结算，纯 Choice 只收集答案，逻辑 Choice 执行真实分支，移动不自行选择。逐 API 页面顺序见 Lua API 文档。

## 10. 隔离开发检查

1. 先确定触发入口和返回协议，核对白名单、实际生产时机及 responseKind；不凭事件名相似就订阅。
2. 明确事件发起者、来源获授权玩家、受益人、物品原所有者和控制者，必要时分别读取。
3. 完成续接按 eventType、effectTypeId、stage 和 outcome 分流，只按实际结果发收益。
4. 显式注册脚本／处理器／来源／文案，测试源失效、无目标、失败、重复回答和存档版本不符。
5. 保存本次真实失败现场；隔离规则通过不计作正常页面、多人联机或完整卡面验收。
