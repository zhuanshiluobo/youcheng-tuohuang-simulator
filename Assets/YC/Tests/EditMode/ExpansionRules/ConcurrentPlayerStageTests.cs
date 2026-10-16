using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 使用正式主链、原生牌区和正式 AnswerInteraction。额外 observer 仅制造可观察的等待边界。
    public sealed class ConcurrentPlayerStageTests
    {
        private const string HoldType = "test.concurrent.required_response";
        private const string GeneratedDefinition = "test.concurrent.generated_character";
        private static ExternalContentPack installed;

        private sealed class Fixture
        {
            public GameState State;
            public EffectRegistry Registry;
            public RoundExecutionService Round;
            public int RandomCalls;

            public InteractionRequest Request(string type, int player) => Open(State, type).Single(request => request.AnsweringPlayerId == player);
            public void Submit(InteractionRequest request, IEnumerable<string> selected = null)
            {
                var result = Session().Submit(Command(request, selected));
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason ?? State.EffectRuntime.LastFaultMessage);
                Assert.That(State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), State.EffectRuntime.LastFaultMessage);
            }
            public GameSession Session()
            {
                var session = new GameSession(State);
                session.RegisterHandler(new AnswerInteractionCommandHandler(Registry, Round));
                return session;
            }
            public void Restore()
            {
                var result = HostRecoveryService.RecoverJson(
                    HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(State)), Registry);
                Assert.That(result.Succeeded, Is.True, result.Diagnostic);
                State = result.State;
                Round = new RoundExecutionService(Registry);
                var advance = Round.Advance(State);
                Assert.That(advance.IsValid, Is.True, advance.Reason);
            }
        }

        private static Fixture Create(int players = 3, bool enterprise = false, int basics = 5,
            bool generatedDiscard = false, Action<EffectRegistry> registerExtra = null)
        {
            installed = installed ?? ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets", "StreamingAssets", "Content", "core"));
            var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId,
                PlayerCount = players, EnabledExpansionIds = new List<string> { "core" } };
            var catalog = ActiveContentCatalogBuilder.Build(installed, config);
            config.ContentPackHash = catalog.ContentPackHash;
            var recipe = catalog.SetupRecipe;
            var definitions = catalog.ContentDefinitions;
            if (basics == 6)
            {
                const string extra = "test.concurrent.extra_basic";
                recipe.Add(extra, extra, "core.basic_character_card", "hand", true);
                var card = GameStateCloneService.DeepClone(definitions.Characters[0]);
                card.CardId = extra; card.TemplateId = extra; definitions.Characters.Add(card);
            }
            recipe.InitialBasicCharacterCount = basics;
            if (generatedDiscard)
            {
                recipe.Add(GeneratedDefinition, GeneratedDefinition, "core.generated_character_card", "discard", true);
                var card = GameStateCloneService.DeepClone(definitions.Characters[0]);
                card.CardId = GeneratedDefinition; card.TemplateId = GeneratedDefinition; definitions.Characters.Add(card);
            }
            // 预置真实地点指示物，让 world.player-entered 实际执行但不随机抽事件正文。
            // 这组测试仅验证调度，不声称覆盖真实扩展的事件、缪尔赛思或赫拉格正文。
            foreach (string location in StaticMapDefinitions.FourPlayerInitialLocationIds)
            {
                string id = "test.concurrent.indicator." + location;
                recipe.Add(id, id, "core.resource_point_token", "location/" + location);
            }
            var enterprises = new EnterpriseDefinitions();
            if (enterprise)
            {
                recipe.EnterpriseEntrance = true;
                for (int index = 0; index < 8; index++)
                {
                    string id = "test.concurrent.enterprise." + index;
                    recipe.Add(id, id, "core.board_card", "reserve");
                    definitions.EnterpriseDefinitionIds.Add(id);
                    enterprises.Register(new EnterpriseDefinition { DefinitionId = id, RuntimeId = id,
                        Levels = Enumerable.Range(0, 6).Select(level => new EnterpriseLevelDefinition {
                            Level = level, Kind = level == 0 ? "empty" : "reward",
                            AbilityId = level == 0 ? "" : id + ".reward." + level }).ToList() });
                    foreach (int level in Enumerable.Range(1, 5))
                        enterprises.BindAbility(id + ".reward." + level,
                            context => new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) });
                }
                enterprises.Freeze();
            }
            var fixture = new Fixture { Registry = new EffectRegistry() };
            ExpansionSetupFlow.Register(fixture.Registry, upper => { fixture.RandomCalls++; return 0; });
            catalog.Register(fixture.Registry);
            ResourceEffectExecutor.Register(fixture.Registry);
            GenericLuaEffectExecutor.Register(fixture.Registry);
            if (enterprise) fixture.Registry.EnterpriseDefinitions = enterprises;
            fixture.Registry.SetupRecipe = GameStateCloneService.DeepClone(recipe);
            fixture.Registry.ContentDefinitions = GameStateCloneService.DeepClone(definitions);
            fixture.State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1,
                Enumerable.Range(1, players).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "玩家" + id }).ToList(),
                config, 321, recipe, definitions);
            foreach (var token in fixture.State.ItemZones.Items.Where(item => item.TypeId == "core.resource_point_token"))
                token.LocalState = Object("resourceType", NormalizedValue.CreateInteger((int)ResourceType.Iron), "amount", NormalizedValue.CreateInteger(1));
            fixture.State.EffectRuntime.PlayerOrderSnapshot = Enumerable.Range(1, players).ToList();
            var map = new MapQueryService(StaticMapDefinitions.Resolve(fixture.State.MapId));
            var influence = new YC.Domain.Influence.InfluenceService(map);
            InfluenceEffectExecutor.Register(fixture.Registry, influence);
            EventCardEffectExecutor.Register(fixture.Registry, map, influence, new EventDeckService(), new ResourceTokenService());
            PlayerEntranceLuaCatalog.Register(fixture.Registry);
            PlayerEntranceEffectExecutor.Register(fixture.Registry, map);
            RoundStartedRedZoneRule.Register(fixture.Registry, map);
            registerExtra?.Invoke(fixture.Registry);
            fixture.Round = new RoundExecutionService(fixture.Registry);
            Assert.That(new ItemZoneQuery().TryValidate(fixture.State, out var reason), Is.True, reason);
            return fixture;
        }

        private static InteractionRequest[] Open(GameState state, string type) => state.EffectRuntime.InteractionRequests
            .Where(request => request.Status == "open" && request.InteractionTypeId == type).ToArray();
        private static GameCommand Command(InteractionRequest request, IEnumerable<string> selected = null)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId,
                OptionIds = (selected ?? request.CandidateIds.Take(request.MinSelections)).ToList() };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            return command;
        }
        private static NormalizedValue Object(params object[] entries)
        {
            var values = new List<NormalizedValueEntry>();
            for (int index = 0; index < entries.Length; index += 2)
                values.Add(new NormalizedValueEntry { Name = (string)entries[index], Value = (NormalizedValue)entries[index + 1] });
            return NormalizedValue.CreateObject(values);
        }
        private static NormalizedValue Field(NormalizedValue value, string name) => value?.Properties?.FirstOrDefault(entry => entry.Name == name)?.Value;
        private static string[] Ids(NormalizedValue value, string name) => Field(value, name)?.Items.Select(item => item.StringValue).ToArray() ?? Array.Empty<string>();
        private static EffectNodeRuntimeState Node(GameState state, string type, int player = -1) => state.EffectRuntime.EffectNodes
            .Single(node => node.EffectTypeId == type && (player < 0 || node.PlayerId == player));
        private static string[] In(GameState state, string role, int player = -1)
        {
            string zone = state.ItemZones.Zones.Single(value => value.LocalZoneId == role && value.OwnerPlayerId == player).ZoneId;
            return state.ItemZones.Items.Where(item => item.ZoneId == zone).Select(item => item.ItemId).ToArray();
        }
        private static void StartCover(Fixture fixture)
        {
            var result = fixture.Round.CreateRound(fixture.State, 1, 1);
            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.CharacterCover));
        }
        private static void StartEntrance(Fixture fixture)
        {
            var result = fixture.Round.StartEntrance(fixture.State);
            Assert.That(result.IsValid, Is.True, result.Reason);
        }
        private static void Hold(EffectRegistry registry, string eventType, bool includeOtherPlayerResponses = false)
        {
            registry.Register(HoldType, context => context.GetLatestInteractionAnswer() != null ? EffectStepResult.Completed() :
                EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = HoldType, AnsweringPlayerId = 1, Visibility = GameStateVisibilityPolicy.Owner,
                    AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "continue" } }),
                EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.concurrent.hold." + eventType,
                eventType, "*", "test.concurrent.hold.handler." + eventType,
                context => context.Event.PlayerId == 1 ? new List<EffectSpec> { EffectSpec.Create(HoldType, playerId: 1) } :
                    includeOtherPlayerResponses ? new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) } : new List<EffectSpec>()));
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void Enterprises_AllPrivateRequestsAcceptReverseOrderAndPublishTogetherAfterRestore(int players)
        {
            var fixture = Create(players, enterprise: true); StartEntrance(fixture);
            var requests = Open(fixture.State, ExpansionSetupFlow.EnterpriseChoiceInteraction);
            Assert.That(requests.Select(request => request.AnsweringPlayerId), Is.EquivalentTo(Enumerable.Range(1, players)));
            Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(-1));
            var selections = requests.ToDictionary(request => request.AnsweringPlayerId, request => request.CandidateIds.Take(request.MinSelections).ToArray());
            var preparation = Node(fixture.State, ExpansionSetupFlow.PreparationTypeId);
            string extra = Field(preparation.ContinuationData, "randomEnterpriseItemId")?.StringValue;
            int randomCalls = fixture.RandomCalls;
            Assert.That(randomCalls, Is.EqualTo(players == 3 ? 8 : 7));
            foreach (int player in Enumerable.Range(1, players))
            {
                var view = GameStateViewProjector.ProjectForPlayer(fixture.State, player);
                Assert.That(view.Interactions.Where(request => request.Kind == ExpansionSetupFlow.EnterpriseChoiceInteraction)
                    .Select(request => request.AnsweringPlayerId), Is.EqualTo(new[] { player }));
                string json = JsonConvert.SerializeObject(view);
                foreach (string hidden in requests.Where(request => request.AnsweringPlayerId != player).SelectMany(request => request.CandidateIds))
                    Assert.That(json, Does.Not.Contain(hidden), "其他玩家的未公开企业候选不能进入本人投影。");
            }
            Assert.That(GameStateViewProjector.ProjectForSpectator(fixture.State).Interactions, Is.Empty);
            for (int player = players; player >= 1; player--)
            {
                fixture.Submit(fixture.Request(ExpansionSetupFlow.EnterpriseChoiceInteraction, player), selections[player]);
                if (player > 1) Assert.That(In(fixture.State, "enterprises"), Is.Empty);
                if (player == players)
                {
                    var pendingIds = Open(fixture.State, ExpansionSetupFlow.EnterpriseChoiceInteraction).Select(request => request.InteractionId).ToArray();
                    fixture.Restore();
                    Assert.That(Open(fixture.State, ExpansionSetupFlow.EnterpriseChoiceInteraction).Select(request => request.InteractionId), Is.EquivalentTo(pendingIds));
                    Assert.That(fixture.RandomCalls, Is.EqualTo(randomCalls));
                    Assert.That(Field(Node(fixture.State, ExpansionSetupFlow.PreparationTypeId).ContinuationData, "randomEnterpriseItemId")?.StringValue, Is.EqualTo(extra));
                }
            }
            var expected = Enumerable.Range(1, players).SelectMany(player => selections[player]).ToList();
            if (players == 3) expected.Add(extra);
            Assert.That(In(fixture.State, "enterprises"), Is.EquivalentTo(expected));
            Assert.That(Ids(Node(fixture.State, ExpansionSetupFlow.PreparationTypeId).ContinuationData, "selectedEnterpriseItemIds"), Is.EqualTo(expected));
            Assert.That(Open(fixture.State, ExpansionSetupFlow.EnterpriseChoiceInteraction), Is.Empty);
            Assert.That(Open(fixture.State, PlayerEntranceEffectExecutor.InteractionTypeId).Select(request => request.AnsweringPlayerId), Is.EqualTo(new[] { 1 }));
            Assert.That(RoundExecutionService.TryValidateSetupTasks(fixture.State, fixture.Registry, out var diagnostic), Is.True, diagnostic);
        }

        [TestCase(true)] [TestCase(false)]
        public void ConcurrentRequests_RefreshBindingButRejectOtherPlayersOriginalGlobalRevision(bool enterprise)
        {
            var fixture = Create(enterprise: enterprise);
            if (enterprise) StartEntrance(fixture); else StartCover(fixture);
            string type = enterprise ? ExpansionSetupFlow.EnterpriseChoiceInteraction : CharacterCoverEffectExecutor.InteractionTypeId;
            var second = fixture.Request(type, 2);
            var stale = Command(second);
            var candidates = second.CandidateIds.ToArray();
            string interactionId = second.GetStableInteractionId(), ownerId = second.OwnerEffectId;
            fixture.Submit(fixture.Request(type, 1));
            var refreshed = fixture.Request(type, 2);
            Assert.That(refreshed.GetStableInteractionId(), Is.EqualTo(interactionId));
            Assert.That(refreshed.OwnerEffectId, Is.EqualTo(ownerId));
            Assert.That(refreshed.CandidateIds, Is.EqualTo(candidates));
            Assert.That(refreshed.StateRevision, Is.EqualTo(fixture.State.EffectRuntime.StateRevision));
            var rejection = fixture.Session().Submit(stale);
            Assert.That(rejection.Succeeded, Is.False);
            Assert.That(rejection.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(fixture.Request(type, 2).CandidateIds, Is.EqualTo(candidates));
            if (!enterprise) Assert.That(fixture.State.FindPlayer(2).CoveredCharacterCardId, Is.Empty);
            fixture.Submit(fixture.Request(type, 2));
        }

        [Test]
        public void Cover_ReverseAnswersWaitForRequiredResponseAfterEveryPrimaryCardIsPlaced()
        {
            var fixture = Create(registerExtra: registry => Hold(registry, "CharacterCardCovered")); StartCover(fixture);
            Assert.That(Open(fixture.State, CharacterCoverEffectExecutor.InteractionTypeId).Select(request => request.AnsweringPlayerId), Is.EquivalentTo(new[] { 1, 2, 3 }));
            Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(-1));
            foreach (int player in new[] { 3, 2, 1 })
            {
                fixture.Submit(fixture.Request(CharacterCoverEffectExecutor.InteractionTypeId, player));
                Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.CharacterCover));
            }
            var main = fixture.State.EffectRuntime.MainNodes.Single(node => node.NodeId == fixture.State.EffectRuntime.ActiveMainNodeId);
            Assert.That(main.CompletedPlayerIds, Is.EquivalentTo(new[] { 1, 2, 3 }));
            Assert.That(Node(fixture.State, CharacterCoverEffectExecutor.EffectTypeId, 1).Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(evt => evt.EventType == "CharacterCoverCompleted"), Is.Zero);
            Assert.That(RoundExecutionService.TryValidateParallelCoverTasks(fixture.State, null, out var beforeRestoreDiagnostic), Is.True, beforeRestoreDiagnostic);
            var missingFact = GameStateCloneService.DeepClone(fixture.State);
            missingFact.CharacterCoverSlots.RemoveAll(slot => slot.SlotId == StableIdFactory.Create("cover_slot",
                Node(missingFact, CharacterCoverEffectExecutor.EffectTypeId, 1).EffectId, "primary"));
            Assert.That(RoundExecutionService.TryValidateParallelCoverTasks(missingFact, null, out _), Is.False,
                "即使牌区仍非空且完成索引仍在，缺少任务的真实主槽也必须拒绝。");
            fixture.Restore();
            fixture.Submit(fixture.Request(HoldType, 1));
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(evt => evt.EventType == "CharacterCoverCompleted"), Is.EqualTo(1));
            Assert.That(RoundExecutionService.TryValidateParallelCoverTasks(fixture.State, fixture.Registry, out var diagnostic), Is.True, diagnostic);
        }

        [Test]
        public void Cover_EmptyHandRecoversGeneratedCharacterByInheritedTypeAndOpensAllOtherRequests()
        {
            var fixture = Create(generatedDiscard: true);
            string retired = fixture.State.ItemZones.Zones.Single(zone => zone.LocalZoneId == "secret_retired").ZoneId;
            var position = typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (string id in In(fixture.State, "hand", 1))
                position.Invoke(null, new object[] { fixture.State, fixture.State.ItemZones.Items.Single(item => item.ItemId == id), retired });
            string generated = fixture.State.ItemZones.Items.Single(item => item.OwnerSubject.PlayerId == 1 && item.DefinitionId == GeneratedDefinition).ItemId;
            StartCover(fixture);
            Assert.That(fixture.Request(CharacterCoverEffectExecutor.InteractionTypeId, 1).CandidateIds, Is.EqualTo(new[] { generated }));
            Assert.That(In(fixture.State, "hand", 1), Is.EqualTo(new[] { generated }));
            Assert.That(Open(fixture.State, CharacterCoverEffectExecutor.InteractionTypeId), Has.Length.EqualTo(3));
            fixture.Submit(fixture.Request(CharacterCoverEffectExecutor.InteractionTypeId, 1));
            Assert.That(fixture.State.FindPlayer(1).CoveredCharacterCardId, Is.EqualTo(generated));
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.CharacterCover));
        }

        [Test]
        public void Cover_RequiredFailurePausesWithoutPublishingCompletionWhileOtherPlayersStillWait()
        {
            var fixture = Create(registerExtra: registry =>
            {
                var originalRegistry = new EffectRegistry(); CharacterCoverEffectExecutor.Register(originalRegistry);
                var original = originalRegistry.Get(CharacterCoverEffectExecutor.EffectTypeId);
                registry.Register(new EffectRegistration(original.EffectTypeId, context => context.Node.PlayerId == 2
                    ? EffectStepResult.Failed("test.concurrent.cover_failure") : original.Executor(context), original.ExecutorKind, original.DefinitionVersion));
            });
            var result = fixture.Round.CreateRound(fixture.State, 1, 1);
            Assert.That(result.IsValid, Is.False);
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(fixture.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(fixture.State.EffectRuntime.LastFaultMessage, Is.EqualTo("test.concurrent.cover_failure"));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Any(evt => evt.EventType == "CharacterCoverCompleted"), Is.False);
            Assert.That(fixture.State.FindPlayer(2).CoveredCharacterCardId, Is.Empty);
        }

        [Test]
        public void Entrance_CommittedPlacementReleasesNextPickerButSerializesPublicResponsesAndAllSetupCompletion()
        {
            var fixture = Create(basics: 6, registerExtra: registry => Hold(registry, PlayerEntranceEffectExecutor.EventType, true));
            var startingScores = fixture.State.Players.ToDictionary(player => player.PlayerId, player => player.Score);
            StartEntrance(fixture);
            for (int player = 1; player <= 3; player++)
            {
                Assert.That(Open(fixture.State, PlayerEntranceEffectExecutor.InteractionTypeId).Select(request => request.AnsweringPlayerId), Is.EqualTo(new[] { player }));
                var request = fixture.Request(PlayerEntranceEffectExecutor.InteractionTypeId, player);
                Assert.That(request.CandidateIds.Intersect(fixture.State.Players.Select(value => value.CityLocationId)), Is.Empty);
                fixture.Submit(request);
                Assert.That(Node(fixture.State, PlayerEntranceEffectExecutor.TypeId, 1).Status, Is.EqualTo(EffectNodeStatus.Blocked));
                if (player > 1)
                {
                    var enter = Node(fixture.State, PlayerEntranceEffectExecutor.TypeId, player);
                    Assert.That(enter.FlowStage, Is.EqualTo("entered"));
                    Assert.That(enter.ChildEffectIds, Is.Not.Empty);
                    Assert.That(enter.ChildEffectIds.Select(id => fixture.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == id).Status), Has.All.EqualTo(EffectNodeStatus.Created));
                    Assert.That(fixture.State.FindPlayer(player).Score, Is.EqualTo(startingScores[player]), "前位入场树未完成时，后位公共响应不能提前修改分数。");
                }
            }
            Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(-1));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Any(evt => evt.EventType == "RoundStarted"), Is.False);
            Assert.That(fixture.State.Players.All(player => player.Resources.GoldVoucher == 0), Is.True);
            var locations = fixture.State.Players.Select(player => player.CityLocationId).ToArray();
            fixture.Restore();
            Assert.That(fixture.State.Players.Select(player => player.CityLocationId), Is.EqualTo(locations));
            fixture.Submit(fixture.Request(HoldType, 1));
            foreach (int player in new[] { 2, 3 }) Assert.That(fixture.State.FindPlayer(player).Score, Is.EqualTo(startingScores[player] + 1));
            Assert.That(Open(fixture.State, ExpansionSetupFlow.BasicCharacterInteraction).Select(request => request.AnsweringPlayerId), Is.EquivalentTo(new[] { 1, 2, 3 }));
            foreach (int player in new[] { 3, 2, 1 }) fixture.Submit(fixture.Request(ExpansionSetupFlow.BasicCharacterInteraction, player));
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(fixture.State.Players.Select(player => player.Resources.GoldVoucher), Is.EqualTo(new[] { 10, 12, 18 }));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(evt => evt.EventType == PlayerEntranceEffectExecutor.EventType), Is.EqualTo(3));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(evt => evt.EventType == "RoundStarted"), Is.EqualTo(1));
        }

        [Test]
        public void Entrance_PreviousPlayersRetentionCanRemainOpenWhileLaterPlayersChooseLocations()
        {
            var fixture = Create(basics: 6); StartEntrance(fixture);
            fixture.Submit(fixture.Request(PlayerEntranceEffectExecutor.InteractionTypeId, 1));
            string retention = fixture.Request(ExpansionSetupFlow.BasicCharacterInteraction, 1).InteractionId;
            Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(2));
            fixture.Submit(fixture.Request(PlayerEntranceEffectExecutor.InteractionTypeId, 2));
            Assert.That(fixture.Request(ExpansionSetupFlow.BasicCharacterInteraction, 1).InteractionId, Is.EqualTo(retention));
            Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(3));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Any(evt => evt.EventType == "RoundStarted"), Is.False);
            Assert.That(RoundExecutionService.TryValidateSetupTasks(fixture.State, fixture.Registry, out var diagnostic), Is.True, diagnostic);
        }

        [TestCase(true)] [TestCase(false)]
        public void Recovery_RejectsMissingRequiredGroupInNewProtocol(bool entrance)
        {
            var fixture = Create(enterprise: entrance);
            if (entrance) StartEntrance(fixture); else StartCover(fixture);
            var archive = HostRecoveryService.CreateArchive(fixture.State);
            var corrupt = archive.Snapshot.State;
            var owner = entrance ? Node(corrupt, ExpansionSetupFlow.PreparationTypeId) : corrupt.EffectRuntime.EffectNodes
                .Single(node => Field(node.NormalizedArguments, "nodeTypeId")?.StringValue == RoundMainlineNodeTypeIds.CharacterCover);
            owner.ChildSchedulingGroups.Clear();
            bool valid = entrance ? RoundExecutionService.TryValidateSetupTasks(corrupt, fixture.Registry, out _) :
                RoundExecutionService.TryValidateParallelCoverTasks(corrupt, fixture.Registry, out _);
            Assert.That(valid, Is.False);
            var recovery = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), fixture.Registry);
            Assert.That(recovery.Succeeded, Is.False);
            Assert.That(recovery.FaultCode, Is.EqualTo(HostRecoveryService.SnapshotInvalid));
        }

        [Test]
        public void LegacyEnterprise_SavedNextIndexContinuesSequentiallyWithoutCreatingNewPlayerTasks()
        {
            var fixture = Create(enterprise: true);
            var executor = new EffectTreeExecutor(fixture.State, fixture.Registry);
            string id = executor.CreateRoot(ExpansionSetupFlow.PreparationSpec(1, false));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(Open(fixture.State, ExpansionSetupFlow.EnterpriseChoiceInteraction).Select(request => request.AnsweringPlayerId), Is.EqualTo(new[] { 1 }));
            for (int player = 1; player <= 3; player++)
            {
                var request = fixture.Request(ExpansionSetupFlow.EnterpriseChoiceInteraction, player);
                executor = new EffectTreeExecutor(fixture.State, fixture.Registry);
                var answer = NormalizedValue.CreateArray(request.CandidateIds.Take(request.MinSelections).Select(NormalizedValue.CreateString).ToList());
                Assert.That(executor.TrySubmitInteraction(request.InteractionId, player, request.StateRevision, answer, out var reason), Is.True, reason);
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, fixture.State.EffectRuntime.LastFaultMessage);
                if (player == 1)
                {
                    var recovery = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(fixture.State)), fixture.Registry);
                    Assert.That(recovery.Succeeded, Is.True, recovery.Diagnostic); fixture.State = recovery.State;
                    Assert.That(Field(fixture.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == id).ContinuationData, "nextIndex").IntegerValue, Is.EqualTo(1));
                }
                if (player < 3) Assert.That(Open(fixture.State, ExpansionSetupFlow.EnterpriseChoiceInteraction).Select(value => value.AnsweringPlayerId), Is.EqualTo(new[] { player + 1 }));
            }
            Assert.That(fixture.State.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == ExpansionSetupFlow.EnterpriseSelectionTypeId), Is.False);
            Assert.That(Field(fixture.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == id).NormalizedArguments, "setupProtocolVersion"), Is.Null);
            Assert.That(In(fixture.State, "enterprises"), Has.Length.EqualTo(4));
        }

        [TestCase("legacy")] [TestCase("enterprise")] [TestCase("entrance")] [TestCase("cover")]
        public void NullRegistryArchive_CreatesAndAppendsCommittedLegacyOrConcurrentState(string stage)
        {
            var fixture = Create(enterprise: stage == "enterprise", basics: stage == "entrance" ? 6 : 5);
            if (stage == "enterprise" || stage == "entrance") StartEntrance(fixture);
            if (stage == "cover") StartCover(fixture);
            Assert.That(RoundExecutionService.TryValidateSetupTasks(fixture.State, null, out var setupDiagnostic), Is.True, setupDiagnostic);
            Assert.That(RoundExecutionService.TryValidateParallelCoverTasks(fixture.State, null, out var coverDiagnostic), Is.True, coverDiagnostic);
            var archive = HostRecoveryService.CreateArchive(fixture.State);
            if (stage == "enterprise") fixture.Submit(fixture.Request(ExpansionSetupFlow.EnterpriseChoiceInteraction, 3));
            else if (stage == "entrance") fixture.Submit(fixture.Request(PlayerEntranceEffectExecutor.InteractionTypeId, 1));
            else if (stage == "cover") fixture.Submit(fixture.Request(CharacterCoverEffectExecutor.InteractionTypeId, 3));
            else RuleCommit.Apply(fixture.State, "test.concurrent.legacy_archive_append", new RuleJournalEntry {
                Kind = RuleJournalEntryKind.DomainState, EntityId = fixture.State.GameId,
                ContentHash = fixture.State.ContentPackHash, Detail = "test.concurrent.legacy_archive_append" });
            Assert.That(RoundExecutionService.TryValidateSetupTasks(fixture.State, null, out setupDiagnostic), Is.True, setupDiagnostic);
            Assert.That(RoundExecutionService.TryValidateParallelCoverTasks(fixture.State, null, out coverDiagnostic), Is.True, coverDiagnostic);
            HostRecoveryService.AppendJournal(archive, fixture.State);
            Assert.That(archive.Journal.Entries, Is.Not.Empty);
            var recovery = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), fixture.Registry);
            Assert.That(recovery.Succeeded, Is.True, recovery.Diagnostic);
            Assert.That(recovery.State.EffectRuntime.StateRevision, Is.EqualTo(fixture.State.EffectRuntime.StateRevision));
            Assert.That(recovery.State.EffectRuntime.ActiveMainNodeId, Is.EqualTo(fixture.State.EffectRuntime.ActiveMainNodeId));
        }

        [TestCase(true)] [TestCase(false)]
        public void NullRegistryArchive_StillRejectsMissingNewGroupDuringCreationAndAppend(bool enterprise)
        {
            var fixture = Create(enterprise: enterprise);
            if (enterprise) StartEntrance(fixture); else StartCover(fixture);
            var archive = HostRecoveryService.CreateArchive(fixture.State);
            var corrupt = GameStateCloneService.DeepClone(fixture.State);
            var owner = enterprise ? Node(corrupt, ExpansionSetupFlow.PreparationTypeId) : corrupt.EffectRuntime.EffectNodes
                .Single(node => Field(node.NormalizedArguments, "nodeTypeId")?.StringValue == RoundMainlineNodeTypeIds.CharacterCover);
            owner.ChildSchedulingGroups.Clear();
            bool valid = enterprise ? RoundExecutionService.TryValidateSetupTasks(corrupt, null, out _) :
                RoundExecutionService.TryValidateParallelCoverTasks(corrupt, null, out _);
            Assert.That(valid, Is.False);
            Assert.That(() => HostRecoveryService.CreateArchive(corrupt), Throws.InvalidOperationException);
            Assert.That(() => HostRecoveryService.AppendJournal(archive, corrupt), Throws.InvalidOperationException);
            Assert.That(archive.Journal.Entries, Is.Empty);
        }

        [Test]
        public void NullRegistryEnterprise_ValidatesPublishedCandidateTypesAndFactsWhileRecoveryKeepsContentChecks()
        {
            var fixture = Create(enterprise: true); StartEntrance(fixture);
            foreach (int player in new[] { 3, 2, 1 }) fixture.Submit(fixture.Request(ExpansionSetupFlow.EnterpriseChoiceInteraction, player));
            Assert.That(RoundExecutionService.TryValidateSetupTasks(fixture.State, null, out var diagnostic), Is.True, diagnostic);
            var archive = HostRecoveryService.CreateArchive(fixture.State);
            string published = In(fixture.State, "enterprises")[0];
            string definition = fixture.State.ItemZones.Items.Single(item => item.ItemId == published).DefinitionId;
            foreach (string damage in new[] { "classification", "candidate", "item_type", "published_zone" })
            {
                var corrupt = GameStateCloneService.DeepClone(fixture.State);
                var item = corrupt.ItemZones.Items.Single(value => value.ItemId == published);
                if (damage == "classification") corrupt.ContentDefinitions.EnterpriseDefinitionIds.Remove(definition);
                else if (damage == "candidate") Field(Node(corrupt, ExpansionSetupFlow.PreparationTypeId).ContinuationData,
                    "candidateEnterpriseItemIds").Items[0] = NormalizedValue.CreateString("test.concurrent.unknown_enterprise");
                else if (damage == "item_type") item.TypeId = "core.basic_character_card";
                else typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { corrupt, item, corrupt.ItemZones.Zones.Single(zone => zone.LocalZoneId == "reserve").ZoneId });
                Assert.That(RoundExecutionService.TryValidateSetupTasks(corrupt, null, out _), Is.False, damage);
                Assert.That(() => HostRecoveryService.CreateArchive(corrupt), Throws.InvalidOperationException, damage);
                Assert.That(() => HostRecoveryService.AppendJournal(archive, corrupt), Throws.InvalidOperationException, damage);
            }
            // 带真实 registry 的 Recover 继续核完整冻结内容，不能用 null-registry 分支容忍不匹配。
            archive.Snapshot.State.ContentDefinitions.EnterpriseDefinitionIds.Remove(definition);
            var recovery = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), fixture.Registry);
            Assert.That(recovery.Succeeded, Is.False);
            Assert.That(recovery.Diagnostic, Does.Contain("content_snapshot_mismatch"));
        }

        [Test]
        public void LegacyCover_SavedSequentialGraphKeepsOneRequestAndItsTurnOrderAfterRestore()
        {
            var fixture = Create();
            // 旧档 fixture 从无 marker 的主链图起步；RoundStarted 和盖放仍由真实执行器结算。
            // 不把新协议已经运行的独立组改成 Sequential 来冒充旧档。
            var chain = new RoundMainChainBuilder().Build(fixture.State.GameId, "", 1, new List<int> { 1, 2, 3 });
            var runtime = fixture.State.EffectRuntime;
            runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 1;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = chain.PlayerOrderSnapshot;
            runtime.FirstMainNodeId = chain.FirstNodeId; runtime.ActiveMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes);
            new RoundExecutionProjector().Project(fixture.State);
            var started = chain.Nodes.Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.RoundStarted);
            var executor = fixture.Round.CreateCommandExecutor(fixture.State);
            string startedEffectId = executor.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                Object("mainNodeId", NormalizedValue.CreateString(started.NodeId), "nodeTypeId", NormalizedValue.CreateString(started.NodeTypeId))));
            fixture.State.EffectRuntime.MainNodes.Single(node => node.NodeId == started.NodeId).ExecutionEffectId = startedEffectId;
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, fixture.State.EffectRuntime.LastFaultMessage);
            var cover = chain.Nodes.Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.CharacterCover);
            Assert.That(fixture.State.EffectRuntime.ActiveMainNodeId, Is.EqualTo(cover.NodeId));
            executor = fixture.Round.CreateCommandExecutor(fixture.State);
            string coverEffectId = executor.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                Object("mainNodeId", NormalizedValue.CreateString(cover.NodeId), "nodeTypeId", NormalizedValue.CreateString(cover.NodeTypeId))));
            fixture.State.EffectRuntime.MainNodes.Single(node => node.NodeId == cover.NodeId).ExecutionEffectId = coverEffectId;
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(fixture.Round.Advance(fixture.State).IsValid, Is.True);
            for (int player = 1; player <= 3; player++)
            {
                Assert.That(Open(fixture.State, CharacterCoverEffectExecutor.InteractionTypeId).Select(request => request.AnsweringPlayerId), Is.EqualTo(new[] { player }));
                Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(player));
                var request = fixture.Request(CharacterCoverEffectExecutor.InteractionTypeId, player);
                executor = fixture.Round.CreateCommandExecutor(fixture.State);
                Assert.That(executor.TrySubmitInteraction(request.InteractionId, player, request.StateRevision,
                    NormalizedValue.CreateString(request.CandidateIds[0]), out var reason), Is.True, reason);
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, fixture.State.EffectRuntime.LastFaultMessage);
                Assert.That(fixture.Round.Advance(fixture.State).IsValid, Is.True);
                if (player == 1) fixture.Restore();
            }
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.ActionRound1));
            var savedCover = fixture.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == coverEffectId);
            Assert.That(Field(savedCover.NormalizedArguments, "coverTaskSchedulingVersion"), Is.Null);
            Assert.That(savedCover.ChildSchedulingGroups.Any(group => group.Mode == EffectChildSchedulingMode.Independent), Is.False);
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(evt => evt.EventType == "CharacterCoverCompleted"), Is.EqualTo(1));
        }
    }
}
