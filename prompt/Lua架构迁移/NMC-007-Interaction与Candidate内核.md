# NMC-007：Interaction 与 Candidate 内核

## 任务提示词

你负责实现统一玩家交互和候选池内核，替代各业务模块自行保存 pending、拼候选与校验回答的模式。此任务先完成通用能力和代表性接线，不提前批量迁移所有内容。

## 前置任务

`NMC-004`、`NMC-006` 已完成。

## 目标

1. 建立可持久化、可投影的 `InteractionRequest`、候选集合和回答合同。
2. 实现 `CandidateSetDraft → Patch → Resolve` 管线与稳定排序的 policy 注册表。
3. 回答命令使用稳定候选 ID 和期望 revision，并在 Host 当前状态上重新验证。
4. 让 Effect 节点以 blocker 等待交互，完成/取消/失效后确定性恢复。

## 实施要求

1. Interaction DTO 至少明确：interactionId、sourceNodeId、目标玩家、kind、prompt key/参数、候选项、数量约束、可否拒绝、visibility、stateRevision 与状态。不得持久化 Presenter、回调或领域对象引用。
2. 候选生成分层：
   - base candidates 表示当前规则上下文下的基础候选；
   - hard constraints 负责绝对合法性，任何 patch 不得绕过；
   - patches 只按注册策略增删/标记候选；
   - resolver 去重、稳定排序并形成公开 DTO。
3. patch 顺序使用明确的稳定键，并定义同一候选重复添加/移除、目标不存在、全部被移除和 patch fault 的行为。
4. Answer 命令不能信任客户端回传的候选正文，只接受稳定 ID/选项值；校验操作者、interaction 状态、revision、数量、当前 hard constraint 和候选仍存在。
5. 玩家合法拒绝按 owning Effect 的显式合同形成 `Failed` 或对应结果，不用异常代替。条件式左侧前拒绝遵守权威文档语义。
6. Interaction 的创建、回答、失效与 blocker 解除都经过 `RuleCommit`；外层失败整体回滚。恢复后同一 interactionId 不重复创建。
7. Presentation 只接通一个通用 renderer/router 合同。可暂留具体业务适配器，但不能让它们拥有第二份权威 pending。

## 测试精简要求

- 用候选种类/patch 行为/回答结果的场景表覆盖等价类；不要为每个资源 ID 或 UI 组件复制同一选择测试。
- Domain 测候选合法性与合并，Application 测命令、事务和 blocker，Presentation 只保留绑定/可见性冒烟。
- 合并 InteractionRouter、Presenter、Controller 中逐层重复的“展示相同列表/提交相同答案”测试。
- 现有业务 `PendingChoice` 等尚未全部迁移前，只删掉已切换代表路径上的重复测试。

## 非目标

- 不在本任务迁移所有角色牌、城市样式、设施和事件牌。
- 不设计具体视觉样式或动画。
- 不允许客户端决定候选合法性。

## 验收标准

- Effect 可创建 interaction 并进入 `Blocked`；有效回答提交后继续；陈旧 revision、越权或非法答案无状态泄漏。
- hard constraints 不可被 patch 绕过，patch/候选顺序跨恢复一致。
- Interaction 可序列化、克隆和按玩家投影；隐藏细节不进入非目标玩家 DTO。
- 无第二份权威 pending，相关及完整 EditMode 通过，`git diff --check` 通过。

## 交付报告

说明 DTO、候选管线、排序、回答命令和可见性边界；列出已接入代表路径、仍待迁移的 pending 字段及对应任务编号，并报告测试净变化。

