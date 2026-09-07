# 当前代码复核与 Development 构建记录

## 1. 本次范围

本次以当前工作区为准复核未提交代码，重点检查 Steam 双人联机验证入口、等待房间兼容策略、双席位开局回归和 Windows Development Build。交互层最终拆分、设施卡、角色牌、城市样式与特殊行动沿用当前实现，并通过完整 EditMode 与构建产物自动跑局一并回归。

本次只做代码审查、测试、构建和记录更新，没有提交或推送 Git，也没有清理工作区中的其他未跟踪文件。

## 2. 当前代码结论

- `SteamLobbyPolicy` 只在 `playerCount=2` 且 Lobby 带有 `sessionKind=steam_two_player_validation` 时接受双人房；未标记的双人房仍会被拒绝，3 人和 4 人协议兼容能力保留。
- `SteamRoomService` 会为双人验证房写入专用 `sessionKind`；当前主菜单普通“创建房间”固定创建 4 人房，双人验证使用独立“Steam 双人验证”入口。
- 双人验证入口在 Mirror 本地测试模式开启时会拒绝创建，避免把 Steam 验证与本地 Telepathy 测试混用。
- 双人验证局只建立两个席位，并复用当前四人地图和按席位推进的行动顺序；它验证 Lobby、身份、命令和同步链路，不代表正式二人规则或二人平衡已经实现。
- `ProjectSettings/ProjectSettings.asset` 当前启用了可调整窗口大小，最新 Development Build 已包含该设置；窗口缩放后的人工版面检查仍需单独执行。
- 静态复核未发现会阻断当前编译、开局或自动跑局的缺失 Handler 与未实现分支。三人地图规则数据仍是已知 placeholder。

## 3. 自动验证

### 完整 EditMode

- 结果文件：`Logs/EditModeTests/editmode-20260728_002551.xml`
- 结果：`818/818 Passed`
- 失败、跳过和不确定项：均为 `0`
- 测试日志：`Logs/EditModeTests/editmode-20260728_002551.log`
- 其中 Steam 核心 `42/42`、等待房间 `13/13`、多人开局回归 `4/4`、开始菜单剪贴板与入口静态回归 `4/4` 均通过。

此前尝试使用逗号拼接多个 fixture 的过滤表达式时得到一次 `0/0 Passed` 空跑；该结果没有匹配任何测试，不计入通过证据。当前结论只采用上述 `818/818` 完整结果。

### Windows Development Build

- 构建命令：`powershell -ExecutionPolicy Bypass -File .\tools\tests\BuildLocalhost.ps1 -Development`
- 构建结果：`Success`
- 构建日志：`Logs/localhost-development-build.log`
- 构建产物：`Builds/LocalhostDevelopment/tuohuang.exe`
- 当前玩法程序集：`Builds/LocalhostDevelopment/tuohuang_Data/Managed/Assembly-CSharp.dll`，生成时间为 2026-07-28 00:29

该产物在本次完整回归之后重新生成，包含交互层最终拆分、Steam 双人验证入口及当前项目设置。`tuohuang.exe` 启动壳本身的时间戳没有变化，判断当前代码是否进入构建应以本次成功日志、更新后的 `Assembly-CSharp.dll` 和随后实际运行的自动跑局结果为准。

### 构建产物自动跑局

- 日志：`Logs/LocalhostBuild/localhost-development-autoplay-20260728_003443.log`
- `Success: True`
- `Round: 8/8`
- `Phase: FinalScoring`
- `ExploreLocationSuccesses: 1`
- `MoveCitySuccesses: 1`
- `DispatchInfluenceSuccesses: 1`
- `BuildFacilitySuccesses: 9`
- `CharacterCardUseSuccesses: 1`
- `DeclareCityStyleSuccesses: 2`
- `SpecialActionSuccesses: 2`
- `DeployInfluenceSuccesses: 12`
- `CharacterCardExecutions` 包含 `discardCount=1`
- `PaidRouteCollectionSuccesses: 1`
- `OpponentRouteRecipientCollectionSuccesses: 1`
- `FinalScoringResolved: True`

## 4. 尚未完成的人工验收

- 在实际窗口中检查开始页新增按钮布局、可调整窗口大小以及不同分辨率下的面板适配。
- Editor Host + Development Build Client 的 Mirror/Telepathy 多进程端到端操作；当前标准 4 人入口需要三个 Client。
- 两个不同 Steam 账号的双人验证房 Lobby、邀请、FizzySteamworks P2P/Relay、断线返回与状态同步。
- 多 Steam 账号的标准多人房端到端验收。
- 交互层按钮按压、地图边框脉冲、确认圆环和待选恢复的人工 UI 手感检查。

## 5. 当前结论

当前代码已通过静态复核、`818/818` 完整 EditMode、Windows Development Build 和构建产物完整自动跑局，可以进入人工 UI 与真实多进程/多 Steam 账号验收。自动化结果不能替代上述人工网络和视觉验收，也不能把双人验证入口表述为正式二人规则。
