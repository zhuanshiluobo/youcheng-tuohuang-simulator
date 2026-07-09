using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.CityStyles
{
    public sealed class CityStylePatternMatcher
    {
        public CityStyleMatchResult Match(GameState state, int playerId, CityStyleDefinition cityStyle)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (cityStyle == null)
            {
                return CityStyleMatchResult.Failure(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "未知城市样式。"));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return CityStyleMatchResult.Failure(ValidationResult.Failure(
                    CommandErrorCode.InvalidPlayer,
                    "宣告玩家不存在。"));
            }

            var requirement = cityStyle.DeclarationRequirement ?? new CityStyleRequirement();
            var requiredCount = requirement.RequiredPatternCells != null && requirement.RequiredPatternCells.Count > 0
                ? requirement.RequiredPatternCells.Count
                : Math.Max(1, requirement.RequiredFacilityCount);
            var candidates = BuildCandidates(state, player, requirement);
            if (candidates.Count < requiredCount)
            {
                return CityStyleMatchResult.Failure(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "满足基础条件的设施数量不足。"));
            }

            var selected = FindMatchingCombination(candidates, requirement, requiredCount);
            if (selected == null)
            {
                return CityStyleMatchResult.Failure(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "已建设施的类型或城市面板位置不满足该样式。"));
            }

            return CityStyleMatchResult.Success(selected);
        }

        private static List<CityStyleFacilityCandidate> BuildCandidates(
            GameState state,
            PlayerState player,
            CityStyleRequirement requirement)
        {
            var result = new List<CityStyleFacilityCandidate>();
            var usedSlots = BuildUsedSlotSet(player);
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var placement = state.Map.Facilities[i];
                if (placement.PlayerId != player.PlayerId ||
                    placement.CityBoardSlotIndex < 0 ||
                    usedSlots.Contains(placement.CityBoardSlotIndex))
                {
                    continue;
                }

                FacilityCardDefinition facility;
                if (!FacilityCardDatabase.TryGet(placement.FacilityCardId, out facility))
                {
                    continue;
                }

                if (!MatchesResourceTypes(facility, requirement.RequiredResourceTypes))
                {
                    continue;
                }

                if (!MatchesRequiredSlots(placement.CityBoardSlotIndex, requirement.RequiredCityBoardSlotIndexes))
                {
                    continue;
                }

                result.Add(new CityStyleFacilityCandidate
                {
                    FacilityId = placement.FacilityCardId,
                    CityBoardSlotIndex = placement.CityBoardSlotIndex,
                    Definition = facility
                });
            }

            return result;
        }

        private static HashSet<int> BuildUsedSlotSet(PlayerState player)
        {
            var result = new HashSet<int>();
            if (player == null || player.DeclaredCityStyles == null)
            {
                return result;
            }

            for (var i = 0; i < player.DeclaredCityStyles.Count; i++)
            {
                var declaration = player.DeclaredCityStyles[i];
                if (declaration == null || declaration.UsedCityBoardSlotIndexes == null)
                {
                    continue;
                }

                for (var slotIndex = 0; slotIndex < declaration.UsedCityBoardSlotIndexes.Count; slotIndex++)
                {
                    result.Add(declaration.UsedCityBoardSlotIndexes[slotIndex]);
                }
            }

            return result;
        }

        private static List<CityStyleFacilityCandidate> FindMatchingCombination(
            List<CityStyleFacilityCandidate> candidates,
            CityStyleRequirement requirement,
            int requiredCount)
        {
            var selected = new List<CityStyleFacilityCandidate>();
            return Search(candidates, requirement, requiredCount, 0, selected);
        }

        private static List<CityStyleFacilityCandidate> Search(
            List<CityStyleFacilityCandidate> candidates,
            CityStyleRequirement requirement,
            int requiredCount,
            int startIndex,
            List<CityStyleFacilityCandidate> selected)
        {
            if (selected.Count == requiredCount)
            {
                return SatisfiesCombination(selected, requirement)
                    ? new List<CityStyleFacilityCandidate>(selected)
                    : null;
            }

            for (var i = startIndex; i < candidates.Count; i++)
            {
                selected.Add(candidates[i]);
                var result = Search(candidates, requirement, requiredCount, i + 1, selected);
                if (result != null)
                {
                    return result;
                }

                selected.RemoveAt(selected.Count - 1);
            }

            return null;
        }

        private static bool SatisfiesCombination(
            List<CityStyleFacilityCandidate> facilities,
            CityStyleRequirement requirement)
        {
            return SatisfiesEffectTypes(facilities, requirement.RequiredEffectTypes) &&
                   SatisfiesRequiredSlotCoverage(facilities, requirement.RequiredCityBoardSlotIndexes) &&
                   SatisfiesColorPattern(facilities, requirement.RequiredPatternCells) &&
                   SatisfiesSameRow(facilities, requirement.RequireSameCityBoardRow);
        }

        private static bool SatisfiesColorPattern(
            List<CityStyleFacilityCandidate> facilities,
            List<CityStylePatternCell> patternCells)
        {
            if (patternCells == null || patternCells.Count == 0)
            {
                return true;
            }

            if (facilities.Count != patternCells.Count)
            {
                return false;
            }

            var rowCount = BuildFacilityService.CityBoardSlotCount / BuildFacilityService.CityBoardSlotCountPerRow;
            for (var anchorRow = 0; anchorRow < rowCount; anchorRow++)
            {
                for (var anchorColumn = 0; anchorColumn < BuildFacilityService.CityBoardSlotCountPerRow; anchorColumn++)
                {
                    if (SatisfiesColorPatternAtAnchor(facilities, patternCells, anchorRow, anchorColumn, rowCount))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool SatisfiesColorPatternAtAnchor(
            List<CityStyleFacilityCandidate> facilities,
            List<CityStylePatternCell> patternCells,
            int anchorRow,
            int anchorColumn,
            int rowCount)
        {
            var usedFacilityIndexes = new HashSet<int>();
            for (var cellIndex = 0; cellIndex < patternCells.Count; cellIndex++)
            {
                var cell = patternCells[cellIndex];
                var row = anchorRow + cell.RowOffset;
                var column = anchorColumn + cell.ColumnOffset;
                if (row < 0 ||
                    row >= rowCount ||
                    column < 0 ||
                    column >= BuildFacilityService.CityBoardSlotCountPerRow)
                {
                    return false;
                }

                var slotIndex = row * BuildFacilityService.CityBoardSlotCountPerRow + column;
                var matchedFacilityIndex = FindMatchingFacilityAtSlot(facilities, usedFacilityIndexes, slotIndex, cell);
                if (matchedFacilityIndex < 0)
                {
                    return false;
                }

                usedFacilityIndexes.Add(matchedFacilityIndex);
            }

            return true;
        }

        private static int FindMatchingFacilityAtSlot(
            List<CityStyleFacilityCandidate> facilities,
            HashSet<int> usedFacilityIndexes,
            int slotIndex,
            CityStylePatternCell cell)
        {
            for (var facilityIndex = 0; facilityIndex < facilities.Count; facilityIndex++)
            {
                if (usedFacilityIndexes.Contains(facilityIndex) ||
                    facilities[facilityIndex].CityBoardSlotIndex != slotIndex ||
                    !MatchesFacilityColors(facilities[facilityIndex].Definition, cell.AllowedFacilityColors))
                {
                    continue;
                }

                return facilityIndex;
            }

            return -1;
        }

        private static bool SatisfiesEffectTypes(
            List<CityStyleFacilityCandidate> facilities,
            List<string> requiredEffectTypes)
        {
            if (requiredEffectTypes == null || requiredEffectTypes.Count == 0)
            {
                return true;
            }

            for (var requiredIndex = 0; requiredIndex < requiredEffectTypes.Count; requiredIndex++)
            {
                var required = Normalize(requiredEffectTypes[requiredIndex]);
                var found = false;
                for (var facilityIndex = 0; facilityIndex < facilities.Count; facilityIndex++)
                {
                    var effectType = facilities[facilityIndex].Definition == null
                        ? string.Empty
                        : Normalize(facilities[facilityIndex].Definition.EffectType);
                    if (effectType == required)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SatisfiesRequiredSlotCoverage(
            List<CityStyleFacilityCandidate> facilities,
            List<int> requiredSlotIndexes)
        {
            if (requiredSlotIndexes == null || requiredSlotIndexes.Count == 0)
            {
                return true;
            }

            for (var requiredIndex = 0; requiredIndex < requiredSlotIndexes.Count; requiredIndex++)
            {
                var required = requiredSlotIndexes[requiredIndex];
                var found = false;
                for (var facilityIndex = 0; facilityIndex < facilities.Count; facilityIndex++)
                {
                    if (facilities[facilityIndex].CityBoardSlotIndex == required)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SatisfiesSameRow(List<CityStyleFacilityCandidate> facilities, bool requireSameRow)
        {
            if (!requireSameRow || facilities.Count <= 1)
            {
                return true;
            }

            var row = facilities[0].CityBoardSlotIndex / BuildFacilityService.CityBoardSlotCountPerRow;
            for (var i = 1; i < facilities.Count; i++)
            {
                if (facilities[i].CityBoardSlotIndex / BuildFacilityService.CityBoardSlotCountPerRow != row)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesFacilityColors(FacilityCardDefinition facility, List<string> allowedColors)
        {
            if (allowedColors == null || allowedColors.Count == 0)
            {
                return true;
            }

            if (facility == null || string.IsNullOrEmpty(facility.Color))
            {
                return false;
            }

            var facilityColors = facility.Color.Split(new[] { ',', '/', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (var facilityColorIndex = 0; facilityColorIndex < facilityColors.Length; facilityColorIndex++)
            {
                var facilityColor = Normalize(facilityColors[facilityColorIndex]);
                for (var allowedColorIndex = 0; allowedColorIndex < allowedColors.Count; allowedColorIndex++)
                {
                    if (facilityColor == Normalize(allowedColors[allowedColorIndex]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool MatchesResourceTypes(FacilityCardDefinition facility, List<ResourceType> resourceTypes)
        {
            if (resourceTypes == null || resourceTypes.Count == 0)
            {
                return true;
            }

            for (var i = 0; i < resourceTypes.Count; i++)
            {
                if (GetResourceAmount(facility.ResourceCost, resourceTypes[i]) > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesRequiredSlots(int cityBoardSlotIndex, List<int> requiredSlotIndexes)
        {
            if (requiredSlotIndexes == null || requiredSlotIndexes.Count == 0)
            {
                return true;
            }

            return requiredSlotIndexes.Contains(cityBoardSlotIndex);
        }

        private static int GetResourceAmount(ResourceSet resources, ResourceType type)
        {
            if (resources == null)
            {
                return 0;
            }

            switch (type)
            {
                case ResourceType.Originium:
                    return resources.Originium;
                case ResourceType.OriginiumShard:
                    return resources.OriginiumShard;
                case ResourceType.Iron:
                    return resources.Iron;
                case ResourceType.PureOriginium:
                    return resources.PureOriginium;
                case ResourceType.GoldVoucher:
                    return resources.GoldVoucher;
                default:
                    return 0;
            }
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }
    }
}
