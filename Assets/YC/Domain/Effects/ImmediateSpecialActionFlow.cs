using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // API-41 / 4.16.2、4.16.4：效果授权不生成额外主要行动额度。
    public static class ImmediateSpecialActionFlow
    {
        public const string TypeId = CharacterContentEffectExecutor.Prefix + "immediate_special";
        public const string Version = "2.1.0";
        public const string ResponseChildSelectionVersion = "immediate_special.response_children.v1.0.1";
        public const string EmptyArgumentsVersion = "immediate_special.empty_arguments.v1.0.1";
        // 内部零参效果沿用EffectSpec默认Null；空Object同样合法，其他非空实参仍拒绝。
        private static bool EmptyArguments(NormalizedValue args) => args == null || args.Kind == NormalizedValueKind.Null ||
            args.Kind == NormalizedValueKind.Object && args.Properties.Count == 0;

        // 业务选择与事件响应都是内核子节点；只在判定本流程唯一Choice时排除已受理响应。
        // 响应仍由内核按原顺序等待／结算，执行与恢复使用同一查询。
        private static List<EffectNodeRuntimeState> OwnChildren(GameState state, EffectNodeRuntimeState node) => node.ChildEffectIds
            .Where(id => !state.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(id)))
            .Select(id => state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)).ToList();

        internal static IEnumerable<AbilityActionOption> Sources(GameState state, EffectRegistry registry, int player)
        {
            if (!registry.HasAbilityCatalog)
            {
                var owner = state.FindPlayer(player);
                foreach (var marker in owner.DeclaredCityStyles.Where(m => m.MarkerArea.Length > 0 && m.UnlockedSpecialActionId.Length > 0 &&
                    m.RemainingSpecialActionUses > 0 && !owner.UsedSpecialActionIdsThisRound.Contains(m.UnlockedSpecialActionId)))
                {
                    var definition = YC.Domain.SpecialActions.SpecialActionDatabase.Get(state, marker.UnlockedSpecialActionId);
                    if (definition == null || definition.LocksCharacterCard && (owner.UsedCharacterThisTurn || state.CharacterUses.Any(u => u.ExecutingPlayerId == player && !u.Finished))) continue;
                    yield return new AbilityActionOption { OptionId = marker.InfluenceMarkerId, ActionId = marker.UnlockedSpecialActionId, TrackBindingId = marker.InfluenceMarkerId };
                }
                yield break;
            }
            // 普通／即时同一权威查询，只由原授权policy决定是否省主要行动额度。
            foreach (var option in registry.QuerySpecialActionSources(state, player, true).Where(o => o.Enabled))
            {
                var track = state.EffectRuntime.UsageTrackBindings.SingleOrDefault(b => b.BindingId == option.TrackBindingId);
                var marker = track == null ? null : UsageTracks.CurrentMarker(state, track);
                if (marker == null) continue;
                option.TrackBindingId = marker.ItemId;
                yield return option;
            }
        }

        internal static EffectSpec Build(int player, IList<AbilityActionOption> sources, Func<AbilityActionOption, EffectSpec> branchFactory = null, bool allowDecline = false)
        {
            var ids = new List<string>(); var branches = new List<List<EffectSpec>>(); var labels = new List<GameplayOptionPresentation>();
            if (sources.Count > 16)
            {
                // 权威树分层，不截断候选，也不提高Choice上限。
                int pageSize = 16;
                while ((sources.Count + pageSize - 1) / pageSize > 16) pageSize = checked(pageSize * 16);
                for (int first = 0; first < sources.Count; first += pageSize)
                {
                    string id = "page:" + first.ToString(CultureInfo.InvariantCulture); ids.Add(id);
                    branches.Add(new List<EffectSpec> { Build(player, sources.Skip(first).Take(pageSize).ToList(), branchFactory, allowDecline) });
                    labels.Add(new GameplayOptionPresentation { CandidateId = id, Label = new GameplayTextRef("core:interaction.choice.source_group",
                        new GameplayTextArgument { Name = "minimum", Kind = "integer", Value = NormalizedValue.CreateInteger(first + 1) },
                        new GameplayTextArgument { Name = "maximum", Kind = "integer", Value = NormalizedValue.CreateInteger(Math.Min(first + pageSize, sources.Count)) }) });
                }
            }
            else foreach (var source in sources)
            {
                ids.Add(source.OptionId);
                var activation = branchFactory?.Invoke(source) ?? CityStyleSpecialActionEffectSpecFactory.Activate(player, source.ActionId, source.TrackBindingId);
                if (branchFactory == null)
                {
                    activation.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "activationSource", Value = NormalizedValue.CreateString("immediate") });
                    activation.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                }
                activation.Presentation = source.DisplayNameRef == null ? null : new EffectPresentationDefinition { Started = source.DisplayNameRef.Clone() };
                branches.Add(new List<EffectSpec> { activation });
                if (source.DisplayNameRef != null) labels.Add(new GameplayOptionPresentation { CandidateId = source.OptionId, Label = source.DisplayNameRef.Clone(),
                    Description = new GameplayTextRef("core:interaction.special.action_details",
                        new GameplayTextArgument { Name = "cost", Kind = "text", Value = NormalizedValue.CreateString(source.EntryCostDescription ?? "") },
                        new GameplayTextArgument { Name = "body", Kind = "text", Value = NormalizedValue.CreateString(source.Description ?? "") },
                        new GameplayTextArgument { Name = "after", Kind = "text", Value = NormalizedValue.CreateString(source.EntryAfterEffectsDescription ?? "") }) });
            }
            var choice = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),
                "options", Strings(ids), "minSelections", NormalizedValue.CreateInteger(allowDecline ? 0 : 1), "maxSelections", NormalizedValue.CreateInteger(1),
                "interactionType", NormalizedValue.CreateString("effect.choice.execute")), player);
            choice.DecisionPlayerId = player; choice.NestedEffectGroups = branches;
            choice.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation {
                Prompt = new GameplayTextRef("core:interaction.special.choose_source"), Options = labels } };
            return choice;
        }

        public static EffectStepResult Execute(EffectExecutionContext context)
        {
            var node = context.Node;
            if (!EmptyArguments(node.NormalizedArguments) ||
                context.State.FindPlayer(node.PlayerId) == null) return EffectStepResult.Failed("immediate_special_arguments_invalid");
            if (node.FlowStage == "")
            {
                var sources = Sources(context.State, context.Registry, node.PlayerId).OrderBy(s => s.SortOrder).ThenBy(s => s.OptionId, StringComparer.Ordinal).ToList();
                if (sources.Count == 0) return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(false),
                    "outcome", NormalizedValue.CreateString("no_legal_target"))).WithFlowStage("empty");
                var choice = Build(node.PlayerId, sources); choice.StableKey = "special-sources";
                return EffectStepResult.Continue("executing").AddChild(choice);
            }
            if (node.FlowStage != "executing") return EffectStepResult.Failed("immediate_special_stage_invalid");
            var children = OwnChildren(context.State, node);
            if (children.Count != 1 || children[0].EffectTypeId != LuaDomainEffectTypeIds.Choice) return EffectStepResult.Failed("immediate_special_child_invalid");
            var child = children[0];
            if (child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed) return EffectStepResult.NoProgress("等待所选特殊行动及响应完成。");
            var result = Object("outcome", NormalizedValue.CreateString(child.Status == EffectNodeStatus.Completed ? "completed" : "failed"),
                "choiceEffectId", NormalizedValue.CreateString(child.EffectId));
            return (child.Status == EffectNodeStatus.Failed ? EffectStepResult.Failed(child.FailureReason, result) : EffectStepResult.Completed(result)).WithFlowStage("finished");
        }

        private static bool Matches(EffectSpecRuntimeState spec, EffectNodeRuntimeState node) => spec.EffectTypeId == node.EffectTypeId && spec.PlayerId == node.PlayerId &&
            (spec.DecisionPlayerId >= 0 ? spec.DecisionPlayerId : spec.PlayerId) == node.DecisionPlayerId && ItemTransferService.MovementV4Digest(new { spec.NormalizedArguments, spec.NestedGroupSizes, spec.NestedEffects }) ==
            ItemTransferService.MovementV4Digest(new { node.NormalizedArguments, node.NestedGroupSizes, node.NestedEffects });

        // 仅承认本流程真实封存并选中的Choice分支，不因插入任意Choice而扩大旧桥授权。
        public static bool OwnsActivation(GameState state, EffectNodeRuntimeState activation)
        {
            if (activation?.EffectTypeId != CityStyleSpecialActionEffectTypeIds.Activate || Text(activation.NormalizedArguments, "activationSource") != "immediate") return false;
            var child = activation; var seen = new HashSet<string>();
            while (seen.Add(child.EffectId))
            {
                var parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == child.ParentEffectId && n.PlayerId == activation.PlayerId);
                if (parent == null || !parent.ChildEffectIds.Contains(child.EffectId)) return false;
                if (parent.EffectTypeId == TypeId) return parent.DefinitionVersion == Version && (parent.FlowStage == "executing" || parent.FlowStage == "finished") && child.EffectTypeId == LuaDomainEffectTypeIds.Choice;
                if (parent.EffectTypeId != LuaDomainEffectTypeIds.Choice || parent.FlowStage != "branch_completed" || parent.NormalizedResult?.Kind != NormalizedValueKind.String) return false;
                var options = Value(parent.NormalizedArguments, "options");
                int branch = options?.Items.FindIndex(v => v.StringValue == parent.NormalizedResult.StringValue) ?? -1;
                if (branch < 0 || branch >= parent.NestedGroupSizes.Count || parent.NestedGroupSizes[branch] != 1) return false;
                int offset = parent.NestedGroupSizes.Take(branch).Sum();
                if (!Matches(parent.NestedEffects[offset], child)) return false;
                child = parent;
            }
            return false;
        }

        public static bool ValidateSaved(GameState state, EffectNodeRuntimeState node)
        {
            if (node.EffectTypeId != TypeId) return true;
            if (node.DefinitionVersion != Version || !EmptyArguments(node.NormalizedArguments) ||
                state.FindPlayer(node.PlayerId) == null || state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == node.EffectId)) return false;
            var children = OwnChildren(state, node);
            if (node.FlowStage == "" || node.FlowStage == "empty") return children.Count == 0;
            return (node.FlowStage == "executing" || node.FlowStage == "finished") && children.Count == 1 && children[0].EffectTypeId == LuaDomainEffectTypeIds.Choice &&
                children[0].PlayerId == node.PlayerId && (node.FlowStage != "finished" || children[0].Status == EffectNodeStatus.Completed || children[0].Status == EffectNodeStatus.Failed);
        }
    }
}
