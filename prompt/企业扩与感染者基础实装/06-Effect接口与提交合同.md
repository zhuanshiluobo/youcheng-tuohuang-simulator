# Effect接口与提交合同

## 2026-10-15 最新修订：条件正文由来源显式提供

用户明确复杂条件式由 Lua 指定完整文本，个别 C# 事件牌和主要行动可由内核来源提供；统一沿现有 GameplayTextRef/WithPresentation 展示，不反编译左右 Effect 树。当前条件确认只显示当前条件子段，采用 `A[icon:core:syntax.condition]B。`，不追加通用费用说明替代正文、不重新纳入已执行奖励。保单 Pay 与复杂左式的原支付/确认语义、普通必付权限、内部技术门控、原小窗和 MainButtons-v3.2-Patch。实现与文件边界见[条件正文与可选建设退出修补合同](条件正文与可选建设退出-修补合同.md)，登记不代表已验证。

## 2026-10-15 用户授权：条件确认与统一支付前

最终入口为[条件确认与统一支付前实施合同](条件确认与统一支付前-实施合同.md)，当前进入实现，不能把合同登记视为已验证。真实规则条件左式开始前确认，内部技术门控不机械增加拒绝权；原本免费无条件正文不使用空左侧包装。单笔非分数混合资源复用现有`PayResource(cost=...)`及同一`PaymentId`，新增真实`BeforeResourcePayment`规则事件，沿已有事件分发和结算来源机制。替代分配只存草稿，最终付款仍由原确认宿主发起，原价不足不挡住合法替代入口。

建设和主要城市移动允许可取消的规则端付款准备，准确取代旧城市移动“预选绝无权威操作”的限定；主行动仍原最终确认受理才挂链，未确认不扣款/次数且可改选。准备费用不是新游戏阶段或最小Effect；建设费用是通用Pay的业务特化。具体文件owner、请求字段、UI用途和必要验证见新合同及负责人接口回执。本批不处理独立代码接口审查的其他问题，不扩企业家或恢复TransferResource。

## 2026-10-14 用户确定：失去资源与TransferResource撤除

用户明确新增`Effect.LoseResource`最小Effect，并要求清理`TransferResource`错误实现，撤销此前冻结。最终实施入口为[失去资源与异客策略-实施合同](失去资源与异客策略-实施合同.md)。新typeId为`effect.resource.lose`、Atomic、初版1.0.0；Lua参数`apiVersion=1,player,targetPlayer,resourceType,amount`。它按实际可扣余额失去资源、不要求足额、不走付款替代，返回实际失去量；具体字段、金券预留边界及文件归属见新合同。实现已停写交付，当前验证进度及证据以执行协调台账为准，不将接口登记当运行通过。

异客策略改为本人依次选择对手、基础资源、0至min(4,实际库存)数量，再执行LoseResource与GainResource，保留双方各1分。选择沿现有页面及bounded_amount协议，选择未齐不改资源。通用数量/续接增量已核定为Choice.amountRange、resultContext以及仅Choice/Lose顶层同能力显式completionHandlerId，准确schema与恢复边界见新合同末节；不新增角色专用资源转移大Effect。旧`Effect.TransferResource`、typeId、执行及恢复分支已撤除，不能保留可执行兼容别名；旧不可支持快照按既有加载机制明确拒绝。下方2026-10-05/09关于该旧API的登记只保留历史解释效力，不能再作为新增调用依据。

## 2026-10-10 用户确定：结束行动的当前节点与整窗边界

当前没有正在结算的节点时，只有在不存在未完成的必须执行收尾项的情况下，才能通过结束行动一次放弃全部剩余非必须执行的收尾项。当前正在结算非必须全部执行的节点时，结束行动只跳过并完成该节点的剩余部分；保留已提交结果和其他待结算项。当前节点语义优先，不得串入整窗放弃分支；必结节点不能借此跳过。

`WholeEffectPolicy`/`CanDeclineWholeEffect`与当前节点停止许可分开，Host在既有命令提交边界使用真实窗口、节点身份与义务状态判定。按钮点击仍只排序已经Event挂链的收尾段，不新建正文；不能因可停止改成手动挂链。用户最终更正明确嘉维尔、永久授勋、缪尔赛思策略延迟段均为非必结收尾：显式 `SettlementWholeEffectPolicy.Optional`，合法段 `CanDeclineWholeEffect=true`，撤销三项未定状态。W、雷蛇、赫拉格负面义务保持Required。具体行为表和最小验收见[结算按钮合同](结算插入按钮-Lua定义与实装准备.md)。以下旧实现记录不是当前新增规则的完成证明。

## 2026-10-09 后续设计纠正：Event挂链与手动插入

正常Event响应必然挂入其Effect；AllowDecline仅控制已挂链效果能否停止，不作为生成底栏插入按钮的判断。可选插入正文由玩家在合法上下文点击后才挂链，不在action结束后统一询问。缪尔赛思额外盖放保持普通Event挂链；普通盖牌候选包含手牌流形，额外盖放仅手牌流形。

收尾按玩家选择已有段的执行顺序。下节“强制子效果先执行、再开放可选来源”是旧实现记录，作为后续目标已撤回；不能据此继续替玩家预排先后。新合同与未实装状态见[结算按钮准备](结算插入按钮-Lua定义与实装准备.md)。本轮只改文档，旧在途协议仍需实现时迁移，不宣称代码已更新。


## 2026-10-09 数量与可选收尾增量

- 数量请求采用 `effect.quantity.choose` / `bounded_amount`，`PromptParameters` 固定整数 `minAmount/maxAmount` 和字符串 `amountKind`（`player_marker` 或资源类型），空 `CandidateIds`，最小/最大候选数均为0。Host 按卡面与当前实际供应给出上限；沿既有 `AnswerInteraction.answerValue` 提交十进制整数字符串，不生成数字 Choice，不改 `selectedAmount` 持久化合同。范围或供应过期时拒绝/刷新，不自动截断答案。`LoadCardMarkers`继续使用；旧TransferResource使用该协议的事实不构成保留其错误实现的理由，撤除及旧数据规则以上方2026-10-14决定为准。
- 数量原语和 `TakeSupplyFacilities` 的可停止步骤使用请求级 `AllowDecline=true` 与布尔 `false`，不设置节点级 AllowDecline。取牌新候选仅含真实设施实例；停止保留已取得及已补原槽的结果，不回滚前序步骤，也不跳过整个角色或主要行动。无候选正常完成。必付费用、强制负面步骤不获得停止资格。
- 持续能力声明 `settlementActionId=character.execute_cleanup`，必须同时有 `cleanupScript`；复用既有 `titleKey/descriptionKey`。可选收尾 Lua 返回实际正文，由注册器包装为内部 `effect.character.content.cleanup_offer`；正文保存在既有 NestedEffects，登记不扣费。NormalizedArguments 固定 apiVersion=1、cardInstanceId、abilityId、windowEffectId，由 Host 产生。
- 主收尾节点先完成强制子效果，再开放 `character.cleanup.settlement`，沿原布尔答案与请求级 AllowDecline。各可选来源统一投影，点击仍走 InvokeSettlementOption；主节点收据 `invokedOptionIds`、窗口失效、正文插入在同次提交。正文结束可重新开放剩余来源；底栏 false 结束窗口使未用来源失效。已启动正文保留自身强制/可停止语义，不放宽 GameSession 门禁。
- 杰西卡登记到同一窗口，费用仍由 BindingCost 实时计算，正文复用 BindingSettlement；授勋既有动作不变。新增结算动作不得被空 useId 的授勋分支误接。异客资源与玩家拆为两次业务选择，保留合法第二技能与稳定玩家引用得分。

详细字段、调用例子与分工见[本轮接口交接](G:/NoMadCity/Logs/CharacterInteractionRevision/20261009/接口交接.md)。本节是已批准合同，实际完成与验证以本轮交付为准。

建设候选补充：`ChooseBuild.includeStoredFacilities` 可选 Boolean，默认 true，普通建设包括本人有效持续牌设施区；工程科限定供应区及海德刷新后免费建设显式 false。`allowDecline` 由设施候选中的 `choice.skip` 执行，不叠加外层内核弃用交互。新增选择复用 `lua.choice` 路由。离场暂存区跨玩家收留仅在真实 pending Departure、目标收留政策、实际载体控制权与 Cube 原主体匹配时允许；Relocate 的祖先链校验保持。

只读门面补充：`Global.Content.FindZones(filter)` 沿执行玩家的 ItemZoneQuery 投影返回可见区域的 `id/templateId/localZoneId/parentId/owner/itemCount`；不含牌堆顺序或隐藏物品身份。`FindInstances` 的可见实例补充 `zoneId/typeId/owner`。

## 2026-10-05剩余企业能力接续合同（先登记，实施/证据见固定执行记录）

所有新入口使用apiVersion=1、当前执行玩家player；跨玩家资源收付只改变明确收付对象，不改节点执行者。以下组合沿现有ContentPrimitiveEffectExecutor/ItemTransferEffectExecutor实现，唯一v1注册，复用原Interaction/Candidate/RuleCommit。Lua不能传随机种子、内部进度或授权键。

| 入口 | 固定参数及语义 | 结果/持久阶段 |
| --- | --- | --- |
| `Effect.RefreshFacilitySupply` / `effect.facility.refresh_supply` | `apiVersion,player`；公开现有整批返回→洗牌→逐空槽补牌组合 | 保留原`returned/shuffled/refilled`及`selectedItemIds`，不边取边补 |
| `Effect.RefillFacilitySlot` / `effect.facility.refill_slot` | `apiVersion,player,slotZoneId`，只能真实供应槽；内部Host工厂仍接受原参数 | 原`changed,movedCount,movedItemIds` |
| `Flow.RecoverCharacterAndDiscardOthers` / `effect.character.recover_discard_others` | `apiVersion,player`；选本人真实弃牌1张回手，公开该牌，再公开弃其余实际手牌；空弃牌Failed，无条件奖励 | `choose/finished`，`selectedItemIds`；同次提交实际移牌，结果`changed,recoveredItemId,discardedItemIds` |
| `Flow.TakeSupplyFacilities` / `effect.facility.take_supply` | `apiVersion,player,maxCount(0…2),distinctBy="name",destination={zoneId}`；目的为公开回盒或本人在场角色设施载区；每张移走并补原槽完成后再列候选，允许停止 | `choose/transferred/refilled/finished`，`nextIndex,selectedItemIds,selectedNames,originalSupplySlotZoneId`；结果`changed,movedItemIds` |
| 原`Effect.TransferResource` / `effect.resource.transfer`（2026-10-14已撤销） | 旧登记曾把数量选择与双边收付合在一起，现不得作为可用API；公开入口和执行实现清理按本文件顶部及新合同 | 历史choose_amount/finished原件不改写；不保留可执行兼容分支 |
| `Effect.SellResource`现有typeId增量 | `apiVersion=1,player,targetPlayer,resourceType,saleMode="all",unitPrice(1…99)`；target为本局任意玩家，三基础资源；在Host提交时售空实际余额，现金归target，不用牌载标记替代；旧无版本选择数量语义保留 | 新分支原子，结果`amount,revenue,targetPlayer,resourceType`；不修改旧awaiting节点解释 |
| `Effect.LoadCardMarkers` / `effect.card.load_markers` | `apiVersion,player,cardInstanceId,localZoneId,maxCount(0…16)`；本人在场角色真实Cube载区，从本人实际玩家供应选0…min(maxCount,库存)；仅首次投入，不改变区容量 | `choose_amount/finished`，`selectedAmount`；结果`changed,movedCount,movedItemIds` |
| `Effect.PayCardMarkers` / `effect.card.pay_markers` | `apiVersion,player,cardInstanceId,localZoneId,count(1…16)`；完整预检真实Cube集合，按原Owner/类别供应归还，整个费用不足不移首枚，不产生地图Removed | 原子，结果`changed,movedCount,movedItemIds` |
| `Effect.StoreRemovedInfluence` / `effect.card.store_removed` | `apiVersion,player,cardInstanceId,localZoneId,influenceItemId`；只能响应祖先Removed/Replaced未解决的真实待去向Cube，匹配原拥有者或声明接受的非玩家Cube；同实例入牌区，不复制 | 原子，经InfluenceDestinationResolver；结果`changed,movedCount,movedItemIds` |
| `Flow.ClearRouteAndPlaceRoad` / `effect.map.clear_route_road` | `apiVersion,player,scoreRemovedPlayers,scoreExecutor`（0…99）；只选无公路航道；冻结选中航道实例，按从发动者起的本局玩家顺序逐枚真实移除、等待响应，放路后按实际Removed事实去重玩家得分，再发动者得分 | `choose_route/removing/road/scoring/finished`，`selectedRouteId,selectedItemIds,nextIndex,processedSubjectKeys`；结果`routeId,removedPlayerIds` |

既有`Effect.SelectInfluence`增加`candidateScope="resource_points"/"routes"`，按实际地图位置过滤并提交复验；增加`requireCompleteSelection=true`用于费用，要求requiredCount=count且一次完整选择足数真实实例后才创建逐枚移除子树。初始不足无支付；每枚等待响应并按实际removedItemIds计数，后续实例变化导致真实失败，不虚报完整费用、不创建Condition右式。`Effect.ChooseBuild`增加`maxGoldVoucherCost`（非负int），候选及付款前复验卡面标价；supply候选同时包含本人有效持续牌设施区，仍付正常费用。

持续定义新增`observedEvents[]`（仅InfluenceRemoved/InfluenceReplaced）、`observerSubjectPolicy="departed_owner"`；cleanupScript仍按本人PlayerCleanupStarted。订阅用真实已激活持续实例，去向事件以该牌控制者调用Lua并保留原移除事实；收尾按事件/实例收据去重。多萝西空区保持，实际换出归还余Cube并注销。可露希尔/异客空区退出由牌载配置声明，不按卡ID判断。

支付替代配置见05。原PayResource（单项与复合）和内部建设费用使用同一付款分配/原子提交；每整次费用总≤配置上限2，仅三基础资源，可选0…2及分配。选量及原完整费用保存，提交复验牌区、标记及剩余余额；失败不扣标记。金券/至纯及售出不替代。旧无替代配置、已完成节点结果及定义版本保持，新增付款阶段`choose_substitution/finished`。

`Global.Map.GetMap/GetRoute`公开真实routes、航道槽及roads，只读且不含隐藏信息；`Global.Content.FindInstances`沿既有ItemZoneQuery授权玩家投影增加实际可见实例id/definitionId/typeId/zoneId/owner/controller，不公开隐藏牌堆、其他玩家的秘密回盒或其他玩家手牌；本人私密回盒按2026-10-08最新可见集合授权，不因此获得取回或操作权限。完成回调使用新快照，不缓存放置前航道影响力总数。道路实例定义由本局冻结ContentDefinitions的组件映射按core.road_token唯一选择；无新映射的基础存档仍用core.component.road，不机械全局替换。

本文件指定未来实现签名和语义，不表示当前宿主已公开这些API。必须结合[05状态命名](05-状态模型与命名合同.md)、[04真实路径](04-路径索引与实施文件归属.md)执行。C#公开字段PascalCase；下表Lua/NormalizedArguments键严格使用原拼写，不另起同义字段。

## A. 复用的实际执行接口

已读取`G:\NoMadCity\Assets\YC\Domain\Effects\EffectKernel.cs`。新增执行器统一形状如下，不能另造`IEffectExecutor`或`ExecuteAsync`协议：

```csharp
public static EffectStepResult Execute(EffectExecutionContext context);
// 在现有EffectRegistry组合入口注册一次：
new EffectRegistration(effectTypeId, Execute, EffectExecutorKind.Atomic, "v1");
// 等待交互/子节点的组合用EffectExecutorKind.IntrinsicFlow。
```

`context.State`是当前内核工作副本，`context.Node`为当前节点；等待用`EffectStepResult.Continue(flowStage).AddInteraction(spec)`，子节点用`.AddChild(effectSpec)`，完成用`Completed(NormalizedValue)`，业务不足用`Failed(reasonCode)`。只在现有内核RuleCommit边界提交；不要自行保存全局State、另开事务、调用Unity刷新或发送网络广播。

Interaction使用现有`EffectInteractionSpec`：必须填InteractionTypeId、AnsweringPlayerId、Visibility、PromptKey、MinSelections、MaxSelections、AllowDecline、AnswerSchema及CandidateSetId/Version/ResolutionId；CandidateIds来自权威候选。UI调用`EffectInteractionCommands.Answer`，Host使用`GetLatestInteractionAnswer()`读取并复验。禁止把玩家点击的索引、卡名或Unity实例当真实itemId。

每新增一个API，依次修改以下**现有文件**，完成前不得加入对外能力清单：

1. `G:\NoMadCity\Assets\YC\Infrastructure\Lua\MoonSharpLuaRuntimeHost.cs`：构造器与参数白名单、只读门面。
2. `G:\NoMadCity\Assets\YC\Infrastructure\Lua\LuaRuntimeContracts.cs`：RawArguments/嵌套组支持，保持规范化限制。
3. `G:\NoMadCity\Assets\YC\Infrastructure\Lua\LuaEffectSpecCompiler.cs`：类型及参数验证、Spec编译。
4. `G:\NoMadCity\Assets\YC\Domain\Effects\GenericLuaEffectExecutor.cs`：迁走相应旧stub注册；不与新执行器双注册。
5. 04列出的具体执行器、现有EffectRegistry组合根、`GameStateCloneService.cs`及两个快照DTO。
6. `G:\NoMadCity\Assets\YC\Domain\Interactions\InteractionRequestProjector.cs`与对应UI路由，仅输出可见信息。

`apiVersion`为参数schema版本，不冒充EffectRegistration.DefinitionVersion；后者本组新执行器用`v1`，变更已有定义版本时须提供恢复映射。旧移牌输入没有apiVersion时继续旧语义；两个既有移牌API的新参数用apiVersion=2，全新API的初版用apiVersion=1，以各表为准。不允许一个注册表对同一typeId重复Register。现有通用注册版本是1.0.0，不可直接视为v1；003及后续执行器逐typeId登记旧参数/阶段/结果到新版的恢复映射，不能无歧义转换的未完成节点拒绝legacy_entity_state_unsupported。

## B. 引用、主体、查询与内部提交

Lua引用固定：`{itemId="..."}`、`{zoneId="..."}`、`{zoneId="...",slotId="..."}`。传给宿主时转换05类型；空串不自动代表当前玩家/当前牌。`player`沿用ctx.playerId字符串；company使用明确主体`{kind="company",companyId="stonehoove_security"}`，不能传player=0。

002新增`ItemZoneQuery`公开只读方法，返回对象为副本或不可变视图，不能供Lua直接修改：

```csharp
ItemInstanceState FindItem(GameState state, string itemId); // 不存在返回null
ZoneInstanceState FindZone(GameState state, string zoneId); // 不存在返回null
IReadOnlyList<string> GetItemIds(GameState state, string zoneId); // 未知区域抛合同错误
IReadOnlyList<string> GetTopItemIds(GameState state, string zoneId, int count); // 可少于count
bool IsInfluence(GameState state, string itemId);
bool IsPlayerMarker(GameState state, string itemId);
```

003新增`ItemTransferService`，不得由UI直接调用：

```csharp
ItemTransferValidationResult Validate(GameState state, ItemTransferRequest request);
ItemTransferResult RelocateItems(EffectExecutionContext context, ItemTransferRequest request);
```

`ItemTransferRequest`固定字段：`List<string> ItemIds`、`ItemPositionRef ExpectedSource`、`ZoneRef Destination`、`string InsertionKind, DestinationSlotId, OperationProfileId, CarrierPolicyId`、`int ExecutingPlayerId, RequiredCount`。`ItemTransferValidationResult`为`bool IsValid,string ReasonCode`；`ItemTransferResult`为`bool Changed,int MovedCount,List<string> MovedItemIds,List<ItemPositionChange> PositionChanges`；PositionChange含ItemId、From、To。收据/commitId由现有内核持有，不额外建立可写TransferReceipt表。

公开MoveItem只接受同一个预期源区域的集合；跨不同源的原子交换必须由有权限的业务执行器验证完整交换后在同一工作副本中提交，不连续两个公开MoveItem假装原子。转移之前对整组去重、复验所有来源、目标、资格/容量，任一失败则整组不动。

## C. 转移、洗牌、有界次数（003）

| Lua名称 / typeId | 必填及可选键 | 结果与阶段 |
| --- | --- | --- |
| `Effect.MoveItem` / `effect.item.move` | `apiVersion=1, player, itemRefs[], expectedSource, destination, insertion, operationProfileId`；可选`requiredCount=0, carrierPolicy="preserve"` | 原子；结果`changed,movedCount,movedItemIds`；逐项from/to在Host结果，秘密实例不公开 |
| `Effect.ShuffleDeck` / `effect.deck.shuffle` | `apiVersion=1, player, deckZone`；不接收客户端随机种子或牌序 | 原子；`changed,cardCount`；Host保存实际最终牌序及输入集合，无公开牌序 |
| `Effect.MoveFacilityCard` / 既有`effect.facility.move_card` | apiVersion=2时接受MoveItem同键，只允许FacilityCard；无apiVersion则仅保留旧supply→deck_bottom且自动补牌路径 | 新版不暗补牌，调用链显式补空槽；旧版由一个兼容组合桥接后调用同一内核 |
| `Effect.MoveCharacterCard` / 既有`effect.character.move` | apiVersion=2时接受MoveItem同键，只允许CharacterCard；无apiVersion保留旧discard→hand批量回收语义 | 不扩大旧协议范围；不把持有者变更/使用结束藏在普通转移里 |
| `Effect.Repeat` / 既有`effect.flow.repeat` | `apiVersion=1, count, effects`；可选`allowEarlyStop=false`；count非负且不大于宿主本次Effect预算 | IntrinsicFlow；FlowStage=`pending_iteration/awaiting_iteration/finished`；ContinuationData=`nextIndex,requestedCount,completedCount` |

`insertion`为结构对象：`{kind="unordered"}`、`{kind="top"}`、`{kind="bottom"}`或`{kind="slot",slotId="..."}`。牌堆index=0是顶；批量入顶保持itemRefs给定顺序，首项仍在最顶；入底追加保持顺序。未知kind拒绝。FixedSlot已有物品拒绝，空牌堆GetTop不足不创造牌。

无apiVersion的兼容包装还保留旧结果形状：MoveFacilityCard返回所选旧cardId字符串，空供应为字符串no_target；MoveCharacterCard返回实际回收数量整数（空为0）。桥接通过迁移映射转换稳定ItemId与旧引用，不把新MoveItem的对象结果塞给旧continuation。既有awaiting阶段/交互答案只能按旧语义恢复；不能用新参数重解释旧持久节点。

MoveItem空数组且requiredCount=0→Completed，changed=false；requiredCount>实际数量→Failed(`insufficient_items`)且不移动；重复itemId、未知实例、源错、禁入分别返回`duplicate_item_id/unknown_item/source_zone_changed/destination_rejected`。源变化由父交互按当前状态刷新候选；不能把真正非法引用当缺式吞掉。

Shuffle只洗当前deckZone成员；空或单牌成功无变化；多牌由Host RNG生成最终排列，在同一规则提交保存OrderRevision和结果。恢复已完成节点不得再掷随机数。设施原语不得顺手回收别区卡，补牌不得顺手洗牌。

现有CardPoolService.Draw/Peek从列表末尾取顶，FacilitySupplyService.ReturnSupplyCardToDeck在索引0插入底部。002迁移旧牌序时反转一次，使新索引0为顶且保留后续抽取序列；003旧API包装及只读旧牌序投影按旧方向适配，不在每次恢复/洗牌时再反转。设施供应固定六个槽区，牌堆不足留下空槽，不用缩短列表改变槽身份。随机最终排列放在类型化ZoneOrderState，NormalizedResult只给数量和允许公开的摘要，不能突破现有NormalizedValue总元素128/深度8/字符串1024字节限制。

Repeat不是预先展开所有依赖当前候选的交互。每轮上一个子树完成后才实例化下一轮effects，记录实际completedCount；allowEarlyStop的停止答案只结束剩余次数，不撤销已完成轮。单步缺候选按该子式结果处理，不能无限重试。

2026-10-03，003实装补充（公开签名不变）：

- MoveItem、ShuffleDeck、两个移牌包装和Repeat的宿主定义版本为`v1`；设施建设仍使用既有`1.2.0`。`Effect.ShuffleFacilityDeck({player})`保留无apiVersion的设施牌堆语义，也接受`apiVersion=1,player,deckZone`的类型包装，deckZone必须是实际设施牌堆。新API玩家键严格为正整数的十进制字符串，旧设施洗牌仅在无版本兼容分支保留旧`p1`形式。
- Host组合工厂提供`ItemTransferEffectExecutor.RefillSpec(player,slotZoneId)`、`RefreshSpec(player)`；内部typeId为`effect.facility.refill_slot`、`effect.facility.refresh_supply`。它们不进入Lua构造器白名单；010通过归属内容装配显式组合。普通建设另以内部`effect.facility.pay_build_cost`原子保存真实支付结果，随后分别生成取牌、补原槽、放城市、得分和入场节点，复用原RuleCommit/DispatchReceipt。
- 空MoveItem对象结果补`reasonCode="no_candidates"`，空补槽/洗牌补`empty_deck`，单牌洗牌补`limit_reached`；旧移牌包装保持原字符串/整数形状。明确apiVersion下缺必要字段的原语保存一次`incomplete_effect_skipped`日志并以失败结果阻断Condition奖励；未知schema、客户端随机参数、权限和损坏状态不按缺式吞掉。
- 本轮实体交互都保存`CandidateSetId/CandidateSetVersion/CandidateResolutionId`与解析记录。`PromptParameters.candidateDefinitionIds`与最终CandidateIds同索引，供授权视角显示卡面；候选按稳定ID排序，不表达秘密牌堆顺序。建设放置请求沿既有`facility.build.placement`，提示参数保存facilityId/paymentMode。来源变化时，尚未支付的建设选择更新候选并保留可回答的新请求。
- 六供应区由002工厂建立，003只移动真实实例；旧FacilitySupply投影始终六项，空槽为显示用空字符串，不能当Item。公开MoveItem不会自动发“进入手牌”“成为影响力”等业务响应；后继007/008显式装配相应事件。
- 私有来源权限仍按实际持有者检查；CharacterCard合法转交可进入另一玩家手牌，ControllerSubject随手牌玩家变化而OwnerSubject保留。PlayerMarker归还供应时按真实OwnerSubject复验，不强制收回到执行者供应。卡载储存区的访问权限跟随载体当前Controller，不能按历史Owner拒绝新持有者；其它玩家私有来源及其它私有目的区仍拒绝。
- 旧`1.0.0`未完成移牌节点，仅无apiVersion的discard→hand初态、supply→deck_bottom初态/awaiting可有限映射；实体供应定义映射必须唯一。旧repeat/stub洗牌或不明阶段不重解释，恢复明确拒绝。终态旧节点保留结果，不重新执行。

实际实现与验证见[移牌洗牌与供应原语执行记录](G:/NoMadCity/prompt/企业扩与感染者基础实装/执行记录/移牌洗牌与供应原语-执行记录.md)。

## D. 企业接口（006）

| Lua名称 / typeId | 固定参数 | 续接/结果 |
| --- | --- | --- |
| `Effect.UpgradeEnterprise` / `effect.enterprise.upgrade` | `apiVersion=1, player, enterpriseItemId, levels`；levels为正整数，向上以5封顶 | `oldLevel,newLevel,changed,arrivalEventIds`；逐级奖励等待完成后再下一级 |
| `Effect.ChangeEnterpriseLevel` / 新`effect.enterprise.change_level` | `apiVersion=1, player, enterpriseItemId, levelDelta, resolveArrivalReward`；levelDelta为非0整数；目标限0—5，越界按封顶/封底处理 | 与Upgrade共用同一等级提交内核；用于规则明确要求的调整，本组结构科5级传-1与true，降至4后创建4级条件奖励 |
| `Effect.ActivateEnterpriseSpecial` / `effect.enterprise.activate_special` | `apiVersion=1, player, unlockedByPlayer, minLevel=1,maxLevel=5`；可选enterpriseItemIds（省略为场上全部）、specialId（已有合法冻结选择时） | `selectedEnterpriseItemId,selectedSpecialId,changed`；无特效Completed(no_candidates) |
| `Effect.SwitchDepartment` / `effect.enterprise.switch_department` | `apiVersion=1, player, enterpriseItemId`；可选departmentItemId（未给则选择合法科室） | 全局换一次，仅player对应等级奖励；1级无奖励 |
| `Flow.UpgradeThenActivateEnterpriseSpecial` / 新`effect.enterprise.upgrade_then_activate` | `apiVersion=1, player, levels`；可选enterpriseItemIds | IntrinsicFlow：`choose_special/upgrade/activate/finished`，只开一次特效选择 |

`Flow`是新增只读**组合构造器表**，由宿主按既有Spec协议返回父流程及基础子Effect，不是可写脚本服务或不可拆分的原子业务。禁止新增Atomic DispatchOperator；老规划typeId若保留只能为IntrinsicFlow兼容入口。

ChangeEnterpriseLevel只由有效定义的规则节点调用，不能增加玩家可主动降级的界面/命令；它不意味着岩蹄可以重复降升领合约。UpgradeEnterprise是向上逐级的类型化入口，ChangeEnterpriseLevel复用同一原子等级变更与到达响应，禁止两套等级状态。结构科-1不是“回到5级重发奖励”。

006固定公开查询签名：`EnterpriseStateQuery.GetLevel(GameState,int,string)`；`EnterpriseStateQuery.GetSpecialCandidates(GameState,int executingPlayerId,int unlockedByPlayerId,int levelDelta,IReadOnlyList<string> enterpriseItemIds)`返回`IReadOnlyList<EnterpriseSpecialCandidate>`。Candidate固定`CandidateId,EnterpriseItemId,SpecialId,CurrentLevel,EffectiveLevelCeiling,DisplayNameKey`，前三个string、等级int。EffectiveLevelCeiling=min(5,current+delta)，delta=0表示当前。

已选specialId必须与同一企业绑定；升级奖励完整结束后执行所选特效，不能重弹选择改变目标。凯尔希冻结specialId/企业实例/解锁范围快照，并保存用户选择顺序和nextIndex；中途新增解锁不追加。等级奖励收据按本次到达EffectId/子项生成，不用`player+enterprise+level`永久唯一键。

2026-10-03企业内核补充：上述五接口的名称/参数不变，已接入唯一v1执行器；新增只读组合构造器`Flow.ActivateAllEnterpriseSpecials`，typeId=`effect.enterprise.activate_all_specials`。参数`apiVersion=1,player,unlockedByPlayer`，可选`enterpriseItemIds,minLevel=1,maxLevel=5`；先冻结合法特效及每家范围，有多家时直接选企业整组，再选择原集合顺序，以nextIndex串行续接。只剩一家或一个特效时省去相应多余选择；空集合Completed(no_candidates)。这是满足同时触发/冻结合同的组合入口，不增加原子企业专用状态机。普通ActivateEnterpriseSpecial和升级后组合仍直接选择特效，不先选企业。

新能力目录为`EffectRegistry.EnterpriseDefinitions`，内容函数仅返回Spec；到达/切换提交先保存真实标记/激活边，再发enterprise.level.changed/enterprise.department.changed，原DispatchReceipt等待响应子树，随后继续。收据和冻结历史字段见05。未知schema、权限、损坏状态及升级后失效目标仍明确失败。Lua归一化的明确MissingField产生内部`effect.content.incomplete`跳过节点，Condition左式缺失阻断右式、右式缺失保留已付费用并记一次日志；不会把非法类型/未知目标转换成缺式。

## E. 角色、特派与插入（007/015）

2026-10-05审阅批准的通用全员升级组合：`Flow.UpgradeEnterpriseForAllPlayers({apiVersion=1,player,enterpriseItemId,levels=1})`，typeId=`effect.enterprise.upgrade_all_players`，IntrinsicFlow定义版本`v1`。player仅为当前发动者，levels严格为1；Lua不能提交玩家列表、索引、授权来源或跨玩家降级。Host冻结从发动者起的本局全部玩家顺序，逐玩家完整等待升级、到达奖励及响应后再派生下一节点。返回`{changed,initiatingPlayerId,completedPlayerIds}`；子完成和到达事件使用接收玩家，父完成继续使用原发动者。普通企业构造器仍拒绝player与当前Lua执行玩家不同；可借用的unlockedByPlayer仅限制为真实本局玩家。状态与内部授权键见05，恢复逐项核验同一授权链，重复回答/恢复不再升级。

公路与Token入口保留原API及字段，在Lua宿主把`ctx.playerId=p1`转换为Domain的`1`后验证，原输入仍必须指向本次执行玩家。旧数字字符串上下文继续可用，不扩大地图操作权限。

| 接口 | 固定合同 |
| --- | --- |
| `Flow.DispatchOperator({apiVersion=1,player,drawCount,excludedReturnItemIds})` | 使用规划`effect.operator.dispatch`作为IntrinsicFlow；drawCount只2或3；来源共享operator_deck；先移顶N到私有暂存，接受入手→未选洗回→公开换出手/弃1，或全洗回得1分 |
| `Flow.ReplaceHandWithOperator({apiVersion=1,player})` | 新`effect.operator.replace_hand`；直接选堆内1和手牌1，整组复验后换牌；不附带普通特派步骤；无合法对子则成功无变化 |
| `Effect.SetCharacterDoubleUseRule` / `effect.character.double_use_rule` | `apiVersion=1,player,scope="current_use",useId,sourceItemId`；限当前本人使用节点；其他本轮许可通过独立scope=`action_round`及明确expiresActionRoundSequence |
| `Effect.CoverCharacterCard`（保留当前typeId） | 新参数`apiVersion=2,player,cardInstanceId,coverMode="additional",blockedActionRoundSequence`；新增槽；老参数仍旧主盖牌入口 |
| `Effect.TransferCharacterCard` / 新`effect.character.transfer` | `apiVersion=1,player,cardInstanceId,recipientPlayer`；必须合法对手；只转交并改ControllerSubject；无特派 |

普通特派父阶段固定`draw_candidates/choose_operator/return_unselected/choose_return_card/finish_return/finished`，拒绝分支为`return_all/grant_decline_score/finished`。已抽候选必须真在暂存区；空堆按普通空集合成功无变化，不凭空发牌或自动加分；只有实际建立的合法放弃分支才发1分。候选不足时展示实际张数，不复制。

`CharacterLifecycleService`方法固定：`FinishUse(EffectExecutionContext context,string useId)`、`Transfer(EffectExecutionContext context,string cardInstanceId,int recipientPlayerId)`；二者在工作副本内返回EffectStepResult。FinishUse复验当前持有者和本次使用归属；转交后不再把牌送回旧玩家弃牌。双发完整结算两个模式后按卡面生命周期执行一次离场，不能第一个模式后销毁实例。

阿米娅插入先支付持续牌→弃牌这个真实成本，成功再把当前UseId设双发；重复命令复用收据。UI不得直接调用SetCharacterDoubleUseRule，而应提交08规定的InvokeSettlementOption命令；后续候选选择仍用AnswerInteraction。费用在各模式正常支付；缺费用不变为免费双发。

015补通用掷骰：`Effect.RollDice({apiVersion=1,player,count=1,sides=6})`沿用`effect.random.roll_dice`，新增执行器文件`G:\NoMadCity\Assets\YC\Domain\Effects\DiceRollEffectExecutor.cs`。本组只开放此1D6参数组合，未知组合明确unsupported_dice_spec；结果`{diceResult=<1..6>}`由Host产生、保存并经现有completionHandlerId/EffectCompleted续接。处理器读取实际NormalizedResult，不能重新RollDice或让客户端传点数。角色内容绑定固定点数分支，执行器不认识刻俄柏卡ID。

### 角色接口实际落地补充（2026-10-03，EXP-007）

- 以上五个Lua入口已完成构造、字段校验、编译和唯一生产注册；普通特派与直接换牌为IntrinsicFlow，移除旧stub。严格拒绝客户端seed、非法版本和player替换。
- Host入口`OperatorDispatchFlow.CreateSpec(GameState,int,excludedReturnItemIds,allowedReturnZoneIds)`按冻结人数/感染者配置选择N；显式N入口仍只接受2/3。可选allowedReturnZoneIds仅供可信Host组合入口收窄/明确来源，Lua固定签名不增加该参数。默认手/弃；当前牌排除由入口传excludedReturnItemIds，不内置卡名。
- 特派结果为`accepted/changed/selectedOperatorItemId/returnItemId/reasonCode`；空堆/no_candidates成功无变化，实际放弃才player_declined并加1分。仅公开换出牌的OperatorDispatchReturnShown事件；未选和接受牌身份留owner视角。直接换牌也公开换出，且不建抽取暂存或调用洗牌/加分。
- `CharacterLifecycleService.Recycle(context,playerId,includeDiscard,coveredItemIds)`由主链显式调用，整组先预览，持续区不回收。`Cover(context,cardId,additional,blockedSequence,acceptedTypeId,reusable)`供内容限定额外槽，稳定SourceId指定可复用来源。
- 内容声明挂入`EffectRegistry.CharacterAbilityContracts[abilityId]`，由CharacterCardLuaCatalog读取skillType；015再声明结算来源的SettlementActionId/TitleKey/DescriptionKey。当前通用来源行动键为character.grant_current_double_use，不登记具体感染者卡。
- `RegisterPersistentObserver`按实际persistent实例触发，payload保存该源cardInstanceRef/useId；普通独立收尾仍走RegisterPlayerCleanupAfterResolveOnce。`BindFacility/ReleaseFacilityBinding`保存同一FacilityCharacterBindingState，任一实例离场注销绑定。
- InvokeSettlementOption沿08固定三参数进入同一EffectTreeExecutor事务，原mode请求失效、invokedOptionIds和Condition子树同提交；真实消费成功才授予双发。消费失败重新给单模式请求，不免费双发或永久挂住入口。

当前实装与验证边界见[角色执行记录](G:/NoMadCity/prompt/企业扩与感染者基础实装/执行记录/角色生命周期与特派-执行记录.md)。正式Unity定向验收待陈旧锁处理，本节不宣告感染者内容/界面完成。

## F. 地图、公路与公司（008/014/015）

保留已有Place/Move/Remove/ReplaceInfluence typeId及基本参数解析，扩展候选主体字段为`subjectFilter="self/player/opponent/any"`和`excludedSubjectKeys[]`。`opponent`包括公司；`player`只真人/已有玩家主体。`subjectKey`固定`player:<id>`或`company:stonehoove_security`。旧ownerFilter只作为适配器解析，不改已有脚本含义。

扩展**现有**`YC.Domain.Influence.InfluenceOperationResult`（04路径），增加`bool Changed`、`string OperationKind`、`List<string> AffectedItemIds`、`List<string> RemovedItemIds`、`List<string> ReplacedItemIds`、`List<string> OriginalSubjectKeys`。保留Succeeded/FailureCode/Reason及Success/Failure工厂签名，StateChanged作为Changed的只读兼容映射，不保留两份可写布尔值；PlayerId等旧单目标字段只作兼容摘要，不用于推断公司主体。实际移除与替换结果分别记录；森蚺替代移动不得加入RemovedItemIds；多萝西取本次Removed待去向真实实例，不复制Cube。该服务结果是瞬时结果，持久结果转换为现有NormalizedResult与实际暂存区，不能直接将带私有属性的服务对象当存档DTO。

OriginalSubjectKeys与AffectedItemIds等长、同索引对应；RemovedItemIds/ReplacedItemIds是AffectedItemIds的实际子集。不能在替换之后按新所属玩家反查原主体。

2026-10-03实装补充：实体路径使用`InfluenceService.Place(EffectExecutionContext,ItemSubjectRef,string)`、`Move(EffectExecutionContext,string,string)`与`Depart(EffectExecutionContext,string,string)`，提交复用ItemTransferService。成功替换发布新实例的InfluencePlaced及原实例的InfluenceReplaced，原实例不发布InfluenceRemoved；替换放置失败保留离场，结果outcome=removed_only，实际发布一次InfluenceRemoved。NormalizedResult保存`changed/operationKind/affectedItemIds/removedItemIds/replacedItemIds/originalSubjectKeys/actualEventTypes/originalPosition/destination`，保留旧单目标摘要；批量移动用紧凑moves数组及同索引主体/实例数组，完整区位事实从同节点Host PositionChanges读取，遵守128元素预算。

`InfluenceDestinationResolver.Resolve(EffectExecutionContext,string itemId,string destinationZoneId)`处理真实待去向实例，返回ItemTransferResult；`ReturnToSupply`按原Owner选择原供应或公司池。合法收留区需声明接受core.cube及entryPolicyId=core.removed_owned_influence；该政策接受非玩家Cube，但要求本次离场暂存、对应pendingItemId和祖先权限，不能按“被选中过”复制。

`InfluenceSubjectQuery.GetCandidateItemIds`组合主体、已处理原主体及合法槽位过滤；`GetLegalResourcePointSlotIds`供014的CompanyInfluenceRules复用；`CountCompanyInRegion`独立计数，不借用玩家-1；`GetCollectionLocationIds(state,playerId,scope,hasCompanyContract)`按资源点去重，normal有合约才扩公司点，extra仍只有本人城市/影响力。普通无目标Completed/no_candidates；SelectInfluence额外允许requiredCount（默认0，0至count），支付式必须明确所需真实数量，不足Failed/insufficient_items，不启动Condition右侧。

`InfluenceService.PlacementAllowed`与`TokenMovementProfileRegistry.DestinationAllowed`为组合根接入禁令的接口。ContentPrimitiveEffectExecutor.Register可传入同一地图查询和InfluenceService，使选择候选与实际提交消费相同放置限制；009/015不得只给某一个执行入口加禁令。城市单次`clearInfluencePolicy=move`按本局玩家顺序生成原实例的MoveInfluence并由执行者逐个选合法去向，默认remove保持原流程，无卡名分支。

`Effect.PlaceRoad({apiVersion=1,player,routeId})`沿用`effect.influence.place_road`；创建/放入一枚道路Token，每航道最多1、供应无限、原语不清除。保留Cube进入covered区，失去影响力资格；官方内容显式先清除后放路。已有公路返回`road_already_present`且候选阶段排除。

`Effect.OperateToken({apiVersion=1,player,tokenItemId,operation="move",destinationLocationId,operationProfileId="core.adjacent_location_token_move"})`沿用`effect.token.operate`；复用地图邻接/开放查询，不调用完整MoveCity链。锁定到期保存05整数轮序；嘉维尔token不会由角色FinishUse自动退场。

008固定只读查询`TokenMovementProfileRegistry.GetDestinationLocationIds(GameState state,string tokenItemId,string operationProfileId)`返回`IReadOnlyList<string>`。组合现有MapQueryService.GetAdjacentLocations和Map.OpenLocationIds，复验注册操作域、真实token位置及禁令；未知操作域拒绝operation_profile_rejected。实际位置由同一ItemTransferService提交，查询不移动、不懒创建区；MapLocationDefinition.RegionId与MapRouteDefinition.RegionId是地图所属区块来源。

`TokenMovementProfileRegistry.Move`只提交同一Token位置；OperateToken发布一次token.moved，结果包含原位置与去向，不走城市费用、清除、留影响力或CityMoveCompleted。`GetLocationId(state,itemId)`及InfluenceSubjectQuery.GetRegionId从当前实际位置查询，可用于015绑定城市的实时区块；`GetActionRoundSequence/IsExpiredBeforeActionRound`提供整数轮序期限比较。锁定字段、触发时点和永久Token生命周期仍由015装配。

公司候选固定经过`CompanyInfluenceRules.GetPlacementSlotIds(GameState state)`返回`IReadOnlyList<string>`，要求资源点指示物、空影响力槽、无城市、开放及额外禁止规则。`GetControlContribution(GameState state,string regionId,int playerId)`返回int：无合约0，有合约该区有效公司数/2整除。合约每玩家最多1份由真实持有区查询。

## H. 独立采集与宣告后接口（009接入，008提供地图/路费策略，007提供绑定）

原API设计已有Collect名称，但当前Lua宿主/编译/执行没有公开链。009新增`Effect.Collect({apiVersion=1,player,scope="extra"})`、typeId=`effect.collection.collect`、IntrinsicFlow执行器`G:\NoMadCity\Assets\YC\Domain\Effects\ResourceCollectionEffectExecutor.cs`。阶段固定`choose_locations/choose_payments/resolve/finished`。公开参数不接收收益、支付能力、采集金券或客户端随机结果；候选和支付选项沿现有Interaction协议。

复用`G:\NoMadCity\Assets\YC\Domain\Harvest\ResourceCollectionService.cs`的资源点收益及RouteTollService路费计算，将正常/额外范围策略显式参数化。extra仅本玩家，按本次开始时支付能力快照付玩家/银行路费；不发采集阶段金券、不消耗HasCollectedResourcesThisRound、不把GameState.Phase临时改为全员采集、不凭合约增加公司点。正常采集仍由现有collection.task交互驱动（它是InteractionTypeId，不是本API的Effect typeId）。结果固定`changed,collectedLocationIds,paidRouteIds`；选点按资源点去重，路费按实际航道去重，恢复读取同一候选/支付快照与收据，不重新采集。继续字段见05。正常/额外共用计算内核，但只有对应Effect/主链提交结果。

009在真实样式宣告完成“实例匹配记录→玩家标记转移→得分”后、响应执行前发一次既有规划事件`CityStyleDeclared`，稳定EventId由本次宣告Effect派生；payload固定`declarationId,cityStyleItemId,usedFacilityItemIds,playerId`。失败宣告不发，恢复不重发；旧GameEvent.ScoreChanged保留其显示语义，不当作宣告事件。007的FacilityCharacterBindingState提供同一设施实例绑定；016按usedFacilityItemIds判断临时聚居点离场和奖励，012/017只消费投影。事件本身不重新匹配图案，也不复制一份设施列表事实。

## G. 结果、缺式和恢复硬规则

- 所有FlowStage用业务名，初态空串解释为该执行器明确起始阶段；未知阶段Fault，不能重置到起点。ContinuationData字段写入当前工作副本并随同一次RuleCommit持久化。
- 完成无变化结果固定`{changed=false,reasonCode="no_candidates"}`或`limit_reached/empty_deck`，实际数量0。真实支付不足使用Failed且不创建Condition右侧；不得只看movedCount=0就一概Completed。
- 缺失/不完整卡面节点写一次日志：sourceId、effectId、missingFieldNames、reasonCode=`incomplete_effect_skipped`，中文可见原因脱敏。条件左式缺失跳过整个条件，右式缺失不退已经完成的左式；未知权限、损坏状态、未知schema不属于可吞掉的缺式。
- 原子执行器的输入验证前不可产生修改。候选答复后复验版本与实例状态；重复提交返回已有结果，乱序/过期返回明确原因并保留可恢复当前请求。
- 新事件只用语义ID：`item.moved`、`deck.shuffled`、`enterprise.level.changed`、`enterprise.department.changed`、`character.transferred`、`token.moved`。继承现有事件有相同语义时复用并登记映射，不双发。事件参数用ItemId/RegionId等稳定引用；隐藏细节仅Host持有。

2026-10-03事件映射定稿：影响力沿用`InfluencePlaced/InfluenceRemoved/InfluenceMoved/InfluenceReplaced`，角色沿用`CharacterEffectActivated/CharacterEffectCompleted`，样式行动沿用`CityStyleSpecialActionActivated`，生命周期沿用`RoundStarted/PlayerCleanupStarted/RoundEnded`。它们不是再发同义小写事件的理由。企业定向发动/完成使用原API规划`EnterpriseSpecialEffectActivated/EnterpriseSpecialEffectCompleted`；等级/科室变化用上列新语义，宣告用06 H的CityStyleDeclared。ItemTransferService只返回事实，具体业务执行器选择该次领域事件；不要在影响力/token/角色转交内部又发一个同义item.moved作为第二业务触发。

林的10次交易用Repeat＋Choice＋Condition(PayResource,GainResource)组合，每次只处理1个资源，卡面卖价源岩3/源石碎片4/异铁5/至纯源石15，买价为各减2；旧SellResource仅支持基础价格3/3/4/15，未公开priceTable或购买参数，不能复用它伪称卡面交易已支持。015以真实资源余额和逐次候选复验，003提供次数式，不另建BuyResource或第二资源账本。

## 共享主链实施接续（2026-10-04，EXP-009）

Effect.Collect 已接实际 effect.collection.collect IntrinsicFlow：apiVersion=1、player、scope=extra，保存 H 的四个固定续段键，复用 ResourceCollectionService 和 RouteTollService，不改全局 Phase/正常资格/阶段券。CityStyleDeclared 在声明匹配、真实标记、得分提交后发布一次，载荷严格为 declarationId/cityStyleItemId/usedFacilityItemIds/playerId。

RoundExecutionService 继续是唯一推进者；RoundStarted 先结算开放，再调用 round.prepare_cover 的企业升级与人数裁供应，之后才能盖牌。每个行动轮只在首玩家窗口发布一次 ActionRoundStarted。实体回合收尾调用 round.player.recycle_characters 和既有样式标记 Effect，不再写旧 covered/discard 标量。014/015/016 按现有时点和同一设施实例接入，不新建感染者回合驱动。节点、实例与版本表见[接入合同](G:/NoMadCity/prompt/企业扩与感染者基础实装/执行记录/内容装配与主链接入合同.md)。

## 感染者公司与授勋请求接续（2026-10-05，EXP-014）

- `Flow.AcquireReward({apiVersion=1,player=ctx.playerId,poolRole="awards"})` → `effect.reward.acquire` v1。候选来自公共实际剩余授勋实例；`reward.choose`，本人可见，单选 candidate_id，按原 interaction/revision 提交。contracts 角色自动取剩余真实合约；本人已有合约或池空时成功无变化。
- 完成一次 `reward.acquired` 公开事件，RouteKey=DefinitionId；载荷 `{changed:true,rewardItemId,definitionId,poolRole,playerId}`。016按定义路由订阅真实实例：一次性授勋执行后用 `Effect.MoveCard` 与 `core.reward_card_transfer` 回盒，持续授勋以该实例保留/注册。沿现有 DispatchReceipt/子树恢复，不把每次投影或读档当成重新领取。
- `CompanyInfluenceRules.GetPlacementSlotIds(GameState)` 与 `GetControlContribution(GameState,string regionId,int playerId)` 已实现。公司放置/移动提交沿 `InfluenceService`，候选同样接 `PlacementAllowed(GameState,string slotId)`。015在组合根注入动态锁定规则到已注册服务；`EffectRegistry.InfluenceService` 是运行时服务引用，不序列化、不另存锁状态。移出锁区/移除保持008合同，替换落点与放入锁区须拒绝。
- `subjectFilter=opponent` 包括公司、`player` 排除公司；排除键仍为 `company:stonehoove_security` 或 `player:<id>`。岩蹄2级的移除支付设置 `requiredCount=1,requireCompleteSelection=true`，要求真实完整移除；1级独立移除没有目标时不阻断后项。
- 普通 `Flow.DispatchOperator` 在感染者下统一抽3，即使继承脚本显式传2。`Flow.ReplaceHandWithOperator` 仍为独立直接换手牌流程。Lua宿主把上下文 p1 规范化为合同数字字符串并验证执行玩家，Domain再次验证，不在卡脚本硬编码转换。
- 015/016的设施绑定继续使用 `FacilityCharacterBindings`、`CharacterLifecycleService` 既有入口；017消费 `SettlementOptionView` 与现有 InvokeSettlementOption，不能建立第二套按钮执行协议。具体字段见05/08，本轮未新增固定UI文字或资产。


## 2026-10-05 设施样式授勋接续（EXP-016）

按[接口核验记录](G:/NoMadCity/prompt/企业扩与感染者基础实装/执行记录/接口核验记录.md)本日增量：新增通用ContentLifecycleEffectExecutor/ContentLifecycleLuaCatalog，RetireFacility/ReturnCityStyleMarker apiVersion=1，ChooseBuild.excludeRainbow，冻结GameContentDefinitions.Awards；快速授勋沿award.activate_special与既有InvokeSettlementOption。实际定义与待登记列表见[感染者设施样式授勋登记清单](G:/NoMadCity/prompt/企业扩与感染者基础实装/执行记录/感染者设施样式授勋登记清单.md)。不得复制奖励/资格或创建第二回合状态。

- 2026-10-05 感染者设施样式授勋实测接续：办事处采用 `Flow.UpgradeDistinctEnterprises(apiVersion=1,player,count=2,levels=1)` 连续选择不同企业，沿原升级与receipt合同；详见[接口核验](G:/NoMadCity/prompt/企业扩与感染者基础实装/执行记录/接口核验记录.md)。

## 2026-10-08 企业升级范围接口追加

审阅批准`Effect.UpgradeEnterprise({apiVersion=1,player,levels,enterpriseItemIds})`：`enterpriseItemIds`是本局真实企业ItemId数组，与旧`enterpriseItemId`必须且只能提供一种；空数组为明确空范围，两字段都缺仍沿缺式跳过，两字段同时给或范围重复/非法明确失败。旧确定目标、全员授权升级子节点、ChangeEnterpriseLevel及特效组合参数不变。Lua仅限制范围，Host按真实活动企业/定义/轨道建立候选并经CandidateSetResolver登记；选择后同一节点继续原逐级升级及奖励等待。

多候选发布owner可见的`enterprise.upgrade / enterprise.upgrade.choose`、candidate_id、必选1且不可拒绝，走正式Company企业选择页；单候选直接执行，空候选Completed(changed=false,reasonCode=no_candidates)。满级仍合法，5→5成功无变化且不发到达奖励。范围结果在旧等级/changed/arrivalEventIds之外带selectedEnterpriseItemId；空范围没有实际目标或等级结果。新Distinct请求同样用enterprise.upgrade但保留enterprise.choose_distinct提示键；旧lua.choice别名仅对该明确提示键由企业页接管，普通Choice不变。Continuation合同见05，详细方案与验证边界见[企业升级接入方案](G:/NoMadCity/prompt/企业扩与感染者基础实装/执行记录/企业升级候选范围与正式页面接入方案.md)。
