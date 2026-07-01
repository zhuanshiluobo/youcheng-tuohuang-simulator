using System;
using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public enum EventEffectKind
    {
        None,
        GainScore,
        GrantResource,
        PlaceInfluence
    }

    public enum EventEffectTargetScope
    {
        None,
        Self,
        Opponents,
        CurrentLocationOrAdjacentRoute,
        AdjacentRoute
    }

    [Serializable]
    public sealed class EventEffect
    {
        public EventEffectKind Kind;
        public ResourceType ResourceType;
        public int Amount;
        public EventEffectTargetScope TargetScope;
        public ResourceType CostResourceType;
        public int CostAmount;

        public static EventEffect GainScore(int amount)
        {
            return new EventEffect
            {
                Kind = EventEffectKind.GainScore,
                Amount = amount,
                TargetScope = EventEffectTargetScope.Self
            };
        }

        public static EventEffect GrantResource(EventEffectTargetScope targetScope, ResourceType resourceType, int amount)
        {
            return new EventEffect
            {
                Kind = EventEffectKind.GrantResource,
                TargetScope = targetScope,
                ResourceType = resourceType,
                Amount = amount
            };
        }

        public static EventEffect PlaceInfluence(EventEffectTargetScope targetScope, int amount, int costAmount = 0, ResourceType costResourceType = ResourceType.GoldVoucher)
        {
            return new EventEffect
            {
                Kind = EventEffectKind.PlaceInfluence,
                TargetScope = targetScope,
                Amount = amount,
                CostAmount = costAmount,
                CostResourceType = costResourceType
            };
        }
    }

    public static class EventEffectUtility
    {
        public static void Apply(GameState state, int playerId, IReadOnlyList<EventEffect> effects)
        {
            if (state == null || effects == null || effects.Count <= 0)
            {
                return;
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return;
            }

            for (var i = 0; i < effects.Count; i++)
            {
                Apply(state, player, effects[i]);
            }
        }

        public static bool HasScoreEffect(IReadOnlyList<EventEffect> effects)
        {
            if (effects == null)
            {
                return false;
            }

            for (var i = 0; i < effects.Count; i++)
            {
                if (effects[i] != null && effects[i].Kind == EventEffectKind.GainScore && effects[i].Amount != 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static string Format(IReadOnlyList<EventEffect> effects)
        {
            if (effects == null || effects.Count <= 0)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            for (var i = 0; i < effects.Count; i++)
            {
                var text = Format(effects[i]);
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }

            return string.Join("；", parts.ToArray());
        }

        private static void Apply(GameState state, PlayerState player, EventEffect effect)
        {
            if (effect == null || effect.Kind == EventEffectKind.None)
            {
                return;
            }

            switch (effect.Kind)
            {
                case EventEffectKind.GainScore:
                    player.Score += effect.Amount;
                    break;
                case EventEffectKind.GrantResource:
                    ApplyResourceEffect(state, player, effect);
                    break;
            }
        }

        private static void ApplyResourceEffect(GameState state, PlayerState player, EventEffect effect)
        {
            switch (effect.TargetScope)
            {
                case EventEffectTargetScope.Self:
                    player.Resources.Set(effect.ResourceType, player.Resources.Get(effect.ResourceType) + effect.Amount);
                    break;
                case EventEffectTargetScope.Opponents:
                    GrantOpponents(state, player.PlayerId, effect.ResourceType, effect.Amount);
                    break;
            }
        }

        private static void GrantOpponents(GameState state, int playerId, ResourceType type, int amount)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                var opponent = state.Players[i];
                if (opponent.PlayerId == playerId)
                {
                    continue;
                }

                opponent.Resources.Set(type, opponent.Resources.Get(type) + amount);
            }
        }

        private static string Format(EventEffect effect)
        {
            if (effect == null || effect.Kind == EventEffectKind.None)
            {
                return string.Empty;
            }

            switch (effect.Kind)
            {
                case EventEffectKind.GainScore:
                    return "获得 " + effect.Amount + " 分数";
                case EventEffectKind.GrantResource:
                    return FormatTarget(effect.TargetScope) + "获得 " + effect.Amount + " " + FormatResource(effect.ResourceType);
                case EventEffectKind.PlaceInfluence:
                    return FormatInfluenceEffect(effect);
                default:
                    return string.Empty;
            }
        }

        private static string FormatInfluenceEffect(EventEffect effect)
        {
            var prefix = string.Empty;
            if (effect.CostAmount > 0)
            {
                prefix = "可以支付 " + effect.CostAmount + " " + FormatResource(effect.CostResourceType) + "来";
            }

            switch (effect.TargetScope)
            {
                case EventEffectTargetScope.CurrentLocationOrAdjacentRoute:
                    return prefix + "在此资源点或相邻的航道上放置 " + effect.Amount + " 个影响力标识";
                case EventEffectTargetScope.AdjacentRoute:
                    return prefix + "在此资源点相邻的航道上放置 " + effect.Amount + " 个影响力标识";
                default:
                    return prefix + "放置 " + effect.Amount + " 个影响力标识";
            }
        }

        private static string FormatTarget(EventEffectTargetScope targetScope)
        {
            switch (targetScope)
            {
                case EventEffectTargetScope.Opponents:
                    return "所有对手";
                case EventEffectTargetScope.Self:
                    return string.Empty;
                default:
                    return string.Empty;
            }
        }

        private static string FormatResource(ResourceType resourceType)
        {
            switch (resourceType)
            {
                case ResourceType.Originium:
                    return "源岩";
                case ResourceType.OriginiumShard:
                    return "源石碎片";
                case ResourceType.Iron:
                    return "异铁";
                case ResourceType.PureOriginium:
                    return "至纯源石";
                case ResourceType.GoldVoucher:
                    return "金券";
                default:
                    return resourceType.ToString();
            }
        }
    }

    [Serializable]
    public sealed class EventCardDefinition
    {
        public string CardId = string.Empty;
        public string Name = string.Empty;
        public string Description = string.Empty;
        public EventColor Color;
        public ResourceType ResourceType;
        public int ResourceAmount = 1;
        public ResourceType RepresentativeResourceType;
        public int RepresentativeResourceAmount = 1;
        public List<string> ChoiceDescriptions = new List<string>();
        public List<ResourceSet> ChoiceRewards = new List<ResourceSet>();
        public List<List<EventEffect>> ChoicePendingEffects = new List<List<EventEffect>>();
    }
}
