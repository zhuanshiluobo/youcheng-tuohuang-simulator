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
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, "1.0.0"));
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
            string source = Text(c.Node.NormalizedArguments, "sourceZone", "supply");
            if (source != "supply" && source != "deck" && source != "reserve") return EffectStepResult.Failed("unsupported_build_source");
            bool free = source == "reserve" || Text(c.Node.NormalizedArguments, "paymentMode") == "free";
            if (c.Node.FlowStage == "built")
            {
                if (c.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed)) return EffectStepResult.Failed("build_child_failed");
                var id = Value(c.Node.ContinuationData, "selectedItemIds")?.Items.FirstOrDefault()?.StringValue;
                if (string.IsNullOrEmpty(id) || !c.State.ItemZones.Items.Any(i => i.ItemId == id && c.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.city_facility_cell")))
                    return EffectStepResult.Failed("build_result_missing");
                return EffectStepResult.Completed(Object("outcome", NormalizedValue.CreateString("built"), "builtItemId", NormalizedValue.CreateString(id)));
            }
            if (c.Node.FlowStage.Length == 0)
            {
                var snapshot = c.Registry.FacilityBuildCandidates.Resolve(c.State, c.Node.PlayerId,
                    new FacilityBuildCandidateOptions { SourceZone = source, IncludeStoredFacilities = includeStored == null || Flag(c.Node.NormalizedArguments, "includeStoredFacilities"),
                        EffectId = Text(c.Node.NormalizedArguments, "effectId"), ExcludeRainbow = Flag(c.Node.NormalizedArguments, "excludeRainbow"),
                        MaxGoldVoucherCost = Value(c.Node.NormalizedArguments, "maxGoldVoucherCost") == null ? (int?)null : Number(c.Node.NormalizedArguments, "maxGoldVoucherCost"),
                        PaymentMode = Text(c.Node.NormalizedArguments, "paymentMode", "auto"), RequireBuildable = true }, c.Node.EffectId, c.State.EffectRuntime.StateRevision + 1);
                return WaitEntityCards(c, snapshot, Flag(c.Node.NormalizedArguments, "allowDecline") || source == "reserve");

            }
            if (c.Node.FlowStage == "card")
            {
                var answer = c.GetLatestInteractionAnswer();
                if (answer?.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue && (Flag(c.Node.NormalizedArguments, "allowDecline") || source == "reserve"))
                    return EffectStepResult.Completed(NormalizedValue.CreateString("player_declined"));
                string selected = Answer(c);
                if (selected == "choice.skip") return EffectStepResult.Completed(NormalizedValue.CreateString("player_declined"));
                var item = new ItemZoneQuery().FindItem(c.State, selected);
                if (!IsEntitySource(c.State, item, source, c.Node.PlayerId, Value(c.Node.NormalizedArguments, "includeStoredFacilities") == null || Flag(c.Node.NormalizedArguments, "includeStoredFacilities")) || !WithinCost(c, item)) return RefreshEntityCandidates(c);
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
                if (!IsEntitySource(c.State, new ItemZoneQuery().FindItem(c.State, selected), source, c.Node.PlayerId, Value(c.Node.NormalizedArguments, "includeStoredFacilities") == null || Flag(c.Node.NormalizedArguments, "includeStoredFacilities")) || !WithinCost(c, new ItemZoneQuery().FindItem(c.State, selected))) return RefreshEntityCandidates(c);
                return BuildEntityChild(c, selected, Answer(c), false);
            }
            throw new InvalidOperationException("build_selection_stage_invalid");
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
            var spec = FacilityBuildEffectSpecFactory.Build(c.Node.PlayerId, itemId, -1, mode, reserve, c.Node.SourceId, false, Text(c.Node.ContinuationData, "selectedSourceZoneId"));
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
