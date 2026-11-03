using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {

        [TestCase(0)][TestCase(1)]
        public void SharedRefillUsesOnePublicTopStepAndRestoresWithoutDrawingTwice(int count)
        {
            var state=State(count);var reg=Registry();string slot=Zone(state,"facility_supply_0").ZoneId;var node=Run(state,RefillSpec(1,slot),reg);
            Assert.That(node.PositionChanges,Is.Empty);Assert.That(Number(node.NormalizedResult,"movedCount"),Is.EqualTo(count));
            var child=state.EffectRuntime.EffectNodes.Single(n=>n.ParentEffectId==node.EffectId);Assert.That(child.EffectTypeId,Is.EqualTo(TypeId));Assert.That(Text(Value(child.NormalizedArguments,"selector"),"mode"),Is.EqualTo("top"));Assert.That(Flag(Value(child.NormalizedArguments,"destination"),"mustBeEmpty"),Is.True);
            string before=YC.Domain.Items.ItemTransferService.MovementV4Digest(state.ItemZones);state=Restore(state,reg);new EffectTreeExecutor(state,reg).RunUntilQuiescent();Assert.That(YC.Domain.Items.ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(before));
        }
        [Test]
        public void SharedCycleKeepsRevealedUnderlyingCardAndNeverRefillsOccupiedOriginalSlot()
        {
            var state=State();Fill(state,1);string slot=Zone(state,"facility_supply_0").ZoneId;string top=Ids(state,"facility_deck")[0],under=Ids(state,"facility_supply_0")[0];
            Run(state,MoveSpec(1,new[]{top},Zone(state,"facility_deck").ZoneId,slot,"top",1));var reg=Registry();var parent=Run(state,ExplicitCycleV4(state,top),reg);
            Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);state=Restore(state,reg);
            Assert.That(Ids(state,"facility_supply_0"),Is.EqualTo(new[]{under}));var done=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==parent.EffectId);Assert.That(done.ChildEffectIds.Count,Is.EqualTo(1));Assert.That(Flag(done.NormalizedResult,"refilled"),Is.False);Restore(state,reg);
        }
        [Test]
        public void ExplicitCycleDoesNotInventTargetForEmptySupply()
        {
            var state=State();var args=Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("cycle_supply"));
            var node=Run(state,EffectSpec.Create(LuaDomainEffectTypeIds.MoveFacilityCard,args,1));
            Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.FailureReason,Is.EqualTo("movement_explicit_target_required"));Assert.That(node.ChildEffectIds,Is.Empty);Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
        }
        [Test]
        public void PublicExpectedEmptyDestinationRejectsOccupancyIntroducedAfterBinding()
        {
            var state=State();var reg=Registry();reg.Register("fixture.concurrent.facility",c=>EffectStepResult.Completed());var external=Run(state,EffectSpec.Create("fixture.concurrent.facility",playerId:1),reg);
            string slot=Zone(state,"facility_supply_0").ZoneId;var spec=CommonV4(TopV4(state),slot,"top");Value(spec.NormalizedArguments,"destination").Properties.Add(new NormalizedValueEntry{Name="mustBeEmpty",Value=NormalizedValue.CreateBoolean(true)});Value(spec.NormalizedArguments,"destination").Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));
            var engine=new EffectTreeExecutor(state,reg);string id=engine.CreateRoot(spec);for(int i=0;i<20&&engine.GetNode(id).FlowStage!="v4_bound";i++)Assert.That(engine.Advance(),Is.True);
            string occupant=Ids(state,"facility_deck").Last();
            artifacts.WriteLabeledText("隔离并发占槽准备","仅为规则定位，在共同意向bound后用真实C#转移原语交错一次外部合法占槽；普通Run会先续跑原待执行根，不能模拟该边界。不计正常对局或UI验收。");
            var ctor=typeof(EffectExecutionContext).GetConstructor(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,new[]{typeof(GameState),typeof(EffectNodeRuntimeState),typeof(EffectRegistry),typeof(EffectRuntimeLimits)},null);Assert.That(ctor,Is.Not.Null);
            var isolated=(EffectExecutionContext)ctor.Invoke(new object[]{state,external,reg,null});
            new YC.Domain.Items.ItemTransferService().RelocateItems(isolated,new YC.Domain.Items.ItemTransferRequest{ExecutingPlayerId=1,ItemIds=new System.Collections.Generic.List<string>{occupant},ExpectedSource=new YC.Domain.Items.ItemPositionRef{ZoneId=Zone(state,"facility_deck").ZoneId},Destination=new YC.Domain.Items.ZoneRef{ZoneId=slot},InsertionKind="top",OperationProfileId="core.facility_card_transfer",RequiredCount=1});
            string[] deck=Ids(state,"facility_deck").ToArray();engine.RunUntilQuiescent();Assert.That(engine.GetNode(id).Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(new[]{"destination_rejected","source_order_changed"},Does.Contain(engine.GetNode(id).FailureReason));Assert.That(Ids(state,"facility_deck"),Is.EqualTo(deck));Assert.That(Ids(state,"facility_supply_0"),Is.EqualTo(new[]{occupant}));
        }
        [Test]
        public void ExternalSupplyChoicePreservesDeclaredPromptAcrossColdRecovery()
        {
            var state=State();Fill(state,1);var reg=Registry();GenericLuaEffectExecutor.Register(reg);string selected=Ids(state,"facility_supply_0")[0];
            var spec=BranchChoice(selected,ExplicitCycleV4(state,selected));const string prompt="core:entrepreneur.M.blueprint.to_bottom";
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry{Name="promptKey",Value=NormalizedValue.CreateString(prompt)});spec.NormalizedArguments.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));
            spec.Presentation=new YC.Domain.Presentation.EffectPresentationDefinition{Interaction=new YC.Domain.Presentation.GameplayInteractionPresentation{Prompt=new YC.Domain.Presentation.GameplayTextRef(prompt)}};
            var parent=Run(state,spec,reg);Assert.That(Open(state).OwnerEffectId,Is.EqualTo(parent.EffectId));Assert.That(parent.ChildEffectIds,Is.Empty);Assert.That(Open(state).InteractionTypeId,Is.EqualTo("facility.supply.select"));
            state=Restore(state,reg);Assert.That(Open(state).PromptKey,Is.EqualTo(prompt));AnswerRequest(state,reg,selected);Restore(state,reg);
        }
        [TestCase(false)][TestCase(true)]
        public void MoveItemExpectedEmptyDoesNotEnableInteractiveSelection(bool decline)
        {
            var state=State();var reg=Registry();if(decline)Fill(state,1);
            var spec=CommonV4(SelectV4(new[]{Zone(state,"facility_supply_0").ZoneId},decline:decline),Zone(state,"facility_supply_1").ZoneId,"top");
            Value(spec.NormalizedArguments,"destination").Properties.Add(new NormalizedValueEntry{Name="mustBeEmpty",Value=NormalizedValue.CreateBoolean(true)});
            Value(spec.NormalizedArguments,"destination").Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));
            var node=Run(state,spec,reg);Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.FailureReason,Is.EqualTo("item_move_selection_not_supported"));Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
        }
        [TestCase("cycle",false)][TestCase("cycle",true)][TestCase("refill",false)][TestCase("refill",true)][TestCase("refresh",false)][TestCase("refresh",true)]
        public void OldFacilitySupplyDefinitionsAndMissingTypedFieldsAreRejected(string operation,bool journal)
        {
            var state=State(2);if(operation=="cycle")Fill(state,1);var reg=Registry();var spec=operation=="cycle"?ExplicitCycleV4(state,Ids(state,"facility_supply_0")[0]):operation=="refill"?RefillSpec(1,Zone(state,"facility_supply_0").ZoneId):RefreshSpec(1);var archive=journal?HostRecoveryService.CreateArchive(state):null;var node=Run(state,spec,reg);if(journal)HostRecoveryService.AppendJournal(archive,state);else archive=HostRecoveryService.CreateArchive(state);
            string clean=HostRecoveryService.SerializeArchive(archive);Assert.That(HostRecoveryService.RecoverJson(clean,reg).Succeeded,Is.True);
            foreach(bool missing in new[]{false,true})
            {
                var raw=JObject.Parse(clean);var saved=(journal?raw.SelectTokens("$..StateAfter").OfType<JObject>().Last():raw["Snapshot"]["State"])["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==node.EffectId);
                if(missing)((JObject)saved["FacilitySupplyFlowV1"]).Property("ReturnedItems").Remove();else saved["DefinitionVersion"]="v1";
                string bad=raw.ToString();artifacts.WriteLabeledText("设施共同包装坏档_"+operation+"_"+journal+"_"+missing,bad);var result=HostRecoveryService.RecoverJson(bad,reg);Assert.That(result.Succeeded,Is.False);Assert.That(result.Diagnostic,Is.EqualTo(missing?"facility_supply_raw_fields_invalid":operation=="cycle"?"facility_move_contract_version_unsupported":"facility_supply_contract_version_unsupported"));Assert.That(raw.ToString(),Is.EqualTo(bad));
            }
        }
        [Test]
        public void UnversionedFacilityMoveRejectsLuaAliasCompilerAndPendingRecovery()
        {
            foreach(string body in new[]{"return {Effect.MoveFacilityCard({player=ctx.playerId,sourceZone='supply',destinationZone='deck_bottom'})}","local old=Effect.MoveFacilityCard;return {old({player=ctx.playerId,sourceZone='supply',destinationZone='deck_bottom'})}"})
            {
                var result=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript("return function(ctx) "+body+" end"),new LuaInvocationContext("facility.retirement","test","p1",0,"1.0.0",2,0,0));Assert.That(result.IsSuccess,Is.False);Assert.That(result.Diagnostic,Does.Contain(RetiredFacilityMoveSignature));
            }
            var state=State();var reg=Registry();Run(state,Shuffle(state),reg);var raw=JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));var args=Object("player",NormalizedValue.CreateString("1"),"sourceZone",NormalizedValue.CreateString("supply"),"destinationZone",NormalizedValue.CreateString("deck_bottom"));
            var leaf=LuaEffectSpec.FromArguments(LuaDomainEffectTypeIds.MoveFacilityCard,args);var catalog=new LuaEffectTypeCatalog();Assert.That(catalog.TryGet(LuaDomainEffectTypeIds.MoveFacilityCard,out var entry),Is.True);Assert.That(entry.Validator(leaf),Is.EqualTo(RetiredFacilityMoveSignature));
            var old=EffectSpec.Create(LuaDomainEffectTypeIds.MoveFacilityCard,args,1);Assert.That(new EffectTreeExecutor(state,reg).TryCreateRoot(old,"","",out _),Is.False);
            ((JArray)raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"][0]["NestedEffects"]).Add(JObject.FromObject(new EffectSpecRuntimeState{EffectTypeId=old.EffectTypeId,NormalizedArguments=args}));var bad=raw.ToString();var rejected=HostRecoveryService.RecoverJson(bad,reg);Assert.That(rejected.Succeeded,Is.False);Assert.That(rejected.Diagnostic,Is.EqualTo(RetiredFacilityMoveSignature));Assert.That(raw.ToString(),Is.EqualTo(bad));
        }
    }
}
