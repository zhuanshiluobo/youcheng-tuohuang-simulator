# Lua 效果树与回合主链设计

> 2026-09-08 · 第一版架构设计稿，尚未实现。
>
> 本文整理用户确认的 Lua 效果、Effect 树、通用阻塞、有限效果轮和回合主链思路，并结合当前代码给出第一阶段迁移方案。本文描述目标架构，不代表当前程序已经具备这些能力。

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

Lua 不直接创建 Unity 弹窗或操作场景对象，而是提出结构化交互请求。最小交互至少包括：

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
  → Lua 返回 Effect 请求、交互请求或固有效果链调用
  → C# 校验并加入执行结构
  → Lua 调用结束，局部运行状态丢弃
```

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
  → Lua 返回子 Effect 或后续 UI 交互请求
  → 子树完成后，C# 产生 EnterpriseSpecialEffectCompleted Event
```

如果原始请求已经带有合法且唯一的企业特效定义 ID，可以跳过 UI 选择，但仍必须产生同一种定向 `EnterpriseSpecialEffectActivated` Event。这样 Lua 不需要知道目标来自按钮直选、弹窗选择、AI 决策还是断线恢复。交互完成时还可以形成通用的 `InteractionAnswered` Event 供其父 Effect 继续，但它不得广播给所有企业脚本；真正进入企业卡面逻辑的入口仍是上述定向 Event。

`EnterpriseSpecialEffectCompleted` 可以开放给多个精确订阅或 `Observer` 通配订阅，用来实现“任意企业发动特效后……”之类的反应。它只能在该企业特效对应的 Effect 子树真正完成后发布，不能在 UI 选择结束时提前发布。

### 4.4 无持久 Lua 状态

示例：

```text
CharacterEffectActivated Event
  → Lua 请求支付资源
ResourcePaymentCompleted Event
  → Lua 请求玩家选择目标
InteractionAnswered Event
  → Lua 请求替换影响力
InfluenceReplaced Event
  → Lua 请求完成角色牌效果
```

Lua 侧禁止：

- 直接写 `GameState`；
- 访问文件、网络、系统时间等非确定来源；
- 使用未受控随机数；
- 调用任意 C# 方法；
- 依赖无法序列化的协程现场才能继续规则。

随机、抽牌和洗牌由 Host C# 提供，实际结果进入 Effect 链。Lua 脚本使用稳定 ID 和版本或内容哈希；恢复未完成效果时必须使用与该局匹配的脚本版本。

## 5. Effect 请求、记录与规则 Event

为避免“Effect Event”一词同时表示多种内容，建议区分：

- `EffectRequest`：Lua 提出的请求，尚未提交，可以被 C# 拒绝。
- `EffectRecord`：C# 成功提交后的权威记录，用于恢复和回放。
- `RuleEvent`：由 C# 根据已校验输入、节点生命周期或成功结算结果形成，并交给 Event 分发器的规则事件。它是 Lua 的唯一规则入口。

例如移动影响力：

```text
Lua 生成 MoveInfluenceRequest
  → C# 校验并原子移动
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

状态至少包括：

```text
Created → Ready → Running → Blocked → Completed
                              └──────→ Faulted
```

节点自身步骤完成、所有默认阻塞子节点完成、额外 Blocker 全部解除后，节点才能进入 `Completed`。

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

### 7.2 四人局固定 15 节点

本稿按用户给出的 15 节点口径，将四人局主链解释为：

| 序号 | 主链节点 | 玩家顺序/完成含义 |
| --- | --- | --- |
| 1 | 第 N 回合开始 | C# 创建并广播 `RoundStarted` Event；其阻塞子树完成后进入行动轮 |
| 2 | 第一行动轮·玩家1窗口 | 第1名玩家完成本行动轮窗口 |
| 3 | 第一行动轮·玩家2窗口 | 第2名玩家完成本行动轮窗口 |
| 4 | 第一行动轮·玩家3窗口 | 第3名玩家完成本行动轮窗口 |
| 5 | 第一行动轮·玩家4窗口 | 第4名玩家完成本行动轮窗口 |
| 6 | 第二行动轮·玩家1窗口 | 第1名玩家完成本行动轮窗口 |
| 7 | 第二行动轮·玩家2窗口 | 第2名玩家完成本行动轮窗口 |
| 8 | 第二行动轮·玩家3窗口 | 第3名玩家完成本行动轮窗口 |
| 9 | 第二行动轮·玩家4窗口 | 第4名玩家完成本行动轮窗口 |
| 10 | 采集 | 全体玩家完成本轮采集后完成；玩家之间不强制使用行动轮顺序 |
| 11 | 收尾·玩家1窗口 | 第1名玩家按规则结算自己的全部收尾 Effect |
| 12 | 收尾·玩家2窗口 | 第2名玩家按规则结算自己的全部收尾 Effect |
| 13 | 收尾·玩家3窗口 | 第3名玩家按规则结算自己的全部收尾 Effect |
| 14 | 收尾·玩家4窗口 | 第4名玩家按规则结算自己的全部收尾 Effect |
| 15 | 第 N 回合结束 | 完成回合级复位、起始玩家处理和 `RoundEnded` Event 发布 |

四人局结构：

```text
第N回合开始
→ 行动轮1·P1 → P2 → P3 → P4
→ 行动轮2·P1 → P2 → P3 → P4
→ 采集
→ 收尾·P1 → P2 → P3 → P4
→ 第N回合结束
```

若玩家数为 `P`，按本稿口径固定主链节点数为：

```text
1个回合开始 + 2P个行动窗口 + 1个采集 + P个收尾窗口 + 1个回合结束
= 3P + 3
```

三人局为 12 个节点，二人局为 9 个节点。

### 7.3 玩家顺序快照

创建回合主链时，C# 使用 `TurnOrderService` 解析本回合顺序，并将结果固化到回合执行记录：

```text
playerOrderSnapshot = [P1, P2, P3, P4]
```

本回合主链不在恢复时重新计算顺序。回合内即使起始玩家标记预定发生变化，也不改变已经创建的主链；新顺序从下一回合的新主链开始生效，除非明确规则要求重建尚未执行部分。

### 7.4 回合开始子树

“第 N 回合开始”节点进入 `Running` 时，C# 发布一次具有稳定 Event ID 的 `RoundStarted`：

```text
eventId = gameId + roundNumber + "round-started"
```

Lua 通过订阅注册表监听该 Event，并在回合开始节点下添加需要先完成的 Effect。例如：

```text
第4回合开始
├─ 开放红色区域
├─ 企业升级有限效果轮
│  ├─ P1选择并完整升级
│  ├─ P2选择并完整升级
│  ├─ P3选择并完整升级
│  └─ P4选择并完整升级
└─ 角色牌盖放有限效果轮
   ├─ P1盖放
   ├─ P2盖放
   ├─ P3盖放
   └─ P4盖放
```

这些节点属于回合开始的阻塞子树，不计入固定 15 节点主链。全部完成后，回合开始节点才能完成并激活第一行动轮的第一个玩家窗口。

这也是迁移当前 `CharacterCover` 的建议位置。是否未来把角色牌盖放直接列入固定主链节点数，可以后续调整；本稿优先保持用户给出的 15 节点口径。

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

Lua 不产生需要保存的运行现场，但以下内容必须进入 Effect 链：

- C# 创建的回合主链节点及其顺序；
- Lua 创建并被内核接受的 Effect 节点；
- 每个节点的父关系、主链前后关系和阻塞关系；
- 玩家选择、明确放弃和窗口结束；
- 抽牌、掷骰、洗牌等非确定结果；
- 原子 Effect 的实际参数、结果和提交序号；
- 节点完成、故障和恢复记录；
- 使用的 Lua 定义版本或内容哈希。

当前 `GameState` 可以视为 Effect 链的运行时投影：

```text
初始对局状态 + 已提交 Effect 链 = 当前 GameState
```

恢复分为：

1. 从初始状态或最近完整快照开始。
2. 按 `commitSequence` 重放已经提交的最小 Effect。
3. 根据节点、父关系、主链关系和 Blocker 重建执行结构。
4. 找到最后一个未完成节点和当前合法玩家窗口。
5. 只对尚未产生后续记录的最新 `RuleEvent` 重新调用 Lua。

恢复历史状态时不能重新向 Lua 广播所有已完成 Event，否则会重复生成子 Effect。可以另设验证模式，重新运行 Lua 并将理论输出与历史记录比较，但验证模式不能写入正式 Effect 链。

完整 `GameState` 快照可以作为加速和联机同步手段，但不能替代 Effect 链所需的因果关系、完成状态与随机结果记录。

## 9. Host、客户端与 UI

- Lua 规则脚本只在 Host 执行。
- 客户端只提交命令或交互答案，不执行权威 Lua 结算。
- C# 创建 `RoundStarted`、`RoundEnded` 和最小 Effect 完成 Event，并负责分发、网络广播或将结果纳入权威状态快照。
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
├─ activeNodeIds
└─ nodes
```

主链节点初期可使用扁平列表存储，通过 ID 重建关系，避免序列化对象递归引用。

### 10.2 创建固定主链

进入新回合时由 C# 一次性创建 `3P + 3` 个固定主链节点。四人局必须创建本稿定义的 15 个节点，并发布一次 `RoundStarted`。

### 10.3 保留兼容投影

迁移期由当前活动主链节点投影旧字段：

- 玩家行动窗口 → `Phase = ActionRound1/ActionRound2`、设置 `CurrentPlayerId`；
- 采集 → `Phase = ResourceCollection`；
- 玩家收尾窗口 → `Phase = Cleanup`、设置 `CurrentPlayerId`；
- 回合结束 → 更新旧 `Round` 与 `StartPlayerId`。

旧命令处理器可以继续读取这些字段，但推进动作必须逐步改为“完成当前主链节点”，避免旧状态机和新主链分别推进。

### 10.4 接入现有命令

- 主要行动成功后，将现有命令结果记录为当前玩家行动窗口的子 Effect。
- `EndAction` 改为申请完成当前玩家窗口，由内核检查阻塞和行动预算。
- 采集成功后完成该玩家的采集子任务；全员完成后完成采集主链节点。
- 当前收尾延迟效果先适配为对应玩家收尾窗口的子 Effect。
- `RoundAdvanceService` 逐步缩减为兼容适配器，最终由回合执行器统一推进。

### 10.5 再接入 Lua

完成回合主链后，再接入 Lua 运行时、最小 Effect 注册表、`RuleEvent` 订阅分发和卡面脚本。这样可以先验证树状回合推进，不把回合迁移、Lua 沙箱和全部卡牌迁移同时混在第一步。

## 11. 最小验收标准

- [ ] 四人局创建新回合时固定生成 15 个主链节点，顺序与本稿一致。
- [ ] `RoundStarted` 由 Host C# 只发布一次，重连和重复调用不生成第二条主链。
- [ ] 回合创建时固化玩家顺序，恢复时不重新推导已创建主链。
- [ ] 第一、第二行动轮严格按主链玩家窗口推进。
- [ ] 玩家行动产生的 Effect 记录挂在正确玩家窗口下。
- [ ] 当前窗口存在阻塞 Effect 或强制交互时不能结束。
- [ ] 采集只有一个主链节点，允许尚未完成的玩家任意顺序提交，并在全员完成后推进。
- [ ] 收尾为按玩家顺序执行的有限效果轮，每名玩家可以按规则决定自己多个收尾 Effect 的顺序。
- [ ] 回合结束只创建一次下一回合主链，最终回合进入最终计分。
- [ ] Lua 不保存协程或局部运行状态，恢复仅依赖初始状态/快照与 Effect 链。
- [ ] 重放已提交 Effect 时不重新触发 Lua 生成重复子 Effect。
- [ ] 跨父子 Blocker 出现循环依赖时被内核拒绝并留下可诊断故障。

## 12. 待确认事项

1. 角色牌盖放长期保持为回合开始阻塞子树，还是未来进入固定主链计数。
2. 固有效果链的定义分别放在 C#、Lua 还是结构化资产中；本稿仅确定调度属于 C#。
3. Effect 链最终采用纯追加记录、节点状态快照，还是两者并存并互相校验。
4. Lua 脚本更新后的旧对局脚本版本保留和迁移策略。
5. 真正“同时选择后统一公开”的规则是否需要独立的选择收集节点。
6. 可选 Effect、无法执行、尽量执行和明确放弃分别使用什么完成记录。
7. 跨父子阻塞允许的范围，以及哪些阻塞关系必须限制在同一回合或同一根效果内。

本文与[命令与效果系统准备规范](命令与效果系统准备规范.md)的关系：旧文档保留规则语义和已确认边界；本文用 Lua、Effect 树、回合主链和有限效果轮重新组织目标运行架构。后续若进入实现，应先统一两份文档中的术语，再建立代码类型。
