# 游城拓荒模拟器

《游城拓荒：铸基者》2D Unity 模拟器项目。项目目标是把基础版规则转化为可运行、可验证、可扩展的数字规则系统。当前已具备 3 到 4 人热座主要规则流程、五种基础城市样式特殊行动的自动化闭环，并已接入 Steam Lobby + Mirror 的 Host 权威联机 MVP；后续继续补齐三人地图、存档回放、正式 UI 与真实网络验收。

本项目为个人学习版与规则模拟练习项目，仅用于学习、研究和开发实践，不涉及商业用途。

## 当前状态

- Unity 工程根目录：`F:\游城拓荒模拟器`
- Unity 版本：`2022.3.62f2c1`
- 文档总入口：`docs/README.md`
- 当前事实源：`docs/概览/项目总览.md`
- 术语统一依据：`docs/规则/项目术语表.md`
- 阶段计划（功能实现已完成，人工验收待收口）：`docs/计划/2026-07_下一阶段完整计划.md`
- 当前重点：特殊行动的人工 UI 与联机交付验收、三人地图真实规则数据、存档回放、正式对局 UI，以及 Steam 多账号真实网络验收。

当前已经落地：

- `Assets/YC` 分层目录与 `YC.Domain`、`YC.Application`、`YC.Tests.EditMode` 程序集。
- `GameState`、`PlayerState`、命令、事件、校验结果和 `GameSession.Submit`。
- 固定阶段流、命令阶段校验、起始玩家和初始地块入场流程。
- 四人地图结构化数据、地图查询、路线搜索、路费计算和红区限制。
- 移动城市、部署、调度、探索、建设、采集、结束行动等命令处理。
- 影响力放置、调度、移除、替换、查询和区块统计服务。
- 事件牌基础数据、三色事件牌堆、资源点指示物和探索事件选择入口。
- 资源采集阶段、收尾推进和最终计分基础闭环；最终计分已结算基础分、区控分、资源分、设施分、城市样式分与平局判定。
- 41 张正式建设牌元数据、16 类正式牌效果契约、2 类备用设施效果契约、设施待选交互、供应槽位定点补牌与自动跑局续结算。
- 雷蛇、极境、德克萨斯、坎诺特、锡人五张标准角色牌的盖放、策略/计谋、待选结算、收尾回收和隐藏信息 UI。
- 城市样式四方向旋转匹配、轨迹选块与统一结算、分数轨显示，以及开始页、地图交互、行动面板、信息面板、设置/规则书入口和卡牌查看器。
- 军工化区域、动员配套体系、复合动力系统、源石工业中枢和高效移动管理体系五种特殊行动的 Host 权威结算、I/II 级标记生命周期、额外主要行动、角色牌行动轮限制和多步待处理状态。
- 特殊行动从公共样式卡预览中的本方可用影响力标记拖拽发动；复合动力系统在提交前选择支付组合，Host 受理后由地图高亮、事件弹窗和可恢复会话继续结算，不再提供独立的行动面板按钮。
- Steam Lobby + Mirror/FizzySteamworks 联机 MVP，以及 Mirror/Telepathy 本地多实例测试模式；等待房间按 Lobby 成员、传输连接和身份验证执行 Host 权威开局门禁。
- 最高等级编辑器资产化已收口：UI Theme、Event/Character、CityStyle/SpecialAction、网络运行时根和地图反馈资源均由持久资产与生产 Bootstrap/Prefab 提供；高价值布局硬编码已分批迁移。
- EditMode 测试脚本与地图、路径、费用、影响力、移动、探索、建设、特殊行动、采集、最终计分、自动跑局、联网等测试。

仍未完成：

- 特殊行动的人工 UI 冒烟、Editor Host + 两个 Development Build Client 的专项三席冒烟、标准四人入口的 Host + 三个 Client 验收，以及多个 Steam 账号的真实 P2P/Relay 验收尚未执行；自动化闭环不能替代这些人工交付验证。
- 设置菜单的音量、画面等通用设置仍待扩展。
- 三人地图真实规则数据。
- 企业扩展角色与规则。
- 完整入场事件体验、日志详情、存档、回放和正式对局 UI。

## 文档入口

先阅读 `docs/README.md`，它提供全部文档的分类索引、事实源优先级和维护规则。常用入口：

- `docs/概览/项目总览.md`：项目定位、当前进展、限制和下一步，是人工维护的主事实源。
- `docs/规则/项目术语表.md`：统一“地块、资源点、航道、路线、路径、主要行动、快速行动”等术语。
- `docs/规则/规则总结.md`：基础版与扩展规则整理稿。
- `docs/架构/总体架构.md`：当前分层、约束、技术选型与架构缺口。
- `docs/计划/2026-07_下一阶段完整计划.md`：当前阶段的设计、开发、优化、审查、里程碑和验收标准。
- `docs/指南/EditMode命令行跑测.md`：Unity EditMode 测试脚本和常用过滤方式。
- `docs/指南/联机手册.md`：Steam 联机与 Mirror 本地多实例操作。
- `docs/设计/特殊行动实现规格与工作清单.md`：五种基础特殊行动的已实现规则、代码结构、交互契约和验收状态。
- `docs/工作记录/已完成模块工作记录/2026-07-22_特殊行动完整结算与样式卡拖拽交互记录.md`：特殊行动实现、测试、Development Build 与自动跑局证据。
- `docs/工作记录/README.md`：按日期索引开发记录，并标注历史方案的替代关系。
- `docs/工作记录/2026-08-12_编辑器资产化迁移进度与续接说明.md`：本轮最高等级资产化完成证据、明确延期项和跨对话续接清单。

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
  README.md         文档总入口与维护规则
  概览/             项目现状与事实源
  规则/             规则总结、术语和牌面文字
  架构/             总体架构与程序模块设计
  设计/             交互与流程设计目标
  计划/             当前阶段范围、里程碑和验收标准
  指南/             测试、诊断、自动跑局和联机操作
  数据/             人工校验后的结构化参考资料
  模块文档/         可落地模块规格
  工作记录/         当前总结、日期索引与历史记录

游城拓荒/          规则书、页面图和素材来源
```

## 架构原则

- `Domain` 只表达规则，不引用 `UnityEngine`。
- 所有玩家操作统一通过命令系统进入校验与结算流水线。
- 非法命令返回明确错误，不修改 `GameState`。
- `GameState` 必须可序列化，支持存档、回放、测试和联网同步。
- 地图图片只用于显示，规则必须使用结构化地图数据。
- UI 只读取状态并提交命令，不直接写玩法规则。
- 表现坐标与规则 id 分离，表现数据不能参与规则结算。
- 新增规则至少配成功路径和失败路径测试。

## 测试

统一使用仓库脚本运行 Unity EditMode 测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1
```

常用过滤示例：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 `
  -TestFilter "YC.Tests.EditMode.ExploreLocationCommandHandlerTests"
```

测试说明见 `docs/指南/EditMode命令行跑测.md`。

2026-08-12 最近验证：

- 编辑器资产化收口：完整 EditMode `967/967`、PlayMode `2/2`、Console error `0`，各逻辑批次均经独立只读审核。
- 后续桌面透视与导航改造：完整 EditMode `993/993`、PlayMode `2/2`，三档缩放四边贴合误差均小于 `0.001`。
- 最新改动尚未重新执行 Development Build、构建产物输入验收和真实多 Steam 账号联机；这些仍属于发布前人工验收范围。

2026-07-22 历史验证：

- 特殊行动专项：`Logs/EditModeTests/editmode-20260722_194602.xml`，`41/41 Passed`。
- 完整 EditMode：`Logs/EditModeTests/editmode-20260722_194715.xml`，`692/692 Passed`。
- `tools/tests/BuildLocalhost.ps1 -Development` 构建成功，产物为 `Builds/LocalhostDevelopment/tuohuang.exe`。
- 最终构建日志为 `Logs/localhost-development-build.log`；构建产物自动跑局日志为 `Logs/LocalhostBuild/localhost-development-autoplay-20260722_1938.log`，正式发动 I 级和 II 级特殊行动各一次并进入最终计分。人工 UI、三进程与多 Steam 账号验收仍待执行。



