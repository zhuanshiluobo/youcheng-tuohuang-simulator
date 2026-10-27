using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Application.Gameplay;
using YC.Application.Interactions;

namespace YC.Presentation.Workflows
{
    /// <summary>主要行动付款的UI在途意图；身份和费用只读本人正式投影，不保存付款账本。</summary>
    public sealed class ActionPaymentPreparation
    {
        private readonly IWritableGameplayContext context;
        private readonly CommandGateway gateway;
        private readonly string actionKind;
        private readonly Action changed;
        private readonly Action<string> prompt;
        private string commandId, commandGameId, commandOperation;
        private int commandPlayerId;
        private bool preparationRequested, shuttingDown, notifyingCancellation;
        private Action afterCancellation;
        public Func<GameStateView> GetVisibleState { private get; set; }
        public Func<EffectBuildScope> GetEffectBuildScope { private get; set; }
        public string LastPreparedSelectionInteractionId { get; private set; }
        public bool IsInFlight => !string.IsNullOrEmpty(commandId);
        public bool LastResolvedApplied { get; private set; }
        public string LastResolvedOperation { get; private set; }
        public bool HasPreparation => preparationRequested || Current != null;
        public bool CanCancel => !IsInFlight && (!HasPreparation || Current?.CanCancel == true);
        private GameStateView View => shuttingDown ? null : GetVisibleState != null ? GetVisibleState() :
            GameStateViewProjector.ProjectForPlayer(context.CurrentState, context.LocalPlayerId);
        public PreparedActionPaymentView Current
        {
            get
            {
                var view = View;
                if (view == null || view.GameId != context.CurrentState?.GameId || view.ViewerPlayerId != context.LocalPlayerId) return null;
                if (GetEffectBuildScope != null) return GetEffectBuildScope()?.Payment;
                var matches = view.PreparedActionPayments.Where(payment => payment.ActionKind == actionKind &&
                    payment.AuthorizationKind == "main_action").ToArray();
                return matches.Length == 1 ? matches[0] : null;
            }
        }
        public ActionPaymentPreparation(IWritableGameplayContext context, CommandGateway gateway, string actionKind,
            Action changed, Action<string> prompt)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            this.actionKind = actionKind;
            this.changed = changed;
            this.prompt = prompt;
        }
        public bool Prepare(string targetId, string paymentMode = null, string sourceZoneId = null)
        {
            using var latency = InteractionLatencyTrace.Begin("payment.prepare");
            if (shuttingDown || IsInFlight) return false;
            GameCommand command;
            if (GetEffectBuildScope != null)
            {
                var scope = GetEffectBuildScope();
                if (scope == null || !scope.CanChoose || !scope.AllowsPaymentMode(paymentMode) ||
                    !scope.TryGetCandidateSourceZoneId(targetId, out var actualSourceZoneId) ||
                    sourceZoneId != actualSourceZoneId) return false;
                command = EffectInteractionCommands.PrepareEffectBuildPayment(scope, targetId, paymentMode);
                LastPreparedSelectionInteractionId = scope.Selection.InteractionId;
            }
            else command = NewCommand("prepare_action");
            command.TargetId = targetId ?? string.Empty;
            if (GetEffectBuildScope == null) command.Parameters["actionKind"] = actionKind;
            if (paymentMode != null) command.Parameters["paymentMode"] = paymentMode;
            if (sourceZoneId != null) command.Parameters["expectedSourceZoneId"] = sourceZoneId;
            preparationRequested = true;
            return Send(command);
        }
        public bool ChangePaymentMode(string mode)
        {
            using var latency = InteractionLatencyTrace.Begin("payment.change_mode");
            var payment = Current;
            if (shuttingDown || IsInFlight || actionKind != "build" || payment == null || !payment.IsReady ||
                mode != "resources" && mode != "gold") return false;
            var command = NewCommand("change_payment_mode");
            command.Parameters["ownerEffectId"] = payment.PaymentOwnerEffectId;
            command.Parameters["interactionId"] = payment.InteractionId;
            command.Parameters["paymentGeneration"] = payment.PaymentGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["paymentMode"] = mode;
            return Send(command);
        }
        public bool Cancel(Action afterApplied)
        {
            // 准确拒绝会同步刷新原页面；该回执的刷新不能重发同一次取消。
            if (shuttingDown || IsInFlight || notifyingCancellation) return false;
            var payment = Current;
            if (payment == null)
            {
                if (preparationRequested) return false;
                afterApplied?.Invoke();
                return true;
            }
            if (!payment.CanCancel) return false;
            var command = GetEffectBuildScope == null ? NewCommand("cancel_action") :
                EffectInteractionCommands.CancelEffectBuildPayment(context.LocalPlayerId, payment.PreparationOwnerEffectId, View.StateRevision);
            command.Parameters["preparationOwnerEffectId"] = payment.PreparationOwnerEffectId;
            afterCancellation = afterApplied;
            return Send(command);
        }
        public bool BindFinalCommand(GameCommand command)
        {
            if (shuttingDown) return false;
            var payment = Current;
            if (!HasPreparation) return true;
            if (command == null || IsInFlight || payment == null || !payment.IsReady || !payment.CanConfirm) return false;
            command.Parameters["paymentPreparationOwnerEffectId"] = payment.PreparationOwnerEffectId;
            return true;
        }
        public bool NotifyResolved(string gameId, string id, bool applied)
        {
            using var latency = InteractionLatencyTrace.Begin("payment.notify_resolved", id);
            if (!IsInFlight || id != commandId || gameId != commandGameId ||
                context.CurrentState?.GameId != commandGameId || context.LocalPlayerId != commandPlayerId) return false;
            commandId = commandGameId = null;
            LastResolvedApplied = applied;
            var operation = commandOperation; commandOperation = null;
            LastResolvedOperation = operation;
            var continuation = afterCancellation;
            var resolvedPlayer = commandPlayerId;
            afterCancellation = null;
            bool cancelling = operation == "cancel_action" || operation == "cancel_effect_build";
            bool preparing = operation == "prepare_action" || operation == "prepare_effect_build";
            if (applied && cancelling || !applied && preparing && Current == null)
                preparationRequested = false;
            var wasNotifyingCancellation = notifyingCancellation;
            notifyingCancellation |= cancelling;
            try { using (InteractionLatencyTrace.Begin("payment.changed_callback", id)) changed?.Invoke(); }
            finally { notifyingCancellation = wasNotifyingCancellation; }
            if (applied && !shuttingDown && context.CurrentState?.GameId == gameId && context.LocalPlayerId == resolvedPlayer)
                using (InteractionLatencyTrace.Begin("payment.follow_callback", id)) continuation?.Invoke();
            return true;
        }
        public void ResetAfterCommit() { preparationRequested = false; afterCancellation = null; }
        public void BeginShutdown()
        {
            shuttingDown = true;
            commandId = commandGameId = commandOperation = null;
            preparationRequested = false;
            afterCancellation = null;
        }
        private GameCommand NewCommand(string operation)
        {
            var command = new GameCommand { Kind = GameCommandKind.PreparePaymentDraft, PlayerId = context.LocalPlayerId };
            command.Parameters["operation"] = operation;
            command.Parameters["expectedRevision"] = (View?.StateRevision ?? context.CurrentState.EffectRuntime.StateRevision)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            return command;
        }
        private bool Send(GameCommand command)
        {
            using var latency = InteractionLatencyTrace.Begin("payment.send", command?.CommandId);
            commandId = command.CommandId; commandGameId = context.CurrentState.GameId; commandPlayerId = command.PlayerId;
            commandOperation = command.Parameters["operation"];
            var submittedGame = commandGameId;
            try
            {
                gateway.Submit(command, new SubmitCallbacks(prompt, CommandGateway.BuildWaitingForHostPrompt("费用准备"))
                {
                    BeforeRejectedPrompt = _ => NotifyResolved(submittedGame, command.CommandId, false),
                    OnAppliedLocally = _ => NotifyResolved(submittedGame, command.CommandId, true)
                });
            }
            catch { NotifyResolved(submittedGame, command.CommandId, false); throw; }
            return true;
        }
        public static bool IsPreparedActionRequest(InteractionRequest request) => IsPreparedParameters(request?.PromptParameters);
        public static bool SameIdentity(PreparedActionPaymentView first, PreparedActionPaymentView second) =>
            first != null && second != null && first.PreparationOwnerEffectId == second.PreparationOwnerEffectId &&
            first.PaymentOwnerEffectId == second.PaymentOwnerEffectId && first.PaymentId == second.PaymentId &&
            first.PaymentGeneration == second.PaymentGeneration && first.OperationWindowId == second.OperationWindowId &&
            first.WindowGeneration == second.WindowGeneration && first.AuthorizationKind == second.AuthorizationKind &&
            first.SelectionOwnerEffectId == second.SelectionOwnerEffectId && first.SelectionInteractionId == second.SelectionInteractionId &&
            first.SelectionCandidateSetId == second.SelectionCandidateSetId && first.SelectionCandidateSetVersion == second.SelectionCandidateSetVersion;
        public static bool IsPreparedActionRequest(InteractionRequestProjection request) => IsPreparedParameters(request?.PromptParameters);
        public static bool IsPreparedParameters(NormalizedValue parameters)
        {
            var kind = parameters?.Properties.Find(entry => entry.Name == "sourceKind")?.Value?.StringValue;
            var owner = parameters?.Properties.Find(entry => entry.Name == "preparationOwnerEffectId")?.Value?.StringValue;
            var commit = parameters?.Properties.Find(entry => entry.Name == "isCommitRequested")?.Value;
            var committed = parameters?.Properties.Find(entry => entry.Name == "isCommitted")?.Value;
            return (kind == "build" || kind == "city_move") && !string.IsNullOrEmpty(owner) &&
                commit?.BooleanValue != true && committed?.BooleanValue != true;
        }
        public static bool HasBlockingChoice(IEnumerable<InteractionRequest> requests, int playerId) =>
            requests?.Any(request => request != null && request.Status == "open" && request.AnsweringPlayerId == playerId &&
                GameStateVisibilityPolicy.IsVisible(request.Visibility, request.AnsweringPlayerId, GameStateViewer.Player(playerId)) &&
                !request.IsInternalMainlineInteraction() &&
                !(IsPreparedActionRequest(request) && IsMainActionAuthorization(request.PromptParameters))) == true;
        private static bool IsMainActionAuthorization(NormalizedValue parameters)
        {
            var kind = YC.Domain.Effects.ItemTransferEffectExecutor.Text(parameters, "authorizationKind");
            return kind.Length == 0 || kind == "main_action";
        }
        public static bool HasBlockingChoice(GameState state, int playerId) => state != null &&
            ((state.PendingChoice?.IsValid() == true) || (state.PendingCardSession?.IsValid() == true) ||
                (state.PendingCharacterEffect?.IsValid() == true) || (state.PendingSpecialAction?.IsValid(state) == true) ||
                HasBlockingChoice(state.EffectRuntime?.InteractionRequests, playerId));
    }
}
