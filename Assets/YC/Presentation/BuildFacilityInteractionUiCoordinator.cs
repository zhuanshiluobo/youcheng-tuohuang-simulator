using System;
using System.Collections.Generic;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>只协调本地建设草稿与建设面板，不持有或修改共享游戏状态。</summary>
    public sealed class BuildFacilityInteractionUiCoordinator : IDisposable
    {
        private readonly BuildInfoPanel panel;
        private readonly TurnActionPresenter presenter;

        internal BuildFacilityInteractionUiCoordinator(
            BuildInfoPanel panel,
            TurnActionPresenter presenter)
        {
            this.panel = panel ?? throw new ArgumentNullException(nameof(panel));
            this.presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            panel.FacilityDragStarted += OnFacilityDragStarted;
            panel.FacilityDropped += OnFacilityDropped;
        }

        public void Refresh(GameState state, int localPlayerId)
        {
            panel.Refresh(state, localPlayerId);
            Synchronize();
        }

        public void Synchronize()
        {
            var model = presenter.BuildBuildFacilityDraftViewModel();
            if (model == null)
            {
                var availability = presenter.BuildBuildFacilityAvailabilityViewModel();
                panel.SetBuildInteraction(
                    true,
                    availability.DraggableFacilityIds,
                    availability.LegalSlotIndexes,
                    string.Empty);
                panel.SetBuildAvailabilityMessage(availability.UnavailableMessage);
                panel.SetPendingBuildGhost(false, string.Empty, -1, null, null);
                return;
            }

            var draggableIds = new List<string>();
            if (model.Phase != BuildFacilityDraftPhase.Dragging)
            {
                for (var i = 0; i < model.Options.Count; i++)
                {
                    if (model.Options[i].CanBuild)
                    {
                        draggableIds.Add(model.Options[i].FacilityId);
                    }
                }
            }

            var showLegalSlots = model.Phase == BuildFacilityDraftPhase.Dragging ||
                                 model.Phase == BuildFacilityDraftPhase.Ghosted;
            panel.SetBuildInteraction(
                true,
                draggableIds,
                showLegalSlots ? model.LegalSlotIndexes : null,
                model.Facility == null ? string.Empty : model.Facility.FacilityId);
            panel.SetBuildAvailabilityMessage(string.Empty);
            panel.SetPendingBuildGhost(
                model.Phase == BuildFacilityDraftPhase.Ghosted,
                model.Facility == null ? string.Empty : model.Facility.FacilityId,
                model.CityBoardSlotIndex,
                OnGhostDragStarted,
                OnGhostDropped);
        }

        public void Dispose()
        {
            panel.FacilityDragStarted -= OnFacilityDragStarted;
            panel.FacilityDropped -= OnFacilityDropped;
        }

        private void OnFacilityDragStarted(string facilityId)
        {
            var model = presenter.BuildBuildFacilityDraftViewModel();
            if (model == null)
            {
                presenter.BeginBuildFacilityDrag(facilityId);
            }
            else
            {
                model.Dispatch(new BuildFacilityIntent.BeginDrag(facilityId));
            }

            Synchronize();
        }

        private void OnFacilityDropped(string facilityId, int slotIndex)
        {
            CompleteDrop(slotIndex);
        }

        private void OnGhostDragStarted()
        {
            var model = presenter.BuildBuildFacilityDraftViewModel();
            if (model != null)
            {
                model.Dispatch(new BuildFacilityIntent.BeginGhostDrag());
            }

            Synchronize();
        }

        private void OnGhostDropped(int slotIndex)
        {
            CompleteDrop(slotIndex);
        }

        private void CompleteDrop(int slotIndex)
        {
            var model = presenter.BuildBuildFacilityDraftViewModel();
            if (model == null)
            {
                if (slotIndex < 0)
                {
                    presenter.RejectBuildFacilityDrop();
                }
                else
                {
                    presenter.DropBuildFacility(slotIndex);
                }
            }
            else if (slotIndex < 0)
            {
                model.Dispatch(new BuildFacilityIntent.RejectDrop());
            }
            else
            {
                model.Dispatch(new BuildFacilityIntent.Drop(slotIndex));
            }

            Synchronize();
        }
    }
}
