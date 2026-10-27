using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    /// <summary>编译进入本构建的机制版本；仅内容声明或客户端字段不能提供机制。</summary>
    public static class RuleModeReadiness
    {
        public static readonly IReadOnlyList<string> ImplementedCapabilities = Array.AsReadOnly(new[] {
            "item_zones.v1", "item_transfer.v1", "deck_shuffle.v1", "enterprise_rules.v1", "character_lifecycle.v1", "infected_rules.v1", "gameplay_presentation.v1",
            "ability_sources.v1", "declared_actions.v1", "authorized_actions.v1", "ability_rule_queries.v1", "usage_tracks.v1", "cube_appearance.v1", "player_areas.v2", "entrepreneur_selection.v1", "card_markers.v2", "departure_claims.v2",
            // 公共机制已接入；登记不代表五位正文、页面或全局恢复/联机版本已就绪。
            "resource_payment.v2", "resource_payment_total.v2", "resource_payment_sources.v2", "cleanup_repeat.v1", "cleanup_sources.v1", "collection_queries.v1", "facility_layers.v1", "final_scoring.v2",
            "strict_remove_input.shortfall.v1", "payment_sources.read.v1", "card_markers.v3", "fixed_marker_delivery.v3",
            "departure_batch_facts.v2", "character_subscription.link.v1", "item_transfer_batch.v2", "facility_execution_binding.v1",
            "lua_frozen_modules.v1", "reward_grant.v1", "scoring_control.count_facts.v1",
            "entrepreneur_content.T.v1", "entrepreneur_content.A.v1", "entrepreneur_content.D.v1", "entrepreneur_content.P.v1", "entrepreneur_content.M.v1",
            "entrepreneur_ui.v1", "host_recovery.v5", "room_protocol.v6" });
        public static readonly IReadOnlyList<string> EntrepreneurRequirements = Array.AsReadOnly(new[] {
            "entrepreneur_selection.v1", "card_markers.v2", "departure_claims.v2", "resource_payment.v2", "cleanup_repeat.v1", "collection_queries.v1", "facility_layers.v1", "final_scoring.v2",
            "entrepreneur_content.T.v1", "entrepreneur_content.A.v1", "entrepreneur_content.D.v1", "entrepreneur_content.P.v1", "entrepreneur_content.M.v1", "entrepreneur_ui.v1", "host_recovery.v5", "room_protocol.v6" });
        public static string Missing(IEnumerable<string> required) => string.Join(",", required.Distinct().Where(c => !ImplementedCapabilities.Contains(c)).OrderBy(c => c, StringComparer.Ordinal));
        public static void ValidateReady(GameState state)
        {
            if (state.RuleConfiguration?.RuleModeId != "entrepreneur") return;
            string missing = Missing(EntrepreneurRequirements);
            if (missing.Length > 0) throw new InvalidOperationException("rule_mode_not_ready:" + missing);
            ValidateContent(state.ContentDefinitions, state.SetupRecipe);
        }
        public static void ValidateContent(GameContentDefinitions definitions, GameSetupRecipe recipe)
        {
            if (definitions == null || recipe?.EnterpriseEntrance != true || definitions.Entrepreneurs.Count < 4) throw new InvalidOperationException("entrepreneur_content_not_ready");
            EntrepreneurContent.ValidateDefinitions(definitions.Entrepreneurs);
            foreach (var d in definitions.Entrepreneurs)
            {
                if (!definitions.AbilitySources.Any(s => s.DefinitionId == d.DefinitionId && s.EffectiveZones.Contains("core.zone.entrepreneur_active"))) throw new InvalidOperationException("entrepreneur_ability_rules_missing:" + d.DefinitionId);
                foreach (var f in d.RoundGroups.SelectMany(g => g.Candidates))
                    if (f.RuleRefs.Count == 0 || f.RuleRefs.Any(string.IsNullOrWhiteSpace) || f.RuleRefs.Distinct(StringComparer.Ordinal).Count() != f.RuleRefs.Count || !definitions.AbilitySources.Any(s => s.DefinitionId == f.EntityDefinitionId && s.ActiveFaceId == f.FaceId && s.AuthorizationPolicy == "active_selection" && s.EffectiveZones.Contains("core.zone.entrepreneur_active")))
                        throw new InvalidOperationException("entrepreneur_face_rules_missing:" + f.FaceId);
            }
            if (recipe.Entries.Any(e => definitions.Awards.Any(a => a.DefinitionId == e.DefinitionId && !a.RandomAwardPool))) throw new InvalidOperationException("content_granted_award_in_initial_recipe");
        }
    }
}
