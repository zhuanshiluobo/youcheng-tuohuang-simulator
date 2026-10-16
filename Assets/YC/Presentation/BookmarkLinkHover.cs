using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>控制详情展开收回和点击转发；布局、样式和文字由预制体维护。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    public sealed class BookmarkLinkHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Button bookmarkButton;
        [SerializeField] private Button detailButton;
        [SerializeField] private CanvasGroup detailGroup;
        [SerializeField, Min(0.01f)] private float animationDuration = 0.2f;

        private RectTransform detailRect;
        private Vector3 expandedScale;
        private float progress;
        private Coroutine animationRoutine;

        private void Awake()
        {
            if (detailGroup == null) return;
            detailRect = detailGroup.transform as RectTransform;
            expandedScale = detailRect.localScale;
        }

        private void OnEnable()
        {
            progress = 0f;
            ApplyAnimation();
            SetInteraction(false);
            if (detailButton != null) detailButton.onClick.AddListener(OpenBookmark);
        }

        private void OnDisable()
        {
            if (animationRoutine != null) StopCoroutine(animationRoutine);
            animationRoutine = null;
            if (detailButton != null) detailButton.onClick.RemoveListener(OpenBookmark);
            progress = 0f;
            ApplyAnimation();
            SetInteraction(false);
            if (detailRect != null) detailRect.localScale = expandedScale;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            AnimateTo(bookmarkButton != null && bookmarkButton.IsActive() && bookmarkButton.IsInteractable());
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            // 详情是书签子对象，移入详情不触发整个交互区域的退出。
            AnimateTo(false);
        }

        private void AnimateTo(bool visible)
        {
            if (!isActiveAndEnabled || detailGroup == null) return;
            if (animationRoutine != null) StopCoroutine(animationRoutine);
            SetInteraction(visible);
            animationRoutine = StartCoroutine(Animate(visible ? 1f : 0f));
        }

        private IEnumerator Animate(float target)
        {
            // 从当前进度反向，快速进出时不跳变；暂停游戏时仍可播放。
            while (!Mathf.Approximately(progress, target))
            {
                progress = Mathf.MoveTowards(progress, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, animationDuration));
                ApplyAnimation();
                yield return null;
            }
            animationRoutine = null;
        }

        private void ApplyAnimation()
        {
            if (detailGroup == null || detailRect == null) return;
            float eased = Mathf.SmoothStep(0f, 1f, progress);
            detailRect.localScale = new Vector3(expandedScale.x * eased, expandedScale.y, expandedScale.z);
            detailGroup.alpha = eased;
        }

        private void SetInteraction(bool visible)
        {
            if (detailGroup == null) return;
            detailGroup.interactable = visible;
            detailGroup.blocksRaycasts = visible;
        }

        private void OpenBookmark()
        {
            if (bookmarkButton != null && bookmarkButton.IsActive() && bookmarkButton.IsInteractable())
                bookmarkButton.onClick.Invoke();
        }
    }
}