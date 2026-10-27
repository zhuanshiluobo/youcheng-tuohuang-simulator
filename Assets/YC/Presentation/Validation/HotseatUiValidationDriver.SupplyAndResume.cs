#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Persistence;
using YC.PlayerJourney;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private bool LocalFullGameResumeMode => !string.IsNullOrEmpty(Argument("--yc-local-resume-game-id="));
        private MatchSaveSlot localResumeChosenSlot;
        private readonly Dictionary<string, ContinuationStage> continuationStages = new Dictionary<string, ContinuationStage>();
        private readonly Dictionary<string, string> continuationStageVersions = new Dictionary<string, string>();
        [Serializable] private sealed class ContinuationStage
        {
            public string GameId, ContentHash, Kind, Id, OwnerId, Entry, CommandId;
            public int Player, Revision;
            public bool Seen, Selected, Confirmed, EffectSettled, ExistingAtResume;
            public string[] SelectedIds = Array.Empty<string>();
        }
        private static bool TrySelectionCandidateId(string automationId, out string id)
        {
            id = null;
            foreach (string prefix in new[] { "selection.card.", "supply.select." })
                if (!string.IsNullOrEmpty(automationId) && automationId != "supply.select.confirm" && automationId.StartsWith(prefix, StringComparison.Ordinal))
                { id = automationId.Substring(prefix.Length); return !string.IsNullOrEmpty(id); }
            return false;
        }
        private static bool SupplyCandidateMatches(InteractionRequestProjection original, InteractionRequestProjection current,
            GameStateView view, int player, string automationId)
        {
            return TrySelectionCandidateId(automationId, out var id) && original != null && current != null &&
                view?.ViewerRole == GameStateViewerRole.Player && view.ViewerPlayerId == player && original.InteractionId == current.InteractionId &&
                FacilitySupplySelectionModel.TryCreate(original, view.ItemZones, player, view.StateRevision, out var expected) &&
                FacilitySupplySelectionModel.TryCreate(current, view.ItemZones, player, view.StateRevision, out var actual) &&
                expected.Identity == actual.Identity && actual.CanSelect(id);
        }
        private Button CurrentSupplyButton(InteractionRequestProjection request, string id)
        {
            string marker = "supply.select." + id;
            if (!SupplyCandidateMatches(request, Request, CurrentPaymentView(), LocalPlayer, marker)) return null;
            var matches = FindObjectsOfType<PlayerAutomationId>().Where(value => value.Id == marker && value.gameObject.activeInHierarchy &&
                value.GetComponent<Button>()?.IsInteractable() == true).Select(value => value.GetComponent<Button>()).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        private IEnumerator NormalSupplySelection(InteractionRequestProjection request)
        {
            Require(request != null && request.InteractionTypeId == FacilitySupplySelectionModel.InteractionType &&
                request.MinSelections == 1 && request.MaxSelections == 1, "供应驱动仅消费原正式单枚选择合同");
            var candidates = request.CandidateIds.Where(id => CurrentSupplyButton(request, id) != null)
                .OrderBy(id => IsUiTargetVisible(CurrentSupplyButton(request, id).gameObject) ? 0 : 1).ToArray();
            Require(candidates.Length > 0, "当前供应请求没有本人合法且可交互的真实顶牌，保留现场");
            string selected = candidates[0]; int before = commands.Count;
            Append("供应原控件分阶段.jsonl", new { Phase = "Seen", Request = request, LegalOriginalButtons = candidates,
                State.GameId, Player = LocalPlayer, HostCommands = commands.Count });
            yield return Press(CurrentSupplyButton(request, selected), "正常供应选择真实顶牌" + selected);
            Require(commands.Count == before, "供应选牌应只更新原草稿，不能提前提交");
            RecordRequestSelection(request, new[] { selected });
            var confirm = FindObjectsOfType<PlayerAutomationId>().Where(value => value.Id == "supply.select.confirm" &&
                value.gameObject.activeInHierarchy && value.GetComponent<Button>()?.IsInteractable() == true).ToArray();
            Require(confirm.Length == 1 && SupplyCandidateMatches(request, Request, CurrentPaymentView(), LocalPlayer, "supply.select." + selected),
                "供应确认前原请求/顶牌或唯一确认控件已变化，不点击陈旧草稿");
            yield return Press(confirm[0].GetComponent<Button>(), "正常供应原确认");
            RecordRequestConfirmation(request, before);
            Append("供应原控件分阶段.jsonl", new { Phase = "Confirmed", Request = request, Selected = selected,
                OriginalReceipt = commands[before], NextRequest = Request, FullSpecialCompletionClaimed = false });
            yield return Capture("正常供应原确认后继");
        }
        private static bool FacilityPanelCandidateMatches(InteractionRequestProjection original, InteractionRequestProjection current,
            GameStateView view, int player, int slot)
        {
            return FacilityPanelSelectionModel.TryCreate(original, view, player, out var expected) &&
                FacilityPanelSelectionModel.TryCreate(current, view, player, out var actual) &&
                expected.Identity == actual.Identity && actual.CanSelect(slot);
        }
        private FacilityBuildDialogView CurrentFacilityPanelPage(InteractionRequestProjection original)
        {
            if (mobile == null) return null;
            var coordinator = Read(mobile, "facilityPanelSelection") as FacilityPanelSelectionUiCoordinator;
            var visible = CurrentPaymentView();
            if (coordinator == null || !coordinator.IsActive ||
                !FacilityPanelSelectionModel.TryCreate(original, visible, LocalPlayer, out var expected)) return null;
            var bound = Read(coordinator, "draft") as FacilityPanelSelectionModel;
            var page = Read(coordinator, "page") as FacilityBuildDialogView;
            return bound != null && bound.Identity == expected.Identity && page != null && page.gameObject.activeInHierarchy &&
                expected.Candidates.Count > 0 && FacilityPanelCandidateMatches(original, Request, visible, LocalPlayer,
                    expected.Candidates[0].SlotIndex) ? page : null;
        }
        private Button CurrentFacilityPanelButton(InteractionRequestProjection request, int slot)
        {
            var page = CurrentFacilityPanelPage(request);
            if (page == null || !FacilityPanelCandidateMatches(request, Request, CurrentPaymentView(), LocalPlayer, slot)) return null;
            var buttons = Read(page, "citySlots") as Button[];
            if (buttons == null || slot < 0 || slot >= buttons.Length) return null;
            var button = buttons[slot];
            return button != null && button.IsActive() && button.IsInteractable() &&
                button.GetComponent<PlayerAutomationId>()?.Id == "facility.panel.slot." + slot.ToString(CultureInfo.InvariantCulture)
                ? button : null;
        }
        private IEnumerator NormalFacilityPanelSelection(InteractionRequestProjection request)
        {
            Require(request != null && request.InteractionTypeId == FacilityPanelSelectionRequest.InteractionType &&
                request.MinSelections == 1 && request.MaxSelections == 1, "设施面板驱动只消费正式单格选择请求");
            var original = GameStateCloneService.DeepClone(request);
            string game = State.GameId, content = State.ContentPackHash;
            int before = commands.Count, revision = State.EffectRuntime.StateRevision;
            var page = CurrentFacilityPanelPage(original);
            Require(page != null && original.StateRevision == revision, "原设施面板尚未绑定本人当前正式候选，保留现场");
            var slots = original.CandidateIds.Select(id => int.Parse(id, CultureInfo.InvariantCulture))
                .Where(slot => CurrentFacilityPanelButton(original, slot) != null)
                .OrderBy(slot => IsUiTargetVisible(CurrentFacilityPanelButton(original, slot).gameObject) ? 0 : 1)
                .ThenBy(slot => slot).ToArray();
            Require(slots.Length > 0, "原设施面板没有可交互的合法格，保留现场，不走地图或代答");
            int chosen = slots[0]; string candidate = chosen.ToString(CultureInfo.InvariantCulture);
            var model = Read(Read(mobile, "facilityPanelSelection"), "draft") as FacilityPanelSelectionModel;
            Append("设施面板原控件分阶段.jsonl", new { Phase = "Seen", State.GameId, Player = LocalPlayer,
                Request = original, Candidates = model.Candidates, OriginalButtons = slots, HostCommands = before });
            var slotButton = CurrentFacilityPanelButton(original, chosen);
            yield return PointerSequenceGuarded(slotButton.gameObject, "正常设施面板原格选择" + candidate,
                () => State.GameId == game && State.ContentPackHash == content && commands.Count == before &&
                    ReferenceEquals(CurrentFacilityPanelButton(original, chosen), slotButton));
            Require(State.GameId == game && State.ContentPackHash == content && commands.Count == before &&
                State.EffectRuntime.StateRevision == revision && ReferenceEquals(CurrentFacilityPanelPage(original), page),
                "设施格选择只应更新原页面草稿；请求、实例顺序或游戏身份已变化，不点旧确认");
            var currentCoordinator = Read(mobile, "facilityPanelSelection");
            var confirm = Read(page, "confirm") as Button;
            bool ConfirmationCurrent() => State.GameId == game && State.ContentPackHash == content && commands.Count == before &&
                ReferenceEquals(CurrentFacilityPanelPage(original), page) && (int)Read(currentCoordinator, "selected") == chosen &&
                FacilityPanelCandidateMatches(original, Request, CurrentPaymentView(), LocalPlayer, chosen) &&
                confirm != null && confirm.IsActive() && confirm.IsInteractable() &&
                confirm.GetComponent<PlayerAutomationId>()?.Id == "facility.panel.confirm";
            Require(ConfirmationCurrent(), "原设施格草稿或唯一确认绑定已变化，不点击陈旧控件");
            RecordRequestSelection(original, new[] { candidate });
            yield return PointerSequenceGuarded(confirm.gameObject, "正常设施面板原确认", ConfirmationCurrent);
            RecordRequestConfirmation(original, before);
            var command = Read(commands[before], "Command") as GameCommand;
            var answered = State.EffectRuntime.InteractionRequests.SingleOrDefault(value =>
                value.GetStableInteractionId() == original.InteractionId);
            Require(command != null && command.OptionIds.SequenceEqual(new[] { candidate }) &&
                answered?.OwnerEffectId == original.OwnerEffectId && answered.Status == "answered" &&
                State.GameId == game && State.ContentPackHash == content && State.EffectRuntime.StateRevision > revision,
                "设施面板原确认未恰好回答原owner的正式格候选或未推进修订");
            Write("设施面板原确认回执-" + original.InteractionId + ".json", new { State.GameId, State.Round,
                OriginalRequest = original, OriginalCandidates = model.Candidates, SelectedSlot = chosen,
                CommandsAdded = commands.Count - before, CommandAndReceipt = commands[before],
                BeforeRevision = revision, AfterRevision = State.EffectRuntime.StateRevision,
                NextRequest = Request, FullSourceEffectCompletionClaimed = false, State = Observe() });
            Covered("page:facility-panel-selection");
            yield return Capture("正常设施面板原确认后继");
        }
        private void ObserveRequestStage(InteractionRequestProjection request)
        {
            if (request == null || !request.VisibleToViewer || request.AnsweringPlayerId != LocalPlayer || request.Status != "open") return;
            if (!continuationStages.ContainsKey(request.InteractionId)) continuationStages.Add(request.InteractionId, new ContinuationStage {
                GameId = State.GameId, ContentHash = State.ContentPackHash, Kind = "正式选择请求", Id = request.InteractionId,
                OwnerId = request.OwnerEffectId, Entry = request.InteractionTypeId, Player = LocalPlayer, Revision = request.StateRevision, Seen = true });
            ObserveContinuationStages();
        }
        private void RecordRequestSelection(InteractionRequestProjection request, string[] selected)
        {
            if (request == null) return;
            ObserveRequestStage(request);
            Require(continuationStages.TryGetValue(request.InteractionId, out var row) && selected.All(request.CandidateIds.Contains), "选择阶段未匹配本人原候选");
            row.Selected = true; row.SelectedIds = selected; ObserveContinuationStages();
        }
        private void RecordRequestConfirmation(InteractionRequestProjection request, int before)
        {
            if (request == null) return;
            Require(commands.Count == before + 1, "原牌选择确认没有恰好一次准确命令");
            var command = Read(commands[before], "Command") as GameCommand;
            var receipt = Read(commands[before], "Result") as WorkflowSubmissionResult;
            int submittedRevision = NetworkMode ? NetworkOriginalRequestRevision(request, commands[before]) : request.StateRevision;
            Require(command?.Kind == GameCommandKind.AnswerInteraction && command.PlayerId == request.AnsweringPlayerId &&
                command.Parameters.TryGetValue("interactionId", out var id) && id == request.InteractionId &&
                command.Parameters.TryGetValue("expectedRevision", out var revision) && revision == submittedRevision.ToString(CultureInfo.InvariantCulture) &&
                command.OptionIds.All(request.CandidateIds.Contains) && receipt?.CommandResult?.Succeeded == true && receipt.AppliedLocally,
                "原牌选择的请求、候选、版本或Applied回执不匹配");
            var row = continuationStages[request.InteractionId]; row.Confirmed = true; row.CommandId = command.CommandId;
            ObserveContinuationStages();
        }
        private void ObserveContinuationStages()
        {
            if (session == null) return;
            foreach (var row in continuationStages.Values.Where(value => value.Kind == "正式选择请求"))
            {
                var request = State.EffectRuntime.InteractionRequests.SingleOrDefault(value => value.InteractionId == row.Id);
                var owner = State.EffectRuntime.EffectNodes.SingleOrDefault(value => value.EffectId == row.OwnerId);
                row.EffectSettled = row.Confirmed && request?.Status == "answered" && owner?.Status == EffectNodeStatus.Completed;
            }
            var visible = CurrentPaymentView();
            foreach (var invocation in State.EffectRuntime.AbilityActionInvocations.Where(value => value.SourceRef?.AuthorizedPlayerId == LocalPlayer &&
                visible?.ItemZones.Items.Any(item => item.ItemId == value.SourceRef.ItemId) == true))
            {
                var record = commands.SingleOrDefault(value => (Read(value, "Command") as GameCommand)?.CommandId == invocation.CommandId);
                if (record == null && resumedCompletedEffectIds.Contains(invocation.OwnerEffectId)) continue;
                string key = "invocation:" + invocation.InvocationId;
                if (!continuationStages.TryGetValue(key, out var row)) continuationStages[key] = row = new ContinuationStage {
                    GameId = State.GameId, ContentHash = State.ContentPackHash, Kind = "来源行动调用", Id = key, OwnerId = invocation.OwnerEffectId,
                    Entry = invocation.ActionId, Player = LocalPlayer, Seen = true, ExistingAtResume = record == null };
                var receipt = record == null ? null : Read(record, "Result") as WorkflowSubmissionResult;
                row.Selected = record != null; row.Confirmed = receipt?.CommandResult?.Succeeded == true && receipt.AppliedLocally;
                row.CommandId = invocation.CommandId;
                row.EffectSettled = DriverQuickInvocationCompleted(invocation, State.EffectRuntime.EffectNodes);
            }
            foreach (var row in continuationStages.Values)
            {
                string version = JsonConvertStage(row);
                if (continuationStageVersions.TryGetValue(row.Id, out var previous) && previous == version) continue;
                continuationStageVersions[row.Id] = version;
                Append("请求与来源行动分阶段.jsonl", new { Stage = row, PreviousConfirmationReused = false,
                    Rule = "请求、供应后继图纸选择和整个来源行动分别核证；恢复前的选择/确认不计本轮新操作" });
            }
        }
        private static string JsonConvertStage(ContinuationStage row) => Newtonsoft.Json.JsonConvert.SerializeObject(row);

        private static int LocalResumeSlotIndex(string slot)
        {
            var parts = (slot ?? "").Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int number) || number < 1 || number > 3) return -1;
            return parts[0] == "manual" ? number - 1 : parts[0] == "auto" ? MatchSaveStore.ManualCount + number - 1 : -1;
        }
        private static MatchSaveSlot SelectLocalResumeSlot(IEnumerable<MatchSaveSlot> slots, int index,
            string gameId, int players, string mode, string contentHash)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(mode) || string.IsNullOrEmpty(contentHash) || players < 2 || players > 4 || index < 0) return null;
            var matches = slots?.Where(slot => slot.Index == index && slot.Exists && slot.Error == null && slot.Data?.GameId == gameId &&
                slot.Data.Mode == LaunchMode.Local && slot.Data.ContentHash == contentHash && slot.Data.Seats?.Count == players).ToArray();
            if (matches?.Length != 1) return null;
            var configuration = matches[0].Data.Configuration;
            return configuration?.PlayerCount == players && configuration.RuleModeId == mode && configuration.ContentPackHash == contentHash ? matches[0] : null;
        }
        private IEnumerator LoadLocalFullGameFromOriginalWindow(StartMenuView menu)
        {
            Require(!NetworkMode && selectedCase == "full-game" && !SaveResumeMode, "本地整局恢复参数仅用于本地full-game");
            string gameId = Argument("--yc-local-resume-game-id="), mode = Argument("--yc-local-resume-rule-mode="), hash = Argument("--yc-local-resume-content-hash=");
            Require(int.TryParse(Argument("--yc-local-resume-player-count="), out int players) && players == ExpectedLocalPlayerCount &&
                !string.IsNullOrEmpty(mode) && !string.IsNullOrEmpty(hash), "同局恢复必须显式给出匹配人数、模式和活动内容哈希");
            int index = LocalResumeSlotIndex(Argument("--yc-local-resume-slot="));
            Require(index >= 0, "同局恢复必须明确正常auto/manual槽位，不默选其它档");
            Require(Guid.TryParse(Argument("--yc-local-resume-source-mvid="), out _), "同局恢复须声明原冻结程序MVID以记录跨构建身份");
            yield return Press(menu.LoadGameButton, "正常主页读取同局完整局原档");
            yield return SaveResumeAwait(() => SaveResumeWindow != null && !(bool)Read(SaveResumeWindow, "busy") && SaveResumeSlots != null, "原正式存档列表");
            localResumeChosenSlot = SelectLocalResumeSlot(SaveResumeSlots, index, gameId, players, mode, hash);
            Require(localResumeChosenSlot != null, "原列表指定同局档的身份、人数、模式或活动内容不匹配，不选其它局");
            Write("同局恢复原列表选择身份.json", new { localResumeChosenSlot.Index, localResumeChosenSlot.Data.GameId,
                localResumeChosenSlot.Data.ContentHash, localResumeChosenSlot.Data.Mode, Players = players, RuleMode = mode,
                localResumeChosenSlot.Data.SavedUtcTicks, CurrentAssembly = typeof(HotseatUiValidationDriver).Assembly.ManifestModule.ModuleVersionId,
                PreviousAssembly = Argument("--yc-local-resume-source-mvid="), HiddenCardsReadForStrategy = false });
            yield return Press(((Button[])Read(SaveResumeWindow, "rows"))[localResumeChosenSlot.Index], "原列表指定同局正常档");
            yield return Press(((GenericWindowView)Read(SaveResumeWindow, "window")).ConfirmButton, "原确认正式读取同一对局");
            yield return SaveResumeAwait(() => FindObjectOfType<MobileCityInteractionController>() != null && !SceneTransitionContext.IsTransitionInProgress, "原生产恢复进入正式游戏场景");
        }
        private void RecordLocalFullGameResumeBoundary()
        {
            Require(localResumeChosenSlot != null && State.GameId == localResumeChosenSlot.Data.GameId &&
                State.ContentPackHash == Argument("--yc-local-resume-content-hash=") && State.Players.Count == ExpectedLocalPlayerCount &&
                State.RuleConfiguration.RuleModeId == Argument("--yc-local-resume-rule-mode="), "生产恢复后的同局身份不匹配，停止在任何行动之前");
            string revision = Argument("--yc-local-resume-revision=");
            Require(string.IsNullOrEmpty(revision) || int.TryParse(revision, out int expected) && State.EffectRuntime.StateRevision == expected,
                "生产恢复后的确认点版本不同，不能操作其它断点");
            resumedCompletedEffectIds.UnionWith(State.EffectRuntime.EffectNodes.Where(node => node.Status == EffectNodeStatus.Completed).Select(node => node.EffectId));
            Write("本地同局正式恢复初始边界.json", new { State.GameId, State.ContentPackHash, State.Round, State.Phase,
                Revision = State.EffectRuntime.StateRevision, Players = State.Players.Count, Request, PriorCommandsReplayed = false,
                ExistingCompletedEffectIds = resumedCompletedEffectIds, CommandsSinceOriginalLoad = commands.Count,
                CurrentAssembly = typeof(HotseatUiValidationDriver).Assembly.ManifestModule.ModuleVersionId,
                PreviousAssembly = Argument("--yc-local-resume-source-mvid="), OriginalSlot = localResumeChosenSlot.Index,
                SameContentHash = true, RestoredThroughOriginalUi = true, ExistingCompletionIsNotNewCoverage = true });
        }
    }
}
#endif
