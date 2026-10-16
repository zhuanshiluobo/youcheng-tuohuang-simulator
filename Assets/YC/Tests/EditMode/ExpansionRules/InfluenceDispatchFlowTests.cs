using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class InfluenceDispatchFlowTests
    {
        [Test]
        public void TwoIndependentMovesUseCurrentPositionsAndSpendOneBudget()
        {
            var f = Create(); var parent = Start(f); var moves = Moves(f, parent);
            Assert.That(moves.Count, Is.EqualTo(2));
            Assert.That(moves[0].Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(moves[1].Status, Is.EqualTo(EffectNodeStatus.Created));
            Assert.That(moves.All(move => !move.AllowDecline), Is.True);
            Assert.That(Current(f).InteractionTypeId, Is.EqualTo(InfluenceDispatchActionEffectExecutor.SourceInteractionTypeId));
            Assert.That(Current(f).CandidateIds, Is.EquivalentTo(new[] { f.InfluenceId }));
            Answer(f, f.InfluenceId); Answer(f, Slot("pier"));
            Assert.That(Item(f).SlotId, Is.EqualTo(Slot("pier")));
            Assert.That(Current(f).OwnerEffectId, Is.EqualTo(moves[1].EffectId));
            Assert.That(Current(f).CandidateIds, Does.Contain(f.InfluenceId), "规则不要求两次使用不同棋子");
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Answer(f, f.InfluenceId); Answer(f, Slot("mine"));
            Assert.That(Item(f).SlotId, Is.EqualTo(Slot("mine")));
            Assert.That(Parent(f).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(MoveEvents(f), Is.EqualTo(2));
            Assert.That(f.State.EffectRuntime.RuleEvents.Any(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.False);
            AssertSpentOnce(f);
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.InteractionTypeId == "action.decline_effect"), Is.False);
            Assert.That(f.State.ItemZones.Items.Count(i => i.OwnerSubject.Kind == ItemSubjectKind.Player && i.OwnerSubject.PlayerId == 1 && i.PhysicalKind == ItemPhysicalKind.Cube), Is.EqualTo(8));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SkippingSecondSourceOrTargetPreservesFirstMovement(bool target)
        {
            var f = Create(); Start(f); Answer(f, f.InfluenceId); Answer(f, Slot("pier"));
            if (target) Answer(f, f.InfluenceId);
            Answer(f, null, true);
            Assert.That(Item(f).SlotId, Is.EqualTo(Slot("pier")));
            Assert.That(Parent(f).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Value(Moves(f, Parent(f))[1].NormalizedResult, "outcome").StringValue, Is.EqualTo("skipped"));
            Assert.That(MoveEvents(f), Is.EqualTo(1)); AssertSpentOnce(f);
        }

        [Test]
        public void SkippingFirstMoveAutomaticallyOpensSecondAndAllowsItToMove()
        {
            var f = Create(); var parent = Start(f); var first = Current(f).OwnerEffectId;
            Answer(f, null, true);
            Assert.That(Current(f).OwnerEffectId, Is.Not.EqualTo(first));
            Assert.That(Current(f).InteractionTypeId, Is.EqualTo(InfluenceDispatchActionEffectExecutor.SourceInteractionTypeId));
            Assert.That(Item(f).SlotId, Is.EqualTo(Slot("quay")));
            Answer(f, f.InfluenceId); Answer(f, Slot("mine"));
            Assert.That(Parent(f).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Item(f).SlotId, Is.EqualTo(Slot("mine"))); AssertSpentOnce(f);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TargetClaimedByAnAnswerResponseFailsWithoutUndoingEarlierMoves(bool afterFirst)
        {
            var f = Create(); Start(f);
            if (afterFirst) { Answer(f, f.InfluenceId); Answer(f, Slot("pier")); }
            Answer(f, f.InfluenceId);
            var targetRequestId = Current(f).GetStableInteractionId();
            f.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.claim_target", "InteractionAnswered", string.Empty, "test.claim_target",
                context => Value(context.Event.Payload, "requestId")?.StringValue == targetRequestId
                    ? new List<EffectSpec> { InfluenceEffectSpecFactory.PlaceInfluence(2, Slot("mine")) }
                    : new List<EffectSpec>()));
            Answer(f, Slot("mine"));
            var parent = Parent(f); var moves = Moves(f, parent);
            Assert.That(parent.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(parent.FailureReason, Is.EqualTo("occupiedslot"));
            Assert.That(moves[afterFirst ? 1 : 0].Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(Item(f).SlotId, Is.EqualTo(Slot(afterFirst ? "pier" : "quay")));
            Assert.That(MoveEvents(f), Is.EqualTo(afterFirst ? 1 : 0));
            if (afterFirst) AssertSpentOnce(f);
            else
            {
                Assert.That(moves[1].Status, Is.EqualTo(EffectNodeStatus.Failed));
                Assert.That(moves[1].FailureReason, Is.EqualTo(moves[0].FailureReason));
                Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
                Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
            }
            Assert.That(f.State.HasPendingChoice(), Is.False);
        }

        [Test]
        public void SkipProtocolRejectsOtherPlayersStaleAnswersAndMandatoryMoves()
        {
            var f = Create(); Start(f); var request = Current(f);
            var engine = new EffectTreeExecutor(f.State, f.Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision,
                NormalizedValue.CreateBoolean(false), out _), Is.False);
            Answer(f, f.InfluenceId);
            engine = new EffectTreeExecutor(f.State, f.Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateBoolean(false), out _), Is.False);
            Answer(f, null, true); Answer(f, null, true);
            Assert.That(MoveEvents(f), Is.Zero); AssertSpentOnce(f);

            var forced = Create(); var forcedEngine = new EffectTreeExecutor(forced.State, forced.Registry);
            forcedEngine.CreateRoot(InfluenceEffectSpecFactory.MoveInfluence(1, forced.InfluenceId, string.Empty));
            Assert.That(forcedEngine.RunUntilQuiescent().Faulted, Is.False);
            var target = Current(forced);
            Assert.That(target.AllowDecline, Is.False);
            Assert.That(forcedEngine.TrySubmitInteraction(target.GetStableInteractionId(), 1, target.StateRevision,
                NormalizedValue.CreateBoolean(false), out _), Is.False);
            Assert.That(Item(forced).SlotId, Is.EqualTo(Slot("quay")));
        }

        [Test]
        public void SavedSecondTargetRequestResumesWithoutRepeatingFirstMovementOrBudget()
        {
            var f = Create(); Start(f); Answer(f, f.InfluenceId); Answer(f, Slot("pier")); Answer(f, f.InfluenceId);
            var request = Current(f);
            var archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State));
            f.Registry = Registry(f.Service); f.Round = new RoundExecutionService(f.Registry);
            var restored = HostRecoveryService.RecoverJson(archive, f.Registry);
            Assert.That(restored.Succeeded, Is.True, restored.FaultCode + ":" + restored.Diagnostic);
            f.State = restored.State;
            Assert.That(new EffectTreeExecutor(f.State, f.Registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(Current(f).GetStableInteractionId(), Is.EqualTo(request.GetStableInteractionId()));
            Answer(f, Slot("mine"));
            Assert.That(Item(f).SlotId, Is.EqualTo(Slot("mine")));
            Assert.That(MoveEvents(f), Is.EqualTo(2)); AssertSpentOnce(f);
        }

        [Test]
        public void EmptySourceSetsCompleteBothMovesWithoutCreatingChoicePopups()
        {
            var f = Create(false); Start(f);
            Assert.That(Parent(f).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Moves(f, Parent(f)).All(n => n.Status == EffectNodeStatus.Completed), Is.True);
            Assert.That(f.State.HasPendingChoice(), Is.False);
            Assert.That(MoveEvents(f), Is.Zero); AssertSpentOnce(f);
        }

        private sealed class Fixture
        {
            public GameState State; public InfluenceService Service; public EffectRegistry Registry;
            public RoundExecutionService Round; public string InfluenceId;
        }

        private static Fixture Create(bool place = true)
        {
            var map = new GameMapDefinition { MapId = "test.dispatch", MinPlayers = 2, MaxPlayers = 4,
                Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "harbor" } },
                Locations = new[] { "quay", "pier", "mine" }.Select(name =>
                    new MapLocationDefinition { LocationId = name, RegionId = "harbor", InfluenceSlotCount = 2, CanDockCity = true }).ToList() };
            var source = new GameState { GameId = "game.dispatch", MapId = map.MapId, ContentPackHash = "content.dispatch", Round = 2,
                Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "content.dispatch" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, RemainingMainActionsThisTurn = 1 },
                    new PlayerState { PlayerId = 2, RemainingMainActionsThisTurn = 1 } } };
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "player_markers", DefinitionId = "core.component.marker",
                TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "player_markers" } };
            foreach (var location in map.Locations) recipe.Add(new ItemRecipeEntry { EntryId = "resource_" + location.LocationId,
                DefinitionId = "core.component.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            var f = new Fixture { State = new ItemZoneStateFactory().CreateForSetup(source, map, recipe), Service = new InfluenceService(new MapQueryService(map)) };
            f.State.Map.OpenLocationIds.AddRange(new[] { "quay", "pier", "mine" }); f.Registry = Registry(f.Service);
            if (place)
            {
                var setup = new EffectTreeExecutor(f.State, f.Registry); string id = setup.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, Slot("quay")));
                Assert.That(setup.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
                Assert.That(setup.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
                f.InfluenceId = Value(setup.GetNode(id).NormalizedResult, "influenceId").ReferenceId;
            }
            var builder = new RoundMainChainBuilder(); var order = new List<int> { 1, 2 };
            var chain = builder.Build(f.State.GameId, builder.CreateRoundExecutionId(f.State.GameId, 2, order), 2, order, 1);
            var window = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && n.PlayerId == 1 && n.ActionRound == 1);
            foreach (var node in chain.Nodes.Where(n => n.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = f.State.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
            f.Round = new RoundExecutionService(f.Registry); var advanced = f.Round.Advance(f.State);
            Assert.That(advanced.IsValid, Is.True, advanced.Reason); return f;
        }

        private static EffectRegistry Registry(InfluenceService service)
        {
            var registry = new EffectRegistry(); InfluenceEffectExecutor.Register(registry, service);
            ItemTransferEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry); return registry;
        }
        private static EffectNodeRuntimeState Start(Fixture f)
        {
            var result = new DispatchInfluenceCommandHandler(f.Service, new RoundAdvanceService(f.Round), f.Registry).Handle(f.State,
                new GameCommand { Kind = GameCommandKind.DispatchInfluence, PlayerId = 1 });
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason); var node = Parent(f);
            Assert.That(node.ParentEffectId, Is.EqualTo(f.State.EffectRuntime.MainNodes.Single(n => n.NodeId == f.State.EffectRuntime.ActiveMainNodeId).ExecutionEffectId));
            return node;
        }
        private static void Answer(Fixture f, string candidate, bool skip = false)
        {
            var projection = InteractionRequestProjector.ProjectForPlayer(Current(f), 1);
            Assert.That(projection.AnswerSchema, Is.EqualTo("candidate_id")); Assert.That(projection.AllowDecline, Is.True);
            var result = new AnswerInteractionCommandHandler(f.Registry, f.Round).Handle(f.State,
                EffectInteractionCommands.Answer(projection, 1, candidate == null ? null : new[] { candidate }, skip));
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(new ItemZoneQuery().TryValidate(f.State, out var reason), Is.True, reason);
        }
        private static void AssertSpentOnce(Fixture f)
        {
            var player = f.State.FindPlayer(1); Assert.That(player.RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(player.CompletedMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(new EffectTreeExecutor(f.State, f.Registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
        }
        private static string Slot(string location) => InfluenceService.GetLocationSlotId(location, 0);
        private static EffectNodeRuntimeState Parent(Fixture f) => f.State.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == InfluenceDispatchActionEffectExecutor.TypeId);
        private static List<EffectNodeRuntimeState> Moves(Fixture f, EffectNodeRuntimeState parent) => parent.ChildEffectIds.Select(id => f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)).ToList();
        private static InteractionRequest Current(Fixture f) => f.State.EffectRuntime.InteractionRequests.Last(r => r.Status == "open" && !r.IsInternalMainlineInteraction());
        private static ItemInstanceState Item(Fixture f) => f.State.ItemZones.Items.Single(i => i.ItemId == f.InfluenceId);
        private static NormalizedValue Value(NormalizedValue value, string name) => value?.Properties?.FirstOrDefault(e => e.Name == name)?.Value;
        private static int MoveEvents(Fixture f) => f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceMoved);
    }
}
