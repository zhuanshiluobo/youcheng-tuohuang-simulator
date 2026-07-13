# Steam/Mirror 等待房间开局门禁修复记录

日期：2026-07-13

## 问题与实际根因

旧实现把席位存在 SteamID 近似当成 `PlayerSeat.IsReady`，`SteamRoomService.StartGameAsync()` 只检查 Steam Lobby 席位数量。`MirrorNetworkRuntime.StartClient()` 启动的是异步连接，而加入 Lobby 很快就能返回房间快照，因此 Host 可以在远端 Fizzy/Mirror 连接尚未建立、地址尚未解析或身份映射尚未形成时开始游戏。

同时，Mirror `NetworkManager` 会在启动阶段直接赋值静态 connected/disconnected 回调；若运行时在 `StartHost/StartClient` 之前订阅，监听器会被覆盖。Host 本地连接在 `StartHost()` 内同步建立到 Server 侧，而 Client connected 回调排到后续 Update；远端 Fizzy 连接则完全异步。旧代码没有统一处理这三种顺序。

`MirrorCommandTransport` 原本只在游戏场景的 `CommandSubmissionController.Initialize()` 中初始化身份绑定，因此等待房间没有可供开局判断的 Mirror 连接权威。

本地测试模式还存在额外信任缺口：Client 曾直接上报裸 `PlayerId`，理论上可抢先声明另一个尚未连接的席位。

## 依赖源码验证

- FizzySteamworks 6.0.1 的 NextGen Server 只在连接进入 `Connected` 后建立 SteamID 与 Mirror connectionId 的双向映射，随后调用 `ServerGetClientAddress(connectionId)`；该方法通过反向映射返回十进制 `CSteamID.ToString()`。
- Mirror 96.6.4 的 `NetworkManager.StartHost()` 先注册内部 Server/Client 回调，再创建 Host 本地连接。Server 本地连接同步加入 `NetworkServer.connections`，Client connected 事件排入下一次 Update；远端连接由 Transport 异步回调。
- 因此运行时必须先调用 `NetworkManager.StartHost/StartClient`，再追加订阅，并枚举一次现有 Server/Client 状态补获 Host 本地连接或极快连接。

这些结论来自当前项目锁定的包源码，不把 EditMode 通过误认为真实 Steam P2P 已就绪。

## 最终状态机

每个 `PlayerSeat` 具有以下独立状态：

1. `LobbyMemberPresent`：玩家仍存在于 Steam Lobby；本地模式表示 TCP 房间信令席位仍被该连接占用。
2. `TransportConnected`：Host 已收到 Mirror Server connected，且连接尚在当前连接表、不处于断开队列。
3. `IdentityVerified`：Host 已将连接身份唯一映射到目标稳定席位。
4. `GameStateSynchronized`：进入游戏场景后，Client 已应用 Host 初始快照并回传与本连接身份一致的确认。

等待房间的 `IsReady` 只由 Host 计算为前三项同时成立。开局必须满足：原始房主、`roomStatus=waiting`、所有目标席位存在、每席位都有唯一存活连接、每条连接身份均验证、Host 本地连接为 connectionId 0，且没有正在断开的连接。失败不会设置 `HasStarted`；关闭 Lobby 可加入状态后若写入 started metadata 失败，会尝试恢复可加入状态。

## Steam Host 权威数据来源

- Lobby 成员：`SteamMatchmaking.GetLobbyMemberByIndex()` 的实际枚举。
- 稳定席位：Host 维护的 `seat.<playerId>` metadata，Host 固定为 Player 1。
- 传输连接：`MirrorNetworkRuntime` 发布的 Server connected/disconnected 事件与 `NetworkServer.connections`。
- 连接身份：Fizzy/Mirror `connection.address` 中的十进制 SteamID。
- Client UI：只读取 Host 写入的 `transportConnected.<seat>` 与 `identityVerified.<seat>`，不能回写权威状态。

非 Lobby SteamID、无法解析的地址、同一 SteamID 的第二条连接、席位身份不匹配连接都会被拒绝，并且不会提升任何席位就绪状态。Lobby 成员离开或等待房间连接断开会立即解除映射、清除就绪；同一 SteamID 重连后仍回到原稳定席位。

## 本地多实例模式

本地模式复用同一个 `WaitingRoomConnectionAuthority` 与 `RoomReadinessPolicy`。TCP 房间信令为每条已分配席位的连接签发随机身份凭据，Host 保存凭据到席位的映射；Client 的 Mirror 连接只呈递凭据，不能自行指定 PlayerId。凭据在后台信令线程接收后排队，由 `MirrorNetworkRuntime.Update()` 在 Unity 主线程发送。

信令断线会撤销凭据和席位，并使对应 Mirror 连接失去就绪；单独的 Mirror 断线允许在信令会话仍有效时使用同一凭据重连并恢复原席位。

## 游戏场景身份一致性

`MirrorCommandTransport` 不再重新猜测身份。Host 初始化时逐条校验游戏场景现有连接的 `connectionId + identity + PlayerId` 是否与等待房间快照完全一致；不一致连接被拒绝。Client 应用初始快照后发送 `InitialStateAppliedMessage`，Host 只接受绑定连接为自己的玩家回传确认，并设置 `GameStateSynchronized`。

## 主要修改文件

- `Assets/YC/Application/Sessions/PlayerSeat.cs`
- `Assets/YC/Infrastructure/Multiplayer/Core/RoomState.cs`
- `Assets/YC/Infrastructure/Multiplayer/Core/SteamIdentityAddress.cs`
- `Assets/YC/Infrastructure/Multiplayer/Core/SteamIdentityBindingRegistry.cs`
- `Assets/YC/Infrastructure/Multiplayer/Core/SteamLobbyPolicy.cs`
- `Assets/YC/Infrastructure/Multiplayer/Core/WaitingRoomReadiness.cs`
- `Assets/YC/Infrastructure/Multiplayer/Core/NetworkRoomService.cs`
- `Assets/YC/Infrastructure/Multiplayer/MirrorNetworkRuntime.cs`
- `Assets/YC/Infrastructure/Multiplayer/SteamRoomService.cs`
- `Assets/YC/Infrastructure/Multiplayer/LocalMirrorRoomService.cs`
- `Assets/YC/Infrastructure/Multiplayer/MirrorCommandTransport.cs`
- `Assets/YC/Presentation/GameLaunchContext.cs`
- `Assets/YC/Presentation/StartMenuController.cs`
- `Assets/YC/Tests/EditMode/WaitingRoomReadinessTests.cs`
- `Assets/YC/Tests/EditMode/NetworkRoomServiceConnectionTests.cs`
- `Assets/YC/Tests/EditMode/SteamMultiplayerCoreTests.cs`

Domain/Application 未新增 UnityEngine、Mirror 或 Steamworks 引用；`PlayerSeat` 只增加普通数据字段。

## 自动验证

- `WaitingRoomReadinessTests`：12/12 通过。
- 上一版全量 EditMode：535/535 通过，结果为 `Logs/EditModeTests/editmode-20260713_204013.xml`。
- 当前工作区已新增 `LocalMirrorIdentityTicket_IsHostIssuedAndRevokedWithSignalingConnection`，并同步更新 Steam/Mirror 核心静态回归断言。
- 2026-07-13 整理文档时，测试脚本检测到 Unity Editor PID 37088 正占用当前项目，因此新增用例、对应定向集合与新全量尚未重跑；不能把预期的 9/9、39/39、536/536 记为已验证结果。

## 尚未覆盖的真实网络边界

- 当前工作区没有 `Builds/LocalMirrorTest` Development Build，三进程流程还需要交互式创建/加入/断线/重连操作；本轮未执行 Editor Host + 两个 Development Build Client，因此不能宣称三进程已通过。
- 没有三个独立 Steam 账号环境，未执行真实 Steam Lobby + P2P/Relay 的多账号验收。
- 尚未实测公网 NAT、Relay 回退、Steam 临时离线、极端连接抖动、Lobby 回调与 Fizzy 连接跨帧乱序、Steam 客户端强制退出等边界。
- EditMode 与包源码验证只证明门禁逻辑和事件接线，不等同于真实 Steam P2P 已验证。
