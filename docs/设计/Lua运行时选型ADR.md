# ADR：Lua 运行时选型与最小集成验证

- 状态：已接受（完成最小 spike，生产接入仍需通过内容管线与 IL2CPP 构建门禁）
- 日期：2026-09-15
- 任务：NMC-002
- 决策人：项目开发组

## 1. 背景与不可妥协约束

本项目的 Lua 用于“效果生成”，不是第二个权威规则引擎。Lua 脚本只能读取宿主传入的只读快照，并返回可归一化的 `EffectSpec[]`；状态校验、资源扣除、合法性判断、提交、回放和存档仍由 C# 主链负责。Lua 不得：

- 取得或修改 `GameState`、领域对象、Unity 对象、网络对象或任意可写 CLR 对象；
- 创建 UI、发起文件/网络/进程访问、读取系统时间或使用不可控随机数；
- 依赖持久化 VM、协程、闭包或脚本内状态完成回合逻辑；
- 直接让任意 Lua table 进入领域层或持久化层。

运行环境是 Unity `2022.3.62f1c1`，项目 API Compatibility Level 为 `.NET Standard 2.1`。选型必须覆盖 Windows Editor、Mono/IL2CPP、移动端和未来 WebGL 的可行性；依赖应可锁定、可审计且不引入不必要的原生插件。

## 2. 候选比较

| 候选 | 执行与平台 | 沙箱/宿主边界 | AOT/IL2CPP 风险 | 依赖与维护风险 | 结论 |
|---|---|---|---|---|---|
| MoonSharp | 纯 C# Lua 解释器；官方说明覆盖 Unity、AOT、IL2CPP，无外部运行时依赖 | 有 `CoreModules` 选择与硬沙箱文档；可以只暴露 Lua table 和显式回调，不注册 UserData | 主要是 C# 解释执行；仍需在项目 IL2CPP 目标上做烟测 | v3 当前为 beta；UPM 分支可直接接入，但 package manifest 标注 MIT 与仓库许可证文本存在差异，需法务/依赖审计 | **选定用于 spike 与第一阶段适配层** |
| xLua | Lua/LuaJIT 与 C# 桥接能力强，支持生成适配器、热补丁 | 能力面很宽，默认不是安全沙箱；需要严格限制 `LuaCallCSharp`、生成代码和暴露类型 | 每个平台需要 native 源码/库；iOS、Android、IL2CPP 需要额外生成代码和 `link.xml`/裁剪配置 | 原生编译链、平台库和第三方组件较多；适合已有 Lua 桥接体系，不适合本任务的最小权限优先 | 暂不选 |
| NLua/KeraLua | .NET 到 Lua 的桥接，Lua 执行依赖 KeraLua | 可自建白名单，但桥接层更容易把 CLR 能力带入脚本 | 原生 KeraLua runtime 需要按平台分发/验证 | Unity 集成涉及 KeraLua 与 native runtime；跨平台发布和 IL2CPP 资产审计成本更高 | 暂不选 |

依据：MoonSharp 官方 [README](https://github.com/moonsharp-devs/moonsharp)、[沙箱文档](https://www.moonsharp.org/sandbox.html) 和 [执行预算 recipe](https://www.moonsharp.org/recipes.html)；xLua 官方 [README](https://github.com/Tencent/xLua/blob/master/README_EN.md)、[FAQ](https://github.com/Tencent/xLua/blob/master/Assets/XLua/Doc/Faq_EN.md) 和 [配置文档](https://github.com/Tencent/xLua/blob/master/Assets/XLua/Doc/Configure_EN.md)；NLua 官方 [仓库](https://github.com/NLua/NLua)。

## 3. 决策

采用 MoonSharp 作为第一阶段 Lua 运行时，当前锁定：

```text
org.moonsharp.moonsharp
https://github.com/moonsharp-devs/moonsharp.git?path=/interpreter#0fb8ba9106c44b140b8f56cb44cb1b50b358897c
package version: 3.0.0-beta.1
```

锁定的是官方 `upm/beta/v3.0` 分支的 commit `0fb8ba9106c44b140b8f56cb44cb1b50b358897c`，而不是浮动分支。`Packages/manifest.json` 与 `Packages/packages-lock.json` 已同步。之所以在 spike 阶段选 v3 beta，是因为该提交带有可直接被 Unity Package Manager 解析的 `interpreter` UPM 子包并且已在本项目 Unity 版本实测；它不是“生产许可证/稳定性已无条件通过”的结论。正式发版前必须重新评估稳定版、固定内部 fork 或继续使用该版本。

### 3.1 许可证与供应链门禁

MoonSharp UPM 子包的 `package.json` 标注 `MIT`，而官方仓库根目录 `LICENSE` 为 BSD 3-Clause，并包含第三方声明。这一差异不能被忽略。当前 ADR 只批准技术 spike；进入发行构建前必须：

1. 固定源码 commit 并保存包源、hash、许可证和第三方 notices；
2. 让法务/发行负责人确认 BSD 3-Clause、Lua/KopiLua 等第三方条款及项目分发方式；
3. 若无法确认，改为项目内审计过的 fork/包归档，并在构建产物中保留 notices。

官方许可证来源：[MoonSharp LICENSE](https://github.com/moonsharp-devs/moonsharp/blob/master/LICENSE)。

## 4. 最小 spike 实现

### 4.1 程序集边界

新增 `YC.Infrastructure.Lua`：

- `YC.Infrastructure.Lua.asmdef` 只引用 `MoonSharp.Interpreter`；
- `noEngineReferences: true`，不引用 `UnityEngine`、`YC.Domain` 或 `YC.Application`；
- `YC.Tests.EditMode` 只通过程序集引用测试宿主；
- 宿主输出是普通 C# DTO，后续可由 Application 层复制/转换到正式 EffectSpec，不把 MoonSharp 类型带出边界。

### 4.2 脚本协议

脚本正文必须返回一个 `handler(ctx)` 函数。调用时宿主只传入只读 `ctx`，脚本返回连续数组：

```lua
return function(ctx)
    local game = GameData.Get()
    return {
        Effect.GainResource({
            recipient = ctx.playerId,
            resourceType = 'gold_voucher',
            amount = game.playerCount + 1,
            reasonId = 'event.demo'
        })
    }
end
```

当前 spike 只登记两个构造器/效果类型：

- `Effect.GainResource` → `effect.resource.gain`，资源白名单暂只有 `gold_voucher`；
- `Effect.GainScore` → `effect.score.gain`。

C# 归一化器会重新检查 `kind`、`effectTypeId`、字段白名单、必填字段、字段类型、玩家目标、整数性和 `1..100` 数值范围。只有归一化后的字符串和整数 DTO 才能离开宿主；不会保存 Lua table、UserData、Unity 对象、闭包或 VM。

### 4.3 沙箱与权限

宿主显式选择模块集合，而不是直接使用默认模块：

```text
GlobalConsts | TableIterators | String | Table | Basic | Bit32
```

因此没有加载 `IO`、`OS_System`、`Dynamic`、`Debug`、`Coroutine`、`LoadMethods` 和 `Math`。特别移除 `Math` 是为了不提供 `math.random/randomseed`；本 spike 没有时间源或随机源。`print` 被导向空 sink。

宿主还将 `os`、`io`、`debug`、`package`、`coroutine`、`math`、`CS`、`System`、`UnityEngine` 等名称接到会产生明确 `ForbiddenApi` 失败的代理上，并将 `require/load/loadfile/dofile` 设为禁用回调。没有任何 `UserData` 注册；`UserData.RegistrationPolicy` 明确设为 `Default`，不使用官方沙箱文档警告的 `Automatic`。

`ctx`、`GameData.Get()`、`PlayerData.Get()` 返回的是只读代理。写入会得到 `ReadOnlySnapshot`；读取其他玩家快照会得到 `ForbiddenStateAccess`。脚本可以修改自己创建的临时 table，但这种修改不会触及宿主状态，且返回前仍由 C# 重新校验。

### 4.4 确定性与资源预算

每次调用新建 `Script`，不保存 VM。脚本只能看到调用上下文快照和白名单回调；无时间、随机、I/O、网络、CLR 反射入口，因而在相同内容 hash、版本和输入快照下，输出顺序由 Lua 返回数组顺序决定。

默认预算如下：

| 项目 | 默认值 | 处理方式 |
|---|---:|---|
| 指令数 | 10,000 | 通过 MoonSharp coroutine 的 `AutoYieldCounter` 预抢占；yield 表示失败并返回 `InstructionBudgetExceeded` |
| 执行时间 | 100 ms | 宿主入口和每个白名单回调检查 `Stopwatch`；超限返回 `TimeBudgetExceeded` |
| 脚本正文 | 32 KiB | 入口校验，超限返回 `ScriptTooLarge` |
| 返回 EffectSpec 数 | 16 | 归一化前检查，超限返回 `ReturnLimitExceeded` |
| 返回 table 深度 | 8 | 递归检查，超限返回 `TableDepthExceeded` |
| 单 table 表项 | 128 | 递归检查，超限返回 `TableEntryLimitExceeded` |

这些是宿主级拒绝预算，不等于操作系统级内存隔离：解释器在一次指令间隔内的分配无法被安全地强制回收，当前 spike 也没有进程级 CPU/内存 cgroup。若未来内容来自不可信用户、需要对抗恶意资源消耗，必须将 Lua 放入独立进程/沙箱服务，或换用具备硬资源隔离的执行环境；不能仅依赖本 ADR 的进程内预算。

### 4.5 版本与内容完整性

`LuaScriptDefinition` 携带 `contentId`、`abilityId`、`handlerId`、`definitionVersion`、正文和 UTF-8 SHA-256 `contentHash`。宿主执行前：

1. 重新计算正文 hash，拒绝 `ContentHashMismatch`；
2. 将定义版本与调用上下文版本做 ordinal 精确比较，拒绝 `VersionMismatch`；
3. 再编译、执行和归一化。

这为后续的内容 manifest、回放记录和 `commitSequence/stateRevision` 关联提供了稳定入口；NMC-002 不提前实现完整 EffectExecutor、收据或存档迁移。

## 5. 验证证据

新增 `Assets/YC/Tests/EditMode/LuaRuntimeHostEditModeTests.cs`，使用 Unity 实际 EditMode runner 验证：

- 有效只读上下文读取与 EffectSpec 归一化；
- 非法字段类型；
- 数值越界；
- 未登记效果类型；
- 禁止 API；
- 只读上下文写入；
- Lua `error` 异常；
- 无限循环指令预算耗尽；
- 版本不匹配；
- 内容 hash 不匹配；
- 非数组返回形状。

执行命令：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-EditModeTests.ps1 `
  -UnityPath 'D:\2022.3.62f1c1\Editor\Unity.exe' `
  -ProjectPath 'G:\NoMadCity' `
  -TestPlatform EditMode `
  -TestFilter 'LuaRuntimeHostEditModeTests' `
  -OutputDirectory 'Temp\NMC002-LuaTargeted3' `
  -LogFile 'Temp\NMC002-LuaTargeted3\editor.log' `
  -ResultsFile 'Temp\NMC002-LuaTargeted3\results.xml'
```

结果：`total=12, passed=12, failed=0, skipped=0`，Unity 退出码为 `0`。包解析日志确认 `org.moonsharp.moonsharp@0fb8ba9106` 已注册，包版本为 `3.0.0-beta.1`。

## 6. 未完成项与后续门禁

本 ADR 不宣称完整生产接入已经完成。进入下一任务前需要：

1. 在可用的目标构建配置上跑 Windows/Android/iOS/WebGL IL2CPP smoke，验证链接裁剪、脚本编译和包分发；当前 NMC-002 只有 Unity Editor 环境，因此没有伪造 IL2CPP 通过证据。
2. 把当前 spike DTO 对接到正式 `EffectSpec` schema、schema registry、Event handler ID、内容 manifest/hash 和回放 receipt；保留 C# 唯一提交权。
3. 用实际资源/分数/位置效果扩展白名单，每个效果类型先补“合法、非法、越界、重复提交/版本错配”测试，再进入内容迁移。
4. 确定脚本错误、预算耗尽、版本错配在回合主链中的可观测失败语义；错误文本只作诊断，网络/存档层使用稳定 fault code。
5. 完成 MoonSharp beta 与许可证差异的依赖审计；若 beta 或许可证门禁不通过，保留本适配层接口，替换实现而不让领域层依赖 MoonSharp。

## 7. 结论

MoonSharp 在本项目当前阶段的关键优势是“纯 C#、可在 Unity/AOT/IL2CPP 路径运行、无原生 runtime、可显式裁剪模块”，最符合 Lua 作为受限 `EffectSpec` 生成器的边界。选型成立的前提不是解释器天然安全，而是本实现坚持最小模块集、无 UserData、只读快照、C# 归一化和显式资源预算。生产发布仍以目标 IL2CPP 烟测、依赖许可证审计和正式内容/回放门禁为准。
