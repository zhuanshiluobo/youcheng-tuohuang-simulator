# EditMode 测试治理

本文档记录 Lua Effect 树改造的测试分层与治理。第 1 至原有章节保留 NMC-001 历史基线，并非当前通过率；2026-09-20 已实现 Lua Host、持久 Effect 树、回合主链及部分内容迁移，最新结果见文末。测试以外部行为、持久合同、权限和事务为准。

## 1. 基线与复测命令

基线和最终复测均使用同一个 Unity Editor、同一个项目路径和同一个 runner。XML 中的 `<test-run>` 是数字口径，不能用静态测试属性数量替代。

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 `
  -UnityPath "D:\2022.3.62f1c1\Editor\Unity.exe" `
  -ProjectPath "G:\NoMadCity" `
  -TestPlatform EditMode `
  -OutputDirectory "G:\NoMadCity\Logs\EditModeTests\NMC-001-final-20260915" `
  -LogFile "G:\NoMadCity\Logs\EditModeTests\NMC-001-final-20260915\editmode-final.log" `
  -ResultsFile "G:\NoMadCity\Logs\EditModeTests\NMC-001-final-20260915\editmode-final.xml"
```

| 口径 | 发现 | 通过 | 失败 | 不确定/跳过 | XML 测试耗时 |
|---|---:|---:|---:|---:|---:|
| 基线 | 1029 | 1024 | 5 | 0 | 7.3004491 s |
| NMC-001 最终 | 961 | 956 | 5 | 0 | 7.1057118 s |
| 变化 | -68 | -68 | 0 | 0 | -0.1947373 s |

最终发现数减少 `68 / 1029 = 6.61%`。本轮安全合并不足目标 15%，没有为达到数字目标而删除独特风险覆盖或把互不相关的测试压成失去定位粒度的单体测试。

基线与最终均由 runner 报告 Unity 退出码 2，这是因为 XML 中仍有失败；XML 结果表明失败集合完全相同，没有新增失败。

## 2. 测试资产清单

当前 XML 发现了 105 个测试 fixture 类。测试目录共 108 个 `.cs` 文件，其中 105 个是 fixture，另有既有的 `FacilityCardDatabaseSetUpFixture.cs`、`ViewerPrefabTestUtility.cs` 和本轮新增的 `EditModeTestCaseRunner.cs` 等测试基础设施文件。以下分类按 fixture 类归类；文件数与类数一致。

| 类别 | 文件数 | 基线发现/通过/失败/跳过 | 基线耗时 | 最终发现/通过/失败/跳过 | 最终耗时 |
|---|---:|---:|---:|---:|---:|
| Domain 规则 | 23 | 210 / 209 / 1 / 0 | 0.204176 s | 197 / 196 / 1 / 0 | 0.189318 s |
| Application/Command | 16 | 188 / 188 / 0 / 0 | 0.136607 s | 175 / 175 / 0 / 0 | 0.138114 s |
| Interaction/Presentation | 36 | 361 / 361 / 0 / 0 | 1.008708 s | 335 / 335 / 0 / 0 | 0.898440 s |
| EditorAsset | 23 | 175 / 171 / 4 / 0 | 5.519614 s | 170 / 166 / 4 / 0 | 5.469847 s |
| Network/Multiplayer | 7 | 95 / 95 / 0 / 0 | 0.123060 s | 84 / 84 / 0 / 0 | 0.118795 s |

### Domain 规则（23 个文件，基线 210 / 最终 197）

`CharacterCardFlowTests.cs`、`CharacterCardOptionQueryServiceTests.cs`、`CharacterCardRemainingEffectsTests.cs`、`FacilityBuildCostServiceTests.cs`、`FacilityCardDatabaseInitializationContractTests.cs`、`FacilityEntryEffectServiceTests.cs`、`FacilityInfluenceEffectServiceTests.cs`、`FederalCouncilEffectTests.cs`、`FinalScoringServiceTests.cs`、`InfluenceMoveRuleTests.cs`、`InfluencePlacementRuleTests.cs`、`InfluenceQueryServiceTests.cs`、`InfluenceRemovalServiceTests.cs`、`InfluenceServiceContractTests.cs`、`InfluenceServiceTests.cs`、`MapPathSearchServiceTests.cs`、`MapQueryServiceTests.cs`、`ResourceCollectionServiceTests.cs`、`ResourceSaleServiceTests.cs`、`ResourceSetTests.cs`、`SpecialActionDomainTests.cs`、`SpecialActionServiceTests.cs`、`TravelCostServiceTests.cs`。

### Application/Command（16 个文件，基线 188 / 最终 175）

`BuildFacilityCommandHandlerTests.cs`、`CommandGatewayTests.cs`、`CommandPhasePolicyTests.cs`、`DeclareCityStyleCommandHandlerTests.cs`、`ExploreLocationCommandHandlerTests.cs`、`GameSessionTests.cs`、`GameplayCommandHandlerTests.cs`、`MainActionBudgetAndSpecialActionLifecycleTests.cs`、`MoveCityCommandHandlerTests.cs`、`MoveCityPreconditionTests.cs`、`ResourceCollectionCommandHandlerTests.cs`、`ResolveFacilityEffectCommandHandlerTests.cs`、`ScoreTrackRefreshingCommandPortTests.cs`、`SetupCommandHandlerTests.cs`、`SpecialActionCommandBoundaryTests.cs`、`SpecialActionCoreIntegrationTests.cs`。

### Interaction/Presentation（36 个文件，基线 361 / 最终 335）

`ActionLogViewerControllerTests.cs`、`CardPointerInteractionTests.cs`、`CharacterCardEffectInteractionUiCoordinatorTests.cs`、`CharacterCardPanelPresenterTests.cs`、`CharacterHandPanelUiTests.cs`、`CharacterMapInteractionCoordinatorTests.cs`、`DeploymentInteractionFeedbackTests.cs`、`ExplorationEventPresenterTests.cs`、`FacilityEffectInteractionUiCoordinatorTests.cs`、`FacilityEffectPendingChoicePresenterTests.cs`、`FontUtilityTests.cs`、`GameSettingsMenuControllerTests.cs`、`InfluenceActionPresenterTests.cs`、`InteractionFlowCoordinatorTests.cs`、`InteractionRouterTests.cs`、`LocalPlayerResolverTests.cs`、`MapCameraGeometryTests.cs`、`MapInteractionRouterTests.cs`、`MapPieceVisualTests.cs`、`MobileCityColorTests.cs`、`MobileCityInteractionArchitectureTests.cs`、`MobileCityInteractionControllerTests.cs`、`PresentationReuseArchitectureTests.cs`、`PresentationSelectionControllerTests.cs`、`ResourceCollectionPresenterTests.cs`、`RightCardSmokeStateFactoryTests.cs`、`RoundTrackerControllerTests.cs`、`RuntimeUiLayoutTests.cs`、`SpecialActionInteractionUiCoordinatorTests.cs`、`SpecialActionPreviewUiTests.cs`、`StartMenuClipboardTests.cs`、`TabletopBoundsContributorTests.cs`、`TabletopCanvasLayoutTests.cs`、`TabletopPointerClassifierTests.cs`、`TabletopViewportNavigationBoundsTests.cs`、`TurnActionPresenterTests.cs`。

### EditorAsset（23 个文件，基线 175 / 最终 170）

`CardVisualCatalogEditorAssetTests.cs`、`CityStyleDeclarationPreviewEditorAssetTests.cs`、`CityStyleSpecialActionCatalogEditorAssetTests.cs`、`EffectDialogLayoutEditorAssetTests.cs`、`EventCharacterCardCatalogEditorAssetTests.cs`、`EventChoiceDialogEditorAssetTests.cs`、`FacilityCardCatalogEditorAssetTests.cs`、`FacilityCardMetadataTests.cs`、`FourPlayerMapDefinitionTests.cs`、`GameplayDialogEditorAssetTests.cs`、`GameplayInteractionHudEditorAssetTests.cs`、`GameSettingsMenuEditorAssetTests.cs`、`InformationPanelsEditorAssetTests.cs`、`MapFeedbackVisualEditorAssetTests.cs`、`MapRouteDisplayDefinitionTests.cs`、`MapViewEditorAssetTests.cs`、`ResourceCounterBoardEditorAssetTests.cs`、`RoundTrackerEditorAssetTests.cs`、`SecondaryLayoutEditorAssetTests.cs`、`SpatialLayoutEditorAssetTests.cs`、`StartMenuEditorAssetTests.cs`、`UiThemeCatalogEditorAssetTests.cs`、`ViewerEditorAssetTests.cs`。

### Network/Multiplayer（7 个文件，基线 95 / 最终 84）

`LocalhostAutoplayRunnerTests.cs`、`MultiplayerLaunchRegressionTests.cs`、`NetworkCommandDispatcherTests.cs`、`NetworkRoomServiceConnectionTests.cs`、`NetworkRuntimeEditorAssetTests.cs`、`SteamMultiplayerCoreTests.cs`、`WaitingRoomReadinessTests.cs`。

## 3. 行为覆盖矩阵

| 类别 | 被保护的不变量 | 最低成本权威层 | 重复候选 | 本轮合并方式 | 保留理由 |
|---|---|---|---|---|---|
| Domain 规则 | 前置条件、路径/资源边界、事务原子性、失败不写回 | Domain service/rule | 上层对同一规则逐项复述的断言 | 只合并同一规则下的输入表；不删除独特失败路径 | Effect 树迁移后仍需证明领域行为不变 |
| Application/Command | 权限、阶段、命令参数、事件顺序、提交后状态 | Command handler / application service | 同一 handler 只替换枚举、资源或目标 ID 的复制用例 | 参数行集中到一个测试并为每行附上下文 | 命令边界是 Host 权威校验的最低成本位置 |
| Interaction/Presentation | 选择状态、取消/返回、等待态、防重复提交、ViewModel 绑定 | Application 结果 + Presentation workflow | UI 逐项重述 Domain 数值规则的测试 | 仅合并同构枚举/ID/分支表；保留交互时序测试 | UI 不能成为规则权威，也不能丢失用户可见状态 |
| EditorAsset | 稳定 GUID、源/目录一致、Prefab 引用、无缺脚本、构建就绪 | Editor builder/catalog validator | 每个资源重复写同一 generic gate | 同一 gate 的资源路径表集中执行；资产特有拓扑断言保留 | 资源漂移是构建期风险，不能由运行时测试替代 |
| Network/Multiplayer | Host 权威、座位与隐私、幂等、断线/邀请/恢复 | Network/session service | 同一策略只替换角色、地址或路径的复制用例 | 参数行集中执行，角色和路径保留上下文 | 隐私、故障恢复和跨会话状态是高价值独特覆盖 |

## 4. 首轮合并记录

新增共享工具 [EditModeTestCaseRunner.cs](../../Assets/YC/Tests/EditMode/EditModeTestCaseRunner.cs)。它逐行执行输入表，收集每行 `AssertionException`，最终一次性报告，并把玩家、ID、资源、分支、路径或枚举值写入失败上下文；所有原有断言仍执行。

| 测试文件 | 合并组 | 基线→最终 | 替代位置 |
|---|---|---:|---|
| `BuildFacilityCommandHandlerTests.cs` | 佣兵无对手、支付模式 | 5→2 | 同名 `[Test]` 中的两张数据表 |
| `CharacterCardPanelPresenterTests.cs` | 玩家颜色、未知/不支持卡牌 | 6→2 | 同名 `[Test]` 数据表 |
| `DeclareCityStyleCommandHandlerTests.cs` | 六种样式、四种旋转 | 10→2 | 同名 `[Test]` 数据表 |
| `FacilityCardCatalogEditorAssetTests.cs` | 场景 bootstrap | 2→1 | `Scenes_InheritConnectedBootstrapWithoutFileMutation` |
| `FacilityCardDatabaseInitializationContractTests.cs` | 三种错误奖励契约 | 3→1 | `Initialize_RejectsIncorrectRequiredReward` |
| `FacilityEffectInteractionUiCoordinatorTests.cs` | 两个佣兵分支返回 | 2→1 | `MercenaryHeadquarters_EscapeReturnsToBranchChoiceWithoutSubmitting` |
| `FacilityEntryEffectServiceTests.cs` | 六种选择设施 | 6→1 | `ResolveChoice_...` 同名数据表 |
| `GameSettingsMenuEditorAssetTests.cs` | 两个场景 | 2→1 | `Scenes_...` 同名数据表 |
| `InteractionRouterTests.cs` | 缺少 ID、缺少原因 | 6→2 | 两个同名 `[Test]` 数据表 |
| `MapCameraGeometryTests.cs` | 三种宽高比、三种缩放 | 6→2 | 两个同名 `[Test]` 数据表 |
| `MobileCityInteractionArchitectureTests.cs` | 11 种工作流类型 | 11→1 | `WorkflowTypes_...` 数据表 |
| `ResourceSaleServiceTests.cs` | 两种无效出售输入 | 2→1 | 同名 `[Test]` 数据表 |
| `RoundTrackerControllerTests.cs` | 2/3 玩家数 | 2→1 | `RefreshFromState_FinalScoreDetailsSupportOneToFourPlayers` 数据表 |
| `RoundTrackerEditorAssetTests.cs` | 两个 Prefab 路径 | 2→1 | `EditorPrefab_HasNoMissingScripts` 数据表 |
| `ScoreTrackRefreshingCommandPortTests.cs` | 三种命令类型 | 3→1 | 同名 `[Test]` 数据表 |
| `SpecialActionServiceTests.cs` | 四种合法支付、三种非法支付 | 7→2 | 两个同名 `[Test]` 数据表 |
| `SteamMultiplayerCoreTests.cs` | ID、地址、角色、返回路径 | 17→6 | 六个同名 `[Test]` 数据表 |
| `TabletopViewportNavigationBoundsTests.cs` | 三种缩放 | 3→1 | 同名 `[Test]` 数据表 |
| `ViewerEditorAssetTests.cs` | 三个 Viewer Prefab | 3→1 | `ViewerPrefab_HasNoMissingScripts` 数据表 |

没有删除测试文件，没有改动生产代码，没有使用 `[Ignore]`，也没有减少原有断言。资产特有拓扑、GUID、哈希和构建门禁仍保持独立测试。

## 5. 基线失败与回归判定

以下 5 个失败在基线和最终 XML 中均存在，测试名和失败集合相同，因此按任务前既有失败处理，不计为 NMC-001 回归：

1. `CityStyleDeclarationPreviewEditorAssetTests.ProductionDialog_UsesRegistryViewAndContainsNoRuntimeUiConstruction`：现有生产源码仍包含门禁禁止的 `new GameObject`。
2. `FacilityCardCatalogEditorAssetTests.BuildReadiness_ValidatesCurrentSourceCatalogAndPrefab`：`FacilityCardCatalog` 源 SHA-256 已过期。
3. `FacilityCardCatalogEditorAssetTests.Source_IsEditorOnlyRetainsGuidAndMatchesCatalogHash`：期望 `5EBE...991F`，实际 `E7B7...FD07`。
4. `ResourceCollectionServiceTests.CollectResource_FromF02ToD03_OwnInfluenceOnD1DoesNotExemptD2Toll`：期望目标 ID `D`，实际 `D2`。
5. `RoundTrackerEditorAssetTests.ProductionPresentation_CreatesOnlyFiveApprovedDynamicGameObjects`：期望 5 个动态 `GameObject`，实际 8 个。

按分类，Domain 规则保留 1 个失败，EditorAsset 保留 4 个失败；Application/Command、Interaction/Presentation、Network/Multiplayer 均无失败。后续若任一失败消失后再次出现，应作为回归重新调查，不能永久视为基线豁免。

## 6. 后续治理规则

1. 新增 Effect 行为时，先在 Domain/Application 写权威行为测试，再补 Presentation 的可见状态或绑定测试；Presentation 不重复验证同一条资源、路径或权限规则。
2. 同一不变量的多个输入必须优先使用数据表；数据表每行保留稳定描述，失败信息不得只显示数组索引。
3. Prefab、卡牌和配置资产优先通过目录/清单驱动 generic gate；资产独有的拓扑、GUID、源哈希和构建门禁不合并。
4. 事务原子性、权限、隐私、序列化、幂等、断线恢复、邀请生命周期和失败后可重试状态属于受保护测试，不得仅因发现数下降而删除。
5. 不使用 `[Ignore]`、测试改名、恒真断言或减少断言来制造数字下降；每次瘦身必须能在替代测试或数据表中定位旧场景。
6. Unity 测试必须串行运行。启动前确认没有命令行包含 `G:\NoMadCity` 的 Unity 进程；不要仅凭 `Temp\UnityLockfile` 判定项目被占用。
7. 每轮改造都保存 XML 和日志，用同一 runner 比较发现、通过、失败、跳过和 XML 测试耗时；失败集合相同才可判定无新增失败。
8. 本轮任务范围的 `git diff --check` 通过。全仓检查仍会报告 Unity 运行期间由用户已有 Prefab 改动产生的空 YAML 字段尾随空格；这些无关文件未被本任务修改或清理。

## 7. NMC-003 沿用入口

- 数据表聚合入口：`Assets/YC/Tests/EditMode/EditModeTestCaseRunner.cs`。
- 现有可复用 fixture/builder：各测试类中的 `CreateActionState`、`CreateState`、`CreateContext`、`CreateFixture`，以及 `FacilityCardDatabaseSetUpFixture.cs`、`ViewerPrefabTestUtility.cs`。
- Effect 树迁移时应新增独立的 EffectSpec/状态 fixture，并让 Lua 只生成可断言的 spec；不要把 Domain 状态写入或 Unity 对象构造塞回测试 helper。
- 复测仍使用 `tools/Run-EditModeTests.ps1`，必须显式传入 `D:\2022.3.62f1c1\Editor\Unity.exe`、项目路径、`EditMode`、独立 XML/日志目录。

## 2026-09-20 分层审查与迁移回归

当前全量基线为 1098 项（1073 通过、25 失败、0 跳过），754.56 秒，来源 `Logs/architecture-20260920-baseline.xml`。修复后的 239 项扩大回归、45 项 UI 答案协议回归、41 项设施/特殊行动回归均通过；这些有交集，不能相加充当全量数字。最终串行全量结果另据 XML 更新。

### 最低成本权威层与保留理由

| 风险组 | 权威层与当前代表测试 | 上层只保留的合同 | 现阶段不能整体删减的原因 |
| --- | --- | --- | --- |
| Lua 沙箱、版本、最小 Effect 与持久内核 | Domain/Infrastructure，NMC003–NMC008、LuaRuntimeHost | 激活挂链、完成回调、故障不发布 | 字段白名单、版本冲突、零/多 Effect、子失败和恢复分别是不同风险 |
| 内容与规则行为 | Domain，NMC010–NMC014、角色/设施/样式规则 | 实际激活和答案协议各族代表 | 尚有七项能力和设施/样式 adapter，旧独特规则断言在迁移完成前仍必要 |
| 命令事务与回合 | Application，Gameplay、ResourceCollection、RoundLifecycle | 失败不写回、revision/receipt、防重复提交、最终计分 | 末人采集导入与轮末复位缺陷证明不能只测单个服务 |
| 权限、联机与恢复 | Domain/Application/Infrastructure，NMC009、NetworkCommandDispatcher、SteamMultiplayerCore | 按连接投影、Host 迁移与序列化合同 | 非目标玩家、观战者、收据重放和隐私不能互相替代 |
| 输入与展示 | Presentation，EffectInteractionContract、角色/设施/SpecialAction 协调器 | 真实按钮答案格式、草稿不改状态、取消、旧 UI 复用 | 真实 UI 已发现仅用字符串答案的规则测试漏掉数组编码错误 |
| 资源与引用 | EditorAsset，各目录/schema/布局与 GUID 完整性 | 只验证引用和布局，不复述结算规则 | Unity 资产 hash、Prefab 必需字段和拖动布局与规则测试不同 |
| 完整命令旅程与场景 | LocalhostAutoplayRunner、MultiplayerLaunchRegression；另有 PlayerJourney | 少量长路径、八回合终局和返回 | 基线两个慢 fixture 分别 527.20s / 208.06s；应继续分离慢套件，不能用这些测试代替真实玩家 UI |

本轮没有为“700”目标添加 Ignore、隐藏 fixture 或移除独特规则断言。旧测试的手动 Cleanup/EndAction 已改走主链夹具，保留特殊行动复位、联邦议会、资源、计分断言。十项角色预选改为 activation-only 表测试，旧数量弹窗的数量、价格、拖动合同保留。最终瘦身受 NMC-014A 未验收阻塞，不能宣布 NMC-015 完成。

基线分类重新按 XML 的 fixture 汇总（新增架构测试单列、慢流程单列）：展示 339 项/1.48s，Application 176 项/3.38s，资产 170 项/6.01s，旧 Domain 197 项/4.15s，新内核与 Lua 131 项/4.01s，联机合同 79 项/0.18s，两个完整流程 fixture 5 项/735.27s，其他边界 1 项/0.004s。分类彼此互斥，合计 1098。分类耗时为 fixture duration 相加，不含整个 runner 开销。

### 最终串行全量结果

最终串行完整 EditMode：**1116/1116 通过，0 失败、0 跳过，802.23 秒**，证据 `Logs/architecture-20260920-final3.xml`。全量基线 1098 项中 25 项失败已消除；本轮发现数净增 18。

| 风险组 | fixture | 发现/通过/失败/跳过 | fixture 耗时 |
| --- | ---: | --- | ---: |
| Interaction/Presentation | 36 | 350 / 350 / 0 / 0 | 1.40s |
| Application/Command | 16 | 176 / 176 / 0 / 0 | 5.44s |
| EditorAsset | 23 | 170 / 170 / 0 / 0 | 5.62s |
| Domain 规则 | 23 | 197 / 197 / 0 / 0 | 10.50s |
| 新增跨层合同/边界 | 2 | 3 / 3 / 0 / 0 | 0.01s |
| 完整流程慢套件 | 2 | 5 / 5 / 0 / 0 | 774.55s |
| 新架构/持久内核与 Lua | 14 | 136 / 136 / 0 / 0 | 4.48s |
| Network/Multiplayer | 5 | 79 / 79 / 0 / 0 | 0.16s |

最终发现数高于 700 的保留依据见上述逐风险组矩阵。不得用单次耗时差异声称性能优化收益。
