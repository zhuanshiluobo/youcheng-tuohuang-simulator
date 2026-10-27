using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Cards;
using YC.Domain.Interactions;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class ItemTransferEffectExecutor
    {
        public const string TypeId = "effect.item.move";
        public const string TransferDefinitionVersion = "3.0.0";
        public const int MaximumTokenCount = 99;
        public const string IndependentItemFailurePolicyVersion = "single_item_expected_failure.v1";
        // 单个显式物品的库存/区位过期是可观察的叶子失败，不让成功兄弟或调用续接升级成行动故障。
        // 显式required outcome、权限/类型/参数错误和多枚足额操作仍由各自严格合同处理。
        public static bool IsExpectedIndependentFailure(EffectNodeRuntimeState node) =>
            node?.EffectTypeId == TypeId && node.Status == EffectNodeStatus.Failed && node.RequiredOutcome == null &&
            Number(node.NormalizedArguments, "apiVersion") == 3 && Number(node.NormalizedArguments, "requiredCount") == 1 &&
            ValidateTokenArguments(node.NormalizedArguments).Length == 0 && Value(node.NormalizedArguments, "itemIds").Items.Count <= 1 &&
            new[] { "insufficient_items", "source_zone_changed", "unknown_item" }.Contains(node.FailureReason);
        // v3只提交Lua已经选定的真实实例。它不解释费用、不选实例、不产生选择请求。
        public static string ValidateTokenArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Select(p => p.Name).Distinct().Count() != args.Properties.Count ||
                args.Properties.Any(p => !new[] { "kind", "effectTypeId", "apiVersion", "player", "itemIds", "expectedSource", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy" }.Contains(p.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Value(args, "apiVersion").IntegerValue != 3 ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1)
                return "token_transfer_arguments_invalid";
            var ids = Value(args, "itemIds");
            if (ids == null || ids.Kind != NormalizedValueKind.Array && !(ids.Kind == NormalizedValueKind.Object && ids.Properties.Count == 0) ||
                ids.Items.Count > MaximumTokenCount || ids.Items.Any(i => i.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(i.StringValue)) ||
                ids.Items.Select(i => i.StringValue).Distinct(StringComparer.Ordinal).Count() != ids.Items.Count) return "token_transfer_items_invalid";
            var count = Value(args, "requiredCount");
            if (count?.Kind != NormalizedValueKind.Integer || count.IntegerValue < 1 || count.IntegerValue > MaximumTokenCount) return "required_count_invalid";
            if (!Reference(Value(args, "expectedSource"), "zoneId")) return "invalid_zone_ref";
            var destination = Value(args, "destination");
            if (!Reference(destination, "zoneId") && !(destination?.Kind == NormalizedValueKind.Object && destination.Properties.Count == 1 && Text(destination, "kind") == "original_supply")) return "invalid_zone_ref";
            if (Value(args, "insertion")?.Kind != NormalizedValueKind.Object || Value(args, "insertion").Properties.Count != 1 || Text(Value(args, "insertion"), "kind") != "unordered") return "insertion_rejected";
            if (!ItemTransferService.IsTokenProfile(Text(args, "operationProfileId")) || Value(args, "carrierPolicy") != null && Text(args, "carrierPolicy") != "preserve") return "operation_profile_rejected";
            return "";
        }
        public static NormalizedValue TokenArguments(int player, IEnumerable<string> ids, string source, string destination, int required, string profile = "core.cube_storage") => Object(
            "apiVersion", NormalizedValue.CreateInteger(3), "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),
            "itemIds", Strings(ids), "expectedSource", Object("zoneId", NormalizedValue.CreateString(source)),
            "destination", destination == null ? Object("kind", NormalizedValue.CreateString("original_supply")) : Object("zoneId", NormalizedValue.CreateString(destination)),
            "insertion", Object("kind", NormalizedValue.CreateString("unordered")), "operationProfileId", NormalizedValue.CreateString(profile), "requiredCount", NormalizedValue.CreateInteger(required));
        public static EffectSpec TokenSpec(int player, IEnumerable<string> ids, string source, string destination, int required, string profile = "core.cube_storage") =>
            new EffectSpec(TypeId, TokenArguments(player, ids, source, destination, required, profile)) { PlayerId = player, DefinitionVersion = TransferDefinitionVersion, Visibility = "owner" };
        private static EffectStepResult ExecuteTokens(EffectExecutionContext context)
        {
            var service = new ItemTransferService();
            // 已提交节点先验证并复用原收据；不能拿移动后的来源状态重新规划或再次扣除。
            if (Value(context.Node.ContinuationData, "transferBatchReceipt") != null)
            {
                if (!ItemTransferService.TryValidateTokenState(context.State, context.Node, out string invalid)) throw new InvalidOperationException(invalid);
                return TransferCompleted(service.RelocateBatch(context, context.Node.TransferBatchRequest), "no_candidates");
            }
            if (Text(context.Node.NormalizedArguments, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture)) return EffectStepResult.Failed("transfer_permission_denied");
            var ids = Value(context.Node.NormalizedArguments, "itemIds").Items.Select(i => i.StringValue).ToList();
            string sourceZone = Text(Value(context.Node.NormalizedArguments, "expectedSource"), "zoneId");
            var source = CardMarkerCarrier.Source(context.State, context.Node, context.Registry.AbilityDeclarations, context.Registry);
            string intent = service.ValidateTokenWaitIntent(context, source, context.Node.NormalizedArguments);
            if (intent.Length > 0) return EffectStepResult.Failed(intent);
            if (CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.ExactItemUse, ItemIds = ids }) ||
                CharacterLifecycleService.TryAwaitEmptyCarrierExits(context, new[] { sourceZone }, ids)) return CharacterLifecycleService.DeferredDependencyWait();
            if (!service.TryPlanTokens(context.State, source, context.Node.NormalizedArguments, context.Registry.AbilityDeclarations,
                context.Node.EffectId + ":move", out var batch, out string reason)) return EffectStepResult.Failed(reason);
            var moved = service.RelocateBatch(context, batch);
            CharacterLifecycleService.ExitEmptyCarriers(context, moved.PositionChanges.Select(c => c.From.ZoneId));
            context.Node.Visibility = GameStateVisibilityPolicy.Owner;
            return TransferCompleted(moved, "no_candidates");
        }
        public static EffectSpec BatchSpec(ItemTransferBatchRequest request) => new EffectSpec(TypeId, ItemTransferService.BatchArguments(request))
            { PlayerId = request.ExecutingPlayerId, DefinitionVersion = TransferDefinitionVersion, Visibility = "owner" };
        private static string ValidateBatchArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(p => !new[] { "kind", "effectTypeId", "apiVersion", "player", "operationId", "transfers", "expectedOrders" }.Contains(p.Name)) ||
                Number(args, "apiVersion", 0) != 2 || !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1 ||
                Text(args, "operationId").Length == 0 || Value(args, "transfers")?.Kind != NormalizedValueKind.Array || Value(args, "transfers").Items.Count == 0 ||
                Value(args, "expectedOrders")?.Kind != NormalizedValueKind.Array) return "transfer_batch_arguments_invalid";
            foreach (var leg in Value(args, "transfers").Items)
            {
                if (leg.Kind != NormalizedValueKind.Object || leg.Properties.Any(p => !new[] { "itemRefs", "expectedSource", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy" }.Contains(p.Name)) ||
                    Value(leg, "itemRefs")?.Kind != NormalizedValueKind.Array || Value(leg, "itemRefs").Items.Count == 0 ||
                    Value(leg, "itemRefs").Items.Any(v => !Reference(v, "itemId")) ||
                    !Reference(Value(leg, "expectedSource"), "zoneId") || !Reference(Value(leg, "destination"), "zoneId") ||
                    Value(leg, "insertion")?.Kind != NormalizedValueKind.Object || Value(leg, "insertion").Properties.Count != 1 ||
                    !new[] { "top", "bottom", "unordered" }.Contains(Text(Value(leg, "insertion"), "kind")) ||
                    Value(leg, "requiredCount")?.Kind != NormalizedValueKind.Integer || Number(leg, "requiredCount", -1) != Value(leg, "itemRefs").Items.Count ||
                    Text(leg, "operationProfileId") != "core.facility_card_transfer" || Text(leg, "carrierPolicy", "preserve") != "preserve")
                    return "transfer_batch_leg_invalid";
            }
            foreach (var expected in Value(args, "expectedOrders").Items)
                if (expected.Kind != NormalizedValueKind.Object || expected.Properties.Count != 4 ||
                    expected.Properties.Any(p => !new[] { "zoneId", "digest", "orderRevision", "expectedTopItemId" }.Contains(p.Name)) ||
                    Text(expected, "zoneId").Length == 0 || Text(expected, "digest").Length == 0 ||
                    Value(expected, "orderRevision")?.Kind != NormalizedValueKind.Integer || Number(expected, "orderRevision", -1) < 0 ||
                    Value(expected, "expectedTopItemId")?.Kind != NormalizedValueKind.String) return "transfer_batch_order_invalid";
            return "";
        }
        public static ItemTransferBatchRequest BatchRequest(NormalizedValue args)
        {
            string invalid = ValidateBatchArguments(args);
            if (invalid.Length > 0) throw new InvalidOperationException(invalid);
            int player = int.Parse(Text(args, "player"), CultureInfo.InvariantCulture);
            return new ItemTransferBatchRequest { OperationId = Text(args, "operationId"), ExecutingPlayerId = player,
                Transfers = Value(args, "transfers").Items.Select(v => new ItemTransferRequest {
                    ExecutingPlayerId = player, ItemIds = Value(v, "itemRefs").Items.Select(i => Text(i, "itemId")).ToList(),
                    ExpectedSource = new ItemPositionRef { ZoneId = Text(Value(v, "expectedSource"), "zoneId") },
                    Destination = new ZoneRef { ZoneId = Text(Value(v, "destination"), "zoneId") },
                    InsertionKind = Text(Value(v, "insertion"), "kind"), OperationProfileId = Text(v, "operationProfileId"),
                    CarrierPolicyId = Text(v, "carrierPolicy", "preserve"), RequiredCount = Number(v, "requiredCount") }).ToList(),
                ExpectedOrders = Value(args, "expectedOrders").Items.Select(v => new ItemTransferOrderExpectation {
                    ZoneId = Text(v, "zoneId"), Digest = Text(v, "digest"), OrderRevision = Number(v, "orderRevision"),
                    ExpectedTopItemId = Text(v, "expectedTopItemId") }).ToList() };
        }
        private static EffectStepResult ExecuteBatch(EffectExecutionContext context)
        {
            var batch = BatchRequest(context.Node.NormalizedArguments);
            if (batch.ExecutingPlayerId != context.Node.PlayerId) throw new InvalidOperationException("transfer_permission_denied");
            bool city = batch.Transfers.Any(t => new[] { t.ExpectedSource.ZoneId, t.Destination.ZoneId }.Any(id =>
                context.State.ItemZones.Zones.Any(z => z.ZoneId == id &&
                    (z.TemplateId == FacilityLayerQuery.CellTemplate || z.TemplateId == FacilityLayerQuery.CoveredTemplate))));
            if (city)
            {
                var build = context.State.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == context.Node.ParentEffectId);
                var slot = FacilityBuildJournal.SelectedSlot(build);
                var facts = FacilityBuildJournal.Read(build);
                if (build?.EffectTypeId != FacilityEntryEffectTypeIds.Build || build.FlowStage != "place" || !Flag(build.ContinuationData, "paid") ||
                    batch.OperationId != build.EffectId + ":place" || slot == null || facts == null || batch.Transfers.Count != (slot.IsOverlay ? 2 : 1) ||
                    !batch.Transfers.Any(t => t.ItemIds.SequenceEqual(new[] { facts.ItemId }) && t.Destination.ZoneId == slot.CellZoneId &&
                        context.State.ItemZones.Zones.Any(z => z.ZoneId == t.ExpectedSource.ZoneId && z.TemplateId == "core.zone.effect_staging" && z.OwnerEffectId == build.EffectId)) ||
                    slot.IsOverlay && !batch.Transfers.Any(t => t.ItemIds.SequenceEqual(new[] { slot.ExpectedTopItemId }) && t.ExpectedSource.ZoneId == slot.CellZoneId &&
                        context.State.ItemZones.Zones.Any(z => z.ZoneId == t.Destination.ZoneId && z.TemplateId == FacilityLayerQuery.CoveredTemplate && z.ParentId == slot.CellZoneId)))
                    throw new InvalidOperationException("facility_build_transfer_authorization_invalid");
                // 前一个提交确认过的旧顶牌与层序，在真正移位之前再次复验。
                context.Registry.FacilityBuildQueries.RevalidateSlot(context.State, slot, build.EffectId);
            }
            foreach (var leg in batch.Transfers)
                if (CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.ExactItemUse, ItemIds = leg.ItemIds }) ||
                    CharacterLifecycleService.TryAwaitEmptyCarrierExits(context, new[] { leg.ExpectedSource.ZoneId }, leg.ItemIds))
                    return CharacterLifecycleService.DeferredDependencyWait();
            var moved = new ItemTransferService().RelocateBatch(context, batch);
            CharacterLifecycleService.ExitEmptyCarriers(context, moved.PositionChanges.Select(c => c.From.ZoneId));
            return TransferCompleted(moved, "no_candidates");
        }

        public const string RefillTypeId = "effect.facility.refill_slot";
        public const string RefreshTypeId = "effect.facility.refresh_supply";
        public static bool CanRestoreLegacy(GameState state, EffectNodeRuntimeState node)
        {
            if (node.EffectTypeId == TypeId && node.DefinitionVersion == "2.0.0" &&
                new[] { 1, 2 }.Contains(Number(node.NormalizedArguments, "apiVersion"))) return ValidateArguments(TypeId, node.NormalizedArguments).Length == 0;
            if (node.DefinitionVersion != "1.0.0" || Value(node.NormalizedArguments, "apiVersion") != null) return false;
            if (node.EffectTypeId == LuaDomainEffectTypeIds.MoveCharacterCard) return node.FlowStage.Length == 0 && Text(node.NormalizedArguments, "sourceZone") == "discard" && Text(node.NormalizedArguments, "destinationZone") == "hand";
            if (node.EffectTypeId == LuaDomainEffectTypeIds.MoveFacilityCard)
                return (node.FlowStage.Length == 0 || node.FlowStage == "awaiting") && Text(node.NormalizedArguments, "sourceZone") == "supply" && Text(node.NormalizedArguments, "destinationZone") == "deck_bottom" && (!ItemZoneLegacyProjection.IsActive(state) || state.ItemZones.Items.Where(item => state.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId)?.TemplateId == "core.zone.facility_supply_slot").GroupBy(item => item.DefinitionId).All(group => group.Count() == 1));
            return false;
        }
        public static string ValidateSupplyArguments(string type, NormalizedValue args)
        {
            if (Value(args, "apiVersion") == null) return ""; // Host 内部既有子式保持原参数。
            var fields = type == RefillTypeId ? new[] { "apiVersion", "player", "slotZoneId", "kind", "effectTypeId" } : new[] { "apiVersion", "player", "kind", "effectTypeId" };
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(p => !fields.Contains(p.Name)) || Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Number(args, "apiVersion") != 1 ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1 || type == RefillTypeId && Text(args, "slotZoneId").Length == 0) return "supply_arguments_invalid";
            return "";
        }
        private static string ValidateSupplyContext(EffectExecutionContext context)
        {
            string error = ValidateSupplyArguments(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (error.Length > 0) return error;
            return Value(context.Node.NormalizedArguments, "apiVersion") != null && Text(context.Node.NormalizedArguments, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture) ? "supply_permission_denied" : "";
        }
        public static void Register(EffectRegistry registry)
        {
            if (registry.TryGet(TypeId, out _)) return;
            registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.Atomic, TransferDefinitionVersion));
            registry.RegisterItemTransferEntryCostPreview((state, source, spec) => {
                if (Text(spec.NormalizedArguments, "player") != source.AuthorizedPlayerId.ToString(CultureInfo.InvariantCulture)) return "transfer_permission_denied";
                return new ItemTransferService().TryPlanTokens(state, source, spec.NormalizedArguments, registry.AbilityDeclarations,
                    "token-transfer-preview", out _, out string reason) ? "" : reason;
            });
            registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.MoveFacilityCard, ExecuteFacility, EffectExecutorKind.IntrinsicFlow));
            registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.MoveCharacterCard, ExecuteCharacter));
            registry.Register(new EffectRegistration(RefillTypeId, context =>
            {
                string error = ValidateSupplyContext(context); if (error.Length > 0) return EffectStepResult.Failed(error);
                var moved = FacilitySupplyService.RefillSlot(context, Text(context.Node.NormalizedArguments, "slotZoneId"));
                return TransferCompleted(moved, "empty_deck");
            }));
            registry.Register(new EffectRegistration(RefreshTypeId, RefreshSupply, EffectExecutorKind.IntrinsicFlow));
        }
        // Host 组合工厂；内容脚本由其归属任务装配，不识别具体企业或角色 ID。
        public static EffectSpec MoveSpec(int player, IEnumerable<string> ids, string source, string destination, string insertion, int required = 0, string profile = "core.facility_card_transfer") => EffectSpec.Create(TypeId,
            Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)), "itemRefs", NormalizedValue.CreateArray(ids.Select(id => Object("itemId", NormalizedValue.CreateString(id))).ToList()), "expectedSource", Object("zoneId", NormalizedValue.CreateString(source)), "destination", Object("zoneId", NormalizedValue.CreateString(destination)), "insertion", Object("kind", NormalizedValue.CreateString(insertion)), "operationProfileId", NormalizedValue.CreateString(profile), "requiredCount", NormalizedValue.CreateInteger(required)), player);
        public static EffectSpec RefillSpec(int player, string slot) => EffectSpec.Create(RefillTypeId, Object("slotZoneId", NormalizedValue.CreateString(slot)), player);
        public static EffectSpec RefreshSpec(int player) => EffectSpec.Create(RefreshTypeId, playerId: player);
        private static EffectStepResult RefreshSupply(EffectExecutionContext context)
        {
            string error = ValidateSupplyContext(context); if (error.Length > 0) return EffectStepResult.Failed(error);
            if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed)) throw new InvalidOperationException("supply_refresh_child_failed");
            var deck = FindZone(context.State, "facility_deck");
            if (context.Node.FlowStage.Length == 0)
            {
                var old = FacilityLayerQuery.Supply(context.State).SelectMany(slot => slot.ItemIds).Select(id => context.State.ItemZones.Items.Single(item => item.ItemId == id)).ToList();
                Set(context.Node, "selectedItemIds", Strings(old.Select(item => item.ItemId)));
                var step = EffectStepResult.Continue("returned");
                foreach (var item in old) step.AddChild(MoveSpec(context.Node.PlayerId, new[] { item.ItemId }, item.ZoneId, deck.ZoneId, "bottom", 1));
                return step;
            }
            if (context.Node.FlowStage == "returned") return EffectStepResult.Continue("shuffled").AddChild(EffectSpec.Create(DeckShuffleEffectExecutor.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(context.Node.PlayerId.ToString(CultureInfo.InvariantCulture)), "deckZone", Object("zoneId", NormalizedValue.CreateString(deck.ZoneId))), context.Node.PlayerId));
            if (context.Node.FlowStage == "shuffled")
            {
                var step = EffectStepResult.Continue("refilled");
                for (int index = 0; index < FacilitySupplyService.SupplySize; index++) step.AddChild(RefillSpec(context.Node.PlayerId, FindZone(context.State, "facility_supply_" + index).ZoneId));
                return step;
            }
            if (context.Node.FlowStage != "refilled") throw new InvalidOperationException("supply_refresh_stage_invalid");
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true)));
        }
        public static string ValidateArguments(string typeId, NormalizedValue args)
        {
            bool typed = typeId != TypeId;
            var version = Value(args, "apiVersion");
            if (!typed && version?.Kind == NormalizedValueKind.Integer && version.IntegerValue == 3) return ValidateTokenArguments(args);
            if (!typed && version?.Kind == NormalizedValueKind.Integer && version.IntegerValue == 2) return ValidateBatchArguments(args);
            if (typed && version == null)
            {
                if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(entry => !new[] { "kind", "effectTypeId", "player", "sourceZone", "destinationZone", "interactionType", "promptKey" }.Contains(entry.Name))) return "invalid_transfer_arguments";
                return Text(args, "sourceZone") == (typeId == LuaDomainEffectTypeIds.MoveFacilityCard ? "supply" : "discard") &&
                    Text(args, "destinationZone") == (typeId == LuaDomainEffectTypeIds.MoveFacilityCard ? "deck_bottom" : "hand") ? "" : "unsupported_card_zone_move";
            }
            var allowed = new HashSet<string>(new[] { "kind", "effectTypeId", "apiVersion", "player", "itemRefs", "expectedSource", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy" });
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(entry => !allowed.Contains(entry.Name))) return "invalid_transfer_arguments";
            if (version?.Kind != NormalizedValueKind.Integer || version.IntegerValue != (typed ? 2 : 1)) return "api_version_unsupported";
            if (!int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player <= 0) return "invalid_player";
            var refs = Value(args, "itemRefs");
            // 空 Lua table 规范化为 Object，只有完全空时才等同空数组。
            if (refs == null || refs.Kind != NormalizedValueKind.Array && !(refs.Kind == NormalizedValueKind.Object && refs.Properties.Count == 0)) return "item_refs_required";
            if (refs.Items.Any(item => !Reference(item, "itemId"))) return "invalid_item_ref";
            if (!Reference(Value(args, "expectedSource"), "zoneId", "slotId") || !Reference(Value(args, "destination"), "zoneId")) return "invalid_zone_ref";
            var insertion = Value(args, "insertion");
            if (insertion?.Kind != NormalizedValueKind.Object || insertion.Properties.Any(entry => entry.Name != "kind" && entry.Name != "slotId") || !new[] { "unordered", "top", "bottom", "slot" }.Contains(Text(insertion, "kind"))) return "insertion_rejected";
            if (Text(insertion, "kind") == "slot" ? Text(insertion, "slotId").Length == 0 : Value(insertion, "slotId") != null) return "insertion_rejected";
            if (Value(args, "requiredCount") != null && (Value(args, "requiredCount").Kind != NormalizedValueKind.Integer || Value(args, "requiredCount").IntegerValue < 0 || Value(args, "requiredCount").IntegerValue > int.MaxValue)) return "required_count_invalid";
            if (Text(args, "carrierPolicy", "preserve") != "preserve" || Text(args, "operationProfileId").Length == 0) return "operation_profile_rejected";
            return "";
        }
        public static EffectStepResult Execute(EffectExecutionContext context) => Move(context, "");
        private static EffectStepResult Move(EffectExecutionContext context, string requiredType)
        {
            YC.Domain.Cards.CharacterLifecycleService.ResumeDeferredDependencies(context);
            var missing = MissingTransferFields(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (missing.Count > 0) return SkipIncomplete(context, missing);
            string reason = ValidateArguments(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (reason.Length > 0) return EffectStepResult.Failed(reason);
            if (context.Node.EffectTypeId == TypeId && Number(context.Node.NormalizedArguments, "apiVersion", 0) == 3) return ExecuteTokens(context);
            if (context.Node.EffectTypeId == TypeId && Number(context.Node.NormalizedArguments, "apiVersion", 0) == 2) return ExecuteBatch(context);
            var request = Request(context.Node.NormalizedArguments);
            if (!UsageTracks.AllowsTrackTransfer(context, request)) return EffectStepResult.Failed("usage_track_host_managed");
            if (request.ExecutingPlayerId != context.Node.PlayerId) throw new InvalidOperationException("transfer_permission_denied");
            // 地图的拓扑、响应、等级变化必须经专门业务入口复验，不能从通用 Lua 绕过。
            if (!new[] { "core.facility_card_transfer", "core.character_card_transfer", "core.reward_card_transfer", "core.cube_storage", "core.player_marker_storage", "core.token_storage" }.Contains(request.OperationProfileId)) return EffectStepResult.Failed("operation_profile_rejected");
            var query = new ItemZoneQuery();
            var source = query.FindZone(context.State, request.ExpectedSource.ZoneId);
            var destination = query.FindZone(context.State, request.Destination.ZoneId);
            var protectedZones = new[] { "core.zone.influence_slots", "core.zone.score_track", "core.zone.board_marker_track", "core.zone.covered_cubes" };
            if (protectedZones.Contains(source?.TemplateId) || protectedZones.Contains(destination?.TemplateId) || source?.TemplateId == "core.zone.city_facility_cell" ||
                destination?.TemplateId == "core.zone.city_facility_cell" && !IsPaidBuildPlacement(context, request, source)) return EffectStepResult.Failed("operation_profile_rejected");
            if (requiredType.Length > 0 && request.ItemIds.Any(id => !new ItemTypeRegistryChecker().Matches(context.State, id, requiredType))) return EffectStepResult.Failed("destination_rejected");
            if (YC.Domain.Cards.CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery {
                DependencyKind = DeferredDependencyKind.ExactItemUse, ItemIds = request.ItemIds }) ||
                request.OperationProfileId == "core.character_card_transfer" && YC.Domain.Cards.CharacterLifecycleService.TryAwaitCardMoves(context, request.ItemIds, request.Destination.ZoneId) ||
                request.ExpectedSource.ZoneId != request.Destination.ZoneId && YC.Domain.Cards.CharacterLifecycleService.TryAwaitEmptyCarrierExits(context, new[] { request.ExpectedSource.ZoneId }, request.ItemIds))
                return YC.Domain.Cards.CharacterLifecycleService.DeferredDependencyWait();
            // 带内容的角色载体必须沿同一生命周期预览归还内容；整组失败不提交前半组。
            if (request.OperationProfileId == "core.character_card_transfer" && request.ItemIds.Any(id => context.State.ItemZones.Zones.Any(zone => zone.ParentKind == ZoneParentKind.Item && zone.ParentId == id && zone.Configuration?.CarrierExitPolicyId == "core.return_contents_before_exit" && context.State.ItemZones.Items.Any(item => item.ZoneId == zone.ZoneId))))
            {
                if (request.InsertionKind != "unordered" || request.DestinationSlotId.Length > 0) return EffectStepResult.Failed("insertion_rejected");
                if (request.ItemIds.Any(id => query.FindItem(context.State, id)?.ZoneId != request.ExpectedSource.ZoneId || request.ExpectedSource.SlotId.Length > 0 && query.FindItem(context.State, id)?.SlotId != request.ExpectedSource.SlotId)) return EffectStepResult.Failed("source_zone_changed");
                if (request.ItemIds.Count < request.RequiredCount) return EffectStepResult.Failed("insufficient_items");
                var preview = GameStateCloneService.DeepClone(context.State);
                var work = new EffectExecutionContext(preview, preview.EffectRuntime.EffectNodes.Single(node => node.EffectId == context.Node.EffectId), context.Registry);
                foreach (string id in request.ItemIds)
                {
                    string error = YC.Domain.Cards.CharacterLifecycleService.MoveCard(work, id, request.Destination.ZoneId);
                    if (error.Length > 0) return EffectStepResult.Failed(error);
                }
                GameStateCloneService.CopyTo(context.State, preview);
                return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(request.ItemIds.Count > 0), "movedCount", NormalizedValue.CreateInteger(request.ItemIds.Count), "movedItemIds", Strings(request.ItemIds)));
            }
            var service = new ItemTransferService();
            var validation = service.Validate(context.State, request);
            if (!validation.IsValid) return EffectStepResult.Failed(validation.ReasonCode);
            context.Node.Visibility = GameStateVisibilityPolicy.Owner;
            var moved = service.RelocateItems(context, request);
            YC.Domain.Cards.CharacterLifecycleService.ExitEmptyCarriers(context, moved.PositionChanges.Select(change => change.From.ZoneId));
            return TransferCompleted(moved, "no_candidates");
        }
        private static bool IsPaidBuildPlacement(EffectExecutionContext context, ItemTransferRequest request, ZoneInstanceState source)
        {
            if (source?.TemplateId != "core.zone.effect_staging" || request.ItemIds.Count != 1) return false;
            var owner = context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == source.OwnerEffectId);
            if (owner?.EffectTypeId != FacilityEntryEffectTypeIds.Build || owner.FlowStage != "place" || !Flag(owner.ContinuationData, "paid")) return false;
            var selected = Value(owner.ContinuationData, "selectedItemIds");
            if (selected?.Items.Count != 1 || selected.Items[0].StringValue != request.ItemIds[0]) return false;
            var ancestor = context.Node;
            while (ancestor != null && ancestor.EffectId != owner.EffectId) ancestor = context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == ancestor.ParentEffectId);
            return ancestor != null;
        }
        private static EffectStepResult TransferCompleted(ItemTransferResult moved, string emptyReason)
        {
            var result = Object("changed", NormalizedValue.CreateBoolean(moved.Changed), "movedCount", NormalizedValue.CreateInteger(moved.MovedCount), "movedItemIds", Strings(moved.MovedItemIds));
            if (!moved.Changed) { result.Properties.Add(new NormalizedValueEntry { Name = "reasonCode", Value = NormalizedValue.CreateString(emptyReason) }); result.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name)); }
            return EffectStepResult.Completed(result);
        }
        public static List<string> MissingTransferFields(string typeId, NormalizedValue args)
        {
            var version = Value(args, "apiVersion");
            if (version?.Kind != NormalizedValueKind.Integer || version.IntegerValue != (typeId == TypeId ? 1 : 2) || args.Properties.Any(entry => !new[] { "kind", "effectTypeId", "apiVersion", "player", "itemRefs", "expectedSource", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy" }.Contains(entry.Name))) return new List<string>();
            return new[] { "player", "itemRefs", "expectedSource", "destination", "insertion", "operationProfileId" }.Where(key => Value(args, key) == null).ToList();
        }
        internal static EffectStepResult SkipIncomplete(EffectExecutionContext context, IEnumerable<string> missing)
        {
            return EffectStepResult.Failed("incomplete_effect_skipped", Object("changed", NormalizedValue.CreateBoolean(false), "reasonCode", NormalizedValue.CreateString("incomplete_effect_skipped")))
                .AddLog(new EffectLogEntrySpec { PlayerId = context.Node.PlayerId, Message = "sourceId=" + context.Node.SourceId + ";effectId=" + context.Node.EffectId + ";missingFieldNames=" + string.Join(",", missing) + ";reasonCode=incomplete_effect_skipped；内容效果缺少必要字段，已跳过。" });
        }
        private sealed class ItemTypeRegistryChecker
        {
            private readonly ItemTypeRegistry types = ItemTypeRegistry.CreateBuiltIn();
            public bool Matches(GameState state, string id, string type) => types.IsAssignableTo(state.ItemZones.Items.Find(item => item.ItemId == id)?.TypeId, type);
        }
        private static EffectStepResult ExecuteCharacter(EffectExecutionContext context)
        {
            if (Value(context.Node.NormalizedArguments, "apiVersion") != null) return Move(context, "core.character_card");
            if (Text(context.Node.NormalizedArguments, "sourceZone") != "discard" || Text(context.Node.NormalizedArguments, "destinationZone") != "hand") return EffectStepResult.Failed("unsupported_character_card_zone_move");
            if (!ItemZoneLegacyProjection.IsActive(context.State)) return ContentPrimitiveEffectExecutor.MoveCards(context);
            var source = FindZone(context.State, "discard", context.Node.PlayerId); var target = FindZone(context.State, "hand", context.Node.PlayerId);
            var request = new ItemTransferRequest { ExecutingPlayerId = context.Node.PlayerId, ExpectedSource = new ItemPositionRef { ZoneId = source.ZoneId }, Destination = new ZoneRef { ZoneId = target.ZoneId }, InsertionKind = "unordered", OperationProfileId = "core.character_card_transfer", ItemIds = new ItemZoneQuery().GetItemIds(context.State, source.ZoneId).ToList() };
            var service = new ItemTransferService(); var validation = service.Validate(context.State, request);
            return validation.IsValid ? EffectStepResult.Completed(NormalizedValue.CreateInteger(service.RelocateItems(context, request).MovedCount)) : EffectStepResult.Failed(validation.ReasonCode);
        }
        private static EffectStepResult ExecuteFacility(EffectExecutionContext context)
        {
            if (Value(context.Node.NormalizedArguments, "apiVersion") != null) return Move(context, "core.facility_card");
            if (Text(context.Node.NormalizedArguments, "sourceZone") != "supply" || Text(context.Node.NormalizedArguments, "destinationZone") != "deck_bottom") return EffectStepResult.Failed("unsupported_facility_card_zone_move");
            if (!ItemZoneLegacyProjection.IsActive(context.State)) return ContentPrimitiveEffectExecutor.CycleFacility(context);
            if (context.Node.FlowStage != "" && context.Node.FlowStage != "awaiting" && context.Node.FlowStage != "refill") throw new InvalidOperationException("facility_transfer_stage_invalid");
            if (context.Node.FlowStage == "refill")
            {
                string original = Text(context.Node.ContinuationData, "originalSupplySlotZoneId");
                if (new ItemZoneQuery().GetItemIds(context.State, original).Count == 0) FacilitySupplyService.RefillSlot(context, original);
                Set(context.Node, "refilled", NormalizedValue.CreateBoolean(true));
                return EffectStepResult.Completed(context.Node.NormalizedResult);
            }
            var candidates = FacilityLayerQuery.Supply(context.State).Where(slot => slot.TopItemId.Length > 0).Select(slot => slot.TopItemId).ToList();
            if (candidates.Count == 0) return EffectStepResult.Completed(NormalizedValue.CreateString("no_target"));
            if (context.Node.FlowStage == "awaiting")
            {
                string selected = Answer(context);
                if (!candidates.Contains(selected))
                {
                    var legacy = candidates.Where(id => context.State.ItemZones.Items.Find(item => item.ItemId == id).DefinitionId == selected).ToList();
                    if (legacy.Count != 1) return EffectStepResult.Failed("source_zone_changed");
                    selected = legacy[0];
                }
                var item = new ItemZoneQuery().FindItem(context.State, selected);
                Set(context.Node, "selectedItemIds", Strings(new[] { selected }));
                Set(context.Node, "originalSupplySlotZoneId", NormalizedValue.CreateString(item.ZoneId));
                var request = new ItemTransferRequest { ItemIds = new List<string> { selected }, ExecutingPlayerId = context.Node.PlayerId, ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = FindZone(context.State, "facility_deck").ZoneId }, InsertionKind = "bottom", RequiredCount = 1, OperationProfileId = "core.facility_card_transfer" };
                var service = new ItemTransferService(); var validation = service.Validate(context.State, request);
                if (!validation.IsValid) return EffectStepResult.Failed(validation.ReasonCode);
                service.RelocateItems(context, request);
                context.Node.NormalizedResult = NormalizedValue.CreateString(item.DefinitionId);
                return EffectStepResult.Continue("refill");
            }
            var interaction = new EffectInteractionSpec { InteractionTypeId = Text(context.Node.NormalizedArguments, "interactionType", "lua.choice"), AnsweringPlayerId = context.Node.PlayerId, Visibility = "owner", PromptKey = Text(context.Node.NormalizedArguments, "promptKey", "effect.facility.choose_supply"), MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id" };
            interaction.CandidateIds.AddRange(candidates);
            return EffectStepResult.Continue("awaiting").AddInteraction(PrepareInteraction(context, interaction));
        }
        // 与节点一起提交候选解析记录；候选按稳定 ID 排序，不泄漏秘密牌堆顺序。
        internal static EffectInteractionSpec PrepareInteraction(EffectExecutionContext context, EffectInteractionSpec interaction)
        {
            string setId = StableIdFactory.Create("candidate", context.Node.EffectId,
                interaction.InteractionTypeId, context.State.EffectRuntime.InteractionRequests.Count(request => request.OwnerEffectId == context.Node.EffectId).ToString(CultureInfo.InvariantCulture));
            var draft = new CandidateSetDraft
            {
                CandidateSetId = setId, Version = 1, CandidateKind = interaction.InteractionTypeId,
                SourceEffectId = context.Node.EffectId, ExecutingPlayerId = context.Node.PlayerId,
                CandidatePoolIds = new List<string>(interaction.CandidateIds),
                DefaultCandidateIds = new List<string>(interaction.CandidateIds),
                CurrentIds = new List<string>(interaction.CandidateIds)
            };
            var resolution = new CandidateSetResolver().Resolve(context.State, draft, new CandidatePolicyRegistry(), context.State.EffectRuntime.StateRevision + 1);
            if (!resolution.Succeeded) throw new InvalidOperationException(resolution.FaultCode);
            context.State.EffectRuntime.CandidateResolutions.Add(resolution.Record);
            interaction.CandidateSetId = setId;
            interaction.CandidateSetVersion = draft.Version;
            interaction.CandidateResolutionId = setId;
            interaction.CandidateIds.Clear();
            interaction.CandidateIds.AddRange(resolution.FinalCandidateIds);
            // 与 CandidateIds 同索引的卡面定义只放在授权交互中，避免 UI 把实例 ID 当文案。
            var definitions = interaction.CandidateIds.Select(id => context.State.ItemZones.Items.Find(item => item.ItemId == id)?.DefinitionId ?? "").ToList();
            if (definitions.Any(id => id.Length > 0))
            {
                var parameters = interaction.PromptParameters.Clone();
                parameters.Properties.Add(new NormalizedValueEntry { Name = "candidateDefinitionIds", Value = Strings(definitions) });
                parameters.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                interaction.PromptParameters = parameters;
            }
            return interaction;
        }
        public static ItemTransferRequest Request(NormalizedValue args) => new ItemTransferRequest
        {
            ExecutingPlayerId = int.Parse(Text(args, "player"), CultureInfo.InvariantCulture), ItemIds = Value(args, "itemRefs").Items.Select(item => Text(item, "itemId")).ToList(),
            ExpectedSource = new ItemPositionRef { ZoneId = Text(Value(args, "expectedSource"), "zoneId"), SlotId = Text(Value(args, "expectedSource"), "slotId") },
            Destination = new ZoneRef { ZoneId = Text(Value(args, "destination"), "zoneId") }, InsertionKind = Text(Value(args, "insertion"), "kind"), DestinationSlotId = Text(Value(args, "insertion"), "slotId"),
            OperationProfileId = Text(args, "operationProfileId"), RequiredCount = Number(args, "requiredCount"), CarrierPolicyId = Text(args, "carrierPolicy", "preserve")
        };
        private static bool Reference(NormalizedValue value, string key, string optional = "") => value?.Kind == NormalizedValueKind.Object && Text(value, key).Length > 0 && value.Properties.All(entry => (entry.Name == key || entry.Name == optional) && entry.Value.Kind == NormalizedValueKind.String);
        public static ZoneInstanceState FindZone(GameState state, string local, int player = -1) => state.ItemZones.Zones.Single(zone => zone.LocalZoneId == local &&
            (local != "covered" || zone.TemplateId == "core.zone.character_covered") && (player < 0 || zone.OwnerPlayerId == player));
        public static NormalizedValue Value(NormalizedValue value, string name) => value?.Properties?.Find(entry => entry.Name == name)?.Value;
        public static string Text(NormalizedValue value, string name, string fallback = "") => Value(value, name)?.Kind == NormalizedValueKind.String ? Value(value, name).StringValue : fallback;
        public static int Number(NormalizedValue value, string name, int fallback = 0) => Value(value, name)?.Kind == NormalizedValueKind.Integer ? (int)Value(value, name).IntegerValue : fallback;
        public static bool Flag(NormalizedValue value, string name) => Value(value, name)?.Kind == NormalizedValueKind.Boolean && Value(value, name).BooleanValue;
        public static NormalizedValue Strings(IEnumerable<string> ids) => NormalizedValue.CreateArray(ids.Select(NormalizedValue.CreateString).ToList());
        public static NormalizedValue Object(params object[] values)
        {
            var entries = new List<NormalizedValueEntry>();
            for (int i = 0; i < values.Length; i += 2) entries.Add(new NormalizedValueEntry { Name = (string)values[i], Value = (NormalizedValue)values[i + 1] });
            return NormalizedValue.CreateObject(entries);
        }
        public static void Set(EffectNodeRuntimeState node, string key, NormalizedValue value)
        {
            var entry = node.ContinuationData.Properties.Find(field => field.Name == key);
            if (entry == null) node.ContinuationData.Properties.Add(new NormalizedValueEntry { Name = key, Value = value }); else entry.Value = value;
            node.ContinuationData.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        }
        public static string Answer(EffectExecutionContext context)
        {
            var value = context.GetLatestInteractionAnswer();
            if (value?.Kind == NormalizedValueKind.Array && value.Items.Count == 1) value = value.Items[0];
            return value?.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value?.StringValue ?? "";
        }
    }
}
