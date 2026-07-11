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
    internal sealed class MapInteractionRouter
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

        public bool HasPendingConfirmation => confirmation.HasPending || influence.HasPendingConfirmation;

        public void OnLocationClicked(string locationId)
        {
            if (HandleDebugClick()) return;
            if (turn.IsAwaitingInitialPlacement)
            {
                if (!turn.IsLocalPlayersTurn())
                {
                    view.ShowPrompt("等待玩家 " + getCurrentPlayerId() + " 完成入场。");
                    return;
                }
                turn.PlaceInitialCity(locationId);
                return;
            }

            var mapView = getMapView();
            if (mapView == null) { CancelConfirmation(true); return; }
            if (exploration.IsSelectingInfluenceTarget)
            {
                exploration.SelectInfluenceAtLocation(locationId, Time.frameCount);
                return;
            }
            if (!mapView.ContainsHighlightedLocation(locationId)) { CancelConfirmation(true); return; }

            switch (coordinator.CurrentMode)
            {
                case InteractionMode.ResolvingResourceCollection: collection.SelectLocation(locationId); break;
                case InteractionMode.ResolvingMoveTarget: RequestMove(locationId); break;
                case InteractionMode.ResolvingExploreTarget: RequestExplore(locationId); break;
                case InteractionMode.ResolvingDeployTarget:
                case InteractionMode.ResolvingDispatchSource:
                case InteractionMode.ResolvingDispatchTarget: influence.SelectLocation(locationId); break;
            }
        }

        public void OnInfluenceSlotClicked(string slotId)
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

            if (coordinator.CurrentMode == InteractionMode.ResolvingResourceCollection)
            {
                InfluenceSlotReference slot;
                string reason;
                if (!InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason) ||
                    slot.Kind != InfluenceSlotKind.Route)
                {
                    view.ShowPrompt("请选择高亮航道支付路费。");
                    return;
                }
                collection.SelectRoutePayment(slot.RouteId);
                return;
            }
            if (coordinator.CurrentMode == InteractionMode.ResolvingDeployTarget ||
                coordinator.CurrentMode == InteractionMode.ResolvingDispatchSource ||
                coordinator.CurrentMode == InteractionMode.ResolvingDispatchTarget)
            {
                influence.SelectSlot(slotId);
            }
        }

        public void OnMobileCityClicked()
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
            if (!HasPendingConfirmation || !Input.GetMouseButtonDown(0)) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
            if (IsPointerOverInteractionTarget()) return;
            CancelConfirmation(true);
        }

        public void ClearConfirmation(bool restorePresentation)
        {
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

        private void RequestMove(string locationId)
        {
            var cost = new TravelCostService(mapQuery).GetCityMoveBaseCost().OriginiumShard;
            Request("MoveCity", locationId, "城市移动：预计花费" + cost + "源石碎片", () => turn.MoveCity(locationId));
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
            if (confirmation.Request(actionKey, targetId, targetId, string.Empty, action, out callback))
            {
                view.ClearHighlights();
                callback?.Invoke();
                return;
            }
            HighlightConfirmation();
            view.ShowPrompt(prompt);
        }

        private void HighlightConfirmation()
        {
            view.ClearHighlights();
            var mapView = getMapView();
            if (mapView == null) return;
            if (!string.IsNullOrEmpty(confirmation.LocationId))
                mapView.SetHighlighted(confirmation.LocationId, new Color(1f, 0.82f, 0.2f, 0.95f));
            if (!string.IsNullOrEmpty(confirmation.SlotId)) mapView.HighlightInfluenceSlot(confirmation.SlotId);
            refreshInfluence();
        }

        private void RestorePresentation()
        {
            view.ClearHighlights();
            switch (coordinator.CurrentMode)
            {
                case InteractionMode.ResolvingMoveTarget: turn.RestoreMovePresentation(); break;
                case InteractionMode.ResolvingExploreTarget:
                case InteractionMode.ResolvingEventInfluenceTarget: exploration.RestorePresentation(); break;
                case InteractionMode.ResolvingDeployTarget:
                case InteractionMode.ResolvingDispatchSource:
                case InteractionMode.ResolvingDispatchTarget: influence.RestorePresentation(); break;
            }
        }

        private string GetCurrentPrompt()
        {
            switch (coordinator.CurrentMode)
            {
                case InteractionMode.ResolvingMoveTarget: return "城市移动：选择一个高亮资源点。";
                case InteractionMode.ResolvingExploreTarget:
                case InteractionMode.ResolvingEventInfluenceTarget: return exploration.GetCurrentPrompt();
                case InteractionMode.ResolvingDeployTarget: return "选择一个影响力空格放置影响力";
                case InteractionMode.ResolvingDispatchSource: return "调度：先选择一个自己的影响力。";
                case InteractionMode.ResolvingDispatchTarget: return "请选择调度目标槽位。";
                default: return "请从右下角行动面板选择主要行动。";
            }
        }

        private void CancelConfirmation(bool restorePresentation) => ClearConfirmation(restorePresentation);

        private bool HandleDebugClick()
        {
            if (!isDebugClick()) return false;
            logDebugCoordinate();
            return true;
        }

        private bool IsPointerOverInteractionTarget()
        {
            var camera = getCamera();
            if (camera == null) return false;
            var screen = Input.mousePosition;
            var world = camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -camera.transform.position.z));
            var hits = Physics2D.OverlapPointAll(world);
            for (var i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit != null && (hit.GetComponent<MapHotspot>() != null ||
                                    hit.GetComponent<InfluenceSlotClickTarget>() != null ||
                                    hit.GetComponent<MobileCityClickTarget>() != null)) return true;
            }
            return false;
        }
    }
}
