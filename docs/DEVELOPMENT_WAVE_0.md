# 当前工程进展

本文件记录 Unity 工程中已经落地的代码和资源。设计资料的统一入口见：

- `游城拓荒/项目总览.md`

## 已完成

### 工程结构

```text
Assets/YC/
  Domain/
  Application/
  Infrastructure/
  Presentation/
  Data/
  Editor/
  Tests/EditMode/
```

已建立程序集：

- `YC.Domain`
- `YC.Application`
- `YC.Tests.EditMode`

### Domain

已实现：

- 基础枚举：玩家颜色、资源类型、阶段、卡牌类型、命令类型、错误码、事件类型。
- 状态：`GameState`、`PlayerState`、地图运行状态、牌堆状态、待选择状态、日志状态。
- 命令：`IGameCommand`、`GameCommand`。
- 结果：`ValidationResult`、`CommandResult`。
- 事件：`GameEvent`。
- 地图：`GameMapDefinition`、位置、航道、区块定义。
- 地图查询：`IMapQueryService`、`MapQueryService`。
- 地图路径：`MapPath`、`MapPathSearchService`。
- 费用：`TravelCostService`。
- 阶段：`PhaseFlow`、`CommandPhasePolicy`。

### Application

已实现：

- `GameSession.Submit(GameCommand command)`。
- `IGameCommandHandler`。
- `SetupCommandHandler`：
  - `ChooseStartPlayer`
  - `ChooseInitialLocation`

### Presentation

已实现：

- `StartScene`：开始页。
- `StartMenuController`：开始按钮进入 `SampleScene`。
- `SampleScene`：默认显示四人地图。
- `MapDisplayController`：正交相机适配地图。

### Data / Editor

已导入：

- 三人地图贴图。
- 四人地图贴图。
- 16:9 开始页封面图。

已实现：

- `MapTexturePostprocessor`：地图贴图导入设置。

### Tests

已建立并通过用户本地 Unity EditMode 测试：

- `ResourceSetTests`
- `GameSessionTests`
- `MapQueryServiceTests`
- `MapPathSearchServiceTests`
- `TravelCostServiceTests`
- `PhaseFlowTests`
- `SetupCommandHandlerTests`

## 当前限制

- 三人地图规则数据仍是 placeholder，未录入真实数据。
- 入场流程只完成起始玩家和初始城市位置，未完成入场事件和初始资金。
- 开始页和地图页尚未接入真实 `GameSession`。
- 未实现日志详情、回放、存档。
- 未实现探索、建设、特殊行动、采集、收尾、最终计分。

## 下一步建议

1. 完成剩余主要行动：探索、建设、特殊行动。
2. 实现采集阶段：多资源点选择、路费支付、资源获取。
3. 实现收尾阶段：收尾效果结算、标记复位、起始玩家传递。
4. 补齐入场事件和多人入场顺序。
5. 建立 Debug 对局 UI。
