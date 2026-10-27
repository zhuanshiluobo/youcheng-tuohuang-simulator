using System;
using System.Globalization;
using System.Linq;
using UnityEngine;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed class FacilityPanelSelectionUiCoordinator : InteractionBase, IInteractionRequestRenderer
    {
        private readonly Func<GameStateView> visible;
        private readonly Func<int> player;
        private readonly GameplayDialogRegistry registry;
        private readonly RectTransform parent;
        private readonly Action<GameCommand> submit;
        private readonly Func<bool> canSubmit;
        private FacilityPanelSelectionModel draft;
        private FacilityBuildDialogView page;
        private int selected = -1;
        private string inFlight = "";
        internal FacilityPanelSelectionUiCoordinator(Func<GameStateView> visible, Func<int> player,
            GameplayDialogRegistry registry, RectTransform parent, Action<GameCommand> submit, Func<bool> canSubmit)
        { this.visible = visible; this.player = player; this.registry = registry; this.parent = parent; this.submit = submit; this.canSubmit = canSubmit; }
        public override string Id => "facility.panel.selection.ui";
        public override InteractionPriority Priority => InteractionPriority.PendingResolution;
        int IInteractionRequestRenderer.Priority => 345;
        public bool CanRender(InteractionRequestProjection request) => FacilityPanelSelectionModel.TryCreate(request, visible(), player(), out _);
        private bool Current(FacilityPanelSelectionModel model)
        {
            if (model == null || !canSubmit() || inFlight.Length > 0) return false;
            var view = visible(); var request = VisibleInteractionRequestSource.Read(null, view, player()).FirstOrDefault(r => r.InteractionId == model.Request.InteractionId);
            return request != null && FacilityPanelSelectionModel.TryCreate(InteractionRequestProjector.ProjectForPlayer(request, player()), view,
                player(), out var fresh) && fresh.Identity == model.Identity;
        }
        public override bool IsActive => Current(draft);
        public void Render(InteractionRequestProjection request)
        {
            if (!FacilityPanelSelectionModel.TryCreate(request, visible(), player(), out var model)) { Clear(); return; }
            if (inFlight.Length > 0) return;
            bool same = draft?.Identity == model.Identity;
            if (!same) selected = -1;
            draft = model;
            if (!same || page == null || !page.gameObject.activeSelf) Draw();
        }
        private void Draw()
        {
            var model = draft; if (!Current(model)) return;
            if (page == null) page = registry.InstantiateFacilityBuild(parent);
            page.ShowPanelSelection(model, registry.CardVisualCatalog, selected, () => Current(model),
                slot => { if (Current(model) && model.CanSelect(slot)) { selected = slot; Draw(); } },
                () => Answer(model, false), () => Answer(model, true));
        }
        private void Answer(FacilityPanelSelectionModel model, bool decline)
        {
            if (GameplayHudFrame.EffectInputSuspended || !Current(model) || (decline ? !model.Request.AllowDecline : !model.CanSelect(selected))) return;
            var command = EffectInteractionCommands.Answer(model.Request, player(), decline ? Array.Empty<string>() :
                new[] { selected.ToString(CultureInfo.InvariantCulture) }, decline);
            inFlight = command.CommandId; page.Hide();
            try { submit(command); } catch { inFlight = ""; Draw(); throw; }
        }
        public override void NotifyCommandSettled(string commandId) { if (inFlight != commandId) return; inFlight = ""; Clear(); }
        public void Clear() { page?.Hide(); draft = null; selected = -1; }
        public override void Cancel() => Clear();
        public override InteractionResult OnEscape() { if (!IsActive) return InteractionResult.Passthrough; if (draft.Request.AllowDecline) Answer(draft, true); return InteractionResult.Consumed; }
        public override InteractionResult OnLocationClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnInfluenceSlotClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnMobileCityClicked() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionPresentation BuildPresentation() => IsActive ? InteractionPresentation.Busy : InteractionPresentation.Empty;
    }
}
