using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Economy;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    // 通用内容原语；不含角色 ID、卡面费用或奖励数值。
    public static class ContentPrimitiveEffectExecutor
    {
        public const string OverrideNextStartPlayer = LuaDomainEffectTypeIds.OverrideNextStartPlayer;
        public const string ActivateFacilityEntry = "effect.facility.activate_entry";
        public const string ChooseResources = "effect.resource.choose_amounts";
        public const string SelectInfluence = "effect.influence.select_action";
        public static void Register(EffectRegistry registry)
        {
            FacilitySelectionEffectExecutor.Register(registry);
            ExplorationSelectionEffectExecutor.Register(registry);
            if (!registry.TryGet(OverrideNextStartPlayer, out _)) registry.Register(new EffectRegistration(OverrideNextStartPlayer, SetNextStart, EffectExecutorKind.Atomic, "1.0.0"));
            Register(registry, SelectInfluence, InfluenceAction);
            Register(registry, ActivateFacilityEntry, ActivateEntry);
            Register(registry, ChooseResources, ChooseResourceAmounts);
            Register(registry, LuaDomainEffectTypeIds.ResourceSell, Sell);
            Register(registry, LuaDomainEffectTypeIds.MoveFacilityCard, CycleFacility);
            Register(registry, LuaDomainEffectTypeIds.MoveCharacterCard, MoveCards);
        }
        private static void Register(EffectRegistry registry, string type, Func<EffectExecutionContext, EffectStepResult> execute)
        {
            if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, execute, EffectExecutorKind.IntrinsicFlow, "1.0.0"));
        }
        private static EffectStepResult SetNextStart(EffectExecutionContext context)
        {
            string target = Text(context.Node.NormalizedArguments, "targetPlayer");
            if (target.StartsWith("p", StringComparison.Ordinal)) target = target.Substring(1);
            if (!int.TryParse(target, out int playerId) || context.State.FindPlayer(playerId) == null) return EffectStepResult.Failed("invalid_player");
            if (Text(context.Node.NormalizedArguments, "scope", "current_round") != "current_round") return EffectStepResult.Failed("unsupported_start_player_scope");
            context.State.EffectRuntime.PendingNextRoundStartPlayerId = playerId;
            return EffectStepResult.Completed(NormalizedValue.CreateInteger(playerId));
        }
        private static EffectStepResult ActivateEntry(EffectExecutionContext context)
        {
            if (context.Node.FlowStage == "activated")
            {
                foreach (var child in context.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                        return EffectStepResult.Failed("facility_entry_child_failed");
                    if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("设施入场尚未完成。");
                }
                return EffectStepResult.Completed(NormalizedValue.CreateString("activated"));
            }
            string id = Text(context.Node.NormalizedArguments, "instanceId");
            foreach (var placement in context.State.Map.Facilities)
            {
                if (placement == null || placement.ContentInstanceId != id || placement.PlayerId != context.Node.PlayerId) continue;
                var definition = FacilityCardDatabase.Get(placement.FacilityCardId);
                if (definition == null || !definition.HasEntryEffect) break;
                return EffectStepResult.Continue("activated").AddChild(FacilityEntryEffectSpecFactory.Activate(
                    context.Node.PlayerId, placement.FacilityCardId, id, placement.CityBoardSlotIndex, context.Node.SourceId));
            }
            return EffectStepResult.Failed("facility_entry_target_invalid");
        }
        private static string Text(NormalizedValue args, string key, string fallback = "")
        {
            if (args?.Properties != null) foreach (var item in args.Properties) if (item.Name == key) return item.Value?.StringValue ?? fallback;
            return fallback;
        }
        private static int Number(NormalizedValue args, string key, int fallback)
        {
            if (args?.Properties != null) foreach (var item in args.Properties) if (item.Name == key && item.Value.Kind == NormalizedValueKind.Integer) return (int)item.Value.IntegerValue;
            return fallback;
        }
        private static string Answer(EffectExecutionContext context)
        {
            var answer = context.GetLatestInteractionAnswer();
            if (answer?.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
            return answer?.Kind == NormalizedValueKind.String ? answer.StringValue : answer?.ReferenceId ?? "";
        }
        private static EffectStepResult Wait(EffectExecutionContext context, List<string> candidates, string stage, string type, string prompt, bool multiple = false)
        {
            var request = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = context.Node.PlayerId,
                Visibility = "owner", PromptKey = prompt, MinSelections = 1, MaxSelections = multiple ? 4 : 1,
                AnswerSchema = multiple ? "candidate_id_array" : "candidate_id" };
            request.CandidateIds.AddRange(candidates);
            return EffectStepResult.Continue(stage).AddInteraction(request);
        }
        private static EffectStepResult InfluenceAction(EffectExecutionContext context)
        {
            var args = context.Node.NormalizedArguments;
            string operation = Text(args, "operation");
            string owner = Text(args, "ownerFilter", "self");
            int count = Number(args, "count", 1);
            if ((operation != "move" && operation != "replace" && operation != "remove") ||
                (owner != "self" && owner != "opponent" && owner != "any") || count < 1 || count > 16)
                return EffectStepResult.Failed("influence_selection_arguments_invalid");
            var map = new MapQueryService(context.State.MapId == StaticMapDefinitions.ThreePlayerMapId ? StaticMapDefinitions.CreateThreePlayerPlaceholder() : StaticMapDefinitions.CreateFourPlayerMap());
            var service = new InfluenceService(map);
            InfluenceIdentity.Ensure(context.State);
            string[] stage = (context.Node.FlowStage ?? "").Split('|');
            int index = stage.Length > 1 ? int.Parse(stage[1], CultureInfo.InvariantCulture) : 0;
            var moved = stage.Length > 2 && stage[2].Length > 0 ? new List<string>(stage[2].Split(',')) : new List<string>();
            if (stage[0] == "awaiting")
            {
                string answer = Answer(context);
                EffectSpec child;
                if (operation == "move")
                {
                    var parts = answer.Split('|');
                    if (parts.Length != 3 || parts[0] != "move") return EffectStepResult.Failed("influence_move_answer_invalid");
                    var target = service.FindInfluence(context.State, parts[1]);
                    if (target == null || target.PlayerId != context.Node.PlayerId || moved.Contains(target.InfluenceId)) return EffectStepResult.Failed("influence_move_source_invalid");
                    child = InfluenceEffectSpecFactory.MoveInfluence(context.Node.PlayerId, target.InfluenceId, parts[2]);
                    if (Text(args, "distinctPolicy") == "instance") moved.Add(target.InfluenceId);
                }
                else
                {
                    var target = service.FindInfluence(context.State, answer);
                    if (target == null || !MatchesOwner(target, context.Node.PlayerId, owner)) return EffectStepResult.Failed("influence_target_invalid");
                    child = operation == "replace" ? InfluenceEffectSpecFactory.ReplaceInfluence(context.Node.PlayerId, target.InfluenceId, context.Node.SourceId) : InfluenceEffectSpecFactory.RemoveInfluence(context.Node.PlayerId, target.InfluenceId, context.Node.SourceId);
                }
                return EffectStepResult.Continue("applied|" + (index + 1) + "|" + string.Join(",", moved)).AddChild(child);
            }
            if (index >= count) return EffectStepResult.Completed(NormalizedValue.CreateInteger(index));
            var candidates = new List<string>();
            foreach (var influence in context.State.Map.Influences)
            {
                if (!MatchesOwner(influence, context.Node.PlayerId, owner) || moved.Contains(influence.InfluenceId)) continue;
                if (operation != "move") { candidates.Add(influence.SlotId); continue; }
                if (influence.PlayerId != context.Node.PlayerId) continue;
                foreach (var location in map.Map.Locations)
                    for (int i = 0; i < location.InfluenceSlotCount; i++) AddMove(candidates, context, service, influence.SlotId, InfluenceService.GetLocationSlotId(location.LocationId, i));
                foreach (var route in map.Map.Routes)
                    for (int i = 0; i < route.InfluenceSlotCount; i++) AddMove(candidates, context, service, influence.SlotId, InfluenceService.GetRouteSlotId(route.RouteId, i));
            }
            candidates.Sort(StringComparer.Ordinal);
            if (candidates.Count == 0) return EffectStepResult.Completed(NormalizedValue.CreateInteger(index));
            string prompt = index == 0 ? Text(args, "promptKey", "effect.influence.choose") : Text(args, "nextPromptKey", Text(args, "promptKey", "effect.influence.choose"));
            return Wait(context, candidates, "awaiting|" + index + "|" + string.Join(",", moved), Text(args, "interactionType", "lua.choice") + "." + index, prompt);
        }
        private static bool MatchesOwner(InfluencePlacement target, int player, string policy) => target != null &&
            (policy == "any" || (policy == "self" ? target.PlayerId == player : target.PlayerId != player));
        private static void AddMove(List<string> list, EffectExecutionContext context, InfluenceService service, string from, string to)
        {
            if (service.CanMoveAtomically(context.State, context.Node.PlayerId, new[] { new InfluenceMoveRequest(from, to) }).IsValid) list.Add("move|" + from + "|" + to);
        }
        private static EffectStepResult ChooseResourceAmounts(EffectExecutionContext context)
        {
            int amount = Number(context.Node.NormalizedArguments, "amount", 0);
            if (amount < 1 || amount > 99) return EffectStepResult.Failed("invalid_resource_choice_amount");
            if (context.Node.FlowStage == "applied") return EffectStepResult.Completed(NormalizedValue.CreateInteger(amount));
            if (context.Node.FlowStage == "awaiting")
            {
                string encoded = Answer(context);
                if (!YC.Domain.Interactions.ResourceAllocationAnswer.TryParse(encoded,
                    new[] { "Originium", "OriginiumShard", "Iron" }, amount, out var amounts, out _))
                    return EffectStepResult.Failed("invalid_resource_allocation");
                var result = EffectStepResult.Continue("applied");
                foreach (var entry in amounts)
                    if (entry.Value > 0) result.AddChild(ResourceEffectSpecFactory.Gain(context.Node.PlayerId, (YC.Domain.Rules.ResourceType)Enum.Parse(typeof(YC.Domain.Rules.ResourceType), entry.Key), entry.Value, context.Node.SourceId));
                return result;
            }
            var request = new EffectInteractionSpec { InteractionTypeId = "facility.entry.choice", AnsweringPlayerId = context.Node.PlayerId,
                Visibility = "owner", PromptKey = "effect.resource.choose_amounts", AnswerSchema = "resource_allocation", MinSelections = 1, MaxSelections = 1 };
            request.CandidateIds.AddRange(new[] { "Originium", "OriginiumShard", "Iron", "total:" + amount });
            return EffectStepResult.Continue("awaiting").AddInteraction(request);
        }

        private static EffectStepResult MoveCards(EffectExecutionContext context)
        {
            if (Text(context.Node.NormalizedArguments, "sourceZone") != "discard" || Text(context.Node.NormalizedArguments, "destinationZone") != "hand") return EffectStepResult.Failed("unsupported_character_card_zone_move");
            var player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null) return EffectStepResult.Failed("invalid_player");
            var cards = new List<string>(player.DiscardCardIds);
            cards.RemoveAll(id => id == player.CoveredCharacterCardId);
            foreach (var id in cards) { player.DiscardCardIds.Remove(id); if (!player.HandCardIds.Contains(id)) player.HandCardIds.Add(id); }
            return EffectStepResult.Completed(NormalizedValue.CreateInteger(cards.Count));
        }
        private static EffectStepResult CycleFacility(EffectExecutionContext context)
        {
            if (Text(context.Node.NormalizedArguments, "sourceZone") != "supply" || Text(context.Node.NormalizedArguments, "destinationZone") != "deck_bottom") return EffectStepResult.Failed("unsupported_facility_card_zone_move");
            if (context.Node.FlowStage == "awaiting")
            {
                string card = Answer(context);
                if (!context.State.Decks.FacilitySupply.Contains(card)) return EffectStepResult.Failed("facility_supply_target_invalid");
                FacilitySupplyService.ReturnSupplyCardToDeck(context.State.Decks.FacilitySupply, context.State.Decks.FacilityDeck, card);
                return EffectStepResult.Completed(NormalizedValue.CreateString(card));
            }
            if (context.State.Decks.FacilitySupply.Count == 0) return EffectStepResult.Completed(NormalizedValue.CreateString("no_target"));
            return Wait(context, new List<string>(context.State.Decks.FacilitySupply), "awaiting", Text(context.Node.NormalizedArguments, "interactionType", "lua.choice"), Text(context.Node.NormalizedArguments, "promptKey", "effect.facility.choose_supply"));
        }
        private static EffectStepResult Sell(EffectExecutionContext context)
        {
            var player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null) return EffectStepResult.Failed("invalid_player");
            string[] ids = { "originium", "originium-shard", "iron", "pure-originium" };
            if (context.Node.FlowStage == "awaiting")
            {
                var answer = context.GetLatestInteractionAnswer();
                var values = answer?.Kind == NormalizedValueKind.Array ? answer.Items : new List<NormalizedValue> { answer };
                var amounts = new int[4]; var seen = new HashSet<int>();
                if (values.Count == 0 || values.Count > 4) return EffectStepResult.Failed("sale_answer_invalid");
                foreach (var value in values)
                {
                    var parts = (value?.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value?.StringValue ?? "").Split('|');
                    if (parts.Length != 3 || parts[0] != "sale" || !int.TryParse(parts[2], out var amount) || amount < 0) return EffectStepResult.Failed("sale_answer_invalid");
                    int i = Array.IndexOf(ids, parts[1]);
                    if (i < 0 || !seen.Add(i)) return EffectStepResult.Failed("sale_answer_invalid");
                    amounts[i] = amount;
                }
                var result = new ResourceSaleService().Sell(player.Resources, new ResourceSaleRequest(amounts[0], amounts[1], amounts[2], amounts[3]));
                return result.Succeeded ? EffectStepResult.Completed(NormalizedValue.CreateInteger(result.Revenue)) : EffectStepResult.Failed("sale_insufficient_resources");
            }
            var candidates = new List<string> { "sale|originium|0" };
            int[] maximums = { player.Resources.Originium, player.Resources.OriginiumShard, player.Resources.Iron, player.Resources.PureOriginium };
            for (int i = 0; i < ids.Length; i++) for (int amount = 1; amount <= maximums[i]; amount++) candidates.Add("sale|" + ids[i] + "|" + amount);
            return Wait(context, candidates, "awaiting", Text(context.Node.NormalizedArguments, "interactionType", "lua.choice"), Text(context.Node.NormalizedArguments, "promptKey", "effect.resource.sell"), true);
        }
    }
}
