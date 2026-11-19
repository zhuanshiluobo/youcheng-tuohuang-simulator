using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Rules
{
    /// <summary>准备合同的共同固定节点与可恢复流程。</summary>
    public static class PreparationFrameworkFlow
    {
        public const string TypeId = "setup.preparation.framework";
        public const string SelectionTypeId = "setup.preparation.enterprise_selection";
        public const string GroupKey = "initial_enterprise_selections";
        public const string Version = "1.0.0";

        public static void Register(EffectRegistry registry, Func<int, int> hostNext = null)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            PreparationWindowFlow.Register(registry);
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId,
                context => Execute(context, hostNext ?? (max => GameplayRandomRuntime.Next(context.State, GameplayRandomRuntime.EnterprisePreparation, max))),
                EffectExecutorKind.IntrinsicFlow, Version));
            if (!registry.TryGet(SelectionTypeId, out _)) registry.Register(new EffectRegistration(SelectionTypeId,
                Select, EffectExecutorKind.IntrinsicFlow, Version));
        }

        public static EffectSpec Spec(int player) => EffectSpec.Create(TypeId, playerId: player);

        private static List<ItemInstanceState> Enterprises(GameState state, EffectRegistry registry) => state.ItemZones.Items
            .Where(item => registry == null ? state.ContentDefinitions?.EnterpriseDefinitionIds?.Contains(item.DefinitionId) == true :
                registry.EnterpriseDefinitions.FindEnterprise(item.DefinitionId) != null)
            .OrderBy(item => item.ItemId, StringComparer.Ordinal).ToList();

        private static List<string> Chosen(PreparationEnterpriseDistributionRecord record) =>
            record.Assignments.SelectMany(item => item.SelectedItemIds).Concat(record.Automatic.Select(item => item.ItemId))
                .Concat(record.ExtraItemIds).ToList();

        private static int Owner(PreparationEnterpriseDistributionRecord record, string itemId)
        {
            var assignment = record.Assignments.SingleOrDefault(item => item.SelectedItemIds.Contains(itemId));
            return assignment != null ? assignment.PlayerId : record.Automatic.SingleOrDefault(item => item.ItemId == itemId)?.InitialOwnerPlayerId ?? -1;
        }

        private static bool ContextValid(EffectExecutionContext context, out PreparationFlowPlan plan, out PreparationRuntimeState runtime)
        {
            plan = context.State.SetupRecipe?.Preparation;
            runtime = context.State.PreparationRuntime;
            if (plan == null || context.State.EffectRuntime?.PlayerOrderSnapshot == null ||
                context.State.EffectRuntime.PlayerOrderSnapshot.Count < 2 || context.State.EffectRuntime.PlayerOrderSnapshot.Count > 4 ||
                context.Node.PlayerId != context.State.EffectRuntime.PlayerOrderSnapshot[0] ||
                context.State.RuleConfiguration?.RuleModeId != plan.ModeId || string.IsNullOrWhiteSpace(context.State.ContentPackHash) ||
                (plan.Enterprise != null) != context.State.SetupRecipe.EnterpriseEntrance) return false;
            try { plan.Validate(); } catch (InvalidOperationException) { return false; }
            return runtime == null || runtime.Version == 1 && runtime.OwnerEffectId == context.Node.EffectId &&
                runtime.ModeId == plan.ModeId && runtime.ContentHash == context.State.ContentPackHash;
        }

        private static EffectStepResult Execute(EffectExecutionContext context, Func<int, int> next)
        {
            if (!ContextValid(context, out var plan, out var runtime)) return EffectStepResult.Failed("preparation_framework_identity_invalid");
            var node = context.Node;
            if (node.FlowStage == string.Empty)
            {
                if (runtime != null || node.ChildEffectIds.Count != 0) return EffectStepResult.Failed("preparation_framework_duplicate");
                context.State.PreparationRuntime = new PreparationRuntimeState { OwnerEffectId = node.EffectId,
                    ModeId = plan.ModeId, ContentHash = context.State.ContentPackHash, Stage = "before" };
                return EffectStepResult.Continue("before").AddChild(PreparationWindowFlow.WindowSpec(PreparationWindowFlow.Before, node.PlayerId));
            }
            if (runtime == null) return EffectStepResult.Failed("preparation_framework_state_missing");
            if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted))
                return EffectStepResult.Failed("preparation_framework_child_failed");
            if (context.ChildNodes.Any(child => child.Status != EffectNodeStatus.Completed))
                return EffectStepResult.NoProgress("等待准备阶段子流程完成。");
            if (node.FlowStage == "before")
            {
                if (runtime.Stage != "before" || runtime.Distribution != null || runtime.EffectiveEnterprise != null) return EffectStepResult.Failed("preparation_framework_state_invalid");
                if (plan.Enterprise == null)
                {
                    runtime.Stage = "after";
                    return EffectStepResult.Continue("after").AddChild(PreparationWindowFlow.WindowSpec(PreparationWindowFlow.After, node.PlayerId));
                }
                var eligible = Enterprises(context.State, context.Registry);
                string reserve = FindZone(context.State, "reserve").ZoneId;
                if (eligible.Any(item => item.ZoneId != reserve)) return EffectStepResult.Failed("preparation_enterprise_source_invalid");
                if (!PreparationOutputResolver.TryResolve(context.State, plan, node.EffectId, out var effective, out string outputReason))
                    return EffectStepResult.Failed(outputReason);
                var count = effective.PlayerCounts.SingleOrDefault(rule => rule.PlayerCount == context.State.EffectRuntime.PlayerOrderSnapshot.Count);
                if (count == null) return EffectStepResult.Failed("preparation_distribution_player_count_missing");
                foreach (int player in context.State.EffectRuntime.PlayerOrderSnapshot)
                    if (PlayerAreaPolicy.Supply(context.State, player, "core.player_marker").Count < count.FinalCount)
                        return EffectStepResult.Failed("preparation_enterprise_markers_insufficient");
                runtime.EffectiveEnterprise = effective;
                runtime.Distribution = PreparationEnterpriseDistribution.Create(effective, node.EffectId, plan.ModeId,
                    context.State.ContentPackHash, context.State.EffectRuntime.PlayerOrderSnapshot, eligible, next);
                runtime.Stage = "middle";
                return EffectStepResult.Continue("middle").AddChild(PreparationWindowFlow.WindowSpec(PreparationWindowFlow.Middle, node.PlayerId));
            }
            if (node.FlowStage == "middle")
            {
                if (runtime.Stage != "middle" || !ValidDistribution(context.State, context.Registry, runtime)) return EffectStepResult.Failed("preparation_distribution_invalid");
                var count = runtime.EffectiveEnterprise.PlayerCounts.Single(rule => rule.PlayerCount == runtime.Distribution.PlayerOrder.Count);
                var tasks = runtime.Distribution.Assignments.Select(assignment => {
                    var spec = EffectSpec.Create(SelectionTypeId, Object("candidateEnterpriseItemIds", Strings(assignment.CandidateItemIds),
                        "requiredEnterpriseItemIds", Strings(assignment.RequiredItemIds), "keepCount", NormalizedValue.CreateInteger(count.Keep)), assignment.PlayerId);
                    spec.StableKey = "preparation-enterprise:" + assignment.PlayerId.ToString(CultureInfo.InvariantCulture);
                    spec.Visibility = GameStateVisibilityPolicy.Owner;
                    return spec;
                }).ToList();
                runtime.Stage = "confirming";
                return EffectStepResult.Continue("confirming").AddIndependentChildren(GroupKey, tasks);
            }
            if (node.FlowStage == "confirming")
            {
                if (runtime.Stage != "confirming" || !ValidDistribution(context.State, context.Registry, runtime) ||
                    runtime.Distribution.Assignments.Any(item => !item.Confirmed)) return EffectStepResult.Failed("preparation_confirmation_incomplete");
                var boards = Chosen(runtime.Distribution);
                var count = runtime.EffectiveEnterprise.PlayerCounts.Single(rule => rule.PlayerCount == runtime.Distribution.PlayerOrder.Count);
                if (boards.Count != count.FinalCount || boards.Distinct(StringComparer.Ordinal).Count() != boards.Count)
                    return EffectStepResult.Failed("preparation_enterprise_count_invalid");
                Publish(context, boards, runtime.Distribution);
                runtime.Stage = "initializing";
                return EffectStepResult.Continue("departments");
            }
            if (node.FlowStage == "departments" || node.FlowStage == "awaiting_department")
            {
                if (runtime.Stage != "initializing" || !ValidDistribution(context.State, context.Registry, runtime)) return EffectStepResult.Failed("preparation_distribution_invalid");
                var boards = Chosen(runtime.Distribution);
                var pending = boards.Select(id => context.State.ItemZones.Items.Single(item => item.ItemId == id))
                    .FirstOrDefault(item => context.Registry.EnterpriseDefinitions.FindEnterprise(item.DefinitionId)?.DepartmentRewardPolicy == "active_department" &&
                        !context.State.ItemZones.Relations.Any(relation => relation.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation && relation.SourceItemId == item.ItemId));
                if (pending != null)
                {
                    var definition = context.Registry.EnterpriseDefinitions.FindEnterprise(pending.DefinitionId);
                    var departments = context.State.ItemZones.Items.Where(item =>
                        context.Registry.EnterpriseDefinitions.FindDepartment(item.DefinitionId)?.EnterpriseRuntimeId == definition.RuntimeId).ToList();
                    if (departments.Count != 3) return EffectStepResult.Failed("content_not_ready");
                    foreach (var item in departments) ItemZoneStateMigrator.SetPosition(context.State, item, FindZone(context.State, "departments").ZoneId);
                    int owner = Owner(runtime.Distribution, pending.ItemId);
                    string selected;
                    if (owner < 0) selected = departments[next(departments.Count)].ItemId;
                    else
                    {
                        if (node.FlowStage == "departments") return Choose(context, "awaiting_department", "setup.department.choose", owner, departments.Select(item => item.ItemId), 1);
                        selected = Answer(context);
                        if (!departments.Any(item => item.ItemId == selected)) return EffectStepResult.Failed("department_choice_invalid");
                    }
                    context.State.ItemZones.Relations.Add(new ItemRelationState { RelationId = StableIdFactory.Create("relation", pending.ItemId, EnterpriseStateQuery.ActiveDepartmentRelation),
                        RelationTypeId = EnterpriseStateQuery.ActiveDepartmentRelation, SourceItemId = pending.ItemId, TargetItemId = selected });
                    return EffectStepResult.Continue("departments");
                }
                bool operators = ExpansionSetupFlow.ActivateConditionalComponents(context, boards);
                // 冻结本次是否实际启用了干员牌堆，供恢复时核验洗牌节点；不能靠当前牌堆位置反推历史。
                Set(node, "initialOperatorShuffleRequired", NormalizedValue.CreateBoolean(operators));
                var result = EffectStepResult.Continue("initial_upgrades");
                if (operators)
                {
                    var shuffle = EffectSpec.Create(DeckShuffleEffectExecutor.TypeId,
                        Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(node.PlayerId.ToString(CultureInfo.InvariantCulture)),
                            "deckZone", Object("zoneId", NormalizedValue.CreateString(FindZone(context.State, "operator_deck").ZoneId))), node.PlayerId);
                    shuffle.StableKey = "initial_operator_deck_shuffle";
                    result.AddChild(shuffle);
                }
                foreach (string board in boards)
                {
                    int player = Owner(runtime.Distribution, board);
                    if (player > 0) result.AddChild(EnterpriseEffectExecutor.UpgradeSpec(player, board, 1));
                }
                return result;
            }
            if (node.FlowStage == "initial_upgrades")
            {
                if (runtime.Stage != "initializing") return EffectStepResult.Failed("preparation_framework_state_invalid");
                runtime.Stage = "after";
                return EffectStepResult.Continue("after").AddChild(PreparationWindowFlow.WindowSpec(PreparationWindowFlow.After, node.PlayerId));
            }
            if (node.FlowStage != "after" || runtime.Stage != "after") return EffectStepResult.Failed("preparation_framework_stage_invalid");
            runtime.Stage = "complete";
            return EffectStepResult.Completed().WithFlowStage("finished");
        }

        private static EffectStepResult Choose(EffectExecutionContext context, string stage, string type, int player, IEnumerable<string> ids, int count, IReadOnlyList<string> required = null)
        {
            var interaction = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = player, Visibility = "owner", PromptKey = type,
                AnswerSchema = "candidate_ids", MinSelections = count, MaxSelections = count, AllowDecline = false };
            if (required != null) interaction.PromptParameters = Object("requiredEnterpriseItemIds", Strings(required));
            interaction.CandidateIds.AddRange(ids.OrderBy(id => id, StringComparer.Ordinal));
            return EffectStepResult.Continue(stage).AddInteraction(PrepareInteraction(context, interaction));
        }

        private static bool ValidDistribution(GameState state, EffectRegistry registry, PreparationRuntimeState runtime, IReadOnlyList<int> expectedOrder = null)
        {
            if (runtime?.EffectiveEnterprise == null ||
                !PreparationOutputResolver.TryResolve(state, state.SetupRecipe?.Preparation, runtime.OwnerEffectId, out var expected, out _, expectedOrder) ||
                !GameStateCloneService.AreEquivalentPersistentValues(expected, runtime.EffectiveEnterprise, typeof(PreparationEnterprisePlan))) return false;
            var enterprises = Enterprises(state, registry);
            // 中间步骤及逐人确认期间只能查看分发记录；不能提前移动企业、公开候选或暗中换牌。
            if ((runtime.Stage == "middle" || runtime.Stage == "confirming") &&
                enterprises.Any(item => item.ZoneId != FindZone(state, "reserve").ZoneId)) return false;
            return PreparationEnterpriseDistribution.Validate(runtime.EffectiveEnterprise, runtime.Distribution, enterprises,
                runtime.OwnerEffectId, runtime.ModeId, runtime.ContentHash, expectedOrder ?? state.EffectRuntime.PlayerOrderSnapshot, out _);
        }

        private static EffectStepResult Select(EffectExecutionContext context)
        {
            var node = context.Node;
            var runtime = context.State.PreparationRuntime;
            var parent = context.State.EffectRuntime.EffectNodes.Find(item => item.EffectId == node.ParentEffectId);
            var group = parent?.ChildSchedulingGroups.SingleOrDefault(item => item.StableGroupKey == GroupKey);
            if (runtime == null || runtime.Stage != "confirming" || parent?.EffectTypeId != TypeId || parent.FlowStage != "confirming" ||
                runtime.OwnerEffectId != parent.EffectId || group == null || !group.ChildEffectIds.Contains(node.EffectId) ||
                !parent.ChildEffectIds.Contains(node.EffectId) || !ValidDistribution(context.State, context.Registry, runtime))
                return EffectStepResult.Failed("preparation_confirmation_authority_invalid");
            var assignment = runtime.Distribution.Assignments.SingleOrDefault(item => item.PlayerId == node.PlayerId);
            var count = runtime.EffectiveEnterprise.PlayerCounts.Single(rule => rule.PlayerCount == runtime.Distribution.PlayerOrder.Count);
            if (assignment == null || assignment.Confirmed || !ReadIds(Value(node.NormalizedArguments, "candidateEnterpriseItemIds")).SequenceEqual(assignment.CandidateItemIds) ||
                !ReadIds(Value(node.NormalizedArguments, "requiredEnterpriseItemIds")).SequenceEqual(assignment.RequiredItemIds) || Number(node.NormalizedArguments, "keepCount") != count.Keep)
                return EffectStepResult.Failed("preparation_confirmation_assignment_invalid");
            if (node.FlowStage == string.Empty)
                return Choose(context, "awaiting_selection", ExpansionSetupFlow.EnterpriseChoiceInteraction, node.PlayerId, assignment.CandidateItemIds, count.Keep, assignment.RequiredItemIds);
            if (node.FlowStage != "awaiting_selection") return EffectStepResult.Failed("preparation_confirmation_stage_invalid");
            var selected = ReadAnswer(context.GetLatestInteractionAnswer());
            if (!PreparationEnterpriseDistribution.TryConfirm(runtime.EffectiveEnterprise, runtime.Distribution,
                Enterprises(context.State, context.Registry), parent.EffectId, runtime.ModeId, runtime.ContentHash,
                context.State.EffectRuntime.PlayerOrderSnapshot, node.PlayerId, selected, out string diagnostic)) return EffectStepResult.Failed(diagnostic);
            return EffectStepResult.Completed(Object("selectedEnterpriseItemIds", Strings(selected))).WithFlowStage("finished");
        }

        private static List<string> ReadIds(NormalizedValue value) => value?.Kind == NormalizedValueKind.Array
            ? value.Items.Select(item => item.Kind == NormalizedValueKind.StableReference ? item.ReferenceId : item.StringValue).ToList() : new List<string>();
        private static List<string> ReadAnswer(NormalizedValue answer) => answer?.Kind == NormalizedValueKind.Array ? ReadIds(answer) :
            answer?.Kind == NormalizedValueKind.String ? new List<string> { answer.StringValue } :
            answer?.Kind == NormalizedValueKind.StableReference ? new List<string> { answer.ReferenceId } : new List<string>();

        public static bool TryValidateConfirmationAnswer(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner,
            InteractionRequest request, NormalizedValue answer, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (owner?.EffectTypeId != SelectionTypeId) return true;
            diagnostic = "企业选择无效，请选择所有必选企业。";
            var runtime = state.PreparationRuntime;
            if (runtime == null || state.SetupRecipe?.Preparation?.Enterprise == null || registry == null ||
                runtime.Stage != "confirming" || request?.InteractionTypeId != ExpansionSetupFlow.EnterpriseChoiceInteraction ||
                request.OwnerEffectId != owner.EffectId || request.AnsweringPlayerId != owner.PlayerId || owner.FlowStage != "awaiting_selection") return false;
            var parent = state.EffectRuntime.EffectNodes.Find(item => item.EffectId == owner.ParentEffectId);
            if (parent?.EffectTypeId != TypeId || parent.EffectId != runtime.OwnerEffectId || parent.FlowStage != "confirming" ||
                !parent.ChildSchedulingGroups.Any(group => group.StableGroupKey == GroupKey && group.ChildEffectIds.Contains(owner.EffectId))) return false;
            var clone = GameStateCloneService.DeepClone(runtime.Distribution);
            bool valid = ValidDistribution(state, registry, runtime) && PreparationEnterpriseDistribution.TryConfirm(runtime.EffectiveEnterprise, clone,
                Enterprises(state, registry), parent.EffectId, state.SetupRecipe.Preparation.ModeId, state.ContentPackHash,
                state.EffectRuntime.PlayerOrderSnapshot, owner.PlayerId, ReadAnswer(answer), out _);
            if (valid) diagnostic = string.Empty;
            return valid;
        }

        private static void Publish(EffectExecutionContext context, List<string> boards, PreparationEnterpriseDistributionRecord record)
        {
            foreach (int player in record.PlayerOrder)
                if (PlayerAreaPolicy.Supply(context.State, player, "core.player_marker").Count < boards.Count)
                    throw new InvalidOperationException("preparation_enterprise_markers_insufficient");
            foreach (var assignment in record.Assignments)
                foreach (string id in assignment.CandidateItemIds.Where(id => !assignment.SelectedItemIds.Contains(id)))
                {
                    var item = context.State.ItemZones.Items.Single(value => value.ItemId == id);
                    item.ControllerSubject = ItemSubjectRef.Player(assignment.PlayerId);
                    ItemZoneStateMigrator.SetPosition(context.State, item, FindZone(context.State, "secret_retired").ZoneId);
                }
            foreach (string id in boards)
            {
                var board = context.State.ItemZones.Items.Single(item => item.ItemId == id);
                ItemZoneStateMigrator.SetPosition(context.State, board, FindZone(context.State, "enterprises").ZoneId);
                // 创建卡槽会发布状态副本并更新原列表；迭代冻结的席位数组避免枚举中被修改。
                foreach (int player in record.PlayerOrder.ToArray())
                {
                    var declaration = new CardZoneDefinition { LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track",
                        AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "unbounded",
                        OrderingKind = ZoneOrderingKind.Track, VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.explicit_transfer",
                        ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                    if (!new ItemZoneStateFactory().TryCreateCardZone(context.State, id, declaration, out string zone, out string reason)) throw new InvalidOperationException(reason);
                    string markerId = PlayerAreaPolicy.Supply(context.State, player, "core.player_marker").First().ItemId;
                    var marker = context.State.ItemZones.Items.Single(item => item.ItemId == markerId);
                    ItemZoneStateMigrator.SetPosition(context.State, marker, zone); marker.SlotId = "level_0";
                }
            }
        }

        private static bool ValidInitializationChildren(GameState state, EffectNodeRuntimeState root,
            IReadOnlyList<EffectNodeRuntimeState> children, PreparationEnterpriseDistributionRecord distribution, bool hasAfter)
        {
            int next = 2 + distribution.PlayerOrder.Count;
            int end = children.Count - (hasAfter ? 1 : 0);
            if (end < next || hasAfter && (children[children.Count - 1].EffectTypeId != PreparationWindowFlow.WindowTypeId ||
                Text(children[children.Count - 1].NormalizedArguments, "window") != PreparationWindowFlow.After)) return false;
            bool scheduled = root.FlowStage == "initial_upgrades" || hasAfter;
            var shuffleRequired = Value(root.ContinuationData, "initialOperatorShuffleRequired");
            if (!scheduled) return shuffleRequired == null && next == end;
            if (shuffleRequired?.Kind != NormalizedValueKind.Boolean) return false;
            if (shuffleRequired.BooleanValue)
            {
                if (next >= end) return false;
                var shuffle = children[next];
                string expectedId = StableIdFactory.Create("effect", root.EffectId, "child", "child_effect",
                    next.ToString(CultureInfo.InvariantCulture), DeckShuffleEffectExecutor.TypeId, "initial_operator_deck_shuffle");
                if (shuffle.EffectId != expectedId || shuffle.EffectTypeId != DeckShuffleEffectExecutor.TypeId ||
                    shuffle.PlayerId != root.PlayerId || Number(shuffle.NormalizedArguments, "apiVersion", -1) != 1 ||
                    Text(shuffle.NormalizedArguments, "player") != root.PlayerId.ToString(CultureInfo.InvariantCulture) ||
                    Text(Value(shuffle.NormalizedArguments, "deckZone"), "zoneId") != FindZone(state, "operator_deck").ZoneId ||
                    hasAfter && shuffle.Status != EffectNodeStatus.Completed) return false;
                next++;
            }
            foreach (string board in Chosen(distribution))
            {
                int player = Owner(distribution, board);
                if (player < 1) continue;
                if (next >= end) return false;
                var upgrade = children[next];
                string expectedId = StableIdFactory.Create("effect", root.EffectId, "child", "child_effect",
                    next.ToString(CultureInfo.InvariantCulture), LuaDomainEffectTypeIds.UpgradeEnterprise, string.Empty);
                if (upgrade.EffectId != expectedId || upgrade.EffectTypeId != LuaDomainEffectTypeIds.UpgradeEnterprise ||
                    upgrade.PlayerId != player || Number(upgrade.NormalizedArguments, "apiVersion", -1) != 1 ||
                    Text(upgrade.NormalizedArguments, "player") != player.ToString(CultureInfo.InvariantCulture) ||
                    Text(upgrade.NormalizedArguments, "enterpriseItemId") != board || Number(upgrade.NormalizedArguments, "levels", -1) != 1 ||
                    hasAfter && upgrade.Status != EffectNodeStatus.Completed) return false;
                next++;
            }
            return next == end;
        }

        /// <summary>恢复时校验窗口、分发、作答及子任务身份；不能从模式声明重新抽牌。</summary>
        public static bool TryValidateRuntime(GameState state, EffectRegistry registry, out string diagnostic, bool requireMainline = true)
        {
            diagnostic = "preparation_framework_recovery_invalid";
            var nodes = state?.EffectRuntime?.EffectNodes;
            if (nodes == null) return false;
            var roots = nodes.Where(node => node.EffectTypeId == TypeId).ToList();
            var windows = nodes.Where(node => node.EffectTypeId == PreparationWindowFlow.WindowTypeId).ToList();
            var tasks = nodes.Where(node => node.EffectTypeId == SelectionTypeId).ToList();
            var stepTasks = nodes.Where(node => node.EffectTypeId == PreparationWindowFlow.StepTypeId).ToList();
            var plan = state.SetupRecipe?.Preparation;
            var runtime = state.PreparationRuntime;
            if (plan == null)
            {
                if (runtime != null || roots.Count != 0 || windows.Count != 0 || tasks.Count != 0 || stepTasks.Count != 0) return false;
                diagnostic = string.Empty; return true;
            }
            try
            {
                plan.Validate();
                if (plan.ModeId != state.RuleConfiguration?.RuleModeId ||
                    (plan.Enterprise != null) != state.SetupRecipe.EnterpriseEntrance || roots.Count > 1) return false;
                if (roots.Count == 0)
                {
                    if (runtime != null || windows.Count != 0 || tasks.Count != 0 || stepTasks.Count != 0) return false;
                    diagnostic = string.Empty; return true;
                }
                var root = roots[0];
                // 准备阶段的顺序属于首回合；当前回合的 PlayerOrderSnapshot 会轮转，不能用于复验历史分发。
                if (state.FindPlayer(root.PlayerId) == null || root.DefinitionVersion != Version) return false;
                var order = new TurnOrderService().GetTurnOrder(state, root.PlayerId);
                if (order.Count < 2 || order.Count > 4 || order.Distinct().Count() != order.Count || order[0] != root.PlayerId) return false;
                if (root.Status != EffectNodeStatus.Completed &&
                    (state.EffectRuntime.PlayerOrderSnapshot == null || !order.SequenceEqual(state.EffectRuntime.PlayerOrderSnapshot))) return false;
                if (requireMainline)
                {
                    var parent = nodes.SingleOrDefault(node => node.EffectId == root.ParentEffectId);
                    string mainId = Text(parent?.NormalizedArguments, "mainNodeId");
                    string nodeType = Text(parent?.NormalizedArguments, "nodeTypeId");
                    if (parent?.EffectTypeId != RoundExecutionService.MainlineEffectTypeId || !parent.ChildEffectIds.Contains(root.EffectId) ||
                        nodeType != RoundMainlineNodeTypeIds.PlayerEntranceSetup && nodeType != RoundMainlineNodeTypeIds.PlayerEntrance ||
                        !state.EffectRuntime.MainNodes.Any(main => main.NodeId == mainId && main.ExecutionEffectId == parent.EffectId &&
                            main.NodeTypeId == nodeType && main.RoundNumber == 1 && main.StartPlayerId == root.PlayerId)) return false;
                }
                if (root.FlowStage == string.Empty)
                {
                    if (runtime != null || root.ChildEffectIds.Count != 0 || windows.Count != 0 || tasks.Count != 0 || stepTasks.Count != 0) return false;
                    diagnostic = string.Empty; return true;
                }
                string stage = root.FlowStage switch {
                    "before" => "before", "middle" => "middle", "confirming" => "confirming",
                    "departments" => "initializing", "awaiting_department" => "initializing", "initial_upgrades" => "initializing",
                    "after" => "after", "finished" => "complete", _ => ""
                };
                if (stage.Length == 0 || runtime == null || runtime.Version != 1 || runtime.OwnerEffectId != root.EffectId ||
                    runtime.ModeId != plan.ModeId || runtime.ContentHash != state.ContentPackHash || runtime.Stage != stage ||
                    (root.Status == EffectNodeStatus.Completed) != (stage == "complete")) return false;
                var children = root.ChildEffectIds.Select(id => nodes.SingleOrDefault(node => node.EffectId == id)).ToList();
                if (children.Any(node => node == null || node.ParentEffectId != root.EffectId)) return false;
                var rootWindows = children.Where(node => node.EffectTypeId == PreparationWindowFlow.WindowTypeId).ToList();
                if (rootWindows.Count != windows.Count || windows.Any(window => window.ParentEffectId != root.EffectId) ||
                    rootWindows.Count(window => Text(window.NormalizedArguments, "window") == PreparationWindowFlow.Before) != 1) return false;
                bool hasMiddle = plan.Enterprise != null && stage != "before";
                bool hasAfter = stage == "after" || stage == "complete";
                if (rootWindows.Count(window => Text(window.NormalizedArguments, "window") == PreparationWindowFlow.Middle) != (hasMiddle ? 1 : 0) ||
                    rootWindows.Count(window => Text(window.NormalizedArguments, "window") == PreparationWindowFlow.After) != (hasAfter ? 1 : 0) ||
                    rootWindows.Count != 1 + (hasMiddle ? 1 : 0) + (hasAfter ? 1 : 0)) return false;
                var before = rootWindows.Single(window => Text(window.NormalizedArguments, "window") == PreparationWindowFlow.Before);
                if (children[0] != before || stage != "before" && before.Status != EffectNodeStatus.Completed ||
                    rootWindows.Any(window => !PreparationWindowFlow.ValidateWindow(state, window, plan, registry, out _, order))) return false;
                if (stepTasks.Any(task => !rootWindows.Any(window => window.EffectId == task.ParentEffectId && window.ChildEffectIds.Contains(task.EffectId)))) return false;
                if (plan.Enterprise == null)
                {
                    if (runtime.Distribution != null || runtime.EffectiveEnterprise != null || tasks.Count != 0 || root.ChildSchedulingGroups.Count != 0 ||
                        stage != "before" && stage != "after" && stage != "complete") return false;
                }
                else if (stage == "before")
                {
                    if (runtime.Distribution != null || runtime.EffectiveEnterprise != null || tasks.Count != 0 || root.ChildSchedulingGroups.Count != 0) return false;
                }
                else
                {
                    if (!ValidDistribution(state, registry, runtime, order)) return false;
                    var middle = rootWindows.Single(window => Text(window.NormalizedArguments, "window") == PreparationWindowFlow.Middle);
                    if (children.IndexOf(middle) != 1 || stage != "middle" && middle.Status != EffectNodeStatus.Completed) return false;
                    if (stage == "middle")
                    {
                        if (tasks.Count != 0 || root.ChildSchedulingGroups.Count != 0 || runtime.Distribution.Assignments.Any(item => item.Confirmed)) return false;
                    }
                    else
                    {
                        var groups = root.ChildSchedulingGroups.Where(group => group.StableGroupKey == GroupKey).ToList();
                        if (groups.Count != 1 || root.ChildSchedulingGroups.Count != 1 || groups[0].OwnerEffectId != root.EffectId ||
                            groups[0].Mode != EffectChildSchedulingMode.Independent || groups[0].ProtocolVersion != 1 ||
                            groups[0].StartIndex != 2 || groups[0].ChildEffectIds.Count != order.Count || tasks.Count != order.Count) return false;
                        var count = runtime.EffectiveEnterprise.PlayerCounts.Single(rule => rule.PlayerCount == order.Count);
                        for (int i = 0; i < order.Count; i++)
                        {
                            var assignment = runtime.Distribution.Assignments[i];
                            var task = nodes.SingleOrDefault(node => node.EffectId == groups[0].ChildEffectIds[i]);
                            int childIndex = groups[0].StartIndex + i;
                            if (task == null || childIndex >= root.ChildEffectIds.Count || root.ChildEffectIds[childIndex] != task.EffectId ||
                                task.ParentEffectId != root.EffectId || task.EffectTypeId != SelectionTypeId || task.PlayerId != order[i] ||
                                task.Visibility != GameStateVisibilityPolicy.Owner || task.DefinitionVersion != Version ||
                                task.EffectId != StableIdFactory.Create("effect", root.EffectId, "child", "child_effect", childIndex.ToString(CultureInfo.InvariantCulture),
                                    SelectionTypeId, "preparation-enterprise:" + order[i].ToString(CultureInfo.InvariantCulture)) ||
                                !ReadIds(Value(task.NormalizedArguments, "candidateEnterpriseItemIds")).SequenceEqual(assignment.CandidateItemIds) ||
                                !ReadIds(Value(task.NormalizedArguments, "requiredEnterpriseItemIds")).SequenceEqual(assignment.RequiredItemIds) ||
                                Number(task.NormalizedArguments, "keepCount", -1) != count.Keep ||
                                (task.Status == EffectNodeStatus.Completed) != assignment.Confirmed ||
                                assignment.Confirmed && !ReadIds(Value(task.NormalizedResult, "selectedEnterpriseItemIds")).SequenceEqual(assignment.SelectedItemIds)) return false;
                            var requests = state.EffectRuntime.InteractionRequests.Where(request => request.OwnerEffectId == task.EffectId && request.Status == "open").ToList();
                            if (requests.Count > 1 || requests.Any(request => request.InteractionTypeId != ExpansionSetupFlow.EnterpriseChoiceInteraction ||
                                request.AnsweringPlayerId != task.PlayerId || request.Visibility != GameStateVisibilityPolicy.Owner ||
                                request.AllowDecline || request.MinSelections != count.Keep || request.MaxSelections != count.Keep ||
                                !ReadIds(Value(request.PromptParameters, "requiredEnterpriseItemIds")).SequenceEqual(assignment.RequiredItemIds) ||
                                !request.CandidateIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(assignment.CandidateItemIds.OrderBy(id => id, StringComparer.Ordinal)))) return false;
                        }
                        if (stage != "confirming" && runtime.Distribution.Assignments.Any(item => !item.Confirmed)) return false;
                        if (stage == "initializing")
                        {
                            string enterpriseZone = FindZone(state, "enterprises").ZoneId;
                            if (Chosen(runtime.Distribution).Any(id => state.ItemZones.Items.Single(item => item.ItemId == id).ZoneId != enterpriseZone)) return false;
                        }
                        if (!ValidInitializationChildren(state, root, children, runtime.Distribution, hasAfter)) return false;
                    }
                }
                if (hasAfter && rootWindows.Single(window => Text(window.NormalizedArguments, "window") == PreparationWindowFlow.After).Status != EffectNodeStatus.Completed && stage == "complete") return false;
                diagnostic = string.Empty; return true;
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is NullReferenceException || error is IndexOutOfRangeException)
            { return false; }
        }
    }
}
