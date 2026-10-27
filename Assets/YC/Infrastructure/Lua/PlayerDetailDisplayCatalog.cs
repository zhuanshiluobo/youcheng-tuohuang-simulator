using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Cards;
using YC.Domain.Presentation;

namespace YC.Infrastructure.Lua
{
    public static class PlayerDetailDisplayCatalog
    {
        public const string Version = "player_details.rule_facts.v1.2.0";
        public static IReadOnlyList<PlayerDetailDisplayDefinition> Read(IEnumerable<ExternalContentDefinition> activeDefinitions, IGameplayTextCatalog textCatalog = null)
        {
            var result = new List<PlayerDetailDisplayDefinition>();
            foreach (var source in activeDefinitions)
            {
                var token = source.Data["playerDetails"];
                if (token == null) continue;
                if (!(token is JArray values) || values.Any(value => !(value is JObject)))
                    throw new InvalidOperationException("player_detail_definition_invalid: " + source.DefinitionId);
                foreach (JObject value in values)
                {
                    foreach (string field in new[] { "requiredRuleRef", "requiredRuleBinding" })
                        if (value.GetValue(field, StringComparison.OrdinalIgnoreCase) is JToken fieldValue && fieldValue.Type != JTokenType.String)
                            throw new InvalidOperationException("player_detail_rule_gate_invalid:" + source.DefinitionId);
                    var definition = value.ToObject<PlayerDetailDisplayDefinition>();
                    definition.DefinitionId = source.DefinitionId;
                    if (string.IsNullOrWhiteSpace(definition.DisplayName)) definition.DisplayName = source.DisplayName;
                    bool typed = definition.EffectTextRef != null;
                    if (definition.RequiredRuleRef == null || definition.RequiredRuleBinding != "owner" && definition.RequiredRuleBinding != "grant" ||
                        definition.RequiredRuleRef.Length > 0 && (string.IsNullOrWhiteSpace(definition.RequiredRuleRef) || !typed) ||
                        definition.RequiredRuleBinding == "grant" && (definition.RequiredRuleRef.Length == 0 || string.IsNullOrEmpty(definition.AbilityId)))
                        throw new InvalidOperationException("player_detail_rule_gate_invalid:" + source.DefinitionId);
                    if (typed)
                    {
                        if (!GameplayPresentationNormalizer.TryValidate(definition.EffectTextRef, out var reason))
                            throw new InvalidOperationException("player_detail_text_ref_invalid:" + source.DefinitionId + ":" + reason);
                        if (textCatalog != null && (!textCatalog.TryGetTemplate(definition.EffectTextRef.Key, out var template) ||
                            !template.Parameters.Select(p => p.Name + ":" + p.Kind).OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(
                                definition.EffectTextRef.Arguments.Select(a => a.Name + ":" + a.Kind).OrderBy(v => v, StringComparer.Ordinal))))
                            throw new InvalidOperationException("player_detail_text_ref_missing_or_mismatched:" + source.DefinitionId);
                    }
                    if (string.IsNullOrWhiteSpace(definition.DisplayName) || !typed && string.IsNullOrWhiteSpace(definition.EffectText) ||
                        !Enum.IsDefined(typeof(PlayerDetailDisplayCategory), definition.DisplayCategory) ||
                        !Enum.IsDefined(typeof(PlayerDetailActivation), definition.Activation) ||
                        ((definition.Activation != PlayerDetailActivation.Held || !string.IsNullOrEmpty(definition.AbilityId)) &&
                            !source.Abilities.Any(ability => ability.AbilityId == definition.AbilityId) && !source.AbilitySources.Any(ability => ability.AbilityId == definition.AbilityId)) ||
                        definition.StorageLocalZoneIds == null ||
                        definition.StorageLocalZoneIds.Any(id => !source.CardZones.Any(zone => zone.LocalZoneId == id)) ||
                        result.Any(existing => existing.DefinitionId == definition.DefinitionId && existing.AbilityId == definition.AbilityId && existing.Activation == definition.Activation &&
                            existing.RequiredRuleRef == definition.RequiredRuleRef && existing.RequiredRuleBinding == definition.RequiredRuleBinding))
                        throw new InvalidOperationException("player_detail_definition_invalid: " + source.DefinitionId);
                    result.Add(definition);
                }
            }
            return result;
        }
    }
}
