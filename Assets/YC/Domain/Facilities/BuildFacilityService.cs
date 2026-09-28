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
        public const string PaymentModeFree = "free";

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

        public static BuildFacilityService CreateForEffectTree()
        {
            return new BuildFacilityService(new NoEntryEffectResolver(), new FacilityBuildCostService());
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

            // 建设顺序：支付 -> 取走并补充供应区 -> 放置 -> 得分 -> 入场效果。
            player.Resources.TryPay(cost);
            ReplaceBuiltFacilityInSupply(state, facility.FacilityId);
            PlaceAndScore(state, player, facility, cityBoardSlotIndex);
            entryEffectResolver.Resolve(state, player, facility, cityBoardSlotIndex);

            return BuildFacilityResult.Success(facility, cityBoardSlotIndex, resolvedPaymentMode);
        }

        /// <summary>
        /// 两阶段建设的第一步：扣费并补供应牌。待放置牌由建设 Effect 持久化，
        /// 后续只能放置，不允许再次支付或取消。
        /// </summary>
        public BuildFacilityResult PayAndRefillForPlacement(GameState state, int playerId, string facilityId, string paymentMode)
        {
            var validation = ValidatePaymentMode(state, playerId, facilityId, paymentMode);
            if (!validation.IsValid) return BuildFacilityResult.Failure(validation);
            if (FindFirstEmptyCityBoardSlot(state, playerId) < 0)
                return BuildFacilityResult.Failure(ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "城市面板没有可放置槽位。"));
            var player = state.FindPlayer(playerId);
            var facility = FacilityCardDatabase.Get(facilityId);
            var resourceCost = buildCostService.GetEffectiveResourceCost(state, player, facility);
            var mode = ResolvePaymentMode(player, facility, resourceCost, paymentMode);
            var cost = mode == PaymentModeGold ? new ResourceSet { GoldVoucher = facility.GoldVoucherCost } : resourceCost;
            if (!player.Resources.TryPay(cost))
                return BuildFacilityResult.Failure(ValidationResult.Failure(CommandErrorCode.InsufficientResource, "支付资源已变化。"));
            ReplaceBuiltFacilityInSupply(state, facilityId);
            return BuildFacilityResult.Success(facility, -1, mode);
        }

        // 仅供已完成支付的持久化建设 Effect 调用；不再要求卡牌仍位于供应区。
        public ValidationResult ValidatePaidPlacement(GameState state, int playerId, string facilityId, int slot)
        {
            var player = state.FindPlayer(playerId);
            if (player == null || !FacilityCardDatabase.TryGet(facilityId, out var facility))
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "待放置设施不存在。" );
            if (FacilityCardDatabase.PlayerHasBuiltUniqueFacility(player, facility))
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该唯一设施已经建设过。" );
            if (slot < 0 || slot >= CityBoardSlotCount || IsCityBoardSlotOccupied(state, playerId, slot))
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "城市面板槽位不可用。" );
            return ValidationResult.Success;
        }

        public BuildFacilityResult PlacePaidFacility(GameState state, int playerId, string facilityId, int slot, string paymentMode)
        {
            var validation = ValidatePaidPlacement(state, playerId, facilityId, slot);
            if (!validation.IsValid) return BuildFacilityResult.Failure(validation);
            var facility = FacilityCardDatabase.Get(facilityId);
            PlaceAndScore(state, state.FindPlayer(playerId), facility, slot);
            return BuildFacilityResult.Success(facility, slot, paymentMode);
        }

        /// <summary>已指定槽位的建设：支付、补牌、放置、得分；入场效果由 Effect 主链负责。</summary>
        public BuildFacilityResult BuildForEffectTree(
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

            player.Resources.TryPay(cost);
            ReplaceBuiltFacilityInSupply(state, facility.FacilityId);
            PlaceAndScore(state, player, facility, cityBoardSlotIndex);
            return BuildFacilityResult.Success(facility, cityBoardSlotIndex, resolvedPaymentMode);
        }

        /// <summary>
        /// 结算由建设牌效果授予的免费备用设施建设。该入口复用标准建设的槽位、
        /// 放置与得分规则，但不支付费用、不触发入场效果，也不改动公共供应区。
        /// </summary>
        public BuildFacilityResult BuildReserveForFree(
            GameState state,
            int playerId,
            string facilityId,
            int cityBoardSlotIndex)
        {
            var validation = ValidateReserveBuild(state, playerId, facilityId, cityBoardSlotIndex);
            if (!validation.IsValid)
            {
                return BuildFacilityResult.Failure(validation);
            }

            var player = state.FindPlayer(playerId);
            var facility = FacilityCardDatabase.Get(facilityId);
            PlaceAndScore(state, player, facility, cityBoardSlotIndex);
            return BuildFacilityResult.Success(facility, cityBoardSlotIndex, PaymentModeFree);
        }

        public ValidationResult ValidateReserveBuild(
            GameState state,
            int playerId,
            string facilityId,
            int cityBoardSlotIndex)
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

            var facility = FacilityCardDatabase.Get(facilityId);
            if (facility == null || !facility.ReserveOnly)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "所选备用设施不存在。");
            }

            if (cityBoardSlotIndex < 0 || cityBoardSlotIndex >= CityBoardSlotCount)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "城市面板槽位无效。");
            }

            if (IsCityBoardSlotOccupied(state, playerId, cityBoardSlotIndex))
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "城市面板槽位已被占用。");
            }

            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                if (state.Map.Facilities[i].FacilityCardId == facilityId)
                {
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该备用设施已经被建设。");
                }
            }

            return ValidationResult.Success;
        }

        private static void ReplaceBuiltFacilityInSupply(GameState state, string facilityId)
        {
            FacilitySupplyService.ReplaceBuiltCard(
                state.Decks.FacilitySupply,
                state.Decks.FacilityDeck,
                facilityId);
        }

        private static void PlaceAndScore(
            GameState state,
            PlayerState player,
            FacilityCardDefinition facility,
            int cityBoardSlotIndex)
        {
            player.BuiltFacilityIds.Add(facility.FacilityId);
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = player.PlayerId,
                FacilityCardId = facility.FacilityId,
                CityBoardSlotIndex = cityBoardSlotIndex
            });
            FacilityInstanceStateService.EnsureIdentity(state, state.Map.Facilities[state.Map.Facilities.Count - 1]);
            player.Score += facility.Score;
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
            FacilityInstanceStateService.EnsureIdentity(state, state.Map.Facilities[state.Map.Facilities.Count - 1]);
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

        private sealed class NoEntryEffectResolver : IFacilityEntryEffectResolver
        {
            public void Resolve(GameState state, PlayerState player, FacilityCardDefinition facility, int cityBoardSlotIndex)
            {
                // 生产路径由 FacilityEntryEffectExecutor 负责；该实现只保留
                // 旧 Build API 的构造兼容性，不会被 Effect-tree 建设调用。
            }
        }
    }
}
