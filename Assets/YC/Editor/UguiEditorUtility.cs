using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.EditorTools
{
    internal static class UguiEditorUtility
    {
        internal static Button CreateViewerCloseButton(
            RectTransform parent,
            string name,
            UnityAction closeAction)
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
