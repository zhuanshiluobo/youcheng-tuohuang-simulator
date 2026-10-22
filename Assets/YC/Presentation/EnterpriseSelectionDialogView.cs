using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Presentation;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>企业／公共科室的资产化表现；选择只能经绑定的请求网关提交。</summary>
    public sealed class EnterpriseSelectionDialogView : MonoBehaviour, IPointerClickHandler
    {
        [Serializable] private sealed class ModeText
        {
            public string title, confirm;
        }
        [SerializeField] private EnterpriseBoardCatalog catalog;
        [SerializeField] private EnterpriseBoardItemView boardTemplate;
        [SerializeField] private EnterpriseBoardItemView fixedCompany;
        [SerializeField] private RectTransform panel, body, candidateContent;
        [SerializeField] private ScrollRect candidateScroll, descriptionScroll;
        [SerializeField] private Text title, context, summary, detailTitle, description, cost;
        [SerializeField] private GameplayTextView descriptionDocument, detailTitleDocument;
        [SerializeField] private RawImage detailImage, enlargedImage;
        [SerializeField] private AspectRatioFitter detailAspect, enlargedAspect;
        [SerializeField] private EnterpriseDepartmentOverlayView enlargedDepartmentOverlay;
        [SerializeField] private GameObject detailPage, emptyState;
        [SerializeField] private GameObject footer;
        [SerializeField] private GameObject detailsReturnSurface;
        [SerializeField] private Button confirm, cancel, back;
        [SerializeField] private Text confirmLabel;
        [SerializeField] private Button closeButton, excerptButton;
        [SerializeField] private Button browseReturnButton;
        [SerializeField] private string browseContext;
        [SerializeField] private Text cancelLabel;
        [SerializeField] private Text detailStatus;
        [SerializeField] private Text cooperationLevel;
        [SerializeField] private string browsingStatus, selectedStatus, availableStatus, unavailableStatus;
        [SerializeField] private string browseTitle, browseCloseLabel, selectionCancelLabel, cooperationLevelFormat;
        [SerializeField] private bool enterprisePickerLayout;
        [SerializeField] private ModeText[] modeText;
        [SerializeField] private string missingRulesText;
        [SerializeField] private string currentDepartmentText;
        [SerializeField] private string selectedFormat;
        [SerializeField] private string selectPrompt;
        [SerializeField] private string pendingText;
        [SerializeField] private string readOnlyText = "公开企业与共享科室";
        [SerializeField] private string multiSelectedFormat = "已选择 {0}，需要 {1}—{2} 项";
        private readonly EnterpriseSelectionDraft draft = new EnterpriseSelectionDraft();
        private readonly Dictionary<string, EnterpriseBoardItemView> items = new Dictionary<string, EnterpriseBoardItemView>();
        private Func<EnterpriseSelectionProjection> latestProjection;
        private Action<EnterpriseSelectionIntent> submit;
        private Action cancelRequest;
        private bool effectPage;
        private GameObject savedFocus;
        private string enlargedBoardId;
        private Action<string> openDepartment;
        private Action returnToEnterprises;
        private Func<IReadOnlyList<GameplayTextRef>, GameplayTextDocument> resolveDetails;
        private Action<Component, GameplayTextDocument, bool> renderDetails;
        private string renderedDetailTitle;
        private GameplayTextDocument renderedDetailTitleDocument;
        private bool navigationSaved;
        private string navigationRequestId, navigationInspectedId;
        private int navigationViewer;
        private Vector2 navigationCandidatePosition, navigationDescriptionPosition;
        private GameObject navigationFocus;
        private int detailsOpenedFrame = -1, detailsReturnedFrame = -1;
        private Vector2 detailsScrollPosition;
        public EnterpriseSelectionDraft Draft => draft;
        public RectTransform Panel => panel;
        public RectTransform Body => body;
        public ScrollRect CandidateScroll => candidateScroll;

        public bool TryValidateConfiguration(out string reason)
        {
            if (catalog == null || boardTemplate == null || panel == null || body == null || candidateContent == null ||
                candidateScroll == null || title == null || context == null || summary == null ||
                enlargedImage == null || detailPage == null || emptyState == null ||
                enlargedDepartmentOverlay == null || !enlargedDepartmentOverlay.IsConfigured ||
                (enterprisePickerLayout && !boardTemplate.HasDepartmentOverlay) ||
                (fixedCompany != null && (!fixedCompany.HasDepartmentOverlay || browseReturnButton == null)) ||
                detailsReturnSurface == null ||
                (enterprisePickerLayout && (closeButton == null || description == null || descriptionScroll == null || detailTitle == null || detailTitleDocument == null)) ||
                confirm == null || cancel == null || back == null || confirmLabel == null || modeText == null ||
                modeText.Length != Enum.GetValues(typeof(EnterpriseSelectionMode)).Length || modeText.Any(value => value == null))
            {
                var fields = GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                reason = "企业／科室选择窗口引用不完整：" + string.Join("、", fields.Where(f =>
                    typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType) && (UnityEngine.Object)f.GetValue(this) == null)
                    .Select(f => f.Name));
                return false;
            }
            return catalog.TryValidateConfiguration(out reason);
        }

        private void Awake()
        {
            confirm.onClick.AddListener(Confirm);
            cancel.onClick.AddListener(Cancel);
            back.onClick.AddListener(ReturnFromDetails);
            closeButton?.onClick.AddListener(Cancel);
            excerptButton?.onClick.AddListener(() => OpenDetails(draft.Inspected?.BoardId));
            browseReturnButton?.onClick.AddListener(ReturnToEnterprises);
            confirm.interactable = false;
        }

        // 后续正式 adapter 必须提供当前玩家可见投影和现有命令网关；这里不注册虚构的请求类型。
        public void BindSubmission(Func<EnterpriseSelectionProjection> current, Action<EnterpriseSelectionIntent> send,
            Action cancelAction)
        { latestProjection = current; submit = send; cancelRequest = cancelAction; Render(); }

        public void BindBrowseNavigation(Action<string> browseDepartment, Action returnAction)
        { openDepartment = browseDepartment; returnToEnterprises = returnAction; }

        public void BindGameplayText(Func<IReadOnlyList<GameplayTextRef>, GameplayTextDocument> resolve,
            Action<Component, GameplayTextDocument, bool> render)
        { resolveDetails = resolve; renderDetails = render; }

        public bool TryValidateGameplayTextConfiguration(out string reason)
        {
            reason = descriptionDocument == null ? "企业／科室正文的 GameplayTextView 引用缺失。" :
                description == null || descriptionDocument.GetComponent<Text>() != description ?
                    "企业／科室正文宿主与原正文 Text 不一致。" :
                enterprisePickerLayout && detailTitleDocument == null ? "企业详情标题的 GameplayTextView 引用缺失。" :
                detailTitleDocument != null && detailTitleDocument.GetComponent<Text>() != detailTitle ?
                    "企业详情标题宿主与原标题 Text 不一致。" : null;
            return reason == null;
        }

        public void SuspendForNavigation()
        {
            if (draft.Projection?.ReadOnly != true) return;
            navigationSaved = true;
            navigationRequestId = draft.Projection.RequestId;
            navigationViewer = draft.Projection.ViewerPlayerId;
            navigationInspectedId = draft.InspectedId;
            candidateScroll.StopMovement();
            descriptionScroll?.StopMovement();
            navigationCandidatePosition = candidateScroll.content.anchoredPosition;
            navigationDescriptionPosition = descriptionScroll == null ? Vector2.zero : descriptionScroll.content.anchoredPosition;
            navigationFocus = EventSystem.current?.currentSelectedGameObject;
        }

        public void ResumeFromNavigation(EnterpriseSelectionProjection projection)
        {
            bool restore = navigationSaved && projection != null && projection.RequestId == navigationRequestId &&
                           projection.ViewerPlayerId == navigationViewer;
            if (restore && projection.Targets.Any(target => target.Id == navigationInspectedId)) draft.Inspect(navigationInspectedId);
            Present(projection);
            if (restore && draft.Projection?.RequestId == projection.RequestId)
            {
                Canvas.ForceUpdateCanvases();
                candidateScroll.StopMovement();
                candidateScroll.content.anchoredPosition = navigationCandidatePosition;
                if (descriptionScroll != null)
                {
                    descriptionScroll.StopMovement();
                    descriptionScroll.content.anchoredPosition = navigationDescriptionPosition;
                }
                if (navigationFocus != null && navigationFocus.activeInHierarchy && EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(navigationFocus);
            }
            navigationSaved = false;
            navigationFocus = null;
            detailsReturnedFrame = Time.frameCount;
        }

        public void Present(EnterpriseSelectionProjection projection, bool fromEffectRequest = false)
        {
            if (projection == null) { ClearPresentation(); return; }
            bool newRequest = draft.Projection == null || draft.Projection.RequestId != projection.RequestId ||
                draft.Projection.Mode != projection.Mode || draft.Projection.ViewerPlayerId != projection.ViewerPlayerId;
            if (!draft.Refresh(projection)) return;
            if (projection.ReadOnly && draft.Inspected == null)
            {
                var first = projection.Targets.FirstOrDefault(target => !IsCurrentDepartment(target.BoardId));
                if (first != null) draft.Inspect(ResolveInspectionId(first.Id));
            }
            effectPage = fromEffectRequest && !projection.ReadOnly && projection.Mode != EnterpriseSelectionMode.BrowseDepartment;
            if (newRequest)
            {
                enlargedBoardId = null; detailPage.SetActive(false); body.gameObject.SetActive(true);
                candidateScroll.StopMovement(); candidateScroll.horizontalNormalizedPosition = 0;
            }
            Render();
            if (GameplayHudFrame.Active != null)
            {
                if (projection.ReadOnly) GameplayHudFrame.Active.ShowInformationPage(gameObject);
                else GameplayHudFrame.Active.ShowPage(gameObject, effectPage);
            }
            else gameObject.SetActive(true);
        }

        public void ClearPresentation()
            => ClearPresentationCore(false);
        public void ClearForNavigation()
            => ClearPresentationCore(true);
        private void ClearPresentationCore(bool preserveInformation)
        {
            latestProjection = null; submit = null; cancelRequest = null;
            openDepartment = null; returnToEnterprises = null;
            Close(false, preserveInformation);
            foreach (var item in items.Values) EnterpriseBoardItemView.Retire(item.gameObject);
            items.Clear();
            if (description != null) description.text = string.Empty;
            if (descriptionDocument != null) descriptionDocument.ClearBinding();
            if (cost != null) cost.text = string.Empty;
            if (detailTitle != null) detailTitle.text = string.Empty;
            if (detailTitleDocument != null) detailTitleDocument.ClearBinding();
            context.text = summary.text = string.Empty;
            if (detailImage != null) detailImage.texture = null;
            enlargedImage.texture = null;
            enlargedDepartmentOverlay.Bind(null, catalog);
            detailPage.SetActive(false);
            if (detailsReturnSurface != null) detailsReturnSurface.SetActive(false);
        }

        public void ShowUnavailable(EnterpriseSelectionMode mode)
        {
            if (GameplayHudFrame.Active != null) GameplayHudFrame.Active.ReleasePage(gameObject);
            BindSubmission(null, null, null);
            Present(new EnterpriseSelectionProjection { Mode = mode, ReadOnly = mode == EnterpriseSelectionMode.BrowseDepartment,
                CanCancel = true, UnavailableReason = missingRulesText });
        }

        public void Choose(string id)
        {
            if (!isActiveAndEnabled || detailPage.activeSelf || Time.frameCount == detailsReturnedFrame) return;
            var target = draft.Projection?.Targets.FirstOrDefault(value => value.Id == id);
            if (target == null || IsCurrentDepartment(target.BoardId)) return;
            draft.Select(ResolveInspectionId(id)); Render();
        }

        public void Inspect(string id)
        {
            if (!isActiveAndEnabled || draft.IsPending || detailPage.activeSelf || Time.frameCount == detailsReturnedFrame) return;
            var target = draft.Projection?.Targets.FirstOrDefault(value => value.Id == id);
            if (target == null || IsCurrentDepartment(target.BoardId)) return;
            draft.Inspect(ResolveInspectionId(id)); Render();
        }

        private bool IsCurrentDepartment(string boardId)
        {
            var p = draft.Projection;
            return p != null && (p.Mode == EnterpriseSelectionMode.InitialDepartment || p.Mode == EnterpriseSelectionMode.SwitchDepartment ||
                p.Mode == EnterpriseSelectionMode.BrowseDepartment) && !string.IsNullOrEmpty(p.CurrentDepartmentId) &&
                string.Equals(boardId, p.CurrentDepartmentId, StringComparison.Ordinal);
        }

        private EnterpriseBoardProjection PublicDepartmentOwner(string boardId)
        {
            if (string.IsNullOrEmpty(boardId)) return null;
            var p = draft.Projection;
            return p?.ReadOnly == true && p.Mode == EnterpriseSelectionMode.Company
                ? p.Boards.FirstOrDefault(board => (board.SupportsDepartmentBrowsing || board.ActiveDepartment != null) &&
                    (board.Id == boardId || board.ActiveDepartment?.ItemId == boardId)) : null;
        }

        private string ResolveInspectionId(string targetId)
        {
            var p = draft.Projection;
            var target = p?.Targets.FirstOrDefault(value => value.Id == targetId);
            var owner = PublicDepartmentOwner(target?.BoardId);
            return owner == null ? targetId : p.Targets.FirstOrDefault(value => value.BoardId == owner.Id)?.Id ?? targetId;
        }

        private void InspectDepartment(string itemId)
        {
            if (!isActiveAndEnabled || draft.IsPending || detailPage.activeSelf || Time.frameCount == detailsReturnedFrame ||
                PublicDepartmentOwner(itemId) == null) return;
            var p = draft.Projection;
            var owner = PublicDepartmentOwner(itemId);
            var target = p.Targets.FirstOrDefault(value => value.BoardId == owner.Id);
            if (target != null) draft.Inspect(target.Id);
            Render();
        }

        public void Complete(EnterpriseSelectionIntent intent, bool accepted, EnterpriseSelectionProjection refreshed)
        {
            if (!draft.Complete(intent)) return;
            // 即使成功回包在调用时带来新请求，也不能关闭新页面。
            if (refreshed != null && (refreshed.RequestId != intent.RequestId || refreshed.Mode != intent.Mode ||
                refreshed.Revision > intent.Revision))
            { Present(refreshed, effectPage); return; }
            if (refreshed != null && refreshed.Revision < intent.Revision) { Render(); return; }
            if (accepted) { Close(); return; }
            if (refreshed == null) { ShowUnavailable(intent.Mode); return; }
            draft.Refresh(refreshed); Render();
        }

        public void OpenDetails(string boardId)
        {
            if (!isActiveAndEnabled || draft.IsPending || Time.frameCount == detailsReturnedFrame ||
                IsCurrentDepartment(boardId) || IsFixedCompany(boardId)) return;
            var owner = PublicDepartmentOwner(boardId);
            if (owner != null)
            {
                // 查看导航保留进入前的右栏、选择和滚动；普通单击才改变查看目标。
                openDepartment?.Invoke(owner.Id);
                return;
            }
            if (!BindEnlargedBoard(boardId)) return;
            candidateScroll.StopMovement();
            detailsScrollPosition = candidateScroll.content.anchoredPosition;
            savedFocus = EventSystem.current?.currentSelectedGameObject;
            enlargedBoardId = boardId;
            detailsOpenedFrame = Time.frameCount;
            body.gameObject.SetActive(false); detailPage.SetActive(true); back.gameObject.SetActive(true);
            cancel.gameObject.SetActive(false);
            if (browseReturnButton != null) browseReturnButton.gameObject.SetActive(false);
            confirm.interactable = false;
            if (detailsReturnSurface != null) detailsReturnSurface.SetActive(true);
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(back.gameObject.activeInHierarchy ? back.gameObject : closeButton?.gameObject);
        }

        private bool BindEnlargedBoard(string boardId)
        {
            if (string.IsNullOrEmpty(boardId) || IsCurrentDepartment(boardId) || IsFixedCompany(boardId)) return false;
            var p = draft.Projection;
            var board = p?.Boards.FirstOrDefault(value => value.Id == boardId);
            if (board == null) return false;
            EnterpriseBoardCatalog.Bind(enlargedImage, enlargedAspect, catalog.Find(board.VisualKey));
            enlargedDepartmentOverlay.Bind(board.ActiveDepartment, catalog);
            return true;
        }

        private bool IsFixedCompany(string itemId) => !string.IsNullOrEmpty(itemId) &&
            (draft.Projection?.FixedCompany?.Id == itemId || draft.Projection?.FixedCompany?.ActiveDepartment?.ItemId == itemId);

        public void ReturnFromDetails()
        {
            if (!detailPage.activeSelf) return;
            detailsReturnedFrame = Time.frameCount;
            enlargedBoardId = null; detailPage.SetActive(false); body.gameObject.SetActive(true);
            back.gameObject.SetActive(false); Render();
            candidateScroll.StopMovement();
            candidateScroll.content.anchoredPosition = detailsScrollPosition;
            if (savedFocus != null && savedFocus.activeInHierarchy && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(savedFocus);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (detailsReturnSurface == null || !detailPage.activeSelf || eventData == null ||
                eventData.button != PointerEventData.InputButton.Left) return;
            eventData.Use();
            if (Time.frameCount != detailsOpenedFrame) ReturnFromDetails();
        }

        private void Confirm()
        {
            if (!isActiveAndEnabled || detailPage.activeSelf || draft.Projection?.ReadOnly == true ||
                draft.Projection?.Mode == EnterpriseSelectionMode.BrowseDepartment || latestProjection == null || submit == null) return;
            var current = latestProjection();
            if (current == null) { ShowUnavailable(draft.Projection.Mode); return; }
            var previousRequest = draft.Projection;
            bool changed = !EnterpriseSelectionDraft.SameContract(current, previousRequest);
            if (!draft.Refresh(current)) return;
            if (changed) { Render(); return; }
            if (!draft.TryBeginSubmit(out var intent)) { Render(); return; }
            Render();
            submit(intent);
        }

        private void Cancel()
        {
            if (draft.Projection == null || !draft.Projection.CanCancel || draft.IsPending) return;
            if (draft.Projection.Mode == EnterpriseSelectionMode.BrowseDepartment) { ReturnToEnterprises(); return; }
            if (latestProjection != null)
            {
                var p = latestProjection();
                if (p == null || !EnterpriseSelectionDraft.SameContract(p, draft.Projection) || !p.CanCancel)
                { if (p != null) Present(p, effectPage); return; }
                cancelRequest?.Invoke();
                return;
            }
            Close(true); // 用户离开资料导航；不取消被覆盖的规则请求。
        }

        private void Close(bool returnFromInformation = false, bool preserveInformation = false)
        {
            var readOnly = draft.Projection?.ReadOnly == true;
            navigationSaved = false; navigationFocus = null;
            draft.Clear();
            if (GameplayHudFrame.Active != null)
            {
                if (readOnly && returnFromInformation) GameplayHudFrame.Active.CloseInformationPage(gameObject);
                else if (readOnly && !preserveInformation) GameplayHudFrame.Active.AbandonInformationPage(gameObject);
                else GameplayHudFrame.Active.ReleasePage(gameObject);
            }
            else gameObject.SetActive(false);
        }

        private void ReturnToEnterprises()
        {
            if (!isActiveAndEnabled || draft.Projection?.Mode != EnterpriseSelectionMode.BrowseDepartment ||
                !draft.Projection.ReadOnly || draft.IsPending || detailPage.activeSelf || Time.frameCount == detailsReturnedFrame) return;
            if (returnToEnterprises != null) returnToEnterprises(); else Close();
        }

        private void Update()
        {
            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            if (detailPage.activeSelf) ReturnFromDetails(); else Cancel();
        }

        private void OnEnable()
        {
            // 展开 effect 前再次取得宿主投影，不能仅凭隐藏前的草稿恢复。
            if (latestProjection == null || draft.Projection == null) return;
            var current = latestProjection();
            if (current == null) { Close(); return; }
            if (draft.Refresh(current)) Render();
        }

        private void Render()
        {
            var p = draft.Projection;
            if (p == null) return;
            var text = modeText[(int)p.Mode];
            title.text = p.ReadOnly && !string.IsNullOrEmpty(browseTitle) ? browseTitle : text.title;
            confirmLabel.text = text.confirm;
            if (cancelLabel != null) cancelLabel.text = p.ReadOnly ? browseCloseLabel : selectionCancelLabel;
            bool departmentBrowse = p.Mode == EnterpriseSelectionMode.BrowseDepartment;
            context.text = departmentBrowse ? browseContext : p.Context ?? string.Empty;
            var valid = new HashSet<string>();
            var attached = new HashSet<string>();
            if (p.ReadOnly && boardTemplate.HasDepartmentOverlay)
                foreach (var board in p.Boards)
                    if (board.ActiveDepartment != null && catalog.Find(board.ActiveDepartment.VisualKey)?.texture != null)
                        attached.Add(board.ActiveDepartment.ItemId);
            foreach (var board in p.Boards)
            {
                // 仅组合显示，投影中的板、目标和稳定 ID 均保留。
                if (attached.Contains(board.Id) || IsCurrentDepartment(board.Id)) continue;
                valid.Add(board.Id);
                if (!items.TryGetValue(board.Id, out var item))
                { item = Instantiate(boardTemplate, candidateContent, false); item.gameObject.SetActive(true); items.Add(board.Id, item); }
                item.transform.SetAsLastSibling();
                item.BindWithActiveDepartment(board, catalog, draft, Choose, OpenDetails,
                    p.ReadOnly || p.Mode == EnterpriseSelectionMode.Company || p.Mode == EnterpriseSelectionMode.Effect, InspectDepartment);
            }
            foreach (var id in new List<string>(items.Keys))
                if (!valid.Contains(id)) { EnterpriseBoardItemView.Retire(items[id].gameObject); items.Remove(id); }
            if (fixedCompany != null)
            {
                fixedCompany.gameObject.SetActive(p.FixedCompany != null);
                if (p.FixedCompany != null) fixedCompany.BindWithActiveDepartment(p.FixedCompany, catalog, draft, null, null, true, null);
            }
            if (enlargedBoardId != null && !BindEnlargedBoard(enlargedBoardId))
            { enlargedBoardId = null; detailsReturnedFrame = Time.frameCount; detailPage.SetActive(false); body.gameObject.SetActive(true); }
            back.gameObject.SetActive(detailPage.activeSelf);
            if (detailsReturnSurface != null) detailsReturnSurface.SetActive(detailPage.activeSelf);
            emptyState.SetActive(valid.Count == 0);
            if (draft.Inspected != null) draft.Inspect(ResolveInspectionId(draft.Inspected.Id));
            var inspected = draft.Inspected;
            var boardInfo = inspected == null ? null : p.Boards.FirstOrDefault(b => b.Id == inspected.BoardId);
            var art = boardInfo == null ? null : catalog.Find(boardInfo.VisualKey);
            var excerpt = art?.companyExcerpt ?? default;
            if (art != null && p.Mode == EnterpriseSelectionMode.Effect && inspected != null)
                excerpt = art.levelExcerpts != null && inspected.Tier < art.levelExcerpts.Length
                    ? art.levelExcerpts[inspected.Tier] : new Rect(inspected.X, inspected.Y, inspected.Width, inspected.Height);
            if (detailImage != null) EnterpriseBoardCatalog.BindExcerpt(detailImage, detailAspect, art, excerpt);
            if (excerptButton != null) excerptButton.interactable = art != null;
            RenderDetailTitle(inspected?.Label);
            if (detailStatus != null) detailStatus.text =
                inspected == null ? string.Empty : p.ReadOnly ? browsingStatus :
                !inspected.Available ? unavailableStatus : draft.IsSelected(inspected.Id) ? selectedStatus : availableStatus;
            RenderDescription(inspected, p);
            if (cost != null) cost.text = inspected?.Cost ?? string.Empty;
            if (cooperationLevel != null) cooperationLevel.text = string.Empty;
            var levelBoard = p.FixedCompany ?? boardInfo;
            int viewer = p.ViewerPlayerId != 0 ? p.ViewerPlayerId : levelBoard?.ViewerPlayerId ?? 0;
            int playerIndex = levelBoard?.PlayerIds == null ? -1 : Array.IndexOf(levelBoard.PlayerIds, viewer);
            if (cooperationLevel != null && !string.IsNullOrEmpty(cooperationLevelFormat) && playerIndex >= 0 &&
                levelBoard.PlayerLevels != null && playerIndex < levelBoard.PlayerLevels.Length && levelBoard.PlayerLevels[playerIndex] >= 0)
                cooperationLevel.text = string.Format(cooperationLevelFormat, levelBoard.PlayerLevels[playerIndex]);
            if (detailTitle != null) detailTitle.gameObject.SetActive(!string.IsNullOrEmpty(detailTitle.text));
            if (cost != null) cost.gameObject.SetActive(!string.IsNullOrEmpty(cost.text));
            if (cooperationLevel != null) cooperationLevel.gameObject.SetActive(!string.IsNullOrEmpty(cooperationLevel.text));
            RefreshSubmissionControls(p);
        }

        public void RefreshSubmissionAvailability(bool available)
        {
            var projection = draft.Projection;
            if (projection == null || projection.SubmissionAvailable == available) return;
            projection.SubmissionAvailable = available;
            RefreshSubmissionControls(projection);
        }

        private void RefreshSubmissionControls(EnterpriseSelectionProjection p)
        {
            bool departmentBrowse = p.Mode == EnterpriseSelectionMode.BrowseDepartment;
            bool bound = latestProjection != null && submit != null;
            confirm.interactable = bound && draft.CanConfirm && !detailPage.activeSelf;
            confirm.gameObject.SetActive(!p.ReadOnly);
            cancel.gameObject.SetActive(!p.ReadOnly && p.CanCancel && !detailPage.activeSelf);
            bool canClose = p.CanCancel && !draft.IsPending && (latestProjection == null || cancelRequest != null);
            cancel.interactable = canClose;
            if (closeButton != null) closeButton.interactable = canClose;
            if (footer != null) footer.SetActive(!p.ReadOnly || departmentBrowse);
            if (browseReturnButton != null)
            {
                browseReturnButton.gameObject.SetActive(departmentBrowse && !detailPage.activeSelf);
                browseReturnButton.interactable = returnToEnterprises != null && !draft.IsPending;
            }
            summary.text = p.ReadOnly ? readOnlyText : draft.IsPending ? pendingText : !bound || !p.SubmissionAvailable
                ? (string.IsNullOrEmpty(p.UnavailableReason) ? missingRulesText : p.UnavailableReason)
                : draft.Selected == null ? selectPrompt : p.MaxSelections > 1
                    ? string.Format(multiSelectedFormat, draft.SelectedIds.Count, p.MinSelections, p.MaxSelections)
                    : string.Format(selectedFormat, draft.Selected.Label);
        }

        private void RenderDetailTitle(string label)
        {
            if (detailTitle == null) return;
            var caption = (label ?? string.Empty).Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            detailTitle.text = caption;
            if (detailTitleDocument == null || renderDetails == null) return;
            if (caption.Length == 0) { detailTitleDocument.ClearBinding(); return; }
            if (renderedDetailTitleDocument == null || renderedDetailTitle != caption)
            {
                renderedDetailTitle = caption;
                renderedDetailTitleDocument = CreateDetailTitleDocument(caption);
            }
            // 全文按当前宿主宽度排版；既有纵向布局采用实测标题高度，向下安排正文。
            renderDetails(detailTitleDocument, renderedDetailTitleDocument, false);
        }

        private static GameplayTextDocument CreateDetailTitleDocument(string caption)
        {
            var runs = new List<GameplayTextRun>();
            for (int start = 0; start < caption.Length;)
            {
                bool number = caption[start] >= '0' && caption[start] <= '9';
                int end = start + 1;
                while (end < caption.Length && (caption[end] >= '0' && caption[end] <= '9') == number) end++;
                // 名称中的数字保持原字面格式，只指定既有数字字体角色，不把名称重新解释为模板。
                runs.Add(new TextRun(caption.Substring(start, end - start), number ? new GameplayTextRunStyle("EffectNumber") : null));
                start = end;
            }
            return new GameplayTextDocument(runs, "enterprise-detail-title:" + caption.Length + ":" + caption);
        }

        private void RenderDescription(EnterpriseSelectionTarget inspected, EnterpriseSelectionProjection projection)
        {
            var notices = inspected == null ? Array.Empty<string>() : new[] {
                inspected.UnavailableReason,
                projection.Mode == EnterpriseSelectionMode.SwitchDepartment && inspected.DepartmentId == projection.CurrentDepartmentId
                    ? currentDepartmentText : null
            }.Where(value => !string.IsNullOrEmpty(value)).ToArray();
            if (descriptionDocument != null && resolveDetails != null && renderDetails != null)
            {
                var document = inspected == null ? null : resolveDetails(inspected.Details);
                if (notices.Length > 0)
                {
                    var runs = document?.Runs.ToList() ?? new List<GameplayTextRun>();
                    foreach (var notice in notices) { if (runs.Count > 0) runs.Add(new LineBreak()); runs.Add(new TextRun(notice)); }
                    var suffix = string.Concat(notices.Select(value => value.Length + ":" + value));
                    var identity = document?.Identity ?? string.Empty; var measurement = document?.MeasurementIdentity ?? string.Empty;
                    document = new GameplayTextDocument(runs, identity.Length + ":" + identity + suffix,
                        measurement.Length + ":" + measurement + suffix);
                }
                renderDetails(descriptionDocument, document, false);
                return;
            }
            // 未接线的旧资产继续原兼容显示；不把它计作正式图文正文迁移完成。
            if (description != null) description.text = inspected == null ? string.Empty : string.Join("\n",
                new[] { inspected.Description }.Concat(notices).Where(value => !string.IsNullOrEmpty(value)));
        }
    }
}
