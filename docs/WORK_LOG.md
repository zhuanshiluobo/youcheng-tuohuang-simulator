# 工作记录

## 2026-06-09

### 本轮已完成

- 补充四人地图结构化定义，替换原有四人地图 placeholder 的核心数据入口。
- 增加四人地图 EditMode 测试，覆盖节点数量、航道连通、区块归属、可停靠点和关键路径查询。
- 增加回合 UI 原型：
  - 右下角回合结束按钮。
  - 底部 START 到 FINAL 回合轨道。
  - 白色圆形指示物每次结束回合后后退一格。
  - 到达 FINAL 时弹出结束提示，并可返回开始页面。
- 增加移动城市规则模块：
  - 初始落点限制为四人地图绿色资源点：`G-01`、`A-01`、`A-02`、`B-01`、`B-02`、`C-01`。
  - 每回合每名玩家只允许移动一次移动城市。
  - 移动目标必须与当前地点相邻。
  - 移动目标不能已有其他玩家移动城市停靠。
  - 暂未接入移动费用机制。
- 增加移动城市 UI 原型：
  - 游戏开始提示选择初始落点。
  - 点击合法资源点放置移动城市。
  - 点击移动城市后高亮显示本回合可到达地点。
  - 点击高亮地点移动城市。
  - 移动城市暂用运行时生成的蓝色占位精灵表示。

### 修改和新增文件

- `Assets/YC/Domain/Maps/StaticMapDefinitions.cs`
- `Assets/YC/Domain/State/GameState.cs`
- `Assets/YC/Domain/Rules/PhaseFlow.cs`
- `Assets/YC/Application/Setup/SetupCommandHandler.cs`
- `Assets/YC/Application/Gameplay/MoveCityCommandHandler.cs`
- `Assets/YC/Presentation/RoundTrackerController.cs`
- `Assets/YC/Presentation/MobileCityInteractionController.cs`
- `Assets/Scenes/SampleScene.unity`
- `Assets/YC/Tests/EditMode/FourPlayerMapDefinitionTests.cs`
- `Assets/YC/Tests/EditMode/MoveCityCommandHandlerTests.cs`
- `Assets/YC/Tests/EditMode/SetupCommandHandlerTests.cs`
- `Assets/YC/Tests/EditMode/MapPathSearchServiceTests.cs`

### 测试覆盖点

- 四人地图：
  - 节点数量。
  - 航道双向连通。
  - 区块包含指定资源点。
  - 可停靠点查询。
  - 关键路径查询。
- 初始入场：
  - 四人地图允许放置在绿色资源点。
  - 四人地图拒绝放置在非允许初始点。
- 移动城市：
  - 成功移动到相邻资源点。
  - 拒绝移动到非相邻资源点。
  - 拒绝同回合重复移动。
  - 拒绝移动到被其他玩家移动城市占据的资源点。

### 已验证

- 使用离线 C# 编译检查验证 Domain、Application 和 EditMode 测试代码可编译。

### 未运行或未完成

- 尚未运行 Unity EditMode 测试：当前环境没有找到可用的 Unity Editor 可执行文件。
- `ProjectSettings/QualitySettings.asset` 和若干素材文件出现在工作区变更中，本轮没有将其纳入提交范围。
- Debug 模式下“所有地点全部高亮显示”的改动尚未落地。

### 仍未确认的规则细节

- 四人地图节点、航道、区块仍依据当前素材做结构化近似录入，后续需要用规则书或高清地图逐点校准。
- 移动城市费用机制已接入命令结算，费用为 3 源石。
- 多玩家完整入场顺序和当前行动玩家推进逻辑仍未完成。
- Presentation 层移动城市 UI 原型已收敛为提交命令，状态修改由 Application/Domain 结算。
