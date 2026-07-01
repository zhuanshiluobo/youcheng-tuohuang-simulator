using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class RulebookViewerController : MonoBehaviour
    {
        private const int PageCount = 28;
        private const float MinZoom = 0.55f;
        private const float MaxZoom = 4f;
        private const float WheelZoomStep = 0.12f;
        private const string PageResourcePrefix = "RulebookPages/page_";

        private RectTransform viewportTransform;
        private RectTransform pageTransform;
        private GameObject rootObject;
        private RawImage pageImage;
        private Text pageLabel;
        private Button previousButton;
        private Button nextButton;
        private int pageIndex;
        private float zoom = 1f;

        private void Awake()
        {
            BuildUi();
        }

        private void Update()
        {
            if (rootObject == null || !rootObject.activeSelf)
            {
                return;
            }

            var wheelDelta = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheelDelta) > 0.01f)
            {
                SetZoom(zoom + wheelDelta * WheelZoomStep);
            }

            if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                ShowPreviousPage();
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                ShowNextPage();
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
            }
        }

        public void Open()
        {
            if (rootObject == null)
            {
                BuildUi();
            }

            rootObject.SetActive(true);
            ShowPage(pageIndex);
        }

        public void Close()
        {
            if (rootObject != null)
            {
                rootObject.SetActive(false);
            }
        }

        private void BuildUi()
        {
            if (rootObject != null)
            {
                return;
            }

            UguiUtility.EnsureEventSystem();
            var canvas = UguiUtility.CreateCanvas("Rulebook Viewer Canvas", 130, transform);
            var canvasTransform = canvas.GetComponent<RectTransform>();

            rootObject = new GameObject("Rulebook Viewer", typeof(RectTransform), typeof(Image));
            rootObject.transform.SetParent(canvasTransform, false);
            rootObject.SetActive(false);

            var rootRect = rootObject.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.74f);

            CreatePanel(rootRect);
        }

        private void CreatePanel(RectTransform parent)
        {
            var panelObject = new GameObject("Rulebook Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(parent, false);

            var panelRect = panelObject.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.04f, 0.04f);
            panelRect.anchorMax = new Vector2(0.96f, 0.96f);
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            CreateHeader(panelRect);
            CreateViewport(panelRect);
            CreateFooter(panelRect);
            previousButton = CreateNavButton(panelRect, "上一页", "<", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(28f, 0f), ShowPreviousPage);
            nextButton = CreateNavButton(panelRect, "下一页", ">", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-28f, 0f), ShowNextPage);
        }

        private void CreateHeader(RectTransform parent)
        {
            var titleObject = new GameObject("Rulebook Title", typeof(RectTransform), typeof(Text), typeof(Outline));
            titleObject.transform.SetParent(parent, false);

            var titleRect = titleObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 64f);
            titleRect.anchoredPosition = Vector2.zero;

            var title = titleObject.GetComponent<Text>();
            title.text = "规则书";
            title.alignment = TextAnchor.MiddleCenter;
            title.color = UiTheme.GoldText;
            title.fontSize = 34;
            title.fontStyle = FontStyle.Bold;
            title.font = FontUtility.GetCjkFont(title.fontSize);

            var titleOutline = titleObject.GetComponent<Outline>();
            titleOutline.effectColor = UiTheme.DarkShadowLight;
            titleOutline.effectDistance = new Vector2(2f, -2f);

            var closeButton = new GameObject("Close Rulebook Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
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
            var viewportObject = new GameObject("Rulebook Viewport", typeof(RectTransform), typeof(Image), typeof(Mask), typeof(ScrollRect));
            viewportObject.transform.SetParent(parent, false);

            viewportTransform = viewportObject.GetComponent<RectTransform>();
            viewportTransform.anchorMin = Vector2.zero;
            viewportTransform.anchorMax = Vector2.one;
            viewportTransform.offsetMin = new Vector2(96f, 82f);
            viewportTransform.offsetMax = new Vector2(-96f, -74f);

            var viewportImage = viewportObject.GetComponent<Image>();
            viewportImage.color = new Color(0.025f, 0.022f, 0.02f, 0.96f);
            viewportObject.GetComponent<Mask>().showMaskGraphic = true;

            pageTransform = new GameObject("Rulebook Page", typeof(RectTransform), typeof(RawImage)).GetComponent<RectTransform>();
            pageTransform.SetParent(viewportTransform, false);
            pageTransform.anchorMin = new Vector2(0.5f, 0.5f);
            pageTransform.anchorMax = new Vector2(0.5f, 0.5f);
            pageTransform.pivot = new Vector2(0.5f, 0.5f);
            pageTransform.anchoredPosition = Vector2.zero;

            pageImage = pageTransform.GetComponent<RawImage>();
            pageImage.color = Color.white;

            var scrollRect = viewportObject.GetComponent<ScrollRect>();
            scrollRect.horizontal = true;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.inertia = true;
            scrollRect.scrollSensitivity = 0f;
            scrollRect.viewport = viewportTransform;
            scrollRect.content = pageTransform;
        }

        private void CreateFooter(RectTransform parent)
        {
            var labelObject = new GameObject("Rulebook Page Label", typeof(RectTransform), typeof(Text), typeof(Outline));
            labelObject.transform.SetParent(parent, false);

            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0.5f, 0f);
            labelRect.anchorMax = new Vector2(0.5f, 0f);
            labelRect.pivot = new Vector2(0.5f, 0f);
            labelRect.sizeDelta = new Vector2(420f, 44f);
            labelRect.anchoredPosition = new Vector2(0f, 22f);

            pageLabel = labelObject.GetComponent<Text>();
            pageLabel.alignment = TextAnchor.MiddleCenter;
            pageLabel.color = UiTheme.GoldText;
            pageLabel.fontSize = 22;
            pageLabel.fontStyle = FontStyle.Bold;
            pageLabel.font = FontUtility.GetCjkFont(pageLabel.fontSize);

            var outline = labelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.DarkShadowLight;
            outline.effectDistance = new Vector2(1f, -1f);
        }

        private Button CreateNavButton(
            RectTransform parent,
            string name,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 anchoredPosition,
            UnityEngine.Events.UnityAction action)
        {
            var buttonObject = new GameObject(name + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(anchorMin.x, 0.5f);
            rect.sizeDelta = new Vector2(58f, 118f);
            rect.anchoredPosition = anchoredPosition;

            ApplyButtonStyle(buttonObject);
            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(action);
            CreateButtonText(rect, label, 34);
            return button;
        }

        private void ShowPreviousPage()
        {
            ShowPage(pageIndex - 1);
        }

        private void ShowNextPage()
        {
            ShowPage(pageIndex + 1);
        }

        private void ShowPage(int index)
        {
            pageIndex = Mathf.Clamp(index, 0, PageCount - 1);
            var resourcePath = PageResourcePrefix + (pageIndex + 1).ToString("00");
            var texture = Resources.Load<Texture2D>(resourcePath);
            if (texture == null)
            {
                Debug.LogError("Rulebook page not found in Resources: " + resourcePath, this);
                return;
            }

            pageImage.texture = texture;
            zoom = 1f;
            ApplyPageSize(texture);
            UpdateControls();
        }

        private void SetZoom(float value)
        {
            zoom = Mathf.Clamp(value, MinZoom, MaxZoom);
            if (pageImage != null && pageImage.texture != null)
            {
                ApplyPageSize(pageImage.texture);
                UpdateControls();
            }
        }

        private void ApplyPageSize(Texture texture)
        {
            Canvas.ForceUpdateCanvases();
            var viewportSize = viewportTransform.rect.size;
            if (viewportSize.x <= 0f || viewportSize.y <= 0f)
            {
                viewportSize = new Vector2(1400f, 860f);
            }

            var scale = Mathf.Min(viewportSize.x / texture.width, viewportSize.y / texture.height);
            var fittedSize = new Vector2(texture.width * scale, texture.height * scale);
            pageTransform.sizeDelta = fittedSize * zoom;
            pageTransform.anchoredPosition = Vector2.zero;
        }

        private void UpdateControls()
        {
            if (pageLabel != null)
            {
                pageLabel.text = string.Format("{0} / {1}  {2}%", pageIndex + 1, PageCount, Mathf.RoundToInt(zoom * 100f));
            }

            if (previousButton != null)
            {
                previousButton.interactable = pageIndex > 0;
            }

            if (nextButton != null)
            {
                nextButton.interactable = pageIndex < PageCount - 1;
            }
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

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.DarkShadowLight;
            outline.effectDistance = new Vector2(1f, -1f);
        }
    }
}
