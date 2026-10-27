using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    /// <summary>
    /// 统一计算设施的实际资源建设费用。查询与正式结算必须共享本服务。
    /// </summary>
    public sealed class FacilityBuildCostService
    {
        public ResourceSet GetEffectiveResourceCost(
            GameState state,
            PlayerState player,
            FacilityCardDefinition facility)
        {
            return GetEffectiveResourceCost(state, player, facility, null);
        }

        internal ResourceSet GetEffectiveResourceCost(
            GameState state, PlayerState player, FacilityCardDefinition facility, BuildFacilityService.ReadOnlyQuery query)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            if (facility == null)
            {
                throw new ArgumentNullException(nameof(facility));
            }

            return Reduce(facility, query == null ? CountBuiltColors(state, player, null) : query.GetBuiltColorCount(player));
        }

        public ResourceSet GetEffectiveResourceCostForInstances(GameState state, PlayerState player, FacilityCardDefinition facility, IReadOnlyList<string> itemIds)
        {
            if (state == null || player == null || facility == null || itemIds == null) throw new ArgumentNullException();
            var colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in itemIds)
            {
                var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == id);
                if (!FacilityLayerQuery.IsFacility(item) || !FacilityLayerQuery.IsInCity(state, id, player.PlayerId))
                    throw new InvalidOperationException("built_facility_fact_invalid");
                AddBaseColors(colors, FacilityCardDatabase.Get(state, item.DefinitionId).Color);
            }
            return Reduce(facility, colors.Count);
        }
        private static ResourceSet Reduce(FacilityCardDefinition facility, int colorCount)
        {
            var result = facility.ResourceCost.Clone();
            foreach (YC.Domain.Rules.ResourceType resource in Enum.GetValues(typeof(YC.Domain.Rules.ResourceType)))
                result.Set(resource, Math.Max(0, result.Get(resource) - (facility.CostReductionPerDistinctBuiltColor?.Get(resource) ?? 0) * colorCount));
            return result;
        }
        internal static int CountBuiltColors(GameState state, PlayerState player, BuildFacilityService.ReadOnlyQuery query)
        {
            var colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var builtFacilityIds = query == null && ItemZoneLegacyProjection.IsActive(state)
                ? new FacilityBuildQueryService().ResolveBuiltFacts(state, player.PlayerId).ItemIds.ToArray()
                : query == null ? player.BuiltFacilityIds.ToArray() : query.GetBuiltFacilityIds(player);
            for (var i = 0; i < builtFacilityIds.Length; i++)
            {
                FacilityCardDefinition built;
                if (!(query == null
                    ? FacilityCardDatabase.TryGet(state, builtFacilityIds[i], out built)
                    : query.TryGetFacility(builtFacilityIds[i], out built)))
                {
                    continue;
                }

                AddBaseColors(colors, built.Color);
            }

            return colors.Count;
        }

        private static void AddBaseColors(HashSet<string> colors, string encodedColors)
        {
            if (string.IsNullOrEmpty(encodedColors))
            {
                return;
            }

            var values = encodedColors.Split(',');
            for (var i = 0; i < values.Length; i++)
            {
                var color = values[i].Trim().ToLowerInvariant();
                if (color == "blue" || color == "yellow" || color == "red")
                {
                    colors.Add(color);
                }
            }
        }
    }
}
