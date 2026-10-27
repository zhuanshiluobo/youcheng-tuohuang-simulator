using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 正式Lua/真实SourceUse及原语提交的隔离组合；所有初始资源/实体前提沿夹具留证，正常覆盖0。
    public sealed class ContentIntegrationR2PaulCommitTests
    {
        private static void MapServices(PaulContentTestFixture f)
        {
            var map = new MapQueryService(f.Map); var influence = f.Registry.InfluenceService;
            RouteResourceSelectionEffectExecutor.Register(f.Registry, map);
            ResourceCollectionEffectExecutor.Register(f.Registry);
            var costs = new TravelCostService(map);
            CityMoveEffectExecutor.Register(f.Registry, map, influence, new CityMovementService(map, influence, costs), costs, new ResourceTokenService());
        }
        [TestCase(false)][TestCase(true)]
        public void P04FormalGrantCommitsNormalAndExtraHarvestOnceForDistinctFreeLocations(bool extra)
        {
            using var f = new PaulContentTestFixture(); f.FinishPreparation(); f.Choose(3, "P04"); MapServices(f); f.DockCity(f.Location);
            f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
            f.Place(f.OtherLocation, 1, type: "entrepreneur.gang_marker");
            f.State.Phase = GamePhase.ResourceCollection;
            f.State.FindPlayer(1).Resources.GoldVoucher = 0; f.State.FindPlayer(1).ResourceCollectionStartGoldVoucher = 0;
            var before = f.State.FindPlayer(1).Resources.Clone();
            string root = f.Run(extra ? EffectSpec.Create(ResourceCollectionEffectExecutor.TypeId,
                Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "scope", NormalizedValue.CreateString("extra")), 1)
                : RouteResourceSelectionEffectExecutor.CreateHarvest(1, false, "qy007.r2.formal.p04"));
            Assert.That(JsonConvert.SerializeObject(f.State.FindPlayer(1).Resources), Is.EqualTo(JsonConvert.SerializeObject(before)));
            f.Restore(extra ? "正式P04额外采集待提交" : "正式P04正常采集待提交");
            var request = f.Request(root, "route_resource.selection");
            string scope = Text(request.PromptParameters, "scopeKey");
            string[] locations = { f.Location, f.OtherLocation };
            var ids = locations.Select(id => RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Location, id)).ToList();
            ids.Add(RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Commit));
            var answer = NormalizedValue.CreateArray(ids.Select(id => NormalizedValue.CreateStableReference("candidate", id)).ToList());
            Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, answer, out string reason), Is.True, reason); f.Pump();
            foreach (ResourceType type in Enum.GetValues(typeof(ResourceType)))
            {
                int gain = f.State.Map.ResourceTokens.Where(t => locations.Contains(t.LocationId) && t.ResourceType == type).Sum(t => t.Amount);
                Assert.That(f.State.FindPlayer(1).Resources.Get(type), Is.EqualTo(before.Get(type) + gain));
            }
            Assert.That(f.State.FindPlayer(1).HasCollectedResourcesThisRound, Is.EqualTo(!extra));
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            string snapshot = JsonConvert.SerializeObject(f.State);
            Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, f.State.EffectRuntime.StateRevision, answer, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(snapshot));
            f.Restore("正式P04采集已实际交付且重复提交拒绝");
        }

        [TestCase(false)][TestCase(true)]
        public void P06FormalGrantRejectsMixedPointThenCommitsAnAllowedCityMove(bool free)
        {
            using var f = new PaulContentTestFixture(); f.FinishPreparation(); f.Choose(5, "P06"); MapServices(f); f.DockCity(f.Location);
            f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker"); f.Place(f.OtherLocation, 1);
            f.State.Phase = GamePhase.ActionRound1; f.State.ActionRound = 1; f.State.CurrentPlayerId = 1;
            f.State.FindPlayer(1).Resources.OriginiumShard = 100;
            f.Artifacts.WriteLabeledText("P06移动隔离资源前提", "仅为正常/免费移动的规则定位预置100碎片；既有城市位置来自DockCity隔离前提，实际后续移动走正式CityMove。");
            string start = f.State.FindPlayer(1).CityLocationId; int shards = f.State.FindPlayer(1).Resources.OriginiumShard;
            string rejected = f.Run(CityMoveEffectSpecFactory.Move(1, f.OtherLocation, free, false, "qy007.r2.formal.p06.banned"));
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == rejected).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(f.State.FindPlayer(1).CityLocationId, Is.EqualTo(start));
            Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(shards));
            string root = f.Run(CityMoveEffectSpecFactory.Move(1, "", free, false, "qy007.r2.formal.p06.move"));
            var target = f.Request(root, CityMoveEffectExecutor.TargetInteractionTypeId);
            Assert.That(target.CandidateIds, Does.Not.Contain(f.OtherLocation));
            string selected = target.CandidateIds.First();
            f.Restore("正式P06合法移动候选待答"); f.Answer(f.Request(root, CityMoveEffectExecutor.TargetInteractionTypeId), selected);
            for (int guard = 0; guard < 20; guard++)
            {
                var request = f.Open.LastOrDefault(q => f.Within(q.OwnerEffectId, root));
                if (request == null) break;
                string type = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId).EffectTypeId;
                if (type == ResourceEffectTypeIds.Pay) f.Answer(request, "pay");
                else
                {
                    // 正式P09会响应CityMoved并放置一枚；必须完成这条真实响应，不能把它误当付款请求。
                    Assert.That(request.InteractionTypeId == "effect.choice.execute" || type == InfluenceEffectTypeIds.PlaceInfluence, Is.True, type);
                    f.Answer(request, request.CandidateIds.Contains("gang") ? "gang" : request.CandidateIds.First());
                }
            }
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(1).CityLocationId, Is.EqualTo(selected));
            Assert.That(selected, Is.Not.EqualTo(f.OtherLocation));
            int paid = f.State.EffectRuntime.EffectNodes.Where(n => f.Within(n.EffectId, root) && n.Payment?.IsCommitted == true).Sum(n => n.Payment.PaymentCost.OriginiumShard);
            Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(shards - paid));
            if (free) Assert.That(paid, Is.Zero);
            else Assert.That(paid, Is.GreaterThan(0));
            f.Restore("正式P06城市位置与真实付款提交后");
        }
    }

    public sealed class ContentIntegrationR2AgnesBatchTests
    {
        [Test]
        public void A06FormalDepartureSourceUsesOneCompleteMixedOwnerBatchAndPaysExactQuantity()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(3, "A06");
            f.Run(ResourceEffectSpecFactory.Gain(1, ResourceType.GoldVoucher, 100, "qy007.r2.isolated.funds"));
            var own = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var slots = f.Registry.InfluenceService.GetLegalPlacementSlotIds(f.State, 1);
                string slot = slots.First(); f.Run(InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
                own.Add(f.State.Map.Influences.Single(x => x.SlotId == slot).InfluenceId);
            }
            string otherSlot = f.Registry.InfluenceService.GetLegalPlacementSlotIds(f.State, 2).First();
            f.Run(InfluenceEffectSpecFactory.PlaceInfluence(2, otherSlot));
            string other = f.State.Map.Influences.Single(x => x.SlotId == otherSlot).InfluenceId;
            int before = f.Stored, gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            var effects = own.Select(id => InfluenceEffectSpecFactory.RemoveInfluence(1, id)).ToList();
            effects.Add(InfluenceEffectSpecFactory.RemoveInfluence(1, other));
            f.Run(InfluenceDepartureBatchExecutor.Create(1, effects));
            // 对手真实收容机会属于批次封口之前，不能略过它就断言已封口。
            while (f.State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open" && q.AnsweringPlayerId == 2))
            { Assert.That(f.Open.AnsweringPlayerId, Is.EqualTo(2)); Assert.That(f.Open.CandidateIds, Does.Contain("decline")); f.Answer("decline"); }
            var batch = f.State.EffectRuntime.InfluenceDepartureBatches.Last(b => b.DepartureIds.Count == 4);
            Assert.That(batch.Sealed, Is.True); Assert.That(batch.ProducerSteps, Is.EquivalentTo(batch.FinishedSteps));
            var uses = f.State.EffectRuntime.AbilitySourceUses.Where(u => u.AbilityId == "entrepreneur.A06.departure").ToList();
            Assert.That(uses, Has.Count.EqualTo(1));
            f.Restore("正式A06完整批次封口与数量待答");
            bool sawCompleteN = false;
            for (int guard = 0; guard < 20 && f.State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"); guard++)
            {
                var q = f.Open;
                if (q.AnsweringPlayerId == 2) { Assert.That(q.CandidateIds, Does.Contain("decline")); f.Answer("decline"); continue; }
                var owner = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == q.OwnerEffectId);
                if (Number(owner.ContinuationContext, "n") == 3)
                {
                    sawCompleteN = true; Assert.That(q.CandidateIds, Does.Contain("3")); f.Answer("3");
                }
                else { Assert.That(q.CandidateIds, Does.Contain("continue")); f.Answer("continue"); }
            }
            Assert.That(sawCompleteN, Is.True); Assert.That(f.Stored, Is.EqualTo(before + 3));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - 6));
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Count(d => d.BatchId == batch.BatchId && d.OriginalSubject.PlayerId == 1), Is.EqualTo(3));
            f.Restore("正式A06完整n选择k与真实付款交付完成"); f.Pump(); Assert.That(f.Stored, Is.EqualTo(before + 3));
        }
    }

    public sealed class ContentIntegrationR2MartinPaymentTests : MartinContentTestFixture
    {
        private static ResourceSet Allocate(ResourceSet remaining, int units)
        {
            var allocation = new ResourceSet();
            foreach (var type in new[] { ResourceType.Originium, ResourceType.OriginiumShard, ResourceType.Iron })
            { int n = Math.Min(remaining.Get(type), units); remaining.Set(type, remaining.Get(type) - n); allocation.Set(type, n); units -= n; }
            Assert.That(units, Is.Zero); return allocation;
        }
        [Test]
        public void FormalM10AwardsThenRealPassengerAndM01JointlyPayOneActualBuild()
        {
            var state = State("entrepreneur.round.M.5", extraRecipe: new[] {
                new ItemRecipeEntry { EntryId = "qy007.passenger", DefinitionId = "operator.passenger", TypeId = "core.operator_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" } });
            state.Round = 5;
            // 隔离选面适配仍产生真实Choice/CommitSelectedFace；由玩家2的正式M10向本人以外发奖。
            Run(state, EffectSpec.Create("qy016.isolated.select_face", Object("definitionId", NormalizedValue.CreateString("entrepreneur.round.M.5")), 2));
            Answer(state, NormalizedValue.CreateString("entrepreneur.candidate.M10"));
            Run(state, EffectSpec.Create("qy016.fixture.setup", Object("abilityId", NormalizedValue.CreateString("entrepreneur.M10.setup")), 2));
            Assert.That(state.ItemZones.Items.Count(i => i.DefinitionId == "entrepreneur.award.M01"), Is.EqualTo(2));
            var reward = state.ItemZones.Items.Single(i => i.DefinitionId == "entrepreneur.award.M01" && i.OwnerSubject.PlayerId == 1);
            Assert.That(ContentLifecycleEffectExecutor.IsHeld(state, reward, 1), Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Where(n => n.RewardGrant != null).Select(n => n.RewardGrant.RecipientPlayerId), Is.EquivalentTo(new[] { 1, 3 }));
            Assert.That(state.EffectRuntime.AbilitySourceUses.Any(u => u.PlayerId == 2 && u.AbilityId == "entrepreneur.M10.setup"), Is.True);
            state = Recover(state, "正式M10真实发奖至本人以外持有后");
            string passenger = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.passenger" && i.ControllerSubject.PlayerId == 1).ItemId;
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, NormalizedValue.CreateString(passenger));
            Run(state, CharacterLifecycleService.CreateUseSpec(1, passenger, "tactic"));
            Assert.That(Registry.QuerySources(state, 1, queryKind: ResourcePaymentSourceService.QueryKind).Any(s => s.AbilityId == "operator.passenger.payment"), Is.False);
            Answer(state, NormalizedValue.CreateString("2"));
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, passenger, "operator.passenger.tactic"), Is.True);
            state = Recover(state, "正式M10发奖与异客真实装载两枚后");
            string definition = state.ContentDefinitions.Facilities.Where(d => !d.Unique && !d.HasEntryEffect &&
                d.ResourceCost.Originium + d.ResourceCost.OriginiumShard + d.ResourceCost.Iron >= 3).OrderBy(d => d.FacilityId, StringComparer.Ordinal).First().FacilityId;
            var card = Card(state, definition);
            var spec = FacilityBuildEffectSpecFactory.Build(1, card.ItemId, 0, "resources", false, "qy007.r2.formal.m10.payment", false, card.ZoneId, "deck");
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "slotObservation", Value = FacilityBuildJournal.EncodeSlot(Registry.FacilityBuildQueries.ResolveSlots(state, 1, card.ItemId).Single(c => c.SlotIndex == 0)) });
            spec.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            var build = Run(state, spec); string paymentId = Open(state).OwnerEffectId;
            var payment = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == paymentId);
            var rules = ResourcePaymentSourceService.Query(state, Registry, payment, payment.Payment.PaymentCost);
            var m = rules.Single(r => r.RuleId == "martin.concession"); var p = rules.Single(r => r.RuleId == "passenger.stored_markers");
            Assert.That(m.MaxUnits, Is.EqualTo(1)); Assert.That(p.MaxUnits, Is.EqualTo(2));
            var markerIds = state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.ParentId == passenger && z.LocalZoneId == "markers")).Select(i => i.ItemId).ToArray();
            var remaining = payment.Payment.PaymentCost.Clone();
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(state, Registry, payment, m.SourceRef.SourceKey, m.RuleId, payment.Payment.PaymentGeneration, 0, Allocate(remaining, 1), Array.Empty<string>(), "", out string reason), Is.True, reason);
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(state, Registry, payment, p.SourceRef.SourceKey, p.RuleId, payment.Payment.PaymentGeneration, 0, Allocate(remaining, 2), markerIds, "", out reason), Is.True, reason);
            Assert.That(ResourcePaymentPlanService.TryBuild(state, Registry, payment, out var plan, out reason), Is.True, reason);
            var before = state.FindPlayer(1).Resources.Clone(); state = Recover(state, "正式M10奖励和异客两来源联合付款草稿");
            Answer(state, NormalizedValue.CreateString("pay"));
            payment = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == paymentId);
            Assert.That(payment.Payment.IsCommitted, Is.True); Assert.That(payment.Payment.SourceReceipts, Has.Count.EqualTo(2));
            foreach (var type in PaymentResources.Types) Assert.That(state.FindPlayer(1).Resources.Get(type), Is.EqualTo(before.Get(type) - plan.FinalCost.Get(type)));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == passenger).ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == reward.ItemId).ZoneId, Is.EqualTo(FindZone(state, "rewards", 1).ZoneId));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == build.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            state = Recover(state, "正式M10奖励与异客同一Build真实付款建设后");
            string digest = RuleStateDigestService.Compute(state); Assert.That(new EffectTreeExecutor(state, Registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(digest));
        }
    }
    public sealed class ContentIntegrationR2MartinFinalTests : MartinContentTestFixture
    {
        [TestCase("M07", 2, 2)] [TestCase("M08", 4, 0)]
        public void SelectedFormalFaceRunsRealFinalSessionAndResourcesFollowSpecialIncome(string face, int special, int ironGain)
        {
            var state = State("entrepreneur.round.M.3");
            state.LaunchPlayerOrder = new List<int> { 1, 2, 3 }; state.Round = 3;
            state.FindPlayer(1).Resources = new ResourceSet { Iron = 99 };
            Artifacts.WriteLabeledText("M终局隔离前提", "只为规则定位布置两格各两层、99异铁及选面轮号；使用真实设施实例/位置转移和正式选面收据，不伪造计分结果。末轮各收尾与完整终局实际执行，正常覆盖0。");
            Position(state, Card(state, "reserve_001"), Cell(state, 0).CellZoneId); Overlay(state, Card(state, "building_015"), 0);
            Position(state, Card(state, "building_016"), Cell(state, 1).CellZoneId); Overlay(state, Card(state, "building_017"), 1);
            SelectFace(state, "entrepreneur.round.M.3", face);
            var sources = Registry.QuerySources(state, 1, "event", "FinalScoringStarted");
            Assert.That(sources.Select(s => s.AbilityId), Is.EquivalentTo(new[] { "entrepreneur.face." + face }));
            var rounds = new RoundExecutionService(Registry);
            void Pump()
            {
                var result = rounds.CreateCommandExecutor(state).RunUntilQuiescent();
                if (result.Faulted) Artifacts.WriteLabeledText("M正式终局首次故障", JsonConvert.SerializeObject(state));
                Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            }
            string root = IsolatedFinalRoundFlow.CreatePendingFinal(state, rounds, Pump);
            int iron = state.FindPlayer(1).Resources.Iron;
            state = Recover(state, "M真实末轮收尾已结清而终局未推进");
            var engine = rounds.CreateCommandExecutor(state); bool reached = false;
            for (int guard = 0; guard < 1000; guard++)
            {
                if (state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).FlowStage == YC.Domain.Scoring.FinalScoringSessionService.ResourceSnapshot)
                { reached = true; break; }
                Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
            }
            Assert.That(reached, Is.True); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron + ironGain));
            state = Recover(state, "M特殊收益已真实交付而默认资源快照未结算"); Pump();
            var ledger = state.FinalScoring.Session.Players.Single(p => p.PlayerId == 1);
            Assert.That(state.FinalScoring.IsResolved, Is.True); Assert.That(ledger.SpecialScore, Is.EqualTo(special));
            Assert.That(ledger.Resources.Iron, Is.EqualTo(iron + ironGain)); Assert.That(ledger.ResourceScore, Is.GreaterThan(0));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.AbilitySourceUses.Count(u => u.AbilityId == "entrepreneur.face." + face), Is.EqualTo(1));
            int score = state.FindPlayer(1).Score;
            state = Recover(state, "M真实终局已完成"); Pump();
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score)); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron + ironGain));
        }
    }

    // 协议调度回归：一次路径只改私有repeatPolicy；不把它计作T09官方卡面规则。
    public sealed class ContentIntegrationR2CleanupSchedulingTests
    {
        [TestCase(false)] [TestCase(true)]
        public void RealOnceAndUnboundedCleanupFinishBeforeTheFollowingSystemStep(bool repeated)
        {
            var artifacts = IsolatedTestArtifacts.BeginCase(); FifthNumberContentTestFixture f = null;
            try
            {
                f = new FifthNumberContentTestFixture(artifacts, onceCleanup: !repeated); f.CollectOne();
                Assert.That(f.Stored, Is.EqualTo(3)); f.OpenCleanup(); int pure = f.State.FindPlayer(1).Resources.PureOriginium;
                var system = f.State.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == f.CleanupRoot && n.EffectTypeId == "effect.player.marker.operate");
                string systemId = system.EffectId; Assert.That(system.Status, Is.EqualTo(EffectNodeStatus.Created));
                if (repeated) f.InvokeCleanup("qy007.r2.scheduling.repeat");
                else
                {
                    var option = f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.SourceItemId == f.Card);
                    Assert.That(f.Engine.TryInvokeCharacterSettlementOption(1, option.OptionId, "", f.State.EffectRuntime.StateRevision,
                        out _, out string reason), Is.True, reason); f.Pump();
                }
                f.Answer("continue"); f.Answer("resource");
                Assert.That(f.Stored, Is.Zero); Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + 1));
                Assert.That(f.State.EffectRuntime.Status, Is.Not.EqualTo(EffectRuntimeStatus.PausedFault));
                Assert.That(f.CleanupWindow.ActiveTargetEffectId, Is.Empty); f.Restore("真实收尾正文完成而系统步骤待执行或已结清");
                if (!f.CleanupWindow.IsClosed)
                {
                    Assert.That(f.Engine.TryEndSettlementAction(1, f.CleanupWindow.Context.Id, "", "", f.State.EffectRuntime.StateRevision,
                        "qy007.r2.scheduling.close", out string reason), Is.True, reason); f.Pump();
                }
                Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == systemId).Status, Is.EqualTo(EffectNodeStatus.Completed));
                f.Restore("真实收尾及其后系统步骤都已完成"); f.Pump();
                Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + 1));
            }
            finally
            {
                if (f != null) artifacts.WriteLabeledText("收尾调度最终真实状态", JsonConvert.SerializeObject(f.State));
                artifacts.Finish();
            }
        }

        [Test]
        public void LongRealNonProgressDiagnosticCommitsOneRecoverableFaultWithinExistingByteBudget()
        {
            var artifacts = IsolatedTestArtifacts.BeginCase(); FifthNumberContentTestFixture f = null;
            try
            {
                f = new FifthNumberContentTestFixture(artifacts); f.FinishOtherPlayer(); f.ResolveOpenRequests();
                Assert.That(f.State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"), Is.False);
                const string parent = "qy007.r2.nonprogress.parent", leaf = "qy007.r2.nonprogress.leaf";
                f.Registry.Register(new EffectRegistration(leaf, c => EffectStepResult.NoProgress(string.Concat(Enumerable.Repeat("协议负例停滞🚩", 100))), EffectExecutorKind.IntrinsicFlow));
                f.Registry.Register(new EffectRegistration(parent, c => c.Node.FlowStage.Length == 0
                    ? EffectStepResult.Continue("等待🚩").AddChildren(Enumerable.Range(0, 10).Select(_ => EffectSpec.Create(leaf, playerId: 1)).ToList())
                    : EffectStepResult.NoProgress("协议负例等待子节点"), EffectExecutorKind.IntrinsicFlow));
                var engine = f.Engine; engine.CreateRoot(EffectSpec.Create(parent, playerId: 1));
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.True);
                Assert.That(f.State.EffectRuntime.LastFaultCode, Is.EqualTo(EffectFaultCodes.NoProgress));
                Assert.That(System.Text.Encoding.UTF8.GetByteCount(engine.LastDiagnostic), Is.GreaterThan(NormalizedValue.MaxStringBytes));
                Assert.That(System.Text.Encoding.UTF8.GetByteCount(f.State.EffectRuntime.LastFaultMessage), Is.LessThanOrEqualTo(NormalizedValue.MaxStringBytes));
                Assert.That(f.State.EffectRuntime.LastFaultMessage, Does.Contain("SHA256="));
                Assert.That(f.State.EffectRuntime.LastFaultMessage, Does.Not.Contain("故障记录失败"));
                Assert.That(f.State.EffectRuntime.Diagnostics.Last().Message, Is.EqualTo(f.State.EffectRuntime.LastFaultMessage));
                Assert.That(f.State.EffectRuntime.Journal.Last().Kind, Is.EqualTo(RuleJournalEntryKind.Fault));
                f.Restore("真实长停滞诊断已原子提交故障");
                Assert.That(f.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            }
            finally
            {
                if (f != null) artifacts.WriteLabeledText("长诊断故障最终真实状态", JsonConvert.SerializeObject(f.State));
                artifacts.Finish();
            }
        }
    }

    public sealed class ContentIntegrationR2LegacyConfirmationTests
    {
        [TestCase(false,false)] [TestCase(false,true)] [TestCase(true,false)] [TestCase(true,true)]
        public void FormalOptionalGateKeepsLegacyBooleanAndCurrentCandidateAnswerEquivalent(bool legacy,bool accept)
        {
            using var f=new TiagoInvestmentTestFixture(); f.CreateHost(); f.StartInvestment(); var q=f.Open;
            Assert.That(q.InteractionTypeId,Is.EqualTo("action.decline_effect")); Assert.That(q.AnswerSchema,Is.EqualTo("candidate_id"));
            int gold=f.State.FindPlayer(1).Resources.GoldVoucher;
            var answer=legacy?NormalizedValue.CreateBoolean(accept):NormalizedValue.CreateString(accept?"continue":"decline");
            Assert.That(EffectTreeExecutor.TryValidateInteractionAnswerOnState(f.State,q,answer,out string valid),Is.True,valid);
            Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(),1,q.StateRevision,answer,out string reason),Is.True,reason); f.Drain();
            if(accept) f.ToPointChoice();
            var dice=f.State.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==LuaDomainEffectTypeIds.RollDice).ToList();
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold-(accept?4:0)));
            Assert.That(dice.Count,Is.EqualTo(accept?2:0));
            Assert.That(dice.All(n=>Number(n.NormalizedResult,"diceResult")>=1&&Number(n.NormalizedResult,"diceResult")<=6),Is.True);
            string random=JsonConvert.SerializeObject(f.State.RandomRuntime);
            string saved=JsonConvert.SerializeObject(dice.Select(n=>new{n.EffectId,n.NormalizedResult}));
            f.Restore("旧布尔与当前候选确认/放弃的真实结果"); f.Drain();
            Assert.That(JsonConvert.SerializeObject(f.State.RandomRuntime),Is.EqualTo(random));
            Assert.That(JsonConvert.SerializeObject(f.State.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==LuaDomainEffectTypeIds.RollDice).Select(n=>new{n.EffectId,n.NormalizedResult})),Is.EqualTo(saved));
        }
    }

    // 仅隔离输入通过现行原生单组转移服务构造，不修改实例字段或绕过权限。
    internal static class IsolatedEntrepreneurTransfer
    {
        internal static EffectSpec ToRetired(GameState state,EffectRegistry registry,int player,string itemId)
        {
            var item=state.ItemZones.Items.Single(i=>i.ItemId==itemId);
            var leg=new ItemTransferRequest { ExecutingPlayerId=player,ItemIds=new List<string>{itemId},RequiredCount=1,
                ExpectedSource=new ItemPositionRef{ZoneId=item.ZoneId},Destination=new ZoneRef{ZoneId=FindZone(state,"retired").ZoneId},
                InsertionKind="unordered",OperationProfileId="core.entrepreneur_card_transfer" };
            var args=Object("itemId",NormalizedValue.CreateString(itemId),"executingPlayer",NormalizedValue.CreateInteger(player),
                "sourceZoneId",NormalizedValue.CreateString(leg.ExpectedSource.ZoneId),"destinationZoneId",NormalizedValue.CreateString(leg.Destination.ZoneId),
                "requiredCount",NormalizedValue.CreateInteger(1),"insertionKind",NormalizedValue.CreateString("unordered"),
                "operationProfileId",NormalizedValue.CreateString(leg.OperationProfileId));
            string expected=args.ToDeterministicString();
            string type="qy007.isolated.entrepreneur.transfer."+Guid.NewGuid().ToString("N");
            // 设施批次只允许facility；隔离退出沿原生单组服务及其完整权限/位置校验。
            registry.Register(new EffectRegistration(type,c=>
            {
                if(c.Node.NormalizedArguments.ToDeterministicString()!=expected) throw new InvalidOperationException("isolated_transfer_input_changed");
                var moved=new ItemTransferService().RelocateItems(c,leg);
                return EffectStepResult.Completed(Object("movedCount",NormalizedValue.CreateInteger(moved.MovedCount)));
            },EffectExecutorKind.Atomic));
            return EffectSpec.Create(type,args,player);
        }
    }

    internal static class IsolatedFinalRoundFlow
    {
        internal static string CreatePendingFinal(GameState state, RoundExecutionService rounds, Action pump)
        {
            state.Round = state.MaxRounds; state.Phase = GamePhase.Cleanup; state.ActionRound = 2;
            var chain = new RoundMainChainBuilder().Build(state.GameId, "", state.Round, state.LaunchPlayerOrder, state.LaunchPlayerOrder[0]);
            var cleanups = chain.Nodes.Where(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow).OrderBy(n => n.MainlineIndex).ToList();
            var final = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded);
            // 仅前序行动/采集是明确隔离前提；所有收尾及其结束都执行真实节点并保留收据。
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < cleanups[0].MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            var rt = state.EffectRuntime; rt.MainNodes.AddRange(chain.Nodes); rt.ActiveMainNodeId = cleanups[0].NodeId;
            rt.FirstMainNodeId = chain.FirstNodeId; rt.CurrentRoundExecutionId = chain.RoundExecutionId;
            rt.CurrentRoundNumber = state.Round; rt.CurrentRoundStartPlayerId = chain.StartPlayerId;
            rt.PlayerOrderSnapshot = chain.PlayerOrderSnapshot.ToList();
            foreach (var cleanup in cleanups)
            {
                var main = state.EffectRuntime.MainNodes.Single(n => n.NodeId == cleanup.NodeId);
                Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.EqualTo(main.NodeId));
                var engine = rounds.CreateCommandExecutor(state);
                main.ExecutionEffectId = engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                    Object("mainNodeId", NormalizedValue.CreateString(main.NodeId), "nodeTypeId", NormalizedValue.CreateString(main.NodeTypeId)), main.PlayerId),
                    state.Round.ToString(System.Globalization.CultureInfo.InvariantCulture), main.NodeId);
                string root = main.ExecutionEffectId;
                main.Status = EffectNodeStatus.Running; pump();
                var window = state.EffectRuntime.EffectNodes.Select(n => n.SettlementWindow).SingleOrDefault(w => w != null && w.Context.Id == state.EffectRuntime.ActiveSettlementWindowId && !w.IsClosed);
                if (window != null)
                {
                    Assert.That(rounds.CreateCommandExecutor(state).TryEndSettlementAction(main.PlayerId, window.Context.Id, "", "", state.EffectRuntime.StateRevision,
                        "qy007.r2.final.cleanup." + main.PlayerId, out string reason), Is.True, reason); pump();
                }
                Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(state.EffectRuntime.MainNodes.Single(n => n.NodeId == cleanup.NodeId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            }
            final = state.EffectRuntime.MainNodes.Single(n => n.NodeId == final.NodeId);
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.EqualTo(final.NodeId));
            final.ExecutionEffectId = rounds.CreateCommandExecutor(state).CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                Object("mainNodeId", NormalizedValue.CreateString(final.NodeId), "nodeTypeId", NormalizedValue.CreateString(final.NodeTypeId)), final.PlayerId),
                state.Round.ToString(System.Globalization.CultureInfo.InvariantCulture), final.NodeId);
            final.Status = EffectNodeStatus.Running;
            // 保留终局尚未推进的原断点，调用者可逐阶段保存恢复。
            return final.ExecutionEffectId;
        }
    }

}
