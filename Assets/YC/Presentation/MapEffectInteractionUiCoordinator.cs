using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Presentation;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>通用地图交互；入场、主要行动、角色和设施共用候选高亮与回答入口。</summary>
    internal sealed class MapEffectInteractionUiCoordinator : InteractionBase, IInteractionRequestRenderer, IMapConfirmationScope, IRouteInteraction
    {
        private readonly Func<GameState> state;
        private readonly Func<int> player;
        private readonly CharacterCardEffectChoiceDialog dialog;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> highlights;
        private readonly Action clearHighlights;
        private readonly Action<GameCommand> submit;
        private readonly Action<string> prompt;
        private string inFlight = string.Empty;
        private string rendered = string.Empty;
        private int revision = -1;
        private int candidateVersion = -1;
        private bool ownsPresentation;
        private bool ownsDialog;
        private InteractionRequestProjection optionalInfluencePresentation;
        public GameplayExpansionUi.GameplayCopy Copy { private get; set; }
        public GameplayExpansionUi ExpansionUi { private get; set; }
        public Func<IReadOnlyList<InteractionRequest>> GetVisibleRequests { private get; set; }
        public Func<GameStateView> GetVisibleState { private get; set; }
        private Func<GameplayTextRef, GameplayTextDocument> resolveText;
        private Action<InteractionRequestProjection, GameplayTextDocument> setDocument;
        private GameStateView presentationView;
        private InteractionRequestProjection presentationInput, presentationProjection, renderedProjection, documentRequest;
        private GameplayTextDocument promptDocument;
        private string presentationGameId;
        private int presentationPlayerId = -1;
        public InteractionRequestProjection PresentationRequest => Current();

        public void BindGameplayText(Func<GameplayTextRef, GameplayTextDocument> resolve,
            Action<InteractionRequestProjection, GameplayTextDocument> render)
        {
            ClearPromptDocument();
            resolveText = resolve;
            setDocument = render;
        }
        public string MapConfirmationScope
        {
            get
            {
                var request = Current();
                return request == null || !string.IsNullOrEmpty(inFlight) ? string.Empty :
                    request.InteractionId + ":" + request.StateRevision + ":" + request.CandidateSetId + ":" + request.CandidateSetVersion;
            }
        }

        public MapEffectInteractionUiCoordinator(Func<GameState> state, Func<int> player,
            CharacterCardEffectChoiceDialog dialog, Action<IReadOnlyList<WorkflowHighlight>> highlights,
            Action clearHighlights, Action<GameCommand> submit, Action<string> prompt)
        {
            this.state = state; this.player = player; this.dialog = dialog;
            this.highlights = highlights; this.clearHighlights = clearHighlights;
            this.submit = submit; this.prompt = prompt;
        }

        public override string Id => "effect.map.interaction";
        public override InteractionPriority Priority => InteractionPriority.PendingResolution;
        int IInteractionRequestRenderer.Priority => 320;
        public override bool IsActive => Current() != null;
        public static bool Supports(string type) => type == "setup.influence.optional" || type == "effect.influence.place.target" || type == "effect.influence.remove.target" || type == PlayerEntranceEffectExecutor.InteractionTypeId || type == CityMoveEffectExecutor.TargetInteractionTypeId || type == "action.decline_effect" || type == "effect.route.choose";
        public bool CanRender(InteractionRequestProjection request) => request != null && request.VisibleToViewer &&
            !EffectDialogSelectionSpec.IsInfluenceMapArray(request) &&
            request.AnsweringPlayerId == player() && request.Status == "open" && (Supports(request.InteractionTypeId) ||
                IsReplacementRequest(request));

        private static bool IsReplacementRequest(InteractionRequestProjection request) =>
            request.InteractionTypeId == "lua.choice" &&
            (request.PromptKey == "effect.influence.replace.target" ||
             request.PromptKey == "effect.influence.replace.choose_target");

        private InteractionRequestProjection Current()
        {
            var visible = GetVisibleState?.Invoke() ?? ExpansionUi?.VisibleView();
            var requests = GetVisibleRequests == null ? VisibleInteractionRequestSource.Read(state(), visible, player()) : GetVisibleRequests();
            if (requests == null) return null;
            foreach (var request in requests)
            {
                var projected = InteractionRequestProjector.ProjectForPlayer(request, player());
                if (CanRender(projected)) return ProjectForPresentation(projected);
            }
            return null;
        }

        private InteractionRequestProjection ProjectForPresentation(InteractionRequestProjection request)
        {
            var gameId = state()?.GameId;
            var playerId = player();
            var visible = GetVisibleState?.Invoke() ?? ExpansionUi?.VisibleView();
            if (presentationGameId == gameId && presentationPlayerId == playerId && ReferenceEquals(presentationView, visible) &&
                presentationInput?.StateRevision == request.StateRevision &&
                InteractionCommandIntent.SameContract(gameId, presentationInput, gameId, request)) return presentationProjection;
            ClearPromptDocument();
            if (presentationGameId != null && (presentationGameId != gameId || presentationPlayerId != playerId))
            {
                rendered = string.Empty;
                revision = candidateVersion = -1;
                inFlight = string.Empty;
            }
            var requests = GetVisibleRequests == null ? VisibleInteractionRequestSource.Read(state(), visible, playerId) : GetVisibleRequests();
            var original = requests?.FirstOrDefault(value => value != null && value.StateRevision == request.StateRevision &&
                InteractionCommandIntent.SameContract(gameId, InteractionRequestProjector.ProjectForPlayer(value, playerId), gameId, request));
            var safe = original == null ? GameStateCloneService.DeepClone(request) :
                InteractionRequestProjector.ProjectForPlayer(original, playerId, visible, gameId);
            if (original == null || visible == null || visible.GameId != gameId ||
                !GameplayHudFrame.MatchesSummaryRequest(safe, visible)) safe.Presentation = null;
            presentationView = visible;
            presentationInput = GameStateCloneService.DeepClone(request);
            presentationProjection = safe;
            presentationGameId = gameId;
            presentationPlayerId = playerId;
            return safe;
        }

        private GameplayTextDocument ResolvePromptDocument(InteractionRequestProjection request)
        {
            if (ReferenceEquals(documentRequest, request)) return promptDocument;
            documentRequest = request;
            promptDocument = EffectDialogTextDocuments.ResolvePrompt(request, resolveText);
            return promptDocument;
        }

        private void PresentPrompt(InteractionRequestProjection request)
        {
            var document = ResolvePromptDocument(request);
            if (document != null && setDocument != null) setDocument(request, document);
            else prompt(Prompt(request));
        }

        private void ClearPromptDocument()
        {
            if (documentRequest != null) setDocument?.Invoke(documentRequest, null);
            documentRequest = null;
            promptDocument = null;
        }

        private string Slot(InteractionRequestProjection request, string candidate)
        {
            if (request.InteractionTypeId == "effect.influence.place.target" || request.InteractionTypeId == "setup.influence.optional") return candidate;
            var influence = state()?.Map?.Influences.Find(i => i.InfluenceId == candidate);
            return influence == null ? string.Empty : influence.SlotId;
        }

        private List<WorkflowHighlight> Targets(InteractionRequestProjection request)
        {
            var result = new List<WorkflowHighlight>();
            foreach (var candidate in request.CandidateIds)
            {
                if (request.InteractionTypeId == "effect.route.choose")
                {
                    result.Add(new WorkflowHighlight(WorkflowHighlightTargetKind.Route, candidate, WorkflowHighlightSemantic.EventInfluenceTarget));
                    continue;
                }
                if (request.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId || request.InteractionTypeId == CityMoveEffectExecutor.TargetInteractionTypeId)
                {
                    result.Add(new WorkflowHighlight(WorkflowHighlightTargetKind.Location, candidate, request.InteractionTypeId == CityMoveEffectExecutor.TargetInteractionTypeId ? WorkflowHighlightSemantic.MoveTarget : WorkflowHighlightSemantic.InitialPlacement));
                    continue;
                }
                var slot = Slot(request, candidate);
                if (!string.IsNullOrEmpty(slot)) result.Add(new WorkflowHighlight(
                    WorkflowHighlightTargetKind.InfluenceSlot, slot,
                    request.InteractionTypeId == "effect.influence.place.target" || request.InteractionTypeId == "setup.influence.optional"
                        ? WorkflowHighlightSemantic.DeployTarget : WorkflowHighlightSemantic.EventInfluenceTarget));
            }
            return result;
        }

        private string Prompt(InteractionRequestProjection request) =>
            request.InteractionTypeId == "effect.route.choose" ? Copy?.RouteTargetPrompt :
            request.InteractionTypeId == "setup.influence.optional" ? Copy?.OptionalInfluencePrompt :
            IsReplacementRequest(request) ? "请选择高亮的对手影响力进行替换。" :
            request.InteractionTypeId == "action.decline_effect" ? OptionalPrompt(request) :
            request.InteractionTypeId == CityMoveEffectExecutor.TargetInteractionTypeId ? "请选择高亮的城市移动目标。" :
            request.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId ? "请选择高亮的入场地点。入场事件结算后轮到下一位玩家。" :
            (request.InteractionTypeId == "effect.influence.place.target" ? "请选择高亮位置放置影响力。" : "请选择高亮的影响力移除。") +
            (request.AllowDecline ? "确认前按 Esc 可取消本次主要行动。" : string.Empty);

        private static string OptionalPrompt(InteractionRequestProjection request) =>
            request.PromptKey == "character.tin_man.purchase.12" ? "支付 12 金券，获得 1 至纯源石？" :
            request.PromptKey == "character.tin_man.purchase.15" ? "支付 15 金券，获得 1 至纯源石？" : "是否执行此项效果？";

        public void Render(InteractionRequestProjection request)
        {
            if (!CanRender(request)) { Clear(); return; }
            if (presentationGameId == state()?.GameId && presentationPlayerId == player() &&
                rendered == request.InteractionId && revision > request.StateRevision) return;
            request = ProjectForPresentation(request);
            if (rendered != request.InteractionId) inFlight = string.Empty;
            if (!string.IsNullOrEmpty(inFlight)) return;
            if (rendered == request.InteractionId && revision == request.StateRevision && candidateVersion == request.CandidateSetVersion &&
                ReferenceEquals(renderedProjection, request)) return;
            renderedProjection = request;
            rendered = request.InteractionId; revision = request.StateRevision;
            candidateVersion = request.CandidateSetVersion;
            ClearOptionalInfluencePresentation();
            if (ownsDialog) { dialog.Hide(); ownsDialog = false; }
            ownsPresentation = true;
            if (request.InteractionTypeId == "setup.influence.optional")
            {
                highlights(Targets(request));
                if (ExpansionUi == null) throw new InvalidOperationException("可选初始影响力缺少底部结算宿主。");
                optionalInfluencePresentation = request;
                ExpansionUi.ShowOptionalInfluenceSkip(request, () => SkipInitialInfluence(request), () => IsCurrent(request));
                PresentPrompt(request);
                return;
            }
            if (request.InteractionTypeId == "action.decline_effect")
            {
                clearHighlights();
                if (request.PromptParameters?.Properties.Find(entry => entry.Name == "sourceKind")?.Value?.StringValue == "condition")
                {
                    ownsDialog = true;
                    if (ExpansionUi == null) throw new InvalidOperationException("条件付款缺少正式玩法文档渲染宿主。");
                    dialog.BindGameplayText(resolveText, ExpansionUi.RenderEnterpriseDetails);
                    dialog.ShowPaymentConfirmation(request, () => IsCurrent(request),
                        () => Answer(request, "continue", false),
                        request.CandidateIds.Contains("decline") ? (Action)(() => Answer(request, "decline", false)) : null);
                    PresentPrompt(request);
                    return;
                }
                string description = OptionalPrompt(request);
                ownsDialog = true;
                dialog.ShowOptions(request, dialog.Copy.OptionalEffectTitle, description, new[]
                {
                    ImmediateOption(request, "continue", dialog.Copy.ExecuteLabel),
                    ImmediateOption(request, "decline", dialog.Copy.DeclineLabel)
                }, () => Answer(request, "decline", false));
                PresentPrompt(request);
                return;
            }
            highlights(Targets(request)); PresentPrompt(request);
        }

        public override InteractionPresentation BuildPresentation()
        {
            if (!string.IsNullOrEmpty(inFlight)) return InteractionPresentation.Busy;
            var request = Current();
            if (request == null) return InteractionPresentation.Empty;
            var document = ResolvePromptDocument(request);
            return new InteractionPresentation(Targets(request), document == null ? Prompt(request) : string.Empty,
                InteractionMode.Busy, true, document);
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            var request = Current();
            if (request == null) return InteractionResult.Passthrough;
            if (GameplayHudFrame.EffectInputSuspended) return InteractionResult.Consumed;
            if (request.PromptParameters?.Properties.Find(entry => entry.Name == "sourceKind")?.Value?.StringValue == "condition")
                return InteractionResult.Consumed;
            if (!string.IsNullOrEmpty(inFlight)) return InteractionResult.Consumed;
            var candidate = request.CandidateIds.Find(id => Slot(request, id) == slotId);
            if (string.IsNullOrEmpty(candidate)) { prompt("请选择高亮的合法目标。"); return InteractionResult.Consumed; }
            Answer(request, candidate, false);
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnLocationClicked(string locationId)
        {
            var request = Current();
            if (request == null) return InteractionResult.Passthrough;
            if (GameplayHudFrame.EffectInputSuspended) return InteractionResult.Consumed;
            if ((request.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId || request.InteractionTypeId == CityMoveEffectExecutor.TargetInteractionTypeId) && request.CandidateIds.Contains(locationId))
                Answer(request, locationId, false);
            else prompt("请选择高亮的合法目标。");
            return InteractionResult.Consumed;
        }
        public override InteractionResult OnMobileCityClicked() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public InteractionResult OnRouteClicked(string routeId)
        {
            var request = Current();
            if (request == null) return InteractionResult.Passthrough;
            if (GameplayHudFrame.EffectInputSuspended || !string.IsNullOrEmpty(inFlight)) return InteractionResult.Consumed;
            if (request.InteractionTypeId == "effect.route.choose" && request.CandidateIds.Contains(routeId))
                Answer(request, routeId, false);
            return InteractionResult.Consumed;
        }
        public override InteractionResult OnEscape()
        {
            var request = Current();
            if (request == null) return InteractionResult.Passthrough;
            if (GameplayHudFrame.EffectInputSuspended) return InteractionResult.Consumed;
            if (request.InteractionTypeId == "setup.influence.optional") SkipInitialInfluence(request);
            else if (request.AllowDecline) Answer(request, null, true);
            else prompt("请先完成当前效果的目标选择。");
            return InteractionResult.Consumed;
        }

        private void Answer(InteractionRequestProjection request, string candidate, bool cancel)
        {
            if (GameplayHudFrame.EffectInputSuspended || !IsCurrent(request) ||
                (cancel ? !request.AllowDecline : !request.CandidateIds.Contains(candidate))) return;
            var command = EffectInteractionCommands.Answer(request, player(),
                cancel ? null : new[] { candidate }, cancel);
            SubmitAnswer(command);
        }

        private void SkipInitialInfluence(InteractionRequestProjection request)
        {
            if (GameplayHudFrame.EffectInputSuspended || !IsCurrent(request) ||
                request.InteractionTypeId != "setup.influence.optional" || request.MinSelections != 0 || !request.AllowDecline) return;
            // 空OptionIds本身没有答案；正式协议以answerValue=false表示跳过本次可选放置。
            SubmitAnswer(EffectInteractionCommands.Answer(request, player(), Array.Empty<string>(), true));
        }

        private void SubmitAnswer(GameCommand command)
        {
            inFlight = command.CommandId;
            ClearPromptDocument();
            ownsPresentation = false;
            ClearOptionalInfluencePresentation();
            if (ownsDialog) { dialog.Hide(); ownsDialog = false; }
            clearHighlights();
            try { submit(command); }
            catch { NotifyCommandSettled(command.CommandId); throw; }
        }

        private bool IsCurrent(InteractionRequestProjection request)
        {
            var current = Current();
            return request != null && current != null && string.IsNullOrEmpty(inFlight) &&
                current.InteractionId == request.InteractionId && current.StateRevision == request.StateRevision &&
                current.AnsweringPlayerId == request.AnsweringPlayerId &&
                current.CandidateSetId == request.CandidateSetId &&
                current.CandidateSetVersion == request.CandidateSetVersion;
        }

        private EffectDialogOption ImmediateOption(InteractionRequestProjection request, string id, string label)
        {
            return new EffectDialogOption(label, () => Answer(request, id, false), request.CandidateIds.Contains(id))
            {
                StableId = id, RequestId = request.InteractionId, Revision = request.StateRevision,
                ExecuteImmediately = true, IsCurrent = () => IsCurrent(request)
            };
        }

        public override void NotifyCommandSettled(string commandId)
        {
            if (!string.IsNullOrEmpty(commandId) && inFlight == commandId)
            { inFlight = string.Empty; rendered = string.Empty; revision = -1; }
        }
        public override void Cancel() { Clear(); }
        private void ClearOptionalInfluencePresentation()
        {
            var request = optionalInfluencePresentation;
            optionalInfluencePresentation = null;
            if (request != null) ExpansionUi?.ClearOptionalInfluenceSkip(request.InteractionId,
                request.StateRevision, request.CandidateSetId, request.CandidateSetVersion, request.AnsweringPlayerId);
        }
        public void Clear()
        {
            ClearPromptDocument();
            presentationView = null;
            presentationInput = presentationProjection = renderedProjection = null;
            presentationGameId = null;
            presentationPlayerId = -1;
            ClearOptionalInfluencePresentation();
            rendered = string.Empty; revision = candidateVersion = -1; inFlight = string.Empty;
            if (!ownsPresentation) return;
            ownsPresentation = false;
            if (ownsDialog) { dialog.Hide(); ownsDialog = false; }
            clearHighlights();
        }
    }
}
