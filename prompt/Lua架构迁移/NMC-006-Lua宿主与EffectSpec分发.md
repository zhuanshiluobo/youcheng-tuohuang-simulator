# NMC-006：Lua 宿主、EffectSpec 与分发

## 任务提示词

你负责把 `NMC-002` 选定的 Lua 运行时接入 `NMC-004` Effect 内核，完成只读 Host API、EffectSpec 编译、订阅分发和持久化 continuation。该任务建立内容平台，不迁移整批业务内容。

## 前置任务

`NMC-002`、`NMC-003`、`NMC-004` 均已验收。

## 目标

1. 实现隔离的 `LuaRuntimeHost` 或等价适配器，保持 Domain/Application 不依赖第三方 Lua。
2. 实现 `Global`、`GameData`、`PlayerData` 等只读、确定性 façade。
3. 将 Lua 返回的受限数据校验并编译为注册过的 EffectSpec/节点，不允许脚本直接修改状态。
4. 实现 handler 索引、稳定分发、版本校验与 `completionHandlerId`/`ContinuationBinding` 恢复。

## 实施要求

1. Lua 内容以 `contentId`、`abilityId`、`handlerId`、脚本版本和内容哈希标识；保存的是这些稳定标识和归一化参数，不保存 Lua 函数/table/VM 对象。
2. Host API 只返回值快照或只读 DTO。禁止向 Lua 暴露 `GameState`、服务定位器、任意 CLR 类型、Unity 对象、文件/网络/系统时间或非确定性随机。
3. EffectSpec 编译器采用白名单注册：未知 type、缺字段、额外敏感字段、类型不符、深度/数量超限都返回结构化 fault，并由内核进入 `paused_fault`。
4. handler 索引按事件类型与目标建立，执行顺序严格为 `(routeTier, priority, contentInstanceId, abilityId, handlerId)`；targeted 与 continuation route 先于普通 observer，低 priority 先。
5. handler 可返回零个或多个 EffectSpec。零个不得制造阻塞；多个按规范形成确定性有序子节点。
6. 动态延续使用已持久化的 `completionHandlerId` 和显式 `ContinuationBinding` 解析。恢复时重新定位相同版本 handler；缺失或 hash 不一致必须 fault，不能换脚本继续。
7. 每次 handler 分发写入 receipt；外层事务失败时 receipt 与节点一并回滚，恢复时已有 receipt 不重放。
8. 统一错误码、源脚本位置和安全诊断文本，日志不得泄露隐藏内容给客户端；客户端投影由 `NMC-009` 完成。

## 测试精简要求

- 以少量代表脚本和参数表覆盖：零/单/多 EffectSpec、排序、非法 schema、禁用 API、版本不匹配、continuation 恢复与 receipt 幂等。
- Lua façade 的同构 getter 用合同表验证，不为每个字段写独立测试；只对权限或派生规则差异保留专项用例。
- 不复制 `NMC-004` 已有的纯 Effect 状态机测试；这里只验证 Lua/编译/分发边界。
- 合并 spike 测试与生产适配测试，删除不再被调用的临时 host/fixture。

## 非目标

- 不实现通用玩家交互 UI，不批量迁移卡牌、城市或设施。
- 不开放写 API，不支持脚本自行调 Unity/网络。
- 不为兼容旧 ID 分支建立第二套 Lua 分发路径。

## 验收标准

- 一段版本化 Lua handler 能读取只读上下文、返回 EffectSpec，并由 EffectExecutor 创建/推进节点。
- 无订阅/空数组直接返回；多 Effect 顺序确定；异常与 schema 错误可复现地进入 `paused_fault`。
- continuation 在序列化/克隆/恢复后定位相同 handler 且不重放 receipt。
- asmdef 边界正确，完整 EditMode 无新增失败，`git diff --check` 通过。

## 交付报告

描述宿主程序集、开放 API 白名单、EffectSpec schema、排序/receipt/continuation 机制、脚本版本策略；列出 spike 清理、暂留兼容入口及测试净变化。

