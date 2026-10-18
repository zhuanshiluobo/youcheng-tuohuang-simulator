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

        internal static ValidationResult ValidateBuildQuote(GameState state, int playerId, string facilityItemId, GameStateView visibleState = null)
        {
            var command = new GameCommand { Kind = GameCommandKind.BuildFacility, PlayerId = playerId, TargetId = facilityItemId ?? "" };
            if (visibleState != null) return ValidateVisibleBuildQuote(state, command, visibleState);
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

        private static ValidationResult ValidateVisibleBuildQuote(GameState state, GameCommand command, GameStateView visibleState)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (visibleState.GameId != state.GameId || visibleState.ContentPackHash != state.ContentPackHash ||
                visibleState.ViewerRole != GameStateViewerRole.Player || visibleState.ViewerPlayerId != command.PlayerId ||
                visibleState.StateRevision != (state.EffectRuntime?.StateRevision ?? 0) ||
                visibleState.Phase != state.Phase || visibleState.CurrentPlayerId != state.CurrentPlayerId || visibleState.IsPausedFault)
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "建设报价的本人可见状态已变化，请刷新后重试。");
            if (!CommandPhasePolicy.IsCommandAllowed(state.Phase, command.Kind) || state.FindPlayer(command.PlayerId) == null ||
                state.CurrentPlayerId != command.PlayerId) return Validate(state, command);
            var budget = MainActionBudgetService.ValidateCanSpend(state, command.PlayerId);
            if (!budget.IsValid) return budget;

            var blocked = ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择项，再提交其他行动。");
            if (state.PendingChoice != null || state.PendingCardSession != null || state.PendingCharacterEffect != null ||
                state.PendingSpecialAction != null || visibleState.WaitingForPlayerIds == null || visibleState.WaitingForPlayerIds.Count != 0 ||
                visibleState.Interactions == null || visibleState.PreparedActionPayments == null) return blocked;
            var requests = visibleState.Interactions.Where(request => request != null && request.Status == "open" &&
                !new InteractionRequest { InteractionTypeId = request.Kind }.IsInternalMainlineInteraction()).ToArray();
            if (requests.Length == 0 && visibleState.PreparedActionPayments.Count == 0) return Validate(state, command);
            if (requests.Length != 1 || visibleState.PreparedActionPayments.Count != 1) return blocked;
            var preparation = visibleState.PreparedActionPayments[0];
            var request = requests[0];
            if (preparation == null || !preparation.IsReady || !preparation.CanCancel || preparation.ActionKind != "build" ||
                preparation.TargetId != command.TargetId || string.IsNullOrEmpty(preparation.PreparationOwnerEffectId) ||
                preparation.PaymentOwnerEffectId != preparation.PreparationOwnerEffectId || preparation.PaymentId != preparation.PaymentOwnerEffectId ||
                preparation.PaymentGeneration < 1 || preparation.WindowGeneration < 1 || string.IsNullOrEmpty(preparation.OperationWindowId) ||
                preparation.PaymentMode != "resources" && preparation.PaymentMode != "gold" ||
                string.IsNullOrEmpty(preparation.InteractionId) || request.InteractionId != preparation.InteractionId ||
                request.OwnerEffectId != preparation.PaymentOwnerEffectId || request.SourceNodeId != preparation.PaymentOwnerEffectId ||
                request.Kind != ResourceEffectExecutor.PaymentConfirmationInteractionTypeId || request.AnsweringPlayerId != command.PlayerId ||
                request.StateRevision != visibleState.StateRevision ||
                !GameStateVisibilityPolicy.IsVisible(request.Visibility, request.AnsweringPlayerId, GameStateViewer.Player(command.PlayerId))) return blocked;
            var owners = visibleState.Effects?.Where(effect => effect != null && effect.EffectId == preparation.PaymentOwnerEffectId).Take(2).ToArray();
            var owner = owners?.Length == 1 ? owners[0] : null;
            var mains = visibleState.Effects?.Where(effect => effect != null && effect.EffectId == owner?.ParentEffectId).Take(2).ToArray();
            var main = mains?.Length == 1 ? mains[0] : null;
            var mainNodeId = ItemTransferEffectExecutor.Text(main?.NormalizedArguments, "mainNodeId");
            if (owner == null || owner.PlayerId != command.PlayerId || owner.Status != EffectNodeStatus.Blocked ||
                owner.FlowStage != ResourceEffectExecutor.PaymentConfirmationStage || main == null || main.PlayerId != command.PlayerId ||
                main.EffectTypeId != RoundExecutionService.MainlineEffectTypeId || main.Status != EffectNodeStatus.Blocked || string.IsNullOrEmpty(mainNodeId) ||
                ItemTransferEffectExecutor.Text(main.NormalizedArguments, "nodeTypeId") != RoundMainlineNodeTypeIds.PlayerActionWindow ||
                // 与 OperationWindowService 的主行动窗口稳定身份相同；仅核公开来源，不复建运行图。
                preparation.OperationWindowId != StableIdFactory.Create("operation_window", state.GameId,
                    RoundMainlineNodeTypeIds.PlayerActionWindow, mainNodeId, main.EffectId, "", "",
                    preparation.WindowGeneration.ToString(CultureInfo.InvariantCulture), command.PlayerId.ToString(CultureInfo.InvariantCulture))) return blocked;
            var facts = request.PromptParameters;
            if (ItemTransferEffectExecutor.Text(facts, "sourceKind") != "build" ||
                ItemTransferEffectExecutor.Text(facts, "preparationOwnerEffectId") != preparation.PreparationOwnerEffectId ||
                ItemTransferEffectExecutor.Text(facts, "ownerEffectId") != preparation.PaymentOwnerEffectId ||
                ItemTransferEffectExecutor.Text(facts, "paymentId") != preparation.PaymentId ||
                ItemTransferEffectExecutor.Number(facts, "paymentGeneration", -1) != preparation.PaymentGeneration ||
                ItemTransferEffectExecutor.Value(facts, "isCommitted")?.Kind != NormalizedValueKind.Boolean ||
                ItemTransferEffectExecutor.Value(facts, "isCommitRequested")?.Kind != NormalizedValueKind.Boolean ||
                ItemTransferEffectExecutor.Flag(facts, "isCommitted") || ItemTransferEffectExecutor.Flag(facts, "isCommitRequested")) return blocked;
            if (state.EffectRuntime?.InteractionRequests.Any(input => input.Status == "open" && !input.IsInternalMainlineInteraction() &&
                (input.GetStableInteractionId() != request.InteractionId || input.OwnerEffectId != request.OwnerEffectId ||
                 input.InteractionTypeId != request.Kind || input.AnsweringPlayerId != command.PlayerId || input.StateRevision != visibleState.StateRevision)) == true)
                return blocked;
            // 正式投影仅发布 IsCurrentPreparation 通过的当前窗口/代际；不把隐藏运行图复制到 UI 状态。
            // IsReady 证明必要响应已结束；CanConfirm 仅约束当前模式，不能阻断另一种支付方式报价。
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
