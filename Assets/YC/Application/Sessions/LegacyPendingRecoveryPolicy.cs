using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    /// <summary>
    /// 当前正式启动器的旧全局Pending恢复边界。只检查旧槽位，不以Effect类型、交互名称或类名拒绝正常节点。
    /// 历史wire值独立保存；Domain撤掉旧技能包装时，不把旧归档误认成新协议。
    /// </summary>
    internal static class LegacyPendingRecoveryPolicy
    {
        internal const string ContractVersion = "legacy-pending-recovery.v1";
        private static readonly HashSet<string> FacilityTypes = new HashSet<string>(StringComparer.Ordinal) {
            "facility_copy_adjacent_entry", "facility_build_additional", "facility_build_extension_hub",
            "facility_sell_resources", "facility_free_city_move", "facility_choose_five_resources",
            "facility_replace_one_influence", "facility_deploy_two_influences", "facility_remove_dispatch_or_explore" };
        private static readonly HashSet<string> CharacterTypes = new HashSet<string>(StringComparer.Ordinal) {
            "character.tin-man.discard", "character.tin-man.first-purchase", "character.tin-man.second-purchase",
            "character.liskarm.cleanup-removal", "character.second-effect.decision", "character.second-effect.execution" };
        private static readonly HashSet<string> SpecialSteps = new HashSet<string>(StringComparer.Ordinal) {
            "await_military_targets", "await_mobilization_target", "await_free_move_target", "await_move_event", "await_route_influence" };

        internal static bool TryValidate(GameState state, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (state == null) return true; // 状态形状由原恢复校验处理。
            if (state.PendingCardSession != null)
                return Reject("PendingCardSession", state.PendingCardSession.ScenarioId, state.PendingCardSession.ChoiceType, out diagnostic);
            if (state.PendingCharacterEffect != null)
                return Reject("PendingCharacterEffect", "", state.PendingCharacterEffect.ChoiceType, out diagnostic);
            if (state.PendingSpecialAction != null)
                return Reject("PendingSpecialAction", state.PendingSpecialAction.SpecialActionId, state.PendingSpecialAction.Step, out diagnostic);
            if (state.PendingChoice != null)
                return Reject("PendingChoice", "", state.PendingChoice.ChoiceType, out diagnostic);
            return true;
        }

        internal static bool TryValidateRaw(JObject state, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (state == null) return true;
            foreach (string field in new[] { "PendingCardSession", "PendingCharacterEffect", "PendingSpecialAction", "PendingChoice" })
            {
                var token = state[field];
                if (token == null || token.Type == JTokenType.Null) continue;
                if (token is not JObject value) return Reject(field, "", "invalid_schema", out diagnostic);
                string type = Text(value, field == "PendingSpecialAction" ? "Step" : "ChoiceType");
                string context = Text(value, field == "PendingSpecialAction" ? "SpecialActionId" : "ScenarioId");
                return Reject(field, context, type, out diagnostic);
            }
            return true;
        }

        private static bool Reject(string field, string context, string type, out string diagnostic)
        {
            string reason;
            if ((field == "PendingCardSession" && context == "facility_entry_effect" || field == "PendingChoice") && FacilityTypes.Contains(type))
                reason = "facility_legacy_handler_not_registered";
            else if ((field == "PendingCharacterEffect" || field == "PendingChoice") && CharacterTypes.Contains(type))
                reason = "character_legacy_lifecycle_not_supported";
            else if (field == "PendingSpecialAction" && SpecialSteps.Contains(type))
                reason = "special_legacy_resolve_not_registered";
            else if (type == "entrance_event")
                reason = "entrance_requires_effect_interaction";
            else if (type == "explore_event" || type == "move_city_event")
                reason = "legacy_card_flow_has_no_supported_entity_origin";
            else reason = "legacy_pending_contract_not_registered";
            diagnostic = HostRecoveryService.LegacyPendingUnsupported + ":" + field + ":" + Safe(context) + ":" + Safe(type) + ":" + reason;
            return false;
        }

        private static string Text(JObject value, string field) => value[field]?.Type == JTokenType.String ? value.Value<string>(field) : "";
        private static string Safe(string value)
        {
            value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
            return value.Length > 128 ? value.Substring(0, 128) : value;
        }
    }
}
