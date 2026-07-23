using System;
using System.Collections.Generic;
using YC.Domain.State;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionLogViewerController : MonoBehaviour
    {
        private const float PanelWidth = 864f;
        private const float RowHeight = 58f;
        private const float ScrollSensitivity = 60f;

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
                CreateRow(entry.PlayerLabel + " " + entry.Message, entry.PlayerColor, RowHeight);
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

                string message;
                if (!TryPrepareDisplayMessage(log.Message, out message))
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
                    Message = message
                });
            }

            result.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
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
            panel.sizeDelta = new Vector2(PanelWidth, 680f);
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
            viewport.offsetMax = new Vector2(-38f, -8f);
            viewportObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);

            var scrollbarObject = new GameObject("Action Log Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            scrollbarObject.transform.SetParent(scrollRectTransform, false);
            var scrollbarRect = scrollbarObject.GetComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.offsetMin = new Vector2(-30f, 8f);
            scrollbarRect.offsetMax = new Vector2(-8f, -8f);
            scrollbarObject.GetComponent<Image>().color = new Color(0.08f, 0.07f, 0.04f, 0.9f);

            var slidingAreaObject = new GameObject("Sliding Area", typeof(RectTransform));
            slidingAreaObject.transform.SetParent(scrollbarRect, false);
            var slidingArea = slidingAreaObject.GetComponent<RectTransform>();
            slidingArea.anchorMin = Vector2.zero;
            slidingArea.anchorMax = Vector2.one;
            slidingArea.offsetMin = new Vector2(3f, 3f);
            slidingArea.offsetMax = new Vector2(-3f, -3f);

            var handleObject = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleObject.transform.SetParent(slidingArea, false);
            var handleRect = handleObject.GetComponent<RectTransform>();
            handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one;
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;
            var handleImage = handleObject.GetComponent<Image>();
            handleImage.color = UiTheme.GoldOutline;

            var scrollbar = scrollbarObject.GetComponent<Scrollbar>();
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.value = 1f;

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
            scroll.scrollSensitivity = ScrollSensitivity;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            scroll.verticalScrollbarSpacing = 8f;
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
            var rowText = CreateText(rowObject.GetComponent<RectTransform>(), "Action Log Row Text", value, 20, TextAnchor.MiddleLeft,
                Vector2.zero, Vector2.one, new Vector2(16f, 6f), new Vector2(-16f, -6f));
            rowText.resizeTextForBestFit = true;
            rowText.resizeTextMinSize = 14;
            rowText.resizeTextMaxSize = 20;
            rowText.verticalOverflow = VerticalWrapMode.Truncate;
        }

        public static bool TryPrepareDisplayMessage(string source, out string message)
        {
            message = (source ?? string.Empty).Trim();
            if (message.Length == 0 ||
                message.IndexOf("ended their action", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("began exploring", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.Contains("\u5df2\u76d6\u653e\u89d2\u8272\u724c") ||
                message.Contains("\u76d6\u653e\u4e86\u89d2\u8272\u724c"))
            {
                message = string.Empty;
                return false;
            }

            var action = StripEnglishPlayerPrefix(message);
            if (action != message)
            {
                message = LocalizeLegacyEnglishAction(action);
                return !string.IsNullOrEmpty(message);
            }

            action = StripChinesePlayerPrefix(message);
            if (action != message)
            {
                const string characterPrefix = "\u7684\u89d2\u8272\u724c";
                if (action.StartsWith(characterPrefix, StringComparison.Ordinal) &&
                    action.Contains("\u5b8c\u6210\u5168\u90e8\u7ed3\u7b97"))
                {
                    var completedIndex = action.IndexOf("\u5df2\u5b8c\u6210", StringComparison.Ordinal);
                    var cardText = completedIndex < 0
                        ? action.Substring(characterPrefix.Length)
                        : action.Substring(characterPrefix.Length, completedIndex - characterPrefix.Length);
                    message = "\u53d1\u52a8\u4e86\u89d2\u8272\u724c" + cardText.Trim() + "\uff0c\u5df2\u5b8c\u6210\u5168\u90e8\u7ed3\u7b97\u3002";
                }
                else
                {
                    message = action;
                }
            }

            return true;
        }

        private static string LocalizeLegacyEnglishAction(string action)
        {
            action = action.Trim();
            if (action.Equals("ended their action.", StringComparison.OrdinalIgnoreCase) ||
                action.StartsWith("began exploring ", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (action.StartsWith("built ", StringComparison.OrdinalIgnoreCase))
            {
                return "\u5efa\u9020\u4e86\u5efa\u7b51\u201c" + TrimPeriod(action.Substring("built ".Length)) + "\u201d\u3002";
            }

            if (action.StartsWith("deployed influence to ", StringComparison.OrdinalIgnoreCase))
            {
                return "\u5728\u4f4d\u7f6e " + TrimPeriod(action.Substring("deployed influence to ".Length)) + " \u90e8\u7f72\u4e86 1 \u4e2a\u5f71\u54cd\u529b\u3002";
            }

            if (action.StartsWith("dispatched influence ", StringComparison.OrdinalIgnoreCase))
            {
                var countText = TrimPeriod(action.Substring("dispatched influence ".Length));
                countText = countText.Replace(" time(s)", string.Empty);
                return "\u8c03\u5ea6\u4e86 " + countText + " \u6b21\u5f71\u54cd\u529b\u3002";
            }

            if (action.StartsWith("explored ", StringComparison.OrdinalIgnoreCase))
            {
                return "\u63a2\u7d22\u4e86\u5730\u70b9 " + TrimPeriod(action.Substring("explored ".Length)) + "\u3002";
            }

            if (action.StartsWith("moved city to ", StringComparison.OrdinalIgnoreCase))
            {
                return "\u5c06\u57ce\u5e02\u79fb\u52a8\u81f3\u5730\u70b9 " + TrimPeriod(action.Substring("moved city to ".Length)) + "\u3002";
            }

            if (action.StartsWith("resolved exploration event ", StringComparison.OrdinalIgnoreCase))
            {
                return "\u7ed3\u7b97\u4e86\u63a2\u7d22\u4e8b\u4ef6\u201c" + TrimPeriod(action.Substring("resolved exploration event ".Length)) + "\u201d\u3002";
            }

            if (action.StartsWith("resolved move city event ", StringComparison.OrdinalIgnoreCase))
            {
                return "\u7ed3\u7b97\u4e86\u79fb\u52a8\u4e8b\u4ef6\u201c" + TrimPeriod(action.Substring("resolved move city event ".Length)) + "\u201d\u3002";
            }

            if (action.Equals("collected resources.", StringComparison.OrdinalIgnoreCase))
            {
                return "\u5b8c\u6210\u4e86\u8d44\u6e90\u6536\u96c6\u3002";
            }

            if (action.StartsWith("was chosen as the start player", StringComparison.OrdinalIgnoreCase))
            {
                return "\u88ab\u9009\u4e3a\u8d77\u59cb\u73a9\u5bb6\uff0c\u5165\u573a\u9636\u6bb5\u5f00\u59cb\u3002";
            }

            if (action.StartsWith("placed their initial city at ", StringComparison.OrdinalIgnoreCase))
            {
                return "\u5c06\u521d\u59cb\u57ce\u5e02\u653e\u7f6e\u5728\u5730\u70b9 " + TrimPeriod(action.Substring("placed their initial city at ".Length)) + "\u3002";
            }

            if (action.StartsWith("resolved entrance event ", StringComparison.OrdinalIgnoreCase))
            {
                var eventText = TrimPeriod(action.Substring("resolved entrance event ".Length));
                var optionIndex = eventText.IndexOf(" with option ", StringComparison.OrdinalIgnoreCase);
                if (optionIndex >= 0)
                {
                    eventText = eventText.Substring(0, optionIndex);
                }

                return "\u7ed3\u7b97\u4e86\u5165\u573a\u4e8b\u4ef6\u201c" + eventText + "\u201d\u3002";
            }

            return action;
        }

        private static string StripEnglishPlayerPrefix(string value)
        {
            const string prefix = "Player ";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            var separator = value.IndexOf(' ', prefix.Length);
            return separator < 0 ? value : value.Substring(separator + 1);
        }

        private static string StripChinesePlayerPrefix(string value)
        {
            const string prefix = "\u73a9\u5bb6 ";
            if (!value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return value;
            }

            var separator = value.IndexOf(' ', prefix.Length);
            return separator < 0 ? value : value.Substring(separator + 1);
        }

        private static string TrimPeriod(string value)
        {
            return value.Trim().TrimEnd('.');
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
