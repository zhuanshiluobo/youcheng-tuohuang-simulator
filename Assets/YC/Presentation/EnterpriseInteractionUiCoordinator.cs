using System;
using System.Collections.Generic;
using System.Linq;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Enterprises;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Domain.Rules;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed class EnterpriseInteractionUiCoordinator : InteractionBase, IInteractionRequestRenderer, IDisposable
    {
        private readonly GameplayExpansionUi adapter;
        private readonly GameplayInteractionHudView hud;
        private readonly Func<IReadOnlyList<InteractionRequest>> requests;
        private readonly Func<int> player;
        private readonly Action<GameCommand> submit;
        private EnterpriseSelectionDialogView enterprise, department;
        private string renderedId = string.Empty, inFlight = string.Empty;
        private int renderedRevision = -1, renderedCandidateVersion = -1, renderedPlayer = -1;
        private bool refreshRequired;
        private InteractionRequestProjection renderedRequest;
        private string renderedGame;
        private readonly InteractionCommandIntent confirmedIntent = new InteractionCommandIntent();
        private EnterpriseSelectionIntent submittedIntent;
        private EnterpriseSelectionDialogView submittedDialog;
        public Func<bool> CanSubmit { private get; set; }
        public Func<bool> RequiresExplicitRefresh { private get; set; }
        public EnterpriseInteractionUiCoordinator(GameplayExpansionUi adapter, GameplayInteractionHudView hud,
            Func<IReadOnlyList<InteractionRequest>> requests, Func<int> player, Action<GameCommand> submit)
        { this.adapter = adapter; this.hud = hud; this.requests = requests; this.player = player; this.submit = submit; }
        public override string Id => "enterprise.interaction";
        public override InteractionPriority Priority => InteractionPriority.PendingResolution;
        int IInteractionRequestRenderer.Priority => 320;
        public override bool IsActive => Current() != null;
        private static bool IsUpgradeType(string type) => type == "enterprise.upgrade" || type == "round.enterprise.upgrade";
        private static bool Handles(string type, string prompt) => type == "setup.enterprise.choose" || type == "setup.department.choose" ||
            type == "enterprise.department" || type == "enterprise.special" || type == "enterprise.special.group" || type == "enterprise.special.order" ||
            IsUpgradeType(type) || type == "lua.choice" && prompt == "enterprise.choose_distinct";
        public bool CanRender(InteractionRequestProjection request) => request != null && request.VisibleToViewer && request.Status == "open" &&
            request.AnsweringPlayerId == player() && Handles(request.InteractionTypeId, request.PromptKey) && adapter.VisibleView() != null;
        private InteractionRequestProjection Current()
        {
            var request = requests()?.FirstOrDefault(value => value.Status == "open" && value.AnsweringPlayerId == player() && Handles(value.InteractionTypeId, value.PromptKey));
            return request == null ? null : InteractionRequestProjector.ProjectForPlayer(request, player());
        }
        private bool IsCurrent(InteractionRequestProjection request)
        {
            var current = Current();
            return CanSubmit?.Invoke() != false && !confirmedIntent.IsBusy && string.IsNullOrEmpty(inFlight) && current != null && CanRender(current) &&
                InteractionCommandIntent.SameContract(renderedGame, request, adapter.VisibleView()?.GameId, current) && current.StateRevision >= request.StateRevision;
        }
        public EnterpriseSelectionProjection BuildProjection(InteractionRequestProjection request)
        {
            if (!CanRender(request)) return null;
            var view = adapter.VisibleView();
            bool isDepartment = request.InteractionTypeId == "enterprise.department" || request.InteractionTypeId == "setup.department.choose";
            bool special = request.InteractionTypeId == "enterprise.special" || request.InteractionTypeId == "enterprise.special.order";
            bool upgrade = IsUpgradeType(request.InteractionTypeId) || request.InteractionTypeId == "lua.choice" && request.PromptKey == "enterprise.choose_distinct";
            var result = new EnterpriseSelectionProjection { RequestId = request.InteractionId, Revision = request.StateRevision,
                GameId = view.GameId, ViewerPlayerId = view.ViewerPlayerId, OwnerEffectId = request.OwnerEffectId,
                CandidateSetId = request.CandidateSetId, RequestContract = InteractionCommandIntent.ContractKey(request),
                CandidateSetVersion = request.CandidateSetVersion, MinSelections = request.MinSelections, MaxSelections = request.MaxSelections,
                SubmissionAvailable = CanSubmit?.Invoke() != false && request.CandidateIds.Count >= request.MinSelections, CanCancel = request.AllowDecline,
                Mode = special ? EnterpriseSelectionMode.Effect : isDepartment ? request.InteractionTypeId == "setup.department.choose"
                    ? EnterpriseSelectionMode.InitialDepartment : EnterpriseSelectionMode.SwitchDepartment : EnterpriseSelectionMode.Company,
                Context = special ? adapter.Copy.SpecialContext : isDepartment ? request.InteractionTypeId == "setup.department.choose"
                    ? adapter.Copy.InitialDepartmentContext : adapter.Copy.SwitchDepartmentContext : upgrade ? string.Empty : adapter.Copy.InitialEnterpriseContext };
            var boardList = new Dictionary<string, EnterpriseBoardProjection>();
            var targets = new List<EnterpriseSelectionTarget>();
            var specials = special ? GameplayExpansionUi.SpecialCandidates(request) : null;
            foreach (var id in request.CandidateIds)
            {
                var candidate = specials?.FirstOrDefault(value => value.CandidateId == id);
                string boardId = candidate?.EnterpriseItemId ?? id;
                var item = view.ItemZones.Items.Find(value => value.ItemId == boardId);
                string definitionId = item?.DefinitionId ?? GameplayExpansionUi.CandidateDefinition(request, id);
                if (!boardList.ContainsKey(boardId)) boardList.Add(boardId, adapter.ProjectCandidateBoard(view, boardId, definitionId, request));
                var definition = adapter.Definitions?.FindEnterprise(definitionId);
                int tier = candidate == null ? 0 : definition?.Levels.Find(value => value.AbilityId == candidate.SpecialId)?.Level ?? 1;
                var area = adapter.LevelArea(tier);
                var target = new EnterpriseSelectionTarget { Id = id, BoardId = boardId, CompanyId = isDepartment ? null : boardId,
                    DepartmentId = isDepartment ? id : null, EffectId = candidate?.SpecialId, Tier = tier, Available = true,
                    Label = candidate == null ? adapter.Name(definitionId) : string.Format(adapter.Copy.SpecialFormat, adapter.Name(definitionId), tier),
                    Description = candidate == null ? boardList[boardId].Summary : string.Format(adapter.Copy.LevelRangeFormat,
                        candidate.CurrentLevel, candidate.EffectiveLevelCeiling) + "\n" + adapter.Description(candidate.DisplayNameKey),
                    Cost = candidate == null ? string.Empty : adapter.Cost(candidate.SpecialId),
                    Details = candidate == null ? boardList[boardId].Details : adapter.SpecialDetails(view, boardId, definitionId,
                        candidate.SpecialId, candidate.CurrentLevel, candidate.EffectiveLevelCeiling),
                    X = area.x, Y = area.y, Width = area.width, Height = area.height };
                targets.Add(target);
                if (isDepartment)
                {
                    var departmentDefinition = adapter.Definitions?.FindDepartment(definitionId);
                    var company = view.ItemZones.Items.FirstOrDefault(value => adapter.Definitions?.FindEnterprise(value.DefinitionId)?.RuntimeId == departmentDefinition?.EnterpriseRuntimeId &&
                        departmentDefinition != null);
                    if (company != null)
                    {
                        result.FixedCompany = adapter.ProjectBoard(view, company.ItemId, company.DefinitionId);
                        result.CurrentDepartmentId = view.ItemZones.Relations.Find(value => value.SourceItemId == company.ItemId &&
                            value.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation)?.TargetItemId;
                    }
                }
            }
            result.Boards = boardList.Values.ToArray(); result.Targets = targets.ToArray();
            return result;
        }
        public void Render(InteractionRequestProjection request)
        {
            if (!CanRender(request) || request.CandidateIds.Count == 0) { Clear(); return; }
            var currentGame = adapter.VisibleView()?.GameId;
            if (renderedGame == currentGame && request.InteractionId == renderedId && player() == renderedPlayer && request.StateRevision < renderedRevision) return;
            if (!string.IsNullOrEmpty(currentGame) && confirmedIntent.IsBusy && !confirmedIntent.BelongsToContext(currentGame, player())) confirmedIntent.Reset();
            bool same = InteractionCommandIntent.SameContract(renderedGame, renderedRequest, currentGame, request);
            if (same && request.StateRevision < renderedRevision) return;
            var waiting = confirmedIntent.WaitingForRefresh;
            if (CanSubmit?.Invoke() != false && confirmedIntent.TryRebuild(currentGame, request, string.Empty, out var rebuilt))
            { renderedRequest = request; renderedRevision = request.StateRevision; Dispatch(rebuilt); return; }
            if (waiting && !confirmedIntent.IsBusy) CompleteSubmitted(false);
            if (confirmedIntent.IsBusy)
            {
                renderedRequest = request; renderedRevision = request.StateRevision;
                if (submittedDialog != null) submittedDialog.Draft.Refresh(BuildProjection(request));
                return;
            }
            inFlight = string.Empty;
            bool isDepartment = request.InteractionTypeId == "enterprise.department" || request.InteractionTypeId == "setup.department.choose";
            var dialog = isDepartment ? department : enterprise;
            if (same && !refreshRequired && request.StateRevision == renderedRevision && request.CandidateSetVersion == renderedCandidateVersion && dialog != null &&
                dialog.Draft.Projection != null)
            {
                dialog.RefreshSubmissionAvailability(CanSubmit?.Invoke() != false && request.CandidateIds.Count >= request.MinSelections);
                return;
            }
            if (!same) { if (enterprise != null) enterprise.ClearPresentation(); if (department != null) department.ClearPresentation(); }
            if (dialog == null)
            {
                dialog = hud.DialogRegistry.InstantiateEnterpriseSelection(hud.Frame.ContentRect, isDepartment);
                if (isDepartment) department = dialog; else enterprise = dialog;
            }
            renderedId = request.InteractionId; renderedRevision = request.StateRevision;
            renderedRequest = request; renderedGame = currentGame;
            renderedCandidateVersion = request.CandidateSetVersion; renderedPlayer = player(); refreshRequired = false;
            dialog.BindGameplayText(adapter.ResolveEnterpriseDetails, adapter.RenderEnterpriseDetails);
            dialog.BindSubmission(() =>
            {
                var current = Current();
                return current != null && current.InteractionId == request.InteractionId ? BuildProjection(current) : null;
            }, intent =>
            {
                var fresh = Current();
                if (GameplayHudFrame.EffectInputSuspended || !IsCurrent(request) || fresh == null || intent.CandidateSetVersion != fresh.CandidateSetVersion) return;
                submittedIntent = intent; submittedDialog = dialog;
                Send(fresh, EffectInteractionCommands.Answer(fresh, player(), intent.TargetIds));
            }, request.AllowDecline ? (Action)(() => { var fresh = Current(); if (IsCurrent(request)) Send(fresh, EffectInteractionCommands.Answer(fresh, player(), null, true)); }) : null);
            dialog.Present(BuildProjection(request), true);
        }
        private void Send(InteractionRequestProjection request, GameCommand command)
        {
            if (!confirmedIntent.Begin(adapter.VisibleView()?.GameId, request, command,
                request.InteractionTypeId == "setup.enterprise.choose")) return;
            Dispatch(command);
        }
        private void Dispatch(GameCommand command)
        {
            inFlight = command.CommandId;
            try { submit(command); }
            catch { refreshRequired = true; throw; }
        }
        public override void NotifyCommandSettled(string commandId)
        {
            // 旧快照/兼容通知没有正式结果身份，不能释放本流程的等待意图。
        }
        public override void NotifyInteractionCommandResolved(string gameId, string commandId, bool accepted, CommandErrorCode errorCode)
        {
            if (!confirmedIntent.Resolve(gameId, commandId, accepted, errorCode, RequiresExplicitRefresh?.Invoke() == true)) return;
            inFlight = string.Empty; refreshRequired = true;
            if (!confirmedIntent.IsBusy) CompleteSubmitted(accepted);
        }
        private void CompleteSubmitted(bool accepted)
        {
            var current = Current();
            var refreshed = current?.InteractionId == submittedIntent?.RequestId ? BuildProjection(current) : null;
            if (submittedDialog != null && submittedIntent != null)
                submittedDialog.Complete(submittedIntent, accepted, refreshed);
            submittedIntent = null; submittedDialog = null;
        }
        public override InteractionResult OnEscape()
        {
            var current = Current(); if (current == null) return InteractionResult.Passthrough;
            if (!GameplayHudFrame.EffectInputSuspended && current.AllowDecline && IsCurrent(current))
                Send(current, EffectInteractionCommands.Answer(current, player(), null, true));
            return InteractionResult.Consumed;
        }
        public override InteractionPresentation BuildPresentation() => IsActive ? InteractionPresentation.Busy : InteractionPresentation.Empty;
        public override InteractionResult OnLocationClicked(string locationId) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnInfluenceSlotClicked(string slotId) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnMobileCityClicked() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override void Cancel() => Clear();
        public void Clear()
        {
            // 场景卸载时 Unity 对象可能先于控制器销毁，不能使用 CLR 的空条件运算符。
            if (enterprise != null) enterprise.ClearPresentation();
            if (department != null) department.ClearPresentation();
            renderedId = inFlight = string.Empty;
            renderedRequest = null;
            renderedRevision = renderedCandidateVersion = renderedPlayer = -1; refreshRequired = false;
        }
        public void Dispose() { Clear(); if (enterprise != null) UnityEngine.Object.Destroy(enterprise.gameObject); if (department != null) UnityEngine.Object.Destroy(department.gameObject); }
    }
}
