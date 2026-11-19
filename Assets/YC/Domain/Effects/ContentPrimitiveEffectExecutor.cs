using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Economy;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Presentation;
using System.Linq;
using YC.Domain.Items;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // 通用内容原语；不含角色 ID、卡面费用或奖励数值。
    public static class ContentPrimitiveEffectExecutor
    {
        public const string OverrideNextStartPlayer = LuaDomainEffectTypeIds.OverrideNextStartPlayer;
        public const string ActivateFacilityEntry = "effect.facility.activate_entry";
        public const string ChooseResources = "effect.resource.choose_amounts";
        public const string RetiredExplorationSelectionTypeId = "effect.exploration.choose";
        public const string RetiredExplorationSelectionReason = "choose_explore_api_retired";
        public const string RetiredBuildSelectionTypeId = "effect.facility.choose_build";
        public const string RetiredBuildSelectionReason = "choose_build_api_retired";
        public const string RetiredInfluenceSelectionTypeId = "effect.influence.select_action";
        public const string RetiredInfluenceSelectionReason = "influence_selection_api_retired";
        public const string RetiredCharacterTransferTypeId = "effect.character.transfer";
        public const string RetiredCharacterTransferReason = "character_transfer_api_retired";
        public const string RetiredCharacterZoneMoveTypeId = "effect.content.transaction.move_character";
        public const string RetiredCharacterZoneMoveReason = "character_zone_move_api_retired";
        public const string IncompleteEffect = "effect.content.incomplete";
        public const string RetiredMarkerLoadTypeId = "effect.card.load_markers", PayMarkers = "effect.card.pay_markers", StoreRemoved = "effect.card.store_removed";
        public const string RetiredMarkerLoadReason = "card_marker_load_api_retired";
        public const string RetiredMarkerStoreTypeId = "effect.content.transaction.store_marker", RetiredMarkerReturnTypeId = "effect.content.transaction.return_marker";
        public const string RetiredFacilityReleaseTypeId = "effect.content.transaction.release_binding", RetiredFacilityReleaseReason = "facility_release_binding_api_retired";
        public const string RetiredEnterpriseUpgradeTypeId = "effect.content.transaction.upgrade_enterprise", RetiredEnterpriseUpgradeReason = "enterprise_upgrade_for_player_api_retired";
        public const string RetiredMarkerStoreReason = "player_marker_store_api_retired", RetiredMarkerReturnReason = "player_marker_return_api_retired";
        public const string RetiredFixedDeliveryReason = "fixed_marker_delivery_contract_retired";
        // 仅保留旧ID用于诊断；不注册为当前可执行Effect。
        public const string RetiredMarkerPaymentReason = "card_marker_payment_api_retired";
        public const string ClearRouteRoad = "effect.map.clear_route_road";
        public const string QuantityInteractionTypeId = "effect.quantity.choose";
        public const string QuantityAnswerSchema = "bounded_amount";
        public const string ResourceSaleSemantic = "resource_sale_batch";
        public static readonly string[] ContentFlowTypeIds = { StoreRemoved, ClearRouteRoad, RewardAcquisitionFlow.TypeId };
        public static string[] ContentFlowFields(string type)
        {
            var fields = new List<string> { "apiVersion", "player" };
            if (type == RewardAcquisitionFlow.TypeId) fields.Add("poolRole");
            if (type == StoreRemoved) fields.AddRange(new[] { "cardInstanceId", "localZoneId" });
            if (type == StoreRemoved) fields.Add("influenceItemId");
            if (type == ClearRouteRoad) fields.AddRange(new[] { "scoreRemovedPlayers", "scoreExecutor" });
            return fields.ToArray();
        }
        public static string RetiredMarkerContractReason(string type, NormalizedValue args)
        {
            if(type==PlayerMarkerOperation.TypeId&&args!=null&&PlayerMarkerOperation.IsLegacy(args))return PlayerMarkerOperation.CleanupHostOnlyReason;
            if(type==LuaDomainEffectTypeIds.MoveFacilityCard&&args!=null&&Value(args,"apiVersion")==null)return RetiredFacilityMoveSignature;
            if (type == CharacterCoverEffectExecutor.EffectTypeId && Value(args, "apiVersion") is NormalizedValue coverVersion &&
                coverVersion.Kind == NormalizedValueKind.Integer && coverVersion.IntegerValue == 2) return CharacterCoverEffectExecutor.RetiredV2Reason;
            if (type == LuaDomainEffectTypeIds.MoveCharacterCard && args != null && Value(args, "apiVersion") == null)
                return CharacterCardMovement.UnversionedSignatureRetired;
            if (type == LuaDomainEffectTypeIds.ShuffleFacilityDeck && args != null && Value(args, "apiVersion") == null)
                return DeckShuffleEffectExecutor.UnversionedSignatureRetired;
            if (type == RetiredExplorationSelectionTypeId) return RetiredExplorationSelectionReason;
            if (type == RetiredCharacterTransferTypeId) return RetiredCharacterTransferReason;
            if (type == RetiredCharacterZoneMoveTypeId) return RetiredCharacterZoneMoveReason;
            if (type == RetiredBuildSelectionTypeId) return RetiredBuildSelectionReason;
            if (type == RetiredInfluenceSelectionTypeId) return RetiredInfluenceSelectionReason;
            if (type == LuaDomainEffectTypeIds.Repeat) return "repeat_api_retired";
            if (type == RetiredFacilityReleaseTypeId) return RetiredFacilityReleaseReason;
            if (type == "effect.character.content.retain") return "character_retain_api_retired";
            if (type == LuaDomainEffectTypeIds.ExecuteMainAction &&
                new[] { "grant_budget", "facility_entry" }.Contains(ItemTransferEffectExecutor.Text(args, "executionMode")))
                return "main_action_execution_mode_retired";
            if (type == RetiredMarkerLoadTypeId) return RetiredMarkerLoadReason;
            if (type == RetiredMarkerStoreTypeId) return RetiredMarkerStoreReason;
            if (type == RetiredMarkerReturnTypeId) return RetiredMarkerReturnReason;
            if (type == RetiredEnterpriseUpgradeTypeId) return RetiredEnterpriseUpgradeReason;
            if (type == RetiredDepartureBatchContract.TypeId) return RetiredDepartureBatchContract.Reason;
            return new[] { "fixedDeliveryRequirement", "deliveryRef" }.Any(k => Value(args, k) != null) ? RetiredFixedDeliveryReason : "";
        }
        // API-16 / 3.2、4.6.4：已退役父包装的现有选路或事件待答不能绕过父节点继续。
        public static string RetiredExecutionOwnerReason(GameState state, EffectNodeRuntimeState owner)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (owner != null && visited.Add(owner.EffectId))
            {
                string reason = RetiredMarkerContractReason(owner.EffectTypeId, owner.NormalizedArguments);
                if (reason.Length > 0) return reason;
                owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == owner.ParentEffectId);
            }
            return "";
        }
        public static string ValidateContentFlow(string type, NormalizedValue args)
        {
            if (type == PayMarkers) return RetiredMarkerPaymentReason;
            string retired = RetiredMarkerContractReason(type, args); if (retired.Length > 0) return retired;
            if (type == StoreRemoved && Value(args, "apiVersion")?.Kind == NormalizedValueKind.Integer && Number(args, "apiVersion") == 2)
            { var copy = args.Clone(); copy.Properties.Single(p => p.Name == "apiVersion").Value = NormalizedValue.CreateInteger(1); return ValidateContentFlow(type, copy); }
            if (!ContentFlowTypeIds.Contains(type) || args?.Kind != NormalizedValueKind.Object ||
                args.Properties.Any(entry => !ContentFlowFields(type).Concat(new[] { "kind", "effectTypeId" }).Contains(entry.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Number(args, "apiVersion") != 1 ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1) return "content_flow_arguments_invalid";
            foreach (string key in ContentFlowFields(type).Except(new[] { "apiVersion", "player" }))
            {
                var value = Value(args, key);
                if (value == null) return "content_flow_arguments_incomplete";
                if (new[] { "scoreRemovedPlayers", "scoreExecutor" }.Contains(key))
                { if (value.Kind != NormalizedValueKind.Integer || value.IntegerValue < 0 || value.IntegerValue > 99) return "content_flow_count_invalid"; }
                else if (value.Kind != NormalizedValueKind.String || value.StringValue.Length == 0) return "content_flow_reference_invalid";
            }
            if (type == RewardAcquisitionFlow.TypeId && !new[] { "awards", "contracts" }.Contains(Text(args, "poolRole"))) return "reward_pool_rejected";
            return "";
        }
        public static string ValidateSaleArguments(NormalizedValue args)
        {
            if (Number(args, "apiVersion", 0) == 2)
                return args.Kind == NormalizedValueKind.Object &&
                    args.Properties.All(p => new[] { "apiVersion", "player", "saleMode", "pricePolicy", "interactionType", "promptKey", "kind", "effectTypeId" }.Contains(p.Name)) &&
                    Text(args, "saleMode") == "batch" && Text(args, "pricePolicy") == "core.resource_sale" &&
                    int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int actor) && actor > 0
                    ? "" : "sale_arguments_invalid";
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(p => !new[] { "apiVersion", "player", "targetPlayer", "resourceType", "saleMode", "unitPrice", "kind", "effectTypeId" }.Contains(p.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Number(args, "apiVersion", 0) != 1 || Text(args, "saleMode") != "all" || !TryBasicResource(Text(args, "resourceType"), out _) ||
                Value(args, "unitPrice")?.Kind != NormalizedValueKind.Integer || Number(args, "unitPrice", 0) < 1 || Number(args, "unitPrice", 0) > 99 ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1 ||
                !int.TryParse(Text(args, "targetPlayer"), NumberStyles.None, CultureInfo.InvariantCulture, out int target) || target < 1) return "sale_arguments_invalid";
            return "";
        }
        public static bool TryValidateContentContinuations(GameState state, out string reason)
        {
            var pending = new Stack<EffectSpecRuntimeState>();
            foreach (var node in state.EffectRuntime.EffectNodes)
            {
                if (!AllocationEffects.ValidateNode(state,node,out reason)) return false;
                reason = RetiredMarkerContractReason(node.EffectTypeId, node.NormalizedArguments); if (reason.Length > 0) return false;
                reason = ItemMovementV4.ValidateSavedVersion(node.EffectTypeId, node.NormalizedArguments, node.DefinitionVersion, true); if (reason.Length > 0) return false;
                reason=FacilityBuildContract.ValidateSavedVersion(node.EffectTypeId,node.DefinitionVersion,true);
                if(reason.Length>0 && !FacilityBuildContract.IsCompletedLegacy(node))return false;
                reason=CharacterDoubleUseConsumption.ValidateSavedVersion(node.EffectTypeId,node.DefinitionVersion,true);if(reason.Length>0)return false;
                if(!CharacterDoubleUseConsumption.TryValidate(state,node)){reason="character_double_use_source_state_invalid";return false;}
                reason = CharacterCoverEffectExecutor.ValidateSavedVersion(node.EffectTypeId, node.NormalizedArguments, node.DefinitionVersion, true); if (reason.Length > 0) return false;
                if (!CharacterCoverEffectExecutor.TryValidateSharedWrapper(state,node)) { reason="character_cover_wrapper_state_invalid";return false; }
                reason = ContentLifecycleEffectExecutor.ValidateSavedReturnMarkerVersion(node.EffectTypeId, node.DefinitionVersion, true); if (reason.Length > 0) return false;
                reason = CharacterCardMovement.ValidateSavedVersion(node.EffectTypeId, node.NormalizedArguments, node.DefinitionVersion, true); if (reason.Length > 0) return false;
                reason=PlayerMarkerOperation.ValidateSavedVersion(node.EffectTypeId,node.NormalizedArguments,node.DefinitionVersion,true);if(reason.Length>0)return false;
                reason = YC.Domain.Movement.MapOperationEffectExecutor.ValidateSavedVersion(node.EffectTypeId,node.DefinitionVersion,true);if(reason.Length>0)return false;
                if(!SpecializedItemMovement.TryValidate(state,node)){reason="specialized_item_movement_state_invalid";return false;}
                reason = ValidateSavedSupplyVersion(node.EffectTypeId,node.NormalizedArguments,node.DefinitionVersion,true);if(reason.Length>0)return false;
                if(!TryValidateSupplyFlow(state,node)){reason="facility_supply_flow_state_invalid";return false;}
                if(node.EffectTypeId==CityStyleMarkerReturnChoice.TypeId&&!CityStyleMarkerReturnChoice.TryValidate(state,node)){reason="city_style_return_choice_state_invalid";return false;}
                if(node.EffectTypeId==LuaDomainEffectTypeIds.Choice){reason=GenericLuaEffectExecutor.ValidateSavedChoiceVersion(node.DefinitionVersion,true);if(reason.Length>0)return false;}
                if (node.EffectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction)
                { reason = GenericLuaEffectExecutor.ValidateSavedMainActionVersion(node.DefinitionVersion, true); if (reason.Length > 0) return false; }
                if (!FacilityBuildContract.TryValidateNode(state, node, out reason)) return false;
                if (!ExplorationContract.TryValidateNode(state, node, out reason)) return false;
                if (node.EffectTypeId == LuaDomainEffectTypeIds.ResourceSell && Number(node.NormalizedArguments, "apiVersion") == 2 &&
                    state.EffectRuntime.InteractionRequests.Any(q => q.OwnerEffectId == node.EffectId && q.Status == "open" &&
                        Text(q.PromptParameters, "interactionSemantic") != ResourceSaleSemantic))
                { reason = "resource_sale_interaction_semantics_missing"; return false; }
                foreach (var spec in node.NestedEffects) pending.Push(spec);
                foreach (var spec in node.SettlementWindow?.Entries.SelectMany(entry => entry.Effects) ?? Enumerable.Empty<EffectSpecRuntimeState>()) pending.Push(spec);
            }
            foreach (var spec in state.EffectRuntime.DeferredResponses.SelectMany(response => response.PendingBody)) pending.Push(spec);
            foreach (var offer in state.EffectRuntime.InfluenceDepartureBatches.SelectMany(batch => batch.Offers))
                if (offer.Body != null) pending.Push(offer.Body);
            foreach (var mapping in state.EffectRuntime.InfluencePlaceMappings)
                if (mapping.OriginalSpec != null) pending.Push(mapping.OriginalSpec);
            while (pending.Count > 0)
            {
                var spec = pending.Pop(); reason=AllocationEffects.ValidateSavedSpec(spec);if(reason.Length>0)return false; reason = RetiredMarkerContractReason(spec.EffectTypeId, spec.NormalizedArguments);
                // 当前真实存放分支的待执行规格也要完整校验，不能等选中后才发现离场实例缺失。
                if (reason.Length == 0 && spec.EffectTypeId == StoreRemoved && Number(spec.NormalizedArguments, "apiVersion") == 2)
                    reason = ValidateContentFlow(spec.EffectTypeId, spec.NormalizedArguments);
                if (reason.Length == 0) reason = ItemMovementV4.ValidateSavedVersion(spec.EffectTypeId, spec.NormalizedArguments, spec.DefinitionVersion);
                if (reason.Length == 0 && !ItemMoveInputNormalization.TryValidate(spec.EffectTypeId,spec.NormalizedArguments,spec.ItemMoveInput,out reason)) return false;
                if(reason.Length==0)reason=FacilityBuildContract.ValidateSavedVersion(spec.EffectTypeId,spec.DefinitionVersion);
                if(reason.Length==0)reason=CharacterDoubleUseConsumption.ValidateSavedVersion(spec.EffectTypeId,spec.DefinitionVersion);
                if (reason.Length == 0) reason = CharacterCoverEffectExecutor.ValidateSavedVersion(spec.EffectTypeId, spec.NormalizedArguments, spec.DefinitionVersion);
                if (reason.Length == 0) reason = ContentLifecycleEffectExecutor.ValidateSavedReturnMarkerVersion(spec.EffectTypeId, spec.DefinitionVersion);
                if (reason.Length == 0) reason = CharacterCardMovement.ValidateSavedVersion(spec.EffectTypeId, spec.NormalizedArguments, spec.DefinitionVersion);
                if(reason.Length==0)reason=PlayerMarkerOperation.ValidateSavedVersion(spec.EffectTypeId,spec.NormalizedArguments,spec.DefinitionVersion);
                if(reason.Length==0)reason=YC.Domain.Movement.MapOperationEffectExecutor.ValidateSavedVersion(spec.EffectTypeId,spec.DefinitionVersion);
                if (reason.Length == 0) reason = ValidateSavedSupplyVersion(spec.EffectTypeId,spec.NormalizedArguments,spec.DefinitionVersion);
                if(reason.Length==0&&spec.EffectTypeId==CityStyleMarkerReturnChoice.TypeId)reason=CityStyleMarkerReturnChoice.ValidateSavedVersion(spec.DefinitionVersion);
                if(reason.Length==0&&spec.EffectTypeId==LuaDomainEffectTypeIds.Choice)reason=GenericLuaEffectExecutor.ValidateSavedChoiceVersion(spec.DefinitionVersion);
                if (reason.Length == 0 && spec.EffectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction)
                    reason = GenericLuaEffectExecutor.ValidateSavedMainActionVersion(spec.DefinitionVersion);
                if (reason.Length == 0 && spec.EffectTypeId == FacilityEntryEffectTypeIds.Build && FacilityBuildContract.IsV3(spec.NormalizedArguments))
                    reason = FacilityBuildContract.ValidateSavedSpec(spec);
                if (reason.Length == 0 && spec.EffectTypeId == ExplorationEffectTypeIds.Explore && ExplorationContract.IsV3(spec.NormalizedArguments))
                    reason = ExplorationContract.ValidateSavedSpec(spec);
                if (reason.Length > 0) return false;
                if (InfluenceSelectionFlow.IsSelection(spec.EffectTypeId, spec.NormalizedArguments))
                { reason = InfluenceSelectionFlow.ValidateSavedSpec(spec); if (reason.Length > 0) return false; }
                if (spec.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence && Value(spec.NormalizedArguments,"targets") != null)
                { reason = InfluenceEffectExecutor.ValidateRemoveSpec(EffectSpec.Create(spec.EffectTypeId,spec.NormalizedArguments,spec.PlayerId)); if (reason.Length > 0) return false; }
                foreach (var child in spec.NestedEffects) pending.Push(child);
            }
            // 不把旧选择/提交节点解释成新转移；原存档由原版本恢复，当前版本给出明确诊断。
            if (state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == PayMarkers)) { reason = RetiredMarkerPaymentReason; return false; }
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => n.ItemMovementV4 != null || n.CommonItemMovementV4 != null || ItemMovementV4.IsV4(n.EffectTypeId, n.NormalizedArguments)))
                if (!ItemMovementV4.TryValidateState(state, node, out reason)) return false;
            foreach (var node in state.EffectRuntime.EffectNodes)
                if (!InfluenceDepartureCompletion.TryValidate(state, node, out reason)) return false;
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => n.TransferBatchRequest?.ProtocolVersion == 3 ||
                n.EffectTypeId == ItemTransferEffectExecutor.TypeId && Number(n.NormalizedArguments, "apiVersion") == 3))
            {
                bool receipt = Value(node.ContinuationData, "transferBatchReceipt") != null;
                if (receipt)
                { if (!ItemTransferService.TryValidateTokenState(state, node, out reason)) return false; }
                else if (node.TransferBatchRequest != null || node.PositionChanges.Count > 0 || node.Status == EffectNodeStatus.Completed)
                { reason = "token_transfer_receipt_missing"; return false; }
            }
            reason = "content_flow_state_invalid";
            var stages = new Dictionary<string, string[]> {
                { RewardAcquisitionFlow.TypeId, new[] { "", "choose_reward", "finished" } },
                { StoreRemoved, new[] { "" } }, { ClearRouteRoad, new[] { "", "choose_route", "removing", "road", "scoring", "finished" } } };
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => ContentFlowTypeIds.Contains(n.EffectTypeId)))
            {
                if (ValidateContentFlow(node.EffectTypeId, node.NormalizedArguments).Length > 0 || Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture) || state.FindPlayer(node.PlayerId) == null || !stages[node.EffectTypeId].Contains(CharacterLifecycleService.GetResumableFlowStage(node))) return false;
                if (node.EffectTypeId == RewardAcquisitionFlow.TypeId)
                {
                    bool finished = node.FlowStage == "finished";
                    string rewardId = Text(node.ContinuationData, "selectedRewardItemId"), role = Text(node.NormalizedArguments, "poolRole");
                    if (finished != Flag(node.ContinuationData, "acquisitionCompleted") || finished != (rewardId.Length > 0)) return false;
                    if (finished && (!state.ItemZones.Items.Any(i => i.ItemId == rewardId && i.TypeId == (role == "awards" ? "core.award_card" : ItemTypeRegistry.ContractTokenTypeId) &&
                            Text(node.NormalizedResult, "definitionId") == i.DefinitionId) ||
                        node.PositionChanges.Count != 1 || !node.PositionChanges.Any(change => change.ItemId == rewardId &&
                            state.ItemZones.Zones.Any(z => z.ZoneId == change.From.ZoneId && z.TemplateId == "core.zone.reward_pool" && z.LocalZoneId == role) &&
                            state.ItemZones.Zones.Any(z => z.ZoneId == change.To.ZoneId && z.TemplateId == "core.zone.player_rewards" && z.OwnerPlayerId == node.PlayerId)) ||
                        !Flag(node.NormalizedResult, "changed") || Text(node.NormalizedResult, "rewardItemId") != rewardId ||
                        Text(node.NormalizedResult, "poolRole") != role || Number(node.NormalizedResult, "playerId", -1) != node.PlayerId)) return false;
                    if (!finished && node.PositionChanges.Count > 0 || node.FlowStage == "choose_reward" && role != "awards") return false;
                }
                var selected = Value(node.ContinuationData, "selectedItemIds");
                if (selected != null && (selected.Kind != NormalizedValueKind.Array || selected.Items.Any(v => v.Kind != NormalizedValueKind.String || !state.ItemZones.Items.Any(i => i.ItemId == v.StringValue)) || selected.Items.Select(v => v.StringValue).Distinct().Count() != selected.Items.Count)) return false;
                if (node.EffectTypeId == ClearRouteRoad && !new[] { "", "choose_route" }.Contains(node.FlowStage))
                {
                    string route = Text(node.ContinuationData, "selectedRouteId");
                    if (!StaticMapDefinitions.Resolve(state.MapId).Routes.Any(r => r.RouteId == route) || selected == null || Number(node.ContinuationData, "nextIndex", -1) < 0 || Number(node.ContinuationData, "nextIndex", -1) > selected.Items.Count) return false;
                    var subjects = Value(node.ContinuationData, "processedSubjectKeys");
                    if (subjects?.Kind != NormalizedValueKind.Array || subjects.Items.Any(v => v.Kind != NormalizedValueKind.String || !(v.StringValue.StartsWith("player:") && int.TryParse(v.StringValue.Substring(7), out int player) && state.FindPlayer(player) != null || v.StringValue.StartsWith("company:")))) return false;
                }
            }
            reason = ""; return true;
        }
        private static bool TryBasicResource(string id, out ResourceType type)
        {
            type = id == "originium" ? ResourceType.Originium : id == "originium-shard" ? ResourceType.OriginiumShard : ResourceType.Iron;
            return new[] { "originium", "originium-shard", "iron" }.Contains(id);
        }
        public static void Register(EffectRegistry registry, IMapQueryService mapQuery = null, InfluenceService influenceService = null)
        {
            RouteResourceSelectionEffectExecutor.Register(registry);
            ContentLifecycleEffectExecutor.Register(registry);
            FacilitySelectionEffectExecutor.Register(registry);
            if (!registry.TryGet(IncompleteEffect, out _)) registry.Register(new EffectRegistration(IncompleteEffect, context =>
                ItemTransferEffectExecutor.SkipIncomplete(context, ItemTransferEffectExecutor.Value(context.Node.NormalizedArguments, "missingFieldNames").Items.Select(value => value.StringValue)), EffectExecutorKind.Atomic));
            if (!registry.TryGet(OverrideNextStartPlayer, out _)) registry.Register(new EffectRegistration(OverrideNextStartPlayer, SetNextStart, EffectExecutorKind.Atomic, "1.0.0"));
            Register(registry, ActivateFacilityEntry, ActivateEntry);
            Register(registry, ChooseResources, ChooseResourceAmounts);
            Register(registry, LuaDomainEffectTypeIds.ResourceSell, Sell);
            foreach (string type in ContentFlowTypeIds)
                if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, context => ExecuteContentFlow(context, mapQuery, influenceService),
                    type == StoreRemoved ? EffectExecutorKind.Atomic : EffectExecutorKind.IntrinsicFlow,
                    "v1") { ExecutionDoesNotRetainState = type == ClearRouteRoad && (influenceService == null || !influenceService.HasExternalStateCallbacks) });
        }
        private static void Register(EffectRegistry registry, string type, Func<EffectExecutionContext, EffectStepResult> execute)
        {
            if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, execute, EffectExecutorKind.IntrinsicFlow, "1.0.0"));
        }
        private static EffectStepResult ExecuteContentFlow(EffectExecutionContext context, IMapQueryService mapQuery, InfluenceService influenceService)
        {
            CharacterLifecycleService.ResumeDeferredDependencies(context);
            string error = ValidateContentFlow(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            if (Text(context.Node.NormalizedArguments, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture) || context.State.FindPlayer(context.Node.PlayerId) == null)
                return EffectStepResult.Failed("content_flow_permission_denied");
            if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)) return EffectStepResult.Failed("content_flow_child_failed");
            switch (context.Node.EffectTypeId)
            {
                case RewardAcquisitionFlow.TypeId: return RewardAcquisitionFlow.Execute(context);
                case StoreRemoved: return StoreRemovedCube(context);
                default: return ClearAndRoad(context, mapQuery, influenceService);
            }
        }
        private static EffectStepResult ContentWait(EffectExecutionContext context, string stage, IEnumerable<string> candidates, int minimum = 1, int maximum = 1, bool allowDecline = false)
        {
            bool routeChoice = context.Node.EffectTypeId == ClearRouteRoad && stage == "choose_route";
            var request = new EffectInteractionSpec { InteractionTypeId = routeChoice ? "effect.route.choose" : "lua.choice",
                AnsweringPlayerId = context.Node.PlayerId, Visibility = "owner", PromptKey = routeChoice ? "effect.route.choose" : context.Node.EffectTypeId,
                MinSelections = minimum, MaxSelections = maximum, AllowDecline = allowDecline, AnswerSchema = maximum > 1 ? "candidate_id_array" : "candidate_id" };
            if (routeChoice)
                request.Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:interaction.map.route") };
            request.CandidateIds.AddRange(candidates.OrderBy(id => id, StringComparer.Ordinal));
            return EffectStepResult.Continue(stage).AddInteraction(PrepareInteraction(context, request));
        }
        public static bool TryReadBoundedAmount(NormalizedValue parameters, NormalizedValue answer, out int amount)
        {
            amount = 0;
            return Value(parameters, "minAmount")?.Kind == NormalizedValueKind.Integer &&
                Value(parameters, "maxAmount")?.Kind == NormalizedValueKind.Integer &&
                Value(parameters, "minAmount").IntegerValue >= 0 &&
                Value(parameters, "maxAmount").IntegerValue <= int.MaxValue &&
                Value(parameters, "maxAmount").IntegerValue >= Value(parameters, "minAmount").IntegerValue &&
                answer?.Kind == NormalizedValueKind.String &&
                int.TryParse(answer.StringValue, NumberStyles.None, CultureInfo.InvariantCulture, out amount) &&
                amount >= Value(parameters, "minAmount").IntegerValue && amount <= Value(parameters, "maxAmount").IntegerValue;
        }
        private static bool Declined(EffectExecutionContext context) =>
            context.GetLatestInteractionAnswer()?.Kind == NormalizedValueKind.Boolean && !context.GetLatestInteractionAnswer().BooleanValue;
        private static ZoneInstanceState RequireCardZone(EffectExecutionContext context)
        {
            var args = context.Node.NormalizedArguments;
            const string operation = "store";
            return CardMarkerCarrier.Resolve(context.State, context.Node, Text(args, "cardInstanceId"), Text(args, "localZoneId"), operation, out _, out _, context.Registry.AbilityDeclarations, Number(args, "apiVersion") == 1, context.Registry);
        }
        private static EffectStepResult StoreRemovedCube(EffectExecutionContext context)
        {
            var target = RequireCardZone(context); var item = context.State.ItemZones.Items.Find(value => value.ItemId == Text(context.Node.NormalizedArguments, "influenceItemId"));
            bool v2 = Number(context.Node.NormalizedArguments, "apiVersion") == 2;
            if (target == null || !v2 && target.Configuration?.EntryPolicyId != "core.removed_owned_influence" || item?.PhysicalKind != ItemPhysicalKind.Cube ||
                item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId != context.Node.PlayerId) return EffectStepResult.Failed("card_zone_permission_denied");
            if (v2)
            {
                CardMarkerCarrier.Resolve(context.State, context.Node, target.ParentId, target.LocalZoneId, "store", out var types, out _, context.Registry.AbilityDeclarations, registry: context.Registry);
                if (!CardMarkerCarrier.Accepts(context.State, target, item, context.Node.PlayerId, types)) return EffectStepResult.Failed("carrier_item_type_rejected");
            }
            var currentDeparture = InfluenceDepartureProtocol.Current(context.State, item.ItemId, context.Node.EffectId);
            if (currentDeparture != null)
            {
                var claim = InfluenceDepartureProtocol.Response(context.State, context.Node.EffectId);
                if (claim?.Kind != "destination" || claim.SourceCardInstanceId != target.ParentId || claim.DepartureId != currentDeparture.DepartureId) return EffectStepResult.Failed("departure_claim_permission_denied");
                var movedV2 = InfluenceDepartureProtocol.Resolve(context, currentDeparture, target.ZoneId);
                return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(movedV2.Changed), "movedCount", NormalizedValue.CreateInteger(movedV2.MovedCount), "movedItemIds", Strings(movedV2.MovedItemIds)));
            }
            var source = context.State.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId);
            var owner = context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == source?.OwnerEffectId);
            if (CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery {
                DependencyKind = DeferredDependencyKind.ExactItemUse, ItemIds = new List<string> { item.ItemId } })) return CharacterLifecycleService.DeferredDependencyWait();
            if (EffectTreeExecutor.TryFindDeferredResponse(context.State, context.Node.EffectId, out var response))
            {
                if (source?.TemplateId != "core.zone.effect_staging" || response.ReservedItemId != item.ItemId || response.SourceCardInstanceId != target.ParentId ||
                    response.AnsweringPlayerId != context.Node.PlayerId || response.ReservationZoneId != source.ZoneId || source.OwnerEffectId != response.ResponseRootEffectId)
                    return EffectStepResult.Failed("deferred_destination_permission_denied");
            }
            else if (source?.TemplateId != "core.zone.effect_staging" || Text(owner?.ContinuationData, "pendingItemId") != item.ItemId || Flag(owner.ContinuationData, "destinationResolved") ||
                Text(owner.ContinuationData, "destinationHandoffResponseId").Length > 0 || !new[] { "remove", "replace" }.Contains(Text(owner.ContinuationData, "pendingDepartureKind"))) return EffectStepResult.Failed("pending_destination_required");
            var moved = new InfluenceDestinationResolver().Resolve(context, item.ItemId, target.ZoneId);
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(moved.Changed), "movedCount", NormalizedValue.CreateInteger(moved.MovedCount), "movedItemIds", Strings(moved.MovedItemIds)));
        }
        private static EffectStepResult ClearAndRoad(EffectExecutionContext context, IMapQueryService query, InfluenceService influence)
        {
            var map = query ?? new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId)); var service = influence ?? new InfluenceService(map);
            var node = context.Node; string routeId = Text(node.ContinuationData, "selectedRouteId");
            if (node.FlowStage.Length == 0 || node.FlowStage == "choose_route")
            {
                var routes = service.GetRoadCandidateRouteIds(context.State);
                if (routes.Count == 0) return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(false), "reasonCode", NormalizedValue.CreateString("no_candidates")));
                routeId = node.FlowStage == "choose_route" ? Answer(context) : "";
                if (!routes.Contains(routeId)) return ContentWait(context, "choose_route", routes);
                var order = context.State.EffectRuntime.PlayerOrderSnapshot.Count > 0 ? context.State.EffectRuntime.PlayerOrderSnapshot.ToList() : context.State.Players.Select(player => player.PlayerId).ToList();
                if (!order.Contains(node.PlayerId) || order.Distinct().Count() != context.State.Players.Count || order.Any(id => context.State.FindPlayer(id) == null)) return EffectStepResult.Failed("player_order_invalid");
                int first = order.IndexOf(node.PlayerId); order = Enumerable.Range(0, order.Count).Select(index => order[(first + index) % order.Count]).ToList();
                var ids = context.State.Map.Influences.Where(item => item.RouteId == routeId).OrderBy(item => order.IndexOf(item.PlayerId) < 0 ? int.MaxValue : order.IndexOf(item.PlayerId)).ThenBy(item => item.InfluenceId, StringComparer.Ordinal).Select(item => item.InfluenceId).ToList();
                Set(node, "selectedRouteId", NormalizedValue.CreateString(routeId)); Set(node, "selectedItemIds", Strings(ids)); Set(node, "nextIndex", NormalizedValue.CreateInteger(0)); Set(node, "processedSubjectKeys", Strings(Array.Empty<string>()));
            }
            if (node.FlowStage == "scoring") return EffectStepResult.Completed(Object("routeId", NormalizedValue.CreateString(routeId), "removedPlayerIds", NormalizedValue.CreateArray(Value(node.ContinuationData, "processedSubjectKeys").Items.Where(v => v.StringValue.StartsWith("player:")).Select(v => NormalizedValue.CreateInteger(int.Parse(v.StringValue.Substring(7)))).ToList()))).WithFlowStage("finished");
            var subjects = Value(node.ContinuationData, "processedSubjectKeys")?.Items.Select(value => value.StringValue).ToList() ?? new List<string>();
            if (node.FlowStage == "road")
            {
                var step = EffectStepResult.Continue("scoring");
                foreach (string subject in subjects) if (subject.StartsWith("player:") && int.TryParse(subject.Substring(7), out int player) && Number(node.NormalizedArguments, "scoreRemovedPlayers", 0) > 0)
                    step.AddChild(ResourceEffectSpecFactory.GainScore(player, Number(node.NormalizedArguments, "scoreRemovedPlayers", 0), node.SourceId));
                if (Number(node.NormalizedArguments, "scoreExecutor", 0) > 0) step.AddChild(ResourceEffectSpecFactory.GainScore(node.PlayerId, Number(node.NormalizedArguments, "scoreExecutor", 0), node.SourceId));
                return step;
            }
            int next = Number(node.ContinuationData, "nextIndex", 0);
            if (node.FlowStage == "removing")
            {
                var result = context.ChildNodes.Last().NormalizedResult;
                if (Value(result, "removedItemIds")?.Items.Count > 0)
                    foreach (var subject in Value(result, "originalSubjectKeys").Items) if (!subjects.Contains(subject.StringValue)) subjects.Add(subject.StringValue);
                Set(node, "processedSubjectKeys", Strings(subjects));
            }
            var frozen = Value(node.ContinuationData, "selectedItemIds").Items;
            while (next < frozen.Count)
            {
                string id = frozen[next++].StringValue; Set(node, "nextIndex", NormalizedValue.CreateInteger(next));
                if (context.State.Map.Influences.Any(item => item.InfluenceId == id && item.RouteId == routeId))
                    return EffectStepResult.Continue("removing").AddChild(InfluenceEffectSpecFactory.RemoveInfluence(node.PlayerId, id, node.SourceId));
            }
            return EffectStepResult.Continue("road").AddChild(EffectSpec.Create(LuaDomainEffectTypeIds.PlaceRoad, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(node.PlayerId.ToString(CultureInfo.InvariantCulture)), "routeId", NormalizedValue.CreateString(routeId)), node.PlayerId));
        }
        private static EffectStepResult SetNextStart(EffectExecutionContext context)
        {
            string target = Text(context.Node.NormalizedArguments, "targetPlayer");
            if (target.StartsWith("p", StringComparison.Ordinal)) target = target.Substring(1);
            if (!int.TryParse(target, out int playerId) || context.State.FindPlayer(playerId) == null) return EffectStepResult.Failed("invalid_player");
            if (Text(context.Node.NormalizedArguments, "scope", "current_round") != "current_round") return EffectStepResult.Failed("unsupported_start_player_scope");
            context.State.EffectRuntime.PendingNextRoundStartPlayerId = playerId;
            return EffectStepResult.Completed(NormalizedValue.CreateInteger(playerId));
        }
        private static EffectStepResult ActivateEntry(EffectExecutionContext context)
        {
            if (context.Node.FlowStage == "activated")
            {
                foreach (var child in context.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                        return EffectStepResult.Failed("facility_entry_child_failed");
                    if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("设施入场尚未完成。");
                }
                return EffectStepResult.Completed(NormalizedValue.CreateString("activated"));
            }
            string id = Text(context.Node.NormalizedArguments, "instanceId");
            foreach (var placement in context.State.Map.Facilities)
            {
                if (placement == null || placement.ContentInstanceId != id || placement.PlayerId != context.Node.PlayerId) continue;
                var definition = FacilityCardDatabase.Get(context.State, placement.FacilityCardId);
                if (definition == null || !definition.HasEntryEffect) break;
                return EffectStepResult.Continue("activated").AddChild(FacilityEntryEffectSpecFactory.Activate(
                    context.Node.PlayerId, placement.FacilityCardId, id, placement.CityBoardSlotIndex, context.Node.SourceId));
            }
            return EffectStepResult.Failed("facility_entry_target_invalid");
        }
        private static string Text(NormalizedValue args, string key, string fallback = "")
        {
            if (args?.Properties != null) foreach (var item in args.Properties) if (item.Name == key) return item.Value?.StringValue ?? fallback;
            return fallback;
        }
        private static int Number(NormalizedValue args, string key, int fallback = 0)
        {
            if (args?.Properties != null) foreach (var item in args.Properties) if (item.Name == key && item.Value.Kind == NormalizedValueKind.Integer) return (int)item.Value.IntegerValue;
            return fallback;
        }
        private static string Answer(EffectExecutionContext context)
        {
            var answer = context.GetLatestInteractionAnswer();
            if (answer?.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
            return answer?.Kind == NormalizedValueKind.String ? answer.StringValue : answer?.ReferenceId ?? "";
        }
        private static EffectStepResult Wait(EffectExecutionContext context, List<string> candidates, string stage, string type, string prompt, bool multiple = false, bool resourceSale = false)
        {
            var request = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = context.Node.PlayerId,
                Visibility = "owner", PromptKey = prompt, MinSelections = 1, MaxSelections = multiple ? 4 : 1,
                AnswerSchema = multiple ? "candidate_id_array" : "candidate_id" };
            if (resourceSale) request.PromptParameters = Object("interactionSemantic", NormalizedValue.CreateString(ResourceSaleSemantic));
            request.CandidateIds.AddRange(candidates);
            return EffectStepResult.Continue(stage).AddInteraction(request);
        }
        private static EffectStepResult ChooseResourceAmounts(EffectExecutionContext context)
        {
            int amount = Number(context.Node.NormalizedArguments, "amount", 0);
            if (amount < 1 || amount > 99) return EffectStepResult.Failed("invalid_resource_choice_amount");
            if (context.Node.FlowStage == "applied") return EffectStepResult.Completed(NormalizedValue.CreateInteger(amount));
            if (context.Node.FlowStage == "awaiting")
            {
                string encoded = Answer(context);
                if (!YC.Domain.Interactions.ResourceAllocationAnswer.TryParse(encoded,
                    new[] { "Originium", "OriginiumShard", "Iron" }, amount, out var amounts, out _))
                    return EffectStepResult.Failed("invalid_resource_allocation");
                var result = EffectStepResult.Continue("applied");
                foreach (var entry in amounts)
                    if (entry.Value > 0) result.AddChild(ResourceEffectSpecFactory.Gain(context.Node.PlayerId, (YC.Domain.Rules.ResourceType)Enum.Parse(typeof(YC.Domain.Rules.ResourceType), entry.Key), entry.Value, context.Node.SourceId));
                return result;
            }
            var request = new EffectInteractionSpec { InteractionTypeId = "facility.entry.choice", AnsweringPlayerId = context.Node.PlayerId,
                Visibility = "owner", PromptKey = "effect.resource.choose_amounts", AnswerSchema = "resource_allocation", MinSelections = 1, MaxSelections = 1,
                PromptParameters = Object("totalAmount", NormalizedValue.CreateInteger(amount),
                    "rowIds", Strings(new[] { "Originium", "OriginiumShard", "Iron" }),
                    "rowMaximums", NormalizedValue.CreateArray(new[] { amount, amount, amount }.Select(value => NormalizedValue.CreateInteger(value)).ToArray())) };
            request.CandidateIds.AddRange(new[] { "Originium", "OriginiumShard", "Iron", "total:" + amount });
            return EffectStepResult.Continue("awaiting").AddInteraction(request);
        }

        private static EffectStepResult Sell(EffectExecutionContext context)
        {
            var args = context.Node.NormalizedArguments;
            bool batch = Number(args, "apiVersion", 0) == 2;
            if (batch && (ValidateSaleArguments(args).Length > 0 || Text(args, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture))) return EffectStepResult.Failed("sale_arguments_invalid");
            if (Value(args, "apiVersion") != null && !batch)
            {
                if (Number(args, "apiVersion", 0) != 1 || Text(args, "saleMode") != "all" || Text(args, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture) ||
                    !int.TryParse(Text(args, "targetPlayer"), NumberStyles.None, CultureInfo.InvariantCulture, out int targetId) || context.State.FindPlayer(targetId) == null ||
                    !TryBasicResource(Text(args, "resourceType"), out var type) || Number(args, "unitPrice", 0) < 1 || Number(args, "unitPrice", 0) > 99) return EffectStepResult.Failed("sale_arguments_invalid");
                var target = context.State.FindPlayer(targetId); int amount = target.Resources.Get(type), revenue = checked(amount * Number(args, "unitPrice", 0));
                target.Resources.Set(type, 0); target.Resources.GoldVoucher = checked(target.Resources.GoldVoucher + revenue);
                return EffectStepResult.Completed(Object("amount", NormalizedValue.CreateInteger(amount), "revenue", NormalizedValue.CreateInteger(revenue), "targetPlayer", NormalizedValue.CreateInteger(targetId), "resourceType", NormalizedValue.CreateString(Text(args, "resourceType"))));
            }
            var player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null) return EffectStepResult.Failed("invalid_player");
            string[] ids = { "originium", "originium-shard", "iron", "pure-originium" };
            if (context.Node.FlowStage == "awaiting")
            {
                var answer = context.GetLatestInteractionAnswer();
                var values = answer?.Kind == NormalizedValueKind.Array ? answer.Items : new List<NormalizedValue> { answer };
                var amounts = new int[4]; var seen = new HashSet<int>();
                if (values.Count == 0 || values.Count > 4) return EffectStepResult.Failed("sale_answer_invalid");
                foreach (var value in values)
                {
                    var parts = (value?.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value?.StringValue ?? "").Split('|');
                    if (parts.Length != 3 || parts[0] != "sale" || !int.TryParse(parts[2], out var amount) || amount < 0) return EffectStepResult.Failed("sale_answer_invalid");
                    int i = Array.IndexOf(ids, parts[1]);
                    if (i < 0 || !seen.Add(i)) return EffectStepResult.Failed("sale_answer_invalid");
                    amounts[i] = amount;
                }
                var result = new ResourceSaleService().Sell(player.Resources, new ResourceSaleRequest(amounts[0], amounts[1], amounts[2], amounts[3]));
                if (!result.Succeeded) return EffectStepResult.Failed("sale_insufficient_resources");
                return EffectStepResult.Completed(batch ? Object("revenue", NormalizedValue.CreateInteger(result.Revenue),
                    "soldCount", NormalizedValue.CreateInteger(amounts.Sum()), "finished", NormalizedValue.CreateBoolean(amounts.Sum() == 0),
                    "amounts", NormalizedValue.CreateArray(amounts.Select(a => NormalizedValue.CreateInteger(a)).ToList())) : NormalizedValue.CreateInteger(result.Revenue));
            }
            var candidates = new List<string> { "sale|originium|0" };
            int[] maximums = { player.Resources.Originium, player.Resources.OriginiumShard, player.Resources.Iron, player.Resources.PureOriginium };
            for (int i = 0; i < ids.Length; i++) for (int amount = 1; amount <= maximums[i]; amount++) candidates.Add("sale|" + ids[i] + "|" + amount);
            return Wait(context, candidates, "awaiting", Text(context.Node.NormalizedArguments, "interactionType", "lua.choice"), Text(context.Node.NormalizedArguments, "promptKey", "effect.resource.sell"), true, batch);
        }
    }
}
