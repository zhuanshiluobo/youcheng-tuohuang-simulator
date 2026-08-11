using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace YC.Presentation
{
    public static class UguiUtility
    {
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
            scaler.referenceResolution = UiTheme.CanvasReferenceResolution;
            scaler.matchWidthOrHeight = UiTheme.CanvasMatchWidthOrHeight;

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
            rect.sizeDelta = UiTheme.ViewerCloseButtonSize;
            rect.anchoredPosition = UiTheme.ViewerCloseButtonOffset;
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
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = fontSize;
            textObject.GetComponent<Outline>().effectColor = UiTheme.DarkShadowLight;
        }

    }
}
