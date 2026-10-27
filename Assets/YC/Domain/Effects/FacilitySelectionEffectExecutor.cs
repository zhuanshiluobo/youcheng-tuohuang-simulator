using System;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Cards;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    // 交互只返回候选答案；建设规则与状态提交仍由 Build Effect 负责。
    public static class FacilitySelectionEffectExecutor
    {
        public const string TypeId = "effect.facility.choose_build";
        public const string PanelTypeId = "effect.facility.select_panel";
        public const string PanelValidationVersion = "1.0.1";
        internal const string PanelCandidatesChanged = "facility_panel_candidates_changed";
        public static EffectSpec PanelSpec(int player, bool allowDecline, string operationProfile, bool emptyIsFailure,
            IEnumerable<string> itemIds, string promptKey)
        {
            var args = Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),
                "allowDecline", NormalizedValue.CreateBoolean(allowDecline), "operationProfile", NormalizedValue.CreateString(operationProfile),
                "emptyIsFailure", NormalizedValue.CreateBoolean(emptyIsFailure), "promptKey", NormalizedValue.CreateString(promptKey));
            if (itemIds != null) args.Properties.Add(new NormalizedValueEntry { Name = "itemIds", Value = Strings(itemIds) });
            args.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return new EffectSpec(PanelTypeId, args) { PlayerId = player, DefinitionVersion = "1.0.0", Visibility = "owner" };
        }
        public static string ValidatePanelArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || !args.IsValid() || args.Properties.Any(p => !new[] { "kind", "effectTypeId", "apiVersion", "player", "allowDecline", "operationProfile", "emptyIsFailure", "promptKey", "itemIds" }.Contains(p.Name)) ||
                Number(args, "apiVersion", 0) != 1 || Value(args, "player")?.Kind != NormalizedValueKind.String ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1 ||
                Value(args, "allowDecline")?.Kind != NormalizedValueKind.Boolean || Value(args, "emptyIsFailure")?.Kind != NormalizedValueKind.Boolean ||
                Text(args, "promptKey").Length == 0 || !new[] { "city_top", ContentLifecycleEffectExecutor.RetireFacilityTypeId }.Contains(Text(args, "operationProfile")) ||
                Value(args, "itemIds") != null && (Value(args, "itemIds").Kind != NormalizedValueKind.Array || Value(args, "itemIds").Items.Any(v => v.Kind != NormalizedValueKind.String)))
                return "facility_panel_arguments_invalid";
            return "";
        }
        private static List<FacilityCellLayerSnapshot> PanelCandidates(GameState state, EffectNodeRuntimeState node)
        {
            var args = node.NormalizedArguments;
            var allowed = Value(args, "itemIds")?.Items.Select(v => v.StringValue).ToList();
            var candidates = FacilityLayerQuery.ResolveCity(state, node.PlayerId).Where(cell => cell.TopItemId.Length > 0 &&
                (allowed == null || allowed.Contains(cell.TopItemId))).ToList();
            if (Text(args, "operationProfile") == ContentLifecycleEffectExecutor.RetireFacilityTypeId)
                candidates = candidates.Where(cell => state.FacilityCharacterBindings.Where(b => b.Active && b.FacilityItemId == cell.TopItemId)
                    .All(b => b.ControllerPlayerId == node.PlayerId && state.ItemZones.Items.Any(i => i.ItemId == b.CharacterItemId && i.ControllerSubject.PlayerId == node.PlayerId))).ToList();
            return candidates;
        }
        private static List<NormalizedValue> PanelCandidateValues(IEnumerable<FacilityCellLayerSnapshot> candidates) =>
            candidates.Select(cell => Object("slotIndex", NormalizedValue.CreateInteger(cell.SlotIndex),
                "cellZoneId", NormalizedValue.CreateString(cell.CellZoneId), "topItemId", NormalizedValue.CreateString(cell.TopItemId),
                "orderDigest", NormalizedValue.CreateString(cell.OrderDigest))).ToList();
        internal static bool NeedsPanelRefresh(GameState state, EffectNodeRuntimeState node)
        {
            if (node?.EffectTypeId != PanelTypeId || node.DefinitionVersion != "1.0.0" || node.FlowStage != "panel_select" ||
                ValidatePanelArguments(node.NormalizedArguments).Length > 0 || Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture)) return false;
            var saved = Value(node.ContinuationData, "panelCandidates");
            return saved?.Kind == NormalizedValueKind.Array && saved.ToDeterministicString() !=
                NormalizedValue.CreateArray(PanelCandidateValues(PanelCandidates(state, node))).ToDeterministicString();
        }
        private static EffectStepResult ExecutePanel(EffectExecutionContext c)
        {
            var args = c.Node.NormalizedArguments;
            string invalid = ValidatePanelArguments(args);
            if (invalid.Length > 0 || Text(args, "player") != c.Node.PlayerId.ToString(CultureInfo.InvariantCulture))
                return EffectStepResult.Failed(invalid.Length > 0 ? invalid : "facility_panel_player_invalid");
            var candidates = PanelCandidates(c.State, c.Node);
            if (candidates.Count == 0) return Flag(args, "emptyIsFailure") ? EffectStepResult.Failed("no_legal_target") :
                EffectStepResult.Completed(Object("outcome", NormalizedValue.CreateString("no_legal_target")));
            if (c.Node.FlowStage == "panel_select")
            {
                var answer = c.GetLatestInteractionAnswer();
                if (answer?.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue && Flag(args, "allowDecline")) return EffectStepResult.Failed("player_declined");
                if (int.TryParse(Answer(c), NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
                {
                    var saved = Value(c.Node.ContinuationData, "panelCandidates")?.Items.SingleOrDefault(v => Number(v, "slotIndex", -1) == slot);
                    var current = candidates.SingleOrDefault(v => v.SlotIndex == slot);
                    if (saved != null && current != null && Text(saved, "topItemId") == current.TopItemId &&
                        Text(saved, "cellZoneId") == current.CellZoneId && Text(saved, "orderDigest") == current.OrderDigest)
                        return EffectStepResult.Completed(Object("slotIndex", NormalizedValue.CreateInteger(slot),
                            "facilityItemId", NormalizedValue.CreateString(current.TopItemId), "cellZoneId", NormalizedValue.CreateString(current.CellZoneId),
                            "orderDigest", NormalizedValue.CreateString(current.OrderDigest)));
                }
            }
            else if (c.Node.FlowStage.Length > 0) throw new InvalidOperationException("facility_panel_stage_invalid");
            var values = PanelCandidateValues(candidates);
            Set(c.Node, "panelCandidates", NormalizedValue.CreateArray(values));
            var request = new EffectInteractionSpec { InteractionTypeId = FacilityPanelSelectionRequest.InteractionType,
                AnsweringPlayerId = c.Node.PlayerId, Visibility = "owner", PromptKey = Text(args, "promptKey"), AnswerSchema = "candidate_id",
                MinSelections = 1, MaxSelections = 1, AllowDecline = Flag(args, "allowDecline"),
                PromptParameters = Object("operationProfile", NormalizedValue.CreateString(Text(args, "operationProfile")),
                    "player", NormalizedValue.CreateInteger(c.Node.PlayerId), "candidateSlots", NormalizedValue.CreateArray(values)) };
            request.CandidateIds.AddRange(candidates.Select(v => v.SlotIndex.ToString(CultureInfo.InvariantCulture)));
            return EffectStepResult.Continue("panel_select").AddInteraction(PrepareInteraction(c, request));
        }

        public const string NormalBuildInteractionType = "facility.build.normal";
        internal static bool IsNormalBuildSelection(EffectNodeRuntimeState selection)
        {
            if (selection?.EffectTypeId != TypeId) return false;
            string source = Text(selection.NormalizedArguments, "sourceZone", "supply");
            string mode = Text(selection.NormalizedArguments, "paymentMode", "auto");
            return (source == "supply" || source == "deck" || source == "authorized") && (mode == "auto" || mode == "resources" || mode == "gold");
        }

        // 候选集合证明授予；此处只复核选中实例的来源、限制与付款模式。
        // 取牌后按冻结源验证，不能要求已放置设施仍在供应槽或载体仍在场。
        internal static bool ValidateEffectBuildSource(GameState state, EffectNodeRuntimeState selection,
            string targetItemId, string sourceZoneId, string paymentMode, bool requireCurrentSource) =>
            ValidateEffectBuildSource(state, selection, targetItemId, sourceZoneId, paymentMode, requireCurrentSource, null);
        internal static bool ValidateEffectBuildSource(GameState state, EffectNodeRuntimeState selection,
            string targetItemId, string sourceZoneId, string paymentMode, bool requireCurrentSource, EffectRegistry registry)
        {
            if (!IsNormalBuildSelection(selection) || state?.ItemZones == null || string.IsNullOrEmpty(sourceZoneId) ||
                Text(selection.NormalizedArguments,"expectedSourceZoneId").Length>0 && Text(selection.NormalizedArguments,"expectedSourceZoneId")!=sourceZoneId) return false;
            if(requireCurrentSource && Flag(selection.NormalizedArguments,"consumeMainAction") && !new YC.Domain.Rules.MainActionBudgetService().ValidateCanSpend(state,selection.PlayerId).IsValid) return false;
            string requested = Text(selection.NormalizedArguments, "paymentMode", "auto");
            if (paymentMode != "resources" && paymentMode != "gold" || requested != "auto" && requested != paymentMode) return false;
            var item = state.ItemZones.Items.Find(i => i.ItemId == targetItemId);
            var zone = state.ItemZones.Zones.Find(z => z.ZoneId == sourceZoneId);
            var definition = FacilityCardDatabase.Get(state, item?.DefinitionId);
            if (!FacilityLayerQuery.IsFacility(item) || zone == null || definition == null || requireCurrentSource && item.ZoneId != sourceZoneId) return false;
            string source = Text(selection.NormalizedArguments, "sourceZone", "supply");
            bool includeStored = Value(selection.NormalizedArguments, "includeStoredFacilities") == null || Flag(selection.NormalizedArguments, "includeStoredFacilities");
            if (source == "authorized")
            {
                if (requireCurrentSource)
                {
                    // 只读归档身份使用下方真实已存候选证明；实际选中、受理、激活仍必须有Registry即时查询。
                    if(registry==null) return false;
                    try
                    {
                        if (registry.FacilityBuildQueries.AuthorizeSource(state, selection.PlayerId, targetItemId, source,
                            selection.EffectId, Text(selection.NormalizedArguments, "sourceKey")).SourceZoneId != sourceZoneId) return false;
                    }
                    catch (InvalidOperationException e) when (e.Message == "invalid_build_source") { return false; }
                }
                else
                {
                    var proof = state.EffectRuntime.InteractionRequests.Where(r => r.OwnerEffectId == selection.EffectId &&
                        r.InteractionTypeId == NormalBuildInteractionType).SelectMany(r => state.EffectRuntime.CandidateResolutions
                            .Where(record => record.CandidateSetId == r.CandidateResolutionId && record.SourceEffectId == selection.EffectId)
                            .SelectMany(record => record.FacilitySourceProofs))
                        .LastOrDefault(v => v.ItemId == targetItemId && v.SourceZoneId == sourceZoneId &&
                            v.SourceKey == Text(selection.NormalizedArguments, "sourceKey") && v.ContentHash == state.ContentPackHash);
                    if (proof == null) return false;
                }
            }
            else if (source == "deck" ? zone.LocalZoneId != "facility_deck" : zone.TemplateId != FacilityLayerQuery.SupplyTemplate &&
                !(includeStored && zone.TemplateId == "core.zone.card_facility_storage" &&
                  (!requireCurrentSource || IsActiveStoredFacility(state, zone, selection.PlayerId))) ||
                source == "supply" && zone.TemplateId == FacilityLayerQuery.SupplyTemplate && requireCurrentSource && !FacilityLayerQuery.IsSupplyTop(state, targetItemId)) return false;
            string effect = Text(selection.NormalizedArguments, "effectId"); var bound = Value(selection.NormalizedArguments, "maxGoldVoucherCost");
            return (effect.Length == 0 || definition.EffectId == effect) && (!Flag(selection.NormalizedArguments, "excludeRainbow") || definition.Color != "rainbow") &&
                (bound == null || bound.Kind == NormalizedValueKind.Integer && bound.IntegerValue >= 0 && definition.GoldVoucherCost <= bound.IntegerValue);
        }
        private static bool SelectionAllowsDecline(EffectNodeRuntimeState node) =>
            Value(node.NormalizedArguments, "allowDecline") != null ? Flag(node.NormalizedArguments, "allowDecline") :
                Text(node.NormalizedArguments, "sourceZone", "supply") == "reserve";
        private static void RecordSourceProofs(FacilityBuildCandidateSnapshot snapshot, CandidateResolutionRecord record)
        {
            record.FacilitySourceProofs = record.FinalCandidateIds.Select(id => {
                var c = snapshot.Candidates.Single(candidate => candidate.ItemId == id);
                return new FacilityCandidateSourceProof { ItemId = c.ItemId, SourceZoneId = c.SourceZoneId,
                    SourceKey = c.Authorization?.SourceKey ?? "", ContentHash = c.Authorization?.ContentHash ?? "" };
            }).ToList();
        }
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, "2.0.1"));
            if (!registry.TryGet(PanelTypeId, out _)) registry.Register(new EffectRegistration(PanelTypeId, ExecutePanel, EffectExecutorKind.IntrinsicFlow, "1.0.0"));
        }
        private static string Text(NormalizedValue value, string key, string fallback = "")
        {
            if (value?.Properties != null) foreach (var p in value.Properties) if (p.Name == key) return p.Value.StringValue ?? fallback;
            return fallback;
        }
        private static string Answer(EffectExecutionContext c)
        {
            var v = c.GetLatestInteractionAnswer();
            if (v?.Kind == NormalizedValueKind.Array && v.Items.Count == 1) v = v.Items[0];
            return v?.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v?.StringValue ?? "";
        }
        private static EffectStepResult Wait(EffectExecutionContext c, string stage, string prompt, List<string> ids)
        {
            if (ids.Count == 0) return EffectStepResult.Completed(NormalizedValue.CreateString("no_legal_target"));
            var request = new EffectInteractionSpec { InteractionTypeId = "facility.entry.choice", PromptKey = prompt,
                AnsweringPlayerId = c.Node.PlayerId, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1,
                AllowDecline = stage == "card" && (Flag(c.Node.NormalizedArguments, "allowDecline") || Text(c.Node.NormalizedArguments, "sourceZone", "supply") == "reserve") };
            request.CandidateIds.AddRange(ids);
            return EffectStepResult.Continue(stage).AddInteraction(ItemZoneLegacyProjection.IsActive(c.State) ? PrepareInteraction(c, request) : request);
        }
        private static EffectStepResult WaitEntityCards(EffectExecutionContext c, FacilityBuildCandidateSnapshot snapshot, bool allowSkip)
        {
            var record = snapshot.Resolution;
            RecordSourceProofs(snapshot, record);
            if (record.FinalCandidateIds.Count == 0) return EffectStepResult.Completed(NormalizedValue.CreateString("no_legal_target"));
            var request = new EffectInteractionSpec { InteractionTypeId = "facility.entry.choice", PromptKey = "facility.entry.choose_additional_build",
                AnsweringPlayerId = c.Node.PlayerId, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, AllowDecline = allowSkip,
                CandidateSetId = record.CandidateSetId, CandidateResolutionId = record.CandidateSetId, CandidateSetVersion = record.Version,
                PromptParameters = Object("candidateDefinitionIds", Strings(record.FinalCandidateIds.Select(id => snapshot.Candidates.FirstOrDefault(candidate => candidate.ItemId == id)?.DefinitionId ?? "")),
                    "isDefaultSupplySet", NormalizedValue.CreateBoolean(snapshot.IsDefaultSupplySet)) };
            request.CandidateIds.AddRange(record.FinalCandidateIds);
            c.State.EffectRuntime.CandidateResolutions.Add(record);
            return EffectStepResult.Continue("card").AddInteraction(request);
        }

        private static EffectStepResult ExecuteEntity(EffectExecutionContext c)
        {
            var rainbow = Value(c.Node.NormalizedArguments, "excludeRainbow");
            if (rainbow != null && rainbow.Kind != NormalizedValueKind.Boolean) return EffectStepResult.Failed("build_selection_arguments_invalid");
            var includeStored = Value(c.Node.NormalizedArguments, "includeStoredFacilities");
            if (includeStored != null && includeStored.Kind != NormalizedValueKind.Boolean) return EffectStepResult.Failed("build_selection_arguments_invalid");
            foreach(string field in new[]{"sourceZone","sourceKey","expectedSourceZoneId"})
                if(Value(c.Node.NormalizedArguments,field)!=null && Value(c.Node.NormalizedArguments,field).Kind!=NormalizedValueKind.String) return EffectStepResult.Failed("build_selection_arguments_invalid");
            if(Value(c.Node.NormalizedArguments,"consumeMainAction")!=null && Value(c.Node.NormalizedArguments,"consumeMainAction").Kind!=NormalizedValueKind.Boolean) return EffectStepResult.Failed("build_selection_arguments_invalid");
            string source = Text(c.Node.NormalizedArguments, "sourceZone", "supply");
            if (source != "supply" && source != "deck" && source != "reserve" && source != "authorized") return EffectStepResult.Failed("unsupported_build_source");
            if (source == "authorized" && Text(c.Node.NormalizedArguments, "sourceKey").Length == 0) return EffectStepResult.Failed("facility_source_authorization_required");
            if(c.Node.FlowStage.Length==0 && Flag(c.Node.NormalizedArguments,"consumeMainAction") && !new YC.Domain.Rules.MainActionBudgetService().ValidateCanSpend(c.State,c.Node.PlayerId).IsValid) return EffectStepResult.Failed("main_action_budget_unavailable");
            c.Registry.FacilityBuildCandidates.UseQueries(c.Registry.FacilityBuildQueries);
            bool free = source == "reserve" || Text(c.Node.NormalizedArguments, "paymentMode") == "free";
            if (c.Node.FlowStage == "built")
            {
                if (c.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed && !(IsNormalBuildSelection(c.Node) && child.Payment?.Preparation?.IsCancelled == true))) return EffectStepResult.Failed("build_child_failed");
                var id = Value(c.Node.ContinuationData, "selectedItemIds")?.Items.FirstOrDefault()?.StringValue;
                if (string.IsNullOrEmpty(id) || !c.ChildNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build &&
                    n.Status == EffectNodeStatus.Completed && FacilityBuildJournal.Read(n)?.ItemId == id))
                    throw new InvalidOperationException("build_result_missing");
                return EffectStepResult.Completed(Object("outcome", NormalizedValue.CreateString("built"), "builtItemId", NormalizedValue.CreateString(id)));
            }
            if (IsNormalBuildSelection(c.Node))
            {
                if (c.Node.FlowStage == "deck_declined") return EffectStepResult.Completed(NormalizedValue.CreateString("player_declined"));
                if (c.Node.FlowStage == "deck_select")
                {
                    var previous = c.GetLatestInteractionAnswer();
                    if (previous?.Kind == NormalizedValueKind.Boolean && !previous.BooleanValue && Flag(c.Node.NormalizedArguments, "allowDecline"))
                        return EffectStepResult.Completed(NormalizedValue.CreateString("player_declined"));
                    if (previous != null && previous.Kind != NormalizedValueKind.Null) return EffectStepResult.Failed("effect_build_requires_preparation");
                }
                if (c.Node.FlowStage.Length != 0 && c.Node.FlowStage != "deck_select") return EffectStepResult.Failed("effect_build_stage_invalid");
                var scope = c.Registry.FacilityBuildCandidates.Resolve(c.State, c.Node.PlayerId,
                    new FacilityBuildCandidateOptions { SourceZone = source, SourceKey = Text(c.Node.NormalizedArguments, "sourceKey"), ExpectedSourceZoneId=Text(c.Node.NormalizedArguments,"expectedSourceZoneId"), IncludeStoredFacilities = source == "supply" && (includeStored == null || Flag(c.Node.NormalizedArguments, "includeStoredFacilities")),
                        EffectId = Text(c.Node.NormalizedArguments, "effectId"), ExcludeRainbow = Flag(c.Node.NormalizedArguments, "excludeRainbow"),
                        MaxGoldVoucherCost = Value(c.Node.NormalizedArguments, "maxGoldVoucherCost") == null ? (int?)null : Number(c.Node.NormalizedArguments, "maxGoldVoucherCost") },
                    c.Node.EffectId, c.State.EffectRuntime.StateRevision + 1);
                var record = scope.Resolution;
                RecordSourceProofs(scope, record);
                if (record.FinalCandidateIds.Count == 0) return EffectStepResult.Completed(NormalizedValue.CreateString("no_legal_target"));
                var request = new EffectInteractionSpec { InteractionTypeId = NormalBuildInteractionType, PromptKey = "facility.entry.choose_additional_build",
                    AnsweringPlayerId = c.Node.PlayerId, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1,
                    AllowDecline = Flag(c.Node.NormalizedArguments, "allowDecline"), CandidateSetId = record.CandidateSetId,
                    CandidateResolutionId = record.CandidateSetId, CandidateSetVersion = record.Version,
                    PromptParameters = Object("sourceZone", NormalizedValue.CreateString(source),
                        "expectedSourceZoneId", NormalizedValue.CreateString(source == "deck" ? FindZone(c.State, "facility_deck").ZoneId : ""),
                        "candidateDefinitionIds", Strings(scope.Candidates.Select(candidate => candidate.DefinitionId)),
                        "isDefaultSupplySet", NormalizedValue.CreateBoolean(scope.IsDefaultSupplySet)) };
                request.CandidateIds.AddRange(record.FinalCandidateIds); c.State.EffectRuntime.CandidateResolutions.Add(record);
                return EffectStepResult.Continue("deck_select").AddInteraction(request);
            }
            if (c.Node.FlowStage.Length == 0)
            {
                var snapshot = c.Registry.FacilityBuildCandidates.Resolve(c.State, c.Node.PlayerId,
                    new FacilityBuildCandidateOptions { SourceZone = source, SourceKey = Text(c.Node.NormalizedArguments, "sourceKey"), ExpectedSourceZoneId=Text(c.Node.NormalizedArguments,"expectedSourceZoneId"), IncludeStoredFacilities = includeStored == null || Flag(c.Node.NormalizedArguments, "includeStoredFacilities"),
                        EffectId = Text(c.Node.NormalizedArguments, "effectId"), ExcludeRainbow = Flag(c.Node.NormalizedArguments, "excludeRainbow"),
                        MaxGoldVoucherCost = Value(c.Node.NormalizedArguments, "maxGoldVoucherCost") == null ? (int?)null : Number(c.Node.NormalizedArguments, "maxGoldVoucherCost"),
                        PaymentMode = Text(c.Node.NormalizedArguments, "paymentMode", "auto"), RequireBuildable = true }, c.Node.EffectId, c.State.EffectRuntime.StateRevision + 1);
                return WaitEntityCards(c, snapshot, SelectionAllowsDecline(c.Node));

            }
            if (c.Node.FlowStage == "card")
            {
                var answer = c.GetLatestInteractionAnswer();
                if (answer?.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue && SelectionAllowsDecline(c.Node))
                    return EffectStepResult.Completed(NormalizedValue.CreateString("player_declined"));
                string selected = Answer(c);
                if (selected == "choice.skip") return EffectStepResult.Failed("build_selection_answer_invalid");
                var item = new ItemZoneQuery().FindItem(c.State, selected);
                if (!IsAuthorizedEntitySource(c, item, source) || !WithinCost(c, item)) return RefreshEntityCandidates(c);
                Set(c.Node, "selectedItemIds", Strings(new[] { selected }));
                Set(c.Node, "selectedSourceZoneId", NormalizedValue.CreateString(item.ZoneId));
                if (free) return BuildEntityChild(c, selected, "free", source == "reserve");
                var facility = FacilityCardDatabase.Get(c.State, item.DefinitionId); var player = c.State.FindPlayer(c.Node.PlayerId);
                var modes = new List<string>();
                foreach (string mode in new[] { "resources", "gold" }) if (BuildFacilityService.ResolvePaymentMode(c.State, player, facility, new FacilityBuildCostService().GetEffectiveResourceCost(c.State, player, facility), mode).Length > 0) modes.Add(mode);
                return Wait(c, "payment", "effect.build.choose_payment", modes);
            }
            if (c.Node.FlowStage == "payment")
            {
                string selected = Value(c.Node.ContinuationData, "selectedItemIds").Items[0].StringValue;
                if (Text(c.Node.ContinuationData, "selectedSourceZoneId").Length > 0 && c.State.ItemZones.Items.Find(item => item.ItemId == selected)?.ZoneId != Text(c.Node.ContinuationData, "selectedSourceZoneId")) return RefreshEntityCandidates(c);
                if (!IsAuthorizedEntitySource(c, new ItemZoneQuery().FindItem(c.State, selected), source) || !WithinCost(c, new ItemZoneQuery().FindItem(c.State, selected))) return RefreshEntityCandidates(c);
                return BuildEntityChild(c, selected, Answer(c), false);
            }
            throw new InvalidOperationException("build_selection_stage_invalid");
        }
        private static bool IsAuthorizedEntitySource(EffectExecutionContext context, ItemInstanceState item, string source)
        {
            if (item == null || Text(context.Node.NormalizedArguments,"expectedSourceZoneId").Length>0 && item.ZoneId!=Text(context.Node.NormalizedArguments,"expectedSourceZoneId")) return false;
            try { context.Registry.FacilityBuildQueries.AuthorizeSource(context.State, context.Node.PlayerId, item.ItemId,
                source, context.Node.EffectId, Text(context.Node.NormalizedArguments, "sourceKey"),
                Value(context.Node.NormalizedArguments, "includeStoredFacilities") == null || Flag(context.Node.NormalizedArguments, "includeStoredFacilities")); return true; }
            catch (InvalidOperationException e) when (e.Message == "invalid_build_source") { return false; }
        }
        private static bool IsEntitySource(GameState state, ItemInstanceState item, string source, int player, bool includeStored = true)
        {
            if (item == null) return false;
            var zone = state.ItemZones.Zones.Single(value => value.ZoneId == item.ZoneId);
            return source == "supply" ? zone.TemplateId == "core.zone.facility_supply_slot" || includeStored && IsActiveStoredFacility(state, zone, player) : source == "deck" ? zone.LocalZoneId == "facility_deck" : zone.TemplateId == "core.zone.box_reserve";
        }
        internal static bool IsActiveStoredFacility(GameState state, ZoneInstanceState zone, int player)
        {
            return FacilityBuildCandidateQuery.IsActiveStoredFacility(state, zone, player);
        }
        private static bool WithinCost(EffectExecutionContext context, ItemInstanceState item)
        {
            if (Flag(context.Node.NormalizedArguments, "excludeRainbow") && FacilityCardDatabase.Get(context.State, item?.DefinitionId)?.Color == "rainbow") return false;
            var bound = Value(context.Node.NormalizedArguments, "maxGoldVoucherCost");
            return bound == null || bound.Kind == NormalizedValueKind.Integer && bound.IntegerValue >= 0 &&
                FacilityCardDatabase.Get(context.State, item?.DefinitionId)?.GoldVoucherCost <= bound.IntegerValue;
        }
        private static EffectStepResult RefreshEntityCandidates(EffectExecutionContext context)
        {
            context.Node.FlowStage = "";
            Set(context.Node, "selectedItemIds", Strings(Array.Empty<string>()));
            context.Node.ContinuationData.Properties.RemoveAll(entry => entry.Name == "selectedSourceZoneId");
            return ExecuteEntity(context).AddLog(new EffectLogEntrySpec { PlayerId = context.Node.PlayerId, Message = "source_zone_changed；设施来源已变化，请重新选择。" });
        }
        private static EffectStepResult BuildEntityChild(EffectExecutionContext c, string itemId, string mode, bool reserve)
        {
            if (mode != "free" && mode != "resources" && mode != "gold") return EffectStepResult.Failed("invalid_build_payment");
            var spec = FacilityBuildEffectSpecFactory.Build(c.Node.PlayerId, itemId, -1, mode, reserve, c.Node.SourceId, Flag(c.Node.NormalizedArguments,"consumeMainAction"), Text(c.Node.ContinuationData, "selectedSourceZoneId"), Text(c.Node.NormalizedArguments, "sourceZone", "supply"), Text(c.Node.NormalizedArguments, "sourceKey"));
            var bound = Value(c.Node.NormalizedArguments, "maxGoldVoucherCost");
            if (bound != null) { spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "maxGoldVoucherCost", Value = bound.Clone() }); spec.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name)); }
            return EffectStepResult.Continue("built").AddChild(spec);
        }
        private static EffectStepResult Execute(EffectExecutionContext c)
        {
            if (ItemZoneLegacyProjection.IsActive(c.State)) return ExecuteEntity(c);
            string zone = Text(c.Node.NormalizedArguments, "sourceZone", "supply");
            if (zone != "supply" && zone != "reserve") return EffectStepResult.Failed("unsupported_build_source");
            bool reserve = zone == "reserve";
            var service = BuildFacilityService.CreateForEffectTree();
            string[] stage = (c.Node.FlowStage ?? "").Split('|');
            if (stage[0] == "built")
            {
                foreach (var child in c.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted) return EffectStepResult.Failed("build_child_failed");
                    if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待建设入场效果完成。");
                }
                return EffectStepResult.Completed();
            }
            if (stage[0] == "")
            {
                var cards = new List<string>();
                IEnumerable<string> source = reserve ? FacilityCardDatabase.ReserveIds : (IEnumerable<string>)c.State.Decks.FacilitySupply;
                foreach (string id in source)
                {
                    var definition = FacilityCardDatabase.Get(c.State, id);
                    string filter = Text(c.Node.NormalizedArguments, "effectId");
                    if (definition == null || (filter.Length > 0 && definition.EffectId != filter)) continue;
                    for (int slot = 0; slot < BuildFacilityService.CityBoardSlotCount; slot++)
                        if ((reserve ? service.ValidateReserveBuild(c.State, c.Node.PlayerId, id, slot) : service.Validate(c.State, c.Node.PlayerId, id, slot, "auto")).IsValid)
                        { cards.Add(id); break; }
                }
                return Wait(c, "card", "facility.entry.choose_additional_build", cards);
            }
            if (stage[0] == "card")
            {
                var answer = c.GetLatestInteractionAnswer();
                if (answer?.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue && (Flag(c.Node.NormalizedArguments, "allowDecline") || reserve))
                    return EffectStepResult.Completed(NormalizedValue.CreateString("player_declined"));
                string card = Answer(c);
                if (reserve && card == "choice.skip") return EffectStepResult.Completed();
                var modes = new List<string>();
                if (reserve) modes.Add("free");
                else foreach (string mode in new[] { "resources", "gold" })
                    if (service.ValidatePaymentMode(c.State, c.Node.PlayerId, card, mode).IsValid) modes.Add(mode);
                return Wait(c, "payment|" + Uri.EscapeDataString(card), "effect.build.choose_payment", modes);
            }
            if (stage[0] == "payment" && stage.Length == 2)
            {
                string card = Uri.UnescapeDataString(stage[1]), payment = Answer(c);
                var slots = new List<string>();
                for (int slot = 0; slot < BuildFacilityService.CityBoardSlotCount; slot++)
                    if ((reserve ? service.ValidateReserveBuild(c.State, c.Node.PlayerId, card, slot) : service.Validate(c.State, c.Node.PlayerId, card, slot, payment)).IsValid)
                        slots.Add("build-slot:" + slot.ToString(CultureInfo.InvariantCulture));
                return Wait(c, "slot|" + stage[1] + "|" + payment, "effect.build.choose_slot", slots);
            }
            if (stage[0] == "slot" && stage.Length == 3)
            {
                string answer = Answer(c);
                if (!answer.StartsWith("build-slot:", StringComparison.Ordinal) || !int.TryParse(answer.Substring(11), out int slot)) return EffectStepResult.Failed("invalid_build_slot");
                return EffectStepResult.Continue("built").AddChild(FacilityBuildEffectSpecFactory.Build(c.Node.PlayerId,
                    Uri.UnescapeDataString(stage[1]), slot, stage[2], reserve, c.Node.SourceId, false));
            }
            return EffectStepResult.Failed("invalid_build_selection_stage");
        }
    }
}
