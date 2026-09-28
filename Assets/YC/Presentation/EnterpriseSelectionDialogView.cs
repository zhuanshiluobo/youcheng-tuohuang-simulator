using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>企业／公共科室的资产化表现。当前没有正式规则适配器；未绑定宿主时确认永久禁用。</summary>
    public sealed class EnterpriseSelectionDialogView : MonoBehaviour
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
        [SerializeField] private RawImage detailImage, enlargedImage;
        [SerializeField] private AspectRatioFitter detailAspect, enlargedAspect;
        [SerializeField] private GameObject detailPage, emptyState;
        [SerializeField] private Button confirm, cancel, back;
        [SerializeField] private Text confirmLabel;
        [SerializeField] private ModeText[] modeText;
        [SerializeField] private string missingRulesText;
        [SerializeField] private string currentDepartmentText;
        [SerializeField] private string selectedFormat;
        [SerializeField] private string selectPrompt;
        [SerializeField] private string pendingText;
        private readonly EnterpriseSelectionDraft draft = new EnterpriseSelectionDraft();
        private readonly Dictionary<string, EnterpriseBoardItemView> items = new Dictionary<string, EnterpriseBoardItemView>();
        private Func<EnterpriseSelectionProjection> latestProjection;
        private Action<EnterpriseSelectionIntent> submit;
        private Action cancelRequest;
        private bool effectPage;
        private GameObject savedFocus;
        private string enlargedBoardId;
        public EnterpriseSelectionDraft Draft => draft;
        public RectTransform Panel => panel;
        public RectTransform Body => body;
        public ScrollRect CandidateScroll => candidateScroll;

        public bool TryValidateConfiguration(out string reason)
        {
            if (catalog == null || boardTemplate == null || panel == null || body == null || candidateContent == null ||
                candidateScroll == null || title == null || context == null || summary == null || description == null ||
                detailImage == null || enlargedImage == null || detailPage == null || emptyState == null ||
                confirm == null || cancel == null || back == null || confirmLabel == null || modeText == null || modeText.Length != 4)
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
            confirm.interactable = false;
        }

        // 后续正式 adapter 必须提供当前玩家可见投影和现有命令网关；这里不注册虚构的请求类型。
        public void BindSubmission(Func<EnterpriseSelectionProjection> current, Action<EnterpriseSelectionIntent> send,
            Action cancelAction)
        { latestProjection = current; submit = send; cancelRequest = cancelAction; Render(); }

        public void Present(EnterpriseSelectionProjection projection, bool fromEffectRequest = false)
        {
            bool newRequest = draft.Projection == null || draft.Projection.RequestId != projection.RequestId ||
                draft.Projection.Mode != projection.Mode;
            if (!draft.Refresh(projection)) return;
            effectPage = fromEffectRequest;
            if (newRequest)
            {
                enlargedBoardId = null; detailPage.SetActive(false); body.gameObject.SetActive(true);
                candidateScroll.StopMovement(); candidateScroll.horizontalNormalizedPosition = 0;
            }
            Render();
            if (GameplayHudFrame.Active != null) GameplayHudFrame.Active.ShowPage(gameObject, effectPage);
            else gameObject.SetActive(true);
        }

        public void ShowUnavailable(EnterpriseSelectionMode mode)
        {
            if (GameplayHudFrame.Active != null) GameplayHudFrame.Active.ReleasePage(gameObject);
            BindSubmission(null, null, null);
            Present(new EnterpriseSelectionProjection { Mode = mode, CanCancel = true, UnavailableReason = missingRulesText });
        }

        public void Choose(string id)
        { if (!isActiveAndEnabled) return; draft.Select(id); Render(); }

        public void Inspect(string id)
        { draft.Inspect(id); Render(); }

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
            var p = draft.Projection;
            var board = p?.Boards.FirstOrDefault(b => b.Id == boardId) ??
                (p?.FixedCompany?.Id == boardId ? p.FixedCompany : null);
            if (board == null) return;
            savedFocus = EventSystem.current?.currentSelectedGameObject;
            enlargedBoardId = boardId;
            EnterpriseBoardCatalog.Bind(enlargedImage, enlargedAspect, catalog.Find(board.VisualKey));
            body.gameObject.SetActive(false); detailPage.SetActive(true); back.gameObject.SetActive(true);
            confirm.interactable = false;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(back.gameObject);
        }

        public void ReturnFromDetails()
        {
            enlargedBoardId = null; detailPage.SetActive(false); body.gameObject.SetActive(true);
            back.gameObject.SetActive(false); Render();
            if (savedFocus != null && savedFocus.activeInHierarchy && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(savedFocus);
        }

        private void Confirm()
        {
            if (!isActiveAndEnabled || detailPage.activeSelf || latestProjection == null || submit == null) return;
            var current = latestProjection();
            if (current == null) { ShowUnavailable(draft.Projection.Mode); return; }
            var previousRequest = draft.Projection;
            bool changed = current.RequestId != previousRequest.RequestId || current.Mode != previousRequest.Mode ||
                current.Revision != previousRequest.Revision;
            if (!draft.Refresh(current)) return;
            if (changed) { Render(); return; }
            if (!draft.TryBeginSubmit(out var intent)) { Render(); return; }
            Render();
            submit(intent);
        }

        private void Cancel()
        {
            if (draft.Projection == null || !draft.Projection.CanCancel || draft.IsPending) return;
            if (latestProjection != null)
            {
                var p = latestProjection();
                if (p == null || p.RequestId != draft.Projection.RequestId || p.Revision != draft.Projection.Revision || !p.CanCancel)
                { if (p != null) Present(p, effectPage); return; }
                cancelRequest?.Invoke();
                return;
            }
            Close(); // 缺规则只读空态的关闭，不是业务取消。
        }

        private void Close()
        {
            draft.Clear();
            if (GameplayHudFrame.Active != null) GameplayHudFrame.Active.ReleasePage(gameObject);
            else gameObject.SetActive(false);
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
            title.text = text.title; confirmLabel.text = text.confirm;
            context.text = p.Context ?? string.Empty;
            var valid = new HashSet<string>();
            foreach (var board in p.Boards)
            {
                valid.Add(board.Id);
                if (!items.TryGetValue(board.Id, out var item))
                { item = Instantiate(boardTemplate, candidateContent, false); item.gameObject.SetActive(true); items.Add(board.Id, item); }
                item.transform.SetAsLastSibling();
                item.Bind(board, catalog, draft, Choose, OpenDetails);
            }
            foreach (var id in new List<string>(items.Keys))
                if (!valid.Contains(id)) { EnterpriseBoardItemView.Retire(items[id].gameObject); items.Remove(id); }
            if (fixedCompany != null)
            {
                fixedCompany.gameObject.SetActive(p.FixedCompany != null);
                if (p.FixedCompany != null) fixedCompany.Bind(p.FixedCompany, catalog, draft, Choose, OpenDetails);
            }
            if (enlargedBoardId != null && !valid.Contains(enlargedBoardId) && p.FixedCompany?.Id != enlargedBoardId)
            { enlargedBoardId = null; detailPage.SetActive(false); body.gameObject.SetActive(true); }
            back.gameObject.SetActive(detailPage.activeSelf);
            emptyState.SetActive(p.Boards.Length == 0);
            var inspected = draft.Inspected;
            var boardInfo = inspected == null ? null : p.Boards.FirstOrDefault(b => b.Id == inspected.BoardId);
            var art = boardInfo == null ? null : catalog.Find(boardInfo.VisualKey);
            EnterpriseBoardCatalog.Bind(detailImage, detailAspect, art);
            if (art != null && p.Mode == EnterpriseSelectionMode.Effect)
            {
                detailImage.uvRect = new Rect(art.uv.x + inspected.X * art.uv.width,
                    art.uv.y + (1 - inspected.Y - inspected.Height) * art.uv.height,
                    inspected.Width * art.uv.width, inspected.Height * art.uv.height);
                detailAspect.aspectRatio = art.aspect * inspected.Width / inspected.Height;
            }
            detailTitle.text = inspected?.Label ?? string.Empty;
            description.text = inspected == null ? string.Empty : string.Join("\n", new[] {
                inspected.Description, inspected.UnavailableReason,
                p.Mode == EnterpriseSelectionMode.SwitchDepartment && inspected.DepartmentId == p.CurrentDepartmentId ? currentDepartmentText : null
            }.Where(s => !string.IsNullOrEmpty(s)));
            cost.text = inspected?.Cost ?? string.Empty;
            bool bound = latestProjection != null && submit != null;
            confirm.interactable = bound && draft.CanConfirm && !detailPage.activeSelf;
            cancel.gameObject.SetActive(p.CanCancel);
            cancel.interactable = !draft.IsPending && (latestProjection == null || cancelRequest != null);
            summary.text = draft.IsPending ? pendingText : !bound || !p.SubmissionAvailable
                ? (string.IsNullOrEmpty(p.UnavailableReason) ? missingRulesText : p.UnavailableReason)
                : draft.Selected == null ? selectPrompt : string.Format(selectedFormat, draft.Selected.Label);
        }
    }
}
