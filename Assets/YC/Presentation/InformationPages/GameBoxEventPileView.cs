using System.Collections.Generic;
using UnityEngine;

namespace YC.Presentation
{
    /// <summary>事件专用牌堆。200×250 是容纳上限，每张牌和边框使用实际切片比例。</summary>
    public sealed class GameBoxEventPileView : MonoBehaviour
    {
        [SerializeField] private GameBoxEventItemView itemPrefab;
        [SerializeField] private RectTransform viewport, content;
        [SerializeField] private Vector2 maximumSize = new Vector2(200, 250);
        [SerializeField] private float padding = 8, frameOutset = 4, tiltDegrees = 6;
        private const int MaximumLayers = 5;
        private readonly List<GameBoxEventItemView> items = new List<GameBoxEventItemView>(MaximumLayers);
        private readonly string[] keys = new string[MaximumLayers];
        private readonly float[] offsets = new float[MaximumLayers], angles = new float[MaximumLayers];
        private readonly Vector2[] sizes = new Vector2[MaximumLayers], centers = new Vector2[MaximumLayers];
        private int visibleCount;
        private bool layoutDirty = true;
        private Vector2 lastViewportSize, lastMaximumSize;
        private float lastPadding, lastFrameOutset, lastTiltDegrees;
        public bool IsConfigured => itemPrefab != null && itemPrefab.IsConfigured && viewport != null && content != null;
        public void Render(IReadOnlyList<string> ids, System.Func<string, Sprite> resolve)
        {
            var first = Mathf.Max(0, ids.Count - MaximumLayers);
            var count = ids.Count - first;
            // 池中实例只追加，按底到顶顺序复用，无需循环调整 sibling 顺序。
            while (items.Count < count) items.Add(Instantiate(itemPrefab, content, false));
            if (visibleCount != count) layoutDirty = true;
            visibleCount = count;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].gameObject.activeSelf != (i < count)) items[i].gameObject.SetActive(i < count);
                if (i >= count) continue;
                var id = ids[first + i];
                var sprite = resolve(id);
                if (keys[i] == id && items[i].Sprite == sprite) continue;
                if (keys[i] != id)
                {
                    uint hash = 2166136261;
                    foreach (var c in id) hash = unchecked((hash ^ c) * 16777619);
                    offsets[i] = (hash % 2001) / 1000f - 1f;
                    keys[i] = id;
                }
                items[i].Bind(id, sprite, null, null);
                layoutDirty = true;
            }
            Arrange();
        }
        private void OnEnable() { layoutDirty = true; Canvas.willRenderCanvases += Arrange; }
        private void OnDisable() => Canvas.willRenderCanvases -= Arrange;
        private void Arrange()
        {
            if (!IsConfigured || visibleCount == 0) return;
            var viewportSize = viewport.rect.size;
            if (!layoutDirty && viewportSize == lastViewportSize && maximumSize == lastMaximumSize &&
                padding == lastPadding && frameOutset == lastFrameOutset && tiltDegrees == lastTiltDegrees) return;
            layoutDirty = false;
            lastViewportSize = viewportSize; lastMaximumSize = maximumSize;
            lastPadding = padding; lastFrameOutset = frameOutset; lastTiltDegrees = tiltDegrees;
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (var i = 0; i < visibleCount; i++)
            {
                sizes[i] = items[i].Measure(maximumSize);
                angles[i] = visibleCount == 1 ? 0 : offsets[i] * tiltDegrees;
                centers[i] = visibleCount == 1 ? Vector2.zero : new Vector2(offsets[i] * sizes[i].x * .06f, i * sizes[i].y * .025f);
                var radians = angles[i] * Mathf.Deg2Rad;
                var extent = sizes[i] * .5f + Vector2.one * frameOutset;
                var rotated = new Vector2(Mathf.Abs(Mathf.Cos(radians)) * extent.x + Mathf.Abs(Mathf.Sin(radians)) * extent.y,
                    Mathf.Abs(Mathf.Sin(radians)) * extent.x + Mathf.Abs(Mathf.Cos(radians)) * extent.y);
                min = Vector2.Min(min, centers[i] - rotated); max = Vector2.Max(max, centers[i] + rotated);
            }
            var available = Vector2.Max(Vector2.zero, viewportSize - Vector2.one * (padding * 2));
            var bounds = max - min;
            var scale = Mathf.Min(1, available.x / Mathf.Max(1, bounds.x), available.y / Mathf.Max(1, bounds.y));
            for (var i = 0; i < visibleCount; i++)
            {
                var item = items[i];
                item.Root.anchorMin = item.Root.anchorMax = item.Root.pivot = new Vector2(.5f, .5f);
                item.SetSize(sizes[i], false);
                item.Root.localScale = Vector3.one * scale;
                item.Root.anchoredPosition = (centers[i] - (min + max) * .5f) * scale;
                item.Root.localEulerAngles = new Vector3(0, 0, angles[i]);
            }
        }
    }
}
