# Host 归档批末检查点修订合同

2026-10-10。依据本轮真实无 UI 测试失败及共享报告的 schema 4 提案，由协调者审定。用户已授权实现修复和 API 协调；这是持久化实现修订，无新增规则或 UI 决策。生产与专用测试唯一 owner：执行者：共享集成（01a1217b-9745-7430-81a7-6672d2ba8932）。协调者只写本合同，不改代码。

## 证据与范围

`G:\NoMadCity\Logs\GameplayPresentation\多人并发离线验证\首次检查结果.json` 中企业、入场、盖放的三个正式提交后追加/恢复用例失败。`冻结归档诊断-内部提交.json` 分别记录 revision 12→16、12→34、24→28，一次追加 5、25、6 条真实记录。现行 `AppendJournal` 为所有较早记录复制批末状态，恢复要求每条记录 revision 与状态精确相等，合同自相矛盾。

批准修改 `G:\NoMadCity\Assets\YC\Application\Sessions\HostRecovery.cs` 中 DTO 版本、追加、JSON 原始字段校验和恢复的必要分支；使用同一文件的私有 helper。允许在 `G:\NoMadCity\Assets\YC\Tests\EditMode\HostJournalCheckpointTests.cs` 及其正常 `.meta` 新建自有专用检查，先在自身报告登记。共享原并发测试/runner的已授权修补继续。不得修改 RuleCommit 热路径、网络 DTO、EffectRuntime 协议、Driver、规则 owner 文件或 UI 资产。

## schema 4 合同

1. `HostSnapshotDto.CurrentSchemaVersion`、`HostJournalDto.CurrentSchemaVersion` 升为 4，新建档案使用 4。保留所有原日志 sequence/revision/commitId/commandId/kind/entity/detail/content 元数据，不把较早 revision 改成最后 revision，不丢中间行。
2. 每次 `AppendJournal` 先以局部列表核完整连续新增记录及最终边界，仅该批末条的 `StateAfter` 保存一次真实最终状态克隆，中间条明确为 null。校验完成后一次性追加；拒绝时原 archive 不得留下半批条目。无新增记录时不制造空批次或额外快照。
3. 每个非空 `StateAfter` 是一个批次的末端。恢复先核原连续性/同 revision 同 commit/内容与身份规则；末 checkpoint 的 revision 精确等末条 revision，实际最后 sequence 精确等末条 sequence，`NextCommitSequence` 精确等末 sequence＋1。checkpoint 的真实 journal 逐条匹配本批全部记录，含原 revision、commit、entity、kind、command、detail 和内容元数据；收据与真实 receipt 对应。不能允许后批 checkpoint 悄悄改写先前已接受 journal 前缀。
4. 原 GameId、LaunchPlayerOrder、内容/handler、runtime 与业务恢复 validator 保持。只在完整批次验证后推进 recovered，尾截断/缺行/重复或倒退/不一致必须拒绝，不恢复半个权威提交。JSON 与对象入口语义相同；schema 4 只对非空 checkpoint 校验完整状态字段，null 中间项仍必须有完整合法日志元数据。
5. 不在每个内部 commit 复制状态，不新增每帧/提交热路径完整业务验算。一次 Append 只为本次最终 checkpoint 做一次需要的完整 clone；恢复侧的严格校验不变成日常 UI 或规则推进的重复检查。

## 旧档兼容决定

schema 2/3 的读取与恢复保持原逐条 checkpoint 严格规则；本轮已写坏的 schema 3 必须继续明确失败，禁止按新批次格式猜测修复。不要只替换 CurrentSchemaVersion 常量而使原合法 2/3 被拒绝。

**旧档转新档采用调用者显式执行既有 API：** 先 `Recover`/`RecoverJson` 成功，再用返回的真实 State 调用 `CreateArchive` 形成 schema 4 新基线，然后追加新提交。保留状态内原 journal，原旧档对象/文件不自动改写。本轮 `AppendJournal` 对 schema 2/3 的直接追加明确拒绝并给出迁移诊断，不静默重置/升级、不新增平行迁移接口。当前生产 Driver 的归档入口本就 `CreateHostArchive` 新建，本批无需改 Driver；发现其他真实旧档续写调用者时给出精确路径供根分派，不抢改其他 owner。

## 必要验证与放行

共享自有补充检查覆盖：真实多内部 commit 一批、连续两次 Append、同 commit 多实体、无新增提交、对象/JSON一致；缺中间行和无checkpoint尾截断；原 revision/commit/entity/receipt 篡改；末 revision/sequence/NextCommitSequence 篡改；内容与启动顺序不符；拒绝不部分追加；合法 2/3 恢复及显式重新建档后续写；无效旧 3 仍拒绝。可合并参数化，不跑旧全套。

规则再次停写后，共享独占当前受影响装配并复验首轮失败及上述新增边界，准确记录过滤器、原失败、受测输入和哈希。HostRecovery修改影响成功的恢复分支时补验对应原已通过方法并说明理由，不能仅测新增类。通过后交稳定 Domain/Application/Lua 和窗口释放回执，根接续 UI 六方法、旧夹具最小21例及 Policy 真实联接。当前仍不启动Unity/构建/完整局/点击；不能将托管通过称为原生联机或整体验收完成。
