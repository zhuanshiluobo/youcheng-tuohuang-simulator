using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
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
        [SerializeField] private Button detailsButton;
        [SerializeField] private Text[] rewardLabels;
        private readonly Dictionary<string, EnterpriseTargetView> targets = new Dictionary<string, EnterpriseTargetView>();
        public RawImage BoardImage => boardImage;

        public void Bind(EnterpriseBoardProjection board, EnterpriseBoardCatalog catalog, EnterpriseSelectionDraft draft,
            Action<string> choose, Action<string> details)
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
            nameText.text = board.Label ?? string.Empty;
            for (int i = 0; i < playerLevels.Length; i++)
            {
                var show = board.PlayerLevels != null && i < board.PlayerLevels.Length;
                playerLevels[i].transform.parent.gameObject.SetActive(show);
                if (show) playerLevels[i].text = board.PlayerLevels[i].ToString();
            }
            detailsButton.onClick.RemoveAllListeners();
            detailsButton.onClick.AddListener(() => details(board.Id));
            var valid = new HashSet<string>();
            foreach (var target in draft.Projection.Targets)
            {
                if (target.BoardId != board.Id) continue;
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
                item.Bind(area, draft.IsAvailable(target), draft.SelectedId == id, draft.IsPending, () => choose(id));
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
