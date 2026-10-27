> 历史记录（整理前原文）。不作为现行派发授权；当前要求见 [现行总览](../../../README.md)。本页链接已按新位置调整，原始字节见 [原始快照](../原始字节快照.zip)。

# 本地正式Profiler操作帧最小取证
2026-10-26，209，QY-004 R8（LOCAL-PROFILER-FRAMES-R8）。这是当前已派任务，压缩后以本文件为工作锚点，勿回到原始“等待派发”。019/qy暂停，018/008/017全部结束停写，你独占本轮主Unity、私有官方分析宿主与一个新Player。

## 已核前置
R7根核13原生/210封存/5953源/771包，正常220提交Private约2→6.3GiB，采集T0→T1仍0.5—2.7秒。R28唯一生产EffectKernel Run调度工作图复用，146封存45728960B和三源根核同，Clone184→120/Copy172→236，隔离分配仅降7.206%，CPU无稳定改善、后段隔离Private更高；五有限过，尚未Unity原生。不能称优化成功。
R29已正式工具尝试，114封存45608739B、1276生产冻结输入根核同，短局93提交正常退出。Profiler实际连接但没有帧，两文件各4B；Player Managed Callstacks Supported No，缓冲停止并添约0.52GBnative成本。不能把该曲线当正常业务基准。原包不可变、R28未在旧R7中。018全部自有宿主已退出。
先读根AGENTS、01/08、207/208，R28和R29完整回执。R29私有候选：实施记录/QY-018-MONO-ALLOCATION-PROFILE-R29/仅私有-默认关闭诊断候选.patch，根已全读；它尚未应用生产。官方分析项目 .codex-tmp/QY018-memory-analysis-r27 与MemoryProfiler1.0.0可复用，原包/原件只读。

## 目标和允许的改动
本轮重点是取得可解码、至少覆盖一笔完整真实操作的正式GC.Alloc帧，按Clone/Copy与其它阶段区分主要分配；不是又做一次未归因600秒正常复测，不四端，不完整局。
允许应用已审R29默认关闭诊断候选的三个文件：Domain/State/GameStateCloneService.cs仅诊断桥；Presentation/Validation/QY018MonoTrace.cs及meta。缺显式参数不创建对象、不启Profiler、不改GC/规则。保原单参数入口、线程scope/异常/嵌套和清理，不持有GameState。
候选原45秒后任意两帧可能全是空闲，不可仅文件有字节就算成功。授权在同一诊断组件及确实需要的Performance.cs一个既有真实命令提交边界做最小采集关联：用真实命令/提交或GameState Clone阶段触发有界记录，能证明窗口含实际提交起止或明确包含了哪部分；不伪造命令、不改规则调用。优先复用已有原CommandId/revision/tick观察；不能扩新驱动框架、全协议/每帧长日志。取样范围至多3个短窗口，每窗口2—4个真实完整帧（一个同步长帧不拆作多帧），若只捕到部分操作，准确说明并在同一正常局用下一实际命令补一次。不要通过单纯加等待时间猜动作发生。
正式Profiler直接由Player本地binary log落盘，不依赖R29失败的远程socket。关闭Deep Profiling，初次不请求managed allocation callstacks；目标不支持时用明确父sample计量，不能凭开关假装得到调用栈。仅主线程正常热座，线程表真实覆盖范围据实记录，不修改网络线程。记录诊断缓冲/本身native开销、GC mode/incremental实际API值，但不改GC策略、不强制GC。
不改其它生产业务源码、Content/Lua/资产/固定文案、Packages/ProjectSettings；R28代码保持，不借此次改新业务。若本地binary API还不能产生有效帧，保原始错误，按确切故障收口，不能增加buffers反复重开/构建或换自写binary解析器。

## 一次构建与最小真实运行
先跑必要5项 QY018SchedulingTransactionTests Unity原生（R28新改），加诊断桥原4有限和默认关闭检查；诊断改动若影响断言，只按真实合同修，不改失败期望。禁止重跑旧13/20/全库矩阵。通过后一次成功Development包 Builds/QY004-R8-20261026-local-profiler；不安装器、不UI Rebuild。保前后全部输入SHA、MVID/Content、必要Build日志。首次失败保存，不把未过原生说成已过。
一个可见单Player，无扩展四人热座正常菜单开局，沿现有真实UI驱动合法候选，独立保存根；无CUA/外部鼠标/系统授权点击，防火墙提示本身不作阻塞，无注入状态/资源/随机。显式不超过90—135秒或120成功提交/600输入；正常预算到即退出，不再跑600秒/220次/15分钟静置。
本地窗口采完后用自己的官方Editor RawFrameDataView/ProfilerDriver读取日志，先确认Load成功、有效帧数、线程表和真实GC.Alloc样本。空文件/只头/zero frame均失败，不包装成零分配。无需为追目标反复开新局。捕获原件总量上限10GiB，buffer沿512MiB上限且只在短窗口打开，用完恢复原值；不得一直开Profiler跑全局。不盖掉用户原Profiler设置/日志。

## 有效数据须能解释的内容
按GameId/CommandId/revision/帧号、源tick把该真实提交与原操作关联。至少列总GC.Alloc字节/次数，整图Clone/Copy父sample内的分配及互斥剩余（父子包含关系不重复加总），主线程执行时间、GC标记、Mono used/reserved、Unity allocated、外部OS Private。Profiler带来的开销单列，不将带诊断Private当无干预正常收益。
没有managed callstack就明确只能阶段归属、不能对象类型排名或全部根。分配数据如指向整图复制支配，说明份额和对应正式阶段；若主要在其它阶段，也如实推翻先验。此轮不再修业务代码，交根安排准确主因处理。

## 收口
证据目录实施记录/QY-004-LOCAL-PROFILER-FRAMES-R8。实际有效/失败帧、原件和可复核官方导出、全部patch/有限与原生结果、包SHA/MVID、默认关闭/正确恢复、实际合法提交及自有PID正常退出、完整回执和封存，之后停写。用户019暂停不变。
所有搜索明确限定路径，不能无限定Git列受限UI原档案。根只改文档且只wait整轮后读最终；不要给其它聊天发消息。
