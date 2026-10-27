using System;
using System.Linq;
using UnityEngine;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>通用供应选择请求复用建设页，确认只提交原正式答案。</summary>
    internal sealed class FacilitySupplySelectionUiCoordinator : InteractionBase, IInteractionRequestRenderer, IDisposable
    {
        private readonly Func<GameStateView> visible;
        private readonly Func<int> player;
        private readonly GameplayDialogRegistry registry;
        private readonly RectTransform parent;
        private readonly Action<GameCommand> submit;
        private readonly Func<bool> canSubmit;
        private FacilityBuildDialogView page;
        private FacilitySupplySelectionModel draft;
        private string selected = string.Empty, inFlight = string.Empty;
        public GameplayExpansionUi ExpansionUi { private get; set; }
        public FacilitySupplySelectionUiCoordinator(Func<GameStateView> visible, Func<int> player,
            GameplayDialogRegistry registry, RectTransform parent, Action<GameCommand> submit, Func<bool> canSubmit)
        { this.visible = visible; this.player = player; this.registry = registry; this.parent = parent; this.submit = submit; this.canSubmit = canSubmit; }
        public override string Id => "facility.supply.selection.ui";
        public override InteractionPriority Priority => InteractionPriority.PendingResolution;
        int IInteractionRequestRenderer.Priority => 345;
        public override bool IsActive => draft != null && Current(draft);
        public bool CanRender(InteractionRequestProjection request) => TryModel(request, out _);
        private bool TryModel(InteractionRequestProjection request, out FacilitySupplySelectionModel model)
        {
            var view = visible(); model = null;
            return view != null && view.ViewerPlayerId == player() &&
                FacilitySupplySelectionModel.TryCreate(request, view.ItemZones, player(), view.StateRevision, out model);
        }
        private bool Current(FacilitySupplySelectionModel model)
        {
            var view = visible();
            if (model == null || view == null || !canSubmit() || !string.IsNullOrEmpty(inFlight)) return false;
            var request = VisibleInteractionRequestSource.Read(null, view, player()).FirstOrDefault(r => r.InteractionId == model.Request.InteractionId);
            return request != null && TryModel(InteractionRequestProjector.ProjectForPlayer(request, player()), out var current) && current.Identity == model.Identity;
        }
        public void Render(InteractionRequestProjection request)
        {
            if (!TryModel(request, out var model)) { Clear(); return; }
            if (!string.IsNullOrEmpty(inFlight)) return;
            bool changed = draft?.Identity != model.Identity;
            if (changed) selected = string.Empty;
            draft = model;
            if (!changed && page != null && page.gameObject.activeSelf) return;
            Draw();
        }
        private void Draw()
        {
            var model = draft;
            if (!Current(model)) return;
            if (page == null) page = registry.InstantiateFacilityBuild(parent);
            var document = ExpansionUi == null ? null : EffectDialogTextDocuments.ResolvePrompt(model.Request, ExpansionUi.ResolveGameplayText);
            page.ShowSupplySelection(model, registry.CardVisualCatalog, selected, () => Current(model),
                id => { if (Current(model) && model.CanSelect(id)) { selected = id; Draw(); } },
                () => Answer(model, false), () => Answer(model, true),
                document == null ? (Action<GameplayTextView>)null : text => ExpansionUi.RenderEnterpriseDetails(text, document, false));
        }
        private void Answer(FacilitySupplySelectionModel model, bool decline)
        {
            if (GameplayHudFrame.EffectInputSuspended || !Current(model) ||
                (decline ? !model.Request.AllowDecline : !model.CanSelect(selected))) return;
            var command = EffectInteractionCommands.Answer(model.Request, player(), decline ? Array.Empty<string>() : new[] { selected }, decline);
            inFlight = command.CommandId; page.Hide();
            try { submit(command); } catch { inFlight = string.Empty; Draw(); throw; }
        }
        public override void NotifyCommandSettled(string commandId)
        { if (inFlight != commandId) return; inFlight = string.Empty; draft = null; selected = string.Empty; }
        public void Clear()
        {
            if (page != null) page.Hide();
            else page = null;
            draft = null; selected = string.Empty;
        }
        public override void Cancel() => Clear();
        public override InteractionResult OnEscape()
        { if (!IsActive) return InteractionResult.Passthrough; if (draft.Request.AllowDecline) Answer(draft, true); return InteractionResult.Consumed; }
        public override InteractionResult OnLocationClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnInfluenceSlotClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnMobileCityClicked() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionPresentation BuildPresentation() => IsActive ? InteractionPresentation.Busy : InteractionPresentation.Empty;
        public void Dispose()
        {
            Clear();
            var released = page; page = null;
            if (released != null) UnityEngine.Object.Destroy(released.gameObject);
        }
    }
}
