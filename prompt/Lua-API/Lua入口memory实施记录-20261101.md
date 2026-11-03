# Lua 入口 memory：跨调用保存与恢复

日期：2026-11-01。状态：已实现；原生 Unity EditMode 编译及本次 53 项隔离检查全部通过。本记录不代表正常对局、界面或联机验收。

## 1. 用户确认的合同

memory 跨多次发动保留，属于 Lua 入口的数据，不属于单张卡面的某个 Effect。A 记录、B 读取时，两者共用同一个 Lua 入口。需要区分玩家、实例或阶段时，由脚本在 table 中组织字段。

外部内容按内容包 ID 和已登记脚本路径生成稳定入口 ID；同一路径的不同 handlerId／abilityId 和完成回调共享，复制到不同路径不会因代码哈希相同而共享。存储索引还包含具名入口函数名；普通效果当前使用返回的 handler。C# 内置脚本默认按内容 ID 和处理器 ID 生成身份，可显式提供同一 EntryId。

## 2. 调用、提交与存档

- `ctx.memory` 首次为 `{}`，之后为上次提交数据的可修改深拷贝；ctx 本身仍只读。
- 原有 `return effects` 不变。新增 `return { effects = {...}, memory = {...} }`；effects 必填且可为空，memory 省略／nil 保留、空 table 清空、非空 table 整体覆写，不做合并。
- 修改副本但没有返回 memory 不保存。只接受受预算限制的纯数据，不保存 VM、闭包、函数、元表或对象引用。
- Lua 返回、归一化、编译通过后，在当前规则工作副本中替换 memory，和效果挂接一起提交；同次提交失败会一起丢弃。后续效果的失败不回滚已提交 memory；需要按成功结果记录时在完成回调里返回。
- 事件响应预览和没有实际执行 owner 的入口费用预览均不写入。候选／规则查询等专用返回合同明确拒绝 effects/memory 包装。
- `EffectRuntimeState` 新增 `LuaMemoryProtocolVersion` 与 `LuaMemories`。每个槽只存 EntryId、EntryPoint、ContentHash、Value，克隆、规则摘要及 Host 存档均覆盖这些字段。
- 等待选择期间可以保存，恢复后同入口继续读取；已提交分发收据避免重放。原始存档缺少已存在 memory 的字段时拒绝，不能默默补成空值；旧状态同时缺少两项新增字段时为空索引，其他恢复规则不放宽。
- 同入口脚本哈希改变时拒绝读取旧 memory，不静默迁移；原内容包校验继续生效。

## 3. 实现位置

| 部分 | 文件与改动 |
| --- | --- |
| 持久化及校验 | [LuaEntryMemoryState.cs](../../Assets/YC/Domain/State/LuaEntryMemoryState.cs)、[EffectRuntimeState.cs](../../Assets/YC/Domain/State/EffectRuntimeState.cs)：入口槽、纯数据校验、深拷贝、整表替换及原始字段检查 |
| Lua 输入／输出 | [LuaMemoryHost.cs](../../Assets/YC/Infrastructure/Lua/LuaMemoryHost.cs)、[LuaRuntimeContracts.cs](../../Assets/YC/Infrastructure/Lua/LuaRuntimeContracts.cs)、[MoonSharpLuaRuntimeHost.cs](../../Assets/YC/Infrastructure/Lua/MoonSharpLuaRuntimeHost.cs)：ctx.memory、副本、返回包装及错误处理 |
| 权威提交 | [LuaEffectEventDispatcher.cs](../../Assets/YC/Infrastructure/Lua/LuaEffectEventDispatcher.cs)、[SourceFactsLuaCatalog.cs](../../Assets/YC/Infrastructure/Lua/SourceFactsLuaCatalog.cs)：调用前读取、编译后写入、识别预览 |
| 入口身份 | [ExternalContentPack.cs](../../Assets/YC/Infrastructure/Lua/ExternalContentPack.cs)：只从已登记脚本路径生成 ID；角色、设施、城市样式、企业、能力来源、内容生命周期和外部事件目录传递同一 ID；重绑能力和完成回调保留身份 |
| 恢复 | [HostRecovery.cs](../../Assets/YC/Application/Sessions/HostRecovery.cs)：逐份 snapshot／journal StateAfter 校验原始 memory 字段 |
| 测试 | [LuaEntryMemoryTests.cs](../../Assets/YC/Tests/EditMode/ExpansionRules/LuaEntryMemoryTests.cs)：30 项新增检查 |
| 作者文档 | [Lua API 第 2.5 节](../../docs/设计/DIY内容开发-Lua%20API.md#25-ctxmemory同一-lua-入口的持久化数据)、[JSON 处理器配置](../../docs/设计/DIY内容开发-JSON编写.md)：共享入口、返回格式、保存时点、预算和 A/B 示例 |

纯数据沿现有 NormalizedValue 合同：字符串键对象或连续同类型数组、整数／布尔／字符串及嵌套 table，整数 ±1,000,000,000，字符串／键至多 1024 UTF-8 字节，整份至多 128 个值节点、8 层嵌套，并受宿主更小预算限制。每局至多 4096 个已写入槽，超限明确失败。

## 4. 验证证据

使用 `D:\2022.3.62f1c1\Editor\Unity.exe` 和项目正式 `tools/Run-EditModeTests.ps1`，没有运行 UI 生成器或正常玩法测试。

最终 [R6 XML](../../TestResults/LuaMemory-20261101-R6/editmode-20261101_182234.xml) 为 **53/53 通过**，无跳过；[原生日志](../../TestResults/LuaMemory-20261101-R6/editmode-20261101_182234.log)保留编译与执行结果：

- LuaEntryMemoryTests：30 项，覆盖 A/B 能力及玩家共享、跨次调用、深拷贝、整表覆写、清空／省略、非法数据、不同入口／对局隔离、哈希不一致、旧字段缺失、损坏原始存档拒绝、选择等待恢复与完成回调、同次分发回滚、费用预览不写入。
- LuaRuntimeHostEditModeTests：16 项，原有脚本返回和宿主合同。
- RuleStateDigestTests：4 项，摘要与克隆／序列化一致性。
- HostRecoveryCollectionTests：3 项，已有集合恢复合同。

首次失败保留：R1 测试辅助方法名错误造成编译失败；R2 修复 memory 误接到 GameData 而未接 ctx 的生产接线，并补足隔离夹具行动阶段；R3 补足夹具原局开局顺序，避免恢复自动补顺序导致摘要不一致；R4 查出候选返回检查会把包装误当空候选，已改为明确拒绝。R5 为 45/45；最终补上费用预览保护并扩大到摘要／恢复相关检查后，R6 为上述 53/53。各轮日志、失败 XML 与隔离证据均保留，没有修改失败记录或将其计为通过。

本次没有修改正式卡牌玩法脚本、场景或预制体，也没有创建提交。工作区原有未提交变更保留。
