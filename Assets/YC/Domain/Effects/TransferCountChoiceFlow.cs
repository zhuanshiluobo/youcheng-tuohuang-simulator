using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // API-41：大数量菜单在移动接口之外编排；每页仍是至多16项的真实Choice。
    public static class TransferCountChoiceFlow
    {
        public const string TypeId = "flow.choice.transfer_count";
        public const string SequenceTypeId = "flow.item.transfer_sequence";
        public const string Version = "1.0.0";
        public const string DeclineContractVersion = "transfer_count.choice_decline.v1.0.1";
        public const string MovementProofVersion = "transfer_count.canonical_move_proof.v1.0.1";

        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, ExecuteChoice,
                EffectExecutorKind.IntrinsicFlow, Version) { Validator = s => ValidateArguments(TypeId, s.NormalizedArguments) });
            if (!registry.TryGet(SequenceTypeId, out _)) registry.Register(new EffectRegistration(SequenceTypeId, ExecuteSequence,
                EffectExecutorKind.IntrinsicFlow, Version) { Validator = s => ValidateArguments(SequenceTypeId, s.NormalizedArguments) });
        }

        public static string ValidateArguments(string type, NormalizedValue args)
        {
            if (type != TypeId && type != SequenceTypeId || args?.Kind != NormalizedValueKind.Object || !args.IsValid()) return "transfer_count_arguments_invalid";
            var allowed = new HashSet<string>(new[] { "kind", "effectTypeId", "apiVersion", "player", "itemIds", "sourceZoneId", "destinationZoneId", "operationProfileId" });
            if (type == TypeId) allowed.UnionWith(new[] { "maximumCount", "unitCost", "allowDecline" });
            if (args.Properties.Any(p => !allowed.Contains(p.Name)) || Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Value(args, "apiVersion").IntegerValue != 1 ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1) return "transfer_count_arguments_invalid";
            var ids = Value(args, "itemIds");
            if (ids?.Kind != NormalizedValueKind.Array || ids.Items.Count < 1 || ids.Items.Count > 99 ||
                ids.Items.Any(v => v.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(v.StringValue)) ||
                ids.Items.Select(v => v.StringValue).Distinct(StringComparer.Ordinal).Count() != ids.Items.Count) return "transfer_count_items_invalid";
            if (string.IsNullOrEmpty(Text(args, "sourceZoneId")) || string.IsNullOrEmpty(Text(args, "destinationZoneId")) ||
                Text(args, "sourceZoneId") == Text(args, "destinationZoneId") ||
                ValidateTokenArguments(TokenArguments(player, new[] { ids.Items[0].StringValue }, Text(args, "sourceZoneId"),
                    Text(args, "destinationZoneId"), 1, Text(args, "operationProfileId"))).Length > 0) return "transfer_count_movement_invalid";
            if (type == SequenceTypeId) return "";
            var maximumValue = Value(args, "maximumCount");
            if (maximumValue?.Kind != NormalizedValueKind.Integer || maximumValue.IntegerValue < 1 || maximumValue.IntegerValue > ids.Items.Count ||
                Value(args, "allowDecline") != null && Value(args, "allowDecline").Kind != NormalizedValueKind.Boolean) return "transfer_count_range_invalid";
            int maximum = Number(args, "maximumCount");
            var cost = Value(args, "unitCost");
            if (cost != null && (cost.Kind != NormalizedValueKind.Object || cost.Properties.Any(p =>
                !Enum.TryParse(p.Name, out ResourceType resource) || !Enum.IsDefined(typeof(ResourceType), resource) || resource.ToString() != p.Name ||
                p.Value.Kind != NormalizedValueKind.Integer || p.Value.IntegerValue < 0 || p.Value.IntegerValue > int.MaxValue / maximum))) return "transfer_count_cost_invalid";
            return "";
        }

        private static List<EffectNodeRuntimeState> Own(GameState state, EffectNodeRuntimeState node) => node.ChildEffectIds
            .Where(id => !state.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(id)))
            .Select(id => state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)).ToList();

        private static EffectSpec Sequence(NormalizedValue args, int player, int count) => EffectSpec.Create(SequenceTypeId, Object(
            "apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),
            "itemIds", Strings(Value(args, "itemIds").Items.Take(count).Select(v => v.StringValue)),
            "sourceZoneId", NormalizedValue.CreateString(Text(args, "sourceZoneId")),
            "destinationZoneId", NormalizedValue.CreateString(Text(args, "destinationZoneId")),
            "operationProfileId", NormalizedValue.CreateString(Text(args, "operationProfileId"))), player);

        private static GameplayTextRef CountText(EffectPresentationDefinition presentation, int count)
        {
            var label = presentation?.Interaction?.CandidateIntegerLabel;
            return label == null ? null : new GameplayTextRef(label.Key, label.Arguments.Select(a => new GameplayTextArgument {
                Name = a.Name, Kind = "integer", Value = NormalizedValue.CreateInteger(checked(a.Multiplier * count)) }).ToArray());
        }

        private static EffectSpec FixedBranch(NormalizedValue args, int player, int count, EffectPresentationDefinition presentation)
        {
            var sequence = Sequence(args, player, count);
            var cost = new ResourceSet();
            foreach (var entry in Value(args, "unitCost")?.Properties ?? new List<NormalizedValueEntry>())
                cost.Set((ResourceType)Enum.Parse(typeof(ResourceType), entry.Name), checked((int)entry.Value.IntegerValue * count));
            var label = CountText(presentation, count);
            if (!cost.Enumerate().Any(p => p.Value > 0))
            {
                sequence.Presentation = label == null ? null : new EffectPresentationDefinition { Started = label };
                return sequence;
            }
            var condition = EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(player, cost) }, new[] { sequence }, decisionPlayerId: player, ruleCondition: true);
            if (label != null) condition.Presentation = new EffectPresentationDefinition { Started = label,
                Interaction = new GameplayInteractionPresentation { Prompt = label.Clone() } };
            return condition;
        }

        private static EffectSpec Build(NormalizedValue args, int player, List<int> counts, EffectPresentationDefinition presentation)
        {
            var options = new List<string>(); var groups = new List<List<EffectSpec>>(); var labels = new List<GameplayOptionPresentation>();
            bool page = counts.Count > 16;
            if (page)
                for (int first = 0; first < counts.Count; first += 16)
                {
                    var subset = counts.Skip(first).Take(16).ToList(); string id = "page:" + subset[0].ToString(CultureInfo.InvariantCulture);
                    options.Add(id); groups.Add(new List<EffectSpec> { Build(args, player, subset, presentation) });
                    labels.Add(new GameplayOptionPresentation { CandidateId = id, Label = new GameplayTextRef("core:interaction.choice.quantity_group",
                        new GameplayTextArgument { Name = "minimum", Kind = "integer", Value = NormalizedValue.CreateInteger(subset.Min()) },
                        new GameplayTextArgument { Name = "maximum", Kind = "integer", Value = NormalizedValue.CreateInteger(subset.Max()) }) });
                }
            else foreach (int count in counts)
            {
                string id = count.ToString(CultureInfo.InvariantCulture); options.Add(id);
                groups.Add(new List<EffectSpec> { FixedBranch(args, player, count, presentation) });
            }
            var choice = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),
                "options", Strings(options), "minSelections", NormalizedValue.CreateInteger(Value(args, "allowDecline") == null || Flag(args, "allowDecline") ? 0 : 1),
                "maxSelections", NormalizedValue.CreateInteger(1), "interactionType", NormalizedValue.CreateString("effect.choice.execute")), player);
            choice.DecisionPlayerId = player;
            choice.NestedEffectGroups = groups;
            choice.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation {
                Prompt = presentation?.Interaction?.Prompt?.Clone(), Options = labels,
                CandidateIntegerLabel = page ? null : GameplayPresentationNormalizer.Clone(presentation?.Interaction?.CandidateIntegerLabel) } };
            return choice;
        }

        private static EffectStepResult ExecuteChoice(EffectExecutionContext context)
        {
            var node = context.Node; string error = ValidateArguments(TypeId, node.NormalizedArguments);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            if (Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture)) return EffectStepResult.Failed("transfer_permission_denied");
            if (node.FlowStage == "")
            {
                var counts = Enumerable.Range(1, Number(node.NormalizedArguments, "maximumCount")).Reverse().ToList();
                var choice = Build(node.NormalizedArguments, node.PlayerId, counts, node.Presentation); choice.StableKey = "quantity-choice";
                return EffectStepResult.Continue("choosing").AddChild(choice);
            }
            if (!TryValidateSaved(context.State, node)) throw new InvalidOperationException("transfer_count_state_invalid");
            var child = Own(context.State, node).Single();
            if (child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed) return EffectStepResult.NoProgress("等待真实数量分支完成。");
            var result = ChoiceResult(context.State, node);
            return (child.Status == EffectNodeStatus.Failed ? EffectStepResult.Failed(child.FailureReason, result) : EffectStepResult.Completed(result)).WithFlowStage("finished");
        }

        private static IEnumerable<EffectNodeRuntimeState> SelectedTree(GameState state, EffectNodeRuntimeState node)
        {
            foreach (var child in Own(state, node))
            {
                yield return child;
                if (child.EffectTypeId == LuaDomainEffectTypeIds.Choice || child.EffectTypeId == EffectTypeIds.Condition)
                    foreach (var nested in SelectedTree(state, child)) yield return nested;
            }
        }

        private static NormalizedValue ChoiceResult(GameState state, EffectNodeRuntimeState node)
        {
            var selected = SelectedTree(state, node).ToList(); int count = 0;
            foreach (var choice in selected.Where(n => n.EffectTypeId == LuaDomainEffectTypeIds.Choice))
                if (choice.NormalizedResult?.Kind == NormalizedValueKind.String && int.TryParse(choice.NormalizedResult.StringValue, out int value)) count = value;
            var sequence = selected.SingleOrDefault(n => n.EffectTypeId == SequenceTypeId);
            return Object("selectedCount", NormalizedValue.CreateInteger(count), "declined", NormalizedValue.CreateBoolean(count == 0),
                "changed", NormalizedValue.CreateBoolean(sequence != null && Flag(sequence.NormalizedResult, "changed")),
                "movedCount", NormalizedValue.CreateInteger(sequence == null ? 0 : Number(sequence.NormalizedResult, "movedCount")),
                "failedCount", NormalizedValue.CreateInteger(sequence == null ? 0 : Number(sequence.NormalizedResult, "failedCount")));
        }

        private static EffectSpec Move(NormalizedValue args, int player, int index)
        {
            var spec = TokenSpec(player, new[] { Value(args, "itemIds").Items[index].StringValue }, Text(args, "sourceZoneId"), Text(args, "destinationZoneId"), 1, Text(args, "operationProfileId"));
            if (!ItemMoveInputNormalization.TryNormalize(spec, out string error)) throw new InvalidOperationException(error);
            spec.StableKey = "item:" + index.ToString(CultureInfo.InvariantCulture); return spec;
        }

        private static EffectStepResult ExecuteSequence(EffectExecutionContext context)
        {
            var node = context.Node; string error = ValidateArguments(SequenceTypeId, node.NormalizedArguments);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            if (Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture)) return EffectStepResult.Failed("transfer_permission_denied");
            if (node.FlowStage == "")
            {
                Set(node, "processed", NormalizedValue.CreateInteger(0)); Set(node, "moved", NormalizedValue.CreateInteger(0)); Set(node, "failed", NormalizedValue.CreateInteger(0));
                return EffectStepResult.Continue("moving").AddChild(Move(node.NormalizedArguments, node.PlayerId, 0));
            }
            if (!TryValidateSaved(context.State, node)) throw new InvalidOperationException("transfer_sequence_state_invalid");
            if (node.FlowStage == "finished") return Number(node.ContinuationData, "failed") > 0
                ? EffectStepResult.Failed("transfer_sequence_partial_failure", SequenceResult(node)).WithFlowStage("finished")
                : EffectStepResult.Completed(SequenceResult(node)).WithFlowStage("finished");
            var last = Own(context.State, node).Last();
            if (last.Status != EffectNodeStatus.Completed && last.Status != EffectNodeStatus.Failed) return EffectStepResult.NoProgress("等待实际单枚移动完成。");
            int processed = Number(node.ContinuationData, "processed") + 1;
            Set(node, "processed", NormalizedValue.CreateInteger(processed));
            bool changed = Flag(last.NormalizedResult, "changed");
            if (changed) Set(node, "moved", NormalizedValue.CreateInteger(Number(node.ContinuationData, "moved") + 1));
            if (last.Status != EffectNodeStatus.Completed || !changed) Set(node, "failed", NormalizedValue.CreateInteger(Number(node.ContinuationData, "failed") + 1));
            if (processed < Value(node.NormalizedArguments, "itemIds").Items.Count)
                return EffectStepResult.Continue("moving").AddChild(Move(node.NormalizedArguments, node.PlayerId, processed));
            var result = SequenceResult(node);
            return (Number(node.ContinuationData, "failed") > 0 ? EffectStepResult.Failed("transfer_sequence_partial_failure", result) : EffectStepResult.Completed(result)).WithFlowStage("finished");
        }

        private static NormalizedValue SequenceResult(EffectNodeRuntimeState node) => Object(
            "changed", NormalizedValue.CreateBoolean(Number(node.ContinuationData, "moved") > 0),
            "movedCount", NormalizedValue.CreateInteger(Number(node.ContinuationData, "moved")),
            "failedCount", NormalizedValue.CreateInteger(Number(node.ContinuationData, "failed")));

        private static object Shape(EffectSpecRuntimeState spec) => new { spec.EffectTypeId, spec.PlayerId, spec.NormalizedArguments,
            spec.Presentation, spec.AllowDecline, spec.DecisionPlayerId, spec.NestedGroupSizes, Children = spec.NestedEffects.Select(Shape).ToList() };

        public static bool TryValidateSaved(GameState state, EffectNodeRuntimeState node)
        {
            if (node.EffectTypeId != TypeId && node.EffectTypeId != SequenceTypeId) return true;
            try
            {
                if (node.DefinitionVersion != Version || ValidateArguments(node.EffectTypeId, node.NormalizedArguments).Length > 0 ||
                    Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture) || state.FindPlayer(node.PlayerId) == null ||
                    node.PositionChanges.Count != 0 || state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == node.EffectId)) return false;
                var children = Own(state, node);
                if (node.FlowStage == "") return children.Count == 0 && node.Status != EffectNodeStatus.Completed;
                if (node.EffectTypeId == TypeId)
                {
                    if (node.FlowStage != "choosing" && node.FlowStage != "finished" || children.Count != 1) return false;
                    var child = children[0];
                    var expected = Build(node.NormalizedArguments, node.PlayerId, Enumerable.Range(1, Number(node.NormalizedArguments, "maximumCount")).Reverse().ToList(), node.Presentation).ToPersistentDefinition();
                    var actual = new EffectSpecRuntimeState { EffectTypeId = child.EffectTypeId, PlayerId = child.PlayerId, NormalizedArguments = child.NormalizedArguments,
                        Presentation = child.Presentation, AllowDecline = child.AllowDecline, DecisionPlayerId = child.DecisionPlayerId, NestedGroupSizes = child.NestedGroupSizes, NestedEffects = child.NestedEffects };
                    if (ItemTransferService.MovementV4Digest(Shape(expected)) != ItemTransferService.MovementV4Digest(Shape(actual))) return false;
                    return node.FlowStage != "finished" || (child.Status == EffectNodeStatus.Completed || child.Status == EffectNodeStatus.Failed) &&
                        ItemTransferService.MovementV4Digest(node.NormalizedResult) == ItemTransferService.MovementV4Digest(ChoiceResult(state, node));
                }
                var data = node.ContinuationData;
                if (data?.Kind != NormalizedValueKind.Object || data.Properties.Count != 3 || data.Properties.Any(p =>
                    !new[] { "processed", "moved", "failed" }.Contains(p.Name) || p.Value.Kind != NormalizedValueKind.Integer || p.Value.IntegerValue < 0 || p.Value.IntegerValue > 99)) return false;
                int processed = Number(data, "processed"), moved = Number(data, "moved"), failed = Number(data, "failed");
                int count = Value(node.NormalizedArguments, "itemIds").Items.Count;
                if (moved > processed || failed > processed || moved + failed < processed || processed > count || node.FlowStage != "moving" && node.FlowStage != "finished" ||
                    children.Count != processed + (node.FlowStage == "moving" ? 1 : 0) || node.FlowStage == "moving" && processed >= count || node.FlowStage == "finished" && processed != count) return false;
                for (int i = 0; i < children.Count; i++)
                {
                    var expected = Move(node.NormalizedArguments, node.PlayerId, i);
                    if (children[i].EffectTypeId != expected.EffectTypeId || children[i].PlayerId != node.PlayerId ||
                        ItemTransferService.MovementV4Digest(children[i].NormalizedArguments) != ItemTransferService.MovementV4Digest(expected.NormalizedArguments) ||
                        i < processed && children[i].Status != EffectNodeStatus.Completed && children[i].Status != EffectNodeStatus.Failed) return false;
                }
                if (children.Take(processed).Count(c => Flag(c.NormalizedResult, "changed")) != moved ||
                    children.Take(processed).Count(c => c.Status != EffectNodeStatus.Completed || !Flag(c.NormalizedResult, "changed")) != failed) return false;
                return node.FlowStage != "finished" || ItemTransferService.MovementV4Digest(node.NormalizedResult) == ItemTransferService.MovementV4Digest(SequenceResult(node));
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is OverflowException) { return false; }
        }
    }
}
