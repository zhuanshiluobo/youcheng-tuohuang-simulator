# 无 UI 验证与多人流程推进边界

**当前权限更新：** 根已审双方方案并按[多人并发实施合同与文件归属](多人并发-实施合同与文件归属.md)放行精确文件实施。下文共享/规则“只读”及归档“尚未修复”是前一批记录，当前由新合同取代：Driver已交7项托管窄检查，原生及真实局待验；规则、共享、UI与组合依唯一owner实施，仍没有Unity/构建/完整局窗口。用户规则及无UI/人工UI安排不变。

更新：2026-10-10。此文采用用户最新决定，优先于历史“先试点击”和“内部＋外部各三局”的自动执行安排。协调者只读源码、写文档、派发；不直接修改代码、测试、工具或资产。

## 当前测试安排

- 后续自动验证使用无 UI 操作方式：正式内部命令、Host 提交、真实 Interaction 回答及网络同步。内部入口不能直接改状态、注入奖励或绕过规则。需加载 Player/场景的测试应如实标记，不能把“程序提交、没有点击”称作完全不加载 UI。
- 全部实现完成后，由用户人工做 UI 验收；agent 不再自动点击、不再为凑外部三局启动窗口。人工范围保留房间、多人并行交互、13 结算来源、文案/图标、本人及他人查看和当前真实资产视觉。
- 以基础、企业、企业＋感染者完整 8 回合内部对局为骨架，按内容覆盖缺口决定必要补局，不因变更输入方式重跑全部历史任务。之前“正式 0/6”为旧六局计划的历史统计；当前内部组合验收与待人工 UI 验收分别记录，不用改分母制造通过。
- 先修已定位的驱动归档生命周期问题、消费已交接收诊断，并落实多人流程所需合同，再冻结受影响输入、串行安排必要检查/构建/内部对局。不在问题未定位时机械重复第三次企业局。当前放行范围见下；本文件不自动授权马上开整局或 Unity。
- 已停止的点击试验保留全部证据。根及执行者不关闭现有用户窗口，不向其输入，不重启或替换运行中的旧 exe。源码修复不代表旧 exe 已修复。

## 本地多人归档错误：已定位、尚未修复

证据目录：`G:\NoMadCity\Logs\ExpansionValidation\ProductionGameRoutes\外部真实点击-v10-20261010-201358\player-1\`。

`authoritative-transactions.jsonl` 在 UTC 12:24:32.8064394 的真实记录中：Before.GameId 为 `49bc557cc0d14b978590dce2ead4564a`、顺序 `[1,2,3]`、revision 108；After.GameId 为 `6f913b3246df4e598c28011f9a991da9`、顺序 `[3,2,1]`、revision 14。前面 9 条属于旧局，后面另有 5 条属于新局。`host-checkpoint.json` 保留旧局；`host-archive-capture-failure.json` 给出新局触发 `launch_player_order_changed`。`result.json` 首次结束原因为 `frozen_configuration_changed`，commands 0、confirmations 9、rounds [1]；失败结果不能改写成归档成功。

当前 `G:\NoMadCity\Assets\YC\Presentation\NetworkGameValidationDriver.cs` 的 `OnConfirmed` 在 stopped 后没有入口保护，`auditArchive`、`previousBusinessSnapshot` 跨场景保留，追加前未隔离 GameId；`Finish` 只使 Update 停止，其他回调仍可进入。该证据足以定位本次跨局混档；不证明旧第六回合确认丢失同源，也不证明普通游戏业务状态被混合。

组合验收唯一负责 `NetworkGameValidationDriver.cs`，本批可作最小生命周期修复及其自有专用测试：停止后不再处理业务/归档回调；第一场已绑定 GameId 与后续场次隔离，在写 Before/After 或 AppendJournal 前拒绝跨局拼接；停止清理订阅须幂等，保留第一次终止原因及原始失败证据。当前每次运行只接受一局，新局需独立运行/输出目录，不能清掉旧档继续冒充同局。具体新增业务命名与测试路径先在本人报告登记。保持同局顺序、内容、revision 和恢复守卫。

`G:\NoMadCity\Assets\YC\Application\Sessions\HostRecovery.cs` 仍归共享集成，组合不得删除/放宽顺序校验或擅改此文件。确需通用 GameId 守卫时先交共享增量；当前优先在归档消费者隔离身份。

组合可消费共享已交 `MirrorCommandTransport.CommandConfirmationObserved` / `LocalCommandConfirmationDiagnostic` 本地观察事件，补接收、pending、Apply、Initial 记录；不顺便实现尚未批准的延迟确认修补。允许直接相关的离线窄检查，不启动 Unity、构建或完整对局；交生产停写、文件哈希与必要后续运行清单后，由根串行放行。不能把日志观察补全称第六回合已修复。

## 多玩家流程：用户行为已明确，方案待落地

企业开局应允许各玩家同时选择各自已分配候选；全体选择完成后再统一发布企业、补足三人局公开企业并初始化依赖内容。不得暴露他人私密候选或因别人提交清掉本人合法选择。

用户继续明确：**正常盖放阶段、正常采集阶段也都是玩家并发。** 玩家各自选择和提交，不以前一名玩家全部完成作为本人可操作的前置；该阶段必要任务全部结束后才能进入下一阶段。并发指多名玩家可同时操作，Host仍按正式权威提交顺序处理状态，不要求多线程改GameState。正常盖放、正常采集与企业选择一起纳入同一套共享调度/请求合同；入场选点仅按下段规则逐位放行。

盖放必须保留本人候选、隐藏牌隐私、每人盖牌名额、无手牌时的既有角色回收（流形属于角色牌）及阶段开始/完成事件的正确时机；缪尔赛思额外盖放和赫拉格新盖牌仍按各自事件/候选规则，不把它们扩大成全员额外槽。采集保留开始时支付能力快照、实际收费与收益、事件因果、合法候选和每人完成记录；其他玩家提交不能重置本人合法草稿或重建已完成采集。额外采集不冒充正常阶段，不改变其独立规则。

当前源码定向事实（只读，不是本轮测试通过）：`G:\NoMadCity\Assets\YC\Domain\Rules\RoundExecutionService.cs` 的 `AddCollectionTasks` 已调用 `AddIndependentChildren("collection_players", tasks)`，`RefreshCollectionSelectionRevisions` 已同步其他未完正常采集请求版本，`RoundExecutionProjector.cs` 在采集时将 `CurrentPlayerId=-1`；`G:\NoMadCity\Assets\YC\Tests\EditMode\ExpansionRules\CollectionMainlineSchedulingTests.cs` 已有 `NormalCollection_MainlineOpensAllPlayersAndAcceptsNonFirstPlayerBeforeFirst`。因此采集列为“已有并发实现，当前联机/客户端待核验”，不能无依据重写。盖放的 `CompleteCharacterCover` 仍按 `ActiveTaskPlayerId` 拒绝非当前玩家，投影仍解析单一盖牌玩家，列为“串行限制待改造”，需覆盖实际通用Effect与旧兼容入口，不能只删一处校验就称完成。

盖放实际执行器路径为 `G:\NoMadCity\Assets\YC\Domain\Cards\CharacterCoverEffectExecutor.cs`；正常采集入口还包括 `G:\NoMadCity\Assets\YC\Domain\Effects\RouteResourceSelectionEffectExecutor.cs`、`G:\NoMadCity\Assets\YC\Domain\Travel\RouteResourceSelectionService.cs`。本批归规则与共享只读核对，生产写入仍须按精确归属放行。

入场选点保持开局顺序；前位选点真实提交、占位生效后，下一位即可选点，不等待前位完成删牌。前位入场效果/奖励/删牌仍必须完成，所有必要开局任务完成后才能进入第一回合。各事件的真实公共依赖不得凭“并行”一律消除。

| 负责人 | 当前本批权限与交付 | 独占文件/入口 |
| --- | --- | --- |
| 结算规则 `01a1217c-77db-7251-8fb2-cda7c1206a4d` | 只读方案：拆玩家独立节点、选点提交里程碑和全体完成屏障；明确字段/恢复/去重/准确生产文件。写自己的执行报告，不改生产、不跑测试。根审合同后实施 | `Assets/YC/Domain/Rules/ExpansionSetupFlow.cs`、`RoundMainChainBuilder.cs`、`RoundExecutionService.cs`、`RoundExecutionProjector.cs`；`Assets/YC/Domain/Effects/PlayerEntranceEffectExecutor.cs`（本批只读） |
| 共享集成 `01a1217b-9745-7430-81a7-6672d2ba8932` | 只读核对现有内核能否同时保留多个玩家 request、权限/候选/提交版本语义；给规则与 UI 可消费的最小接口增量，不放宽全局 revision guard | `Assets/YC/Domain/Effects/EffectKernel.cs`、EffectRuntime/Interaction 共享模型与投影、`Assets/YC/Application/Interactions/AnswerInteractionCommandHandler.cs`（本批只读） |
| 组合验收 `01a1217e-d855-7c02-943c-b5676422e028` | 上述归档驱动窄修和接收诊断消费；将自动路线改为内部验证、UI 转人工待验；保持原审计证据标准 | `Assets/YC/Presentation/NetworkGameValidationDriver.cs` 与其本人专用测试；不改规则、DTO、Lua、HostRecovery 或 UI 资产 |
| 界面集成、文字渲染、缺陷与验收 | 当前保持停写待命；接口冻结后按各自原 owner 串行派受影响消费/迁移/验证，不为通知全体而重复唤醒 | 既有控制器、renderer、Prefab 和测试归属不变 |

方案必须说明：企业不再由单一 nextIndex 阻塞；入场不再以整名玩家节点全部结束作为下一选点的唯一条件；盖放去除玩家间人为串行依赖并保留全部必要完成屏障；采集复用已有并发节点而只修真实缺口；CurrentPlayerId 与本人可回答请求如何共存；一个玩家提交后的合法请求如何刷新而不误丢选择；同一 GameId 的恢复和重复提交如何防止重抽候选、重复占点/奖励/删牌/盖牌/回收/采集收益。不能按任务序号命名新脚本、类型、变量或协议 ID。

必要验收仅覆盖改变的边界：企业多请求同时存在及逆序回答、隐私与三人补企业只一次；前位删牌待定时下位选点已可答且排除已占点；未收齐不能进入第一回合；盖放多人请求/逆序提交、隐藏投影、空手含流形回收及未收齐不进行动；采集已有并发路径的真实联机提交、合法草稿/预算保持、收益一次及未收齐不进收尾；并发提交/重复/他人/失效候选拒绝及同步；中途恢复不重发结果。优先复用采集现有针对性测试与完整局证据，不扩大重跑全部历史测试。真实 UI 展示与操作留给全部实现后的人工验收。

## 当前分类

- 已实现已验证：共享本地接收诊断的两个原生方法检查（只在该范围）；点击试点实际 9 次工具点击（只到部分房间/企业选择，不是完整局）。
- 已实现待验证：采集已有独立玩家子任务、全体完成屏障与其他请求版本刷新，真实联机/客户端待核验；先前报告其他已有实现但缺当前组合证据的项目继续按各 owner 报告保留。
- 尚未实现：本轮归档停止/跨局修复、诊断消费者、企业并行、盖放并行和入场流水推进；正文剩余迁移与正式业务覆盖缺口仍按台账待办。
- 待明确规则/UI：企业、正常盖放、正常采集并发及入场选点流水行为、最终人工 UI 验收决定已经明确，无需重复问用户；仅后续发现未确定且影响体验的具体项才列缺项。当前没有因此阻断开发的用户决策。
