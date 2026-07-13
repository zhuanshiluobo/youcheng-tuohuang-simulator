using System;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public interface IFacilityEntryEffectResolver
    {
        void Resolve(GameState state, PlayerState player, FacilityCardDefinition facility, int cityBoardSlotIndex);
    }

    public sealed class FacilityEntryEffectResolver : IFacilityEntryEffectResolver
    {
        private readonly FacilityEntryEffectService service;

        public FacilityEntryEffectResolver()
            : this(new FacilityEntryEffectService())
        {
        }

        public FacilityEntryEffectResolver(FacilityEntryEffectService service)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public void Resolve(GameState state, PlayerState player, FacilityCardDefinition facility, int cityBoardSlotIndex)
        {
            service.Resolve(state, player, facility, cityBoardSlotIndex);
        }
    }

    public sealed class BuildFacilityService
    {
        public const int CityBoardSlotCount = 12;
        public const int CityBoardSlotCountPerRow = 3;
        public const int CoreCommandTowerCityBoardSlotIndex = 7;
        public const string PaymentModeAuto = "auto";
        public const string PaymentModeResources = "resources";
        public const string PaymentModeGold = "gold";

        private readonly IFacilityEntryEffectResolver entryEffectResolver;
        private readonly FacilityBuildCostService buildCostService;

        public BuildFacilityService()
            : this(new FacilityEntryEffectResolver(), new FacilityBuildCostService())
        {
        }

        public BuildFacilityService(IFacilityEntryEffectResolver entryEffectResolver)
            : this(entryEffectResolver, new FacilityBuildCostService())
        {
        }

        public BuildFacilityService(
            IFacilityEntryEffectResolver entryEffectResolver,
            FacilityBuildCostService buildCostService)
        {
            this.entryEffectResolver = entryEffectResolver ?? throw new ArgumentNullException(nameof(entryEffectResolver));
            this.buildCostService = buildCostService ?? throw new ArgumentNullException(nameof(buildCostService));
        }

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
            var effectiveResourceCost = buildCostService.GetEffectiveResourceCost(state, player, facility);
            var resolvedPaymentMode = ResolvePaymentMode(player, facility, effectiveResourceCost, paymentMode);
            var cost = resolvedPaymentMode == PaymentModeGold
                ? new ResourceSet { GoldVoucher = facility.GoldVoucherCost }
                : effectiveResourceCost;

            // 规则书顺序：支付 -> 放置 -> 得分 -> 入场效果 -> 补充供应区。
            player.Resources.TryPay(cost);
            player.BuiltFacilityIds.Add(facility.FacilityId);
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = playerId,
                FacilityCardId = facility.FacilityId,
                CityBoardSlotIndex = cityBoardSlotIndex
            });
            player.Score += facility.Score;
            entryEffectResolver.Resolve(state, player, facility, cityBoardSlotIndex);
            ReplaceBuiltFacilityInSupply(state, facility.FacilityId);

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

        private static void ReplaceBuiltFacilityInSupply(GameState state, string facilityId)
        {
            var supplyIndex = state.Decks.FacilitySupply.IndexOf(facilityId);
            if (supplyIndex < 0)
            {
                return;
            }

            if (state.Decks.FacilityDeck.Count > 0)
            {
                state.Decks.FacilitySupply[supplyIndex] = state.Decks.FacilityDeck[0];
                state.Decks.FacilityDeck.RemoveAt(0);
            }
            else
            {
                state.Decks.FacilitySupply.RemoveAt(supplyIndex);
            }

            RefillFacilitySupply(state);
        }

        public static void EnsureInitialCoreCommandTowers(GameState state)
        {
            if (state == null)
            {
                return;
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                EnsureInitialCoreCommandTower(state, state.Players[i]);
            }
        }

        public static void EnsureInitialCoreCommandTower(GameState state, PlayerState player)
        {
            if (state == null || player == null)
            {
                return;
            }

            if (!player.BuiltFacilityIds.Contains(FacilityCardDatabase.CoreCommandTower))
            {
                player.BuiltFacilityIds.Add(FacilityCardDatabase.CoreCommandTower);
            }

            if (HasFacilityAtCityBoardSlot(state, player.PlayerId, CoreCommandTowerCityBoardSlotIndex))
            {
                return;
            }

            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = player.PlayerId,
                FacilityCardId = FacilityCardDatabase.CoreCommandTower,
                CityBoardSlotIndex = CoreCommandTowerCityBoardSlotIndex
            });
        }

        public ValidationResult Validate(
            GameState state,
            int playerId,
            string facilityId,
            int cityBoardSlotIndex,
            string paymentMode)
        {
            var facilityValidation = ValidateFacility(state, playerId, facilityId);
            if (!facilityValidation.IsValid)
            {
                return facilityValidation;
            }

            var slotValidation = ValidateCityBoardSlot(state, playerId, facilityId, cityBoardSlotIndex);
            if (!slotValidation.IsValid)
            {
                return slotValidation;
            }

            return ValidatePaymentMode(state, playerId, facilityId, paymentMode);
        }

        public ValidationResult ValidateFacility(GameState state, int playerId, string facilityId)
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

            if (FacilityCardDatabase.PlayerHasBuiltUniqueFacility(player, facility))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该唯一设施已经建设过。");
            }

            return ValidationResult.Success;
        }

        public ValidationResult ValidateCityBoardSlot(
            GameState state,
            int playerId,
            string facilityId,
            int cityBoardSlotIndex)
        {
            var facilityValidation = ValidateFacility(state, playerId, facilityId);
            if (!facilityValidation.IsValid)
            {
                return facilityValidation;
            }

            if (cityBoardSlotIndex < 0 || cityBoardSlotIndex >= CityBoardSlotCount)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "城市面板槽位无效。");
            }

            if (IsCityBoardSlotOccupied(state, playerId, cityBoardSlotIndex))
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "城市面板槽位已被占用。");
            }

            return ValidationResult.Success;
        }

        public ValidationResult ValidatePaymentMode(
            GameState state,
            int playerId,
            string facilityId,
            string paymentMode)
        {
            var facilityValidation = ValidateFacility(state, playerId, facilityId);
            if (!facilityValidation.IsValid)
            {
                return facilityValidation;
            }

            var normalized = NormalizePaymentMode(paymentMode);
            if (normalized != PaymentModeAuto &&
                normalized != PaymentModeResources &&
                normalized != PaymentModeGold)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "建设支付方式无效。");
            }

            var player = state.FindPlayer(playerId);
            var facility = FacilityCardDatabase.Get(facilityId);

            var effectiveResourceCost = buildCostService.GetEffectiveResourceCost(state, player, facility);
            if (string.IsNullOrEmpty(ResolvePaymentMode(player, facility, effectiveResourceCost, normalized)))
            {
                var reason = normalized == PaymentModeResources
                    ? "资源不足，无法按所选方式建设该设施。"
                    : normalized == PaymentModeGold
                        ? "金券不足，无法按所选方式建设该设施。"
                        : "资源或金券不足，无法建设该设施。";
                return ValidationResult.Failure(CommandErrorCode.InsufficientResource, reason);
            }

            return ValidationResult.Success;
        }

        public ResourceSet GetEffectiveResourceCost(GameState state, int playerId, string facilityId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            var facility = FacilityCardDatabase.Get(facilityId);
            if (player == null || facility == null)
            {
                return new ResourceSet();
            }

            return buildCostService.GetEffectiveResourceCost(state, player, facility);
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

        private static string ResolvePaymentMode(
            PlayerState player,
            FacilityCardDefinition facility,
            ResourceSet effectiveResourceCost,
            string paymentMode)
        {
            var normalized = NormalizePaymentMode(paymentMode);
            if (normalized == PaymentModeResources)
            {
                return player.Resources.CanPay(effectiveResourceCost) ? PaymentModeResources : string.Empty;
            }

            if (normalized == PaymentModeGold)
            {
                return player.Resources.GoldVoucher >= facility.GoldVoucherCost ? PaymentModeGold : string.Empty;
            }

            if (normalized != PaymentModeAuto)
            {
                return string.Empty;
            }

            if (player.Resources.CanPay(effectiveResourceCost))
            {
                return PaymentModeResources;
            }

            return player.Resources.GoldVoucher >= facility.GoldVoucherCost ? PaymentModeGold : string.Empty;
        }

        private static string NormalizePaymentMode(string paymentMode)
        {
            return string.IsNullOrEmpty(paymentMode)
                ? PaymentModeAuto
                : paymentMode.Trim().ToLowerInvariant();
        }

        private static bool IsCityBoardSlotOccupied(GameState state, int playerId, int cityBoardSlotIndex)
        {
            return HasFacilityAtCityBoardSlot(state, playerId, cityBoardSlotIndex);
        }

        private static bool HasFacilityAtCityBoardSlot(GameState state, int playerId, int cityBoardSlotIndex)
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
