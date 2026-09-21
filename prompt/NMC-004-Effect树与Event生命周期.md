# NMC-004：Effect 树、Blocker 与 Event 生命周期

## 任务提示词

你负责实现不依赖具体 Lua 内容的 Effect 树内核：节点状态机、父子阻塞、Event 生命周期、响应窗口与故障停机。使用 C# 假处理器完成内核验证，不要把内容迁移混入本任务。先完整阅读共同约束、两份 Lua 权威文档和 `NMC-003` 产物。

## 前置任务

`NMC-003` 已完成；运行状态、事务、稳定 ID 和序列化 DTO 已可用。

## 目标

1. 实现 Effect 类型注册、节点创建、执行、阻塞、恢复和终态推进。
2. 实现 Event 产生、确定性 handler 分发、dispatch receipt 与 `EffectCompleted` 完成响应窗口。
3. 对脚本/处理器异常、未知类型、无进展和循环建立可诊断的 fail-stop。

## 必须遵守的语义

- 节点状态为 `Created → Ready → Running/Blocked → Completed/Failed`，内核错误进入 `Faulted`。`Running` 表示主体步骤正在执行或已完成主体并正在决定后续；`Blocked` 表示它在等交互、子节点或完成响应子节点。
- 子节点必须按确定性顺序执行。子节点 `Failed` 只表示该分支合法失败并解除 blocker，不自动令父节点失败；父节点处理器根据已记录结果决定自己的 `Completed` 或 `Failed`。
- `EffectCompleted` 在源节点写入终态前分发。产生的响应节点作为源节点 blocker；全部响应子节点 `Completed/Failed` 后，源节点才提交既定终态。
- `Faulted` 与业务 `Failed` 严格分离。任何未处理内核异常、未知处理器、循环或无法推进都使整个 `EffectRuntimeState.status = paused_fault`，禁止继续主链。
- 首版为 fail-stop，不自行跳过坏节点，也不悄悄改成 `Failed`。

## 实施要求

1. 建立显式 `EffectRegistry`/`EffectExecutor` 或等价职责；节点数据与运行中服务分离，恢复后可由稳定类型 ID 重建执行逻辑。
2. 每步推进通过 `RuleCommit` 落地，并能在 commit 后中断/恢复。不得依赖调用栈、协程对象或内存委托保存 continuation。
3. handler 稳定排序键必须落实为 `(routeTier, priority, contentInstanceId, abilityId, handlerId)`，其中 `routeTier` 保证 targeted/continuation 早于普通 observer，priority 数值小者先执行。
4. 每次 Event 与 handler 执行都有稳定 dispatch key/receipt。恢复时已经有 receipt 的 handler 不重复产生子节点。
5. 建立最大步数/树深/节点数等防护，且超限进入可诊断 `paused_fault`。
6. 实现条件式内核合同：左侧检查与条件本身属于同一 intrinsic-flow owner；玩家在左侧前拒绝则条件节点 `Failed` 且无子节点；左侧首个失败停止剩余左侧并令条件节点 `Completed(condition_not_met)`，不创建右侧；右侧子节点失败不向上传播，条件节点最终 `Completed(condition_met)`。

## 测试精简要求

- 用一张参数化状态转换/阻塞矩阵覆盖父子结果组合，不为每条箭头复制测试类。
- Event 测试集中验证排序、receipt 幂等、完成响应窗口和恢复；不要在每个假 Effect 中重复这些断言。
- 合并现有命令结果、延迟效果或事件测试中已经由内核矩阵权威覆盖的基础推进细节，但保留尚未迁移业务路径的独特合同。
- 跑目标过滤测试与完整 EditMode，报告净变化。

## 非目标

- 不接入真实 Lua 运行时，不开放玩家 UI，不迁移具体卡牌/设施/城市内容。
- 不实现客户端投影或自动修复 `paused_fault`。
- 不允许旧回合服务与 Effect 主链同时推进。

## 验收标准

- 任意阻塞点序列化/克隆后能继续，已完成 handler 不会重放。
- `EffectCompleted` 响应严格发生在源节点终态提交前，并能跨恢复继续。
- 子节点业务失败不会隐式污染父节点；内核故障稳定进入 `paused_fault`。
- 条件式语义、排序键、上限和无进展检测都有数据驱动测试。
- 完整 EditMode 无新增失败，`git diff --check` 通过。

## 交付报告

给出状态转换表、blocker 类型、commit/恢复点、Event 排序与幂等机制、故障码；列出被内核替代并清理的旧基础设施、仍保留的业务适配器和测试净变化。

