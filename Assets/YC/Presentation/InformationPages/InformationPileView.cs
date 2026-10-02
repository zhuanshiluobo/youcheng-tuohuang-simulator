using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class InformationPileView : MonoBehaviour
    {
        [SerializeField] private Image[] layers;
        [SerializeField] private Button inspectButton;
        [SerializeField] private Text countText;
        [SerializeField] private GameObject emptyLabel;
        [SerializeField] private GameObject awaitingImageLabel;
        [SerializeField] private GameObject inspectHint;
        [SerializeField] private Vector2 maximumCardSize = new Vector2(182, 260);
        [SerializeField] private float padding = 10;
        private IReadOnlyList<Sprite> cards;
        public Button InspectButton => inspectButton;
        public bool IsConfigured => layers != null && layers.Length == 5 && inspectButton != null &&
            countText != null && emptyLabel != null && awaitingImageLabel != null && inspectHint != null;
        public string ConfigurationReason => "层=" + (layers?.Length ?? 0) + "，入口=" + (inspectButton != null) +
            "，数量=" + (countText != null) + "，空状态=" + (emptyLabel != null) +
            "，等待状态=" + (awaitingImageLabel != null) + "，提示=" + (inspectHint != null);
        public void Render(IReadOnlyList<Sprite> images)
        {
            cards = images; countText.text = (cards?.Count ?? 0).ToString();
            emptyLabel.SetActive(cards == null || cards.Count == 0);
            inspectHint.SetActive(cards != null && cards.Count > 0);
            Arrange();
        }
        private void OnRectTransformDimensionsChange() { if (cards != null) Arrange(); }
        private void OnEnable() { if (cards != null) Arrange(); }
        private void Arrange()
        {
            var n = Mathf.Min(5, cards?.Count ?? 0); var start = (cards?.Count ?? 0) - n;
            var ready = true;
            for (var i = 0; i < n; i++) ready &= cards[start+i] != null && cards[start+i].rect.width > 0 && cards[start+i].rect.height > 0;
            awaitingImageLabel.SetActive(n > 0 && !ready); inspectButton.interactable = n > 0 && ready;
            for (var i = 0; i < 5; i++) layers[i].gameObject.SetActive(ready && i < n);
            if (!ready || n == 0) return;
            var angles = new[] { -6f, 4f, -1f, 6f, -3f };
            var offsets = new[] { new Vector2(-.07f,-.03f),new Vector2(.06f,.02f),new Vector2(-.03f,.03f),new Vector2(.04f,-.02f),Vector2.zero };
            var sizes = new Vector2[n]; var centers = new Vector2[n];
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity); var max = -min;
            for (var i = 0; i < n; i++)
            {
                var source = cards[start+i].rect.size;
                sizes[i] = source * Mathf.Min(maximumCardSize.x/source.x, maximumCardSize.y/source.y);
                centers[i] = n == 1 ? Vector2.zero : Vector2.Scale(offsets[5-n+i], sizes[i]);
                var angle = n == 1 ? 0 : angles[5-n+i];
                var a = angle*Mathf.Deg2Rad;
                var extent = new Vector2(Mathf.Abs(Mathf.Cos(a))*sizes[i].x+Mathf.Abs(Mathf.Sin(a))*sizes[i].y,
                    Mathf.Abs(Mathf.Sin(a))*sizes[i].x+Mathf.Abs(Mathf.Cos(a))*sizes[i].y)*.5f;
                min = Vector2.Min(min,centers[i]-extent); max=Vector2.Max(max,centers[i]+extent);
            }
            var area = ((RectTransform)transform).rect.size - Vector2.one*padding*2;
            var span = max-min; var scale = Mathf.Min(1, area.x/span.x, area.y/span.y);
            for (var i = 0; i < n; i++)
            {
                layers[i].sprite = cards[start+i]; layers[i].preserveAspect = true;
                var rect = layers[i].rectTransform;
                rect.sizeDelta = sizes[i]*scale; rect.anchoredPosition = (centers[i]-(min+max)*.5f)*scale;
                rect.localRotation = Quaternion.Euler(0,0,n == 1 ? 0 : angles[5-n+i]);
            }
        }
    }
}
