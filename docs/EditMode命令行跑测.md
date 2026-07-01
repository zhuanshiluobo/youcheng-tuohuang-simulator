# EditMode 命令行跑测

本项目是 Unity 工程，测试入口使用 Unity Test Runner，不依赖 `.sln` 或 `.csproj`。

仓库内入口：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1
```

统一使用仓库内脚本启动测试，不直接调用某个固定位置的 `Unity.exe`。

脚本会自动查找 `2022.3.62f2c1` 的常见安装位置，包括 Unity Hub 标准目录和盘符根目录安装。

如果 Unity 安装在脚本无法发现的位置，可以传入显式路径，或设置 `UNITY_EDITOR_PATH`：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 `
  -UnityPath "D:\Unity\Hub\Editor\2022.3.62f2c1\Editor\Unity.exe"
```

默认输出目录为 `Logs\EditModeTests`，会生成 Unity 日志和 NUnit XML 结果文件。该目录在仓库忽略规则内，不需要提交。

## 结果判定

脚本启动 Unity 前会检查当前项目是否已经被 Unity 打开：

- Windows 上会读取已有 `Unity.exe` 进程命令行。
- 如果发现同一项目路径，会输出 PID、命令行和项目路径后退出。
- 如果发现 `Temp\UnityLockfile`，会提示锁文件路径和写入时间。确认没有 Unity 进程后，可人工处理陈旧锁文件，或临时传入 `-SkipProjectLockCheck` 交给 Unity 自行判断。

Unity 退出后，脚本不会立刻认定结果文件缺失，而是等待 `-testResults` 指定的 XML 非空并稳定。默认最多等待 `120` 秒，稳定时间为 `1000` 毫秒。

最终成功/失败以 XML 根节点 `<test-run>` 为准：

- `result=Passed` 且 `failed=0`：脚本退出码为 `0`。
- 其他 XML 结果：脚本退出码为 `1`。
- 若 Unity 进程退出码非 0，但 XML 明确显示全部通过，脚本会输出 warning，并按 XML 通过处理。
- 若等待超时仍没有稳定 XML，脚本退出码为 `1`，需要查看对应 Unity log。

## 常用参数

- `-TestFilter`：按测试类、命名空间或测试名过滤。
- `-LogFile`：指定 Unity 日志文件。
- `-ResultsFile`：指定 NUnit XML 结果文件。
- `-OutputDirectory`：指定默认日志和结果目录。
- `-UnityVersion`：覆盖默认查找版本，默认是 `2022.3.62f2c1`。
- `-UnityPath`：绕过自动发现，直接使用指定 Editor。
- `-ResultsWaitTimeoutSeconds`：等待 NUnit XML 生成并稳定的最长秒数，默认 `120`。
- `-ResultsStableMilliseconds`：XML 文件大小和写入时间保持稳定的毫秒数，默认 `1000`。
- `-SkipProjectLockCheck`：跳过脚本侧 Unity 进程和 `Temp\UnityLockfile` 检查，仅在确认锁文件为陈旧状态等特殊情况下使用。
- `-NoGraphics`：追加 Unity `-nographics`。
- `-ExtraUnityArgs`：追加其他 Unity 命令行参数。

示例：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 `
  -TestFilter "YC.Tests.EditMode.ExploreLocationCommandHandlerTests" `
  -LogFile ".\Logs\EditModeTests\explore.log" `
  -ResultsFile ".\Logs\EditModeTests\explore.xml"
```

## 今天新增测试的跑法

探索相关：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 `
  -TestFilter "YC.Tests.EditMode.ExploreLocationCommandHandlerTests"
```

航道表现配置相关：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 `
  -TestFilter "YC.Tests.EditMode.MapRouteDisplayDefinitionTests"
```

联网命令同步相关：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 `
  -TestFilter "YC.Tests.EditMode.NetworkCommandDispatcherTests"
```

一次跑这三类测试时，Unity 的 `-testFilter` 不适合表达多组 OR 过滤。建议连续执行三条命令，并分别输出结果文件：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 -TestFilter "YC.Tests.EditMode.ExploreLocationCommandHandlerTests" -ResultsFile ".\Logs\EditModeTests\explore.xml" -LogFile ".\Logs\EditModeTests\explore.log"
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 -TestFilter "YC.Tests.EditMode.MapRouteDisplayDefinitionTests" -ResultsFile ".\Logs\EditModeTests\map-route.xml" -LogFile ".\Logs\EditModeTests\map-route.log"
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 -TestFilter "YC.Tests.EditMode.NetworkCommandDispatcherTests" -ResultsFile ".\Logs\EditModeTests\network.xml" -LogFile ".\Logs\EditModeTests\network.log"
```
