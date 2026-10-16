using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Items;
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
            ResourceSet effectiveResourceCost, string definitionId = "", string sourceZoneId = "", string carrierItemId = "")
        {
            FacilityId = facilityId ?? string.Empty;
            DefinitionId = definitionId.Length == 0 ? FacilityId : definitionId;
            SourceZoneId = sourceZoneId; CarrierItemId = carrierItemId;
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
        public string DefinitionId { get; }
        public string SourceZoneId { get; }
        public string CarrierItemId { get; }
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
        private readonly FacilityBuildCandidateQuery candidates;

        public BuildFacilityOptionQueryService()
            : this(new BuildFacilityService())
        {
        }

        public BuildFacilityOptionQueryService(BuildFacilityService buildFacilityService)
            : this(buildFacilityService, new FacilityBuildCandidateQuery()) { }

        public BuildFacilityOptionQueryService(BuildFacilityService buildFacilityService, FacilityBuildCandidateQuery candidates)
        {
            this.candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
            this.buildFacilityService = buildFacilityService ?? throw new ArgumentNullException(nameof(buildFacilityService));
        }

        public BuildFacilityOptionQueryResult Query(GameState state, int playerId, string facilityId)
        {
            return Query(state, playerId, facilityId, true);
        }

        public BuildFacilityOptionQueryResult QueryForAdditionalBuild(
            GameState state,
            int playerId,
            string facilityId)
        {
            return Query(state, playerId, facilityId, false);
        }

        public IReadOnlyList<int> GetLegalReserveBuildSlots(GameState state, int playerId, string facilityId)
        {
            var slots = new List<int>();
            for (int i = 0; i < BuildFacilityService.CityBoardSlotCount; i++)
                if (buildFacilityService.ValidateReserveBuild(state, playerId, facilityId, i).IsValid) slots.Add(i);
            return slots.AsReadOnly();
        }

        private BuildFacilityOptionQueryResult Query(
            GameState state,
            int playerId,
            string facilityId,
            bool requireMainAction)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return buildFacilityService.WithReadOnlyQuery(state, playerId, null,
                query => Query(state, playerId, facilityId, requireMainAction, query));
        }

        private BuildFacilityOptionQueryResult Query(
            GameState state, int playerId, string facilityId, bool requireMainAction, BuildFacilityService.ReadOnlyQuery query, FacilityBuildCandidate source = null)
        {
            var facilityValidation = query.ValidateFacility(facilityId);
            if (source != null)
            {
                var item = state.ItemZones.Items.Find(value => value.ItemId == source.ItemId);
                if (item?.ZoneId != source.SourceZoneId || item.DefinitionId != source.DefinitionId)
                    facilityValidation = ValidationResult.Failure(CommandErrorCode.InvalidTarget, "设施来源已变化，请重新选择。");
            }
            if (requireMainAction)
            {
                var actionValidation = MainActionCommandGuard.ValidateBuildQuote(state, playerId, facilityId ?? string.Empty);
                if (!actionValidation.IsValid)
                {
                    facilityValidation = actionValidation;
                }
            }

            var slotOptions = new List<BuildFacilitySlotOptionResult>(BuildFacilityService.CityBoardSlotCount);
            var hasLegalSlot = false;
            for (var slotIndex = 0; slotIndex < BuildFacilityService.CityBoardSlotCount; slotIndex++)
            {
                var validation = facilityValidation.IsValid
                    ? query.ValidateCityBoardSlot(facilityId, slotIndex)
                    : facilityValidation;
                slotOptions.Add(new BuildFacilitySlotOptionResult(slotIndex, validation));
                hasLegalSlot |= validation.IsValid;
            }

            var resourcesValidation = facilityValidation.IsValid
                ? query.ValidatePaymentMode(
                    facilityId,
                    BuildFacilityService.PaymentModeResources)
                : facilityValidation;
            var goldValidation = facilityValidation.IsValid
                ? query.ValidatePaymentMode(
                    facilityId,
                    BuildFacilityService.PaymentModeGold)
                : facilityValidation;

            var overallValidation = ResolveOverallValidation(
                query,
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
                query.GetEffectiveResourceCost(facilityId),
                source?.DefinitionId ?? FacilityCardDatabase.Get(state, facilityId)?.FacilityId ?? facilityId,
                source?.SourceZoneId ?? state.ItemZones.Items.Find(item => item.ItemId == facilityId)?.ZoneId ?? "",
                source?.CarrierItemId ?? state.ItemZones.Zones.Find(zone => zone.ZoneId == state.ItemZones.Items.Find(item => item.ItemId == facilityId)?.ZoneId && zone.ParentKind == ZoneParentKind.Item)?.ParentId ?? "");
        }

        public IReadOnlyList<BuildFacilityOptionQueryResult> Query(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (ItemZoneLegacyProjection.IsActive(state)) return Query(state, playerId, QueryCandidates(state, playerId));
            var supply = state.Decks.FacilitySupply.ToArray();
            return buildFacilityService.WithReadOnlyQuery<IReadOnlyList<BuildFacilityOptionQueryResult>>(state, playerId, supply, query =>
            {
                var options = new List<BuildFacilityOptionQueryResult>(supply.Length);
                for (var i = 0; i < supply.Length; i++)
                {
                    options.Add(Query(state, playerId, supply[i], true, query));
                }
                return options.AsReadOnly();
            });
        }

        public FacilityBuildCandidateSnapshot QueryCandidates(GameState state, int playerId, int stateRevision = 0)
            => candidates.Resolve(state, playerId, stateRevision: state.EffectRuntime?.StateRevision ?? stateRevision);

        // 来源快照由打开/实际来源变化刷新；费用与槽位可反复查询，不重复调用候选 Lua。
        public IReadOnlyList<BuildFacilityOptionQueryResult> Query(GameState state, int playerId, FacilityBuildCandidateSnapshot snapshot)
        {
            if (state == null || snapshot == null) throw new ArgumentNullException();
            return buildFacilityService.WithReadOnlyQuery<IReadOnlyList<BuildFacilityOptionQueryResult>>(state, playerId, null, query => {
                var result = new List<BuildFacilityOptionQueryResult>(snapshot.Candidates.Count);
                foreach (var candidate in snapshot.Candidates)
                    result.Add(Query(state, playerId, candidate.ItemId, true, query, candidate));
                return result.AsReadOnly();
            });
        }

        private ValidationResult ResolveOverallValidation(
            BuildFacilityService.ReadOnlyQuery query,
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
                return query.ValidatePaymentMode(
                    facilityId,
                    BuildFacilityService.PaymentModeAuto);
            }

            return ValidationResult.Success;
        }
    }
}
