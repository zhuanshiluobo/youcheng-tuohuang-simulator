using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Domain.Rules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class DeckShuffleEffectExecutor
    {
        public const string TypeId = "effect.deck.shuffle";
        public static void Register(EffectRegistry registry, Func<int, int> next = null)
        {
            if (registry.TryGet(TypeId, out _)) return;
            var hostNext = next;
            registry.Register(new EffectRegistration(TypeId, context => Execute(context, hostNext)));
            registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.ShuffleFacilityDeck, context => Execute(context, hostNext)));
        }
        public static string ValidateArguments(string typeId, NormalizedValue args)
        {
            if (typeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck && Value(args, "apiVersion") == null)
                return args.Properties.All(entry => new[] { "kind", "effectTypeId", "player" }.Contains(entry.Name)) ? "" : "invalid_shuffle_arguments";
            return Value(args, "apiVersion")?.Kind == NormalizedValueKind.Integer && Number(args, "apiVersion") == 1 &&
                Value(args, "deckZone")?.Kind == NormalizedValueKind.Object && Text(Value(args, "deckZone"), "zoneId").Length > 0 &&
                Value(args, "deckZone").Properties.Count == 1 && args.Properties.All(entry => new[] { "kind", "effectTypeId", "apiVersion", "player", "deckZone" }.Contains(entry.Name)) && int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) && player > 0 ? "" : "invalid_shuffle_arguments";
        }
        public static EffectStepResult Execute(EffectExecutionContext context) => Execute(context, null);
        private static EffectStepResult Execute(EffectExecutionContext context, Func<int, int> next)
        {
            next = next ?? (max => GameplayRandomRuntime.Next(context.State, GameplayRandomRuntime.DeckShuffle, max));
            var missing = MissingFields(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (missing.Count > 0) return SkipIncomplete(context, missing);
            string reason = ValidateArguments(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (reason.Length > 0) return EffectStepResult.Failed(reason);
            string playerKey = Text(context.Node.NormalizedArguments, "player", context.Node.PlayerId.ToString(CultureInfo.InvariantCulture));
            if (context.Node.EffectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck && Value(context.Node.NormalizedArguments, "apiVersion") == null && playerKey.StartsWith("p", StringComparison.Ordinal)) playerKey = playerKey.Substring(1);
            if (playerKey != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture) || context.State.FindPlayer(context.Node.PlayerId) == null) throw new InvalidOperationException("shuffle_permission_denied");
            if (!ItemZoneLegacyProjection.IsActive(context.State))
            {
                if (context.Node.EffectTypeId != LuaDomainEffectTypeIds.ShuffleFacilityDeck) return EffectStepResult.Failed("unknown_zone");
                var deck = context.State.Decks.FacilityDeck;
                for (int i = deck.Count - 1; i > 0; i--) { int j = next(i + 1); string swap = deck[i]; deck[i] = deck[j]; deck[j] = swap; }
                return Result(deck.Count);
            }
            var query = new ItemZoneQuery();
            if (!query.TryValidate(context.State, out reason)) throw new InvalidOperationException("item_state_invalid: " + reason);
            string zoneId = context.Node.EffectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck ? FindZone(context.State, "facility_deck").ZoneId : Text(Value(context.Node.NormalizedArguments, "deckZone"), "zoneId");
            var zone = query.FindZone(context.State, zoneId);
            if (zone == null || ZoneTemplateRegistry.CreateBuiltIn(ItemTypeRegistry.CreateBuiltIn()).Find(zone.TemplateId).OrderingKind != ZoneOrderingKind.Deck) return EffectStepResult.Failed("orderingKind_not_deck");
            if (zone.OwnerPlayerId > 0 && zone.OwnerPlayerId != context.Node.PlayerId) throw new InvalidOperationException("shuffle_permission_denied");
            if (context.Node.EffectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck && Value(context.Node.NormalizedArguments, "deckZone") != null && Text(Value(context.Node.NormalizedArguments, "deckZone"), "zoneId") != zoneId) return EffectStepResult.Failed("unsupported_facility_deck");
            var order = context.State.ItemZones.Orders.Single(value => value.ZoneId == zoneId);
            context.Node.ShuffleInputItemIds = new List<string>(order.ItemIds);
            for (int i = order.ItemIds.Count - 1; i > 0; i--) { int j = next(i + 1); string swap = order.ItemIds[i]; order.ItemIds[i] = order.ItemIds[j]; order.ItemIds[j] = swap; }
            if (order.ItemIds.Count > 1) order.OrderRevision++;
            return Result(order.ItemIds.Count);
        }
        public static List<string> MissingFields(string typeId, NormalizedValue args)
        {
            if (Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Number(args, "apiVersion") != 1 || args.Properties.Any(entry => !new[] { "kind", "effectTypeId", "apiVersion", "player", "deckZone" }.Contains(entry.Name))) return new List<string>();
            return new[] { "player", "deckZone" }.Where(key => Value(args, key) == null).ToList();
        }
        private static EffectStepResult Result(int count) => EffectStepResult.Completed(count > 1
            ? Object("changed", NormalizedValue.CreateBoolean(true), "cardCount", NormalizedValue.CreateInteger(count))
            : Object("changed", NormalizedValue.CreateBoolean(false), "cardCount", NormalizedValue.CreateInteger(count), "reasonCode", NormalizedValue.CreateString(count == 0 ? "empty_deck" : "limit_reached")));
    }
}
