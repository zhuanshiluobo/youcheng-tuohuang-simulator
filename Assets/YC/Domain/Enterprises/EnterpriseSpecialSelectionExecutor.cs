using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Enterprises
{
    public static class EnterpriseSpecialSelectionExecutor
    {
        public const string UpgradeThenActivateTypeId = "effect.enterprise.upgrade_then_activate";
        public const string ActivateAllTypeId = "effect.enterprise.activate_all_specials";
        public static void Register(EffectRegistry registry)
        {
            foreach (string typeId in new[] { LuaDomainEffectTypeIds.ActivateEnterpriseSpecial, UpgradeThenActivateTypeId, ActivateAllTypeId })
                if (!registry.TryGet(typeId, out _)) registry.Register(new EffectRegistration(typeId, Execute, EffectExecutorKind.IntrinsicFlow));
        }
        public static EffectSpec ActivateSpec(int playerId, int unlockedByPlayerId, string enterpriseItemId, string specialId) => EffectSpec.Create(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial,
            Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(playerId.ToString(CultureInfo.InvariantCulture)),
                "unlockedByPlayer", NormalizedValue.CreateString(unlockedByPlayerId.ToString(CultureInfo.InvariantCulture)), "enterpriseItemIds", Strings(new[] { enterpriseItemId }), "specialId", NormalizedValue.CreateString(specialId)), playerId);

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var invalid = EnterpriseEffectExecutor.CheckArguments(context); if (invalid != null) return invalid;
            var node = context.Node; bool upgrade = node.EffectTypeId == UpgradeThenActivateTypeId, all = node.EffectTypeId == ActivateAllTypeId;
            int unlockedByPlayerId = upgrade ? node.PlayerId : int.Parse(Text(node.NormalizedArguments, "unlockedByPlayer"), CultureInfo.InvariantCulture);
            if (context.State.FindPlayer(unlockedByPlayerId) == null) throw new InvalidOperationException("invalid_unlocking_player");
            var query = new EnterpriseStateQuery(context.Registry.EnterpriseDefinitions);
            if (node.FlowStage.Length == 0)
            {
                Freeze(context, query, unlockedByPlayerId, upgrade ? Number(node.NormalizedArguments, "levels") : 0);
                if (node.FrozenEnterpriseSpecials.Count == 0) return Finish(node, false, "no_candidates");
                if (all)
                {
                    var groups = node.FrozenEnterpriseSpecials.Select(value => value.Candidate.EnterpriseItemId).Distinct().ToList();
                    if (groups.Count > 1) return Request(context, "choose_group", "enterprise.special.group", groups, 1);
                    SelectGroup(node, groups[0]);
                    return RequestOrder(context);
                }
                string specialId = Text(node.NormalizedArguments, "specialId");
                if (specialId.Length > 0)
                {
                    var selected = node.FrozenEnterpriseSpecials.SingleOrDefault(value => value.Candidate.SpecialId == specialId);
                    if (selected == null) return EffectStepResult.Failed("enterprise_special_not_unlocked");
                    return Select(context, selected, upgrade);
                }
                return Request(context, "choose_special", "enterprise.special", node.FrozenEnterpriseSpecials.Select(value => value.Candidate.CandidateId), 1);
            }
            if (node.FlowStage == "choose_special")
            {
                string answer = Answer(context);
                var selected = node.FrozenEnterpriseSpecials.SingleOrDefault(value => value.Candidate.CandidateId == answer);
                if (selected == null) throw new InvalidOperationException("enterprise_special_answer_invalid");
                if (!StillLegal(context, query, selected, upgrade ? Number(node.NormalizedArguments, "levels") : 0, false))
                {
                    Freeze(context, query, unlockedByPlayerId, upgrade ? Number(node.NormalizedArguments, "levels") : 0);
                    if (node.FrozenEnterpriseSpecials.Count == 0) return Finish(node, false, "no_candidates");
                    return Request(context, "choose_special", "enterprise.special", node.FrozenEnterpriseSpecials.Select(value => value.Candidate.CandidateId), 1);
                }
                return Select(context, selected, upgrade);
            }
            if (node.FlowStage == "choose_group")
            {
                string selectedEnterpriseItemId = Answer(context);
                if (!node.FrozenEnterpriseSpecials.Any(value => value.Candidate.EnterpriseItemId == selectedEnterpriseItemId)) throw new InvalidOperationException("enterprise_special_group_invalid");
                SelectGroup(node, selectedEnterpriseItemId); return RequestOrder(context);
            }
            if (node.FlowStage == "choose_order")
            {
                var answer = context.GetLatestInteractionAnswer();
                var values = answer?.Kind == NormalizedValueKind.Array ? answer.Items : new List<NormalizedValue> { answer };
                var order = values.Select(value => value?.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value?.StringValue ?? "").ToList();
                if (order.Count != node.FrozenEnterpriseSpecials.Count || order.Distinct().Count() != order.Count || order.Any(id => !node.FrozenEnterpriseSpecials.Any(value => value.Candidate.CandidateId == id)))
                    throw new InvalidOperationException("enterprise_special_order_invalid");
                node.FrozenEnterpriseSpecials = order.Select(id => node.FrozenEnterpriseSpecials.Single(value => value.Candidate.CandidateId == id)).ToList();
                Set(node, "frozenSpecialIds", Strings(node.FrozenEnterpriseSpecials.Select(value => value.Candidate.SpecialId)));
                return EffectStepResult.Continue("resolve_frozen");
            }
            if (node.FlowStage == "resolve_frozen")
            {
                int nextIndex = Number(node.ContinuationData, "nextIndex", -1);
                if (nextIndex < 0 || nextIndex > node.FrozenEnterpriseSpecials.Count || node.ChildEffectIds.Count != nextIndex) throw new InvalidOperationException("enterprise_frozen_state_invalid");
                if (nextIndex == node.FrozenEnterpriseSpecials.Count) return Finish(node, context.ChildNodes.Any(child => Flag(child.NormalizedResult, "changed")));
                var selected = node.FrozenEnterpriseSpecials[nextIndex];
                if (!StillLegal(context, query, selected, 0, true)) return EffectStepResult.Failed("enterprise_special_no_longer_legal");
                // 借用范围属于快照的玩家；基础子式的 player 仍是发动者。
                var spec = ActivateSpec(node.PlayerId, selected.UnlockedByPlayerId, selected.Candidate.EnterpriseItemId, selected.Candidate.SpecialId);
                spec.StableKey = node.EffectId + ":frozen:" + nextIndex;
                Set(node, "nextIndex", NormalizedValue.CreateInteger(nextIndex + 1));
                return EffectStepResult.Continue("resolve_frozen").AddChild(spec);
            }
            if (node.FlowStage == "upgrade")
            {
                if (context.ChildNodes.Count != 1 || context.ChildNodes[0].Status != EffectNodeStatus.Completed) return EffectStepResult.Failed("enterprise_upgrade_failed");
                var selected = RequireSelection(node);
                if (!StillLegal(context, query, selected, 0, false)) return EffectStepResult.Failed("enterprise_special_no_longer_legal");
                return Activate(context, selected);
            }
            if (node.FlowStage == "activate")
            {
                var selected = RequireSelection(node);
                var failed = context.ChildNodes.FirstOrDefault(child => child.Status == EffectNodeStatus.Failed);
                var completed = Finish(node, failed == null, failed?.FailureReason ?? "");
                if (failed != null) completed = EffectStepResult.Failed(failed.FailureReason, completed.NormalizedResult).WithFlowStage("finished");
                return completed.AddEvent(new EffectEventRequest { EventId = StableIdFactory.Create("event", node.EffectId, "special_completed"),
                    EventType = "EnterpriseSpecialEffectCompleted", SourceEffectId = node.EffectId, PlayerId = node.PlayerId, TargetEntityId = selected.Candidate.EnterpriseItemId,
                    Payload = SelectionPayload(selected, node.PlayerId) });
            }
            if (node.FlowStage == "finished") return EffectStepResult.Completed(node.NormalizedResult);
            throw new InvalidOperationException("enterprise_special_stage_invalid");
        }
        private static FrozenEnterpriseSpecial RequireSelection(EffectNodeRuntimeState node)
        {
            if (node.FrozenEnterpriseSpecials.Count != 1 || Text(node.ContinuationData, "selectedEnterpriseItemId") != node.FrozenEnterpriseSpecials[0].Candidate.EnterpriseItemId ||
                Text(node.ContinuationData, "selectedSpecialId") != node.FrozenEnterpriseSpecials[0].Candidate.SpecialId) throw new InvalidOperationException("enterprise_selection_state_invalid");
            return node.FrozenEnterpriseSpecials[0];
        }
        private static void Freeze(EffectExecutionContext context, EnterpriseStateQuery query, int unlockedByPlayerId, int levelDelta)
        {
            var args = context.Node.NormalizedArguments;
            var requested = Value(args, "enterpriseItemIds")?.Items.Select(value => value.StringValue).ToList();
            var candidates = query.GetSpecialCandidates(context.State, context.Node.PlayerId, unlockedByPlayerId, levelDelta, requested)
                .Where(candidate => { var definition = query.RequireDefinition(context.State, candidate.EnterpriseItemId); int level = definition.Levels.Single(entry => entry.AbilityId == candidate.SpecialId).Level;
                    return level >= Number(args, "minLevel", 1) && level <= Number(args, "maxLevel", 5); });
            context.Node.FrozenEnterpriseSpecials = candidates.Select(candidate => new FrozenEnterpriseSpecial { Candidate = candidate, UnlockedByPlayerId = unlockedByPlayerId,
                EnterpriseDefinitionId = context.State.ItemZones.Items.Single(item => item.ItemId == candidate.EnterpriseItemId).DefinitionId }).ToList();
            // 已有 all 父节点的基础子式沿冻结范围，不用中途下降或上升重新判定解锁。
            var parent = context.State.EffectRuntime.EffectNodes.Find(value => value.EffectId == context.Node.ParentEffectId);
            if (parent?.EffectTypeId == ActivateAllTypeId)
            {
                var frozen = parent.FrozenEnterpriseSpecials.Single(value => value.Candidate.EnterpriseItemId == requested.Single() && value.Candidate.SpecialId == Text(args, "specialId"));
                context.Node.FrozenEnterpriseSpecials = new List<FrozenEnterpriseSpecial> { GameStateCloneService.DeepClone(frozen) };
            }
        }
        private static bool StillLegal(EffectExecutionContext context, EnterpriseStateQuery query, FrozenEnterpriseSpecial selected, int levelDelta, bool frozenRange)
        {
            var item = context.State.ItemZones.Items.SingleOrDefault(value => value.ItemId == selected.Candidate.EnterpriseItemId);
            if (item?.DefinitionId != selected.EnterpriseDefinitionId || !query.GetEnterpriseItemIds(context.State).Contains(item.ItemId)) return false;
            var definition = query.RequireDefinition(context.State, item.ItemId);
            var special = definition.Levels.SingleOrDefault(level => level.Kind == "special" && level.AbilityId == selected.Candidate.SpecialId);
            if (special == null || special.Level > selected.Candidate.EffectiveLevelCeiling) return false;
            return frozenRange || query.GetSpecialCandidates(context.State, context.Node.PlayerId, selected.UnlockedByPlayerId, levelDelta, new[] { item.ItemId }).Any(candidate => candidate.CandidateId == selected.Candidate.CandidateId);
        }
        private static EffectStepResult Select(EffectExecutionContext context, FrozenEnterpriseSpecial selected, bool upgrade)
        {
            var node = context.Node; node.FrozenEnterpriseSpecials = new List<FrozenEnterpriseSpecial> { selected };
            string selectedEnterpriseItemId = selected.Candidate.EnterpriseItemId, selectedSpecialId = selected.Candidate.SpecialId;
            Set(node, "selectedEnterpriseItemId", NormalizedValue.CreateString(selectedEnterpriseItemId)); Set(node, "selectedSpecialId", NormalizedValue.CreateString(selectedSpecialId));
            Set(node, "candidateLevelCeiling", NormalizedValue.CreateInteger(selected.Candidate.EffectiveLevelCeiling));
            if (upgrade) return EffectStepResult.Continue("upgrade").AddChild(EnterpriseEffectExecutor.UpgradeSpec(node.PlayerId, selectedEnterpriseItemId, Number(node.NormalizedArguments, "levels")));
            var parent = context.State.EffectRuntime.EffectNodes.Find(value => value.EffectId == node.ParentEffectId);
            if (!StillLegal(context, new EnterpriseStateQuery(context.Registry.EnterpriseDefinitions), selected, 0, parent?.EffectTypeId == ActivateAllTypeId)) return EffectStepResult.Failed("enterprise_special_no_longer_legal");
            return Activate(context, selected);
        }
        private static EffectStepResult Activate(EffectExecutionContext context, FrozenEnterpriseSpecial selected) => EffectStepResult.Continue("activate").AddEvent(new EffectEventRequest
        {
            EventId = StableIdFactory.Create("event", context.Node.EffectId, "special_activated"), EventType = "EnterpriseSpecialEffectActivated", SourceEffectId = context.Node.EffectId,
            TargetEntityId = selected.Candidate.EnterpriseItemId, PlayerId = context.Node.PlayerId, Payload = SelectionPayload(selected, context.Node.PlayerId)
        });
        private static NormalizedValue SelectionPayload(FrozenEnterpriseSpecial selected, int playerId) => Object("enterpriseItemId", NormalizedValue.CreateString(selected.Candidate.EnterpriseItemId),
            "specialId", NormalizedValue.CreateString(selected.Candidate.SpecialId), "abilityId", NormalizedValue.CreateString(selected.Candidate.SpecialId), "unlockedByPlayerId", NormalizedValue.CreateInteger(selected.UnlockedByPlayerId),
            "executingPlayerId", NormalizedValue.CreateInteger(playerId), "beneficiaryPlayerId", NormalizedValue.CreateInteger(playerId));
        private static void SelectGroup(EffectNodeRuntimeState node, string selectedEnterpriseItemId)
        {
            node.FrozenEnterpriseSpecials = node.FrozenEnterpriseSpecials.Where(value => value.Candidate.EnterpriseItemId == selectedEnterpriseItemId).ToList();
            Set(node, "selectedEnterpriseItemId", NormalizedValue.CreateString(selectedEnterpriseItemId)); Set(node, "nextIndex", NormalizedValue.CreateInteger(0));
            Set(node, "frozenSpecialIds", Strings(node.FrozenEnterpriseSpecials.Select(value => value.Candidate.SpecialId)));
        }
        private static EffectStepResult RequestOrder(EffectExecutionContext context)
        {
            if (context.Node.FrozenEnterpriseSpecials.Count == 1) return EffectStepResult.Continue("resolve_frozen");
            return Request(context, "choose_order", "enterprise.special.order", context.Node.FrozenEnterpriseSpecials.Select(value => value.Candidate.CandidateId), context.Node.FrozenEnterpriseSpecials.Count);
        }
        private static EffectStepResult Request(EffectExecutionContext context, string stage, string kind, IEnumerable<string> ids, int count)
        {
            var interaction = PrepareInteraction(context, new EffectInteractionSpec { InteractionTypeId = kind, AnsweringPlayerId = context.Node.PlayerId, Visibility = "owner", PromptKey = kind + ".choose",
                AnswerSchema = "candidate_id", MinSelections = count, MaxSelections = count }.WithCandidates(ids));
            var snapshots = interaction.CandidateIds.Select(id => context.Node.FrozenEnterpriseSpecials.First(value => kind == "enterprise.special.group" ? value.Candidate.EnterpriseItemId == id : value.Candidate.CandidateId == id)).ToList();
            interaction.PromptParameters = Object("candidateEnterpriseItemIds", Strings(snapshots.Select(value => value.Candidate.EnterpriseItemId)), "candidateSpecialIds", Strings(snapshots.Select(value => value.Candidate.SpecialId)),
                "candidateLevelCeilings", NormalizedValue.CreateArray(snapshots.Select(value => NormalizedValue.CreateInteger(value.Candidate.EffectiveLevelCeiling)).ToList()),
                "candidateCurrentLevels", NormalizedValue.CreateArray(snapshots.Select(value => NormalizedValue.CreateInteger(value.Candidate.CurrentLevel)).ToList()),
                "candidateDisplayNameKeys", Strings(snapshots.Select(value => value.Candidate.DisplayNameKey)));
            return EffectStepResult.Continue(stage).AddInteraction(interaction);
        }
        private static EffectStepResult Finish(EffectNodeRuntimeState node, bool changed, string reason = "")
        {
            var result = Object("changed", NormalizedValue.CreateBoolean(changed), "selectedEnterpriseItemId", NormalizedValue.CreateString(Text(node.ContinuationData, "selectedEnterpriseItemId")),
                "selectedSpecialId", NormalizedValue.CreateString(Text(node.ContinuationData, "selectedSpecialId")));
            if (reason.Length > 0) result.Properties.Add(new NormalizedValueEntry { Name = "reasonCode", Value = NormalizedValue.CreateString(reason) });
            result.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return EffectStepResult.Completed(result).WithFlowStage("finished");
        }
    }
}
