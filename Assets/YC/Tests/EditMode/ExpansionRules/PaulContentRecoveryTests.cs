using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class PaulContentRecoveryTests
    {
        private PaulContentTestFixture f;
        [TearDown] public void End() => f?.Dispose();
        [Test] public void TypeChoicePendingAnswerAndCompletedLeafResumeWithoutSecondPlacement()
        {
            f = new PaulContentTestFixture(); string slot = InfluenceService.GetLocationSlotId(f.Location, 0);
            string root = f.Run(InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
            var pending = f.Request(root, "effect.choice.execute"); string requestId = pending.GetStableInteractionId();
            f.Restore("P09Choice待答"); pending = f.Open.Single(x => x.GetStableInteractionId() == requestId); f.Answer(pending, "gang");
            int count = f.State.Map.Influences.Count; int records = f.State.EffectRuntime.InfluencePlaceMappings.Count;
            f.Restore("P09Choice已答及叶完成"); f.Pump();
            Assert.That(f.State.Map.Influences.Count, Is.EqualTo(count)); Assert.That(f.State.EffectRuntime.InfluencePlaceMappings.Count, Is.EqualTo(records));
            Assert.That(f.Engine.TrySubmitInteraction(requestId, 1, NormalizedValue.CreateString("ordinary"), out _), Is.False);
        }
        [TestCase(0)][TestCase(1)] public void StrictShortLeftAndConditionResultRestoreWithoutUpgradingToFault(int count)
        {
            f = new PaulContentTestFixture(); f.Choose(5, "P05"); f.DockCity(f.Location); for (int i = 0; i < count; i++) f.Place(f.Location, i);
            string root = f.Invoke("entrepreneur.face.P05"); var q = f.Request(root); string id = q.GetStableInteractionId();
            f.Restore("P5条件执行待答" + count); f.Answer(f.Open.Single(x => x.GetStableInteractionId() == id), "continue");
            var condition = f.State.EffectRuntime.EffectNodes.Single(n => f.Within(n.EffectId, root) && n.EffectTypeId == "effect.flow.condition");
            Assert.That(Text(condition.NormalizedResult, "outcome"), Is.EqualTo("condition_not_met"));
            string facts = JsonConvert.SerializeObject(new { f.State.Map.Influences, f.State.FindPlayer(1).Resources });
            f.Restore("P5左式足额失败后" + count); f.Pump();
            Assert.That(JsonConvert.SerializeObject(new { f.State.Map.Influences, f.State.FindPlayer(1).Resources }), Is.EqualTo(facts));
        }
        [Test] public void ShareOriginalPointAndBeneficiaryTreePersistAcrossSelectedBranch()
        {
            f = new PaulContentTestFixture(); f.Choose(3, "P03"); string gang = f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker"); f.Place(f.OtherLocation, 1, 2);
            U10ShareTestSupport.AddDepartureWait(f);
            string root = U10ShareTestSupport.StartCleanup(f, "P03"); f.Answer(f.Request(root, "action.decline_effect"), "continue");
            var selected = f.Request(root, "effect.influence.remove.target"); string id = selected.GetStableInteractionId(); f.Restore("分享U12真实实例选择待答");
            selected = f.Open.Single(x => x.GetStableInteractionId() == id);
            Assert.That(f.Engine.TrySubmitInteraction(id, 2, selected.StateRevision, NormalizedValue.CreateString(gang), out _), Is.False);
            f.Answer(selected, gang);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => f.Within(n.EffectId, root) && n.EffectTypeId == ResourceEffectTypeIds.Gain), Is.False, "必要离场响应待答时不得发收益。");
            var wait = f.Open.Single(q => q.InteractionTypeId == "qy007.u10.departure.wait"); string waitId = wait.GetStableInteractionId();
            f.Restore("分享真实移除已提交而必要响应待答"); f.Answer(f.Open.Single(q => q.GetStableInteractionId() == waitId), "ack");
            U10ShareTestSupport.AssertOneCondition(f, root);
            Assert.That(f.Engine.TrySubmitInteraction(id, 1, NormalizedValue.CreateString(gang), out _), Is.False);
            var nodes = f.State.EffectRuntime.EffectNodes.Where(n => f.Within(n.EffectId, root) && n.EffectTypeId == "effect.resource.gain").ToList();
            Assert.That(nodes.Count, Is.EqualTo(2)); Assert.That(nodes.Select(n => Value(n.NormalizedArguments, "executingPlayer").ReferenceId).Distinct(), Is.EquivalentTo(new[] { "p1", "p2" }));
            string paid = JsonConvert.SerializeObject(f.State.Players.Select(p => p.Resources).ToList()); f.Restore("分享收益已构造到账"); f.Pump();
            Assert.That(JsonConvert.SerializeObject(f.State.Players.Select(p => p.Resources).ToList()), Is.EqualTo(paid));
        }
        [TestCase("P03")][TestCase("P04")]
        public void SelectedGrantAndCompletedReplacementRestoreWithoutSecondPlacement(string face)
        {
            f = new PaulContentTestFixture(); f.Choose(3, face); f.Restore("R3正式选面收据与grant已提交");
            string slot = InfluenceService.GetLocationSlotId(f.OtherLocation, 0);
            string root = f.Run(InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
            var placed = f.State.Map.Influences.Single(i => i.SlotId == slot);
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == placed.InfluenceId).TypeId, Is.EqualTo("entrepreneur.gang_marker"));
            var mapping = f.State.EffectRuntime.InfluencePlaceMappings.Single(m => m.LogicalOperationId == root);
            Assert.That(mapping.Sources.Single(s => s.AbilityId == "entrepreneur.P09.place").GrantingSourceRefs, Is.Not.Empty);
            int count = f.State.Map.Influences.Count, mappings = f.State.EffectRuntime.InfluencePlaceMappings.Count;
            f.Restore("R3真实帮派替代完成"); f.Pump();
            Assert.That(f.State.Map.Influences.Count, Is.EqualTo(count)); Assert.That(f.State.EffectRuntime.InfluencePlaceMappings.Count, Is.EqualTo(mappings));
        }
        [Test] public void RevokedTargetGrantRestoresPendingOrdinaryChoiceWithoutRegranting()
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03");
            var group = f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == 3);
            string item = group.Candidates.Single(c => c.CandidateId == group.SelectedCandidateId).EntityItemId;
            var targetKeys = f.Registry.QuerySources(f.State, 1).Where(s => s.ItemId == item).Select(s => s.SourceKey).ToList(); Assert.That(targetKeys, Is.Not.Empty);
            var ability = f.State.ItemZones.Items.Single(i => i.ItemId == f.AbilityItem);
            Assert.That(Value(ability.LocalState, "grantingSourceRefs").Items.RemoveAll(v => targetKeys.Contains(v.StringValue)), Is.EqualTo(targetKeys.Count));
            f.Artifacts.WriteLabeledText("R3撤销grant隔离前提", "只从P09授权表撤销P03真实key，P03实际选面仍有效；另一面grant保留。后续进入真实挂接/请求/恢复。");
            string slot = InfluenceService.GetLocationSlotId(f.OtherLocation, 0), root = f.Run(InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
            string id = f.Request(root, "effect.choice.execute").GetStableInteractionId();
            f.Restore("R3撤销后普通帮派Choice待答"); f.Answer(f.Open.Single(q => q.GetStableInteractionId() == id), "ordinary");
            var placed = f.State.Map.Influences.Single(i => i.SlotId == slot);
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == placed.InfluenceId).TypeId, Is.EqualTo("core.player_marker"));
            Assert.That(f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.P09.place").GrantingSourceRefs.Intersect(targetKeys), Is.Empty);
            int count = f.State.Map.Influences.Count; f.Restore("R3撤销后明确普通叶已完成"); f.Pump(); Assert.That(f.State.Map.Influences.Count, Is.EqualTo(count));
        }
        [Test] public void P09ResponsesToStrictBatchPrecedeP5RewardAndRestoreOnce()
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(5, "P05"); f.DockCity(f.Location);
            string first = f.Place(f.Location, 0, type: "entrepreneur.gang_marker"), second = f.Place(f.Location, 1, type: "entrepreneur.gang_marker");
            f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
            var point = f.State.Map.ResourceTokens.Single(p => p.LocationId == f.OtherLocation); int before = f.Resource(1, point.ResourceType);
            string root = f.Invoke("entrepreneur.face.P05"); string qid = f.Request(root).GetStableInteractionId();
            f.Restore("R3批次条件执行前"); f.Answer(f.Open.Single(q => q.GetStableInteractionId() == qid), "continue");
            var departures = f.State.EffectRuntime.InfluenceDepartures.Where(d => d.ItemId == first || d.ItemId == second).ToList();
            Assert.That(departures, Has.Count.EqualTo(2)); Assert.That(departures.Select(d => d.BatchId).Distinct().Count(), Is.EqualTo(1));
            var city = f.State.Map.Influences.Where(i => i.LocationId == f.Location).ToList(); Assert.That(city, Has.Count.EqualTo(2));
            Assert.That(city.All(i => f.State.ItemZones.Items.Single(item => item.ItemId == i.InfluenceId).TypeId == "core.player_marker"), Is.True);
            var departureIds = departures.Select(d => d.DepartureId).ToList();
            var events = f.State.EffectRuntime.RuleEvents.Where(e => e.EventType == "InfluenceRemoved" && departureIds.Contains(Text(e.Payload, "departureId"))).ToList();
            Assert.That(events, Has.Count.EqualTo(2));
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Count(u => u.AbilityId == "entrepreneur.P09.departure" && events.Any(e => e.EventId == u.TriggerEventId) && u.SourceRef.ItemId == f.AbilityItem && u.Outcome == "completed"), Is.EqualTo(2));
            Assert.That(f.Resource(1, point.ResourceType) - before, Is.EqualTo(point.Amount));
            string facts = JsonConvert.SerializeObject(new { f.State.Map.Influences, f.State.FindPlayer(1).Resources, f.State.EffectRuntime.InfluenceDepartures });
            int uses = f.State.EffectRuntime.AbilitySourceUses.Count; f.Restore("R3两份P09必要补放与P5收益结清"); f.Pump();
            Assert.That(JsonConvert.SerializeObject(new { f.State.Map.Influences, f.State.FindPlayer(1).Resources, f.State.EffectRuntime.InfluenceDepartures }), Is.EqualTo(facts));
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Count, Is.EqualTo(uses));
        }
        [Test] public void MissingSourceSelectionReceiptIsRejectedByRealHost()
        {
            f = new PaulContentTestFixture(); f.Choose(7, "P07");
            string raw = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State)); var archive = JObject.Parse(raw); int changed = 0;
            foreach (var item in archive.Descendants().OfType<JObject>().Where(x => (string)x["DefinitionId"] == "entrepreneur.round.P.7" && x["LocalState"] is JObject).ToList())
            {
                var properties = item["LocalState"]["Properties"] as JArray; if (properties == null) continue;
                foreach (var property in properties.OfType<JObject>().Where(x => (string)x["Name"] == "sourceSelectionReceipt").ToList()) { property.Remove(); changed++; }
            }
            Assert.That(changed, Is.GreaterThan(0), "必须真正命中快照或StateAfter原始来源字段");
            f.Artifacts.WriteLabeledText("损坏归档原串", archive.ToString());
            var result = HostRecoveryService.RecoverJson(archive.ToString(), f.Registry); Assert.That(result.Succeeded, Is.False); f.Artifacts.WriteLabeledText("Host拒绝诊断", result.Diagnostic);
        }
    }
}
