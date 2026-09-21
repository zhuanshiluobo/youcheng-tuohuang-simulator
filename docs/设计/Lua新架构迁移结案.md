# Lua 新架构迁移结案审计

## 结论：不可结案（2026-09-20）

NMC-015 的审计与清理有必要，已在本轮执行；**不能宣布 NMC-015 全部完成或全内容迁移完成**。NMC-014A 要求的角色、设施、城市样式纯 Lua 组合仍未全部迁完，且部分 Pending adapter 仍有生产调用方。不能删除这些路径或其独特测试来制造结案。

本轮是全仓层级扫描与高风险链路深查，不等同于对全部代码逐行形式化证明。扫描范围为 `Assets/YC`：Domain 125、Application 30、Infrastructure 29、Presentation 185、Editor 52 个 C# 文件（审查时点，不含测试）。检查程序集反向引用、状态写入入口、内容 ID 分支、旧 Pending、完整网络 DTO、UI 回答协议和回合推进；深入验证交互、资源、恢复、收尾与失败事务。

## 迁移矩阵

| 内容 | 当前路径 | 本轮结论 |
| --- | --- | --- |
| 四个入场节点、B-01 的 222、入场事件牌 | 主链→PlayerEntered→Lua→最小资源/翻牌 Effect | 已接入；保留前序证据，并受最终全量保护 |
| 主要行动部署 | 激活挂链→基础 PlaceInfluence→确认或取消 | 已接入；其余主要行动的旧草稿入口仍需逐项迁移 |
| 角色十个策略/计谋 UI 入口 | 点击只提交激活，内核创建请求 | 本轮统一，不再预选同一份参数 |
| 雷蛇策略/收尾 | 两个 Lua PlaceInfluence + 持久定时 RemoveInfluence | 已迁移；本轮删除重复的 C# 专用 executor |
| 极境策略 | Lua 最少资源候选→Choice→versioned continuation→GainResource(4) | 本轮迁移，内容 1.2.0，含等待中恢复和重复答案 |
| 坎诺特计谋 | Lua 一次资源选择→每位玩家 Pay/Gain→发动者 GainScore | 本轮迁移，内容 1.2.0；保留人数、金额和一次结算断言 |
| 极境计谋、锡人策略 | Lua Condition 与基础资源/移动/得分节点 | 条件式专题已迁移，专用执行器撤去 |
| 十项角色能力 | 外部 JSON + Lua，组合通用 Effect；五项专用 executor 已移除 | 已完成本轮外部定义迁移；旧无 Registry 兼容路径另列 |
| 设施资源奖励、护航调度中心 | Lua 直接返回资源/两个独立放置节点 | 已有直接组合；其余设施仍用 facility_entry 适配 |
| 城市样式/特殊行动 | 部分 Lua 基础节点 + 旧行为执行器和 Pending 路径 | 未完成；本轮修复兼容守卫、事件会话接线及轮末标记复位 |
| 通用 UI | Router 按能力选择，Application 共用 Answer/Decline，现有弹窗/地图表现 | 事件、角色、地图使用能力 Router；设施/特殊行动的新请求共用回答协议，仍保留专用同步入口和旧 Pending adapter |
| Lua 公开 API | 已实现路径才安装公开构造器；旧未知节点 fail-stop | 去除规划 API 的虚假可用性；完整 Choice 分支数组等仍未实现 |

## 残留审计与处理

| 路径/符号 | 当前调用方 | 替代路径/测试 | 决定与移除条件 |
| --- | --- | --- | --- |
| `CharacterCardEffectInteractionUiCoordinator` 的初始预选方法、回调参数 | 原 Controller 单一入口 | 十项 activation-only 表测试、通用 Choice 实际弹窗测试 | 已删除旧资源/出售/德克萨斯预选方法及无用参数，保留旧锡人 Pending 展示 |
| `CharacterAbilityEffectExecutor` 极境策略、坎诺特计谋、雷蛇策略专用方法 | 原 CharacterAbilityCatalog | Lua 组合；NMC010、真实玩家流程 | 已删除重复 executor，目录明确无 C# fallback；误走旧专用节点会 fail-stop |
| `RoundAdvanceService` | 多个 Application 命令处理器、组合根、诊断与旧测试 | RoundExecutionService 持久主链 | 仍是多调用方兼容外观；尚不满足 NMC-015 的单入口移除条件。后续逐一改为注入共享主链服务 |
| `PendingChoice` / `PendingCardSession` | 旧事件、设施、采集/建设 UI 及诊断 | InteractionRequest、通用 renderer | 不删除。对应事件/设施所有行为完成 Effect 迁移后再逐类移除 |
| `PendingCharacterEffect` / `CharacterCardService` 旧卡面分支 | 默认旧命令构造、旧角色协调器和规则测试 | 生产共享 Registry 的角色激活/Sequence | 五项能力、双发兼容和弃牌流尚未全迁，保留并标为阻塞，不再新增这种路径 |
| `PendingSpecialAction` / `SpecialActionService` | UseSpecialAction 兼容命令、旧 UI 与诊断 | 城市样式 activation/Interaction | 仍有真实调用；本轮修复不能漏进 HasPendingChoice、非法恢复不能阻塞、事件回答带会话 ID。全部城市样式迁移后删除 |
| `DelayedCharacterEffects` | 旧角色服务/测试、序列化兼容字段 | TimingBinding + PlayerCleanupStarted | 雷蛇新路径不再使用；旧字段的全存档移除策略未完成，不批量删字段 |
| `FacilityEntryEffectService` / behavior executor | 设施命令与 Lua facility_entry 适配 | 设施 Lua 直接最小 Effect 组合 | 除资源与双放置外仍未迁完，不能删 |
| `InitialGameStateDto`、dispatcher 旧完整状态/重执行方法 | 当前检索到的调用者为 NMC003/NetworkCommandDispatcher 兼容测试，Mirror 生产不使用 | Initial/ConfirmedGameStateViewDto；NMC011 隐私/事务测试 | 需要把旧测试中独特同步/收据断言迁到投影合同后再删，不能仅删测试；仍列为 NMC-015 待清理 |
| Controller 交互类型白名单 | 原 SynchronizeEventCardInteraction | InteractionRequestRouter.RouteOpen + renderer.CanRender | 已移除，新增 Lua Choice 不再需要改 Controller |
| UI 局部计数器命令 ID | 原事件/角色/设施 renderer | EffectInteractionCommands 的唯一 ID + 固定观察 revision | 已替换，避免重建 UI 后命令 ID 撞车 |
| asmdef / 第三方包 / prefab | 当前程序集编译与资源引用 | 编译、引用完整性、资源测试 | 未发现可证明无用并安全移除的依赖；没有批量删除文件、目录或 meta |

## 恢复、权限与事务

- Domain/Application 保持 `noEngineReferences=true`。Domain 无上层 using；Application 无 Unity/Lua/Presentation 反向引用。组合根的装配依赖是允许的边界。
- Mirror 生产按每个连接投影 Initial/ConfirmedGameStateViewDto。客户端不接收 Host 的完整树、隐藏手牌或完整状态，不执行确认命令重算规则。旧完整状态兼容 API 尚未移除，不能声称整个仓库已不存在这类符号。
- Lua Choice 将字符串、单个候选引用、UI 候选引用数组统一为单选字符串/多选字符串数组，再进入 continuation。真实 UI 曾在这里停滞，已新增与 UI 同协议的恢复/重放回归。
- continuation 的内容版本/hash 与通用执行器版本独立。缺失或不匹配的绑定仍故障暂停；完成奖励不得通过重新激活来恢复。
- UI 只提交候选或资源分配意图，数量须由 Host 重新校验；内核在命令工作副本上结算，失败不发布。多步骤效果仍按节点提交边界运行，不把动画当作业务确认。
- Host 持久状态主要使用稳定 ID、归一化值与列表。旧 GameCommand.Parameters 的 Dictionary 属兼容命令合同，不能据此声称“全仓全部 DTO 已完成最终纯列表合同”；NMC-015 的最终 DTO 移除复核仍待完成。

## 测试与保留理由

- 本轮全量基线：1098 项，1073 通过、25 失败、0 跳过，754.56 秒，`Logs/architecture-20260920-baseline.xml`。
- 修复后定向集合：239/239，`Logs/architecture-expanded3.xml`。
- 坎诺特真实 UI 修复后八回合通过：100 输入、16 部署、1 取消、1 角色行动、1 征收、0 错误，终局并返回菜单。证据 `Logs/PlayerJourney/20260920-architecture-cannot-fixed/result.json`。
- 极境真实 UI 八回合通过：100 输入、16 部署、1 取消、1 次资源选择、0 错误，终局并返回菜单。证据 `Logs/PlayerJourney/20260920-architecture-elysium/result.json`。
- 只读快照、Lua 分发、角色及城市目录实际调用回归 `architecture-snapshot-contract2.xml`：41/41。
- 最终串行完整 EditMode：**1116/1116 通过，0 失败、0 跳过，802.23 秒**，证据 `Logs/architecture-20260920-final3.xml`。全量基线 1098 项中 25 项失败已消除；本轮发现数净增 18。
- 基线慢项：LocalhostAutoplayRunner 527.20 秒；MultiplayerLaunchRegression 208.06 秒。两者属于命令/诊断与场景回归，不能代替玩家 UI 黑盒。
- 未按 700 目标删除独特断言、加 Ignore 或隐藏目录。主链/内核、隐私/恢复、兼容规则、输入协调、资源引用分别保护不同风险；其中旧规则与新路径重叠部分只有在全部行为迁移、替代断言逐项核对后才可合并。因此当前尚不是 NMC-015 的“最终瘦身完成”状态。
- 已把旧人工 EndAction 收尾测试迁到真实主链，保留轮末标记、联邦议会、资源和最终计分断言。角色初始预选测试改为十项入口表，数量弹窗的拖动/价格/数量断言保留。

## 仍阻止结案的项目

1. 剩余设施/城市样式组合仍有 C# 内容规则；角色所需 Choice 分支、出售和首批卡区原语已闭合，更广泛卡区参数仍未实现。
2. 主要行动除部署外仍有 UI 先选参数的旧路径；设施/特殊行动仍有专用 Pending 展示协调器。
3. 旧完整状态 dispatcher API、旧命令重执行测试、Pending/延迟字段及多入口 RoundAdvance 外观未全清理。
4. 测试发现数仍高于 700；迁移前不能安全删除这些不同规则和兼容路径的独特覆盖。
5. 本轮玩家测试为单机 Editor UI；正式构建、四进程、Steam、多机断线恢复不属于已经通过的证据。

### 城市样式审查补充（前一轮发现，后续修复见文末条件式专题）

复合动力系统的目录脚本仍把选择提示写在 `orderPolicy`，本轮改为 Choice 明确支持的 `promptKey`，该模块内容版本升为 1.0.1；增加真实注册目录→触发事件→Lua 执行→开放请求的回归，而非只检查订阅登记。此测试只证明选择入口合同，**不能证明复合动力选择后的移动与航道放置已完成 Lua 迁移**。现有脚本仍缺后续组合，是 NMC-014A 的具体阻塞项。高效移动管理系统仍从旧候选数组预取目的地，连续移动后候选重算与逐段交互也须继续迁移；不能把城市样式登记齐全等同于全部规则可用。

宿主只读代理在通用参数归一化时误读为空数组的问题已修复：宿主记录自己创建的代理，以弱引用映射读取其底层只读值，返回值预算检查同时处理真实内容，不执行任意脚本元方法。真实复合动力目录调用回归因此能打开候选请求。这属于通用宿主合同修复，不扩大上述城市内容完成范围。

### 完整状态兼容路径与性能审查

`AuthoritativeCommandDispatcher.SubmitAuthorityCommand` 仍给内部 `ConfirmedGameCommandDto.State` 深克隆完整状态；Mirror 的实际 `CreateConfirmedStateViewSynchronization` 从会话按连接投影，不发送此完整副本。旧初始状态/客户端重执行 API 的调用者主要是兼容测试，但不能由此说完整 DTO 类型整体没有生产引用。后续应把内部确认事件改为命令/序号收据，迁移独特旧同步断言，再删无用副本及旧 API。反射深克隆和完整树累积是性能排查候选，当前只有全量慢项测量，尚未有 profiler 证据证明该副本占了多少耗时。

### 角色能力的迁移边界（历史阶段，已由文末外部内容迁移更新）

角色中的极境计谋、锡人策略已撤去专用执行器，其余五项仍保留适配。UI 入口只提交激活，以下列出已落实和后续边界。不能通过把方法改名成“通用 Effect”宣称迁完。

| 能力/现有执行器 | 应补齐或复用的组合 | 必须保留的独特风险 |
| --- | --- | --- |
| 雷蛇计谋 `ExecuteLiskarmControlPosition` | 已用 Lua Condition(PayResource, 选点适配→ReplaceInfluence)；剩余选点适配待迁入通用替换交互 | 对手城市只移除、一般目标替换；资金与目标复核、removed_only 结果 |
| 极境计谋（专用执行器已撤去） | Lua Condition：先 PayResource，再普通 MoveCity 的候选修正 | 不是突袭；普通移动生命周期与候选交互、支付后恢复 |
| 德克萨斯策略 `ExecuteTexasSpecialDelivery` | 先 GainResource，再设施候选与移动设施卡区/补牌 | 选中供应牌必须仍存在，牌区唯一归属，补牌与洗牌的确定性 |
| 德克萨斯计谋 `ExecuteTexasRemoveAndDoubleMove` | 移除及两次调度选择，稳定实例 ID，Pay/Remove/Move 基础节点 | 已改为先支付再逐步提交移除/移动；剩余选点适配待迁入通用移动影响力交互；稳定实例排重和恢复已测 |
| 坎诺特策略 `ExecuteCannotTradeChannel` | 资源分配请求 + 通用 SellResource，内部编排 Pay/Gain | 出售数量与单价由 Host 复核，空售/过期答案不能重复奖励；保留数量弹窗 |
| 锡人策略（专用执行器已撤去） | Lua GainScore + 两个可放弃 Condition(Pay, Gain) | 拒绝首次购买仍能进行第二次；恢复不得重复得分或扣费 |
| 锡人计谋 `ExecuteTinManDeepPlanning` | 先同时回收全部弃牌，再按回收数量生成 Choice + Gain/Move | 第一项奖励选择前牌区已提交；恢复按持久剩余次数继续，正在结算的锡人不回收 |

优先补齐可被多项内容复用的资源分配/出售、卡区移动和逐段移动候选合同，再逐项移除 executor 与旧服务分支。与数量和价格相关的规则继续沿用现有规则数据，本轮没有另行调整价格平衡。

- 雷蛇本轮真实 UI 八回合通过：`Logs/PlayerJourney/20260920-architecture-liskarm/result.json`，102 次输入、16 部署、1 取消、1 角色行动、1 收尾移除、0 错误，终局并返回菜单。

从 NMC-001 原始基线 1029 到本轮最终 1116，净增 87；相对当时治理后 961，净增 155。这些包含新内核、交互、隐私与恢复合同；仍高于 700，按测试治理风险矩阵保留，最终瘦身尚未完成。

最终 `git diff --check` 通过；保留工作区前序改动，未删除文件或目录，未提交 Git。完整 EditMode 全绿不改变本审计“不可结案”的迁移结论。


### 条件式专题纠正（2026-09-20，后续于上述全量审计）

此前对极境“必须使用突袭语义”的判断撤回。2.6 的规则是修改候选的普通城市移动，本次复用了完整 MoveCity。此前复合动力只有 Choice、高效移动预取目标、锡人首次拒绝提前结束的缺口已修复。复合动力材料选择现在属于左 Effect；支付成功后右侧才产生移动请求，事件完成后才产生航道放置请求。固定源石费用重复叠加亦已修复。

最终 `ConditionalCardEffectTests` 15 项及新增 UI 按钮 4 项已通过，最终代码复跑 1129/1129（排除本轮前次全量已通过的 5 项慢用例）；全量结果和版本边界以 `docs/测试/2026-09-20_条件式先支付回归.md` 为准。旧 1116/1116 和三个黑盒跑局是前一轮代码证据，不能充当本次计谋和城市样式的新增黑盒验证。

NMC-015 仍不可结案：五项角色适配、部分设施/城市样式、完整 CandidatePatch 参数链、旧 Pending 调用及发布级多人黑盒验证尚未全部关闭。本次支付组合节点是可复用宿主机制，费用与声明生命周期仍有目录/C# 编排，未声称已全部纯 Lua 化。


### 2026-09-20 规则语义复核

再次以 API 初稿 2.6 为依据核对十项基础角色能力、六种城市样式，并检查移动事件时序。确认并修复四处卡面偏差：锡人计谋先同时回收再选奖励；坎诺特策略允许全部出售数量为零；德克萨斯策略先获得 12 金券再选设施，供应区为空不取消奖励；动员配套由只移除修正为 Choice → ReplaceInfluence，不能放置时保留移除。先前表中把锡人逐张回收当成迁移目标的描述已撤回。

另修正未指定目的地的 MoveCity：BeforeCityMove 先于候选生成与 UI，回答后不再次触发该事件。角色宿主版本升至 1.3.0，动员 Lua 内容升至 1.1.0，移动宿主升至 1.2.0；旧未完成节点不得静默套用新流程。

这些修复不代表五项角色适配已纯 Lua 化。旧 CharacterCardService / Pending 兼容路径仍存在；旧 CanRaidCityForCharacter / RaidCityForCharacter 只作为兼容实现证据，不能作为新规则依据。专项与回归证据见 `../测试/2026-09-20_规则语义复核.md`。


### 2026-09-20 外部内容迁移（当前状态）

十项角色能力已全部改为外部 Lua 组合，原五项专用 executor 方法已移除。静态角色定义、名称、贴图与初始手牌顺序来自外部 JSON；用不存在于 C# 枚举中的新角色完成激活的回归已接入。Choice 分支、SellResource、MoveFacilityCard、MoveCharacterCard 与 SelectInfluence 是通用宿主原语，角色费用、奖励与执行顺序留在 Lua。

现有设施、事件、城市样式静态定义与贴图也从统一包加载；设施/城市样式脚本正文已外置，但部分行为适配仍在 C#。企业与企业家给出未启用的同格式模板，不能声称其玩法已经实现。NMC-015 仍受剩余内容适配、旧 Pending 调用和发布级联机验证阻塞。

详细实现见《外部内容包与Lua定义》，本轮测试与黑盒证据见《2026-09-20_外部内容包迁移回归》。上文带日期的“五项角色适配”描述是历史阶段，不再表示当前新链路状态。


### 2026-09-20 内容装配可执行迁移

外部定义的 replaces 已接入实际装配，不再只是校验元数据：由 enabledExpansionIds 选择扩展，解析显式替换链，保留根牌组槽位，只向 Domain/角色注册器/贴图读取提供最终定义。新增缺失依赖、未启用目标、冲突替换拒绝。设施共用数据迁为 19 份模板加 46 份实体引用，展开后与迁移前完整 data 逐项一致；并修复 Texture2D 失效重载时的重复键异常。

用户已明确：永续与一次性技能的生命周期留待具体扩展卡牌实现。本轮不提前规定移出游戏、整局限用或永续可用区规则。NMC-015 仍有设施行为适配、旧 Pending 调用、完整候选补丁链及发布级联机验收等未完成项。测试证据见《2026-09-20_内容装配与设施模板回归》。

## 2026-09-20 设施 Lua 组合与接口补齐

本轮五类设施转为外部 Lua 组合：资源出售使用 SellResource；替换或放置影响力使用 Choice 后执行 SelectInfluence/PlaceInfluence；任选五基本资源使用 ChooseResources；免费移动并放置航道影响力使用 MoveCity 完成事件后再挂 PlaceInfluence；核心相邻设施奖励使用公开设施快照统计后 GainResource。免费移动的影响力候选来自实际经过的航道，不能预选右侧目标或默认第一航道。

新增 `Effect.ChooseResources({ player = ctx.playerId, amount = 5 })`：amount 为 1～99，选择源岩 Originium、源石 OriginiumShard、异铁 Iron，总量必须准确相等。请求使用 resource_allocation schema，候选带 total:N；UI 只收集数量。内核提交前与 executor 执行时均复验，非法回答保留原请求且不改变资源；有效回答后才挂 GainResource，恢复和重复提交不得重复发放。

`Global.Content.FindInstances()` 当前提供已建设设施公开快照：id、definitionId、owner、slotIndex、adjacentToCore。它不开放 GameState 或 Unity 对象，也不代表所有内容区域查询已实现。Lua 只读取快照并返回 Effect，状态变更由内核执行，UI 继续复用现有数量弹窗和地图选择。

正式组合根的各行动处理器共用包含同一效果注册表的 RoundExecutionService/RoundAdvanceService，避免独立服务跳过新回合主链。已迁移五类旧 facility behavior 分支撤下；该宿主版本升至 1.1.0，旧行为节点明确报版本不匹配，不能静默使用新规则续算。复制、额外建设、扩展枢纽等其余设施适配与旧 Pending 兼容入口仍存在，不能将本轮记为全部设施纯 Lua 化。

永续、一次性生命周期按用户要求留待具体扩展牌实施。NMC-015 尚有其他内容适配、旧 Pending 调用、完整候选补丁链和发布级联机验收等未关闭项。

## 2026-09-21 相邻设施入场复制迁移

附属能源设施的相邻筛选与复制编排已改为 `lua/facilities/copy_adjacent_entry.lua`：读取公开设施快照，筛选己方十字相邻、非彩色且有入场效果的设施，再用 Choice 生成单次选择。无合法邻居时正常完成，未选中的分支不结算。复用原 facility.entry.choice 弹窗，不修改布局。

新增 `Effect.ActivateFacilityEntry({ player = ctx.playerId, instanceId = '设施实例ID' })`。宿主复验实际实例仍存在、归属于执行玩家且具备入场效果，再挂现有设施入场节点；保存后从已挂子节点继续，不重复触发。它不包含相邻或颜色规则，这些限制属于卡牌 Lua。公开设施快照新增 color、hasEntryEffect，只读构造快照，不在查询时修改 GameState。

原 ReplayAdjacentEntry 专用分支已撤下，设施入口宿主版本升至 1.2.0。旧版本未完成节点不能静默套用新代码恢复。其他旧设施适配仍在：额外建设、扩展枢纽、收尾标记，以及载具仓库等。

本轮审查另发现载具仓库旧适配缺少“移除后调度”分支，探索选项也尚未完成合法候选交互。该问题已登记，当前未修复，不能把该设施记作完成。下一步应补齐通用探索交互后迁移完整 Choice 两分支，避免继续使用预选目的地和专用结算逻辑。NMC-015 仍不可结案。

## 2026-09-21 基础设施后续迁移（当前状态）

载具仓库、额外建设、延伸枢纽与联邦理事处已迁为 Lua 组合及通用原语。14 种入场模板全部脱离旧 facility_entry 适配，旧设施 Behavior 与城市样式 ScriptStep 已撤下。此前记录的载具仓库缺失分支现已修复。建设不再默认空位或错误扣行动次数；探索逐段选择路线、落点及收费方；起始玩家覆盖值在回合边界消费并清除。

统一请求编号修复了同一 Effect 连续同类交互时 ID 重复的问题，设施展示也已接入统一路由。建设颜色折扣改为外部 costReductionPerDistinctBuiltColor 数据。接口、版本与剩余 UI 边界详见《基础内容迁移与UI接入边界》。NMC-015 仍不能结案，不把基础内容迁移等同旧 UI、存档和多人发布验收完成。
