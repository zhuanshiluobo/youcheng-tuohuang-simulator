using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Cards;

namespace YC.Infrastructure.Lua
{
    public static class PlayerDetailDisplayCatalog
    {
        public static IReadOnlyList<PlayerDetailDisplayDefinition> Read(IEnumerable<ExternalContentDefinition> activeDefinitions)
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
                    var definition = value.ToObject<PlayerDetailDisplayDefinition>();
                    definition.DefinitionId = source.DefinitionId;
                    if (string.IsNullOrWhiteSpace(definition.DisplayName)) definition.DisplayName = source.DisplayName;
                    if (string.IsNullOrWhiteSpace(definition.DisplayName) || string.IsNullOrWhiteSpace(definition.EffectText) ||
                        !Enum.IsDefined(typeof(PlayerDetailDisplayCategory), definition.DisplayCategory) ||
                        !Enum.IsDefined(typeof(PlayerDetailActivation), definition.Activation) ||
                        ((definition.Activation != PlayerDetailActivation.Held || !string.IsNullOrEmpty(definition.AbilityId)) && !source.Abilities.Any(ability => ability.AbilityId == definition.AbilityId)) ||
                        definition.StorageLocalZoneIds == null ||
                        definition.StorageLocalZoneIds.Any(id => !source.CardZones.Any(zone => zone.LocalZoneId == id)) ||
                        result.Any(existing => existing.DefinitionId == definition.DefinitionId && existing.AbilityId == definition.AbilityId && existing.Activation == definition.Activation))
                        throw new InvalidOperationException("player_detail_definition_invalid: " + source.DefinitionId);
                    result.Add(definition);
                }
            }
            return result;
        }
    }
}
