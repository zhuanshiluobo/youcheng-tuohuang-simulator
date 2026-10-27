using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public partial class EffectTreeExecutor
    {
        private bool TryQueueDepartureResponses(GameState work, RuleEvent e, EffectNodeRuntimeState owner, EffectEventHandlerRegistration handler,
            IList<EffectSpec> specs, List<RuleJournalEntry> entries, List<string> attached, List<string> responseIds)
        {
            var d = InfluenceDepartureProtocol.ForEvent(work, e);
            if (d == null || e.EventType != InfluenceEventTypeIds.InfluenceRemoved) return false;
            var ordinary = new List<EffectSpec>();
            foreach (var spec in specs)
            {
                string card = "", ability = handler.AbilityId, kind = "", sourceKey = "";
                AbilitySourceDeclaration declaration = null;
                if (spec.EffectTypeId == AbilitySourceEffectExecutor.TypeId)
                {
                    ability = Text(spec.NormalizedArguments, "abilityId"); card = Text(spec.NormalizedArguments, "itemId"); sourceKey = Text(spec.NormalizedArguments, "sourceKey");
                    declaration = registry.AbilityDeclarations.Single(s => s.AbilityId == ability);
                    if (declaration.AllowedOperations.Contains("response.destination")) kind = "destination";
                    if (declaration.AllowedOperations.Contains("response.optional_effect"))
                    { if (kind.Length > 0) throw new InvalidOperationException("response_kind_conflict"); kind = "optional_effect"; }
                }
                else if (spec.SourceId.StartsWith("character-card:", StringComparison.Ordinal))
                {
                    card = spec.SourceId.Substring("character-card:".Length);
                    if (InfluenceDestinationResolver.HasStorageChoice(spec, spec.PlayerId, card, d.ItemId)) kind = "destination";
                }
                if (kind.Length == 0) { ordinary.Add(spec); continue; }
                if (declaration?.AllowedOperations.Contains("response.batch") == true)
                {
                    if (kind != "optional_effect") throw new InvalidOperationException("batch_response_must_not_hold_item");
                    var batch = work.EffectRuntime.InfluenceDepartureBatches.Single(b => b.BatchId == d.BatchId);
                    var offer = batch.Offers.SingleOrDefault(o => o.SourceKey == sourceKey);
                    if (offer == null) { offer = new DepartureBatchOffer { SourceKey = sourceKey, Body = spec.ToRuntimeState(), EventId = e.EventId, SubscriptionId = handler.SubscriptionId }; batch.Offers.Add(offer); }
                    if (!offer.DepartureIds.Contains(d.DepartureId)) { offer.DepartureIds.Add(d.DepartureId); offer.EventIds.Add(e.EventId); }
                    continue;
                }
                QueueResponse(work, e, owner, handler, spec, d, kind, card, ability, sourceKey, attached, responseIds, entries);
            }
            if (ordinary.Count == specs.Count) return false;
            if (ordinary.Count > 0) attached.AddRange(AppendChildEffects(work, owner, ordinary, "event", handler.SubscriptionId, entries, settlementTriggerId: e.EventId, operationContext: e.OperationContext));
            return true;
        }

        private void QueueResponse(GameState work, RuleEvent e, EffectNodeRuntimeState owner, EffectEventHandlerRegistration handler, EffectSpec spec,
            InfluenceDepartureRecord d, string kind, string card, string ability, string sourceKey, List<string> attached, List<string> responseIds, List<RuleJournalEntry> entries,
            string batchId = "", IList<string> sourceEvents = null)
        {
            string identity = batchId.Length > 0 ? batchId : d.DepartureId;
            string id = StableIdFactory.Create("departure_response", identity, card, ability, sourceKey);
            if (work.EffectRuntime.DeferredResponses.Any(r => r.DeferredResponseId == id)) return;
            if (spec.PlayerId < 1 || !work.ItemZones.Items.Any(i => i.ItemId == card && i.ControllerSubject.PlayerId == spec.PlayerId)) throw new InvalidOperationException("departure_response_source_invalid");
            bool bypass = work.RuleConfiguration.PlayerStageSchedulingMode == PlayerStageSchedulingMode.Concurrent &&
                OperationWindowService.TryResolveSingleMainWindowOnWorking(work, e, out _) &&
                (handler.AttachmentKind == EventResponseAttachmentKind.DeferredPlayerResponse ||
                 registry.AbilityDeclarations.Any(a => a.AbilityId == ability && a.AllowedOperations.Contains("response.self")));
            OperationWindowService.TryResolveSingleMainWindowOnWorking(work, e, out var mainContext);
            var rootSpec = SettlementEntryService.CreateCleanupSegment(spec.PlayerId, id, Array.Empty<EffectSpec>());
            rootSpec.SourceId = "departure-response:" + card;
            string rootId = StableIdFactory.Create("departure_response_root", id);
            var root = CreateNode(rootSpec, rootId, owner.RoundId, "");
            root.OperationContext = (mainContext ?? e.OperationContext ?? owner.OperationContext)?.Clone();
            root.FlowStage = "settling";
            work.EffectRuntime.EffectNodes.Add(root);
            var response = new DeferredResponseState { ProtocolVersion = 1, Kind = kind, DepartureId = kind == "destination" ? d.DepartureId : "",
                BatchId = batchId, SourceOperationEffectId = e.SourceEffectId, IsBypass = bypass, RequiresProducerCompletion = !bypass || IsRequiredEventLocation(work, e.SourceEffectId), SourceKey = sourceKey,
                SourceEventIds = sourceEvents?.ToList() ?? new List<string> { e.EventId }, SourceEventId = e.EventId,
                DeferredResponseId = id, ResponseRootEffectId = rootId, SourceCardInstanceId = card, AbilityId = ability,
                SourceDepartureEffectId = kind == "destination" ? e.SourceEffectId : "", ReservedItemId = kind == "destination" ? d.ItemId : "",
                OriginalSupplyZoneId = kind == "destination" ? d.OriginalSupplyZoneId : "", DestinationHandlerId = kind == "destination" ? "departure.destination.v2" : "",
                AnsweringPlayerId = spec.PlayerId, OriginMainOperationWindowId = mainContext?.OriginMainOperationWindowId ?? "",
                MainWindowPlayerIdAtCreation = mainContext?.MainWindowPlayerIdAtCreation ?? -1, Status = DeferredResponseStatus.Queued,
                PendingBody = new List<EffectSpecRuntimeState> { spec.ToRuntimeState() }, SubscriptionId = handler.SubscriptionId,
                DefinitionVersion = handler.DefinitionVersion, HandlerHash = handler.ContentHash };
            response.SourceRef = sourceKey.Length > 0 ? GameStateCloneService.DeepClone(registry.QuerySources(work, spec.PlayerId).Single(s => s.SourceKey == sourceKey)) : null;
            response.VersionRef = new AbilityVersionRef { ContentPackHash = registry.ContentPackHash, DefinitionVersion = response.SourceRef?.DeclarationVersion ?? handler.DefinitionVersion, HandlerHash = sourceKey.Length > 0 ? registry.HandlerHash(registry.AbilityDeclarations.Single(a => a.AbilityId == ability).HandlerRef) : handler.ContentHash, ExecutorVersion = InfluenceDepartureProtocol.Version };
            work.EffectRuntime.DeferredResponses.Add(response);
            if (kind == "destination") d.ResponseIds.Add(id);
            attached.Add(rootId); responseIds.Add(id);
            entries.Add(new RuleJournalEntry { Kind = RuleJournalEntryKind.DomainState, EntityId = id, Detail = "departure_response_queued" });
        }

        private void FlushDepartureResponses(GameState work, List<RuleJournalEntry> entries)
        {
            foreach (var d in work.EffectRuntime.InfluenceDepartures.Where(v => v.Status == "open").ToList())
            {
                // 执行器已建立记录但Removed尚在待发布列表时，不能提前认定没有响应。
                if (!d.EventIds.Any(id => work.EffectRuntime.RuleEvents.Any(e => e.EventId == id && e.EventType == InfluenceEventTypeIds.InfluenceRemoved))) continue;
                ActivateClaim(work, d, entries);
                // 没有可执行去向时先完成原有返供；不能让可选响应挡住负责返供的生产节点。
                if (d.Status == "open" && d.CurrentIndex == d.ResponseIds.Count)
                {
                    var custody = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == d.CustodyRootId);
                    new ItemTransferService().TransferDepartureCustodyOnWorking(work, d, custody, d.OriginalSupplyZoneId);
                    d.Status = "returned"; d.DestinationZoneId = d.OriginalSupplyZoneId;
                }
            }
            foreach (var batch in work.EffectRuntime.InfluenceDepartureBatches.Where(b => b.Sealed))
            foreach (var offer in batch.Offers.Where(o => !work.EffectRuntime.DeferredResponses.Any(r => r.ProtocolVersion == 1 && r.BatchId == batch.BatchId && r.SourceKey == o.SourceKey)).ToList())
            {
                if (!InfluenceDepartureProtocol.DestinationsSettled(work, offer.EventIds)) continue;
                var source = registry.QuerySources(work, -1, "event").SingleOrDefault(v => v.SourceKey == offer.SourceKey);
                var eligible = new List<int>();
                if (source != null)
                    for (int index = 0; index < offer.EventIds.Count; index++)
                    {
                        var trigger = work.EffectRuntime.RuleEvents.Single(v => v.EventId == offer.EventIds[index]);
                        var registration = registry.FindEventHandler(offer.SubscriptionId);
                        var original = AbilitySourceEffectExecutor.Create(source, trigger, registration.HandlerId, false, work.Round);
                        if (registry.HasSettledEventResponseBody(new EffectEventHandlerContext(work, trigger,
                            work.EffectRuntime.EffectNodes.Single(n => n.EffectId == trigger.OwnerNodeId), registration), original, source)) eligible.Add(index);
                    }
                if (eligible.Count == 0) { batch.Offers.Remove(offer); continue; }
                offer.DepartureIds = eligible.Select(index => offer.DepartureIds[index]).ToList();
                offer.EventIds = eligible.Select(index => offer.EventIds[index]).ToList();
                offer.EventId = offer.EventIds[0];
                var first = work.EffectRuntime.RuleEvents.Single(v => v.EventId == offer.EventId);
                offer.Body = AbilitySourceEffectExecutor.Create(source, first, registry.FindEventHandler(offer.SubscriptionId).HandlerId, false, work.Round).ToRuntimeState();
                var spec = EffectRegistry.FromRuntimeSpec(offer.Body);
                var payload = Value(spec.NormalizedArguments, "triggerPayload");
                if (offer.DepartureIds.Count <= 16)
                {
                    InfluenceDestinationResolver.Set(payload, "departureIds", Strings(offer.DepartureIds));
                    InfluenceDestinationResolver.Set(payload, "sourceEventIds", Strings(offer.EventIds));
                }
                else
                {
                    // 全量ID保留在typed批次/offer/response中；聚合正文只传有界凭据和完整数量。
                    InfluenceDestinationResolver.Set(payload, "batchFactsProtocolVersion", NormalizedValue.CreateInteger(2));
                    InfluenceDestinationResolver.Set(payload, "batchFactsDigest", NormalizedValue.CreateString(InfluenceDepartureProtocol.BatchFactsDigest(batch.BatchId, offer)));
                }
                InfluenceDestinationResolver.Set(payload, "departureCount", NormalizedValue.CreateInteger(offer.DepartureIds.Count));
                InfluenceDestinationResolver.Set(payload, "batchId", NormalizedValue.CreateString(batch.BatchId));
                // 不伪造事件，聚合正文仍引用首个真实 Removed 及全部源事件。
                var e = work.EffectRuntime.RuleEvents.Single(v => v.EventId == offer.EventId);
                var receipt = work.EffectRuntime.DispatchReceipts.Single(r => r.EventId == e.EventId && r.SubscriptionId == offer.SubscriptionId);
                var handler = registry.FindEventHandler(receipt.SubscriptionId);
                var d = InfluenceDepartureProtocol.Find(work, offer.DepartureIds[0]);
                var attached = new List<string>(); var responses = new List<string>();
                QueueResponse(work, e, work.EffectRuntime.EffectNodes.Single(n => n.EffectId == e.SourceEffectId), handler, spec, d, "optional_effect",
                    Text(spec.NormalizedArguments, "itemId"), Text(spec.NormalizedArguments, "abilityId"), offer.SourceKey, attached, responses, entries, batch.BatchId, offer.EventIds);
                receipt.AttachedEffectIds.AddRange(attached); receipt.DeferredResponseIds.AddRange(responses); receipt.ActualAttachmentKind = EventResponseAttachmentKind.DeferredPlayerResponse;
                batch.ResponseIds.AddRange(responses);
            }
            foreach (var r in work.EffectRuntime.DeferredResponses.Where(r => r.ProtocolVersion == 1 && r.Kind == "optional_effect" && r.Status == DeferredResponseStatus.Queued).ToList())
            {
                if (!InfluenceDepartureProtocol.DestinationsSettled(work, r.SourceEventIds)) continue;
                if (r.BatchId.Length == 0)
                {
                    var source = registry.QuerySources(work, r.AnsweringPlayerId, "event").SingleOrDefault(v => v.SourceKey == r.SourceKey);
                    var trigger = work.EffectRuntime.RuleEvents.Single(v => v.EventId == r.SourceEventId);
                    var registration = registry.FindEventHandler(r.SubscriptionId);
                    if (source == null || !registry.HasSettledEventResponseBody(new EffectEventHandlerContext(work, trigger,
                        work.EffectRuntime.EffectNodes.Single(n => n.EffectId == trigger.OwnerNodeId), registration), EffectRegistry.FromRuntimeSpec(r.PendingBody[0]), source))
                    { SkipResponse(work, r); continue; }
                }
                ActivateResponse(work, r, entries);
            }
        }
        private bool IsResponseSourceActive(GameState work, DeferredResponseState r) => r.SourceKey.Length > 0
            ? registry.QuerySources(work, r.AnsweringPlayerId).Any(s => s.SourceKey == r.SourceKey && s.ItemId == r.SourceCardInstanceId && s.AbilityId == r.AbilityId)
            : CharacterLifecycleService.IsSubscriptionActive(work, r.SourceCardInstanceId, r.AbilityId);
        private void ActivateClaim(GameState work, InfluenceDepartureRecord d, List<RuleJournalEntry> entries)
        {
            while (d.CurrentIndex < d.ResponseIds.Count)
            {
                var r = work.EffectRuntime.DeferredResponses.Single(v => v.DeferredResponseId == d.ResponseIds[d.CurrentIndex]);
                if (r.Status == DeferredResponseStatus.Pending) return;
                if (r.Status != DeferredResponseStatus.Queued) { d.CurrentIndex++; continue; }
                if (d.Status != "open" || !IsResponseSourceActive(work, r)) { SkipResponse(work, r); d.CurrentIndex++; continue; }
                var root = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == r.ResponseRootEffectId);
                r.ReservationZoneId = InfluenceDestinationResolver.GetStagingZoneId(work, root.EffectId);
                new ItemTransferService().TransferDepartureCustodyOnWorking(work, d, root, r.ReservationZoneId);
                ActivateResponse(work, r, entries); return;
            }
        }
        private static void SkipResponse(GameState work, DeferredResponseState r)
        {
            r.Status = DeferredResponseStatus.Ineligible;
            var root = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == r.ResponseRootEffectId);
            root.Status = EffectNodeStatus.Completed; root.PendingOutcome = EffectPendingOutcome.Completed;
        }
        private void ActivateResponse(GameState work, DeferredResponseState r, List<RuleJournalEntry> entries)
        {
            if (!IsResponseSourceActive(work, r)) { SkipResponse(work, r); return; }
            r.Status = DeferredResponseStatus.Pending;
            var root = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == r.ResponseRootEffectId);
            root.Status = EffectNodeStatus.Ready;
            var bodyIds = AppendChildEffects(work, root, r.PendingBody.Select(EffectRegistry.FromRuntimeSpec).ToList(), "event", r.SubscriptionId, entries, settlementTriggerId: r.SourceEventId, operationContext: root.OperationContext);
            var sourceReceipt = work.EffectRuntime.DispatchReceipts.Single(receipt => receipt.EventId == r.SourceEventId && receipt.SubscriptionId == r.SubscriptionId);
            sourceReceipt.AttachedEffectIds.AddRange(bodyIds);
            ActivateNextChild(work.EffectRuntime, root);
        }
        private void CompleteDepartureResponse(GameState work, EffectNodeRuntimeState node, DeferredResponseState r, List<RuleJournalEntry> entries)
        {
            if (node.PendingOutcome != EffectPendingOutcome.Completed || node.ChildEffectIds.Any(id => work.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).Status != EffectNodeStatus.Completed))
                throw new InvalidOperationException("departure_response_body_failed");
            // Choice 完成不代表所选 Store 成功；失败保留离场现场，不能自动变成放弃。
            if (r.Kind == "destination" && work.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.StoreRemoved &&
                n.Status == EffectNodeStatus.Failed && InfluenceDepartureProtocol.IsWithin(work, n.EffectId, node.EffectId)))
                throw new InvalidOperationException("departure_destination_store_failed");
            if (r.Kind == "optional_effect") { r.Status = DeferredResponseStatus.EffectCompleted; return; }
            var d = InfluenceDepartureProtocol.Find(work, r.DepartureId);
            if (d == null || d.CustodyRootId != node.EffectId) throw new InvalidOperationException("departure_current_claim_invalid");
            r.Status = d.Status == "stored" ? DeferredResponseStatus.Stored : DeferredResponseStatus.Declined;
            r.ResolvedDestinationZoneId = d.DestinationZoneId;
            d.CurrentIndex++;
            ActivateClaim(work, d, entries);
            if (d.Status == "stored")
            {
                foreach (var next in work.EffectRuntime.DeferredResponses.Where(v => v.ProtocolVersion == 1 && v.DepartureId == d.DepartureId && v.Status == DeferredResponseStatus.Queued)) SkipResponse(work, next);
                d.CurrentIndex = d.ResponseIds.Count; FlushDepartureResponses(work, entries); return;
            }
            if (d.CurrentIndex == d.ResponseIds.Count)
            {
                new ItemTransferService().TransferDepartureCustodyOnWorking(work, d, node, d.OriginalSupplyZoneId);
                d.Status = "returned"; d.DestinationZoneId = d.OriginalSupplyZoneId;
                r.Status = DeferredResponseStatus.Returned; r.ResolvedDestinationZoneId = d.DestinationZoneId;
            }
            FlushDepartureResponses(work, entries);
        }
        internal static bool ValidateDepartureResponse(GameState state, DeferredResponseState r, EffectRegistry registry, out string reason)
        {
            reason = "departure_response_invalid";
            if (r.VersionRef == null || r.VersionRef.ExecutorVersion != InfluenceDepartureProtocol.Version || r.SourceKey.Length > 0 && (r.SourceRef?.SourceKey != r.SourceKey || r.SourceRef?.AbilityId != r.AbilityId || r.SourceRef?.ItemId != r.SourceCardInstanceId) || r.ProtocolVersion != 1 || !new[] { "destination", "optional_effect" }.Contains(r.Kind) || r.PendingBody == null || r.PendingBody.Count != 1 || r.SourceEventIds == null || r.SourceEventIds.Count == 0 ||
                r.SourceEventIds.Distinct().Count() != r.SourceEventIds.Count || !Enum.IsDefined(typeof(DeferredResponseStatus), r.Status)) return false;
            var runtime = state.EffectRuntime;
            if (r.BatchId.Length > 0 && !InfluenceDepartureProtocol.ValidBatchPayload(state,r)) return false;
            var originalEvent = runtime.RuleEvents.SingleOrDefault(e => e.EventId == r.SourceEventId);
            string identity = r.BatchId.Length > 0 ? r.BatchId : Text(originalEvent?.Payload, "departureId");
            if (r.DeferredResponseId != StableIdFactory.Create("departure_response", identity, r.SourceCardInstanceId, r.AbilityId, r.SourceKey) ||
                r.ResponseRootEffectId != StableIdFactory.Create("departure_response_root", r.DeferredResponseId) ||
                r.SourceKey.Length > 0 && (r.SourceRef.AuthorizedPlayerId != r.AnsweringPlayerId || Text(r.PendingBody[0].NormalizedArguments, "sourceKey") != r.SourceKey || Text(r.PendingBody[0].NormalizedArguments, "itemId") != r.SourceCardInstanceId || Text(r.PendingBody[0].NormalizedArguments, "abilityId") != r.AbilityId)) return false;
            var root = runtime.EffectNodes.SingleOrDefault(n => n.EffectId == r.ResponseRootEffectId);
            if (root == null || root.ParentEffectId.Length > 0 || root.PlayerId != r.AnsweringPlayerId || r.AnsweringPlayerId < 1 ||
                r.SourceEventIds.Any(id => !runtime.RuleEvents.Any(e => e.EventId == id && e.EventType == InfluenceEventTypeIds.InfluenceRemoved)) ||
                !runtime.RuleEvents.Any(e => e.EventId == r.SourceEventId && e.SourceEffectId == r.SourceOperationEffectId) ||
                !runtime.DispatchReceipts.Any(receipt => receipt.EventId == r.SourceEventId && receipt.SubscriptionId == r.SubscriptionId && receipt.Succeeded && receipt.AttachedEffectIds.Contains(root.EffectId) && receipt.DeferredResponseIds.Contains(r.DeferredResponseId))) return false;
            if (r.IsBypass && (root.OperationContext?.OriginMainOperationWindowId != r.OriginMainOperationWindowId || root.OperationContext?.MainWindowPlayerIdAtCreation != r.MainWindowPlayerIdAtCreation || r.MainWindowPlayerIdAtCreation < 1)) return false;
            if (!r.IsBypass && !r.RequiresProducerCompletion || IsRequiredEventLocation(state, r.SourceOperationEffectId) && !r.RequiresProducerCompletion) return false;
            if (registry != null)
            {
                var handler = registry.FindEventHandler(r.SubscriptionId);
                if (handler == null || handler.DefinitionVersion != r.DefinitionVersion || handler.ContentHash != r.HandlerHash || r.VersionRef.ContentPackHash != registry.ContentPackHash) return false;
                if (r.SourceKey.Length > 0)
                {
                    var declaration = registry.AbilityDeclarations.SingleOrDefault(d => d.AbilityId == r.AbilityId && d.DefinitionId == r.SourceRef.DefinitionId);
                    if (declaration == null || declaration.DeclarationVersion != r.SourceRef.DeclarationVersion || r.VersionRef.DefinitionVersion != declaration.DeclarationVersion || r.VersionRef.HandlerHash != registry.HandlerHash(declaration.HandlerRef)) return false;
                    if (r.IsBypass && !declaration.AllowedOperations.Contains("response.self") && handler.AttachmentKind != EventResponseAttachmentKind.DeferredPlayerResponse) return false;
                }
                else if (r.IsBypass && handler.AttachmentKind != EventResponseAttachmentKind.DeferredPlayerResponse) return false;
            }
            if (r.Kind == "optional_effect")
            {
                if (r.Status == DeferredResponseStatus.Pending && !InfluenceDepartureProtocol.DestinationsSettled(state, r.SourceEventIds))
                { reason = "departure_optional_before_destination"; return false; }
                if (r.DepartureId.Length > 0 || r.ReservedItemId.Length > 0 || r.ReservationZoneId.Length > 0 || r.OriginalSupplyZoneId.Length > 0 || r.SourceDepartureEffectId.Length > 0 || r.DestinationHandlerId.Length > 0 ||
                    !new[] { DeferredResponseStatus.Pending, DeferredResponseStatus.Queued, DeferredResponseStatus.Ineligible, DeferredResponseStatus.EffectCompleted }.Contains(r.Status)) return false;
            }
            else
            {
                var d = InfluenceDepartureProtocol.Find(state, r.DepartureId);
                if (d == null || !d.ResponseIds.Contains(r.DeferredResponseId) || r.ReservedItemId != d.ItemId || r.SourceDepartureEffectId != d.SourceEffectId || r.OriginalSupplyZoneId != d.OriginalSupplyZoneId ||
                    r.Status == DeferredResponseStatus.Pending && d.CustodyRootId != root.EffectId ||
                    r.Status == DeferredResponseStatus.Queued && r.ReservationZoneId.Length > 0) return false;
            }
            if (r.RequestId.Length > 0 && !runtime.InteractionRequests.Any(q => q.RequestId == r.RequestId && q.AnsweringPlayerId == r.AnsweringPlayerId && InfluenceDepartureProtocol.IsWithin(state, q.OwnerEffectId, root.EffectId))) return false;
            if (r.Status == DeferredResponseStatus.Pending && IsTerminal(root.Status) || r.Status != DeferredResponseStatus.Pending && r.Status != DeferredResponseStatus.Queued && root.Status != EffectNodeStatus.Completed) return false;
            reason = ""; return true;
        }
    }
}
