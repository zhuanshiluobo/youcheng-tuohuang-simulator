# NMC-014A：Lua 新架构偏差审计与纠正

## 任务提示词

你负责纠正 `NMC-001` 至 `NMC-014` 实施后出现的架构偏差。当前代码虽然已经具备 Effect 树、Lua Host、Interaction、Candidate 和回合主链的外形，但多处仍由 C# 内容分支直接完成卡面规则，或用未写入 Lua API 文档的“大 Effect”把具体规则包回 C#。本任务不是继续增加适配层，而是依据两份 Lua 权威文档重新建立严格边界，并让生产路径真正按该边界运行。

本任务必须实施修复，不能只提交审计报告。开始实现前先复核下文列出的证据，因为工作区可能正在并行纠正雷蛇；不得覆盖用户已有改动。

## 权威依据与冲突规则

完整阅读并遵守：

1. `AGENTS.md`
2. `prompt/00-共同执行约束.md`
3. `docs/设计/Lua效果树与回合主链设计.md`
4. `docs/设计/Lua效果系统API初稿.md`
5. `docs/设计/Lua运行时选型ADR.md`
6. `NMC-004` 至 `NMC-014` 的提示词、完成记录和当前实现

`docs/设计/命令与效果系统准备规范.md` 已废弃，不得作为实现或保留旧路径的依据。发生冲突时，以两份 Lua 文档为准。

以下用户确认语义是本任务硬约束：

- `Running` 表示执行节点主体；主体完成后如仍有交互、Event 响应、子 Effect 或其他 blocker，节点进入 `Blocked`。
- `Blocked` 承担等待/完成中语义，解除后进入 `Completed` 或 `Failed`，不新增 `Completing`。
- 子节点 `Failed` 只解除 blocker，不自动向上传播；父执行器显式解释结果。
- Lua 返回数组中的 Effect 是当前结算节点下的有序子节点，不额外创建无语义的 Sequence 节点。
- 收尾窗口没有任何实际收尾 Effect 时必须自动完成并推进；不能等待玩家点击“结束回合”。
- 玩家行动窗口仍要求玩家显式结束；“行动窗口显式结束”和“空收尾窗口自动结束”不得共用一个笼统的完成交互。
- 开放红区引发的企业升级不在本任务范围。

## 当前结论

当前实现不可视为符合新架构。至少存在以下已经确认的偏差；执行者必须把它们作为起始审计清单，不得仅处理雷蛇或护航调度中心两个个例。

### A. Lua 公共 API 超出文档并被用作内容隧道

| 已确认位置 | 当前行为 | 不符合点 | 必须处理 |
| --- | --- | --- | --- |
| `MoonSharpLuaRuntimeHost.InstallEffectConstructors`、`LuaEffectTypeCatalog` | 暴露 `Effect.GainResourceToOpponents` | 文档白名单没有该构造函数 | 用文档内通用 Effect式表达，或先修订权威文档并获得用户确认；本任务不得自行扩展白名单 |
| 同上、`CharacterCardLuaCatalog` | 暴露并统一返回 `Effect.CharacterAbility` | Lua 只传 `abilityId`，具体卡面效果仍在 C# 分流 | 删除该 Lua 公共构造函数；改用文档中的 `ResolveCharacterCardEffect` 固有入口和具体 Lua Effect式 |
| 同上、`CityStyleLuaCatalog` | 暴露 `Effect.CityStyleSpecialAction`、`Effect.GrantMainActions` | 文档未定义这两个 Lua 构造函数；`operation` 把内容语义传回 C# | 用 `CityStyleSpecialActionActivated` 返回文档内通用 Effect；额外行动通过已定义的 `ExecuteMainAction`/行动预算固有合同表达，不保留私有构造函数 |
| 同上、`FacilityLuaCatalog` | 暴露 `Effect.FacilityEntry({ operation = p.effectId })` | 具体设施规则通过 `effectId/operation` 回到 C# behavior switch | 删除该 Lua 公共构造函数；设施 Lua 直接返回资源、影响力、移动、探索、建设等通用 Effect式 |

不得采用以下伪修复：

- 只把 `CharacterAbility`、`FacilityEntry`、`CityStyleSpecialAction` 改名成文档中某个 API，内部仍按具体卡牌/设施/样式 ID 分支。
- 给这些私有构造函数补一段本地注释或测试后继续保留。
- 在 Lua 中只返回 `operation`、`behaviorFamily` 或具体内容 ID，让一个 C# mega executor 决定完整规则。
- 保留同一具体规则的 Lua 实现和 C# fallback 双路径。

### B. 文档内首批 API 与当前可调用合同不闭合

当前 Lua Host、`LuaEffectSpecCompiler`、`EffectRegistry` 和执行器注册并非同一份可证明一致的白名单：

- `Effect.Condition` 已有部分内核实现，但 Lua Host/编译器没有对应构造和嵌套 Effect 编译路径。
- `Effect.MoveCity`、`Effect.RevealEventCard`、`Effect.ResolveCharacterCardEffect`、`Effect.OpenPlayerTaskGroup` 是现有生产内容和首批合同需要的 API，但 Lua 不能调用。
- `Candidate.Add`、`Candidate.Remove`、`Candidate.Intersect` 没有安装到 Lua 全局；`CandidateSetBuilding` 没有形成完整的 Lua 补丁分发链。设施和城市样式仍在 C# 构造最终候选或把最终候选列表塞给 Lua。
- Lua 的 `Effect.PlaceInfluence` 编译器当前强制 `targetSlotId`，但文档明确允许省略唯一目标，由 Effect 在执行时建立候选和交互。这样 Lua 无法表达雷蛇/护航调度中心所需的两个连续独立放置节点。
- Lua 资源 Effect 当前用一套私有 `recipient/resourceType/amount` 简化协议，并限制目标只能是当前调用玩家；为了给对手资源又增加了未记录构造函数。必须按文档统一 PlayerRef、付款方/接收方及候选/费用合同，不能继续用特例绕开。

建立一份机器可检查的端到端矩阵。每个实际暴露给 Lua 的符号必须同时满足：

1. 存在于权威文档白名单，并已有足够的单项合同；
2. Host 只接受文档字段、类型和上限；
3. `LuaEffectSpec` 能无损表示；
4. 编译器能生成归一化 `EffectSpec`；
5. 正式共享 `EffectRegistry` 一定注册对应执行器；
6. 执行器的 `atomic/intrinsicFlow` 类型、交互、Event、失败和恢复语义符合文档；
7. 至少一项端到端测试从真实 Lua 源码跑到节点终态。

任何一层缺失时，该符号不得继续暴露。文档列出但尚未被当前内容使用、且单项合同仍标注待填写的 API，不要求为凑数量一次性实现；但现有内容所必需的首批 API 不能再用未记录 mega effect 代替。

### C. 具体内容仍由 C# 决定

#### C1. 角色牌

`CharacterCardLuaCatalog` 为十项能力生成同一种脚本，只返回 `Effect.CharacterAbility`；`CharacterAbilityCatalog` 再按 ability ID 选择 C# executor。`CharacterAbilityEffectExecutor` 中仍存在角色专用交互、资源直接加减、`ResourceSaleService`、`CityMovementService`、`InfluenceService`、设施牌堆操作、弃牌/手牌移动和购买分支。

雷蛇策略即使已经在 C# executor 中改成两个 `PlaceInfluence` 子节点，仍不算完成迁移：具体卡面组合必须由雷蛇 Lua handler 返回两个独立的 `Effect.PlaceInfluence`。每个节点都不预先固定目标，第二个节点只能在第一个进入 `Completed/Failed` 后基于最新状态生成候选。

逐项迁移十项角色能力。C# 只保留角色牌翻开、使用权限、双发顺序、卡区移动、完成标记等文档规定的通用固有流程；不得再按具体 ability/card ID 实现卡面奖励、费用、目标或组合步骤。

#### C2. 设施牌

`FacilityLuaCatalog` 除资源奖励外统一返回 `FacilityEntry(operation = effectId)`；`FacilityBehaviorFamilyResolver`、`FacilityEntryEffectExecutor.ExecuteBehavior` 和相关 service 再按内容/行为族生成候选与执行组合。这仍是设施内容的 C# 权威实现。

特别检查 `building_037` 护航调度中心：当前 `DeployInfluences` 先在一个 Interaction 中同时选择两个槽位，再生成两个已经固定目标的放置子节点。这不等价于两个连续的放置 Effect。正确结构必须是父设施 Lua handler 返回两个有序 `Effect.PlaceInfluence`；第一次完成后，第二次才按最新供应、地图和候选补丁建立自己的 Interaction。第一个位置自然不能再次成为第二个合法位置，无需由设施 executor 维护组合选择。

逐项迁移所有设施入场/收尾内容。允许 C# 保留建设固有流程、设施实例身份和真正通用的原子 Effect；不得保留以具体 `effectId`、facility ID 或 behavior family 为入口的完整规则执行器。

#### C3. 城市样式与事件牌

- `CityStyleLuaCatalog` 当前只选择 `place_influence`、`replace_influence`、`composite_move`、`free_move` 等 operation；具体目标、阶段和组合仍在 `CityStyleSpecialActionEffectExecutor.ExecuteScriptStep`。把具体样式 Effect式迁入 Lua，C# 只保留通用特殊行动激活、预算/标记固有步骤和文档内通用 Effect。
- `CityMoveEffectExecutor.RegisterCityMoveContinuation` 当前用 C# Event handler 检查资源点并直接创建事件牌 Effect；文档要求 `CityMoveCompleted` 由 Lua 观察并按需返回 `Effect.RevealEventCard`。把该内容判断迁到版本化 Lua handler。
- `EventCardLuaCatalog` 已较接近目标，但仍使用未记录的 `GainResourceToOpponents`，且内容生成器以 C# `EventEffectKind` switch 决定脚本。消除未记录 API；若保留脚本生成器，必须证明它只是版本化内容的确定性编译/装载步骤，运行时没有第二套按卡 ID 结算的 C# 路径。

### D. 主链和命令入口没有形成一棵树

当前 `MoveCity`、`Explore`、`DeployInfluence`、`DispatchInfluence`、`UseCharacterCard`、`UseSpecialAction` 等命令使用 `TryCreateRoot/CreateRoot` 创建独立根节点；建设先由 `BuildFacilityService` 直接提交，再另建一个设施入场根节点；城市样式宣告也仍是直接 service 路径。它们没有作为当前 `player_action_window` 执行 Effect 的子节点，因此主链不能用父子 blocker 证明“当前玩家的所有行动子树已经结束”。

要求：

1. 为当前活动玩家窗口提供唯一、受校验的子 Effect 挂载入口。
2. 主要行动、快速行动、角色牌、特殊行动及其全部后续都挂在该窗口下面；稳定 `parentEffectId` 可从快照恢复。
3. 非当前窗口玩家、错误行动轮、已有不可并发交互或预算不合法时，不能创建游离根节点。
4. 建设、探索、移动、部署、调度、特殊行动和城市样式宣告都必须有文档内固有入口 Effect；命令层只提交意图和稳定 ID，不先直接改状态再补一个效果节点。
5. `EndAction` 只申请完成当前行动窗口；只有预算合同满足且该窗口的所有阻塞子树终结后才能推进。
6. 移除或停用被新入口替代的直接 service 推进、独立 root 和双结算路径。

### E. 主链完成语义错误

`RoundExecutionService.IsManualNode` 当前把 `CharacterCover`、`PlayerActionWindow`、`Collection` 和 `PlayerCleanupWindow` 都归为同一类，并由 `ExecuteMainlineStage` 对它们统一创建 `round.mainline.complete:*` Interaction。这导致空收尾、采集甚至行动过程中出现无业务含义的“完成选择”。现有 `NMC005RoundExecutionEditModeTests.FullRound_CompletesPerPlayerWindowsAndCreatesNextRound` 还把逐个调用 `EndCurrentPlayerWindow` 结束收尾写成正确期望，必须改掉。

按节点类型拆开实现：

- `player_action_window`：等待挂载的行动子树；满足预算后仍由玩家显式 `EndAction`。
- `collection`：建立/记录各玩家采集子任务，全员完成后自动推进，不创建全局“确认完成”Interaction。
- `player_cleanup_window`：进入节点即发布一次 `PlayerCleanupStarted`，把 Lua 返回项挂到该玩家收尾节点；若没有处理器或返回空数组，立即自动完成；有必需 Effect 时等待其子树，全部结束后按规则自动结束。只有文档明确允许玩家排序时才创建排序/选择下一个交互。
- `character_cover`：继续使用每玩家盖放子任务屏障，不复用通用完成确认。
- `round_ended`：执行回合级复位、起始玩家覆盖和下一回合创建，不把这些操作藏在“最后一名玩家点击结束收尾”的处理器里。

当前 `EndCleanupWindow` 还会调用 `CharacterCardService.BeginCleanupEffects`，并在最后一名玩家处直接做全局清理；`RunCityStyleCleanupEffects` 又把城市样式清理建立成独立根节点，而 `PlayerCleanupStarted` 的 Lua observer 已经可能生成同类清理子节点。必须消除这类重复入口：所有玩家收尾内容只从对应收尾 Event 挂树一次，回合级复位只由 `round_ended` 执行一次。

补齐文档要求但当前缺失的生命周期 Event，至少包括 `PlayerActionWindowCompleted`、`PlayerCleanupCompleted`、`FinalScoringStarted` 和当前架构实际需要的 `PlayerFinalScoringStarted`。Event 必须在文档规定时点只发布一次，响应子树仍阻塞来源节点终态。

### F. 内核语义和组合注册需要复核

至少修复并验证：

1. `EffectTreeExecutor.Fault` 当前按故障节点向下标记后代，而文档要求子节点发生不可恢复故障时沿父关系把所属 Effect 根标为 `Faulted`，运行时进入 `paused_fault`；`Faulted` 不得作为普通 `Completed/Failed` 终态释放父 blocker 或启动下一个兄弟。
2. `Completed/Failed` 子节点正常解除 blocker；父节点对失败的解释必须显式。不得因为修复 fault 传播而重新引入普通失败自动上抛。
3. Lua 数组产生有序兄弟节点；前一项 `Failed` 后普通有序式仍可执行下一项。条件式只有左侧首个失败停止右侧，右侧失败不自动改写条件节点结果。
4. 无订阅者或 Lua 返回空数组时，不创建空壳节点，来源节点可在同一推进周期完成。
5. `EffectCompleted` 仍是终态前的最后响应窗口；每节点只分发一次，响应子节点挂在来源节点下。
6. 建立唯一的正式 Effect 组合/注册入口，避免生产、自动跑局、默认构造器和测试各自拼出不同 `EffectRegistry`。启动时对“Lua Host 暴露类型—编译器类型—Registry executor”执行一致性校验，缺项 fail-fast。
7. Candidate 补丁顺序固定为文档比较键；Lua 补丁不能突破 Host 建立的 `candidatePoolIds`，回答时按最新 revision 和最终候选复验。

## 多人主链专项审查

此前主要用单人模式观察不能作为主链正确性的证据。单人局会掩盖玩家顺序快照、跨玩家窗口串行、采集屏障、逐玩家收尾、回答权限和起始玩家轮转错误。

必须至少覆盖 2、3、4 人，且断言：

- 固定主链节点数为 `3P + 4`，玩家顺序取创建回合时快照，恢复时不重算。
- 两个行动轮均严格按快照顺序；玩家行动 Effect 的祖先链能追溯到正确玩家窗口。
- 采集允许玩家任意顺序提交，但只在全员完成后推进。
- 所有玩家都没有收尾 Effect 时，各收尾节点连续自动通过，不产生任何完成确认 Interaction，并直接进入 `RoundEnded/下一回合`。
- 只有部分玩家有收尾 Effect 时，只阻塞对应玩家节点；其他玩家空窗口自动通过。
- 一个玩家有多个可排序收尾 Effect 时，只由该玩家回答排序/选择交互；完成后才进入下一玩家。
- 最后一名玩家收尾完成后只执行一次回合复位和下一回合创建；旧回合不得遗留 open Interaction 或非终态 Effect。
- 非当前玩家不能结束窗口、回答私有 Interaction 或挂载行动子 Effect。

可保留一项单人开发冒烟，但不得用它替代以上多人权威测试，也不得把只在单人局成立的快捷路径写进生产代码。

## 实施顺序

按以下顺序工作，避免一边迁移内容一边继续扩大错误 API：

1. 冻结并测试 Lua API 白名单，列出“文档存在/Host 暴露/编译器支持/Registry 注册/生产调用”矩阵。
2. 修复 Effect 内核的 fault、子节点、Event 完成窗口和统一 Registry 一致性。
3. 修复主链节点类型语义、行动窗口子树挂载和空收尾自动推进；先用 2/3/4 人无内容场景验收。
4. 补齐现有内容必需的文档内首批 API 与 Candidate Lua 补丁链。
5. 迁移角色牌、设施、城市样式和事件牌中的私有 mega effect/operation tunnel。
6. 逐项切断 C# 内容 ID/behavior family 执行分支和独立 root/direct service 入口。
7. 合并测试、运行完整回归，最后输出残留零引用/真实保留理由。

不得在第 1 步尚未冻结白名单时继续增加新 Lua 构造函数；不得在第 3 步尚未通过多人空内容主链时用自动跑局结果宣称主链正确。

## 测试与测试精简

新增或重组一组 `NMC014A` 架构一致性测试，优先使用 `TestCaseSource` 和场景表，不按每张牌复制 fixture。至少覆盖：

- Lua 全局 API 精确白名单：未记录符号不可见，禁止动态字段和直接 C#/Unity 对象。
- 每个已暴露构造函数的真实 Lua → 规范化 → 编译 → Registry → 执行/阻塞 → 终态闭环。
- `PlaceInfluence` 无固定目标时生成自己的 Interaction；两个有序放置的第二个候选基于第一次提交后的状态。
- 雷蛇策略和护航调度中心分别产生两个真实、有序、可恢复的放置子 Effect，而不是一个二选位 Interaction 或 C# 原子组合。
- 角色、设施和城市样式 Lua handler 不再返回已禁止的 mega effect；C# 不按具体内容 ID 结算规则。
- 行动 Effect 的父链归属、显式 EndAction、错误玩家拒绝和恢复幂等。
- 2/3/4 人空收尾自动推进、部分玩家有收尾、多个收尾排序和下一回合唯一创建。
- 子节点 `Failed` 与 `Faulted` 的不同传播，条件式左右语义，完成响应窗口只分发一次。
- Candidate Lua Add/Remove/Intersect 的稳定顺序、越池拒绝、revision 过期拒绝和回答复验。
- Host 保存/恢复后不重复执行 Lua、不重复扣费、不重复建子节点或 Event。

同步审查并改写已经固化错误合同的测试，尤其是 NMC005、NMC006、NMC007、NMC010、NMC013、NMC014 以及角色/设施旧 service 测试。保留最低成本层的一份权威行为测试；catalog 完整性用数据驱动表覆盖。不得只新增一批测试而保留等量旧调用细节测试。

使用用户指定 Unity：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 `
  -UnityPath "D:\2022.3.62f1c1\Editor\Unity.exe" `
  -ProjectPath "G:\NoMadCity" `
  -TestPlatform EditMode
```

先运行 NMC014A 及受影响模块过滤器，再运行一次完整 EditMode。读取 XML 与 Editor 日志；不得只看 Unity 进程退出码。报告实际发现数、通过/失败/跳过、耗时和相对任务开始的净变化。

## 代码残留清理

本任务会大幅改变调用关系，必须用 `rg` 逐项核对并清理失去生产调用方的：

- 未记录 Lua 构造函数、编译注册和对应 mega executor；
- 角色 ability ID → C# executor 映射和具体卡面规则方法；
- 设施 effect ID/behavior family → C# 完整规则分支；
- 城市样式 operation → C# 内容规则分支；
- 命令处理器中的独立 `CreateRoot/TryCreateRoot` 和先改状态后补 Effect 的路径；
- 空收尾/采集使用的 `round.mainline.complete` 通用交互与固化其行为的测试；
- 已被 Effect/Interaction/Candidate 取代的旧 pending、延迟效果和 direct service 路径。

每个仍保留的兼容适配器必须有真实生产调用方、唯一权威写入边界和明确移除条件。不能用“历史测试还在调用”作为保留生产规则实现的理由。

遵守 `AGENTS.md`：禁止批量或递归删除。每次只删除一个已确认引用的明确文件，并单独核对 Unity `.meta`。

## 不纳入本任务

- 不审查、不修复、不在交付风险表中记录旧 UI 的兼容性问题。旧专用弹窗、Presenter、Coordinator 是否还能显示不作为本任务结论；本任务只保证通用 `InteractionRequest` 数据合同正确。
- 不改变卡面规则文本、美术、Prefab 或交互视觉设计。
- 不处理开放红区引发的企业升级。
- 不为尚未被现有内容使用且合同仍待填写的全部远期 Effect API 一次性补空实现。
- 不以旧存档永久兼容为理由保留双规则引擎；若确有存档迁移需求，单独报告数据迁移问题，不恢复旧规则执行路径。

## 验收标准

只有全部满足以下条件才可判定 NMC-014A 完成：

- Lua 实际可见 API 是权威文档白名单的受支持子集，不存在 `CharacterAbility`、`FacilityEntry`、`CityStyleSpecialAction`、`GrantMainActions`、`GainResourceToOpponents` 等未记录公共构造函数。
- 所有已暴露 API 通过端到端覆盖矩阵；Host、编译器和正式 Registry 不再各自维护不一致白名单。
- 现有角色牌、设施牌、城市样式和事件牌的具体规则由版本化 Lua handler 组合文档内通用 Effect；C# 没有具体内容 ID/operation 的第二套权威执行分支。
- 雷蛇与护航调度中心都以两个顺序子 `PlaceInfluence` 结算，第二次候选在第一次终态后重新构建。
- 玩家行动及其后续 Effect 全部挂在正确 `player_action_window` 下，不产生游离业务根节点。
- 空玩家收尾节点自动推进，不出现 `round.mainline.complete` 或结束回合按钮依赖；行动窗口仍必须显式结束。
- 2、3、4 人主链、采集屏障、逐玩家收尾、下一回合创建和权限测试全部通过；不能只提供单人结果。
- 普通 `Failed`、不可恢复 `Faulted`、完成响应窗口、Candidate 补丁和恢复幂等符合文档。
- 被替代的 C# 内容分支、旧推进入口和错误测试已经逐项清理；没有 silent fallback、双写或双结算。
- 目标与完整 EditMode 通过，测试数量经过合并审查，`git diff --check` 通过。

## 交付报告

在 `docs/工作记录/已完成模块工作记录/` 新增 NMC-014A 记录，并先给出“符合”或“仍不符合”的明确结论。报告至少包含：

1. Lua API 端到端矩阵及删除的越界符号；
2. 角色/设施/城市样式/事件牌逐内容迁移矩阵；
3. 主链节点类型完成语义和 2/3/4 人测试结果；
4. 行动 Effect 的父链证据与独立 root 零残留结果；
5. Effect 内核 `Failed/Faulted/Blocked/EffectCompleted` 验证结果；
6. Candidate Lua 补丁和回答复验结果；
7. 删除或停用的代码、测试和残留适配器清单；
8. Unity XML 的发现/通过/失败/跳过/耗时及测试净变化；
9. 仍然阻止架构结案的问题。

交付报告不要列出旧 UI 兼容问题，也不要用 UI 兼容状态降低或抬高本任务完成结论。
