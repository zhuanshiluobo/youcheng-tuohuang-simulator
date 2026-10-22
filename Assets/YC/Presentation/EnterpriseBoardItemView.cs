using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    public sealed class EnterpriseBoardItemView : MonoBehaviour
    {
        [SerializeField] private RawImage boardImage;
        [SerializeField] private AspectRatioFitter boardAspect;
        [SerializeField] private RectTransform targetRoot;
        [SerializeField] private EnterpriseTargetView targetTemplate;
        [SerializeField] private Text nameText;
        [SerializeField] private Text[] playerLevels;
        [SerializeField] private InfluenceMarker2DView[] playerLevelMarkers;
        [SerializeField] private bool interactionEnabled = true;
        [SerializeField] private Button detailsButton;
        [SerializeField] private Text[] rewardLabels;
        [SerializeField] private int[] playerColorOrder;
        [SerializeField] private CardPickerCardButton boardPreviewButton;
        [SerializeField] private EnterpriseDepartmentOverlayView departmentOverlay;
        private readonly Dictionary<string, EnterpriseTargetView> targets = new Dictionary<string, EnterpriseTargetView>();
        public RawImage BoardImage => boardImage;
        public bool HasDepartmentOverlay => departmentOverlay != null && departmentOverlay.IsConfigured;

        public void Bind(EnterpriseBoardProjection board, EnterpriseBoardCatalog catalog, EnterpriseSelectionDraft draft,
            Action<string> choose, Action<string> details) =>
            BindWithActiveDepartment(board, catalog, draft, choose, details, false, null);

        public void BindWithActiveDepartment(EnterpriseBoardProjection board, EnterpriseBoardCatalog catalog, EnterpriseSelectionDraft draft,
            Action<string> choose, Action<string> details, bool showActiveDepartment, Action<string> inspectDepartment)
        {
            var artwork = catalog.Find(board.VisualKey);
            EnterpriseBoardCatalog.Bind(boardImage, boardAspect, artwork);
            for (int i = 0; rewardLabels != null && i < rewardLabels.Length; i++)
            {
                bool visible = artwork?.rewardAnchors != null && i < artwork.rewardAnchors.Length;
                rewardLabels[i].gameObject.SetActive(visible);
                if (!visible) continue;
                rewardLabels[i].rectTransform.anchorMin = rewardLabels[i].rectTransform.anchorMax = artwork.rewardAnchors[i];
            }
            if (nameText != null) nameText.text = board.Label ?? string.Empty;
            for (int i = 0; playerLevels != null && i < playerLevels.Length; i++)
            {
                int index = playerColorOrder != null && i < playerColorOrder.Length && board.PlayerColors != null
                    ? Array.IndexOf(board.PlayerColors, playerColorOrder[i]) : i;
                var show = board.PlayerLevels != null && index >= 0 && index < board.PlayerLevels.Length && board.PlayerLevels[index] >= 0;
                playerLevels[i].transform.parent.gameObject.SetActive(show);
                if (show) playerLevels[i].text = board.PlayerLevels[index].ToString();
            }
            for (int i = 0; playerLevelMarkers != null && i < playerLevelMarkers.Length; i++)
            {
                var marker = playerLevelMarkers[i];
                if (marker == null) continue;
                int color = playerColorOrder != null && i < playerColorOrder.Length ? playerColorOrder[i] : i;
                int index = board.PlayerColors == null ? -1 : Array.IndexOf(board.PlayerColors, color);
                bool show = board.PlayerLevels != null && index >= 0 && index < board.PlayerLevels.Length && board.PlayerLevels[index] >= 0;
                marker.gameObject.SetActive(show);
                if (show) marker.RenderPlayer((PlayerColor)color, board.PlayerLevels[index]);
            }
            if (detailsButton != null)
            {
                detailsButton.onClick.RemoveAllListeners();
                detailsButton.interactable = interactionEnabled && details != null && !draft.IsPending;
                if (interactionEnabled && details != null) detailsButton.onClick.AddListener(() => details(board.Id));
            }
            var projection = draft.Projection;
            Func<bool> canPreview = () => interactionEnabled && gameObject.activeInHierarchy &&
                ReferenceEquals(draft.Projection, projection) && !draft.IsPending;
            if (boardPreviewButton != null)
            {
                boardPreviewButton.onClick.RemoveAllListeners();
                boardPreviewButton.interactable = interactionEnabled && !draft.IsPending;
                boardPreviewButton.ConfigurePreview(interactionEnabled && details != null ? (Action)(() => details(board.Id)) : null, canPreview);
            }
            if (departmentOverlay != null)
            {
                var department = showActiveDepartment ? board.ActiveDepartment : null;
                var interactiveDepartment = interactionEnabled && draft.Projection.ReadOnly;
                departmentOverlay.Bind(department, catalog,
                    !interactiveDepartment || department == null || inspectDepartment == null ? (Action)null : () => inspectDepartment(department.ItemId),
                    !interactiveDepartment || department == null || details == null ? (Action)null : () => details(department.ItemId), canPreview);
            }
            var valid = new HashSet<string>();
            foreach (var target in draft.Projection.Targets)
            {
                if (!interactionEnabled || target.BoardId != board.Id) continue;
                valid.Add(target.Id);
                if (!targets.TryGetValue(target.Id, out var item))
                {
                    item = Instantiate(targetTemplate, targetRoot, false);
                    item.gameObject.SetActive(true);
                    targets.Add(target.Id, item);
                }
                var area = draft.Projection.Mode == EnterpriseSelectionMode.Effect
                    ? new Rect(target.X, target.Y, target.Width, target.Height) : new Rect(0, 0, 1, 1);
                var id = target.Id;
                item.Bind(area, draft.IsAvailable(target), draft.IsSelected(id), draft.IsPending, () => choose(id),
                    draft.Projection.ReadOnly, targetRoot, draft.Projection.Mode != EnterpriseSelectionMode.Effect,
                    () => details(board.Id), canPreview);
            }
            foreach (var id in new List<string>(targets.Keys))
                if (!valid.Contains(id)) { Retire(targets[id].gameObject); targets.Remove(id); }
        }

        internal static void Retire(GameObject value)
        {
            value.SetActive(false);
            if (UnityEngine.Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
    }
}
