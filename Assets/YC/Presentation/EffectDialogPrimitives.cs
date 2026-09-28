using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Interactions;

namespace YC.Presentation
{
    internal sealed class EffectDialogOption
    {
        public EffectDialogOption(string label, Action select, bool enabled = true)
        {
            Label = label ?? string.Empty;
            Select = select;
            Enabled = enabled;
        }

        public string Label { get; private set; }

        public Action Select { get; private set; }

        public bool Enabled { get; private set; }

        public string StableId;
        public string RequestId;
        public int Revision;
        public Func<bool> IsCurrent;
        public bool ExecuteImmediately;
        public string SourceLabel;
        public string DescriptionLabel;
    }

    internal sealed class EffectDialogSelectionSpec
    {
        public InteractionRequestProjection Request;
        public IReadOnlyCollection<string> SelectedIds;
        public Func<string, string> Label;
        public Func<string, string> SourceLabel;
        public Func<string, string> DescriptionLabel;
        public Func<string, Texture2D> CardTexture;
        public Action<string> Select;
        public Action Confirm;
        public Action Cancel;
        public Func<bool> IsCurrent;
        public Func<string, bool> CanSelect;
        public string OptionNamePrefix;
        public string ConfirmLabel;
        public string CancelLabel;
        public bool IsEffectPage = true;
        public string CardPickerHint;
        public bool AllowStageFolding;
        public bool ReadOnly;

        internal bool HasCardArtwork()
        {
            if (CardTexture == null || Request?.CandidateIds == null) return false;
            foreach (var id in Request.CandidateIds)
                if (!string.IsNullOrEmpty(id) && CardTexture(id) != null) return true;
            return false;
        }
    }

    internal sealed class ResourceAllocationSpec
    {
        public string Title = string.Empty;
        public string Description = string.Empty;
        public IReadOnlyList<string> Labels;
        public IReadOnlyList<int> Maximums;
        public IReadOnlyList<int> UnitPrices;
        public int ExactTotal = -1;
        public int MinimumTotal;
        public string LabelNamePrefix = "Resource Label ";
        public string DecreaseNamePrefix = "Decrease ";
        public string ValueNamePrefix = "Value ";
        public string IncreaseNamePrefix = "Increase ";
        public string ConfirmName = "Confirm";
        public string ConfirmLabel = "确认结算";
        public string CancelName = "Skip";
        public string CancelLabel = "跳过";
        public string CancelDraftLabel;
        public string SummaryName = string.Empty;
        public float RowStartY = -168f;
        public float RowSpacing = 68f;
        public float LabelWidth = 280f;
        public float LabelHeight = 46f;
        public float LabelX = 175f;
        public float DecreaseX = 366f;
        public float ValueX = 432f;
        public float IncreaseX = 498f;
        public Func<int, string, int, string> FormatRowLabel;
        public Func<IReadOnlyList<int>, string> FormatSummary;
        public Action<IReadOnlyList<int>> Confirm;
        public Action Cancel;
        public bool CloseBeforeConfirm;
        public bool CloseBeforeCancel;
        public IReadOnlyList<int> InitialValues;
        public Action<IReadOnlyList<int>> DraftChanged;
        public Func<bool> IsCurrent;
    }

    /// <summary>运行时效果弹窗共享的无领域语义 UI 壳层。</summary>
    internal sealed class EffectDialogShell
    {
        // 游戏内效果弹窗统一位于设置/日志按钮（120）之下、其余常驻游戏 UI 之上。
        internal const int SortingOrder = GameplayUiLayers.Page;

        private readonly GameplayDialogRegistry registry;
        private EffectDialogShellView view;
        private CardViewer cardViewer;
        private float savedScrollPosition = 1f;
        private bool openingDetails;
        internal bool IsVisible => view != null && view.gameObject.activeInHierarchy;
        internal bool OwnsPage(GameObject page) => !openingDetails && view != null && view.gameObject == page;

        internal float ScrollPosition => view == null ? savedScrollPosition : view.ScrollPosition;

        internal void RestoreScroll(float value)
        {
            if (view == null) return;
            savedScrollPosition = Mathf.Clamp01(value);
            view.RestoreScrollPosition(savedScrollPosition);
        }

        internal EffectDialogShell(GameplayDialogRegistry configuredRegistry)
        {
            registry = configuredRegistry ?? throw new ArgumentNullException(nameof(configuredRegistry));
        }

        public bool IsShowing
        {
            get { return view != null; }
        }

        public RectTransform Rebuild(
            RectTransform canvas,
            string overlayName,
            string panelName,
            Vector2 size,
            Vector2 position,
            bool blockBackgroundInput = false, bool effectPage = true, bool informationPage = false,
            bool cardPicker = false)
        {
            Hide();
            if (canvas == null)
            {
                return null;
            }

            if (registry == null)
            {
                throw new InvalidOperationException("EffectDialogShell 缺少显式 GameplayDialogRegistry 注入。");
            }

            view = registry.InstantiateEffectDialogShell(canvas, effectPage, !informationPage, cardPicker);
            if (view == null)
            {
                return null;
            }

            if (!view.TryValidateConfiguration(out var reason))
            {
                var invalidView = view;
                view = null;
                DestroyView(invalidView);
                throw new InvalidOperationException(reason);
            }

            view.PrepareForUse(overlayName, panelName, size, position, blockBackgroundInput);
            return view.ExpandedContent;
        }

        public void Hide()
        {
            if (cardViewer != null) cardViewer.Dismiss();
            if (view == null)
            {
                return;
            }

            var releasedView = view;
            savedScrollPosition = releasedView.ScrollPosition;
            view = null;
            DestroyView(releasedView);
        }

        internal void SuspendForMapInteraction(string message)
        {
            var frame = GameplayHudFrame.Active;
            if (frame == null || view == null) return;
            frame.SetInteractionMessage(message);
            frame.SuspendEffectForMapInteraction();
        }

        private static void DestroyView(EffectDialogShellView target)
        {
            if (target == null)
            {
                return;
            }

            target.gameObject.SetActive(false);
            GameplayHudFrame.Active?.ReleaseStagePage(target.gameObject);
            if (UnityEngine.Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target.gameObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target.gameObject);
            }
        }

        public static RectTransform AddOptionScroll(
            RectTransform panel,
            string name,
            float bottom,
            float top)
        {
            var shellView = ResolveView(panel);
            return shellView.ConfigureOptionScroll(name, bottom, top);
        }

        public static void AddOptions(
            RectTransform content,
            IReadOnlyList<EffectDialogOption> options,
            string buttonNamePrefix,
            Action beforeSelect)
        {
            if (content == null || options == null)
            {
                return;
            }

            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                if (option == null) continue;
                if (option.ExecuteImmediately)
                {
                    var effectRow = ResolveView(content).CreateEffectRow();
                    effectRow.gameObject.name = buttonNamePrefix + i;
                    effectRow.Bind(new UiEffectRowView.Binding
                    {
                        StableItemId = option.StableId,
                        RequestId = option.RequestId,
                        Revision = option.Revision,
                        RowMode = UiEffectRowView.Mode.ChoiceExecute,
                        RightStatus = UiEffectRowView.Status.Use,
                        Enabled = option.Enabled,
                        Title = option.Label
                    }, (_, __) => effectRow != null && effectRow.gameObject.activeInHierarchy &&
                        !GameplayHudFrame.EffectInputSuspended && (option.IsCurrent == null || option.IsCurrent()), null,
                    _ => { beforeSelect?.Invoke(); option.Select?.Invoke(); });
                    continue;
                }
                var row = ResolveView(content).CreateOptionRow(content);
                row.ResetSelection();
                row.gameObject.name = buttonNamePrefix + i;
                row.Label.text = option.Label;
                var button = row.Button;
                if (!string.IsNullOrEmpty(option.StableId))
                    YC.PlayerJourney.PlayerAutomationId.Attach(button.gameObject, option.StableId);
                button.onClick.RemoveAllListeners();
                button.interactable = option.Enabled;
                row.Background.color = option.Enabled ? Color.white :
                    new Color(.75f, .75f, .75f, 1f);
                if (!option.Enabled)
                {
                    continue;
                }

                var select = option.Select;
                var invoked = false;
                button.onClick.AddListener(() =>
                {
                    if (invoked || !button.isActiveAndEnabled || !button.IsInteractable() ||
                        (option.IsCurrent != null && !option.IsCurrent()))
                    {
                        return;
                    }

                    invoked = true;
                    beforeSelect?.Invoke();
                    select?.Invoke();
                });
            }
        }

        public void AddSelection(RectTransform panel, EffectDialogSelectionSpec spec)
        {
            var shellView = ResolveView(panel);
            if (!spec.IsEffectPage && !spec.ReadOnly)
                GameplayHudFrame.Active?.ConfigureStagePageFolding(shellView.gameObject, spec.AllowStageFolding);
            var profile = shellView.LayoutProfile;
            var request = spec.Request;
            var selected = new HashSet<string>(spec.SelectedIds ?? new string[0], StringComparer.Ordinal);
            var candidates = request.CandidateIds ?? new List<string>();
            shellView.ResourceSummaryText.gameObject.SetActive(!spec.ReadOnly);
            shellView.ConfigureSelectionMode(spec.ReadOnly, spec.CardPickerHint);
            shellView.ResourceSummaryText.text = string.Format(profile.SelectionSummaryFormat,
                selected.Count, request.MinSelections, request.MaxSelections);
            var cardMode = false;
            foreach (var candidate in candidates)
                if (spec.CardTexture != null && spec.CardTexture(candidate) != null) { cardMode = true; break; }
            var content = cardMode
                ? shellView.ConfigureCardScroll(profile.SelectionCardSize, profile.SelectionMinimumCardWidth)
                : shellView.ConfigureOptionScroll("Selection Scroll", profile.OptionsScrollBottomWithBack,
                    profile.OptionsScrollTop);
            bool CanAct() => shellView != null && shellView.gameObject.activeInHierarchy &&
                (!spec.IsEffectPage || !GameplayHudFrame.EffectInputSuspended) &&
                (spec.IsCurrent == null || spec.IsCurrent());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < candidates.Count; i++)
            {
                var id = candidates[i];
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                var label = spec.Label == null ? id : spec.Label(id);
                if (cardMode)
                {
                    var card = shellView.CreateFacilityCard();
                    card.gameObject.name = (spec.OptionNamePrefix ?? "Selection Card ") + i;
                    YC.PlayerJourney.PlayerAutomationId.Attach(card.Button.gameObject, "selection.card." + id);
                    var texture = spec.CardTexture == null ? null : spec.CardTexture(id);
                    card.CardImage.texture = texture;
                    card.CardImage.color = texture == null ? Color.clear : Color.white;
                    card.FallbackLabel.text = label;
                    card.FallbackLabel.gameObject.SetActive(texture == null);
                    if (card.SelectionImage != null) card.SelectionImage.enabled = selected.Contains(id);
                    else card.Outline.enabled = selected.Contains(id);
                    card.Button.onClick.RemoveAllListeners();
                    card.PointerInteraction.ConfigureClick(card.Button, null, null);
                    card.PointerInteraction.ConfigureDrag(null, null, null, null);
                    card.Button.interactable = !spec.ReadOnly && (spec.CanSelect == null || spec.CanSelect(id));
                    card.Button.onClick.AddListener(() =>
                    {
                        if (!spec.ReadOnly && CanAct() && (spec.CanSelect == null || spec.CanSelect(id)))
                            spec.Select?.Invoke(id);
                    });
                    void OpenDetails()
                    {
                            if (!CanAct() || texture == null) return;
                            var savedScroll = shellView.ScrollPosition;
                            openingDetails = true;
                            try
                            {
                                if (cardViewer == null)
                                    cardViewer = registry.InstantiateCardViewer(shellView.transform.parent as RectTransform);
                                CardImagePreviewUtility.Open(ref cardViewer, shellView.transform.parent,
                                    texture);
                            }
                            finally { openingDetails = false; }
                            if (cardViewer == null) return;
                            cardViewer.SetReturn(() =>
                            {
                                if (spec.IsCurrent != null && !spec.IsCurrent())
                                {
                                    Hide();
                                    return;
                                }
                                if (spec.IsEffectPage) GameplayHudFrame.Active?.ResumeEffectPage();
                                else if (shellView != null)
                                {
                                    if (spec.ReadOnly) GameplayHudFrame.Active?.ShowPage(shellView.gameObject, false);
                                    else GameplayHudFrame.Active?.ShowStagePage(shellView.gameObject);
                                    shellView.gameObject.SetActive(true);
                                    shellView.RestoreScrollPosition(savedScroll);
                                    UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(card.Button.gameObject);
                                }
                            }, () => shellView != null && (spec.IsCurrent == null || spec.IsCurrent()));
                    }
                    card.ConfigureInspection(OpenDetails, CanAct);
                }
                else
                {
                    var row = shellView.CreateOptionRow(content, spec.SourceLabel != null);
                    row.gameObject.name = (spec.OptionNamePrefix ?? "Selection Option ") + i;
                    if (spec.SourceLabel != null)
                        row.SetSourceAndDescription(spec.SourceLabel(id), spec.DescriptionLabel == null ? label : spec.DescriptionLabel(id));
                    else row.Label.text = label;
                    row.SetSelected(selected.Contains(id));
                    row.Button.onClick.RemoveAllListeners();
                    row.Button.interactable = !spec.ReadOnly && (spec.CanSelect == null || spec.CanSelect(id));
                    row.Button.onClick.AddListener(() =>
                    {
                        if (CanAct() && (spec.CanSelect == null || spec.CanSelect(id)))
                            spec.Select?.Invoke(id);
                    });
                }
            }
            if (seen.Count == 0)
                CreateText(content, "Selection Empty", profile.SelectionEmptyText, 18, TextAnchor.MiddleCenter);
            var submitted = false;
            if (!spec.ReadOnly)
            {
            var confirm = CreateButton(panel, "Confirm Selection",
                string.IsNullOrEmpty(spec.ConfirmLabel) ? profile.SelectionConfirmLabel : spec.ConfirmLabel, 18);
            confirm.interactable = selected.Count >= request.MinSelections && selected.Count <= request.MaxSelections;
            confirm.onClick.AddListener(() =>
            {
                if (submitted || !confirm.IsInteractable() || !CanAct()) return;
                submitted = true;
                spec.Confirm?.Invoke();
            });
            }
            if (spec.Cancel != null && request.AllowDecline)
            {
                var cancel = CreateButton(panel, "Cancel Selection",
                    string.IsNullOrEmpty(spec.CancelLabel) ? profile.SelectionCancelLabel : spec.CancelLabel, 18, false);
                cancel.onClick.AddListener(() =>
                {
                    if (submitted || (!spec.ReadOnly && !CanAct())) return;
                    submitted = true;
                    spec.Cancel();
                });
            }
        }

        public void AddResourceAllocation(RectTransform panel, ResourceAllocationSpec spec)
        {
            AddHeading(panel, spec.Title, spec.Description);
            var shellView = ResolveView(panel);
            shellView.ConfigureResourceScroll();
            var count = spec.Labels == null ? 0 : spec.Labels.Count;
            var values = new int[count];
            int Maximum(int index) => Mathf.Max(0, spec.Maximums != null && index < spec.Maximums.Count
                ? spec.Maximums[index] : (spec.ExactTotal >= 0 ? spec.ExactTotal : int.MaxValue));
            for (var i = 0; i < count; i++)
                values[i] = Mathf.Clamp(spec.InitialValues != null && i < spec.InitialValues.Count
                    ? spec.InitialValues[i] : (spec.ExactTotal >= 0 && i == 0 ? spec.ExactTotal : 0), 0, Maximum(i));
            var valueTexts = new Text[count];
            var rows = new EffectDialogResourceRowView[count];
            var validInputs = new bool[count];
            for (var i = 0; i < count; i++) validInputs[i] = true;
            var decreases = new Button[count];
            var increases = new Button[count];
            var summary = shellView.ResourceSummaryText;
            summary.gameObject.SetActive(!string.IsNullOrEmpty(spec.SummaryName));
            if (summary.gameObject.activeSelf) summary.gameObject.name = spec.SummaryName;
            var submitted = false;
            bool CanAct() => !submitted && shellView != null && shellView.gameObject.activeInHierarchy &&
                !GameplayHudFrame.EffectInputSuspended && (spec.IsCurrent == null || spec.IsCurrent());
            int Total() { var total = 0; foreach (var value in values) total += value; return total; }
            bool Valid() => Total() >= spec.MinimumTotal && (spec.ExactTotal < 0 || Total() == spec.ExactTotal);
            Button confirmButton = null;
            Button cancelButton = null;
            Action refresh = () =>
            {
                var total = Total();
                for (var i = 0; i < count; i++)
                {
                    values[i] = Mathf.Clamp(values[i], 0, Maximum(i));
                    if (rows[i].ValueInput != null) { if (validInputs[i]) rows[i].ValueInput.SetTextWithoutNotify(values[i].ToString()); }
                    else valueTexts[i].text = values[i].ToString();
                    rows[i].RenderDraft(spec.Labels[i], Maximum(i), values[i],
                        spec.UnitPrices != null && i < spec.UnitPrices.Count ? (int?)spec.UnitPrices[i] : null);
                    decreases[i].interactable = !submitted && values[i] > 0;
                    increases[i].interactable = !submitted && values[i] < Maximum(i) &&
                        (spec.ExactTotal < 0 || total < spec.ExactTotal);
                }
                if (confirmButton != null) confirmButton.interactable = !submitted && Valid() && Array.TrueForAll(validInputs, valid => valid);
                if (cancelButton != null && !string.IsNullOrEmpty(spec.CancelDraftLabel))
                    cancelButton.GetComponentInChildren<Text>().text = total > 0 ? spec.CancelDraftLabel : spec.CancelLabel;
                var snapshot = new List<int>(values).AsReadOnly();
                if (summary.gameObject.activeSelf && spec.FormatSummary != null)
                    summary.text = spec.FormatSummary(snapshot);
                spec.DraftChanged?.Invoke(snapshot);
            };
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var row = shellView.CreateResourceRow();
                rows[i] = row;
                row.gameObject.name = "Resource Allocation Row " + i;
                row.Label.gameObject.name = spec.LabelNamePrefix + i;
                row.Label.text = spec.FormatRowLabel == null ? spec.Labels[i] : spec.FormatRowLabel(i,
                    spec.Labels[i], spec.UnitPrices != null && i < spec.UnitPrices.Count ? spec.UnitPrices[i] : 0);
                valueTexts[i] = row.ValueText;
                valueTexts[i].gameObject.name = spec.ValueNamePrefix + i;
                if (row.ValueInput != null)
                {
                    row.ValueInput.onValueChanged.RemoveAllListeners();
                    row.ValueInput.onEndEdit.RemoveAllListeners();
                    row.ValueInput.onValueChanged.AddListener(value =>
                    {
                        if (!CanAct()) return;
                        validInputs[index] = int.TryParse(value, out var amount) && amount >= 0 && amount <= Maximum(index);
                        if (validInputs[index]) { values[index] = amount; refresh(); }
                        if (confirmButton != null) confirmButton.interactable = Valid() && Array.TrueForAll(validInputs, valid => valid);
                    });
                    row.ValueInput.onEndEdit.AddListener(_ =>
                    {
                        if (!CanAct()) return;
                        validInputs[index] = true;
                        refresh();
                    });
                }
                decreases[i] = row.DecreaseButton;
                decreases[i].gameObject.name = spec.DecreaseNamePrefix + i;
                decreases[i].onClick.RemoveAllListeners();
                decreases[i].onClick.AddListener(() =>
                {
                    if (!CanAct() || values[index] <= 0) return;
                    values[index]--; validInputs[index] = true; refresh();
                });
                increases[i] = row.IncreaseButton;
                increases[i].gameObject.name = spec.IncreaseNamePrefix + i;
                increases[i].onClick.RemoveAllListeners();
                increases[i].onClick.AddListener(() =>
                {
                    if (!CanAct() || values[index] >= Maximum(index) ||
                        (spec.ExactTotal >= 0 && Total() >= spec.ExactTotal)) return;
                    values[index]++; validInputs[index] = true; refresh();
                });
            }
            confirmButton = CreateButton(panel, spec.ConfirmName, spec.ConfirmLabel, 18);
            confirmButton.onClick.AddListener(() =>
            {
                if (!CanAct() || !Valid() || !Array.TrueForAll(validInputs, valid => valid)) return;
                submitted = true;
                var result = new List<int>(values).AsReadOnly();
                if (spec.CloseBeforeConfirm) Hide();
                spec.Confirm?.Invoke(result);
            });
            if (spec.Cancel != null)
            {
                var cancel = CreateButton(panel, spec.CancelName, spec.CancelLabel, 18, false);
                cancelButton = cancel;
                cancel.onClick.AddListener(() =>
                {
                    if (!CanAct()) return;
                    submitted = true;
                    if (spec.CloseBeforeCancel) Hide();
                    spec.Cancel();
                });
            }
            refresh();
        }

        public static void AddHeading(
            RectTransform panel,
            string title,
            string description,
            float descriptionHeight = 70f,
            string titleName = "Title",
            string descriptionName = "Description",
            int titleSize = 27)
        {
            if (panel == null)
            {
                return;
            }

            ResolveView(panel).ConfigureHeading(
                title,
                description,
                descriptionHeight,
                titleName,
                descriptionName,
                titleSize);
        }

        public static Text CreateText(Transform parent, string name, string value, int fontSize, TextAnchor alignment)
        {
            var shellView = ResolveView(parent);
            var text = UnityEngine.Object.Instantiate(shellView.DescriptionText, parent, false);
            text.gameObject.name = name ?? string.Empty;
            text.gameObject.SetActive(true);
            text.text = value ?? string.Empty;
            text.fontSize = fontSize;
            text.color = UiTheme.ValueText;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        public static Button CreateButton(Transform parent, string name, string label, int fontSize, bool primary = true)
        {
            var shellView = ResolveView(parent);
            var action = shellView.AcquireActionButton(parent as RectTransform, primary);
            action.gameObject.name = name ?? string.Empty;
            action.Label.text = label ?? string.Empty;
            action.Label.raycastTarget = false;
            action.Button.onClick.RemoveAllListeners();
            return action.Button;
        }

        internal static FacilityEffectCardView CreateFacilityCard(RectTransform parent)
        {
            var shellView = ResolveView(parent);
            var card = shellView.CreateFacilityCard();
            return card;
        }

        internal static RectTransform ConfigureCardScroll(RectTransform parent, Vector2 cardSize)
        {
            return ResolveView(parent).ConfigureCardScroll(cardSize);
        }

        private static EffectDialogShellView ResolveView(Transform source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var shellView = source.GetComponentInParent<EffectDialogShellView>();
            if (shellView == null)
            {
                throw new InvalidOperationException("Effect dialog content must belong to an EffectDialogShellView prefab instance.");
            }

            return shellView;
        }

        public static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        public static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 size,
            Vector2 position)
        {
            var parentLayout = rect.parent == null ? null : rect.parent.GetComponent<LayoutGroup>();
            if (parentLayout != null && parentLayout.enabled)
            {
                var element = rect.GetComponent<LayoutElement>();
                if (element != null) { element.preferredWidth = size.x; element.preferredHeight = size.y; }
                return;
            }
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }
    }
}
