#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.PlayerJourney;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private sealed class DriverQuickRow
        {
            public Button Button;
            public AbilityActionOption Option;
            public string AutomationId, ItemId, DefinitionId, ActionId, AttemptKey, CoverageKey, KindKey;
        }
        private sealed class DriverQuickPending
        {
            public DriverQuickRow Row;
            public ActionCoverageRow Coverage;
            public string InvocationId, GameId, RootId;
            public object CommandReceipt;
        }
        private readonly HashSet<string> driverQuickAttempts = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> driverQuickCompleted = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<DriverQuickPending> driverQuickPending = new List<DriverQuickPending>();
        private bool driverQuickActionPerformed;

        private static List<DriverQuickRow> ReadVisibleQuickRows(GameStateView visible, string window,
            ISet<string> attempted, Func<string, int> coverage, Action<object> observe)
        {
            var result = new List<DriverQuickRow>();
            foreach (var marker in FindObjectsOfType<PlayerAutomationId>().Where(value =>
                value.Id.StartsWith("action.declared.", StringComparison.Ordinal) || value.Id.StartsWith("action.character.", StringComparison.Ordinal)))
            {
                var button = marker.GetComponent<Button>();
                AbilityActionOption option = null;
                string item = null, definition = null, action = null, reason = null;
                if (visible == null || visible.ViewerRole != GameStateViewerRole.Player) reason = "没有本人正式投影";
                else if (visible.HasAbilityActionCatalog)
                {
                    option = visible.AbilityActions.SingleOrDefault(value => marker.Id == "action.declared." + value.OptionId);
                    if (option == null) reason = "原行未关联当前声明目录的OptionId";
                    else if (!option.Enabled || option.EntryKind != "quick") reason = "正式投影不可用或不是快速入口：" + option.DisabledReason;
                    else if (option.SourceRef == null || option.VersionRef?.ContentPackHash != visible.ContentPackHash ||
                        option.SourceRef.AuthorizedPlayerId != visible.ViewerPlayerId || option.RequestRef?.AnsweringPlayerId != visible.ViewerPlayerId ||
                        !option.RequestRef.AllowedAnswers.Contains(option.OptionId) ||
                        !visible.ItemZones.Items.Any(value => value.ItemId == option.SourceRef.ItemId && value.DefinitionId == option.SourceRef.DefinitionId))
                        reason = "来源、观察者、版本或正式请求不匹配";
                    else { item = option.SourceRef.ItemId; definition = option.SourceRef.DefinitionId; action = option.ActionId; }
                }
                else
                {
                    var character = visible.Characters.SingleOrDefault(value => marker.Id == "action.character." + value.ItemId &&
                        value.ControllerPlayerId == visible.ViewerPlayerId && value.CanUse);
                    if (character == null) reason = "旧行没有本人正式可用角色来源";
                    else { item = character.ItemId; definition = character.DefinitionId; action = "legacy.character.use"; }
                }
                if (reason == null && (button == null || !button.IsActive() || !button.IsInteractable())) reason = "正式候选存在，但原按钮当前不可用";
                string key = item == null ? null : window + ":" + item + ":" + (option?.SourceRef.SourceKey ?? "legacy") + ":" +
                    action + ":" + option?.SourceRef.ActiveFaceId + ":" + option?.TrackBindingId;
                if (reason == null && attempted.Contains(key)) reason = "本行动窗口同一来源／面／动作／轨道已尝试，继续其它候选或正常推进";
                string kind = item != null && visible.Characters.Any(value => value.ItemId == item) ? "quick-kind:character" : "quick-kind:declared";
                string covered = "quick-action:" + definition + ":" + action;
                observe?.Invoke(new { Row = marker.Id, ItemId = item, DefinitionId = definition, ActionId = action,
                    OptionId = option?.OptionId, Source = option?.SourceRef, Request = option?.RequestRef,
                    Enabled = option?.Enabled, ButtonAvailable = button != null && button.IsActive() && button.IsInteractable(),
                    Reason = reason ?? "当前合法，等待缺口排序", AttemptKey = key });
                if (reason == null) result.Add(new DriverQuickRow { Button = button, Option = option, AutomationId = marker.Id,
                    ItemId = item, DefinitionId = definition, ActionId = action, AttemptKey = key, CoverageKey = covered, KindKey = kind });
            }
            return result.OrderBy(row => coverage(row.KindKey) == 0 ? 0 : 1).ThenBy(row => coverage(row.CoverageKey))
                .ThenBy(row => row.Option?.SortOrder ?? 0).ThenBy(row => row.AutomationId, StringComparer.Ordinal).ToList();
        }

        private static bool AccurateDriverQuickReceipt(AbilityActionOption option, int player, GameCommand command, WorkflowSubmissionResult result) =>
            option != null && command?.Kind == GameCommandKind.InvokeSettlementOption && command.PlayerId == player &&
            command.Parameters.TryGetValue("intent", out var intent) && intent == AbilityActionCommand.Intent &&
            command.Parameters.TryGetValue("optionId", out var id) && id == option.OptionId &&
            command.Parameters.TryGetValue("requestId", out var request) && request == option.RequestRef.InteractionId &&
            command.Parameters.TryGetValue("factsDigest", out var digest) && digest == option.RequestRef.FactsDigest &&
            command.Parameters.TryGetValue("contentHash", out var hash) && hash == option.VersionRef.ContentPackHash &&
            result?.CommandResult?.Succeeded == true && result.AppliedLocally;

        private static IEnumerator ContinueDriverQuickRequest(InteractionRequestProjection request, Func<IEnumerator> mode)
        {
            if (request != null && new[] { CharacterLifecycleService.ModeInteractionTypeId, CharacterLifecycleService.OrderInteractionTypeId,
                CharacterLifecycleService.SecondModeInteractionTypeId }.Contains(request.InteractionTypeId)) yield return mode();
            // 其它正式请求交原RunActionStep；无请求则直接回到正常推进，不虚构角色模式页。
        }

        private IEnumerator TryVisibleQuickAction()
        {
            driverQuickActionPerformed = false;
            Require(Request == null, "快速行动不能挤掉当前必答请求");
            yield return Press(MarkedButton("action.tab.1"), "正常快速行动原页签");
            var visible = CurrentPaymentView();
            Require(visible != null, "快速行动缺少当前本人正式显示投影");
            RecordQuickInventory(visible);
            string window = State.GameId + ":" + State.Round + ":" + State.ActionRound + ":" + LocalPlayer;
            int Count(string key) => (driverQuickCompleted.TryGetValue(key, out var count) ? count : 0) +
                (CampaignMode ? CampaignPriorityCount(key) : 0);
            var rows = ReadVisibleQuickRows(visible, window, driverQuickAttempts, Count,
                value => Append("快捷行动候选与未执行原因.jsonl", value));
            var sharedSpecial = PreferredSharedSpecial(visible);
            if (sharedSpecial != null)
                rows = rows.Where(row => row.Option?.TrackBindingId != sharedSpecial.TrackBindingId).ToList();
            if (rows.Count == 0) yield break;
            var chosen = rows[0];
            var matrix = BeginCoverageAttempt(chosen.Option == null ? CoverageRow(visible, "快速行动", chosen.ItemId, chosen.DefinitionId, "", chosen.ActionId, "角色牌") : QuickCoverage(visible, chosen.Option));
            matrix.Legal = true; matrix.Selected = true; matrix.Reason = "本人正式合法入口；先于消耗资源的主要行动使用，按结清缺口排序";
            CoverageEvent(matrix, "选择");
            driverQuickAttempts.Add(chosen.AttemptKey);
            driverQuickActionPerformed = true;
            int before = commands.Count, actor = LocalPlayer;
            Append("快捷行动决策.jsonl", new { visible.GameId, visible.StateRevision, PlayerId = actor, chosen.AutomationId,
                chosen.ItemId, chosen.DefinitionId, chosen.ActionId, OptionId = chosen.Option?.OptionId, chosen.AttemptKey,
                PriorKind = Count(chosen.KindKey), PriorAction = Count(chosen.CoverageKey),
                RemainingLegal = rows.Skip(1).Select(row => new { row.ItemId, row.ActionId, Reason = "本次先执行缺口优先的一项，后续步骤继续排序" }).ToArray(),
                Reason = "只排序本人正式合法快速行；主预算耗尽前仍先检查快速入口；每窗口同来源动作最多尝试一次" });
            yield return Press(chosen.Button, "正常原快速行动行" + chosen.AutomationId);
            var submitted = commands.Skip(before).ToArray();
            if (chosen.Option != null)
            {
                var accepted = submitted.Where(record => AccurateDriverQuickReceipt(chosen.Option, actor,
                    Read(record, "Command") as GameCommand, Read(record, "Result") as WorkflowSubmissionResult)).ToArray();
                Require(accepted.Length == 1, "声明式快速行动没有唯一原命令的准确成功Applied回执");
                var command = (GameCommand)Read(accepted[0], "Command");
                var invocation = State.EffectRuntime.AbilityActionInvocations.SingleOrDefault(value => value.CommandId == command.CommandId &&
                    value.OptionId == chosen.Option.OptionId && value.ActionId == chosen.ActionId && value.SourceRef.ItemId == chosen.ItemId &&
                    value.SourceRef.SourceKey == chosen.Option.SourceRef.SourceKey);
                Require(invocation != null, "快速行动准确回执未关联到原来源／ActionId的实际调用记录");
                matrix.Confirmed = true; matrix.NextRequest = Request?.InteractionTypeId; matrix.Evidence = command.CommandId;
                CoverageEvent(matrix, "确认", new { command.CommandId, invocation.InvocationId, Result = Read(accepted[0], "Result") });
                driverQuickPending.Add(new DriverQuickPending { Row = chosen, Coverage = matrix, InvocationId = invocation.InvocationId,
                    GameId = visible.GameId, RootId = invocation.OwnerEffectId, CommandReceipt = accepted[0] });
                Append("快捷行动准确受理.jsonl", new { visible.GameId, chosen.ItemId, chosen.ActionId,
                    OptionId = chosen.Option.OptionId, CommandReceipt = accepted[0], Invocation = invocation,
                    NextRequest = Request, FullEffectCompletionClaimed = false });
            }
            else Require(submitted.Any(record => (Read(record, "Command") as GameCommand)?.Kind == GameCommandKind.UseCharacterCard &&
                (Read(record, "Result") as WorkflowSubmissionResult)?.CommandResult?.Succeeded == true &&
                (Read(record, "Result") as WorkflowSubmissionResult)?.AppliedLocally == true), "旧角色行缺少原使用命令的准确回执");
            yield return ContinueDriverQuickRequest(Request, DriverQuickModeChoice);
            ObserveDriverQuickActionCompletion();
        }

        private IEnumerator DriverQuickModeChoice()
        {
            if (CampaignMode) { yield return CampaignModeChoice(); yield break; }
            var viewer = FindObjectsOfType<CardViewer>().FirstOrDefault(value => value.gameObject.activeInHierarchy &&
                (value.StrategyButton.IsActive() && value.StrategyButton.IsInteractable() || value.PlotButton.IsActive() && value.PlotButton.IsInteractable()));
            Require(viewer != null, "正式角色模式请求没有原可用查看页");
            string definition = CurrentPaymentView()?.ItemZones.Items.FirstOrDefault(value => value.ItemId == viewer.CardId)?.DefinitionId ?? viewer.CardId;
            var choices = new[] { new { Mode = "strategy", Button = viewer.StrategyButton }, new { Mode = "tactic", Button = viewer.PlotButton } }
                .Where(value => value.Button.IsActive() && value.Button.IsInteractable())
                .OrderBy(value => CoverageCount("character:" + definition + ":" + value.Mode)).ToArray();
            yield return Press(choices[0].Button, "正常原角色模式" + choices[0].Mode);
        }

        private static bool DriverQuickInvocationCompleted(AbilityActionInvocation invocation, IReadOnlyList<EffectNodeRuntimeState> nodes) =>
            invocation != null && invocation.Phase == "completed" && invocation.Outcome == "completed" && invocation.ReceiptIds.Count > 0 &&
            nodes.Any(node => node.EffectId == invocation.OwnerEffectId && node.Status == EffectNodeStatus.Completed) &&
            invocation.BodyEffectIds.All(id => nodes.Any(node => node.EffectId == id && node.Status == EffectNodeStatus.Completed));

        private void ObserveDriverQuickActionCompletion()
        {
            if (session == null) return;
            foreach (var pending in driverQuickPending.ToArray())
            {
                Require(State.GameId == pending.GameId, "未完成的快速行动跨局，不能续签旧覆盖");
                var invocation = State.EffectRuntime.AbilityActionInvocations.Single(value => value.InvocationId == pending.InvocationId);
                Require(invocation.Phase != "faulted", "快速行动正文调用故障：" + invocation.FailureReason);
                if (invocation.Phase != "completed") continue;
                bool completed = DriverQuickInvocationCompleted(invocation, State.EffectRuntime.EffectNodes);
                var evidence = new { pending.GameId, pending.Row.ItemId, pending.Row.DefinitionId, pending.Row.ActionId,
                    pending.CommandReceipt, Invocation = invocation, Completed = completed,
                    BodyNodes = State.EffectRuntime.EffectNodes.Where(node => invocation.BodyEffectIds.Contains(node.EffectId)).ToArray(),
                    PaymentNodes = State.EffectRuntime.EffectNodes.Where(node => invocation.PaymentEffectIds.Contains(node.EffectId)).ToArray(),
                    CurrentRequest = Request, CharacterUses = State.CharacterUses.Where(use => use.CardInstanceId == pending.Row.ItemId).ToArray() };
                Append("快捷行动实际结算.jsonl", evidence);
                pending.Coverage.EffectSettled = completed; pending.Coverage.Outcome = invocation.Outcome;
                CoverageEvent(pending.Coverage, "效果终态", new { invocation.InvocationId, invocation.Phase, invocation.Outcome,
                    invocation.ReceiptIds, invocation.BodyEffectIds, invocation.PaymentEffectIds, Completed = completed });
                driverQuickPending.Remove(pending);
                if (!completed) continue;
                foreach (var key in new[] { pending.Row.CoverageKey, pending.Row.KindKey })
                    driverQuickCompleted[key] = (driverQuickCompleted.TryGetValue(key, out var count) ? count : 0) + 1;
                if (CampaignMode) CampaignCompleted(pending.Row.CoverageKey, evidence);
            }
        }
    }
}
#endif
