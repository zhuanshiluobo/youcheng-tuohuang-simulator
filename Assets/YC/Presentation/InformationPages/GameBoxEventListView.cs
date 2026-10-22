using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>游戏盒事件只读列表，窗口来源为游戏盒，条目来源为事件原图显示组件。</summary>
    public sealed class GameBoxEventListView : MonoBehaviour
    {
        [SerializeField] private InformationPageDismiss dismiss;
        [SerializeField] private Text title, hint;
        [SerializeField] private ScrollRect scroll;
        [SerializeField] private GameBoxEventItemView itemPrefab;
        [SerializeField] private float maximumCardHeight = 360, framePadding = 12;
        private readonly List<GameBoxEventItemView> items = new List<GameBoxEventItemView>();
        private readonly List<string> boundIds = new List<string>();
        private int visibleCount;
        private bool sizesDirty = true;
        private Vector2 lastViewportSize;
        private float lastMaximumCardHeight, lastFramePadding;
        private Func<bool> valid;
        private Action closed;
        private CardViewer viewer;
        private bool showing;
        public bool IsShowing => showing;
        public bool IsVisible => showing && gameObject.activeInHierarchy;
        public float ScrollPosition => scroll.horizontalNormalizedPosition;
        public bool IsConfigured => dismiss != null && title != null && hint != null && scroll != null &&
            scroll.viewport != null && scroll.content != null && scroll.horizontalScrollbar != null && itemPrefab != null && itemPrefab.IsConfigured;
        public void Present(string heading, string instructions, IReadOnlyList<string> ids, IReadOnlyList<Sprite> sprites,
            Func<bool> isCurrent, Action close, float position)
        {
            valid = isCurrent; closed = close; showing = true;
            title.text = heading; hint.text = instructions; dismiss.Dismiss = Return;
            while (items.Count < ids.Count)
            {
                items.Add(Instantiate(itemPrefab, scroll.content, false));
                boundIds.Add(null);
            }
            if (visibleCount != ids.Count) sizesDirty = true;
            visibleCount = ids.Count;
            for (var i = 0; i < items.Count; i++)
            {
                var index = i;
                items[i].gameObject.SetActive(i < ids.Count);
                if (i >= ids.Count) continue;
                if (boundIds[i] != ids[i] || items[i].Sprite != sprites[i]) sizesDirty = true;
                boundIds[i] = ids[i];
                items[i].Bind(ids[i], sprites[i], () => Inspect(index), Current);
            }
            GameplayHudFrame.Active?.ShowInformationPage(gameObject);
            if (GameplayHudFrame.Active == null) gameObject.SetActive(true);
            Canvas.ForceUpdateCanvases();
            RefreshSizes(); LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            scroll.StopMovement(); scroll.horizontalNormalizedPosition = position;
        }
        private bool Current() => showing && (valid == null || valid());
        private void OnEnable() { sizesDirty = true; Canvas.willRenderCanvases += RefreshSizes; }
        private void OnDisable() => Canvas.willRenderCanvases -= RefreshSizes;
        private void RefreshSizes()
        {
            if (!IsConfigured) return;
            var viewportSize = scroll.viewport.rect.size;
            if (!sizesDirty && viewportSize == lastViewportSize && maximumCardHeight == lastMaximumCardHeight &&
                framePadding == lastFramePadding) return;
            sizesDirty = false;
            lastViewportSize = viewportSize; lastMaximumCardHeight = maximumCardHeight; lastFramePadding = framePadding;
            var height = Mathf.Max(0, Mathf.Min(maximumCardHeight, viewportSize.y - framePadding * 2));
            for (var i = 0; i < visibleCount; i++)
                items[i].SetSize(items[i].Measure(new Vector2(float.MaxValue, height)), true);
        }
        private void Inspect(int index)
        {
            if (!Current() || !IsVisible || index < 0 || index >= items.Count || items[index].Sprite == null) return;
            var position = ScrollPosition;
            if (viewer == null) viewer = CardViewer.InstantiateFor(transform);
            viewer.OpenInspect(items[index].Sprite, () =>
            {
                if (!Current()) { Hide(true); return; }
                GameplayHudFrame.Active?.ShowInformationPage(gameObject);
                if (GameplayHudFrame.Active == null) gameObject.SetActive(true);
                Canvas.ForceUpdateCanvases(); scroll.StopMovement(); scroll.horizontalNormalizedPosition = position;
                EventSystem.current?.SetSelectedGameObject(items[index].gameObject);
            }, Current);
        }
        private void Update()
        {
            if (showing && !Current()) { Hide(true); return; }
        }
        private void Return() { if (Current()) closed?.Invoke(); else Hide(true); }
        public void Hide(bool abandon = false)
        {
            showing = false; valid = null; closed = null;
            if (viewer != null) viewer.Dismiss();
            if (abandon) GameplayHudFrame.Active?.AbandonInformationPage(gameObject);
            else GameplayHudFrame.Active?.ReleasePage(gameObject);
            gameObject.SetActive(false);
        }
        private void OnDestroy() { if (viewer != null) Destroy(viewer.gameObject); }
    }
}
