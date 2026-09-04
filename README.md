# 游城拓荒模拟器

《游城拓荒：铸基者》2D Unity 模拟器项目。项目将基础版规则转化为可运行、可验证、可扩展的数字规则系统；仅用于学习、研究和开发实践，不涉及商业用途。

## 版本信息

- 当前 Alpha：`v0.4.2-alpha`；Unity `Application.version` / `PlayerSettings.bundleVersion` 为 `0.4.2-alpha`。
- 产品名称：`游城拓荒模拟器`。
- 公司/组织标识：`zhuanshiluobo`。
- Windows Release 目录：`Builds/v0.4.2-alpha/`，主程序为 `tuohuang.exe`。
- Windows Development 验收目录：`Builds/LocalhostDevelopment/`，不作为另一套版本号。
- 本版本说明：[发布说明 v0.4.2-alpha](docs/概览/发布说明_v0.4.2-alpha.md)。

## 当前状态

- Unity：`2022.3.62f2c1`
- **当前规则支持范围：标准四人地图与四人基础版流程。** 三人地图仍是 placeholder，不能作为已支持的正式模式。
- 五种基础城市样式特殊行动已完成 Host 权威规则结算、待处理会话与自动化交互闭环；人工 UI、三/四席多进程和真实 Steam P2P/Relay 验收尚未完成。
- Steam Lobby + Mirror/FizzySteamworks 联机 MVP 已接入；Mirror/Telepathy 可用于本地多实例命令同步验证。
- 2026-09-03 已重新运行完整 Unity EditMode：**`1014/1014 Passed`**，失败 `0`、跳过 `0`，耗时约 `28.9` 秒（任务 `06d770a182c540868e93d1ad9557373a`）。
- 保留日志中最近一次完整 Windows Player 构建证据为 2026-08-11 的 `Release030Alpha` 成功构建；它早于当前工作树，**不构成当前版本发布验收**。

## 当前能力

- 分层规则核心：`Domain`、`Application`、`Infrastructure`、`Presentation` 与 EditMode 测试程序集。
- 固定阶段流、命令校验、初始入场、四人地图、路线/路费、红区、影响力、移动、部署、调度、探索、建设、采集、收尾与最终计分。
- 41 张正式建设牌数据、设施待选结算、五张标准角色牌、城市样式旋转匹配及五种特殊行动。
- 城市样式/特殊行动、事件、角色、主题、网络运行时根和地图反馈均已接入持久化资产与生产 Prefab/Bootstrap。
- Host 权威命令、状态快照同步、等待房间开局门禁，以及本地多实例验证模式。

## 已知限制

- 三人地图缺少真实规则数据。
- 特殊行动的人工 UI 冒烟、Host + 两个 Client 专项三席、Host + 三个 Client 标准四席，以及真实 Steam 多账号 P2P/Relay 验收未执行。
- 存档、完整回放、日志详情、多人入场体验、企业扩展角色与正式对局 UI 未完成。

## 文档入口

当前状态只维护在以下三个入口；不要从历史工作记录、旧测试数字、静态行数或构造数量推断当前实现。

1. [项目总览](docs/概览/项目总览.md)：唯一当前事实源，记录能力范围、验证基线、构建状态和已知限制。
2. [发布检查清单](docs/概览/发布检查清单.md)：发布前的测试、构建、人工验收和已知问题收口项。
3. [文档索引](docs/README.md)：文档分类、事实源优先级和历史资料使用规则。

常用辅助资料：

- [项目术语表](docs/规则/项目术语表.md)
- [规则总结](docs/规则/规则总结.md)
- [总体架构](docs/架构/总体架构.md)
- [EditMode 命令行跑测](docs/指南/EditMode命令行跑测.md)
- [联机手册](docs/指南/联机手册.md)

## 工程目录

```text
Assets/YC/
  Domain/          纯规则、纯状态、纯查询，不依赖 UnityEngine
  Application/     GameSession、命令分发、流程编排
  Infrastructure/  联网、回放、后续存档与数据加载
  Presentation/    Unity UI、地图显示、输入适配
  Data/            地图、图片、图标和后续数据资产
  Editor/          编辑器导入与辅助工具
  Tests/EditMode/  规则和流程测试

docs/
  README.md        文档总入口与维护规则
  概览/            当前事实源与发布检查清单
  规则/            规则总结、术语和牌面文字
  架构/            总体架构与程序模块设计
  设计/            交互与流程设计目标
  指南/            测试、诊断、自动跑局和联机操作
  工作记录/        按日期归档的历史实现和验收记录
```

## 测试

统一使用仓库脚本运行 Unity EditMode 测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1
```

发布前还必须按[发布检查清单](docs/概览/发布检查清单.md)重新构建 Player，并完成相应的人工验收；自动化测试通过不能替代该流程。
