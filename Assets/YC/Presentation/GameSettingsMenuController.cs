using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Application.Sessions;

namespace YC.Presentation
{
    public sealed class GameSettingsMenuController : MonoBehaviour
    {
        private const float PanelWidth = 640f;
        private const float PanelHeight = 430f;
        private const float AnimationSpeed = 14f;
        private const float CornerButtonSize = 64f;
        private const float CornerButtonGap = CornerButtonSize * 0.2f;
        private const string DefaultStartSceneName = "StartScene";

        [SerializeField] private string startSceneName = DefaultStartSceneName;
        [SerializeField] private bool showReturnToStartButton = true;

        private RectTransform canvasTransform;
        private RectTransform menuPanel;
        private GameObject overlayObject;
        private GameObject confirmationObject;
        private GameObject returnButtonObject;
        private GameObject actionLogButtonObject;
        private ActionLogViewerController actionLogViewer;
        private bool isOpen;
        private bool isAnimating;
        private Vector2 targetPosition;

        public bool IsOpen
        {
            get { return isOpen; }
        }

        private void Awake()
        {
            BuildUi();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HandleEscapePressed();
            }

            if (!isAnimating || menuPanel == null)
            {
                return;
            }

            menuPanel.anchoredPosition = Vector2.Lerp(
                menuPanel.anchoredPosition,
                targetPosition,
                Time.unscaledDeltaTime * AnimationSpeed);

            if (Vector2.Distance(menuPanel.anchoredPosition, targetPosition) > 0.5f)
            {
                return;
            }

            menuPanel.anchoredPosition = targetPosition;
            isAnimating = false;

            if (!isOpen && overlayObject != null)
            {
                overlayObject.SetActive(false);
            }
        }

        public void HandleEscapePressed()
        {
            if (MobileCityInteractionController.WasBuildEscapeConsumedThisFrame() ||
                CityStyleDeclarationPreviewInputHandler.WasEscapeConsumedThisFrame() ||
                CityStyleDeclarationPreviewInputHandler.HasOpenDialog() ||
                ZoomableImageViewerController.WasEscapeConsumedThisFrame() ||
                ZoomableImageViewerController.HasOpenViewer())
            {
                return;
            }

            var cityController = FindObjectOfType<MobileCityInteractionController>();
            if (cityController != null && cityController.TryHandleBuildFacilityEscape())
            {
                return;
            }

            if (confirmationObject != null && confirmationObject.activeSelf)
            {
                HideConfirmation();
                return;
            }

            if (isOpen)
            {
                Close();
                return;
            }

            Open();
        }

        public void Open()
        {
            if (overlayObject == null || menuPanel == null)
            {
                return;
            }

            isOpen = true;
            isAnimating = true;
            confirmationObject.SetActive(false);
            overlayObject.SetActive(true);
            menuPanel.anchoredPosition = new Vector2(0f, GetHiddenPanelY());
            targetPosition = Vector2.zero;
        }

        public void Close()
        {
            if (menuPanel == null)
            {
                return;
            }

            isOpen = false;
            isAnimating = true;
            confirmationObject.SetActive(false);
            targetPosition = new Vector2(0f, GetHiddenPanelY());
        }

        public void SetReturnToStartButtonVisible(bool visible)
        {
            showReturnToStartButton = visible;

            if (returnButtonObject != null)
            {
                returnButtonObject.SetActive(visible);
            }

            if (!visible && confirmationObject != null)
            {
                confirmationObject.SetActive(false);
            }
        }

        public void ConfigureActionLog(GameSession session)
        {
            if (session == null)
            {
                return;
            }

            if (actionLogButtonObject == null)
            {
                BuildActionLogButton(canvasTransform);
            }

            if (actionLogViewer == null)
            {
                var viewerObject = new GameObject("ActionLogViewer");
                viewerObject.transform.SetParent(transform, false);
                actionLogViewer = viewerObject.AddComponent<ActionLogViewerController>();
            }

            actionLogViewer.Configure(() => session.State);
            actionLogButtonObject.SetActive(true);
        }

        public static GameSettingsMenuController EnsureInScene(
            Transform parent,
            bool showReturnToStartButton = true,
            bool showHintCardButton = true)
        {
            _ = showHintCardButton;
            var existing = FindObjectOfType<GameSettingsMenuController>();
            if (existing != null)
            {
                existing.SetReturnToStartButtonVisible(showReturnToStartButton);
                return existing;
            }

            var go = new GameObject("GameSettingsMenu");
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var controller = go.AddComponent<GameSettingsMenuController>();
            controller.SetReturnToStartButtonVisible(showReturnToStartButton);
            return controller;
        }

        private void BuildUi()
        {
            UguiUtility.EnsureEventSystem();

            var canvas = UguiUtility.CreateCanvas("Settings Menu Canvas", 120, transform);
            canvasTransform = canvas.GetComponent<RectTransform>();

            BuildGearButton(canvasTransform);
            BuildOverlay(canvasTransform);
        }

        private void BuildGearButton(RectTransform parent)
        {
            var buttonObject = new GameObject("Settings Gear Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(64f, 64f);
            rect.anchoredPosition = new Vector2(-34f, -34f);

            buttonObject.GetComponent<Image>().color = UiTheme.PanelBackgroundLighter;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(2f, -2f);

            buttonObject.GetComponent<Button>().onClick.AddListener(Open);

            var iconObject = new GameObject("Gear Icon", typeof(RectTransform), typeof(Image));
            iconObject.transform.SetParent(buttonObject.transform, false);

            var iconRect = iconObject.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(42f, 42f);
            iconRect.anchoredPosition = Vector2.zero;

            var iconImage = iconObject.GetComponent<Image>();
            iconImage.sprite = CreateGearSprite();
            iconImage.color = UiTheme.GoldText;
        }

        private void BuildActionLogButton(RectTransform parent)
        {
            actionLogButtonObject = new GameObject("Action Log Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            actionLogButtonObject.transform.SetParent(parent, false);

            var rect = actionLogButtonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(CornerButtonSize, CornerButtonSize);
            rect.anchoredPosition = new Vector2(-34f - CornerButtonSize - CornerButtonGap, -34f);

            actionLogButtonObject.GetComponent<Image>().color = UiTheme.PanelBackgroundLighter;
            var outline = actionLogButtonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(2f, -2f);
            actionLogButtonObject.GetComponent<Button>().onClick.AddListener(() => actionLogViewer.Open());

            var textObject = new GameObject("Action Log Icon", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(actionLogButtonObject.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var text = textObject.GetComponent<Text>();
            text.text = "\u65e5\u5fd7";
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = 18;
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(18);
        }

        private void BuildOverlay(RectTransform parent)
        {
            overlayObject = new GameObject("Settings Overlay", typeof(RectTransform), typeof(Image), typeof(Button));
            overlayObject.transform.SetParent(parent, false);
            overlayObject.SetActive(false);

            var overlayRect = overlayObject.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            overlayObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.38f);
            overlayObject.GetComponent<Button>().onClick.AddListener(Close);

            BuildMenuPanel(overlayRect);
            BuildConfirmation(menuPanel);
        }

        private void BuildMenuPanel(RectTransform parent)
        {
            var panelObject = new GameObject("Settings Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(parent, false);

            menuPanel = panelObject.GetComponent<RectTransform>();
            menuPanel.anchorMin = new Vector2(0.5f, 0.5f);
            menuPanel.anchorMax = new Vector2(0.5f, 0.5f);
            menuPanel.pivot = new Vector2(0.5f, 0.5f);
            menuPanel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            menuPanel.anchoredPosition = new Vector2(0f, GetHiddenPanelY());

            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            BuildHeader(menuPanel);
            BuildBody(menuPanel);
            BuildReturnButton(menuPanel);
        }

        private void BuildHeader(RectTransform parent)
        {
            var titleObject = new GameObject("Settings Title", typeof(RectTransform), typeof(Text), typeof(Outline));
            titleObject.transform.SetParent(parent, false);

            var titleRect = titleObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 64f);
            titleRect.anchoredPosition = Vector2.zero;

            var title = titleObject.GetComponent<Text>();
            title.text = "设置";
            title.alignment = TextAnchor.MiddleCenter;
            title.color = UiTheme.GoldText;
            title.fontSize = 34;
            title.fontStyle = FontStyle.Bold;
            title.font = FontUtility.GetCjkFont(title.fontSize);

            var titleOutline = titleObject.GetComponent<Outline>();
            titleOutline.effectColor = UiTheme.DarkShadowLight;
            titleOutline.effectDistance = new Vector2(2f, -2f);

            var separatorObject = new GameObject("Header Separator", typeof(RectTransform), typeof(Image));
            separatorObject.transform.SetParent(parent, false);

            var separatorRect = separatorObject.GetComponent<RectTransform>();
            separatorRect.anchorMin = new Vector2(0f, 1f);
            separatorRect.anchorMax = new Vector2(1f, 1f);
            separatorRect.pivot = new Vector2(0.5f, 1f);
            separatorRect.sizeDelta = new Vector2(0f, 2f);
            separatorRect.anchoredPosition = new Vector2(0f, -64f);

            separatorObject.GetComponent<Image>().color = UiTheme.GoldSeparator;

            CreateHeaderCloseButton(parent);
        }

        private void BuildBody(RectTransform parent)
        {
            var bodyObject = new GameObject("Settings Body", typeof(RectTransform), typeof(Image));
            bodyObject.transform.SetParent(parent, false);

            var bodyRect = bodyObject.GetComponent<RectTransform>();
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = Vector2.one;
            bodyRect.offsetMin = new Vector2(28f, 84f);
            bodyRect.offsetMax = new Vector2(-28f, -76f);

            bodyObject.GetComponent<Image>().color = UiTheme.ScrollBackground;

            CreateBodyButton(bodyRect, "规则书", new Vector2(184f, 52f), new Vector2(24f, -24f), OpenRulebook);
        }

        private void BuildReturnButton(RectTransform parent)
        {
            var button = CreatePanelButton(parent, "返回主菜单", new Vector2(174f, 48f), new Vector2(-28f, 20f), ShowConfirmation);
            returnButtonObject = button.gameObject;
            returnButtonObject.SetActive(showReturnToStartButton);
        }

        private void CreateHeaderCloseButton(RectTransform parent)
        {
            var buttonObject = new GameObject("Close Settings Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(46f, 36f);
            rect.anchoredPosition = new Vector2(-14f, -14f);

            ApplyButtonStyle(buttonObject);
            buttonObject.GetComponent<Button>().onClick.AddListener(Close);
            CreateButtonText(rect, "×", 24);
        }

        private void BuildConfirmation(RectTransform parent)
        {
            confirmationObject = new GameObject("Return Confirmation", typeof(RectTransform), typeof(Image));
            confirmationObject.transform.SetParent(parent, false);
            confirmationObject.SetActive(false);

            var overlayRect = confirmationObject.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;
            confirmationObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.42f);

            var dialogObject = new GameObject("Return Confirmation Dialog", typeof(RectTransform), typeof(Image), typeof(Outline));
            dialogObject.transform.SetParent(overlayRect, false);

            var dialogRect = dialogObject.GetComponent<RectTransform>();
            dialogRect.anchorMin = new Vector2(0.5f, 0.5f);
            dialogRect.anchorMax = new Vector2(0.5f, 0.5f);
            dialogRect.pivot = new Vector2(0.5f, 0.5f);
            dialogRect.sizeDelta = new Vector2(480f, 220f);
            dialogRect.anchoredPosition = Vector2.zero;

            dialogObject.GetComponent<Image>().color = UiTheme.PanelBackgroundLighter;
            var outline = dialogObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            CreateDialogText(dialogRect, "确认返回主菜单？", 28, new Vector2(0f, 50f), FontStyle.Bold);
            CreateDialogText(dialogRect, "当前对局进度不会自动保存。", 17, new Vector2(0f, 10f), FontStyle.Normal);
            CreateDialogButton(dialogRect, "确认返回", new Vector2(-104f, -62f), ReturnToStartScene);
            CreateDialogButton(dialogRect, "取消", new Vector2(104f, -62f), HideConfirmation);
        }

        private Button CreatePanelButton(
            RectTransform parent,
            string label,
            Vector2 size,
            Vector2 anchoredPosition,
            UnityEngine.Events.UnityAction action)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            ApplyButtonStyle(buttonObject);
            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(action);

            CreateButtonText(rect, label, 20);
            return button;
        }

        private static Button CreateBodyButton(
            RectTransform parent,
            string label,
            Vector2 size,
            Vector2 anchoredPosition,
            UnityEngine.Events.UnityAction action)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            ApplyButtonStyle(buttonObject);
            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(action);

            CreateButtonText(rect, label, 22);
            return button;
        }

        private static void CreateDialogButton(RectTransform parent, string label, Vector2 position, UnityEngine.Events.UnityAction action)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(154f, 42f);
            rect.anchoredPosition = position;

            ApplyButtonStyle(buttonObject);
            buttonObject.GetComponent<Button>().onClick.AddListener(action);
            CreateButtonText(rect, label, 20);
        }

        private static Text CreateDialogText(RectTransform parent, string content, int fontSize, Vector2 position, FontStyle style)
        {
            var textObject = new GameObject(content + " Text", typeof(RectTransform), typeof(Text), typeof(Outline));
            textObject.transform.SetParent(parent, false);

            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(450f, 40f);
            rect.anchoredPosition = position;

            var text = textObject.GetComponent<Text>();
            text.text = content;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.font = FontUtility.GetCjkFont(fontSize);

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.DarkShadowLight;
            outline.effectDistance = new Vector2(1f, -1f);
            return text;
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

        private void ShowConfirmation()
        {
            confirmationObject.SetActive(true);
        }

        private void HideConfirmation()
        {
            confirmationObject.SetActive(false);
        }

        private void OpenRulebook()
        {
            var viewer = FindObjectOfType<RulebookViewerController>();
            if (viewer == null)
            {
                var viewerObject = new GameObject("RulebookViewer");
                viewerObject.transform.SetParent(transform, false);
                viewer = viewerObject.AddComponent<RulebookViewerController>();
            }

            viewer.Open();
        }

        private void ReturnToStartScene()
        {
            GameLaunchContext.ShutdownOnlineSession();
            SceneManager.LoadScene(string.IsNullOrEmpty(startSceneName) ? DefaultStartSceneName : startSceneName);
        }

        private float GetHiddenPanelY()
        {
            var canvasHeight = canvasTransform == null || canvasTransform.rect.height <= 0f
                ? 1080f
                : canvasTransform.rect.height;
            return canvasHeight * 0.5f + PanelHeight * 0.5f + 48f;
        }

        private static Sprite CreateGearSprite()
        {
            const int size = 96;
            const float innerRadius = 17f;
            const float ringRadius = 28f;
            const float toothRadius = 40f;
            const int teeth = 8;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            var center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var offset = new Vector2(x, y) - center;
                    var distance = offset.magnitude;
                    var angle = Mathf.Atan2(offset.y, offset.x);
                    if (angle < 0f)
                    {
                        angle += Mathf.PI * 2f;
                    }

                    var toothStep = Mathf.PI * 2f / teeth;
                    var toothCenterDistance = Mathf.Abs(Mathf.DeltaAngle(angle * Mathf.Rad2Deg, Mathf.Round(angle / toothStep) * toothStep * Mathf.Rad2Deg));
                    var hasTooth = toothCenterDistance <= 11f;
                    var outerRadius = hasTooth ? toothRadius : ringRadius;
                    var alpha = distance >= innerRadius && distance <= outerRadius ? 1f : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }

    }
}
