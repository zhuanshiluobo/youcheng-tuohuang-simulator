using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class OperationWindowFactsTests
    {
        private static int commandOrdinal;
        private static void Definitions()
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            if (!CharacterCardDatabase.IsInitialized) CharacterCardDatabase.Initialize(pack.CreateCharacters());
            if (!EventCardDatabase.IsInitialized) EventCardDatabase.InitializeExternal(pack.CreateEvents());
            if (!YC.Domain.Facilities.FacilityCardDatabase.IsInitialized) YC.Domain.Facilities.FacilityCardDatabase.InitializeExternal(pack.CreateFacilities());
        }
        private static GameState Basic(GamePhase phase = GamePhase.ActionRound1, int players = 3)
        {
            Definitions();
            var state = new GameState { GameId = "operation-window-facts", MapId = StaticMapDefinitions.FourPlayerMapId,
                Phase = phase, Round = 1, StartPlayerId = 1, CurrentPlayerId = 1, UseSeatTurnOrder = true,
                LaunchPlayerOrder = Enumerable.Range(1, players).ToList() };
            for (int id = 1; id <= players; id++)
            {
                var p = new PlayerState { PlayerId = id, Color = (PlayerColor)(id - 1), CityLocationId = phase == GamePhase.Entrance ? "" : "B-01" };
                CharacterCardDatabase.InitializePlayerHand(p); state.Players.Add(p);
            }
            return state;
        }
        private static GameCommand Command(GameCommandKind kind, int player) => new GameCommand {
            CommandId = "operation-fact-command-" + (++commandOrdinal).ToString(CultureInfo.InvariantCulture), Kind = kind, PlayerId = player };
        private static GameCommand Answer(InteractionRequest request)
        {
            var c = Command(GameCommandKind.AnswerInteraction, request.AnsweringPlayerId);
            c.Parameters["interactionId"] = request.GetStableInteractionId();
            c.Parameters["expectedRevision"] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            if (request.AnswerSchema == "boolean_accept") c.Parameters["answerValue"] = "true";
            else c.OptionIds.AddRange(request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
            return c;
        }
        private static void Submit(GameSession session, GameCommand command)
        {
            var result = session.Submit(command);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            Assert.That(session.State.EffectRuntime.CurrentOperationContext, Is.Null);
        }
        private static GameSession Session(GameState state, EffectRegistry registry, RoundExecutionService round)
        {
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            session.RegisterHandler(new EndActionCommandHandler(new RoundAdvanceService(round),
                new FinalScoringService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap())), registry, round));
            return session;
        }
        private static OperationWindowState ActionWindow(GameState state) => OperationWindowService.ReadOpenWindows(state)
            .Single(w => w.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow);
        private static string AddWaitingAction(GameState state, EffectRegistry registry, RoundExecutionService round, int answerer, bool twice)
        {
            const string type = "test.operation.required_input";
            registry.Register(new EffectRegistration(type, ctx => {
                if (ctx.GetLatestInteractionAnswer() != null)
                {
                    if (twice && ctx.Node.FlowStage == "first") return EffectStepResult.Continue("second").AddInteraction(
                        new EffectInteractionSpec { InteractionTypeId = "test.operation.second", AnsweringPlayerId = answerer, AnswerSchema = "boolean_accept" });
                    return EffectStepResult.Completed();
                }
                return EffectStepResult.Continue("first").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = "test.operation.first", AnsweringPlayerId = answerer, AnswerSchema = "boolean_accept" });
            }, EffectExecutorKind.IntrinsicFlow));
            var engine = round.CreateCommandExecutor(state);
            Assert.That(engine.TryCreatePlayerActionEffect(1, EffectSpec.Create(type, playerId: answerer), "1", "test.operation", out var id), Is.True);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            return id;
        }

        [Test]
        public void SameActorContinuesOneWindowAndTheNextActorGetsARealBoundary()
        {
            var state = Basic(); var registry = new EffectRegistry(); var round = new RoundExecutionService(registry);
            Assert.That(round.PrepareActionWindowForSmoke(state, 1).IsValid, Is.True);
            var id = ActionWindow(state).OperationWindowId;
            AddWaitingAction(state, registry, round, 1, true);
            var session = Session(state, registry, round);
            var first = Answer(state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == "test.operation.first"));
            Submit(session, first);
            var secondRequest = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == "test.operation.second");
            Assert.That(ActionWindow(state).OperationWindowId, Is.EqualTo(id));
            Assert.That(secondRequest.OperationContext.SourceCommandId, Is.EqualTo(first.CommandId));
            var second = Answer(secondRequest); Submit(session, second);
            Assert.That(ActionWindow(state).OperationWindowId, Is.EqualTo(id));
            Assert.That(state.EffectRuntime.RuleEvents.Single(e => e.EventType == "InteractionAnswered" && e.OperationContext.SourceCommandId == second.CommandId).OperationContext.MainWindowPlayerIdAtCreation, Is.EqualTo(1));
            round.MarkMainActionComplete(state, 1);
            Submit(session, Command(GameCommandKind.EndAction, 1));
            Assert.That(ActionWindow(state).OperationWindowId == id, Is.False);
            Assert.That(ActionWindow(state).AuthorizedPlayerIds, Is.EqualTo(new[] { 2 }));
            Assert.That(state.EffectRuntime.OperationWindows.Single(w => w.OperationWindowId == id).Lifecycle, Is.EqualTo(OperationWindowLifecycle.Closed));
            var before = GameStateCloneService.DeepClone(state);
            var read = OperationWindowService.ReadOpenWindows(state); read[0].AuthorizedPlayerIds.Clear();
            Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True);
        }

        [Test]
        public void IndependentEnterpriseCoverAndCollectionKeepOneFixedContract()
        {
            var formal = BuildHost(true);
            var enterprise = OperationWindowService.ReadOpenWindows(formal.Item1.State).Single(w => w.WindowKind == "enterprise_selection");
            Assert.That(enterprise.AuthorizedPlayerIds, Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(enterprise.AvailablePlayerIds, Is.EqualTo(new[] { 1, 2, 3 }));
            var selected = formal.Item1.State.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.AnsweringPlayerId == 3);
            Submit(formal.Item1, Answer(selected));
            var still = OperationWindowService.ReadOpenWindows(formal.Item1.State).Single(w => w.OperationWindowId == enterprise.OperationWindowId);
            Assert.That(still.AuthorizedPlayerIds, Is.EqualTo(enterprise.AuthorizedPlayerIds));
            Assert.That(still.AvailablePlayerIds.Contains(1) && still.AvailablePlayerIds.Contains(2), Is.True);

            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var source = Basic(GamePhase.Setup);
            source.ContentPackHash = "operation-fixture-content";
            source.RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 3, ContentPackHash = source.ContentPackHash };
            foreach (var p in source.Players) { p.HandCardIds.Clear(); p.Resources.GoldVoucher = 20; }
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, new[] {
                new ItemRecipeEntry { EntryId = "characters", DefinitionId = "texas", TypeId = "core.basic_character_card", Quantity = 2, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "cities", DefinitionId = "core.mobile_city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" },
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.component.marker", TypeId = "core.player_marker", Quantity = 6, PerPlayer = true, InitialZoneRole = "supply" } });
            for (int p = 1; p <= 3; p++)
            {
                var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == p);
                var location = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == new[] { "A-01", "B-01", "C-01" }[p - 1]);
                var dock = state.ItemZones.Zones.Single(z => z.ParentId == location.ZoneId && z.TemplateId == "core.zone.city_dock");
                typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { state, city, dock.ZoneId });
            }
            var registry = new EffectRegistry(); var round = new RoundExecutionService(registry);
            Assert.That(round.CreateRound(state, 1, 1).IsValid, Is.True);
            var session = Session(state, registry, round);
            var cover = OperationWindowService.ReadOpenWindows(state).Single(w => w.WindowKind == RoundMainlineNodeTypeIds.CharacterCover);
            Assert.That(cover.AvailablePlayerIds, Is.EqualTo(new[] { 1, 2, 3 }));
            var request = state.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId);
            Submit(session, Answer(request));
            Assert.That(OperationWindowService.ReadOpenWindows(state).Single(w => w.OperationWindowId == cover.OperationWindowId).AuthorizedPlayerIds, Is.EqualTo(cover.AuthorizedPlayerIds));
            for (int guard = 0; state.Phase == GamePhase.CharacterCover && guard < 10; guard++)
                Submit(session, Answer(state.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId)));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            for (int cycle = 0; cycle < 2; cycle++) for (int p = 1; p <= 3; p++) Assert.That(round.CompleteMainAction(state, p).IsValid, Is.True);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            var collection = OperationWindowService.ReadOpenWindows(state).Single(w => w.WindowKind == RoundMainlineNodeTypeIds.Collection);
            Assert.That(collection.AvailablePlayerIds, Is.EqualTo(new[] { 1, 2, 3 }));
            foreach (var r in state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection"))
            {
                Assert.That(OperationWindowService.TryResolveCommandContext(state, Answer(r), out var c, out _), Is.True);
                Assert.That(c.OperationWindowId, Is.EqualTo(collection.OperationWindowId));
                Assert.That(c.MainWindowPlayerIdAtCreation, Is.EqualTo(r.AnsweringPlayerId));
            }
            var dto = OperationWindowProjection.ToDescriptor(state, collection, 4, 2, 11, 0, new[] { "host", "client" }, "prefix", "window");
            Assert.That(dto.SimulationEpoch, Is.EqualTo(4)); Assert.That(dto.AuthorizedPlayerIds, Is.EqualTo(new[] { 1, 2, 3 }));
            dto.AuthorizedPlayerIds.Clear(); Assert.That(collection.AuthorizedPlayerIds.Count, Is.EqualTo(3));
        }

        [Test]
        public void EntrancePlacementPipelineKeepsThePreviousPlayersRequiredWork()
        {
            var state = Basic(GamePhase.Entrance, 4); state.Round = 0;
            foreach (string location in StaticMapDefinitions.FourPlayerInitialLocationIds)
                if (location != "B-01") state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = location, ResourceType = ResourceType.Iron, Amount = 1 });
            state.Decks.EventDeckGreen.Add("event_green_01");
            var handler = new SetupCommandHandler(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap())); EventCardLuaCatalog.Register(handler.EffectRegistry);
            var round = new RoundExecutionService(handler.EffectRegistry); Assert.That(round.StartEntrance(state).IsValid, Is.True);
            var session = Session(state, handler.EffectRegistry, round); session.RegisterHandler(handler);
            var first = Command(GameCommandKind.ChooseInitialLocation, 1); first.TargetId = "B-01";
            var r = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.AnsweringPlayerId == 1 && r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId);
            first.Parameters["interactionId"] = r.GetStableInteractionId(); first.Parameters["expectedRevision"] = r.StateRevision.ToString(CultureInfo.InvariantCulture);
            Submit(session, first);
            var windows = OperationWindowService.ReadOpenWindows(state);
            var prior = windows.Single(w => w.WindowKind == "entrance_player" && w.AuthorizedPlayerIds.Contains(1));
            Assert.That(prior.AvailablePlayerIds, Is.EqualTo(new[] { 1 }));
            Assert.That(windows.Any(w => w.WindowKind == "entrance_player" && w.AvailablePlayerIds.Contains(2)), Is.True);
            Assert.That(windows.Any(w => w.AvailablePlayerIds.Contains(3)), Is.False);
            var second = Command(GameCommandKind.ChooseInitialLocation, 2); second.TargetId = "A-01";
            r = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.AnsweringPlayerId == 2 && r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId);
            second.Parameters["interactionId"] = r.GetStableInteractionId(); second.Parameters["expectedRevision"] = r.StateRevision.ToString(CultureInfo.InvariantCulture);
            Submit(session, second);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
            Assert.That(OperationWindowService.ReadOpenWindows(state).Any(w => w.OperationWindowId == prior.OperationWindowId && w.AvailablePlayerIds.Contains(1)), Is.True);
        }

        [Test]
        public void RequiredResponseKeepsItsSourceAndRecoveryPreservesWindowFacts()
        {
            var state = Basic(); var registry = new EffectRegistry(); var round = new RoundExecutionService(registry);
            Assert.That(round.PrepareActionWindowForSmoke(state, 1).IsValid, Is.True);
            string origin = ActionWindow(state).OperationWindowId;
            var nodeId = AddWaitingAction(state, registry, round, 2, false);
            var session = Session(state, registry, round);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == "test.operation.first");
            var answer = Answer(request);
            Assert.That(ActionWindow(state).AvailablePlayerIds, Is.Empty);
            Assert.That(OperationWindowService.TryResolveCommandContext(state, answer, out var context, out _), Is.True);
            Assert.That(context.WindowKind, Is.EqualTo(OperationWindowService.RequiredResponse));
            Assert.That(context.ActingPlayerId, Is.EqualTo(2)); Assert.That(context.MainWindowPlayerIdAtCreation, Is.EqualTo(1));
            Assert.That(context.OriginMainOperationWindowId, Is.EqualTo(origin));
            var before = GameStateCloneService.DeepClone(state);
            Assert.That(session.Submit(Command(GameCommandKind.EndAction, 1)).Succeeded, Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True);
            answer.Parameters["expectedRevision"] = (state.EffectRuntime.StateRevision + 1).ToString(CultureInfo.InvariantCulture);
            Assert.That(session.Submit(answer).Succeeded, Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True);
            Assert.That(state.EffectRuntime.Blockers.Any(b => b.TargetEffectId == nodeId && !b.IsResolved), Is.True);
            Submit(session, Answer(request));
            var evt = state.EffectRuntime.RuleEvents.Last(e => e.EventType == "InteractionAnswered");
            Assert.That(evt.OperationContext.ActingPlayerId, Is.EqualTo(2)); Assert.That(evt.OperationContext.MainWindowPlayerIdAtCreation, Is.EqualTo(1));

            var formal = BuildHost(true); var actual = formal.Item1.State;
            Submit(formal.Item1, Answer(actual.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.AnsweringPlayerId == 3)));
            var windows = JsonConvert.SerializeObject(OperationWindowService.ReadOpenWindows(actual));
            var json = HostRecoveryService.SerializeArchive(formal.Item1.CreateHostArchive());
            var recovery = HostRecoveryService.RecoverJson(json, formal.Item2);
            Assert.That(recovery.Succeeded, Is.True, recovery.Diagnostic);
            Assert.That(JsonConvert.SerializeObject(OperationWindowService.ReadOpenWindows(recovery.State)), Is.EqualTo(windows));
            Assert.That(GameStateCloneService.AreEquivalent(actual, GameStateCloneService.DeepClone(actual)), Is.True);
            var corrupt = JObject.Parse(json); ((JArray)corrupt["Snapshot"]["State"]["EffectRuntime"]["OperationWindows"])[0]["SourceEffectId"] = "missing";
            Assert.That(HostRecoveryService.RecoverJson(corrupt.ToString(), formal.Item2).Succeeded, Is.False);
            var badCause = JObject.Parse(json);
            var cause = ((JArray)badCause["Snapshot"]["State"]["EffectRuntime"]["RuleEvents"]).First(e => e["OperationContext"]?.Type == JTokenType.Object);
            cause["OperationContext"]["WindowFacts"]["OperationWindowId"] = "forged";
            Assert.That(HostRecoveryService.RecoverJson(badCause.ToString(), formal.Item2).Succeeded, Is.False);
            var badOrigin = JObject.Parse(json);
            var originCause = ((JArray)badOrigin["Snapshot"]["State"]["EffectRuntime"]["RuleEvents"]).First(e => e["OperationContext"]?.Type == JTokenType.Object);
            originCause["OperationContext"]["OriginMainOperationWindowId"] = "forged-origin";
            Assert.That(HostRecoveryService.RecoverJson(badOrigin.ToString(), formal.Item2).Succeeded, Is.False);
            var legacy = JObject.Parse(json); var runtime = (JObject)legacy["Snapshot"]["State"]["EffectRuntime"];
            foreach (var key in new[] { "OperationWindows", "OperationWindowSchemaVersion", "NextOperationWindowOrdinal", "CurrentOperationContext" }) runtime.Remove(key);
            foreach (var entry in legacy.Descendants().OfType<JObject>().ToArray())
            { entry.Remove("OperationContext"); entry.Remove("ContinuationOperationContext"); }
            var old = HostRecoveryService.RecoverJson(legacy.ToString(), formal.Item2);
            Assert.That(old.Succeeded, Is.True, old.Diagnostic); Assert.That(old.State.EffectRuntime.OperationWindowSchemaVersion, Is.EqualTo(0));
            var oldBefore = GameStateCloneService.DeepClone(old.State);
            Assert.That(OperationWindowService.ReadOpenWindows(old.State), Is.Empty); Assert.That(GameStateCloneService.AreEquivalent(old.State, oldBefore), Is.True);
            OperationWindowService.Synchronize(old.State); Assert.That(OperationWindowService.TryValidate(old.State, out _), Is.True);

            // 自动输入根没有真实操作者时保留未知值，不能用回答者或CurrentPlayerId补造主操作来源。
            var automatic = Basic(); var automaticRegistry = new EffectRegistry();
            automaticRegistry.Register(new EffectRegistration("test.operation.automatic_input", c => {
                string stage = c.Node.FlowStage == "" ? "first" : c.Node.FlowStage == "first" ? "second" : "third";
                if (c.Node.FlowStage == "third") return EffectStepResult.Completed();
                return EffectStepResult.Continue(stage).AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = "test.operation.automatic_input." + stage, AnsweringPlayerId = stage == "second" ? 3 : 2, AnswerSchema = "boolean_accept" });
            }, EffectExecutorKind.IntrinsicFlow));
            var automaticEngine = new EffectTreeExecutor(automatic, automaticRegistry);
            automaticEngine.CreateRoot(EffectSpec.Create("test.operation.automatic_input", playerId: -1)); automaticEngine.RunUntilQuiescent();
            var automaticRequest = automatic.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(OperationWindowService.TryResolveCommandContext(automatic, Answer(automaticRequest), out var automaticContext, out _), Is.True);
            Assert.That(automaticContext.ActingPlayerId, Is.EqualTo(2)); Assert.That(automaticContext.MainWindowPlayerIdAtCreation, Is.EqualTo(-1));
            var automaticSession = new GameSession(automatic); automaticSession.RegisterHandler(new AnswerInteractionCommandHandler(automaticRegistry));
            string firstWindow = automaticContext.OperationWindowId;
            Submit(automaticSession, Answer(automaticRequest));
            var nextRequest = automatic.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(OperationWindowService.TryResolveCommandContext(automatic, Answer(nextRequest), out var otherActor, out _), Is.True);
            Assert.That(otherActor.OperationWindowId == firstWindow, Is.False);
            Submit(automaticSession, Answer(nextRequest));
            nextRequest = automatic.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(OperationWindowService.TryResolveCommandContext(automatic, Answer(nextRequest), out var returnActor, out _), Is.True);
            Assert.That(returnActor.WindowGeneration, Is.EqualTo(2)); Assert.That(returnActor.OperationWindowId == firstWindow, Is.False);
            Submit(automaticSession, Answer(nextRequest));
        }

        private static Tuple<GameSession, EffectRegistry> BuildHost(bool enterprise)
        {
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var instance = contextType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static); var previous = instance.GetValue(null); var setter = instance.GetSetMethod(true);
            GameObject host = null;
            try
            {
                setter.Invoke(null, new object[] { null }); host = new GameObject("OperationWindowFacts"); var context = host.AddComponent(contextType);
                var random = GameplayRandomRuntime.Create("operation-formal-match");
                var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3,
                    EnabledExpansionIds = enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" } };
                var seats = Enumerable.Range(1, 3).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "玩家" + p }).ToList();
                contextType.GetMethods().Single(m => m.Name == "Configure" && m.GetParameters().Length == 9).Invoke(context,
                    new object[] { LaunchMode.Host, 1, "operation-formal-room", seats, config, new[] { 1, 2, 3 }, "operation-formal-match", 42, random });
                var result = bootstrapper.GetMethod("Build", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { context, false, false });
                return Tuple.Create((GameSession)result.GetType().GetField("Session").GetValue(result), (EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result));
            }
            finally { if (host != null) UnityEngine.Object.DestroyImmediate(host); setter.Invoke(null, new[] { previous }); }
        }
    }
}
