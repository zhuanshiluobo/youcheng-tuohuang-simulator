using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Influence;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class MartinContentRecoveryTests : MartinContentTestFixture
    {
        [TestCase(false)][TestCase(true)] public void ActualM05ExchangeColdRecoveryKeepsOneAtomicBatchAndAllOriginalIds(bool committed)
        {
            var state=State("entrepreneur.round.M.1",true);ActivateFace(state,"entrepreneur.round.M.1","M05");
            var blueprint=Blueprint(state,"entrepreneur.facility.M02");var supply=Supply(state,"building_015",0);
            string random=JsonConvert.SerializeObject(state.RandomRuntime);Action(state,"M05");Answer(state,NormalizedValue.CreateString("place_twice"));
            UntilPrompt(state,"core:entrepreneur.M.blueprint.choose_supply");Answer(state,NormalizedValue.CreateString(supply.ItemId));
            state=Recover(state,"M05供应已选图纸待答");var request=Open(state);var engine=new EffectTreeExecutor(state,Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString(blueprint.ItemId),out string reason),Is.True,reason);
            UntilNode(engine,()=>state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==ItemTransferEffectExecutor.TypeId&&Text(n.NormalizedArguments,"operationId").StartsWith("martin:exchange:")&&(committed?n.Status==EffectNodeStatus.Completed:n.Status==EffectNodeStatus.Created||n.Status==EffectNodeStatus.Ready)));
            state=Recover(state,committed?"M05双腿已原子提交":"M05双腿构造未提交");Assert.That(new EffectTreeExecutor(state,Registry).RunUntilQuiescent().Faulted,Is.False);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==supply.ItemId).ZoneId,Is.EqualTo(BlueprintZone(state)));
            Assert.That(FacilityLayerQuery.Supply(state).Single(s=>s.SlotIndex==0).TopItemId,Is.EqualTo(blueprint.ItemId));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.TransferBatchRequest?.OperationId.StartsWith("martin:exchange:")==true),Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime),Is.EqualTo(random));
        }
        [Test] public void CorruptSecondLegOfActualEmittedBatchCannotCommitFirstLeg()
        {
            var state=State("entrepreneur.round.M.1",true);ActivateFace(state,"entrepreneur.round.M.1","M05");var blueprint=Blueprint(state,"entrepreneur.facility.M02");var supply=Supply(state,"building_015",0);
            Action(state,"M05");Answer(state,NormalizedValue.CreateString("place_twice"));UntilPrompt(state,"core:entrepreneur.M.blueprint.choose_supply");Answer(state,NormalizedValue.CreateString(supply.ItemId));
            var request=Open(state);var engine=new EffectTreeExecutor(state,Registry);Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString(blueprint.ItemId),out string reason),Is.True,reason);
            UntilNode(engine,()=>state.EffectRuntime.EffectNodes.Any(n=>Text(n.NormalizedArguments,"operationId").StartsWith("martin:exchange:")));
            var batch=state.EffectRuntime.EffectNodes.Single(n=>Text(n.NormalizedArguments,"operationId").StartsWith("martin:exchange:"));
            // 隔离畸形输入：只破坏真实Lua已生成请求的第二腿，不以此代替正式成功路径。
            var second=Value(batch.NormalizedArguments,"transfers").Items[1];Value(second,"destination").Properties.Single(p=>p.Name=="zoneId").Value=NormalizedValue.CreateString("missing.zone");
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.True);Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==blueprint.ItemId).ZoneId,Is.EqualTo(BlueprintZone(state)));
            Assert.That(FacilityLayerQuery.Supply(state).Single(s=>s.SlotIndex==0).TopItemId,Is.EqualTo(supply.ItemId));
            Artifacts.WriteLabeledText("第二腿无效首次现场",JsonConvert.SerializeObject(state));
        }
        [Test] public void StaleBlueprintAnswerRefreshesRealSupplyChoiceAndDoesNotSubstituteAnotherCard()
        {
            var state=State("entrepreneur.round.M.1",true);ActivateFace(state,"entrepreneur.round.M.1","M05");var a=Blueprint(state,"entrepreneur.facility.M02");Blueprint(state,"entrepreneur.facility.M02");var supply=Supply(state,"building_015",0);
            Action(state,"M05");Answer(state,NormalizedValue.CreateString("place_twice"));UntilPrompt(state,"core:entrepreneur.M.blueprint.choose_supply");Answer(state,NormalizedValue.CreateString(supply.ItemId));var old=Open(state);
            Position(state,state.ItemZones.Items.Single(i=>i.ItemId==a.ItemId),FindZone(state,"retired").ZoneId);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==a.ItemId).ZoneId,Is.EqualTo(FindZone(state,"retired").ZoneId));
            var engine=new EffectTreeExecutor(state,Registry);
            Assert.That(engine.TrySubmitInteraction(old.GetStableInteractionId(),1,old.StateRevision,NormalizedValue.CreateString(a.ItemId),out string stale),Is.False);
            Assert.That(stale,Does.Contain("过期"));
            // 普通旧版本答案必须拒绝。单独只读调用正式Lua续接与生产快照构造器，核验业务重查返回新供应选择。
            // 这段不创建运行时请求、不提交效果、不伪造已答收据，不能称作正常界面刷新已验。
            var owner=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==old.OwnerEffectId);
            string unchanged=JsonConvert.SerializeObject(state);
            var r=ReadOnlyFormalContinuation(state,owner,Rounds,Object("continuationContext",owner.ContinuationContext.Clone(),
                "normalizedResult",NormalizedValue.CreateString(a.ItemId),"outcome",NormalizedValue.CreateString("completed"),
                "effectTypeId",NormalizedValue.CreateString(owner.EffectTypeId)));
            Assert.That(r.IsSuccess,Is.True,r.Diagnostic);var next=r.Effects.Single();
            Assert.That(next.EffectTypeId,Is.EqualTo("effect.flow.choice"));
            Assert.That(Text(next.RawArguments,"promptKey"),Is.EqualTo("core:entrepreneur.M.blueprint.choose_supply"));
            Assert.That(Text(next.ContinuationContext,"stage"),Is.EqualTo("M.pair.supply"));
            Assert.That(JsonConvert.SerializeObject(state),Is.EqualTo(unchanged));
            Assert.That(FacilityLayerQuery.Supply(state).Single(s=>s.SlotIndex==0).TopItemId,Is.EqualTo(supply.ItemId));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.TransferBatchRequest?.OperationId.StartsWith("martin:exchange:")==true),Is.False);
            Artifacts.WriteLabeledText("旧版本拒绝及正式Lua只读失效重查",JsonConvert.SerializeObject(new { stale,r.Status,r.Diagnostic,next.EffectTypeId,next.RawArguments,next.ContinuationContext,scope="Lua返回新选择的只读断言；未创建/刷新运行时请求，正常覆盖0" }));
            Recover(state,"图纸已真实移出旧答案拒绝后");
        }
        private LuaInvocationResult ReadOnlyFormalContinuation(GameState state,EffectNodeRuntimeState owner,string path,NormalizedValue payload)
        {
            string source=Pack.GetScript(path);
            Assert.That(LuaContentHasher.ComputeSha256(source),Is.EqualTo(owner.ContinuationContentHash));
            var script=new LuaScriptDefinition(owner.ContinuationContentId,owner.ContinuationAbilityId,owner.ContinuationHandlerId,
                owner.ContinuationDefinitionVersion,source,owner.ContinuationContentHash);
            var definition=new LuaHandlerDefinition("qy007.readonly.continuation","EffectCompleted","*",script,
                EffectHandlerRole.Continuation,contentInstanceId:owner.ContinuationContentInstanceId,abilityId:owner.ContinuationAbilityId);
            var factory=typeof(LuaEffectEventDispatcher).GetMethod("CreateInvocationContext",BindingFlags.Instance|BindingFlags.NonPublic);
            Assert.That(factory,Is.Not.Null);
            var ev=new RuleEvent { EventId="qy007.readonly.continuation",EventType="EffectCompleted",PlayerId=1,Payload=payload,HostOnlyPayload=payload.Clone() };
            var context=(LuaInvocationContext)factory.Invoke(new LuaEffectEventDispatcher(Registry),new object[] { definition,new EffectEventHandlerContext(state,ev,owner,null) });
            return new MoonSharpLuaRuntimeHost().Invoke(script,context);
        }
        [Test] public void OwnedTowerStagingAndPlacementRecoveryNeverTakesAnotherTowerOrMainAction()
        {
            var state=State("entrepreneur.round.M.3",true,new[]{new ItemRecipeEntry {EntryId="owned-tower-recovery",DefinitionId="entrepreneur.facility.additional_core_tower",TypeId="core.facility_card",Quantity=1,PerPlayer=true,InitialZoneRole="supply"}});
            SelectFace(state,"entrepreneur.round.M.3","M07");int budget=state.FindPlayer(1).RemainingMainActionsThisTurn;Immediate(state,"M07");
            var build=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Build);string item=Text(build.NormalizedArguments,"facilityId");
            state=Recover(state,"追加塔已取入待建区");Answer(state,NormalizedValue.CreateString("0"));state=Recover(state,"追加塔完成后");
            Assert.That(Cell(state,0).TopItemId,Is.EqualTo(item));Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn,Is.EqualTo(budget));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Build),Is.EqualTo(1));
        }
        [Test] public void OpponentChoiceAndCompletedScopeRecoverAfterOriginalSourceExits()
        {
            var state=State(carrier:true);Position(state,Card(state,"reserve_001"),Cell(state,0).CellZoneId);Position(state,Card(state,"reserve_001"),Cell(state,0,2).CellZoneId,player:2);
            var build=Run(state,BuildScenario(state,Card(state,"entrepreneur.facility.M02"),1));string request=Open(state).GetStableInteractionId();int gold=state.FindPlayer(2).Resources.GoldVoucher;
            state=Recover(state,"本人入场完成对手待答");Assert.That(Open(state).GetStableInteractionId(),Is.EqualTo(request));Answer(state,NormalizedValue.CreateString("p2"));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher,Is.EqualTo(gold+12));
            var carrier=state.ItemZones.Items.Single(i=>i.DefinitionId=="entrepreneur.ability.M13"&&i.ControllerSubject.PlayerId==1);
            var retired=Run(state,IsolatedEntrepreneurTransfer.ToRetired(state,Registry,1,carrier.ItemId));
            Assert.That(retired.Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==carrier.ItemId).ZoneId,Is.EqualTo(FindZone(state,"retired").ZoneId));
            state=Recover(state,"完成对手scope后原M13退出");Assert.That(new EffectTreeExecutor(state,Registry).RunUntilQuiescent().Faulted,Is.False);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher,Is.EqualTo(gold+12));Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==build.EffectId).PlayerId,Is.EqualTo(1));
        }
        [Test] public void LosingLastRemovalTargetFaultsAndPreservesScoredBuildBeforeEntry()
        {
            var state=State(carrier:true);OwnInfluence(state);Position(state,Card(state,"entrepreneur.facility.additional_core_tower"),Cell(state,0).CellZoneId);
            var build=Run(state,BuildScenario(state,Card(state,"entrepreneur.facility.M02"),0));var facts=FacilityBuildJournal.Read(build);
            string target=InfluenceIdentity.GetStableId(state,state.Map.Influences.Single(i=>i.PlayerId==1));var engine=new EffectTreeExecutor(state,Registry);
            var request=Open(state);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString(target),out string reason),Is.True,reason);
            // 真实必做选择已受理，另一真实移除先提交，使原足额义务在执行时失去目标。
            string competing=engine.CreateRoot(InfluenceEffectSpecFactory.RemoveInfluence(1,target));Assert.That(engine.RunUntilQuiescent().Faulted,Is.True);
            Assert.That(engine.GetNode(competing).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(FacilityBuildJournal.Read(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==build.EffectId)).ScoreCommitted,Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.EntryEffectId).FlowStage,Is.Empty);
            state=Recover(state,"必做目标丢失首次故障");Assert.That(state.EffectRuntime.Status,Is.EqualTo(EffectRuntimeStatus.PausedFault));
        }
        [Test] public void EffectIdCannotReplaceTheRealAnsweredChoice()
        {
            var state=State(carrier:true);Position(state,Card(state,"reserve_001"),Cell(state,0,2).CellZoneId,player:2);
            var build=Run(state,BuildScenario(state,Card(state,"entrepreneur.facility.M02"),0));var choice=Open(state);string interaction=choice.GetStableInteractionId(),effect=choice.OwnerEffectId;
            Answer(state,NormalizedValue.CreateString("p2"));var receipt=state.EffectRuntime.AbilitySourceUses.Single(r=>r.AbilityId=="entrepreneur.M13.urbanization_after"&&r.PlayerId==1);
            Assert.DoesNotThrow(()=>EffectExecutionBinding.Create(state,Registry,build.EffectId,receipt.SourceRef,interaction,2,receipt.VersionRef));
            var wrong=Assert.Throws<InvalidOperationException>(()=>EffectExecutionBinding.Create(state,Registry,build.EffectId,receipt.SourceRef,effect,2,receipt.VersionRef));
            Assert.That(wrong.Message,Is.EqualTo("effect_execution_choice_invalid"));
            state=Recover(state,"真实Choice身份与effectId伪造拒绝后");
        }
        [Test] public void ExitedM13BeforeOpponentAnswerCannotUseCompletedChoiceAsFreshAuthorization()
        {
            var state=State(carrier:true);Position(state,Card(state,"reserve_001"),Cell(state,0,2).CellZoneId,player:2);
            var build=Run(state,BuildScenario(state,Card(state,"entrepreneur.facility.M02"),0));var choice=Open(state);int gold=state.FindPlayer(2).Resources.GoldVoucher;
            var use=state.EffectRuntime.AbilitySourceUses.Single(u=>u.AbilityId=="entrepreneur.M13.urbanization_after"&&u.PlayerId==1);
            var carrier=state.ItemZones.Items.Single(i=>i.DefinitionId=="entrepreneur.ability.M13"&&i.ControllerSubject.PlayerId==1);
            var transfer=Run(state,IsolatedEntrepreneurTransfer.ToRetired(state,Registry,1,carrier.ItemId));
            Assert.That(transfer.Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==carrier.ItemId).ZoneId,Is.EqualTo(FindZone(state,"retired").ZoneId));
            var engine=new EffectTreeExecutor(state,Registry);
            Assert.That(engine.TrySubmitInteraction(choice.GetStableInteractionId(),1,choice.StateRevision,NormalizedValue.CreateString("p2"),out string reason),Is.False);
            Assert.That(reason,Does.Contain("过期"));
            var denied=Assert.Throws<InvalidOperationException>(()=>EffectExecutionBinding.Create(state,Registry,build.EffectId,use.SourceRef,choice.GetStableInteractionId(),2,use.VersionRef));
            Assert.That(denied.Message,Is.EqualTo("effect_execution_source_invalid"));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.ExecutionBinding!=null),Is.False);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher,Is.EqualTo(gold));
            Assert.That(state.EffectRuntime.Status,Is.Not.EqualTo(EffectRuntimeStatus.PausedFault));
            Artifacts.WriteLabeledText("待答来源退出拒绝旧答案和新scope",JsonConvert.SerializeObject(new { reason,denied.Message,State=state }));
            state=Recover(state,"待答来源退出旧答案拒绝且无新scope");
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher,Is.EqualTo(gold));
        }
        [TestCase(false)] [TestCase(true)]
        public void M10GrantColdRecoveryBeforeOrAfterCommitDoesNotDuplicateOrChargeAgain(bool committed)
        {
            var state=State("entrepreneur.round.M.5"); SelectFace(state,"entrepreneur.round.M.5","M10");
            string random=JsonConvert.SerializeObject(state.RandomRuntime); int initial=state.FindPlayer(1).Resources.GoldVoucher;
            var engine=BeginImmediate(state,"M10"); bool reached=false;
            for(int i=0;i<500 && !reached;i++)
            {
                Assert.That(engine.Advance(),Is.True);
                reached=state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==RewardGrantEffectExecutor.TypeId && (committed?n.RewardGrant!=null:n.RewardGrant==null));
            }
            Assert.That(reached,Is.True); var issued=state.ItemZones.Items.Where(i=>i.DefinitionId=="entrepreneur.award.M01").Select(i=>i.ItemId).ToList();
            state=Recover(state,committed?"M10已发待续接":"M10发放前"); engine=new EffectTreeExecutor(state,Registry);
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(state.ItemZones.Items.Count(i=>i.DefinitionId=="entrepreneur.award.M01"),Is.EqualTo(2));
            Assert.That(issued.All(id=>state.ItemZones.Items.Any(i=>i.ItemId==id)),Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(initial+10));
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime),Is.EqualTo(random));
            var recoveredAgain=Recover(state,"M10完整结清再次恢复");
            Assert.That(new EffectTreeExecutor(recoveredAgain,Registry).RunUntilQuiescent().Faulted,Is.False);
            Assert.That(recoveredAgain.ItemZones.Items.Count(i=>i.DefinitionId=="entrepreneur.award.M01"),Is.EqualTo(2));
        }
        [Test] public void M11PanelRecoveryPreservesRealSelectedSourceAndOriginalTopIdentity()
        {
            var state=State("entrepreneur.round.M.7"); SelectFace(state,"entrepreneur.round.M.7","M11");
            InitializeTrack(state,"entrepreneur.round.M.7","usage.entrepreneur.M.7");
            Position(state,Card(state,"building_015"),Cell(state,0).CellZoneId); Action(state,"M11");
            Assert.That(Open(state).InteractionTypeId,Is.EqualTo("action.decline_effect"));Answer(state,NormalizedValue.CreateString("continue"));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(q=>q.InteractionTypeId=="action.decline_effect"),Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==Open(state).OwnerEffectId).EffectTypeId,Is.EqualTo(FacilitySelectionEffectExecutor.PanelTypeId));
            string request=Open(state).GetStableInteractionId(),top=Cell(state,0).TopItemId;
            state=Recover(state,"M11面板待本人回答");
            Assert.That(Open(state).GetStableInteractionId(),Is.EqualTo(request)); Assert.That(Cell(state,0).TopItemId,Is.EqualTo(top));
            Answer(state,NormalizedValue.CreateString("0"));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==ContentLifecycleEffectExecutor.RetireFacilityTypeId && n.Status==EffectNodeStatus.Completed),Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==top).ZoneId,Is.EqualTo(FindZone(state,"retired").ZoneId));
        }
        [Test] public void M11OldPanelAnswerCannotRetireTheNewTopAfterRealRetirementChangesCandidates()
        {
            var state=State("entrepreneur.round.M.7"); SelectFace(state,"entrepreneur.round.M.7","M11");
            InitializeTrack(state,"entrepreneur.round.M.7","usage.entrepreneur.M.7");
            Position(state,Card(state,"building_015"),Cell(state,0).CellZoneId); string lower=Cell(state,0).TopItemId;
            Overlay(state,Card(state,"building_016"),0);
            Action(state,"M11");Assert.That(Open(state).InteractionTypeId,Is.EqualTo("action.decline_effect"));Answer(state,NormalizedValue.CreateString("continue"));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(q=>q.InteractionTypeId=="action.decline_effect"),Is.EqualTo(1));
            var old=Open(state); var cell=Cell(state,0);
            // 隔离定位：另一条真实Retire操作改变面板候选；不用替换面板答案或实例ID。
            Run(state,EffectSpec.Create(ContentLifecycleEffectExecutor.RetireFacilityTypeId,
                Object("apiVersion",NormalizedValue.CreateInteger(2),"player",NormalizedValue.CreateString("1"),
                    "facilityItemId",NormalizedValue.CreateString(cell.TopItemId),"expectedCellZoneId",NormalizedValue.CreateString(cell.CellZoneId),
                    "expectedOrderDigest",NormalizedValue.CreateString(cell.OrderDigest)),1));
            Assert.That(Cell(state,0).TopItemId,Is.EqualTo(lower));
            var engine=new EffectTreeExecutor(state,Registry);
            Assert.That(engine.TrySubmitInteraction(old.GetStableInteractionId(),old.AnsweringPlayerId,old.StateRevision,
                NormalizedValue.CreateString("0"),out string why),Is.False,why);
            Assert.That(Cell(state,0).TopItemId,Is.EqualTo(lower)); Assert.That(Open(state).GetStableInteractionId(),Is.Not.EqualTo(old.GetStableInteractionId()));
            Recover(state,"M11候选变化后新请求待确认");
        }
        [Test] public void M12InsufficientPureCannotCreateBuildOrPlacement()
        {
            var state=State("entrepreneur.round.M.7"); SelectFace(state,"entrepreneur.round.M.7","M12");
            InitializeTrack(state,"entrepreneur.round.M.7","usage.entrepreneur.M.7");
            state.FindPlayer(1).Resources.PureOriginium=0; // 隔离规则定位的初始资源条件，不计正常流程。
            Action(state,"M12");
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==FacilitySelectionEffectExecutor.TypeId || n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence),Is.False);
            state=Recover(state,"M12左式不足后"); Assert.That(new EffectTreeExecutor(state,Registry).RunUntilQuiescent().Faulted,Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==FacilitySelectionEffectExecutor.TypeId || n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence),Is.False);
        }
    }
}
