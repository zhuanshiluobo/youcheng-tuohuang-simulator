using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class MainActionPreselectionTests
    {
        [Test]
        public void ExplorationPreviewAndDraftEditsDoNotWriteAuthorityResourcesOrRandom()
        {
            var f = Production(); var service = new RouteResourceSelectionService(f.Map);
            var original = Snapshot(f.State);
            Assert.That(service.TryCreateExplorationPreview(f.State, 1, out var session, out var draft, out var projection).IsValid, Is.True);
            Assert.That(session.OwnerEffectId, Is.Empty); Assert.That(session.BudgetBindingId, Is.Empty);
            Assert.That(session.ActiveRequestBinding, Is.Null);
            for (int i = 0; i < 3; i++)
            {
                draft = draft.WithSelection(new[] { "A1", "A2" }, new[] { "A-01" }, Array.Empty<RouteRecipientChoice>());
                projection = service.Query(f.State, session, draft, session.SelectableLocationUpperBound);
                Assert.That(projection.Validation.IsValid, Is.True, projection.Validation.Reason);
                Assert.That(projection.Preview.FinalizationValidation.IsValid, Is.True);
                Assert.That(projection.Preview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(4));
                draft = draft.WithSelection(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RouteRecipientChoice>());
                service.Query(f.State, session, draft, session.SelectableLocationUpperBound);
            }
            Assert.That(Snapshot(f.State), Is.EqualTo(original));
        }

        [Test]
        public void ClientPreviewUsesOnlyOwnPublicCountsAndPreservesSanitizedState()
        {
            var f = Production(); var service = new RouteResourceSelectionService(f.Map);
            var visible = GameStateViewProjector.Project(f.State, GameStateViewer.Player(1));
            var client = GameStateViewProjector.ToClientState(visible);
            Assert.That(client.EffectRuntime, Is.Null); Assert.That(client.Decks.EventDeckGreen.Count, Is.Zero);
            var original = Snapshot(client);
            Assert.That(service.TryCreateExplorationPreview(client, 1, out _, out _, out _).IsValid, Is.False);
            Assert.That(service.TryCreateExplorationPreview(client, visible, 1, out var session, out var draft, out var projection).IsValid, Is.True);
            Assert.That(session.OpenedAtStateRevision, Is.EqualTo(visible.StateRevision));
            Assert.That(projection.ObservedStateRevision, Is.EqualTo(visible.StateRevision));
            Assert.That(service.Query(client, session, draft.WithSelection(new[] { "A1", "A2" }, new[] { "A-01" }, Array.Empty<RouteRecipientChoice>()),
                session.SelectableLocationUpperBound).Preview.FinalizationValidation.IsValid, Is.True);
            visible.ViewerPlayerId = 2;
            Assert.That(service.TryCreateExplorationPreview(client, visible, 1, out _, out _, out _).IsValid, Is.False);
            visible.ViewerPlayerId = 1;
            visible.Decks.EventDeckGreenCount = visible.Decks.EventDeckYellowCount = visible.Decks.EventDeckRedCount = 0;
            Assert.That(service.TryCreateExplorationPreview(client, visible, 1, out _, out _, out _).IsValid, Is.False);
            Assert.That(Snapshot(client), Is.EqualTo(original));
        }

        [Test]
        public void DispatchRejectsEmptyHalfForeignSlotAliasAndInvalidTargetBeforeHook()
        {
            var f = Dispatch(); var handler = new DispatchInfluenceCommandHandler(f.Influence, new RoundAdvanceService(f.Round), f.Registry);
            var source = Place(f, 1, Slot("quay")); var foreign = Place(f, 2, Slot("quay", 1));
            foreach (var command in new[] {
                DispatchCommand("", ""), DispatchCommand(source, ""), DispatchCommand("", Slot("pier")),
                DispatchCommand(foreign, Slot("pier")), DispatchCommand(Slot("quay"), Slot("pier")),
                DispatchCommand(source, "missing.slot"), DispatchCommand(source, Slot("quay", 1)),
                DispatchCommand(source, Slot("pier"), new Dictionary<string,string> { { "source2", source }, { "target2", Slot("mine") } }),
                DispatchCommand(source, Slot("pier"), new Dictionary<string,string> { { "sourceSlotId", Slot("quay") } }) })
            {
                var original = Snapshot(f.State); var result = handler.Handle(f.State, command);
                Assert.That(result.Succeeded, Is.False, command.SourceId + "→" + command.TargetId);
                Assert.That(Snapshot(f.State), Is.EqualTo(original));
            }
        }

        [Test]
        public void ConfirmedFirstDispatchMoveKeepsSecondChoiceAndSpendsOnceEvenWithExtraBudget()
        {
            foreach (bool stopAtTarget in new[] { false, true })
            {
                var f = Dispatch(); var source = Place(f, 1, Slot("quay"));
                f.State.FindPlayer(1).RemainingMainActionsThisTurn = 3;
                var handler = new DispatchInfluenceCommandHandler(f.Influence, new RoundAdvanceService(f.Round), f.Registry);
                var result = handler.Handle(f.State, DispatchCommand(source, Slot("pier")));
                Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
                var parent = f.State.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == InfluenceDispatchActionEffectExecutor.TypeId);
                var moves = parent.ChildEffectIds.Select(id => f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)).ToArray();
                Assert.That(moves[0].Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(Text(moves[0].NormalizedArguments, "causeKind"), Is.EqualTo(InfluenceCauseKinds.MoveCity));
                Assert.That(Text(moves[0].NormalizedArguments, "subjectFilter"), Is.EqualTo("self"));
                Assert.That(f.State.ItemZones.Items.Single(item => item.ItemId == source).SlotId, Is.EqualTo(Slot("pier")));
                Assert.That(Current(f).OwnerEffectId, Is.EqualTo(moves[1].EffectId));
                Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(3));
                if (stopAtTarget) Answer(f, NormalizedValue.CreateString(source));
                var stale = Current(f); var wrong = new EffectTreeExecutor(f.State, f.Registry); var original = Snapshot(f.State);
                Assert.That(wrong.TrySubmitInteraction(stale.GetStableInteractionId(), 2, f.State.EffectRuntime.StateRevision,
                    NormalizedValue.CreateBoolean(false), out _), Is.False);
                Assert.That(wrong.TrySubmitInteraction(stale.GetStableInteractionId(), 1, f.State.EffectRuntime.StateRevision - 1,
                    NormalizedValue.CreateBoolean(false), out _), Is.False);
                Assert.That(Snapshot(f.State), Is.EqualTo(original));
                Answer(f, NormalizedValue.CreateBoolean(false));
                Assert.That(parent.Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(2));
                Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
                Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceMoved), Is.EqualTo(1));
                Run(f); Run(f);
                Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
            }
        }

        [Test]
        public void ConfirmedDispatchCanMoveAgainAndRecoveredSecondRequestDoesNotRepeatFirstMove()
        {
            var f = Dispatch(); var source = Place(f, 1, Slot("quay"));
            Assert.That(new DispatchInfluenceCommandHandler(f.Influence, new RoundAdvanceService(f.Round), f.Registry)
                .Handle(f.State, DispatchCommand(source, Slot("pier"))).Succeeded, Is.True);
            Answer(f, NormalizedValue.CreateString(source));
            var savedRequest = Current(f).GetStableInteractionId();
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State)), f.Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); f.State = restored.State;
            Run(f); Assert.That(Current(f).GetStableInteractionId(), Is.EqualTo(savedRequest));
            Answer(f, NormalizedValue.CreateString(Slot("mine")));
            Assert.That(f.State.ItemZones.Items.Single(item => item.ItemId == source).SlotId, Is.EqualTo(Slot("mine")));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceMoved), Is.EqualTo(2));
            Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
        }

        [Test]
        public void ExploreRejectsIncompleteTamperedUnaffordableOrStaleSelectionsBeforeHook()
        {
            var f = Production(); var handler = ExploreHandler(f);
            foreach (var command in new[] {
                ExploreCommand("", ""), ExploreCommand("A-01", ""), ExploreCommand("A-01", "missing.route"),
                ExploreCommand("A-01", "A1,A1,A2"), ExploreCommand("A-01", "A1,,A2"), ExploreCommand("missing.location", "A1,A2"),
                ExploreCommand("A-01", "A1,A2", "A1=0"), ExploreCommand("A-01", "A1,A2", "A1=-1;A1=-1"),
                ExploreCommand("A-01", "A1,A2", "A1=999"),
                ExploreCommand("A-01", "A1,A2", extra: new Dictionary<string,string> { { "goldVoucherAmount", "0" } }),
                ExploreCommand("A-01", "A1,A2", extra: new Dictionary<string,string> { { "commitmentId", "forged" } }),
                ExploreCommand("A-01", "A1,A2", extra: new Dictionary<string,string> { { "pathLocationIds", "A-03,missing.location,A-01" } }) })
            {
                var original = Snapshot(f.State); var result = handler.Handle(f.State, command);
                Assert.That(result.Succeeded, Is.False, command.TargetId + ":" + result.Validation.Reason);
                Assert.That(Snapshot(f.State), Is.EqualTo(original));
            }
            f.State.FindPlayer(1).Resources.GoldVoucher = 1;
            var before = Snapshot(f.State);
            Assert.That(handler.Handle(f.State, ExploreCommand("A-01", "A1,A2")).Succeeded, Is.False);
            Assert.That(Snapshot(f.State), Is.EqualTo(before));
            f.State.FindPlayer(1).Resources.GoldVoucher = 8;
            MoveFixtureCity(f.State, "B-01");
            before = Snapshot(f.State);
            Assert.That(handler.Handle(f.State, ExploreCommand("A-01", "A1,A2")).Succeeded, Is.False);
            Assert.That(Snapshot(f.State), Is.EqualTo(before));
        }

        [Test]
        public void ConfirmedExploreSealsAuthorityAndEscrowThenConsumesOnlyOneMainAction()
        {
            foreach (bool free in new[] { false, true })
            {
                var f = Production();
                if (free) { Place(f, 1, InfluenceService.GetRouteSlotId("A1", 0)); Place(f, 1, InfluenceService.GetRouteSlotId("A2", 0)); }
                f.State.FindPlayer(1).RemainingMainActionsThisTurn = 3;
                var result = ExploreHandler(f).Handle(f.State, ExploreCommand("A-01", free ? "" : "A1,A2"));
                Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
                var parent = f.State.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == ExplorationActionEffectExecutor.TypeId);
                Assert.That(new RouteSelectionSessionAuthority(f.Map).TryResolveFinalized(f.State, parent.EffectId, out var session, out var commitment).IsValid, Is.True);
                Assert.That(session.OwnerEffectId, Is.EqualTo(parent.EffectId)); Assert.That(session.Stage, Is.EqualTo(RouteSelectionStage.Committed));
                var child = f.State.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == parent.EffectId && n.EffectTypeId == ExplorationEffectTypeIds.Explore);
                Assert.That(child.NormalizedArguments.Properties.Single(p => p.Name == "commitmentId").Value.StringValue, Is.EqualTo(commitment.CommitmentId));
                Assert.That(commitment.AcceptedPreview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(free ? 0 : 4));
                Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.InteractionTypeId == "route_resource.selection"), Is.False);
                Assert.That(RouteTollEscrowService.QueryBalance(f.State, 1).Reserved, Is.EqualTo(free ? 0 : 4));
                Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(3));
                Finish(f);
                Assert.That(parent.Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(f.State.Map.ResourceTokens.Any(t => t.LocationId == "A-01"), Is.True);
                Assert.That(f.State.Map.Influences.Any(i => i.LocationId == "A-01" && i.PlayerId == 1), Is.True);
                Assert.That(RouteTollEscrowService.QueryBalance(f.State, 1).Reserved, Is.Zero);
                Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(2));
                Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
                Run(f); Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
            }
        }

        [Test]
        public void ExplicitRouteRecipientMustMatchCurrentOwnerAndFrozenFeeReachesChosenOwner()
        {
            var f = Production(); Place(f, 2, InfluenceService.GetRouteSlotId("A1", 0));
            var handler = ExploreHandler(f); var original = Snapshot(f.State);
            Assert.That(handler.Handle(f.State, ExploreCommand("A-01", "A1,A2", "A1=3")).Succeeded, Is.False);
            Assert.That(Snapshot(f.State), Is.EqualTo(original));
            int before = f.State.FindPlayer(2).Resources.GoldVoucher;
            var result = handler.Handle(f.State, ExploreCommand("A-01", "A1,A2", "A1=2"));
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            var parent = f.State.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == ExplorationActionEffectExecutor.TypeId);
            Assert.That(new RouteSelectionSessionAuthority(f.Map).TryResolveFinalized(f.State, parent.EffectId, out _, out var commitment).IsValid, Is.True);
            Assert.That(commitment.AcceptedPreview.PaymentPlan.Lines.Single(l => l.RouteId == "A1").ReceiverSubjectKey, Is.EqualTo("player:2"));
            Finish(f); Assert.That(f.State.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(before + 2));
        }

        [Test]
        public void PreviewKeepsExplicitRecipientChoiceForRoutesWithMultipleOwners()
        {
            var f = Production();
            var top = f.State.Decks.EventDeckGreen[0];
            var map = new GameMapDefinition { MapId = "preselection.multiple_recipients", Locations = new[] { "S", "T" }.Select(id =>
                new MapLocationDefinition { LocationId = id, EventColor = EventColor.Green, InfluenceSlotCount = 2 }).ToList(),
                Routes = new List<MapRouteDefinition> { new MapRouteDefinition { RouteId = "paid", FromLocationId = "S", ToLocationId = "T",
                    CoveredLocationIds = new List<string> { "S", "T" }, InfluenceSlotCount = 2 } } };
            var state = new GameState { GameId = "preselection.multiple_recipients", MapId = map.MapId, Phase = GamePhase.ActionRound1, CurrentPlayerId = 1,
                ContentDefinitions = f.State.ContentDefinitions, Players = Enumerable.Range(1, 3).Select(id => new PlayerState {
                    PlayerId = id, CityLocationId = id == 1 ? "S" : "", InfluenceSupply = 4, Resources = new ResourceSet { GoldVoucher = 4 } }).ToList() };
            state.Decks.EventDeckGreen.Add(top);
            foreach (int player in new[] { 2, 3 }) state.Map.Influences.Add(new InfluencePlacement {
                PlayerId = player, RouteId = "paid", SlotId = InfluenceService.GetRouteSlotId("paid", player - 2), InfluenceId = "owner" + player });
            var service = new RouteResourceSelectionService(new MapQueryService(map)); var original = Snapshot(state);
            Assert.That(service.TryCreateExplorationPreview(state, 1, out var session, out var draft, out _).IsValid, Is.True);
            var selection = draft.WithSelection(new[] { "paid" }, new[] { "T" }, Array.Empty<RouteRecipientChoice>());
            Assert.That(service.Query(state, session, selection, session.SelectableLocationUpperBound).Preview.FinalizationValidation.IsValid, Is.False);
            selection = selection.WithSelection(new[] { "paid" }, new[] { "T" }, new[] { new RouteRecipientChoice("paid", "player:3") });
            var preview = service.Query(state, session, selection, session.SelectableLocationUpperBound).Preview;
            Assert.That(preview.FinalizationValidation.IsValid, Is.True); Assert.That(preview.PaymentPlan.Lines.Single().ReceiverSubjectKey, Is.EqualTo("player:3"));
            Assert.That(Snapshot(state), Is.EqualTo(original));
        }

        [Test]
        public void GrantedExploreKeepsCommonRouteInteractionAndDoesNotConsumeMainAction()
        {
            var f = Production(); var before = f.State.FindPlayer(1).RemainingMainActionsThisTurn;
            var result = ExploreHandler(f).BeginGrantedExplore(f.State,
                new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 }, "A-01");
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            var request = Current(f); Assert.That(request.InteractionTypeId, Is.EqualTo("route_resource.selection"));
            var scope = Text(request.PromptParameters, "scopeKey");
            var ids = new[] { RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Commit),
                RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Location, "A-01"),
                RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.RouteAndRecipient, "A1", "bank"),
                RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.RouteAndRecipient, "A2", "bank") };
            Answer(f, NormalizedValue.CreateArray(ids.Select(NormalizedValue.CreateString).ToList())); Finish(f);
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(before));
            Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void SavedConfirmedExploreRestoresRegisteredParentWithoutRepeatingEscrowOrBudget()
        {
            var f = Production(); f.State.FindPlayer(1).RemainingMainActionsThisTurn = 3;
            Assert.That(ExploreHandler(f).Handle(f.State, ExploreCommand("A-01", "A1,A2")).Succeeded, Is.True);
            var request = Current(f).GetStableInteractionId(); var random = JsonConvert.SerializeObject(f.State.RandomRuntime);
            var archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State));
            var replacement = Production(); ExploreHandler(replacement);
            var restored = HostRecoveryService.RecoverJson(archive, replacement.Registry);
            Assert.That(restored.Succeeded, Is.True, restored.FaultCode + ":" + restored.Diagnostic);
            replacement.State = restored.State; Run(replacement);
            Assert.That(Current(replacement).GetStableInteractionId(), Is.EqualTo(request));
            Assert.That(RouteTollEscrowService.QueryBalance(replacement.State, 1).Reserved, Is.EqualTo(4));
            Assert.That(JsonConvert.SerializeObject(replacement.State.RandomRuntime), Is.EqualTo(random));
            Finish(replacement);
            Assert.That(replacement.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(2));
            Assert.That(replacement.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(RouteTollEscrowService.QueryBalance(replacement.State, 1).Reserved, Is.Zero);
        }

        private sealed class Fixture
        {
            public GameState State; public EffectRegistry Registry; public InfluenceService Influence;
            public IMapQueryService Map; public RoundExecutionService Round; public ExplorationService Exploration;
        }

        private static Fixture Production()
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            EventCardDatabase.InitializeExternal(pack.CreateEvents());
            var config = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.FourPlayerMapId, EnabledExpansionIds = new List<string> { "core" } };
            var catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, Enumerable.Range(1, 3).Select(p => new PlayerSeat { PlayerId = p }).ToList(),
                config, 321, catalog.SetupRecipe, catalog.ContentDefinitions, new[] { 1, 2, 3 }, "main_action_preselection.production", null);
            MoveFixtureCity(state, "A-03");
            var f = new Fixture { State = state, Map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)), Registry = new EffectRegistry() };
            f.Influence = new InfluenceService(f.Map); var decks = new EventDeckService(321); var tokens = new ResourceTokenService();
            f.Exploration = new ExplorationService(f.Map, f.Influence, decks, tokens);
            catalog.Register(f.Registry); GenericLuaEffectExecutor.Register(f.Registry); ResourceEffectExecutor.Register(f.Registry);
            InfluenceEffectExecutor.Register(f.Registry, f.Influence); EventCardEffectExecutor.Register(f.Registry, f.Map, f.Influence, decks, tokens);
            ExplorationEffectExecutor.Register(f.Registry, f.Map, f.Exploration, decks, tokens);
            PrepareWindow(f, new List<int> { 1, 2, 3 }); f.State.FindPlayer(1).Resources.GoldVoucher = 8;
            return f;
        }

        private static Fixture Dispatch()
        {
            var map = new GameMapDefinition { MapId = "main_action_preselection.dispatch", MinPlayers = 2, MaxPlayers = 4,
                Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "harbor" } },
                Locations = new[] { "quay", "pier", "mine" }.Select(id => new MapLocationDefinition {
                    LocationId = id, RegionId = "harbor", InfluenceSlotCount = 2, CanDockCity = true }).ToList() };
            var state = new GameState { GameId = "main_action_preselection.dispatch", MapId = map.MapId, ContentPackHash = "preselection.dispatch",
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "preselection.dispatch" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "supply", DefinitionId = "core.component.marker",
                TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "supply" } };
            foreach (var location in map.Locations) recipe.Add(new ItemRecipeEntry { EntryId = "resource_" + location.LocationId,
                DefinitionId = "core.component.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            var f = new Fixture { State = new ItemZoneStateFactory().CreateForSetup(state, map, recipe), Map = new MapQueryService(map), Registry = new EffectRegistry() };
            f.State.Map.OpenLocationIds.AddRange(new[] { "quay", "pier", "mine" }); f.Influence = new InfluenceService(f.Map);
            InfluenceEffectExecutor.Register(f.Registry, f.Influence); ItemTransferEffectExecutor.Register(f.Registry); ResourceEffectExecutor.Register(f.Registry);
            PrepareWindow(f, new List<int> { 1, 2 }); return f;
        }

        private static void PrepareWindow(Fixture f, List<int> order)
        {
            var builder = new RoundMainChainBuilder(); var chain = builder.Build(f.State.GameId, builder.CreateRoundExecutionId(f.State.GameId, 2, order), 2, order, 1);
            var window = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && n.PlayerId == 1 && n.ActionRound == 1);
            foreach (var node in chain.Nodes.Where(n => n.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = f.State.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
            f.Round = new RoundExecutionService(f.Registry); var validation = f.Round.Advance(f.State);
            Assert.That(validation.IsValid, Is.True, validation.Reason);
        }

        private static ExploreLocationCommandHandler ExploreHandler(Fixture f) => new ExploreLocationCommandHandler(f.Exploration, new RoundAdvanceService(f.Round), f.Registry);
        private static GameCommand DispatchCommand(string source, string target, Dictionary<string,string> parameters = null) =>
            new GameCommand { Kind = GameCommandKind.DispatchInfluence, PlayerId = 1, SourceId = source, TargetId = target, Parameters = parameters ?? new Dictionary<string,string>() };
        private static GameCommand ExploreCommand(string target, string routes, string recipients = "", Dictionary<string,string> extra = null)
        {
            var parameters = extra ?? new Dictionary<string,string>(); parameters[ExploreLocationCommandHandler.RouteIdsParameter] = routes;
            parameters[ExploreLocationCommandHandler.PaymentRecipientsParameter] = recipients;
            return new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1, TargetId = target, Parameters = parameters };
        }
        private static string Place(Fixture f, int player, string slot)
        {
            var executor = new EffectTreeExecutor(f.State, f.Registry); var id = executor.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(player, slot)); Run(f);
            var node = executor.GetNode(id); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed), node.FailureReason);
            return node.NormalizedResult.Properties.Single(p => p.Name == "influenceId").Value.ReferenceId;
        }
        private static void Answer(Fixture f, NormalizedValue answer)
        {
            var request = Current(f); var executor = new EffectTreeExecutor(f.State, f.Registry);
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, f.State.EffectRuntime.StateRevision, answer, out var reason), Is.True, reason);
            Run(f);
        }
        private static void Finish(Fixture f)
        {
            for (int i = 0; i < 80; i++)
            {
                var request = f.State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open" && !r.IsInternalMainlineInteraction());
                if (request == null) return;
                Assert.That(request.InteractionTypeId, Is.Not.EqualTo("route_resource.selection"));
                var choices = request.CandidateIds.Take(Math.Max(1, request.MinSelections)).Select(NormalizedValue.CreateString).ToList();
                Answer(f, request.AnswerSchema == "candidate_ids" ? NormalizedValue.CreateArray(choices) : choices[0]);
            }
            Assert.Fail("探索事件链未在保护次数内结束。");
        }
        private static void Run(Fixture f) => Assert.That(new EffectTreeExecutor(f.State, f.Registry).RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
        private static InteractionRequest Current(Fixture f) => f.State.EffectRuntime.InteractionRequests.Last(r => r.Status == "open" && !r.IsInternalMainlineInteraction());
        private static string Snapshot(GameState state) => JsonConvert.SerializeObject(state);
        private static string Text(NormalizedValue value, string key) => value.Properties.First(p => p.Name == key).Value.StringValue;
        private static string Slot(string location, int index = 0) => InfluenceService.GetLocationSlotId(location, index);
        private static void MoveFixtureCity(GameState state, string target)
        {
            var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == 1);
            var parent = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == target);
            var dock = state.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.city_dock");
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { state, city, dock.ZoneId });
        }
    }
}
