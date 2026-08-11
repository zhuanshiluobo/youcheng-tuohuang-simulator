using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ZoomableImageViewerController : MonoBehaviour
    {
        private const float MinZoom = 0.55f;
        private const float MaxZoom = 4f;
        private const float WheelZoomStep = 0.12f;
        private const float PanelScreenFill = 0.92f;
        private const float PanelHorizontalChrome = 192f;
        private const float PanelVerticalChrome = 156f;
        private const float CollapsedPanelHeight = 58f;

        private static readonly HashSet<ZoomableImageViewerController> Instances =
            new HashSet<ZoomableImageViewerController>();
        private static ZoomableImageViewerController registeredPrefab;
        private static int escapeConsumedFrame = -1;

        [SerializeField] private ZoomableImageViewerView view;

        private Func<int, Texture2D> textureProvider;
        private string primaryActionText = string.Empty;
        private string secondaryActionText = string.Empty;
        private Action primaryAction;
        private Action secondaryAction;
        private string viewerName = "Image";
        private string title = string.Empty;
        private int pageCount = 1;
        private int pageIndex;
        private float zoom = 1f;
        private bool collapseEnabled;
        private bool collapsed;
        private bool initialized;
        private bool ownsDetachedCanvas;
        private string collapsedSummary = string.Empty;
        private Vector2 expandedPanelSize;
        private Vector2 expandedPanelPosition;

        public bool IsOpen => initialized && view.RootObject.activeSelf;
        public float Zoom => zoom;
        public int PageIndex => pageIndex;
        public bool IsCollapsed => collapsed;

        public static bool WasEscapeConsumedThisFrame()
        {
            return escapeConsumedFrame == Time.frameCount;
        }

        public static bool HasOpenViewer()
        {
            foreach (var viewer in Instances)
            {
                if (viewer != null && viewer.IsOpen)
                {
                    return true;
                }
            }

            return false;
        }

        public static void RegisterPrefab(ZoomableImageViewerController prefab)
        {
            if (prefab == null)
            {
                Debug.LogError("无法注册空的 ZoomableImageViewer prefab 引用。");
                return;
            }

            registeredPrefab = prefab;
        }

        public static void UnregisterPrefab(ZoomableImageViewerController prefab)
        {
            if (registeredPrefab == prefab)
            {
                registeredPrefab = null;
            }
        }

        public static ZoomableImageViewerController InstantiateRegistered(
            Transform parent,
            string instanceName)
        {
            if (registeredPrefab == null)
            {
                Debug.LogError(
                    "缺少已注册的 ZoomableImageViewer prefab。请由场景中的 GameSettingsMenuController 提供显式资产引用。");
                return null;
            }

            // 动态边界：按需实例化完整编辑器 Prefab，不在运行时创建任何固定 UI 组件。
            var instance = Instantiate(registeredPrefab, parent, false);
            if (!string.IsNullOrEmpty(instanceName))
            {
                instance.gameObject.name = instanceName;
            }

            instance.DetachCanvasForDynamicInstance();

            return instance;
        }

        private void Awake()
        {
            TryInitialize();
        }

        private void OnEnable()
        {
            Instances.Add(this);
        }

        private void OnDisable()
        {
            Instances.Remove(this);
            if (ownsDetachedCanvas && view != null && view.RootObject != null)
            {
                view.RootObject.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            Instances.Remove(this);
            if (ownsDetachedCanvas && view != null && view.CanvasObject != null)
            {
                if (UnityEngine.Application.isPlaying)
                {
                    Destroy(view.CanvasObject);
                }
                else
                {
                    DestroyImmediate(view.CanvasObject);
                }
            }
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }

            var wheelDelta = Input.mouseScrollDelta.y;
            if (!collapsed && Mathf.Abs(wheelDelta) > 0.01f)
            {
                SetZoom(zoom + wheelDelta * WheelZoomStep);
            }

            if (Input.GetMouseButtonDown(1))
            {
                Close();
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                escapeConsumedFrame = Time.frameCount;
                Close();
            }
            else if (pageCount > 1 && Input.GetKeyDown(KeyCode.LeftArrow))
            {
                ShowPage(pageIndex - 1);
            }
            else if (pageCount > 1 && Input.GetKeyDown(KeyCode.RightArrow))
            {
                ShowPage(pageIndex + 1);
            }
        }

        public void Configure(
            string configuredViewerName,
            string configuredTitle,
            int configuredPageCount,
            Func<int, Texture2D> configuredTextureProvider)
        {
            if (!TryInitialize())
            {
                return;
            }

            viewerName = string.IsNullOrEmpty(configuredViewerName) ? "Image" : configuredViewerName;
            title = configuredTitle ?? string.Empty;
            pageCount = Mathf.Max(1, configuredPageCount);
            textureProvider = configuredTextureProvider;
            view.ApplyViewerName(viewerName);
            view.TitleText.text = title;
            view.PageLabel.gameObject.SetActive(pageCount > 1);
            view.PreviousButton.gameObject.SetActive(pageCount > 1);
            view.NextButton.gameObject.SetActive(pageCount > 1);

            if (collapsed)
            {
                SetCollapsed(false);
            }

            UpdateControls();
        }

        public void Open(int initialPage = 0)
        {
            if (!TryInitialize())
            {
                return;
            }

            view.RootObject.SetActive(true);
            ShowPage(initialPage);
        }

        public void ConfigureActions(
            string configuredPrimaryActionText,
            Action configuredPrimaryAction,
            string configuredSecondaryActionText = "",
            Action configuredSecondaryAction = null)
        {
            primaryActionText = configuredPrimaryActionText ?? string.Empty;
            primaryAction = configuredPrimaryAction;
            secondaryActionText = configuredSecondaryActionText ?? string.Empty;
            secondaryAction = configuredSecondaryAction;
            if (TryInitialize())
            {
                UpdateActionButtons();
            }
        }

        public void ConfigureReferenceCollapse(string summary)
        {
            if (!TryInitialize())
            {
                return;
            }

            collapseEnabled = true;
            collapsedSummary = summary ?? string.Empty;
            view.CollapsedSummaryText.text = collapsedSummary;
            view.CollapseToggleButton.gameObject.SetActive(true);
            ApplyCollapseState();
        }

        public void DisableReferenceCollapse()
        {
            if (!TryInitialize())
            {
                return;
            }

            if (collapsed)
            {
                SetCollapsed(false);
            }

            collapseEnabled = false;
            collapsedSummary = string.Empty;
            view.CollapseToggleButton.gameObject.SetActive(false);
            view.CollapsedSummaryText.gameObject.SetActive(false);
        }

        public void SetCollapsed(bool value)
        {
            if (!TryInitialize() || (!collapseEnabled && value))
            {
                return;
            }

            if (collapsed == value)
            {
                ApplyCollapseState();
                return;
            }

            if (value)
            {
                expandedPanelSize = view.PanelTransform.sizeDelta;
                expandedPanelPosition = view.PanelTransform.anchoredPosition;
            }

            collapsed = value;
            ApplyCollapseState();
        }

        public void Close()
        {
            if (initialized)
            {
                view.RootObject.SetActive(false);
            }
        }

        public void SetZoom(float value)
        {
            zoom = Mathf.Clamp(value, MinZoom, MaxZoom);
            if (initialized && view.Image.texture != null)
            {
                ApplyImageSize(view.Image.texture);
                UpdateControls();
            }
        }

        private bool TryInitialize()
        {
            if (initialized)
            {
                return true;
            }

            var reason = "View 未绑定。";
            if (view == null || !view.TryValidateConfiguration(out reason))
            {
                Debug.LogError(
                    "ZoomableImageViewerController 缺少完整编辑器 View 引用：" +
                    reason,
                    this);
                enabled = false;
                return false;
            }

            BindButton(view.CloseButton, Close);
            BindButton(view.PreviousButton, () => ShowPage(pageIndex - 1));
            BindButton(view.NextButton, () => ShowPage(pageIndex + 1));
            BindButton(view.PrimaryActionButton, () => primaryAction?.Invoke());
            BindButton(view.SecondaryActionButton, () => secondaryAction?.Invoke());
            BindButton(view.CollapseToggleButton, () => SetCollapsed(!collapsed));
            expandedPanelSize = view.PanelTransform.sizeDelta;
            expandedPanelPosition = view.PanelTransform.anchoredPosition;
            view.RootObject.SetActive(false);
            view.PrimaryActionButton.gameObject.SetActive(false);
            view.SecondaryActionButton.gameObject.SetActive(false);
            view.CollapseToggleButton.gameObject.SetActive(false);
            view.CollapsedSummaryText.gameObject.SetActive(false);
            initialized = true;
            Instances.Add(this);
            return true;
        }

        private static void BindButton(Button button, UnityEngine.Events.UnityAction action)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }

        private void DetachCanvasForDynamicInstance()
        {
            if (!TryInitialize())
            {
                return;
            }

            view.CanvasObject.transform.SetParent(null, false);
            ownsDetachedCanvas = true;
        }

        private void ShowPage(int index)
        {
            pageIndex = Mathf.Clamp(index, 0, pageCount - 1);
            var texture = textureProvider == null ? null : textureProvider(pageIndex);
            if (texture == null)
            {
                Debug.LogError(viewerName + " viewer texture not found at page " + pageIndex + ".", this);
                return;
            }

            view.Image.texture = texture;
            zoom = 1f;
            ApplyPanelSize(texture);
            ApplyImageSize(texture);
            UpdateControls();
        }

        private void ApplyPanelSize(Texture texture)
        {
            Canvas.ForceUpdateCanvases();
            var root = view.PanelTransform.parent as RectTransform;
            var rootSize = root == null ? Vector2.zero : root.rect.size;
            if (rootSize.x <= 0f || rootSize.y <= 0f)
            {
                rootSize = UiTheme.CanvasReferenceResolution;
            }

            var maximumPanelSize = rootSize * PanelScreenFill;
            var maximumViewportSize = new Vector2(
                Mathf.Max(1f, maximumPanelSize.x - PanelHorizontalChrome),
                Mathf.Max(1f, maximumPanelSize.y - PanelVerticalChrome));
            var fitScale = Mathf.Min(
                maximumViewportSize.x / texture.width,
                maximumViewportSize.y / texture.height);
            var fittedImageSize = new Vector2(texture.width, texture.height) * fitScale;
            view.PanelTransform.sizeDelta = new Vector2(
                fittedImageSize.x + PanelHorizontalChrome,
                fittedImageSize.y + PanelVerticalChrome);
            if (!collapsed)
            {
                expandedPanelSize = view.PanelTransform.sizeDelta;
                expandedPanelPosition = view.PanelTransform.anchoredPosition;
            }

            Canvas.ForceUpdateCanvases();
        }

        private void ApplyCollapseState()
        {
            view.ExpandedContentObject.SetActive(!collapsed);
            view.CollapsedSummaryText.text = collapsedSummary;
            view.CollapsedSummaryText.gameObject.SetActive(collapseEnabled && collapsed);
            view.CollapseToggleButton.gameObject.SetActive(collapseEnabled);

            var toggleRect = view.CollapseToggleButton.GetComponent<RectTransform>();
            if (collapseEnabled && collapsed)
            {
                toggleRect.anchorMin = new Vector2(1f, 0.5f);
                toggleRect.anchorMax = new Vector2(1f, 0.5f);
                toggleRect.pivot = new Vector2(1f, 0.5f);
                toggleRect.sizeDelta = new Vector2(142f, 34f);
                toggleRect.anchoredPosition = new Vector2(-12f, 0f);
            }
            else
            {
                toggleRect.anchorMin = new Vector2(0.5f, 0f);
                toggleRect.anchorMax = new Vector2(0.5f, 0f);
                toggleRect.pivot = new Vector2(0.5f, 0f);
                toggleRect.sizeDelta = new Vector2(208f, 48f);
                toggleRect.anchoredPosition = new Vector2(0f, 18f);
            }

            view.CollapseToggleLabel.text = collapsed ? "▼ 展开卡牌" : "▲ 收起卡牌";
            if (collapsed)
            {
                var width = Mathf.Max(560f, expandedPanelSize.x);
                view.PanelTransform.sizeDelta = new Vector2(width, CollapsedPanelHeight);
                view.PanelTransform.anchoredPosition = expandedPanelPosition +
                    new Vector2(0f, (expandedPanelSize.y - CollapsedPanelHeight) * 0.5f);
            }
            else if (expandedPanelSize.x > 0f && expandedPanelSize.y > 0f)
            {
                view.PanelTransform.sizeDelta = expandedPanelSize;
                view.PanelTransform.anchoredPosition = expandedPanelPosition;
            }

            view.RootBackgroundImage.color = collapsed
                ? new Color(0f, 0f, 0f, 0f)
                : new Color(0f, 0f, 0f, 0.74f);
            view.RootBackgroundImage.raycastTarget = !collapsed;
        }

        private void ApplyImageSize(Texture texture)
        {
            Canvas.ForceUpdateCanvases();
            var viewportSize = view.ViewportTransform.rect.size;
            if (viewportSize.x <= 0f || viewportSize.y <= 0f)
            {
                viewportSize = new Vector2(1400f, 860f);
            }

            var scale = Mathf.Min(viewportSize.x / texture.width, viewportSize.y / texture.height);
            view.ImageTransform.sizeDelta =
                new Vector2(texture.width * scale, texture.height * scale) * zoom;
            view.ImageTransform.anchoredPosition = Vector2.zero;
        }

        private void UpdateControls()
        {
            if (pageCount > 1)
            {
                view.PageLabel.text = string.Format("{0} / {1}", pageIndex + 1, pageCount);
            }

            view.PreviousButton.interactable = pageIndex > 0;
            view.NextButton.interactable = pageIndex < pageCount - 1;
        }

        private void UpdateActionButtons()
        {
            UpdateActionButton(
                view.PrimaryActionButton,
                view.PrimaryActionLabel,
                primaryActionText,
                primaryAction);
            UpdateActionButton(
                view.SecondaryActionButton,
                view.SecondaryActionLabel,
                secondaryActionText,
                secondaryAction);
        }

        private static void UpdateActionButton(Button button, Text label, string text, Action action)
        {
            var visible = action != null && !string.IsNullOrEmpty(text);
            button.gameObject.SetActive(visible);
            label.text = text ?? string.Empty;
        }
    }
}
