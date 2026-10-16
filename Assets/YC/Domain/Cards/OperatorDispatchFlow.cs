using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Cards
{
    /// <summary>普通特派和直接换手牌分别编排；真实抽牌、移动、洗牌由共享原语提交。</summary>
    public static class OperatorDispatchFlow
    {
        public const string TypeId = "effect.operator.dispatch", ReplaceTypeId = "effect.operator.replace_hand";
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, context => { CharacterLifecycleService.ResumeDeferredDependencies(context); return Execute(context); }, EffectExecutorKind.IntrinsicFlow));
            if (!registry.TryGet(ReplaceTypeId, out _)) registry.Register(new EffectRegistration(ReplaceTypeId, context => { CharacterLifecycleService.ResumeDeferredDependencies(context); return ReplaceHand(context); }, EffectExecutorKind.IntrinsicFlow));
        }
        public static int GetDrawCount(GameState state) => state.RuleConfiguration?.EnabledExpansionIds.Contains("infected") == true || state.Players.Count == 2 ? 3 : 2;
        public static EffectSpec CreateSpec(GameState state, int player, IEnumerable<string> excludedReturnItemIds = null, IEnumerable<string> allowedReturnZoneIds = null) =>
            CreateSpec(player, GetDrawCount(state), excludedReturnItemIds, allowedReturnZoneIds);
        public static EffectSpec CreateSpec(int player, int drawCount, IEnumerable<string> excludedReturnItemIds = null, IEnumerable<string> allowedReturnZoneIds = null)
        {
            var args = Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)), "drawCount", NormalizedValue.CreateInteger(drawCount), "excludedReturnItemIds", Strings(excludedReturnItemIds ?? Array.Empty<string>()));
            if (allowedReturnZoneIds != null) args.Properties.Add(new NormalizedValueEntry { Name = "allowedReturnZoneIds", Value = Strings(allowedReturnZoneIds) });
            args.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            var spec = EffectSpec.Create(TypeId, args, player); spec.Visibility = GameStateVisibilityPolicy.Owner; return spec;
        }
        public static string ValidateArguments(string typeId, NormalizedValue args)
        {
            string[] keys = typeId == ReplaceTypeId ? new[] { "apiVersion", "player" } : new[] { "apiVersion", "player", "drawCount", "excludedReturnItemIds", "allowedReturnZoneIds" };
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(entry => entry.Name != "kind" && entry.Name != "effectTypeId" && !keys.Contains(entry.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Number(args, "apiVersion") != 1 || CharacterLifecycleService.ParsePlayer(Text(args, "player")) < 1) return "dispatch_arguments_invalid";
            if (typeId == ReplaceTypeId) return "";
            if (Value(args, "drawCount")?.Kind != NormalizedValueKind.Integer || Number(args, "drawCount") != 2 && Number(args, "drawCount") != 3) return "dispatch_draw_count_invalid";
            foreach (string key in new[] { "excludedReturnItemIds", "allowedReturnZoneIds" })
            {
                var value = Value(args, key);
                if (value != null && !(value.Kind == NormalizedValueKind.Object && value.Properties.Count == 0) && (value.Kind != NormalizedValueKind.Array || value.Items.Any(item => item.Kind != NormalizedValueKind.String || item.StringValue.Length == 0))) return "dispatch_arguments_invalid";
            }
            return "";
        }
        public static EffectStepResult Execute(EffectExecutionContext context)
        {
            context.Node.Visibility = GameStateVisibilityPolicy.Owner;
            string invalid = ValidateArguments(TypeId, context.Node.NormalizedArguments);
            if (invalid.Length > 0) return EffectStepResult.Failed(invalid);
            if (CharacterLifecycleService.ParsePlayer(Text(context.Node.NormalizedArguments, "player")) != context.Node.PlayerId) return EffectStepResult.Failed("invalid_player");
            if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed)) return EffectStepResult.Failed("dispatch_child_failed");
            var state = context.State; var node = context.Node; int player = node.PlayerId;
            var deck = FindZone(state, "operator_deck");
            string stage = node.FlowStage;
            if (stage == "" || stage == "draw_candidates")
            {
                int drawCount = YC.Domain.Rules.InfectedRoundRules.IsEnabled(state) ? 3 : Number(node.NormalizedArguments, "drawCount");
                var top = new ItemZoneQuery().GetTopItemIds(state, deck.ZoneId, drawCount).ToList();
                if (top.Count == 0) return Result(context, false, "empty_deck");
                if (ReturnCandidates(context).Count == 0) return Result(context, false, "no_candidates");
                var declaration = new CardZoneDefinition { LocalZoneId = "dispatch_candidates", ZoneTemplateId = "core.zone.effect_staging",
                    AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Card }, AcceptedTypeIds = new List<string> { "core.operator_card" },
                    CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents", VisibilityPolicyId = ItemZoneVisibility.Owner };
                if (!new ItemZoneStateFactory().TryCreateEffectStaging(state, node.EffectId, declaration, out string staging, out string reason)) return EffectStepResult.Failed(reason);
                Set(context.Node, "stagingZoneId", NormalizedValue.CreateString(staging)); Set(context.Node, "selectedItemIds", Strings(top));
                return EffectStepResult.Continue("choose_operator").AddChild(CharacterMove(player, top, deck.ZoneId, staging));
            }
            string stagingZoneId = Text(node.ContinuationData, "stagingZoneId");
            if (stage == "choose_operator")
            {
                var answer = context.GetLatestInteractionAnswer();
                if (answer == null) return Choose(context, stage, "operator.dispatch.choose", Members(state, stagingZoneId), true);
                bool declined = answer.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue;
                if (declined) { Set(node, "declined", NormalizedValue.CreateBoolean(true)); return EffectStepResult.Continue("return_all").AddChild(CharacterMove(player, Members(state, stagingZoneId), stagingZoneId, deck.ZoneId, "bottom")); }
                string selected = Answer(context);
                if (!Members(state, stagingZoneId).Contains(selected)) return EffectStepResult.Failed("dispatch_candidate_changed");
                Set(node, "selectedOperatorItemId", NormalizedValue.CreateString(selected));
                return EffectStepResult.Continue("return_unselected").AddChild(CharacterMove(player, new[] { selected }, stagingZoneId, FindZone(state, "hand", player).ZoneId));
            }
            if (stage == "return_unselected")
            {
                // 移回未选项和洗牌是两个节点；恢复沿节点收据继续，不再次抽顶。
                return EffectStepResult.Continue("choose_return_card").AddChild(CharacterMove(player, Members(state, stagingZoneId), stagingZoneId, deck.ZoneId, "bottom")).AddChild(Shuffle(player, deck.ZoneId));
            }
            if (stage == "choose_return_card")
            {
                string selected = Answer(context);
                var candidates = ReturnCandidates(context);
                if (selected.Length == 0 || selected == Text(node.ContinuationData, "selectedOperatorItemId") && !candidates.Contains(selected)) return Choose(context, stage, "operator.dispatch.return", candidates, false);
                // 最新回答可能仍是上一个选择；请求类型和阶段都由Host固定。
                var request = state.EffectRuntime.InteractionRequests.LastOrDefault(value => value.OwnerEffectId == node.EffectId && value.InteractionTypeId == "operator.dispatch.return" && value.Status == "answered");
                if (request == null) return Choose(context, stage, "operator.dispatch.return", candidates, false);
                if (!candidates.Contains(selected)) return Choose(context, stage, "operator.dispatch.return", candidates, false);
                Set(node, "returnItemId", NormalizedValue.CreateString(selected));
                if (CharacterLifecycleService.TryAwaitCardMoves(context, new[] { selected }, FindZone(state, "retired").ZoneId)) return CharacterLifecycleService.DeferredDependencyWait();
                string reason = CharacterLifecycleService.MoveCard(context, selected, FindZone(state, "retired").ZoneId);
                if (reason.Length > 0) return EffectStepResult.Failed(reason);
                return EffectStepResult.Continue("finish_return").AddEvent(new EffectEventRequest { EventType = "OperatorDispatchReturnShown", PlayerId = player, Visibility = "public", ResponseKind = RuleEventResponseKind.None,
                    Payload = Object("itemId", NormalizedValue.CreateString(selected), "definitionId", NormalizedValue.CreateString(state.ItemZones.Items.Single(item => item.ItemId == selected).DefinitionId)) });
            }
            if (stage == "finish_return") return Result(context, true);
            if (stage == "return_all") return EffectStepResult.Continue("grant_decline_score").AddChild(Shuffle(player, deck.ZoneId));
            if (stage == "grant_decline_score") return EffectStepResult.Continue("finished").AddChild(ResourceEffectSpecFactory.GainScore(player, 1));
            if (stage == "finished") return Result(context, false, "player_declined");
            throw new InvalidOperationException("dispatch_stage_invalid");
        }
        private static EffectStepResult Result(EffectExecutionContext context, bool accepted, string reason = "") => EffectStepResult.Completed(Object(
            "accepted", NormalizedValue.CreateBoolean(accepted), "changed", NormalizedValue.CreateBoolean(accepted || Flag(context.Node.ContinuationData, "declined")),
            "selectedOperatorItemId", NormalizedValue.CreateString(Text(context.Node.ContinuationData, "selectedOperatorItemId")), "returnItemId", NormalizedValue.CreateString(Text(context.Node.ContinuationData, "returnItemId")), "reasonCode", NormalizedValue.CreateString(reason))).WithFlowStage("finished");
        private static List<string> ReturnCandidates(EffectExecutionContext context)
        {
            var args = context.Node.NormalizedArguments;
            var zones = Value(args, "allowedReturnZoneIds")?.Items.Select(value => value.StringValue).ToList() ?? new List<string> { FindZone(context.State, "hand", context.Node.PlayerId).ZoneId, FindZone(context.State, "discard", context.Node.PlayerId).ZoneId };
            if (zones.Any(id => !context.State.ItemZones.Zones.Any(zone => zone.ZoneId == id && zone.OwnerPlayerId == context.Node.PlayerId && zone.TemplateId.StartsWith("core.zone.character_", StringComparison.Ordinal)))) throw new InvalidOperationException("dispatch_return_source_rejected");
            var excluded = Value(args, "excludedReturnItemIds")?.Items.Select(value => value.StringValue).ToList() ?? new List<string>();
            var types = ItemTypeRegistry.CreateBuiltIn();
            return context.State.ItemZones.Items.Where(item => zones.Contains(item.ZoneId) && item.ControllerSubject.PlayerId == context.Node.PlayerId && !excluded.Contains(item.ItemId) && types.HasOperationProfile(item.TypeId, "core.character_card_use")).Select(item => item.ItemId).ToList();
        }
        private static EffectStepResult Choose(EffectExecutionContext context, string stage, string type, IEnumerable<string> ids, bool decline)
        {
            var choices = ids.ToList();
            if (choices.Count == 0) return EffectStepResult.Failed("dispatch_return_candidates_changed");
            var request = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = context.Node.PlayerId, Visibility = "owner", PromptKey = type, MinSelections = 1, MaxSelections = 1, AllowDecline = decline, AnswerSchema = "candidate_id" };
            request.CandidateIds.AddRange(choices); return EffectStepResult.Continue(stage).AddInteraction(PrepareInteraction(context, request));
        }
        private static List<string> Members(GameState state, string zoneId) => new ItemZoneQuery().GetItemIds(state, zoneId).ToList();
        private static EffectSpec CharacterMove(int player, IEnumerable<string> ids, string source, string target, string insertion = "unordered") => MoveSpec(player, ids, source, target, insertion, profile: "core.character_card_transfer");
        private static EffectSpec Shuffle(int player, string deck) => EffectSpec.Create(DeckShuffleEffectExecutor.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)), "deckZone", Object("zoneId", NormalizedValue.CreateString(deck))), player);
        private static EffectStepResult ReplaceHand(EffectExecutionContext context)
        {
            context.Node.Visibility = GameStateVisibilityPolicy.Owner;
            string error = ValidateArguments(ReplaceTypeId, context.Node.NormalizedArguments);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            if (CharacterLifecycleService.ParsePlayer(Text(context.Node.NormalizedArguments, "player")) != context.Node.PlayerId) return EffectStepResult.Failed("invalid_player");
            var state = context.State; var node = context.Node; var deck = FindZone(state, "operator_deck"); var hand = FindZone(state, "hand", node.PlayerId);
            var operators = Members(state, deck.ZoneId); var cards = Members(state, hand.ZoneId);
            if (operators.Count == 0 || cards.Count == 0) return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(false), "reasonCode", NormalizedValue.CreateString("no_candidates")));
            if (node.FlowStage == "") return Choose(context, "choose_operator", "operator.replace.choose", operators, false);
            if (node.FlowStage == "choose_operator")
            {
                string selected = Answer(context); if (!operators.Contains(selected)) return Choose(context, node.FlowStage, "operator.replace.choose", operators, false);
                Set(node, "selectedOperatorItemId", NormalizedValue.CreateString(selected));
                return Choose(context, "choose_hand", "operator.replace.hand", cards, false);
            }
            if (node.FlowStage != "choose_hand") throw new InvalidOperationException("operator_replace_stage_invalid");
            string incoming = Text(node.ContinuationData, "selectedOperatorItemId"), outgoing = Answer(context);
            if (!operators.Contains(incoming) || !cards.Contains(outgoing)) return EffectStepResult.Failed("operator_replace_pair_changed");
            if (CharacterLifecycleService.TryAwaitCardMoves(context, new[] { incoming }, hand.ZoneId) ||
                CharacterLifecycleService.TryAwaitCardMoves(context, new[] { outgoing }, FindZone(state, "retired").ZoneId)) return CharacterLifecycleService.DeferredDependencyWait();
            // 整组交换先在隔离预览完成，再一次发布。第二笔非法不会留下第一笔移入。
            var preview = GameStateCloneService.DeepClone(state);
            var work = new EffectExecutionContext(preview, preview.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId), context.Registry);
            var move = new ItemTransferRequest { ItemIds = new List<string> { incoming }, ExecutingPlayerId = node.PlayerId, ExpectedSource = new ItemPositionRef { ZoneId = deck.ZoneId }, Destination = new ZoneRef { ZoneId = hand.ZoneId }, InsertionKind = "unordered", OperationProfileId = "core.character_card_transfer", RequiredCount = 1 };
            var valid = new ItemTransferService().Validate(preview, move); if (!valid.IsValid) return EffectStepResult.Failed(valid.ReasonCode);
            new ItemTransferService().RelocateItems(work, move);
            error = CharacterLifecycleService.MoveCard(work, outgoing, FindZone(preview, "retired").ZoneId); if (error.Length > 0) return EffectStepResult.Failed(error);
            GameStateCloneService.CopyTo(state, preview);
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true), "selectedOperatorItemId", NormalizedValue.CreateString(incoming), "returnItemId", NormalizedValue.CreateString(outgoing)))
                .AddEvent(new EffectEventRequest { EventType = "OperatorDispatchReturnShown", PlayerId = node.PlayerId, Visibility = GameStateVisibilityPolicy.Public, ResponseKind = RuleEventResponseKind.None,
                    Payload = Object("itemId", NormalizedValue.CreateString(outgoing), "definitionId", NormalizedValue.CreateString(state.ItemZones.Items.Single(item => item.ItemId == outgoing).DefinitionId)) });
        }
    }
}
