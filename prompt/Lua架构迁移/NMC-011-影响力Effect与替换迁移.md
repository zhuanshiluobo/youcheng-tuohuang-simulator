# NMC-011：影响力 Effect 与替换迁移

## 任务提示词

你负责把影响力放置、移除、移动和替换收敛为通用 Effect 合同，保留现有领域原子能力但移除上层卡牌/ID 特判。尤其要实现“移除成功、放置失败仍算替换流程完成但只保留移除”的既定语义。

## 前置任务

`NMC-004`、`NMC-006`、`NMC-007` 已完成。

## 目标

1. 定义并实现 `PlaceInfluence`、`RemoveInfluence`、`MoveInfluence`、`ReplaceInfluence` 或等价稳定 Effect 合同。
2. 统一影响力实例 ID、owner、来源/目标、`causeKind`、Event 和日志。
3. 将现有 `InfluenceService` 等规则原子接入 executor，杜绝卡牌直接改状态。
4. 迁移替换流程并清理旧专用分支。

## 核心语义

- 移除前先验证，移除失败时外层 `ReplaceInfluence` 为 `Failed`，状态不变，不产生成功事件。
- 移除提交并完成其 `InfluenceRemoved` 响应窗口后，重新计算/校验放置条件。
- 放置失败不回滚已成功移除；外层为 `Completed(removed_only)`，不产生 `InfluencePlaced` 或 `InfluenceReplaced`。
- 放置成功时依次形成 `InfluenceRemoved`、`InfluencePlaced`、`InfluenceReplaced`；每个事件只在对应状态提交后产生，并遵守各自响应窗口。

## 实施要求

1. Effect 参数只用稳定玩家/位置/影响力实例 ID 与归一化数据，不持有对象引用。定义来源、目标相同、目标满、资源不足、目标在响应后变化等边界。
2. 可复用现有 Domain service 作为合法性/原子修改器，但所有生产调用统一由 Effect handler 进入。旧命令或内容适配器只能构造 EffectSpec，不能再直接修改状态。
3. `MoveInfluence` 明确是单一合同还是 remove/place 组合，并保证事件、失败和事务语义与文档一致。
4. `causeKind` 与 sourceNodeId/内容来源可恢复、可投影，不能靠调用栈推断。
5. 替换的第二段必须在移除响应全部结束后重新验证，不能缓存旧合法性结果。
6. 搜索所有影响力直写、替换特判和重复事件入口。迁移完成的调用方立即清理；尚待事件牌/城市/设施迁移的调用方用单一显式 adapter 登记到 `NMC-012`～`014`。

## 测试精简要求

- 用行为矩阵交叉覆盖 remove/place 结果、响应后状态变化和最终 outcome；避免四个 Effect 各复制整套资源/位置排列。
- 领域原子合法性只在最低层权威测试；Effect 层关注编排、Event 顺序、部分完成与恢复。
- 合并现有 InfluencePlacement/Removal/Movement/Replacement 中同构 ID、玩家、地点用例，保留真正不同的容量、所有权和权限规则。
- 替换流程必须有 commit checkpoint 恢复测试。

## 非目标

- 不迁移全部使用影响力的事件牌、城市样式或设施；仅提供合同和迁移必要的共享入口。
- 不把 `removed_only` 当作回滚失败，也不擅自补偿放回。
- 不处理红区开放引发的企业升级。

## 验收标准

- 四类影响力变更均由注册 Effect 执行，事件与结果可序列化恢复。
- 替换三种结果精确符合核心语义，特别是放置失败只移除且外层 Completed。
- 生产代码无已迁移范围的直接状态写入或 ID 特判，无旧新双事件。
- 相关及完整 EditMode 通过，测试矩阵比旧复制用例更紧凑，`git diff --check` 通过。

## 交付报告

附 Effect 参数/结果/Event 表、替换时序、现有 service 复用方式、迁移/暂留调用方和删除清单，并报告测试净变化。

