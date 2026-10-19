using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Domain.Rules;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>正常/额外采集与各来源探索共用的地图输入和请求 renderer。</summary>
    internal sealed class RouteResourceSelectionInteractionUiCoordinator : InteractionBase,
        IInteractionRequestRenderer, IRouteInteraction
    {
        private readonly Func<GameState> state;
        private readonly Func<GameStateView> visibleState;
        private readonly Func<int> player;
        private readonly Func<IReadOnlyList<InteractionRequest>> requests;
        private readonly IRouteSelectionClientViewReader reader;
        private readonly EventChoiceDialog dialog;
        private readonly MobileCityWorkflowViewAdapter view;
        private readonly Action<GameCommand> submit;
        private readonly Action refreshConfirmation;
        private InteractionRequestProjection rendered;
        private string gameId;
        private string inFlight;
        private readonly InteractionCommandIntent confirmedIntent = new InteractionCommandIntent();
        private bool ownsDialog;
        private bool ownsPresentation;
        private string editingOptionKey;
        private int lastInputFrame = -1;
        private int lastSubmissionFrame = -1;
        private readonly IRouteResourceSelectionService service;
        private bool localPreview;
        private GameState localPreviewState;
        private int localPreviewPlayer, localPreviewRevision;
        public Func<bool> IsNetworkSession { private get; set; }
        internal bool CanPreserveNormalHarvestDraft(InteractionRequestProjection routedRequest)
        {
            if (IsNetworkSession?.Invoke() != true || localPreview || Presenter.Session?.Purpose != RouteSelectionPurpose.NormalHarvest ||
                rendered == null || (!ownsPresentation && !confirmedIntent.HasPending)) return false;
            var current = Current();
            var currentGame = state()?.GameId;
            return current != null && CanRender(current) && current.StateRevision >= rendered.StateRevision &&
                InteractionCommandIntent.SameContract(gameId, rendered, currentGame, current) &&
                InteractionCommandIntent.SameContract(gameId, routedRequest, currentGame, current) &&
                (!confirmedIntent.HasPending || confirmedIntent.IsPendingForRequest(currentGame, current));
        }
        public bool HasUnsubmittedMainActionDraft => localPreview && string.IsNullOrEmpty(inFlight);
        public bool IsMainActionSubmissionInFlight => localPreview && !string.IsNullOrEmpty(inFlight);

        public RouteResourceSelectionInteractionUiCoordinator(Func<GameState> state,
            Func<GameStateView> visibleState, Func<int> player,
            Func<IReadOnlyList<InteractionRequest>> requests, IRouteSelectionClientViewReader reader,
            IRouteResourceSelectionService service, EventChoiceDialog dialog,
            MobileCityWorkflowViewAdapter view, Action<GameCommand> submit, Action refreshConfirmation)
        {
            this.state = state; this.visibleState = visibleState; this.player = player;
            this.service = service;
            this.requests = requests; this.reader = reader; this.dialog = dialog;
            this.view = view; this.submit = submit; this.refreshConfirmation = refreshConfirmation;
            Presenter = new RouteResourceSelectionPresenter(state, service);
        }

        public RouteResourceSelectionPresenter Presenter { get; }
        public Func<bool> CanSubmit { private get; set; }
        public Func<bool> RequiresExplicitRefresh { private get; set; }
        public override string Id => "route_resource.selection.ui";
        public override InteractionPriority Priority => localPreview ? InteractionPriority.ActiveAction : InteractionPriority.PendingResolution;
        int IInteractionRequestRenderer.Priority => 340;
        public override bool IsActive => localPreview && (IsLocalPreviewCurrent() || !string.IsNullOrEmpty(inFlight)) || Current() != null;
        public bool CanConfirm => !localPreview && IsCurrent(rendered) && Presenter.CanConfirm && !ownsDialog;
        public bool CanRender(InteractionRequestProjection request) => request != null &&
            request.VisibleToViewer && request.Status == "open" && request.AnsweringPlayerId == player() &&
            request.InteractionTypeId == "route_resource.selection";

        private InteractionRequestProjection Current()
        {
            var visible = requests();
            if (visible == null) return null;
            foreach (var request in visible)
            {
                var projected = InteractionRequestProjector.ProjectForPlayer(request, player());
                if (CanRender(projected)) return projected;
            }
            return null;
        }

        public void BeginLocalExploration()
        {
            if (CanSubmit?.Invoke() == false || !string.IsNullOrEmpty(inFlight)) return;
            var current = state();
            var publicView = visibleState();
            var result = service.TryCreateExplorationPreview(current, publicView, player(), out var session, out var draft, out var projection);
            if (!result.IsValid) { view.ShowPrompt(dialog.Copy.RouteInvalidPrompt); return; }
            Clear(); localPreview = true; localPreviewState = current; localPreviewPlayer = player();
            localPreviewRevision = session.OpenedAtStateRevision; gameId = current.GameId;
            Presenter.BeginLocalPreview(session, draft, projection); ownsPresentation = true; PresentEdit();
        }
        private bool IsLocalPreviewCurrent() => localPreview && ReferenceEquals(state(), localPreviewState) &&
            state()?.GameId == gameId && player() == localPreviewPlayer &&
            (state()?.EffectRuntime?.StateRevision ?? visibleState()?.StateRevision) == localPreviewRevision;
        public void CancelLocalPreview() { if (HasUnsubmittedMainActionDraft) Clear(); }
        public void RefreshLocalPreview()
        {
            if (!localPreview || !string.IsNullOrEmpty(inFlight)) return;
            if (!IsLocalPreviewCurrent()) { Clear(); return; }
            Presenter.RefreshFromState(); PresentEdit();
        }

        public void Render(InteractionRequestProjection request)
        {
            if (!CanRender(request)) { Clear(); return; }
            if (localPreview) Clear();
            var currentGame = state()?.GameId;
            if (gameId == currentGame && request.InteractionId == rendered?.InteractionId && request.StateRevision < rendered.StateRevision) return;
            if (!string.IsNullOrEmpty(currentGame) && confirmedIntent.IsBusy && !confirmedIntent.BelongsToContext(currentGame, player())) confirmedIntent.Reset();
            // 联机正常采集已确认的原计划等待准确回执；他人发表不能清草稿或重新编制答案。
            if (IsNetworkSession?.Invoke() == true && Presenter.Session?.Purpose == RouteSelectionPurpose.NormalHarvest &&
                confirmedIntent.IsPendingForRequest(currentGame, request)) return;
            var publicState = visibleState();
            var interaction = publicState?.Interactions?.FirstOrDefault(value => value.InteractionId == request.InteractionId);
            if (interaction == null || !reader.TryRead(publicState, interaction, out var session, out var binding).IsValid)
            { view.ShowPrompt(dialog.Copy.RouteInvalidPrompt); return; }
            var selectable = session.SelectableLocationUpperBound.Where(location =>
                request.CandidateIds.Contains(RouteSelectionAnswerCodec.Encode(binding.ScopeKey,
                    RouteSelectionCandidateKind.Location, location))).ToArray();
            var same = InteractionCommandIntent.SameContract(gameId, rendered, currentGame, request);
            if (same && request.StateRevision < rendered.StateRevision) return;
            if (!same || !Presenter.TryRefreshOpenBinding(session, binding, selectable, out _))
            {
                Clear();
                Presenter.Begin(session, binding, selectable);
            }
            gameId = currentGame; rendered = request;
            if (ownsDialog && editingOptionKey != RouteOptionKey(Presenter.Draft?.EditingRouteId))
            { dialog.Hide(); ownsDialog = false; Presenter.CancelRouteEdit(); }
            if (CanSubmit?.Invoke() != false && confirmedIntent.WaitingForRefresh)
            {
                var dependency = Presenter.TryValidateConfirmedIntent(out var plan) ? plan : "invalid";
                if (confirmedIntent.TryRebuild(currentGame, request, dependency, out var rebuilt))
                { Dispatch(rebuilt); return; }
            }
            inFlight = confirmedIntent.CommandId;
            if (confirmedIntent.IsBusy) return;
            ownsPresentation = true;
            view.SetHighlights(Presenter.BuildHighlights());
            GameplayHudFrame.Active?.SetRouteSelectionSummary(Prompt());
        }

        private string Prompt()
        {
            var copy = dialog.Copy;
            if (confirmedIntent.IsBusy || IsMainActionSubmissionInFlight) return copy.RouteSubmittingPrompt;
            var projection = Presenter.Projection;
            if (projection == null || !projection.Validation.IsValid) return copy.RouteInvalidPrompt;
            var instruction = Presenter.Session.Purpose == RouteSelectionPurpose.Exploration
                ? copy.RouteExplorePrompt : copy.RouteHarvestPrompt;
            var preview = projection.Preview;
            var cost = preview == null ? string.Format(copy.RouteDraftCostFormat, projection.ProvisionalGoldVoucherAmount)
                : string.Format(copy.RouteFinalCostFormat, preview.SelectedLocationIds.Count, preview.PaymentPlan.TotalGoldVoucherAmount);
            if (preview?.PrunedRouteIds.Count > 0)
                cost += " " + string.Format(copy.RoutePrunedFormat, preview.PaymentPlan.TotalGoldVoucherAmount);
            if (preview != null && !preview.FinalizationValidation.IsValid)
                cost += " " + copy.RouteInvalidPrompt;
            return instruction + " " + cost;
        }

        public override InteractionPresentation BuildPresentation() => !Presenter.IsActive
            ? InteractionPresentation.Empty
            : new InteractionPresentation(!confirmedIntent.IsBusy && !IsMainActionSubmissionInFlight ? Presenter.BuildHighlights() : null,
                Prompt(), InteractionMode.Busy, true);

        public InteractionResult OnRouteClicked(string routeId)
        {
            if (!IsActive) return InteractionResult.Passthrough;
            if (!CanUseMapInput()) return InteractionResult.Consumed;
            var option = Presenter.SelectRoute(routeId);
            if (option == null) return InteractionResult.Consumed;
            var bound = rendered;
            var boundDraftVersion = Presenter.Draft.LocalDraftVersion;
            var boundGame = gameId;
            editingOptionKey = RouteOptionKey(routeId);
            var boundOption = editingOptionKey;
            Func<bool> current = () => gameId == boundGame && (localPreview ? IsLocalPreviewCurrent() && CanSubmit?.Invoke() != false : IsCurrent(bound)) &&
                Presenter.Draft?.LocalDraftVersion == boundDraftVersion && RouteOptionKey(routeId) == boundOption;
            ownsDialog = true;
            view.ShowRoutePaymentOptions(new RoutePaymentOptionsViewModel
            {
                RouteId = routeId, GoldVoucherAmount = option.TollGoldVoucherAmount,
                Recipients = option.EligibleRecipients,
                SelectedSubjectKey = Presenter.Draft.RecipientChoices.FirstOrDefault(value => value.RouteId == routeId)?.ReceiverSubjectKey,
                IsCurrent = current,
                SelectRecipient = subject =>
                {
                    if (!current()) return;
                    ownsDialog = false;
                    Presenter.ConfirmRecipient(routeId, subject); PresentEdit();
                },
                Cancel = () =>
                {
                    if (!current()) return;
                    ownsDialog = false; Presenter.CancelRouteEdit(); PresentEdit();
                },
                Remove = option.IsOriginallyConfirmed ? (Action)(() =>
                {
                    if (!current()) return;
                    ownsDialog = false; Presenter.RemoveRoute(routeId); PresentEdit();
                }) : null
            });
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnLocationClicked(string locationId)
        {
            if (!IsActive) return InteractionResult.Passthrough;
            if (!CanUseMapInput()) return InteractionResult.Consumed;
            // 航道一次点击编辑；探索资源点沿原交互二次点击确认，不把航道也改为双击。
            if (Presenter.Session.Purpose == RouteSelectionPurpose.Exploration &&
                Presenter.Draft.SelectedLocationIds.Contains(locationId)) Confirm();
            else if (Presenter.ToggleLocation(locationId)) PresentEdit();
            return InteractionResult.Consumed;
        }

        private bool CanUseMapInput()
        {
            if (GameplayHudFrame.EffectInputSuspended || ownsDialog || !(localPreview ? IsLocalPreviewCurrent() && CanSubmit?.Invoke() != false && string.IsNullOrEmpty(inFlight) : IsCurrent(rendered)) ||
                lastInputFrame == Time.frameCount) return false;
            lastInputFrame = Time.frameCount;
            return true;
        }

        private string RouteOptionKey(string routeId)
        {
            var option = Presenter.Projection?.RouteOptions?.FirstOrDefault(value => value.RouteId == routeId);
            return option == null ? null : option.TollGoldVoucherAmount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                string.Join("|", option.EligibleRecipients.Select(value => value.SubjectKey));
        }

        private void PresentEdit()
        {
            view.SetHighlights(Presenter.BuildHighlights());
            var prompt = Prompt();
            GameplayHudFrame.Active?.SetRouteSelectionSummary(prompt);
            refreshConfirmation();
            view.ShowPrompt(prompt);
        }

        public void Confirm() => Answer(false);

        private void Answer(bool cancel)
        {
            if (localPreview) { AnswerLocalPreview(cancel); return; }
            var fresh = Current();
            if (fresh != null && InteractionCommandIntent.SameContract(gameId, rendered, state()?.GameId, fresh)) Render(fresh);
            if (!IsCurrent(rendered) || ownsDialog || GameplayHudFrame.EffectInputSuspended ||
                lastSubmissionFrame == Time.frameCount) return;
            if (!Presenter.TryBuildAnswer(rendered.CandidateIds, cancel, out var answer))
            {
                view.ShowPrompt(dialog.Copy.RouteInvalidPrompt);
                return;
            }
            var command = EffectInteractionCommands.Answer(rendered, player(), answer, false);
            if (!confirmedIntent.Begin(gameId, rendered, command,
                Presenter.Session.Purpose == RouteSelectionPurpose.NormalHarvest && IsNetworkSession?.Invoke() != true, SubmissionDependency())) return;
            lastSubmissionFrame = Time.frameCount;
            Dispatch(command);
        }

        private void AnswerLocalPreview(bool cancel)
        {
            if (cancel) { CancelLocalPreview(); return; }
            if (!IsLocalPreviewCurrent() || CanSubmit?.Invoke() == false || !string.IsNullOrEmpty(inFlight) || ownsDialog ||
                GameplayHudFrame.EffectInputSuspended || lastSubmissionFrame == Time.frameCount) return;
            Presenter.RefreshFromState();
            if (!Presenter.CanConfirm || Presenter.Draft.SelectedLocationIds.Count != 1) return;
            var command = new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = localPreviewPlayer,
                TargetId = Presenter.Draft.SelectedLocationIds[0] };
            command.Parameters[YC.Application.Gameplay.ExploreLocationCommandHandler.RouteIdsParameter] = string.Join(",", Presenter.Draft.ConfirmedRouteIds);
            if (Presenter.Draft.RecipientChoices.Any(choice => choice.ReceiverSubjectKey != "bank" &&
                (!choice.ReceiverSubjectKey.StartsWith("player:", StringComparison.Ordinal) ||
                 !int.TryParse(choice.ReceiverSubjectKey.Substring(7), out var receiver) || receiver <= 0))) return;
            command.Parameters[YC.Application.Gameplay.ExploreLocationCommandHandler.PaymentRecipientsParameter] = string.Join(";", Presenter.Draft.RecipientChoices.Select(choice =>
                choice.RouteId + "=" + (choice.ReceiverSubjectKey == "bank" ? "-1" : choice.ReceiverSubjectKey.StartsWith("player:", StringComparison.Ordinal) ? choice.ReceiverSubjectKey.Substring(7) : string.Empty)));
            lastSubmissionFrame = Time.frameCount;
            try { Dispatch(command); } catch { inFlight = null; throw; }
        }

        private string SubmissionDependency() => Presenter.Projection?.Validation.IsValid == true &&
            Presenter.Projection?.Preview?.FinalizationValidation.IsValid == true
            ? Presenter.Projection.Preview.PlanDigest : "invalid";

        private void Dispatch(GameCommand command)
        {
            inFlight = command.CommandId;
            ownsPresentation = false;
            view.ClearHighlights();
            submit(command);
        }

        private bool IsCurrent(InteractionRequestProjection request) =>
            CanSubmit?.Invoke() != false && !confirmedIntent.IsBusy && request != null && gameId == state()?.GameId && request.AnsweringPlayerId == player() &&
            InteractionCommandIntent.SameContract(gameId, request, state()?.GameId, Current()) &&
            request.StateRevision <= Current().StateRevision;

        public override InteractionResult OnInfluenceSlotClicked(string slotId) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnMobileCityClicked() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnEscape()
        {
            if (!IsActive) return InteractionResult.Passthrough;
            if (ownsDialog || GameplayHudFrame.EffectInputSuspended) return InteractionResult.Consumed;
            if (Presenter.Session?.AllowCancelBeforeCommit == true) Answer(true);
            else view.ShowPrompt(dialog.Copy.RouteCannotCancelPrompt);
            return InteractionResult.Consumed;
        }

        public void Clear()
        {
            if (localPreview) GameplayHudFrame.Active?.SetRouteSelectionSummary(null);
            localPreview = false; localPreviewState = null;
            if (ownsDialog) dialog.Hide();
            if (ownsPresentation)
            {
                view.ClearHighlights();
                GameplayHudFrame.Active?.SetRouteSelectionSummary(null);
            }
            ownsDialog = false; ownsPresentation = false; rendered = null; gameId = null; inFlight = null;
            Presenter.Clear();
        }

        public override void Cancel() { if (string.IsNullOrEmpty(inFlight) && !confirmedIntent.IsBusy) Clear(); }
        public override void NotifyCommandSettled(string commandId)
        {
        }
        // 调用方明确报告未进入提交口；只释放该预览自己记录的局、玩家和命令锁，不伪造 Host 回执。
        public void NotifyMainActionCommandNotDispatched(GameCommand command)
        {
            if (!localPreview || command == null || command.CommandId != inFlight || command.PlayerId != localPreviewPlayer) return;
            ResolveLocalPreview(gameId, command.CommandId, false);
        }
        private bool ResolveLocalPreview(string resolvedGameId, string commandId, bool accepted)
        {
            if (!localPreview || commandId != inFlight || resolvedGameId != gameId) return false;
            inFlight = null;
            if (accepted || !IsLocalPreviewCurrent()) Clear();
            else { ownsPresentation = true; PresentEdit(); }
            return true;
        }
        public override void NotifyInteractionCommandResolved(string resolvedGameId, string commandId, bool accepted, CommandErrorCode errorCode)
        {
            if (ResolveLocalPreview(resolvedGameId, commandId, accepted)) return;
            if (confirmedIntent.Resolve(resolvedGameId, commandId, accepted, errorCode, RequiresExplicitRefresh?.Invoke() == true)) inFlight = null;
        }
    }
}
