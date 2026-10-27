#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        [Serializable] private sealed class ActionCoverageRow
        {
            public string Id, ParentId, GameId, ContentHash, ProgramVersion, AssemblyMvid, Kind, SourceItem, Definition, Face, Entry, Name;
            public int Round, ActionRound, Player, Revision;
            public bool Seen, Selected, Confirmed, EffectSettled, RecoveryVerified, Legal;
            public bool InventoryOnly, SourceSettled;
            public string Reason, Choice, NextRequest, Outcome, Evidence, UseId;
        }
        private sealed class CleanupClick
        {
            public SettlementEffectChipView Chip;
            public SettlementOptionView Option;
            public ActionCleanupCandidate Candidate;
            public ActionCoverageRow Coverage;
        }
        private sealed class CleanupPending
        {
            public CleanupClick Click;
            public string RootId, InvocationId, CommandId;
            public HashSet<string> BeforeEffects;
        }
        private readonly ActionCleanupCoveragePolicy cleanupPolicy = new ActionCleanupCoveragePolicy();
        private readonly Dictionary<string, ActionCoverageRow> actionCoverage = new Dictionary<string, ActionCoverageRow>(StringComparer.Ordinal);
        private readonly List<CleanupPending> cleanupPending = new List<CleanupPending>();
        private readonly List<Tuple<ActionCoverageRow, string>> conditionCoveragePending = new List<Tuple<ActionCoverageRow, string>>();
        private readonly HashSet<string> actionCoverageObservedModes = new HashSet<string>(StringComparer.Ordinal);
        private bool cleanupActionPerformed;
        private string actionCoverageRound;

        private ActionCoverageRow CoverageRow(GameStateView view, string kind, string source, string definition, string face, string entry, string name)
        {
            string id = string.Join("|", view.GameId, view.ContentPackHash, view.Round, view.ActionRound, view.ViewerPlayerId, kind, source, definition, face, entry);
            if (!actionCoverage.TryGetValue(id, out var row))
            {
                row = new ActionCoverageRow { Id = id, GameId = view.GameId, ContentHash = view.ContentPackHash,
                    ProgramVersion = UnityEngine.Application.version, AssemblyMvid = GetType().Assembly.ManifestModule.ModuleVersionId.ToString(),
                    Round = view.Round, ActionRound = view.ActionRound, Player = view.ViewerPlayerId, Kind = kind,
                    SourceItem = source, Definition = definition, Face = face, Entry = entry, Name = name, RecoveryVerified = false, InventoryOnly = true };
                actionCoverage.Add(id, row);
            }
            row.Revision = view.StateRevision; row.Seen = true; return row;
        }
        private ActionCoverageRow BeginCoverageAttempt(ActionCoverageRow inventory)
        {
            var attempt = JsonConvert.DeserializeObject<ActionCoverageRow>(JsonConvert.SerializeObject(inventory));
            attempt.ParentId = inventory.Id;
            attempt.Id = inventory.Id + "|尝试" + (actionCoverage.Values.Count(row => row.ParentId == inventory.Id) + 1);
            attempt.InventoryOnly = false; attempt.Selected = attempt.Confirmed = attempt.EffectSettled = attempt.RecoveryVerified = attempt.SourceSettled = false;
            attempt.Outcome = attempt.NextRequest = attempt.Evidence = null;
            actionCoverage.Add(attempt.Id, attempt); return attempt;
        }
        private IEnumerable<ActionCoverageRow> ActionCoverageGaps() => actionCoverage.Values.Where(row =>
            row.InventoryOnly ? !actionCoverage.Values.Any(attempt => attempt.ParentId == row.Id && attempt.EffectSettled) : !row.EffectSettled);
        private void CoverageEvent(ActionCoverageRow row, string step, object proof = null)
        {
            Append("行动与收尾覆盖矩阵.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Step = step, Row = row, Proof = proof,
                RecoveryVerified = false, AcceptanceScope = "单项观察证据，不能单独宣称整局通过" });
        }
        private static string OwnResourceFacts(GameStateView view) => JsonConvert.SerializeObject(
            view.Players.FirstOrDefault(player => player.PlayerId == view.ViewerPlayerId)?.Resources);
        private static string VisibleFace(GameStateView view, string item) => view.ItemZones.Items.FirstOrDefault(value => value.ItemId == item)?
            .LocalState?.Properties.FirstOrDefault(value => value.Name == "activeFaceId")?.Value?.StringValue ?? "";

        private static bool WithinVisibleEffect(GameStateView view, string child, string root)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(child) && seen.Add(child))
            {
                if (child == root) return true;
                child = view.Effects.FirstOrDefault(node => node.EffectId == child)?.ParentEffectId;
            }
            return false;
        }
        private string VisibleConditionSource(GameStateView view, string effect) =>
            cleanupPending.FirstOrDefault(p => p.Click.Coverage.Player == view.ViewerPlayerId && WithinVisibleEffect(view, effect, p.RootId))?.Click.Coverage.SourceItem ??
            driverQuickPending.FirstOrDefault(p => p.Coverage.Player == view.ViewerPlayerId && WithinVisibleEffect(view, effect, p.RootId))?.Coverage.SourceItem;

        private ActionCoverageRow QuickCoverage(GameStateView view, AbilityActionOption option) => CoverageRow(view, "快速行动",
            option.SourceRef.ItemId, option.SourceRef.DefinitionId, option.SourceRef.ActiveFaceId, option.ActionId, option.DisplayName);
        private void RecordQuickInventory(GameStateView view)
        {
            foreach (var option in view.AbilityActions.Where(value => value.EntryKind == "quick" &&
                value.SourceRef?.AuthorizedPlayerId == view.ViewerPlayerId))
            {
                var row = QuickCoverage(view, option); row.Legal = option.Enabled; row.Reason = option.DisabledReason;
                CoverageEvent(row, "看见", new { option.OptionId, option.TrackBindingId, option.RequestRef,
                    OwnResources = view.Players.FirstOrDefault(p => p.PlayerId == view.ViewerPlayerId)?.Resources,
                    Track = view.UsageTracks.Where(track => track.Binding?.BindingId == option.TrackBindingId).ToArray(),
                    Plan = option.Enabled ? "先于消耗资源的主要行动使用；每次返回重读共用轨道资格" : "保留原禁用原因；正常推进后重读，不注入资源或猜费用" });
            }
            if (!view.AbilityActions.Any(value => value.EntryKind == "quick"))
                Append("行动收尾未覆盖与下一局优先.jsonl", new { view.GameId, view.Round, Player = view.ViewerPlayerId,
                    Category = "快速行动", Reason = "当前本人正式目录未出现入口；不能据此断言未抽到或已通过", Next = "后续正常行动继续盘点" });
        }

        private AbilityActionOption PreferredSharedSpecial(GameStateView view)
        {
            if (view?.HasAbilityActionCatalog != true || Request != null ||
                (view.Players.FirstOrDefault(player => player.PlayerId == view.ViewerPlayerId)?.RemainingMainActionsThisTurn > 0) != true) return null;
            return view.AbilityActions.Where(option => option.EntryKind == "special" && option.Enabled &&
                option.SourceRef?.AuthorizedPlayerId == view.ViewerPlayerId && !string.IsNullOrEmpty(option.TrackBindingId) &&
                view.AbilityActions.Any(quick => quick.EntryKind == "quick" && quick.TrackBindingId == option.TrackBindingId) &&
                actionCoverage.Values.Any(row => row.GameId == view.GameId && row.Kind == "快速行动" && row.SourceItem == option.SourceRef.ItemId &&
                    row.Face == option.SourceRef.ActiveFaceId && row.EffectSettled) &&
                !actionCoverage.Values.Any(row => row.GameId == view.GameId && row.Kind == "特殊行动" && row.SourceItem == option.SourceRef.ItemId &&
                    row.Entry == option.ActionId && row.EffectSettled))
                .OrderBy(option => option.SortOrder).ThenBy(option => option.OptionId, StringComparer.Ordinal).FirstOrDefault();
        }

        private IEnumerator TryVisibleSharedSpecialAction()
        {
            driverQuickActionPerformed = false;
            var visible = CurrentPaymentView(); var option = PreferredSharedSpecial(visible);
            if (option == null) yield break;
            string attempt = visible.GameId + ":" + visible.Round + ":" + visible.ActionRound + ":" + visible.ViewerPlayerId + ":special:" + option.OptionId;
            if (!driverQuickAttempts.Add(attempt)) yield break;
            yield return Press(MarkedButton("action.tab.0"), "共用轨道特殊入口返回原主要行动页签");
            var button = hud.ActionPanelView.SpecialButton;
            Require(button.IsActive() && button.IsInteractable(), "正式共用轨道特殊候选可用，但原特殊入口不可用");
            yield return Press(button, "同来源快速行动已结清，优先补合法特殊入口缺口");
            var projected = (Read(mobile, "turnActionPresenter") as TurnActionPresenter)?.CityStyleInteraction.QuerySpecialActions().Options
                .SingleOrDefault(value => value.DeclaredAction?.OptionId == option.OptionId && value.CanUse);
            Require(projected != null, "原特殊页面没有对应本人当前正式候选，不能点击旧行");
            string stable = projected.DeclarationMarkerId + ":" + projected.SpecialActionId;
            var row = FindObjectsOfType<UiEffectRowView>().SingleOrDefault(value => value.ItemId == stable &&
                Read(value, "mode") is UiEffectRowView.Mode mode && mode == UiEffectRowView.Mode.ChoiceExecute);
            var actual = Read(row, "statusButton") as UnityEngine.UI.Button;
            Require(actual != null && actual.IsActive() && actual.IsInteractable(), "原特殊页面缺少对应声明的实际执行按钮");
            var matrix = BeginCoverageAttempt(CoverageRow(visible, "特殊行动", option.SourceRef.ItemId, option.SourceRef.DefinitionId,
                option.SourceRef.ActiveFaceId, option.ActionId, option.DisplayName));
            matrix.Legal = matrix.Selected = true; matrix.Reason = "快速入口已正常结清；本次只使用当前仍合法的共用轨道特殊候选";
            CoverageEvent(matrix, "选择", new { option.OptionId, option.TrackBindingId, StableRow = stable });
            int before = commands.Count;
            yield return Press(actual, "正常原特殊行动行");
            var accepted = commands.Skip(before).Where(record => AccurateDriverQuickReceipt(option, visible.ViewerPlayerId,
                Read(record, "Command") as GameCommand, Read(record, "Result") as WorkflowSubmissionResult)).ToArray();
            Require(accepted.Length == 1, "特殊行动未得到唯一原请求的准确成功Applied回执");
            var command = (GameCommand)Read(accepted[0], "Command");
            // 仅核对这次原命令已经产生的本人调用收据；不以Host数据选择动作或候选。
            var invocation = State.EffectRuntime.AbilityActionInvocations.SingleOrDefault(value => value.CommandId == command.CommandId &&
                value.OptionId == option.OptionId && value.SourceRef?.AuthorizedPlayerId == visible.ViewerPlayerId);
            Require(invocation != null, "原特殊命令未关联到同次本人来源调用");
            matrix.Confirmed = true; matrix.NextRequest = Request?.InteractionTypeId; matrix.Evidence = command.CommandId;
            CoverageEvent(matrix, "确认", new { command.CommandId, invocation.InvocationId });
            driverQuickPending.Add(new DriverQuickPending { Row = new DriverQuickRow { Option = option, ItemId = option.SourceRef.ItemId,
                    DefinitionId = option.SourceRef.DefinitionId, ActionId = option.ActionId, CoverageKey = "special-action:" + option.SourceRef.DefinitionId + ":" + option.ActionId,
                    KindKey = "special-kind:declared" }, Coverage = matrix, InvocationId = invocation.InvocationId,
                RootId = invocation.OwnerEffectId, GameId = visible.GameId, CommandReceipt = accepted[0] });
            driverQuickActionPerformed = true; ObserveDriverQuickActionCompletion();
        }

        private IEnumerator TryVisibleCleanupAction()
        {
            cleanupActionPerformed = false;
            var visible = CurrentPaymentView();
            Require(visible != null, "收尾盘点缺少本人当前显示投影");
            if (Request != null && !new[] { "character.cleanup.settlement", YC.Domain.Cards.CharacterLifecycleService.ModeInteractionTypeId,
                YC.Domain.Cards.CharacterLifecycleService.OrderInteractionTypeId, YC.Domain.Cards.CharacterLifecycleService.SecondModeInteractionTypeId }
                .Contains(Request.InteractionTypeId)) yield break;
            var clicks = new List<CleanupClick>();
            foreach (var chip in hud.GetComponentsInChildren<SettlementEffectChipView>())
            {
                var option = Read(chip, "option") as SettlementOptionView;
                if (option == null || !chip.gameObject.activeInHierarchy || !option.VisibleToViewer ||
                    option.AnsweringPlayerId != visible.ViewerPlayerId || option.WindowId != visible.ActiveSettlementWindowId ||
                    (option.EntryKind != "cleanup_order" && option.EntryKind != "optional_insertion")) continue;
                var formal = visible.CharacterSettlementOptions.SingleOrDefault(value => value.OptionId == option.OptionId &&
                    value.VisibleToViewer && value.AnsweringPlayerId == visible.ViewerPlayerId && value.WindowId == option.WindowId &&
                    value.StateRevision == visible.StateRevision && value.CandidateSetVersion == option.CandidateSetVersion &&
                    value.OwnerEffectId == option.OwnerEffectId && value.SourceItemId == option.SourceItemId &&
                    value.SourceDefinitionId == option.SourceDefinitionId && value.EntryKind == option.EntryKind);
                var row = CoverageRow(visible, option.EntryKind == "cleanup_order" ? "收尾" : "可选插入", option.SourceItemId, option.SourceDefinitionId,
                    VisibleFace(visible, option.SourceItemId), option.LocalId,
                    (Read(chip, "labelDocument") as YC.Domain.Presentation.GameplayTextDocument)?.ToPlainText() ?? option.Presentation?.Label?.Key ?? option.TitleKey);
                string reason = formal == null ? "原收尾行未匹配当前本人正式候选" :
                    !formal.IsEnabled ? formal.DisabledReasonCode : option.DisabledReasonCode;
                var candidate = new ActionCleanupCandidate { Key = row.Id + ":" + option.WindowId + ":" + option.OptionId,
                    CoverageKey = "settlement:" + option.SourceDefinitionId + ":" + row.Face + ":" + option.LocalId,
                    Facts = option.CleanupRequest?.RequestGeneration + ":" + OwnResourceFacts(visible), Kind = "收尾",
                    Enabled = formal?.IsEnabled == true && (bool)Read(chip, "executionAllowed"), Pending = (bool)Read(chip, "pending") ||
                        !string.IsNullOrEmpty(option.ActiveInvocationId), Required = option.EntryKind == "cleanup_order" && option.CleanupRequest == null && !option.CanDeclineWholeEffect,
                    Repeatable = option.CleanupRequest != null && option.RepeatPolicy == "unbounded", Reason = reason, Order = option.RegistrationSequence };
                candidate.PriorCompleted = CampaignMode ? CampaignPriorityCount(candidate.CoverageKey) : 0;
                row.Legal = candidate.Enabled; row.Reason = cleanupPolicy.BlockReason(candidate);
                CoverageEvent(row, "看见", new { option.OptionId, option.WindowId, option.RepeatPolicy, option.CleanupRequest,
                    option.SettlementState, option.DisabledReason, PriorSettled = cleanupPolicy.Completed(candidate.CoverageKey) });
                clicks.Add(new CleanupClick { Chip = chip, Option = option, Candidate = candidate, Coverage = row });
            }
            var choice = cleanupPolicy.Choose(clicks.Select(value => value.Candidate));
            if (choice == null)
            {
                Append("行动收尾未覆盖与下一局优先.jsonl", new { visible.GameId, visible.Round, Player = visible.ViewerPlayerId,
                    Category = "收尾", Reason = clicks.Count == 0 ? "当前本人显示中未出现收尾来源" : "当前无尚可有限尝试的合法来源",
                    Gaps = clicks.Select(value => new { value.Coverage.Id, value.Coverage.Reason }).ToArray(),
                    Next = "保留缺口，先完成必答响应；正常后续回合优先未结清来源，不重开" });
                yield break;
            }
            var selected = clicks.Single(value => ReferenceEquals(value.Candidate, choice));
            cleanupPolicy.Begin(choice); cleanupActionPerformed = true;
            selected.Coverage = BeginCoverageAttempt(selected.Coverage);
            selected.Coverage.Selected = true; CoverageEvent(selected.Coverage, "选择");
            int before = commands.Count;
            var oldEffects = new HashSet<string>(visible.Effects.Select(value => value.EffectId));
            yield return PointerSequence(selected.Chip.gameObject, "正常收尾原来源，优先未结清缺口");
            var records = commands.Skip(before).Where(record => (Read(record, "Command") as GameCommand)?.Kind == GameCommandKind.InvokeSettlementOption).ToArray();
            Require(records.Length == 1, "收尾原行未得到唯一原来源命令，保留首次现场，不补点");
            var command = Read(records[0], "Command") as GameCommand;
            var result = Read(records[0], "Result") as WorkflowSubmissionResult;
            Require(command.PlayerId == visible.ViewerPlayerId && command.Parameters.TryGetValue("optionId", out var id) &&
                id == selected.Option.OptionId && result?.CommandResult?.Succeeded == true && result.AppliedLocally,
                "收尾原来源缺少匹配本人的准确成功Applied回执");
            if (selected.Option.CleanupRequest != null)
                Require(CleanupSourceCommand.TryRead(command, out var reference, out _) &&
                    reference.InteractionId == selected.Option.CleanupRequest.InteractionId &&
                    reference.RequestGeneration == selected.Option.CleanupRequest.RequestGeneration &&
                    reference.FactsDigest == selected.Option.CleanupRequest.FactsDigest &&
                    reference.WindowId == selected.Option.WindowId && reference.OwnerEffectId == selected.Option.OwnerEffectId,
                    "收尾准确受理不匹配本次原请求的身份、代数与摘要");
            selected.Coverage.Confirmed = true; selected.Coverage.NextRequest = Request?.InteractionTypeId;
            selected.Coverage.Evidence = command.CommandId; CoverageEvent(selected.Coverage, "确认", records[0]);
            var after = CurrentPaymentView();
            var roots = after?.Effects.Where(node => !oldEffects.Contains(node.EffectId) && node.PlayerId == visible.ViewerPlayerId &&
                node.ParentEffectId == selected.Option.OwnerEffectId && node.EffectTypeId == CleanupSourceInvocationExecutor.TypeId).ToArray();
            string root = selected.Option.CleanupRequest == null ?
                (selected.Option.EntryKind == "optional_insertion" ? after?.CharacterSettlementOptions.SingleOrDefault(value =>
                    value.OptionId == selected.Option.OptionId && value.AnsweringPlayerId == visible.ViewerPlayerId)?.TargetEffectId : selected.Option.TargetEffectId) :
                roots?.Length == 1 ? roots[0].EffectId : null;
            cleanupPending.Add(new CleanupPending { Click = selected, RootId = root, CommandId = command.CommandId,
                BeforeEffects = oldEffects,
                InvocationId = roots?.Length == 1 ? ItemTransferEffectExecutor.Text(roots[0].NormalizedArguments, "invocationId") : "" });
            if (string.IsNullOrEmpty(root)) CoverageEvent(selected.Coverage, "等待原玩家可见结清证据", new {
                command.CommandId, CurrentViewer = after?.ViewerPlayerId,
                Reason = "当前可能正由其他玩家响应；仅在原玩家重新取得正式可见投影后关联目标，不从Host隐藏状态补证" });
            ObserveActionCleanupCoverage();
        }

        private sealed class ExpectedConditionFailureProof
        {
            public string ConditionId, FailedChildId, OriginalFailureReason;
            public int LeftChildIndex;
            public string[] UninstantiatedRightTypes;
        }
        private static List<EffectNodeRuntimeState> DriverCleanupSourceTree(string rootId, IReadOnlyList<EffectNodeRuntimeState> nodes)
        {
            if (string.IsNullOrEmpty(rootId) || nodes == null || nodes.Any(node => node == null || string.IsNullOrEmpty(node.EffectId)) ||
                nodes.GroupBy(node => node.EffectId).Any(group => group.Count() != 1)) return null;
            var index = nodes.ToDictionary(node => node.EffectId, StringComparer.Ordinal);
            if (!index.ContainsKey(rootId)) return null;
            bool Within(EffectNodeRuntimeState node)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (node != null && visited.Add(node.EffectId))
                {
                    if (node.EffectId == rootId) return true;
                    if (!index.TryGetValue(node.ParentEffectId ?? "", out node)) return false;
                }
                return false;
            }
            return nodes.Where(Within).ToList();
        }
        private static string DriverCleanupSourceOutcome(string rootId, IReadOnlyList<EffectNodeRuntimeState> nodes,
            out List<ExpectedConditionFailureProof> conditions, out List<string> unexplained)
        {
            conditions = new List<ExpectedConditionFailureProof>(); unexplained = new List<string>();
            var tree = DriverCleanupSourceTree(rootId, nodes);
            if (tree == null || tree.Single(node => node.EffectId == rootId).Status != EffectNodeStatus.Completed)
            { unexplained.Add(rootId); return "failed"; }
            var index = tree.ToDictionary(node => node.EffectId, StringComparer.Ordinal);
            foreach (var node in tree)
                if (node.Status != EffectNodeStatus.Completed && node.Status != EffectNodeStatus.Failed ||
                    node.ChildEffectIds == null || node.ChildEffectIds.Distinct().Count() != node.ChildEffectIds.Count ||
                    node.ChildEffectIds.Any(id => string.IsNullOrEmpty(id) || !index.TryGetValue(id, out var child) || child.ParentEffectId != node.EffectId))
                    unexplained.Add(node.EffectId);
            foreach (var failed in tree.Where(node => node.Status == EffectNodeStatus.Failed))
            {
                if (!index.TryGetValue(failed.ParentEffectId ?? "", out var parent)) { unexplained.Add(failed.EffectId); continue; }
                var met = ItemTransferEffectExecutor.Value(parent.NormalizedResult, "conditionMet");
                int childIndex = parent.ChildEffectIds?.IndexOf(failed.EffectId) ?? -1;
                bool accepted = parent.EffectTypeId == EffectTypeIds.Condition && parent.Status == EffectNodeStatus.Completed &&
                    met?.Kind == NormalizedValueKind.Boolean && !met.BooleanValue &&
                    ItemTransferEffectExecutor.Text(parent.NormalizedResult, "outcome") == "condition_not_met" &&
                    ItemTransferEffectExecutor.Text(parent.NormalizedResult, "failedChildId") == failed.EffectId &&
                    parent.NestedGroupSizes?.Count == 2 && parent.NestedGroupSizes[0] > 0 && parent.NestedGroupSizes[1] >= 0 &&
                    parent.NestedEffects?.Count == parent.NestedGroupSizes[0] + parent.NestedGroupSizes[1] &&
                    parent.NestedEffects.All(spec => spec != null && !string.IsNullOrEmpty(spec.EffectTypeId)) &&
                    childIndex >= 0 && childIndex < parent.NestedGroupSizes[0] && childIndex == parent.ChildEffectIds.Count - 1 &&
                    parent.ChildEffectIds.Count <= parent.NestedGroupSizes[0] && parent.FlowStage == "left:" + childIndex &&
                    parent.NestedEffects[childIndex]?.EffectTypeId == failed.EffectTypeId &&
                    tree.Where(node => node.ParentEffectId == parent.EffectId).All(node => parent.ChildEffectIds.Contains(node.EffectId));
                if (!accepted) { unexplained.Add(failed.EffectId); continue; }
                conditions.Add(new ExpectedConditionFailureProof { ConditionId = parent.EffectId, FailedChildId = failed.EffectId,
                    OriginalFailureReason = failed.FailureReason, LeftChildIndex = childIndex,
                    UninstantiatedRightTypes = parent.NestedEffects.Skip(parent.NestedGroupSizes[0]).Select(spec => spec.EffectTypeId).ToArray() });
            }
            foreach (var condition in tree.Where(node => node.EffectTypeId == EffectTypeIds.Condition &&
                ItemTransferEffectExecutor.Text(node.NormalizedResult, "outcome") == "condition_not_met"))
                if (!conditions.Any(proof => proof.ConditionId == condition.EffectId)) unexplained.Add(condition.EffectId);
            return unexplained.Count > 0 ? "failed" : conditions.Count > 0 ? "condition_not_met" : "completed";
        }
        private void ObserveActionCleanupCoverage()
        {
            var view = CurrentPaymentView(); if (view == null) return;
            foreach (var pending in conditionCoveragePending.Where(value => value.Item1.GameId == view.GameId && value.Item1.Player == view.ViewerPlayerId).ToArray())
            {
                var node = view.Effects.SingleOrDefault(value => value.EffectId == pending.Item2);
                if (node == null || !new[] { EffectNodeStatus.Completed, EffectNodeStatus.Failed, EffectNodeStatus.Faulted }.Contains(node.Status)) continue;
                pending.Item1.EffectSettled = pending.Item1.Choice == "执行" && node.Status == EffectNodeStatus.Completed &&
                    ItemTransferEffectExecutor.Text(node.NormalizedResult, "outcome") == "condition_met";
                pending.Item1.Outcome = ItemTransferEffectExecutor.Text(node.NormalizedResult, "outcome");
                CoverageEvent(pending.Item1, "条件式终态", node); conditionCoveragePending.Remove(pending);
            }
            foreach (var pending in cleanupPending.Where(value => value.Click.Coverage.GameId == view.GameId &&
                value.Click.Coverage.Player == view.ViewerPlayerId).ToArray())
            {
                if (string.IsNullOrEmpty(pending.RootId))
                {
                    var source = pending.Click.Option;
                    if (source.CleanupRequest == null)
                        pending.RootId = view.CharacterSettlementOptions.SingleOrDefault(value => value.OptionId == source.OptionId &&
                            value.AnsweringPlayerId == view.ViewerPlayerId)?.TargetEffectId;
                    var candidates = view.Effects.Where(node => !pending.BeforeEffects.Contains(node.EffectId) &&
                        node.PlayerId == view.ViewerPlayerId && node.ParentEffectId == source.OwnerEffectId &&
                        (source.CleanupRequest == null || node.EffectTypeId == CleanupSourceInvocationExecutor.TypeId)).ToArray();
                    if (string.IsNullOrEmpty(pending.RootId) && candidates.Length == 1) pending.RootId = candidates[0].EffectId;
                    if (source.CleanupRequest != null && candidates.Length == 1)
                        pending.InvocationId = ItemTransferEffectExecutor.Text(candidates[0].NormalizedArguments, "invocationId");
                    if (string.IsNullOrEmpty(pending.RootId)) continue;
                }
                var root = view.Effects.SingleOrDefault(node => node.EffectId == pending.RootId);
                if (root == null || new[] { EffectNodeStatus.Created, EffectNodeStatus.Ready, EffectNodeStatus.Running, EffectNodeStatus.Blocked }.Contains(root.Status)) continue;
                Require(root.Status != EffectNodeStatus.Faulted, "收尾原执行目标发生故障，保留首次现场");
                string treeOutcome = DriverCleanupSourceOutcome(root.EffectId, State.EffectRuntime.EffectNodes,
                    out var expectedConditions, out var unexplainedFailures);
                string outcome = pending.Click.Option.CleanupRequest == null ? treeOutcome :
                    ItemTransferEffectExecutor.Text(root.NormalizedResult, "outcome");
                if (pending.Click.Option.CleanupRequest != null)
                {
                    Require(ItemTransferEffectExecutor.Text(root.NormalizedResult, "invocationId") == pending.InvocationId &&
                        new[] { "completed", "declined", "condition_failed" }.Contains(outcome), "收尾终态缺少同次调用的结清结果");
                    if (outcome == "completed") outcome = treeOutcome;
                    else if (outcome == "condition_failed") outcome = treeOutcome == "condition_not_met" ? treeOutcome : "failed";
                    else
                    {
                        var declinedTree = DriverCleanupSourceTree(root.EffectId, State.EffectRuntime.EffectNodes);
                        var declinedIndex = declinedTree?.ToDictionary(node => node.EffectId, StringComparer.Ordinal);
                        if (declinedTree == null || declinedTree.Any(node => node.Status != EffectNodeStatus.Completed &&
                            (node.Status != EffectNodeStatus.Failed || node.FailureReason != "player_declined") ||
                            node.ChildEffectIds == null || node.ChildEffectIds.Distinct().Count() != node.ChildEffectIds.Count ||
                            node.ChildEffectIds.Any(id => string.IsNullOrEmpty(id) || !declinedIndex.TryGetValue(id, out var child) || child.ParentEffectId != node.EffectId) ||
                            node.EffectId != root.EffectId && (!declinedIndex.TryGetValue(node.ParentEffectId ?? "", out var parent) ||
                                parent.ChildEffectIds == null || !parent.ChildEffectIds.Contains(node.EffectId)))) outcome = "failed";
                        else unexplainedFailures.Clear(); // 同次正式declined结果只接纳明确player_declined，原节点不改写。
                    }
                }
                cleanupPolicy.Complete(pending.Click.Candidate, outcome);
                pending.Click.Coverage.SourceSettled = root.Status == EffectNodeStatus.Completed &&
                    new[] { "completed", "condition_not_met", "declined" }.Contains(outcome);
                pending.Click.Coverage.EffectSettled |= root.Status == EffectNodeStatus.Completed && outcome == "completed";
                pending.Click.Coverage.Outcome = outcome; CoverageEvent(pending.Click.Coverage, "效果终态", new {
                    pending.CommandId, pending.InvocationId, Root = root, ExpectedConditions = expectedConditions,
                    UnexplainedFailures = unexplainedFailures, DeclaredDeclineAccepted = outcome == "declined",
                    PositiveEffectCompletionClaimed = outcome == "completed",
                    RecoveryVerified = false });
                if (outcome == "completed" && CampaignMode) CampaignCompleted(pending.Click.Candidate.CoverageKey,
                    new { pending.Click.Coverage.Id, pending.CommandId, pending.InvocationId, Root = root });
                cleanupPending.Remove(pending);
            }
            foreach (var use in view.CharacterUses.Where(value => value.ExecutingPlayerId == view.ViewerPlayerId))
            {
                if (!actionCoverage.Values.Any(row => row.GameId == view.GameId && row.Player == view.ViewerPlayerId &&
                    row.Kind == "角色模式" && row.UseId == use.UseId && row.Confirmed)) continue;
                foreach (var mode in use.CompletedModes)
                {
                    var activation = view.Effects.SingleOrDefault(node => node.EffectTypeId == YC.Domain.Cards.CharacterAbilityEffectExecutor.ActivationEffectTypeId &&
                        ItemTransferEffectExecutor.Text(node.NormalizedArguments, "useId") == use.UseId &&
                        ItemTransferEffectExecutor.Text(node.NormalizedArguments, "mode") == mode);
                    if (activation?.Status != EffectNodeStatus.Completed) continue;
                    if (actionCoverageObservedModes.Add(view.GameId + ":" + use.UseId + ":" + mode))
                    {
                        string definition = view.ItemZones.Items.FirstOrDefault(item => item.ItemId == use.CardInstanceId)?.DefinitionId;
                        var row = actionCoverage.Values.FirstOrDefault(value => value.GameId == view.GameId && value.Player == view.ViewerPlayerId &&
                            value.Kind == "角色模式" && value.UseId == use.UseId && value.Entry == mode && value.Confirmed && !value.EffectSettled) ??
                            BeginCoverageAttempt(CoverageRow(view, "角色模式", use.CardInstanceId, definition, "", mode, mode == "strategy" ? "策略" : "计谋"));
                        row.UseId = use.UseId;
                        row.EffectSettled = use.Modes.Single(value => value.Mode == mode).Activated;
                        row.Outcome = row.EffectSettled ? "completed" : "角色生命周期已结束，但正文含失败或放弃，未计正常效果完成";
                        CoverageEvent(row, "效果终态", new { use.UseId, use.CompletedModes, use.DoubleUseGranted, use.Finished,
                            Activation = activation, Contract = use.Modes.Single(value => value.Mode == mode) });
                    }
                }
            }
            string round = view.GameId + ":" + view.Round;
            if (actionCoverageRound != null && actionCoverageRound != round)
            {
                Append("每局每回合实际覆盖与缺口.jsonl", new { FinishedRound = actionCoverageRound,
                    Covered = actionCoverage.Values.Where(value => value.EffectSettled).ToArray(),
                    Gaps = ActionCoverageGaps().ToArray(),
                    Next = "继续本局，优先当前本人合法可达的未结清来源；不因目标未出现重开" });
                PersistActionCleanupCoverage("上一回合已结束");
            }
            actionCoverageRound = round;
        }

        private bool DeclineActionCoverageCondition(string source, bool canExecute, bool canDecline) =>
            cleanupPolicy.ChooseDecline(source, canExecute, canDecline, actionCoverage.Values.Any(row => row.SourceItem == source && row.EffectSettled));

        private void PersistActionCleanupCoverage(string reason)
        {
            Write("行动与收尾覆盖汇总.json", new { Reason = reason, Rows = actionCoverage.Values.ToArray(),
                Pending = cleanupPending.Select(value => new { value.Click.Coverage.Id, value.CommandId, value.RootId }).ToArray(),
                PendingConditions = conditionCoveragePending.Select(value => new { value.Item1.Id, Root = value.Item2 }).ToArray(),
                Gaps = ActionCoverageGaps().Select(row => new { row.Id, row.Reason,
                    Next = "同版本后续正常回合或多局中，优先本人可见且合法的缺口" }).ToArray(),
                Unclaimed = new[] { "未出现的角色或卡面不能记未抽到或通过", "恢复未执行，所有RecoveryVerified为false", "模拟策略用例不计正常局验收" } });
        }
    }
}
#endif
