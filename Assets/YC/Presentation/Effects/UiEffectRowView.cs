using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>3.4 条目只负责显示及输入分区；规则可用性必须由调用方显式提供。</summary>
    public sealed class UiEffectRowView : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
    {
        public enum Mode { ChoiceExecute, PersistentDetail, QuickAction }
        public enum Status { None, Unused, Used, Use }
        public enum QuoteState { NotApplicable, Waiting, Valid, CannotPay, Expired, NeedsTarget }

        public sealed class Binding
        {
            public string StableItemId;
            public string RequestId;
            public int Revision;
            public Mode RowMode;
            public Status RightStatus;
            public bool Enabled;
            public bool Pending;
            public string Title;
            public string Description;
            public string Source;
            public string Eligibility;
            public string CostImpact;
            public string UnavailableReason;
            public QuoteState Quote;
            public Marker[] Markers;
        }

        [Serializable]
        public struct Marker
        {
            [Range(1, 15)] public int Mask;
            public int Count;
            public string Description;
        }

        [SerializeField] private Image background;
        [SerializeField] private Image frame;
        [SerializeField] private Text title;
        [SerializeField] private Text description;
        [SerializeField] private Button bodyButton;
        [SerializeField] private Button statusButton;
        [SerializeField] private Image statusBackground;
        [SerializeField] private Image statusIcon;
        [SerializeField] private RectTransform[] markerSlots;
        [SerializeField] private Image[] markerImages;
        [SerializeField] private Text[] markerCounts;
        [SerializeField] private Sprite[] markerByMask;
        [SerializeField] private Sprite[] rowBackgrounds;
        [SerializeField] private Sprite[] rowFrames;
        [SerializeField] private Sprite[] statusBackgrounds;
        [SerializeField] private Sprite useIcon;
        [SerializeField] private Sprite disabledUseIcon;
        [SerializeField] private Sprite unusedIcon;
        [SerializeField] private Sprite usedIcon;
        [SerializeField] private Mode mode;
        [SerializeField] private Status status = Status.None;
        [SerializeField] private bool explicitlyEnabled;
        [SerializeField] private bool externalCommandButton;
        [Header("报价与条目说明")]
        [SerializeField] private string sourcePrefix = "来源：";
        [SerializeField] private string eligibilityPrefix = "资格：";
        [SerializeField] private string costPrefix = "费用：";
        [SerializeField] private string unavailablePrefix = "不可用：";
        [SerializeField] private string quoteWaitingText = "等待正式报价";
        [SerializeField] private string quoteValidText = "报价有效";
        [SerializeField] private string quoteCannotPayText = "报价不可支付";
        [SerializeField] private string quoteExpiredText = "报价已失效";
        [SerializeField] private string quoteNeedsTargetText = "需补充目标后报价";

        private string itemId;
        private string requestId;
        private int revision;
        private bool pending;
        private bool hasExecute;
        private bool hover;
        private bool focus;
        [SerializeField] private HorizontalLayoutGroup rowLayout;
        private Func<string, int, bool> contextIsCurrent;

        public string ItemId => itemId;
        public Button StatusButton => statusButton;

        public void Bind(Binding data, Func<string, int, bool> isCurrent,
            Action<string> showBody, Action<string> execute)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var details = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(data.Description)) details.Add(data.Description);
            if (!string.IsNullOrEmpty(data.Source)) details.Add(sourcePrefix + data.Source);
            if (!string.IsNullOrEmpty(data.Eligibility))
                details.Add(eligibilityPrefix + data.Eligibility);
            if (!string.IsNullOrEmpty(data.CostImpact)) details.Add(costPrefix + data.CostImpact);
            switch (data.Quote)
            {
                case QuoteState.Waiting: details.Add(quoteWaitingText); break;
                case QuoteState.Valid: details.Add(quoteValidText); break;
                case QuoteState.CannotPay: details.Add(quoteCannotPayText); break;
                case QuoteState.Expired: details.Add(quoteExpiredText); break;
                case QuoteState.NeedsTarget: details.Add(quoteNeedsTargetText); break;
            }
            if (!string.IsNullOrEmpty(data.UnavailableReason))
                details.Add(unavailablePrefix + data.UnavailableReason);
            Bind(data.StableItemId, data.RequestId, data.Revision, data.RowMode,
                data.RightStatus, data.Enabled, data.Pending, data.Title,
                string.Join("\n", details.ToArray()), data.Markers,
                isCurrent, showBody, execute);
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (background == null || frame == null || title == null || description == null || rowLayout == null ||
                bodyButton == null || statusButton == null || statusBackground == null || statusIcon == null ||
                markerSlots == null || markerSlots.Length != 2 || markerImages == null ||
                markerImages.Length != 2 || markerCounts == null || markerCounts.Length != 2 ||
                markerByMask == null || markerByMask.Length != 16 ||
                rowBackgrounds == null || rowBackgrounds.Length != 4 ||
                rowFrames == null || rowFrames.Length != 4 ||
                statusBackgrounds == null || statusBackgrounds.Length != 4 ||
                useIcon == null || disabledUseIcon == null || unusedIcon == null || usedIcon == null)
            {
                reason = "3.4 效果条目引用不完整。";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public void Bind(string stableItemId, string currentRequestId, int currentRevision,
            Mode rowMode, Status rightStatus, bool enabled, bool isPending,
            string heading, string detail, Marker[] markers,
            Func<string, int, bool> isCurrent, Action<string> showBody, Action<string> execute)
        {
            if (externalCommandButton)
                throw new InvalidOperationException("此条目的正式按钮由原入口控制，不能重新绑定执行监听。");
            bodyButton.onClick.RemoveAllListeners();
            statusButton.onClick.RemoveAllListeners();
            itemId = stableItemId;
            requestId = currentRequestId;
            revision = currentRevision;
            mode = rowMode;
            status = rightStatus;
            explicitlyEnabled = enabled;
            pending = isPending;
            hasExecute = execute != null;
            externalCommandButton = false;
            contextIsCurrent = isCurrent;
            title.text = heading ?? string.Empty;
            description.text = detail ?? string.Empty;
            if (markers != null)
            {
                for (var i = 0; i < markers.Length && i < 2; i++)
                {
                    var mask = markers[i].Mask;
                    if (mask < 1 || mask > 15 || markers[i].Count < 0 ||
                        (mask & (mask - 1)) == 0 ||
                        string.IsNullOrEmpty(markers[i].Description)) continue;
                    description.text += (description.text.Length == 0 ? string.Empty : "\n") +
                        markers[i].Description;
                }
            }
            var validMarkers = new System.Collections.Generic.List<Marker>(2);
            if (rowMode != Mode.ChoiceExecute && markers != null)
                for (var i = 0; i < markers.Length && validMarkers.Count < 2; i++)
                    if (markers[i].Mask >= 1 && markers[i].Mask <= 15 && markers[i].Count >= 0)
                        validMarkers.Add(markers[i]);

            bodyButton.interactable = showBody != null;
            for (var i = 0; i < 2; i++)
            {
                var hasMarker = i < validMarkers.Count;
                markerSlots[i].gameObject.SetActive(hasMarker);
                if (!hasMarker) continue;
                var marker = validMarkers[i];
                var singleColor = (marker.Mask & (marker.Mask - 1)) == 0;
                var square = markerImages[i].GetComponent<InfluenceMarker2DView>();
                if (square != null)
                    square.RenderMask(marker.Mask, marker.Count, singleColor);
                else
                {
                    markerImages[i].sprite = markerByMask[marker.Mask];
                    markerCounts[i].gameObject.SetActive(singleColor);
                    markerCounts[i].text = marker.Count.ToString();
                }
            }
            if (showBody != null)
            {
                bodyButton.onClick.AddListener(() =>
                {
                    if (IsCurrent()) showBody(itemId);
                });
            }
            if (execute != null)
            {
                statusButton.onClick.AddListener(() =>
                {
                    if (CanExecute() && IsCurrent()) execute(itemId);
                });
            }
            Refresh();
            if (rowLayout != null) LayoutRebuilder.MarkLayoutForRebuild((RectTransform)rowLayout.transform);
        }

        public void ClearBinding()
        {
            ClearListeners();
            itemId = requestId = null;
            revision = 0;
            contextIsCurrent = null;
            explicitlyEnabled = pending = false;
            hasExecute = false;
            for (var i = 0; markerSlots != null && i < markerSlots.Length; i++)
                if (markerSlots[i] != null) markerSlots[i].gameObject.SetActive(false);
            if (title != null) title.text = string.Empty;
            if (description != null) description.text = string.Empty;
            Refresh();
            if (rowLayout != null) LayoutRebuilder.MarkLayoutForRebuild((RectTransform)rowLayout.transform);
        }

        private void ClearListeners()
        {
            if (bodyButton != null) bodyButton.onClick.RemoveAllListeners();
            if (statusButton != null && !externalCommandButton) statusButton.onClick.RemoveAllListeners();
        }

        private bool IsCurrent() => !string.IsNullOrEmpty(itemId) &&
            contextIsCurrent != null && contextIsCurrent(requestId, revision);
        private bool CanExecute() => status == Status.Use && explicitlyEnabled &&
            !pending && hasExecute && IsCurrent();

        private void LateUpdate() { Refresh(); }
        public void OnPointerEnter(PointerEventData data) { hover = true; Refresh(); }
        public void OnPointerExit(PointerEventData data) { hover = false; Refresh(); }
        public void OnSelect(BaseEventData data) { focus = true; Refresh(); }
        public void OnDeselect(BaseEventData data) { focus = false; Refresh(); }
        private void OnDisable() { hover = focus = false; }

        public void Refresh()
        {
            if (background == null || frame == null || statusButton == null) return;
            var canUse = externalCommandButton ? statusButton.interactable : CanExecute();
            if (!externalCommandButton) statusButton.interactable = canUse;
            var index = !canUse && status == Status.Use ? 3 : hover || focus ? 1 : 0;
            background.sprite = rowBackgrounds[index];
            frame.sprite = rowFrames[index];
            if (status == Status.None)
            {
                statusBackground.gameObject.SetActive(false);
                statusButton.gameObject.SetActive(false);
            }
            else
            {
                statusBackground.gameObject.SetActive(true);
                statusButton.gameObject.SetActive(true);
                statusBackground.sprite = statusBackgrounds[index];
                statusIcon.sprite = status == Status.Use ? (canUse ? useIcon : disabledUseIcon) :
                    status == Status.Used ? usedIcon : unusedIcon;
            }
            if (status != Status.Use) statusButton.interactable = false;
        }
    }
}
