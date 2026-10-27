using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class G1CleanupProtocolTests
    {
        private IsolatedTestArtifacts artifacts;
        private CleanupSourceTestFixture fixture;
        [SetUp] public void Begin() { artifacts = IsolatedTestArtifacts.BeginCase(); fixture = null; }
        [TearDown] public void End() { fixture?.Snapshot("G1收尾最终现场"); artifacts.Finish(); }
        [TestCase(false)] [TestCase(true)]
        public void PaidCleanupKeepsRealDepartureCustodyAcrossCurrentHostRestore(bool concurrent)
        {
            const string body = @"return function(ctx) return {Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,
leftEffects={tokenTransfer(ctx.payload.sourceItemId,3)},
rightEffects={Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='self',count=1,requiredCount=1})}})} end";
            fixture = new CleanupSourceTestFixture(artifacts, initialMarkers: 3, bodyOverride: body, storageResponse: true, concurrent: concurrent, inMemoryInput: true);
            var f = fixture;
            var map = StaticMapDefinitions.Resolve(f.State.MapId);
            string slot = InfluenceService.GetLocationSlotId(map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > 0).LocationId, 0);
            f.Engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, slot, itemTypeId: "core.player_marker")); f.Drain();
            f.Start("qy018.paid.cleanup"); f.Answer("continue");
            f.Restore("G1收尾已付等待真实移除");
            f.Answer(f.Open.CandidateIds.First());
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Single().Status, Is.EqualTo("open"));
            Assert.That(f.Source.ActiveInvocationId, Is.Not.Empty);
            string invocation = f.Source.ActiveInvocationId;
            Assert.That(CleanupSourceDependencies.Instance.HasPendingForWindow(f.State, f.RootId, out _), Is.True);
            Assert.That(f.Engine.TryEndSettlementAction(1, f.Window.Context.Id, "", "", f.State.EffectRuntime.StateRevision, "qy018.too-early", out _), Is.False);
            f.Restore("G1真实离场及已付收尾义务待答");
            Assert.That(f.Source.ActiveInvocationId, Is.EqualTo(invocation));
            f.Answer("store");
            Assert.That(f.Stored, Is.EqualTo(1)); Assert.That(f.Source.ActiveInvocationId, Is.Empty);
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Count, Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Single().Status, Is.Not.EqualTo("open"));
            Assert.That(CleanupSourceDependencies.Instance.HasPendingForWindow(f.State, f.RootId, out _), Is.False);
            f.Restore("G1真实收容完成回列表不重扣");
            Assert.That(f.Stored, Is.EqualTo(1));
        }
    }
    public sealed class G1FacilityProtocolTests : FacilityLayerTestFixture
    {
        protected override bool UseInMemoryContent => true;
        protected override bool UseCardQueryCarrier => true;
        private GameState current;
        [TearDown] public void Preserve() { if (current != null) artifacts.WriteLabeledText("G1设施最终现场", JsonConvert.SerializeObject(current)); }
        [Test] public void AuthorizedCandidatesKeepTypedHistoricalProofsAndRejectMissingOrCoercedRows()
        {
            current = State(copies: 2);
            string carrier = current.ItemZones.Items.Single(i => i.DefinitionId == Token && i.OwnerSubject.PlayerId == 1).ItemId;
            var zone = new YC.Domain.Items.CardZoneDefinition { LocalZoneId = "g1_facilities", ZoneTemplateId = "core.zone.card_facility_storage",
                AcceptedItemKinds = new System.Collections.Generic.List<YC.Domain.Items.ItemPhysicalKind> { YC.Domain.Items.ItemPhysicalKind.Card },
                AcceptedTypeIds = new System.Collections.Generic.List<string> { "core.facility_card" }, CapacityPolicy = "unbounded",
                OrderingKind = YC.Domain.Items.ZoneOrderingKind.Unordered, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer",
                CarrierExitPolicyId = "core.return_contents_before_exit", VisibilityPolicyId = YC.Domain.Items.ItemZoneVisibility.Public };
            Assert.That(new YC.Domain.Items.ItemZoneStateFactory().TryCreateCardZone(current, carrier, zone, out string storage, out string reason), Is.True, reason);
            string definition = Definition(current, d => !d.Unique && !d.HasEntryEffect);
            Transfer(current, Leg(current, Card(current, definition).ItemId, storage), Leg(current, Card(current, definition, 1).ItemId, storage));
            string key = Registry.QuerySources(current, 1, "ruleQuery").Single(source => source.AbilityId == QueryAbility).SourceKey;
            var node = Run(current, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId,
                Object("sourceZone", NormalizedValue.CreateString("authorized"), "sourceKey", NormalizedValue.CreateString(key),
                    "paymentMode", NormalizedValue.CreateString("auto"), "allowDecline", NormalizedValue.CreateBoolean(true)), 1));
            var request = current.EffectRuntime.InteractionRequests.Single(r => r.OwnerEffectId == node.EffectId && r.Status == "open");
            var record = current.EffectRuntime.CandidateResolutions.Single(r => r.CandidateSetId == request.CandidateResolutionId);
            Assert.That(record.FacilitySourceProofs.Select(p => p.ItemId), Is.EqualTo(request.CandidateIds));
            Assert.That(record.FacilitySourceProofs.All(p => p.SourceKey == key && p.ContentHash == current.ContentPackHash), Is.True);
            Assert.That(request.PromptParameters.IsValid(), Is.True);
            current = Recover(current, "G1授权完整候选与历史来源证明");
            var archive = Newtonsoft.Json.Linq.JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(current)));
            var raw = (Newtonsoft.Json.Linq.JArray)archive["Snapshot"]["State"]["EffectRuntime"]["CandidateResolutions"];
            var target = raw.Single(r => (string)r["CandidateSetId"] == request.CandidateResolutionId);
            ((Newtonsoft.Json.Linq.JArray)target["FacilitySourceProofs"]).Clear();
            Assert.That(HostRecoveryService.RecoverJson(archive.ToString(), Registry).Succeeded, Is.False);
            archive = Newtonsoft.Json.Linq.JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(current)));
            raw = (Newtonsoft.Json.Linq.JArray)archive["Snapshot"]["State"]["EffectRuntime"]["CandidateResolutions"];
            target = raw.Single(r => (string)r["CandidateSetId"] == request.CandidateResolutionId);
            target["FacilitySourceProofs"][0]["SourceZoneId"] = 123;
            Assert.That(HostRecoveryService.RecoverJson(archive.ToString(), Registry).Succeeded, Is.False);
        }
        [Test] public void InFlightLayerRetirementRevealsOriginalLowerCardOnceAfterCurrentHostRestore()
        {
            current = State();
            string definition = Definition(current, d => !d.Unique && !d.HasEntryEffect);
            string lower = Card(current, definition).ItemId, top = Card(current, definition, 1).ItemId;
            Put(current, lower, 0); Overlay(current, top, 0);
            current = Recover(current, "G1上下层真实起点");
            var cell = Cell(current, 0); var engine = new EffectTreeExecutor(current, Registry);
            string id = engine.CreateRoot(EffectSpec.Create(ContentLifecycleEffectExecutor.RetireFacilityTypeId,
                Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString("1"),
                    "facilityItemId", NormalizedValue.CreateString(top), "expectedCellZoneId", NormalizedValue.CreateString(cell.CellZoneId),
                    "expectedOrderDigest", NormalizedValue.CreateString(cell.OrderDigest)), 1));
            for (int guard = 0; engine.GetNode(id).TransferBatchRequest == null && guard < 100; guard++) Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
            Assert.That(engine.GetNode(id).TransferBatchRequest, Is.Not.Null);
            current = Recover(current, "G1层转移真实在途");
            engine = new EffectTreeExecutor(current, Registry);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, current.EffectRuntime.LastFaultMessage);
            Assert.That(Cell(current, 0).TopItemId, Is.EqualTo(lower));
            Assert.That(Cell(current, 0).CoveredItemIds, Is.Empty);
            Assert.That(engine.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            current = Recover(current, "G1顶层离场下层保留完成");
            string before = RuleStateDigestService.Compute(current);
            Assert.That(new EffectTreeExecutor(current, Registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(RuleStateDigestService.Compute(current), Is.EqualTo(before));
        }
    }
}
