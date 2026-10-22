# Steam 联机重连接续实施合同

2026-10-13，用户要求把现有重连逻辑接入 Steam 联机。此次明确授权取代旧合同中“不扩大 Steam 自动重连”的范围限制。根只读源码、核证据、写协调文档和派发；实现由下列既有执行者完成。

## 目标与当前缺口

同一游戏进程、原 Steam 账号、原房主和原已开始对局，短暂运输断开后自动重建 FizzySteamworks 连接，以原 SteamID 回到原玩家席位，随后使用现有确认前缀恢复和准确命令结果接续。不得重新开局、重置随机数、重复执行断线前命令，或把运行中恢复交给初始状态安装器。用户主动退出、服务销毁、局已结束及原房主离开按已有结束语义处理；本批不新增主机迁移、跨进程读档或 UI。

当前根已读源码确认：

- `G:\NoMadCity\Assets\YC\Infrastructure\Multiplayer\MirrorNetworkRuntime.cs` 的 `TryBeginLocalClientReconnect` 仅接纳本地模式和本地身份票据，重试调用 `StartLocalClient`。
- `G:\NoMadCity\Assets\YC\Infrastructure\Multiplayer\SteamRoomService.cs` 的 `OnNetworkDisconnected` 立即 `ShutdownNetworkAndLobby` 并发布 `RoomDisbanded`，没有物理断线与终结分工。
- `G:\NoMadCity\Assets\YC\Infrastructure\Multiplayer\MirrorCommandTransport.cs` 的 `TryAcceptLocalSimulationConnectionRecovery` 和 `IsAwaitingLocalSimulationConnectionRecovery` 也限本地模式。其确认前缀恢复、`OnLocalClientConnected` 接续及 Host SteamID 身份分支已经存在，必须复用。

旧的本地四端完整局只能支持共享规则/协议结论，不能证明 Steam 运输已经通过。本批先补运输生命周期接线，再做受影响验证。

## 唯一文件负责人

协议负责人 `01a1217e-5acf-7863-9dbb-19d11084399d` 独占以下生产三文件：

1. `G:\NoMadCity\Assets\YC\Infrastructure\Multiplayer\MirrorNetworkRuntime.cs`
2. `G:\NoMadCity\Assets\YC\Infrastructure\Multiplayer\MirrorCommandTransport.cs`
3. `G:\NoMadCity\Assets\YC\Infrastructure\Multiplayer\SteamRoomService.cs`

可新增 `G:\NoMadCity\Assets\YC\Tests\EditMode\SteamReconnectLifecycleTests.cs` 及对应 `.meta`；仅在兼容现有入口确实影响夹具时，窄改 `G:\NoMadCity\Assets\YC\Tests\EditMode\LocalMirrorReconnectLifecycleTests.cs`，保留原断言语义。本人隔离输出与交付位于 `G:\NoMadCity\Logs\SteamReconnect\协议\`，最终机器回执为 `G:\NoMadCity\Logs\SteamReconnect\协议交付.json`。

新组合 `01a13383-7ca4-7780-965c-fea872f39146` 负责只读环境核对、原生/构建/独立 Player 运行窗口。若新中性状态需要驱动适配，可窄改 `G:\NoMadCity\Assets\YC\Presentation\NetworkGameValidationDriver.cs` 及 `G:\NoMadCity\Assets\YC\Tests\EditMode\NetworkGameValidationLifecycleTests.cs`，不与协议测试并写。不为本任务新增全套规则或全仓测试。本人证据位于 `G:\NoMadCity\Logs\SteamReconnect\组合\`。

`LocalMirrorRoomService.cs`、等待房间 Authority、身份注册表、DTO/Host、Bootstrapper、提交/Mobile控制器、FizzySteamworks/Mirror/Steamworks包、场景与Prefab本批先只读。若定位到必须改动的跨文件缺口，列精确接口与原因给根调整归属，不自行跨写。其余四位无新写入任务；旧组合执行者已退役，禁止唤醒。分离文件可并行，原生或必要构建前仅短暂停写实际输入。

## 单一运行时接线

1. 在现有运行时统一重试循环中接入 Steam 分支，不复制第二套命令或恢复协议。统一入口命名 `TryBeginClientReconnect()`、状态 `IsReconnectingClient`，以 `HasClientReconnectIdentity` 表达已保留且有效的原连接身份；原 `TryBeginLocalClientReconnect()` / `IsReconnectingLocalClient` 保留兼容并委托同一循环，不能改变原本地分支与热座语义。现有 `LocalClientRestarted`、`LocalClientReconnectFailed` 可继续代表本进程客户端回调，避免另立并行通知链。
2. Steam 原身份须由现有 SteamRoomService 在已验证权威房间开始后设置，入口命名 `ConfigureSteamClientReconnect(ulong lobbyId, ulong hostSteamId, ulong localSteamId, string gameId)`，清除入口 `ClearSteamClientReconnect()`。这里是内部连接上下文，不是客户端自报身份 DTO。核验原局、原账号、原Lobby/房主和客户端模式；Steam 分支不能拿本地票据替代 SteamID。
3. 首次物理断线先给房间服务与命令运输机会接纳恢复，已接纳时不触发终结退房。Steam重试使用现有 `StartClient(originalHostSteamId)` 与FizzySteamworks，重注册Mirror消息接收器。重复断线/重复回调不重置期限、不叠加重试、不重复发布业务提交。先复用现有60秒期限及1秒首次/3秒间隔，不增加游戏整局预算，不进行额外性能改造。
4. 不因短暂断线调用 `LeaveLobby`、Shutdown Steam API、清原席位或重新 `JoinRoomAsync`；不经仅允许等待房间的新加入流程恢复正在进行的对局。若Steam实际已失去原Lobby/原房主，保持既有终结边界，不能绕过成员/房主校验或猜测主机迁移。对网络抖动期间的房主信息读取失败，不能仅凭瞬时空值擅自认定换主；以可验证原身份和现有期限决定接续或明确失败。
5. 主动离开、`Dispose`、`ShutdownNetwork`、原房主变更或有效期限耗尽时，取消重试、清连接上下文并按现有终结事件退出一次。停止后的旧回调不得复活连接、房间或对局。

## 会话恢复与命令

`MirrorCommandTransport` 使用统一运行时身份/重连接纳条件，解除只限本地运输的条件，但保原GameId、ContentPackHash、membership、epoch、准确Fault和绑定installer校验。`IsAwaitingLocalSimulationConnectionRecovery`、`CanAwaitLocalSimulationConnectionRecovery` 和已有失败码消费接口保持兼容，不能另造第二恢复状态机。若需要更改公共语义必须在交付列清真实消费者。

重新连接后，通过SteamID与冻结席位的原映射绑定新connectionId，不把connectionId当玩家ID；拒绝错身份、重复绑定、错局和停止会话。Host已断开的旧连接先准确释放，等待房间身份认证与运输层绑定的回调先后需覆盖。复用现有 `RequestConfirmedRecovery`、确认前缀安装、全成员恢复屏障以及准确本地结果通知。未知或旧revision业务命令不重发，不重新签名、不放宽strict revision；确认前缀覆盖的本人命令准确解busy，未覆盖则保持原命令查询/恢复语义。

不改玩法、候选、费用、随机序列、主行动选择挂链时点和 Local 热座 Sequential 顺序；特殊行动继续选具体项后挂链、费用目标在链内选择。

## 必要验证与执行顺序

协议先实施和最小隔离检查，回执交实际文件hash、接口、测试名、原始结果、未覆盖项和 `WritingStopped=true`。组合可并行只读检查 Steam/Unity环境与测试入口；生产三源停写、回执稳定后直接进入受影响原生窗口，不逐小步骤等根许可。

必要原生集中覆盖：Steam物理断线保原Lobby/席位并接纳重连，重复回调及退出/耗尽不复活；重连后原SteamID绑定新连接并复用确认前缀、准确本地结果和后继提交；本地分支最少直接回归，避免共享循环误改已有行为。必须使用真实生产对象和真实恢复入口；可控平台适配仅隔离Steam SDK环境，不得把模拟Steam身份或内存连接说成实际Steam端到端。测试程序集无法直接引用默认程序集时，沿已有反射装配真实对象方式解决，不能改asmdef或用替身跳过受验逻辑。

原生通过后，只有需要打包新生产代码时做一次必要构建并固定自包含快照。同一快照运行期间工作区可继续独立开发。已有基础/企业/感染者八回合规则结论复用，不为纯运输接线机械重跑所有模式；是否加一场本地完整局仅由共享行为实质变化和窄验缺口决定，先说明必要性。

实际 Steam 最终接缝目标为两个已登录且可参与该AppID的独立Steam账号/设备：正式建房/加入/原身份、同步和一次正式命令，短暂断线后自动回连原席位，准确结果与后继操作一致；无自动点击，不索取账号密码、不切换用户登录、不结束用户进程、不擅自断系统网络。组合只读核环境是否具备并报告具体缺口；真实条件不具备时先完成实现和可运行的必要原生/构建，明确“Steam端到端待验”，不把它作为停止代码实现的理由。若实际外部环境操作需要用户配合，给出最短操作步骤和待观察结果。

原失败和实际退出码原样保留，夹具错误允许本人窄修后仅补失败例。分别报告代码已实现、原生通过、构建通过、实际Steam端到端、人工画面。无自动提交/推送/发布、删除/清锁/杀用户进程；不访问受限UI原档案，不运行Rebuild，不写UI资产。

## 本批验收接管

用户已明确目前没有第二台登录另一Steam账号的设备，要求“先完成接线和自动验证”。因此本批完成范围为三源接线、必要自动接缝检查和构建；实际Steam双账号运输与人工画面保留待验，不再等待设备或重复询问，不据受控SDK检查声称真实Steam端到端已通过。

本批22:14:53创建的唯一补验Unity批处理进程PID167072，原件目录`G:\NoMadCity\Logs\SteamReconnect\协议\原生-20261013-221453`，22:15:17已输出Passed XML，22:15:19后日志停在Shutdown/License通道退出且数分钟未退出。协议负责人可完成本次自有测试进程收尾：操作前再次核对PID、创建时间、Unity绝对路径、项目路径、精确testFilter及testResults/logFile均与本批native-start.json一致；仅匹配该进程才允许定向结束，严禁扩大到其它Unity/用户进程、杀进程树或清锁。保留XML、原日志、实际退出码并明确ForcedTermination=true、NaturalExit=false；检查通过与退出异常分列，不重跑已通过项目。确认进程已不存在且无其它项目占用后接一次必要构建。本节是自有测试运行窗口收尾，不授予关闭用户Editor的权限。

2026-10-13本批新组合在环境只读阶段因服务容量错误结束，最后工具已完成，环境计划尚未落盘，未启动原生或构建。根撤回其本批Driver/LifecycleTests写权和运行窗口，不重复唤醒。协议负责人01a1217e-5acf-7863-9dbb-19d11084399d继续原三源实施，并在稳定停写后接管本批环境只读核对、最少必要原生和一次必要构建，按以上相同边界完成。先复用既有原生运行方法/Logs，必要本人运行脚本仅在Logs\SteamReconnect\协议内创建；不用改Driver即可验证则不改。若确需Driver或原NetworkGameValidationLifecycleTests消费变化，先报精确差额，根再授该文件权。其他执行者无本批验证窗口。该变更解除协议负责人此前“不自行启动Unity”的限制，仅限本批已停写、无用户Editor占用后的必要原生/构建；不许关闭用户进程或清锁。
