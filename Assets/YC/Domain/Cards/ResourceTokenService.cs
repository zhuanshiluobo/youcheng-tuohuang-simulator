using System;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public sealed class ResourceTokenService
    {
        public bool HasResourceToken(MapRuntimeState map, string locationId)
        {
            for (var i = 0; i < map.ResourceTokens.Count; i++)
            {
                if (map.ResourceTokens[i].LocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        public ResourceTokenState PlaceToken(MapRuntimeState map, string locationId, ResourceType resourceType, int amount)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (string.IsNullOrEmpty(locationId)) throw new ArgumentNullException(nameof(locationId));
            if (amount < 1) throw new ArgumentOutOfRangeException(nameof(amount));

            var existing = FindToken(map, locationId);
            if (existing != null)
            {
                return existing;
            }

            var token = new ResourceTokenState
            {
                LocationId = locationId,
                ResourceType = resourceType,
                Amount = amount
            };

            map.ResourceTokens.Add(token);
            return token;
        }

        public ResourceTokenState FindToken(MapRuntimeState map, string locationId)
        {
            for (var i = 0; i < map.ResourceTokens.Count; i++)
            {
                if (map.ResourceTokens[i].LocationId == locationId)
                {
                    return map.ResourceTokens[i];
                }
            }

            return null;
        }
    }
}
