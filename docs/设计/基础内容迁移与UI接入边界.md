# 基础内容迁移与 UI 接入边界

本页记录 2026-09-21 的当前实现，历史回归报告中的“剩余设施适配”按当时状态保留。

## 当前基础内容

| 内容 | 当前执行方式 |
| --- | --- |
| 五张角色、十项能力 | 外部 JSON 定义与外部 Lua 组合通用 Effect |
| 设施 | 19 组模板、颜色数量清单展开 46 个实体；14 种入场模板全部使用 Lua 组合，没有正式脚本再调用旧 facility_entry 适配 |
| 无入场设施 | 五种模板使用空入场脚本；唯一性、储备及开局身份为结构数据，费用修正单独定义 |
| 事件牌 | 外部 JSON 数据驱动事件效果，按实际颜色池注册及抽牌 |
| 城市样式 | 外部脚本组合通用效果；额外行动直接使用 GrantMainActions，旧 ScriptStep 执行分支已撤下；标记可用次数仍由共用生命周期服务管理 |

这些结果不等于所有规划 API、企业玩法和扩展规则都已实现。永续、一次性及企业、企业家的具体规则按用户约定留待扩展卡实现。

## 本轮新增与修正

- `Effect.ChooseBuild({player, sourceZone='supply'})`：选择可支付且有合法位置的设施，再选择资源/金券支付方式和实际位置。全部选择完成后挂 Build。普通建设费用照付，效果授予的建设不扣主要行动次数。
- `Effect.ChooseBuild({player, sourceZone='reserve', effectId='reserve_extension_hub'})`：按 Lua 指定的储备类型筛选，允许跳过，选择位置后免费建设。不会默认第一个空位。
- `Effect.ChooseExplore({player})`：逐段规划路线，选择合法探索落点；同一收费区域有多个接收玩家时明确选择接收人。规划时可以退回上一段或放弃；确认前不付路费、不翻事件牌。选择完成后挂现有 Explore，再由事件链处理奖励与影响力。
- `Effect.OverrideNextStartPlayer({player, targetPlayer, scope='current_round'})`：原子写入本回合的下一起始玩家覆盖值，后一次覆盖前一次。当前玩家和起始标记不立即改变；回合边界消费后清除，下一回合不残留。
- `Effect.GrantMainActions({player, amount, lockCharacterCard})`：直接授予额外行动。源石工业枢纽使用此原语，不再绕 ExecuteMainAction 的 grant_budget 模式。
- 载具仓库由 Lua Choice 表达“移除后调度”与“探索”两分支；移除、移动、路线、收费方及事件选择均逐段等待，不能预选后续目标。
- 设施费用新增 `data.costReductionPerDistinctBuiltColor`，格式为五资源 ResourceSet。例如 `{"originium":1}` 表示每种已建基础颜色减少 1 源岩成本，最低为零；彩色不额外计入基础颜色。费用预览与实际支付共用计算。金券整体支付仍使用 goldVoucherCost，不将资源折扣错误套到金券整价。

外部费用修正不按 effectId 分支。旧内置资产的固定效果 ID 仅在兼容目录初始化时转成同一数据字段；正式外部内容必须显式声明修正。字段每项为 0～99，缺省为零。

## 层级与恢复

Lua 负责选择卡面组合和数值，Domain 原语负责候选合法性、持久化交互与状态提交，Presentation 只展示候选并提交 AnswerInteraction。设施 renderer 已加入统一 InteractionRequestRouter，原地图点击 adapter 保留作为输入转发器。

同一 Effect 可多次唤起同类交互。请求序号现在按该节点已持久化的同类请求累计，不再每次从零开始；恢复或探索退回上一步不会重用旧请求 ID。建设、路线和收费方选择保存在节点 FlowStage，提交时复核实际状态。旧答案不会作用于后续请求。

设施旧 Behavior 与 ExecuteMainAction 的 facility_entry 分支、城市样式旧 ScriptStep 都明确拒绝继续执行。设施入口宿主版本 1.3.0，建设宿主版本 1.1.0；内容包哈希也随脚本和模板变化。旧未完成对局不能静默套用新规则。

## 剩余 UI 接入和验收

1. 主要行动的建设、探索等仍有原 UI 先选参数的入口。底层选择原语已可执行；要改为“点击后先入主链”，必须调整现有行动 UI 的启用、取消和反悔状态，避免原工作流与新请求同时争用输入。部署已经使用新路径。
2. 新设施流程目前复用列表弹窗，可执行且保留原布局；探索路线、建设位置的地图/城市面板高亮、支付费用预览及较多候选时的展示，需要实际 UI 验收和改善。
3. 旧 Pending 字段、旧协调器及旧默认命令构造仍有兼容或旧 UI 调用。不能直接删除字段和测试来宣称迁移完成；须逐一切换入口并验证旧存档拒绝/恢复行为。
4. 多玩家真实 UI、断线重连、跨客户端隐藏信息与发布包验收仍需完成。EditMode 联机相关测试不能代替发布级多人验收。

NMC-015 尚不能结案。当前已落实本轮基础卡牌无需新增 UI 的规则迁移；上述入口和表现改造是下一阶段，扩展尚未定义的规则不计作已完成。

## 自动跑局的组合根

自动跑局工具不再通过 Type.GetType/GetMethod 反射访问 Infrastructure。Application 接收 `Action<EffectRegistry>` 内容装配回调，Editor、Presentation 与测试明确传入 `LuaContentCatalog.Register`；该注册器组合入场、事件、角色、设施与城市样式目录。自动跑局的各命令处理器也共用同一个 RoundExecutionService/RoundAdvanceService。此修改修复了 Register 新增重载后的 AmbiguousMatchException，并消除反射掩盖的跨层依赖。
