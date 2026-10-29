using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Presentation;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Rules
{
    /// <summary>主链拥有的开局与盖牌前子树；没有独立的回合推进器。</summary>
    public static class ExpansionSetupFlow
    {
        public const string PreparationTypeId = "setup.enterprise.prepare";
        public const string EnterpriseSelectionTypeId = "setup.enterprise.select";
        public const string RetentionTypeId = "setup.basic_character.retain";
        public const string RoundPreparationTypeId = "round.prepare_cover";
        public const string InitialInfluenceTypeId = "setup.initial_influence";
        public const string EnterpriseChoiceInteraction = "setup.enterprise.choose";
        public const string BasicCharacterInteraction = "setup.basic_character.discard";
        public const string LegacyBasicCharacterKeepInteraction = "setup.basic_character.keep";
        public const string SupplyRetirementInteraction = "round.supply.retire";

        public static void Register(EffectRegistry registry, Func<int, int> hostNext = null)
        {

            EntrepreneurFlow.Register(registry);
            if (!registry.TryGet(PreparationTypeId, out _)) registry.Register(new EffectRegistration(PreparationTypeId, c => PrepareEnterprises(c, hostNext ?? (max => GameplayRandomRuntime.Next(c.State, GameplayRandomRuntime.EnterprisePreparation, max))), EffectExecutorKind.IntrinsicFlow, "2.0.0"));
            if (!registry.TryGet(EnterpriseSelectionTypeId, out _)) registry.Register(EnterpriseSelectionTypeId, ExecuteEnterpriseSelection, EffectExecutorKind.IntrinsicFlow);
            if (!registry.TryGet(RetentionTypeId, out _)) registry.Register(RetentionTypeId, RetainBasicCharacters, EffectExecutorKind.IntrinsicFlow);
            if (!registry.TryGet(RoundPreparationTypeId, out _)) registry.Register(new EffectRegistration(RoundPreparationTypeId, PrepareRound, EffectExecutorKind.IntrinsicFlow, "2.0.0"));
            if (!registry.TryGet(InitialInfluenceTypeId, out _)) registry.Register(InitialInfluenceTypeId, InitialInfluence, EffectExecutorKind.IntrinsicFlow);
        }
        public static EffectSpec PreparationSpec(int player, bool concurrent = true) => EffectSpec.Create(PreparationTypeId,
            concurrent ? Object("setupProtocolVersion", NormalizedValue.CreateInteger(2)) : null, player);
        public static EffectSpec EnterpriseSelectionSpec(int player, IReadOnlyList<string> candidates, int keepCount)
        {
            var spec = EffectSpec.Create(EnterpriseSelectionTypeId,
                Object("candidateEnterpriseItemIds", Strings(candidates), "keepCount", NormalizedValue.CreateInteger(keepCount)), player);
            spec.StableKey = "enterprise-selection:" + player.ToString(CultureInfo.InvariantCulture);
            spec.Visibility = GameStateVisibilityPolicy.Owner;
            return spec;
        }
        public static EffectSpec RetentionSpec(int player) => EffectSpec.Create(RetentionTypeId, playerId: player);
        public static EffectSpec RoundPreparationSpec(int player) => EffectSpec.Create(RoundPreparationTypeId, playerId: player);
        public static EffectSpec InitialInfluenceSpec(int player) => EffectSpec.Create(InitialInfluenceTypeId, playerId: player);
        private static bool Waiting(EffectExecutionContext context)
        {
            if (context.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) throw new InvalidOperationException("setup_child_failed");
            return context.ChildNodes.Any(n => n.Status != EffectNodeStatus.Completed);
        }
        private static List<string> ReadIds(NormalizedValue value) => value?.Kind == NormalizedValueKind.Array ? value.Items.Select(v => v.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v.StringValue).ToList() : new List<string>();
        private static EffectStepResult Choose(EffectExecutionContext context, string stage, string type, int player, IEnumerable<string> ids, int min, int max, bool decline = false)
        {
            var interaction = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = player, Visibility = "owner", PromptKey = type,
                AnswerSchema = "candidate_ids", MinSelections = min, MaxSelections = max, AllowDecline = decline };
            if (type == "setup.influence.optional")
                interaction.Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:interaction.map.initial_influence") };
            interaction.CandidateIds.AddRange(ids.OrderBy(id => id, StringComparer.Ordinal));
            return EffectStepResult.Continue(stage).AddInteraction(PrepareInteraction(context, interaction));
        }
        private static List<string> Selected(EffectExecutionContext context)
        {
            var answer = context.GetLatestInteractionAnswer();
            if (answer?.Kind == NormalizedValueKind.Array) return ReadIds(answer);
            string id = Answer(context);
            return string.IsNullOrEmpty(id) ? new List<string>() : new List<string> { id };
        }
        private static EffectStepResult ExecuteEnterpriseSelection(EffectExecutionContext context)
        {
            context.Node.Visibility = GameStateVisibilityPolicy.Owner;
            var parent = context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == context.Node.ParentEffectId);
            var group = parent?.ChildSchedulingGroups.FirstOrDefault(candidate => candidate.StableGroupKey == "initial_enterprise_selections");
            if (parent?.EffectTypeId != PreparationTypeId || Number(parent.NormalizedArguments, "setupProtocolVersion") != 2 && Number(parent.ContinuationData, "setupProtocolVersion") != 3 ||
                parent.FlowStage != "awaiting_initial_enterprise_tasks" || group == null ||
                group.OwnerEffectId != parent.EffectId || group.ProtocolVersion != 1 || group.Mode != EffectChildSchedulingMode.Independent ||
                !parent.ChildEffectIds.Contains(context.Node.EffectId) || !group.ChildEffectIds.Contains(context.Node.EffectId))
                return EffectStepResult.Failed("enterprise_selection_outside_setup");
            var candidates = ReadIds(Value(context.Node.NormalizedArguments, "candidateEnterpriseItemIds"));
            int keep = Number(context.Node.NormalizedArguments, "keepCount");
            int count = group.ChildEffectIds.Count, draw = count == 2 ? 4 : 2;
            if (count < 2 || count > 4 || keep != (count == 2 ? 2 : 1) ||
                !candidates.SequenceEqual(ReadIds(Value(parent.ContinuationData, "candidateEnterpriseItemIds"))
                    .Skip(group.ChildEffectIds.IndexOf(context.Node.EffectId) * draw).Take(draw)))
                return EffectStepResult.Failed("enterprise_selection_arguments_invalid");
            if (context.State.FindPlayer(context.Node.PlayerId) == null || keep < 1 || candidates.Count < keep ||
                candidates.Distinct().Count() != candidates.Count || candidates.Any(id =>
                    !context.State.ItemZones.Items.Any(item => item.ItemId == id && context.Registry.EnterpriseDefinitions.FindEnterprise(item.DefinitionId) != null)))
                return EffectStepResult.Failed("enterprise_selection_arguments_invalid");
            if (context.Node.FlowStage == string.Empty)
                return Choose(context, "awaiting_selection", EnterpriseChoiceInteraction, context.Node.PlayerId, candidates, keep, keep);
            if (context.Node.FlowStage != "awaiting_selection") return EffectStepResult.Failed("enterprise_selection_stage_invalid");
            var selected = Selected(context);
            if (selected.Count != keep || selected.Distinct().Count() != keep || selected.Any(id => !candidates.Contains(id)))
                return EffectStepResult.Failed("enterprise_choice_invalid");
            Set(context.Node, "selectedEnterpriseItemIds", Strings(selected));
            return EffectStepResult.Completed(Object("selectedEnterpriseItemIds", Strings(selected))).WithFlowStage("finished");
        }
        private static EffectStepResult PrepareEnterprises(EffectExecutionContext context, Func<int, int> next)
        {
            if (context.State.SetupRecipe?.EnterpriseEntrance != true) return EffectStepResult.Completed();
            var node = context.Node;
            var order = context.State.EffectRuntime.PlayerOrderSnapshot;
            var definitions = context.Registry.EnterpriseDefinitions;
            if (node.FlowStage == string.Empty)
            {
                var boards = context.State.ItemZones.Items.Where(i => definitions.FindEnterprise(i.DefinitionId) != null).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
                int draw = order.Count == 2 ? 4 : 2;
                bool concurrent = Number(node.NormalizedArguments, "setupProtocolVersion") == 2;
                if (boards.Count < draw * order.Count + (concurrent && order.Count == 3 ? 1 : 0)) return EffectStepResult.Failed("content_not_ready");
                for (int index = boards.Count - 1; index > 0; index--) { int pick = next(index + 1); string swap = boards[index]; boards[index] = boards[pick]; boards[pick] = swap; }
                Set(node, "candidateEnterpriseItemIds", Strings(boards.Take(draw * order.Count)));
                Set(node, "unassignedEnterpriseItemIds", Strings(boards.Skip(draw * order.Count)));
                Set(node, "selectedEnterpriseItemIds", Strings(Array.Empty<string>()));
                Set(node, "initialEnterprisePlayerIds", NormalizedValue.CreateArray(new List<NormalizedValue>()));
                if (EntrepreneurFlow.Enabled(context.State))
                {
                    Set(node, "setupProtocolVersion", NormalizedValue.CreateInteger(3));
                    if (order.Count == 3)
                    {
                        var unassigned = boards.Skip(draw * order.Count).ToList();
                        Set(node, "randomEnterpriseItemId", NormalizedValue.CreateString(unassigned[next(unassigned.Count)]));
                    }
                    return EffectStepResult.Continue("entrepreneur_setup").AddChild(EntrepreneurFlow.SetupSpec(node.PlayerId));
                }
                if (concurrent)
                {
                    if (order.Count == 3)
                    {
                        var unassigned = boards.Skip(draw * order.Count).ToList();
                        Set(node, "randomEnterpriseItemId", NormalizedValue.CreateString(unassigned[next(unassigned.Count)]));
                    }
                    var tasks = order.Select((player, index) => EnterpriseSelectionSpec(player,
                        boards.Skip(index * draw).Take(draw).ToArray(), order.Count == 2 ? 2 : 1)).ToList();
                    return EffectStepResult.Continue("awaiting_initial_enterprise_tasks")
                        .AddIndependentChildren("initial_enterprise_selections", tasks);
                }
                Set(node, "nextIndex", NormalizedValue.CreateInteger(0));
                return EffectStepResult.Continue("choose_initial_enterprises");
            }
            if (node.FlowStage == "entrepreneur_setup")
            {
                if (Waiting(context)) return EffectStepResult.NoProgress("等待企业家设置。");
                if (context.State.EntrepreneurSetup?.Stage != "complete") throw new InvalidOperationException("entrepreneur_setup_incomplete");
                var original = ReadIds(Value(node.ContinuationData, "candidateEnterpriseItemIds")); int draw = order.Count == 2 ? 4 : 2;
                return EffectStepResult.Continue("awaiting_initial_enterprise_tasks").AddIndependentChildren("initial_enterprise_selections",
                    order.Select((player, index) => EnterpriseSelectionSpec(player, original.Skip(index * draw).Take(draw).ToList(), order.Count == 2 ? 2 : 1)).ToList());
            }
            if (node.FlowStage == "awaiting_initial_enterprise_tasks")
            {
                var tasks = context.ChildNodes.Where(t => t.EffectTypeId == EnterpriseSelectionTypeId).ToList();
                if (tasks.Any(task => task.Status == EffectNodeStatus.Failed || task.Status == EffectNodeStatus.Faulted))
                    return EffectStepResult.Failed("enterprise_selection_failed");
                if (tasks.Any(task => task.Status != EffectNodeStatus.Completed)) return EffectStepResult.NoProgress("等待各玩家选择企业。");
                if (Number(node.NormalizedArguments, "setupProtocolVersion") != 2 && Number(node.ContinuationData, "setupProtocolVersion") != 3 || tasks.Count(t => t.EffectTypeId == EnterpriseSelectionTypeId) != order.Count)
                    return EffectStepResult.Failed("enterprise_selection_tasks_invalid");
                var chosen = new List<string>();
                var owners = NormalizedValue.CreateArray(new List<NormalizedValue>());
                foreach (int player in order)
                {
                    var task = tasks.SingleOrDefault(child => child.PlayerId == player && child.EffectTypeId == EnterpriseSelectionTypeId);
                    if (task == null) return EffectStepResult.Failed("enterprise_selection_tasks_invalid");
                    var selected = ReadIds(Value(task.NormalizedResult, "selectedEnterpriseItemIds"));
                    var candidates = ReadIds(Value(task.NormalizedArguments, "candidateEnterpriseItemIds"));
                    if (selected.Count != (order.Count == 2 ? 2 : 1) || selected.Distinct().Count() != selected.Count || selected.Any(id => !candidates.Contains(id)))
                        return EffectStepResult.Failed("enterprise_choice_invalid");
                    chosen.AddRange(selected);
                    foreach (string id in selected) owners.Items.Add(Object("enterpriseItemId", NormalizedValue.CreateString(id), "playerId", NormalizedValue.CreateInteger(player)));
                }
                if (order.Count == 3)
                {
                    string extra = Text(node.ContinuationData, "randomEnterpriseItemId");
                    if (!ReadIds(Value(node.ContinuationData, "unassignedEnterpriseItemIds")).Contains(extra)) return EffectStepResult.Failed("enterprise_extra_invalid");
                    chosen.Add(extra);
                }
                Set(node, "selectedEnterpriseItemIds", Strings(chosen));
                Set(node, "initialEnterprisePlayerIds", owners);
                PublishEnterprises(context, chosen);
                return EffectStepResult.Continue("prepare_departments");
            }
            if (node.FlowStage == "choose_initial_enterprises" || node.FlowStage == "awaiting_initial_enterprises")
            {
                int index = Number(node.ContinuationData, "nextIndex");
                int draw = order.Count == 2 ? 4 : 2, keep = order.Count == 2 ? 2 : 1;
                if (index < order.Count)
                {
                    var candidates = ReadIds(Value(node.ContinuationData, "candidateEnterpriseItemIds")).Skip(index * draw).Take(draw).ToList();
                    if (node.FlowStage == "choose_initial_enterprises") return Choose(context, "awaiting_initial_enterprises", EnterpriseChoiceInteraction, order[index], candidates, keep, keep);
                    var selected = Selected(context);
                    if (selected.Count != keep || selected.Distinct().Count() != keep || selected.Any(id => !candidates.Contains(id))) return EffectStepResult.Failed("enterprise_choice_invalid");
                    var all = ReadIds(Value(node.ContinuationData, "selectedEnterpriseItemIds")); all.AddRange(selected);
                    Set(node, "selectedEnterpriseItemIds", Strings(all));
                    var owners = Value(node.ContinuationData, "initialEnterprisePlayerIds").Clone();
                    foreach (string id in selected) owners.Items.Add(Object("enterpriseItemId", NormalizedValue.CreateString(id), "playerId", NormalizedValue.CreateInteger(order[index])));
                    Set(node, "initialEnterprisePlayerIds", owners);
                    Set(node, "nextIndex", NormalizedValue.CreateInteger(index + 1));
                    return EffectStepResult.Continue("choose_initial_enterprises");
                }
                var chosen = ReadIds(Value(node.ContinuationData, "selectedEnterpriseItemIds"));
                if (order.Count == 3)
                {
                    var undrawn = ReadIds(Value(node.ContinuationData, "unassignedEnterpriseItemIds"));
                    if (undrawn.Count == 0) return EffectStepResult.Failed("content_not_ready");
                    string extra = undrawn[next(undrawn.Count)]; chosen.Add(extra); Set(node, "randomEnterpriseItemId", NormalizedValue.CreateString(extra));
                }
                Set(node, "selectedEnterpriseItemIds", Strings(chosen));
                PublishEnterprises(context, chosen);
                Set(node, "nextIndex", NormalizedValue.CreateInteger(0));
                return EffectStepResult.Continue("prepare_departments");
            }
            if (node.FlowStage == "prepare_departments" || node.FlowStage == "awaiting_department")
            {
                var boards = ReadIds(Value(node.ContinuationData, "selectedEnterpriseItemIds"));
                var rhine = boards.Select(id => context.State.ItemZones.Items.Single(i => i.ItemId == id)).SingleOrDefault(i => definitions.FindEnterprise(i.DefinitionId)?.DepartmentRewardPolicy == "active_department");
                if (rhine != null && !context.State.ItemZones.Relations.Any(r => r.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation && r.SourceItemId == rhine.ItemId))
                {
                    var departmentItems = context.State.ItemZones.Items.Where(i => definitions.FindDepartment(i.DefinitionId)?.EnterpriseRuntimeId == definitions.FindEnterprise(rhine.DefinitionId).RuntimeId).ToList();
                    if (departmentItems.Count != 3) return EffectStepResult.Failed("content_not_ready");
                    foreach (var item in departmentItems) ItemZoneStateMigrator.SetPosition(context.State, item, FindZone(context.State, "departments").ZoneId);
                    var owner = Value(node.ContinuationData, "initialEnterprisePlayerIds").Items.Find(v => Text(v, "enterpriseItemId") == rhine.ItemId);
                    string selected;
                    if (owner == null) selected = departmentItems[next(departmentItems.Count)].ItemId;
                    else
                    {
                        if (node.FlowStage == "prepare_departments") return Choose(context, "awaiting_department", "setup.department.choose", Number(owner, "playerId"), departmentItems.Select(i => i.ItemId), 1, 1);
                        selected = Answer(context);
                        if (!departmentItems.Any(i => i.ItemId == selected)) return EffectStepResult.Failed("department_choice_invalid");
                    }
                    context.State.ItemZones.Relations.Add(new ItemRelationState { RelationId = StableIdFactory.Create("relation", rhine.ItemId, EnterpriseStateQuery.ActiveDepartmentRelation), RelationTypeId = EnterpriseStateQuery.ActiveDepartmentRelation, SourceItemId = rhine.ItemId, TargetItemId = selected });
                }
                bool initializedOperators = ActivateConditionalComponents(context, boards);
                var step = EffectStepResult.Continue("initial_upgrades");
                if (initializedOperators)
                {
                    var shuffle = EffectSpec.Create(DeckShuffleEffectExecutor.TypeId,
                        Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(node.PlayerId.ToString(CultureInfo.InvariantCulture)),
                            "deckZone", Object("zoneId", NormalizedValue.CreateString(FindZone(context.State, "operator_deck").ZoneId))), node.PlayerId);
                    shuffle.StableKey = "initial_operator_deck_shuffle";
                    step.AddChild(shuffle);
                }
                foreach (var owner in Value(node.ContinuationData, "initialEnterprisePlayerIds").Items) step.AddChild(EnterpriseEffectExecutor.UpgradeSpec(Number(owner, "playerId"), Text(owner, "enterpriseItemId"), 1));
                return step;
            }
            if (node.FlowStage != "initial_upgrades" && node.FlowStage != "finished") throw new InvalidOperationException("enterprise_setup_stage_invalid");
            if (Waiting(context)) return EffectStepResult.NoProgress("等待初始企业升级结算。");
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true)));
        }
        private static void PublishEnterprises(EffectExecutionContext context, List<string> boards)
        {
            if (boards.Count != 4 || boards.Distinct().Count() != 4) throw new InvalidOperationException("enterprise_board_count_invalid");
            // 未选企业是各玩家已经看过的私密回盒牌，不能继续留在无人可见的备用区。
            var order = context.State.EffectRuntime.PlayerOrderSnapshot;
            var candidates = ReadIds(Value(context.Node.ContinuationData, "candidateEnterpriseItemIds"));
            int draw = order.Count == 2 ? 4 : 2;
            for (int index = 0; index < order.Count; index++)
            foreach (string id in candidates.Skip(index * draw).Take(draw).Where(id => !boards.Contains(id)))
            {
                var discarded = context.State.ItemZones.Items.Single(item => item.ItemId == id);
                discarded.ControllerSubject = ItemSubjectRef.Player(order[index]);
                ItemZoneStateMigrator.SetPosition(context.State, discarded, FindZone(context.State, "secret_retired").ZoneId);
            }
            foreach (string id in boards)
            {
                var board = context.State.ItemZones.Items.Single(i => i.ItemId == id);
                ItemZoneStateMigrator.SetPosition(context.State, board, FindZone(context.State, "enterprises").ZoneId);
                foreach (int player in context.State.EffectRuntime.PlayerOrderSnapshot.ToArray())
                {
                    var declaration = new CardZoneDefinition { LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track",
                        AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "unbounded",
                        OrderingKind = ZoneOrderingKind.Track, VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                    if (!new ItemZoneStateFactory().TryCreateCardZone(context.State, id, declaration, out string zoneId, out string reason)) throw new InvalidOperationException(reason);
                    var marker = context.State.ItemZones.Items.FirstOrDefault(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == player && i.ZoneId == FindZone(context.State, "supply", player).ZoneId);
                    if (marker == null) throw new InvalidOperationException("insufficient_items");
                    ItemZoneStateMigrator.SetPosition(context.State, marker, zoneId); marker.SlotId = "level_0";
                }
            }
        }
        private static bool ActivateConditionalComponents(EffectExecutionContext context, List<string> boards)
        {
            bool initializedOperators = false;
            var runtimeIds = boards.Select(id => context.Registry.EnterpriseDefinitions.FindEnterprise(context.State.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId).RuntimeId).ToList();
            foreach (var entry in context.State.SetupRecipe.Entries.Where(e => e.Condition.Kind == "enterprise_selected" && runtimeIds.Contains(e.Condition.RuntimeId)))
                foreach (var item in context.State.ItemZones.Items.Where(i => i.DefinitionId == entry.DefinitionId && i.ZoneId == FindZone(context.State, "reserve").ZoneId).ToList())
                    {
                    string destination = context.State.ItemZones.Zones.Single(z => z.LocalZoneId == entry.InitialZoneRole && z.OwnerPlayerId == (entry.PerPlayer ? item.OwnerSubject.PlayerId : -1)).ZoneId;
                    ItemZoneStateMigrator.SetPosition(context.State, item, destination);
                    if (entry.InitialZoneRole == "operator_deck") initializedOperators = true;
                    if (item.PhysicalKind == ItemPhysicalKind.Cube) item.OriginalSupplyZoneId = destination;
                }
            return initializedOperators;
        }
        public static void ActivateAbilityComponents(EffectExecutionContext context, string abilityId)
        {
            if (context?.State?.SetupRecipe == null || string.IsNullOrEmpty(abilityId)) throw new ArgumentException("ability_components_context_invalid");
            foreach (var entry in context.State.SetupRecipe.Entries.Where(e => e.Condition.Kind == "ability_granted" && e.Condition.AbilityId == abilityId))
                foreach (var item in context.State.ItemZones.Items.Where(i => i.DefinitionId == entry.DefinitionId && i.ZoneId == FindZone(context.State, "reserve").ZoneId && (!entry.PerPlayer || i.OwnerSubject.PlayerId == context.Node.PlayerId)).ToList())
                    ItemZoneStateMigrator.SetPosition(context.State, item, FindZone(context.State, entry.InitialZoneRole, entry.PerPlayer ? context.Node.PlayerId : -1).ZoneId);
        }
        public static EffectStepResult RetainBasicCharacters(EffectExecutionContext context)
        {
            var recipe = context.State.SetupRecipe;
            if (recipe == null) return EffectStepResult.Completed();
            var node = context.Node; int player = node.PlayerId;
            if (Flag(node.ContinuationData, "basicCharacterRetentionCompleted")) return EffectStepResult.Completed();
            var types = ItemTypeRegistry.CreateBuiltIn();
            var hand = FindZone(context.State, "hand", player);
            int discardCount = recipe.InitialBasicCharacterCount - recipe.BasicCharacterKeepCount;
            if (node.FlowStage == string.Empty)
            {
                var initial = context.State.ItemZones.Items.Where(i => i.ZoneId == hand.ZoneId && i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId == player && types.IsAssignableTo(i.TypeId, "core.basic_character_card")).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
                if (initial.Count != recipe.InitialBasicCharacterCount) return EffectStepResult.Failed("initial_basic_character_count_mismatch");
                Set(node, "initialBasicCharacterIds", Strings(initial));
                if (discardCount == 0) { Set(node, "selectedBasicCharacterDiscardIds", Strings(Array.Empty<string>())); return EffectStepResult.Continue("retire_selected_basic"); }
                return Choose(context, "choose_basic_character_discards", BasicCharacterInteraction, player, initial, discardCount, discardCount);
            }
            if (node.FlowStage == "choose_basic_character_discards")
            {
                var selected = Selected(context);
                if (selected.Count != discardCount || selected.Distinct().Count() != selected.Count) return EffectStepResult.Failed("basic_character_discard_count_invalid");
                var initial = ReadIds(Value(node.ContinuationData, "initialBasicCharacterIds"));
                if (selected.Any(id => !initial.Contains(id))) return EffectStepResult.Failed("basic_character_candidate_changed");
                Set(node, "selectedBasicCharacterDiscardIds", Strings(selected));
                return EffectStepResult.Continue("retire_selected_basic");
            }
            // 仅续跑已经接受答案的旧档阶段；旧keep待选由正式命令入口明确拒绝。
            // 旧selectedBasicCharacterIds始终表示保留集合，不能按新弃牌集合解释。
            if (node.FlowStage == "choose_basic_characters")
            {
                var selected = Selected(context);
                if (selected.Count != recipe.BasicCharacterKeepCount || selected.Distinct().Count() != selected.Count) return EffectStepResult.Failed("basic_character_keep_count_invalid");
                var initial = ReadIds(Value(node.ContinuationData, "initialBasicCharacterIds"));
                if (selected.Any(id => !initial.Contains(id)) || initial.Any(id => !context.State.ItemZones.Items.Any(i => i.ItemId == id && i.ZoneId == hand.ZoneId && i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId == player && types.IsAssignableTo(i.TypeId, "core.basic_character_card")))) return EffectStepResult.Failed("basic_character_candidate_changed");
                Set(node, "selectedBasicCharacterIds", Strings(selected));
                return EffectStepResult.Continue("retire_unselected_basic");
            }
            if (node.FlowStage != "retire_selected_basic" && node.FlowStage != "retire_unselected_basic") throw new InvalidOperationException("basic_character_retention_stage_invalid");
            var frozen = ReadIds(Value(node.ContinuationData, "initialBasicCharacterIds"));
            var retained = node.FlowStage == "retire_unselected_basic" ? ReadIds(Value(node.ContinuationData, "selectedBasicCharacterIds")) :
                frozen.Except(ReadIds(Value(node.ContinuationData, "selectedBasicCharacterDiscardIds"))).ToList();
            var retired = node.FlowStage == "retire_selected_basic" ? ReadIds(Value(node.ContinuationData, "selectedBasicCharacterDiscardIds")) : frozen.Except(retained).ToList();
            if (frozen.Count != recipe.InitialBasicCharacterCount || frozen.Distinct().Count() != frozen.Count ||
                retained.Count != recipe.BasicCharacterKeepCount || retained.Distinct().Count() != retained.Count || retained.Any(id => !frozen.Contains(id)) ||
                retired.Count != discardCount || retired.Distinct().Count() != retired.Count || retired.Any(id => !frozen.Contains(id)) ||
                frozen.Any(id => !context.State.ItemZones.Items.Any(i => i.ItemId == id && i.ZoneId == hand.ZoneId && i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId == player && types.IsAssignableTo(i.TypeId, "core.basic_character_card"))))
                return EffectStepResult.Failed("basic_character_candidate_changed");
            // 一次整组转移与完成事实共同提交；公开结果只包含数量。
            var request = new ItemTransferRequest { ExecutingPlayerId = player, ItemIds = retired, ExpectedSource = new ItemPositionRef { ZoneId = hand.ZoneId }, Destination = new ZoneRef { ZoneId = FindZone(context.State, "secret_retired").ZoneId }, InsertionKind = "unordered", OperationProfileId = "core.character_card_transfer", CarrierPolicyId = "preserve", RequiredCount = retired.Count };
            var service = new ItemTransferService(); var valid = service.Validate(context.State, request);
            if (!valid.IsValid) return EffectStepResult.Failed(valid.ReasonCode);
            service.RelocateItems(context, request);
            Set(node, "retiredBasicCharacterIds", Strings(retired)); Set(node, "basicCharacterRetentionCompleted", NormalizedValue.CreateBoolean(true));
            node.Visibility = GameStateVisibilityPolicy.Owner;
            return EffectStepResult.Completed(Object("retainedCount", NormalizedValue.CreateInteger(retained.Count), "retiredCount", NormalizedValue.CreateInteger(retired.Count))).WithFlowStage("finished");
        }
        private static EffectStepResult InitialInfluence(EffectExecutionContext context)
        {
            if (context.State.Players.Count != 2 || context.Node.PlayerId == context.State.StartPlayerId) return EffectStepResult.Completed();
            if (context.Node.FlowStage == string.Empty)
            {
                var map = new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId));
                var service = new InfluenceService(map);
                var slots = service.GetLegalPlacementSlotIds(context.State, context.Node.PlayerId);
                if (slots.Count == 0) return EffectStepResult.Completed();
                return Choose(context, "awaiting_initial_influence", "setup.influence.optional", context.Node.PlayerId, slots, 0, 1, true);
            }
            if (context.Node.FlowStage == "awaiting_initial_influence")
            {
                var selected = Selected(context); if (selected.Count == 0) return EffectStepResult.Completed();
                if (selected.Count != 1) return EffectStepResult.Failed("initial_influence_invalid");
                return EffectStepResult.Continue("placing_initial_influence").AddChild(InfluenceEffectSpecFactory.CreatePlaceInfluence(context.Node.PlayerId, selected[0], "setup.initial_influence"));
            }
            if (context.Node.FlowStage != "placing_initial_influence") throw new InvalidOperationException("initial_influence_stage_invalid");
            if (Waiting(context)) return EffectStepResult.NoProgress("等待初始影响力。");
            return EffectStepResult.Completed();
        }
        private static EffectStepResult PrepareRound(EffectExecutionContext context)
        {
            var node = context.Node; var order = context.State.EffectRuntime.PlayerOrderSnapshot;
            if (context.State.SetupRecipe == null) return EffectStepResult.Completed();
            if (node.FlowStage == string.Empty) { Set(node, "nextIndex", NormalizedValue.CreateInteger(0)); return EffectStepResult.Continue("choose_red_zone_upgrade"); }
            int index = Number(node.ContinuationData, "nextIndex");
            int openRound = order.Count == 4 ? 4 : order.Count == 3 ? 5 : 6;
            if (node.FlowStage == "choose_red_zone_upgrade" || node.FlowStage == "awaiting_red_zone_upgrade" || node.FlowStage == "resolving_red_zone_upgrade")
            {
                if (context.State.SetupRecipe.EnterpriseEntrance && context.State.Round == openRound && index < order.Count)
                {
                    if (node.FlowStage == "resolving_red_zone_upgrade") { if (Waiting(context)) return EffectStepResult.NoProgress("等待企业升级。"); Set(node, "nextIndex", NormalizedValue.CreateInteger(index + 1)); return EffectStepResult.Continue("choose_red_zone_upgrade"); }
                    var candidates = new EnterpriseStateQuery(context.Registry.EnterpriseDefinitions).GetEnterpriseItemIds(context.State);
                    if (node.FlowStage == "choose_red_zone_upgrade") return Choose(context, "awaiting_red_zone_upgrade", "round.enterprise.upgrade", order[index], candidates, 1, 1);
                    string selected = Answer(context); if (!candidates.Contains(selected)) return EffectStepResult.Failed("enterprise_choice_invalid");
                    return EffectStepResult.Continue("resolving_red_zone_upgrade").AddChild(EnterpriseEffectExecutor.UpgradeSpec(order[index], selected, 1));
                }
                Set(node, "nextIndex", NormalizedValue.CreateInteger(0)); return EffectStepResult.Continue("choose_supply_retirement");
            }
            if (node.FlowStage == "choose_supply_retirement" || node.FlowStage == "awaiting_supply_retirement" || node.FlowStage == "refilling_supply")
            {
                int count = InfectedRoundRules.GetSupplyRetirementCount(context.State, order.Count);
                if (index >= count)
                {
                    Set(node, "nextIndex", NormalizedValue.CreateInteger(0));
                    return EffectStepResult.Continue("entrepreneur_faces");
                }
                if (node.FlowStage == "refilling_supply") { if (Waiting(context)) return EffectStepResult.NoProgress("等待供应补牌。"); Set(node, "nextIndex", NormalizedValue.CreateInteger(index + 1)); return EffectStepResult.Continue("choose_supply_retirement"); }
                var supply = context.State.ItemZones.Items.Where(i => context.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).ToList();
                if (supply.Count == 0) { Set(node, "nextIndex", NormalizedValue.CreateInteger(index + 1)); return EffectStepResult.Continue("choose_supply_retirement"); }
                if (node.FlowStage == "choose_supply_retirement") return Choose(context, "awaiting_supply_retirement", SupplyRetirementInteraction, order[index], supply.Select(i => i.ItemId), 1, 1);
                var item = supply.SingleOrDefault(i => i.ItemId == Answer(context)); if (item == null) return EffectStepResult.Failed("supply_candidate_changed");
                string slot = item.ZoneId; Set(node, "originalSupplySlotZoneId", NormalizedValue.CreateString(slot));
                return EffectStepResult.Continue("refilling_supply").AddChild(MoveSpec(order[index], new[] { item.ItemId }, slot, FindZone(context.State, "retired").ZoneId, "unordered", 1)).AddChild(RefillSpec(order[index], slot));
            }
            if (node.FlowStage == "entrepreneur_faces" || node.FlowStage == "awaiting_entrepreneur_face")
            {
                if (!EntrepreneurFlow.Enabled(context.State) || !new[] { 1, 3, 5, 7 }.Contains(context.State.Round)) return EffectStepResult.Completed();
                if (node.FlowStage == "awaiting_entrepreneur_face")
                {
                    if (Waiting(context)) return EffectStepResult.NoProgress("等待回合抉择。");
                    Set(node, "nextIndex", NormalizedValue.CreateInteger(index + 1));
                    return EffectStepResult.Continue("entrepreneur_faces");
                }
                if (index >= order.Count) return EffectStepResult.Completed();
                return EffectStepResult.Continue("awaiting_entrepreneur_face").AddChild(EntrepreneurFlow.FaceSpec(order[index]));
            }
            throw new InvalidOperationException("round_preparation_stage_invalid");
        }
    }
}
