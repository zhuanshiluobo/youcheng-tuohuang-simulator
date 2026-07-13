using System;
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
        private static int escapeConsumedFrame = -1;

        private Func<int, Texture2D> textureProvider;
        private RectTransform panelTransform;
        private RectTransform viewportTransform;
        private RectTransform imageTransform;
        private GameObject rootObject;
        private Image rootBackgroundImage;
        private GameObject expandedContentObject;
        private RawImage image;
        private Text pageLabel;
        private Text titleText;
        private Button previousButton;
        private Button nextButton;
        private Button primaryActionButton;
        private Button secondaryActionButton;
        private Text primaryActionLabel;
        private Text secondaryActionLabel;
        private Text collapsedSummaryText;
        private RectTransform collapseToggleRect;
        private Text collapseToggleLabel;
        private string primaryActionText = string.Empty;
        private string secondaryActionText = string.Empty;
        private Action primaryAction;
        private Action secondaryAction;
        private string viewerName;
        private string title;
        private int pageCount;
        private int pageIndex;
        private float zoom = 1f;
        private bool collapseEnabled;
        private bool collapsed;
        private string collapsedSummary = string.Empty;
        private Vector2 expandedPanelSize;
        private Vector2 expandedPanelPosition;

        public bool IsOpen => rootObject != null && rootObject.activeSelf;
        public float Zoom => zoom;
        public int PageIndex => pageIndex;
        public bool IsCollapsed => collapsed;

        public static bool WasEscapeConsumedThisFrame()
        {
            return escapeConsumedFrame == Time.frameCount;
        }

        public static bool HasOpenViewer()
        {
            var viewers = FindObjectsOfType<ZoomableImageViewerController>();
            for (var i = 0; i < viewers.Length; i++)
            {
                if (viewers[i] != null && viewers[i].IsOpen)
                {
                    return true;
                }
            }

            return false;
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
            viewerName = string.IsNullOrEmpty(configuredViewerName) ? "Image" : configuredViewerName;
            title = configuredTitle ?? string.Empty;
            pageCount = Mathf.Max(1, configuredPageCount);
            textureProvider = configuredTextureProvider;

            if (collapsed)
            {
                SetCollapsed(false);
            }

            if (rootObject == null)
            {
                BuildUi();
            }
            else if (titleText != null)
            {
                titleText.text = title;
            }
        }

        public void Open(int initialPage = 0)
        {
            if (rootObject == null)
            {
                BuildUi();
            }

            rootObject.SetActive(true);
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
            if (rootObject != null)
            {
                UpdateActionButtons();
            }
        }

        public void ConfigureReferenceCollapse(string summary)
        {
            collapseEnabled = true;
            collapsedSummary = summary ?? string.Empty;
            if (collapsedSummaryText != null)
            {
                collapsedSummaryText.text = collapsedSummary;
            }

            if (collapseToggleRect != null)
            {
                collapseToggleRect.gameObject.SetActive(true);
            }

            ApplyCollapseState();
        }

        public void DisableReferenceCollapse()
        {
            if (collapsed)
            {
                SetCollapsed(false);
            }

            collapseEnabled = false;
            collapsedSummary = string.Empty;
            if (collapseToggleRect != null)
            {
                collapseToggleRect.gameObject.SetActive(false);
            }
            if (collapsedSummaryText != null)
            {
                collapsedSummaryText.gameObject.SetActive(false);
            }
        }

        public void SetCollapsed(bool value)
        {
            if (!collapseEnabled && value)
            {
                return;
            }

            if (collapsed == value)
            {
                ApplyCollapseState();
                return;
            }

            if (value && panelTransform != null)
            {
                expandedPanelSize = panelTransform.sizeDelta;
                expandedPanelPosition = panelTransform.anchoredPosition;
            }

            collapsed = value;
            ApplyCollapseState();
        }

        public void Close()
        {
            if (rootObject != null)
            {
                rootObject.SetActive(false);
            }
        }

        public void SetZoom(float value)
        {
            zoom = Mathf.Clamp(value, MinZoom, MaxZoom);
            if (image != null && image.texture != null)
            {
                ApplyImageSize(image.texture);
                UpdateControls();
            }
        }

        private void BuildUi()
        {
            UguiUtility.EnsureEventSystem();
            var canvas = UguiUtility.CreateCanvas(viewerName + " Viewer Canvas", 130, transform);
            var canvasTransform = canvas.GetComponent<RectTransform>();

            rootObject = new GameObject(viewerName + " Viewer", typeof(RectTransform), typeof(Image));
            rootObject.transform.SetParent(canvasTransform, false);
            rootObject.SetActive(false);

            var rootRect = rootObject.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootBackgroundImage = rootObject.GetComponent<Image>();
            rootBackgroundImage.color = new Color(0f, 0f, 0f, 0.74f);

            CreatePanel(rootRect);
        }

        private void CreatePanel(RectTransform parent)
        {
            var panelObject = new GameObject(viewerName + " Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(parent, false);
            panelTransform = panelObject.GetComponent<RectTransform>();
            panelTransform.anchorMin = new Vector2(0.5f, 0.5f);
            panelTransform.anchorMax = new Vector2(0.5f, 0.5f);
            panelTransform.pivot = new Vector2(0.5f, 0.5f);
            panelTransform.anchoredPosition = Vector2.zero;
            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            expandedContentObject = new GameObject(viewerName + " Expanded Content", typeof(RectTransform));
            expandedContentObject.transform.SetParent(panelTransform, false);
            var expandedRect = expandedContentObject.GetComponent<RectTransform>();
            expandedRect.anchorMin = Vector2.zero;
            expandedRect.anchorMax = Vector2.one;
            expandedRect.offsetMin = Vector2.zero;
            expandedRect.offsetMax = Vector2.zero;

            CreateHeader(expandedRect);
            CreateViewport(expandedRect);
            CreateFooter(expandedRect);

            if (pageCount > 1)
            {
                previousButton = CreateNavButton(expandedRect, "Previous " + viewerName, "<", new Vector2(0f, 0.5f), new Vector2(28f, 0f), () => ShowPage(pageIndex - 1));
                nextButton = CreateNavButton(expandedRect, "Next " + viewerName, ">", new Vector2(1f, 0.5f), new Vector2(-28f, 0f), () => ShowPage(pageIndex + 1));
            }

            CreateCollapseControls(panelTransform);
        }

        private void CreateCollapseControls(RectTransform parent)
        {
            var summaryObject = new GameObject(viewerName + " Collapsed Summary", typeof(RectTransform), typeof(Text), typeof(Outline));
            summaryObject.transform.SetParent(parent, false);
            var summaryRect = summaryObject.GetComponent<RectTransform>();
            summaryRect.anchorMin = new Vector2(0f, 0.5f);
            summaryRect.anchorMax = new Vector2(1f, 0.5f);
            summaryRect.sizeDelta = new Vector2(-160f, 42f);
            summaryRect.anchoredPosition = new Vector2(-68f, 0f);
            collapsedSummaryText = summaryObject.GetComponent<Text>();
            collapsedSummaryText.text = collapsedSummary;
            collapsedSummaryText.alignment = TextAnchor.MiddleLeft;
            collapsedSummaryText.color = UiTheme.GoldText;
            collapsedSummaryText.fontSize = 18;
            collapsedSummaryText.fontStyle = FontStyle.Bold;
            collapsedSummaryText.font = FontUtility.GetCjkFont(collapsedSummaryText.fontSize);
            summaryObject.GetComponent<Outline>().effectColor = UiTheme.DarkShadowLight;
            summaryObject.SetActive(false);

            var toggleButton = CreateFooterActionButton(parent, viewerName + " Collapse Toggle", new Vector2(0f, 18f));
            collapseToggleRect = toggleButton.GetComponent<RectTransform>();
            collapseToggleLabel = toggleButton.GetComponentInChildren<Text>();
            toggleButton.onClick.AddListener(() => SetCollapsed(!collapsed));
            toggleButton.gameObject.SetActive(false);
        }

        private void CreateHeader(RectTransform parent)
        {
            var titleObject = new GameObject(viewerName + " Title", typeof(RectTransform), typeof(Text), typeof(Outline));
            titleObject.transform.SetParent(parent, false);
            var titleRect = titleObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 64f);

            titleText = titleObject.GetComponent<Text>();
            titleText.text = title;
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = UiTheme.GoldText;
            titleText.fontSize = 34;
            titleText.fontStyle = FontStyle.Bold;
            titleText.font = FontUtility.GetCjkFont(titleText.fontSize);
            titleObject.GetComponent<Outline>().effectColor = UiTheme.DarkShadowLight;

            var closeButton = new GameObject("Close " + viewerName + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            closeButton.transform.SetParent(parent, false);
            var closeRect = closeButton.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1f, 1f);
            closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.sizeDelta = new Vector2(42f, 42f);
            closeRect.anchoredPosition = new Vector2(-18f, -12f);
            ApplyButtonStyle(closeButton);
            closeButton.GetComponent<Button>().onClick.AddListener(Close);
            CreateButtonText(closeRect, "×", 30);
        }

        private void CreateViewport(RectTransform parent)
        {
            var viewportObject = new GameObject(viewerName + " Viewport", typeof(RectTransform), typeof(Image), typeof(Mask), typeof(ScrollRect));
            viewportObject.transform.SetParent(parent, false);
            viewportTransform = viewportObject.GetComponent<RectTransform>();
            viewportTransform.anchorMin = Vector2.zero;
            viewportTransform.anchorMax = Vector2.one;
            viewportTransform.offsetMin = new Vector2(96f, 82f);
            viewportTransform.offsetMax = new Vector2(-96f, -74f);
            viewportObject.GetComponent<Image>().color = new Color(0.025f, 0.022f, 0.02f, 0.96f);
            viewportObject.GetComponent<Mask>().showMaskGraphic = true;

            imageTransform = new GameObject(viewerName + " Image", typeof(RectTransform), typeof(RawImage)).GetComponent<RectTransform>();
            imageTransform.SetParent(viewportTransform, false);
            imageTransform.anchorMin = new Vector2(0.5f, 0.5f);
            imageTransform.anchorMax = new Vector2(0.5f, 0.5f);
            imageTransform.pivot = new Vector2(0.5f, 0.5f);
            image = imageTransform.GetComponent<RawImage>();
            image.color = Color.white;

            var scrollRect = viewportObject.GetComponent<ScrollRect>();
            scrollRect.horizontal = true;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.inertia = true;
            scrollRect.scrollSensitivity = 0f;
            scrollRect.viewport = viewportTransform;
            scrollRect.content = imageTransform;
        }

        private void CreateFooter(RectTransform parent)
        {
            var labelObject = new GameObject(viewerName + " Page Label", typeof(RectTransform), typeof(Text), typeof(Outline));
            labelObject.transform.SetParent(parent, false);
            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0.5f, 0f);
            labelRect.anchorMax = new Vector2(0.5f, 0f);
            labelRect.pivot = new Vector2(0.5f, 0f);
            labelRect.sizeDelta = new Vector2(520f, 44f);
            labelRect.anchoredPosition = new Vector2(0f, 22f);
            pageLabel = labelObject.GetComponent<Text>();
            pageLabel.alignment = TextAnchor.MiddleCenter;
            pageLabel.color = UiTheme.GoldText;
            pageLabel.fontSize = 20;
            pageLabel.fontStyle = FontStyle.Bold;
            pageLabel.font = FontUtility.GetCjkFont(pageLabel.fontSize);
            labelObject.SetActive(pageCount > 1);

            primaryActionButton = CreateFooterActionButton(parent, "Primary " + viewerName + " Action", new Vector2(-112f, 18f));
            secondaryActionButton = CreateFooterActionButton(parent, "Secondary " + viewerName + " Action", new Vector2(112f, 18f));
            primaryActionLabel = primaryActionButton.GetComponentInChildren<Text>();
            secondaryActionLabel = secondaryActionButton.GetComponentInChildren<Text>();
            primaryActionButton.onClick.AddListener(() => primaryAction?.Invoke());
            secondaryActionButton.onClick.AddListener(() => secondaryAction?.Invoke());
            UpdateActionButtons();
        }

        private static Button CreateFooterActionButton(RectTransform parent, string name, Vector2 position)
        {
            var buttonObject = new GameObject(name + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(208f, 48f);
            rect.anchoredPosition = position;
            ApplyButtonStyle(buttonObject);
            CreateButtonText(rect, string.Empty, 20);
            return buttonObject.GetComponent<Button>();
        }

        private void UpdateActionButtons()
        {
            UpdateActionButton(primaryActionButton, primaryActionLabel, primaryActionText, primaryAction);
            UpdateActionButton(secondaryActionButton, secondaryActionLabel, secondaryActionText, secondaryAction);
        }

        private static void UpdateActionButton(Button button, Text label, string text, Action action)
        {
            if (button == null)
            {
                return;
            }

            var visible = action != null && !string.IsNullOrEmpty(text);
            button.gameObject.SetActive(visible);
            if (label != null)
            {
                label.text = text ?? string.Empty;
            }
        }

        private Button CreateNavButton(RectTransform parent, string name, string label, Vector2 anchor, Vector2 position, UnityEngine.Events.UnityAction action)
        {
            var buttonObject = new GameObject(name + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(anchor.x, 0.5f);
            rect.sizeDelta = new Vector2(58f, 118f);
            rect.anchoredPosition = position;
            ApplyButtonStyle(buttonObject);
            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(action);
            CreateButtonText(rect, label, 34);
            return button;
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

            image.texture = texture;
            zoom = 1f;
            ApplyPanelSize(texture);
            ApplyImageSize(texture);
            UpdateControls();
        }

        private void ApplyPanelSize(Texture texture)
        {
            Canvas.ForceUpdateCanvases();
            var root = panelTransform == null ? null : panelTransform.parent as RectTransform;
            var rootSize = root == null ? Vector2.zero : root.rect.size;
            if (rootSize.x <= 0f || rootSize.y <= 0f)
            {
                rootSize = new Vector2(1920f, 1080f);
            }

            var maximumPanelSize = rootSize * PanelScreenFill;
            var maximumViewportSize = new Vector2(
                Mathf.Max(1f, maximumPanelSize.x - PanelHorizontalChrome),
                Mathf.Max(1f, maximumPanelSize.y - PanelVerticalChrome));
            var fitScale = Mathf.Min(
                maximumViewportSize.x / texture.width,
                maximumViewportSize.y / texture.height);
            var fittedImageSize = new Vector2(texture.width, texture.height) * fitScale;
            panelTransform.sizeDelta = new Vector2(
                fittedImageSize.x + PanelHorizontalChrome,
                fittedImageSize.y + PanelVerticalChrome);
            if (!collapsed)
            {
                expandedPanelSize = panelTransform.sizeDelta;
                expandedPanelPosition = panelTransform.anchoredPosition;
            }
            Canvas.ForceUpdateCanvases();
        }

        private void ApplyCollapseState()
        {
            if (panelTransform == null)
            {
                return;
            }

            if (expandedContentObject != null)
            {
                expandedContentObject.SetActive(!collapsed);
            }

            if (collapsedSummaryText != null)
            {
                collapsedSummaryText.text = collapsedSummary;
                collapsedSummaryText.gameObject.SetActive(collapseEnabled && collapsed);
            }

            if (collapseToggleRect != null)
            {
                collapseToggleRect.gameObject.SetActive(collapseEnabled);
                if (collapseEnabled && collapsed)
                {
                    collapseToggleRect.anchorMin = new Vector2(1f, 0.5f);
                    collapseToggleRect.anchorMax = new Vector2(1f, 0.5f);
                    collapseToggleRect.pivot = new Vector2(1f, 0.5f);
                    collapseToggleRect.sizeDelta = new Vector2(142f, 34f);
                    collapseToggleRect.anchoredPosition = new Vector2(-12f, 0f);
                }
                else
                {
                    collapseToggleRect.anchorMin = new Vector2(0.5f, 0f);
                    collapseToggleRect.anchorMax = new Vector2(0.5f, 0f);
                    collapseToggleRect.pivot = new Vector2(0.5f, 0f);
                    collapseToggleRect.sizeDelta = new Vector2(208f, 48f);
                    collapseToggleRect.anchoredPosition = new Vector2(0f, 18f);
                }
            }

            if (collapseToggleLabel != null)
            {
                collapseToggleLabel.text = collapsed ? "▼ 展开卡牌" : "▲ 收起卡牌";
            }

            if (collapsed)
            {
                var width = Mathf.Max(560f, expandedPanelSize.x);
                panelTransform.sizeDelta = new Vector2(width, CollapsedPanelHeight);
                panelTransform.anchoredPosition = expandedPanelPosition +
                    new Vector2(0f, (expandedPanelSize.y - CollapsedPanelHeight) * 0.5f);
            }
            else if (expandedPanelSize.x > 0f && expandedPanelSize.y > 0f)
            {
                panelTransform.sizeDelta = expandedPanelSize;
                panelTransform.anchoredPosition = expandedPanelPosition;
            }

            if (rootBackgroundImage != null)
            {
                rootBackgroundImage.color = collapsed ? new Color(0f, 0f, 0f, 0f) : new Color(0f, 0f, 0f, 0.74f);
                rootBackgroundImage.raycastTarget = !collapsed;
            }
        }

        private void ApplyImageSize(Texture texture)
        {
            Canvas.ForceUpdateCanvases();
            var viewportSize = viewportTransform.rect.size;
            if (viewportSize.x <= 0f || viewportSize.y <= 0f)
            {
                viewportSize = new Vector2(1400f, 860f);
            }

            var scale = Mathf.Min(viewportSize.x / texture.width, viewportSize.y / texture.height);
            imageTransform.sizeDelta = new Vector2(texture.width * scale, texture.height * scale) * zoom;
            imageTransform.anchoredPosition = Vector2.zero;
        }

        private void UpdateControls()
        {
            if (pageLabel != null && pageCount > 1)
            {
                pageLabel.text = string.Format(
                    "{0} / {1}",
                    pageIndex + 1,
                    pageCount);
            }
            if (previousButton != null) previousButton.interactable = pageIndex > 0;
            if (nextButton != null) nextButton.interactable = pageIndex < pageCount - 1;
        }

        private static void ApplyButtonStyle(GameObject buttonObject)
        {
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(2f, -2f);
        }

        private static void CreateButtonText(RectTransform parent, string content, int fontSize)
        {
            var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text), typeof(Outline));
            textObject.transform.SetParent(parent, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);
            var text = textObject.GetComponent<Text>();
            text.text = content;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(fontSize);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = fontSize;
            textObject.GetComponent<Outline>().effectColor = UiTheme.DarkShadowLight;
        }
    }
}
