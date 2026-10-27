using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class DeferredResponseSchedulingTests
    {
        [Test]
        public void KernelDetachesAdoptsAllBodiesAndWaitsForExplicitAnswer()
        {
            var source = new GameState { GameId = "shared-kernel-fixture", MapId = "map.shared-kernel", Phase = GamePhase.ActionRound1,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            source.RuleConfiguration = new GameRuleConfiguration { MapId = source.MapId, PlayerCount = 2, PlayerStageSchedulingMode = PlayerStageSchedulingMode.Concurrent };
            var state = new ItemZoneStateFactory().CreateForSetup(source, new YC.Domain.Maps.GameMapDefinition { MapId = source.MapId, MinPlayers = 2, MaxPlayers = 4 },
                new[] { new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 1, PerPlayer = true, InitialZoneRole = "supply" } });
            string itemId = state.ItemZones.Items.Single(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 2).ItemId;
            var registry = new EffectRegistry(); SettlementEntryService.Register(registry);
            registry.Register("test.shared.departure", context => {
                string staging = new YC.Domain.Influence.InfluenceDestinationResolver().GetStagingZoneId(context);
                // 领域离场适配夹具：本条只验证共享挂载和暂存接缝，不替代正式地图移除测试。
                context.State.ItemZones.Items.Single(i => i.ItemId == itemId).ZoneId = staging;
                ItemTransferEffectExecutor.Set(context.Node, "pendingItemId", NormalizedValue.CreateString(itemId));
                ItemTransferEffectExecutor.Set(context.Node, "pendingDepartureKind", NormalizedValue.CreateString("remove"));
                return EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "InfluenceRemoved", PlayerId = 2, ResponseKind = RuleEventResponseKind.Effects });
            });
            registry.Register("test.shared.choice", context => context.Node.FlowStage == "" ?
                EffectStepResult.Continue("answer").AddInteraction(new EffectInteractionSpec { InteractionTypeId = "test.shared.destination", PromptKey = "test.shared.destination",
                    AnsweringPlayerId = 2, AnswerSchema = "candidate_id", CandidateIds = { "decline" }, MinSelections = 1, MaxSelections = 1 }) :
                EffectStepResult.Completed(context.GetLatestInteractionAnswer()), EffectExecutorKind.IntrinsicFlow);
            registry.Register("test.shared.after", context => { context.State.FindPlayer(2).Resources.Iron++; return EffectStepResult.Completed(); });
            DeferredEventResponseDescriptor Describe(EffectEventHandlerContext context, EffectSpec body) => new DeferredEventResponseDescriptor {
                SourceEventId = context.Event.EventId, SourceDepartureEffectId = context.Event.SourceEffectId, SourceCardInstanceId = "fixture-card", AbilityId = "fixture-ability",
                AnsweringPlayerId = 2, ReservedItemId = itemId, OriginalSupplyZoneId = context.State.ItemZones.Items.Single(i => i.ItemId == itemId).OriginalSupplyZoneId, DestinationHandlerId = "fixture-destination" };
            bool Validate(GameState value, DeferredResponseState record, out string diagnostic) { diagnostic = ""; return value.ItemZones.Items.Any(i => i.ItemId == record.ReservedItemId); }
            registry.RegisterDeferredResponseDestination(new DeferredResponseDestinationRegistration("fixture-destination", "fixture-ability", Describe,
                (work, root, response) => {
                    string oldZone = work.ItemZones.Items.Single(i => i.ItemId == itemId).ZoneId;
                    var zone = new CardZoneDefinition { LocalZoneId = "removed_influence", ZoneTemplateId = "core.zone.effect_staging",
                        AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.cube" },
                        CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, VisibilityPolicyId = ItemZoneVisibility.Public,
                        EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                    if (!new ItemZoneStateFactory().TryCreateEffectStaging(work, root.EffectId, zone, out var zoneId, out var zoneFault)) throw new System.InvalidOperationException(zoneFault);
                    response.ReservationZoneId = zoneId;
                    return new ItemTransferService().AdoptDeferredReservationOnWorking(work, response, oldZone, response.ReservationZoneId);
                }, (context, response) => {
                    var answer = context.ChildNodes.First().NormalizedResult;
                    if (answer?.StringValue != "decline") return DeferredDestinationResult.Unresolved("explicit_answer_required");
                    new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ItemIds = new List<string> { itemId }, ExpectedSource = new ItemPositionRef { ZoneId = response.ReservationZoneId },
                        Destination = new ZoneRef { ZoneId = response.OriginalSupplyZoneId }, ExecutingPlayerId = 2, OperationProfileId = "core.cube_storage", RequiredCount = 1, InsertionKind = "unordered" });
                    return DeferredDestinationResult.Returned(response.OriginalSupplyZoneId);
                }, Validate));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("fixture-observer", "InfluenceRemoved", "*", "fixture-observer", context =>
                new[] { EffectSpec.Create("test.shared.choice", playerId: 2), EffectSpec.Create("test.shared.after", playerId: 2) }, EffectHandlerRole.Observer, abilityId: "fixture-ability")
                { AttachmentKind = EventResponseAttachmentKind.DeferredPlayerResponse, DescribeDeferredResponse = Describe });
            var executor = new EffectTreeExecutor(state, registry);
            string departureId = executor.CreateRoot(EffectSpec.Create("test.shared.departure", playerId: 1));
            state.EffectRuntime.MainNodes.Add(new MainlineNodeRuntimeState { NodeId = "fixture-main", NodeTypeId = RoundMainlineNodeTypeIds.PlayerActionWindow, ExecutionEffectId = departureId, PlayerId = 1, Status = EffectNodeStatus.Ready });
            OperationWindowService.Synchronize(state);
            Assert.That(OperationWindowService.TryResolveCommandContext(state, new GameCommand { CommandId = "fixture-remove", PlayerId = 1, Kind = GameCommandKind.EndAction }, out var operation, out _), Is.True);
            state.EffectRuntime.EffectNodes.Single(n => n.EffectId == departureId).OperationContext = operation;
            var report = executor.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, executor.LastDiagnostic);
            var record = state.EffectRuntime.DeferredResponses.Single();
            Assert.That(executor.GetNode(departureId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(executor.GetNode(departureId).ChildEffectIds, Is.Empty);
            Assert.That(executor.GetNode(record.ResponseRootEffectId).ChildEffectIds.Count, Is.EqualTo(2));
            Assert.That(record.Status, Is.EqualTo(DeferredResponseStatus.Pending));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == itemId).ZoneId, Is.EqualTo(record.ReservationZoneId));
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(0));
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision - 1, NormalizedValue.CreateString("decline"), out _), Is.False);
            Assert.That(record.Status, Is.EqualTo(DeferredResponseStatus.Pending));
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision, NormalizedValue.CreateString("decline"), out var fault), Is.True, fault);
            report = executor.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, executor.LastDiagnostic);
            record = state.EffectRuntime.DeferredResponses.Single();
            Assert.That(record.Status, Is.EqualTo(DeferredResponseStatus.Returned));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == itemId).ZoneId, Is.EqualTo(record.OriginalSupplyZoneId));
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(1));
            Assert.That(executor.GetNode(record.ResponseRootEffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
        }

        [Test]
        public void ClassificationRequiresRealSingleAuthorizedMainWindow()
        {
            var state = State(); var source = SourceEvent(state);
            Assert.That(OperationWindowService.TryResolveSingleMainWindowOnWorking(state, source, out var main), Is.True);
            Assert.That(main.MainWindowPlayerIdAtCreation, Is.EqualTo(1));
            var window = state.EffectRuntime.OperationWindows.Single();
            window.AuthorizedPlayerIds.Add(2); window.AvailablePlayerIds.Remove(2);
            Assert.That(OperationWindowService.TryResolveSingleMainWindowOnWorking(state, source, out _), Is.False);
            window.AuthorizedPlayerIds.Remove(2);
            source.SourceEffectId = "unrelated";
            Assert.That(OperationWindowService.TryResolveSingleMainWindowOnWorking(state, source, out _), Is.False);
            source.SourceEffectId = "departure"; source.OperationContext.WindowGeneration++;
            Assert.That(OperationWindowService.TryResolveSingleMainWindowOnWorking(state, source, out _), Is.False);
        }

        [Test]
        public void PendingResponseKeepsOwnWindowAfterOriginClosesAndLocalStillBlocks()
        {
            var state = State(); var response = AddResponse(state);
            var request = new InteractionRequest { RequestId = "answer", InteractionTypeId = "test.answer", OwnerEffectId = response.ResponseRootEffectId,
                AnsweringPlayerId = 2, Status = "open" };
            state.EffectRuntime.InteractionRequests.Add(request);
            var context = SourceEvent(state).OperationContext;
            Assert.That(EffectTreeExecutor.BlocksCommand(state, request, context), Is.False);
            state.RuleConfiguration.PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential;
            Assert.That(EffectTreeExecutor.BlocksCommand(state, request, context), Is.True);
            state.RuleConfiguration.PlayerStageSchedulingMode = PlayerStageSchedulingMode.Concurrent;
            state.EffectRuntime.EffectNodes.Find(n => n.EffectId == "main").Status = EffectNodeStatus.Completed;
            state.EffectRuntime.MainNodes[0].Status = EffectNodeStatus.Completed;
            OperationWindowService.Synchronize(state);
            var window = OperationWindowService.ReadOpenWindows(state).Single();
            Assert.That(window.SourceEffectId, Is.EqualTo(response.ResponseRootEffectId));
            Assert.That(window.ParentOperationWindowId, Is.EqualTo(response.OriginMainOperationWindowId));
            Assert.That(window.AuthorizedPlayerIds, Is.EqualTo(new[] { 2 }));
            Assert.That(window.AvailablePlayerIds, Is.EqualTo(new[] { 2 }));
            Assert.That(state.HasOpenActionableInteraction(), Is.True);
        }

        [Test]
        public void ExactDependenciesAttachOnlyToWorkingWithoutCompletingOwner()
        {
            var state = State(); var response = AddResponse(state);
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "consumer", Status = EffectNodeStatus.Ready, PlayerId = 1 });
            var query = new DeferredDependencyQuery { OwnerEffectId = "consumer", DependencyKind = DeferredDependencyKind.ExactItemUse, ItemIds = new List<string> { "unrelated-item" } };
            Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state, query), Is.Empty);
            query.ItemIds[0] = response.ReservedItemId;
            var dependencies = EffectTreeExecutor.GetDeferredResponseDependencies(state, query);
            Assert.That(dependencies.Count, Is.EqualTo(1));
            var work = GameStateCloneService.DeepClone(state); var entries = new List<RuleJournalEntry>();
            Assert.That(new EffectTreeExecutor(state, new EffectRegistry()).TryAttachDeferredResponseDependenciesOnWorking(work, "consumer", dependencies, entries), Is.True);
            Assert.That(state.EffectRuntime.Blockers, Is.Empty);
            Assert.That(work.EffectRuntime.Blockers.Single().TargetEffectId, Is.EqualTo(response.ResponseRootEffectId));
            Assert.That(work.EffectRuntime.EffectNodes.Find(n => n.EffectId == "consumer").PendingOutcome, Is.EqualTo(EffectPendingOutcome.None));
            Assert.That(work.EffectRuntime.EffectNodes.Find(n => n.EffectId == "consumer").Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(entries.Count, Is.EqualTo(1));
            query.DependencyKind = DeferredDependencyKind.FinalizeGame;
            Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state, query).Count, Is.EqualTo(1));
            query.OwnerEffectId = response.ResponseRootEffectId;
            Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state, query), Is.Empty);
        }

        private static GameState State()
        {
            var state = new GameState { GameId = "shared-deferred-fixture", RuleConfiguration = new GameRuleConfiguration { PlayerStageSchedulingMode = PlayerStageSchedulingMode.Concurrent },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "main", PlayerId = 1, Status = EffectNodeStatus.Ready });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "departure", ParentEffectId = "main", PlayerId = 1, Status = EffectNodeStatus.Ready });
            state.EffectRuntime.MainNodes.Add(new MainlineNodeRuntimeState { NodeId = "main-node", NodeTypeId = RoundMainlineNodeTypeIds.PlayerActionWindow,
                ExecutionEffectId = "main", PlayerId = 1, Status = EffectNodeStatus.Ready });
            OperationWindowService.Synchronize(state); return state;
        }

        private static RuleEvent SourceEvent(GameState state)
        {
            OperationWindowService.TryResolveCommandContext(state, new GameCommand { CommandId = "source-command", PlayerId = 1, Kind = GameCommandKind.EndAction }, out var context, out _);
            return new RuleEvent { EventId = "source-event", SourceEffectId = "departure", OperationContext = context };
        }

        private static DeferredResponseState AddResponse(GameState state)
        {
            var source = SourceEvent(state); state.EffectRuntime.RuleEvents.Add(source);
            string responseId = StableIdFactory.Create("deferred_response", state.GameId, "departure", "reserved", "card", "ability");
            var response = new DeferredResponseState { DeferredResponseId = responseId, ResponseRootEffectId = StableIdFactory.Create("deferred_response_root", responseId), SourceEventId = source.EventId,
                SourceDepartureEffectId = "departure", SourceCardInstanceId = "card", AbilityId = "ability", ReservedItemId = "reserved", ReservationZoneId = "staging",
                OriginalSupplyZoneId = "supply", DestinationHandlerId = "destination", AnsweringPlayerId = 2,
                OriginMainOperationWindowId = source.OperationContext.OriginMainOperationWindowId, MainWindowPlayerIdAtCreation = 1 };
            state.EffectRuntime.DeferredResponses.Add(response);
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = response.ResponseRootEffectId, PlayerId = 2, Status = EffectNodeStatus.Blocked,
                OperationContext = source.OperationContext.Clone(), SourceId = "character-card:card", EffectTypeId = SettlementEntryService.SegmentTypeId });
            var departure = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == "departure");
            ItemTransferEffectExecutor.Set(departure, "pendingItemId", NormalizedValue.CreateString(response.ReservedItemId));
            ItemTransferEffectExecutor.Set(departure, "destinationHandoffResponseId", NormalizedValue.CreateString(response.DeferredResponseId));
            state.EffectRuntime.DispatchReceipts.Add(new DispatchReceipt { Succeeded = true, EventId = source.EventId,
                ActualAttachmentKind = EventResponseAttachmentKind.DeferredPlayerResponse, DeferredResponseIds = new List<string> { response.DeferredResponseId },
                AttachedEffectIds = new List<string> { response.ResponseRootEffectId } });
            state.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = response.ReservationZoneId, TemplateId = "core.zone.effect_staging", OwnerEffectId = response.ResponseRootEffectId });
            state.ItemZones.Items.Add(new ItemInstanceState { ItemId = response.ReservedItemId, TypeId = "core.player_marker", PhysicalKind = ItemPhysicalKind.Cube, ZoneId = response.ReservationZoneId });
            ItemZoneLegacyProjection.Bind(state, true);
            return response;
        }
    }
}
