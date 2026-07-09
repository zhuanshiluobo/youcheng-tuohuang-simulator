# 2026-06-30 事件选择表现层 PendingCardSession 迁移记录

## 背景

通用抽卡流程在 2026-06-29 已经把探索事件、移动城市事件和入场事件统一收口到 `CardFlowService + IEventCardScenario`，并通过 `CardFlowStateAdapter.OpenPendingSession()` 同时写入：

- 新状态 `PendingCardSessionState`
- 旧兼容状态 `PendingChoiceState`

收口后底层结算已经以 `PendingCardSessionState` 为准，但表现层和部分命令入口仍直接读取旧 `PendingChoiceState`。这会让旧状态继续像主事实源一样扩散，不利于后续把更多事件流程接入统一卡牌会话。

## 本次完成内容

### 1. 新增待选快照读取入口

文件：

- `Assets/YC/Domain/Cards/CardFlowStateAdapter.cs`

新增：

- `PendingCardChoiceView`
- `CardFlowStateAdapter.GetPendingChoiceView(GameState state)`

读取策略：

1. 若 `PendingCardSessionState` 有效，优先从 session 构造待选快照。
2. 若 session 不存在或无效，再回退读取旧 `PendingChoiceState`。
3. 若两者都无效，则返回空。

这让 UI 和命令入口可以统一消费一个只读快照，而不用各自判断两套状态。

### 2. 表现层切换为 PendingCardSession 优先

文件：

- `Assets/YC/Presentation/MobileCityInteractionController.cs`
- `Assets/YC/Presentation/EventChoiceDialog.cs`

调整：

- `MobileCityInteractionController` 新增 `CurrentPendingChoice`，内部调用 `CardFlowStateAdapter.GetPendingChoiceView(...)`。
- 待选事件开窗、事件选项分流、事件影响力目标选择、事件来源资源点元数据、行动面板待选归属提示，都改为读取待选快照。
- `EventChoiceDialog` 的展示模型不直接持有状态事实源，继续作为纯弹窗渲染入口使用。

结果：

- 表现层优先直接消费 `PendingCardSessionState`。
- 旧 `PendingChoiceState` 不再是事件选择 UI 的主事实源。
- 旧状态仍可在没有有效 session 的兼容场景下 fallback。

### 3. 三条事件结算链路切到统一快照

文件：

- `Assets/YC/Application/Setup/SetupCommandHandler.cs`
- `Assets/YC/Application/Gameplay/ExploreLocationCommandHandler.cs`
- `Assets/YC/Application/Gameplay/MoveCityCommandHandler.cs`

调整：

- 入场事件、探索事件、移动城市事件的待选结算入口改为读取 `CardFlowStateAdapter.GetPendingChoiceView(...)`。
- 探索事件和移动城市事件打开待选时，不再额外手工写入旧 `PendingChoiceState`，避免旧状态继续扩散为主事实源。
- 旧 `PendingChoiceState` 的双写仍统一保留在 `CardFlowStateAdapter.OpenPendingSession()`。

### 4. 保留 SourceCommandId

文件：

- `Assets/YC/Domain/Exploration/ExplorationService.cs`
- `Assets/YC/Domain/Movement/CityMovementService.cs`
- `Assets/YC/Application/Gameplay/ExploreLocationCommandHandler.cs`
- `Assets/YC/Application/Gameplay/MoveCityCommandHandler.cs`

调整：

- 探索和移动城市打开待选、即时结算时，把 `command.CommandId` 继续透传到 `CardFlowStartRequest` 或 `CardFlowExecuteRequest`。
- 避免去掉命令处理器手写旧 choice 后丢失 `SourceCommandId`。

## 回归测试

补充或扩展的测试：

- `Assets/YC/Tests/EditMode/SetupCommandHandlerTests.cs`
  - 入场事件打开待选后写入 `PendingCardSessionState`
  - 清掉旧 `PendingChoiceState` 后仍可通过新 session 结算
- `Assets/YC/Tests/EditMode/ExploreLocationCommandHandlerTests.cs`
  - 探索事件打开待选后写入 `PendingCardSessionState`
  - 清掉旧 `PendingChoiceState` 后仍可通过新 session 结算
- `Assets/YC/Tests/EditMode/MoveCityCommandHandlerTests.cs`
  - 移动城市事件打开待选后写入 `PendingCardSessionState`
  - 清掉旧 `PendingChoiceState` 后仍可通过新 session 结算
- `Assets/YC/Tests/EditMode/PresentationSelectionControllerTests.cs`
  - `GetPendingChoiceView(...)` 优先返回 `PendingCardSessionState`
  - session 缺失时 fallback 到旧 `PendingChoiceState`

实际通过的 EditMode：

- `YC.Tests.EditMode.SetupCommandHandlerTests`：15/15
  - `Logs/EditModeTests/editmode-20260630_232645.xml`
- `YC.Tests.EditMode.ExploreLocationCommandHandlerTests`：26/26
  - `Logs/EditModeTests/editmode-20260630_232947.xml`
- `YC.Tests.EditMode.MoveCityCommandHandlerTests`：14/14
  - `Logs/EditModeTests/editmode-20260630_233116.xml`
- `YC.Tests.EditMode.PresentationSelectionControllerTests`：6/6
  - `Logs/EditModeTests/editmode-20260630_233156.xml`

## 当前兼容边界

- `PendingChoiceState` 仍由 `CardFlowStateAdapter.OpenPendingSession()` 双写，服务旧快照、存量 UI 或调试输出。
- `GameState.HasPendingChoice()` 仍同时识别 `PendingCardSessionState` 和 `PendingChoiceState`。
- 新增事件选择 UI 或结算逻辑不应再直接以旧 `PendingChoiceState` 作为主事实源，应通过 `CardFlowStateAdapter.GetPendingChoiceView(...)` 或直接使用 `PendingCardSessionState`。
- 复杂事件参数仍沿用 `StringKeyValuePair` 列表传递；未来如果出现多段目标或更复杂分支，再评估强类型封装。
