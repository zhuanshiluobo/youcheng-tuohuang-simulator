using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    /// <summary>
    /// 设施入场效果的规则入口。确定性效果立即结算；需要玩家选择的效果打开设施待选会话。
    /// </summary>
    public sealed class FacilityEntryEffectService
    {
        private readonly FacilityEntryEffectAvailabilityService availabilityService;

        public FacilityEntryEffectService()
        {
        }

        public FacilityEntryEffectService(FacilityEntryEffectAvailabilityService availabilityService)
        {
            this.availabilityService = availabilityService ??
                throw new ArgumentNullException(nameof(availabilityService));
        }

        public void Resolve(
            GameState state,
            PlayerState player,
            FacilityCardDefinition facility,
            int cityBoardSlotIndex)
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

            player.Resources.Add(facility.OnBuiltReward);

            switch (facility.EffectId)
            {
                case FacilityCardEffectIds.ClaimStartMarkerAtCleanup:
                    FederalCouncilEffectService.RecordLatestBuilder(state, player.PlayerId);
                    return;
                case FacilityCardEffectIds.GainGoldPerCoreAdjacentFacility:
                    player.Resources.GoldVoucher += CountCoreAdjacentFacilities(state, player.PlayerId) * 4;
                    return;
                case FacilityCardEffectIds.CopyAdjacentEntryEffect:
                    OpenChoice(
                        state,
                        player,
                        facility,
                        cityBoardSlotIndex,
                        FacilityPendingChoiceTypes.CopyAdjacentEntryEffect,
                        FindReplayableAdjacentSlots(state, player.PlayerId, cityBoardSlotIndex));
                    return;
                case FacilityCardEffectIds.BuildAdditionalFacility:
                    OpenChoice(
                        state,
                        player,
                        facility,
                        cityBoardSlotIndex,
                        FacilityPendingChoiceTypes.BuildAdditionalFacility,
                        FindAdditionalBuildOptions(state, player, facility.FacilityId));
                    return;
                case FacilityCardEffectIds.BuildExtensionHub:
                    var extensionOptions = FindAvailableExtensionHubs(state);
                    extensionOptions.Add(FacilityPendingChoiceTypes.SkipOption);
                    OpenChoice(
                        state,
                        player,
                        facility,
                        cityBoardSlotIndex,
                        FacilityPendingChoiceTypes.BuildExtensionHub,
                        extensionOptions);
                    return;
                case FacilityCardEffectIds.SellResources:
                    OpenChoice(
                        state,
                        player,
                        facility,
                        cityBoardSlotIndex,
                        FacilityPendingChoiceTypes.SellResources,
                        new List<string>
                        {
                            FacilityPendingChoiceTypes.ConfirmOption,
                            FacilityPendingChoiceTypes.SkipOption
                        });
                    return;
                case FacilityCardEffectIds.FreeCityMoveAndDeployRouteInfluence:
                    if (GetAvailabilityService(state).HasLegalFreeCityMove(state, player.PlayerId))
                    {
                        OpenChoice(
                            state,
                            player,
                            facility,
                            cityBoardSlotIndex,
                            FacilityPendingChoiceTypes.FreeCityMove,
                            new List<string> { FacilityPendingChoiceTypes.ConfirmOption });
                    }
                    return;
                case FacilityCardEffectIds.ChooseFiveBasicResources:
                    OpenChoice(
                        state,
                        player,
                        facility,
                        cityBoardSlotIndex,
                        FacilityPendingChoiceTypes.ChooseFiveBasicResources,
                        new List<string> { FacilityPendingChoiceTypes.ConfirmOption });
                    return;
                case FacilityCardEffectIds.ReplaceOneInfluence:
                {
                    var mercenaryOptions = GetAvailabilityService(state)
                        .GetReplaceOrDeployOptions(state, player.PlayerId);
                    if (mercenaryOptions.Count == 0)
                    {
                        mercenaryOptions.Add(FacilityPendingChoiceTypes.SkipOption);
                    }

                    OpenChoice(
                        state,
                        player,
                        facility,
                        cityBoardSlotIndex,
                        FacilityPendingChoiceTypes.ReplaceOneInfluence,
                        mercenaryOptions);
                    return;
                }
                case FacilityCardEffectIds.DeployTwoInfluences:
                    if (GetAvailabilityService(state).HasLegalInfluenceDeployment(state, player.PlayerId, 2))
                    {
                        OpenChoice(
                            state,
                            player,
                            facility,
                            cityBoardSlotIndex,
                            FacilityPendingChoiceTypes.DeployTwoInfluences,
                            new List<string> { FacilityPendingChoiceTypes.ConfirmOption });
                    }
                    return;
                case FacilityCardEffectIds.RemoveThenDispatchOrExplore:
                {
                    var warehouseOptions = GetAvailabilityService(state)
                        .GetRemoveOrExploreOptions(state, player.PlayerId);
                    if (warehouseOptions.Count == 0)
                    {
                        warehouseOptions.Add(FacilityPendingChoiceTypes.SkipOption);
                    }

                    OpenChoice(
                        state,
                        player,
                        facility,
                        cityBoardSlotIndex,
                        FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore,
                        warehouseOptions);
                    return;
                }
            }
        }

        private FacilityEntryEffectAvailabilityService GetAvailabilityService(GameState state)
        {
            return availabilityService ?? FacilityEntryEffectAvailabilityService.CreateDefault(state);
        }

        public static List<int> GetOrthogonalNeighborSlotIndexes(int slotIndex)
        {
            var result = new List<int>();
            if (slotIndex < 0 || slotIndex >= BuildFacilityService.CityBoardSlotCount)
            {
                return result;
            }

            var row = slotIndex / BuildFacilityService.CityBoardSlotCountPerRow;
            var column = slotIndex % BuildFacilityService.CityBoardSlotCountPerRow;
            if (row > 0)
            {
                result.Add(slotIndex - BuildFacilityService.CityBoardSlotCountPerRow);
            }

            if (column > 0)
            {
                result.Add(slotIndex - 1);
            }

            if (column + 1 < BuildFacilityService.CityBoardSlotCountPerRow)
            {
                result.Add(slotIndex + 1);
            }

            if (slotIndex + BuildFacilityService.CityBoardSlotCountPerRow < BuildFacilityService.CityBoardSlotCount)
            {
                result.Add(slotIndex + BuildFacilityService.CityBoardSlotCountPerRow);
            }

            return result;
        }

        private static int CountCoreAdjacentFacilities(GameState state, int playerId)
        {
            var neighborSlots = GetOrthogonalNeighborSlotIndexes(BuildFacilityService.CoreCommandTowerCityBoardSlotIndex);
            var count = 0;
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var placement = state.Map.Facilities[i];
                if (placement.PlayerId == playerId && neighborSlots.Contains(placement.CityBoardSlotIndex))
                {
                    count += 1;
                }
            }

            return count;
        }

        private static List<string> FindReplayableAdjacentSlots(GameState state, int playerId, int sourceSlotIndex)
        {
            var result = new List<string>();
            var neighbors = GetOrthogonalNeighborSlotIndexes(sourceSlotIndex);
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var placement = state.Map.Facilities[i];
                if (placement.PlayerId != playerId || !neighbors.Contains(placement.CityBoardSlotIndex))
                {
                    continue;
                }

                FacilityCardDefinition target;
                if (!FacilityCardDatabase.TryGet(placement.FacilityCardId, out target) ||
                    !target.HasEntryEffect ||
                    string.Equals(target.Color, "rainbow", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(placement.CityBoardSlotIndex.ToString());
            }

            return result;
        }

        private static List<string> FindAdditionalBuildOptions(
            GameState state,
            PlayerState player,
            string sourceFacilityId)
        {
            var result = new List<string>();
            if (BuildFacilityService.FindFirstEmptyCityBoardSlot(state, player.PlayerId) < 0)
            {
                return result;
            }

            var costService = new FacilityBuildCostService();
            for (var i = 0; i < state.Decks.FacilitySupply.Count; i++)
            {
                var facilityId = state.Decks.FacilitySupply[i];
                FacilityCardDefinition candidate;
                if (string.Equals(facilityId, sourceFacilityId, StringComparison.Ordinal) ||
                    !FacilityCardDatabase.TryGet(facilityId, out candidate) ||
                    FacilityCardDatabase.PlayerHasBuiltUniqueFacility(player, candidate))
                {
                    continue;
                }

                var resourceCost = costService.GetEffectiveResourceCost(state, player, candidate);
                if (player.Resources.CanPay(resourceCost) ||
                    player.Resources.GoldVoucher >= candidate.GoldVoucherCost)
                {
                    result.Add(facilityId);
                }
            }

            return result;
        }

        private static List<string> FindAvailableExtensionHubs(GameState state)
        {
            var candidates = new[]
            {
                FacilityCardDatabase.ExtensionHubBlue,
                FacilityCardDatabase.ExtensionHubYellow,
                FacilityCardDatabase.ExtensionHubRed
            };
            var result = new List<string>();
            for (var i = 0; i < candidates.Length; i++)
            {
                if (!IsFacilityBuilt(state, candidates[i]))
                {
                    result.Add(candidates[i]);
                }
            }

            return result;
        }

        private static bool IsFacilityBuilt(GameState state, string facilityId)
        {
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                if (state.Map.Facilities[i].FacilityCardId == facilityId)
                {
                    return true;
                }
            }

            return false;
        }

        private static void OpenChoice(
            GameState state,
            PlayerState player,
            FacilityCardDefinition facility,
            int cityBoardSlotIndex,
            string choiceType,
            List<string> optionIds)
        {
            if (optionIds == null || optionIds.Count == 0)
            {
                return;
            }

            state.PendingCardSession = new PendingCardSessionState
            {
                SessionId = Guid.NewGuid().ToString("N"),
                ScenarioId = FacilityPendingChoiceTypes.ScenarioId,
                ChoiceType = choiceType,
                CardId = facility.FacilityId,
                PlayerId = player.PlayerId,
                TargetId = cityBoardSlotIndex.ToString(),
                OptionIds = optionIds,
                ContextData = new List<StringKeyValuePair>
                {
                    new StringKeyValuePair { Key = "effectId", Value = facility.EffectId },
                    new StringKeyValuePair { Key = "sourceSlotIndex", Value = cityBoardSlotIndex.ToString() }
                }
            };

            state.PendingChoice = null;
        }
    }
}
