using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class WindowCloseInputHandler : MonoBehaviour
    {
        private readonly List<RaycastResult> raycastResults = new List<RaycastResult>();
        private UnityAction closeAction;
        private bool closeRequested;

        public void Configure(UnityAction configuredCloseAction)
        {
            closeAction = configuredCloseAction;
            closeRequested = false;
        }

        public void RequestClose()
        {
            if (closeRequested)
            {
                return;
            }

            closeRequested = true;
            closeAction?.Invoke();
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(1) && IsPointerOverThisWindow())
            {
                RequestClose();
            }
        }

        private bool IsPointerOverThisWindow()
        {
            if (EventSystem.current == null)
            {
                return true;
            }

            raycastResults.Clear();
            EventSystem.current.RaycastAll(
                new PointerEventData(EventSystem.current) { position = Input.mousePosition },
                raycastResults);
            for (var i = 0; i < raycastResults.Count; i++)
            {
                var hit = raycastResults[i].gameObject;
                if (hit == null)
                {
                    continue;
                }

                var hitTransform = hit.transform;
                return hitTransform == transform || hitTransform.IsChildOf(transform);
            }

            return false;
        }
    }

    public static class UguiUtility
    {
        private static Sprite triangleUpSprite;
        private static Sprite triangleDownSprite;

        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null || Object.FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        public static Canvas CreateCanvas(string name, int sortingOrder, Transform parent = null)
        {
            var canvasObject = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            if (parent != null)
            {
                canvasObject.transform.SetParent(parent, false);
            }

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            return canvas;
        }

        public static Button CreateViewerCloseButton(
            RectTransform parent,
            string name,
            UnityEngine.Events.UnityAction closeAction)
        {
            var buttonObject = new GameObject(
                name,
                typeof(RectTransform),
                typeof(Image),
                typeof(Button),
                typeof(Outline));
            buttonObject.transform.SetParent(parent, false);
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(42f, 42f);
            rect.anchoredPosition = new Vector2(-18f, -12f);
            ApplyViewerButtonStyle(buttonObject);
            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(closeAction);
            CreateViewerButtonText(rect, "×", 30);
            return button;
        }

        public static Button CreateWindowCloseControls(
            GameObject inputOwner,
            RectTransform panel,
            string closeButtonName,
            UnityAction closeAction)
        {
            UnityAction requestClose = closeAction;
            if (inputOwner != null)
            {
                var inputHandler = inputOwner.GetComponent<WindowCloseInputHandler>() ??
                                   inputOwner.AddComponent<WindowCloseInputHandler>();
                inputHandler.Configure(closeAction);
                requestClose = inputHandler.RequestClose;
            }

            var closeButton = CreateViewerCloseButton(panel, closeButtonName, requestClose);
            if (closeButton != null)
            {
                closeButton.transform.SetAsLastSibling();
            }

            return closeButton;
        }

        public static Image CreateTriangleIcon(
            RectTransform parent,
            string name,
            bool pointsUp)
        {
            if (parent == null)
            {
                return null;
            }

            var iconObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            iconObject.transform.SetParent(parent, false);
            var rect = iconObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(16f, 16f);
            rect.anchoredPosition = new Vector2(18f, 0f);

            var image = iconObject.GetComponent<Image>();
            image.sprite = GetTriangleSprite(pointsUp);
            image.color = UiTheme.GoldText;
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        public static void SetTriangleIconDirection(Image icon, bool pointsUp)
        {
            if (icon != null)
            {
                icon.sprite = GetTriangleSprite(pointsUp);
            }
        }

        public static Sprite GetTriangleSprite(bool pointsUp)
        {
            if (pointsUp)
            {
                if (triangleUpSprite == null)
                {
                    triangleUpSprite = CreateTriangleSprite(true);
                }

                return triangleUpSprite;
            }

            if (triangleDownSprite == null)
            {
                triangleDownSprite = CreateTriangleSprite(false);
            }

            return triangleDownSprite;
        }

        private static void ApplyViewerButtonStyle(GameObject buttonObject)
        {
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(2f, -2f);
        }

        private static void CreateViewerButtonText(RectTransform parent, string content, int fontSize)
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

        private static Sprite CreateTriangleSprite(bool pointsUp)
        {
            const int size = 32;
            const float padding = 5f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.name = pointsUp ? "UI Triangle Up Texture" : "UI Triangle Down Texture";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            var center = (size - 1) * 0.5f;
            var bottom = padding;
            var top = size - 1 - padding;
            var height = top - bottom;
            var maximumHalfWidth = center - padding;
            for (var y = 0; y < size; y++)
            {
                var verticalProgress = (y - bottom) / height;
                var withinHeight = verticalProgress >= 0f && verticalProgress <= 1f;
                var halfWidth = pointsUp
                    ? maximumHalfWidth * (1f - verticalProgress)
                    : maximumHalfWidth * verticalProgress;
                for (var x = 0; x < size; x++)
                {
                    var inside = withinHeight && Mathf.Abs(x - center) <= halfWidth + 0.5f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, inside ? 1f : 0f));
                }
            }

            texture.Apply(false, true);
            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                size);
            sprite.name = pointsUp ? "UI Triangle Up" : "UI Triangle Down";
            return sprite;
        }

        public static Sprite CreateCircleSprite(int size, float radius, float thickness)
        {
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

        public static Sprite CreateFilledSquareSprite(int size, float sideLength)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;

            var center = (size - 1) * 0.5f;
            var halfSide = sideLength * 0.5f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var alpha = Mathf.Abs(x - center) <= halfSide &&
                                Mathf.Abs(y - center) <= halfSide
                        ? 1f
                        : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }

        public static Sprite CreateSquareOutlineSprite(int size, float sideLength, float thickness)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;

            var center = (size - 1) * 0.5f;
            var outerHalfSide = sideLength * 0.5f;
            var innerHalfSide = Mathf.Max(0f, outerHalfSide - thickness);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = Mathf.Abs(x - center);
                    var dy = Mathf.Abs(y - center);
                    var insideOuter = dx <= outerHalfSide && dy <= outerHalfSide;
                    var insideInner = dx <= innerHalfSide && dy <= innerHalfSide;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, insideOuter && !insideInner ? 1f : 0f));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }
    }
}
