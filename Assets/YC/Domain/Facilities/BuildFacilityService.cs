using System;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public sealed class BuildFacilityService
    {
        public const int CityBoardSlotCount = 12;
        public const int CityBoardSlotCountPerRow = 3;
        public const string PaymentModeAuto = "auto";
        public const string PaymentModeResources = "resources";
        public const string PaymentModeGold = "gold";

        public BuildFacilityResult Build(
            GameState state,
            int playerId,
            string facilityId,
            int cityBoardSlotIndex,
            string paymentMode)
        {
            var validation = Validate(state, playerId, facilityId, cityBoardSlotIndex, paymentMode);
            if (!validation.IsValid)
            {
                return BuildFacilityResult.Failure(validation);
            }

            var player = state.FindPlayer(playerId);
            var facility = FacilityCardDatabase.Get(facilityId);
            var resolvedPaymentMode = ResolvePaymentMode(player, facility, paymentMode);
            var cost = resolvedPaymentMode == PaymentModeGold
                ? new ResourceSet { GoldVoucher = facility.GoldVoucherCost }
                : facility.ResourceCost;

            player.Resources.TryPay(cost);
            player.Resources.Add(facility.OnBuiltReward);
            player.Score += facility.Score;
            player.BuiltFacilityIds.Add(facility.FacilityId);
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = playerId,
                FacilityCardId = facility.FacilityId,
                CityBoardSlotIndex = cityBoardSlotIndex
            });
            state.Decks.FacilitySupply.Remove(facility.FacilityId);
            RefillFacilitySupply(state);

            return BuildFacilityResult.Success(facility, cityBoardSlotIndex, resolvedPaymentMode);
        }

        public static void RefillFacilitySupply(GameState state)
        {
            if (state == null || state.Decks == null)
            {
                return;
            }

            while (state.Decks.FacilitySupply.Count < 6 && state.Decks.FacilityDeck.Count > 0)
            {
                state.Decks.FacilitySupply.Add(state.Decks.FacilityDeck[0]);
                state.Decks.FacilityDeck.RemoveAt(0);
            }
        }

        public ValidationResult Validate(
            GameState state,
            int playerId,
            string facilityId,
            int cityBoardSlotIndex,
            string paymentMode)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "建设玩家不存在。");
            }

            FacilityCardDefinition facility;
            if (!FacilityCardDatabase.TryGet(facilityId, out facility))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "未知设施牌。");
            }

            if (!state.Decks.FacilitySupply.Contains(facility.FacilityId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "设施供应区没有这张设施牌。");
            }

            if (facility.Unique && player.BuiltFacilityIds.Contains(facility.FacilityId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该唯一设施已经建设过。");
            }

            if (cityBoardSlotIndex < 0 || cityBoardSlotIndex >= CityBoardSlotCount)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "城市面板槽位无效。");
            }

            if (IsCityBoardSlotOccupied(state, playerId, cityBoardSlotIndex))
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "城市面板槽位已被占用。");
            }

            if (string.IsNullOrEmpty(ResolvePaymentMode(player, facility, paymentMode)))
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientResource, "资源或金券不足，无法建设该设施。");
            }

            return ValidationResult.Success;
        }

        public static int FindFirstEmptyCityBoardSlot(GameState state, int playerId)
        {
            if (state == null)
            {
                return -1;
            }

            for (var i = 0; i < CityBoardSlotCount; i++)
            {
                if (!IsCityBoardSlotOccupied(state, playerId, i))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string ResolvePaymentMode(PlayerState player, FacilityCardDefinition facility, string paymentMode)
        {
            var normalized = string.IsNullOrEmpty(paymentMode) ? PaymentModeAuto : paymentMode.Trim().ToLowerInvariant();
            if (normalized == PaymentModeResources)
            {
                return player.Resources.CanPay(facility.ResourceCost) ? PaymentModeResources : string.Empty;
            }

            if (normalized == PaymentModeGold)
            {
                return player.Resources.GoldVoucher >= facility.GoldVoucherCost ? PaymentModeGold : string.Empty;
            }

            if (normalized != PaymentModeAuto)
            {
                return string.Empty;
            }

            if (player.Resources.CanPay(facility.ResourceCost))
            {
                return PaymentModeResources;
            }

            return player.Resources.GoldVoucher >= facility.GoldVoucherCost ? PaymentModeGold : string.Empty;
        }

        private static bool IsCityBoardSlotOccupied(GameState state, int playerId, int cityBoardSlotIndex)
        {
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var placement = state.Map.Facilities[i];
                if (placement.PlayerId == playerId &&
                    placement.CityBoardSlotIndex == cityBoardSlotIndex)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
