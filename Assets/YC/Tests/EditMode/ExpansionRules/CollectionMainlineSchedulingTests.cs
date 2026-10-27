using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CollectionMainlineSchedulingTests
    {
        private const string StartResponseType = "test.collection.started_response";
        private IsolatedTestArtifacts artifacts;
        private ExternalContentPack pack;

        [SetUp]
        public void LoadDefinitions()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            string root = artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "009主链独占内容");
            pack = ExternalContentPack.Load(root); artifacts.RecordContent("009主链实际内容", root, pack.ContentHash);
            if (!CharacterCardDatabase.IsInitialized)
                CharacterCardDatabase.Initialize(pack.CreateCharacters());
        }
        [TearDown] public void FinishEvidence() => artifacts?.Finish();

        [Test]
        public void NormalCollection_MainlineOpensAllPlayersAndAcceptsNonFirstPlayerBeforeFirst()
        {
            var state = EnterCollection(new EffectRegistry(), out var round);
            var requests = OpenSelections(state);
            Assert.That(requests.Select(r => r.AnsweringPlayerId), Is.EquivalentTo(new[] { 1, 2, 3 }));
            var scopes = requests.ToDictionary(r => r.AnsweringPlayerId, Scope);
            var startCount = state.EffectRuntime.RuleEvents.Count(e => e.EventType == "CollectionStarted");

            SubmitEmpty(state, round, 2);
            Assert.That(state.FindPlayer(2).HasCollectedResourcesThisRound, Is.True);
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
            Assert.That(state.FindPlayer(3).HasCollectedResourcesThisRound, Is.False);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            var remainingIds = OpenSelections(state).Select(r => r.InteractionId).ToArray();
            state = Restore(state, round.EffectRegistry);
            round = new RoundExecutionService(round.EffectRegistry);
            Assert.That(round.Advance(state).IsValid, Is.True);
            Assert.That(OpenSelections(state).Select(r => r.InteractionId), Is.EquivalentTo(remainingIds));
            foreach (var id in new[] { 1, 3 })
            {
                var request = OpenSelections(state).Single(r => r.AnsweringPlayerId == id);
                Assert.That(Scope(request), Is.EqualTo(scopes[id]));
                Assert.That(state.FindPlayer(id).ResourceCollectionStartGoldVoucher, Is.EqualTo(20));
                Assert.That(Long(request.PromptParameters, "startGold"), Is.EqualTo(20));
            }
            SubmitEmpty(state, round, 3);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            SubmitEmpty(state, round, 1);
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.Players.All(p => !p.HasCollectedResourcesThisRound), Is.True);
            Assert.That(state.Players.All(p => p.Resources.GoldVoucher == 20), Is.True);
            Assert.That(OpenSelections(state), Is.Empty);
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "CollectionStarted"), Is.EqualTo(startCount));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "RoundEnded"), Is.EqualTo(1));
            var nextRoundNodes = state.EffectRuntime.MainNodes.Where(n => n.RoundNumber == 2).Select(n => n.NodeId).ToArray();
            Assert.That(round.Advance(state).IsValid, Is.True);
            Assert.That(round.Advance(state).IsValid, Is.True);
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.MainNodes.Where(n => n.RoundNumber == 2).Select(n => n.NodeId), Is.EquivalentTo(nextRoundNodes));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "RoundEnded"), Is.EqualTo(1));
        }

        [Test]
        public void CollectionStartedResponse_WaitsAcrossHostRestoreAndCannotIncreaseStartBudget()
        {
            var registry = new EffectRegistry();
            registry.Register(new EffectRegistration(StartResponseType, context =>
            {
                if (context.GetLatestInteractionAnswer() != null)
                {
                    context.State.FindPlayer(1).Resources.GoldVoucher += 7;
                    return EffectStepResult.Completed();
                }
                return EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec
                {
                    InteractionTypeId = StartResponseType, AnsweringPlayerId = 1, AnswerSchema = "boolean_accept"
                });
            }, EffectExecutorKind.IntrinsicFlow));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.collection.start_subscription",
                "CollectionStarted", "", "test.collection.start_handler",
                context => new List<EffectSpec> { EffectSpec.Create(StartResponseType, playerId: 1) }));
            var state = EnterCollection(registry, out var round);
            Assert.That(OpenSelections(state), Is.Empty);
            var main = state.EffectRuntime.MainNodes.Single(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId);
            var collectionOwner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == main.ExecutionEffectId);
            Assert.That(collectionOwner.FlowStage, Is.EqualTo("collection_started"));
            var frozen = collectionOwner.ContinuationData.Properties.Single(p => p.Name == "collectionStartGoldVoucherAmounts").Value;
            Assert.That(frozen.Items.Select(value => Long(value, "amount")), Is.EqualTo(new long[] { 20, 20, 20 }));
            var waiting = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == StartResponseType);
            var id = waiting.InteractionId;
            var initialNodes = state.EffectRuntime.EffectNodes.Count;
            state = Restore(state, registry);
            round = new RoundExecutionService(registry);
            Assert.That(round.Advance(state).IsValid, Is.True);
            Assert.That(OpenSelections(state), Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(initialNodes));
            waiting = state.EffectRuntime.InteractionRequests.Single(r => r.InteractionId == id);
            Submit(state, round, waiting, NormalizedValue.CreateBoolean(true));
            Assert.That(OpenSelections(state).Select(r => r.AnsweringPlayerId), Is.EquivalentTo(new[] { 1, 2, 3 }));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(27));
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(20));
            Assert.That(Long(OpenSelections(state).Single(r => r.AnsweringPlayerId == 1).PromptParameters,
                "startGold"), Is.EqualTo(20));
            foreach (var request in OpenSelections(state))
            {
                var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
                Assert.That(owner.NormalizedArguments.Properties.Single(p => p.Name == "budgetOwnerEffectId").Value.StringValue,
                    Is.EqualTo(main.ExecutionEffectId));
            }
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "CollectionStarted"), Is.EqualTo(1));
        }

        [Test]
        public void FailedCollectionTask_DoesNotPublishCompletionOrAdvanceTheRound()
        {
            var actual = new EffectRegistry(); RouteResourceSelectionEffectExecutor.Register(actual);
            var original = actual.Get(RouteResourceSelectionEffectExecutor.TypeId);
            var registry = new EffectRegistry();
            registry.Register(new EffectRegistration(original.EffectTypeId, context => context.Node.PlayerId == 2
                ? EffectStepResult.Failed("test_collection_payment_failed") : original.Executor(context),
                original.ExecutorKind, original.DefinitionVersion));
            var state = EnterCollection(registry, out var round);
            Assert.That(OpenSelections(state).Select(r => r.AnsweringPlayerId), Is.EquivalentTo(new[] { 1, 3 }));
            var active = state.EffectRuntime.ActiveMainNodeId;
            SubmitEmpty(state, round, 1);
            SubmitEmpty(state, round, 3, allowCollectionFailure: true);
            Assert.That(state.Round, Is.EqualTo(1));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.EqualTo(active));
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(state.EffectRuntime.LastFaultCode, Is.EqualTo("collection_failed"), state.EffectRuntime.LastFaultMessage);
            Assert.That(state.EffectRuntime.LastFaultMessage, Is.EqualTo("test_collection_payment_failed"));
            Assert.That(state.FindPlayer(2).HasCollectedResourcesThisRound, Is.False);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "CollectionCompleted"), Is.False);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "RoundEnded" || e.EventType == "PlayerCleanupStarted"), Is.False);
            var commitCount = state.EffectRuntime.Journal.Count;
            Assert.That(round.Advance(state).IsValid, Is.False);
            Assert.That(state.EffectRuntime.Journal.Count, Is.EqualTo(commitCount));
        }

        [Test]
        public void LegacyCompletionFlag_WithOpenCommonRequest_IsRejectedWithoutChangingState()
        {
            var state = EnterCollection(new EffectRegistry(), out var round);
            state.FindPlayer(1).HasCollectedResourcesThisRound = true;
            var before = JsonConvert.SerializeObject(state);
            var result = round.CompleteResourceCollection(state, 1);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo(CommandErrorCode.PendingChoiceRequired));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(OpenSelections(state).Select(r => r.AnsweringPlayerId), Is.EquivalentTo(new[] { 1, 2, 3 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyCollectionOpen_RestoresExistingTasksWithoutRebuildingOrReissuing(bool commonSelection)
        {
            var prototype = new RoundExecutionService();
            var original = prototype.EffectRegistry.Get(RoundExecutionService.MainlineEffectTypeId);
            var registry = new EffectRegistry();
            registry.Register(new EffectRegistration(original.EffectTypeId, context =>
            {
                var main = context.State.EffectRuntime.MainNodes.Single(n => n.ExecutionEffectId == context.Node.EffectId);
                if (main.NodeTypeId != RoundMainlineNodeTypeIds.Collection || context.Node.FlowStage.Length > 0)
                    return original.Executor(context);
                var opened = EffectStepResult.Continue("collection_open");
                foreach (var player in context.State.EffectRuntime.PlayerOrderSnapshot)
                {
                    if (commonSelection) opened.AddChild(RouteResourceSelectionEffectExecutor.CreateHarvest(player, false, context.Node.SourceId));
                    else opened.AddInteraction(new EffectInteractionSpec
                    {
                        InteractionTypeId = "collection.task", AnsweringPlayerId = player, AnswerSchema = "boolean_accept"
                    });
                }
                return opened;
            }, original.ExecutorKind, original.DefinitionVersion));
            var state = EnterCollection(registry, out var round);
            var taskIds = state.EffectRuntime.EffectNodes.Select(n => n.EffectId).ToArray();
            var openIds = state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").Select(r => r.InteractionId).ToArray();
            var requestCount = state.EffectRuntime.InteractionRequests.Count;
            var realRegistry = new RoundExecutionService().EffectRegistry;
            state = Restore(state, realRegistry, omitSchedulingGroups: true); round = new RoundExecutionService(realRegistry);
            Assert.That(round.Advance(state).IsValid, Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Select(n => n.EffectId), Is.EquivalentTo(taskIds));
            Assert.That(state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").Select(r => r.InteractionId), Is.EquivalentTo(openIds));
            Assert.That(state.EffectRuntime.InteractionRequests.Count, Is.EqualTo(requestCount));
            if (commonSelection)
            {
                // 无独立组字段的历史布局按保存的默认顺序继续，不能重发或改写旧scope。
                foreach (var player in new[] { 1, 2, 3 }) SubmitEmpty(state, round, player);
            }
            else
            {
                foreach (var player in new[] { 1, 2, 3 })
                {
                    state.FindPlayer(player).HasCollectedResourcesThisRound = true;
                    var completed = round.CompleteResourceCollection(state, player);
                    Assert.That(completed.IsValid, Is.True, completed.Reason);
                }
            }
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
        }

        private GameState EnterCollection(EffectRegistry registry, out RoundExecutionService round)
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var source = new GameState
            {
                GameId = "collection-mainline-order", MapId = map.MapId, ContentPackHash = pack.ContentHash,
                Phase = GamePhase.Setup, Round = 1, MaxRounds = 8, StartPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 3, ContentPackHash = pack.ContentHash },
                // 最小机械夹具冻结本case实际角色定义；未声明额外纯查询来源。
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList() },
                Players = Enumerable.Range(1, 3).Select(id => new PlayerState
                {
                    PlayerId = id, Color = (PlayerColor)(id - 1), Resources = new ResourceSet { GoldVoucher = 20 }
                }).ToList()
            };
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, new[]
            {
                new ItemRecipeEntry { EntryId = "characters", DefinitionId = "texas", TypeId = "core.basic_character_card", Quantity = 2, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "cities", DefinitionId = "core.mobile_city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" },
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.component.marker", TypeId = "core.player_marker", Quantity = 6, PerPlayer = true, InitialZoneRole = "supply" }
            });
            var locations = new[] { "A-01", "B-01", "C-01" };
            for (var id = 1; id <= 3; id++)
            {
                var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == id);
                var location = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == locations[id - 1]);
                var dock = state.ItemZones.Zones.Single(z => z.ParentId == location.ZoneId && z.TemplateId == "core.zone.city_dock");
                typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { state, city, dock.ZoneId });
            }
            round = new RoundExecutionService(registry);
            var created = round.CreateRound(state, 1, 1);
            Assert.That(created.IsValid, Is.True, created.Reason);
            for (var guard = 0; state.Phase == GamePhase.CharacterCover && guard < 16; guard++)
            {
                Assert.That(state.CurrentPlayerId, Is.EqualTo(-1));
                var request = state.EffectRuntime.InteractionRequests.FirstOrDefault(item => item.Status == "open" &&
                    item.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId);
                Assert.That(request, Is.Not.Null, state.EffectRuntime.LastFaultMessage);
                Assert.That(request.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
                var cover = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
                cover.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
                cover.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                cover.OptionIds.Add(request.CandidateIds[0]);
                var result = new AnswerInteractionCommandHandler(registry, round).Handle(state, cover);
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            }
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            for (var actionRound = 1; actionRound <= 2; actionRound++)
                for (var id = 1; id <= 3; id++)
                {
                    var action = round.CompleteMainAction(state, id);
                    Assert.That(action.IsValid, Is.True, action.Reason);
                }
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            return state;
        }

        private static InteractionRequest[] OpenSelections(GameState state) => state.EffectRuntime.InteractionRequests
            .Where(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection").ToArray();
        private static string Scope(InteractionRequest request) => request.PromptParameters.Properties.Single(p => p.Name == "scopeKey").Value.StringValue;
        private static long Long(NormalizedValue value, string field) => value.Properties.Single(p => p.Name == field).Value.IntegerValue;
        private GameState Restore(GameState state, EffectRegistry registry, bool omitSchedulingGroups = false)
        {
            if (!omitSchedulingGroups)
            {
                var actual = artifacts.Recover(state, "009主链采集真实恢复", registry);
                Assert.That(actual.Succeeded, Is.True, actual.Diagnostic); return actual.State;
            }
            var json = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            if (omitSchedulingGroups)
            {
                var archive = JObject.Parse(json);
                var collectionOwners = state.EffectRuntime.MainNodes.Where(n => n.NodeTypeId == RoundMainlineNodeTypeIds.Collection)
                    .Select(n => n.ExecutionEffectId).ToArray();
                // 只探测历史采集布局，不能剥除已完成的新角色覆盖阶段必需组。
                foreach (var value in archive.Descendants().OfType<JObject>().Where(n =>
                    (string)n["EffectTypeId"] == RoundExecutionService.MainlineEffectTypeId && collectionOwners.Contains((string)n["EffectId"])))
                    value.Remove("ChildSchedulingGroups");
                json = archive.ToString();
            }
            artifacts.WriteLabeledText("旧调度组字段缺省原始归档", json);
            var restored = HostRecoveryService.RecoverJson(json, registry);
            artifacts.WriteLabeledText("旧调度组字段缺省恢复结果", JsonConvert.SerializeObject(new { restored.Succeeded, restored.Diagnostic, restored.State }));
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            return restored.State;
        }
        private static void SubmitEmpty(GameState state, RoundExecutionService round, int player, bool allowCollectionFailure = false)
        {
            var request = OpenSelections(state).Single(r => r.AnsweringPlayerId == player);
            var commit = RouteSelectionAnswerCodec.Encode(Scope(request), RouteSelectionCandidateKind.Commit);
            Assert.That(request.CandidateIds, Does.Contain(commit));
            Submit(state, round, request, NormalizedValue.CreateArray(new List<NormalizedValue>
            {
                NormalizedValue.CreateStableReference("candidate", commit)
            }), allowCollectionFailure);
        }
        private static void Submit(GameState state, RoundExecutionService round, InteractionRequest request,
            NormalizedValue answer, bool allowCollectionFailure = false)
        {
            var executor = round.CreateCommandExecutor(state);
            Assert.That(executor.TrySubmitInteraction(request.InteractionId, request.AnsweringPlayerId,
                state.EffectRuntime.StateRevision, answer, out var diagnostic), Is.True, diagnostic);
            var report = executor.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var advanced = round.Advance(state);
            if (allowCollectionFailure)
            {
                Assert.That(advanced.IsValid, Is.False);
                return;
            }
            Assert.That(advanced.IsValid, Is.True, advanced.Reason);
        }
    }
}
