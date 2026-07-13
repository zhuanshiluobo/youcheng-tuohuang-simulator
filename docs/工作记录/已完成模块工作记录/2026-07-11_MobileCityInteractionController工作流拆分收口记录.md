# MobileCityInteractionController 工作流拆分收口记录

日期：2026-07-11

## 背景

`MobileCityInteractionController` 在本轮开始前约 3998 行，同时承担 Unity 输入、命令构造与提交、阶段提示、事件选择、资源采集路径、影响力预览、地图高亮和开发冒烟状态准备。第一轮纯选择状态拆分已降低部分复杂度，但主控制器仍持有多个工作流状态，Presentation 也仍存在直接准备玩法状态的职责边界问题。

## 本次架构收口

- 新增无 Unity 引擎依赖的 `YC.Presentation.Workflows` 程序集，引用 `YC.Domain` 与 `YC.Application`，集中承载可直接实例化测试的交互工作流。
- 新增 `InteractionMode`、`IInteractionWorkflow` 与 `InteractionFlowCoordinator`，统一工作流切换、旧流程取消和当前模式管理。
- 新增动态状态与提交端口：`IGameplayContext` 每次读取当前 `GameState` 和本地玩家，避免缓存可能被联网快照替换的状态；`IGameCommandPort` 统一暴露 `CommandResult` 与 `AppliedLocally`；`IInteractionView` 使用语义高亮，不依赖 `UnityEngine.Color`。
- 将资源采集、影响力、探索与事件、回合与行动入口分别收口到 `ResourceCollectionPresenter`、`InfluenceActionPresenter`、`ExplorationEventPresenter`、`TurnActionPresenter`。
- 原有资源采集、探索路径、支付接收方、事件选择、影响力目标、地图二次确认、建设和城市样式等纯 C# 选择控制器迁入 `Presentation/Workflows`，保留原 `.meta` GUID。
- Unity 侧新增 `MobileCityGameplayAdapter`、`MobileCityWorkflowViewAdapter`、`MapInteractionRouter` 和 `DispatchDecisionView`，分别适配玩法查询、提示与语义高亮、地图点击和调度决策弹窗。
- `ActionPanelController` 改为渲染不可变 `ActionPanelViewModel` 并转发点击；工作流切换统一通过协调器执行。
- `CommandSubmissionController` 保持 Presentation 层唯一的本地/联网命令提交适配入口。

## 开发冒烟状态边界

- Application 层新增 `RightCardSmokeStateFactory`，从启动参数创建新的初始 `GameState`，再设置右侧卡区冒烟所需的阶段、玩家、资源、城市位置和供应牌。
- `GameSessionBootstrapper` 在构造 `GameSession` 前选择普通初始状态或冒烟状态。
- `MobileCityInteractionController` 只解析既有命令行模式和决定启动后打开 `Build`、`Declare` 或 `None` 界面，不再直接修改 `GameState`、`PlayerState`、资源或牌库。
- 工厂测试覆盖普通启动不变、冒烟状态正确以及输入集合不被新状态意外共享。

## 控制器收口结果

- `MobileCityInteractionController.cs` 从本轮基线约 3998 行降至 771 行。
- 主控制器当前只保留生命周期、序列化引用、依赖组装、点击转发和统一视图刷新。
- 新增架构测试约束：工作流程序集无 Unity 引擎依赖、Presenter 不继承 `MonoBehaviour`、主控制器不直接持有迁移后的工作流状态或构造玩法命令、控制器行数不超过 1000、`RefreshAllFromState` 只负责视图刷新。
- `InfluenceService` 新增无副作用的 `CanMoveAtomically` 查询，通过投影影响力状态校验连续调度，不在 Presentation 临时修改后回滚玩法状态。

## 测试与构建验证

- 新增或扩展 `InteractionFlowCoordinatorTests`、`RightCardSmokeStateFactoryTests`、`ResourceCollectionPresenterTests`、`InfluenceActionPresenterTests`、`ExplorationEventPresenterTests`、`TurnActionPresenterTests`、`MobileCityInteractionArchitectureTests` 和 `MobileCityInteractionControllerTests`。
- 全量 EditMode：`328 passed / 0 failed`，结果文件为 `Logs/EditModeTests/editmode-20260711_150613.xml`。
- Localhost 构建成功，日志为 `Logs/right-card-smoke-build-20260711_150732.log`。
- 右侧卡区开发冒烟验证通过：`Build` 打开建设选择弹窗、`Declare` 打开城市样式宣告弹窗、`None` 只展示右侧卡区；截图分别为 `Logs/Screenshots/right-card-smoke-build.png`、`right-card-smoke-declare.png` 和 `right-card-smoke-none.png`。

## 残余风险

- `ExplorationEventPresenter` 当前约 1282 行，虽然职责已限定在探索与事件这一条组合工作流，但仍是四个 Presenter 中体积最大的一个。后续若继续扩展事件类型或路径支付分支，应优先拆为路径、支付和事件目标子 Presenter。
- `MobileCityInteractionController` 仍是场景中的 Unity 组装根，序列化引用和少量运行时对象创建暂时保留；新增玩法规则不得回流到该类。
- 本次未提交、未暂存、未推送，并保留工作树原有未提交修改。
