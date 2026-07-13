# 2026-07-12 Steam/Mirror 联机替换与本地测试模式记录

## 1. 工作目标

本轮将原有 Unity Authentication/Lobby/Relay/Netcode for GameObjects 联机路径替换为 `Steamworks.NET + Steam Matchmaking Lobby + Mirror + FizzySteamworks`，固定使用测试 AppID `480`，不涉及 Steamworks 后台、Depot、部署或上架。同时增加不依赖多 Steam 账号的 Mirror 本地多实例测试模式。

开始实施前，工作树既有改动已提交并推送，基线提交为 `99fbf03 重构对局交互流程并补充牌面素材`。本轮迁移在该基线上继续开发。

## 2. 正式 Steam 联机实现

### 2.1 依赖与启动

- `Packages/manifest.json` 与 `packages-lock.json` 接入 Mirror `96.6.4`、FizzySteamworks `6.0.1`、Steamworks.NET `2025.163.0` 和 Newtonsoft Json。
- 移除 Unity Authentication、Lobby、Relay、Netcode for GameObjects 与 Unity Transport 依赖。
- 根目录新增 `steam_appid.txt`，内容为 `480`。
- `SteamAppIdBuildPostprocessor` 在 Windows 构建完成后把 `steam_appid.txt` 复制到可执行文件旁。
- `SteamBootstrap` 只负责 Steam API 生命周期；主菜单和单机开始不主动初始化 Steam，只有创建/加入 Steam 房间时按需初始化。

### 2.2 房间与身份

- `SteamRoomService` 创建或加入 3/4 人 Steam Lobby，保存项目命名空间、协议版本、房间状态、人数、原始房主 SteamID 和席位元数据。
- 房主固定为 `PlayerId=1`，其余成员按稳定规则分配席位，不依赖 Steam Lobby 成员枚举顺序。
- `PlayerSeat` 从旧 `NetcodeClientId` 调整为 `SteamId + NetworkClientId`。
- `SteamIdentityBindingRegistry` 绑定真实 SteamID、Mirror connectionId 与 PlayerId，拒绝非 Lobby 成员、重复连接和冒用玩家身份的命令。

### 2.3 Mirror 命令同步

- `MirrorNetworkRuntime` 动态创建 `NetworkManager + FizzySteamworks`，启用 Steam Relay 和 SteamNetworkingSockets，不创建 Network Player。
- `MirrorCommandTransport` 继续复用 `AuthoritativeCommandDispatcher` 和既有 DTO：Host 本地提交规则命令，Client 只发送意图，Host 校验后向远端广播确认状态。
- Client 加入后持续请求初始快照；确认序号断档或应用失败时重新请求快照。
- Host 不向本地 `LocalConnectionToClient` 重复发送已本地应用的确认消息。

### 2.4 生命周期与可恢复错误

- `SteamRoomService` 跨开始页和对局场景保持房间状态；普通场景销毁只 Shutdown，不永久 Dispose。
- `GameLaunchContext.ShutdownOnlineSession()` 统一处理设置菜单、回合结束界面和断线返回主菜单，先退出 Lobby、停止 Mirror，再加载 `StartScene`。
- 主菜单打开和单机开始不初始化 Steam。
- Steam 未启动、创建失败和加入失败作为可恢复 UI 错误处理，不使用会触发 Unity `Error Pause` 的 `LogError/LogException`。
- “取消”会撤销等待操作；迟到的 LobbyCreated/LobbyEntered 回调会被忽略并离开对应 Lobby，不能在取消后后台继续启动房间。

## 3. Mirror 本地多实例测试模式

### 3.1 模式选择

- Editor 菜单：`YC/联机测试/启用 Mirror 本地测试模式`。
- Development Build 参数：`--yc-local-multiplayer-test`。
- 可选玩家名：`--yc-local-player-name=Client2`。
- 未启用本地模式时仍走正式 Steam 路径。

### 3.2 房间与传输

- `OnlineRoomServiceProvider` 根据模式创建 `SteamRoomService` 或 `LocalMirrorRoomService`。
- `LocalMirrorRoomService` 复用 `NetworkRoomService` 作为本地房间和席位协调层。
- 协调端口从原 `7777` 调整为 `7780-7799`；Mirror Telepathy 固定使用 TCP `7777`，避免冲突。
- 本地 Client 断开时，Host 释放对应席位、恢复等待状态并广播新的房间快照。

### 3.3 模拟身份握手

- `LocalMirrorIdentity` 为 Player 1-4 生成稳定且互不重复的开发身份。
- Client 进入对局后发送 `LocalPlayerIdentityMessage`；Host 根据本地房间分配的 PlayerId 绑定模拟身份和 connectionId。
- 身份未绑定前 Host 不接受命令；身份消息和初始状态请求按秒重试，处理场景加载先后顺序差异。
- 完成身份绑定后，本地路径与 Steam 路径共用同一个 `MirrorCommandTransport`、权威命令、快照和重同步实现。

## 4. 主要文件

- `Assets/YC/Infrastructure/Multiplayer/SteamBootstrap.cs`
- `Assets/YC/Infrastructure/Multiplayer/SteamRoomService.cs`
- `Assets/YC/Infrastructure/Multiplayer/MirrorNetworkRuntime.cs`
- `Assets/YC/Infrastructure/Multiplayer/MirrorCommandTransport.cs`
- `Assets/YC/Infrastructure/Multiplayer/LocalMirrorTestMode.cs`
- `Assets/YC/Infrastructure/Multiplayer/LocalMirrorRoomService.cs`
- `Assets/YC/Infrastructure/Multiplayer/OnlineRoomServiceProvider.cs`
- `Assets/YC/Infrastructure/Multiplayer/Core/LocalMirrorIdentity.cs`
- `Assets/YC/Editor/LocalMirrorTestModeMenu.cs`
- `Assets/YC/Editor/SteamAppIdBuildPostprocessor.cs`
- `Assets/YC/Presentation/StartMenuController.cs`
- `Assets/YC/Presentation/GameLaunchContext.cs`
- `Assets/YC/Tests/EditMode/SteamMultiplayerCoreTests.cs`

原 `UnityOfficialRoomService.cs` 与 `UnityNetcodeCommandTransport.cs` 已删除；运行代码和依赖清单未发现旧 UGS/NGO 残留。

## 5. 验证

- 第一次完整迁移验证：EditMode `337/337` 通过。
- 生命周期、可恢复错误和本地模式补齐后的最终验证：EditMode `356/356` 通过。
- 最终结果：`Logs/EditModeTests/editmode-20260712_211712.xml`。
- `git diff --check` 通过，仅存在既有 CRLF/LF 转换提示。
- 搜索未发现 `UnityOfficialRoomService`、`UnityNetcodeCommandTransport`、Unity Services、NGO 或 Unity Transport 的运行依赖残留。

## 6. 未验证边界

- 尚未实际启动 Editor 房主加两个 Development Build Client 完成三进程端到端操作。
- 尚未使用三个不同 Steam 账号完成真实 Steam Lobby、Steam P2P/Relay 和断线端到端验收。
- AppID 480 是共享测试环境，只适用于开发验证，不代表正式 Steam App 配置。
- Mirror 本地测试模式验证当前 Mirror 游戏同步，但不验证 Steam 网络层。

详细操作见 `docs/联机手册.md`；当前架构与历史方案见 `docs/模块文档/多人房间与联机入口方案.md`。
