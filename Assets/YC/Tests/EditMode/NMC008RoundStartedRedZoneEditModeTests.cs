using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class NMC008RoundStartedRedZoneEditModeTests
    {
        [TestCaseSource(nameof(RedZoneScheduleCases))]
        public void RoundStarted_LuaSelectsRedZoneAndContinuesMainChain(
            int playerCount,
            int round,
            bool shouldOpen)
        {
            MapQueryService mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            EffectRegistry registry = CreateRedZoneRegistry(mapQuery);
            RoundExecutionService service = CreateRoundService(registry, playerCount);
            GameState state = CreateState("nmc008-schedule-" + playerCount + "-" + round, playerCount, mapQuery.Map.MapId);

            ValidationResult result = service.CreateRound(state, round, 1);

            AssertValid(result);
            Assert.That(GetActiveNode(state).NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.CharacterCover));
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
            Assert.That(Sorted(state.Map.OpenLocationIds), Is.EqualTo(
                shouldOpen ? Sorted(StaticMapDefinitions.FourPlayerRedZoneLocationIds) : new List<string>()));

            RuleEvent roundStarted = state.EffectRuntime.RuleEvents.Find(candidate => candidate.EventType == "RoundStarted");
            Assert.That(roundStarted, Is.Not.Null);
            DispatchReceipt receipt = state.EffectRuntime.DispatchReceipts.Find(candidate =>
                candidate.EventId == roundStarted.EventId &&
                candidate.SubscriptionId == RoundStartedRedZoneRule.SubscriptionId);
            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.AttachedEffectIds, Has.Count.EqualTo(shouldOpen ? 1 : 0));

            if (shouldOpen)
            {
                EffectNodeRuntimeState opening = state.EffectRuntime.EffectNodes.Find(candidate =>
                    receipt.AttachedEffectIds.Contains(candidate.EffectId));
                Assert.That(opening, Is.Not.Null);
                Assert.That(opening.EffectTypeId, Is.EqualTo(SetLocationsOpenEffectExecutor.EffectTypeId));
                Assert.That(opening.Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(state.EffectRuntime.RuleEvents, Has.Some.Matches<RuleEvent>(candidate =>
                    candidate.EventType == "LocationsOpenStateChanged"));
                Assert.That(state.Logs, Has.Count.EqualTo(1));
            }
            else
            {
                Assert.That(state.EffectRuntime.RuleEvents, Has.None.Matches<RuleEvent>(candidate =>
                    candidate.EventType == "LocationsOpenStateChanged"));
                Assert.That(state.Logs, Is.Empty);
            }

            MainlineNodeRuntimeState cover = GetActiveNode(state);
            EffectNodeRuntimeState roundStartedEffect = state.EffectRuntime.EffectNodes.Find(candidate =>
                candidate.EffectId == state.EffectRuntime.MainNodes[0].ExecutionEffectId);
            Assert.That(roundStartedEffect.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(cover.Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(state.EffectRuntime.InteractionRequests, Has.Some.Matches<InteractionRequest>(request =>
                request.OwnerEffectId == cover.ExecutionEffectId && request.Status == "open"));
        }

        [TestCaseSource(nameof(SetLocationsOpenCases))]
        public void SetLocationsOpen_ContractCasesAreAtomicAndExplicit(
            string caseName,
            string[] requestedIds,
            bool isOpen,
            string[] initialOpenIds,
            EffectNodeStatus expectedStatus,
            string[] expectedChangedIds,
            string[] expectedOpenIds)
        {
            MapQueryService mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            EffectRegistry registry = new EffectRegistry();
            SetLocationsOpenEffectExecutor.Register(registry, mapQuery);
            GameState state = CreateState("nmc008-contract-" + caseName, 4, mapQuery.Map.MapId);
            state.Map.OpenLocationIds.AddRange(initialOpenIds);
            EffectTreeExecutor executor = new EffectTreeExecutor(state, registry);

            string effectId = executor.CreateRoot(CreateSetSpec(requestedIds, isOpen));
            EffectRunReport report = executor.RunUntilQuiescent();
            EffectNodeRuntimeState node = executor.GetNode(effectId);

            Assert.That(report.Faulted, Is.False, report.FaultCode);
            Assert.That(node.Status, Is.EqualTo(expectedStatus));
            Assert.That(Sorted(state.Map.OpenLocationIds), Is.EqualTo(Sorted(expectedOpenIds)));
            Assert.That(GetStringArray(node.NormalizedResult, "changedLocationIds"),
                Is.EqualTo(Sorted(expectedChangedIds)));
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
        }

        [Test]
        public void RoundStarted_WithNoSubscriber_DirectlyPassesThroughWithoutEmptyInteraction()
        {
            GameState state = CreateState("nmc008-no-subscriber", 4, StaticMapDefinitions.FourPlayerMapId);
            RoundExecutionService service = new RoundExecutionService();

            AssertValid(service.CreateRound(state, 4, 1));

            MainlineNodeRuntimeState active = GetActiveNode(state);
            Assert.That(active.NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.CharacterCover));
            MainlineNodeRuntimeState roundStarted = state.EffectRuntime.MainNodes[0];
            EffectNodeRuntimeState execution = state.EffectRuntime.EffectNodes.Find(candidate =>
                candidate.EffectId == roundStarted.ExecutionEffectId);
            Assert.That(execution.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(execution.ChildEffectIds, Is.Empty);
            Assert.That(state.EffectRuntime.InteractionRequests, Has.None.Matches<InteractionRequest>(request =>
                request.OwnerEffectId == execution.EffectId));
        }

        [Test]
        public void RoundStarted_WithEmptyLuaResponse_WritesReceiptAndDirectlyPassesThrough()
        {
            MapQueryService mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            EffectRegistry registry = new EffectRegistry();
            LuaScriptDefinition definition = new LuaScriptDefinition(
                "content.nmc008.empty",
                "ability.nmc008.empty",
                "handler.nmc008.empty",
                "1.0.0",
                "return function(ctx) return {} end",
                LuaContentHasher.ComputeSha256("return function(ctx) return {} end"));
            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition(
                "nmc008.empty",
                "RoundStarted",
                string.Empty,
                definition,
                EffectHandlerRole.Primary));
            RoundExecutionService service = CreateRoundService(registry, 4);
            GameState state = CreateState("nmc008-empty", 4, mapQuery.Map.MapId);

            AssertValid(service.CreateRound(state, 4, 1));

            MainlineNodeRuntimeState active = GetActiveNode(state);
            MainlineNodeRuntimeState roundStarted = state.EffectRuntime.MainNodes[0];
            EffectNodeRuntimeState execution = state.EffectRuntime.EffectNodes.Find(candidate =>
                candidate.EffectId == roundStarted.ExecutionEffectId);
            DispatchReceipt receipt = state.EffectRuntime.DispatchReceipts.Find(candidate =>
                candidate.SubscriptionId == "nmc008.empty");
            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.AttachedEffectIds, Is.Empty);
            Assert.That(execution.ChildEffectIds, Is.Empty);
            Assert.That(execution.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(active.NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.CharacterCover));
            Assert.That(state.Map.OpenLocationIds, Is.Empty);
        }

        [Test]
        public void RoundStarted_AfterRecoveryDoesNotReplayOpenEventReceiptOrVisibleLog()
        {
            MapQueryService mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            EffectRegistry registry = CreateRedZoneRegistry(mapQuery);
            RoundExecutionService service = CreateRoundService(registry, 4);
            GameState state = CreateState("nmc008-recovery", 4, mapQuery.Map.MapId);

            AssertValid(service.CreateRound(state, 4, 1));
            GameState recovered = GameStateCloneService.DeepClone(state);
            int eventCount = recovered.EffectRuntime.RuleEvents.Count;
            int receiptCount = recovered.EffectRuntime.DispatchReceipts.Count;
            int logCount = recovered.Logs.Count;
            int journalCount = recovered.EffectRuntime.Journal.Count;

            EffectRunReport replay = new EffectTreeExecutor(recovered, registry).RunUntilQuiescent();
            Assert.That(replay.Faulted, Is.False, replay.FaultCode);
            AssertValid(service.Advance(recovered));
            Assert.That(recovered.EffectRuntime.RuleEvents, Has.Count.EqualTo(eventCount));
            Assert.That(recovered.EffectRuntime.DispatchReceipts, Has.Count.EqualTo(receiptCount));
            Assert.That(recovered.Logs, Has.Count.EqualTo(logCount));
            Assert.That(recovered.EffectRuntime.Journal, Has.Count.EqualTo(journalCount));
            Assert.That(Sorted(recovered.Map.OpenLocationIds), Is.EqualTo(
                Sorted(StaticMapDefinitions.FourPlayerRedZoneLocationIds)));
        }

        private static IEnumerable<TestCaseData> RedZoneScheduleCases()
        {
            yield return new TestCaseData(2, 5, false).SetName("2人第5回合不开放");
            yield return new TestCaseData(2, 6, true).SetName("2人第6回合开放");
            yield return new TestCaseData(3, 4, false).SetName("3人第4回合不开放");
            yield return new TestCaseData(3, 5, true).SetName("3人第5回合开放");
            yield return new TestCaseData(4, 3, false).SetName("4人第3回合不开放");
            yield return new TestCaseData(4, 4, true).SetName("4人第4回合开放");
        }

        private static IEnumerable<TestCaseData> SetLocationsOpenCases()
        {
            yield return new TestCaseData(
                "duplicate-no-change",
                new[] { "F-01", "E-02", "F-01" },
                true,
                new[] { "E-02", "F-01" },
                EffectNodeStatus.Completed,
                new string[0],
                new[] { "E-02", "F-01" });
            yield return new TestCaseData(
                "unknown-location",
                new[] { "Z-99" },
                true,
                new string[0],
                EffectNodeStatus.Failed,
                new string[0],
                new string[0]);
            yield return new TestCaseData(
                "illegal-region",
                new[] { "A-01" },
                true,
                new string[0],
                EffectNodeStatus.Failed,
                new string[0],
                new string[0]);
            var tooMany = new List<string>();
            for (int i = 0; i < SetLocationsOpenEffectExecutor.MaxLocationCount + 1; i++)
            {
                tooMany.Add("F-01-" + i);
            }

            yield return new TestCaseData(
                "too-many-input",
                tooMany.ToArray(),
                true,
                new string[0],
                EffectNodeStatus.Failed,
                new string[0],
                new string[0]);
        }

        private static EffectRegistry CreateRedZoneRegistry(IMapQueryService mapQuery)
        {
            var registry = new EffectRegistry();
            RoundStartedRedZoneRule.Register(registry, mapQuery);
            return registry;
        }

        private static RoundExecutionService CreateRoundService(EffectRegistry registry, int playerCount)
        {
            var turnOrder = new TurnOrderService();
            return new RoundExecutionService(
                turnOrder,
                new CharacterCardService(turnOrder),
                new MainActionBudgetService(),
                new SpecialActionLifecycleService(),
                registry);
        }

        private static GameState CreateState(string gameId, int playerCount, string mapId)
        {
            var state = new GameState
            {
                GameId = gameId,
                MapId = mapId,
                Phase = GamePhase.Setup,
                StartPlayerId = 1,
                Players = new List<PlayerState>()
            };
            for (int i = 1; i <= playerCount; i++)
            {
                state.Players.Add(new PlayerState { PlayerId = i, Color = (PlayerColor)(i - 1) });
            }

            return state;
        }

        private static EffectSpec CreateSetSpec(IList<string> locationIds, bool isOpen)
        {
            var locations = new List<NormalizedValue>();
            for (int i = 0; i < locationIds.Count; i++)
            {
                locations.Add(NormalizedValue.CreateStableReference("location", locationIds[i]));
            }

            return EffectSpec.Create(
                SetLocationsOpenEffectExecutor.EffectTypeId,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry
                    {
                        Name = "isOpen",
                        Value = NormalizedValue.CreateBoolean(isOpen)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "locations",
                        Value = NormalizedValue.CreateArray(locations)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "reasonId",
                        Value = NormalizedValue.CreateString("test.nmc008")
                    }
                }));
        }

        private static List<string> GetStringArray(NormalizedValue result, string propertyName)
        {
            NormalizedValue value = GetProperty(result, propertyName);
            var ids = new List<string>();
            if (value != null && value.Kind == NormalizedValueKind.Array && value.Items != null)
            {
                for (int i = 0; i < value.Items.Count; i++) ids.Add(value.Items[i].StringValue);
            }

            return ids;
        }

        private static NormalizedValue GetProperty(NormalizedValue value, string name)
        {
            if (value == null || value.Kind != NormalizedValueKind.Object || value.Properties == null) return null;
            for (int i = 0; i < value.Properties.Count; i++)
            {
                if (value.Properties[i] != null && value.Properties[i].Name == name) return value.Properties[i].Value;
            }

            return null;
        }

        private static MainlineNodeRuntimeState GetActiveNode(GameState state)
        {
            return state.EffectRuntime.MainNodes.Find(candidate =>
                candidate.NodeId == state.EffectRuntime.ActiveMainNodeId);
        }

        private static List<string> Sorted(ICollection<string> values)
        {
            var result = new List<string>(values ?? new List<string>());
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static void AssertValid(ValidationResult result)
        {
            Assert.That(result.IsValid, Is.True, result.Reason);
        }
    }
}
