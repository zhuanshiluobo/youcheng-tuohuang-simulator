using System;
using System.Collections.Generic;
using YC.Domain.State;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionLogViewerController : MonoBehaviour
    {
        private Func<GameState> stateProvider;
        private GameObject rootObject;
        private RectTransform contentTransform;
        private bool isOpen;

        public bool IsOpen => isOpen;

        public void Configure(Func<GameState> provider)
        {
            stateProvider = provider;
            EnsureUi();
        }

        public void Open()
        {
            EnsureUi();
            Refresh();
            rootObject.SetActive(true);
            isOpen = true;
        }

        public void Close()
        {
            if (rootObject != null)
            {
                rootObject.SetActive(false);
            }

            isOpen = false;
        }

        public void Refresh()
        {
            EnsureUi();
            ClearRows();

            var entries = BuildDisplayEntries(stateProvider == null ? null : stateProvider());
            if (entries.Count == 0)
            {
                CreateRow("\u6682\u65e0\u6210\u529f\u884c\u52a8\u8bb0\u5f55", UiTheme.LabelText, 68f);
                return;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                CreateRow(entry.PlayerLabel + "\n" + entry.Message, entry.PlayerColor, 78f);
            }
        }

        public static List<ActionLogDisplayEntry> BuildDisplayEntries(GameState state)
        {
            var result = new List<ActionLogDisplayEntry>();
            if (state == null || state.Logs == null)
            {
                return result;
            }

            for (var i = 0; i < state.Logs.Count; i++)
            {
                var log = state.Logs[i];
                if (log == null)
                {
                    continue;
                }

                var player = state.FindPlayer(log.PlayerId);
                result.Add(new ActionLogDisplayEntry
                {
                    Sequence = log.Sequence,
                    PlayerId = log.PlayerId,
                    PlayerLabel = player == null
                        ? "\u73a9\u5bb6 " + log.PlayerId
                        : (string.IsNullOrEmpty(player.Name) ? GetColorName(player.Color) : player.Name),
                    PlayerColor = player == null ? UiTheme.ValueText : UiTheme.GetPlayerColor(player.Color, 1f),
                    Message = log.Message ?? string.Empty
                });
            }

            result.Sort((left, right) => right.Sequence.CompareTo(left.Sequence));
            return result;
        }

        private void EnsureUi()
        {
            if (rootObject != null)
            {
                return;
            }

            var canvas = UguiUtility.CreateCanvas("Action Log Viewer Canvas", 125, transform);
            rootObject = new GameObject("Action Log Overlay", typeof(RectTransform), typeof(Image), typeof(Button));
            rootObject.transform.SetParent(canvas.transform, false);
            var rootRect = rootObject.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.65f);
            rootObject.GetComponent<Button>().onClick.AddListener(Close);

            var panelObject = new GameObject("Action Log Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(rootRect, false);
            var panel = panelObject.GetComponent<RectTransform>();
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(720f, 680f);
            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            panelObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;

            CreateText(panel, "Action Log Title", "\u5bf9\u5c40\u884c\u52a8\u65e5\u5fd7", 30, TextAnchor.MiddleCenter,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -64f), new Vector2(0f, 0f));
            CreateCloseButton(panel);
            BuildScrollView(panel);
            rootObject.SetActive(false);
        }

        private void BuildScrollView(RectTransform panel)
        {
            var scrollObject = new GameObject("Action Log Scroll View", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollObject.transform.SetParent(panel, false);
            var scrollRectTransform = scrollObject.GetComponent<RectTransform>();
            scrollRectTransform.anchorMin = Vector2.zero;
            scrollRectTransform.anchorMax = Vector2.one;
            scrollRectTransform.offsetMin = new Vector2(26f, 24f);
            scrollRectTransform.offsetMax = new Vector2(-26f, -76f);
            scrollObject.GetComponent<Image>().color = UiTheme.ScrollBackground;

            var viewportObject = new GameObject("Action Log Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            viewportObject.transform.SetParent(scrollRectTransform, false);
            var viewport = viewportObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(8f, 8f);
            viewport.offsetMax = new Vector2(-8f, -8f);
            viewportObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);

            var contentObject = new GameObject("Action Log Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObject.transform.SetParent(viewport, false);
            contentTransform = contentObject.GetComponent<RectTransform>();
            contentTransform.anchorMin = new Vector2(0f, 1f);
            contentTransform.anchorMax = new Vector2(1f, 1f);
            contentTransform.pivot = new Vector2(0.5f, 1f);
            contentTransform.sizeDelta = Vector2.zero;
            var layout = contentObject.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = scrollObject.GetComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = contentTransform;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
        }

        private void CreateCloseButton(RectTransform panel)
        {
            var buttonObject = new GameObject("Close Action Log Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(panel, false);
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(46f, 36f);
            rect.anchoredPosition = new Vector2(-14f, -14f);
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            buttonObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            buttonObject.GetComponent<Button>().onClick.AddListener(Close);
            CreateText(rect, "Close Action Log Label", "\u00d7", 24, TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        }

        private void CreateRow(string value, Color accent, float height)
        {
            var rowObject = new GameObject("Action Log Row", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            rowObject.transform.SetParent(contentTransform, false);
            rowObject.GetComponent<Image>().color = new Color(accent.r, accent.g, accent.b, 0.18f);
            rowObject.GetComponent<LayoutElement>().preferredHeight = height;
            CreateText(rowObject.GetComponent<RectTransform>(), "Action Log Row Text", value, 20, TextAnchor.MiddleLeft,
                Vector2.zero, Vector2.one, new Vector2(16f, 6f), new Vector2(-16f, -6f));
        }

        private void ClearRows()
        {
            for (var i = contentTransform.childCount - 1; i >= 0; i--)
            {
                var child = contentTransform.GetChild(i).gameObject;
                if (UnityEngine.Application.isPlaying)
                {
                    Destroy(child);
                }
                else
                {
                    DestroyImmediate(child);
                }
            }
        }

        private static Text CreateText(RectTransform parent, string name, string value, int size, TextAnchor alignment,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.font = FontUtility.GetCjkFont(size);
            text.fontSize = size;
            text.color = UiTheme.ValueText;
            text.alignment = alignment;
            return text;
        }

        private static string GetColorName(YC.Domain.Rules.PlayerColor color)
        {
            switch (color)
            {
                case YC.Domain.Rules.PlayerColor.Blue: return "\u84dd\u8272\u73a9\u5bb6";
                case YC.Domain.Rules.PlayerColor.Red: return "\u7ea2\u8272\u73a9\u5bb6";
                case YC.Domain.Rules.PlayerColor.Green: return "\u7eff\u8272\u73a9\u5bb6";
                case YC.Domain.Rules.PlayerColor.Yellow: return "\u9ec4\u8272\u73a9\u5bb6";
                default: return "\u73a9\u5bb6";
            }
        }
    }

    public sealed class ActionLogDisplayEntry
    {
        public int Sequence;
        public int PlayerId;
        public string PlayerLabel = string.Empty;
        public Color PlayerColor = Color.white;
        public string Message = string.Empty;
    }
}
