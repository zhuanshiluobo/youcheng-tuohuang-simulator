using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json;
using YC.Tests.EditMode.ExpansionRules;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    public sealed class DeferredInfluenceDestinationTests
    {
        private static IsolatedTestArtifacts artifacts;
        private static string contentRoot;
        private static Fixture current;
        [SetUp] public void BeginEvidence()
        {
            current = null; artifacts = IsolatedTestArtifacts.BeginCase();
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            const string script = "lua/characters/operator.dorothy.tactic.lua";
            string historical = File.ReadAllText(Path.Combine(installed,script)).Replace("StoreRemovedInfluence({ apiVersion = 2", "StoreRemovedInfluence({ apiVersion = 1");
            contentRoot = artifacts.CopyContent(installed, "旧v1离场协议兼容输入", new Dictionary<string,string>{{script,historical}});
            artifacts.WriteLabeledText("旧协议兼容边界", "本夹具不注册新版来源目录。隔离构造 v1 影响力帧并使用 Store v1 私有正文；只验旧协议在途恢复，不计当前内容协议验收。");
        }
        [TearDown] public void FinishEvidence()
        {
            if (current?.State != null) artifacts.WriteLabeledText("当前真实状态", JsonConvert.SerializeObject(current.State));
            artifacts.Finish();
        }
        private sealed class Fixture
        {
            public GameState State;
            public readonly EffectRegistry Registry = new EffectRegistry();
            public readonly GameMapDefinition Map = StaticMapDefinitions.CreateThreePlayerMap();
            public readonly RoundExecutionService Round;
            public string CardId, RemovedId, DepartureId;
            public int Commands;
            public Fixture(int actor = 2, int stock = 2, int loaded = 1, bool sequential = false)
            {
                current = this;
                var configuration = new GameRuleConfiguration { MapId = Map.MapId, PlayerCount = 3,
                    EnabledExpansionIds = new List<string> { "core", "enterprise" },
                    PlayerStageSchedulingMode = sequential ? PlayerStageSchedulingMode.Sequential : PlayerStageSchedulingMode.Concurrent };
                var pack = ExternalContentPack.Load(contentRoot).CreateActiveSnapshot(configuration);
                artifacts.RecordContent("R2实际离场内容", contentRoot, pack.ContentHash);
                configuration.ContentPackHash = pack.ContentHash;
                var query = new MapQueryService(Map);
                ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry);
                ItemTransferEffectExecutor.Register(Registry); DeckShuffleEffectExecutor.Register(Registry);
                CharacterLifecycleService.Register(Registry); CharacterContentEffectExecutor.Register(Registry);
                ContentPrimitiveEffectExecutor.Register(Registry, query, new InfluenceService(query));
                CharacterCardLuaCatalog.Register(Registry, pack);
                InfluenceEffectExecutor.Register(Registry, new InfluenceService(query));
                Round = new RoundExecutionService(Registry);
                var source = new GameState { GameId = "deferred-destination-domain", MapId = Map.MapId, Round = 1, ActionRound = 1, MaxRounds = 2,
                    Phase = GamePhase.ActionRound1, StartPlayerId = actor, CurrentPlayerId = 1, UseSeatTurnOrder = true,
                    LaunchPlayerOrder = new List<int> { 1, 2, 3 }, ContentPackHash = pack.ContentHash, RuleConfiguration = configuration,
                    ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(),
                        Events = pack.CreateEvents().ToList(), CityStyles = pack.CreateCityStyles().ToList() } };
                for (int id = 1; id <= 3; id++) source.Players.Add(new PlayerState { PlayerId = id, Color = (PlayerColor)(id - 1),
                    CityLocationId = Map.Locations.First(location => !location.IsRedZone).LocationId, Resources = new ResourceSet { GoldVoucher = 30 } });
                var dorothy = pack.ActiveDefinitions.Single(definition => definition.DefinitionId == "operator.dorothy");
                var recipe = new List<ItemRecipeEntry> {
                    new ItemRecipeEntry { EntryId = "deferred_markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = stock, PerPlayer = true, InitialZoneRole = "supply" },
                    new ItemRecipeEntry { EntryId = "deferred_dorothy", DefinitionId = dorothy.DefinitionId, TypeId = dorothy.ItemTypeId, Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" }
                };
                foreach (var location in Map.Locations.Where(location => !location.IsRedZone)) recipe.Add(new ItemRecipeEntry {
                    EntryId = "deferred_resource_" + location.LocationId, DefinitionId = "core.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
                State = new ItemZoneStateFactory().CreateForSetup(source, Map, recipe, new[] { new ItemCardZoneDeclaration { DefinitionId = dorothy.DefinitionId, CardZones = dorothy.CardZones } });
                foreach (var token in State.ItemZones.Items.Where(item => item.TypeId == "core.resource_point_token"))
                    token.LocalState = Object("resourceType", NormalizedValue.CreateInteger((int)ResourceType.Originium), "amount", NormalizedValue.CreateInteger(2));
                State.Map.OpenLocationIds.AddRange(Map.Locations.Where(location => !location.IsRedZone).Select(location => location.LocationId));
                State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2, 3 };
                CardId = State.ItemZones.Items.Single(item => item.DefinitionId == dorothy.DefinitionId && item.ControllerSubject.PlayerId == 1).ItemId;
                RunRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(Open(CharacterCoverEffectExecutor.InteractionTypeId), CardId);
                RunRoot(CharacterLifecycleService.CreateUseSpec(1, CardId, "tactic"));
                Answer(State.EffectRuntime.InteractionRequests.Single(request => request.Status == "open" && request.InteractionTypeId == ContentPrimitiveEffectExecutor.QuantityInteractionTypeId), loaded.ToString(CultureInfo.InvariantCulture));
                foreach (var request in State.EffectRuntime.InteractionRequests.Where(request => request.Status == "open").ToList())
                    Answer(request, request.CandidateIds.Contains("finish") ? "finish" : request.CandidateIds.First());
                Assert.That(CharacterLifecycleService.IsSubscriptionActive(State, CardId, "operator.dorothy.tactic"), Is.True);
                string slot = Slot(0); RunRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
                RemovedId = State.ItemZones.Items.Single(item => new ItemZoneQuery().IsInfluence(State, item.ItemId)).ItemId;
                State.CurrentPlayerId = actor;
                Assert.That(Round.PrepareActionWindowForSmoke(State, actor).IsValid, Is.True);
            }
            private EffectSpec tokenBody;
            public string RunTokenTransfer(int count, bool includePending = false)
            {
                const string ability = "qy006.deferred.token-transfer", signal = "QY006TokenConsumptionRequested", signalType = "qy006.token-consumer.signal";
                if (!Registry.HasAbilityCatalog)
                {
                    // 真实规则事件消费者，不在p2行动窗口冒充p1 quick行动；来源及use收据均由正式执行器生成。
                    var declaration = new AbilitySourceDeclaration { DefinitionId = "operator.dorothy", AbilityId = ability, Kind = "event", HandlerRef = ability,
                        DeclarationVersion = "1.0.0", AuthorizationPolicy = "controller", PresentationRef = "operator.dorothy",
                        EffectiveZones = new List<string> { "core.zone.character_persistent", "core.zone.character_covered" },
                        ObservedEvents = new List<string> { signal },
                        AllowedOperations = new List<string> { "carrier.transfer.original_supply:operator.dorothy:markers:core.cube" } };
                    Registry.RegisterAbilityHandler(ability, "qy006.deferred.token-transfer/1", _ => new[] { tokenBody });
                    Registry.FreezeAbilityCatalog(new[] { declaration });
                    State.ContentDefinitions.AbilitySources = new List<AbilitySourceDeclaration> { declaration };
                    AbilitySourceEffectExecutor.Register(Registry);
                    Registry.RegisterEventHandler(new EffectEventHandlerRegistration(ability + ".subscription", signal, "", ability + ".dispatch", context =>
                    {
                        var source = Registry.QuerySources(context.State, 1, "event", signal).Single(s => s.AbilityId == ability && s.ItemId == CardId);
                        return new[] { AbilitySourceEffectExecutor.Create(source, context.Event, ability, false, context.State.Round) };
                    }, EffectHandlerRole.Primary));
                    Registry.Register(signalType, context => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = signal, PlayerId = 1, Payload = Object() }));
                }
                int currentPlayer = State.CurrentPlayerId;
                var ids = State.ItemZones.Items.Where(i => i.ZoneId == Storage.ZoneId).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).Take(count).ToList();
                if (includePending && ids.Count < count) ids.Add(RemovedId);
                Assert.That(ids.Count, Is.EqualTo(count));
                tokenBody = TokenSpec(1, ids, Storage.ZoneId, null, count, "core.cube_storage");
                var engine = Round.CreateCommandExecutor(State);
                string root = engine.CreateRoot(EffectSpec.Create(signalType, Object(), 1)); Pump();
                Assert.That(State.CurrentPlayerId, Is.EqualTo(currentPlayer), "消费者不切换当前玩家或关闭p2窗口。");
                var actualEvent = State.EffectRuntime.RuleEvents.Single(e => e.EventType == signal && e.SourceEffectId == root);
                var use = State.EffectRuntime.AbilitySourceUses.Single(u => u.AbilityId == ability && u.TriggerEventId == actualEvent.EventId);
                Assert.That(use.SourceRef.AuthorizedPlayerId, Is.EqualTo(1)); Assert.That(use.SourceRef.ItemId, Is.EqualTo(CardId));
                var nodes = State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId && Number(n.NormalizedArguments, "apiVersion") == 3 &&
                    InfluenceDepartureProtocol.IsWithin(State, n.EffectId, use.OwnerEffectId)).ToList();
                Assert.That(nodes.Count, Is.EqualTo(1), "必须真正到达该来源use下的api3工作节点，而非只创建消费者父节点。");
                artifacts.WriteLabeledText("合法事件消费者与实际token工作节点", JsonConvert.SerializeObject(new { currentPlayer, actualEvent, use, node = nodes.Single() }));
                return nodes.Single().EffectId;
            }
            public string Slot(int index) => InfluenceService.GetLocationSlotId(Map.Locations.First(location => !location.IsRedZone && location.InfluenceSlotCount > index).LocationId, index);
            public ZoneInstanceState Storage => State.ItemZones.Zones.Single(zone => zone.ParentKind == ZoneParentKind.Item && zone.ParentId == CardId && zone.LocalZoneId == "markers");
            public DeferredResponseState Response => State.EffectRuntime.DeferredResponses.Single();
            public InteractionRequest Open(string type = "effect.choice.execute") => State.EffectRuntime.InteractionRequests.Last(request => request.Status == "open" && request.InteractionTypeId == type);
            public EffectNodeRuntimeState Node(string id) => State.EffectRuntime.EffectNodes.Single(node => node.EffectId == id);
            public void Pump()
            {
                Assert.That(Round.CreateCommandExecutor(State).RunUntilQuiescent().Faulted, Is.False, State.EffectRuntime.LastFaultMessage);
                RoundExecutionService.RefreshConcurrentInteractionRevisions(State);
            }
            public string RunRoot(EffectSpec spec)
            {
                var engine = Round.CreateCommandExecutor(State); string id = engine.CreateRoot(spec); MakeHistoricalInfluenceFrame(id); Pump();
                Assert.That(Node(id).Status, Is.Not.EqualTo(EffectNodeStatus.Failed), Node(id).FailureReason); return id;
            }
            private void MakeHistoricalInfluenceFrame(string id)
            {
                var n=Node(id);
                if (new[]{InfluenceEffectTypeIds.RemoveInfluence,InfluenceEffectTypeIds.ReplaceInfluence,InfluenceEffectTypeIds.PlaceInfluence}.Contains(n.EffectTypeId)) n.DefinitionVersion="v1";
            }
            public EffectSpec MoveCardSpec(string destination) => EffectSpec.Create(ItemTransferEffectExecutor.TypeId,
                Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                    "itemRefs", NormalizedValue.CreateArray(new[] { Object("itemId", NormalizedValue.CreateString(CardId)) }),
                    "expectedSource", Object("zoneId", NormalizedValue.CreateString(State.ItemZones.Items.Single(item => item.ItemId == CardId).ZoneId)),
                    "destination", Object("zoneId", NormalizedValue.CreateString(destination)), "insertion", Object("kind", NormalizedValue.CreateString("unordered")),
                    "operationProfileId", NormalizedValue.CreateString("core.character_card_transfer"), "requiredCount", NormalizedValue.CreateInteger(1)), 1);
            public string RunAction(EffectSpec spec)
            {
                var command = new GameCommand { CommandId = "deferred-domain-cause-" + (++Commands), PlayerId = spec.PlayerId, Kind = GameCommandKind.EndAction };
                Assert.That(OperationWindowService.TryResolveCommandContext(State, command, out var cause, out var validation), Is.True, validation?.Reason);
                State.EffectRuntime.CurrentOperationContext = cause;
                try
                {
                    var engine = Round.CreateCommandExecutor(State);
                    Assert.That(engine.TryCreatePlayerActionEffect(spec.PlayerId, spec, "1", "deferred-domain", out string id), Is.True);
                    MakeHistoricalInfluenceFrame(id); Pump(); return id;
                }
                finally { State.EffectRuntime.CurrentOperationContext = null; }
            }
            public void Depart(bool replace = false, int actor = 2)
            {
                DepartureId = RunAction(replace ? InfluenceEffectSpecFactory.ReplaceInfluence(actor, RemovedId) : InfluenceEffectSpecFactory.RemoveInfluence(actor, RemovedId));
            }
            public void Answer(InteractionRequest request, string value)
            {
                var engine = Round.CreateCommandExecutor(State);
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, NormalizedValue.CreateString(value), out string reason), Is.True, reason);
                Pump();
            }
            public void Restore()
            {
                string before = RuleStateDigestService.Compute(State);
                var restored = artifacts.Recover(State, "R2离场响应恢复", Registry);
                Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
                Assert.That(RuleStateDigestService.Compute(restored.State), Is.EqualTo(before));
                State = restored.State;
            }
        }

        [TestCase(false, "store")]
        [TestCase(false, "decline")]
        [TestCase(true, "store")]
        [TestCase(true, "decline")]
        public void FormalDepartureSettlesOneOriginalInstanceAndPreservesHistoricalDestination(bool replace, string answer)
        {
            var f = new Fixture(); f.Depart(replace);
            Assert.That(f.Node(f.DepartureId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.Response.Status, Is.EqualTo(DeferredResponseStatus.Pending));
            Assert.That(f.State.ItemZones.Items.Single(item => item.ItemId == f.RemovedId).ZoneId, Is.EqualTo(f.Response.ReservationZoneId));
            var initial = f.Open(); string interactionId = initial.GetStableInteractionId(); int revision = initial.StateRevision;
            var beforeRead = GameStateCloneService.DeepClone(f.State);
            Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(f.State, new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.FinalizeGame }).Count, Is.EqualTo(1));
            Assert.That(GameStateCloneService.AreEquivalent(f.State, beforeRead), Is.True);
            f.RunAction(ResourceEffectSpecFactory.GainScore(2, 3));
            Assert.That(f.State.FindPlayer(2).Score, Is.EqualTo(3));
            var engine = f.Round.CreateCommandExecutor(f.State);
            var beforeStale = GameStateCloneService.DeepClone(f.State);
            Assert.That(f.Round.CreateCommandExecutor(f.State).TrySubmitInteraction(interactionId, 1, revision, NormalizedValue.CreateString(answer), out _), Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(f.State, beforeStale), Is.True);
            f.Restore(); f.Answer(f.Open(), answer);
            Assert.That(f.Response.Status, Is.EqualTo(answer == "store" ? DeferredResponseStatus.Stored : DeferredResponseStatus.Returned));
            var cube = f.State.ItemZones.Items.Single(item => item.ItemId == f.RemovedId);
            Assert.That(cube.TypeId, Is.EqualTo("core.player_marker")); Assert.That(cube.OwnerSubject.PlayerId, Is.EqualTo(1));
            Assert.That(cube.ZoneId, Is.EqualTo(answer == "store" ? f.Storage.ZoneId : f.Response.OriginalSupplyZoneId));
            Assert.That(f.State.ItemZones.Items.Count(item => item.ItemId == f.RemovedId), Is.EqualTo(1));
            int transfers = f.State.EffectRuntime.EffectNodes.Sum(node => node.PositionChanges.Count(change => change.ItemId == f.RemovedId));
            Assert.That(engine.TrySubmitInteraction(interactionId, 1, revision, NormalizedValue.CreateString(answer), out _), Is.False);
            f.Pump(); Assert.That(f.State.EffectRuntime.EffectNodes.Sum(node => node.PositionChanges.Count(change => change.ItemId == f.RemovedId)), Is.EqualTo(transfers));
            f.Restore();
            if (answer == "store")
            {
                f.RunTokenTransfer(2);
                Assert.That(f.State.ItemZones.Items.Single(item => item.ItemId == f.RemovedId).ZoneId, Is.EqualTo(f.Response.OriginalSupplyZoneId));
                f.Restore();
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void SameActorAndLocalSequentialKeepTheNecessaryChain(bool sequential)
        {
            int actor = sequential ? 2 : 1;
            var f = new Fixture(actor: actor, sequential: sequential); f.Depart(actor: actor);
            Assert.That(f.State.EffectRuntime.DeferredResponses, Is.Empty);
            Assert.That(f.Node(f.DepartureId).Status, Is.EqualTo(EffectNodeStatus.Blocked));
            f.Answer(f.Open(), "decline");
            Assert.That(f.Node(f.DepartureId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.ItemZones.Items.Single(item => item.ItemId == f.RemovedId).ZoneId, Is.EqualTo(FindZone(f.State, "supply", 1).ZoneId));
        }

        [Test]
        public void ConsumptionWaitsOnTheRealWorkingNodeAndResumesWithoutReplayingTheAnswer()
        {
            var f = new Fixture(); f.Depart();
            string paying = f.RunTokenTransfer(2, includePending: true);
            Assert.That(f.Node(paying).FlowStage, Is.EqualTo(CharacterLifecycleService.DeferredDependencyStage));
            Assert.That(f.Node(paying).PendingOutcome, Is.EqualTo(EffectPendingOutcome.None)); Assert.That(f.Node(paying).PositionChanges, Is.Empty);
            Assert.That(f.State.EffectRuntime.Blockers.Any(blocker => blocker.OwnerEffectId == paying && blocker.TargetEffectId == f.Response.ResponseRootEffectId && !blocker.IsResolved), Is.True);
            f.Restore(); f.Answer(f.Open(), "store");
            Assert.That(f.Node(paying).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.Node(paying).PositionChanges.Count, Is.EqualTo(2));
            f.Pump(); Assert.That(f.Node(paying).PositionChanges.Count, Is.EqualTo(2)); f.Restore();
        }

        [Test]
        public void ExistingMarkerAndSameOwnerInPlayMoveRemainIndependentOfThePendingDestination()
        {
            var f = new Fixture(); f.Depart();
            string move = f.RunRoot(f.MoveCardSpec(FindZone(f.State, "covered", 1).ZoneId));
            Assert.That(f.Node(move).Status, Is.EqualTo(EffectNodeStatus.Completed));
            // 场内移动夹具补齐初次真实盖牌已建立的槽关系；不以裸 ZoneId 冒充合法盖牌状态。
            var coverSlot = f.State.CharacterCoverSlots.Single(slot => slot.PlayerId == 1);
            Assert.That(FindZone(f.State, "covered", 1).LegalSlotIds, Does.Contain(coverSlot.SlotId));
            coverSlot.CardInstanceId = f.CardId;
            f.State.ItemZones.Items.Single(item => item.ItemId == f.CardId).SlotId = coverSlot.SlotId;
            Assert.That(CharacterLifecycleService.TryValidate(f.State, out string coverReason), Is.True, coverReason);
            string pay = f.RunTokenTransfer(1);
            Assert.That(f.Node(pay).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.Node(pay).PositionChanges.Count, Is.EqualTo(1)); Assert.That(f.Response.Status, Is.EqualTo(DeferredResponseStatus.Pending));
            f.Answer(f.Open(), "store");
            Assert.That(f.State.ItemZones.Items.Single(item => item.ItemId == f.RemovedId).ZoneId, Is.EqualTo(f.Storage.ZoneId)); f.Restore();
        }

        [Test]
        public void ClosingTheRealParentWindowKeepsTheResponseAvailableInTheNextPlayerWindow()
        {
            var f = new Fixture(); f.Depart();
            string parentWindow = f.Response.OriginMainOperationWindowId;
            var command = new GameCommand { CommandId = "deferred-domain-close", Kind = GameCommandKind.EndAction, PlayerId = 2 };
            Assert.That(OperationWindowService.TryResolveCommandContext(f.State, command, out var context, out var validation), Is.True, validation?.Reason);
            f.State.EffectRuntime.CurrentOperationContext = context;
            try { Assert.That(f.Round.CompleteMainAction(f.State, 2).IsValid, Is.True); }
            finally { f.State.EffectRuntime.CurrentOperationContext = null; }
            f.Pump();
            Assert.That(f.State.EffectRuntime.OperationWindows.Single(window => window.OperationWindowId == parentWindow).Lifecycle, Is.EqualTo(OperationWindowLifecycle.Closed));
            Assert.That(f.Response.Status, Is.EqualTo(DeferredResponseStatus.Pending));
            int next = f.State.CurrentPlayerId;
            Assert.That(next, Is.Not.EqualTo(2)); f.RunAction(ResourceEffectSpecFactory.GainScore(next, 1));
            f.Restore(); f.Answer(f.Open(), "store");
            Assert.That(f.Response.Status, Is.EqualTo(DeferredResponseStatus.Stored)); f.Restore();
        }

        [Test]
        public void LastRoundWorkingNodeWaitsBeforeFinalEventAndDoesNotRepeatCleanup()
        {
            var f = new Fixture(); f.Depart(); f.State.MaxRounds = 1;
            // 使用真实主链构建器已产生的末轮节点，单独检查该执行器的封局边界。
            var main = f.State.EffectRuntime.MainNodes.Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded);
            var engine = f.Round.CreateCommandExecutor(f.State);
            string id = engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                Object("mainNodeId", NormalizedValue.CreateString(main.NodeId), "nodeTypeId", NormalizedValue.CreateString(main.NodeTypeId)), main.PlayerId));
            f.State.EffectRuntime.MainNodes.Single(node => node.NodeId == main.NodeId).ExecutionEffectId = id;
            f.Pump();
            Assert.That(f.Node(id).FlowStage, Is.EqualTo(FinalScoringSessionService.Prepare));
            Assert.That(f.Node(id).Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(f.Node(id).PendingOutcome, Is.EqualTo(EffectPendingOutcome.None));
            Assert.That(f.State.EffectRuntime.Blockers.Any(blocker => blocker.OwnerEffectId == id && blocker.TargetEffectId == f.Response.ResponseRootEffectId && !blocker.IsResolved), Is.True);
            f.Restore();
            Assert.That(f.Node(id).FlowStage, Is.EqualTo(FinalScoringSessionService.Prepare));
            Assert.That(f.State.EffectRuntime.RuleEvents.Any(ruleEvent => ruleEvent.EventType == "FinalScoringStarted"), Is.False);
            Assert.That(f.State.FinalScoring?.IsResolved ?? false, Is.False);
            int children = f.Node(id).ChildEffectIds.Count, pendingStart = f.State.EffectRuntime.PendingNextRoundStartPlayerId;
            f.Answer(f.Open(), "decline");
            Assert.That(f.Node(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.Node(id).ChildEffectIds.Count, Is.EqualTo(children));
            Assert.That(f.State.EffectRuntime.PendingNextRoundStartPlayerId, Is.EqualTo(pendingStart));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(ruleEvent => ruleEvent.EventType == "FinalScoringStarted"), Is.EqualTo(1));
            f.Restore(); f.Pump();
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(ruleEvent => ruleEvent.EventType == "FinalScoringStarted"), Is.EqualTo(1));
            Assert.That(f.Node(id).ChildEffectIds.Count, Is.EqualTo(children));
            Assert.That(f.State.EffectRuntime.PendingNextRoundStartPlayerId, Is.EqualTo(pendingStart));
        }

        [Test]
        public void CarrierExitAndSupplyRequirementWaitWhileFinalScoringRemainsUnpublished()
        {
            var f = new Fixture(); f.Depart();
            string carrier = f.RunRoot(EffectSpec.Create(ItemTransferEffectExecutor.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                "itemRefs", NormalizedValue.CreateArray(new[] { Object("itemId", NormalizedValue.CreateString(f.CardId)) }),
                "expectedSource", Object("zoneId", NormalizedValue.CreateString(f.State.ItemZones.Items.Single(item => item.ItemId == f.CardId).ZoneId)),
                "destination", Object("zoneId", NormalizedValue.CreateString(FindZone(f.State, "retired").ZoneId)), "insertion", Object("kind", NormalizedValue.CreateString("unordered")),
                "operationProfileId", NormalizedValue.CreateString("core.character_card_transfer"), "requiredCount", NormalizedValue.CreateInteger(1)), 1));
            string place = f.RunRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, f.Slot(1)));
            Assert.That(f.Node(carrier).FlowStage, Is.EqualTo(CharacterLifecycleService.DeferredDependencyStage));
            Assert.That(f.Node(place).FlowStage, Is.EqualTo(CharacterLifecycleService.DeferredDependencyStage));
            Assert.That(f.Node(carrier).PositionChanges, Is.Empty); Assert.That(f.Node(place).PositionChanges, Is.Empty);
            f.State.Phase = GamePhase.FinalScoring;
            var before = GameStateCloneService.DeepClone(f.State);
            Assert.That(new FinalScoringService(new MapQueryService(f.Map)).Resolve(f.State).Succeeded, Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(f.State, before), Is.True);
            f.State.Phase = GamePhase.ActionRound1; f.Answer(f.Open(), "decline");
            Assert.That(f.Node(carrier).Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(f.Node(place).Status, Is.EqualTo(EffectNodeStatus.Completed));
            f.Restore();
        }
    }
}
