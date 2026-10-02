# Effect接口与提交合同

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

`apiVersion`为新增参数schema版本，不冒充EffectRegistration.DefinitionVersion；后者本组新执行器用`v1`，变更已有定义版本时须提供恢复映射。旧移牌输入没有apiVersion时继续旧语义；新参数必须apiVersion=2。不允许一个注册表对同一typeId重复Register。

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

MoveItem空数组且requiredCount=0→Completed，changed=false；requiredCount>实际数量→Failed(`insufficient_items`)且不移动；重复itemId、未知实例、源错、禁入分别返回`duplicate_item_id/unknown_item/source_zone_changed/destination_rejected`。源变化由父交互按当前状态刷新候选；不能把真正非法引用当缺式吞掉。

Shuffle只洗当前deckZone成员；空或单牌成功无变化；多牌由Host RNG生成最终排列，在同一规则提交保存OrderRevision和结果。恢复已完成节点不得再掷随机数。设施原语不得顺手回收别区卡，补牌不得顺手洗牌。

Repeat不是预先展开所有依赖当前候选的交互。每轮上一个子树完成后才实例化下一轮effects，记录实际completedCount；allowEarlyStop的停止答案只结束剩余次数，不撤销已完成轮。单步缺候选按该子式结果处理，不能无限重试。

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

## E. 角色、特派与插入（007/015）

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

## F. 地图、公路与公司（008/014/015）

保留已有Place/Move/Remove/ReplaceInfluence typeId及基本参数解析，扩展候选主体字段为`subjectFilter="self/player/opponent/any"`和`excludedSubjectKeys[]`。`opponent`包括公司；`player`只真人/已有玩家主体。`subjectKey`固定`player:<id>`或`company:stonehoove_security`。旧ownerFilter只作为适配器解析，不改已有脚本含义。

新增`InfluenceOperationResult`固定`bool Changed`、`string OperationKind`、`List<string> AffectedItemIds`、`List<string> RemovedItemIds`、`List<string> ReplacedItemIds`、`List<string> OriginalSubjectKeys`。实际移除与替换结果分别记录；森蚺替代移动不得加入RemovedItemIds；多萝西取本次Removed待去向真实实例，不复制Cube。

OriginalSubjectKeys与AffectedItemIds等长、同索引对应；RemovedItemIds/ReplacedItemIds是AffectedItemIds的实际子集。不能在替换之后按新所属玩家反查原主体。

`Effect.PlaceRoad({apiVersion=1,player,routeId})`沿用`effect.influence.place_road`；创建/放入一枚道路Token，每航道最多1、供应无限、原语不清除。保留Cube进入covered区，失去影响力资格；官方内容显式先清除后放路。已有公路返回`road_already_present`且候选阶段排除。

`Effect.OperateToken({apiVersion=1,player,tokenItemId,operation="move",destinationLocationId,operationProfileId="core.adjacent_location_token_move"})`沿用`effect.token.operate`；复用地图邻接/开放查询，不调用完整MoveCity链。锁定到期保存05整数轮序；嘉维尔token不会由角色FinishUse自动退场。

公司候选固定经过`CompanyInfluenceRules.GetPlacementSlotIds(GameState state)`返回`IReadOnlyList<string>`，要求资源点指示物、空影响力槽、无城市、开放及额外禁止规则。`GetControlContribution(GameState state,string regionId,int playerId)`返回int：无合约0，有合约该区有效公司数/2整除。合约每玩家最多1份由真实持有区查询。

## G. 结果、缺式和恢复硬规则

- 所有FlowStage用业务名，初态空串解释为该执行器明确起始阶段；未知阶段Fault，不能重置到起点。ContinuationData字段写入当前工作副本并随同一次RuleCommit持久化。
- 完成无变化结果固定`{changed=false,reasonCode="no_candidates"}`或`limit_reached/empty_deck`，实际数量0。真实支付不足使用Failed且不创建Condition右侧；不得只看movedCount=0就一概Completed。
- 缺失/不完整卡面节点写一次日志：sourceId、effectId、missingFieldNames、reasonCode=`incomplete_effect_skipped`，中文可见原因脱敏。条件左式缺失跳过整个条件，右式缺失不退已经完成的左式；未知权限、损坏状态、未知schema不属于可吞掉的缺式。
- 原子执行器的输入验证前不可产生修改。候选答复后复验版本与实例状态；重复提交返回已有结果，乱序/过期返回明确原因并保留可恢复当前请求。
- 新事件只用语义ID：`item.moved`、`deck.shuffled`、`enterprise.level.changed`、`enterprise.department.changed`、`character.transferred`、`token.moved`。继承现有事件有相同语义时复用并登记映射，不双发。事件参数用ItemId/RegionId等稳定引用；隐藏细节仅Host持有。
