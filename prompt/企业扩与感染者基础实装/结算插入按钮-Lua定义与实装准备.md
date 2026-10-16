# 结算插入按钮：Lua 定义与实装准备

> 2026-10-14当前实施状态：指定素材和组件已经接入，旧“未导入/尚未实装”措辞仅描述准备时点，不代表当前状态。用户明确两条竖线不实施；容器布局和入场修补以[当前修补合同](结算按钮布局与入场表现-修补合同.md)为准，功能与视觉验收分列。

2026-10-09。范围为实施准备，不是实装完成报告。本文件不授权运行界面生成器，也不代表已经导入图片、修改 Prefab 或开放新卡面规则。既有 bug 补丁已分别交接，实际集成验证以[局内交互与持续效果修订](执行记录/局内交互与持续效果修订.md)的最终记录为准。

## 目标与已确定边界

**底栏的收录标准以用户 2026-10-09 本轮纠正为准：①尚未挂入效果链，由玩家决定是否在当前合法时点挂入的可选插入；②收尾阶段需要玩家决定执行顺序的结算。** 不能以“效果可以放弃”“含 Choice/Condition”或“由 Event 追加”判断第一类。

| 分类 | 拟定字段 `entryKind` | 按钮的含义 | 未点击时 |
| --- | --- | --- | --- |
| 玩家决定此时挂链 | `optional_insertion` | 点击才创建并挂入正文，例如阿米娅、异客支付替代 | 只有可用来源/元数据，没有该次正文节点；玩家继续原操作时不生成“已挂链但放弃”的假节点 |
| 收尾顺序选择 | `cleanup_order` | 选择已经由收尾时点产生的待结算段中，哪一段接下来执行 | 待结算段已经存在，点击不重复创建；能否放弃另按规则判断 |

**普通 Event 响应是第三条路径，不属于可选插入按钮。** 有效订阅满足触发条件后，Event 必须把对应 Effect 挂上链并按正常顺序推进。进入选牌、选点、选数量等交互后，玩家可以依该 Effect 的允许停止规则点击“结束行动”；这表示停止已存在的效果，不表示此前可以选择不挂链。缪尔赛思额外盖放就是这一类。

两类底栏来源及正常 Event 效果的规则均由 Lua 声明；C# 提供通用挂链、排序、投影、提交和显示能力。**挂链是否由玩家决定、执行顺序是否由玩家决定、挂链后能否停止，是三个独立属性。** 不能用 AllowDecline、optional 分支或按钮是否可点击互相推导。

按钮放在现有底栏“结算提示”，显示顺序保持加入顺序。可选插入点击才挂正文；收尾点击调度已有待结算段，不能再挂一份。正文开始后不再问一次 activate/decline，也不创建新的主要行动或快速行动。普通 Event 效果正常进入自身交互，无额外底栏“发动”入口。

**2026-10-10 用户确定布局：按钮布局与动态提示布局互斥，按钮布局优先。** 当前有应显示的结算按钮时，只显示按钮布局；没有按钮时，显示当前合法动态提示。撤销左右分区并存方案；pending/disabled 仍是显示中的按钮，不能按“是否可点击”决定切回提示。布局显隐不取消交互、清除效果或改变候选。保留已有共用框体和静态文字资产，只控制对应布局显隐，不新增第二份标题。

可选插入的可用期依附当前合法操作上下文：例如角色使用页面尚未选择模式时的阿米娅、当前付款尚未提交时的异客。**不在每个 action 做完后新增“是否结算”的步骤，不让主链等待玩家逐项否决可选来源。** 当前上下文正常推进后，相应可用来源失效；未点击的正文从未产生。

旧方案“先结算所有强制收尾，再统一开放可选收尾”撤销。相同时点、属于玩家自行排序的收尾效果统一登记，不能按强制/可选预先分成两批。规则明确在此前/此后发生的阶段切换、角色回收、样式轨道复位等系统步骤，仍按主链规定时点自动推进，不为了凑按钮混入排序集合。

本文件是实施合同，不是完成证明。2026-10-10 起由用户已创建的六个执行者按《执行协调台账》领取唯一写入范围；原 EXP 记录保留历史意义，根聊天仍不直接改代码。禁止用任务编号命名脚本、类型、变量、物体、Prefab 或协议 ID。

## 必读路径与现状

### 工程与素材依据

1. `G:\NoMadCity\AGENTS.md`
2. `G:\NoMadCity\游城拓荒\可用UI素材\README.md`
3. `G:\NoMadCity\游城拓荒\可用UI素材\AGENTS.md`
4. `G:\NoMadCity\游城拓荒\可用UI素材\文档\当前用途标准与实际参数.md`
5. `G:\NoMadCity\游城拓荒\可用UI素材\清单\当前用途标准.json`：M03。
6. `G:\NoMadCity\游城拓荒\可用UI素材\文档\用途与替换规则.md`
7. `G:\NoMadCity\游城拓荒\可用UI素材\文档\用户例外与入口.md`
8. `G:\NoMadCity\游城拓荒\可用UI素材\清单\可用素材索引.json`
9. `G:\NoMadCity\游城拓荒\可用UI素材\文档\原始说明\Settlement-Effects-v1.0\README.md`
10. `G:\NoMadCity\游城拓荒\可用UI素材\参数\Settlement-Effects-v1.0\assets.json`
11. `G:\NoMadCity\游城拓荒\可用UI素材\参数\Settlement-Effects-v1.0\layout.json`
12. `G:\NoMadCity\游城拓荒\可用UI素材\文档\原始说明\Settlement-Icons-Extra-v1.1\README.md`
13. `G:\NoMadCity\游城拓荒\可用UI素材\参数\Settlement-Icons-Extra-v1.1\icons.json`

不得读取受限原档案 `G:\NoMadCity\游城拓荒\UI素材`，不得执行仍从该目录读文件的历史导入工具。本库 reference、预览、HTML 和生成脚本不是生产图片来源。

### 架构与既有合同

- `G:\NoMadCity\docs\设计\Lua效果树与回合主链设计.md`
- `G:\NoMadCity\docs\设计\Lua效果系统API初稿.md`：0.3、0.4、0.5、1.2.2。
- `G:\NoMadCity\docs\设计\外部内容包与Lua定义.md`
- `G:\NoMadCity\prompt\企业扩与感染者基础实装\06-Effect接口与提交合同.md`
- `G:\NoMadCity\prompt\企业扩与感染者基础实装\07-内容定义与注册合同.md`
- `G:\NoMadCity\prompt\企业扩与感染者基础实装\08-房间与局内UI接口合同.md`
- `G:\NoMadCity\Logs\CharacterInteractionRevision\20261009\接口交接.md`

早期架构文档中的“待实装”、固定卡牌数量和旧行为是历史阶段记录；判断当前代码用下面的实际入口。当前并非“所有底栏来源都已由 Lua 完整定义”：部分正文已有 Lua，来源声明/文字部分仍在 JSON，阿米娅消费并授权当前双发的组合、异客支付替代、部分来源查询和收尾包装仍由 C# 组织；现有投影没有图标字段。后续同时完成声明迁移与按钮接入，不能只换外观。当前 `CharacterSettlementOptionQuery` 的返回集合不是完整需求清单。

| 当前文件 | 当前职责 | 后续修改边界 |
| --- | --- | --- |
| `G:\NoMadCity\Assets\YC\Infrastructure\Lua\ExternalContentPack.cs` | JSON、脚本、资源路径和内容哈希；能力的 settlementActionId/titleKey/descriptionKey | 新声明入口及图标目录校验；卡面规则不继续塞进 JSON |
| `G:\NoMadCity\Assets\YC\Infrastructure\Lua\MoonSharpLuaRuntimeHost.cs` | 只读 Effect 构造器白名单、参数归一化 | 注册下文 Settlement.Offer 元数据协议、纯可用性返回协议；不暴露 Unity |
| `G:\NoMadCity\Assets\YC\Infrastructure\Lua\CharacterCardLuaCatalog.cs` | 本局 Registry 注册；当前对 cleanup 正文 WrapCleanupOffer | 迁移已选来源，避免 Lua 新声明和旧包装同时生成两份 |
| `G:\NoMadCity\Assets\YC\Domain\Cards\CharacterSettlementOptionQuery.cs` | 查询阿米娅/绑定/收尾/授勋选项 | 从通用 offer 记录投影，不再按角色名生成按钮 |
| `G:\NoMadCity\Assets\YC\Domain\Cards\CharacterContentEffectExecutor.cs` | cleanup_offer、绑定、费用和正文 | 保留业务原语，迁移登记包装；费用仍由实际 Effect 提交 |
| `G:\NoMadCity\Assets\YC\Domain\Cards\CharacterLifecycleService.cs` | 当前角色双发、消费来源、持久状态 | 给 Lua 复用通用消费原语，保留来源/使用实例约束 |
| `G:\NoMadCity\Assets\YC\Domain\Rules\RoundExecutionService.cs` | 玩家收尾窗口及强制子树推进 | 窗口生命周期复用；不能每条最小 Effect 都新增一次确认 |
| `G:\NoMadCity\Assets\YC\Domain\Effects\ResourceEffectExecutor.cs` | `PaymentZones/PaymentCandidates/ExecutePayment`，异客目前用 `choose_substitution` 枚举支付组合 | 付款时显示可用来源，由玩家决定是否挂入替代链；挂入后做数量分配。整笔付款共用上限，不只接建设 |
| `G:\NoMadCity\Assets\YC\Domain\Effects\FacilityEntryEffectExecutor.cs` | 内含 `FacilityBuildEffectExecutor.PayEntityCost`，建设进入同一 `ExecutePayment` | 复用统一支付窗口，不另造建设专用异客按钮或第二笔扣费 |
| `G:\NoMadCity\Assets\YC\Domain\Effects\ContentLifecycleEffectExecutor.cs` | 授勋取得、快速行动、持续收尾及样式/设施生命周期 | 区分立即结算、原快速行动入口和收尾顺序项；不得统一搬到底栏 |
| `G:\NoMadCity\Assets\YC\Domain\Effects\EffectKernel.cs` | 同提交失效请求、收据、插入子树 | 复用现有事务，不能重开与主链脱离的根节点 |
| `G:\NoMadCity\Assets\YC\Application\Sessions\SettlementOptionView.cs` | UI DTO，已有身份/版本/来源/可用性/文案字段 | 增量增加显示和顺序字段，保留现有 ID 字段 |
| `G:\NoMadCity\Assets\YC\Application\Interactions\InvokeSettlementOptionCommandHandler.cs` | Host 命令入口 | 继续使用同一命令；不新增按卡名分支命令 |
| `G:\NoMadCity\Assets\YC\Presentation\GameplayExpansionUi.cs` | settlementHost、pendingOptions、RefreshSettlementBar、InvokeSettlement | 替换底栏渲染器，保留主界面明细/快速行动和卡面查看 |
| `G:\NoMadCity\Assets\YC\Presentation\Prefabs\Gameplay\GameplayInteractionHud.prefab` | 当前 CharacterSettlementHost 与临时 UiEffectRowView 模板 | 仅结算条容器与模板接线；其他区域不变 |

当前 HUD 中 `settlementHost` 引用 fileID `8600000000000000002`，`settlementRowTemplate` 引用 `8600000000000100016`；CharacterSettlementHost 自身是水平布局，尚无本包规定的独立裁切滚动层。后续先核验实际文件仍有这些对象，不能按旧 fileID 盲写。旧合同要求复用 UiEffectRowView 是当前过渡实现；本方案的新按钮仅替换底栏这份模板，不替换玩家明细条目，也不改变明细的高度 76、正文高度 31。

## 准备采用的素材与布局

### 选材清单

本批保留 Settlement-Effects-v1.0 的5张整底背景，按钮图标按用户最新修改的逐项清单绑定。13个来源当前使用7个图标键，已全部匹配21张色版；诗怀雅与林按用户确认使用 `target_other`；不把素材文件数当成按钮数。Settlement-Icons-Extra-v1.1 只补图标，不替换背景。图形含义不限制用户选图，不能反过来改变真实规则或交互。

**逐个按钮的显示文字、IconKey 已指定**，见 [结算区域按钮图标与文字清单](结算区域按钮-图标与文字清单.md)；全部绝对路径、哈希、参数、来源与文本 Key 见 [素材映射](结算插入按钮-素材映射.json)。这是待实装选定清单，不代表已接入。

| 用途 | 可用库内精确路径规则 | 资源键 |
| --- | --- | --- |
| 五态完整背景 | `资产/结算插入按钮/Settlement-Effects-v1.0/assets/bases/chip-{default,hover,pressed,pending,disabled}@2x.png` | 沿素材映射中的背景键 |
| 阿米娅·合作协议 | `资产/结算备用图标/Settlement-Icons-Extra-v1.1/assets/icons-extra/ui-chain-link-{dark,light,muted}@2x.png` | `core:settlement.chain_link` |
| 异客·隐蔽仓库 | 同上目录 `ui-substitute-{dark,light,muted}@2x.png` | `core:settlement.substitute` |
| 多萝西、泥岩、涤火杰西卡 | 同上目录 `ui-trigger-ready-{dark,light,muted}@2x.png` | `core:settlement.trigger_ready` |
| 诗怀雅、林 | 同上目录 `ui-target-other-{dark,light,muted}@2x.png` | `core:settlement.target_other` |
| 百炼嘉维尔 | 同上目录 `ui-scope-adjacent-{dark,light,muted}@2x.png` | `core:settlement.scope_adjacent` |
| 授勋卡 | 同上目录 `ui-choose-order-{dark,light,muted}@2x.png` | `core:settlement.choose_order` |
| 缪尔赛思、W、雷蛇、赫拉格 | 同上目录 `ui-chain-defer-{dark,light,muted}@2x.png` | `core:settlement.chain_defer` |

表内路径根为 `G:\NoMadCity\游城拓荒\可用UI素材\`；花括号只描述三态/五态枚举，实际文件以映射 JSON 逐条列出的路径为准，不能作为复制/删除通配符。使用完整背景，不叠加 fill/frame。旧 insert/build/cooperate/discount 示例图标不用于本批，键盘焦点图排除；不能按素材示例造四种游戏操作。

拟生产目录为 `G:\NoMadCity\Assets\YC\Presentation\Settlement\Sprites\Bases\` 与 `Icons\`。新增来源须先补同一清单；不得默默套通用“插入”图。

导入约定：Sprite Single、sRGB、透明、无 MipMap、无有损压缩、白 Tint。整底图 384×88，Sprite border 的 Unity 顺序 L/B/R/T 为 44/42/44/42；Image Sliced。选 PPU=200、Canvas referencePixelsPerUnit=100、Image pixelsPerUnitMultiplier=1，使 @2x 边角在逻辑坐标中仍为 22/21。实施时核验当前 Canvas 的 referencePixelsPerUnit；若不是 100，按相同比率配置，仅改新按钮，不重置全局 Canvas 或既有框体。图标采用48×48源图、Simple、PreserveAspect，显示22×22，不九切片。

### 新组件层级与参数

保留现有结算槽底板、边框、固定标题与父布局，不照抄预览的绝对屏幕位置。新 Prefab `G:\NoMadCity\Assets\YC\Presentation\Settlement\Prefabs\SettlementEffectStrip.prefab` 只提供按钮区域：

```text
CharacterSettlementHost（现有宿主）
  SettlementEffectStrip
    Viewport（RectMask2D，裁切和安全边距）
      Content（单行水平布局）
        SettlementEffectChip（稳定布局占位、Button）
          Visual（仅此子层做入场动画）
            Background / Icon（按声明可省略） / Label
    TooltipHost（位于裁切窗口外）
```

按钮源 Prefab 为 `G:\NoMadCity\Assets\YC\Presentation\Settlement\Prefabs\SettlementEffectChip.prefab`。固定标题、布局数字、五态 Sprite、字体和动效均放序列化配置；动态来源按钮的文案与图标键来自内容声明；不在控制器反复固定重赋。主界面的“结算提示”标题已有来源，不生成第二份标题。

- 1920×1080 逻辑基准：槽高 60，按钮高 44，上下留白各 8，外部左右安全边距各 24，按钮间距 10。
- 若当前宿主包含固定标题，预留 R=152；总宽 W<520 时标题收起，R=0。若宿主本来只覆盖正文区域，R=0，不能二次预留。Viewport 左边为 R+24，宽 W−R−48，高 44。外层保证可用宽至少 136。
- 安全边距在 Viewport 外，不能只给 Content 加 Padding。末按钮在滚到最右端时完整显示，右侧仍留 24。
- 字号默认 18，单行；按实际字体测量。纯文字宽 max(88,文字宽+32)，图标加文字宽 max(88,文字宽+62)，纯图标宽 48；文字按钮向上取偶数，最大为向下取偶数的 min(280,Viewport宽)。超长省略并可查看完整提示，不缩字、不换行。
- 这条“单行省略”只适用于新胶囊按钮；不改变玩家明细“完整效果文字，必要时同步缩小两框字号”的规则。
- 超出时横向滚动，不显示横向滚动条、不换行、不压扁按钮。拖动超过 6 逻辑单位取消该次使用；不等待双击计时。拖动后的短暂误触抑制只作用于刚发生拖动的指针，不延迟普通单击。
- 到滚动边界后，不能继续滚动时不吞掉外层滚轮。**本轮不新增 Tab、方向键、Home/End、Enter/Space 导航或触发，也不接焦点框。** 这些出自素材示例，不是用户要求；示例参数不能成为游戏交互合同。
- default/hover/pressed/pending/disabled 五态同宽高。pending 表示请求在途，disabled 表示 Host 判定不可用；两者不可点击，不能混用。
- 默认图标 dark，pending 用 light，disabled 用 muted；彩色自定义图不施加单色染色。纯图标模式必须有完整名称。
- 加入顺序不按名字、角色或可用性重新排序；同 ID 更新原位。新加入项视觉层从右侧移入，360ms、前100ms淡入、同帧批量布局；这些动画不能阻塞命令处理。批量错开60ms，额外上限180ms；减弱动画时直接归位。
- 队尾自动跟随仅在原来不溢出或距右端≤12时发生；用户主动浏览后1200ms不抢滚动。新窗口清队列及旧异步回调，恢复同窗口不重复装填动画。

## Lua 声明与挂链合同（新增部分尚未实现）

原方案把两类都写成“登记一段，点击再挂正文”，需要撤回。现在分别保存如下事实：

| 路径 | 触发时保存什么 | 何时产生正文节点 | 底栏点击做什么 |
| --- | --- | --- | --- |
| `optional_insertion` | 来源、可用条件、当前合法上下文及效果定义元数据 | 玩家点击且 Host 接受后 | 挂载这一次新正文 |
| 普通 Event 响应 | Event 与分发收据、由该次 Event 返回的 EffectSpec | Event 正常分发时，不等玩家许可 | 不生成这类底栏入口 |
| `cleanup_order` | 本次收尾触发后真实生成的待结算 Effect 及其显示元数据 | 收尾 Event 分发时 | 从已有段中选下一段执行 |

### 可选插入声明

撤回原拟定的 `Effect.OfferSettlement` / `effect.settlement.offer` 登记节点。改为拟新增纯数据构造器 **`Settlement.Offer`**，通过独立返回协议 `responseKind="settlementOffers"` 提供元数据，不是普通 `effects` 返回中的 EffectSpec。构造器不创建执行节点、不支付、不造 activate/decline 请求、不阻塞父行动。它可由当前上下文事件通知刷新，但 Event 只更新“现在有什么可以手动挂链”，不能提前返回这次能力的执行正文给内核。

命名沿用 `entryKind="optional_insertion"`；Lua 定义提供字段如下：

| 字段 | 固定语义 |
| --- | --- |
| apiVersion | 整数1，未知版本拒绝 |
| localId | 完整 abilityId 前缀的稳定业务键；非显示名、实例ID或任务编号 |
| player/sourceItemId | 执行玩家及真实来源 ItemId，Host 核对上下文 |
| windowId/ownerEffectId | 当前合法可用期身份及未来挂载父节点；不是另开一个玩家确认阶段 |
| maxUses | 当前可用期的上限，首批为1；刷新不能重置，其他周期沿原规则约束 |
| visibility | 首批 owner，只投影授权玩家可见的来源 |
| availabilityHandlerId | 已登记的纯查询处理器；不得返回执行Effect或更改状态 |
| presentation | 显示名、说明、可选图标；不决定规则资格 |
| effects | 尚未实例化的有序 EffectSpec 定义；可以归一化保存，不得创建 EffectNodeRuntimeState、TargetEffectId、付款事实或执行收据 |

点击后沿现有 InvokeSettlementOption 命令，在 Host 同次提交中消费本次使用额度并把 `effects` 正式实例化、挂入指定父节点，之后照常结算。未点击而可用期结束只移除元数据，不挂“放弃”节点，也不要求回答 decline。正文的停止规则仍来自其自身 Effect/请求，不能把可选挂链资格变成任何步骤都可跳过。

### 收尾顺序元数据

收尾来源仍在 `PlayerCleanupStarted` 等既有时点返回实际正文。Host 按真实事件一次挂入对应玩家收尾节点，并将同段挂入待排序集合；`entryKind="cleanup_order"` 只标记调度用途。挂链后先等待玩家决定段的先后，不能先执行正文再显示按钮，也不能点一下重新复制整段。

每段保留真实 `TargetEffectId`（段根节点）、`SourceEffectId`（若是延迟来源，指原激活节点）、`SourceItemId`、`WindowId`、`LocalId`、`RegistrationSequence` 及 Lua 声明的 `Presentation`。排序描述随该段定义携带，不通过 `Settlement.Offer` 假装成尚未产生的可选能力。当前段完成后推进同一集合的剩余段；一段内部顺序按原卡面固定。

`WholeEffectPolicy` 只用于是否允许放弃未完成收尾段，值为 optional 或 required。它不控制“是否由 Event 挂链”，也不能用于判断是否属于 optional_insertion。已经产生的负面义务保持存在；来源卡离场不抹掉原始激活所生成的义务。保存/恢复分别保存待排序段、当前执行段、已完成段，不能只用 invokedOptionIds 推导完成。

不需要玩家安排收尾顺序的正常 Event 效果，不接受这两类底栏登记包装。已有 Choice/Condition 若交互冗余，应在原效果链中处理其选牌、付款或停止交互，不能以修 UI 为由改成玩家决定是否挂链。


### 文案和图标字段

显示合同统一采用 [局内玩法文案与图标转译准备](局内玩法文案与图标转译-实装准备.md)。拟新增 DTO 的形状固定如下，Lua/JSON 使用 camelCase：

```csharp
public sealed class SettlementPresentationDefinition {
    public GameplayTextRef Label;
    public GameplayTextRef Description;
    public GameplayTextRef AriaLabel;
    public string IconKey;
    public string ContentMode;
}
```

- `GameplayTextRef` 统一为 Key+有类型的 Arguments。撤销本文件旧拟案并列 Label/LabelKey、Description/DescriptionKey、AriaLabel/AriaLabelKey 的写法；旧已存在 DTO 只经显式适配器承接，不能把旧 TitleKey 中的中文当 key 查表。
- contentMode 为 text、icon_text、icon；本批13项全部指定 icon_text。Label 是按钮文字，Description 是当前可执行段的完整说明，卡牌完整正文仍引用同一内容目录。来源查看仍直接打开卡面。
- IconKey 是图标目录键，不能是磁盘路径、GUID 或 URL。UI按目录 dark/light/muted 色版绑定状态，Lua不判断鼠标悬停。
- 统一用 `pack.json.presentation.icons` 指向图标目录；撤销旧拟定 `pack.json.uiIcons`，不得并存两个权威目录。`SettlementIconCatalog` 若保留，只作统一图标目录的动作图子集适配。
- 内建图按用户最新逐项清单登记，数量以素材映射为准；不按图形含义改变效果。DIY图片限定在其内容包内并参与哈希。缺图记录来源及缺项，不借用近似图；运行时可以明确文字降级，但不能据此认定视觉完成。

### 可用条件仍由 Lua 定义，Host 执行

新增 `SettlementAvailabilityRequested` 纯查询合同，`responseKind="settlementAvailability"`。处理器由内容包声明的 `settlementHandlers` 映射注册：`handlerId`、`script`、`entryPoint`、`version`；JSON 只定位函数，规则判断写 Lua。entryPoint 指模块的命名函数，旧 `return function(ctx)` 脚本接口保持兼容，不能为了新类型改坏所有现有脚本。

该函数接收只读 `ctx` 和当前 option/window/source 引用，返回归一化 `{ enabled = boolean, disabledReasonKey = string }`，不返回 Effect，不创建随机数、不写状态。统一结果 DTO 命名 `SettlementAvailabilityResult`。所需的牌上数量、玩家资源、绑定成本和订阅状态，通过已有只读规则查询或新增有命名合同的 Query 提供，不能把可写 GameState 整包给 Lua。

主效果登记前后的来源存在性、当前窗口身份、玩家权限、次数收据属于 Host 通用边界。费用预览可以用上述纯 Lua 查询；真正使用仍执行正文中的 Pay/Condition，在提交时按当前状态检查费用。禁止 UI 用文字“需要2源岩”反推出支付。

缓存按 `(optionId, stateRevision)`，其中 optionId 已含窗口、来源及原始激活身份；同一投影周期同一项只算一次。纯 UI 悬停、点击草稿和动画不重新运行 Lua。先按事件/能力订阅索引筛来源，再查当前窗口候选，不每帧扫描全部卡、全部历史节点、全部脚本。缓存只用于显示，Host 收到使用命令仍检查当前窗口和规则资格。

## 窗口、主链、收据与投影

### 可用上下文与收尾窗口

本文件的 window 有两种含义：可选插入的**有效上下文范围**、玩家收尾的**排序窗口**。前者不是额外的确认步骤。拟新增 `SettlementAvailabilityContextChanged` 用于按相关订阅刷新元数据，不作为必须由玩家回答的 InteractionRequest；正常 Event 的 Effect 分发不因此停下来等待“是否发动”。

上下文字段固定为 `id`、`kind`、`ownerEffectId`、`playerId`、`targetUseId`、`triggerId`、`stateRevision`。支付上下文另提供 `paymentId` 和只读 `paymentCost`。只使用主链中已有真实身份，不从显示标题、最新全局事件或客户端时间推导。

| kind | 含义与消失时点 |
| --- | --- |
| character_use_before_mode | 当前角色使用页面中的阿米娅可用期。玩家主动点击才挂消费及授权双发链；直接选模式则正常推进，不另等一次阿米娅是否使用 |
| resource_payment_before_commit | 当前整笔付款未提交时的异客可用期。玩家主动点击才挂替代分配链；照常提交付款时未使用就按原费用处理。不能等付款或整个action完成后再询问 |
| player_cleanup | 已由收尾时点生成的待结算段集合，点击决定下一段；不能将强制/可选预排成两批 |

旧方案中的 `influence_departure_response`、`generated_cover_opportunity`、`collection_before_resolve`、`city_style_after_declaration` 不再作为可选结算按钮窗口。多萝西离场响应、缪尔赛思盖放/回收、宣告后的条件式等均由对应 Event 正常挂链。

```text
普通 Event → 对应 Effect 必然挂入 → 正常选牌/选点/分配等交互
                                     └─ 允许时点击结束行动，停止已挂链效果

当前合法上下文 → 只显示手动插入来源
                ├─ 未点击而继续原操作 → 不产生该次正文
                └─ 点击 → 挂入正文 → 结算后恢复原操作

收尾 Event → 产生并挂入待结算段集合
             └─ 点击已有段 → 按其原内部顺序结算 → 再选下一段
```

收尾中的付款可以开放异客元数据，点击后暂时执行替代链，再回到同一笔付款和原收尾段；不能借嵌套开启第二个主要行动。当前快速行动授勋保留原快速行动入口。自动建设费用修正仍是报价政策。

`WindowId` 固定为 `StableIdFactory.Create("settlement_window", gameId, ownerEffectId, kind, playerId.ToString(CultureInfo.InvariantCulture), targetUseId, triggerId)`。它不随 InteractionId 或 revision 改变；不同支付、不同玩家/Use 的上下文不能混用。

可选插入的 `OptionId` 固定为 `StableIdFactory.Create("settlement_option", windowId, sourceItemId, localId)`；收尾顺序项固定为 `StableIdFactory.Create("settlement_order", windowId, targetEffectId)`。后者指向已存在的段，不能仅按卡名/ItemId去重而吞掉同来源不同次激活产生的义务。`RegistrationSequence` 只决定显示顺序，不决定执行顺序。

同身份的元数据刷新保留 OptionId、显示位置及使用额度；有相关状态变化才刷新资格，不能重复挂正文或重置使用收据。可选来源失效不生成Effect；收尾排序项失效/完成必须来自真实节点及来源规则，不能直接删记录代替结算。


### 提交与恢复

复用 `EffectInteractionCommands.InvokeSettlementOption(playerId, optionId, targetUseId, stateRevision)` 与 `GameCommandKind.InvokeSettlementOption`。UI提交的是来源身份或已存在段的身份，不提交Lua函数、费用或可伪造的节点。

- `optional_insertion`：接受命令时才挂正文，并与使用收据同次提交。已有父交互需要暂停时保存恢复位置；没有现存父请求就不伪造一个。阿米娅沿当前 Use 保存 `DoubleUseSourceItemId`、真实消费完成后保存 `SourceConsumed`，继续该 Use；不重开角色使用。
- 异客：合法付款上下文存在时可点击插入；正文完成返回同一 PaymentId。未点击而正常付款不产生替代链。支付的等待与确认属于付款本身，不能多造一个“要不要异客”的停止点。
- `cleanup_order`：命令只选择已有 TargetEffectId 开始执行，不重复 AddChild。该段的真实状态决定完成与剩余顺序集合；提交收据不等于整段已完成。
- 普通 Event 链：无需 InvokeSettlementOption。时点到了就挂并执行；后续选目标/数量用原 AnswerInteraction。能否结束当前 Effect 沿原 AllowDecline，不能要求先按卡名发动按钮。

重复命令返回原收据，不重复消费或付款；恢复普通Event链从已存在的节点继续，恢复可选来源只恢复元数据和使用记录，恢复收尾从原待排序集合继续。禁止把“未点击”与“已挂链后停止”保存为同一事实。

底栏“结束行动”有原本的上下文语义：停止已挂链且允许停止的当前Effect，保留已发生结果；或在当前没有待处理效果时按行动要求结束。它不是一份用于否决所有手动插入来源的答案。第二技能停止后，尚未做主要行动则回主要行动，已完成主要行动才能真正结束。

**2026-10-10 用户最终明确“结束行动”的上下文规则：**

| 当前上下文 | 结束行动的处理 |
| --- | --- |
| 没有正在结算的节点，且仍有未完成的必须执行收尾项 | 不允许用结束行动放弃收尾集合；仍由玩家选择结算顺序，不强制必结项排在收益之前 |
| 没有正在结算的节点，且没有必须执行的收尾项 | 一次放弃当前窗口所有剩余非必须执行收尾项，完成这一收尾窗口 |
| 正在结算一个非必须全部执行的节点 | 仅跳过当前节点尚未执行的部分并完成当前节点；其他待结算项保留，不顺带批量放弃，不退回已经提交的结果 |
| 正在结算必须全部执行的节点 | 不得用结束行动跳过；无合法候选时仍按该节点原有规则完成 |

当前节点存在时，必须优先走当前节点停止语义，不能先进入整窗放弃分支。`WholeEffectPolicy`/`CanDeclineWholeEffect`只表达待排序段整段政策；当前节点停止仍读取其既有停止许可和准确节点身份，不能由一个 bool 替代两种政策。停止不得扩大到父链、兄弟节点或其他收尾项。Host 在原命令提交边界判定并更新状态，UI只消费投影，不从按钮颜色、可见性、文案或卡名判断必结义务。

无候选按所选段原有规则处理，不能登记时提前清空义务，也不能造一个永远无法点的必结算项。以上操作语义不改变 Event 先挂链、按钮只排序的事实。嘉维尔、永久授勋、缪尔赛思策略延迟段已由用户明确均为非必结收尾，具体政策见下表；不再保留这三项的未定状态。


### DTO 增量

保留 `SettlementOptionView` 的 OptionId、InteractionId、OwnerEffectId、SourceItemId、TargetUseId、CandidateId、AnsweringPlayerId、StateRevision、CandidateSetVersion、RequiredGoldVoucherAmount、IsEnabled、VisibleToViewer 和 DisabledReasonCode。新增：

```csharp
public string WindowId = string.Empty;
public string LocalId = string.Empty;
public long RegistrationSequence;
public string EntryKind = string.Empty;
public string TargetEffectId = string.Empty; // 收尾顺序项必有；可选插入未点击时为空
public bool CanDeclineWholeEffect;
public string SettlementState = string.Empty; // available / executing / resolved / declined
public SettlementPresentationDefinition Presentation;
```

同名字段从 Domain 的 `CharacterSettlementOption` 或其后续通用替代 DTO 一路传到玩家投影。不得让 Presentation 直接读取 Host 原始 offer。旧 TitleKey/DescriptionKey 先保留适配，迁移来源齐全后再单独决定移除；不可在这个 UI 任务顺手重命名所有旧 DTO。

新增 `SettlementEffectStripView` 的接口定为：

```csharp
void BindWindow(string windowId, IReadOnlyList<SettlementOptionView> options,
    Action<SettlementOptionView> use, Action<string> inspectSource);
void SetPending(string windowId, string optionId, bool pending);
void ClearWindow();
```

`BindWindow` 做按 OptionId 的增量更新，不清空重建全部按钮。pending 是客户端在途显示，不序列化进规则状态；最终可用性以 Host 投影为准。每次绑定最多布局一次，只有新项播放装填动画。窗口令牌必须包含会话和本地观察者身份，避免热座或重连串页。

## 现有内容迁移清单

### 核对范围与依据

本表核对基础内容、企业扩和非企业家感染者已登记来源；企业家模式不混入本批。依据用户最新两类收录标准、`G:\NoMadCity\docs\规则\规则总结.md` 第9节（同一玩家自行决定多个收尾效果顺序）、`G:\NoMadCity\游城拓荒\企业扩\卡面效果与Effect式.md`、`G:\NoMadCity\游城拓荒\感染者扩\卡面效果与Effect式.md`，并对照当前 Lua/JSON 和 C# 实际分发。感染者规则书 `G:\NoMadCity\游城拓荒\规则书\游城拓荒感染者-规则书20241111.pdf` 的 PDF第5页（印刷8、9页）及第6页流程图已视觉回查；它不构成把所有触发按钮化的依据。

阅读下表时，脚本根目录 **C = `G:\NoMadCity\Assets\StreamingAssets\Content\core\lua\characters\`**，设施目录 **F = `G:\NoMadCity\Assets\StreamingAssets\Content\core\lua\facilities\`**，样式目录 **S = `G:\NoMadCity\Assets\StreamingAssets\Content\core\lua\city_styles\`**，授勋目录 **A = `G:\NoMadCity\Assets\StreamingAssets\Content\core\lua\awards\`**。表中 `C/文件名` 等表示对应完整目录下的精确文件，不表示仓库根目录下存在 C 文件夹。对应 JSON 分别在 `G:\NoMadCity\Assets\StreamingAssets\Content\core\character\`、`facility\`、`city_style\`、`award\`，主文件名同来源 definitionId。

### 第一类：玩家决定是否此时挂链（`entryKind=optional_insertion`）

目前确认按此语义纳入的来源是阿米娅和异客。依据是“此时是否挂正文尚由玩家决定”，不是因为牌面存在“可”或当前实现使用了 Choice。

| 来源 | 现有入口 | 目标挂链方式 |
| --- | --- | --- |
| 阿米娅·合作协议 | `C/infected.character.amiya.tactic.lua`；`infected.character.amiya.json` 的 `character.grant_current_double_use`；`CharacterLifecycleService.CreateDoubleUseInsertion` | 当前角色使用页面显示可用来源；点击才挂消费阿米娅并授权当前 Use 双发的正文。直接选角色模式不另询问；初次激活留场仍为普通角色能力正文 |
| 异客·隐蔽仓库支付替代 | `C/operator.passenger.tactic.lua` 目前只负责初始装标记；`operator.passenger.json` 的 `core.basic_resource_substitution`；`ResourceEffectExecutor.ExecutePayment` | 当前整笔付款的合法时点显示来源，点击才挂替代分配/提交逻辑。不是做完action后问是否结算，也不是所有付款自动先挂替代链然后要求放弃。初始装标记不属于此类 |

### 第二类：收尾由玩家选择顺序（`entryKind=cleanup_order`）

以下 **11种来源段全部纳入排序清单**，不再用“授勋等”省略，也不因旧投影没列出而漏掉。实际本局只登记已触发且属于该玩家、该时点的来源。一个技能的支付与奖励保持同一段，不拆开放到列表两处；同一个已触发技能里的多个影响力最小 Effect 依次执行。

| 来源及完整收尾段 | 现有精确入口 | 迁移边界与放弃政策 |
| --- | --- | --- |
| 多萝西：移除牌上2标记→放置1影响力 | `C/operator.dorothy.tactic.lua` 的收尾分支；JSON `character.execute_cleanup` | 按整段条件式可不使用；Lua登记替代旧 WrapCleanupOffer，不能双登记。牌上标记不足不可发动；空载多萝西仍留场 |
| 泥岩：支付2源岩→放置1影响力 | `C/operator.mudrock.tactic.lua` 的收尾分支 | 按整段条件式可不使用。付款可嵌套异客支付替代，不能把“本体源岩不足但可合法替代”判为不可用 |
| 涤火杰西卡：绑定设施卡面金券费用+2→得2分并弃置角色 | `C/infected.character.jessica_the_liberated.cleanup.lua`；`CharacterContentEffectExecutor.BindingWindow` | 整段可选，金券不足禁用；来源须为有效真实绑定，执行者是使用她的玩家。绑定设施已离场不能凭旧数据继续发动 |
| 诗怀雅：归还牌上1标记→得6金券→自己及原标记所有者同一家企业各升1级 | `C/infected.character.swire.tactic.lua` 的收尾分支；`CharacterContentEffectExecutor.ConsumeMarker` | 先选此整段，再在正文选牌上标记和企业。当前原语允许条件付款前 decline，迁移保留；已归还标记和取得金券不因后续停止退回 |
| 林：移除牌上1标记→从至少20金券对手处取得10金券 | `C/infected.character.lin.tactic.lua` 的收尾分支；同一 ConsumeMarker 原语 | 当前条件段可不发动。有标记但没有合格对手仍允许选择消耗标记，之后不获金券；不能把“没有合格对手”误作整个按钮不可用 |
| 百炼嘉维尔：移动指示物至相邻资源点→移动到达点1影响力 | `C/infected.character.gavial_the_invincible.tactic.lua` 的收尾分支 | `wholeEffectPolicy="optional"`。整段纳入排序，保留永久 token 和影响力原所有者约束。后半无目标按原规则处理，不撤销 token 已移动结果 |
| 持续授勋：得3金券→1个任选基础资源→移动1己方影响力 | `A/infected.award.permanent_support.cleanup.lua`；`ContentLifecycleEffectExecutor.ResolveAward` | `wholeEffectPolicy="optional"`。内部任选资源仍是正文选择，不拆三个底栏按钮。每收尾一次，完整保留领取来源和次数；未执行而整段放弃不补发奖励，已提交奖励不回退 |
| 缪尔赛思策略的延迟段：放置1己方影响力→移动1己方影响力 | `C/infected.character.muelsyse.strategy.lua` 同脚本挂 `cleanupScript` | `wholeEffectPolicy="optional"`。由本回合已发动策略生成真实延迟段，不能凭当前是否有这张手牌生成；内部操作顺序固定，来源离场不删除已挂段 |
| W策略的延迟段：移除2己方影响力 | `C/operator.w.strategy.lua` 的收尾分支 | **不可因点击结束收尾而放弃**。仍由玩家决定何时选此段，不能强制排在所有收益之前；来源牌已回盒也不撤销已产生的本回合义务 |
| 雷蛇策略的延迟段：移除1己方影响力 | `C/liskarm.cleanup.lua` | 同上，不可放弃负面义务；没有合法己方影响力时按原Effect规则完成，不能卡住结束 |
| 赫拉格策略的延迟段：移除2己方影响力 | `C/infected.character.hellagur.strategy.lua` 的收尾分支 | 同上，需关联原始激活节点；不能以当前来源离场或 UI 关闭消掉义务 |

**2026-10-10 用户最终更正已定稿：嘉维尔、永久授勋、缪尔赛思策略延迟段均属于“非必须执行、空闲时可整段放弃”的收尾项。** 三者显式采用 `wholeEffectPolicy="optional"` / `SettlementWholeEffectPolicy.Optional`，合法段投影 `CanDeclineWholeEffect=true`；撤销三项 Unspecified/待裁定状态，不采用用户已说明误点的“必须执行”选项。11类收尾合计8类optional、3类required（W、雷蛇、赫拉格负面义务）。它们仍由 Event 先挂链，点击只选已有段。当前正在执行节点时，仍按该节点自身停止许可和身份处理，不能从整段optional直接扩大所有子节点的停止权限；已提交结果不回退。

### 异客支付替代的具体迁移合同

1. 在 `operator.passenger.tactic` 的持续能力中补 Lua 付款上下文的可用性声明与 `Settlement.Offer` 元数据，按钮文字固定为“异客·隐蔽仓库”，图标固定为 `core:settlement.substitute`。原激活时至多5枚标记的次数式分配保持原流程；这一步不新增底栏按钮。
2. 现有 C# 在 `ResourceEffectExecutor` 内按 `paymentPolicyId` 枚举 `pay|zone|rock|shard|iron` 组合并用 `lua.choice` 展示。后续拆出通用支付上下文/替代预算原语；基础资源范围、至多2、来源标记与空牌离场规则由该能力 Lua 声明。UI不解析组合字符串，不按 `operator.passenger` 写分支。
3. 每笔实际支付有稳定 `PaymentId`，建议直接使用整笔付款的 owner EffectId；同一个资源组合付款被内部拆成分量时必须共享这个身份。字段定为 `PaymentId`、`PaymentCost`、`SubstitutionBudget`、`SelectedSubstitution`。后者含按 ResourceType 分配的数量及真实 `SourceZoneId`，不是新获得资源。
4. 点击按钮后用次数/数量分配交互选择源岩、源石碎片、异铁的替代数，总和至多 `min(2,牌上可用标记数,当前可替代费用总数)`，每类不超过该类本笔费用。金券、至纯源石不可替代；不是每种各2，不可出售。
5. 异客正文未主动挂入时，玩家正常提交付款就执行原付款；付款不足仍失败并按父条件式处理，不能因放弃按钮把原付款当成功。只有金券/至纯源石的付款、免费建设及无可用标记不需要开放此插入窗口。
6. 分配过程中只记这笔支付的草稿，不先移除标记或增减玩家资源。确认时沿同一支付提交边界移回真实标记并扣剩余费用，不能按钮点击扣一次、付款节点再扣一次。牌空弃置跟随这次真实转移；已有的原子支付能力继续使用，不拆成客户端多次命令。
7. 可支付性查询同时考虑正常库存和合法标记替代。不能先按“没有足额本体资源”挡住泥岩、建设等父效果，再等一个永远无法出现的异客按钮。
8. 无论付款来源是主要行动建设、角色、企业、科室或样式，只要调用同一资源支付语义，都进入同一合同。`FacilityBuildEffectExecutor.PayEntityCost` 位于 `FacilityEntryEffectExecutor.cs`，不能再创建另一套建设专用规则。
9. 必要覆盖仅做一笔混合资源付款和一个非建设付款，确认合计至多2、恢复不重扣、未使用时走原付款；不增加每帧全局付款审计或全历史扫描。

### 流形普通盖放与额外盖放的候选合同

2026-10-09 用户明确补充；本节是待落实要求，不代表本轮修改了运行时。

| 入口 | 候选类型 | 来源区域 | 名额与触发 |
| --- | --- | --- | --- |
| 规则“盖放角色牌”阶段 | 可操作的 `core.character_card` 及其派生类，包含 `core.generated_character_card` | 本人手牌 | 使用普通盖牌名额；流形不必先走额外盖放 |
| 缪尔赛思额外盖放Effect | `core.generated_character_card` 及合法派生类 | **仅本人手牌** | 对应Event触发即挂链，进入角色牌选择；使用独立受限额外槽，可结束当前Effect放弃 |
| 赫拉格新盖牌 | 卡面允许的角色牌，包含流形 | 本人手牌或弃牌 | 沿原技能新盖槽及本轮禁用规则；不受缪尔赛思“仅手牌流形”限制 |

候选变量固定为 `handZoneId`、`primaryCoverCandidateIds`、`additionalFlowingCoverCandidateIds`。普通候选使用类型继承判断 `IsAssignableTo(item.TypeId, "core.character_card")`，不能只列基础角色/干员白名单，不能用 `item.TypeId == "core.character_card"` 排除全部派生类。额外候选必须同时满足 `item.ZoneId == handZoneId` 和 `IsAssignableTo(item.TypeId, "core.generated_character_card")`，再应用原有持有者、使用限制与槽资格。

选择阶段和提交阶段使用同一候选来源合同。`CharacterLifecycleService.Cover(..., additional: true)` 还服务赫拉格，不能把它全局改成“仅手牌”或“仅流形”；缪尔赛思该次请求须携带/保存自身的手牌流形候选范围，提交时依据该范围处理，不能只凭 additional 或 reusable 推导所有卡牌同一规则。继续使用既有角色选牌与 AnswerInteraction，不造流形专用 Choice 列表或底栏发动命令。

只读定位地址：

- `G:\NoMadCity\Assets\YC\Domain\Items\ItemTypeRegistry.cs`：流形继承 `core.character_card`。
- `G:\NoMadCity\Assets\YC\Domain\Cards\CharacterCoverEffectExecutor.cs`：正常盖牌候选与答案入口。
- `G:\NoMadCity\Assets\YC\Domain\Cards\CharacterLifecycleService.cs`：`Cover` 的通用类型、来源和槽处理。
- `G:\NoMadCity\Assets\YC\Domain\Cards\CharacterContentEffectExecutor.cs`：`Cover` 的额外盖放候选，当前可复用槽分支从手牌筛选。
- `G:\NoMadCity\Assets\StreamingAssets\Content\core\lua\characters\infected.character.muelsyse.tactic.lua`：普通Event直接返回 `ChooseAdditionalCover`，不能改为 Offer。

后续仅需少量定向覆盖：普通阶段可选手牌流形；额外效果排除弃牌流形及非流形手牌；事件后未经发动点击已有Effect并能结束当前Effect；赫拉格仍可从弃牌区新盖。读取上述局部代码不等于已经验证完整UI路径，本轮不运行测试。

### 对照后明确不作为独立底栏来源的内容

| 内容及精确入口 | 保持的入口与原因 |
| --- | --- |
| 缪尔赛思额外盖放，`C/infected.character.muelsyse.tactic.lua` 的相应Event分支及激活正文 | 订阅和触发条件满足时，额外盖放Effect一定挂上链，直接进入选牌；只从当前玩家手牌选流形，结束行动可放弃当前效果。不显示可选插入来源按钮 |
| 缪尔赛思采集开始回收，`C/infected.character.muelsyse.tactic.lua` 的 `CollectionStarted` 分支 | 正常Event响应挂链；内部是否回收/允许停止按卡面处理，不能因为有 `recycle/decline` 就改成手动决定挂链 |
| 多萝西收留离场影响力，`C/operator.dorothy.tactic.lua` 的 `InfluenceRemoved/InfluenceReplaced` 分支 | 对应Event自动挂响应链；收留或沿默认去向属于该链内部处理，不先要求点击底栏才挂响应。保留原实例/所有者和已成立的移除事实 |
| 临时聚居点，`F/infected.facility.temporary_settlement.declared.lua` | 本次宣告实际使用的设施触发Event后正常挂条件式；条件是否执行不等于此时是否挂链，不增加宣告后发动按钮 |
| 城区管理中心，`S/infected.city_style.district_management.declared.lua` | 宣告Event正常挂回收样式标记的条件式；选标记或放弃在既有链内处理，不增加做完宣告后“是否结算”的阶段 |
| 异客/多萝西初始装入标记，`C/operator.passenger.tactic.lua`、`C/operator.dorothy.tactic.lua` 的激活分支 | 数量分配是已经发动的计谋内部交互，不是新的可选插入源 |
| 可露希尔，`C/operator.closure.tactic.lua` | 取设施仍在计谋内部；`CandidateSetBuilding` 自动向“可选设施牌”加入牌下真实实例，不额外问是否发动一次永续。明细查看仍直接卡面 |
| 所有普通正文的选企业/企业特效、影响力/航道、资源、数量、重复次数、盖放目标、干员接受/换出等 | 各自使用企业面板、地图高亮、资源/次数式、卡牌选择等对应交互。不是仅因有选择就新增胶囊；跳过用原底栏结束语义，不把 `choice.skip` 当候选牌 |
| 快速行动授勋 `A/infected.award.enterprise_special.quick.lua` | 保留玩家快速行动入口和次数，不搬到收尾或在两处复制入口 |
| 立即授勋 `A/infected.award.score.acquired.lua`、`A/infected.award.roads.acquired.lua` | 取得后立即结算；其正文含选择不代表整段可不发动 |
| 新兴建筑群 `S/infected.city_style.emerging_buildings.declared.lua`，设施入场奖励、企业升级到达奖励、系统自动补牌 | 按原事件链直接挂载；不因同属宣告/建设后插入就放按钮。正文可停止的部分仍按原规则 |
| 首动与拓荒指挥中枢赋予的双发资格，`S/city_style_pioneer_command_hub.special.lua` | 保留既有资格及角色使用链；第二模式直接接续，不能重建“继续/结束使用”中间页 |
| 建设按颜色自动修正费用，`G:\NoMadCity\Assets\YC\Domain\Facilities\FacilityBuildCostService.cs` | 当前真实报价政策，不为了“减价”图标改成点击才生效 |
| 初始可选影响力跳过 `GameplayExpansionUi.ShowOptionalInfluenceSkip`，调度两次移动、底栏停止当前Effect | 系统/当前Effect控制，不伪造 Lua 卡面能力混入来源队列 |
| 角色回收、样式标记复位、先手传递、回合递增、锁定token到期清理等 | 系统时点与生命周期自动处理，不属于玩家选择收尾效果先后顺序的来源卡效果 |

后续发现其他来源时，先查“当前正文是否已经必须由Event挂链”。若是，不能因正文允许放弃而列为可选插入；只有是否此时挂链由玩家决定的来源才进第一类。收尾需要玩家选择执行顺序的段独立进第二类。企业家模式须单独做同样核对，不能把未审阅素材中的能力默认并入本批。

角色文件迁移时提高相关定义/脚本版本并更新内容哈希。旧在途 `effect.character.content.cleanup_offer` 节点保留原执行器恢复路径；不能把旧节点静默解释为新类型，也不能承诺不同内容哈希的旧对局可无条件恢复。

## 后续任务拆分、依赖与文件所有权

下面是执行准备，不在本轮启动这些实装工作。

| 工作包 | 负责人建议 | 修改范围 | 依赖与并行 |
| --- | --- | --- | --- |
| 通用声明与窗口 | EXP-015 | 新 `Assets/YC/Domain/Effects/SettlementEntryService.cs`（元数据/已有节点调度，不新增可选登记Effect）；新 `Assets/YC/Infrastructure/Lua/SettlementLuaCatalog.cs`；DTO、Lua构造器、Host提交、窗口投影 | 先落实本文件接口；EffectKernel/EffectRuntimeState/ExternalContentPack/CharacterCardLuaCatalog 串行独占，必须保留本轮修复 |
| 素材与按钮组件 | EXP-012 | `Assets/YC/Presentation/Settlement/` 下新 View、配置、Sprite和两Prefab；只读正式DTO | 可与规则底座并行准备独立新组件；暂不改公共HUD。5张背景及用户最新图标按逐来源清单接入，未定位键须先落实文件，焦点层排除；不启动旧生成器 |
| 内容迁移 | EXP-015 主，必要时 EXP-010 | 上表真实 Lua/JSON 引用，纯可用性脚本 | 等通用构造器与查询合同通过；不同卡文件可并行，Registry/加载器共享文件只一人写 |
| 支付插入与正常Event边界 | EXP-010，与015串行交接 | ResourceEffectExecutor.cs、FacilityEntryEffectExecutor.cs；只读核对影响力离场和宣告链 | 等上下文/付款合同；与015不能同时写公共执行器。异客覆盖整笔支付和非建设入口；不得把离场/宣告响应改成手动发动 |
| HUD 接线 | EXP-012 | GameplayExpansionUi.cs；GameplayInteractionHud.prefab 结算宿主；必要主题引用 | 等规则DTO、组件、两类入口及支付嵌套的真实投影完成；先记录场景是否覆盖相同宿主，再改实际来源。不能拿未使用源Prefab代替真实画面 |
| 少量联合验收 | EXP-013 | 定向测试与证据 | 等所有生产冻结，独占Unity；不重建/保存覆盖UI，失败按实际归属回报 |

与玩法文案迁移联合实施时，先落实共享的 `GameplayTextRef`、文案/图标目录和投影合同，再并行开发结算规则与文字 renderer；按钮组件直接消费统一显示合同。两侧所需能力完成后接入真实 HUD，先完成一条真实路径再接齐13个来源，其余页面文案按批次迁移，不必全部改完才交付按钮。具体顺序见 [玩法文案准备第10.1节](局内玩法文案与图标转译-实装准备.md#101-与结算按钮的共同落地顺序)。

主依赖：共享显示合同 → 结算规则与文字/按钮组件并行 → 内容迁移及支付/事件恢复接入 → HUD接入 → 冻结后少量联合验收。每个来源的规则政策须在其实际接入前定稿。全量内容范围按上述两类逐项交接，不能以“两张示例牌能显示”代替迁移完成；共享文件保持单一写入负责人。

只在未来明确执行时更新 M03“未实现”的状态；本准备文件及素材映射不能把清单状态改成“已接入”。旧临时结算行只能在新入口实际替换后移除引用，删除资产另行核验，不能顺手批删。

## 少量验收与防卡顿要求

1. 一个真实阿米娅窗口：按钮由 Lua 声明生成，单击一次插入当前使用链；源牌消费一次、顺序正确、旧请求拒绝、恢复不重复消费。前后主链身份相同。
2. 一个真实收尾窗口，含可放弃条件段与己方负面移除义务：Event已产生对应段，点击仅选已有TargetEffectId，不重复挂链；同列且由玩家选先后，不弹 activate/decline，不预扣费。覆盖未执行节点且仍有必结项时拒绝整窗结束、必结项全部完成后一次放弃剩余可选项，以及当前可停止节点结束后其他项仍在。已提交结果不回退，明细仍直接卡面查看。
3. 异客一笔混合资源支付及一个非建设来源，顺带覆盖收尾嵌套支付返回：合计上限2，不重复扣标记/资源，未点击前无替代正文节点，未用走原支付。再对比缪尔赛思正常Event：未经底栏点击已有额外盖放节点，直接选牌或结束当前效果；不以两者画面相似当成同一种挂链方式。
4. 同一按钮组件覆盖溢出、末尾完整显示、长文字完整说明、拖动不点击、普通单击立即触发、五态同尺寸；重绑同ID不重新入场，换人不显示上一人的私密项。按钮存在（含pending/disabled）时不显示动态提示布局，按钮消失后恢复当前合法提示，不展示旧窗口反馈。不新增键盘交互测试。
5. 同一输入只取少量点击/绑定计时；确认普通点击没有人为延迟、动画不阻塞提交、同revision不重跑Lua、纯hover不全投影、同帧批次不逐个重建布局。发现成本再定位，不增加每帧校验或全历史扫描。以上为后续实施的定向检查，本轮不运行游戏测试。

功能测试不锁所有可调字号、坐标和颜色；必要裁切/防误触/状态语义可以检查。视觉必须使用实际场景、实际源Prefab/覆盖和当前素材截图确认，不能只用hash或测试通过宣称画面符合预期。

## 本次准备完成与未做内容

已按用户对挂链时点的纠正重新划分：阿米娅/异客由玩家点击决定此时挂链；缪尔赛思额外盖放/回收、多萝西离场响应、临时聚居点与城区管理中心保留正常Event挂链；11种收尾来源参与已有段排序。补充普通盖牌可选流形、额外盖放只取手牌流形的合同。上述修订均为文档要求，不代表代码已经按新要求修改或验证。

2026-10-10 补充：结束当前节点与无活动节点时结束收尾窗口的行为、按钮优先互斥布局、三项收尾均为optional已由用户明确。13个来源图标键与文字保持指定清单，`target_other` 和三种状态图无待确认项。六执行者已获分批实施授权，实际代码进度及验证以《执行协调台账》和各自执行记录为准；本合同更新不代表代码、Prefab或当前版本已经通过。
