using System;
using UnityEngine;
using UnityEngine.EventSystems;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Presentation.Maps;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>Routes map input by workflow mode and owns transient second-click confirmation state.</summary>
    internal sealed class MapInteractionRouter : InteractionBase
    {
        private readonly Func<MapViewPresenter> getMapView;
        private readonly Func<Camera> getCamera;
        private readonly IMapQueryService mapQuery;
        private readonly InteractionFlowCoordinator coordinator;
        private readonly TurnActionPresenter turn;
        private readonly ResourceCollectionPresenter collection;
        private readonly InfluenceActionPresenter influence;
        private readonly ExplorationEventPresenter exploration;
        private readonly IInteractionView view;
        private readonly Func<int> getCurrentPlayerId;
        private readonly Func<bool> isDebugClick;
        private readonly Action logDebugCoordinate;
        private readonly Action refreshActionPanel;
        private readonly Action refreshInfluence;
        private readonly MapInteractionConfirmationController confirmation = new MapInteractionConfirmationController();

        public MapInteractionRouter(
            Func<MapViewPresenter> getMapView,
            Func<Camera> getCamera,
            IMapQueryService mapQuery,
            InteractionFlowCoordinator coordinator,
            TurnActionPresenter turn,
            ResourceCollectionPresenter collection,
            InfluenceActionPresenter influence,
            ExplorationEventPresenter exploration,
            IInteractionView view,
            Func<int> getCurrentPlayerId,
            Func<bool> isDebugClick,
            Action logDebugCoordinate,
            Action refreshActionPanel,
            Action refreshInfluence)
        {
            this.getMapView = getMapView;
            this.getCamera = getCamera;
            this.mapQuery = mapQuery;
            this.coordinator = coordinator;
            this.turn = turn;
            this.collection = collection;
            this.influence = influence;
            this.exploration = exploration;
            this.view = view;
            this.getCurrentPlayerId = getCurrentPlayerId;
            this.isDebugClick = isDebugClick;
            this.logDebugCoordinate = logDebugCoordinate;
            this.refreshActionPanel = refreshActionPanel;
            this.refreshInfluence = refreshInfluence;
        }

        public bool HasPendingConfirmation => confirmation.HasPending || influence.HasPendingConfirmation || exploration.HasPendingInfluenceConfirmation;

        public override string Id => "default.map-route";

        public override InteractionPriority Priority => InteractionPriority.DefaultRoute;

        public override bool IsActive => true;

        public override InteractionResult OnLocationClicked(string locationId)
        {
            RouteLocationClick(locationId);
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            RouteInfluenceSlotClick(slotId);
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            RouteMobileCityClick();
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnEscape()
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionPresentation BuildPresentation()
        {
            return InteractionPresentation.Empty;
        }

        public override void Cancel()
        {
            ClearConfirmation(false);
        }

        private void RouteLocationClick(string locationId)
        {
            if (HandleDebugClick()) return;
            if (turn.IsAwaitingInitialPlacement)
            {
                if (!turn.IsLocalPlayersTurn())
                {
                    view.ShowPrompt("等待玩家 " + getCurrentPlayerId() + " 完成入场。");
                    return;
                }
                var initialMapView = getMapView();
                if (initialMapView != null && initialMapView.ContainsHighlightedLocation(locationId))
                {
                    Request("InitialPlacement", locationId, GetCurrentPrompt(), () => turn.PlaceInitialCity(locationId));
                }
                return;
            }

            var mapView = getMapView();
            if (mapView == null) { CancelConfirmation(true); return; }
            if (exploration.IsSelectingInfluenceTarget)
            {
                exploration.SelectInfluenceAtLocation(locationId, Time.frameCount);
                return;
            }

            if (coordinator.IsActive(collection))
            {
                if (collection.CanToggleLocation(locationId)) collection.SelectLocation(locationId);
                return;
            }

            if (!mapView.ContainsHighlightedLocation(locationId)) { CancelConfirmation(true); return; }

            if (coordinator.IsActive(exploration) && exploration.IsSelectingExploreTarget)
            {
                RequestExplore(locationId);
                return;
            }

            if (coordinator.IsActive(influence))
            {
                var hadPendingConfirmation = influence.HasPendingConfirmation;
                influence.SelectLocation(locationId);
                if (hadPendingConfirmation && !influence.HasPendingConfirmation)
                {
                    mapView.PlayLocationConfirmation(locationId);
                }
            }
        }

        private void RouteInfluenceSlotClick(string slotId)
        {
            if (HandleDebugClick()) return;
            var mapView = getMapView();
            if (mapView == null) { CancelConfirmation(true); return; }
            if (exploration.IsSelectingInfluenceTarget)
            {
                if (!mapView.ContainsHighlightedInfluenceSlot(slotId))
                {
                    view.ShowPrompt("请选择高亮的影响力槽位。");
                    return;
                }
                exploration.SelectInfluenceSlot(slotId, Time.frameCount);
                return;
            }
            if (!mapView.ContainsHighlightedInfluenceSlot(slotId)) { CancelConfirmation(true); return; }

            if (coordinator.IsActive(collection)) return;

            if (coordinator.IsActive(influence))
            {
                var hadPendingConfirmation = influence.HasPendingConfirmation;
                influence.SelectSlot(slotId);
                if (hadPendingConfirmation && !influence.HasPendingConfirmation)
                {
                    mapView.PlayInfluenceSlotConfirmation(slotId);
                }
            }
        }

        private void RouteMobileCityClick()
        {
            if (HandleDebugClick() || turn.IsAwaitingInitialPlacement) return;
            if (!turn.IsLocalPlayersTurn())
            {
                view.ShowPrompt("等待玩家 " + getCurrentPlayerId() + " 行动。");
                return;
            }
            view.ShowPrompt("请使用右下角行动面板选择主要行动。");
        }

        public void UpdateCancellation()
        {
            if (!HasPendingConfirmation || !Input.GetMouseButtonUp(0) || MapDisplayController.SuppressMapClick) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
            if (IsPointerOverInteractionTarget()) return;
            CancelConfirmation(true);
        }

        public void ClearConfirmation(bool restorePresentation)
        {
            if (exploration.HasPendingInfluenceConfirmation) exploration.CancelPendingInfluenceConfirmation(restorePresentation);
            var hadMapConfirmation = confirmation.HasPending;
            var hadInfluenceConfirmation = influence.HasPendingConfirmation;
            if (hadMapConfirmation) confirmation.Clear();
            if (hadInfluenceConfirmation) influence.CancelPendingConfirmation(restorePresentation);
            if (restorePresentation && hadMapConfirmation)
            {
                RestorePresentation();
                refreshActionPanel();
                view.ShowPrompt(GetCurrentPrompt());
            }
        }

        private void RequestExplore(string locationId)
        {
            int cost;
            string error;
            if (!exploration.TryEstimateGoldVoucherCost(locationId, out cost, out error))
            {
                view.ShowPrompt(error);
                return;
            }
            Request("Explore", locationId, "探索：预计花费" + cost + "金券", () => exploration.SelectTarget(locationId));
        }

        private void Request(string actionKey, string targetId, string prompt, Action action)
        {
            Action callback;
            if (confirmation.Request(actionKey, targetId, targetId, string.Empty, action, out callback, Time.frameCount))
            {
                view.ClearHighlights();
                getMapView()?.PlayLocationConfirmation(targetId);
                callback?.Invoke();
                return;
            }
            HighlightConfirmation();
            view.ShowPrompt(prompt);
        }

        private void HighlightConfirmation()
        {
            RestorePresentation();
            var mapView = getMapView();
            if (mapView == null) return;
            if (!string.IsNullOrEmpty(confirmation.LocationId))
                mapView.SetPendingButton(WorkflowHighlightTargetKind.Location, confirmation.LocationId);
            if (!string.IsNullOrEmpty(confirmation.SlotId)) mapView.SetPendingButton(WorkflowHighlightTargetKind.InfluenceSlot, confirmation.SlotId);
            refreshInfluence();
        }

        private void RestorePresentation()
        {
            view.ClearHighlights();
            if (turn.IsAwaitingInitialPlacement)
            {
                view.SetHighlights(turn.BuildInitialPlacementHighlights());
                return;
            }
            if (exploration.IsSelectingInfluenceTarget)
            {
                exploration.RestorePresentation();
                return;
            }

            if (coordinator.IsActive(turn) && turn.IsSelectingMoveTarget)
            {
                turn.RestoreMovePresentation();
                return;
            }

            if (coordinator.IsActive(exploration))
            {
                exploration.RestorePresentation();
                return;
            }

            if (coordinator.IsActive(influence))
            {
                influence.RestorePresentation();
            }
        }

        private string GetCurrentPrompt()
        {
            if (exploration.IsSelectingInfluenceTarget)
            {
                return exploration.CurrentPrompt;
            }

            if (coordinator.IsActive(turn) && turn.IsSelectingMoveTarget)
            {
                return "城市移动：选择一个高亮资源点。";
            }

            if (coordinator.IsActive(exploration))
            {
                return exploration.CurrentPrompt;
            }

            if (coordinator.IsActive(influence))
            {
                return influence.CurrentPrompt;
            }

            if (coordinator.IsActive(collection))
            {
                return collection.BuildStatus();
            }

            return "请从右下角行动面板选择主要行动。";
        }

        private void CancelConfirmation(bool restorePresentation) => ClearConfirmation(restorePresentation);

        private bool HandleDebugClick()
        {
            if (!isDebugClick()) return false;
            logDebugCoordinate();
            return true;
        }

        private bool IsPointerOverInteractionTarget() =>
            IsPointerOverInteractionTarget(Input.mousePosition);

        private bool IsPointerOverInteractionTarget(Vector2 screenPosition)
        {
            var camera = getCamera();
            if (camera == null) return false;
            var hits = Physics2D.GetRayIntersectionAll(camera.ScreenPointToRay(screenPosition));
            for (var i = 0; i < hits.Length; i++)
            {
                var hit = hits[i].collider;
                if (hit != null && (hit.GetComponent<MapButtonView>() != null || hit.GetComponent<MapHotspot>() != null ||
                                    hit.GetComponent<InfluenceSlotClickTarget>() != null ||
                                    hit.GetComponent<MobileCityClickTarget>() != null)) return true;
            }
            return false;
        }
    }
}
