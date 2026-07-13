using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class BuildFacilitySlotOptionResult
    {
        public BuildFacilitySlotOptionResult(int cityBoardSlotIndex, ValidationResult validation)
        {
            CityBoardSlotIndex = cityBoardSlotIndex;
            Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        }

        public int CityBoardSlotIndex { get; private set; }
        public ValidationResult Validation { get; private set; }

        public bool IsLegal
        {
            get { return Validation.IsValid; }
        }

        public string Reason
        {
            get { return Validation.Reason; }
        }
    }

    public sealed class BuildFacilityPaymentOptionResult
    {
        public BuildFacilityPaymentOptionResult(string paymentMode, ValidationResult validation)
        {
            PaymentMode = paymentMode ?? string.Empty;
            Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        }

        public string PaymentMode { get; private set; }
        public ValidationResult Validation { get; private set; }

        public bool IsAvailable
        {
            get { return Validation.IsValid; }
        }

        public string Reason
        {
            get { return Validation.Reason; }
        }
    }

    public sealed class BuildFacilityOptionQueryResult
    {
        public BuildFacilityOptionQueryResult(
            string facilityId,
            ValidationResult validation,
            List<BuildFacilitySlotOptionResult> slotOptions,
            BuildFacilityPaymentOptionResult resourcesPayment,
            BuildFacilityPaymentOptionResult goldPayment,
            ResourceSet effectiveResourceCost)
        {
            FacilityId = facilityId ?? string.Empty;
            Validation = validation ?? throw new ArgumentNullException(nameof(validation));
            SlotOptions = (slotOptions ?? new List<BuildFacilitySlotOptionResult>()).AsReadOnly();
            ResourcesPayment = resourcesPayment ?? throw new ArgumentNullException(nameof(resourcesPayment));
            GoldPayment = goldPayment ?? throw new ArgumentNullException(nameof(goldPayment));
            EffectiveResourceCost = effectiveResourceCost ?? new ResourceSet();
            PaymentOptions = new List<BuildFacilityPaymentOptionResult>
            {
                ResourcesPayment,
                GoldPayment
            }.AsReadOnly();
        }

        public string FacilityId { get; private set; }
        public ValidationResult Validation { get; private set; }
        public IReadOnlyList<BuildFacilitySlotOptionResult> SlotOptions { get; private set; }
        public BuildFacilityPaymentOptionResult ResourcesPayment { get; private set; }
        public BuildFacilityPaymentOptionResult GoldPayment { get; private set; }
        public IReadOnlyList<BuildFacilityPaymentOptionResult> PaymentOptions { get; private set; }
        public ResourceSet EffectiveResourceCost { get; private set; }

        public bool CanBuild
        {
            get { return Validation.IsValid; }
        }

        public string Reason
        {
            get { return Validation.Reason; }
        }
    }

    public sealed class BuildFacilityOptionQueryService
    {
        private readonly BuildFacilityService buildFacilityService;

        public BuildFacilityOptionQueryService()
            : this(new BuildFacilityService())
        {
        }

        public BuildFacilityOptionQueryService(BuildFacilityService buildFacilityService)
        {
            this.buildFacilityService = buildFacilityService ?? throw new ArgumentNullException(nameof(buildFacilityService));
        }

        public BuildFacilityOptionQueryResult Query(GameState state, int playerId, string facilityId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var actionValidation = MainActionCommandGuard.Validate(state, new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = playerId,
                TargetId = facilityId ?? string.Empty
            });
            var facilityValidation = actionValidation.IsValid
                ? buildFacilityService.ValidateFacility(state, playerId, facilityId)
                : actionValidation;

            var slotOptions = new List<BuildFacilitySlotOptionResult>(BuildFacilityService.CityBoardSlotCount);
            var hasLegalSlot = false;
            for (var slotIndex = 0; slotIndex < BuildFacilityService.CityBoardSlotCount; slotIndex++)
            {
                var validation = facilityValidation.IsValid
                    ? buildFacilityService.ValidateCityBoardSlot(state, playerId, facilityId, slotIndex)
                    : facilityValidation;
                slotOptions.Add(new BuildFacilitySlotOptionResult(slotIndex, validation));
                hasLegalSlot |= validation.IsValid;
            }

            var resourcesValidation = facilityValidation.IsValid
                ? buildFacilityService.ValidatePaymentMode(
                    state,
                    playerId,
                    facilityId,
                    BuildFacilityService.PaymentModeResources)
                : facilityValidation;
            var goldValidation = facilityValidation.IsValid
                ? buildFacilityService.ValidatePaymentMode(
                    state,
                    playerId,
                    facilityId,
                    BuildFacilityService.PaymentModeGold)
                : facilityValidation;

            var overallValidation = ResolveOverallValidation(
                state,
                playerId,
                facilityId,
                facilityValidation,
                hasLegalSlot,
                resourcesValidation,
                goldValidation);
            return new BuildFacilityOptionQueryResult(
                facilityId,
                overallValidation,
                slotOptions,
                new BuildFacilityPaymentOptionResult(BuildFacilityService.PaymentModeResources, resourcesValidation),
                new BuildFacilityPaymentOptionResult(BuildFacilityService.PaymentModeGold, goldValidation),
                buildFacilityService.GetEffectiveResourceCost(state, playerId, facilityId));
        }

        public IReadOnlyList<BuildFacilityOptionQueryResult> Query(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var options = new List<BuildFacilityOptionQueryResult>(state.Decks.FacilitySupply.Count);
            for (var i = 0; i < state.Decks.FacilitySupply.Count; i++)
            {
                options.Add(Query(state, playerId, state.Decks.FacilitySupply[i]));
            }

            return options.AsReadOnly();
        }

        private ValidationResult ResolveOverallValidation(
            GameState state,
            int playerId,
            string facilityId,
            ValidationResult facilityValidation,
            bool hasLegalSlot,
            ValidationResult resourcesValidation,
            ValidationResult goldValidation)
        {
            if (!facilityValidation.IsValid)
            {
                return facilityValidation;
            }

            if (!hasLegalSlot)
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "城市面板没有合法空槽位。");
            }

            if (!resourcesValidation.IsValid && !goldValidation.IsValid)
            {
                return buildFacilityService.ValidatePaymentMode(
                    state,
                    playerId,
                    facilityId,
                    BuildFacilityService.PaymentModeAuto);
            }

            return ValidationResult.Success;
        }
    }
}
