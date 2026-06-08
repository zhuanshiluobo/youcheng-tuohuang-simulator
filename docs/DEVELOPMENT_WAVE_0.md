# 第 0 波开发契约

本文记录当前 Unity 工程的第一批代码落点，供后续多 Agent 并行开发使用。

## 已建立目录

```text
Assets/YC/
  Domain/
    Commands/
    Events/
    Maps/
    Rules/
    State/
  Application/
    Sessions/
  Infrastructure/
  Presentation/
  Data/
  Tests/EditMode/
```

## 程序集边界

- `YC.Domain`：纯 C# 规则核心，`noEngineReferences=true`，不能引用 `UnityEngine`。
- `YC.Application`：整局编排、命令提交、日志和回放入口，依赖 `YC.Domain`。
- `YC.Tests.EditMode`：编辑器测试，覆盖规则核心和应用层。

## 共享契约

- 基础枚举：`PlayerColor`、`ResourceType`、`GamePhase`、`GameCommandKind`、`CommandErrorCode`、`GameEventKind`。
- 状态结构：`GameState`、`PlayerState`、`MapRuntimeState`、`DeckRuntimeState`、`PendingChoiceState`、`GameLogEntry`。
- 命令结构：`IGameCommand`、`GameCommand`、`ValidationResult`、`CommandResult`。
- 事件结构：`GameEvent`。
- 地图结构：`GameMapDefinition`、`MapLocationDefinition`、`MapRouteDefinition`、`MapRegionDefinition`、`IMapQueryService`。
- 地图路径：`MapPath`、`MapPathSearchService`，用于查找最短路径和枚举有限步数内的简单路径。
- 费用服务：`TravelCostService`，当前把航道路费建模为金券费用，并提供城市移动基础费用入口。
- 应用入口：`GameSession.Submit(GameCommand command)` 和 `IGameCommandHandler`。
- 入场命令：`SetupCommandHandler`，当前处理 `ChooseStartPlayer` 和 `ChooseInitialLocation`。

## 并行开发边界

- 地图 Agent：只扩展 `Assets/YC/Domain/Maps` 和地图相关测试。
- 状态机 Agent：只扩展 `Assets/YC/Domain/Rules` 和阶段相关测试。
- 命令流水线 Agent：新增具体 command handler 时应放在 `Assets/YC/Application` 或后续明确的 resolver 目录。
- UI Agent：只能读取 `GameState` 并提交 `GameCommand`，不能直接写规则状态。

## 当前验收口径

- `Domain` 不依赖 Unity 场景对象。
- 非法命令必须返回 `ValidationResult.Failure`，不能修改 `GameState`。
- 合法命令通过 handler/resolver 修改状态，并生成事件或日志。
- 资源与影响力后续必须有守恒测试。
- 地图图片只做表现层底图，规则判断必须依赖结构化地图数据。
- 当前地图结构仍是 placeholder，真实节点、航道、区块和费用需要后续从规则书/地图图面录入。
- 当前入场流程只处理起始玩家和初始城市位置，后续还需补齐入场事件与多人依次选择推进。
