using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public sealed partial class InfluenceEffectExecutor
    {
        public static EffectSpec StrictRemove(int player, IEnumerable<string> targets, int required, string reason = "rule.unspecified") =>
            EffectSpec.Create(RemoveEffectTypeId, Object("apiVersion", NormalizedValue.CreateInteger(2), "executingPlayer", InfluenceEffectSpecFactory.PlayerReference(player),
                "targets", Strings(targets), "requiredCount", NormalizedValue.CreateInteger(required), "reasonId", NormalizedValue.CreateString(reason)), player);

        private EffectStepResult ExecuteItemDepartureV2(EffectExecutionContext context, bool replace)
        {
            var node = context.Node; var state = context.State; var args = node.NormalizedArguments;
            var departures = state.EffectRuntime.InfluenceDepartures.Where(d => d.SourceEffectId == node.EffectId).ToList();
            if (node.FlowStage == "awaiting_destination_v2")
            {
                foreach (var d in departures.Where(d => d.Status == "open"))
                {
                    var pending = state.EffectRuntime.DeferredResponses.Where(r => d.ResponseIds.Contains(r.DeferredResponseId) && (r.Status == DeferredResponseStatus.Pending || r.Status == DeferredResponseStatus.Queued)).ToList();
                    if (pending.Count > 0)
                    {
                        // Replace、严格左式及本地响应必须结清；并发普通 Remove 可在保管交接后让无关主链继续。
                        if (replace || Value(args, "targets") != null || pending.Any(r => r.RequiresProducerCompletion))
                        {
                            context.TryAttachDeferredResponseDependenciesOnWorking(pending.Select(r => new DeferredResponseDependency { DeferredResponseId = r.DeferredResponseId, ResponseRootEffectId = r.ResponseRootEffectId, Reason = "等待本次离场去向" }).ToList());
                            return EffectStepResult.Continue("awaiting_destination_v2");
                        }
                        continue;
                    }
                    InfluenceDepartureProtocol.Resolve(context, d, d.OriginalSupplyZoneId);
                }
                InfluenceDestinationResolver.Set(node.NormalizedResult, "responseRefs", Strings(departures.SelectMany(d => d.ResponseIds)));
                InfluenceDestinationResolver.Set(node.NormalizedResult, "destinations", NormalizedValue.CreateArray(departures.Select(d => Object("departureId", NormalizedValue.CreateString(d.DepartureId), "zoneId", NormalizedValue.CreateString(d.Status == "open" ? d.CustodyZoneId : d.DestinationZoneId), "status", NormalizedValue.CreateString(d.Status))).ToList()));
                if (replace)
                {
                    var d = departures.Single();
                    TryReadReplace(args, out int player, out _, out var source, out var owner, out var cause, out _);
                    return EffectStepResult.Continue("placing_replacement_v2").AddChild(InfluenceEffectSpecFactory.PlaceInfluence(player, d.OriginalPosition.SlotId, source.SourceId, owner, cause, node.EffectId, ReadString(args, "itemTypeId", "")));
                }
                return EffectStepResult.Completed(node.NormalizedResult.Clone());
            }
            if (node.FlowStage == "placing_replacement_v2")
            {
                var root = context.ChildNodes.Last(n => n.EffectTypeId == PlaceEffectTypeId || state.EffectRuntime.InfluencePlaceMappings.Any(m => m.ActualRootEffectId == n.EffectId));
                var placed = InfluencePlaceAttachment.ResultNode(state, root.EffectId);
                bool success = placed != null && placed.Status == EffectNodeStatus.Completed && Value(placed.NormalizedResult, "changed")?.BooleanValue == true;
                var d = departures.Single();
                InfluenceDestinationResolver.Set(node.NormalizedResult, "outcome", NormalizedValue.CreateString(success ? "replaced" : "removed_only"));
                InfluenceDestinationResolver.Set(node.NormalizedResult, "placed", placed?.NormalizedResult.Clone() ?? Object("changed", NormalizedValue.CreateBoolean(false)));
                InfluenceDestinationResolver.Set(node.NormalizedResult, "replacedItemIds", Strings(success ? new[] { d.ItemId } : Array.Empty<string>()));
                InfluenceDestinationResolver.Set(node.NormalizedResult, "actualEventTypes", Strings(success ? new[] { InfluenceEventTypeIds.InfluenceRemoved, InfluenceEventTypeIds.InfluencePlaced, InfluenceEventTypeIds.InfluenceReplaced } : new[] { InfluenceEventTypeIds.InfluenceRemoved }));
                var result = EffectStepResult.Completed(node.NormalizedResult.Clone());
                if (success)
                {
                    var payload = DeparturePayloadV2(context, d);
                    InfluenceDestinationResolver.Set(payload, "placeEffectId", InfluenceEffectSpecFactory.EffectReference(placed.EffectId));
                    InfluenceDestinationResolver.Set(payload, "placed", placed.NormalizedResult.Clone());
                    InfluenceDestinationResolver.Set(payload, "placedInfluenceRef", GetResultReference(placed.NormalizedResult, "influenceId", "influence"));
                    var replacedEvent = Event(node, InfluenceEventTypeIds.InfluenceReplaced, d.ItemId, "replaced.v2", payload);
                    d.EventIds.Add(replacedEvent.EventId); result.AddEvent(replacedEvent);
                }
                return result;
            }
            if (node.FlowStage != "" && node.FlowStage != "awaiting_target") return EffectStepResult.Failed("invalid_flow_stage");
            if (!TryPlayer(args, "executingPlayer", out int executing) || executing != node.PlayerId || state.FindPlayer(executing) == null) return EffectStepResult.Failed("invalid_player");
            var candidates = SubjectCandidates(context);
            var rawTargets = Value(args, "targets"); var targets = new List<string>();
            int required = 1;
            if (rawTargets != null)
            {
                required = Number(args, "requiredCount");
                targets = rawTargets.Items.Select(v => v.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v.StringValue).ToList();
                if (targets.Count != required || targets.Distinct().Count() != required || targets.Any(id => !candidates.Contains(id))) return StrictRemovalFailed(required, "incomplete_influence_payment");
            }
            else
            {
                string target = ReadStableId(args, "targetInfluence", "influence");
                if (target.Length == 0)
                {
                    if (candidates.Count == 0) return EmptyResult();
                    if (node.FlowStage == "awaiting_target") target = ReadAnswer(context.GetLatestInteractionAnswer());
                    if (target.Length == 0 || !candidates.Contains(target)) return EffectStepResult.Continue("awaiting_target").AddInteraction(PrepareInteraction(context, CreateRemovalInteraction(context, candidates)));
                }
                if (!candidates.Contains(target)) return EffectStepResult.Failed("invalid_influence_target");
                targets.Add(target);
            }
            // 同一个工作副本提交所有地图变更；先在独立预演副本验证完整集合。
            var preview = GameStateCloneService.DeepClone(state);
            var previewContext = new EffectExecutionContext(preview, preview.EffectRuntime.EffectNodes.Single(n => n.EffectId == node.EffectId), context.Registry);
            try
            {
                foreach (string id in targets) if (!influenceService.Depart(previewContext, id, replace ? "replace" : "remove").Succeeded) return StrictRemovalFailed(required, "incomplete_influence_payment");
            }
            catch (InvalidOperationException) { return StrictRemovalFailed(required, "incomplete_influence_payment"); }
            string batchId = node.DepartureBatchId;
            var batch = batchId.Length == 0 ? null : state.EffectRuntime.InfluenceDepartureBatches.SingleOrDefault(b => b.BatchId == batchId && !b.Sealed && b.ProducerSteps.Contains(node.EffectId));
            if (batchId.Length > 0 && batch == null) throw new InvalidOperationException("departure_batch_producer_invalid");
            if (batch == null)
            {
                batchId = StableIdFactory.Create("departure_batch", node.EffectId);
                batch = new InfluenceDepartureBatch { BatchId = batchId, OwnerEffectId = node.EffectId, ProducerSteps = new List<string> { node.EffectId } };
                state.EffectRuntime.InfluenceDepartureBatches.Add(batch);
            }
            var resultStep = EffectStepResult.Continue("awaiting_destination_v2");
            var records = new List<InfluenceDepartureRecord>();
            for (int i = 0; i < targets.Count; i++)
            {
                var item = state.ItemZones.Items.Single(v => v.ItemId == targets[i]);
                var original = GameStateCloneService.DeepClone(item);
                var fact = influenceService.Depart(context, item.ItemId, replace ? "replace" : "remove");
                if (!fact.Succeeded) throw new InvalidOperationException("departure_commit_changed");
                if (i == 0) node.NormalizedResult = Result(fact, replace ? "replacement_pending" : "removed", "");
                var d = new InfluenceDepartureRecord { DepartureId = StableIdFactory.Create("departure", node.EffectId, i.ToString(CultureInfo.InvariantCulture)),
                    OperationIndex = i, SourceEffectId = node.EffectId, ItemId = item.ItemId, OriginalSubject = GameStateCloneService.DeepClone(original.OwnerSubject),
                    OriginalPosition = new ItemPositionRef { ZoneId = original.ZoneId, SlotId = original.SlotId }, OriginalSupplyZoneId = original.OriginalSupplyZoneId,
                    CauseKind = replace ? "replace" : "remove", CustodyRootId = node.EffectId, CustodyZoneId = item.ZoneId, BatchId = batchId };
                state.EffectRuntime.InfluenceDepartures.Add(d); records.Add(d); batch.DepartureIds.Add(d.DepartureId);
                var e = Event(node, InfluenceEventTypeIds.InfluenceRemoved, item.ItemId, "removed.v2:" + i, DeparturePayloadV2(context, d));
                d.EventIds.Add(e.EventId); resultStep.AddEvent(e);
            }
            batch.FinishedSteps.Add(node.EffectId);
            batch.Sealed = batch.OwnerEffectId == node.EffectId && batch.FinishedSteps.Count == batch.ProducerSteps.Count;
            InfluenceDestinationResolver.Set(node.NormalizedResult, "requestedCount", NormalizedValue.CreateInteger(required));
            InfluenceDestinationResolver.Set(node.NormalizedResult, "actualRemovedCount", NormalizedValue.CreateInteger(records.Count));
            InfluenceDestinationResolver.Set(node.NormalizedResult, "removedItemIds", Strings(records.Select(d => d.ItemId)));
            InfluenceDestinationResolver.Set(node.NormalizedResult, "affectedItemIds", Strings(records.Select(d => d.ItemId)));
            InfluenceDestinationResolver.Set(node.NormalizedResult, "actualEventTypes", Strings(new[] { InfluenceEventTypeIds.InfluenceRemoved }));
            InfluenceDestinationResolver.Set(node.NormalizedResult, "departureIds", Strings(records.Select(d => d.DepartureId)));
            return resultStep;
        }
        private static EffectStepResult StrictRemovalFailed(int requested, string reason) => EffectStepResult.Failed(reason,
            Object("changed", NormalizedValue.CreateBoolean(false), "requestedCount", NormalizedValue.CreateInteger(requested), "actualRemovedCount", NormalizedValue.CreateInteger(0), "removedItemIds", Strings(Array.Empty<string>()), "departureIds", Strings(Array.Empty<string>())));
        private static NormalizedValue DeparturePayloadV2(EffectExecutionContext context, InfluenceDepartureRecord d)
        {
            var item = context.State.ItemZones.Items.Single(i => i.ItemId == d.ItemId);
            var payload = Object("eventProtocolVersion", NormalizedValue.CreateInteger(2), "departureId", NormalizedValue.CreateString(d.DepartureId),
                "batchId", NormalizedValue.CreateString(d.BatchId), "causeKind", NormalizedValue.CreateString(d.CauseKind), "influenceId", NormalizedValue.CreateString(d.ItemId),
                "influenceItemId", NormalizedValue.CreateString(d.ItemId), "influenceRefBeforeRemoval", InfluenceEffectSpecFactory.InfluenceReference(d.ItemId),
                "originalSubject", InfluenceEffectSpecFactory.SubjectValue(d.OriginalSubject.Kind == ItemSubjectKind.Player ? InfluenceIdentity.CreatePlayerOwner(d.OriginalSubject.PlayerId) : new RuleSubjectReference { SubjectType = "company", DefinitionId = d.OriginalSubject.CompanyId }),
                "ownerPlayerId", NormalizedValue.CreateInteger(d.OriginalSubject.PlayerId), "itemTypeId", NormalizedValue.CreateString(item.TypeId),
                "fromSlotId", NormalizedValue.CreateString(d.OriginalPosition.SlotId), "originalSupplyZoneId", NormalizedValue.CreateString(d.OriginalSupplyZoneId),
                "sourceEffectId", NormalizedValue.CreateString(d.SourceEffectId), "removeEffectId", InfluenceEffectSpecFactory.EffectReference(d.SourceEffectId), "replaceEffectId", d.CauseKind == "replace" ? InfluenceEffectSpecFactory.EffectReference(d.SourceEffectId) : NormalizedValue.CreateNull());
            InfluenceDestinationResolver.Set(payload, "affectedItemIds", Strings(new[] { d.ItemId }));
            InfluenceDestinationResolver.Set(payload, "removedItemIds", Strings(new[] { d.ItemId }));
            InfluenceDestinationResolver.Set(payload, "originalPosition", Position(d.OriginalPosition));
            InfluenceDestinationResolver.Set(payload, "destination", Position(new ItemPositionRef { ZoneId = d.CustodyZoneId }));
            InfluenceDestinationResolver.Set(payload, "originalSubjectKeys", Strings(new[] { d.OriginalSubject.Kind == ItemSubjectKind.Player ? "player:" + d.OriginalSubject.PlayerId : "company:" + d.OriginalSubject.CompanyId }));
            return payload;
        }
    }
}
