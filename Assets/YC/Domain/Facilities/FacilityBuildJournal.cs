using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Facilities
{
    public sealed class FacilityBuildRecord
    {
        internal NormalizedValue Data;
        public string BuildId => Text(Data, "buildId");
        public string ItemId => Text(Data, "itemId");
        public string DefinitionId => Text(Data, "definitionId");
        public int BuilderId => Number(Data, "builderId");
        public int SlotIndex => Number(Value(Data, "slot"), "slotIndex", -1);
        public bool IsOverlay => Flag(Value(Data, "slot"), "isOverlay");
        public bool EntryRequired => Flag(Data, "entryRequired");
        public string EntryEffectId => Text(Data, "entryEffectId");
        public string ObligationEffectId => Text(Data, "obligationEffectId");
        public string PlacedEventId => Text(Data, "placedEventId");
        public bool ScoreCommitted => Text(Data, "scoreReceiptId").Length > 0;
        public bool AssemblyCommitted => Text(Data, "assemblyReceiptId").Length > 0;
        public NormalizedValue RemovalPrecondition => Value(Value(Data, "slot"), "removalPrecondition")?.Clone() ?? NormalizedValue.CreateNull();
    }
    public static class FacilityBuildJournal
    {
        public const string Version = "1.1.0", EventType = "FacilityPlaced";
        public static FacilityBuildRecord Read(EffectNodeRuntimeState node)
        {
            var data = Value(node?.ContinuationData, "buildFacts");
            return data == null ? null : new FacilityBuildRecord { Data = data.Clone() };
        }
        public static void Start(EffectExecutionContext context, FacilityBuildSourceAuthorization source, bool entryRequired)
        {
            if (Read(context.Node) != null) throw new InvalidOperationException("facility_build_facts_already_created");
            var item = context.State.ItemZones.Items.Single(i => i.ItemId == source.ItemId);
            Set(context.Node, "buildFacts", Object("protocolVersion", NormalizedValue.CreateInteger(1),
                "buildId", NormalizedValue.CreateString(context.Node.EffectId), "itemId", NormalizedValue.CreateString(item.ItemId),
                "definitionId", NormalizedValue.CreateString(item.DefinitionId), "builderId", NormalizedValue.CreateInteger(context.Node.PlayerId),
                "sourceZoneId", NormalizedValue.CreateString(source.SourceZoneId), "sourceKind", NormalizedValue.CreateString(source.SourceKind),
                "sourceKey", NormalizedValue.CreateString(source.SourceKey), "sourceContentHash", NormalizedValue.CreateString(source.ContentHash),
                "sourceVersion", NormalizedValue.CreateString(source.DefinitionVersion), "sourceHandlerHash", NormalizedValue.CreateString(source.HandlerHash),
                "slot", NormalizedValue.CreateNull(), "placementEffectId", NormalizedValue.CreateString(""),
                "scoreReceiptId", NormalizedValue.CreateString(""), "placedEventId", NormalizedValue.CreateString(""),
                "assemblyReceiptId", NormalizedValue.CreateString(""), "assemblyChildIds", Strings(Array.Empty<string>()), "entryRequired", NormalizedValue.CreateBoolean(entryRequired),
                "entryEffectId", NormalizedValue.CreateString(""), "obligationEffectId", NormalizedValue.CreateString("")));
        }
        private static void Change(EffectNodeRuntimeState node, string key, NormalizedValue value)
        {
            var data = Read(node)?.Data ?? throw new InvalidOperationException("facility_build_facts_missing");
            var old = data.Properties.SingleOrDefault(p => p.Name == key);
            if (old == null) throw new InvalidOperationException("facility_build_fact_unknown");
            old.Value = value; Set(node, "buildFacts", data);
        }
        public static void SelectSlot(EffectNodeRuntimeState node, FacilityBuildSlotCandidate slot) => Change(node, "slot", EncodeSlot(slot));
        public static FacilityBuildSlotCandidate SelectedSlot(EffectNodeRuntimeState node)
        {
            var value = Value(Read(node)?.Data, "slot");
            return value == null || value.Kind == NormalizedValueKind.Null ? null : DecodeSlot(value);
        }
        public static void RecordScore(EffectExecutionContext context)
        {
            var facts = Read(context.Node) ?? throw new InvalidOperationException("facility_build_facts_missing");
            if (facts.ScoreCommitted) throw new InvalidOperationException("facility_build_score_duplicate");
            string operationId = context.Node.EffectId + ":place";
            var transfer = context.ChildNodes.Single(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId && Text(n.NormalizedArguments, "operationId") == operationId);
            if (transfer.Status != EffectNodeStatus.Completed || Text(Value(transfer.ContinuationData, "transferBatchReceipt"), "operationId") != operationId)
                throw new InvalidOperationException("facility_build_placement_receipt_missing");
            Change(context.Node, "placementEffectId", NormalizedValue.CreateString(transfer.EffectId));
            Change(context.Node, "scoreReceiptId", NormalizedValue.CreateString(StableIdFactory.Create("build-score", context.Node.EffectId, facts.ItemId)));
        }
        public static EffectEventRequest PlacedEvent(EffectExecutionContext context)
        {
            var facts = Read(context.Node) ?? throw new InvalidOperationException("facility_build_facts_missing");
            if (!facts.ScoreCommitted || SelectedSlot(context.Node) == null) throw new InvalidOperationException("facility_build_not_placed");
            string id = StableIdFactory.Create("event", context.Node.EffectId, EventType, "1");
            Change(context.Node, "placedEventId", NormalizedValue.CreateString(id));
            return new EffectEventRequest { EventId = id, EventType = EventType, SourceEffectId = context.Node.EffectId,
                OwnerNodeId = context.Node.EffectId, PlayerId = context.Node.PlayerId, DefinitionVersion = "1",
                SemanticKey = "facility_placed", Visibility = "public", ResponseKind = RuleEventResponseKind.Effects,
                Payload = Facts(context.Node) };
        }
        // 在ApplyStepResult的前置事件与普通子节点全部装配完成后、同一次提交之前调用。
        public static void RecordAssembly(GameState state, EffectNodeRuntimeState build)
        {
            var facts = Read(build);
            if (facts == null || facts.PlacedEventId.Length == 0 || facts.AssemblyCommitted) return;
            if (!state.EffectRuntime.RuleEvents.Any(e => e.EventId == facts.PlacedEventId && e.SourceEffectId == build.EffectId && e.EventType == EventType))
                throw new InvalidOperationException("facility_placed_event_missing");
            var entry = build.ChildEffectIds.Select(id => state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id))
                .Where(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Activate &&
                    Value(n.NormalizedArguments, "facilityInstanceId")?.ReferenceId == facts.ItemId).ToList();
            if (entry.Count != (facts.EntryRequired ? 1 : 0)) throw new InvalidOperationException("facility_build_entry_assembly_invalid");
            var slot = SelectedSlot(build);
            var obligations = build.ChildEffectIds.Select(id => state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id))
                .Where(n => n.RequiredOutcome != null && n.ContinuationContentInstanceId == slot.OverlayAuthorization?.ItemId &&
                    n.ContinuationAbilityId == slot.RequiredResponseAbilityId).ToList();
            if(slot.IsOverlay)
                foreach(var root in build.ChildEffectIds.Select(id=>state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id)).Where(n=>IsWrappedResponse(state,build,n)))
                {
                    if(root.RequiredOutcome!=null || root.AllowDecline) throw new InvalidOperationException("facility_wrapped_requirement_conflict");
                    root.RequiredOutcome=new RequiredEffectOutcome { ResultField="completedCount",ExpectedValue=Number(slot.RemovalPrecondition,"requiredCount") };
                    obligations.Add(root);
                }
            if (slot.IsOverlay)
            {
                if (obligations.Count != 1 || obligations[0].AllowDecline ||
                    obligations[0].RequiredOutcome.ExpectedValue != Number(slot.RemovalPrecondition, "requiredCount") ||
                    obligations[0].RequiredOutcome.ResultField != "completedCount")
                    throw new InvalidOperationException("facility_required_response_missing");
                if (entry.Count == 1 && build.ChildEffectIds.IndexOf(obligations[0].EffectId) >= build.ChildEffectIds.IndexOf(entry[0].EffectId))
                    throw new InvalidOperationException("facility_required_response_order_invalid");
                Change(build, "obligationEffectId", NormalizedValue.CreateString(obligations[0].EffectId));
            }
            Change(build, "entryEffectId", NormalizedValue.CreateString(entry.FirstOrDefault()?.EffectId ?? ""));
            Change(build, "assemblyChildIds", Strings(build.ChildEffectIds));
            Change(build, "assemblyReceiptId", NormalizedValue.CreateString(StableIdFactory.Create("build-assembly", facts.PlacedEventId,
                string.Join("|", build.ChildEffectIds))));
        }
        public static NormalizedValue Facts(EffectNodeRuntimeState build)
        {
            var record = Read(build);
            if (record == null) return NormalizedValue.CreateNull();
            var slot = Value(record.Data, "slot");
            return Object("protocolVersion", NormalizedValue.CreateInteger(1), "buildId", NormalizedValue.CreateString(record.BuildId),
                "itemId", NormalizedValue.CreateString(record.ItemId), "definitionId", NormalizedValue.CreateString(record.DefinitionId),
                "builderId", NormalizedValue.CreateInteger(record.BuilderId), "sourceZoneId", Value(record.Data, "sourceZoneId").Clone(),
                "slotIndex", NormalizedValue.CreateInteger(record.SlotIndex), "previousTopItemId", Value(slot, "expectedTopItemId")?.Clone() ?? NormalizedValue.CreateString(""),
                "overlay", NormalizedValue.CreateBoolean(record.IsOverlay), "overlayAuthorization", Value(slot, "overlayAuthorization")?.Clone() ?? NormalizedValue.CreateNull(),
                "removalPrecondition", record.RemovalPrecondition, "entryRequired", NormalizedValue.CreateBoolean(record.EntryRequired),
                "entryEffectId", NormalizedValue.CreateString(record.EntryEffectId));
        }
        public static bool TryValidate(GameState state, EffectNodeRuntimeState build, out string reason)
        {
            reason = "facility_build_facts_invalid";
            var record = Read(build);
            if (record == null)
            {
                if (build.DefinitionVersion == "2.0.0" && (build.FlowStage.Length > 0 || build.ChildEffectIds.Count > 0 ||
                    build.Status == EffectNodeStatus.Completed)) return false;
                reason = ""; return true; // 新根尚未执行，或最初资格校验已明确失败；不能补造建设事实。
            }
            var d = record.Data;
            var required = new[] { "protocolVersion", "buildId", "itemId", "definitionId", "builderId", "sourceZoneId", "sourceKind", "sourceKey",
                "sourceContentHash", "sourceVersion", "sourceHandlerHash", "slot", "placementEffectId", "scoreReceiptId", "placedEventId",
                "assemblyReceiptId", "assemblyChildIds", "entryRequired", "entryEffectId", "obligationEffectId" };
            if (d.Kind != NormalizedValueKind.Object || !d.IsValid() || d.Properties.Count != required.Length ||
                d.Properties.Any(p => !required.Contains(p.Name)) || Number(d, "protocolVersion") != 1 ||
                record.BuildId != build.EffectId || record.BuilderId != build.PlayerId ||
                state.ItemZones.Items.SingleOrDefault(i => i.ItemId == record.ItemId)?.DefinitionId != record.DefinitionId ||
                Text(d, "sourceContentHash") != state.ContentPackHash || Value(d, "entryRequired")?.Kind != NormalizedValueKind.Boolean)
                return false;
            var slotValue = Value(d, "slot");
            if (slotValue == null || slotValue.Kind != NormalizedValueKind.Null && slotValue.Kind != NormalizedValueKind.Object) return false;
            if (slotValue.Kind == NormalizedValueKind.Object)
            {
                var slot = SelectedSlot(build);
                if (slot.ProtocolVersion != 1 || slot.PlayerId != build.PlayerId || slot.NewItemId != record.ItemId ||
                    slot.SlotIndex < 0 || slot.SlotIndex >= BuildFacilityService.CityBoardSlotCount ||
                    slot.CellZoneId.Length == 0 || slot.OrderDigest.Length == 0 || slot.StateRevision < 0 ||
                    slot.IsOverlay && (slot.ExpectedTopItemId.Length == 0 || slot.OverlayAuthorization == null ||
                        slot.RequiredResponseAbilityId.Length == 0 || Number(slot.RemovalPrecondition, "requiredCount") < 1)) return false;
            }
            if (record.ScoreCommitted && slotValue.Kind != NormalizedValueKind.Object ||
                !record.ScoreCommitted && (build.FlowStage == "score" || build.FlowStage == "awaiting_entry" || build.Status == EffectNodeStatus.Completed)) return false;
            if (record.ScoreCommitted)
            {
                var transfer = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == Text(d, "placementEffectId"));
                if (transfer?.ParentEffectId != build.EffectId || transfer.Status != EffectNodeStatus.Completed ||
                    Text(Value(transfer.ContinuationData, "transferBatchReceipt"), "operationId") != build.EffectId + ":place" ||
                    Text(d, "scoreReceiptId") != StableIdFactory.Create("build-score", build.EffectId, record.ItemId) ||
                    !transfer.PositionChanges.Any(c => c.ItemId == record.ItemId && c.To.ZoneId == SelectedSlot(build).CellZoneId)) return false;
            }
            if (record.AssemblyCommitted)
            {
                if (!record.ScoreCommitted || !state.EffectRuntime.RuleEvents.Any(e => e.EventId == record.PlacedEventId &&
                    e.SourceEffectId == build.EffectId && e.EventType == EventType)) return false;
                var assembled = Value(d, "assemblyChildIds");
                if (assembled?.Kind != NormalizedValueKind.Array || assembled.Items.Any(v => v.Kind != NormalizedValueKind.String) ||
                    assembled.Items.Select(v => v.StringValue).Distinct(StringComparer.Ordinal).Count() != assembled.Items.Count ||
                    !build.ChildEffectIds.Take(assembled.Items.Count).SequenceEqual(assembled.Items.Select(v => v.StringValue)) ||
                    Text(d, "assemblyReceiptId") != StableIdFactory.Create("build-assembly", record.PlacedEventId,
                        string.Join("|", assembled.Items.Select(v => v.StringValue)))) return false;
                if (record.EntryRequired)
                {
                    var entry = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == record.EntryEffectId);
                    if (!assembled.Items.Any(v => v.StringValue == record.EntryEffectId) || entry?.ParentEffectId != build.EffectId ||
                        entry.EffectTypeId != FacilityEntryEffectTypeIds.Activate || Value(entry.NormalizedArguments, "facilityInstanceId")?.ReferenceId != record.ItemId) return false;
                }
                if (record.IsOverlay)
                {
                    var slot = SelectedSlot(build);
                    var obligation = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == record.ObligationEffectId);
                    if (!assembled.Items.Any(v => v.StringValue == record.ObligationEffectId) || obligation?.ParentEffectId != build.EffectId ||
                        obligation.RequiredOutcome == null || obligation.AllowDecline ||
                        !(IsWrappedResponse(state,build,obligation) ? ValidateWrappedInner(state,obligation) :
                            obligation.ContinuationContentInstanceId == slot.OverlayAuthorization.ItemId && obligation.ContinuationAbilityId == slot.RequiredResponseAbilityId) ||
                        obligation.RequiredOutcome.ResultField != "completedCount" ||
                        obligation.RequiredOutcome.ExpectedValue != Number(slot.RemovalPrecondition, "requiredCount") ||
                        record.EntryRequired && build.ChildEffectIds.IndexOf(record.ObligationEffectId) >= build.ChildEffectIds.IndexOf(record.EntryEffectId)) return false;
                }
            }
            reason = ""; return true;
        }
        // 只认本次Placed正式派发挂入的直接SourceUse；包装中的身份来自冻结声明和原查询授权。
        public static bool IsWrappedResponse(GameState state,EffectNodeRuntimeState build,EffectNodeRuntimeState node)
        {
            var f=Read(build); var slot=SelectedSlot(build); var a=slot?.OverlayAuthorization;
            if(f==null || a==null || node?.EffectTypeId!=AbilitySourceEffectExecutor.TypeId || node.ParentEffectId!=build.EffectId || node.PlayerId!=build.PlayerId) return false;
            var args=node.NormalizedArguments;
            var d=state.ContentDefinitions.AbilitySources.SingleOrDefault(s=>s.DefinitionId==a.DefinitionId && s.AbilityId==slot.RequiredResponseAbilityId);
            if(d==null || Text(args,"itemId")!=a.ItemId || Text(args,"definitionId")!=a.DefinitionId || Text(args,"abilityId")!=d.AbilityId ||
                Text(args,"handlerRef")!=d.HandlerRef || Text(args,"declarationVersion")!=d.DeclarationVersion || Text(args,"activeFaceId")!=a.ActiveFaceId ||
                Text(args,"authorizationRef")!=a.AuthorizationRef || Text(args,"effectiveZoneId")!=a.EffectiveZoneId || Text(args,"sourceKey").Length==0 ||
                Text(args,"triggerEventId")!=f.PlacedEventId || Text(args,"scopeKey")!="event:"+f.PlacedEventId ||
                !state.EffectRuntime.DispatchReceipts.Any(r=>r.Succeeded && r.EventId==f.PlacedEventId && r.AttachedEffectIds.Contains(node.EffectId) &&
                    (r.HandlerId=="facility.overlay.source" || r.HandlerId=="ability-source.event.FacilityPlaced"))) return false;
            var receipt=state.EffectRuntime.AbilitySourceUses.SingleOrDefault(r=>r.OwnerEffectId==node.EffectId);
            return receipt==null ? node.FlowStage.Length==0 : receipt.SourceRef!=null && receipt.SourceKey==Text(args,"sourceKey") &&
                receipt.TriggerEventId==f.PlacedEventId && receipt.AbilityId==d.AbilityId && receipt.SourceRef.ItemId==a.ItemId &&
                receipt.VersionRef?.DefinitionVersion==d.DeclarationVersion && receipt.VersionRef.ContentPackHash==state.ContentPackHash;
        }
        private static bool ValidateWrappedInner(GameState state,EffectNodeRuntimeState root)
        {
            if(root.FlowStage.Length==0) return root.ChildEffectIds.Count==0;
            var r=state.EffectRuntime.AbilitySourceUses.SingleOrDefault(x=>x.OwnerEffectId==root.EffectId);
            if(r==null || root.FlowStage!="body") return false;
            var leaves=root.ChildEffectIds.Select(id=>state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id)).Where(n=>n.RequiredOutcome!=null).ToList();
            return leaves.Count==1 && !leaves[0].AllowDecline && leaves[0].ContinuationAbilityId==r.AbilityId &&
                leaves[0].ContinuationContentHash==r.VersionRef.HandlerHash && leaves[0].RequiredOutcome.ResultField==root.RequiredOutcome.ResultField &&
                leaves[0].RequiredOutcome.ExpectedValue==root.RequiredOutcome.ExpectedValue;
        }
        public static NormalizedValue WrappedResult(GameState state,EffectNodeRuntimeState root)
        {
            var build=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==root.ParentEffectId);
            if(build==null || Read(build)?.ObligationEffectId!=root.EffectId || !IsWrappedResponse(state,build,root) || !ValidateWrappedInner(state,root))
                throw new InvalidOperationException("facility_wrapped_obligation_invalid");
            var leaf=root.ChildEffectIds.Select(id=>state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id)).Single(n=>n.RequiredOutcome!=null);
            if(leaf.Status!=EffectNodeStatus.Completed || Number(leaf.NormalizedResult,root.RequiredOutcome.ResultField,-1)!=root.RequiredOutcome.ExpectedValue)
                throw new InvalidOperationException("facility_wrapped_obligation_unfulfilled");
            return leaf.NormalizedResult.Clone();
        }
        public static NormalizedValue EncodeSlot(FacilityBuildSlotCandidate c) => Object(
            "protocolVersion", NormalizedValue.CreateInteger(1), "playerId", NormalizedValue.CreateInteger(c.PlayerId),
            "slotIndex", NormalizedValue.CreateInteger(c.SlotIndex), "stateRevision", NormalizedValue.CreateInteger(c.StateRevision),
            "cellZoneId", NormalizedValue.CreateString(c.CellZoneId), "newItemId", NormalizedValue.CreateString(c.NewItemId),
            "expectedTopItemId", NormalizedValue.CreateString(c.ExpectedTopItemId), "orderDigest", NormalizedValue.CreateString(c.OrderDigest),
            "isOverlay", NormalizedValue.CreateBoolean(c.IsOverlay), "requiredResponseAbilityId", NormalizedValue.CreateString(c.RequiredResponseAbilityId), "overlayAuthorization", EncodeSource(c.OverlayAuthorization),
            "removalPrecondition", c.RemovalPrecondition.Clone());
        public static FacilityBuildSlotCandidate DecodeSlot(NormalizedValue v) => new FacilityBuildSlotCandidate {
            ProtocolVersion = Number(v, "protocolVersion"), PlayerId = Number(v, "playerId"), SlotIndex = Number(v, "slotIndex", -1),
            StateRevision = Number(v, "stateRevision"), CellZoneId = Text(v, "cellZoneId"), NewItemId = Text(v, "newItemId"),
            ExpectedTopItemId = Text(v, "expectedTopItemId"), OrderDigest = Text(v, "orderDigest"), IsOverlay = Flag(v, "isOverlay"),
            RequiredResponseAbilityId = Text(v, "requiredResponseAbilityId"),
            OverlayAuthorization = DecodeSource(Value(v, "overlayAuthorization")), RemovalPrecondition = Value(v, "removalPrecondition").Clone() };
        public static NormalizedValue EncodeSource(AbilitySourceRef s) => s == null ? NormalizedValue.CreateNull() : Object(
            "sourceKey", NormalizedValue.CreateString(s.SourceKey), "itemId", NormalizedValue.CreateString(s.ItemId),
            "definitionId", NormalizedValue.CreateString(s.DefinitionId), "abilityId", NormalizedValue.CreateString(s.AbilityId),
            "activeFaceId", NormalizedValue.CreateString(s.ActiveFaceId), "declarationVersion", NormalizedValue.CreateString(s.DeclarationVersion),
            "authorizedPlayerId", NormalizedValue.CreateInteger(s.AuthorizedPlayerId), "authorizationRef", NormalizedValue.CreateString(s.AuthorizationRef),
            "effectiveZoneId", NormalizedValue.CreateString(s.EffectiveZoneId), "ruleOwnerItemId", NormalizedValue.CreateString(s.RuleOwnerItemId),
            "grantingSourceRefs", Strings(s.GrantingSourceRefs));
        public static AbilitySourceRef DecodeSource(NormalizedValue v) => v == null || v.Kind == NormalizedValueKind.Null ? null : new AbilitySourceRef {
            SourceKey = Text(v, "sourceKey"), ItemId = Text(v, "itemId"), DefinitionId = Text(v, "definitionId"), AbilityId = Text(v, "abilityId"),
            ActiveFaceId = Text(v, "activeFaceId"), DeclarationVersion = Text(v, "declarationVersion"), AuthorizedPlayerId = Number(v, "authorizedPlayerId"),
            AuthorizationRef = Text(v, "authorizationRef"), EffectiveZoneId = Text(v, "effectiveZoneId"), RuleOwnerItemId = Text(v, "ruleOwnerItemId"),
            GrantingSourceRefs = Value(v, "grantingSourceRefs").Items.Select(i => i.StringValue).ToList() };
    }
}

