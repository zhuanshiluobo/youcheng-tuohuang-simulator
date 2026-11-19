# DIY 包体作者指南

本指南描述当前读取器已经实现的包体合同。包通过解析、安装、出现在房间以及完成正常对局是不同的验证结果。安装与发布操作见 [DIY 内容安装与发布说明](DIY内容安装与发布说明.md)；发布时应说明所需游戏构建及实际验证范围。

## 包目录与身份

一个包的根目录直接包含 `pack.json`。手动放入当前游戏构建的用户数据根时，目录应为 `Mods/<packId>/<version>/pack.json`；不要额外嵌套一层同名目录。Windows 游戏数据根以该构建实际的 `Application.persistentDataPath` 为准，不以作者的用户名或盘符为准。

`packId` 由小写字母、数字以及分隔符 `.`、`_`、`-` 组成，不能使用 `core`；`version` 是三段非负整数，例如 `1.0.0`。模式、扩展、定义、规则绑定和配方等自有身份以 `packId.` 开头。版本号与内容 API、能力名、实际内容哈希是不同概念。修改同一版本的文件会改变内容哈希；正式发布应增加版本，旧版本应保留供对应存档使用。

清单使用 `format: "nomadcity.diy"`、`schemaVersion: 1` 和 `contentApiVersion: 1`，不冒充 core 的 schema 3。可只提供模式、只提供扩展，也可同时提供两者；每个包至少提供其中一种。包依赖使用 `dependencies`，模式和扩展的 `requires` 表示扩展依赖。版本范围只接受 `=1.0.0` 或 `>=1.0.0 <2.0.0` 两种形式。

字段与格式见 [包清单 Schema](diy-pack.schema.json) 和 [事件追加配方 Schema](diy-recipe.schema.json)。Schema 不能替代正式读取器的跨文件、依赖、资源和活动组合校验。未知字段、重复 JSON 键、重复身份和缺失的引用均会失败。

## 纯模式示例

以下是 `pack.json` 的完整示例，不含新卡牌：

```json
{
  "format": "nomadcity.diy",
  "schemaVersion": 1,
  "packId": "demo.modes",
  "version": "1.0.0",
  "displayName": "荒原模式包",
  "author": "示例作者",
  "description": "复用基础开局配方。",
  "contentApiVersion": 1,
  "requiredCapabilities": ["diy.pack.v1", "diy.rule_modes.v1"],
  "dependencies": [{"packId": "core", "versionRange": ">=2.2.0 <3.0.0"}],
  "ruleModes": [{
    "modeId": "demo.modes.expedition",
    "displayName": "荒原探索",
    "description": "使用基础开局内容。",
    "requires": ["core"],
    "applicableMapIds": ["map-three-players", "map-four-players"],
    "setupRecipeRefs": [{"packId": "core", "recipeId": "standard"}]
  }],
  "definitions": [],
  "setupRecipes": []
}
```

这只说明包的形状和配方引用；实际宿主仍需满足所声明的核心版本、能力、地图和依赖。模式可通过 `mechanisms` 显式引用宿主已登记的机制，例如 `entrepreneur_selection.v1`；该机制自身所需的扩展和能力仍须满足。不要仅凭模式名称推断机制。

扩展新增事件时，要同时登记事件定义和追加配方，并使事件颜色、目标牌堆、地图和模式一致。替换事件使用目标定义的 `replaces` 合同，不再额外追加同一张牌。事件选择、按钮区域与奖励脚本见 [事件内容说明](DIY内容开发-Event.md)。

## 准备阶段

每局可启用多个模式。基础模式可以在 `ruleModes` 条目中声明 `preparation`；版本目前为 `1`。基础模式省略时使用当前所选扩展对应的官方准备流程，附加模式省略时不重复插入普通官方流程；若模式需要企业家机制且尚无模式提供该机制的准备步骤，则由一个来源补入一次。显式声明的准备步骤参与组合；不同模式声明的企业分发或城市样式配置必须一致，实际冲突会阻止开局。宿主从声明推导 `diy.preparation_flow.v1` 能力，建议也将它写入包的 `requiredCapabilities`。

房间初始为标准模式，首次选择其他模式会切换到该模式；之后可继续点击其他模式加入组合，再次点击已选模式可移除。房间始终保留至少一个模式；需要同时启用标准模式时，可在选择其他模式后再点击标准模式。

模式可在自己的 `ruleModes` 条目中声明 `"preparationOrder": 10`，序号为非负整数。同一窗口内先执行序号小的模式，再执行序号大的模式；分发前、分发后和确认后三个窗口各自排序，固定企业节点不会移动。同一序号的模式不能同时启用。未声明序号但有准备步骤，或继承企业家准备步骤的旧模式按 0 处理；没有步骤也没有序号的模式不占序号。需要明确互斥时可声明 `"conflicts": ["其他模式ID"]`。步骤身份、序号和启用模式均纳入本局冻结内容；模式内部仍按数组顺序执行。声明序号或模式互斥时，宿主会检查 `diy.multi_rule_modes.v1` 能力。

三个步骤数组按顺序执行：`beforeDistribution` 在企业分发前，`betweenDistributionAndConfirmation` 在分发后、玩家确认前，`afterConfirmation` 在企业确认和初始化后。缺省数组为空；中间步骤需要声明 `enterprise`。每个步骤包含唯一 `stepId`、`sourcePackId`、`sourcePackVersion`、`kind`、`handlerId`、`handlerVersion` 和 `execution`；整个准备流程最多 128 个步骤。`once` 执行一次并使用固定玩家顺序首位作为 `ctx.playerId`，`eachInOrder` 依照固定玩家顺序依次完成，`eachIndependent` 分别执行并在全部完成后继续。涉及共同候选或共享状态的选择应使用有序执行。

`kind: "lua"` 需要包内 `lua/` 下的 `script`，可选 `completion` 指定既有完成续接；`kind: "builtin"` 只能引用宿主已登记的处理器，不声明脚本或续接。源包必须是模式自身或其依赖闭包中的实际版本；处理器身份、版本和脚本内容在开局时冻结。可选 `arguments` 是对象，只能包含空值、布尔值、整数、字符串、数组和对象；使用现有 `ctx.payload.arguments` 读取。参数的整数范围、嵌套、总元素数和字符串字节数仍由正式读取器校验，不支持浮点数。脚本返回既有 `Effect` 数组，不直接修改游戏状态或扩展 Lua 接口。

`enterprise` 可分别按 2、3、4 人配置 `draw`、`keep`、`extraRandom` 和 `finalCount`，仅支持声明过的人数。`pool` 和可选 `cityStyles.levelOne`、`cityStyles.levelTwo` 使用 `all`、`fixed` 或 `requiredRandom`：`all` 使用全部符合条件的定义，`fixed` 使用 `candidateDefinitionIds` 名单，`requiredRandom` 可从名单中抽取 `count` 个并确保包含 `requiredDefinitionIds`。`enterprise.fixed` 可声明固定企业及其 `ownerOrderIndex`，`-1` 表示无初始归属；`requireConfirmation` 为真时必须指定玩家顺序索引。最终数量须等于玩家人数乘保留数、再加公共随机数及不需确认的固定企业数，且候选必须足够。企业和城市样式的定义、人数以及彼此关系由宿主在实际装配时再次校验。

前置步骤若通过现有效果结果影响企业分发，可用 `distributionInputs` 将指定步骤的 `effectTypeId`、`effectStableKey` 和 `resultField` 绑定到 `candidateDefinitionIds`、`requiredDefinitionIds`、`candidateCount` 或 `extraRandomCount`。`playerScope` 必须与步骤的执行方式一致；数量只接受 `once`。这不是任意 Lua 状态写入入口，结果必须来自正式效果完成记录。

完整示例见[准备阶段模式清单](../../tools/packaging/examples/准备阶段模式/pack.json)和[步骤脚本](../../tools/packaging/examples/准备阶段模式/lua/start-iron.lua)：按顺序让每位玩家在分发前获得 1 异铁。示例位于开发示例目录，不会自动装入游戏。完成 JSON Schema 校验只表示字段形状正确，实际读取器还会验证来源包、脚本、候选池、人数与完整规则合同。

## 规则绑定

模式或扩展的 `ruleBindings` 指定现有正式入口和包内脚本；脚本仍使用 [现有 Lua API](DIY内容开发-Lua%20API.md)，不能直接写游戏状态。当前已接入并经过隔离测试的入口有：

| `eventType` | 作用与返回合同 | 其他字段 |
| --- | --- | --- |
| `PlayerCleanupStarted` | 玩家收尾开始时返回现有效果数组 | 可选 `completion`，需要 `diy.rule_bindings.cleanup.v1` |
| `RoundStarted`、`RoundEnded`、`CharacterCardCovered`、`CharacterCoverCompleted`、`CharacterUseFinished`、`CollectionStarted`、`CityMoveCompleted`、`FacilityPlaced`、`FinalScoringStarted` | 在对应的既有事件时返回现有效果数组 | 可选 `completion`，需要 `diy.rule_bindings.events.v1` |
| `OperatorDispatchStarting` | 抽牌前返回 `{drawCountDelta=整数}`；多个来源累加，仅修改本次数量 | 纯补丁，无 Effect／完成续接，使用 `diy.rule_bindings.dispatch.v1` |
| `CandidateSetBuilding` | 按现有候选补丁合同调整城市移动或设施建设候选，只读 | `candidateKind` 为 `"city_destination"` 或 `"facility.build.card"`；不得声明 `completion`；需要 `diy.rule_bindings.candidate.v1` |
| `EffectCompleted` | 对指定效果类型的真实完成事实返回现有效果数组 | 必填 `completedEffectTypes`；可选 `completionPolicy` 和 `completion`；需要 `diy.rule_bindings.completed.v1` |

建议将每项绑定所需能力写入包的 `requiredCapabilities`，或写入该绑定所属模式／扩展的 `requiredCapabilities`；其他模式或扩展中的声明不适用。为兼容旧包，宿主也会从收尾、普通事件和候选绑定推导对应能力，检查时不改写包文件或内容哈希。`EffectCompleted` 仍须显式声明其能力。宿主不具备已选玩法所需能力时会拒绝启用；仅属于未选玩法的能力不会阻止其他玩法。

这些事件的产生时机和实际载荷见[事件内容说明](DIY内容开发-Event.md)。全局阶段事件的 `ctx.playerId` 可能是 `"none"`；如需针对实际玩家产生效果，应使用该入口已有载荷中的玩家引用，不得凭空指定玩家。影响力离场事件需要来源授权和去重协议，目前不作为通用模式或扩展绑定开放。

`EffectCompleted` 的 `completedEffectTypes` 填写 1 至 64 个不重复的公开效果类型 ID，例如 `"effect.resource.gain"`；每项最多 256 字符，仅允许英文字母、数字、点、下划线和连字符，不能订阅内部来源包装 `effect.ability.source.resolve`。`completionPolicy` 默认为 `"succeeded"`，只观察成功完成；`"terminal"` 同时观察正常失败终态。宿主沿用现有完成事件的真实性和特殊选择根过滤，不把完成续接当作通用事件订阅。避免让观察器产生会再次触发自身的同类效果，以免形成循环并触及规则执行预算。

完整的模式绑定示例见[完成事件模式清单](../../tools/packaging/examples/完成事件模式/pack.json)及其[脚本](../../tools/packaging/examples/完成事件模式/lua/city-score.lua)。该示例在城市移动成功完成后加 1 分，属于规则演示。

例如，模式中的城市候选绑定可写成：

```json
"ruleBindings": [{
  "bindingId": "demo.modes.city_rule",
  "eventType": "CandidateSetBuilding",
  "candidateKind": "city_destination",
  "script": "lua/city_rule.lua",
  "version": "1.0.0"
}]
```

该示例建议在包级或所属模式的 `requiredCapabilities` 中声明 `diy.rule_bindings.candidate.v1`。`lua/city_rule.lua` 可以使用现有候选接口；以下示例移除本次候选池的第一项：

```lua
return function(ctx)
    local first = ctx.payload.candidatePoolIds[1]
    if first == nil then return {} end
    return {Candidate.Remove({ids = {first}, reasonCode = 'demo.modes.example'})}
end
```

只读候选入口不能返回 `Effect`、写入持久记忆或引用本次池外的候选。城市目的地会在普通候选补丁后继续执行既有硬性排除；设施候选仍按正式建设流程复核。不要把其他查询类型或事件名称当作已接入的模式绑定；宿主未声明的入口会拒绝加载。

`city_destination` 的池由正式城市移动流程按当前地点的相邻点构建，`Candidate.Add` 不能添加不相邻的池外地点。使用 `context.destinations` 中的 `candidateId/locationId` 映射识别地点，并回传 candidateId。直接通过 `MoveItem` 转移城市物品不进入这个候选流程，也不发布 `CityMoveCompleted`；参数与离图限制见 [Lua API 城市物品](DIY内容开发-Lua%20API.md#811-城市物品离图回图与直接转移)。

## 文件与执行限制

包内路径使用 `/` 分隔的相对路径，不允许绝对路径、盘符、空片段、`.`、`..`、Windows 保留名称、大小写碰撞、符号链接或目录联接。当前限制为最多 4096 个文件、单文件 64 MiB、文件总量 512 MiB；Lua 源码每个文件最多 32 KiB；图片单边最多 8192 像素、总面积最多 33554432 像素。游戏发现阶段还会实际解码图片并核对尺寸。

Lua 调用继续受现有沙箱与预算约束；不提供文件、网络、CLR、Unity 或自由 `require`。共享模块使用正式冻结模块声明，来源必须在依赖闭包中。加载时只做语法检查，不执行未选脚本。

一个包同一时刻只绑定一个默认版本；新版本不会静默取代已绑定版本。房间和存档的精确版本与内容哈希优先于本机默认值；缺少版本或同版本内容发生变化时应保留原文件并明确失败。当前独立的版本配置逻辑将在下次启动时生效，不热替换进行中的对局。
