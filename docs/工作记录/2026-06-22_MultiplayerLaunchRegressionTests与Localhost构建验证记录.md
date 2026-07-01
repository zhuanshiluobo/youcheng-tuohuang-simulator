# 2026-06-22 MultiplayerLaunchRegressionTests 与 Localhost 构建验证记录

## 背景

在修复第 8 回合结算弹窗时机后，需要关闭占用项目的 Unity 编辑器，补跑 `MultiplayerLaunchRegressionTests`，确认四人局只在第 8 回合第 2 行动轮第 4 位玩家结束行动后进入 `FinalScoring`，随后重新构建 `Builds/Localhost`。

## 测试验证

执行命令：

```powershell
powershell -ExecutionPolicy Bypass -File tools\Run-EditModeTests.ps1 -TestFilter MultiplayerLaunchRegressionTests -NoGraphics
```

结果：

- Unity Editor：`F:\2022.3.62f2c1\Editor\Unity.exe`
- 结果 XML：`Logs/EditModeTests/editmode-20260622_154953.xml`
- Unity 日志：`Logs/EditModeTests/editmode-20260622_154953.log`
- 测试汇总：`result=Passed, total=3, passed=3, failed=0, inconclusive=0, skipped=0`
- 关键用例：`HostLaunch_FourPlayers_EndsOnlyAfterRound8SecondActionRoundFourthPlayer` 通过

该用例覆盖：

- 推进到第 8 回合第 1 行动轮时，仍显示第 8 回合。
- 第 8 回合第 1 行动轮四名玩家结束后，阶段进入 `ActionRound2`，不进入结算。
- 第 8 回合第 2 行动轮玩家 1、2、3 结束后，仍保持 `ActionRound2`，当前玩家为 4。
- 第 8 回合第 2 行动轮玩家 4 结束后，状态进入 `FinalScoring`，`ActionRound` 归零，回合条进入最终索引。

## 构建验证

执行 Unity batchmode 构建：

```powershell
& "F:\2022.3.62f2c1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath "F:\游城拓荒模拟器" -executeMethod YC.Editor.LocalhostBuildMenu.BuildLocalhost -logFile "F:\游城拓荒模拟器\Logs\LocalhostBuild\localhost-build-20260622_155100.log"
```

构建结果：

- 构建日志：`Logs/LocalhostBuild/localhost-build-20260622_155100.log`
- 日志结论：`Build Finished, Result: Success.`
- 输出结论：`Localhost simulator build succeeded: Builds/Localhost\游城拓荒模拟器.exe`

重新生成的主要产物：

- `Builds/Localhost/游城拓荒模拟器.exe`，更新时间 `2026/6/22 15:52:22`
- `Builds/Localhost/游城拓荒模拟器_Data`，更新时间 `2026/6/22 15:52:34`
- `Builds/Localhost/游城拓荒模拟器_BurstDebugInformation_DoNotShip`，更新时间 `2026/6/22 15:52:48`

## 注意事项

- 第二次重复启动 Unity 构建时撞到仍在运行的第一次 batchmode 实例，Unity 日志 `localhost-build-20260622_155230.log` 报告项目已被另一 Unity 实例打开；该次重复启动不作为构建结果依据。
- 第一次构建继续完成并成功退出，最终以 `localhost-build-20260622_155100.log` 和 `Builds/Localhost` 的 15:52 新产物为准。
- `Builds/Localhost` 中仍保留旧的 `tuohuang.exe` / `tuohuang_Data` 等历史产物；按项目规则未进行批量删除。
