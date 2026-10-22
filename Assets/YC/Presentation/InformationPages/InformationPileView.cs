using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class InformationPileView : MonoBehaviour
    {
        [SerializeField] private CardPileView pile;
        [SerializeField] private GameBoxEventPileView eventPile;
        [SerializeField] private Button inspectButton;
        [SerializeField] private Text countText;
        [SerializeField] private GameObject emptyLabel;
        [SerializeField] private GameObject awaitingImageLabel;
        [SerializeField] private GameObject inspectHint;
        private readonly List<string> ids = new List<string>();
        private readonly Dictionary<string, Sprite> imagesById = new Dictionary<string, Sprite>();
        public Button InspectButton => inspectButton;
        public bool IsConfigured => ((pile != null && pile.IsConfigured && eventPile == null) ||
            (eventPile != null && eventPile.IsConfigured && pile == null)) && inspectButton != null &&
            countText != null && emptyLabel != null && awaitingImageLabel != null && inspectHint != null;
        public string ConfigurationReason => "牌堆=" + ((pile != null && pile.IsConfigured) || (eventPile != null && eventPile.IsConfigured)) + "，入口=" + (inspectButton != null) +
            "，数量=" + (countText != null) + "，空状态=" + (emptyLabel != null) +
            "，等待状态=" + (awaitingImageLabel != null) + "，提示=" + (inspectHint != null);
        public void Render(IReadOnlyList<Sprite> images)
        {
            RenderCards(null, images);
        }

        public void RenderCards(IReadOnlyList<GameBoxCardState> cards, IReadOnlyList<Sprite> images)
        {
            var count = cards?.Count ?? images?.Count ?? 0;
            countText.text = count.ToString();
            emptyLabel.SetActive(count == 0);
            inspectHint.SetActive(count > 0);
            ids.Clear(); imagesById.Clear();
            var ready = true;
            for (var i = 0; i < count; i++)
            {
                // 兼容纯图预览入口；正式游戏盒始终传领域公开的稳定实例 ID。
                var id = cards == null ? "preview-" + i : cards[i]?.InstanceId;
                var image = images != null && i < images.Count ? images[i] : null;
                if (string.IsNullOrEmpty(id) || imagesById.ContainsKey(id) || image == null || image.rect.width <= 0 || image.rect.height <= 0)
                { ready = false; continue; }
                ids.Add(id); imagesById.Add(id, image);
            }
            awaitingImageLabel.SetActive(count > 0 && !ready);
            inspectButton.interactable = count > 0 && ready;
            var visibleIds = ready ? (IReadOnlyList<string>)ids : Array.Empty<string>();
            if (eventPile != null) eventPile.Render(visibleIds, id => imagesById[id]);
            else pile.RenderSprites(visibleIds, id => imagesById[id], null);
        }
    }
}
