using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>显示持久化的已支付建设请求，回答只提交城市槽位。</summary>
    internal sealed class FacilityBuildPlacementUiCoordinator : InteractionBase, IInteractionRequestRenderer
    {
        private readonly Func<GameState> state;
        private readonly Func<int> player;
        private readonly GameplayDialogRegistry registry;
        private readonly RectTransform parent;
        private readonly Action<GameCommand> submit;
        private FacilityBuildDialogView page;
        private string rendered = string.Empty;
        private int revision = -1;
        private int selectedSlot = -1;
        private string inFlight = string.Empty;
        public Func<IReadOnlyList<InteractionRequest>> GetVisibleRequests { private get; set; }

        public FacilityBuildPlacementUiCoordinator(Func<GameState> state, Func<int> player,
            GameplayDialogRegistry registry, RectTransform parent, Action<GameCommand> submit)
        {
            this.state = state; this.player = player; this.registry = registry;
            this.parent = parent; this.submit = submit;
        }

        public override string Id => "facility.build.placement.ui";
        public override InteractionPriority Priority => InteractionPriority.PendingResolution;
        int IInteractionRequestRenderer.Priority => 330;
        public override bool IsActive => Current() != null;
        public bool CanRender(InteractionRequestProjection request) => request != null && request.VisibleToViewer &&
            request.AnsweringPlayerId == player() && request.Status == "open" &&
            request.InteractionTypeId == FacilityBuildEffectExecutor.PlacementInteractionType;

        private InteractionRequestProjection Current()
        {
            var requests = GetVisibleRequests == null ? VisibleInteractionRequestSource.Read(state(), null, player()) : GetVisibleRequests();
            if (requests == null) return null;
            foreach (var request in requests)
            {
                var projection = InteractionRequestProjector.ProjectForPlayer(request, player());
                if (CanRender(projection)) return projection;
            }
            return null;
        }

        private bool IsCurrent(InteractionRequestProjection request)
        {
            var current = Current();
            return string.IsNullOrEmpty(inFlight) && current != null && request != null &&
                current.InteractionId == request.InteractionId && current.StateRevision == request.StateRevision &&
                current.CandidateSetId == request.CandidateSetId && current.CandidateSetVersion == request.CandidateSetVersion;
        }

        public void Render(InteractionRequestProjection request)
        {
            if (!CanRender(request)) { Clear(); return; }
            if (rendered == request.InteractionId && revision >= request.StateRevision && page != null) return;
            if (!string.IsNullOrEmpty(inFlight)) return;
            if (rendered != request.InteractionId) selectedSlot = -1;
            rendered = request.InteractionId; revision = request.StateRevision;
            Show(request);
        }

        private void Show(InteractionRequestProjection request)
        {
            if (!IsCurrent(request) || state() == null) return;
            if (page == null) page = registry.InstantiateFacilityBuild(parent);
            string Parameter(string name)
            {
                if (request.PromptParameters?.Properties != null)
                    foreach (var item in request.PromptParameters.Properties)
                        if (item.Name == name) return item.Value?.StringValue ?? string.Empty;
                return string.Empty;
            }
            var facilityId = Parameter("facilityId");
            var legal = new List<int>();
            foreach (var candidate in request.CandidateIds)
                if (int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)) legal.Add(slot);
            if (!legal.Contains(selectedSlot)) selectedSlot = -1;
            var model = new BuildFacilityDraftViewModel(BuildFacilityDraftPhase.Placing,
                new BuildFacilityOptionQueryService().Query(state(), player()), null,
                FacilityCardDatabase.Get(facilityId), selectedSlot, Parameter("paymentMode"),
                string.Empty, legal, _ => { });
            page.RefreshSupplyStatus(state(), player());
            page.Show(model, state().Map.Facilities, player(), registry.CardVisualCatalog,
                registry.EffectDialogLayoutProfile, facilityId, string.Empty, () => IsCurrent(request),
                _ => { }, slot =>
                {
                    if (!IsCurrent(request) || !legal.Contains(slot)) return;
                    selectedSlot = slot; Show(request);
                }, _ => { }, () => Answer(request), () => { });
        }

        private void Answer(InteractionRequestProjection request)
        {
            if (GameplayHudFrame.EffectInputSuspended || !IsCurrent(request) || selectedSlot < 0) return;
            var candidate = selectedSlot.ToString(CultureInfo.InvariantCulture);
            if (!request.CandidateIds.Contains(candidate)) return;
            var command = EffectInteractionCommands.Answer(request, player(), new[] { candidate }, false);
            inFlight = command.CommandId;
            page?.Hide();
            try { submit(command); }
            catch { NotifyCommandSettled(command.CommandId); Show(Current()); throw; }
        }

        public override void NotifyCommandSettled(string commandId)
        {
            if (inFlight != commandId) return;
            inFlight = string.Empty; revision = -1;
            var request = Current();
            if (request != null) Render(request); else Clear();
        }
        public override InteractionResult OnLocationClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnInfluenceSlotClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnMobileCityClicked() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnEscape() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionPresentation BuildPresentation() => InteractionPresentation.Empty;
        public override void Cancel() => Clear();
        public void Clear()
        {
            if (page != null) page.Hide();
            rendered = inFlight = string.Empty; revision = selectedSlot = -1;
        }
    }
}
