using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    /// <summary>感染者仅提供主链策略；不持有回合状态或重复推进。</summary>
    public static class InfectedRoundRules
    {
        public static bool IsEnabled(GameState state) => state?.RuleConfiguration?.EnabledExpansionIds?.Contains("infected") == true && (state.RuleConfiguration.RuleModeId == "standard" || state.RuleConfiguration.RuleModeId == "entrepreneur");
        public static int GetSupplyRetirementCount(GameState state, int playerCount) => playerCount == 2 ? 2 : IsEnabled(state) && playerCount == 3 ? 1 : 0;
        public static EffectStepResult RecycleCharacters(EffectExecutionContext context, int playerId)
        {
            var covered = context.State.ItemZones.Items.Where(i => context.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId &&
                z.TemplateId == "core.zone.character_covered" && z.OwnerPlayerId == playerId)).Select(i => i.ItemId).ToList();
            bool recycleDiscard = covered.Count > 0;
            return CharacterLifecycleService.Recycle(context, playerId, recycleDiscard, covered);
        }
    }
}
