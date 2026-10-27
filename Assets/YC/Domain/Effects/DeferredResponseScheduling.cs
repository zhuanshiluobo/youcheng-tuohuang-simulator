using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        private readonly Dictionary<string, DeferredResponseDestinationRegistration> deferredDestinations =
            new Dictionary<string, DeferredResponseDestinationRegistration>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> deferredDestinationAbilities = new Dictionary<string, string>(StringComparer.Ordinal);
        public void RegisterDeferredResponseDestination(DeferredResponseDestinationRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (deferredDestinations.TryGetValue(registration.DestinationHandlerId, out var existing))
            {
                if (existing.AbilityId != registration.AbilityId) throw new InvalidOperationException("旁路去向处理器身份冲突。");
                return;
            }
            if (deferredDestinationAbilities.ContainsKey(registration.AbilityId)) throw new InvalidOperationException("同一能力不能登记两份旁路去向合同。");
            deferredDestinations.Add(registration.DestinationHandlerId, registration);
            deferredDestinationAbilities.Add(registration.AbilityId, registration.DestinationHandlerId);
        }
        public bool TryGetDeferredResponseDestination(string destinationHandlerId, out DeferredResponseDestinationRegistration registration)
        {
            registration = null;
            return !string.IsNullOrEmpty(destinationHandlerId) && deferredDestinations.TryGetValue(destinationHandlerId, out registration);
        }
        public bool TryGetDeferredResponseDestinationForAbility(string abilityId, out DeferredResponseDestinationRegistration registration)
        {
            registration = null;
            return !string.IsNullOrEmpty(abilityId) && deferredDestinationAbilities.TryGetValue(abilityId, out var id) && deferredDestinations.TryGetValue(id, out registration);
        }
    }

    public partial class EffectTreeExecutor
    {
        private bool TryAttachDeferredEventResponsesOnWorking(GameState work, RuleEvent sourceEvent, EffectNodeRuntimeState owner,
            EffectEventHandlerRegistration handler, IList<EffectSpec> specs, List<RuleJournalEntry> entries,
            List<string> attached, List<string> responseIds)
        {
            if (handler.AttachmentKind != EventResponseAttachmentKind.DeferredPlayerResponse || handler.DescribeDeferredResponse == null ||
                work.RuleConfiguration?.PlayerStageSchedulingMode != YC.Domain.Rules.PlayerStageSchedulingMode.Concurrent ||
                !YC.Domain.Rules.OperationWindowService.TryResolveSingleMainWindowOnWorking(work, sourceEvent, out var mainContext) ||
                IsRequiredEventLocation(work, sourceEvent.SourceEffectId)) return false;
            var context = new EffectEventHandlerContext(work, sourceEvent, owner, handler);
            var groups = new Dictionary<string, List<EffectSpec>>(StringComparer.Ordinal);
            var descriptors = new Dictionary<string, DeferredEventResponseDescriptor>(StringComparer.Ordinal);
            var required = new List<EffectSpec>();
            foreach (var spec in specs)
            {
                var descriptor = handler.DescribeDeferredResponse(context, spec);
                if (descriptor == null || descriptor.AnsweringPlayerId < 1 || descriptor.AnsweringPlayerId == mainContext.MainWindowPlayerIdAtCreation)
                { required.Add(spec); continue; }
                if (descriptor.SourceEventId != sourceEvent.EventId || descriptor.SourceDepartureEffectId != sourceEvent.SourceEffectId ||
                    string.IsNullOrEmpty(descriptor.SourceCardInstanceId) || string.IsNullOrEmpty(descriptor.AbilityId) ||
                    string.IsNullOrEmpty(descriptor.ReservedItemId) || string.IsNullOrEmpty(descriptor.OriginalSupplyZoneId) ||
                    !registry.TryGetDeferredResponseDestination(descriptor.DestinationHandlerId, out var registration) || registration.AbilityId != descriptor.AbilityId ||
                    spec.PlayerId != descriptor.AnsweringPlayerId)
                    throw new KernelException(EffectFaultCodes.InvalidEventResponse, "旁路去向描述与实际事件、响应者不一致。");
                string id = StableIdFactory.Create("deferred_response", work.GameId, descriptor.SourceDepartureEffectId,
                    descriptor.ReservedItemId, descriptor.SourceCardInstanceId, descriptor.AbilityId);
                if (!groups.TryGetValue(id, out var body)) { body = new List<EffectSpec>(); groups.Add(id, body); descriptors.Add(id, descriptor); }
                else if (descriptors[id].AnsweringPlayerId != descriptor.AnsweringPlayerId || descriptors[id].DestinationHandlerId != descriptor.DestinationHandlerId ||
                    descriptors[id].OriginalSupplyZoneId != descriptor.OriginalSupplyZoneId)
                    throw new KernelException(EffectFaultCodes.InvalidEventResponse, "同一旁路义务的去向材料冲突。");
                if (!string.IsNullOrEmpty(descriptor.StableResponseKey) && descriptor.StableResponseKey != id)
                    throw new KernelException(EffectFaultCodes.InvalidEventResponse, "旁路业务身份与去向描述不一致。");
                body.Add(spec);
            }
            if (groups.Count == 0) return false;
            if (required.Count > 0) attached.AddRange(AppendChildEffects(work, owner, required, "event", handler.SubscriptionId, entries,
                settlementTriggerId: sourceEvent.EventId, operationContext: sourceEvent.OperationContext));
            foreach (var pair in groups)
            {
                var descriptor = descriptors[pair.Key];
                var response = work.EffectRuntime.DeferredResponses.Find(r => r.DeferredResponseId == pair.Key);
                if (response != null)
                {
                    if (response.AnsweringPlayerId != descriptor.AnsweringPlayerId || response.ReservedItemId != descriptor.ReservedItemId ||
                        response.DestinationHandlerId != descriptor.DestinationHandlerId || response.OriginalSupplyZoneId != descriptor.OriginalSupplyZoneId)
                        throw new KernelException(EffectFaultCodes.InvalidEventResponse, "已有旁路义务材料冲突。");
                    attached.Add(response.ResponseRootEffectId); responseIds.Add(response.DeferredResponseId);
                    continue;
                }
                if (work.EffectRuntime.DeferredResponses.Any(r => r.Status == DeferredResponseStatus.Pending && r.ReservedItemId == descriptor.ReservedItemId))
                    throw new KernelException(EffectFaultCodes.InvalidEventResponse, "同一物品已由另一旁路义务保管。");
                if (work.EffectRuntime.EffectNodes.Count >= limits.MaxNodeCount)
                    throw new KernelException(EffectFaultCodes.NodeLimitExceeded, "旁路根超过节点容量。");
                var rootSpec = SettlementEntryService.CreateCleanupSegment(descriptor.AnsweringPlayerId, pair.Key, Array.Empty<EffectSpec>());
                rootSpec.SourceId = "character-card:" + descriptor.SourceCardInstanceId;
                string rootId = StableIdFactory.Create("deferred_response_root", pair.Key);
                var root = CreateNode(rootSpec, rootId, owner.RoundId, string.Empty);
                root.OperationContext = mainContext.Clone(); root.Status = EffectNodeStatus.Ready; root.FlowStage = "settling";
                work.EffectRuntime.EffectNodes.Add(root);
                response = new DeferredResponseState { DeferredResponseId = pair.Key, ResponseRootEffectId = rootId,
                    SourceEventId = sourceEvent.EventId, SourceDepartureEffectId = descriptor.SourceDepartureEffectId,
                    SourceCardInstanceId = descriptor.SourceCardInstanceId, AbilityId = descriptor.AbilityId,
                    AnsweringPlayerId = descriptor.AnsweringPlayerId, OriginMainOperationWindowId = mainContext.OriginMainOperationWindowId,
                    MainWindowPlayerIdAtCreation = mainContext.MainWindowPlayerIdAtCreation, ReservedItemId = descriptor.ReservedItemId,
                    OriginalSupplyZoneId = descriptor.OriginalSupplyZoneId, DestinationHandlerId = descriptor.DestinationHandlerId };
                work.EffectRuntime.DeferredResponses.Add(response);
                registry.TryGetDeferredResponseDestination(response.DestinationHandlerId, out var destination);
                var adopted = destination.AdoptPendingDestination(work, root, response);
                if (adopted == null || adopted.MovedCount != 1 || string.IsNullOrEmpty(response.ReservationZoneId) ||
                    !work.ItemZones.Items.Any(i => i.ItemId == response.ReservedItemId && i.ZoneId == response.ReservationZoneId))
                    throw new KernelException(EffectFaultCodes.InvalidEventResponse, "旁路物品交接没有完成。");
                var departure = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == response.SourceDepartureEffectId);
                ItemTransferEffectExecutor.Set(departure, "destinationHandoffResponseId", NormalizedValue.CreateString(response.DeferredResponseId));
                entries.Add(new RuleJournalEntry { Kind = RuleJournalEntryKind.EffectNode, EntityId = rootId, Detail = "deferred_response" });
                AppendChildEffects(work, root, pair.Value, "event", handler.SubscriptionId, entries,
                    settlementTriggerId: sourceEvent.EventId, operationContext: mainContext);
                ActivateNextChild(work.EffectRuntime, root);
                attached.Add(rootId); responseIds.Add(response.DeferredResponseId);
                entries.Add(new RuleJournalEntry { Kind = RuleJournalEntryKind.DomainState, EntityId = response.DeferredResponseId, Detail = "deferred_response_pending" });
            }
            return true;
        }

        private static bool IsRequiredEventLocation(GameState state, string effectId)
        {
            var runtime = state.EffectRuntime; var visited = new HashSet<string>(StringComparer.Ordinal);
            var child = runtime.EffectNodes.Find(n => n.EffectId == effectId);
            while (child != null && visited.Add(child.EffectId))
            {
                if (runtime.MainNodes.Any(m => m.ExecutionEffectId == child.EffectId &&
                    m.NodeTypeId != YC.Domain.Rules.RoundMainlineNodeTypeIds.PlayerActionWindow && m.NodeTypeId != YC.Domain.Rules.RoundMainlineNodeTypeIds.PlayerCleanupWindow)) return true;
                var parent = runtime.EffectNodes.Find(n => n.EffectId == child.ParentEffectId);
                if (parent?.EffectTypeId == EffectTypeIds.Condition)
                {
                    if (!parent.FlowStage.StartsWith("right:", StringComparison.Ordinal)) return true;
                }
                if (parent?.ChildSchedulingGroups.Any(g => g.StableGroupKey == "entrance_player_tasks" && g.ChildEffectIds.Contains(child.EffectId)) == true) return true;
                child = parent;
            }
            return false;
        }

        private void CompleteDeferredResponseOnWorking(GameState work, EffectNodeRuntimeState node, List<RuleJournalEntry> entries)
        {
            var response = work.EffectRuntime.DeferredResponses.Find(r => r.ResponseRootEffectId == node.EffectId && r.Status == DeferredResponseStatus.Pending);
            if (response == null) return;
            if (response.ProtocolVersion == 1) { CompleteDepartureResponse(work, node, response, entries); return; }
            if (!registry.TryGetDeferredResponseDestination(response.DestinationHandlerId, out var registration))
                throw new KernelException(EffectFaultCodes.UnknownEventHandler, "旁路去向处理器未登记。");
            var context = new EffectExecutionContext(work, node, registry, limits,
                dependencies => TryAttachDeferredResponseDependenciesOnWorking(work, node.EffectId, dependencies, entries));
            var result = registration.CompleteDeferredDestination(context, response);
            if (result?.Completed != true || !work.ItemZones.Items.Any(i => i.ItemId == response.ReservedItemId && i.ZoneId == result.DestinationZoneId) ||
                work.ItemZones.Items.Any(i => i.ZoneId == response.ReservationZoneId))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, result?.Diagnostic ?? "旁路根终止前去向尚未结清。");
            response.Status = result.Status; response.ResolvedDestinationZoneId = result.DestinationZoneId;
            entries.Add(new RuleJournalEntry { Kind = RuleJournalEntryKind.DomainState, EntityId = response.DeferredResponseId, Detail = "deferred_response_" + response.Status });
        }

        public static bool TryValidateDeferredResponses(GameState state, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = "deferred_response_state_invalid";
            if (state?.EffectRuntime?.DeferredResponses == null || registry == null) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal); var roots = new HashSet<string>(StringComparer.Ordinal);
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var response in state.EffectRuntime.DeferredResponses)
            {
                if (response?.ProtocolVersion == 1) { if (!ValidateDepartureResponse(state, response, registry, out diagnostic)) return false; continue; }
                if (response == null || string.IsNullOrEmpty(response.DeferredResponseId) || !ids.Add(response.DeferredResponseId) ||
                    string.IsNullOrEmpty(response.ResponseRootEffectId) || !roots.Add(response.ResponseRootEffectId) ||
                    !Enum.IsDefined(typeof(DeferredResponseStatus), response.Status) || response.AnsweringPlayerId < 1 ||
                    response.MainWindowPlayerIdAtCreation < 1 || response.MainWindowPlayerIdAtCreation == response.AnsweringPlayerId ||
                    string.IsNullOrEmpty(response.OriginMainOperationWindowId) || string.IsNullOrEmpty(response.ReservationZoneId) ||
                    string.IsNullOrEmpty(response.OriginalSupplyZoneId)) return false;
                var root = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == response.ResponseRootEffectId);
                var sourceEvent = state.EffectRuntime.RuleEvents.Find(e => e.EventId == response.SourceEventId);
                var receipt = state.EffectRuntime.DispatchReceipts.Find(r => r.EventId == response.SourceEventId && r.DeferredResponseIds?.Contains(response.DeferredResponseId) == true);
                var observer = receipt == null ? null : registry.FindEventHandler(receipt.SubscriptionId);
                if (root == null || !string.IsNullOrEmpty(root.ParentEffectId) || root.PlayerId != response.AnsweringPlayerId ||
                    !HasDeferredSchedulingIdentity(state, response, root) ||
                    response.Status == DeferredResponseStatus.Pending && (IsTerminal(root.Status) || !reserved.Add(response.ReservedItemId)) ||
                    !state.ItemZones.Items.Any(i => i.ItemId == response.ReservedItemId) ||
                    !state.EffectRuntime.RuleEvents.Any(e => e.EventId == response.SourceEventId && e.SourceEffectId == response.SourceDepartureEffectId) ||
                    !state.EffectRuntime.DispatchReceipts.Any(r => r.EventId == response.SourceEventId &&
                        r.ActualAttachmentKind == EventResponseAttachmentKind.DeferredPlayerResponse && r.DeferredResponseIds?.Contains(response.DeferredResponseId) == true) ||
                    !registry.TryGetDeferredResponseDestination(response.DestinationHandlerId, out var registration) || registration.AbilityId != response.AbilityId ||
                    receipt == null || sourceEvent == null || observer == null || observer.HandlerId != receipt.HandlerId || observer.EventType != sourceEvent.EventType ||
                    observer.AttachmentKind != EventResponseAttachmentKind.DeferredPlayerResponse || observer.DescribeDeferredResponse == null || observer.AbilityId != response.AbilityId) return false;
                if (!registration.TryValidateDeferredDestination(state, response, out diagnostic)) return false;
            }
            diagnostic = string.Empty; return true;
        }

        public static bool TryFindDeferredResponse(GameState state, string ownerEffectId, out DeferredResponseState response)
        {
            response = null;
            if (state?.EffectRuntime?.DeferredResponses == null || string.IsNullOrEmpty(ownerEffectId)) return false;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var node = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == ownerEffectId);
            while (node != null && visited.Add(node.EffectId))
            {
                var candidate = state.EffectRuntime.DeferredResponses.Find(r => r.ResponseRootEffectId == node.EffectId);
                if (candidate?.ProtocolVersion == 1 && candidate.Status == DeferredResponseStatus.Pending && ValidateDepartureResponse(state, candidate, null, out _))
                { response = candidate; return true; }
                if (candidate != null && candidate.ProtocolVersion == 0 && candidate.Status == DeferredResponseStatus.Pending && !IsTerminal(node.Status) &&
                    string.IsNullOrEmpty(node.ParentEffectId) && candidate.AnsweringPlayerId > 0 &&
                    candidate.MainWindowPlayerIdAtCreation > 0 && candidate.AnsweringPlayerId != candidate.MainWindowPlayerIdAtCreation &&
                    !string.IsNullOrEmpty(candidate.OriginMainOperationWindowId) &&
                    state.EffectRuntime.RuleEvents.Any(e => e.EventId == candidate.SourceEventId && e.SourceEffectId == candidate.SourceDepartureEffectId) &&
                    state.ItemZones.Items.Any(i => i.ItemId == candidate.ReservedItemId) && HasDeferredSchedulingIdentity(state, candidate, node))
                { response = candidate; return true; }
                node = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == node.ParentEffectId);
            }
            return false;
        }

        private static bool HasDeferredSchedulingIdentity(GameState state, DeferredResponseState response, EffectNodeRuntimeState root)
        {
            var runtime = state.EffectRuntime; var cause = root.OperationContext; var facts = cause?.WindowFacts;
            var departure = runtime.EffectNodes.Find(n => n.EffectId == response.SourceDepartureEffectId);
            return runtime.SchemaVersion == EffectRuntimeState.CurrentSchemaVersion &&
                response.DeferredResponseId == StableIdFactory.Create("deferred_response", state.GameId, response.SourceDepartureEffectId,
                    response.ReservedItemId, response.SourceCardInstanceId, response.AbilityId) &&
                root.EffectId == StableIdFactory.Create("deferred_response_root", response.DeferredResponseId) &&
                root.SourceId == "character-card:" + response.SourceCardInstanceId && root.EffectTypeId == SettlementEntryService.SegmentTypeId &&
                facts != null && facts.OperationWindowId == response.OriginMainOperationWindowId &&
                cause.OriginMainOperationWindowId == response.OriginMainOperationWindowId && cause.MainWindowPlayerIdAtCreation == response.MainWindowPlayerIdAtCreation &&
                facts.AuthorizedPlayerIds?.Count == 1 && facts.AuthorizedPlayerIds[0] == response.MainWindowPlayerIdAtCreation &&
                (facts.WindowKind == YC.Domain.Rules.RoundMainlineNodeTypeIds.PlayerActionWindow || facts.WindowKind == YC.Domain.Rules.RoundMainlineNodeTypeIds.PlayerCleanupWindow) &&
                departure != null && ItemTransferEffectExecutor.Text(departure.ContinuationData, "pendingItemId") == response.ReservedItemId &&
                ItemTransferEffectExecutor.Text(departure.ContinuationData, "destinationHandoffResponseId") == response.DeferredResponseId &&
                runtime.DispatchReceipts.Any(r => r.Succeeded && r.ActualAttachmentKind == EventResponseAttachmentKind.DeferredPlayerResponse &&
                    r.EventId == response.SourceEventId && r.DeferredResponseIds?.Contains(response.DeferredResponseId) == true && r.AttachedEffectIds.Contains(root.EffectId)) &&
                state.ItemZones.Zones.Any(z => z.ZoneId == response.ReservationZoneId && z.TemplateId == "core.zone.effect_staging" && z.OwnerEffectId == root.EffectId);
        }

        public static bool BlocksCommand(GameState state, InteractionRequest request, OperationCommandContext commandContext)
        {
            if (request == null || request.Status != "open" || request.IsInternalMainlineInteraction()) return false;
            return state?.RuleConfiguration?.PlayerStageSchedulingMode != YC.Domain.Rules.PlayerStageSchedulingMode.Concurrent ||
                commandContext == null || !TryFindDeferredResponse(state, request.OwnerEffectId, out var response) ||
                request.AnsweringPlayerId != response.AnsweringPlayerId || response.ProtocolVersion == 1 && !response.IsBypass;
        }

        public static IReadOnlyList<DeferredResponseDependency> GetDeferredResponseDependencies(GameState state, DeferredDependencyQuery query)
        {
            if (state?.EffectRuntime?.DeferredResponses == null || query == null) return Array.Empty<DeferredResponseDependency>();
            TryFindDeferredResponse(state, query.OwnerEffectId, out var ownResponse);
            ownResponse = ownResponse ?? InfluenceDepartureProtocol.Response(state, query.OwnerEffectId);
            return state.EffectRuntime.DeferredResponses
                .Where(r => (r.Status == DeferredResponseStatus.Pending || r.ProtocolVersion == 1 && r.Status == DeferredResponseStatus.Queued) &&
                    r.DeferredResponseId != ownResponse?.DeferredResponseId && !(ownResponse?.ProtocolVersion == 1 && ownResponse.DepartureId.Length > 0 && r.DepartureId == ownResponse.DepartureId) && MatchesDependency(state, r, query))
                .OrderBy(r => r.DeferredResponseId, StringComparer.Ordinal).GroupBy(r => r.DeferredResponseId, StringComparer.Ordinal)
                .Select(g => new DeferredResponseDependency { DeferredResponseId = g.Key, ResponseRootEffectId = g.First().ResponseRootEffectId,
                    Reason = "等待真实旁路去向：" + query.DependencyKind }).ToArray();
        }

        private static bool MatchesDependency(GameState state, DeferredResponseState response, DeferredDependencyQuery query)
        {
            if (response.ProtocolVersion == 1 && response.Kind == "optional_effect")
            {
                if (query.DependencyKind == DeferredDependencyKind.FinalizeGame) return true;
                if (query.DependencyKind == DeferredDependencyKind.CarrierExit) return response.SourceCardInstanceId == query.SourceCardInstanceId;
                if (query.DependencyKind == DeferredDependencyKind.CardMarkerConsumption && query.SourceCardInstanceId == response.SourceCardInstanceId)
                {
                    var definition = state.ItemZones.Items.Single(i => i.ItemId == response.SourceCardInstanceId).DefinitionId;
                    var permissions = state.ContentDefinitions.AbilitySources.SingleOrDefault(s => s.AbilityId == response.AbilityId)?.AllowedOperations ?? new List<string>();
                    if(query.RequiredOwnerPlayerId>=0 && query.RequiredOwnerPlayerId!=response.AnsweringPlayerId || query.ItemIds?.Count>0 || !query.ConsumesAll && query.RequiredCount<=0) return false;
                    var types=ItemTypeRegistry.CreateBuiltIn();
                    return permissions.Where(p => p.StartsWith("carrier.load:" + definition + ":", StringComparison.Ordinal)).Any(p =>
                    {
                        var fields=p.Split(':'); if(fields.Length!=4) return false;
                        bool matches=fields[3].Split(',').Any(t=>(string.IsNullOrEmpty(query.RequiredTypeId) || types.IsAssignableTo(t,query.RequiredTypeId)) &&
                            (query.RequiredTypeIds.Count==0 || query.RequiredTypeIds.Any(w=>types.IsAssignableTo(t,w))));
                        return matches && state.ItemZones.Zones.Any(z => z.ParentId == response.SourceCardInstanceId && z.LocalZoneId == fields[2] &&
                            (string.IsNullOrEmpty(query.SourceZoneId) ? z.LocalZoneId=="markers" : z.ZoneId == query.SourceZoneId));
                    });
                }
                return false;
            }
            switch (query.DependencyKind)
            {
                case DeferredDependencyKind.FinalizeGame: return true;
                case DeferredDependencyKind.CarrierExit: return response.SourceCardInstanceId == query.SourceCardInstanceId;
                case DeferredDependencyKind.ExactItemUse: return query.ItemIds?.Contains(response.ReservedItemId) == true;
                case DeferredDependencyKind.CardMarkerConsumption:
                    if (response.SourceCardInstanceId != query.SourceCardInstanceId) return false;
                    if (!string.IsNullOrEmpty(query.SourceZoneId) && !PendingStorageZones(state, response).Contains(query.SourceZoneId)) return false;
                    if (query.ItemIds?.Count > 0) return query.ItemIds.Contains(response.ReservedItemId);
                    var zone = state.ItemZones.Zones.Find(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == query.SourceCardInstanceId &&
                        (string.IsNullOrEmpty(query.SourceZoneId) ? z.LocalZoneId == "markers" : z.ZoneId == query.SourceZoneId));
                    return WouldAffectSelection(state, response, query, zone?.ZoneId);
                case DeferredDependencyKind.SupplyRequirement:
                    return !string.IsNullOrEmpty(query.OriginalSupplyZoneId) && response.OriginalSupplyZoneId == query.OriginalSupplyZoneId &&
                        WouldAffectSelection(state, response, query, query.OriginalSupplyZoneId);
                default: throw new ArgumentException("未知旁路依赖语义。", nameof(query));
            }
        }

        private static HashSet<string> PendingStorageZones(GameState state, DeferredResponseState response)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (response.ProtocolVersion == 1)
            {
                var definition = state.ItemZones.Items.Single(i => i.ItemId == response.SourceCardInstanceId).DefinitionId;
                var permissions = state.ContentDefinitions.AbilitySources.SingleOrDefault(s => s.AbilityId == response.AbilityId)?.AllowedOperations ?? new List<string>();
                foreach (var permission in permissions.Where(p => p.StartsWith("carrier.store:" + definition + ":", StringComparison.Ordinal)))
                    foreach (var zone in state.ItemZones.Zones.Where(z => z.ParentId == response.SourceCardInstanceId && z.LocalZoneId == permission.Split(':')[2])) result.Add(zone.ZoneId);
            }
            void Add(string type, NormalizedValue args)
            {
                if (type != ContentPrimitiveEffectExecutor.StoreRemoved || ItemTransferEffectExecutor.Text(args, "cardInstanceId") != response.SourceCardInstanceId ||
                    ItemTransferEffectExecutor.Text(args, "influenceItemId") != response.ReservedItemId) return;
                string local = ItemTransferEffectExecutor.Text(args, "localZoneId");
                foreach (var zone in state.ItemZones.Zones.Where(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == response.SourceCardInstanceId && z.LocalZoneId == local)) result.Add(zone.ZoneId);
            }
            void Specs(IEnumerable<EffectSpecRuntimeState> specs)
            { foreach (var spec in specs) { Add(spec.EffectTypeId, spec.NormalizedArguments); Specs(spec.NestedEffects); } }
            if (response.ProtocolVersion == 1) Specs(response.PendingBody);
            var queue = new Queue<string>(); var seen = new HashSet<string>(); queue.Enqueue(response.ResponseRootEffectId);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue(); if (!seen.Add(id)) continue;
                var node = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == id); if (node == null) continue;
                Add(node.EffectTypeId, node.NormalizedArguments); Specs(node.NestedEffects);
                foreach (string child in node.ChildEffectIds) queue.Enqueue(child);
            }
            return result;
        }
        private static bool WouldAffectSelection(GameState state, DeferredResponseState response, DeferredDependencyQuery query, string zoneId)
        {
            var pending = state.ItemZones.Items.Find(i => i.ItemId == response.ReservedItemId);
            if (pending == null || pending.PhysicalKind != ItemPhysicalKind.Cube) return false;
            var types = ItemTypeRegistry.CreateBuiltIn();
            bool TypeMatches(ItemInstanceState item) => item.PhysicalKind == ItemPhysicalKind.Cube &&
                (string.IsNullOrEmpty(query.RequiredTypeId) || types.IsAssignableTo(item.TypeId, query.RequiredTypeId)) &&
                (query.RequiredTypeIds.Count == 0 || query.RequiredTypeIds.Any(t => types.IsAssignableTo(item.TypeId, t))) &&
                (query.RequiredOwnerPlayerId < 0 || item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == query.RequiredOwnerPlayerId);
            if (!TypeMatches(pending)) return false;
            if (query.ItemIds?.Count > 0) return query.ItemIds.Contains(pending.ItemId);
            if (query.ConsumesAll) return true;
            if (query.RequiredCount <= 0) return false;
            var current = state.ItemZones.Items.Where(i => i.ZoneId == zoneId && TypeMatches(i)).OrderBy(i => i.ItemId, StringComparer.Ordinal).Take(query.RequiredCount).ToArray();
            return current.Length < query.RequiredCount || StringComparer.Ordinal.Compare(pending.ItemId, current.Last().ItemId) < 0;
        }

        public bool TryAttachDeferredResponseDependenciesOnWorking(GameState work, string ownerEffectId,
            IReadOnlyList<DeferredResponseDependency> dependencies, List<RuleJournalEntry> entries)
        {
            if (work?.EffectRuntime == null || entries == null || ReferenceEquals(work, state))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "旁路依赖必须挂在当前工作副本中。");
            if (dependencies == null || dependencies.Count == 0) return false;
            if (dependencies.Any(d => d == null)) throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "旁路依赖不能为空。");
            bool waiting = false;
            foreach (var dependency in dependencies.OrderBy(d => d.DeferredResponseId, StringComparer.Ordinal))
            {
                var response = work.EffectRuntime.DeferredResponses.Find(r => r.DeferredResponseId == dependency.DeferredResponseId && r.ResponseRootEffectId == dependency.ResponseRootEffectId);
                if (response == null) throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "旁路依赖不属于该工作副本。");
                if (response.Status != DeferredResponseStatus.Pending && response.Status != DeferredResponseStatus.Queued) continue;
                string id = StableIdFactory.Create("deferred_dependency", ownerEffectId, response.DeferredResponseId);
                bool existing = work.EffectRuntime.Blockers.Any(b => b.BlockerId == id);
                AddBlockerOnWorking(work, ownerEffectId, response.ResponseRootEffectId, string.Empty, "deferred_response_dependency", dependency.Reason, id);
                if (!existing) entries.Add(new RuleJournalEntry { Kind = RuleJournalEntryKind.DomainState, EntityId = id, Detail = dependency.Reason });
                waiting = true;
            }
            return waiting;
        }
    }
}
