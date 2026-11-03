using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Movement;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfluenceOperationDomainTests
    {
        [Test]
        public void SpecializedTokenOwnsNoMovementAndWaitsForCommonMovedResponsesAcrossRestore()
        {
            var state=State();string token=MapToken(state);
            registry.Register("api31.token.ack",c=>c.GetLatestInteractionAnswer()==null?EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec{InteractionTypeId="api31.token.ack",AnsweringPlayerId=2,Visibility="owner",AnswerSchema="candidate_id",MinSelections=1,MaxSelections=1,CandidateIds={"confirm"}}):EffectStepResult.Completed(),EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api31.token.ack","token.moved","*","api31.token.ack",c=>new System.Collections.Generic.List<EffectSpec>{EffectSpec.Create("api31.token.ack",playerId:2)},role:EffectHandlerRole.Observer));
            var parent=Run(state,TokenSpec(state,token,"pier",false));var body=SpecializedItemMovement.Body(state,parent);
            Assert.That(parent.PositionChanges,Is.Empty);Assert.That(body.PositionChanges.Count,Is.EqualTo(1));Assert.That(parent.Status,Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.RuleEvents.Single(e=>e.EventType=="token.moved").OwnerNodeId,Is.EqualTo(body.EffectId));
            string receipt=body.CommonItemMovementV4.Receipt.ReceiptId;state=Restore(state);
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");var engine=new EffectTreeExecutor(state,registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),2,NormalizedValue.CreateString("confirm"),out var reason),Is.True,reason);
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);state=Restore(state);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==parent.EffectId).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==body.EffectId).CommonItemMovementV4.Receipt.ReceiptId,Is.EqualTo(receipt));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="token.moved"),Is.EqualTo(1));
        }
        [TestCase("source",false)][TestCase("old_definition",false)][TestCase("old_definition",true)]
        public void SpecializedTokenRejectsChangedSourceAndOldWrapperWithoutArchiveRewrite(string fault,bool journal)
        {
            var state=State();string token=MapToken(state);var archive=HostRecoveryService.CreateArchive(state);
            var parent=Run(state,TokenSpec(state,token,"pier",false));
            if(journal)HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);else archive=HostRecoveryService.CreateArchive(state);
            string clean=HostRecoveryService.SerializeArchive(archive);var raw=JObject.Parse(clean);
            var checkpoint=journal?raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            var saved=checkpoint["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==parent.EffectId);
            if(fault=="source")saved["ContinuationData"]["Properties"].Single(p=>(string)p["Name"]=="sharedTokenSourceZone")["Value"]["StringValue"]="foreign";
            else saved["DefinitionVersion"]="v1";
            string bad=raw.ToString();artifacts.WriteLabeledText("专用地图坏档_"+fault+"_"+journal,bad);
            Assert.That(HostRecoveryService.RecoverJson(bad,registry).Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(bad));
            Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
    }
}
