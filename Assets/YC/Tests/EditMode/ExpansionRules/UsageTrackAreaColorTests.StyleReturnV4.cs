using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class UsageTrackAreaColorTests
    {
        private static EffectSpec StyleReturnSpec(GameState state, int player, string explicitId = null)
        {
            var args = Object("apiVersion", NormalizedValue.CreateInteger(4), "player", NormalizedValue.CreateString(player.ToString()), "operation", NormalizedValue.CreateString("return_city_style_marker"));
            if (explicitId != null)
            {
                var item = state.ItemZones.Items.Single(i => i.ItemId == explicitId);
                args.Properties.Add(new NormalizedValueEntry { Name = "selector", Value = Object("mode", NormalizedValue.CreateString("explicit"), "itemIds", Strings(new[] { explicitId }),
                    "expectedSource", Object("zoneId", NormalizedValue.CreateString(item.ZoneId), "slotId", NormalizedValue.CreateString(item.SlotId.Length == 0 ? "no-slot" : item.SlotId))) });
            }
            args.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name)); return EffectSpec.Create(TypeId, args, player);
        }
        private static EffectSpec ExplicitReturnWrapper(GameState state,string id)=>EffectSpec.Create(ContentLifecycleEffectExecutor.ReturnMarkerTypeId,
            Object("apiVersion",NormalizedValue.CreateInteger(2),"player",NormalizedValue.CreateString("1"),"selector",Value(StyleReturnSpec(state,1,id).NormalizedArguments,"selector").Clone()),1);
        private static string HistoricalFacts(GameState state, int player) => JsonConvert.SerializeObject(new {
            score=state.FindPlayer(player).Score, declarations=state.FindPlayer(player).DeclaredCityStyles.Select(d => new {d.DeclarationId,d.CityStyleId,d.CityStyleItemId,d.InfluenceMarkerId,d.UnlockedSpecialActionId,d.UsedFacilityIds,d.UsedCityBoardSlotIndexes}),
            used=state.FindPlayer(player).UsedSpecialActionIdsThisRound, groups=state.EffectRuntime.UsageGroupReceipts, bindings=state.EffectRuntime.UsageTrackBindings });
        private string BeginStyle(GameState state, EffectSpec spec)
        {
            var engine=new EffectTreeExecutor(state,registry);string id=engine.CreateRoot(spec);Run(engine);return id;
        }
        private void SelectStyle(GameState state, string marker)
        {
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");var engine=new EffectTreeExecutor(state,registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,NormalizedValue.CreateString(marker),out var reason),Is.True,reason);Run(engine);
        }
        [Test]
        public void StyleReturnV4AndDedicatedMatchFromSameActualInitialState()
        {
            var initial=State(finite:true);string own=DeclareFinite(initial,1),other=DeclareFinite(initial,2);var direct=GameStateCloneService.DeepClone(initial);var dedicated=GameStateCloneService.DeepClone(initial);
            BeginStyle(direct,StyleReturnSpec(direct,1,own));BeginStyle(dedicated,ReturnChoiceFlow());
            Assert.That(direct.EffectRuntime.InteractionRequests,Is.Empty);var request=dedicated.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");Assert.That(request.CandidateIds,Is.EqualTo(new[]{own}));Assert.That(request.CandidateIds,Does.Not.Contain(other));Assert.That(CityStyleMarkerReturn.Candidates(dedicated,1),Is.EqualTo(request.CandidateIds));Assert.That(dedicated.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==LuaDomainEffectTypeIds.Choice).EffectId,Is.EqualTo(request.OwnerEffectId));Assert.That(dedicated.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==ContentLifecycleEffectExecutor.ReturnMarkerTypeId),Is.Empty);SelectStyle(dedicated,own);
            Assert.That(JsonConvert.SerializeObject(direct.ItemZones),Is.EqualTo(JsonConvert.SerializeObject(dedicated.ItemZones)));Assert.That(JsonConvert.SerializeObject(direct.Players),Is.EqualTo(JsonConvert.SerializeObject(dedicated.Players)));Assert.That(JsonConvert.SerializeObject(direct.Map),Is.EqualTo(JsonConvert.SerializeObject(dedicated.Map)));
            Assert.That(ItemTransferEffectTests.Api32Views(direct),Is.EqualTo(ItemTransferEffectTests.Api32Views(dedicated)));
            Assert.That(JsonConvert.SerializeObject(direct.EffectRuntime.EffectNodes.Single(n=>n.CommonItemMovementV4?.Receipt!=null).CommonItemMovementV4.Receipt.Items),Is.EqualTo(JsonConvert.SerializeObject(dedicated.EffectRuntime.EffectNodes.Single(n=>n.CommonItemMovementV4?.Receipt!=null).CommonItemMovementV4.Receipt.Items)));
            Restore(direct,"同初态共同返还完成");Restore(dedicated,"同初态专用返还完成");
        }
        [Test]
        public void StyleReturnV4ReplacementUsesActualMemberAndKeepsSpentGroup()
        {
            var state=State(finite:true);string original=DeclareFinite(state,1);var engine=new EffectTreeExecutor(state,registry);string actionId=YC.Domain.SpecialActions.SpecialActionDatabase.SourceStoneIndustrialHub;
            engine.CreateRoot(AbilityActionExecutor.Create(registry.QueryActions(state,1).Single(o=>o.ActionId==actionId),"style.return.replacement.used",Object()));Run(engine);
            var binding=state.EffectRuntime.UsageTrackBindings.Single();string source=state.ItemZones.Items.Single(i=>i.ItemId==original).ZoneId;BeginStyle(state,StyleReturnSpec(state,1,original));
            string replacement=state.ItemZones.Items.First(i=>i.TypeId=="core.player_marker"&&i.OwnerSubject.PlayerId==1&&i.ItemId!=original&&i.ZoneId==FindZone(state,"supply",1).ZoneId).ItemId;
            BeginStyle(state,EnterpriseEffectTests.MarkerSpecV4(1,EnterpriseEffectTests.MarkerLegV4(replacement,FindZone(state,"supply",1).ZoneId,source,"2")));
            Assert.That(UsageTracks.CurrentMarker(state,binding).ItemId,Is.EqualTo(replacement));Assert.That(registry.QueryActions(state,1).Single(o=>o.ActionId==actionId).Enabled,Is.False);
            string history=HistoricalFacts(state,1);state=Restore(state,"换入实际成员不重置用量");BeginStyle(state,ReturnChoiceFlow());SelectStyle(state,replacement);
            Assert.That(HistoricalFacts(state,1),Is.EqualTo(history));Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==replacement).ZoneId,Is.EqualTo(FindZone(state,"supply",1).ZoneId));Restore(state,"换入成员共同返还完成");
        }
        [Test]
        public void StyleReturnV4DedicatedWaitsForCommonCompletionResponseAndRestores()
        {
            var state=State(finite:true);string marker=DeclareFinite(state,1);
            registry.Register("api30.return.ack",c=>c.Node.FlowStage==""?EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec{InteractionTypeId="api30.return.ack",AnsweringPlayerId=2,Visibility="owner",AnswerSchema="candidate_id",MinSelections=1,MaxSelections=1,CandidateIds={"confirm"}}):EffectStepResult.Completed(),EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api30.return.ack","EffectCompleted","*","api30.return.ack",c=>Text(c.Event.Payload,"effectTypeId")==TypeId&&Text(c.Event.Payload,"outcome")=="completed"?new List<EffectSpec>{EffectSpec.Create("api30.return.ack",playerId:2)}:new List<EffectSpec>(),role:EffectHandlerRole.Observer));
            string root=BeginStyle(state,ReturnChoiceFlow());SelectStyle(state,marker);Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).Status,Is.Not.EqualTo(EffectNodeStatus.Completed));
            state=Restore(state,"共同返还完成响应等待中");var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");Assert.That(request.AnsweringPlayerId,Is.EqualTo(2));var engine=new EffectTreeExecutor(state,registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),2,NormalizedValue.CreateString("confirm"),out var reason),Is.True,reason);Run(engine);Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.EffectNodes.Where(n=>n.CommonItemMovementV4!=null).Sum(n=>n.PositionChanges.Count),Is.EqualTo(1));Restore(state,"共同返还响应完成");
        }
        [TestCase("explicit")][TestCase("dedicated")]
        public void StyleReturnV4UsesRealOriginalSupplyAndPreservesUsedHistory(string mode)
        {
            var state=State(finite:true);string marker=DeclareFinite(state,1);var engine=new EffectTreeExecutor(state,registry);
            var action=registry.QueryActions(state,1).Single(o=>o.ActionId==YC.Domain.SpecialActions.SpecialActionDatabase.SourceStoneIndustrialHub);
            engine.CreateRoot(AbilityActionExecutor.Create(action,"style.return.used",Object()));Run(engine);
            string history=HistoricalFacts(state,1), source=state.ItemZones.Items.Single(i=>i.ItemId==marker).ZoneId, slot=state.ItemZones.Items.Single(i=>i.ItemId==marker).SlotId;
            string root=BeginStyle(state,mode=="dedicated"?ReturnChoiceFlow():StyleReturnSpec(state,1,mode=="explicit"?marker:null));
            if(mode!="explicit")
            {
                var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");Assert.That(request.CandidateIds,Is.EqualTo(new[]{marker}));Assert.That(request.MinSelections,Is.EqualTo(1));Assert.That(request.MaxSelections,Is.EqualTo(1));Assert.That(request.AllowDecline,Is.False);
                Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==marker).ZoneId,Is.EqualTo(source));state=Restore(state,"二级返还确认前");SelectStyle(state,marker);
            }
            var move=state.EffectRuntime.EffectNodes.Single(n=>n.CommonItemMovementV4?.Receipt!=null);var receipt=move.CommonItemMovementV4.Receipt;
            Assert.That(receipt.Result.MovedItemIds,Is.EqualTo(new[]{marker}));Assert.That(receipt.Items.Single().From.ZoneId,Is.EqualTo(source));Assert.That(receipt.Items.Single().From.SlotId,Is.EqualTo(slot));
            Assert.That(receipt.Items.Single().To.ZoneId,Is.EqualTo(state.ItemZones.Items.Single(i=>i.ItemId==marker).OriginalSupplyZoneId));Assert.That(receipt.Result.MovedCount,Is.EqualTo(1));
            Assert.That(HistoricalFacts(state,1),Is.EqualTo(history));Assert.That(UsageTracks.CurrentMarker(state,state.EffectRuntime.UsageTrackBindings.Single()),Is.Null);
            Assert.That(registry.QueryActions(state,1).Any(o=>o.ActionId==action.ActionId),Is.False);Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).Status,Is.EqualTo(EffectNodeStatus.Completed));
            if(mode=="dedicated") {var wrapper=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root);Assert.That(Text(wrapper.NormalizedResult,"markerItemId"),Is.EqualTo(marker));Assert.That(wrapper.PositionChanges,Is.Empty);Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==move.ParentEffectId).EffectTypeId,Is.EqualTo(ContentLifecycleEffectExecutor.ReturnMarkerTypeId));}
            state=Restore(state,"二级返还完成后");Run(new EffectTreeExecutor(state,registry));Assert.That(state.EffectRuntime.EffectNodes.SelectMany(n=>n.PositionChanges).Count(c=>c.ItemId==marker&&c.From.ZoneId==source&&c.To.ZoneId==receipt.Items.Single().To.ZoneId),Is.EqualTo(1));
        }
        [TestCase(false)][TestCase(true)]
        public void ReturnMarkerBusinessFailsWithoutCandidatesAndMoveItemRequiresExplicitTarget(bool dedicated)
        {
            var state=State(finite:true);string before=JsonConvert.SerializeObject(state.ItemZones);
            if(dedicated){string root=BeginStyle(state,ReturnChoiceFlow());Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).FailureReason,Is.EqualTo("lua_choice_no_candidates"));Restore(state,"无二级候选业务失败");}
            else{var spec=StyleReturnSpec(state,1);Assert.That(ItemMovementV4.ValidateArguments(spec.NormalizedArguments),Is.EqualTo("item_move_explicit_target_required"));var engine=new EffectTreeExecutor(state,registry);string id=engine.CreateRoot(spec);Run(engine);Assert.That(engine.GetNode(id).FailureReason,Is.EqualTo("item_move_explicit_target_required"));}
            Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(before));
        }
        [TestCase("other_owner")][TestCase("supply")][TestCase("non_style_track")][TestCase("source_changed")]
        public void StyleReturnV4RejectsIneligibleExplicitSourcesWithoutMoving(string fault)
        {
            var state=State(finite:true);string marker=DeclareFinite(state,fault=="other_owner"?2:1);
            if(fault=="supply")marker=state.ItemZones.Items.First(i=>i.TypeId=="core.player_marker"&&i.OwnerSubject.PlayerId==1&&i.ZoneId==FindZone(state,"supply",1).ZoneId).ItemId;
            if(fault=="non_style_track"){Initialize(state);marker=UsageTracks.CurrentMarker(state,state.EffectRuntime.UsageTrackBindings.Single(b=>b.TrackId==Track)).ItemId;}
            var spec=StyleReturnSpec(state,1,marker);
            if(fault=="supply")Value(Value(spec.NormalizedArguments,"selector"),"expectedSource").Properties.RemoveAll(p=>p.Name=="slotId");
            if(fault=="source_changed")Value(Value(Value(spec.NormalizedArguments,"selector"),"expectedSource"),"zoneId").StringValue=FindZone(state,"supply",1).ZoneId;
            string before=JsonConvert.SerializeObject(state.ItemZones);string root=BeginStyle(state,spec);var node=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root);
            Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.FailureReason,Is.EqualTo(fault=="source_changed"?"source_zone_changed":"city_style_marker_return_not_permitted"));Assert.That(node.PositionChanges,Is.Empty);Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(before));Restore(state,"明确二级返还非法源_"+fault);
        }
        [TestCase("")][TestCase("v4_bound")][TestCase("v4_committed")][TestCase("v4_finished")]
        public void ExplicitStyleReturnRestoresMovementPhasesWithoutAnySelection(string stage)
        {
            var state=State(finite:true);string marker=DeclareFinite(state,1);var engine=new EffectTreeExecutor(state,registry);string root=engine.CreateRoot(StyleReturnSpec(state,1,marker));
            for(int i=0;engine.GetNode(root).FlowStage!=stage&&i<40;i++)Assert.That(engine.Advance(),Is.True);
            Assert.That(engine.GetNode(root).FlowStage,Is.EqualTo(stage));Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
            state=Restore(state,"显式返还真实阶段_"+stage);engine=new EffectTreeExecutor(state,registry);Run(engine);
            state=Restore(state,"显式返还续接完成_"+stage);Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).CommonItemMovementV4.Receipt.Result.MovedCount,Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
        }
        [Test]
        public void StyleReturnV4PublicLuaCompilesAndRejectsOverrides()
        {
            var state=State(finite:true);string marker=DeclareFinite(state,1);
            string source="return function(ctx) return {Effect.MoveItem({apiVersion=4,player=ctx.playerId,operation='return_city_style_marker',selector={mode='explicit',itemIds={"+JsonConvert.SerializeObject(marker)+"},expectedSource={zoneId="+JsonConvert.SerializeObject(state.ItemZones.Items.Single(i=>i.ItemId==marker).ZoneId)+"}}})} end";
            var invocation=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("style.return","style.return","style.return","1",source,LuaContentHasher.ComputeSha256(source)),new LuaInvocationContext("style.return","style.return","p1",0,"1",2,0,0));
            Assert.That(invocation.IsSuccess,Is.True,invocation.Diagnostic);Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation,new LuaEffectCompilationContext{PlayerId=1},out var specs,out var failure),Is.True,failure?.Diagnostic);BeginStyle(state,specs.Single());Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);Restore(state,"公开Lua二级返还完成");
            foreach(string field in new[]{"requiredCount","destination","insertion","operationProfileId","carrierPolicy","reveal"})
            {var invalid=StyleReturnSpec(state,1).NormalizedArguments;invalid.Properties.Add(new NormalizedValueEntry{Name=field,Value=NormalizedValue.CreateString("forged")});invalid.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));Assert.That(ItemMovementV4.ValidateArguments(invalid),Is.EqualTo("item_move_v4_field_rejected"),field);}
        }
        [TestCase(false)][TestCase(true)]
        public void StyleReturnV4OldDedicatedDefinitionRejectedInSnapshotAndJournal(bool journal)
        {
            var state=State(finite:true);string marker=DeclareFinite(state,1);var archive=HostRecoveryService.CreateArchive(state);string root=BeginStyle(state,ExplicitReturnWrapper(state,marker));
            if(journal)HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);else archive=HostRecoveryService.CreateArchive(state);
            string clean=HostRecoveryService.SerializeArchive(archive);var raw=JObject.Parse(clean);var checkpoint=journal?raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            checkpoint["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==root)["DefinitionVersion"]="v1";string bad=raw.ToString();artifacts.WriteLabeledText("旧二级返还坏档_"+journal,bad);
            var rejected=HostRecoveryService.RecoverJson(bad,registry);Assert.That(rejected.Succeeded,Is.False);Assert.That(rejected.Diagnostic,Is.EqualTo("city_style_marker_return_version_unsupported"));Assert.That(raw.ToString(),Is.EqualTo(bad));Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
        [TestCase(false)][TestCase(true)]
        public void StyleReturnV4RejectsOldDedicatedPendingSpec(bool journal)
        {
            var state=State(finite:true);string marker=DeclareFinite(state,1);var archive=HostRecoveryService.CreateArchive(state);var engine=new EffectTreeExecutor(state,registry);
            string root=engine.CreateRoot(EffectSpec.Condition(new List<EffectSpec>{ExplicitReturnWrapper(state,marker)},new List<EffectSpec>()));
            if(journal)HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);else archive=HostRecoveryService.CreateArchive(state);
            string clean=HostRecoveryService.SerializeArchive(archive);var raw=JObject.Parse(clean);var checkpoint=journal?raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            checkpoint["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==root)["NestedEffects"][0]["DefinitionVersion"]="v1";string bad=raw.ToString();artifacts.WriteLabeledText("旧二级返还待执行坏档_"+journal,bad);
            var rejected=HostRecoveryService.RecoverJson(bad,registry);Assert.That(rejected.Succeeded,Is.False);Assert.That(rejected.Diagnostic,Is.EqualTo("city_style_marker_return_version_unsupported"));Assert.That(raw.ToString(),Is.EqualTo(bad));Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
    }
}
