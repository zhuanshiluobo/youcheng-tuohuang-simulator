using System;
using System.Collections.Generic;
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

            var result = facility.ResourceCost.Clone();
            if (facility.Name != "城邦工业区")
            {
                return result;
            }

            var colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < player.BuiltFacilityIds.Count; i++)
            {
                FacilityCardDefinition built;
                if (!FacilityCardDatabase.TryGet(player.BuiltFacilityIds[i], out built))
                {
                    continue;
                }

                AddBaseColors(colors, built.Color);
            }

            result.Originium = Math.Max(0, result.Originium - colors.Count);
            return result;
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
