# NMC-008：RoundStarted 红区纵向切片

## 任务提示词

你负责完成第一个可玩的端到端 Lua 纵向切片：回合主链发出 `RoundStarted`，Lua handler 根据规则返回 `SetLocationsOpen`，EffectExecutor 权威修改可开放资源点集合，然后继续主链。该切片用于验证整套架构，而不是顺带迁移其他内容。

## 前置任务

`NMC-005`、`NMC-006`、`NMC-007` 已完成。

## 目标

1. 把 `RoundStarted → Lua handler → EffectSpec → SetLocationsOpen → Event/日志 → 下一主链节点` 跑通。
2. 实现第一份完整、版本化的具体 Effect 合同 `SetLocationsOpen`。
3. 验证无订阅者/空 EffectSpec 数组的直通分支，以及 commit 后中断恢复的幂等性。

## 实施要求

1. 先在代码与 Lua schema 中写清 `SetLocationsOpen` 的输入、目标集合、合法无变化、业务失败、事件、可见日志和序列化形式。
2. Lua 只选择/声明要设置的资源点稳定 ID 集合；C# executor 重新解析、验证并提交。不得把可写 location 对象交给 Lua。
3. 资源点集合结果必须确定性排序/去重；未知 ID、非法区域或超量输入按合同处理，不得静默忽略。
4. `RoundStarted` 没有 subscriber，或所有 subscriber 返回空数组时，源节点完成并立即进入角色盖放主节点，不产生空 interaction/空 blocker。
5. 有响应时，`RoundStarted` 等待子 Effect；子节点合法失败只解除 blocker，由 RoundStarted 合同决定继续，不自动 fault。
6. 开放红区的本质仅是“可开放资源点集合变化”。由红区开放间接触发的企业升级暂不处理，也不建立占位双写。
7. 写入事件/日志和 dispatch receipt；在每个 RuleCommit 后模拟恢复，确保不重复开放、不重跑 handler、不跳过下一主链节点。
8. 搜索并移除同一红区开放规则的旧直写入口。若旧代码仍服务其他规则，收窄到单一适配器并登记。

## 测试精简要求

- 一个数据表覆盖玩家数/回合输入对应的红区集合、重复 ID、未知 ID、无变化、无订阅和空返回。
- 只保留少量端到端测试覆盖 Lua 分发、状态提交、事件/日志和下一阶段；底层排序/事务不重复测试。
- 将旧红区/资源点开放测试映射到 `SetLocationsOpen` 权威用例，合并只换地图位置的复制测试。
- 完成后必须运行完整 EditMode。

## 非目标

- 不处理红区引发的企业升级。
- 不迁移角色牌、事件牌、设施或城市样式。
- 不让 Lua 决定主链下一阶段。

## 验收标准

- 指定 Unity 中端到端切片通过；红区集合、Event、日志和主链推进均来自新路径。
- 无 subscriber/空返回零阻塞直通。
- 任意 commit 后恢复结果一致，handler receipt 防止重放。
- 相同规则没有旧新双写；完整 EditMode 无新增失败，`git diff --check` 通过。

## 交付报告

给出时序、Effect 合同、脚本位置/版本、恢复检查点、清理掉的旧入口与暂留适配器，报告测试前后数字。这份报告将作为后续内容迁移的参考模板。

