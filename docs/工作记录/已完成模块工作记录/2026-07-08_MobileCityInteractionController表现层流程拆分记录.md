# MobileCityInteractionController 表现层流程拆分记录

日期：2026-07-08

## 背景

`MobileCityInteractionController` 已接通移动城市、探索、部署、调度、建设、宣告样式、事件待选和采集等交互，但同时承担了较多原型期流程状态。为了降低后续维护风险，本次先做低风险拆分，不重写 UI，不改变命令提交路径和弹窗视觉。

## 本次调整

- 新增 `ExplorePathSelectionController`，保存探索目标、路径候选和当前选择，供探索路径/路费选择流程复用。
- 新增 `ResourceCollectionSelectionController`，保存采集候选资源点、已选资源点、已付费航道、路费接收者和资源点路径索引。
- 新增 `MapInteractionConfirmationController`，保存二次确认的 action/target/location/slot 和确认回调，主控制器继续负责实际地图高亮恢复。
- `MobileCityInteractionController` 继续作为 `MonoBehaviour` 入口，保留 Unity 输入、`EventChoiceDialog` 弹窗、`MapViewPresenter` 高亮和 `GameCommand` 提交职责。
- 既有 `EventInfluenceTargetSelectionController`、`ExplorePaymentRecipientSelectionController` 继续承担事件影响力目标和探索路费接收者选择状态。

## 测试覆盖

- `PresentationSelectionControllerTests` 新增：
  - 探索路径候选与索引选择。
  - 采集路径选择、资源点切换、选中路线构建和路费接收方编码。
  - 二次确认取消/清理状态，以及第二次点击复用首次确认回调。
- 既有事件影响力目标选择和探索路费接收者选择测试继续保留。

## 验证状态

- 代码侧已移除 `MobileCityInteractionController` 内部的探索路径、采集选择和二次确认旧字段。
- `git diff --check` 已通过；目标文件中未再检出旧的探索、采集和二次确认状态字段。
- 首次直接运行 Unity EditMode 过滤测试时，Unity batchmode 卡住且未产生日志和 XML，随后只停止了本次启动的 `Unity.exe`。
- 2026-07-09 再次过滤运行 `YC.Tests.EditMode.PresentationSelectionControllerTests`，外层设置 180 秒硬超时；Unity 仍在生成日志/XML 前卡住，超时后已自动停止本次启动的 PID。
- 截断后未发现残留的项目 Unity Editor 进程，也不存在 `Temp/UnityLockfile`。当前只能确认静态检查通过，不能把这次超时记为测试通过或测试失败；未验证边界位于 Unity 启动或测试框架初始化阶段。

## 后续边界

- 在 Unity 命令行测试入口恢复后，重新运行 `PresentationSelectionControllerTests`，以生成的 XML 结果作为通过依据。
- 后续可继续拆分调度决策弹窗、地图高亮策略和 UI 生成细节；本次不扩大到视觉重设计或命令链路重写。
