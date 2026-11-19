using System;
using System.Collections.Generic;
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
        public const string RetentionTypeId = "setup.basic_character.retain";
        public const string RoundPreparationTypeId = "round.prepare_cover";
        public const string InitialInfluenceTypeId = "setup.initial_influence";
        public const string EnterpriseChoiceInteraction = "setup.enterprise.choose";
        public const string BasicCharacterInteraction = "setup.basic_character.discard";
        public const string LegacyBasicCharacterKeepInteraction = "setup.basic_character.keep";
        public const string SupplyRetirementInteraction = "round.supply.retire";

        public static void Register(EffectRegistry registry)
        {

            EntrepreneurFlow.Register(registry);
            if (!registry.TryGet(RetentionTypeId, out _)) registry.Register(RetentionTypeId, RetainBasicCharacters, EffectExecutorKind.IntrinsicFlow);
            if (!registry.TryGet(RoundPreparationTypeId, out _)) registry.Register(new EffectRegistration(RoundPreparationTypeId, PrepareRound, EffectExecutorKind.IntrinsicFlow, "2.0.0") { ExecutionDoesNotRetainState = true });
            if (!registry.TryGet(InitialInfluenceTypeId, out _)) registry.Register(InitialInfluenceTypeId, InitialInfluence, EffectExecutorKind.IntrinsicFlow);
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
        internal static bool ActivateConditionalComponents(EffectExecutionContext context, List<string> boards)
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
                    var query = new EnterpriseStateQuery(context.Registry.EnterpriseDefinitions);
                    var candidates = query.GetEnterpriseItemIds(context.State).Where(id => query.CanUpgrade(context.State, order[index], id)).ToList();
                    if (candidates.Count == 0)
                    {
                        Set(node, "nextIndex", NormalizedValue.CreateInteger(index + 1));
                        return EffectStepResult.Continue("choose_red_zone_upgrade");
                    }
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
                return EffectStepResult.Continue("refilling_supply").AddChild(ItemMovementV4.TransferSpec(order[index], new[] { item.ItemId }, slot, FindZone(context.State, "retired").ZoneId, "unordered", 1)).AddChild(RefillSpec(order[index], slot));
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
