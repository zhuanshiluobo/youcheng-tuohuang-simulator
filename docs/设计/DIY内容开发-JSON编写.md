# DIY 内容开发：JSON 编写与隔离装配

更新：2026-10-28。核对基线：`3a398b86a1a5433714dd5dc4a9fc55aa4a68213a` 及本次工作区生产源码。

配套 [Lua API](DIY内容开发-Lua%20API.md)。本版依据当前 `ExternalContentPack`、活动目录、领域定义和正式内容文件编写；[Lua效果系统API初稿](Lua效果系统API初稿.md) 仅作为设计背景。旧文档中的固定内容数量、企业/企业家“只有未启用模板”等阶段性结论不能作为当前合同。

目标是让内容作者独立维护 JSON、Lua 和已获准的素材，由装配方接入完整内容包。**文件可被解析、能力可被注册、内容可正常入局、画面已验收，是四个不同结果。**

## 1. 隔离开发的实际边界

### 1.1 当前支持与限制

| 项目 | 当前实现 |
| --- | --- |
| 内容根 | 编辑器/开发工程：`Assets/StreamingAssets/Content/core`；独立程序：`<程序名>_Data/StreamingAssets/Content/core` |
| 装载入口 | `ExternalContentPack.Load(root)` 解析内容；`ActiveContentCatalogBuilder.Build(pack, configuration)` 构建本局活动目录 |
| 多目录/ZIP 插件安装 | 不能假定存在自动扫描或任意 ZIP 导入；规则文件仍由同一 `pack.json` 明确引用 |
| 自定义扩展 ID | 包加载器能读字符串 ID，但正式 `GameRuleConfiguration.TryValidate` 目前仅接受 `core/enterprise/infected`。随意填 `my_diy` 会在开局配置层被拒绝 |
| 新游戏模式 | 当前仅 `standard/entrepreneur`；新增模式名不是纯内容开发 |
| JSON Schema | 仓库有 `pack.schema.json/content.schema.json/facility_inventory.schema.json`，但并未完整覆盖当前 schema 3 和全部运行时合同；编辑器校验不能替代正式加载器 |
| 运行期热换 | 不支持。修改后启动新局；旧存档恢复须保留匹配的版本和内容哈希 |
| 新底层机制 | 新物品类型、区域模板、操作域、queryKind、事件入口、UI 页面需要已注册内核支持，不能只新增字符串 |

不要给用户编造 `--mod-dir` 一类未实现的启动参数。表现扩展注册目录也不等于规则扩展包安装器。

### 1.2 推荐工作目录与交付单位

以下是建议的源文件组织方式，不是会被游戏自动扫描的目录：

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

装配方在**私有完整内容包副本**内加入这些文件，合并清单和正式开局配方，再从该私有构建的正常菜单进入。保留正式 core 原件。当前无引擎改动的演示方案是：在私有包中使用现有扩展归属，例如 `expansionId: "core"`，同时给所有新内容使用独立的 `diy_` / `diy.` ID 前缀。这表示私有 core 变体，不是新增独立可选扩展。

完全独立的自定义扩展 ID、菜单启停和任意扩展配方合并，须单独完成开局配置/装配入口支持。不能修改配置期望值来把尚未接通的能力算作通过。

## 2. `pack.json`：明确列出所有入口

新装配以当前 schema 3 为基线。推荐修改完整包副本的现有清单，保留其基础配方和依赖；以下是字段说明，不是一份能独立开局的缩减清单。

| 字段 | 类型/要求 | 用途 |
| --- | --- | --- |
| `schemaVersion` | 整数，支持 2、3；新包用 3 | 格式版本 |
| `packId` | 非空字符串 | 物理内容包身份；与扩展归属不同 |
| `version` | 非空字符串 | 内容包版本；不是 API 版本 |
| `definitions` | 包内相对路径数组 | 每份顶层定义 JSON；顺序会影响稳定槽位顺序 |
| `requiredCapabilities` | schema 3 必填字符串数组 | 包所需的宿主能力；没有要求填 `[]` |
| `expansionDependencies` | schema 3 必填对象数组 | `{expansionId, requires:[...]}`；活动装配还读取 conflicts/requiredCapabilities |
| `setupRecipes` | schema 3 必填路径数组 | 开局实体配方；标准局需要 recipeId 为 `standard` 的配方 |
| `enabledExpansionIds` | 可选、不重复的扩展 ID 数组 | 包初次解析时的启用集合；本局还要经过正式开局配置重选 |
| `ruleModes` | 可选模式声明数组 | 非 standard 模式的依赖和能力需求；不能任意扩展模式名 |
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
| `expansionId` | 必须显式填写；正式开局的限制见第 1 节 |
| `replaces` | 必须显式存在；不替换填 `null`，不能省略 |
| `enabled` | 可省略，默认 true |
| `artwork` | 包内 PNG/JPG/JPEG 路径；通常必需，组件有有限例外 |
| `artworkSpriteId` | 可选；非空时必须引用已登记切片，且与 artwork 同图 |
| `tags` | 字符串数组，建议显式 `[]` |
| `applicableModes` | 数组；空表示不限制，否则仅 standard/entrepreneur，无重复 |
| `requiredCapabilities` | 数组；每项必须是宿主已实现的能力 |
| `abilities` | 非 null 数组；角色必须含策略与计谋，其他内容通常可为 `[]` |
| `playerMarkerZones` | 非 null 数组；旧玩家标记区声明，见第 7 节 |
| `itemTypeId` | 已登记、与物理种类相容；不要编造新底层类型 |
| `cardZones` | 牌上区域声明数组，不能为 null |
| `usageTracks` | 使用轨道声明数组，与真实区域/槽位配套 |
| `abilitySources` | 真实来源/入口/订阅/查询声明，见第 6 节 |
| `abilityHandlers` | 当前定义的 Lua 处理器登记 |
| `actionCallPolicies` | 显式行动调用授权；不是通用免成本开关 |
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
      "completionHandlerId": "diy.demo.strategy.done",
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
          "directCallPolicy": "deny",
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
  "actionCallPolicies": [],
  "usageTracks": [],
  "data": {}
}
```

策略脚本使用 [Lua API 第 4.4 节](DIY内容开发-Lua%20API.md#44-完成回调可复制示例)，存为 `lua/diy/demo.strategy.lua`。计谋脚本使用该文第 4.1 节“支付换分数”完整脚本，存为 `lua/diy/demo.tactic.lua`。

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
      "template": "策略：选择获得2源岩或2铁。计谋：支付2金券，获得1分。"
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
      "template": "获得2铁"
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

`abilities[].observedEvents` 当前仅允许：`InfluenceRemoved`、`InfluenceReplaced`、`CharacterCardCovered`、`CharacterEffectCompleted`、`CharacterUseFinished`、`CollectionStarted`、`CandidateSetBuilding`。它不是整套内核事件总表。

`settlement.entryKind` 为 `optional_insertion/cleanup_order`；`localId` 必须以 `abilityId + "."` 开头；必须有 availability handler，optional_insertion 还要有 offer handler；cleanup_order 的 `wholeEffectPolicy` 为 `optional/required`。

## 6. `abilitySources`、处理器与调用策略

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
| `allowedOperations` | 精确能力授权；不是通配符权限列表 |
| `presentationRef` | 所属 definitionId、能力 ID 或已有 playerDetails 能力引用 |
| `actions` | 可见行动入口数组 |
| `repeatPolicy` | `once/unbounded`；收尾来源次数策略，继续可用，不属于被剔除的次数式 Effect |
| `cleanupInjectionPolicy` | `always/when_available`；when_available 仅适用于 kind=cleanup 且 repeatPolicy=once |
| `completionPolicy` | `succeeded/terminal`；完成事件触发范围 |
| `completedEffectTypes` | 观察 EffectCompleted 时必需的类型过滤；非空时要求 kind=event，不可递归观察来源执行器本身 |

来源事件白名单比角色旧能力更广：`PlayerCleanupStarted, RoundStarted, RoundEnded, CharacterCardCovered, CharacterCoverCompleted, CharacterEffectCompleted, CharacterUseFinished, EffectCompleted, CollectionStarted, CityMoveCompleted, InfluenceRemoved, InfluenceReplaced, FacilityPlaced, FinalScoringStarted, CandidateSetBuilding`。仍须满足来源类型、区域和权限。

### 6.2 行动字段

必填 `actionId, entryKind, bodyHandlerRef, presentationRef`；其余常用字段如下：

| 字段 | 用途 |
| --- | --- |
| `entryCostRef` | 入口成本处理器；别把成本误写成先给奖励再扣资源 |
| `entryAfterEffectsRef` | 激活后的附加步骤处理器 |
| `usageTrackId` | 当前定义声明的使用轨道 |
| `lifecyclePolicy` | `unbounded/once/round/action_round/position`，按当前次数/位置合同选择 |
| `lockPolicy` | 与角色使用等行为的互斥策略；默认 none |
| `directCallPolicy` | 默认 deny；直接调用须登记实际策略 |
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

`actionCallPolicies` 的必填为 `policyRef/callerAbilityId/targetActionId`，还可绑定 `callerHandlerRef/callerHandlerHash` 和 `omitEntryCost/omitEntryAfterEffects/omitMainAction`。这些配置须与调用者、目标行动及真实来源一致；正文中 `Effect.InvokeAction` 使用同一 `callPolicyRef`。不要把“代码能构造”当成已有调用授权。

## 7. 牌上区域与使用轨道

### 7.1 `playerMarkerZones` 与 `cardZones`

`playerMarkerZones` 项含 `zoneId/capacity`，ID 不重复，capacity 非负。它保留旧能力元数据用途。真实物品区域使用 `cardZones`，不能仅靠旧列表造一份独立库存。

`cardZones` 常用字段：`localZoneId, zoneTemplateId, slots, acceptedItemKinds, acceptedTypeIds, entryPolicyId, exitPolicyId, capacityPolicy, capacity, orderingKind, visibilityPolicyId, carrierExitPolicyId`。另有模板支持的支付/携带字段，须以 `ZoneTemplateRegistry.ValidateExternalFields/ValidateExternal` 为准。

- `localZoneId` 在本卡唯一，且不能与既有 playerMarkerZones 的 zoneId 冲突。
- `zoneTemplateId`、物理种类、接受类型和策略都必须已经注册。
- `capacityPolicy='unbounded'` 时禁止提供 capacity；有界容量按模板校验。
- slots 如提供必须为字符串数组；不是任意 UI 槽位位置表。
- 牌被移动/覆盖/退役时，其携带物由 carrierExitPolicy 合同处理，不能默默丢失或复制。

普通新内容不应声明自己不需要的存储区。标记进入/离开真实区域用当前转移/支付合同；本版不提供旧 `LoadCardMarkers` 或 `PayCardMarkers` 写法。

### 7.2 `usageTracks`

主要字段为 `protocolVersion, version, trackId, cardZoneLocalId, slots, initialSlotId, activationTransitions, resetTransitions, resetBoundary, initializationPolicy, usageGroupScope`。transition 项为 `{fromSlotId,toSlotId}`。trackId 在有效内容中全局唯一，区域与全部槽位要匹配；行动通过 usageTrackId 绑定。

次数状态由 Host 按实际标记位置和行动激活维护。不能在 Lua 中修改一个全局变量当成“每回合一次”，也不能通过普通 MoveItem 手动挪动宿主管理的使用标记。收尾后复位时点应使用现有 resetBoundary，不得把某张牌的结束事件当成全局收尾已完成。

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

`data` 主要字段：`cardId, name, description, color, resourceType, resourceAmount, representativeResourceType, representativeResourceAmount, choiceDescriptions, choiceRewards, choicePendingEffects`，可增加 `effectScript` 指向自定义规则。

默认事件规则由 `EventCardLuaCatalog` 根据 JSON 生成 Lua；存在 effectScript 时使用该脚本。JSON 的选项描述/奖励/待执行效果按同一选项索引对应，不能只加一段描述而不补规则数组。自定义脚本读取宿主 payload 中的真实 `optionIndex` 等，不能自己选择事件结果。

资源表使用 `originium/originiumShard/iron/pureOriginium/goldVoucher`。`choicePendingEffects` 的 kind 为现有 `EventEffectKind`（None/GainScore/GrantResource/PlaceInfluence），targetScope 为 `EventEffectTargetScope`；它不是通用 JSON Effect AST。数字枚举示例不可随意改数值，应核对当前枚举或采用正式反序列化支持的名称。

当前参考：[event_yellow_01.json](../../Assets/StreamingAssets/Content/core/event/event_yellow_01.json)、[EventCardLuaCatalog](../../Assets/YC/Infrastructure/Lua/EventCardLuaCatalog.cs)。

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

实际读取的 JSON、Lua、图片及相关表现输入会参与内容身份。修改后应建新局；要恢复旧待答，保留原包。不同版本的 Choice、转移协议或剔除接口不能静默迁移。不要手改存档中的 hash 或 handler 版本来绕过检查。

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

本版新增示例是文档材料；未向生产 core 清单、配方或资产写入，也不宣称缺少实际卡图的示例已正常开局验收。

本版检查记录：六个 JSON 代码块通过语法与重复键检查，文档本地链接已核对。第 4 节角色的来源字段通过当前 `ValidateRaw/ValidateAndLoad`，配套文案在内存内容快照中通过引用校验；未为示例代选卡图，未声称该完整新角色包通过开局、正常 UI 或联机验收。第 9 节哈希占位必须由作者替换为实际值。

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
| 必须剔除/避免的旧链路 | [待移除与未完成事项](../../prompt/企业家内容实施规划-20261019/04-高风险语义与回归清单.md) |
