using System;
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
                AnsweringPlayerId = c.Node.PlayerId, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
            request.CandidateIds.AddRange(ids);
            return EffectStepResult.Continue(stage).AddInteraction(request);
        }
        private static EffectStepResult Execute(EffectExecutionContext c)
        {
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
                    var definition = FacilityCardDatabase.Get(id);
                    string filter = Text(c.Node.NormalizedArguments, "effectId");
                    if (definition == null || (filter.Length > 0 && definition.EffectId != filter)) continue;
                    for (int slot = 0; slot < BuildFacilityService.CityBoardSlotCount; slot++)
                        if ((reserve ? service.ValidateReserveBuild(c.State, c.Node.PlayerId, id, slot) : service.Validate(c.State, c.Node.PlayerId, id, slot, "auto")).IsValid)
                        { cards.Add(id); break; }
                }
                if (reserve) cards.Add("choice.skip");
                return Wait(c, "card", "facility.entry.choose_additional_build", cards);
            }
            if (stage[0] == "card")
            {
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
