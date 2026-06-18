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

## 常用参数

- `-TestFilter`：按测试类、命名空间或测试名过滤。
- `-LogFile`：指定 Unity 日志文件。
- `-ResultsFile`：指定 NUnit XML 结果文件。
- `-OutputDirectory`：指定默认日志和结果目录。
- `-UnityVersion`：覆盖默认查找版本，默认是 `2022.3.62f2c1`。
- `-UnityPath`：绕过自动发现，直接使用指定 Editor。
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
