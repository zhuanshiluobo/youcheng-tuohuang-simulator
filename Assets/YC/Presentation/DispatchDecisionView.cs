using System;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed class DispatchDecisionView
    {
        private readonly Func<RectTransform> getCanvas;
        private GameObject overlay;

        public DispatchDecisionView(Func<RectTransform> getCanvas)
        {
            this.getCanvas = getCanvas ?? throw new ArgumentNullException(nameof(getCanvas));
        }

        public void Show(DispatchDecisionViewModel viewModel)
        {
            var canvas = getCanvas();
            if (viewModel == null || canvas == null) return;

            Hide();
            overlay = new GameObject("Dispatch Decision Overlay", typeof(RectTransform), typeof(Image));
            overlay.transform.SetParent(canvas, false);
            var overlayRect = overlay.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;
            overlay.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

            var panel = new GameObject("Dispatch Decision Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panel.transform.SetParent(overlayRect, false);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(520f, 190f);
            panelRect.anchoredPosition = new Vector2(0f, -20f);
            panel.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panel.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            CreateText(panelRect, viewModel.Title, 20, FontStyle.Bold, new Vector2(0f, -42f));
            CreateText(panelRect, viewModel.Message, 15, FontStyle.Normal, new Vector2(0f, -78f));
            CreateButton(panelRect, viewModel.ContinueLabel, new Vector2(-120f, -130f), viewModel.ContinueAction);
            CreateButton(panelRect, viewModel.FinishLabel, new Vector2(120f, -130f), viewModel.FinishAction);
        }

        public void Hide()
        {
            if (overlay == null) return;
            UnityEngine.Object.Destroy(overlay);
            overlay = null;
        }

        private static void CreateText(RectTransform parent, string value, int fontSize, FontStyle style, Vector2 position)
        {
            var go = new GameObject("Dispatch Decision Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(460f, 30f);
            rect.anchoredPosition = position;
            var text = go.GetComponent<Text>();
            text.text = value ?? string.Empty;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.font = FontUtility.GetCjkFont(fontSize);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = fontSize;
        }

        private static void CreateButton(RectTransform parent, string label, Vector2 position, Action action)
        {
            var go = new GameObject((label ?? string.Empty) + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(170f, 44f);
            rect.anchoredPosition = position;
            go.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = go.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);
            var button = go.GetComponent<Button>();
            if (action != null) button.onClick.AddListener(() => action());

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(go.transform, false);
            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(10f, 0f);
            labelRect.offsetMax = new Vector2(-10f, 0f);
            var text = labelObject.GetComponent<Text>();
            text.text = label ?? string.Empty;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = 16;
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(16);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = 16;
        }
    }
}
