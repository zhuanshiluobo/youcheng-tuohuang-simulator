using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class PlayerSelectionPageView : MonoBehaviour
    {
        [SerializeField] private RectTransform window, optionsRow;
        [SerializeField] private PlayerSelectionOptionView optionTemplate;
        [SerializeField] private Text title, description, instruction, summary, hint;
        [SerializeField] private GameObject emptyState;
        [SerializeField] private Button confirm, cancel, close;
        [SerializeField] private string exactFormat = "请选择 {0} 位玩家", rangeFormat = "请选择 {0} 至 {1} 位玩家";
        [SerializeField] private string optionalFormat = "可选择最多 {0} 位玩家，也可不选";
        [SerializeField] private string summaryFormat = "已选择 {0} / {1} 位玩家", missingFormat = "还需选择 {0} 位玩家";
        [SerializeField] private string singleHint = "点击其他玩家可更换选择", multipleHint = "点击已选玩家可取消选择";
        [SerializeField] private string limitHint = "已达选择上限，请先取消一位已选玩家", insufficientHint = "可选玩家不足，无法满足本次选择要求";
        [SerializeField] private string unavailableHint = "当前选择请求已失效，请等待状态更新";
        private readonly PlayerSelectionDraft draft = new PlayerSelectionDraft();
        private readonly List<PlayerSelectionOptionView> options = new List<PlayerSelectionOptionView>();
        private Action<int[]> confirmed;
        private Action cancelled;
        private Action<IReadOnlyList<int>> changed;
        private Func<bool> isCurrent;
        private Func<PlayerSelectionConfig> latest;
        private bool effectPage, finished;
        private GameObject previousFocus;
        public PlayerSelectionDraft Draft => draft;
        public RectTransform Window => window;
        public RectTransform OptionsRow => optionsRow;
        public IReadOnlyList<PlayerSelectionOptionView> Options => options.AsReadOnly();
        public Button ConfirmButton => confirm;
        public Button CancelButton => cancel;
        public Button CloseButton => close;
        public bool TryValidateConfiguration(out string reason)
        {
            var valid = window != null && optionsRow != null && optionTemplate != null && optionTemplate.IsConfigured &&
                title != null && description != null && instruction != null && summary != null && hint != null &&
                emptyState != null && confirm != null && cancel != null && close != null;
            reason = valid ? string.Empty : "玩家选择页面引用不完整。"; return valid;
        }
        private void Awake()
        { confirm.onClick.AddListener(Confirm); cancel.onClick.AddListener(Cancel); close.onClick.AddListener(Cancel); }
        public void Present(PlayerSelectionConfig config, Action<int[]> onConfirm, Action onCancel,
            Func<bool> current = null, bool fromEffectRequest = false, Action<IReadOnlyList<int>> onChanged = null,
            Func<PlayerSelectionConfig> currentProjection = null)
        {
            if (!gameObject.activeInHierarchy) previousFocus = EventSystem.current?.currentSelectedGameObject;
            confirmed = onConfirm; cancelled = onCancel; isCurrent = current; changed = onChanged;
            latest = currentProjection; effectPage = fromEffectRequest; finished = false;
            Refresh(config);
            if (GameplayHudFrame.Active != null) GameplayHudFrame.Active.ShowPage(gameObject, effectPage);
            else gameObject.SetActive(true);
            FocusFirst();
        }
        public void Refresh(PlayerSelectionConfig config)
        { draft.Refresh(config); Render(); }
        private bool CanAct => !finished && isActiveAndEnabled && (!effectPage || !GameplayHudFrame.EffectInputSuspended) && (isCurrent == null || isCurrent());
        public void Choose(int id)
        {
            if (!CanAct) return;
            var selected = draft.SelectedIds.Contains(id);
            if (!draft.Toggle(id))
            {
                if (!selected && draft.Config.Mode == PlayerSelectionMode.Multiple && draft.SelectedIds.Count >= draft.Config.Max)
                    hint.text = limitHint;
                return;
            }
            Render(); changed?.Invoke(draft.SelectedIds);
        }
        private void Render()
        {
            var config = draft.Config;
            if (config == null) return;
            if (!string.IsNullOrEmpty(config.Title)) title.text = config.Title;
            description.text = config.Description ?? string.Empty;
            description.gameObject.SetActive(!string.IsNullOrEmpty(config.Description));
            instruction.text = config.Min == config.Max ? string.Format(exactFormat,config.Min) :
                config.Min == 0 ? string.Format(optionalFormat,config.Max) : string.Format(rangeFormat,config.Min,config.Max);
            summary.text = string.Format(summaryFormat,draft.SelectedIds.Count,config.Max);
            hint.text = !draft.HasEnoughPlayers ? insufficientHint : draft.SelectedIds.Count < config.Min ?
                string.Format(missingFormat,config.Min-draft.SelectedIds.Count) : config.Mode == PlayerSelectionMode.Single ? singleHint : multipleHint;
            while (options.Count < config.Players.Length) options.Add(Instantiate(optionTemplate,optionsRow,false));
            optionTemplate.gameObject.SetActive(false);
            for (var i = 0; i < options.Count; i++)
            {
                options[i].gameObject.SetActive(i < config.Players.Length);
                if (i >= config.Players.Length) continue;
                options[i].gameObject.name = "Player Option " + config.Players[i].Id;
                options[i].Bind(config.Players[i],draft.SelectedIds.Contains(config.Players[i].Id),Choose);
            }
            emptyState.SetActive(config.Players.Length == 0);
            confirm.interactable = !finished && confirmed != null && draft.CanConfirm && (isCurrent == null || isCurrent());
            cancel.gameObject.SetActive(config.AllowCancel); close.gameObject.SetActive(config.AllowCancel);
        }
        public void Confirm()
        {
            if (!CanAct || confirmed == null) return;
            if (latest != null)
            {
                var previousId = draft.Config.RequestId;
                var previousRevision = draft.Config.Revision;
                var current = latest();
                if (current == null) { confirm.interactable = false; hint.text = unavailableHint; return; }
                Refresh(current);
                if (current.RequestId != previousId || current.Revision != previousRevision) return;
            }
            if (!draft.CanConfirm) return;
            var callback = confirmed; var result = draft.Result(); finished = true;
            Close(); callback(result);
        }
        public void Cancel()
        {
            if (!CanAct || draft.Config == null || !draft.Config.AllowCancel) return;
            var callback = cancelled; finished = true; Close(); callback?.Invoke();
        }
        public bool TryHandleEscape()
        {
            if (!isActiveAndEnabled) return false;
            if (draft.Config != null && draft.Config.AllowCancel) Cancel();
            return true;
        }
        public void Close()
        {
            if (GameplayHudFrame.Active != null) GameplayHudFrame.Active.ReleasePage(gameObject);
            else gameObject.SetActive(false);
        }
        private List<Button> Focusable() => options.Select(p => p.Button).Concat(new[] { cancel,confirm,close })
            .Where(b => b.gameObject.activeInHierarchy && b.IsInteractable()).ToList();
        private void FocusFirst()
        {
            var buttons = Focusable();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(buttons.Count > 0 ? buttons[0].gameObject : gameObject);
        }
        private void MoveFocus(int step)
        {
            var buttons = Focusable();
            if (buttons.Count == 0 || EventSystem.current == null) return;
            var index = buttons.FindIndex(b => b.gameObject == EventSystem.current.currentSelectedGameObject);
            if (index < 0) index = step > 0 ? -1 : 0;
            EventSystem.current.SetSelectedGameObject(buttons[(index+step+buttons.Count)%buttons.Count].gameObject);
        }
        private void Update()
        {
            if (isCurrent != null && !isCurrent())
            { confirm.interactable = false; hint.text = unavailableHint; return; }
            if (!CanAct) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { TryHandleEscape(); return; }
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected == null || !selected.transform.IsChildOf(transform)) FocusFirst();
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                var step = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? -1 : 1;
                MoveFocus(step);
            }
            if (Input.GetKeyDown(KeyCode.Space) && selected != null && selected.transform.IsChildOf(transform))
                selected.GetComponent<Button>()?.onClick.Invoke();
        }
        private void OnDisable()
        {
            var current = EventSystem.current;
            if (current != null && (current.currentSelectedGameObject == null || current.currentSelectedGameObject.transform.IsChildOf(transform)))
                current.SetSelectedGameObject(previousFocus != null && previousFocus.activeInHierarchy ? previousFocus : null);
        }
    }
}
