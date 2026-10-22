using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Presentation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>客户端在途回执身份；不得保存到游戏规则或存档中。</summary>
    public readonly struct SettlementSubmissionToken
    {
        public readonly long Scope, Entry, Request;
        public SettlementSubmissionToken(long scope, long entry, long request)
        { Scope = scope; Entry = entry; Request = request; }
    }

    /// <summary>无 Unity 对象依赖的实际绑定状态，来源查看与使用资格分别检查。</summary>
    public sealed class SettlementEffectStripBinding
    {
        public sealed class Entry
        {
            public SettlementOptionView Option { get; internal set; }
            public long Identity { get; internal set; }
            public bool Pending { get; internal set; }
            public long Request { get; internal set; }
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<string, Entry> index = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private string sessionId = string.Empty;
        private long nextEntry, nextRequest;
        public string WindowId { get; private set; } = string.Empty;
        public int ViewerPlayerId { get; private set; } = -1;
        public long Scope { get; private set; }
        public IReadOnlyList<Entry> Entries => entries.AsReadOnly();
        public bool HasVisibleOptions => entries.Count > 0;

        public bool SetScope(string session, int viewer)
        {
            if (string.IsNullOrEmpty(session) || viewer < 0) throw new ArgumentException("结算绑定需要会话身份与有效观察者。");
            if (sessionId == session && ViewerPlayerId == viewer) return false;
            Clear(); sessionId = session; ViewerPlayerId = viewer; return true;
        }

        public void BindWindow(string windowId, IReadOnlyList<SettlementOptionView> options)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new InvalidOperationException("必须先设置结算会话与观察者。");
            if (string.IsNullOrEmpty(windowId)) throw new ArgumentException("结算窗口身份不能为空。", nameof(windowId));
            if (options == null) throw new ArgumentNullException(nameof(options));
            // 先校验完整批次，坏投影不能使原窗口只更新一半。
            var incoming = new Dictionary<string, SettlementOptionView>(StringComparer.Ordinal);
            var ordered = new List<SettlementOptionView>();
            foreach (var option in options)
            {
                if (option == null) throw new ArgumentException("结算投影不能含空条目。");
                if (!option.VisibleToViewer) continue;
                if (string.IsNullOrEmpty(option.OptionId) || option.WindowId != windowId ||
                    (option.EntryKind != "optional_insertion" && option.EntryKind != "cleanup_order") ||
                    (option.EntryKind == "cleanup_order" && string.IsNullOrEmpty(option.TargetEffectId)) ||
                    (option.EntryKind == "optional_insertion" && !HasValidInsertionTarget(option)))
                    throw new ArgumentException("结算选项的窗口、身份或入口类型不完整：" + option.OptionId);
                if (incoming.ContainsKey(option.OptionId)) throw new ArgumentException("结算 OptionId 重复：" + option.OptionId);
                if (option.Presentation == null || option.Presentation.Description == null ||
                    (option.Presentation.ContentMode != "icon" && option.Presentation.Label == null) ||
                    (option.Presentation.ContentMode == "icon" && option.Presentation.AriaLabel == null))
                    throw new ArgumentException("结算选项缺少统一名称或完整说明：" + option.OptionId);
                GameplayPresentationNormalizer.Validate(option.Presentation);
                var copy = Copy(option);
                incoming.Add(copy.OptionId, copy); ordered.Add(copy);
            }
            // 同窗口已知条目的过期版本不能覆盖新的资格与文案。
            if (WindowId == windowId)
                foreach (var option in ordered)
                    if (index.TryGetValue(option.OptionId, out var previous) && option.StateRevision < previous.Option.StateRevision)
                        throw new ArgumentException("结算投影版本已过期：" + option.OptionId);
            if (WindowId != windowId) { Clear(); WindowId = windowId; }
            for (var i = entries.Count - 1; i >= 0; i--)
                if (!incoming.ContainsKey(entries[i].Option.OptionId))
                { index.Remove(entries[i].Option.OptionId); entries.RemoveAt(i); }
            foreach (var entry in entries) entry.Option = incoming[entry.Option.OptionId];
            // 新项按登记顺序加入；同 ID 更新保持原来的位置。
            ordered.Sort((a, b) => {
                var order = a.RegistrationSequence.CompareTo(b.RegistrationSequence);
                return order != 0 ? order : optionsIndex(options, a.OptionId).CompareTo(optionsIndex(options, b.OptionId));
            });
            foreach (var option in ordered)
                if (!index.ContainsKey(option.OptionId))
                {
                    var entry = new Entry { Option = option, Identity = ++nextEntry };
                    entries.Add(entry); index.Add(option.OptionId, entry);
                }
        }

        private static int optionsIndex(IReadOnlyList<SettlementOptionView> options, string id)
        { for (var i = 0; i < options.Count; i++) if (options[i]?.OptionId == id) return i; return int.MaxValue; }
        private static bool HasValidInsertionTarget(SettlementOptionView option)
        {
            var accepted = option.SettlementState == "executing" || option.SettlementState == "resolved";
            if (accepted) return !string.IsNullOrEmpty(option.TargetEffectId) && !option.IsEnabled;
            // 空态仅兼容旧的未接受展示投影；不允许它携带已挂链目标。
            return (string.IsNullOrEmpty(option.SettlementState) || option.SettlementState == "available") &&
                string.IsNullOrEmpty(option.TargetEffectId);
        }

        public bool TryGet(string optionId, out Entry entry) => index.TryGetValue(optionId ?? string.Empty, out entry);

        public bool TryBeginUse(long scope, long identity, int revision, string optionId, out SettlementOptionView option)
        {
            option = null;
            if (!Current(scope, identity, revision, optionId, out var entry) || entry.Pending ||
                !entry.Option.IsEnabled || entry.Option.AnsweringPlayerId != ViewerPlayerId) return false;
            entry.Pending = true; entry.Request = ++nextRequest;
            option = Copy(entry.Option); return true;
        }

        public bool TryInspect(long scope, long identity, int revision, string optionId, out string sourceItemId)
        {
            sourceItemId = string.Empty;
            if (!Current(scope, identity, revision, optionId, out var entry) || !entry.Option.CanInspectSource ||
                string.IsNullOrEmpty(entry.Option.SourceItemId)) return false;
            sourceItemId = entry.Option.SourceItemId; return true;
        }

        public bool TryGetSource(long scope, long identity, int revision, string optionId, out SettlementOptionView source)
        {
            source = null;
            if (!Current(scope, identity, revision, optionId, out var entry) || !entry.Option.CanInspectSource ||
                (string.IsNullOrEmpty(entry.Option.SourceItemId) && string.IsNullOrEmpty(entry.Option.SourceDefinitionId))) return false;
            source = Copy(entry.Option); return true;
        }

        private bool Current(long scope, long identity, int revision, string optionId, out Entry entry)
        {
            entry = null;
            return scope == Scope && TryGet(optionId, out entry) && entry.Identity == identity &&
                entry.Option.StateRevision == revision && entry.Option.VisibleToViewer;
        }

        public SettlementSubmissionToken CapturePending(string optionId)
        {
            if (!TryGet(optionId, out var entry) || !entry.Pending) return default;
            return new SettlementSubmissionToken(Scope, entry.Identity, entry.Request);
        }

        public bool SetPending(string windowId, string optionId, bool pending, SettlementSubmissionToken token)
        {
            if (windowId != WindowId || token.Scope != Scope || !TryGet(optionId, out var entry) ||
                token.Entry != entry.Identity || token.Request != entry.Request || token.Request == 0) return false;
            entry.Pending = pending; return true;
        }

        public bool SetPending(string windowId, string optionId, bool pending)
        {
            if (WindowId != windowId || !TryGet(optionId, out var entry)) return false;
            if (pending && !entry.Pending) entry.Request = ++nextRequest;
            entry.Pending = pending; return true;
        }

        public void Clear()
        { entries.Clear(); index.Clear(); WindowId = string.Empty; unchecked { Scope++; } }

        private static SettlementOptionView Copy(SettlementOptionView s) => new SettlementOptionView {
            WindowId = s.WindowId, LocalId = s.LocalId, EntryKind = s.EntryKind, TargetEffectId = s.TargetEffectId,
            SettlementState = s.SettlementState, RegistrationSequence = s.RegistrationSequence,
            CanDeclineWholeEffect = s.CanDeclineWholeEffect, Presentation = GameplayPresentationNormalizer.Clone(s.Presentation),
            SourceDefinitionId = s.SourceDefinitionId, CanInspectSource = s.CanInspectSource,
            DisabledReason = GameplayPresentationNormalizer.Clone(s.DisabledReason),
            OptionId = s.OptionId, InteractionId = s.InteractionId, OwnerEffectId = s.OwnerEffectId,
            SourceItemId = s.SourceItemId, TargetUseId = s.TargetUseId, CandidateId = s.CandidateId,
            AnsweringPlayerId = s.AnsweringPlayerId, StateRevision = s.StateRevision, CandidateSetVersion = s.CandidateSetVersion,
            RequiredGoldVoucherAmount = s.RequiredGoldVoucherAmount, TitleKey = s.TitleKey, DescriptionKey = s.DescriptionKey,
            DisabledReasonCode = s.DisabledReasonCode, IsEnabled = s.IsEnabled, VisibleToViewer = s.VisibleToViewer
        };
    }

    /// <summary>只实例化序列化模板；缺引用时拒绝绑定，不在运行时补建 HUD。</summary>
    public static class SettlementEffectStripGeometry
    {
        public static float HeaderReservation(float width, bool headerVisible, float reservedWidth, float hideBelowWidth) =>
            headerVisible && width >= hideBelowWidth ? Math.Max(0, reservedWidth) : 0;
    }

    public sealed class SettlementEffectStripView : MonoBehaviour, IScrollHandler
    {
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private RectTransform viewport, content;
        [SerializeField] private HorizontalLayoutGroup rowLayout;
        [SerializeField] private SettlementEffectChipView chipTemplate;
        [SerializeField] private RectTransform tooltipHost;
        [SerializeField] private Component tooltipName, tooltipDescription;
        [SerializeField] private GameObject header;
        [SerializeField, Min(0)] private float headerReservedWidth = 152f, hideHeaderBelowWidth = 520f, horizontalInset = 24f;
        [SerializeField] private bool reducedMotion;
        [SerializeField, Min(0)] private float batchDelay = .06f, maximumBatchDelay = .18f;
        [SerializeField, Min(0)] private float followTailDistance = 12f, manualScrollQuietTime = 1.2f;
        private readonly SettlementEffectStripBinding binding = new SettlementEffectStripBinding();
        private readonly Dictionary<string, SettlementEffectChipView> chips = new Dictionary<string, SettlementEffectChipView>(StringComparer.Ordinal);
        private readonly Stack<SettlementEffectChipView> pool = new Stack<SettlementEffectChipView>();
        private readonly Dictionary<SettlementEffectChipView, float> pendingArrivals = new Dictionary<SettlementEffectChipView, float>();
        private Func<GameplayTextRef, GameplayTextDocument> resolveText;
        private Action<Component, GameplayTextDocument, bool> bindText;
        private Func<Component, GameplayTextDocument, float> measureText;
        private Func<string, string, Sprite> resolveIcon;
        private Action<SettlementOptionView> use;
        private Action<string> inspectSource;
        private Action<SettlementOptionView> inspectAuthorizedSource;
        private bool followAfterLayout;
        private float lastManualScroll = float.NegativeInfinity;
        private string tooltipOptionId = string.Empty;
        public long ScopeToken => binding.Scope;
        public string WindowId => binding.WindowId;
        public int VisibleOptionCount => binding.Entries.Count;
        public bool HasVisibleOptions => binding.HasVisibleOptions;
        public event Action<bool> ButtonsVisibilityChanged;
        private bool reportedButtonsVisible;
        private bool headerVisibilityCaptured, headerWasActive;

        /// <summary>接同一统一文案 resolver/renderer 与精确图标目录。bool 为单行省略模式。</summary>
        public void ConfigurePresentation(Func<GameplayTextRef, GameplayTextDocument> resolver,
            Action<Component, GameplayTextDocument, bool> renderer,
            Func<Component, GameplayTextDocument, float> labelMeasurement, Func<string, string, Sprite> iconResolver)
        {
            resolveText = resolver ?? throw new ArgumentNullException(nameof(resolver));
            bindText = renderer ?? throw new ArgumentNullException(nameof(renderer));
            measureText = labelMeasurement ?? throw new ArgumentNullException(nameof(labelMeasurement));
            resolveIcon = iconResolver ?? throw new ArgumentNullException(nameof(iconResolver));
            foreach (var chip in chips.Values) chip.InvalidatePresentation();
        }

        public void SetScope(string sessionId, int viewerPlayerId)
        {
            if (!binding.SetScope(sessionId, viewerPlayerId)) return;
            ReleaseAll();
            // resolver 闭包也携带授权上下文；换观察者后必须重新注入，不能沿用旧名称字典。
            resolveText = null; bindText = null; measureText = null; resolveIcon = null;
            NotifyButtonsVisibility();
        }

        public bool ValidateReferences(out string reason)
        {
            reason = string.Empty;
            if (scrollRect == null || viewport == null || content == null || rowLayout == null || chipTemplate == null ||
                tooltipHost == null || tooltipName == null || tooltipDescription == null)
            { reason = "结算条序列化引用不完整。"; return false; }
            if (scrollRect.content != content || scrollRect.viewport != viewport || rowLayout.transform != content ||
                !content.IsChildOf(viewport) || !viewport.IsChildOf(transform) || tooltipHost.IsChildOf(viewport) ||
                !scrollRect.horizontal || scrollRect.vertical || scrollRect.horizontalScrollbar != null ||
                rowLayout.childForceExpandWidth || viewport.GetComponent<RectMask2D>() == null ||
                scrollRect.transform == transform || viewport.IsChildOf(scrollRect.transform) ||
                scrollRect.movementType != ScrollRect.MovementType.Clamped ||
                chipTemplate.gameObject.activeSelf || (chipTemplate.transform.parent != content && chipTemplate.gameObject.scene.IsValid()))
            { reason = "结算条的裁切、水平滚动、模板或提示宿主配置不完整。"; return false; }
            return chipTemplate.ValidateReferences(out reason);
        }

        public void BindWindow(string windowId, IReadOnlyList<SettlementOptionView> options,
            Action<SettlementOptionView> onUse, Action<string> onInspectSource)
            => BindWindow(windowId, options, onUse, onInspectSource, null);

        /// <summary>授权来源事实回调优先，可按 SourceDefinitionId 查看已离场来源，不传完整来源实体。</summary>
        public void BindWindow(string windowId, IReadOnlyList<SettlementOptionView> options,
            Action<SettlementOptionView> onUse, Action<string> onInspectSource, Action<SettlementOptionView> onInspectAuthorizedSource)
        {
            if (!ValidateReferences(out var reason)) throw new InvalidOperationException(reason);
            if (resolveText == null || bindText == null || measureText == null || resolveIcon == null)
                throw new InvalidOperationException("结算条尚未连接统一文案与图标展示端口。");
            var previousScope = binding.Scope;
            var wasAtTail = TailDistance() <= followTailDistance;
            binding.BindWindow(windowId, options);
            if (previousScope != binding.Scope) ReleaseAll();
            use = onUse; inspectSource = onInspectSource; inspectAuthorizedSource = onInspectAuthorizedSource;
            ApplyViewportLayout();
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in binding.Entries) live.Add(entry.Option.OptionId);
            var removed = new List<string>();
            foreach (var id in chips.Keys) if (!live.Contains(id)) removed.Add(id);
            foreach (var id in removed) Release(id);
            var added = 0;
            var position = 0;
            try
            {
                foreach (var entry in binding.Entries)
                {
                    var id = entry.Option.OptionId;
                    var isNew = !chips.TryGetValue(id, out var chip);
                    if (isNew)
                    {
                        chip = pool.Count > 0 ? pool.Pop() : Instantiate(chipTemplate, content, false);
                        chips.Add(id, chip);
                    }
                    chip.transform.SetSiblingIndex(++position); // 停用模板与池中实例不参与实际布局。
                    chip.Bind(entry.Option, entry.Pending, entry.Option.IsEnabled && entry.Option.AnsweringPlayerId == binding.ViewerPlayerId && onUse != null,
                        binding.Scope, entry.Identity, viewport.rect.width,
                        resolveText, bindText, measureText, resolveIcon, Use, Inspect, ShowTooltip, HideTooltip, scrollRect, ManualScroll);
                    chip.gameObject.SetActive(true);
                    if (isNew)
                    {
                        chip.PrepareArrival(reducedMotion);
                        pendingArrivals.Add(chip, Mathf.Min(added++ * batchDelay, maximumBatchDelay));
                    }
                }
            }
            catch { ClearWindow(); throw; } // 展示端口或模板失败时关闭整批，避免留半个可提交的窗口。
            if (added > 0 && (previousScope != binding.Scope || wasAtTail) && Time.unscaledTime - lastManualScroll >= manualScrollQuietTime) followAfterLayout = true;
            HideTooltip();
            // 批量完成后只显式提交一次布局请求；没有逐项强制重建。
            LayoutRebuilder.MarkLayoutForRebuild(content);
            NotifyButtonsVisibility();
        }

        public SettlementSubmissionToken CapturePending(string optionId) => binding.CapturePending(optionId);

        /// <summary>当前绑定的同步显示更新。异步命令回执使用带 token 的重载。</summary>
        public void SetPending(string windowId, string optionId, bool pending)
        { if (binding.SetPending(windowId, optionId, pending)) RefreshPending(optionId); }

        public void SetPending(string windowId, string optionId, bool pending, SettlementSubmissionToken token)
        { if (binding.SetPending(windowId, optionId, pending, token)) RefreshPending(optionId); }

        private void RefreshPending(string id)
        { if (chips.TryGetValue(id, out var chip) && binding.TryGet(id, out var entry)) chip.SetPending(entry.Pending); }

        private void Use(long scope, long identity, int revision, string id)
        {
            if (use == null || !binding.TryBeginUse(scope, identity, revision, id, out var option)) return;
            var token = binding.CapturePending(id);
            RefreshPending(id); HideTooltip();
            try { use(option); }
            catch { SetPending(option.WindowId, id, false, token); throw; }
        }

        private void Inspect(long scope, long identity, int revision, string id)
        {
            if (inspectAuthorizedSource != null)
            {
                if (binding.TryGetSource(scope, identity, revision, id, out var source)) inspectAuthorizedSource(source);
                return;
            }
            if (inspectSource != null && binding.TryInspect(scope, identity, revision, id, out var itemId)) inspectSource(itemId);
        }

        private void ShowTooltip(string id, GameplayTextDocument label, GameplayTextDocument description)
        {
            if (!binding.TryGet(id, out _)) return;
            tooltipOptionId = id;
            bindText(tooltipName, label, false); bindText(tooltipDescription, description, false);
            tooltipHost.gameObject.SetActive(true);
        }

        private void HideTooltip()
        {
            tooltipOptionId = string.Empty;
            if (tooltipHost != null) tooltipHost.gameObject.SetActive(false);
            if (bindText != null)
            {
                if (tooltipName != null) bindText(tooltipName, null, false);
                if (tooltipDescription != null) bindText(tooltipDescription, null, false);
            }
        }

        private void Release(string id)
        {
            var chip = chips[id]; chips.Remove(id);
            pendingArrivals.Remove(chip);
            chip.ClearBinding(); chip.gameObject.SetActive(false); pool.Push(chip);
            if (tooltipOptionId == id) HideTooltip();
        }

        private void ReleaseAll()
        {
            foreach (var chip in chips.Values)
            { chip.ClearBinding(); chip.gameObject.SetActive(false); pool.Push(chip); }
            chips.Clear(); use = null; inspectSource = null; inspectAuthorizedSource = null; followAfterLayout = false;
            pendingArrivals.Clear();
            lastManualScroll = float.NegativeInfinity; HideTooltip();
            if (scrollRect != null) { scrollRect.StopMovement(); scrollRect.horizontalNormalizedPosition = 0; }
        }

        public void ClearWindow() { binding.Clear(); ReleaseAll(); NotifyButtonsVisibility(); }
        private void NotifyButtonsVisibility()
        {
            if (reportedButtonsVisible == HasVisibleOptions) return;
            reportedButtonsVisible = HasVisibleOptions;
            ButtonsVisibilityChanged?.Invoke(reportedButtonsVisible);
        }
        private void OnEnable()
        {
            if (header != null) { headerWasActive = header.activeSelf; headerVisibilityCaptured = true; }
            ApplyViewportLayout();
        }
        private void OnDisable()
        {
            Canvas.willRenderCanvases -= CompleteLayout;
            ClearWindow();
            if (headerVisibilityCaptured && header != null) header.SetActive(headerWasActive);
            headerVisibilityCaptured = false;
        }

        private void ApplyViewportLayout()
        {
            if (viewport == null || !(transform is RectTransform slot)) return;
            bool wantsHeader = headerVisibilityCaptured ? headerWasActive : header != null && header.activeSelf;
            var reserved = SettlementEffectStripGeometry.HeaderReservation(slot.rect.width, wantsHeader, headerReservedWidth, hideHeaderBelowWidth);
            if (isActiveAndEnabled && headerVisibilityCaptured && header != null)
            {
                bool show = wantsHeader && slot.rect.width >= hideHeaderBelowWidth;
                if (header.activeSelf != show) header.SetActive(show);
            }
            // 只响应自身尺寸和序列化配置；保留Prefab提供的纵向中心、槽高和按钮高。
            var minimum = viewport.offsetMin; var maximum = viewport.offsetMax;
            float left = reserved + horizontalInset, right = -horizontalInset;
            if (!Mathf.Approximately(minimum.x, left)) { minimum.x = left; viewport.offsetMin = minimum; }
            if (!Mathf.Approximately(maximum.x, right)) { maximum.x = right; viewport.offsetMax = maximum; }
        }

        private void OnRectTransformDimensionsChange()
        {
            if (viewport == null || content == null) return;
            ApplyViewportLayout();
            foreach (var chip in chips.Values) chip.FitWidth(viewport.rect.width);
            if (chips.Count > 0) LayoutRebuilder.MarkLayoutForRebuild(content);
        }

        private void LateUpdate()
        {
            if (!followAfterLayout && pendingArrivals.Count == 0) return;
            // 本帧所有布局结束后再跟随，避免用上一批 Content 宽度设置尾端。
            Canvas.willRenderCanvases -= CompleteLayout;
            Canvas.willRenderCanvases += CompleteLayout;
        }

        private void CompleteLayout()
        {
            if (CanvasUpdateRegistry.IsRebuildingLayout() || viewport == null || viewport.rect.width <= 0) return;
            Canvas.willRenderCanvases -= CompleteLayout;
            if (followAfterLayout && scrollRect != null && Time.unscaledTime - lastManualScroll >= manualScrollQuietTime)
                scrollRect.horizontalNormalizedPosition = 1;
            followAfterLayout = false;
            foreach (var arrival in pendingArrivals)
            {
                var chip = arrival.Key;
                if (chip == null || !chip.gameObject.activeInHierarchy) continue;
                var rect = (RectTransform)chip.transform;
                var left = viewport.InverseTransformPoint(rect.TransformPoint(new Vector3(rect.rect.xMin, 0, 0))).x - viewport.rect.xMin;
                // 已包含content的实际滚动位移；只动画Visual，布局根和来源顺序不动。
                chip.PlayArrival(reducedMotion, arrival.Value, viewport.rect.width, left);
            }
            pendingArrivals.Clear();
        }

        private void OnDestroy() { Canvas.willRenderCanvases -= CompleteLayout; }
        private float TailDistance() => content == null || viewport == null || scrollRect == null ? 0 :
            Mathf.Max(0, content.rect.width - viewport.rect.width) * (1 - Mathf.Clamp01(scrollRect.horizontalNormalizedPosition));
        private void ManualScroll() { lastManualScroll = Time.unscaledTime; followAfterLayout = false; HideTooltip(); }

        public void OnScroll(PointerEventData eventData)
        {
            if (scrollRect == null || content == null || viewport == null || eventData == null) return;
            var delta = Mathf.Abs(eventData.scrollDelta.x) > Mathf.Abs(eventData.scrollDelta.y)
                ? eventData.scrollDelta.x : -eventData.scrollDelta.y;
            var position = scrollRect.horizontalNormalizedPosition;
            if (content.rect.width > viewport.rect.width &&
                ((delta < 0 && position < 1) || (delta > 0 && position > 0)))
            { ManualScroll(); scrollRect.OnScroll(eventData); return; }
            // strip 与 ScrollRect 分属不同物体；本组件统一接收滚轮，边界事件交还外层。
            var parent = transform.parent;
            while (parent != null)
            {
                if (parent.GetComponent<IScrollHandler>() != null)
                { ExecuteEvents.Execute(parent.gameObject, eventData, ExecuteEvents.scrollHandler); return; }
                parent = parent.parent;
            }
        }
    }
}
