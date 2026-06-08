using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuController : MonoBehaviour
    {
        [SerializeField] private string mapSceneName = "SampleScene";
        [SerializeField] private Sprite coverSprite;

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

            CreateCover(canvasObject.transform);
            CreateStartButton(canvasObject.transform);
        }

        private void CreateCover(Transform parent)
        {
            var coverObject = new GameObject("Rulebook Cover", typeof(RectTransform), typeof(Image));
            coverObject.transform.SetParent(parent, false);

            var rectTransform = coverObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            var image = coverObject.GetComponent<Image>();
            image.sprite = coverSprite;
            image.color = Color.white;
            image.preserveAspect = true;
        }

        private void CreateStartButton(Transform parent)
        {
            var buttonObject = new GameObject("Start Button", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            var rectTransform = buttonObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0f);
            rectTransform.anchorMax = new Vector2(0.5f, 0f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = new Vector2(0f, 245f);
            rectTransform.sizeDelta = new Vector2(640f, 140f);

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.12f, 0.08f, 0.05f, 0.18f);

            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(StartGame);

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
            text.fontSize = 92;
            text.fontStyle = FontStyle.Bold;
            text.font = Font.CreateDynamicFontFromOSFont(new[] { "SimHei", "Microsoft YaHei", "Arial" }, text.fontSize);

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.12f, 0.08f, 0.04f, 0.95f);
            outline.effectDistance = new Vector2(5f, -5f);
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
