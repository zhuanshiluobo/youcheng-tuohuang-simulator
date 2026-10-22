using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>正式效果建设请求复用正常建设工作流和原建设资产；候选与费用只读本人投影。</summary>
    internal sealed class FacilityBuildRequestUiCoordinator : InteractionBase, IInteractionRequestRenderer, IDisposable
    {
        private readonly Func<GameState> state;
        private readonly Func<GameStateView> visible;
        private readonly Func<int> player;
        private readonly Func<bool> canSubmit;
        private readonly GameplayDialogRegistry registry;
        private readonly RectTransform parent;
        private readonly IGameCommandPort port;
        private readonly Action refresh;
        private readonly Action<string> prompt;
        private readonly Dictionary<string, Flow> flows = new Dictionary<string, Flow>();
        private Flow active;
        private FacilityBuildDialogView page;
        private bool drawing, disposed;

        private sealed class Flow
        {
            internal string Game, Owner, InputId;
            internal int Player, Revision = -1;
            internal bool Dirty = true;
            internal BuildInteraction Workflow;
        }

        public FacilityBuildRequestUiCoordinator(Func<GameState> state, Func<GameStateView> visible, Func<int> player,
            GameplayDialogRegistry registry, RectTransform parent, IGameCommandPort port, Func<bool> canSubmit,
            Action refresh, Action<string> prompt)
        {
            this.state = state; this.visible = visible; this.player = player; this.registry = registry;
            this.parent = parent; this.port = port; this.canSubmit = canSubmit; this.refresh = refresh; this.prompt = prompt;
        }
        public override string Id => "facility.build.request.ui";
        public override InteractionPriority Priority => InteractionPriority.PendingResolution;
        int IInteractionRequestRenderer.Priority => 340;
        public override bool IsActive => active != null && TryScope(active, out var scope) && scope.Input != null;

        public bool CanRender(InteractionRequestProjection request) => !disposed && TryRead(request, out _);
        private bool TryRead(InteractionRequestProjection request, out EffectBuildScope scope)
        {
            scope = null;
            var view = visible();
            if (request == null || !request.VisibleToViewer || request.Status != "open" ||
                request.AnsweringPlayerId != player() || view == null || view.PreparedActionPayments == null || request.StateRevision != view.StateRevision) return false;
            string owner;
            if (request.InteractionTypeId == FacilitySelectionEffectExecutor.NormalBuildInteractionType) owner = request.OwnerEffectId;
            else if (request.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId)
            {
                var matches = view.PreparedActionPayments.Where(value => value != null && value.AuthorizationKind == "effect_build" &&
                    value.PaymentOwnerEffectId == request.OwnerEffectId && value.InteractionId == request.InteractionId).Take(2).ToArray();
                if (matches.Length != 1) return false;
                owner = matches[0].SelectionOwnerEffectId;
            }
            else return false;
            return EffectBuildScope.TryReadEffectBuildScope(state(), view, player(), owner, out scope) &&
                scope.Input?.InteractionId == request.InteractionId && scope.Input.OwnerEffectId == request.OwnerEffectId &&
                scope.Input.CandidateSetId == request.CandidateSetId && scope.Input.CandidateSetVersion == request.CandidateSetVersion;
        }

        private bool TryScope(Flow flow, out EffectBuildScope scope) =>
            EffectBuildScope.TryReadEffectBuildScope(state(), visible(), flow.Player, flow.Owner, out scope);

        public void Render(InteractionRequestProjection request)
        {
            if (disposed) return;
            if (!TryRead(request, out var scope)) { Clear(); return; }
            foreach (var obsolete in flows.Values.Where(value => value.Game != scope.VisibleState.GameId).ToArray()) Retire(obsolete);
            string key = scope.PlayerId + ":" + scope.OwnerEffectId;
            bool created = !flows.TryGetValue(key, out var flow);
            if (created)
            {
                flow = new Flow { Game = scope.VisibleState.GameId, Owner = scope.OwnerEffectId, Player = scope.PlayerId };
                flows.Add(key, flow);
                var context = new ScopeContext(state, flow.Player);
                flow.Workflow = new BuildInteraction(context,
                    new ScopePort(port, () => CanOperate(flow)), new View(this, flow), new InteractionFlowCoordinator(),
                    () => false, _ => Refresh(flow));
                flow.Workflow.GetVisibleState = visible;
            }
            bool changedFlow = active != flow;
            if (changedFlow) page?.Hide();
            active = flow;
            if (!created && !changedFlow && !flow.Dirty && flow.Revision == request.StateRevision && flow.InputId == request.InteractionId) return;
            flow.Dirty = false; flow.Revision = request.StateRevision; flow.InputId = request.InteractionId;
            if (created) flow.Workflow.BeginEffectBuild(scope);
            else flow.Workflow.RefreshEffectBuild();
        }

        private bool CanOperate(Flow flow) => !disposed && canSubmit() && state()?.GameId == flow.Game && player() == flow.Player &&
            TryScope(flow, out var scope) && scope.Input != null && !GameplayHudFrame.EffectInputSuspended;

        private void Draw(Flow flow, BuildFacilityDraftViewModel model)
        {
            if (drawing || active != flow || model == null || !TryScope(flow, out var scope) || scope.Input == null) return;
            drawing = true;
            try
            {
                if (page == null) page = registry.InstantiateFacilityBuild(parent);
                var inputId = scope.Input.InteractionId; var revision = scope.VisibleState.StateRevision;
                bool Current() => active == flow && CanOperate(flow) && !flow.Workflow.IsSubmissionInFlight &&
                    TryScope(flow, out var current) && current.Input?.InteractionId == inputId && current.VisibleState.StateRevision == revision;
                page.RefreshSupplyStatus(state(), flow.Player, scope.VisibleState.Decks.FacilityDeckCount);
                page.Show(model, state().Map.Facilities.ToArray(), flow.Player, registry.CardVisualCatalog,
                    registry.EffectDialogLayoutProfile, flow.Workflow.FacilityId, string.Empty, Current,
                    id => { if (Current()) flow.Workflow.SelectFacility(id); }, _ => { },
                    mode => { if (Current() && flow.Workflow.TryPreparePayment(mode)) flow.Workflow.Confirm(); },
                    () => { if (Current()) flow.Workflow.Confirm(); },
                    () => { if (Current()) flow.Workflow.CancelExplicitly(); });
            }
            finally { drawing = false; }
        }

        private void Refresh(Flow flow) { flow.Dirty = true; refresh(); }
        private void Hide(Flow flow) { if (active == flow) page?.Hide(); }
        private void Retire(Flow flow)
        {
            flow.Workflow.BeginShutdown(); Hide(flow);
            flows.Remove(flow.Player + ":" + flow.Owner);
            if (active == flow) active = null;
        }
        public override void NotifyInteractionCommandResolved(string gameId, string commandId, bool accepted, CommandErrorCode errorCode)
        {
            foreach (var flow in flows.Values.ToArray())
                flow.Workflow.NotifyInteractionCommandResolved(gameId, commandId, accepted, errorCode);
        }
        public override void NotifyCommandSettled(string commandId)
        {
            // 泛化settled只刷新；成功/拒绝必须由准确回执消费，不能提前清除在途准备。
            foreach (var flow in flows.Values) flow.Dirty = true;
        }
        public void Clear()
        {
            // 跨玩家必要响应/看卡只让出宿主，不取消准备或丢掉原真实点击。
            page?.Hide(); active = null;
            var view = visible();
            foreach (var flow in flows.Values.ToArray())
                if (view != null && (view.GameId != flow.Game || view.ViewerPlayerId == flow.Player &&
                    view.Effects.All(effect => effect.EffectId != flow.Owner ||
                        effect.Status == EffectNodeStatus.Completed || effect.Status == EffectNodeStatus.Failed || effect.Status == EffectNodeStatus.Faulted))) Retire(flow);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var flow in flows.Values.ToArray()) Retire(flow);
            if (page != null)
            {
                page.Hide();
                UnityEngine.Object.Destroy(page.gameObject);
                page = null;
            }
        }
        public override void Cancel() => Clear();
        public override InteractionResult OnEscape()
        { if (!IsActive) return InteractionResult.Passthrough; if (CanOperate(active)) active.Workflow.CancelExplicitly(); return InteractionResult.Consumed; }
        public override InteractionResult OnLocationClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnInfluenceSlotClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnMobileCityClicked() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionPresentation BuildPresentation() => IsActive ? InteractionPresentation.Busy : InteractionPresentation.Empty;

        private sealed class ScopeContext : IWritableGameplayContext
        {
            private readonly Func<GameState> state;
            public ScopeContext(Func<GameState> state, int player) { this.state = state; LocalPlayerId = player; }
            public GameState CurrentState => state();
            public int LocalPlayerId { get; }
            public bool ControlsCurrentPlayerLocally => false;
            public void SetLocalPlayerId(int playerId) { }
        }
        private sealed class ScopePort : IGameCommandPort
        {
            private readonly IGameCommandPort port; private readonly Func<bool> canSubmit;
            public ScopePort(IGameCommandPort port, Func<bool> canSubmit) { this.port = port; this.canSubmit = canSubmit; }
            public WorkflowSubmissionResult Submit(GameCommand command) => canSubmit() ? port.Submit(command) :
                new WorkflowSubmissionResult(CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "本次建设输入已变化，请等待同步。")), false);
        }
        private sealed class View : ITurnActionView
        {
            private readonly FacilityBuildRequestUiCoordinator owner; private readonly Flow flow;
            public View(FacilityBuildRequestUiCoordinator owner, Flow flow) { this.owner = owner; this.flow = flow; }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel model) => owner.Draw(flow, model);
            public void HideBuildFacilityDraft() => owner.Hide(flow);
            public void RefreshFromState() => owner.Refresh(flow);
            public void RefreshInformation() => owner.Refresh(flow);
            public void RefreshActionPanel() => owner.Refresh(flow);
            public void ShowPrompt(string message) => owner.prompt(message);
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights) { }
            public void ClearHighlights() { }
            public void ShowPendingChoice() => owner.Refresh(flow);
            public void CompleteMainActionPresentation(string name) { }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel model) { }
        }
    }
}
