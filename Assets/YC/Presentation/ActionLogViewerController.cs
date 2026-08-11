using System;
using System.Collections.Generic;
using YC.Domain.State;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionLogViewerController : MonoBehaviour
    {
        private const float RowHeight = 58f;

        [SerializeField] private ActionLogViewerView view;
        private Func<GameState> stateProvider;
        private bool initialized;
        private bool isOpen;

        public bool IsOpen => isOpen;

        private void Awake()
        {
            TryInitialize();
        }

        public void Configure(Func<GameState> provider)
        {
            stateProvider = provider;
        }

        public void Open()
        {
            if (!TryInitialize())
            {
                return;
            }

            Refresh();
            view.OverlayObject.SetActive(true);
            isOpen = true;
        }

        public void Close()
        {
            if (initialized)
            {
                view.OverlayObject.SetActive(false);
            }

            isOpen = false;
        }

        public void Refresh()
        {
            if (!TryInitialize())
            {
                return;
            }

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

        private bool TryInitialize()
        {
            if (initialized)
            {
                return true;
            }

            var reason = "View 未绑定。";
            if (view == null || !view.TryValidateConfiguration(out reason))
            {
                Debug.LogError(
                    "ActionLogViewerController 缺少完整编辑器 View 引用：" +
                    reason,
                    this);
                enabled = false;
                return false;
            }

            BindButton(view.OverlayCloseButton, Close);
            BindButton(view.CloseButton, Close);
            view.RowTemplate.gameObject.SetActive(false);
            view.OverlayObject.SetActive(false);
            initialized = true;
            return true;
        }

        private void CreateRow(string value, Color accent, float height)
        {
            // 动态边界：日志条目仅实例化编辑器行模板并绑定文本/颜色/高度。
            var row = Instantiate(view.RowTemplate, view.ContentTransform, false);
            row.gameObject.name = "Action Log Row";
            row.gameObject.SetActive(true);
            row.Bind(value, accent, height);
        }

        private static void BindButton(Button button, UnityEngine.Events.UnityAction action)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
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
            for (var i = view.ContentTransform.childCount - 1; i >= 0; i--)
            {
                var child = view.ContentTransform.GetChild(i).gameObject;
                if (child == view.RowTemplate.gameObject)
                {
                    continue;
                }

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
