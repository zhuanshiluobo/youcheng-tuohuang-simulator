using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    // 同一次本人投影的只读适配，不序列化、不拥有候选或付款账本。
    public sealed class EffectBuildScope
    {
        public GameStateView VisibleState { get; }
        public InteractionView Selection { get; }
        public InteractionView Input { get; }
        public PreparedActionPaymentView Payment { get; }
        public string RequestedPaymentMode { get; }
        public string OwnerEffectId => Selection.OwnerEffectId;
        public string SourceZone => ItemTransferEffectExecutor.Text(Selection.PromptParameters, "sourceZone");
        // 旧deck消费者只读兼容；供应多源必须按目标调用TryGetCandidateSourceZoneId。
        public string SourceZoneId => SourceZone == "deck"
            ? ItemTransferEffectExecutor.Text(Selection.PromptParameters, "expectedSourceZoneId") : string.Empty;
        public int PlayerId => VisibleState.ViewerPlayerId;
        public bool CanChoose => Payment == null && Input?.InteractionId == Selection.InteractionId;
        internal EffectBuildScope(GameStateView view, InteractionView selection, InteractionView input, PreparedActionPaymentView payment,
            string requestedPaymentMode)
        { VisibleState = view; Selection = selection; Input = input; Payment = payment; RequestedPaymentMode = requestedPaymentMode; }
        public bool AllowsPaymentMode(string mode) => (mode == "resources" || mode == "gold") &&
            (RequestedPaymentMode == "auto" || RequestedPaymentMode == mode);

        public bool TryGetCandidateSourceZoneId(string targetItemId, out string sourceZoneId)
        {
            sourceZoneId = string.Empty;
            int index = Selection.CandidateIds.IndexOf(targetItemId);
            if (index < 0) return false;
            if (SourceZone == "deck")
            {
                sourceZoneId = ItemTransferEffectExecutor.Text(Selection.PromptParameters, "expectedSourceZoneId");
                return sourceZoneId.Length > 0;
            }
            if (SourceZone != "supply" && SourceZone != "authorized" || VisibleState.ItemZones?.Items == null || VisibleState.ItemZones.Zones == null) return false;
            var items = VisibleState.ItemZones.Items.Where(value => value != null && value.ItemId == targetItemId).Take(2).ToArray();
            if (items.Length != 1) return false;
            var item = items[0];
            var definitions = ItemTransferEffectExecutor.Value(Selection.PromptParameters, "candidateDefinitionIds");
            if (item.PhysicalKind != ItemPhysicalKind.Card || definitions?.Items == null || index >= definitions.Items.Count ||
                item.DefinitionId != definitions.Items[index]?.StringValue) return false;
            var zones = VisibleState.ItemZones.Zones.Where(value => value?.Zone?.ZoneId == item.ZoneId && value.IdentitiesVisible).Take(2).ToArray();
            if (zones.Length != 1) return false;
            var zone = zones[0].Zone;
            bool supply = SourceZone == "supply" && zone.TemplateId == "core.zone.facility_supply_slot" &&
                VisibleState.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zone.ZoneId)?.ItemIds.FirstOrDefault() == item.ItemId;
            bool stored = zone.TemplateId == "core.zone.card_facility_storage" && zone.ParentKind == ZoneParentKind.Item &&
                (zone.OwnerPlayerId < 1 || zone.OwnerPlayerId == PlayerId) &&
                item.OwnerSubject != null && item.ControllerSubject != null &&
                (item.OwnerSubject.Kind == ItemSubjectKind.None || item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == PlayerId) &&
                (item.ControllerSubject.Kind == ItemSubjectKind.None || item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId == PlayerId);
            if (stored)
            {
                var carriers = VisibleState.ItemZones.Items.Where(value => value != null && value.ItemId == zone.ParentId &&
                    value.ControllerSubject?.Kind == ItemSubjectKind.Player && value.ControllerSubject.PlayerId == PlayerId).Take(2).ToArray();
                stored = carriers.Length == 1 && VisibleState.ItemZones.Zones.Any(value => value?.Zone?.ZoneId == carriers[0].ZoneId &&
                    (value.Zone.TemplateId == "core.zone.character_persistent" || SourceZone == "authorized" && value.Zone.TemplateId == "core.zone.entrepreneur_active"));
            }
            if (!supply && !stored) return false;
            sourceZoneId = zone.ZoneId;
            return !string.IsNullOrEmpty(sourceZoneId);
        }

        public InteractionRequestProjection ProjectInput()
        {
            if (Input == null) return null;
            return InteractionRequestProjector.ProjectForPlayer(new InteractionRequest {
                InteractionId = Input.InteractionId, RequestId = Input.InteractionId, SourceNodeId = Input.SourceNodeId,
                OwnerEffectId = Input.OwnerEffectId, InteractionTypeId = Input.Kind, AnsweringPlayerId = Input.AnsweringPlayerId,
                Visibility = Input.Visibility, PromptKey = Input.PromptKey, PromptParameters = Input.PromptParameters,
                CandidateSetId = Input.CandidateSetId, CandidateSetVersion = Input.CandidateSetVersion,
                CandidateIds = Input.CandidateIds, MinSelections = Input.MinSelections, MaxSelections = Input.MaxSelections,
                AllowDecline = Input.AllowDecline, AnswerSchema = Input.AnswerSchema, Status = Input.Status,
                StateRevision = Input.StateRevision
            }, PlayerId);
        }

        public static bool TryReadDeckBuildScope(GameState state, GameStateView view, int playerId,
            string selectionOwnerEffectId, out EffectBuildScope scope) =>
            TryReadEffectBuildScope(state, view, playerId, selectionOwnerEffectId, out scope);

        public static bool TryReadEffectBuildScope(GameState state, GameStateView view, int playerId,
            string selectionOwnerEffectId, out EffectBuildScope scope)
        {
            scope = null;
            if (state == null || view == null || view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != playerId ||
                view.GameId != state.GameId || view.ContentPackHash != state.ContentPackHash ||
                view.StateRevision != (state.EffectRuntime?.StateRevision ?? 0) || view.IsPausedFault ||
                view.Interactions == null || view.PreparedActionPayments == null) return false;
            var parents = view.Effects?.Where(value => value != null && value.EffectId == selectionOwnerEffectId &&
                value.EffectTypeId == FacilitySelectionEffectExecutor.TypeId && value.PlayerId == playerId &&
                value.Status != EffectNodeStatus.Completed && value.Status != EffectNodeStatus.Failed && value.Status != EffectNodeStatus.Faulted).Take(2).ToArray();
            if (parents?.Length != 1) return false;
            string sourceZone = ItemTransferEffectExecutor.Text(parents[0].NormalizedArguments, "sourceZone");
            if (sourceZone.Length == 0) sourceZone = "supply";
            string paymentMode = ItemTransferEffectExecutor.Text(parents[0].NormalizedArguments, "paymentMode");
            if (paymentMode.Length == 0) paymentMode = "auto";
            if ((sourceZone != "deck" && sourceZone != "supply" && sourceZone != "authorized") ||
                (paymentMode != "auto" && paymentMode != "resources" && paymentMode != "gold")) return false;
            var payments = view.PreparedActionPayments.Where(value => value != null && value.AuthorizationKind == "effect_build" &&
                value.ActionKind == "build" && value.SelectionOwnerEffectId == selectionOwnerEffectId).Take(2).ToArray();
            if (payments.Length > 1) return false;
            var payment = payments.FirstOrDefault();
            var selections = view.Interactions.Where(value => value != null && value.OwnerEffectId == selectionOwnerEffectId &&
                value.Kind == FacilitySelectionEffectExecutor.NormalBuildInteractionType && value.AnsweringPlayerId == playerId &&
                ItemTransferEffectExecutor.Text(value.PromptParameters, "sourceZone") == sourceZone &&
                (payment == null ? value.Status == "open" : value.InteractionId == payment.SelectionInteractionId)).Take(2).ToArray();
            if (selections.Length != 1) return false;
            var selection = selections[0];
            if (string.IsNullOrEmpty(selection.InteractionId) || string.IsNullOrEmpty(selection.CandidateSetId) || selection.CandidateSetVersion < 1 ||
                !GameStateVisibilityPolicy.IsVisible(selection.Visibility, playerId, GameStateViewer.Player(playerId)) ||
                selection.CandidateIds == null || selection.CandidateIds.Count == 0 ||
                selection.CandidateIds.Any(string.IsNullOrEmpty) || selection.CandidateIds.Distinct().Count() != selection.CandidateIds.Count) return false;
            var definitions = ItemTransferEffectExecutor.Value(selection.PromptParameters, "candidateDefinitionIds");
            if (definitions?.Kind != NormalizedValueKind.Array || definitions.Items.Count != selection.CandidateIds.Count ||
                definitions.Items.Any(value => value?.Kind != NormalizedValueKind.String || FacilityCardDatabase.Get(state, value.StringValue) == null)) return false;
            InteractionView input = selection;
            if (payment != null)
            {
                if (string.IsNullOrEmpty(payment.PreparationOwnerEffectId) || payment.PreparationOwnerEffectId != payment.PaymentOwnerEffectId ||
                    payment.PaymentId != payment.PaymentOwnerEffectId || payment.PaymentGeneration < 1 ||
                    payment.SelectionCandidateSetId != selection.CandidateSetId || payment.SelectionCandidateSetVersion != selection.CandidateSetVersion ||
                    !selection.CandidateIds.Contains(payment.TargetId) || payment.PaymentMode != "resources" && payment.PaymentMode != "gold" ||
                    paymentMode != "auto" && payment.PaymentMode != paymentMode) return false;
                var inputs = view.Interactions.Where(value => value != null && value.Status == "open" &&
                    value.InteractionId == payment.InteractionId && value.OwnerEffectId == payment.PaymentOwnerEffectId &&
                    value.Kind == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId && value.AnsweringPlayerId == playerId).Take(2).ToArray();
                input = inputs.Length == 1 ? inputs[0] : null;
                if (payment.IsReady && input == null) return false;
                if (input != null && (ItemTransferEffectExecutor.Text(input.PromptParameters, "preparationOwnerEffectId") != payment.PreparationOwnerEffectId ||
                    ItemTransferEffectExecutor.Text(input.PromptParameters, "paymentId") != payment.PaymentId ||
                    ItemTransferEffectExecutor.Number(input.PromptParameters, "paymentGeneration", -1) != payment.PaymentGeneration ||
                    input.AnswerSchema != "candidate_id" || input.CandidateIds?.Contains(ResourceEffectExecutor.PaymentConfirmationCandidateId) != true)) return false;
            }
            if (input != null && (input.Status != "open" || input.StateRevision != view.StateRevision ||
                !GameStateVisibilityPolicy.IsVisible(input.Visibility, playerId, GameStateViewer.Player(playerId)))) return false;
            var candidateScope = new EffectBuildScope(view, selection, input, payment, paymentMode);
            // 来源只读同次本人投影；不为供应重新查询主要行动候选，也不回读隐藏牌堆实例。
            if (selection.CandidateIds.Any(id => !candidateScope.TryGetCandidateSourceZoneId(id, out _))) return false;
            scope = candidateScope;
            return true;
        }
    }

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
            ResourceSet effectiveResourceCost, string definitionId = "", string sourceZoneId = "", string carrierItemId = "",
            ValidationResult preparationValidation = null)
        {
            FacilityId = facilityId ?? string.Empty;
            DefinitionId = definitionId.Length == 0 ? FacilityId : definitionId;
            SourceZoneId = sourceZoneId; CarrierItemId = carrierItemId;
            Validation = validation ?? throw new ArgumentNullException(nameof(validation));
            PreparationValidation = preparationValidation ?? validation;
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
        public ValidationResult PreparationValidation { get; }
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

        public BuildFacilityOptionQueryResult Query(GameState state, int playerId, string facilityId, GameStateView visibleState)
        {
            return Query(state, playerId, facilityId, true, visibleState);
        }

        public BuildFacilityOptionQueryResult QueryForAdditionalBuild(
            GameState state,
            int playerId,
            string facilityId)
        {
            return Query(state, playerId, facilityId, false);
        }

        public IReadOnlyList<BuildFacilityOptionQueryResult> QueryForEffectBuild(GameState state, int playerId,
            GameStateView visibleState, string selectionOwnerEffectId)
        {
            if (!EffectBuildScope.TryReadEffectBuildScope(state, visibleState, playerId, selectionOwnerEffectId, out var scope))
                return Array.Empty<BuildFacilityOptionQueryResult>();
            var definitions = ItemTransferEffectExecutor.Value(scope.Selection.PromptParameters, "candidateDefinitionIds");
            return buildFacilityService.UseQueries(candidates.Queries).WithReadOnlyQuery<IReadOnlyList<BuildFacilityOptionQueryResult>>(state, playerId, null, query => {
                var options = new List<BuildFacilityOptionQueryResult>(scope.Selection.CandidateIds.Count);
                for (int i = 0; i < scope.Selection.CandidateIds.Count; i++)
                {
                    var id = scope.Selection.CandidateIds[i];
                    var definitionId = definitions.Items[i].StringValue;
                    var definition = FacilityCardDatabase.Get(state, definitionId);
                    if (!scope.TryGetCandidateSourceZoneId(id, out var sourceZoneId)) return Array.Empty<BuildFacilityOptionQueryResult>();
                    var player = state.FindPlayer(playerId);
                    var structural = player == null ? ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "建设玩家不存在。") :
                        query.HasBuiltUnique(definition) ?
                        ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该唯一设施已经建设过。") : ValidationResult.Success;
                    var slots = new List<BuildFacilitySlotOptionResult>();
                    for (int slot = 0; slot < BuildFacilityService.CityBoardSlotCount; slot++)
                        slots.Add(new BuildFacilitySlotOptionResult(slot, structural.IsValid ?
                            query.ValidatePaidPlacement(id, slot) : structural));
                    if (structural.IsValid && !slots.Any(slot => slot.IsLegal))
                        structural = ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "城市面板没有合法建设槽位。");
                    var cost = query.GetEffectiveResourceCost(id);
                    ValidationResult Payment(string mode) => !structural.IsValid ? structural : !scope.AllowsPaymentMode(mode) ?
                        ValidationResult.Failure(CommandErrorCode.InvalidTarget, "本次建设未授权该支付方式。") :
                        (mode == "gold" ? player.Resources.GoldVoucher >= definition.GoldVoucherCost :
                            player.Resources.CanPay(cost) || ResourceEffectExecutor.CanPayWithStoredMarkers(state, playerId, cost)) ? ValidationResult.Success :
                        ValidationResult.Failure(CommandErrorCode.InsufficientResource, "资源不足，无法按所选方式建设该设施。");
                    var resources = Payment("resources"); var gold = Payment("gold");
                    var validation = !structural.IsValid ? structural : resources.IsValid || gold.IsValid ? ValidationResult.Success : resources;
                    options.Add(new BuildFacilityOptionQueryResult(id, validation, slots,
                        new BuildFacilityPaymentOptionResult("resources", resources), new BuildFacilityPaymentOptionResult("gold", gold),
                        cost, definitionId, sourceZoneId, preparationValidation: structural));
                }
                return options.AsReadOnly();
            });
        }

        public IReadOnlyList<int> GetLegalReserveBuildSlots(GameState state, int playerId, string facilityId)
        {
            buildFacilityService.UseQueries(candidates.Queries);
            var slots = new List<int>();
            for (int i = 0; i < BuildFacilityService.CityBoardSlotCount; i++)
                if (buildFacilityService.ValidateReserveBuild(state, playerId, facilityId, i).IsValid) slots.Add(i);
            return slots.AsReadOnly();
        }

        private BuildFacilityOptionQueryResult Query(
            GameState state,
            int playerId,
            string facilityId,
            bool requireMainAction, GameStateView visibleState = null)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return buildFacilityService.UseQueries(candidates.Queries).WithReadOnlyQuery(state, playerId, null,
                query => Query(state, playerId, facilityId, requireMainAction, query, visibleState: visibleState));
        }

        private BuildFacilityOptionQueryResult Query(
            GameState state, int playerId, string facilityId, bool requireMainAction, BuildFacilityService.ReadOnlyQuery query,
            FacilityBuildCandidate source = null, GameStateView visibleState = null)
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
                var actionValidation = MainActionCommandGuard.ValidateBuildQuote(state, playerId, facilityId ?? string.Empty, visibleState);
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
            => Query(state, playerId, (GameStateView)null);

        public IReadOnlyList<BuildFacilityOptionQueryResult> Query(GameState state, int playerId, GameStateView visibleState)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (ItemZoneLegacyProjection.IsActive(state)) return Query(state, playerId, QueryCandidates(state, playerId), visibleState);
            var supply = state.Decks.FacilitySupply.ToArray();
            return buildFacilityService.UseQueries(candidates.Queries).WithReadOnlyQuery<IReadOnlyList<BuildFacilityOptionQueryResult>>(state, playerId, supply, query =>
            {
                var options = new List<BuildFacilityOptionQueryResult>(supply.Length);
                for (var i = 0; i < supply.Length; i++)
                {
                    options.Add(Query(state, playerId, supply[i], true, query, visibleState: visibleState));
                }
                return options.AsReadOnly();
            });
        }

        public FacilityBuildCandidateSnapshot QueryCandidates(GameState state, int playerId, int stateRevision = 0)
            => candidates.Resolve(state, playerId, stateRevision: state.EffectRuntime?.StateRevision ?? stateRevision);

        // 来源快照由打开/实际来源变化刷新；费用与槽位可反复查询，不重复调用候选 Lua。
        public IReadOnlyList<BuildFacilityOptionQueryResult> Query(GameState state, int playerId, FacilityBuildCandidateSnapshot snapshot)
            => Query(state, playerId, snapshot, null);

        public IReadOnlyList<BuildFacilityOptionQueryResult> Query(GameState state, int playerId, FacilityBuildCandidateSnapshot snapshot, GameStateView visibleState)
        {
            if (state == null || snapshot == null) throw new ArgumentNullException();
            return buildFacilityService.UseQueries(candidates.Queries).WithReadOnlyQuery<IReadOnlyList<BuildFacilityOptionQueryResult>>(state, playerId, null, query => {
                var result = new List<BuildFacilityOptionQueryResult>(snapshot.Candidates.Count);
                foreach (var candidate in snapshot.Candidates)
                    result.Add(Query(state, playerId, candidate.ItemId, true, query, candidate, visibleState));
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
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "城市面板没有合法建设槽位。");
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
