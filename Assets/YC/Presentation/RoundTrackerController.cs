using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class RoundTrackerController : MonoBehaviour
    {
        private const int FirstRoundIndex = 1;
        private const int FinalIndex = 9;

        private static readonly string[] RoundLabels =
        {
            "START", "1", "2", "3", "4", "5", "6", "7", "8", "FINAL"
        };

        [SerializeField] private string startSceneName = "StartScene";

        private RectTransform markerTransform;
        private RectTransform trackSlotsTransform;
        private Button endRoundButton;
        private int currentIndex = FirstRoundIndex;

        private void Awake()
        {
            BuildRoundUi();
            MoveMarkerToCurrentIndex();
        }

        public void EndCurrentRound()
        {
            if (currentIndex >= FinalIndex)
            {
                ShowGameOverDialog();
                return;
            }

            currentIndex++;
            MoveMarkerToCurrentIndex();

            if (currentIndex >= FinalIndex)
            {
                endRoundButton.interactable = false;
                ShowGameOverDialog();
                return;
            }

            var mobileCityInteraction = FindObjectOfType<MobileCityInteractionController>();
            if (mobileCityInteraction != null)
            {
                mobileCityInteraction.BeginNextRound();
            }
        }

        public void ReturnToStartScene()
        {
            SceneManager.LoadScene(startSceneName);
        }

        private void BuildRoundUi()
        {
            EnsureEventSystem();

            var canvasObject = new GameObject("Round UI Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var canvasTransform = canvasObject.GetComponent<RectTransform>();
            CreateRoundTrack(canvasTransform);
            CreateEndRoundButton(canvasTransform);
        }

        private void CreateRoundTrack(RectTransform parent)
        {
            var trackObject = new GameObject("Round Track", typeof(RectTransform), typeof(Image), typeof(Outline));
            trackObject.transform.SetParent(parent, false);

            var trackTransform = trackObject.GetComponent<RectTransform>();
            trackTransform.anchorMin = new Vector2(0.5f, 0f);
            trackTransform.anchorMax = new Vector2(0.5f, 0f);
            trackTransform.pivot = new Vector2(0.5f, 0f);
            trackTransform.sizeDelta = new Vector2(720f, 84f);
            trackTransform.anchoredPosition = new Vector2(0f, 30f);

            var background = trackObject.GetComponent<Image>();
            background.color = new Color(0.1f, 0.1f, 0.09f, 0.88f);

            var outline = trackObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.95f);
            outline.effectDistance = new Vector2(2f, -2f);

            CreateTrackBand(trackTransform, "Start Band", 0, 3, new Color(0.82f, 0.78f, 0.67f, 0.95f));
            CreateTrackBand(trackTransform, "Danger Band", 4, FinalIndex, new Color(0.56f, 0.08f, 0.06f, 0.95f));

            trackSlotsTransform = new GameObject("Round Slots", typeof(RectTransform)).GetComponent<RectTransform>();
            trackSlotsTransform.SetParent(trackTransform, false);
            trackSlotsTransform.anchorMin = new Vector2(0.5f, 0.5f);
            trackSlotsTransform.anchorMax = new Vector2(0.5f, 0.5f);
            trackSlotsTransform.pivot = new Vector2(0.5f, 0.5f);
            trackSlotsTransform.sizeDelta = new Vector2(650f, 54f);
            trackSlotsTransform.anchoredPosition = new Vector2(0f, 4f);

            for (var i = 0; i < RoundLabels.Length; i++)
            {
                CreateRoundLabel(trackSlotsTransform, i);
            }

            markerTransform = new GameObject("Round Marker", typeof(RectTransform), typeof(Image), typeof(Outline)).GetComponent<RectTransform>();
            markerTransform.SetParent(trackSlotsTransform, false);
            markerTransform.anchorMin = new Vector2(0.5f, 0.5f);
            markerTransform.anchorMax = new Vector2(0.5f, 0.5f);
            markerTransform.pivot = new Vector2(0.5f, 0.5f);
            markerTransform.sizeDelta = new Vector2(32f, 32f);

            var markerImage = markerTransform.GetComponent<Image>();
            markerImage.sprite = CreateCircleSprite();
            markerImage.color = Color.white;

            var markerOutline = markerTransform.GetComponent<Outline>();
            markerOutline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.9f);
            markerOutline.effectDistance = new Vector2(2f, -2f);
        }

        private static void CreateTrackBand(RectTransform parent, string name, int fromIndex, int toIndex, Color color)
        {
            var bandObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            bandObject.transform.SetParent(parent, false);

            var band = bandObject.GetComponent<RectTransform>();
            band.anchorMin = new Vector2(0.5f, 0.5f);
            band.anchorMax = new Vector2(0.5f, 0.5f);
            band.pivot = new Vector2(0.5f, 0.5f);

            var slotWidth = 65f;
            var width = (toIndex - fromIndex + 1) * slotWidth;
            var centerIndex = (fromIndex + toIndex) * 0.5f;
            band.sizeDelta = new Vector2(width, 36f);
            band.anchoredPosition = new Vector2((centerIndex - (RoundLabels.Length - 1) * 0.5f) * slotWidth, 0f);

            bandObject.GetComponent<Image>().color = color;
        }

        private static void CreateRoundLabel(RectTransform parent, int index)
        {
            var labelObject = new GameObject("Round Label " + RoundLabels[index], typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(parent, false);

            var labelTransform = labelObject.GetComponent<RectTransform>();
            labelTransform.anchorMin = new Vector2(0.5f, 0.5f);
            labelTransform.anchorMax = new Vector2(0.5f, 0.5f);
            labelTransform.pivot = new Vector2(0.5f, 0.5f);
            labelTransform.sizeDelta = index == 0 || index == FinalIndex
                ? new Vector2(86f, 42f)
                : new Vector2(48f, 42f);
            labelTransform.anchoredPosition = new Vector2(GetSlotX(index), 0f);

            var label = labelObject.GetComponent<Text>();
            label.text = RoundLabels[index];
            label.alignment = TextAnchor.MiddleCenter;
            label.color = index >= 4 ? Color.white : Color.black;
            label.fontSize = index == 0 || index == FinalIndex ? 22 : 28;
            label.fontStyle = FontStyle.Bold;
            label.font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Microsoft YaHei", "SimHei" }, label.fontSize);
        }

        private void CreateEndRoundButton(RectTransform parent)
        {
            var buttonObject = new GameObject("End Round Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var buttonTransform = buttonObject.GetComponent<RectTransform>();
            buttonTransform.anchorMin = new Vector2(1f, 0f);
            buttonTransform.anchorMax = new Vector2(1f, 0f);
            buttonTransform.pivot = new Vector2(1f, 0f);
            buttonTransform.sizeDelta = new Vector2(270f, 76f);
            buttonTransform.anchoredPosition = new Vector2(-42f, 42f);

            ApplyButtonStyle(buttonObject);

            endRoundButton = buttonObject.GetComponent<Button>();
            endRoundButton.onClick.AddListener(EndCurrentRound);

            CreateButtonText(buttonTransform, "结束本回合", 34);
        }

        private void ShowGameOverDialog()
        {
            var canvasTransform = markerTransform.GetComponentInParent<Canvas>().GetComponent<RectTransform>();
            var overlayObject = new GameObject("Game Over Overlay", typeof(RectTransform), typeof(Image));
            overlayObject.transform.SetParent(canvasTransform, false);

            var overlayTransform = overlayObject.GetComponent<RectTransform>();
            overlayTransform.anchorMin = Vector2.zero;
            overlayTransform.anchorMax = Vector2.one;
            overlayTransform.offsetMin = Vector2.zero;
            overlayTransform.offsetMax = Vector2.zero;

            var overlayImage = overlayObject.GetComponent<Image>();
            overlayImage.color = new Color(0f, 0f, 0f, 0.62f);

            var dialogObject = new GameObject("Game Over Dialog", typeof(RectTransform), typeof(Image), typeof(Outline));
            dialogObject.transform.SetParent(overlayTransform, false);

            var dialogTransform = dialogObject.GetComponent<RectTransform>();
            dialogTransform.anchorMin = new Vector2(0.5f, 0.5f);
            dialogTransform.anchorMax = new Vector2(0.5f, 0.5f);
            dialogTransform.pivot = new Vector2(0.5f, 0.5f);
            dialogTransform.sizeDelta = new Vector2(620f, 300f);
            dialogTransform.anchoredPosition = Vector2.zero;

            var dialogImage = dialogObject.GetComponent<Image>();
            dialogImage.color = new Color(0.16f, 0.1f, 0.055f, 0.98f);

            var dialogOutline = dialogObject.GetComponent<Outline>();
            dialogOutline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.95f);
            dialogOutline.effectDistance = new Vector2(4f, -4f);

            var titleTransform = new GameObject("Game Over Text", typeof(RectTransform), typeof(Text), typeof(Outline)).GetComponent<RectTransform>();
            titleTransform.SetParent(dialogTransform, false);
            titleTransform.anchorMin = new Vector2(0f, 0.48f);
            titleTransform.anchorMax = new Vector2(1f, 1f);
            titleTransform.offsetMin = new Vector2(28f, 0f);
            titleTransform.offsetMax = new Vector2(-28f, -18f);

            var titleText = titleTransform.GetComponent<Text>();
            titleText.text = "游戏结束";
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            titleText.fontSize = 58;
            titleText.fontStyle = FontStyle.Bold;
            titleText.font = Font.CreateDynamicFontFromOSFont(new[] { "SimHei", "Microsoft YaHei", "Arial" }, titleText.fontSize);

            var titleOutline = titleTransform.GetComponent<Outline>();
            titleOutline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.98f);
            titleOutline.effectDistance = new Vector2(3f, -3f);

            var returnButtonObject = new GameObject("Return Start Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            returnButtonObject.transform.SetParent(dialogTransform, false);

            var returnButtonTransform = returnButtonObject.GetComponent<RectTransform>();
            returnButtonTransform.anchorMin = new Vector2(0.5f, 0f);
            returnButtonTransform.anchorMax = new Vector2(0.5f, 0f);
            returnButtonTransform.pivot = new Vector2(0.5f, 0f);
            returnButtonTransform.sizeDelta = new Vector2(430f, 86f);
            returnButtonTransform.anchoredPosition = new Vector2(0f, 42f);

            ApplyButtonStyle(returnButtonObject);
            returnButtonObject.GetComponent<Button>().onClick.AddListener(ReturnToStartScene);
            CreateButtonText(returnButtonTransform, "点击返回开始页面", 34);
        }

        private static void ApplyButtonStyle(GameObject buttonObject)
        {
            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.16f, 0.1f, 0.055f, 0.96f);

            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);
            outline.effectDistance = new Vector2(4f, -4f);
        }

        private static void CreateButtonText(RectTransform parent, string content, int fontSize)
        {
            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(Outline));
            textObject.transform.SetParent(parent, false);

            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.GetComponent<Text>();
            text.text = content;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.font = Font.CreateDynamicFontFromOSFont(new[] { "SimHei", "Microsoft YaHei", "Arial" }, text.fontSize);

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.98f);
            outline.effectDistance = new Vector2(3f, -3f);
        }

        private void MoveMarkerToCurrentIndex()
        {
            if (markerTransform == null)
            {
                return;
            }

            markerTransform.anchoredPosition = new Vector2(GetSlotX(currentIndex), 33f);
        }

        private static float GetSlotX(int index)
        {
            return (index - (RoundLabels.Length - 1) * 0.5f) * 65f;
        }

        private static Sprite CreateCircleSprite()
        {
            const int size = 64;
            const float radius = 27f;
            const float thickness = 7f;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            var center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), center);
                    var alpha = distance <= radius && distance >= radius - thickness ? 1f : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
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
