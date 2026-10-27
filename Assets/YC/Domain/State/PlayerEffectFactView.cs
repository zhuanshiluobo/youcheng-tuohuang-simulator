using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.Effects;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.State
{
    [Serializable]
    public sealed class PlayerEffectFactView
    {
        public string FactId = string.Empty, SourceItemId = string.Empty, DefinitionId = string.Empty;
        public string SourceEffectId = string.Empty, AbilityId = string.Empty, BindingId = string.Empty;
        public int DetailPlayerId = -1;
        public PlayerDetailActivation Activation;
        public List<string> CoverSlotIds = new List<string>();
        public List<string> GrantingRuleFactIds = new List<string>();
    }

    public static class PlayerEffectFactProjector
    {
        public static List<PlayerEffectFactView> Project(GameState state, GameStateView visible)
        {
            var result = new List<PlayerEffectFactView>();
            if (state?.EffectRuntime == null || visible == null || visible.ViewerRole == GameStateViewerRole.Unknown) return result;
            var items = visible.ItemZones.Items.ToDictionary(item => item.ItemId);
            foreach (var use in state.CharacterUses)
            {
                if (!items.TryGetValue(use.CardInstanceId, out var item) || item.ControllerSubject.Kind != ItemSubjectKind.Player) continue;
                foreach (var mode in use.Modes.Where(mode => mode.Activated && mode.SubscriptionActive))
                {
                    if (!CharacterLifecycleService.IsSubscriptionActive(state, item.ItemId, mode.AbilityId)) continue;
                    var fact = new PlayerEffectFactView { SourceItemId = item.ItemId, DefinitionId = item.DefinitionId,
                        SourceEffectId = use.OwnerEffectId, AbilityId = mode.AbilityId, DetailPlayerId = item.ControllerSubject.PlayerId,
                        Activation = PlayerDetailActivation.Subscription,
                        FactId = StableIdFactory.Create("player_effect", item.ItemId, mode.AbilityId) };
                    if (result.Any(value => value.FactId == fact.FactId)) continue;
                    var binding = visible.FacilityCharacterBindings.Find(value => value.Active && value.CharacterItemId == item.ItemId);
                    fact.BindingId = binding?.BindingId ?? string.Empty;
                    // 特殊盖槽的来源Effect必须确实属于该载体，不按角色名推断。
                    foreach (var slot in visible.CharacterCoverSlots.Where(slot => slot.Reusable))
                    {
                        var original = state.CharacterCoverSlots.Find(value => value.SlotId == slot.SlotId);
                        if (original?.SourceEffectId == "character-card:" + item.ItemId) { fact.CoverSlotIds.Add(slot.SlotId); continue; }
                        var source = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == original?.SourceEffectId);
                        var visited = new HashSet<string>();
                        while (source != null && visited.Add(source.EffectId))
                        {
                            if (source.SourceId == "character-card:" + item.ItemId || Value(source.NormalizedArguments, "cardInstanceId")?.ReferenceId == item.ItemId)
                            { fact.CoverSlotIds.Add(slot.SlotId); break; }
                            source = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == source.ParentEffectId);
                        }
                    }
                    result.Add(fact);
                }
            }
            foreach (var node in state.EffectRuntime.EffectNodes)
            foreach (var timing in node.TimingBindings.Where(binding => binding.Status == "registered" || binding.Status == "attached"))
            {
                var id = Value(node.NormalizedArguments, "cardInstanceId")?.ReferenceId;
                // 公开弃牌/回盒仍可见；秘密回盒及未获准查看的来源不能经义务投影泄露。
                if (id == null || !items.TryGetValue(id, out var item)) continue;
                result.Add(new PlayerEffectFactView { FactId = timing.BindingId, SourceItemId = id,
                    DefinitionId = item.DefinitionId, SourceEffectId = node.EffectId, AbilityId = timing.AbilityId,
                    DetailPlayerId = timing.ScopedPlayerId, Activation = PlayerDetailActivation.Delayed });
            }
            if (state.ContentDefinitions?.AbilitySources.Count > 0)
            {
                // 与原效果事实复用同一次合法来源查询；SourceKey仅原值关联，不拆解猜牌。
                var sources = AbilitySourceService.QuerySources(state, state.ContentDefinitions.AbilitySources);
                var byKey = sources.ToDictionary(s => s.SourceKey, StringComparer.Ordinal);
                var rulesByItem = (visible.PlayerRules ?? new List<PlayerRuleFactView>()).GroupBy(r => r.SourceItemId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                foreach (var source in sources)
                {
                    if (!items.ContainsKey(source.ItemId)) continue;
                    var declaration = state.ContentDefinitions.AbilitySources.Single(d => d.AbilityId == source.AbilityId);
                    var activation = declaration.AuthorizationPolicy == "character_subscription" ? PlayerDetailActivation.Subscription : PlayerDetailActivation.Held;
                    if (result.Any(f => f.SourceItemId == source.ItemId && f.AbilityId == source.AbilityId && f.DetailPlayerId == source.AuthorizedPlayerId && f.Activation == activation)) continue;
                    var fact = new PlayerEffectFactView { FactId = source.SourceKey, SourceItemId = source.ItemId, DefinitionId = source.DefinitionId,
                        AbilityId = source.AbilityId, DetailPlayerId = source.AuthorizedPlayerId, Activation = activation };
                    foreach (string key in source.GrantingSourceRefs)
                        if (byKey.TryGetValue(key, out var grant) && grant.AuthorizedPlayerId == source.AuthorizedPlayerId &&
                            items.ContainsKey(grant.ItemId) && rulesByItem.TryGetValue(grant.ItemId, out var rules))
                            fact.GrantingRuleFactIds.AddRange(rules.Where(r => r.RuleOwnerItemId == source.ItemId && r.DetailPlayerId == source.AuthorizedPlayerId).Select(r => r.FactId));
                    fact.GrantingRuleFactIds = fact.GrantingRuleFactIds.Distinct(StringComparer.Ordinal).ToList();
                    result.Add(fact);
                }
            }
            return result;
        }
    }
}
