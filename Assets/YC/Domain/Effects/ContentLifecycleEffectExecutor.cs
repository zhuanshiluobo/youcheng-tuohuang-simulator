using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Facilities;
using YC.Domain.CityStyles;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    /// <summary>通用设施离场、样式标记和奖励生命周期；不包含具体卡面规则。</summary>
    public static class ContentLifecycleEffectExecutor
    {
        public const string RetireFacilityTypeId = "effect.facility.retire";
        public const string ReturnMarkerTypeId = "effect.city_style.return_marker";
        public const string AwardActivateTypeId = "effect.reward.activate";
        public const string AwardCleanupTypeId = "effect.reward.cleanup";
        public const string AwardActionId = "award.activate_special";
        public static readonly string[] PublicTypeIds = { RetireFacilityTypeId, ReturnMarkerTypeId };
        public static string[] Fields(string type) => type == RetireFacilityTypeId
            ? new[] { "apiVersion", "player", "facilityItemId", "expectedCellZoneId", "expectedOrderDigest" } : new[] { "apiVersion", "player" };
        public static string ValidateArguments(string type, NormalizedValue args) =>
            args?.Kind != NormalizedValueKind.Object || args.Properties.Any(p => !Fields(type).Concat(new[] { "kind", "effectTypeId" }).Contains(p.Name)) ||
            (Number(args, "apiVersion") != 1 && !(type == RetireFacilityTypeId && Number(args, "apiVersion") == 2)) || Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer ||
            !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1 ||
            type == RetireFacilityTypeId && (string.IsNullOrWhiteSpace(Text(args, "facilityItemId")) ||
                Number(args, "apiVersion") == 2 && (Text(args, "expectedCellZoneId").Length == 0 || Text(args, "expectedOrderDigest").Length == 0)) ? "lifecycle_arguments_invalid" : "";

        public static void Register(EffectRegistry registry)
        {
            RewardGrantEffectExecutor.Register(registry);
            foreach (var type in PublicTypeIds)
                if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, context =>
                {
                    CharacterLifecycleService.ResumeDeferredDependencies(context);
                    string error = ValidateArguments(context.Node.EffectTypeId, context.Node.NormalizedArguments);
                    if (error.Length > 0) return EffectStepResult.Failed(error);
                    if (Text(context.Node.NormalizedArguments, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture)) return EffectStepResult.Failed("lifecycle_permission_denied");
                    return context.Node.EffectTypeId == RetireFacilityTypeId ? RetireFacility(context) : ReturnMarker(context);
                }, EffectExecutorKind.IntrinsicFlow, type == RetireFacilityTypeId ? "2.0.0" : "v1"));
            foreach (var type in new[] { AwardActivateTypeId, AwardCleanupTypeId })
                if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, ResolveAward, EffectExecutorKind.IntrinsicFlow, "v1"));
        }

        public static bool TryValidate(GameState state, out string reason)
        {
            reason = "content_lifecycle_restore_invalid";
            var awards = state.ContentDefinitions?.Awards;
            if (state.ContentDefinitions != null && (awards == null || awards.Any(d => d == null || string.IsNullOrWhiteSpace(d.DefinitionId) ||
                !new[] { "held_quick_action", "immediate_retired", "persistent", "held" }.Contains(d.Lifecycle)) || awards.Select(d => d.DefinitionId).Distinct().Count() != awards.Count)) return false;
            var receipts = state.EffectRuntime.EffectNodes.Where(n => RewardGrantEffectExecutor.IsAcquisition(n.EffectTypeId) && Flag(n.ContinuationData, "acquisitionCompleted")).ToList();
            if (receipts.GroupBy(n => Text(n.ContinuationData, "selectedRewardItemId")).Any(g => g.Count() != 1)) return false;
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => PublicTypeIds.Contains(n.EffectTypeId) || n.EffectTypeId == AwardActivateTypeId || n.EffectTypeId == AwardCleanupTypeId))
            {
                if (state.FindPlayer(node.PlayerId) == null) return false;
                if (PublicTypeIds.Contains(node.EffectTypeId))
                {
                    if (ValidateArguments(node.EffectTypeId, node.NormalizedArguments).Length > 0 || Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture)) return false;
                    if (node.EffectTypeId == RetireFacilityTypeId)
                    {
                        if (!new[] { "", "retiring_bound_characters", "finished" }.Contains(CharacterLifecycleService.GetResumableFlowStage(node))) return false;
                        if (CharacterLifecycleService.GetResumableFlowStage(node).Length == 0) continue;
                        string id = Text(node.NormalizedArguments, "facilityItemId");
                        var ids = Value(node.ContinuationData, "boundCharacterItemIds");
                        int next = Number(node.ContinuationData, "nextIndex", -1);
                        if (ids?.Kind != NormalizedValueKind.Array || ids.Items.Any(v => v.Kind != NormalizedValueKind.String) || ids.Items.Select(v => v.StringValue).Distinct().Count() != ids.Items.Count ||
                            next < 0 || next > ids.Items.Count || node.FlowStage == "finished" && next != ids.Items.Count ||
                            !node.PositionChanges.Any(c => c.ItemId == id && c.To.ZoneId == FindZone(state, "retired").ZoneId)) return false;
                        if (node.DefinitionVersion == "2.0.0")
                        {
                            var receipt = Value(node.ContinuationData, "transferBatchReceipt");
                            string promoted = Text(node.ContinuationData, "promotedFacilityItemId");
                            var moved = Value(receipt, "movedItemIds");
                            if (Text(receipt, "operationId") != node.EffectId + ":retire" || Text(node.ContinuationData, "retiredFacilityItemId") != id ||
                                moved?.Kind != NormalizedValueKind.Array || !moved.Items.Select(v => v.StringValue).SequenceEqual(
                                    promoted.Length == 0 ? new[] { id } : new[] { id, promoted })) return false;
                            var exit = node.PositionChanges.SingleOrDefault(c => c.ItemId == id && c.To.ZoneId == FindZone(state, "retired").ZoneId);
                            if (exit == null || promoted.Length > 0 && !node.PositionChanges.Any(c => c.ItemId == promoted && c.To.ZoneId == exit.From.ZoneId &&
                                state.ItemZones.Zones.Any(z => z.ZoneId == c.From.ZoneId && z.TemplateId == FacilityLayerQuery.CoveredTemplate && z.ParentId == exit.From.ZoneId))) return false;
                        }
                        foreach (var bound in ids.Items)
                            if (!state.FacilityCharacterBindings.Any(b => b.FacilityItemId == id && b.CharacterItemId == bound.StringValue && b.ControllerPlayerId == node.PlayerId && !b.Active)) return false;
                        for (int index = 0; index < next; index++)
                            if (!node.PositionChanges.Any(c => c.ItemId == ids.Items[index].StringValue && c.To.ZoneId == FindZone(state, "retired").ZoneId)) return false;
                    }
                    else if (!new[] { "", "choose_marker" }.Contains(node.FlowStage) || node.PositionChanges.Any(c =>
                        !state.ItemZones.Zones.Any(z => z.ZoneId == c.From.ZoneId && z.TemplateId == "core.zone.board_marker_track") ||
                        c.To.ZoneId != FindZone(state, "supply", node.PlayerId).ZoneId)) return false;
                }
                else
                {
                    if (node.NormalizedArguments?.Kind != NormalizedValueKind.Object || node.NormalizedArguments.Properties.Count != 1 ||
                        Text(node.NormalizedArguments, "rewardItemId").Length == 0 || !new[] { "", "activated" }.Contains(node.FlowStage)) return false;
                    if (node.FlowStage.Length == 0) continue;
                    string id = Text(node.NormalizedArguments, "rewardItemId");
                    var receipt = receipts.Find(n => Text(n.ContinuationData, "selectedRewardItemId") == id && n.PlayerId == node.PlayerId);
                    if (receipt == null || node.EffectTypeId == AwardActivateTypeId && !CharacterSettlementOptionQuery.IsInvoked(receipt, CharacterSettlementOptionQuery.OptionId(receipt.EffectId, id, AwardActionId)) ||
                        node.EffectTypeId == AwardCleanupTypeId && (!int.TryParse(Text(receipt.ContinuationData, "lastAwardCleanupKey"), out int round) || round > state.Round)) return false;
                }
            }
            reason = ""; return true;
        }

        private static EffectStepResult RetireFacility(EffectExecutionContext context)
        {
            var node = context.Node;
            string id = Text(node.NormalizedArguments, "facilityItemId");
            var item = context.State.ItemZones.Items.Find(i => i.ItemId == id);
            var retired = FindZone(context.State, "retired");
            if (node.FlowStage.Length == 0)
            {
                if (item?.TypeId != "core.facility_card" || item.ControllerSubject.PlayerId != node.PlayerId ||
                    !context.State.ItemZones.Zones.Any(z => z.ZoneId == item.ZoneId && z.TemplateId == "core.zone.city_facility_cell" && z.OwnerPlayerId == node.PlayerId))
                    return EffectStepResult.Failed("facility_retirement_source_invalid");
                var bound = context.State.FacilityCharacterBindings.Where(b => b.Active && b.FacilityItemId == id).ToList();
                if (bound.Any(b => b.ControllerPlayerId != node.PlayerId || !context.State.ItemZones.Items.Any(i => i.ItemId == b.CharacterItemId && i.ControllerSubject.PlayerId == node.PlayerId)))
                    return EffectStepResult.Failed("character_facility_binding_invalid");
                if (CharacterLifecycleService.TryAwaitCardMoves(context, bound.Select(binding => binding.CharacterItemId), retired.ZoneId)) return CharacterLifecycleService.DeferredDependencyWait();
                Set(node, "boundCharacterItemIds", Strings(bound.Select(b => b.CharacterItemId).Distinct()));
                Set(node, "nextIndex", NormalizedValue.CreateInteger(0));
                var request = new ItemTransferRequest { ExecutingPlayerId = node.PlayerId, ItemIds = new List<string> { id },
                    ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = retired.ZoneId },
                    InsertionKind = "unordered", RequiredCount = 1, OperationProfileId = "core.facility_card_transfer" };
                var service = new ItemTransferService();
                var cell = YC.Domain.Facilities.FacilityLayerQuery.ResolveCell(context.State, node.PlayerId,
                    YC.Domain.Facilities.FacilityLayerQuery.CitySlot(context.State, id));
                if (Number(node.NormalizedArguments, "apiVersion") == 2 &&
                    (Text(node.NormalizedArguments, "expectedCellZoneId") != cell.CellZoneId ||
                     Text(node.NormalizedArguments, "expectedOrderDigest") != cell.OrderDigest))
                    return EffectStepResult.Failed("facility_retirement_source_changed");
                var legs = new List<ItemTransferRequest> { request };
                string promoted = cell.CoveredItemIds.FirstOrDefault() ?? "";
                if (promoted.Length > 0) legs.Add(new ItemTransferRequest { ExecutingPlayerId = node.PlayerId,
                    ItemIds = new List<string> { promoted }, RequiredCount = 1, ExpectedSource = new ItemPositionRef { ZoneId = cell.CoveredZoneId },
                    Destination = new ZoneRef { ZoneId = cell.CellZoneId }, InsertionKind = "unordered", OperationProfileId = "core.facility_card_transfer" });
                var batch = ItemTransferService.CreateBatch(context.State, node.PlayerId, node.EffectId + ":retire", legs.ToArray());
                service.RelocateBatch(context, batch);
                Set(context.Node, "retiredFacilityItemId", NormalizedValue.CreateString(id));
                Set(context.Node, "promotedFacilityItemId", NormalizedValue.CreateString(promoted));
                return EffectStepResult.Continue("retiring_bound_characters");
            }
            if (node.FlowStage != "retiring_bound_characters" && node.FlowStage != "finished") throw new InvalidOperationException("facility_retirement_stage_invalid");
            if (item?.ZoneId != retired.ZoneId) throw new InvalidOperationException("facility_retirement_state_invalid");
            var ids = Value(node.ContinuationData, "boundCharacterItemIds").Items;
            int next = Number(node.ContinuationData, "nextIndex");
            if (next < ids.Count)
            {
                if (CharacterLifecycleService.TryAwaitCardMoves(context, new[] { ids[next].StringValue }, retired.ZoneId)) return CharacterLifecycleService.DeferredDependencyWait();
                string error = CharacterLifecycleService.MoveCard(context, ids[next].StringValue, retired.ZoneId);
                if (error.Length > 0) return EffectStepResult.Failed(error);
                Set(context.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == node.EffectId), "nextIndex", NormalizedValue.CreateInteger(next + 1));
                return EffectStepResult.Continue("retiring_bound_characters");
            }
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true), "facilityItemId", NormalizedValue.CreateString(id))).WithFlowStage("finished");
        }

        private static EffectStepResult ReturnMarker(EffectExecutionContext context)
        {
            var node = context.Node;
            if (node.FlowStage != "" && node.FlowStage != "choose_marker") throw new InvalidOperationException("city_style_return_stage_invalid");
            var declarations = context.State.FindPlayer(node.PlayerId).DeclaredCityStyles.Where(d =>
                CityStyleDatabase.Get(context.State, d.CityStyleId)?.Level == 2 && d.MarkerArea.Length > 0).ToList();
            var ids = declarations.Select(d => d.InfluenceMarkerId).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return EffectStepResult.Failed("no_city_style_marker");
            string selected = node.FlowStage == "choose_marker" ? Answer(context) : "";
            if (!ids.Contains(selected))
            {
                var request = new EffectInteractionSpec { InteractionTypeId = "lua.choice", AnsweringPlayerId = node.PlayerId, Visibility = "owner",
                    PromptKey = "city_style.choose_level_two_marker", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
                request.CandidateIds.AddRange(ids);
                return EffectStepResult.Continue("choose_marker").AddInteraction(PrepareInteraction(context, request));
            }
            var item = context.State.ItemZones.Items.Single(i => i.ItemId == selected);
            new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = node.PlayerId, ItemIds = new List<string> { selected },
                ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId, SlotId = item.SlotId }, Destination = new ZoneRef { ZoneId = FindZone(context.State, "supply", node.PlayerId).ZoneId },
                InsertionKind = "unordered", RequiredCount = 1, OperationProfileId = "core.player_marker_storage" });
            // 分数、宣告历史、同回合已使用记录一概不写；资格只来自实际轨道。
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true), "markerItemId", NormalizedValue.CreateString(selected)));
        }

        public static EffectNodeRuntimeState Acquisition(GameState state, string itemId) => state.EffectRuntime.EffectNodes.SingleOrDefault(n =>
            RewardGrantEffectExecutor.IsAcquisition(n.EffectTypeId) && Flag(n.ContinuationData, "acquisitionCompleted") && Text(n.ContinuationData, "selectedRewardItemId") == itemId);
        public static bool IsHeld(GameState state, ItemInstanceState item, int player) => item?.TypeId == "core.award_card" && item.ControllerSubject.PlayerId == player &&
            state.ItemZones.Zones.Any(z => z.ZoneId == item.ZoneId && z.TemplateId == "core.zone.player_rewards" && z.OwnerPlayerId == player);
        public static IReadOnlyList<CharacterSettlementOption> GetAwardOptions(GameState state, int player)
        {
            var options = new List<CharacterSettlementOption>();
            foreach (var item in state.ItemZones.Items.Where(i => i.TypeId == "core.award_card" && i.ControllerSubject.PlayerId == player))
            {
                if (state.ContentDefinitions?.AbilitySources.Any(d => d.DefinitionId == item.DefinitionId && d.Actions.Count > 0) == true) continue;
                var definition = state.ContentDefinitions?.Awards.Find(d => d.DefinitionId == item.DefinitionId && d.Lifecycle == "held_quick_action");
                var owner = Acquisition(state, item.ItemId);
                if (definition == null || owner == null) continue;
                string optionId = CharacterSettlementOptionQuery.OptionId(owner.EffectId, item.ItemId, AwardActionId);
                bool invoked = CharacterSettlementOptionQuery.IsInvoked(owner, optionId);
                if (!IsHeld(state, item, player) && !invoked) continue;
                string reason = invoked ? "settlement_already_invoked" : state.CurrentPlayerId != player ? "not_current_player" :
                    state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2 ? "wrong_phase" :
                    HasBlockingInteraction(state, player) || state.CharacterUses.Any(u => !u.Finished) ? "reward_window_closed" : "";
                options.Add(new CharacterSettlementOption { OptionId = optionId, OwnerEffectId = owner.EffectId, SourceItemId = item.ItemId, CandidateId = item.ItemId,
                    AnsweringPlayerId = player, StateRevision = state.EffectRuntime.StateRevision, TitleKey = definition.TitleKey, DescriptionKey = definition.DescriptionKey,
                    IsEnabled = reason.Length == 0, DisabledReasonCode = reason, VisibleToViewer = true });
            }
            return options;
        }
        private static bool HasBlockingInteraction(GameState state, int player)
        {
            var window = state.EffectRuntime.MainNodes.Find(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId &&
                n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && n.PlayerId == player);
            if (state.EffectRuntime.ActiveMainNodeId.Length > 0 && window == null) return true;
            // 行动轮的结束确认始终留在主链上，不阻止前/后快速行动；其他效果仍须先结算。
            return state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && !(window != null &&
                r.OwnerEffectId == window.ExecutionEffectId && r.InteractionTypeId.StartsWith(RoundExecutionService.MainlineCompletionInteractionTypeId + ":", StringComparison.Ordinal)));
        }
        public static EffectSpec AwardSpec(int player, string itemId, bool cleanup) => EffectSpec.Create(cleanup ? AwardCleanupTypeId : AwardActivateTypeId,
            Object("rewardItemId", NormalizedValue.CreateString(itemId)), player);
        private static EffectStepResult ResolveAward(EffectExecutionContext context)
        {
            var node = context.Node;
            bool cleanup = node.EffectTypeId == AwardCleanupTypeId;
            if (node.FlowStage == "activated")
                return context.ChildNodes.Any(c => c.Status == EffectNodeStatus.Failed || c.Status == EffectNodeStatus.Faulted)
                    ? EffectStepResult.Failed("reward_effect_failed") : EffectStepResult.Completed();
            if (node.FlowStage.Length > 0) throw new InvalidOperationException("reward_lifecycle_stage_invalid");
            var item = context.State.ItemZones.Items.Find(i => i.ItemId == Text(node.NormalizedArguments, "rewardItemId"));
            var owner = item == null ? null : Acquisition(context.State, item.ItemId);
            var definition = context.State.ContentDefinitions?.Awards.Find(d => d.DefinitionId == item?.DefinitionId);
            if (owner == null || definition?.Lifecycle != (cleanup ? "persistent" : "held_quick_action") || !IsHeld(context.State, item, node.PlayerId)) return EffectStepResult.Failed("reward_source_invalid");
            if (cleanup)
            {
                string key = context.State.Round.ToString(CultureInfo.InvariantCulture);
                if (Text(owner.ContinuationData, "lastAwardCleanupKey") == key) return EffectStepResult.Completed();
                Set(owner, "lastAwardCleanupKey", NormalizedValue.CreateString(key));
            }
            else
            {
                string optionId = CharacterSettlementOptionQuery.OptionId(owner.EffectId, item.ItemId, AwardActionId);
                var option = GetAwardOptions(context.State, node.PlayerId).SingleOrDefault(o => o.OptionId == optionId);
                if (CharacterSettlementOptionQuery.IsInvoked(owner, optionId)) return EffectStepResult.Completed();
                if (option?.IsEnabled != true) return EffectStepResult.Failed(option?.DisabledReasonCode ?? "reward_window_closed");
                var invoked = Value(owner.ContinuationData, "invokedOptionIds")?.Clone() ?? Strings(Array.Empty<string>());
                invoked.Items.Add(NormalizedValue.CreateString(optionId)); Set(owner, "invokedOptionIds", invoked);
            }
            return EffectStepResult.Continue("activated").AddEvent(new EffectEventRequest { EventType = cleanup ? "reward.cleanup" : "reward.activated",
                RouteKey = item.DefinitionId, PlayerId = node.PlayerId, Payload = Object("rewardItemId", NormalizedValue.CreateString(item.ItemId)) });
        }
    }
}
