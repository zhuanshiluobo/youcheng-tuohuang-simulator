using System;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Effects;
using System.Linq;
using System.Globalization;

namespace YC.Application.Gameplay
{
    internal static class MainActionCommandGuard
    {
        private static readonly MainActionBudgetService MainActionBudgetService = new MainActionBudgetService();

        public static ValidationResult Validate(GameState state, GameCommand command)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            var phaseValidation = CommandPhasePolicy.ValidatePhase(state, command.Kind);
            if (!phaseValidation.IsValid)
            {
                return phaseValidation;
            }

            var player = state.FindPlayer(command.PlayerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "命令玩家不存在。");
            }

            if (state.CurrentPlayerId != command.PlayerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "命令玩家不是当前玩家。");
            }

            if (state.HasPendingChoice() && !IsPreparedActionConfirmation(state, command))
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择项，再提交其他行动。");
            }

            return MainActionBudgetService.ValidateCanSpend(state, command.PlayerId);
        }

        internal static ValidationResult ValidateBuildQuote(GameState state, int playerId, string facilityItemId)
        {
            var command = new GameCommand { Kind = GameCommandKind.BuildFacility, PlayerId = playerId, TargetId = facilityItemId ?? "" };
            if (state?.EffectRuntime == null || !state.HasPendingChoice()) return Validate(state, command);
            if (!CommandPhasePolicy.IsCommandAllowed(state.Phase, command.Kind) || state.CurrentPlayerId != playerId ||
                !MainActionBudgetService.ValidateCanSpend(state, playerId).IsValid || state.PendingChoice != null || state.PendingCardSession != null ||
                state.PendingCharacterEffect != null || state.PendingSpecialAction != null) return Validate(state, command);
            var owners = state.EffectRuntime.EffectNodes.Where(node => ResourcePaymentPreparationService.IsCurrentPreparation(state, node, playerId)).Take(2).ToList();
            if (owners.Count != 1) return Validate(state, command);
            var owner = owners[0]; var payment = owner.Payment; var preparation = payment.Preparation;
            if (preparation.ActionKind != "build" || preparation.TargetId != facilityItemId || preparation.PreparationOwnerEffectId != owner.EffectId ||
                payment.PaymentId != owner.EffectId || payment.OwnerEffectId != owner.EffectId || payment.IsCommitted || payment.IsCommitRequested ||
                ResourcePaymentPreparationService.HasPendingResponse(state, owner) ||
                state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" && !request.IsInternalMainlineInteraction() && request.OwnerEffectId != owner.EffectId) ||
                !state.EffectRuntime.OperationWindows.Any(window => window.OperationWindowId == preparation.OperationWindowId &&
                    window.WindowGeneration == preparation.WindowGeneration && window.AvailablePlayerIds.Contains(playerId)) ||
                !ResourcePaymentPreparationService.TryQuote(state, playerId, "build", facilityItemId, preparation.ActionArguments, out _, out _))
                return Validate(state, command);
            // 只报价两种支付方式；不制造另一个模式的最终确认身份，也不要求当前模式可付。
            return ValidationResult.Success;
        }

        // Session与处理器共用只读资格；实际挂链、复验和扣款仍由领域入口执行。
        internal static bool IsPreparedActionConfirmation(GameState state, GameCommand command)
        {
            string Read(string key) => command?.Parameters != null && command.Parameters.TryGetValue(key, out var value) ? value ?? "" : "";
            if (state?.EffectRuntime == null || command == null || string.IsNullOrEmpty(command.CommandId) ||
                command.Kind != GameCommandKind.BuildFacility && command.Kind != GameCommandKind.MoveCity ||
                !CommandPhasePolicy.IsCommandAllowed(state.Phase, command.Kind) || state.CurrentPlayerId != command.PlayerId ||
                !MainActionBudgetService.ValidateCanSpend(state, command.PlayerId).IsValid) return false;
            string preparationId = Read("paymentPreparationOwnerEffectId");
            var owner = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == preparationId);
            if (string.IsNullOrEmpty(preparationId) || !ResourcePaymentPreparationService.IsCurrentPreparation(state, owner, command.PlayerId) ||
                owner.Payment.Preparation.PreparationOwnerEffectId != preparationId || owner.Payment.OwnerEffectId != preparationId || owner.Payment.PaymentId != preparationId ||
                owner.Status != EffectNodeStatus.Blocked || owner.FlowStage != ResourceEffectExecutor.PaymentConfirmationStage ||
                !ResourcePaymentPreparationService.CanConfirm(state, owner) || ResourcePaymentPreparationService.HasPendingResponse(state, owner) ||
                state.PendingChoice != null || state.PendingCardSession != null || state.PendingCharacterEffect != null || state.PendingSpecialAction != null ||
                state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" && !request.IsInternalMainlineInteraction() && request.OwnerEffectId != preparationId)) return false;
            var input = state.EffectRuntime.InteractionRequests.SingleOrDefault(request => request.Status == "open" &&
                request.OwnerEffectId == preparationId && request.AnsweringPlayerId == command.PlayerId &&
                request.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId);
            if (input == null || input.StateRevision != state.EffectRuntime.StateRevision ||
                command.Parameters.ContainsKey("expectedRevision") && (!int.TryParse(Read("expectedRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out int revision) ||
                    revision != state.EffectRuntime.StateRevision)) return false;
            var preparation = owner.Payment.Preparation;
            if (!OperationWindowService.TryResolveCommandContext(state, command, out var operation, out _) ||
                operation.OperationWindowId != preparation.OperationWindowId || operation.WindowGeneration != preparation.WindowGeneration ||
                operation.WindowKind != RoundMainlineNodeTypeIds.PlayerActionWindow || operation.ActingPlayerId != command.PlayerId) return false;
            string target = command.TargetId ?? "";
            var arguments = preparation.ActionArguments.Clone();
            if (command.Kind == GameCommandKind.BuildFacility)
            {
                string facility = Read(BuildFacilityCommandHandler.FacilityIdParameter);
                if (facility.Length > 0 && target.Length > 0 && facility != target) return false;
                if (facility.Length > 0) target = facility;
                if (preparation.ActionKind != "build" || preparation.TargetId != target ||
                    Read(BuildFacilityCommandHandler.PaymentModeParameter).Trim().ToLowerInvariant() != ItemTransferEffectExecutor.Text(arguments, "paymentMode")) return false;
                string declaredSource = Read(BuildFacilityCommandHandler.ExpectedSourceZoneIdParameter);
                if (declaredSource.Length > 0 && declaredSource != ItemTransferEffectExecutor.Text(arguments, "expectedSourceZoneId")) return false;
                int slot = -1;
                string slotText = Read(BuildFacilityCommandHandler.CityBoardSlotIndexParameter);
                if (slotText.Length > 0 && !int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)) return false;
                arguments.Properties.RemoveAll(entry => entry.Name == "cityBoardSlotIndex");
                arguments.Properties.Add(new NormalizedValueEntry { Name = "cityBoardSlotIndex", Value = NormalizedValue.CreateInteger(slot) });
                arguments.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            }
            else
            {
                string candidateTarget = YC.Domain.Movement.CityMoveCandidateQueryService.GetTargetLocationId(target);
                if (!string.IsNullOrEmpty(candidateTarget)) target = candidateTarget;
                if (preparation.ActionKind != "city_move" || preparation.TargetId.Length == 0 || preparation.TargetId != target ||
                    !preparation.BusinessBeforeReady || preparation.SourceLocationId != state.FindPlayer(command.PlayerId).CityLocationId) return false;
            }
            return ResourcePaymentPreparationService.TryQuote(state, command.PlayerId, preparation.ActionKind, target, arguments, out var cost, out _) &&
                ResourcePaymentPreparationService.SameCost(cost, owner.Payment.PaymentCost);
        }
    }
}
