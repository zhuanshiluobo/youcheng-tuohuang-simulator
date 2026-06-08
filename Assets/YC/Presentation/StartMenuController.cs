using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuController : MonoBehaviour
    {
        private static readonly Vector2 CoverReferenceSize = new Vector2(5888f, 3312f);
        private static readonly Rect ButtonImageRect = new Rect(2220f, 2528f, 1460f, 323f);

        [SerializeField] private string mapSceneName = "SampleScene";
        [SerializeField] private Texture2D coverTexture;

        private void Awake()
        {
            BuildMenu();
        }

        public void StartGame()
        {
            SceneManager.LoadScene(mapSceneName);
        }

        private void BuildMenu()
        {
            EnsureEventSystem();

            var canvasObject = new GameObject("Start Menu Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var coverFrame = CreateCoverFrame(canvasObject.GetComponent<RectTransform>());
            CreateCover(coverFrame);
            CreateStartButton(coverFrame);
        }

        private static RectTransform CreateCoverFrame(RectTransform canvasTransform)
        {
            Canvas.ForceUpdateCanvases();

            var coverFrameObject = new GameObject("Cover Frame", typeof(RectTransform));
            coverFrameObject.transform.SetParent(canvasTransform, false);

            var coverFrame = coverFrameObject.GetComponent<RectTransform>();
            coverFrame.anchorMin = new Vector2(0.5f, 0.5f);
            coverFrame.anchorMax = new Vector2(0.5f, 0.5f);
            coverFrame.pivot = new Vector2(0.5f, 0.5f);

            var canvasSize = canvasTransform.rect.size;
            var frameWidth = canvasSize.x;
            var frameHeight = frameWidth * CoverReferenceSize.y / CoverReferenceSize.x;
            if (frameHeight > canvasSize.y)
            {
                frameHeight = canvasSize.y;
                frameWidth = frameHeight * CoverReferenceSize.x / CoverReferenceSize.y;
            }

            if (frameWidth <= 0f || frameHeight <= 0f)
            {
                frameWidth = 1920f;
                frameHeight = 1080f;
            }

            coverFrame.sizeDelta = new Vector2(frameWidth, frameHeight);
            coverFrame.anchoredPosition = Vector2.zero;
            return coverFrame;
        }

        private void CreateCover(Transform parent)
        {
            var coverObject = new GameObject("Rulebook Cover", typeof(RectTransform), typeof(RawImage));
            coverObject.transform.SetParent(parent, false);

            var rectTransform = coverObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            var image = coverObject.GetComponent<RawImage>();
            image.texture = coverTexture;
            image.color = Color.white;
        }

        private void CreateStartButton(Transform parent)
        {
            var buttonObject = new GameObject("Start Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rectTransform = buttonObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);

            var parentTransform = (RectTransform)parent;
            var frameSize = parentTransform.sizeDelta.x > 0f && parentTransform.sizeDelta.y > 0f
                ? parentTransform.sizeDelta
                : new Vector2(1920f, 1080f);
            var scaleX = frameSize.x / CoverReferenceSize.x;
            var scaleY = frameSize.y / CoverReferenceSize.y;

            rectTransform.sizeDelta = new Vector2(ButtonImageRect.width * scaleX, ButtonImageRect.height * scaleY);
            rectTransform.anchoredPosition = new Vector2(
                (ButtonImageRect.center.x - CoverReferenceSize.x * 0.5f) * scaleX,
                (CoverReferenceSize.y * 0.5f - ButtonImageRect.center.y) * scaleY);

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.16f, 0.1f, 0.055f, 0.96f);

            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(StartGame);

            var buttonOutline = buttonObject.GetComponent<Outline>();
            buttonOutline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);
            buttonOutline.effectDistance = new Vector2(4f, -4f);

            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(Outline));
            textObject.transform.SetParent(buttonObject.transform, false);

            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.GetComponent<Text>();
            text.text = "开始游戏";
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            text.fontSize = Mathf.RoundToInt(140f * scaleY);
            text.fontStyle = FontStyle.Bold;
            text.font = Font.CreateDynamicFontFromOSFont(new[] { "SimHei", "Microsoft YaHei", "Arial" }, text.fontSize);

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.98f);
            outline.effectDistance = new Vector2(4f, -4f);
        }

        private static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }
    }
}
