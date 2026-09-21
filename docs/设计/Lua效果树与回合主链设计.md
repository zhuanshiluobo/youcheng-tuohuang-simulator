# Lua 效果树与回合主链设计

> 2026-09-08 · 第一版架构设计稿，尚未实现。
>
> 本文整理用户确认的 Lua 效果、Effect 树、通用阻塞、有限效果轮和回合主链思路，并结合当前代码给出第一阶段迁移方案。本文描述目标架构，不代表当前程序已经具备这些能力。
>
> 三类可调用内容的逐项接口、内核处理、UI 阻塞点和第一批 Event 在[《Lua 效果系统 API 初稿》](Lua效果系统API初稿.md)中继续维护。
>
> [《命令与效果系统准备规范》](命令与效果系统准备规范.md)已经废弃，仅保留为历史讨论记录。发生冲突时始终以本文和《Lua 效果系统 API 初稿》为准。

## 1. 目标与边界

目标是在 Host 权威前提下，将规则内容与可信执行内核分开：

- C# 保留规则上的最小 Effect 实现、规则校验、状态提交、UI 交互协议和流程控制。
- 角色牌、企业、企业家牌、城市样式、设施牌入场效果等卡面内容由 Lua 编写和组合。
- Lua 不直接修改 `GameState`，只能请求已注册的 C# Effect、固有效果链或最小交互。
- Lua 不保存协程、调用栈和局部运行状态；已经发生的玩家输入、随机结果与 Effect 提交结果进入 Effect 链。
- Effect 树的每一层都有完成状态。阻塞型子 Effect 默认阻止父 Effect 完成。
- 允许通过通用阻塞选项表达跨父子关系的等待，但必须检测循环依赖和无进展状态。
- 回合至少先迁移为树状执行结构。每回合由 C# 创建并广播“第 N 回合开始”事件，同时创建固定回合主链。

本稿不要求立即把当前全部规则迁移到 Lua。第一阶段优先建立回合主链、节点完成状态、玩家窗口和 Effect 挂载位置，再逐步接入 Lua 内容。

## 2. 当前代码基线

当前回合机不是树，而是扁平状态机：

- `GameState` 使用 `Round`、`Phase`、`ActionRound`、`StartPlayerId`、`CurrentPlayerId` 表示当前位置。
- 玩家行动完成情况分散在 `ActedMainActionThisTurn`、`RemainingMainActionsThisTurn`、`HasCollectedResourcesThisRound` 等字段。
- `RoundAdvanceService` 根据这些字段直接切换阶段和当前玩家。
- 角色牌盖放与两轮行动采用固定玩家顺序。
- 采集不检查 `CurrentPlayerId`，依靠每名玩家的完成标记形成全员完成屏障。
- 收尾由起始玩家推动，遍历平面的 `DelayedCharacterEffects`，没有按玩家建立收尾窗口。
- `GamePhase.RoundStart` 已定义，但正常回合推进没有进入该阶段；收尾后直接进入 `CharacterCover`。
- 当前只有 `PendingChoice`、`PendingCardSession`、`PendingCharacterEffect`、`PendingSpecialAction` 等专用待选状态，没有通用 Effect 父子结构。

相关代码：

- `Assets/YC/Domain/State/GameState.cs`
- `Assets/YC/Domain/Rules/GameEnums.cs`
- `Assets/YC/Domain/Rules/RoundAdvanceService.cs`
- `Assets/YC/Domain/Rules/TurnOrderService.cs`
- `Assets/YC/Domain/Cards/CharacterCardService.cs`
- `Assets/YC/Domain/Harvest/ResourceCollectionService.cs`

现有 `Phase`、`CurrentPlayerId` 和玩家完成标记在迁移期可以保留为兼容投影，但不应继续作为新回合树的唯一事实源。

## 3. 三类可调用内容

### 3.1 卡面最小 Effect

卡面最小 Effect 是由 C# 实现、可被 Lua 和固有效果链调用的最小可信规则变化。例如：

- 获得、失去、支付资源；
- 获得或失去分数；
- 放置、移除、移动、替换影响力；
- 改变移动城市停靠位置；
- 抽取、翻开、弃置、回收或移除卡牌；
- 放置资源点、公路或设施；
- 改变行动预算、标记位置或规则限制状态。

每个最小 Effect 必须：

1. 使用稳定 `effectTypeId`。
2. 对玩家、来源、目标和参数进行 C# 侧校验。
3. 不允许 Lua 取得 `GameState` 的可写引用。
4. 明确是完整提交、没有提交还是进入故障；不能留下无法识别的半提交状态。
5. 成功后产生可记录的 Effect 结果；需要触发其他卡面时，再发布明确的 `RuleEvent`。

“卡面最小”表示其可以构成卡面规则，不表示它只能被卡牌使用。基础行动和系统流程也可以复用同一最小 Effect。

### 3.2 最小 UI 交互

Lua 不直接创建 Unity 弹窗、`InteractionRequest` 或操作场景对象。Lua 返回需要目标、数量、排序或确认的 `EffectSpec`，对应的 C# Effect 执行器根据已注册合同创建结构化 `InteractionRequest`。最小交互至少包括：

- 单选；
- 多选；
- 数量选择；
- 目标选择；
- 卡牌选择；
- 多个 Effect 排序；
- 是否发动或明确放弃；
- 显式结束当前玩家效果窗口。

交互请求包含稳定请求 ID、所属 Effect、应答玩家、可见范围、数量约束和 C# 复验规则。UI 只展示请求并提交答案；关闭弹窗不能视为 Effect 完成。

玩家答案属于外部输入，必须进入 Effect 链。Lua 的临时局部变量不进入 Effect 链。

### 3.3 固有效果链

固有效果链表示规则书中反复使用、结构稳定的流程，例如：

- 玩家主要行动窗口；
- 角色牌盖放轮；
- 探索；
- 建设；
- 移动城市行动；
- 采集；
- 按玩家顺序结算收尾效果；
- 回合开始和回合结束。

C# 负责固有效果链的创建、调度、阻塞和完成判定。链中实际的规则变化仍通过最小 Effect 提交；卡面追加和改写的内容由 Lua 生成。

固有效果链的模板最终采用 C#、Lua 还是结构化数据，可以按流程稳定性分别决定。本稿先确认：回合主链由 C# 创建，所有卡面效果由 Lua 创建。

## 4. Lua 的运行模型

Lua 是无持久运行状态的 Effect 生成器：

```text
C# 接收玩家输入或完成规则结算
  → 校验输入，并形成具有稳定 ID 的 RuleEvent
  → EventDispatcher 根据 Event 订阅索引调用 Lua 处理函数
  → Lua 返回连续有序的 EffectSpec[]
  → C# 整体校验并将每个 EffectSpec 编译为当前结算节点的子节点
  → Lua 调用结束，局部运行状态丢弃
```

Lua 不直接返回独立的交互请求。需要玩家输入的卡面最小 Effect 或固有效果链由各自的 C# 执行器创建 `InteractionRequest`；固有效果链在 Lua 侧同样以一个 `EffectSpec` 表达。

### 4.1 Event 是 Lua 的唯一规则入口

玩家输入不能直接调用卡面 Lua。C# 必须先校验和记录输入，将它转换成明确的 `RuleEvent`，再由 Event 分发器调用 Lua。最小 Effect 的结算完成、UI 交互的回答、回合节点的进入和完成，也都以 `RuleEvent` 作为后续 Lua 规则的入口。

这意味着 Lua 处理函数只接收完整 Event 上下文，不依赖调用方的临时 C# 对象或 UI 状态：

```text
RuleEvent
├─ eventId
├─ eventType
├─ sourceEffectId
├─ routeKey
├─ targetEntityId
├─ playerId
├─ payload
├─ sequence
└─ definitionVersion
```

`eventId` 用于幂等去重，`sourceEffectId` 建立 Event 与 Effect 链的因果关系。`routeKey` 是用于查找 Lua 处理器的稳定内容定义 ID，例如某个企业特效的定义 ID；`targetEntityId` 是本局中被操作的企业、卡牌或玩家实例，两者不能混用。不是每个 Event 都必须写成一份独立存档；只要可以从权威输入和 Effect 链确定性重建，就可以作为派生数据。但 Event 的稳定 ID、分发结果和由它生成的子 Effect 必须能在链中对应起来，避免恢复时重复生成。

### 4.2 定向分发，不让所有企业脚本自行筛选

企业特效 Event 不应广播给所有企业 Lua，再让每个脚本判断“是不是自己”。这种方式会把路由规则散落到卡面脚本中，也难以检查重复处理和扩展冲突。

同时，也不应维护一个写死所有企业 ID 的中央 Lua 分流函数。否则加入扩展企业时必须修改或覆写这个函数，核心包和扩展包会互相争夺分流权。

C# 在载入基础内容和扩展内容时，根据 Lua 模块的注册信息建立订阅索引：

```text
LuaEventSubscription
├─ eventType
├─ routeKey 或显式 wildcard
├─ handlerModule
├─ handlerFunction
├─ role = Primary | Observer
└─ priority
```

典型索引键为：

```text
(eventType, routeKey) → 一个 Primary 处理器
```

例如：

```text
(EnterpriseSpecialEffectActivated, enterprise.acme.special.effect_1)
  → enterprise.acme.onSpecialEffect1Activated
```

Event 分为两种分发语义：

- **定向 Event**：带 `routeKey`，例如已选中的企业特效；分发器只调用该精确键的 `Primary` 处理器。
- **开放 Event**：例如 `RoundStarted` 或 `ResourceCollected`；分发器只扇出给显式注册了该 `eventType` 的 `Observer`，而不是调用所有内容脚本。

确实需要监听“任意企业特效已完成”的卡牌，可以显式注册 `Observer` 通配订阅；通配订阅用于规则反应，不能代替目标企业的主要处理器。多个观察者产生的效果仍必须交给 Effect 链按明确的顺序策略或有限效果轮调度，不能依赖 Lua 模块加载顺序。

扩展包只需注册新的稳定企业特效定义 ID 和处理器，分发器本身不变。若扩展要替换既有企业的实现，应通过内容目录中显式的替换或补丁关系完成，而不是覆写分流函数；加载完成后，同一精确索引键只能有一个有效 `Primary` 处理器，冲突必须在开局前报错。

### 4.3 企业特效的选择与触发

“请求发动企业特效”和“目标企业的特效正式触发”不是同一个 Event。推荐流程是：

```text
上游 Effect 或合法玩家命令请求发动企业特效
  → C# 校验请求是否可以进入选择流程
  → 创建最小 UI 交互：选择企业或具体特效
  → 玩家回答被校验并记录
  → C# 产生定向 EnterpriseSpecialEffectActivated Event
  → EventDispatcher 按 specialEffectDefinitionId 形成 routeKey 并查找 Lua 处理器
  → 只调用选中企业的 Lua
  → Lua 返回 EffectSpec[]；需要交互的子 Effect 由其 C# 执行器创建 InteractionRequest
  → 子树完成后，C# 产生 EnterpriseSpecialEffectCompleted Event
```

如果原始请求已经带有合法且唯一的企业特效定义 ID，可以跳过 UI 选择，但仍必须产生同一种定向 `EnterpriseSpecialEffectActivated` Event。这样 Lua 不需要知道目标来自按钮直选、弹窗选择、AI 决策还是断线恢复。交互完成时还可以形成通用的 `InteractionAnswered` Event 供其父 Effect 继续，但它不得广播给所有企业脚本；真正进入企业卡面逻辑的入口仍是上述定向 Event。

`EnterpriseSpecialEffectCompleted` 可以开放给多个精确订阅或 `Observer` 通配订阅，用来实现“任意企业发动特效后……”之类的反应。它只能在该企业特效对应的 Effect 子树真正完成后发布，不能在 UI 选择结束时提前发布。

### 4.4 无持久 Lua 状态

示例：

```text
CharacterEffectActivated Event
  → Lua 请求支付资源
ResourcePaid Event
  → Lua 返回替换影响力 EffectSpec
  → C# 替换节点创建目标选择 InteractionRequest
InteractionAnswered Event
  → C# 复验答案并继续替换节点
InfluenceRemoved Event
  → 结算“影响力被移除时”的响应
InfluencePlaced Event
  → 结算“影响力被放置时”的响应
InfluenceReplaced Event
  → 角色牌效果链继续完成
```

一次成功的替换会依次形成移除、放置、替换三类独立规则事实。三个 Event 各有自己的稳定 Event ID，并共享关联字段 `replaceEffectId`；它们不能合并成一个 Event。订阅 `InfluenceRemoved` 或 `InfluencePlaced` 的卡面仍会观察到替换内部真实发生的移除或放置，订阅 `InfluenceReplaced` 的卡面只观察完整替换成功。

Lua 侧禁止：

- 直接写 `GameState`；
- 访问文件、网络、系统时间等非确定来源；
- 使用未受控随机数；
- 调用任意 C# 方法；
- 依赖无法序列化的协程现场才能继续规则。

随机、抽牌和洗牌由 Host C# 提供，实际结果进入 Effect 链。Lua 脚本使用稳定 ID 和版本或内容哈希；恢复未完成效果时必须使用与该局匹配的脚本版本。

## 5. Effect 描述、节点、记录与规则 Event

为避免“Effect Event”一词同时表示多种内容，统一区分：

- `EffectSpec`：Lua 构造函数生成的临时声明式描述，尚未挂树或提交，可以被 C# 拒绝。
- `EffectNode`：C# 接受 `EffectSpec` 后创建的权威运行节点，包含状态、关系、输入和结果。
- `EffectRecord`：C# 成功提交后的权威记录，用于恢复和回放。
- `RuleEvent`：由 C# 根据已校验输入、节点生命周期或成功结算结果形成，并交给 Event 分发器的规则事件。它是 Lua 的唯一规则入口。

例如移动影响力：

```text
Lua 生成 MoveInfluence EffectSpec
  → C# 校验并创建 MoveInfluence EffectNode
  → 节点执行器原子移动
  → 写入 MoveInfluenceRecord
  → 形成并发布 InfluenceMoved Event
```

内部移除旧位置、放入新位置不需要分别发布外部规则 Event。Effect 链记录的是具有规则语义的原子移动，而不是用于实现移动的任意字段写入。

## 6. Effect 树与通用阻塞

### 6.1 节点

建议的逻辑节点至少包含：

```text
EffectNode
├─ nodeId
├─ roundId
├─ parentEffectId
├─ sourceId
├─ effectTypeId
├─ definitionVersion
├─ playerId
├─ status
├─ arguments
├─ result
├─ childEffectIds
├─ blockers
└─ commitSequence
```

状态固定为：

```text
Created → Ready → Running ───────────────→ Completed
                    ├────────────────────→ Failed
                    ├─→ Blocked ─────────→ Completed
                    │       └────────────→ Failed
                    └────────────────────→ Faulted
Created / Ready / Blocked ───────────────→ Faulted
```

- `Created`：节点及规范化输入已经持久化，但前序节点或父执行器尚未允许其运行。
- `Ready`：前序条件已满足，可以被执行器领取。
- `Running`：节点执行当前主体步骤。原子 Effect 可以在此直接得到结果；固有效果链可以在此创建第一个交互或子节点。
- `Blocked`：节点已经完成当前主体步骤，正在等待交互、语义 Event 响应、默认阻塞子节点或额外 Blocker。它可以尚未确定最终结果，也可以已经带有 `pendingOutcome` 并承担完成响应窗口的职责；不再增加独立的 `Completing` 状态。
- `Completed`：节点以成功、合法无变化或由父执行器认可的部分完成结束。
- `Failed`：节点以规则失败结束，例如玩家主动放弃、前置条件不成立或执行时目标失效。它是已结算终态，不是程序异常。
- `Faulted`：脚本错误、数据损坏、违反内核不变量等导致无法可信继续的故障终态。

节点在 `Running` 中完成当前主体步骤后，如果创建了尚未结束的 Blocker，就进入 `Blocked`。Blocker 解除时，父执行器在同一状态下重新判断：可以创建下一个子节点并继续保持 `Blocked`，也可以确定 `pendingOutcome = completed / failed`。当 `pendingOutcome` 已确定且没有未结束 Blocker 时，节点进入对应终态。`Faulted` 不视为普通解除条件；第一版固定采用 fail-stop：子节点故障时沿父关系把所属 Effect 根标记为 `Faulted`，将 `EffectRuntimeState` 置为 `paused_fault`，停止回合主链自动推进并保存诊断记录，不能按普通失败继续。

子节点进入 `Failed` 只表示它已经结束并解除阻塞，**不会自动把失败向上传播**。父节点或固有效果链执行器可以读取子节点结果，再依据自身合同决定完成、失败、跳过后续分支或继续执行。普通有序 Effect式在一个子节点失败后继续下一个兄弟节点；条件式和其他复合流程必须在自己的合同中显式解释子节点结果。

#### 6.1.1 完成响应窗口与 `EffectCompleted`

允许 Lua 添加响应子节点的“完成后 Event”必须发生在源节点最终落入终态之前。统一时序为：

```text
节点在 Running 中完成主体并得到 pendingOutcome
  → 发布该 Effect 自己的成功语义 Event（仅成功时）
  → 有阻塞响应时进入 Blocked，等待响应子节点进入终态
  → 发布一次 EffectCompleted，payload 携带 pendingOutcome
  → 有 EffectCompleted 响应时进入或继续保持 Blocked
  → 所有响应子节点进入 Completed 或 Failed
  → 源节点进入 pendingOutcome 对应的 Completed 或 Failed
```

因此 `EffectCompleted` 表示“节点主体及此前的语义响应已经结算，现在进入通用完成响应窗口”，而不是“源节点已经写入终态”。它的响应仍挂在源节点下并使源节点进入或保持 `Blocked`。分发收据必须保证每个节点只形成一次 `EffectCompleted`；没有订阅者或 Lua 返回空 `EffectSpec[]` 时，不创建子节点并可在同一 Host 事务中直接进入终态。

### 6.2 阻塞关系

父 Effect 创建子 Effect 时，默认添加“子节点完成前阻塞父节点完成”的关系。少数不需要等待的观察或表现行为必须显式声明非阻塞，不能依赖默认放行。

跨父子阻塞通过稳定目标 ID 记录：

```text
EffectBlocker
├─ blockerId
├─ ownerEffectId
├─ targetEffectId 或 interactionRequestId
├─ blockerKind
├─ status
└─ reason
```

内核添加 Blocker 时必须检查：

- 直接或间接循环依赖；
- 已完成节点被重新阻塞；
- 不存在的目标；
- 永远不会产生完成 Event 的目标；
- 长时间无进展但又不是合法玩家等待的状态。

由于允许跨父子依赖，完整执行结构从数学上看可能形成带依赖边的有向图；“Effect 树”仍用于描述来源和所有权，额外依赖边只负责阻塞。

### 6.3 不使用独立持久化栈

本设计不保存另一份 Effect 栈。当前活动路径可以从 Effect 父子关系、节点状态和阻塞关系推导。执行器内部可以临时构造调用路径，但不能让树和栈成为两份可能不一致的权威状态。

## 7. 回合主链

### 7.1 主链与子树

每回合创建一条固定主链。主链节点使用 `previousNodeId`、`nextNodeId` 表达顺序；每个主链节点下面可以挂 Effect 子树。

两类关系含义不同：

- 主链 `next`：前一节点完成后，后一节点变为可执行。
- Effect `parent/child`：子 Effect 默认阻塞父 Effect 完成。

因此，主链后继不是当前节点的阻塞型子 Effect。这样“回合开始”可以在自己的开始阶段子树完成后进入 `Completed`，再激活第一轮第一名玩家，而不需要等整个回合结束才完成。

业务上仍可将整体称为“以第 N 回合开始为顶部、带一条固定主干和若干 Effect 子树的树状执行结构”。

### 7.1.1 开局入场前缀（2026-09-19 补充）

入场也由主链和 Effect 内核驱动。第一回合主链在 `RoundStarted` 之前，按入场顺序为每位玩家创建一个 `player_entrance` 节点。四人局有四个入场节点，因此首条链共 20 个节点；之后每回合仍是下节的 16 个节点。其他人数按实际玩家数创建入场前缀，不创建空座位节点。

```text
入场·P1 → 入场·P2 → 入场·P3 → 入场·P4 → 第1回合开始 → 统一盖放角色牌 → …
  └ flow.player.enter
      ├ entrance.location 交互：选择合法、未被占用的入场点
      ├ 放置城市、初始化核心指挥塔、开放地点
      └ PlayerEntered(player, resourcePoint)
          ├ B-01：GainResource(源岩, 2)、GainResource(源石, 2)、GainResource(异铁, 2)
          └ 无资源点标记时：RevealEventCard(绿色事件牌，结算后放置资源点标记)
```

`flow.player.enter` 是 C# 固有流程 Effect，不是卡面最小 Effect，也不作为卡牌 Lua 的任意入口。它只能在对应玩家的活动入场主链节点下执行；普通城市移动、探索和后来再次到达 B-01 均不发布 `PlayerEntered`。

`PlayerEntered` 的世界规则由 Lua 处理，使用事件中的玩家和资源点生成通用资源与事件牌 Effect。入场节点必须等待这些阻塞响应子树及全部交互完成，才允许下一位玩家入场。事件牌使用现有通用事件牌树和 `AnswerInteraction`，不再创建入场专用 `PendingCardSession`。失败停止推进；恢复沿用已保存的阶段、事件、抽牌结果与交互，不重复发资源或抽牌。

所有玩家完成入场后，在首次发布 `RoundStarted` 前，通过通用资源 Effect 按入场顺序发放开局金券（四人 10/12/14/18，三人 10/12/18，两人 10/18，单人 10）。该步骤有持久化阶段，不能在推进或恢复时重复发放。`GamePhase.Entrance` 和当前玩家只是活动入场节点的兼容投影，不再独立决定流程推进。

### 7.2 四人局每回合固定 16 节点（不含一次性入场前缀）

四人局固定主链由 16 个节点组成。角色牌盖放是每回合必经且会阻塞行动轮开始的规则阶段，因此在“第 N 回合开始”之后单独占一个主链节点；各玩家的盖放操作仍作为该节点内部的子任务，不分别计入固定主链节点数。

| 序号 | 主链节点 | 玩家顺序/完成含义 |
| --- | --- | --- |
| 1 | 第 N 回合开始 | C# 创建并广播 `RoundStarted` Event；其阻塞响应完成后进入统一盖放角色牌节点 |
| 2 | 统一盖放角色牌 | 为本回合所有玩家建立盖放子任务；全部盖放及其响应完成后进入第一行动轮 |
| 3 | 第一行动轮·玩家1窗口 | 第1名玩家完成本行动轮窗口 |
| 4 | 第一行动轮·玩家2窗口 | 第2名玩家完成本行动轮窗口 |
| 5 | 第一行动轮·玩家3窗口 | 第3名玩家完成本行动轮窗口 |
| 6 | 第一行动轮·玩家4窗口 | 第4名玩家完成本行动轮窗口 |
| 7 | 第二行动轮·玩家1窗口 | 第1名玩家完成本行动轮窗口 |
| 8 | 第二行动轮·玩家2窗口 | 第2名玩家完成本行动轮窗口 |
| 9 | 第二行动轮·玩家3窗口 | 第3名玩家完成本行动轮窗口 |
| 10 | 第二行动轮·玩家4窗口 | 第4名玩家完成本行动轮窗口 |
| 11 | 采集 | 全体玩家完成本轮采集后完成；玩家之间不强制使用行动轮顺序 |
| 12 | 收尾·玩家1窗口 | 第1名玩家按规则结算自己的全部收尾 Effect |
| 13 | 收尾·玩家2窗口 | 第2名玩家按规则结算自己的全部收尾 Effect |
| 14 | 收尾·玩家3窗口 | 第3名玩家按规则结算自己的全部收尾 Effect |
| 15 | 收尾·玩家4窗口 | 第4名玩家按规则结算自己的全部收尾 Effect |
| 16 | 第 N 回合结束 | 完成回合级复位、起始玩家处理和 `RoundEnded` Event 发布 |

四人局结构：

```text
第N回合开始
→ 统一盖放角色牌
→ 行动轮1·P1 → P2 → P3 → P4
→ 行动轮2·P1 → P2 → P3 → P4
→ 采集
→ 收尾·P1 → P2 → P3 → P4
→ 第N回合结束
```

若玩家数为 `P`，按本稿口径固定主链节点数为：

```text
1个回合开始 + 1个统一盖放角色牌 + 2P个行动窗口 + 1个采集 + P个收尾窗口 + 1个回合结束
= 3P + 4
```

四人局为 16 个节点，三人局为 13 个节点，二人局为 10 个节点。

### 7.3 玩家顺序快照

创建回合主链时，C# 使用 `TurnOrderService` 解析本回合顺序，并将结果固化到回合执行记录：

```text
playerOrderSnapshot = [P1, P2, P3, P4]
```

本回合主链不在恢复时重新计算顺序。回合内即使起始玩家标记预定发生变化，也不改变已经创建的主链；新顺序从下一回合的新主链开始生效，除非明确规则要求重建尚未执行部分。

### 7.4 回合开始节点与统一盖放角色牌节点

“第 N 回合开始”节点进入 `Running` 时，C# 发布一次具有稳定 Event ID 的 `RoundStarted`。稳定 ID 必须由统一 `StableIdFactory` 生成：各组件先转成不受区域设置影响的字符串，以 UTF-8 字节长度前缀编码，整体计算 SHA-256 并输出小写十六进制，最终格式为 `<kind>_<64位哈希>`。不能直接拼接组件，也不能把本地化名称或可变显示文本放入组件。其逻辑组件为：

```text
eventId = StableId(gameId, roundExecutionId, "round_started")
```

Lua 通过订阅注册表监听该 Event，并在回合开始节点下添加需要先完成的回合开始 Effect。例如第一批接入第 4 回合红区开放时形成：

```text
第4回合开始
└─ 开放红色区域
```

这些响应属于回合开始节点的阻塞子树，不额外计入固定主链。Lua 返回空 `EffectSpec[]` 或当前没有有效订阅者时，不创建任何子节点，回合开始节点直接完成并激活“统一盖放角色牌”节点。

开放红区本质上是改变一组资源点的开放状态：基础规则 Lua 取得该回合应开放的稳定 `LocationRef[]`，返回一个 `Effect.SetLocationsOpen`，由 C# 在单个提交边界内更新整个集合。开放红区引发的企业升级不属于第一批回合骨架和红区 Effect 的完成条件，后续确认企业升级规则后再由 `RoundStarted` 的 Lua 返回额外兄弟 Effect；C# 不因地块变为开放而隐式升级企业。

“统一盖放角色牌”节点进入 `Running` 后，为 `playerOrderSnapshot` 中每名玩家建立一个盖放子任务。第一阶段沿用当前固定玩家顺序，依次开放各玩家的盖放交互；若以后确认规则允许同时提交，只调整该节点内部的任务调度，不改变 16 节点主链。

单人盖放提交后形成 `CharacterCardCovered` Event，其阻塞响应仍挂在该玩家盖放子任务下。所有玩家的盖放子任务及其响应进入终态后，C# 形成 `CharacterCoverCompleted` Event；该 Event 的响应阻塞统一盖放节点。响应完成后，统一盖放节点才能完成并激活第一行动轮的第一个玩家窗口。

### 7.5 玩家行动窗口

一个玩家行动窗口是有限效果轮中的一次玩家访问。玩家执行的主要行动、快速行动及其子 Effect 挂在该窗口下面。

窗口完成至少要求：

- 没有未完成的阻塞 Effect；
- 没有未回答的强制交互；
- 玩家已经满足本窗口最低主要行动要求，或规则允许跳过；
- 玩家显式提交结束窗口；
- 额外主要行动预算已经用完或被玩家明确放弃。

源石工业中枢等增加行动预算的效果扩展当前窗口内容，不在固定主链中额外插入“玩家行动轮节点”。如果未来存在真正新增一个独立玩家回合的效果，再由 Lua 或固有效果链插入新的流程节点，并记录插入依据。

### 7.6 采集节点

采集在固定主链中只占一个节点，因为基础规则不要求玩家严格按行动轮顺序依次提交采集。

进入采集节点时，为所有本轮参与玩家开放采集子任务：

```text
采集
├─ P1采集：未完成
├─ P2采集：未完成
├─ P3采集：未完成
└─ P4采集：未完成
```

任意尚未完成的玩家都可以提交，但 Host 仍逐条串行提交状态。采集节点在所有必需玩家完成后进入 `Completed`。如果未来规则要求先收集所有选择再统一结算，应增加“收集完成”和“结算完成”两个门槛，不能复用立即提交语义。

### 7.7 收尾有限效果轮

收尾本身带玩家顺序，因此固定主链为每名玩家创建一个收尾窗口，而不是建立无序全员屏障。

当前玩家的收尾窗口打开后：

1. Lua 根据回合、玩家和当前状态生成该玩家的收尾 Effect。
2. 如果同一玩家有多个可排序收尾 Effect，创建排序或“选择下一个效果”交互。
3. 选中的 Effect 及全部阻塞子 Effect 完整结算。
4. 返回该玩家收尾窗口，继续处理剩余必需 Effect。
5. 所有必需 Effect 完成后，玩家显式结束或由规则自动结束窗口。
6. 下一个玩家收尾窗口才变为 `Ready`。

这部分将替代当前按 `DelayedCharacterEffects` 平面列表寻找第一条效果的处理方式。

### 7.8 回合结束与下一回合

“第 N 回合结束”节点负责：

- 完成回合级复位；
- 结算或确认起始玩家标记变化；
- 发布一次 `RoundEnded` Event；
- 若已达到最终回合，进入最终计分流程；
- 否则创建“第 N+1 回合开始”及其固定主链。

回合开始和结束 Event 都必须使用稳定实例 ID 去重。网络重连、状态替换和恢复不能重复创建同一回合主链，也不能重复通知 Lua 生成相同子 Effect。

## 8. Effect 链存储与恢复

第一版固定采用“**可继续执行的状态快照 + 快照内的追加式 Effect 日志**”。两者在同一个 Host 事务中提交，不允许只写其中一份：

- `GameState` 继续作为当前对局的权威运行快照，并新增 `EffectRuntimeState`；网络同步和断线恢复优先直接加载该快照。
- `EffectRuntimeState.Journal` 是只追加的因果与审计记录，用于验证、诊断和从初始状态重建；正常继续游戏不需要每次从头重放。
- 快照中的领域状态是命令校验和查询的运行时事实；日志是解释这些事实如何产生的权威记录。加载时若二者的版本、末尾序号或校验信息不一致，必须拒绝继续，不能任选一份静默覆盖另一份。
- 完整日志只保存在 Host 权威快照或服务端存档中，不随每次客户端状态同步下发；客户端 `GameStateView` 只接收当前可见节点、交互和必要序号。

`EffectRuntimeState` 第一版至少包含：

```text
EffectRuntimeState
├─ schemaVersion
├─ status = active / paused_fault
├─ stateRevision
├─ nextCommitSequence
├─ effectNodes[]
├─ interactionRequests[]
├─ ruleEvents[]
├─ dispatchReceipts[]
└─ journal[]
```

以下内容必须进入快照中的 Effect 运行状态或日志：

- C# 创建的回合主链节点及其顺序；
- Lua 创建并被内核接受的 Effect 节点；
- 每个节点的父关系、主链前后关系和阻塞关系；
- 玩家选择、明确放弃和窗口结束；
- 抽牌、掷骰、洗牌等非确定结果；
- 原子 Effect 的实际参数、结果和提交序号；
- 节点完成、故障和恢复记录；
- 使用的 Lua 定义版本或内容哈希。

版本与序号规则固定为：

- `RuleCommit` 是内部最小原子提交点：一次领域状态变化以及与它不可分割的节点状态、Event、分发收据和日志写入属于同一个 `RuleCommit`。
- `stateRevision` 是权威状态的逻辑版本。工作副本中的每个实际改变领域状态或 Effect 运行状态的 `RuleCommit` 都递增一次；纯查询不递增。一次 `GameSession.Submit` 可以连续产生多个 revision，但这些版本只随最终工作副本一起原子发布。
- `commitSequence` 是日志条目的全局单调序号。一个 `RuleCommit` 可以追加多条日志，它们使用连续且不重复的 `commitSequence`，并共享该 `RuleCommit` 提交后的同一个 `stateRevision`；下一个改变状态的 `RuleCommit` 必须使用更大的 revision。
- 规则状态变化、节点状态变化、Event 记录、分发收据和对应日志必须在同一事务边界提交。任何一步失败时整笔事务都不生效。
- `nextCommitSequence` 始终等于下一条日志应取得的序号；加载时必须大于现有日志最大序号。

新对局初始值固定为 `stateRevision = 0`、`nextCommitSequence = 1`。初始化对局、创建第一回合主链等首次权威变化通过正常 Host 事务提交，不为初始化另设负数或零号日志。

迁移期的外层 Host 事务边界固定放在 `GameSession.Submit`：命令先在完整的 `GameState` 工作副本上执行，树执行器可在同一工作副本中连续推进所有无需外部输入的节点；每个 `RuleCommit` 分别递增工作副本中的 `stateRevision`，后续 Lua 查询绑定当时最新的工作 revision。执行到玩家/网络等待点后统一校验，再以工作副本替换正式状态并广播。命令被拒绝、处理器抛错或最终校验失败时丢弃整个工作副本，其中产生的 revision 和日志序号也不算已使用。旧命令处理器只允许修改工作副本，从而不要求每个旧服务自行实现回滚。

当前 `GameState` 与日志满足：

```text
初始对局状态 + 已提交 Effect 链 = 当前 GameState
```

正常恢复分为：

1. 加载最近完整 `GameState` 与其中的 `EffectRuntimeState`。
2. 校验 `schemaVersion`、内容版本、`stateRevision`、日志末尾和分发收据。
3. 从已保存的节点、主链关系和 Blocker 找到 `Ready`、`Running`、`Blocked` 节点及当前合法玩家窗口。
4. 已有成功挂载收据的 Event 只继续现有子节点；只有尚无分发收据且仍处于合法响应窗口的 Event 才调用 Lua。

审计或重建模式才从初始状态开始，按 `commitSequence` 重放日志并与保存快照比较。该模式不得向正式对局追加记录。

恢复历史状态时不能重新向 Lua 广播所有已完成 Event，否则会重复生成子 Effect。可以另设验证模式，重新运行 Lua 并将理论输出与历史记录比较，但验证模式不能写入正式 Effect 链。

为兼容 Unity `JsonUtility`，`normalizedArguments`、`normalizedResult`、Event `payload` 和交互答案不得直接使用 `Dictionary`、`object`、接口实例或 Unity 对象引用。第一版统一使用带 `kind` 标签的可序列化值 DTO；只允许 `bool`、整数、字符串、稳定引用、同类数组和由有序 `name/value` 列表组成的对象。每个 Effect/Event/Interaction 的 schema 以稳定 ID 和版本校验这些字段，字段顺序也必须规范化，保证哈希、回放和跨端结果确定。

## 9. Host、客户端与 UI

- Lua 规则脚本只在 Host 执行。
- 客户端只提交命令或交互答案，不执行权威 Lua 结算。
- C# 创建 `RoundStarted`、`RoundEnded` 和最小 Effect 完成 Event，并负责分发及将结果纳入权威状态快照。
- Host 保存完整 `GameState`；发给客户端的必须是按连接玩家生成的 `GameStateView`。投影同时裁剪领域状态、Effect 节点结果、Event payload、交互候选和答案，不能把完整状态广播给客户端后再由 UI 隐藏。
- UI 根据当前 `Ready` 节点和交互请求决定谁可以操作、显示什么提示。
- 当前只存在一个合法玩家窗口时，其他玩家输入被拒绝。
- 采集等无序节点可以同时向多名玩家显示各自待办，但 Host 仍串行提交收到的命令。
- UI 动画不应默认阻塞规则完成；只有明确的规则交互请求进入 Blocker。

## 10. 第一阶段迁移方案

第一阶段只要求把每回合的玩家行动和阶段推进迁移到树状执行结构，不要求同时完成 Lua 化。

### 10.1 新增回合执行状态

建议在 `GameState` 增加可序列化的回合执行状态，至少包含：

```text
RoundExecutionState
├─ roundExecutionId
├─ roundNumber
├─ playerOrderSnapshot
├─ firstNodeId
├─ activeMainNodeId
└─ nodes
```

主链节点初期可使用扁平列表存储，通过 ID 重建关系，避免序列化对象递归引用。固定主链严格串行，因此 `activeMainNodeId` 至多一个，指向当前 `Ready`、`Running` 或 `Blocked` 的主链节点；统一盖放和采集等节点内部同时开放的玩家子任务属于 `EffectRuntimeState.effectNodes`，不塞入主链活动 ID。

### 10.2 创建固定主链

进入新回合时由 C# 一次性创建 `3P + 4` 个固定主链节点。四人局必须创建本稿定义的 16 个节点，并在第一个节点进入 `Running` 时发布一次 `RoundStarted`。第二个节点固定为“统一盖放角色牌”。

主链节点类型 ID 固定使用 `round_started`、`character_cover`、`player_action_window`、`collection`、`player_cleanup_window`、`round_ended`；行动轮次、玩家 ID 和主链序号是节点实例 ID 的组成参数，不通过拼接本地化名称区分。所有节点实例 ID 由同一个 `StableIdFactory` 根据 `gameId`、`roundExecutionId`、节点类型、主链序号、可选玩家 ID 和行动轮次生成。

第一回合入口也必须走同一创建器：入场流程完成后创建 `roundNumber = 1` 的 `RoundExecutionState`，激活 `round_started`，再由主链进入 `character_cover`。旧 `SetupCommandHandler` 不得继续直接把阶段推进到 `CharacterCover`。非最终回合的 `round_ended` 使用同一创建器创建下一回合；最终回合的 `round_ended` 创建并激活最终计分根节点，不再创建下一回合主链。

### 10.3 保留兼容投影

迁移期只允许 `RoundExecutionProjector` 根据当前活动主链节点写入旧字段：

- 第 N 回合开始 → `Phase = RoundStart`；
- 统一盖放角色牌 → `Phase = CharacterCover`；
- 玩家行动窗口 → `Phase = ActionRound1/ActionRound2`、设置 `CurrentPlayerId`；
- 采集 → `Phase = ResourceCollection`；
- 玩家收尾窗口 → `Phase = Cleanup`、设置 `CurrentPlayerId`；
- 回合结束 → 更新旧 `Round` 与 `StartPlayerId`。

旧命令处理器可以继续读取这些字段，但不得自行写入或推进它们；推进动作必须改为“请求当前主链节点完成”，再由回合执行器切换 `activeMainNodeId` 并调用投影器。`RoundExecutionState` 是唯一推进事实源，旧状态机不能与新主链分别推进。

### 10.4 接入现有命令

- 主要行动成功后，将现有命令结果记录为当前玩家行动窗口的子 Effect。
- 现有角色牌盖放命令改为回答“统一盖放角色牌”节点下当前玩家的盖放子任务；全员盖放及其 Event 响应完成后再完成主链节点。
- `EndAction` 改为申请完成当前玩家窗口，由内核检查阻塞和行动预算。
- 采集成功后完成该玩家的采集子任务；全员完成后完成采集主链节点。
- 当前收尾延迟效果先适配为对应玩家收尾窗口的子 Effect。
- `RoundAdvanceService` 逐步缩减为兼容适配器，最终由回合执行器统一推进。

### 10.5 再接入 Lua

完成回合主链后，再接入 Lua 运行时、最小 Effect 注册表、`RuleEvent` 订阅分发和卡面脚本。纯回合骨架阶段允许 `RoundStarted` 没有订阅者并直接进入下一主链节点，不为红区开放或企业升级增加临时 C# 分支；接入 `Effect.SetLocationsOpen` 与基础规则 Lua 后，才把需要开放红区的正式对局视为规则完整。企业升级可以在更后的内容迁移中补充。

## 11. 最小验收标准

### 11.1 第一阶段：回合主链

- [ ] 四人局创建新回合时固定生成 16 个主链节点，顺序与本稿一致。
- [ ] `RoundStarted` 由 Host C# 只发布一次，重连和重复调用不生成第二条主链。
- [ ] “统一盖放角色牌”是回合开始后的第二个固定主链节点；全员盖放及对应 Event 响应完成前不能进入第一行动轮。
- [ ] 回合创建时固化玩家顺序，恢复时不重新推导已创建主链。
- [ ] 第一、第二行动轮严格按主链玩家窗口推进。
- [ ] 玩家行动产生的 Effect 记录挂在正确玩家窗口下。
- [ ] 当前窗口存在阻塞 Effect 或强制交互时不能结束。
- [ ] 采集只有一个主链节点，允许尚未完成的玩家任意顺序提交，并在全员完成后推进。
- [ ] 收尾为按玩家顺序执行的有限效果轮，每名玩家可以按规则决定自己多个收尾 Effect 的顺序。
- [ ] 回合结束只创建一次下一回合主链，最终回合进入最终计分。
- [ ] 第一回合从入场完成进入 `round_started`，不再由旧处理器直接跳到 `CharacterCover`。
- [ ] `RoundStarted` 没有订阅者或返回空列表时不创建子节点，并立即允许主链进入统一盖放节点。
- [ ] `RoundExecutionState` 是唯一推进事实源，兼容 `Phase` 和 `CurrentPlayerId` 只由投影器更新。
- [ ] 最终回合结束只创建一次最终计分根节点，非最终回合只创建一次下一回合主链。

### 11.2 第二阶段：Effect/Event/Lua 内核

- [ ] 节点主体有未结束子节点或响应时进入 `Blocked`，全部子节点成功或失败终止后进入预定的 `Completed` 或 `Failed`。
- [ ] 子节点 `Failed` 不自动向父节点传播；条件式和固有效果链按各自合同解释失败结果。
- [ ] Lua 不保存协程或局部运行状态，恢复仅依赖权威快照与 Effect 日志。
- [ ] 已有分发收据的 Event 不重新触发 Lua 生成重复子 Effect。
- [ ] 跨父子 Blocker 出现循环依赖时被内核拒绝并留下可诊断故障。
- [ ] `GameState`、Effect 运行快照和追加日志在同一 Host 事务中提交，序号或校验不一致时拒绝恢复。
- [ ] 同一外层命令中的连续 `RuleCommit` 使用严格递增的 `stateRevision`；外层事务失败后正式状态、revision 和日志序号全部保持不变。

## 12. 待确认事项

1. 固有效果链的定义分别放在 C#、Lua 还是结构化资产中；本稿仅确定调度属于 C#。
2. Lua 脚本更新后的旧对局脚本版本保留和迁移策略。
3. 真正“同时选择后统一公开”的规则是否需要独立的选择收集节点。
4. 各单项 Effect 的无法执行、尽量执行和合法无变化结果字段。
5. 跨父子阻塞允许的范围，以及哪些阻塞关系必须限制在同一回合或同一根效果内。
6. 具体 Lua 运行时、沙箱资源上限、脚本打包以及 IL2CPP/AOT 支持方案。
7. `paused_fault` 对局的人工恢复或终止协议；第一版先固定安全暂停。

本文与[命令与效果系统准备规范](命令与效果系统准备规范.md)的关系：旧文档已经废弃，只保留为历史讨论记录，不再保留任何高于本文的规则语义或已确认边界。实现、测试和评审以本文及[《Lua 效果系统 API 初稿》](Lua效果系统API初稿.md)为准。


## 实施校核补充（2026-09-20）

- 十项角色 UI 点击先提交激活，由节点创建唯一 Interaction；事件、角色、通用地图通过同一回答命令协议进入内核。部署取消仍撤销本次行动挂接，不消耗行动预算。
- 稳定候选投影、答案确认、权威提交和表现刷新按 `总体架构.md` 第 6 节执行。动画不得跨层推进规则。
- `RoundEnded` 固有结算补回特殊行动标记的轮末复位，并以持久主节点完成标记防止重复执行；它应在玩家收尾子树完成后运行。
- 旧采集快照必须在写资源和采集完成标记之前建立主链。否则最后一人提交会在导入时自动结束采集/收尾，随后旧完成入口错误拒绝已经发生的操作。
- 测试使用真实盖牌、行动窗口、采集和自动收尾，不再把改写 `Phase=Cleanup`、多次 EndAction 当作新主链的正常流程。旧兼容入口仍需单列审计。
- 内容版本和内核执行器版本是两条独立合同。恢复 continuation 依据内容身份、版本与 hash；不能拿 Choice 的执行器版本代替角色模块版本。
- 当前仍未满足 NMC-014A 全内容迁移前置条件，NMC-015 不可宣布迁移结案，见 `Lua新架构迁移结案.md`。


## 2026-09-20：条件式与交互提交边界

角色和城市样式的条件费用必须先进入 Effect 链：激活 → Condition 左式（可能等待材料组合/放弃选择）→ 支付提交 → Condition 右式（逐步创建目标交互、执行并展示变化）。右式候选不能在左式完成前生成，UI 只回答请求，不持有待扣费用或跨步骤修改状态。

极境计谋使用通用 MoveCity，额外约束相邻、已探索、有己方影响力；没有单独突袭结算。德克萨斯计谋每次移除/移动都是基础子 Effect，实际提交后再生成下一步，按 InfluenceId 排除已移动实例。锡人的两个可选条件分别执行，普通失败不回滚其无条件得分和后续兄弟节点。

复合动力通过 `effect.resource.pay.choice` 在条件左侧复用原材料分配弹窗；一次校验并支付整个组合，右侧才创建 MoveCity。移动及事件完成后，版本化 Lua continuation 生成限定航道候选的 PlaceInfluence。高效移动两段复用实时目标查询。支付前取消不消耗特殊行动标记/预算，支付成功后右式普通失败不退款。

本轮未移除仍有调用方的旧 Pending API；完整迁移状态见《Lua新架构迁移结案》，不得把通用节点已接入等同于全部内容已迁完。


## 2026-09-21：基础设施与持久交互补充

基础设施的 14 种入场模板已由 Lua 组合通用 Effect，完整执行范围及 UI 边界见[基础内容迁移与 UI 接入边界](基础内容迁移与UI接入边界.md)。ChooseBuild、ChooseExplore 先逐段收集选择，再挂具体建设/探索节点；每段选择持久化到 FlowStage，提交时重新验证，不在展示层改变玩家资源。

同一节点连续唤起相同类型交互时，请求序号按该节点已持久化的同类请求累计。恢复和路线回退不能重用旧请求 ID，也不能让旧答案命中新请求。OverrideNextStartPlayer 写入下一回合起始玩家覆盖值，在回合边界应用并清除，不提前改变当前行动顺序。

自动跑局在外层组合根注入 LuaContentCatalog.Register，Application 不再反射查找 Infrastructure。主要行动建设/探索的旧 UI 入口及取消流程仍须切换，不能将设施选择原语已实现等同所有主要行动已先入主链；NMC-015 尚未结案。
