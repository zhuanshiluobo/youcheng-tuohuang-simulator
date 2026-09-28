using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.CityStyles;
using YC.Domain.SpecialActions;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class MainActionDraftEffectTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void Dispatch_TwoMovesStayDraftUntilConfirmation(bool cancel)
        {
            var state = new GameState
            {
                MapId = StaticMapDefinitions.FourPlayerMapId,
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1,
                CurrentPlayerId = 1, StartPlayerId = 1,
                Players = { new PlayerState { PlayerId = 1, RemainingMainActionsThisTurn = 1 } }
            };
            string first = InfluenceService.GetLocationSlotId("A-01", 0), second = InfluenceService.GetLocationSlotId("B-01", 0);
            string target1 = InfluenceService.GetLocationSlotId("C-01", 0), target2 = InfluenceService.GetLocationSlotId("D-01", 0);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = first, LocationId = "A-01" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = second, LocationId = "B-01" });
            foreach (var location in new[] { "C-01", "D-01" })
                state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = location, ResourceType = ResourceType.Iron });
            var registry = new EffectRegistry();
            MainActionSelectionEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap())));
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(MainActionSelectionEffectExecutor.Create(1, MainActionSelectionEffectExecutor.Dispatch));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            foreach (var choice in new[] { first, target1, "action.dispatch.add", second, target2 })
            {
                var request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open");
                Assert.That(request, Is.Not.Null);
                Assert.That(request.AllowDecline, Is.True);
                Assert.That(request.CandidateIds, Does.Contain(choice));
                string reason;
                Assert.That(executor.TrySubmitInteraction(request.InteractionId, 1, request.StateRevision,
                    NormalizedValue.CreateStableReference("candidate", choice), out reason), Is.True, reason);
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
                Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(first));
                Assert.That(state.Map.Influences[1].SlotId, Is.EqualTo(second));
                Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
            }
            var confirmation = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open");
            Assert.That(confirmation.PromptKey, Is.EqualTo("action.main.confirm"));
            string diagnostic;
            Assert.That(executor.TrySubmitInteraction(confirmation.InteractionId, 1, confirmation.StateRevision,
                cancel ? NormalizedValue.CreateBoolean(false) : NormalizedValue.CreateStableReference("candidate", "action.confirm"), out diagnostic), Is.True, diagnostic);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(cancel ? first : target1));
            Assert.That(state.Map.Influences[1].SlotId, Is.EqualTo(cancel ? second : target2));
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(cancel ? 0 : 1));
            Assert.That(state.HasPendingChoice(), Is.False);
            executor.RunUntilQuiescent();
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(cancel ? 0 : 1));
        }
        [TestCase(MainActionSelectionEffectExecutor.Move)]
        [TestCase(ExplorationSelectionEffectExecutor.TypeId)]
        [TestCase(FacilitySelectionEffectExecutor.TypeId)]
        [TestCase(MainActionSelectionEffectExecutor.Special)]
        public void MainAction_CancelDraftLeavesStateUnchanged(string type)
        {
            var state = DraftState();
            if (type == ExplorationSelectionEffectExecutor.TypeId)
            {
                state.FindPlayer(1).Resources.GoldVoucher = 30;
                state.Decks.EventDeckGreen.Add("event_green_01");
                state.Decks.EventDeckYellow.Add("event_yellow_04");
                state.Decks.EventDeckRed.Add("event_red_01");
            }
            var registry = new EffectRegistry();
            MainActionSelectionEffectExecutor.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(MainActionEffectExecutor.Create(1, new EffectSpec(type) { PlayerId = 1 }));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            var request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open");
            Assert.That(request, Is.Not.Null, type);
            Assert.That(request.AllowDecline, Is.True);
            string reason;
            Assert.That(executor.TrySubmitInteraction(request.InteractionId, 1, request.StateRevision,
                NormalizedValue.CreateBoolean(false), out reason), Is.True, reason);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            Assert.That(state.HasPendingChoice(), Is.False);
            AssertDraftUnchanged(state);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Build_SelectionRemainsDraftUntilFinalConfirmation(bool cancel)
        {
            var state = DraftState();
            var registry = new EffectRegistry();
            new RoundExecutionService(registry);
            FacilityEntryEffectExecutor.Register(registry);
            MainActionSelectionEffectExecutor.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(MainActionEffectExecutor.Create(1, new EffectSpec(FacilitySelectionEffectExecutor.TypeId) { PlayerId = 1 }));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            foreach (var choice in new[] { FacilityCardDatabase.BoroughAdministrativeDistrict, "resources", "build-slot:2" })
            {
                AnswerDraft(state, executor, NormalizedValue.CreateStableReference("candidate", choice));
                AssertDraftUnchanged(state);
            }
            Assert.That(state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open").PromptKey, Is.EqualTo("action.main.confirm"));
            AnswerDraft(state, executor, cancel ? NormalizedValue.CreateBoolean(false) : NormalizedValue.CreateStableReference("candidate", "action.confirm"));
            Assert.That(state.HasPendingChoice(), Is.False);
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(cancel ? 0 : 1));
            Assert.That(state.Map.Facilities.Count, Is.EqualTo(cancel ? 0 : 1));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(cancel ? 30 : 27));
            executor.RunUntilQuiescent();
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(cancel ? 0 : 1));
        }

        [Test]
        public void Exploration_TargetFirstReusesDefaultPathAndSlot_ThenCanCancelWithoutPayment()
        {
            var state = DraftState();
            state.FindPlayer(1).Resources.GoldVoucher = 30;
            state.Decks.EventDeckGreen.Add("event_green_01");
            state.Decks.EventDeckYellow.Add("event_yellow_04");
            state.Decks.EventDeckRed.Add("event_red_01");
            var service = new YC.Domain.Exploration.ExplorationService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            var registry = new EffectRegistry();
            MainActionSelectionEffectExecutor.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(MainActionEffectExecutor.Create(1, new EffectSpec(ExplorationSelectionEffectExecutor.TypeId) { PlayerId = 1 }));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            var request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open");
            Assert.That(request.PromptKey, Is.EqualTo("action.explore.target"));
            Assert.That(request.CandidateIds, Is.Not.Empty);
            foreach (var candidate in request.CandidateIds) Assert.That(candidate, Does.StartWith("explore.target:"));
            string target = request.CandidateIds[0].Substring(15);
            var originalPaths = service.FindDefaultPathChoices(state, 1, target);
            Assert.That(service.RequiresPathChoice(state, 1, originalPaths), Is.False, "无不同收费方时沿用原来的自动选路。");
            string expectedSlot = service.ResolveInfluenceSlotId(state, 1, target, "");
            AnswerDraft(state, executor, NormalizedValue.CreateStableReference("candidate", request.CandidateIds[0]));
            request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open");
            Assert.That(request.PromptKey, Is.EqualTo("action.main.confirm"), "目的地之后不应要求逐段行走或再点落点。");
            var node = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == request.OwnerEffectId);
            var fields = node.FlowStage.Split('|');
            Assert.That(fields[1], Is.EqualTo(string.Join(",", originalPaths[0].LocationIds.ConvertAll(System.Uri.EscapeDataString))));
            Assert.That(fields[2], Is.EqualTo(string.Join(",", originalPaths[0].RouteIds.ConvertAll(System.Uri.EscapeDataString))));
            Assert.That(System.Uri.UnescapeDataString(fields[3]), Is.EqualTo(expectedSlot));
            AssertDraftUnchanged(state);
            AnswerDraft(state, executor, NormalizedValue.CreateBoolean(false));
            AssertDraftUnchanged(state);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(30));
            Assert.That(state.Decks.EventDeckGreen.Count + state.Decks.EventDeckYellow.Count + state.Decks.EventDeckRed.Count, Is.EqualTo(3));
            Assert.That(state.HasPendingChoice(), Is.False);
        }

        private static void AnswerDraft(GameState state, EffectTreeExecutor executor, NormalizedValue answer)
        {
            var request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open");
            Assert.That(request, Is.Not.Null);
            string reason;
            Assert.That(executor.TrySubmitInteraction(request.InteractionId, 1, request.StateRevision, answer, out reason), Is.True, reason);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
        }

        private static GameState DraftState()
        {
            var state = new GameState {
                MapId = StaticMapDefinitions.FourPlayerMapId, Phase = GamePhase.ActionRound1,
                Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                Players = { new PlayerState { PlayerId = 1, CityLocationId = "A-01", InfluenceSupply = 30,
                    RemainingMainActionsThisTurn = 1, Resources = new ResourceSet { Originium = 30, Iron = 30, OriginiumShard = 30 } } }
            };
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.BoroughAdministrativeDistrict);
            foreach (var location in new[] { "A-01", "A-02", "B-01" })
                state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = location, ResourceType = ResourceType.Iron });
            state.FindPlayer(1).DeclaredCityStyles.Add(new CityStyleDeclarationState {
                InfluenceMarkerId = "test-marker", CityStyleId = CityStyleDatabase.EfficientMobileManagementSystem,
                UnlockedSpecialActionId = SpecialActionDatabase.EfficientMobileManagementSystem,
                MarkerArea = CityStyleMarkerAreas.UsesTwo, RemainingSpecialActionUses = 2
            });
            return state;
        }

        private static void AssertDraftUnchanged(GameState state)
        {
            var player = state.FindPlayer(1);
            Assert.That(player.CityLocationId, Is.EqualTo("A-01"));
            Assert.That(player.Resources.Iron, Is.EqualTo(30));
            Assert.That(player.Resources.Originium, Is.EqualTo(30));
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(30));
            Assert.That(player.CompletedMainActionsThisTurn, Is.Zero);
            Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(2));
            Assert.That(state.Map.Facilities, Is.Empty);
            Assert.That(state.Map.Influences, Is.Empty);
        }
    }
}
