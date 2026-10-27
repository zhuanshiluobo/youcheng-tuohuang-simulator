using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>真实T正式正文的隔离规则验证；本轮只交源码，须单一整合后统一运行。</summary>
    public sealed class FifthNumberContentTests
    {
        private IsolatedTestArtifacts artifacts;
        private FifthNumberContentTestFixture f;
        [SetUp] public void Begin() { artifacts = IsolatedTestArtifacts.BeginCase(); f = null; }
        [TearDown] public void End() { f?.Snapshot("第五号规则用例最终状态"); artifacts?.Finish(); }

        [Test] public void FormalIdentityRetainsOneSetupAndTenIndependentImmediateHandlers()
        {
            f = new FifthNumberContentTestFixture(artifacts);
            var t = f.Pack.Definitions.Single(d => d.DefinitionId == FifthNumberContentTestFixture.AbilityDefinition);
            var model = t.Data["entrepreneur"].ToObject<EntrepreneurDefinition>();
            Assert.That(model.Components.Single(c => c.EntryId == "entrepreneur.initial_markers").Quantity, Is.EqualTo(2));
            Assert.That(model.SetupHandlerRefs, Is.Empty, "初始两枚由既有设置组件交付，不能再接一次Load。");
            var faces = model.RoundGroups.SelectMany(g => g.Candidates).ToList();
            Assert.That(faces.Count, Is.EqualTo(8)); Assert.That(faces.Select(x => x.EntityDefinitionId).Distinct().Count(), Is.EqualTo(4));
            Assert.That(faces.Sum(x => x.ImmediateHandlerRefs.Count), Is.EqualTo(10));
            foreach (var face in faces)
            foreach (string handler in face.ImmediateHandlerRefs)
                Assert.That(f.Registry.AbilityDeclarations.Count(d => d.DefinitionId == face.EntityDefinitionId && d.ActiveFaceId == face.FaceId && d.Kind == "setup" && d.HandlerRef == handler), Is.EqualTo(1));
            Assert.That(f.Registry.QuerySources(f.State, 1).Any(s => s.ActiveFaceId.Length > 0), Is.False, "预览及未选面不启用来源。");
            Assert.That(t.AbilitySources.SelectMany(s => s.Actions), Is.Empty, "T没有快速或特殊入口。");
            var source = t.AbilitySources.Single(s => s.Kind == "cleanup");
            Assert.That(source.RepeatPolicy, Is.EqualTo("unbounded"));
            Assert.That(t.AbilitySources.Single(s => s.Kind == "event").ObservedEvents, Is.EqualTo(new[] { "InfluenceRemoved" }));
        }

        [TestCase("T07")][TestCase("T01")]
        public void ImmediatePureOriginiumIsAutomaticAndOnlyBenefitsTheOwner(string face)
        {
            f = new FifthNumberContentTestFixture(artifacts);
            int own = f.State.FindPlayer(1).Resources.PureOriginium, other = f.State.FindPlayer(2).Resources.PureOriginium;
            f.ChooseFace(face);
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(own + 1));
            Assert.That(f.State.FindPlayer(2).Resources.PureOriginium, Is.EqualTo(other));
            Assert.That(f.Group(face == "T07" ? 1 : 7).ImmediateEntries.Single().Status, Is.EqualTo("completed"));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Where(q => q.Status == "open").All(q => q.AnsweringPlayerId != 1), Is.True);
        }

        [TestCase("T08", 1)][TestCase("T02", 2)]
        public void PlacementIconsHaveSeparateRealNodesAndRecheckTheMap(string face, int count)
        {
            f = new FifthNumberContentTestFixture(artifacts); int before = f.State.Map.Influences.Count(i => i.PlayerId == 1);
            f.ChooseFace(face); var ids = new List<string>();
            for (int n = 0; n < count; n++)
            {
                var request = f.PlacementRequest(); ids.Add(request.OwnerEffectId);
                Assert.That(request.AllowDecline, Is.False);
                f.AnswerCandidate(request, request.CandidateIds.First());
            }
            Assert.That(ids.Distinct().Count(), Is.EqualTo(count));
            Assert.That(f.State.Map.Influences.Count(i => i.PlayerId == 1), Is.EqualTo(before + count));
            var nodes = ids.Select(id => f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)).ToList();
            Assert.That(nodes.All(n => n.Status == EffectNodeStatus.Completed), Is.True);
            if (count == 2) Assert.That(nodes[0].ParentEffectId, Is.EqualTo(nodes[1].ParentEffectId));
        }

        [Test] public void T03EmptyRemovalStillGainsTwoAndDispatchDeclineDoesNotCancelTheOtherImmediate()
        {
            f = new FifthNumberContentTestFixture(artifacts); f.ClearOwnMap();
            int score = f.State.FindPlayer(1).Score, pure = f.State.FindPlayer(1).Resources.PureOriginium;
            f.ChooseFace("T03"); var dispatch = f.Open("operator.dispatch.choose");
            Assert.That(dispatch.CandidateIds.Count, Is.EqualTo(3));
            f.Answer(dispatch, NormalizedValue.CreateBoolean(false));
            Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score - 1), "失2分保留，主动放弃特派按原Flow得1分。");
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + 2));
            Assert.That(f.Group(5).ImmediateEntries.Select(e => e.Status), Is.EqualTo(new[] { "completed", "completed" }));
            var remove = f.State.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.SelectInfluence && n.PlayerId == 1);
            Assert.That(remove.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Number(remove.NormalizedResult, "completedCount"), Is.Zero);
        }

        [Test] public void T03WithATargetWaitsForTheRealDestinationBeforeItsSiblingGain()
        {
            f = new FifthNumberContentTestFixture(artifacts); f.ClearOwnMap(); f.Place();
            int pure = f.State.FindPlayer(1).Resources.PureOriginium;
            f.ChooseFace("T03"); f.Answer(f.Open("operator.dispatch.choose"), NormalizedValue.CreateBoolean(false));
            var remove = f.Open(); Assert.That(remove.AllowDecline, Is.False);
            f.AnswerCandidate(remove, remove.CandidateIds.First());
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure));
            Assert.That(f.StoreRequest().AnsweringPlayerId, Is.EqualTo(1));
            f.Answer(f.StoreRequest(), NormalizedValue.CreateString("store"));
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + 2));
            Assert.That(f.Group(5).ImmediateEntries[1].Status, Is.EqualTo("completed"));
        }

        [TestCase(false)][TestCase(true)]
        public void T04DispatchBranchesKeepTheSecondImmediateAndItsTwoSiblingPlacements(bool accept)
        {
            f = new FifthNumberContentTestFixture(artifacts); int before = f.State.Map.Influences.Count(i => i.PlayerId == 1);
            f.ChooseFace("T04"); var dispatch = f.Open("operator.dispatch.choose");
            if (accept)
            {
                f.AnswerCandidate(dispatch, dispatch.CandidateIds.First());
                var returned = f.Open("operator.dispatch.return"); f.AnswerCandidate(returned, returned.CandidateIds.First());
            }
            else f.Answer(dispatch, NormalizedValue.CreateBoolean(false));
            string first = f.PlacementRequest().OwnerEffectId; f.AnswerCandidate(f.PlacementRequest(), f.PlacementRequest().CandidateIds.First());
            string second = f.PlacementRequest().OwnerEffectId; f.AnswerCandidate(f.PlacementRequest(), f.PlacementRequest().CandidateIds.First());
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(f.State.Map.Influences.Count(i => i.PlayerId == 1), Is.EqualTo(before + 2));
            Assert.That(f.Group(5).ImmediateEntries.Select(e => e.Status), Is.EqualTo(new[] { "completed", "completed" }));
        }

        [TestCase("T05", "PureOriginium", 1)][TestCase("T06", "GoldVoucher", 18)]
        public void AllPlayersUpgradeTheSameEnterpriseAfterTheOwnerReward(string face, string resource, int amount)
        {
            f = new FifthNumberContentTestFixture(artifacts);
            int before = resource == "PureOriginium" ? f.State.FindPlayer(1).Resources.PureOriginium : f.State.FindPlayer(1).Resources.GoldVoucher;
            f.ChooseFace(face);
            int current = resource == "PureOriginium" ? f.State.FindPlayer(1).Resources.PureOriginium : f.State.FindPlayer(1).Resources.GoldVoucher;
            Assert.That(current, Is.EqualTo(before + amount));
            var choice = f.Open(); string enterprise = choice.CandidateIds.First();
            var query = new EnterpriseStateQuery(f.Registry.EnterpriseDefinitions);
            string definition = f.State.ItemZones.Items.Single(i => i.ItemId == enterprise).DefinitionId;
            int maximum = f.Registry.EnterpriseDefinitions.FindEnterprise(definition).Levels.Max(l => l.Level);
            int one = query.GetLevel(f.State, 1, enterprise), two = query.GetLevel(f.State, 2, enterprise);
            f.AnswerCandidate(choice, enterprise); f.ResolveOpenRequests();
            Assert.That(query.GetLevel(f.State, 1, enterprise), Is.EqualTo(Math.Min(maximum, one + 1)));
            Assert.That(query.GetLevel(f.State, 2, enterprise), Is.EqualTo(Math.Min(maximum, two + 1)));
            var upgrade = f.State.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == EnterpriseEffectExecutor.UpgradeAllPlayersTypeId);
            Assert.That(upgrade.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.Group(3).ImmediateEntries.Single().Status, Is.EqualTo("completed"));
        }

        [TestCase("T05")][TestCase("T06")]
        public void OwnersMaximumLevelDoesNotFilterTheEnterpriseForTheOtherPlayer(string face)
        {
            f = new FifthNumberContentTestFixture(artifacts);
            f.FinishOtherPlayer(); f.ResolveOpenRequests();
            var query = new EnterpriseStateQuery(f.Registry.EnterpriseDefinitions);
            string enterprise = query.GetEnterpriseItemIds(f.State).First();
            string definition = f.State.ItemZones.Items.Single(i => i.ItemId == enterprise).DefinitionId;
            int maximum = f.Registry.EnterpriseDefinitions.FindEnterprise(definition).Levels.Max(l => l.Level);
            for (int i = 0; i < maximum && query.GetLevel(f.State, 1, enterprise) < maximum; i++)
            {
                f.Run(EnterpriseEffectExecutor.UpgradeSpec(1, enterprise, 1)); f.ResolveOpenRequests();
            }
            Assert.That(query.GetLevel(f.State, 1, enterprise), Is.EqualTo(maximum));
            int other = query.GetLevel(f.State, 2, enterprise); f.ChooseFace(face);
            var choice = f.Open(); Assert.That(choice.CandidateIds, Does.Contain(enterprise));
            f.AnswerCandidate(choice, enterprise); f.ResolveOpenRequests();
            Assert.That(query.GetLevel(f.State, 1, enterprise), Is.EqualTo(maximum));
            Assert.That(query.GetLevel(f.State, 2, enterprise), Is.EqualTo(Math.Min(maximum, other + 1)));
            Assert.That(f.Group(3).ImmediateEntries.Single().Status, Is.EqualTo("completed"));
        }

        [TestCase("t", false)][TestCase("dorothy", false)][TestCase("supply", false)]
        [TestCase("t", true)][TestCase("dorothy", true)][TestCase("supply", true)]
        public void IndependentRealStorageChoicesResolveExactlyOneDestination(string destination, bool replace)
        {
            f = new FifthNumberContentTestFixture(artifacts, dorothy: true); f.ActivateDorothy();
            int t = f.Stored; string item = f.Place(); f.Depart(item, replace: replace);
            f.CompleteDestinations(destination);
            var instance = f.State.ItemZones.Items.Single(i => i.ItemId == item);
            var departure = f.State.EffectRuntime.InfluenceDepartures.Last(d => d.ItemId == item);
            Assert.That(departure.Status, Is.EqualTo(destination == "supply" ? "returned" : "stored"));
            if (destination == "t") Assert.That(instance.ZoneId, Is.EqualTo(f.Storage.ZoneId));
            else if (destination == "dorothy") Assert.That(f.State.ItemZones.Zones.Single(z => z.ZoneId == instance.ZoneId).ParentId,
                Is.EqualTo(f.State.ItemZones.Items.Single(i => i.DefinitionId == "operator.dorothy" && i.OwnerSubject.PlayerId == 1).ItemId));
            else
            {
                Assert.That(departure.DestinationZoneId, Is.EqualTo(instance.OriginalSupplyZoneId));
                if (instance.ZoneId != instance.OriginalSupplyZoneId)
                {
                    Assert.That(replace, Is.True);
                    Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence &&
                        InfluenceDepartureProtocol.IsWithin(f.State, n.EffectId, departure.SourceEffectId) &&
                        n.PositionChanges.Any(p => p.ItemId == item && p.From.ZoneId == instance.OriginalSupplyZoneId && p.To.ZoneId == instance.ZoneId)), Is.True);
                    Assert.That(f.State.Map.Influences.Any(i => i.InfluenceId == item), Is.True);
                }
            }
            Assert.That(f.Stored, Is.EqualTo(t + (destination == "t" ? 1 : 0)));
            Assert.That(f.State.EffectRuntime.DeferredResponses.Count(r => r.DepartureId == departure.DepartureId && r.Status == DeferredResponseStatus.Stored), Is.EqualTo(destination == "supply" ? 0 : 1));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "InfluenceRemoved" && Text(e.Payload, "departureId") == departure.DepartureId), Is.EqualTo(1));
        }

        [Test] public void SixMarkersUseTwoFormalInvocationsWithDifferentMandatoryRewards()
        {
            f = new FifthNumberContentTestFixture(artifacts);
            for (int i = 0; i < 4; i++) f.CollectOne();
            Assert.That(f.Stored, Is.EqualTo(6)); f.OpenCleanup();
            int pure = f.State.FindPlayer(1).Resources.PureOriginium, influence = f.State.Map.Influences.Count(i => i.PlayerId == 1);
            int departures = f.State.EffectRuntime.InfluenceDepartures.Count;
            f.InvokeCleanup("qy012.exchange.first"); f.Answer("continue");
            var choice = f.Open(); Assert.That(choice.AllowDecline, Is.False);
            Assert.That(choice.CandidateIds, Is.EquivalentTo(new[] { "place", "resource" }));
            Assert.That(f.Stored, Is.EqualTo(3)); f.AnswerCandidate(choice, "place");
            f.AnswerCandidate(f.PlacementRequest(), f.PlacementRequest().CandidateIds.First());
            f.InvokeCleanup("qy012.exchange.second"); f.Answer("continue"); f.Answer("resource");
            Assert.That(f.Stored, Is.Zero); Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + 1));
            Assert.That(f.State.Map.Influences.Count(i => i.PlayerId == 1), Is.EqualTo(influence + 1));
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Count, Is.EqualTo(departures), "牌上支付不产生地图离场或重收。");
            var invocations = CleanupSourceService.Invocations(f.State).Where(i => i.SourceRef.ItemId == f.Card).ToList();
            Assert.That(invocations.Count, Is.EqualTo(2)); Assert.That(invocations.Select(i => i.InvocationId).Distinct().Count(), Is.EqualTo(2));
            var transfers = f.State.EffectRuntime.EffectNodes.Where(n => n.TransferBatchRequest?.ProtocolVersion == 3 &&
                n.PositionChanges.Count(c => c.From.ZoneId == f.Storage.ZoneId) == 3).ToList();
            Assert.That(transfers.Count, Is.EqualTo(2), "两笔独立兑换分别提交一笔api3 token事务。");
            foreach (var transfer in transfers)
            {
                Assert.That(Number(transfer.NormalizedResult, "movedCount"), Is.EqualTo(3));
                Assert.That(ItemTransferService.TryValidateTokenState(f.State, transfer, out string reason), Is.True, reason);
            }
            Assert.That(f.Registry.QuerySources(f.State, 1).Any(s => s.ItemId == f.Card && s.AbilityId == "entrepreneur.T09.departure"), Is.True);
            Assert.That(f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.SourceItemId == f.Card).IsEnabled, Is.False);
        }

        [Test] public void InsufficientInventoryRejectsTheFormalEntryWithoutTransferringAnyMarker()
        {
            f = new FifthNumberContentTestFixture(artifacts); f.OpenCleanup();
            var option = f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.SourceItemId == f.Card);
            Assert.That(option.IsEnabled, Is.False);
            string[] items = f.State.ItemZones.Items.Where(i => i.ZoneId == f.Storage.ZoneId).Select(i => i.ItemId).OrderBy(s => s).ToArray();
            int transfers = f.State.EffectRuntime.EffectNodes.Sum(n => n.PositionChanges.Count);
            var command = CleanupSourceCommand.Create(1, "qy012.insufficient", option.CleanupRequest, f.State.EffectRuntime.StateRevision);
            var rejected = new InvokeSettlementOptionCommandHandler(f.Registry).Handle(f.State, command);
            Assert.That(rejected.Succeeded, Is.False);
            Assert.That(f.State.ItemZones.Items.Where(i => i.ZoneId == f.Storage.ZoneId).Select(i => i.ItemId).OrderBy(s => s), Is.EqualTo(items));
            Assert.That(CleanupSourceService.Invocations(f.State).Any(i => i.SourceRef.ItemId == f.Card), Is.False);
            Assert.That(f.State.EffectRuntime.EffectNodes.Sum(n => n.PositionChanges.Count), Is.EqualTo(transfers));
        }

        [Test] public void DecliningBeforePaymentKeepsTheStableSourceAndAllowsALaterInvocation()
        {
            f = new FifthNumberContentTestFixture(artifacts); f.CollectOne(); f.OpenCleanup();
            string option = f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.SourceItemId == f.Card).OptionId;
            f.InvokeCleanup("qy012.decline.first"); f.Answer("decline"); Assert.That(f.Stored, Is.EqualTo(3));
            Assert.That(f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.SourceItemId == f.Card).OptionId, Is.EqualTo(option));
            int pure = f.State.FindPlayer(1).Resources.PureOriginium;
            f.InvokeCleanup("qy012.decline.retry"); f.Answer("continue"); f.Answer("resource");
            Assert.That(f.Stored, Is.Zero); Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + 1));
            Assert.That(CleanupSourceService.Invocations(f.State).Count(i => i.SourceRef.ItemId == f.Card), Is.EqualTo(2));
        }

        [TestCase(0, 0)][TestCase(3, 0)][TestCase(4, 1)][TestCase(7, 1)][TestCase(8, 2)]
        public void T01AddsItsBonusAndStillKeepsTheDefaultResourceScore(int pure, int extra)
        {
            f = new FifthNumberContentTestFixture(artifacts); f.ChooseFace("T01"); f.FinishOtherPlayer();
            // 明确的隔离计分输入；不计正常对局覆盖，也不写真实存档。
            var resources = f.State.FindPlayer(1).Resources; resources.PureOriginium = pure;
            resources.Originium = resources.OriginiumShard = resources.Iron = 0;
            f.StartFinal(); f.Pump(); var player = f.State.FinalScoring.Session.Players.Single(p => p.PlayerId == 1);
            Assert.That(player.SpecialScore, Is.EqualTo(extra)); Assert.That(player.ResourceScore, Is.EqualTo(pure));
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure));
            Assert.That(f.State.FinalScoring.IsResolved, Is.True);
        }

        [Test] public void T02UsesTheUnifiedControlSnapshotWithoutChangingRegionValues()
        {
            f = new FifthNumberContentTestFixture(artifacts); f.ChooseFace("T02");
            for (int i = 0; i < 2; i++) f.AnswerCandidate(f.PlacementRequest(), f.PlacementRequest().CandidateIds.First());
            f.FinishOtherPlayer(); f.StartFinal(); f.Pump();
            var session = f.State.FinalScoring.Session; int regions = session.Control.Regions.Count(r => r.ControllerPlayerId == 1);
            Assert.That(session.Players.Single(p => p.PlayerId == 1).SpecialScore, Is.EqualTo(regions * 2));
            Assert.That(session.Control.Regions.SelectMany(r => r.ValueModifiers).Any(m => m.Source.AbilityId == "entrepreneur.face.T02"), Is.False);
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(1));
        }
    }
}
