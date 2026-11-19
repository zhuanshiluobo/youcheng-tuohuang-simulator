# DIY 内容开发：JSON 编写与隔离装配

本文依据当前读取器、活动目录及 core `2.2.2` 对应实现说明作者合同。独立 DIY 包使用 `nomadcity.diy` schema 1；core 内容清单与定义使用另一套格式，不要混用。

配套文档：[包体作者指南](DIY包体作者指南.md)、[Lua API](DIY内容开发-Lua%20API.md)、[Event 与处理器](DIY内容开发-Event.md)、[安装与发布](DIY内容安装与发布说明.md)。

内容作者独立维护 JSON、Lua 和已获准的素材，由正式包加载和活动装配接入。文件可被解析、能力可被注册、内容可正常入局、画面已验收，是四个不同结果。

## 1. 隔离开发的实际边界

### 1.1 当前支持与限制

| 项目 | 当前实现 |
| --- | --- |
| 内容根 | core 在编辑器/开发工程的 `Assets/StreamingAssets/Content/core`，独立程序在 `<程序名>_Data/StreamingAssets/Content/core`；已安装的 DIY 包在游戏数据根的 `Mods/<packId>/<version>` |
| 装载入口 | core 使用 `ExternalContentPack.Load(root)`；游戏启动时发现已安装的 DIY 版本，活动目录按所选模式、扩展和依赖装配 |
| 多目录/ZIP 安装 | 游戏发现 `Mods/<packId>/<version>/pack.json`，不直接扫描任意 ZIP；ZIP 须通过安装器安装或按[安装说明](DIY内容安装与发布说明.md)解压到正确目录 |
| 自定义扩展和模式 | 独立 DIY 包可声明自有扩展或模式，并在房间选择；身份、依赖、能力、地图和配方必须符合[包体作者指南](DIY包体作者指南.md)，不能只在定义中填一个未声明的名字 |
| JSON Schema | core 使用 schema 2/3，独立 DIY 包使用 `nomadcity.diy` schema 1；不同清单不能混用。Schema 不能替代正式加载器的跨文件、依赖和活动组合校验 |
| 运行期热换 | 不支持。修改后启动新局；旧存档恢复须保留匹配的版本和内容哈希 |
| 新底层机制 | 新物品类型、区域模板、操作域、queryKind、事件入口、UI 页面需要已注册内核支持，不能只新增字符串 |

不要给用户编造 `--mod-dir` 一类未实现的启动参数。表现扩展注册目录也不等于规则扩展包安装器。

### 1.2 推荐工作目录与交付单位

准备随安装器提供的正式包可放入[内置 DIY 开发目录](../README.md)；打包时会自动发布并校验目录中的包。安装后的发现目录和版本规则见[安装说明](DIY内容安装与发布说明.md)。

以下是旧的私有 core 示例所用的源文件组织方式，不是会被游戏自动扫描的目录，也不是一份完整的独立 DIY 包：

```text
DIY工作区/示例角色/
  character/diy_demo.json
  lua/diy/demo.strategy.lua
  lua/diy/demo.tactic.lua
  presentation/diy.demo.zh-CN.json
  artwork/diy_demo.png
  装配说明.md
```

内容作者交付上述文件、基线提交、版本、所需能力/操作域、ID 清单和装配说明。卡图必须由用户提供或经过项目素材规则明确许可；没有图就标记缺项，不拿默认图、预览图或其他用途素材占位。

第 4 节的角色示例仍通过**私有完整 core 包副本**加入定义和开局配方，使用 `expansionId: "core"`，只表示私有 core 变体；保留正式 core 原件。它不能直接当作独立扩展包使用。独立 DIY 包的清单、命名、模式与扩展声明及已支持的追加配方，以[包体作者指南](DIY包体作者指南.md)为准；不要把私有 core 示例的身份或配方格式直接套入 DIY 清单。

## 2. `pack.json`：明确列出所有入口

本节说明完整 core 包的 schema 3 清单，适用于下文的私有 core 示例；保留原有基础配方和依赖。独立 DIY 包使用另一种 schema 1 清单，见[包体作者指南](DIY包体作者指南.md)，不要混用下表字段。以下字段说明不是能独立开局的缩减清单。

| 字段 | 类型/要求 | 用途 |
| --- | --- | --- |
| `schemaVersion` | core 格式支持 2、3；本节以 3 为基线 | 格式版本 |
| `packId` | 非空字符串 | 物理内容包身份；与扩展归属不同 |
| `version` | 非空字符串 | 内容包版本；不是 API 版本 |
| `definitions` | 包内相对路径数组 | 每份顶层定义 JSON；顺序会影响稳定槽位顺序 |
| `requiredCapabilities` | schema 3 必填字符串数组 | 包所需的宿主能力；没有要求填 `[]` |
| `expansionDependencies` | schema 3 必填对象数组 | `{expansionId, requires:[...]}`；活动装配还读取 conflicts/requiredCapabilities |
| `setupRecipes` | schema 3 必填路径数组 | 开局实体配方；标准局需要 recipeId 为 `standard` 的配方 |
| `enabledExpansionIds` | 可选、不重复的扩展 ID 数组 | 包初次解析时的启用集合；本局还要经过正式开局配置重选 |
| `ruleModes` | core 模式声明数组 | 登记核心模式及其依赖、能力与配方；独立 DIY 模式使用 DIY 清单声明 |
| `facilityInventory` | 可选路径 | 批量设施数量清单 |
| `artworkSlices` | 可选路径 | 卡面整图/切片登记 |
| `sharedArtwork` | 可选键→路径对象 | 既有消费者使用的公共图片，不是可任意扩展 UI 的接口 |
| `presentation` | 可选对象；实际内容引用表现时必须配套 | `schemaVersion:1, texts:[路径...], icons:路径, defaults:路径` |
| `settlementHandlers` | 可选处理器数组 | 每项 `handlerId/script/entryPoint/version`，ID 唯一 |
| `luaModules` / `scriptDependencies` | 可选，必须成对 | 装载时冻结共享 Lua，见第 9 节 |

旧 `uiIcons` 字段被拒绝，应使用 `presentation.icons`。

新增文件必须能从清单或定义引用链到达；放进文件夹本身不等于装载。`definitions` 与 `facilityInventory.definitionFiles` 不要重复列同一份定义。

`enabled:false` 的定义仍会被读入、检查图片和 Lua 语法，随后才过滤是否启用；它不是跳过坏文件验证的开关。包至少要有一个有效启用定义。

## 3. 内容定义共同外壳

| 字段 | 要求 |
| --- | --- |
| `definitionId` | 非空、包内唯一的稳定 ID；建议 `diy_demo` 或带命名空间的 ID，不沿用另一张牌的 ID |
| `contentType` | 见下方类型表，区分大小写 |
| `version` | 非空版本；修改规则、字段合同或依赖时更新 |
| `displayName` | 非空用户可见中文名 |
| `expansionId` | 必须显式填写；独立 DIY 定义必须属于本包声明的扩展 |
| `replaces` | 必须显式存在；不替换填 `null`，不能省略 |
| `enabled` | 可省略，默认 true |
| `artwork` | 包内 PNG/JPG/JPEG 路径；通常必需，组件有有限例外 |
| `artworkSpriteId` | 可选；非空时必须引用已登记切片，且与 artwork 同图 |
| `tags` | 字符串数组，建议显式 `[]` |
| `applicableModes` | 数组；空表示不限制，非空时不得重复，模式必须真实声明。独立 DIY 包引用其他 DIY 包的模式时，模式来源须在声明者的包依赖闭包中 |
| `requiredCapabilities` | 数组；每项必须是宿主已实现的能力 |
| `abilities` | 非 null 数组；角色必须含策略与计谋，其他内容通常可为 `[]` |
| `playerMarkerZones` | 非 null 数组；旧玩家标记区声明，见第 7 节 |
| `itemTypeId` | 已登记、与物理种类相容；不要编造新底层类型 |
| `cardZones` | 牌上区域声明数组，不能为 null |
| `usageTracks` | 使用轨道声明数组，与真实区域/槽位配套 |
| `abilitySources` | 真实来源/入口/订阅/查询声明，见第 6 节 |
| `abilityHandlers` | 当前定义的 Lua 处理器登记 |
| `presentation` | 既有内容表现 DTO，如文案引用或详情脚本 |
| `dataTemplate` | 只支持设施数据模板，一层合并 |
| `data` | 类型专属对象，不可为 null |

| `contentType` | 当前用途与限制 |
| --- | --- |
| `character` | 角色/干员；策略、计谋、生命周期和使用入口配套 |
| `facility` | 设施；入场 Lua、成本、颜色、层级/供应规则 |
| `event` | 事件；选项、奖励或指定 Lua |
| `city_style` | 城市样式；宣告条件、特殊行动、标记/轨道 |
| `enterprise` / `department` | 企业等级/特殊效果、科室奖励；受现有模式与目录约束 |
| `component` | 已注册物理类型的组件；Cube/die/Token 各有声明要求 |
| `contract` / `award` | 合约、奖励；获得/持有/激活/离场按现有合同 |
| `entrepreneur_ability` / `entrepreneur_round` | 企业家能力与回合候选面；需要完整模式配套、选面和来源声明 |
| `enterprise_board` | 加载器接受格式，但启用时明确拒绝；只作未启用定义，不能作为新玩法入口 |

企业和企业家现有实现不代表支持任意数量/组合。`ActiveContentCatalogBuilder` 仍校验部分正式扩展的企业、科室、干员、样式数量与必需替换项。追加该类内容可能需要独立引擎工作；不要删掉校验来伪装纯 JSON 接入。

## 4. 可配套的角色示例

本例使用私有完整 core 包中的 `expansionId: "core"` 和独立 ID。**它新增一张角色，不替换既有牌**。示例规则：策略在两种资源中选一种获得 2；计谋支付 2 金券获得 1 分。数值只是 DIY 示例，不对应正式卡面。

文件：`character/diy_demo.json`。

```json
{
  "definitionId": "diy_demo",
  "contentType": "character",
  "version": "1.0.0",
  "displayName": "示例拓荒者",
  "expansionId": "core",
  "replaces": null,
  "enabled": true,
  "artwork": "artwork/diy_demo.png",
  "tags": ["diy"],
  "applicableModes": ["standard"],
  "requiredCapabilities": [],
  "playerMarkerZones": [],
  "cardZones": [],
  "abilities": [
    {
      "abilityId": "diy.demo.strategy",
      "mode": "strategy",
      "skillType": "normal",
      "hasSpecialZone": false,
      "specialZoneId": "",
      "subscriptionId": "diy.demo.strategy",
      "script": "lua/diy/demo.strategy.lua",
      "version": "1.0.0",
      "completionHandlerId": "",
      "cleanupScript": ""
    },
    {
      "abilityId": "diy.demo.tactic",
      "mode": "tactic",
      "skillType": "normal",
      "hasSpecialZone": false,
      "specialZoneId": "",
      "subscriptionId": "diy.demo.tactic",
      "script": "lua/diy/demo.tactic.lua",
      "version": "1.0.0",
      "completionHandlerId": "",
      "cleanupScript": ""
    }
  ],
  "abilitySources": [
    {
      "protocolVersion": 1,
      "abilityId": "diy.demo.use",
      "kind": "quick",
      "handlerRef": "host.character.use",
      "declarationVersion": "1.0.0",
      "authorizationPolicy": "character_covered",
      "effectiveZones": ["core.zone.character_covered"],
      "activeFaceId": "",
      "observedEvents": [],
      "queryKinds": [],
      "allowedOperations": [],
      "responseKinds": [],
      "presentationRef": "diy_demo",
      "actions": [
        {
          "actionId": "diy.demo.use",
          "entryKind": "quick",
          "bodyHandlerRef": "host.character.use",
          "entryCostRef": "",
          "entryAfterEffectsRef": "",
          "usageTrackId": "",
          "lifecyclePolicy": "position",
          "lockPolicy": "none",
          "sortOrder": 0,
          "presentationRef": "diy_demo",
          "presentation": {
            "name": {"key": "core:diy.demo.use.name"},
            "description": {"key": "core:diy.demo.use.description"}
          }
        }
      ]
    }
  ],
  "abilityHandlers": [],
  "usageTracks": [],
  "data": {}
}
```

策略脚本存为 `lua/diy/demo.strategy.lua`。选择的两项直接包含真实收益，不需要纯值选择或完成回调；参数见 [Lua 逻辑分支](DIY内容开发-Lua%20API.md#42-effectchoice逻辑分支)。

```lua
return function(ctx)
    if ctx.eventType ~= 'CharacterEffectActivated' then return {} end
    return {
        Effect.WithPresentation(
            Effect.Choice({player=ctx.playerId, branches={
                {id='originium', effects={
                    Effect.GainResource({recipient=ctx.playerId, resourceType='originium', amount=2})
                }},
                {id='iron', effects={
                    Effect.GainResource({recipient=ctx.playerId, resourceType='iron', amount=2})
                }}
            }}),
            {interaction={
                prompt=Text.Ref('core:diy.demo.choose_resource'),
                options={
                    {candidateId='originium', label=Text.Ref('core:diy.demo.choose_originium')},
                    {candidateId='iron', label=Text.Ref('core:diy.demo.choose_iron')}
                }
            }}
        )
    }
end
```

计谋脚本存为 `lua/diy/demo.tactic.lua`。付款成功才执行得分；参数见 [Lua 条件式](DIY内容开发-Lua%20API.md#41-effectcondition)。

```lua
return function(ctx)
    if ctx.eventType ~= 'CharacterEffectActivated' then return {} end
    return {
        Effect.Condition({
            leftEffects={
                Effect.PayResource({payer=ctx.playerId, resourceType='gold_voucher', amount=2})
            },
            rightEffects={Effect.GainScore({recipient=ctx.playerId, amount=1})}
        })
    }
end
```

配套文案文件 `presentation/diy.demo.zh-CN.json`：

```json
{
  "schemaVersion": 2,
  "locale": "zh-CN",
  "messages": {
    "core:diy.demo.use.name": {
      "parameters": {},
      "template": "使用示例拓荒者"
    },
    "core:diy.demo.use.description": {
      "parameters": {},
      "template": "策略：选择获得2源岩或2异铁。计谋：支付2金券，获得1分。"
    },
    "core:diy.demo.choose_resource": {
      "parameters": {},
      "template": "选择要获得的资源"
    },
    "core:diy.demo.choose_originium": {
      "parameters": {},
      "template": "获得2源岩"
    },
    "core:diy.demo.choose_iron": {
      "parameters": {},
      "template": "获得2异铁"
    }
  }
}
```

这里的 `core:` 与示例沿用的物理 packId 对应；`diy.demo` 是新增文案命名空间。项目 `presentation.schemaVersion=1` 与文案文件 `schemaVersion=2` 不冲突，它们是不同协议。

在私有完整包中完成以下装配：

1. 将定义路径追加到现有 `pack.json.definitions`，将文案路径追加到现有 `presentation.texts`，保留原图标和 defaults。
2. 提供已获准的实际卡图 `artwork/diy_demo.png`。本任务不生成或代选卡图；缺图时不能宣称完整包已可加载。
3. 在私有 `recipes/standard.json` 的 `entries` 追加以下条目；这是新局正式内容配方，不是测试中注入手牌。
4. 如希望所有六张基础角色都进入初始手牌，按私有玩法同时检查 `initialBasicCharacterCount/basicCharacterKeepCount/maxRetainedBasicCharacterCount`，不能只增加 entry 而忽视保留数量；改动仅作用于这份私有包。
5. 用正常开局配置构建活动目录，确认定义、实例、使用入口和实际可见页面均可达。

```json
{
  "entryId": "initial_diy_demo",
  "definitionId": "diy_demo",
  "quantity": 1,
  "perPlayer": true,
  "initialZoneRole": "hand",
  "condition": {"kind": "always", "runtimeId": "", "abilityId": ""}
}
```

仅加 `definitions` 不保证发到手牌；仅写两个 `abilities` 也不能替代盖放后的 `abilitySources` 使用入口。

## 5. `abilities`：角色策略与计谋

角色必须恰好两项能力，`mode` 分别为 `strategy` 与 `tactic`。`abilityId/subscriptionId` 均要在包内唯一，脚本存在且能编译，版本非空。

| 字段 | 合同 |
| --- | --- |
| `skillType` | `normal/persistent/one_shot` |
| `hasSpecialZone` | persistent 必须显式 true/false；其他类型不能设 true |
| `specialZoneId` | hasSpecialZone=true 时引用本定义已登记区域；false 时为空或省略 |
| `completionHandlerId` | 完成事件续接 ID；沿已登记入口回调同一规则脚本，不是另一个 script 路径 |
| `cleanupScript` | 既有角色收尾入口脚本；不是自由定时器 |
| `observedEvents` | 永续事件白名单，见下表 |
| `observerSubjectPolicy` | 有事件时仅 `departed_owner/self`；无事件时为空 |
| `eventResponseAttachments` | 事件→已登记响应挂载类型；DeferredPlayerResponse 仅 departed_owner 的 InfluenceRemoved/InfluenceReplaced |
| `candidateKindFilter` | CandidateSetBuilding 要求 self 且使用正式设施建设候选类型；其他情况为空 |
| `settlementActionId/titleKey/descriptionKey` | 既有角色结算来源入口；不能随意新增 action ID |
| `settlement` | 段/插入/收尾入口声明，须引用已登记结算处理器 |
| `presentation` | 已有能力/效果表现字段 |

`abilities[].observedEvents` 当前仅允许：`InfluenceRemoved`、`InfluenceReplaced`、`CharacterCardCovered`、`CharacterEffectCompleted`、`CharacterUseFinished`、`CollectionStarted`、`OperatorDispatchStarting`、`CandidateSetBuilding`。它不是整套内核事件总表。

`settlement.entryKind` 为 `optional_insertion/cleanup_order`；`localId` 必须以 `abilityId + "."` 开头；必须有 availability handler，optional_insertion 还要有 offer handler；cleanup_order 的 `wholeEffectPolicy` 为 `optional/required`。

## 6. `abilitySources`、处理器与行动调用

这套声明用于同一物理内容上的行动、事件、收尾、纯查询等来源；不是另一份状态。Host 结合真实实例位置、控制者、选面和调用收据确定是否有效。

### 6.1 来源字段

必填：`protocolVersion, abilityId, kind, handlerRef, declarationVersion, authorizationPolicy, effectiveZones, presentationRef`。推荐把数组显式写为 `[]`。未知字段与大小写重复字段会被拒绝。

| 字段 | 用途 |
| --- | --- |
| `protocolVersion` | 当前为 1 |
| `definitionId` | 可省略；填写时必须等于所属内容定义 ID |
| `abilityId` | 当前来源稳定身份，不与另一来源复用 |
| `kind` | `event/cleanup/quick/special/ruleQuery/setup`；来源类别不等于任意时点都能执行 |
| `handlerRef` | 本内容处理器、能力订阅处理器或明确的 Host 入口 |
| `declarationVersion` | 声明版本 |
| `authorizationPolicy` | controller/holder/owner/character_covered/character_subscription/active_selection/public_declaration，各自要求真实证明 |
| `effectiveZones` | 已登记区域模板 ID 数组，不是某一局生成的 zoneId |
| `activeFaceId` | 企业家等选面内容的生效面；不能写一个不存在的面来造资格 |
| `observedEvents` | 该来源订阅的事件名 |
| `queryKinds` | 只读规则查询协议名；不能自行发明新协议 |
| `allowedOperations` | 默认空数组；载体转移、奖励创建、计分、延迟义务等机制的精确声明，按相应 API 合同填写；不用于行动调用许可，不再填写 `action.invoke:*` 或响应挂载类型 |
| `responseKinds` | 默认空数组；非空时要求 kind=event，各项无重复且来自 `response.destination/response.self/response.optional_effect/response.batch/response.required_effect`；声明离场响应的调度方式，不授予行动调用许可 |
| `presentationRef` | 所属 definitionId、能力 ID 或已有 playerDetails 能力引用 |
| `actions` | 可见行动入口数组 |
| `repeatPolicy` | `once/unbounded`；收尾来源次数策略，继续可用，不属于被剔除的次数式 Effect |
| `cleanupInjectionPolicy` | `always/when_available`；when_available 仅适用于 kind=cleanup 且 repeatPolicy=once |
| `completionPolicy` | `succeeded/terminal`；完成事件触发范围 |
| `completedEffectTypes` | 观察 EffectCompleted 时必需的类型过滤；非空时要求 kind=event，不可递归观察来源执行器本身 |

来源事件白名单比角色旧能力更广：`PlayerCleanupStarted, RoundStarted, RoundEnded, CharacterCardCovered, CharacterCoverCompleted, CharacterEffectCompleted, CharacterUseFinished, EffectCompleted, CollectionStarted, OperatorDispatchStarting, CityMoveCompleted, InfluenceRemoved, InfluenceReplaced, FacilityPlaced, FinalScoringStarted, CandidateSetBuilding`。仍须满足来源类型、区域和权限。

### 6.2 行动字段

必填 `actionId, entryKind, bodyHandlerRef, presentationRef`；其余常用字段如下：

| 字段 | 用途 |
| --- | --- |
| `entryCostRef` | 入口成本处理器；别把成本误写成先给奖励再扣资源 |
| `entryAfterEffectsRef` | 激活后的附加步骤处理器 |
| `usageTrackId` | 当前定义声明的使用轨道 |
| `lifecyclePolicy` | `unbounded/once/round/action_round/position`，按当前次数/位置合同选择 |
| `lockPolicy` | 与角色使用等行为的互斥策略；默认 none |
| `sortOrder` | 行动展示排序整数 |
| `presentation` | name/description 等文案引用，必须能解析 |

`host.character.use` 是现有角色使用入口，示例可引用；不能伪造任意 `host.*` 处理器。也不要复制旧 `host.style.compat_cost` 兼容报价作为新内容费用 API。

行动 `entryKind` 当前只接受 `quick/special`，不能与 settlement.entryKind 的 optional_insertion/cleanup_order 混用。

自有处理器示意：

```json
{
  "handlerId": "diy.demo.cleanup",
  "script": "lua/diy/demo.cleanup.lua",
  "version": "1.0.0",
  "completionHandlerId": ""
}
```

此对象应放入 `abilityHandlers`，并由真实来源的 handlerRef、action 或 `cleanup.defer:` 引用。它是格式片段，不能只加处理器就期待自动执行。

普通效果 Lua 支持持久化 `ctx.memory`，无需在 JSON 新增 `memory` 字段。A 记录、B 使用同一份数据时，让两个合法处理器的 `script` 指向**同一包内路径**；它们可以有不同 handlerId／abilityId。索引按包 ID 和脚本路径建立，不按卡牌、能力或玩家隔离；不同路径复制相同代码不会共享，同脚本的完成回调会继续使用原数据。脚本返回 `{effects={...}, memory={...}}` 才覆写，省略保留、空 table 清空。完整用法、数据预算与保存边界见 [Lua API 第 1.4 节](DIY内容开发-Lua%20API.md#14-ctxmemory)。

需要按玩家或具体牌实例区分时，由 Lua 在 memory 内组织字段。不要往 JSON 填任意 memoryId，也不要靠更换路径或哈希静默迁移旧存档；入口数据保存在 Host 对局存档中，同入口脚本哈希不一致明确拒绝。查询／表现入口的返回合同保持不变。

### 6.3 效果内行动调用

来源声明 `actions[]` 中的真实行动可以被正在执行的效果通过 `Effect.InvokeAction` 调用。JSON 负责声明目标的 `entryCostRef`、`bodyHandlerRef`、`entryAfterEffectsRef`、`usageTrackId`、`lifecyclePolicy`、`lockPolicy` 等实际业务规则；调用方和目标不需要额外登记调用许可。

作者内容不再填写顶层 `actionCallPolicies`、行动字段 `directCallPolicy`，也不在 `allowedOperations` 中添加 `action.invoke:*`。`Effect.InvokeAction` 不接受 `callPolicyRef`。费用和行动消费是否省略由每一次 Lua 调用的可选布尔参数明确指定，三个参数都默认 false：

| Lua 调用字段 | false／省略 | true |
| --- | --- | --- |
| `omitEntryCost` | 正常执行 entryCostRef 的入口费用／条件 | 省略该入口子树，不豁免正文内费用 |
| `omitEntryAfterEffects` | 正文之后执行 entryAfterEffectsRef | 省略该入口后置效果，不省略正文及其他必要响应 |
| `omitMainAction` | 目标行动需要时正常消费匹配的主要行动额度 | 本次调用不消费主要行动额度，不增加额度 |

这些字段写在 `Effect.InvokeAction({...})` 内，不写在 JSON 行动定义中。使用次数、生命周期、真实来源、占用和互斥条件仍按目标行动定义执行。当前参数与页面顺序见 [Lua API 第 9.3.1 节](DIY内容开发-Lua%20API.md#931-effectinvokeaction效果内调用真实行动)。

需要挑选目标时使用 `SourceData.GetCallableActions`，过滤字段为 `actionId/itemId/sourceKey`；完整读取须沿 `nextCursor` 分页至 -1。返回的 enabled 只报告来源、次数和占用等资格，不表示已经付费或有足够行动额度；执行时结合上述参数再次检查。

旧策略中的三个 omit 值应逐次转写到对应 Lua 调用，保留原本的正常费用、明确豁免和行动消费语义。内容字段或规则改变时更新对应定义／处理器版本；旧调用存档（含已完成的历史记录）不会自动补写新参数，不保证能用当前内容和执行器恢复。这是合同兼容边界，不应靠删除存档字段或补默认值继续执行。

额外主要行动额度由 Lua 的 `Effect.GrantMainActions` 授予，不在 JSON 调用策略中配置。`allowedActionTypes` 可省略／空表示不限制，非空时可以无重复地组合 `deploy/dispatch/explore/move_city/build/special`。每批额度单独保存允许范围和剩余次数，优先消费范围最窄的匹配批次，同范围大小按授予顺序消费；完整参数见 [Lua API 第 6.4 节](DIY内容开发-Lua%20API.md#64-设施操作与主行动)。

## 7. 牌上区域与使用轨道

### 7.1 `playerMarkerZones` 与 `cardZones`

`playerMarkerZones` 项含 `zoneId/capacity`，ID 不重复，capacity 非负。它保留旧能力元数据用途。真实物品区域使用 `cardZones`，不能仅靠旧列表造一份独立库存。

`cardZones` 常用字段：`localZoneId, zoneTemplateId, slots, acceptedItemKinds, acceptedTypeIds, entryPolicyId, exitPolicyId, capacityPolicy, capacity, orderingKind, visibilityPolicyId, carrierExitPolicyId`。另有模板支持的支付/携带字段，须以 `ZoneTemplateRegistry.ValidateExternalFields/ValidateExternal` 为准。

- `localZoneId` 在本卡唯一，且不能与既有 playerMarkerZones 的 zoneId 冲突。
- `zoneTemplateId`、物理种类、接受类型和策略都必须已经注册。
- `capacityPolicy='unbounded'` 时禁止提供 capacity；有界容量按模板校验。
- slots 如提供必须为字符串数组；不是任意 UI 槽位位置表。
- 牌被移动/覆盖/退役时，其携带物由 carrierExitPolicy 合同处理，不能默默丢失或复制。

`zoneTemplateId` 指已登记的区域模板，例如 `core.zone.card_cube_storage`，不是当前对局的区域实例 ID。`localZoneId` 是该载体内部的区域名；Lua 真正移动物品时，应查询并传入实例的 `zoneId`，不能把模板 ID 当作目的地。

`visibilityPolicyId` 控制区域内物品身份的可见性，并同时受父区域／载体可见性限制：

| 值 | 对玩家的实际作用 |
| --- | --- |
| `core.public_items` | 允许公开区域内物品；不会越过私有父区域直接公开隐藏载体内容 |
| `core.owner_items` | 只向区域所属玩家展示；牌上区域以载体当前控制者为准，不是只看卡牌原所有者 |
| `core.hidden_items` | 不向玩家公开内部物品身份；不等于授予脚本读取隐藏牌堆顺序的权限 |

`core.secret_retired_count` 是宿主内部退役区域策略，外部 `cardZones` 声明不接受。区域数量／元数据与物品身份分别投影；设置 owner 并不保证其他玩家看不到数量，也不能据此推断整个玩家详情条目、标题或来源卡面会被隐藏。

普通新内容不应声明自己不需要的存储区。标记进入/离开真实区域用当前转移/支付合同；本版不提供旧 `LoadCardMarkers` 或 `PayCardMarkers` 写法。

### 7.2 `usageTracks`

主要字段为 `protocolVersion, version, trackId, cardZoneLocalId, slots, initialSlotId, activationTransitions, resetTransitions, resetBoundary, initializationPolicy, usageGroupScope`。transition 项为 `{fromSlotId,toSlotId}`。trackId 在有效内容中全局唯一，区域与全部槽位要匹配；行动通过 usageTrackId 绑定。

次数状态由 Host 按实际标记位置和行动激活维护，不能在 Lua 中修改一个全局变量当成“每回合一次”。当前 `MoveItem` v4 的 `core.player_marker_storage` 允许在合法玩家标记区域／轨道间直接转移，并根据真实落点同步资格及适用的轨道绑定；仍需满足槽位、容量、所有者和同玩家标记互斥等检查，不等于任意标记或旧移动版本都可绕过轨道保护。直接移动不补发行动收益，也不清除本回合已有的使用收据；挪回可用格不保证已用行动可以再次发动。参数见 [Lua API 物品移动](DIY内容开发-Lua%20API.md#81-effectmoveitem-v4单组转移)。收尾后自动复位时点使用现有 resetBoundary，不得把某张牌的结束事件当成全局收尾已完成。

## 8. 其他类型的编写入口

### 8.1 设施、模板与数量

设施 `data` 主要字段：`facilityId, manifestId, name, color, score, resourceCost, goldVoucherCost, costReductionPerDistinctBuiltColor, unique, uniqueGroupId, hasEntryEffect, keywords, effectId, effectType, description, effectText, defaultSupply, reserveOnly, effectScript, completionHandlerId`。身份/名称会由活动定义装配覆盖；其余按 FacilityCardDefinition 与设施校验读取。

- 新行为使用独立 `effectId` 和当前可用 Lua，不按旧 C# 枚举号猜功能。共用 effectId 的有效实例必须引用同一 effectScript。
- `effectScript` 是入场规则入口；`completionHandlerId` 供该脚本续接。`hasEntryEffect` 决定是否注册入场处理器。
- `dataTemplate` 只能指向 `{ "contentType":"facility", "data":{...} }`；模板 data 为底、定义 data 为覆盖，对象递归合并，数组整体替换，显式 null 覆盖。只支持一层，不递归继承模板。
- 模板本身不加入 definitions；实体/数量清单才生成可装配定义。

`facilityInventory` 文件包含 `groups`，可另有 `definitionFiles`。每组填 `idPrefix, definition, variants`；variant 填 `color/count`，可含 artwork/artworkSpriteId/instances。颜色为 `blue/yellow/red/rainbow`，单色 count 为 0～128，总生成数最多 512。instances 可填稳定 id 与图片；未填时生成 `<idPrefix>_<color>_<三位序号>`。数量减少前必须确认旧实例身份和配方，不自动迁移旧存档。

`defaultSupply` 与 `reserveOnly` 描述实体角色，不是牌张数量。新设施仍须匹配正式配方、供应区及补牌规则；文件加载成功不代表会进入随机供应区。

当前参考：[facility_inventory.json](../../Assets/StreamingAssets/Content/core/facility_inventory.json)、[FacilityLuaCatalog](../../Assets/YC/Infrastructure/Lua/FacilityLuaCatalog.cs)。

### 8.2 事件

core 2.2.0 的事件 `data` 使用 `eventChoiceSchemaVersion: 1` 和唯一的 `choices` 列表。每项填写卡内稳定 `choiceId`、`description`、`button.topPercent/bottomPercent`、包内 Lua `script` 和入口 `version`，需要完成续接时再填写本定义已登记的 `completionHandlerId`。卡牌说明、颜色、资源种类及数量仍由 `description/color/resourceType/resourceAmount/representativeResourceType/representativeResourceAmount` 声明。

按钮百分比以实际单张卡面顶部为 0、底部为 100，采用 `[topPercent, bottomPercent)`；要求有限、正高度、范围内且同卡不重叠。最多 16 项；改变列表顺序或按钮位置不能改变 `choiceId` 与脚本的绑定。零选项必须填写 `choices: []` 和 `immediateResolve: { "script": "…", "version": "…" }`；有选项时不能再声明立即入口。

每个脚本沿用现行 `return function(ctx) ... end` 和 Effect 数组合同。宿主在验证选择后只调用该项脚本，并提供 `ctx.payload.choiceId`；不使用数组下标分派奖励。脚本不能重复实现宿主负责的基础影响力。需要支付或后续选择时使用现有 Effect 和本定义正式登记的完成处理器；加载时校验脚本路径和语法，不执行未选脚本。

正式加载器拒绝旧 `choiceDescriptions/choiceRewards/choicePendingEffects`、整卡 `data.effectScript` 及新旧混写。旧内容只能先用独立迁移工具生成并审核新定义；未完成的旧事件存档不自动按新顺序转换。能力标识为 `event.choices.v1`、`event.artwork_buttons.v1`。独立 DIY 包的目录、模式和扩展声明及当前支持的规则入口见 [DIY 包体作者指南](DIY包体作者指南.md)；实际构建的验收范围以实施记录为准。

作者校验需要同时检查 JSON Schema 和正式加载器：标准 JSON Schema 本身不能比较两项的 `choiceId` 或百分比，也不能检查包内文件和 Lua 语法。安装 Python 的 `jsonschema` 与 .NET 9 SDK 后，可执行 `python -X utf8 tools/packaging/validate_event_pack.py <内容包根目录> <对应版本的content.schema.json> <对应游戏的Managed目录>`。该工具先校验事件定义的 Schema，再调用指定游戏程序集中的正式加载器，报告内容指纹和程序集身份；两步均通过才算完整校验。请使用目标游戏同一版本的 Schema 与 Managed 目录。此工具目前校验现有 core 格式，不能作为独立 DIY 包读取已完成的证明。

当前参考：[event_yellow_01.json](../../Assets/StreamingAssets/Content/core/event/event_yellow_01.json)、[对应选择 Lua](../../Assets/StreamingAssets/Content/core/lua/events/choices/event_yellow_01.choice_01.lua)、[事件合同](../../docs/设计/DIY包体设计.md#3-事件牌选择合同)。

### 8.3 城市样式

`data` 主要字段：`cityStyleId, name, level, score, description, maxDeclarationsPerPlayer, declarationReward, declarationRequirement, specialActionId, specialAction, effectScript, luaSubscriptionId, luaAbilityId, luaCompletionHandlerId, luaVersion`。declarationRequirement 使用既有设施数量/颜色/图案格等查询规则；specialAction 使用既有费用、宣告标记、使用轨道和行动入口。

真实特殊行动通常还需 abilitySources、abilityHandlers、cardZones 和 usageTracks。替换样式保留原运行时槽位及原 specialActionId 关系，不能只改一个 ID 破坏入口绑定。

当前参考：[军工化区域定义](../../Assets/StreamingAssets/Content/core/city_style/city_style_military_industrial_area.json)、[CityStyleLuaCatalog](../../Assets/YC/Infrastructure/Lua/CityStyleLuaCatalog.cs)。

### 8.4 企业、企业家、组件、奖励

这些类型使用各自的严格声明和配方。建议从同类型的**当前正式定义**提取字段形状，再改独立 ID 与规则；不要复制历史 examples 中未接通的能力假装当前合同。

| 类型 | 重点及当前参考 |
| --- | --- |
| enterprise/department | levels/rewards、特殊效果处理器、科室归属和文本消费者；[企业定义目录](../../Assets/StreamingAssets/Content/core/enterprise/enterprise.rhine_lab.json) |
| entrepreneur_ability/entrepreneur_round | 正式候选面、sourceId、轮次、真实选面收据、卡图 SHA-256、能力来源；[EntrepreneurContentCatalog](../../Assets/YC/Infrastructure/Lua/EntrepreneurContentCatalog.cs) |
| component | 已登记 itemTypeId；声明 Cube 外观的组件、六面实体骰有不要求卡图的有限例外；其他 Token/卡牌仍需实际图片 |
| contract/award | 获取策略、持有区域、激活/清理生命周期；GrantReward 目标还要求 `acquisitionPolicy='content_granted'` 和精确来源授权；[ContentLifecycleLuaCatalog](../../Assets/YC/Infrastructure/Lua/ContentLifecycleLuaCatalog.cs) |

高级类型的字段应随实际玩法合同交付；本文不把“共同外壳能反序列化”描述成任意自定义企业或组件已经能开局。

### 8.5 干员特派的配方与前置事件

正式开局配方 `operatorDrawCount` 是干员特派的基础抽牌数量，默认 2，合法范围为整数 1～99。内置感染者配方设为 3；其 `setupRecipeComposition[].overrides` 明确包含 operatorDrawCount。基础配方的 `minimumOperatorDrawByPlayerCount` 可按人数提供最低值，内置双人局为 3。最终合并值随本局冻结，Flow.DispatchOperator v2 在运行时读取该值，不另行按扩展名强制覆盖。

这是正式开局配方字段，不属于仅允许追加事件牌的 DIY append-recipe 格式。DIY 模式／扩展可通过 `ruleBindings` 绑定 `OperatorDispatchStarting`；内容能力可在 `observedEvents` 声明该事件，角色能力仍须满足 persistent 与 observerSubjectPolicy 等既有约束。该事件采用纯补丁返回 `{drawCountDelta=整数}`，不返回 Effect，不消费使用次数，不挂接响应子树；增减值范围 -99～99，合并后最终数量必须为 1～99。DIY 绑定使用 `diy.rule_bindings.dispatch.v1`，禁止 completion；角色订阅要求 self，禁止为此事件配置 eventResponseAttachments。完整字段及示例见 [Event 干员特派前置事件](DIY内容开发-Event.md#46-干员特派前置事件)和 [Lua API 的 Flow 接口](DIY内容开发-Lua%20API.md#10-flow包含业务的组合流程)。

## 9. 共享 Lua 模块冻结

schema 3 下 `luaModules` 与 `scriptDependencies` 必须同时存在。模块条目严格四键：`moduleId/version/script/sha256`。绑定条目严格三键：`protocolVersion/script/modules`；每项引用严格四键：`moduleId/version/sha256/alias`。

```json
{
  "luaModules": [
    {
      "moduleId": "diy.demo.helpers",
      "version": "1.0.0",
      "script": "lua/diy/helpers.lua",
      "sha256": "填写实际Lua正文SHA256的64位小写十六进制值"
    }
  ],
  "scriptDependencies": [
    {
      "protocolVersion": 1,
      "script": "lua/diy/demo.strategy.lua",
      "modules": [
        {
          "moduleId": "diy.demo.helpers",
          "version": "1.0.0",
          "sha256": "与模块登记完全相同的实际SHA256",
          "alias": "Helpers"
        }
      ]
    }
  ]
}
```

这是需替换哈希的格式片段，不可原样装载。目标脚本须已由能力等入口加载；路径使用 `lua/... .lua`，禁止反斜杠、空片段、`.`、`..` 和重解析点。每脚本 1～128 个模块，alias 唯一，模块自身不能又是装配入口，v1 不支持隐式递归依赖。

模块可以返回函数表，业务脚本调用 `Shared.Helpers.<函数>`。修改模块后同时更新登记与引用 hash；hash 按宿主读取的 Lua 源文本 UTF-8 计算，换行/BOM 改变也可能改变结果。装配后的完整脚本仍受 Lua 沙箱与预算限制。

## 10. 文案、图片与切片

文案必须有可追踪消费者：JSON presentation、Lua `Text.Ref`、动态拼接键、展示脚本或正式 UI。新增模板参数必须同时声明 parameters 的类型，调用端参数名/类型一致；不要从旧企业家文本整段复制未使用键。不能仅凭 rg 零命中就删动态文案。

`artwork` 是包内相对路径，当前只接受 PNG/JPG/JPEG。不使用 Unity GUID 或机器绝对路径。整图通过 artworkSpriteId 引用切片；切片文件格式为：

- 根：`schemaVersion:1, coordinateOrigin:'bottom-left', sheets:[], slices:[]`。
- sheet：`id, artwork, width, height, rows, columns, cellWidth, cellHeight`；columns×cellWidth=width，rows×cellHeight=height。
- slice：`id, sheetId, cell, row, column, rect:{x,y,width,height}`；cell/row/column 从 1 开始，按整图从上往下、从左往右编号，rect 使用左下原点。矩形须与网格序号一致，不能越界、重复或占用 sheet.skippedCells 中的格子。

UI 选材唯一入口为 [可用UI素材 README](../../游城拓荒/可用UI素材/README.md)，并遵守当前用途标准与精确索引。受限原档案 `游城拓荒/UI素材` 不在本内容开发指南的访问授权内。没有匹配素材时报告具体缺项，继续无图部分，不能回捞旧图或生成占位掩盖。

插入结算按钮不再显示悬浮文字，不新增对应字段/绑定；保留 hover 视觉、按钮行为、详情和无障碍文本。内容文案也不能覆盖编辑器中已有静态 UI 的文字和样式。

## 11. 替换、版本、哈希和存档

替换必须有新 definitionId，且目标同类型、非自身、无环。例如以下是可放入定义的 replaces 值，仍须由目标玩法明确决定是否采用：

```json
{
  "expansionId": "core",
  "contentType": "character",
  "definitionId": "elysium"
}
```

替换者与目标都要加载并启用；同一目标不能有两个启用替换者。链式替换最终使用叶定义，原始根定义保留为稳定 RuntimeId 槽位；不能依赖文件顺序覆盖。事件替换在活动装配时还会拒绝仅改名称/描述而规则未变的替换。

至少区分以下版本：

| 身份 | 含义 |
| --- | --- |
| schemaVersion | 文件格式版本 |
| pack.version / definition.version | 包和定义的内容版本 |
| ability/handler.version / declarationVersion | Lua 处理器与来源声明版本 |
| apiVersion / protocolVersion | 单项调用或结构协议；各接口独立，不能统一填最新数字 |
| InstalledContentHash / ContentPackHash | 已安装内容身份与当前配置的活动内容身份，不能互相替换 |
| PresentationContentHash | 表现内容身份，按其消费合同使用 |
| Lua 内容哈希 | 冻结正文/装配正文身份；continuation 和恢复依赖它 |

实际读取的 JSON、Lua、图片及相关表现输入会参与内容身份，宿主规则实现指纹也参与活动内容身份。例如城市物品转移 `item_mobile_city_transfer` 与采集计划实现有独立身份；core 包版本字符串相同不保证活动哈希相同或旧存档兼容。修改后应建新局；要恢复旧待答，保留原包。不同版本的 Choice、转移协议或剔除接口不能静默迁移。不要手改存档中的 hash 或 handler 版本来绕过检查。

## 12. 验证与交付清单

| 层级 | 必查项 | 能证明什么 |
| --- | --- | --- |
| 静态文件 | UTF-8、JSON 语法、重复键、字段拼写、路径不越界、ID 唯一、禁用 API、引用闭环 | 文本/结构初查 |
| 包加载 | 正式 ExternalContentPack.Load；图片存在、脚本编译、区域/权限/模板/切片合同 | 包装载可接受 |
| 活动装配 | 正式 GameRuleConfiguration 与 ActiveContentCatalogBuilder；扩展、能力、配方、模式和替换 | 本局内容可装配 |
| 隔离规则 | 调用/支付/选择/回调、空候选、失败、来源失效、保存恢复与版本拒绝 | 受测规则合同，不是正常 UI 验收 |
| 正常流程 | 可见独立程序，从菜单、建房、开局和合法操作进入；记录版本、hash、GameId 和证据 | 实际页面及玩法覆盖 |

普通文档校验、运行测试和开局不能调用 UI Rebuild、重新生成或覆盖场景/预制体。测试仍能合法推进时，夹具预期差异先记录并继续本局；真实崩溃、不同步、必答请求丢失或无合法下一步保现场中断。随机未出现只能标记未覆盖，按多局累积补齐。

交付包应说明：新增/替换定义和入口、所需宿主版本、规则与表现依赖、实际卡图来源、装配清单增量、正式配方变化、Lua 完成回调、剔除项检查结果、已测/未测/失败与原因。不要只交两段脚本而省略进入游戏的路径。

本文示例用于说明字段，不会自动安装到正式内容中。第 4 节卡图路径和第 9 节模块哈希必须替换为包内实际资源与摘要；没有完整资源及实际对局验证，不能把示例视为可直接发布的包。


## 13. 当前源码入口

| 内容 | 来源 |
| --- | --- |
| 文件加载/共同字段/类型/替换/模板 | [ExternalContentPack.cs](../../Assets/YC/Infrastructure/Lua/ExternalContentPack.cs) |
| 正式开局可接受的扩展与模式 | [GameRuleConfiguration.cs](../../Assets/YC/Domain/Rules/GameRuleConfiguration.cs) |
| 活动目录/能力/配方/正式扩展限制 | [ActiveContentCatalogBuilder.cs](../../Assets/YC/Infrastructure/Lua/ActiveContentCatalogBuilder.cs) |
| 能力来源与处理器 | [AbilitySourceLuaCatalog.cs](../../Assets/YC/Infrastructure/Lua/AbilitySourceLuaCatalog.cs)、[AbilitySourceContracts.cs](../../Assets/YC/Domain/Effects/AbilitySourceContracts.cs) |
| 使用轨道 | [UsageTrackContent.cs](../../Assets/YC/Infrastructure/Lua/UsageTrackContent.cs) |
| 冻结模块 | [FrozenLuaModules.cs](../../Assets/YC/Infrastructure/Lua/FrozenLuaModules.cs) |
| 运行时路径与图片装配 | [ExternalContentRuntime.cs](../../Assets/YC/Presentation/ExternalContentRuntime.cs) |
| 当前不开放的 Lua 入口 | [Lua API 编写检查](DIY内容开发-Lua%20API.md#13-不开放的名称与编写检查) |
