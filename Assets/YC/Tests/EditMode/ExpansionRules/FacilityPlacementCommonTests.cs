using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 使用现有隔离建设夹具；不计正常菜单、建房、开局或画面验收。
    public sealed class FacilityPlacementCommonTests : FacilityLayerTestFixture
    {
        private EffectNodeRuntimeState CompleteCommonBuild(GameState state)
        {
            string definition=Definition(state,d=>!d.Unique&&!d.HasEntryEffect);string item=Card(state,definition).ItemId;
            var build=Run(state,Build(state,item,0));Assert.That(build.Status,Is.EqualTo(EffectNodeStatus.Completed));return build;
        }
        [Test]
        public void PaidPlacementUsesOnePublicCommonBatchAndRealControllerReceiptAcrossRecovery()
        {
            var state=State();var build=CompleteCommonBuild(state);var facts=FacilityBuildJournal.Read(build);
            var body=state.EffectRuntime.EffectNodes.Single(n=>n.CommonItemMovementV4?.FacilityPlacement?.BuildEffectId==build.EffectId);
            Assert.That(Number(body.NormalizedArguments,"apiVersion"),Is.EqualTo(4));Assert.That(Text(body.NormalizedArguments,"operation"),Is.EqualTo("transfer_batch"));
            Assert.That(body.TransferBatchRequest,Is.Null);Assert.That(Value(body.ContinuationData,"transferBatchReceipt"),Is.Null);
            var receipt=body.CommonItemMovementV4.Receipt;var item=receipt.Items.Single();
            Assert.That(item.ItemId,Is.EqualTo(facts.ItemId));Assert.That(item.To.ZoneId,Is.EqualTo(Cell(state,0).CellZoneId));
            Assert.That(item.ControllerAfter.Kind,Is.EqualTo(ItemSubjectKind.Player));Assert.That(item.ControllerAfter.PlayerId,Is.EqualTo(1));
            string id=receipt.ReceiptId;int score=state.FindPlayer(1).Score;state=Recover(state,"共同建设真实控制者和收据");
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==body.EffectId).CommonItemMovementV4.Receipt.ReceiptId,Is.EqualTo(id));
            Assert.That(state.FindPlayer(1).Score,Is.EqualTo(score));Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType==FacilityBuildJournal.EventType),Is.EqualTo(1));
        }
        [Test]
        public void CopiedPlacementArgumentsAndBuildSourceCannotAuthorizeAnotherRoot()
        {
            var state=State();var build=CompleteCommonBuild(state);var body=state.EffectRuntime.EffectNodes.Single(n=>n.CommonItemMovementV4?.FacilityPlacement?.BuildEffectId==build.EffectId);
            var spec=EffectSpec.Create(TypeId,body.NormalizedArguments.Clone(),1);spec.SourceId=build.EffectId;
            string before=ItemTransferService.MovementV4Digest(state.ItemZones);var denied=Run(state,spec);
            Assert.That(denied.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(denied.FailureReason,Is.EqualTo("operation_profile_rejected"));
            Assert.That(denied.PositionChanges,Is.Empty);Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(before));Recover(state,"裸根不能借真实建设ID取得权限");
        }
        [TestCase("proof")][TestCase("missing_proof")][TestCase("raw_boolean")][TestCase("order")][TestCase("old_build")]
        public void CommonPlacementRejectsAlteredProofAndOldBuildWithoutRewritingArchive(string fault)
        {
            var state=State();var build=CompleteCommonBuild(state);var body=state.EffectRuntime.EffectNodes.Single(n=>n.CommonItemMovementV4?.FacilityPlacement?.BuildEffectId==build.EffectId);
            string clean=HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));var raw=JObject.Parse(clean);var nodes=raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"];
            var saved=nodes.Single(n=>(string)n["EffectId"]==body.EffectId);var plan=saved["CommonItemMovementV4"];
            if(fault=="proof")plan["FacilityPlacement"]["BuildEffectId"]="foreign";
            if(fault=="missing_proof")((JObject)plan).Property("FacilityPlacement").Remove();
            if(fault=="raw_boolean")plan["FacilityPlacement"]["Overlay"]="false";
            if(fault=="order")saved["NormalizedArguments"]["Properties"].Single(p=>(string)p["Name"]=="expectedOrders")["Value"]["Items"][0]["Properties"].Single(p=>(string)p["Name"]=="digest")["Value"]["StringValue"]="foreign";
            if(fault=="old_build")nodes.Single(n=>(string)n["EffectId"]==build.EffectId)["DefinitionVersion"]="2.0.0";
            string bad=raw.ToString();artifacts.WriteLabeledText("共同建设坏档_"+fault,bad);Assert.That(HostRecoveryService.RecoverJson(bad,Registry).Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(bad));Assert.That(HostRecoveryService.RecoverJson(clean,Registry).Succeeded,Is.True);
        }
    }
}
