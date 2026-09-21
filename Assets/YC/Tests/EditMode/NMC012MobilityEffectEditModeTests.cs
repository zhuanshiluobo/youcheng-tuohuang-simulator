using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class NMC012MobilityEffectEditModeTests
    {
        [Test]
        public void CityMove_RevalidatesTargetAfterBeforeResponseAndDoesNotCommitStaleTarget()
        {
            MapQueryService mapQuery;
            GameState state = CreateState(out mapQuery);
            var registry = CreateRegistry(mapQuery, state);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(CityMoveEffectSpecFactory.Move(1, "A-02"));

            for (int i = 0; i < 20 && executor.GetNode(effectId).FlowStage != "before_city_move"; i++)
            {
                Assert.That(executor.Advance(), Is.True, executor.LastDiagnostic);
            }

            state.FindPlayer(2).CityLocationId = "A-02";
            EffectRunReport report = executor.RunUntilQuiescent();
            EffectNodeRuntimeState node = executor.GetNode(effectId);

            Assert.That(report.Faulted, Is.False, report.FaultCode);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
            Assert.That(CountEvents(state, CityMoveEventTypeIds.CityMoveCompleted), Is.EqualTo(0));
        }

        [Test]
        public void CityMove_CompletesThroughCityMoveCompletedAndLeavesNoLegacyCardSession()
        {
            MapQueryService mapQuery;
            GameState state = CreateState(out mapQuery);
            var registry = CreateRegistry(mapQuery, state);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(CityMoveEffectSpecFactory.Move(1, "A-02"));

            EffectRunReport first = executor.RunUntilQuiescent();
            Assert.That(first.Faulted, Is.False, first.FaultCode);
            Assert.That(first.WaitingForInput, Is.True);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-02"));
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(CountEvents(state, CityMoveEventTypeIds.CityMoveCompleted), Is.EqualTo(1));

            FinishOpenInteractions(state, effectId);

            Assert.That(executor.GetNode(effectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
            Assert.That(CountEvents(state, CityMoveEventTypeIds.CityMoveCompleted), Is.EqualTo(1));
        }

        [Test]
        public void MoveCityCommand_AcceptsStableCandidateIdAndStillRevalidatesTarget()
        {
            MapQueryService mapQuery;
            GameState state = CreateState(out mapQuery);
            var influenceService = new InfluenceService(mapQuery);
            var eventDeckService = new EventDeckService(73);
            var resourceTokenService = new ResourceTokenService();
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                new TravelCostService(mapQuery),
                eventDeckService,
                resourceTokenService);
            eventDeckService.InitializeDecks(
                state.Decks,
                EventCardDatabase.GreenCardIds,
                EventCardDatabase.YellowCardIds,
                EventCardDatabase.RedCardIds);
            var registry = CreateRegistry(mapQuery, state, false);
            var handler = new MoveCityCommandHandler(
                movementService,
                new RoundAdvanceService(),
                registry);

            CommandResult result = handler.Handle(state, new GameCommand
            {
                CommandId = "nmc012-candidate-command",
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = CityMoveCandidateQueryService.CandidatePrefix + "A-02"
            });

            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-02"));
        }

        [Test]
        public void CityMove_CanBeDeclinedBeforeAnyCommit()
        {
            MapQueryService mapQuery;
            GameState state = CreateState(out mapQuery);
            var registry = CreateRegistry(mapQuery, state);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(CityMoveEffectSpecFactory.Move(
                1,
                "A-02",
                false,
                true,
                "nmc012-decline",
                true));

            EffectRunReport first = executor.RunUntilQuiescent();
            Assert.That(first.WaitingForInput, Is.True);
            InteractionRequest request = FindOpenInteraction(state, "action.decline_effect");
            Assert.That(request, Is.Not.Null);

            string diagnostic;
            Assert.That(executor.TrySubmitInteraction(
                request.GetStableInteractionId(),
                1,
                request.StateRevision,
                NormalizedValue.CreateBoolean(true),
                out diagnostic), Is.True, diagnostic);
            EffectRunReport declined = executor.RunUntilQuiescent();

            Assert.That(declined.Faulted, Is.False, declined.FaultCode);
            Assert.That(executor.GetNode(effectId).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
            Assert.That(CountEvents(state, CityMoveEventTypeIds.BeforeCityMove), Is.EqualTo(0));
        }

        [Test]
        public void Explore_PaysRouteTollBeforeEventCardChildAndUsesStableTargetInteraction()
        {
            MapQueryService mapQuery;
            GameState state = CreateState(out mapQuery);
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            var path = new MapPath
            {
                LocationIds = new List<string> { "A-01", "A-02" },
                RouteIds = new List<string> { "A1" }
            };
            var registry = CreateRegistry(mapQuery, state);
            string effectId = new EffectTreeExecutor(state, registry).CreateRoot(
                ExplorationEffectSpecFactory.Explore(
                    1,
                    "A-02",
                    path,
                    InfluenceService.GetLocationSlotId("A-02", 0),
                    new Dictionary<string, int>()));

            var executor = new EffectTreeExecutor(state, registry);
            EffectRunReport report = executor.RunUntilQuiescent();

            Assert.That(report.Faulted, Is.False, report.FaultCode);
            Assert.That(report.WaitingForInput, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.Map.ResourceTokens.Exists(token => token.LocationId == "A-02"), Is.True);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(state.EffectRuntime.InteractionRequests.Exists(request =>
                request.Status == "open" && request.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId), Is.True);
        }

        [Test]
        public void Explore_WithoutInfluenceSlot_ResolvesFirstLegalTargetSlotInsideEffect()
        {
            MapQueryService mapQuery;
            GameState state = CreateState(out mapQuery);
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            var path = new MapPath
            {
                LocationIds = new List<string> { "A-01", "A-02" },
                RouteIds = new List<string> { "A1" }
            };
            var registry = CreateRegistry(mapQuery, state);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(
                ExplorationEffectSpecFactory.Explore(
                    1,
                    "A-02",
                    path,
                    string.Empty,
                    new Dictionary<string, int>()));

            EffectRunReport report = executor.RunUntilQuiescent();
            EffectNodeRuntimeState exploration = executor.GetNode(effectId);
            EffectNodeRuntimeState eventCard = state.EffectRuntime.EffectNodes.Find(node =>
                node != null && node.ParentEffectId == effectId &&
                node.EffectTypeId == EventCardEffectTypeIds.Resolve);

            Assert.That(report.Faulted, Is.False, report.FaultCode);
            Assert.That(report.WaitingForInput, Is.True);
            Assert.That(exploration.Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(eventCard, Is.Not.Null);
            Assert.That(ReadStableReference(eventCard.NormalizedArguments, "primaryInfluenceSlot"),
                Is.EqualTo(InfluenceService.GetLocationSlotId("A-02", 0)));
        }

        [Test]
        public void ExploreLocationCommand_WithoutInfluenceSlotIsAcceptedByEffectHandler()
        {
            MapQueryService mapQuery;
            GameState state = CreateState(out mapQuery);
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            var influenceService = new InfluenceService(mapQuery);
            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                new EventDeckService(73),
                new ResourceTokenService());
            var handler = new ExploreLocationCommandHandler(
                explorationService,
                new RoundAdvanceService(),
                CreateRegistry(mapQuery, state));

            CommandResult result = handler.Handle(state, new GameCommand
            {
                CommandId = "nmc012-explore-without-slot",
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(FindOpenInteraction(state, EventCardEffectExecutor.OptionInteractionTypeId), Is.Not.Null);
            Assert.That(state.EffectRuntime.EffectNodes.Exists(node =>
                node != null && node.EffectTypeId == EventCardEffectTypeIds.Resolve &&
                ReadStableReference(node.NormalizedArguments, "primaryInfluenceSlot") ==
                InfluenceService.GetLocationSlotId("A-02", 0)), Is.True);
        }

        [Test]
        public void EventCard_OptionIdsAreStableAndRecoveryDoesNotDrawAgain()
        {
            MapQueryService mapQuery;
            GameState state = CreateState(out mapQuery);
            var registry = CreateRegistry(mapQuery, state);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-02"));

            EffectRunReport reveal = executor.RunUntilQuiescent();
            Assert.That(reveal.WaitingForInput, Is.True);
            InteractionRequest request = FindOpenInteraction(state, EventCardEffectExecutor.OptionInteractionTypeId);
            Assert.That(request, Is.Not.Null);
            Assert.That(request.CandidateIds[0], Does.Match("^event_green_[0-9]{2}:option:[0-9]+$"));
            string cardId = request.CandidateIds[0].Substring(0, request.CandidateIds[0].IndexOf(":option:", StringComparison.Ordinal));
            int remainingAfterReveal = new EventDeckService().RemainingCount(state.Decks, EventColor.Green);

            var recovered = GameStateCloneService.DeepClone(state);
            FinishOpenInteractions(recovered, effectId);

            Assert.That(new EventDeckService().RemainingCount(recovered.Decks, EventColor.Green), Is.EqualTo(remainingAfterReveal));
            Assert.That(recovered.EffectRuntime.RuleEvents.Exists(ruleEvent =>
                ruleEvent.EventType == EventCardEffectEventTypeIds.Revealed && ruleEvent.Payload.ToDeterministicString().Contains(cardId)), Is.True);
        }

        [Test]
        public void EventCardLuaCatalog_ProducesVersionedSandboxSourcesForEveryCard()
        {
            var cardIds = new List<string>();
            cardIds.AddRange(EventCardDatabase.GreenCardIds);
            cardIds.AddRange(EventCardDatabase.RedCardIds);
            cardIds.AddRange(EventCardDatabase.YellowCardIds);

            Assert.That(cardIds, Has.Count.EqualTo(EventCardDatabase.ExpectedDefinitionCount));
            for (int i = 0; i < cardIds.Count; i++)
            {
                LuaScriptDefinition definition = EventCardLuaCatalog.CreateDefinition(cardIds[i]);
                Assert.That(definition.DefinitionVersion, Is.EqualTo(EventCardLuaCatalog.DefinitionVersion));
                Assert.That(definition.HandlerId, Is.EqualTo("lua.event_card." + cardIds[i]));
                Assert.That(definition.Source, Does.Contain("return function(ctx)"));
                Assert.That(definition.Source, Does.Contain("Effect."));
                Assert.That(definition.ContentHash, Is.EqualTo(LuaContentHasher.ComputeSha256(definition.Source)));
            }
        }

        [Test]
        public void EventCardLuaCatalog_CompilesPaymentInfluenceAndOpponentRewardFamilies()
        {
            LuaScriptDefinition paymentDefinition = EventCardLuaCatalog.CreateDefinition("event_yellow_04");
            LuaInvocationResult paymentInvocation = InvokeEventCardScript(
                paymentDefinition,
                "0",
                "location:A-01:0");
            Assert.That(paymentInvocation.Status, Is.EqualTo(LuaInvocationStatus.Success), paymentInvocation.Diagnostic);
            Assert.That(paymentInvocation.Effects, Has.Count.EqualTo(2));
            Assert.That(paymentInvocation.Effects[0].EffectTypeId, Is.EqualTo(ResourceEffectTypeIds.Pay));
            Assert.That(paymentInvocation.Effects[1].EffectTypeId, Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence));

            LuaScriptDefinition opponentDefinition = EventCardLuaCatalog.CreateDefinition("event_red_05");
            LuaInvocationResult opponentInvocation = InvokeEventCardScript(opponentDefinition, "2", string.Empty);
            Assert.That(opponentInvocation.Status, Is.EqualTo(LuaInvocationStatus.Success), opponentInvocation.Diagnostic);
            Assert.That(opponentInvocation.Effects, Has.Count.EqualTo(2));
            Assert.That(opponentInvocation.Effects[0].EffectTypeId, Is.EqualTo(ResourceEffectTypeIds.Gain));
            Assert.That(opponentInvocation.Effects[1].EffectTypeId, Is.EqualTo(ResourceEffectTypeIds.Gain));
            Assert.That(opponentInvocation.Effects[0].RecipientPlayerId, Is.EqualTo("p1"));
            Assert.That(opponentInvocation.Effects[1].RecipientPlayerId, Is.EqualTo("p2"));

            var compiler = new LuaEffectSpecCompiler();
            List<EffectSpec> compiled;
            LuaEffectCompilationFault fault;
            Assert.That(compiler.TryCompile(
                opponentInvocation,
                new LuaEffectCompilationContext
                {
                    SourceId = opponentDefinition.ContentId,
                    StableKeyPrefix = "nmc012-test",
                    PlayerId = 1
                },
                out compiled,
                out fault), Is.True, fault == null ? string.Empty : fault.Diagnostic);
            Assert.That(compiled, Has.Count.EqualTo(2));
        }

        [Test]
        public void EventCard_ZeroChoiceDefinitionResolvesImmediatelyWithoutOpeningInteraction()
        {
            var originalDefinitions = new List<EventCardDefinition>();
            var replacementDefinitions = new List<EventCardDefinition>();
            var cardIds = new List<string>();
            cardIds.AddRange(EventCardDatabase.GreenCardIds);
            cardIds.AddRange(EventCardDatabase.RedCardIds);
            cardIds.AddRange(EventCardDatabase.YellowCardIds);
            for (int i = 0; i < cardIds.Count; i++)
            {
                EventCardDefinition definition = EventCardDatabase.Get(cardIds[i]);
                originalDefinitions.Add(definition);
                replacementDefinitions.Add(EventCardDatabase.Get(cardIds[i]));
            }

            replacementDefinitions[0].ChoiceDescriptions.Clear();
            replacementDefinitions[0].ChoiceRewards.Clear();
            replacementDefinitions[0].ChoicePendingEffects.Clear();
            ResetEventCardDatabase();
            try
            {
                Assert.DoesNotThrow(() => EventCardDatabase.Initialize(replacementDefinitions));

                MapQueryService mapQuery;
                GameState state = CreateState(out mapQuery);
                state.Decks.EventDeckGreen.Add("event_green_01");
                var registry = CreateRegistry(mapQuery, state, false);
                var executor = new EffectTreeExecutor(
                    state,
                    registry);
                string effectId = executor.CreateRoot(
                    EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-02"));

                EffectRunReport report = executor.RunUntilQuiescent();

                Assert.That(report.Faulted, Is.False, report.FaultCode);
                Assert.That(report.WaitingForInput, Is.False);
                Assert.That(executor.GetNode(effectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(FindOpenInteraction(state, EventCardEffectExecutor.OptionInteractionTypeId), Is.Null);
                Assert.That(CountEvents(state, EventCardEffectEventTypeIds.Resolved), Is.EqualTo(1));
            }
            finally
            {
                ResetEventCardDatabase();
                EventCardDatabase.Initialize(originalDefinitions);
            }
        }

        private static EffectRegistry CreateRegistry(MapQueryService mapQuery, GameState state)
        {
            return CreateRegistry(mapQuery, state, true);
        }

        private static EffectRegistry CreateRegistry(MapQueryService mapQuery, GameState state, bool initializeDeck)
        {
            var influenceService = new InfluenceService(mapQuery);
            var resourceTokenService = new ResourceTokenService();
            var eventDeckService = new EventDeckService(73);
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                new TravelCostService(mapQuery),
                eventDeckService,
                resourceTokenService);
            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                eventDeckService,
                resourceTokenService);
            if (initializeDeck)
            {
                eventDeckService.InitializeDecks(
                    state.Decks,
                    EventCardDatabase.GreenCardIds,
                    EventCardDatabase.YellowCardIds,
                    EventCardDatabase.RedCardIds);
            }

            var registry = new EffectRegistry();
            InfluenceEffectExecutor.Register(registry, influenceService);
            ResourceEffectExecutor.Register(registry);
            CityMoveEffectExecutor.Register(
                registry,
                mapQuery,
                influenceService,
                movementService,
                new TravelCostService(mapQuery),
                resourceTokenService);
            ExplorationEffectExecutor.Register(
                registry,
                mapQuery,
                explorationService,
                eventDeckService,
                resourceTokenService);
            EventCardEffectExecutor.Register(
                registry,
                mapQuery,
                influenceService,
                eventDeckService,
                resourceTokenService);
            EventCardLuaCatalog.Register(registry);
            return registry;
        }

        private static GameState CreateState(out MapQueryService mapQuery)
        {
            mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var state = new GameState
            {
                GameId = "nmc012-test",
                MapId = mapQuery.Map.MapId,
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Players = new List<PlayerState>
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        CityLocationId = "A-01",
                        Resources = new ResourceSet { OriginiumShard = 3 },
                        InfluenceSupply = 30
                    },
                    new PlayerState { PlayerId = 2, CityLocationId = "G-01", InfluenceSupply = 30 }
                }
            };
            return state;
        }

        private static void FinishOpenInteractions(GameState state, string rootEffectId)
        {
            for (int guard = 0; guard < 12; guard++)
            {
                InteractionRequest request = FindAnyOpenInteraction(state);
                if (request == null) return;

                var registry = BuildRegistryForRecovery(state);
                var executor = new EffectTreeExecutor(state, registry);
                var selected = new List<NormalizedValue>();
                int selectionCount = request.MinSelections > 0 ? request.MinSelections : 1;
                for (int i = 0; i < selectionCount && i < request.CandidateIds.Count; i++)
                {
                    selected.Add(NormalizedValue.CreateStableReference("candidate", request.CandidateIds[i]));
                }
                NormalizedValue answer = request.CandidateIds.Count == 0
                    ? NormalizedValue.CreateBoolean(true)
                    : request.MaxSelections > 1
                        ? NormalizedValue.CreateArray(selected)
                        : selected[0];
                string diagnostic;
                Assert.That(executor.TrySubmitInteraction(
                    request.GetStableInteractionId(),
                    request.AnsweringPlayerId,
                    request.StateRevision,
                    answer,
                    out diagnostic), Is.True, diagnostic);
                EffectRunReport report = executor.RunUntilQuiescent();
                Assert.That(report.Faulted, Is.False, report.FaultCode);
            }
            Assert.Fail("通用交互未在限定步数内收敛：" + rootEffectId);
        }

        private static EffectRegistry BuildRegistryForRecovery(GameState state)
        {
            MapQueryService mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            return CreateRegistry(mapQuery, state, false);
        }

        private static InteractionRequest FindOpenInteraction(GameState state, string typeId)
        {
            return state.EffectRuntime.InteractionRequests.Find(request => request != null && request.Status == "open" && request.InteractionTypeId == typeId);
        }

        private static InteractionRequest FindAnyOpenInteraction(GameState state)
        {
            return state.EffectRuntime.InteractionRequests.Find(request => request != null && request.Status == "open");
        }

        private static int CountEvents(GameState state, string eventType)
        {
            int count = 0;
            for (int i = 0; i < state.EffectRuntime.RuleEvents.Count; i++)
            {
                if (state.EffectRuntime.RuleEvents[i].EventType == eventType) count++;
            }
            return count;
        }

        private static string ReadStableReference(NormalizedValue value, string name)
        {
            if (value == null || value.Kind != NormalizedValueKind.Object || value.Properties == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < value.Properties.Count; i++)
            {
                NormalizedValueEntry entry = value.Properties[i];
                if (entry != null && entry.Name == name && entry.Value != null &&
                    entry.Value.Kind == NormalizedValueKind.StableReference)
                {
                    return entry.Value.ReferenceId;
                }
            }

            return string.Empty;
        }

        private static void ResetEventCardDatabase()
        {
            typeof(EventCardDatabase).GetMethod(
                    "ResetForTests",
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
        }

        private static LuaInvocationResult InvokeEventCardScript(
            LuaScriptDefinition definition,
            string optionIndex,
            string selectedSlotId)
        {
            var payloadEntries = new List<LuaSnapshotEntry>
            {
                new LuaSnapshotEntry("optionIndex", LuaSnapshotValue.String(optionIndex)),
                new LuaSnapshotEntry("primaryInfluenceSlotId", LuaSnapshotValue.String(string.Empty)),
                new LuaSnapshotEntry(
                    "selectedInfluenceSlotIds",
                    LuaSnapshotValue.Array(string.IsNullOrEmpty(selectedSlotId)
                        ? new List<LuaSnapshotValue>()
                        : new List<LuaSnapshotValue> { LuaSnapshotValue.String(selectedSlotId) }))
            };
            var snapshot = new LuaFacadeSnapshot
            {
                ContentVersion = definition.DefinitionVersion,
                EventPayload = LuaSnapshotValue.Object(payloadEntries)
            };
            snapshot.TurnOrder.Add("p1");
            snapshot.TurnOrder.Add("p2");
            snapshot.Players.Add(new LuaPlayerSnapshot("p1", 0, 0));
            snapshot.Players.Add(new LuaPlayerSnapshot("p2", 0, 0));
            var context = new LuaInvocationContext(
                "nmc012-event",
                EventCardEffectEventTypeIds.Resolved,
                "p1",
                1,
                definition.DefinitionVersion,
                2,
                0,
                0,
                snapshot);
            return new MoonSharpLuaRuntimeHost().Invoke(definition, context);
        }
    }
}
