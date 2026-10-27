#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.PlayerJourney;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private static bool RequestedEntrepreneurMode(string mode, string legacy) =>
            !string.IsNullOrEmpty(mode) ? string.Equals(mode, "entrepreneur", StringComparison.OrdinalIgnoreCase) : legacy == "true";
        private bool EntrepreneurUiMode => RequestedEntrepreneurMode(Argument("--yc-ui-rule-mode="), Argument("--yc-hotseat-entrepreneur="));
        private static int RequestedLocalPlayerCount(bool entrepreneur, string supplied)
        {
            if (!entrepreneur) return 4;
            return int.TryParse(supplied ?? "2", System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int count) && count >= 2 && count <= 4 ? count : 0;
        }
        private int ExpectedLocalPlayerCount => NetworkMode ? 4 : RequestedLocalPlayerCount(EntrepreneurUiMode, Argument("--yc-hotseat-player-count="));

        private IEnumerator ConfigureEntrepreneurRoom(StartMenuRoomPanelView room)
        {
            Require(CampaignMode && (!NetworkMode || networkRole == "host"), "企业家房间配置仅由正常完整局房主操作");
            Require(room.DisplayedRoom.LocalPlayerId == room.DisplayedRoom.HostPlayerId, "企业家房间配置必须是本端正式房主身份");
            Require(ExpectedLocalPlayerCount >= 2 && ExpectedLocalPlayerCount <= 4, "--yc-hotseat-player-count 必须为2、3或4");
            string mapId = ExpectedLocalPlayerCount == 4 ? "map-four-players" : "map-three-players";
            stage = "原房间控件配置" + ExpectedLocalPlayerCount + "人全扩展企业家";
            // 内容目录校验要求至少两人；先正常入座，再修改地图、扩展和模式。
            if (!NetworkMode)
            {
                var seats = (StartMenuRoomPanelView.SeatBinding[])Read(room, "seats");
                for (int seat = 1; seat < ExpectedLocalPlayerCount; seat++)
                    yield return EntrepreneurRoomControl(room, seats[seat].HotSeatButton, "房间原按钮添加本地玩家" + (seat + 1),
                        () => room.DisplayedRoom.PlayerCount == seat + 1);
                Require(room.DisplayedRoom.PlayerCount == ExpectedLocalPlayerCount, "本地玩家未正常入座");
            }
            foreach (var id in new[] { "enterprise", "infected" })
            {
                var option = ((StartMenuRoomPanelView.ContentOption[])Read(room, "expansions")).Single(v => v.Id == id);
                if (!option.Selection.gameObject.activeSelf)
                    yield return EntrepreneurRoomControl(room, option.Button, "房间启用扩展" + id,
                        () => room.DisplayedRoom.Configuration.EnabledExpansionIds.Contains(id));
            }
            var mode = ((StartMenuRoomPanelView.ContentOption[])Read(room, "ruleModes")).Single(v => v.Id == "entrepreneur");
            if (!mode.Selection.gameObject.activeSelf)
                yield return EntrepreneurRoomControl(room, mode.Button, "房间原按钮选择企业家模式",
                    () => room.DisplayedRoom.Configuration.RuleModeId == "entrepreneur");
            var map = ((StartMenuRoomPanelView.ContentOption[])Read(room, "maps")).Single(v => v.Id == mapId);
            if (!map.Selection.gameObject.activeSelf)
                yield return EntrepreneurRoomControl(room, map.Button, "房间原按钮选择对应人数地图",
                    () => room.DisplayedRoom.Configuration.MapId == mapId);
            var actual = room.DisplayedRoom;
            Require((NetworkMode ? actual.PlayerCount >= 1 && actual.PlayerCount <= 4 : actual.PlayerCount == ExpectedLocalPlayerCount) && actual.Configuration.RuleModeId == "entrepreneur" &&
                actual.Configuration.MapId == mapId &&
                new[] { "core", "enterprise", "infected" }.All(actual.Configuration.EnabledExpansionIds.Contains),
                "原房间配置未形成对应人数、地图及全扩展企业家");
            Write("企业家实际房间配置.json", actual);
            yield return Capture(ExpectedLocalPlayerCount + "人全扩展企业家房间");
        }

        private IEnumerator EntrepreneurRoomControl(StartMenuRoomPanelView room, Button button, string description, Func<bool> accepted)
        {
            var before = room.DisplayedRoom;
            yield return Press(button, description);
            var wait = Time.realtimeSinceStartup;
            while ((bool)Read(room, "requestPending") && Time.realtimeSinceStartup - wait < 30f) yield return null;
            var after = room.DisplayedRoom;
            Append("企业家房间逐项回执.jsonl", new { Description = description, Before = before, After = after,
                Status = room.StatusText.text, Summary = room.ValidationText.text, Accepted = accepted(), Retry = false });
            Require(accepted(), description + "未受理：" + room.StatusText.text);
        }

        private static bool IsEntrepreneurUiRequest(InteractionRequestProjection request) => request != null &&
            new[] { EntrepreneurFlow.PreviewInteraction, EntrepreneurFlow.ChooseInteraction, EntrepreneurFlow.FaceInteraction }
                .Contains(request.InteractionTypeId);

        private void RecordEntrepreneurVisibleFacts()
        {
            var visible = CurrentPaymentView();
            Require(visible != null, "企业家驱动缺少当前座位、当前版本的正式显示投影");
            Append("企业家本人可见事实.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                visible.GameId, visible.ContentPackHash, visible.StateRevision, visible.ViewerPlayerId,
                State.Round, State.Phase, Request, visible.Entrepreneurs,
                DecisionInput = "仅当前座位正式显示投影和公开信息；未跨座保存私有候选用于决策" });
        }

        private IEnumerator EntrepreneurUiChoice(InteractionRequestProjection request)
        {
            var visible = CurrentPaymentView();
            Require(visible != null && request.AnsweringPlayerId == visible.ViewerPlayerId && request.VisibleToViewer &&
                request.StateRevision == visible.StateRevision && request.MinSelections == 1 && request.MaxSelections == 1,
                "企业家正式请求的回答者、可见性或版本不符");
            var before = commands.Count;
            var actor = LocalPlayer;
            var originalId = request.InteractionId;
            string selected;
            yield return new WaitForEndOfFrame();
            SaveCapture("企业家请求-玩家" + actor + "-回合" + State.Round + "-" + request.InteractionTypeId);
            if (request.InteractionTypeId == EntrepreneurFlow.PreviewInteraction)
            {
                var page = FindObjectsOfType<EnterpriseSelectionDialogView>().FirstOrDefault(v => v.IsShowing && v.Draft.Projection?.ReadOnly == true);
                if (page == null) throw new DriverContinuationPendingException("本人企业只读预览原页面尚未就绪，重新读取当前路由");
                var displayed = page.Draft.Projection.Boards.Select(b => b.Id).ToArray();
                var own = visible.Entrepreneurs.OwnEnterpriseCandidateItemIds.ToArray();
                CheckContent(own.Length > 0 && displayed.SequenceEqual(own) && page.Draft.SelectedIds.Count == 0 &&
                    !page.Draft.CanConfirm, "本人初始企业预览须使用原企业界面，保留原顺序且不可选择提交");
                CheckContent(request.CandidateIds.SequenceEqual(new[] { "continue" }), "企业预览继续请求与旧夹具预期不符");
                Append("U02原企业页面.jsonl", new {Frame = Time.frameCount, visible.GameId, visible.StateRevision, visible.ViewerPlayerId,
                    Page = page.GetType().Name, Prefab = "GameplayDialogRegistry.EnterpriseSelectionPrefab", page.Draft.Projection,
                    CommandsBeforeContinue = commands.Count, NormalEnterpriseSelectionSubmitted = false});
                if (UiRegressionMode) yield return InspectAssignedEnterprisePage(page, own);
                selected = request.CandidateIds.Single();
                yield return Press(page.ReadOnlyContinueButton,
                    "本人初始企业只读预览后原继续按钮");
            }
            else
            {
                if (UiRegressionMode && request.InteractionTypeId == EntrepreneurFlow.ChooseInteraction)
                {
                    yield return Press(FindObjectsOfType<Button>().Single(b => b.name == "Inspect Assigned Enterprises" && b.IsInteractable()),
                        "选企业家时返回查看本人企业候选");
                    var preview = FindObjectsOfType<EnterpriseSelectionDialogView>().Single(v => v.IsShowing && v.Draft.Projection?.ReadOnly == true);
                    yield return InspectAssignedEnterprisePage(preview, visible.Entrepreneurs.OwnEnterpriseCandidateItemIds.ToArray());
                    yield return Press(preview.ReadOnlyContinueButton, "原企业只读页返回企业家能力牌选择");
                    Require(commands.Count == before && Request?.InteractionId == originalId, "返回预览改变了原请求或提交了企业");
                }
                var cards = FindObjectsOfType<PlayerAutomationId>().Where(v => v.Id.StartsWith("selection.card.") &&
                    v.GetComponent<Button>()?.IsInteractable() == true).ToArray();
                var ids = cards.Select(v => v.Id.Substring("selection.card.".Length)).ToArray();
                CheckContent(ids.Length == request.CandidateIds.Count && ids.All(request.CandidateIds.Contains),
                    "企业家选择控件与正式候选集合不一致");
                // 以当前正式顺序选首个尚可选项；不按未公开牌或库存推算收益。
                selected = request.CandidateIds.FirstOrDefault(ids.Contains);
                if (selected == null) throw new DriverContinuationPendingException("当前企业家正式候选暂无对应原控件，重新读取，不代答");
                if (request.InteractionTypeId == EntrepreneurFlow.FaceInteraction)
                {
                    var own = visible.Entrepreneurs.Players.Single(p => p.PlayerId == actor);
                    CheckContent(own.RoundSlots.Any(s => s.RoundNumber == State.Round && s.Candidates.Any(c => c.CandidateId == selected)),
                        "企业家选面不属于本人当前回合正式组");
                }
                yield return Press(cards.Single(v => v.Id == "selection.card." + selected).GetComponent<Button>(),
                    "本人企业家正式候选原控件选择" + selected);
                if (commands.Count != before)
                    throw new DriverContentDifferenceException("企业家草稿点击实际已提交，保首次差异并重新读取当前请求，不再点击旧确认");
                yield return Press(FindObjectsOfType<Button>().Single(b => b.name == "Confirm Selection" && b.IsInteractable()),
                    "企业家原确认按钮首次确认");
            }
            RequireOriginalReceipt(before, "企业家原控件首次确认");
            var record = commands[before];
            var command = Read(record, "Command") as GameCommand;
            var result = Read(record, "Result") as WorkflowSubmissionResult;
            Require(command?.Kind == GameCommandKind.AnswerInteraction && command.PlayerId == actor &&
                command.Parameters.TryGetValue("interactionId", out var id) && id == originalId &&
                command.OptionIds.SequenceEqual(new[] { selected }) &&
                result?.CommandResult?.Succeeded == true && result.AppliedLocally,
                "企业家首次确认未获得原命令的准确Applied回执");
            Append("企业家原控件完成.jsonl", new { visible.GameId, visible.ContentPackHash, State.Round, PlayerId = actor,
                OriginalRequest = request, Selected = selected, BeforeVisible = visible.Entrepreneurs,
                CommandAndReceipt = record, AfterVisible = CurrentPaymentView()?.Entrepreneurs,
                DirectSubmission = false, Retry = false });
        }
        private IEnumerator InspectAssignedEnterprisePage(EnterpriseSelectionDialogView page, string[] ids)
        {
            int before = commands.Count;
            var visible = CurrentPaymentView();
            Require(visible != null && Request != null, "企业详情查看缺少当前正式投影或原请求");
            string gameId = visible.GameId, contentHash = visible.ContentPackHash;
            int revision = visible.StateRevision, viewer = visible.ViewerPlayerId;
            string request = Newtonsoft.Json.JsonConvert.SerializeObject(Request);
            var expectedProjection = page.Draft.Projection;
            bool Current()
            {
                var current = CurrentPaymentView();
                return current != null && current.GameId == gameId && current.ContentPackHash == contentHash &&
                    current.StateRevision == revision && current.ViewerPlayerId == viewer &&
                    Newtonsoft.Json.JsonConvert.SerializeObject(Request) == request && commands.Count == before;
            }
            Require(page.Draft.Projection.Boards.Select(b => b.Id).SequenceEqual(ids) &&
                page.Draft.Projection.Boards.All(b => b.Details.Length > 0), "原企业页面候选或正式企业详情缺失");
            var target = page.GetComponentsInChildren<EnterpriseTargetView>().First(v => v.gameObject.activeInHierarchy);
            yield return Press((Button)Read(target, "button"), "原企业页面单击企业只查看详情栏");
            Require(commands.Count == before && page.Draft.SelectedIds.Count == 0, "只读点企业产生了选择或命令");
            // 当前企业页以原企业板右键/长按放大；右栏摘录按钮在该源资产中并未启用。
            var originalButton = (Button)Read(target, "button");
            var point = UiPointerPoint(originalButton.gameObject);
            Require(FirstUiClickTarget(point) == originalButton.gameObject, "企业板右键必须命中原可见控件");
            var pointer = Pointer(point); pointer.button = UnityEngine.EventSystems.PointerEventData.InputButton.Right;
            Append("控件事件.jsonl", new {Index = ++eventNumber, Frame = Time.frameCount, Stage = stage,
                Description = "原企业板右键放大", Input = "原可见控件右键PointerDown/Up/Click，非外部鼠标",
                Path = ComponentPath(originalButton.transform), ScreenX = point.x, ScreenY = point.y});
            UnityEngine.EventSystems.ExecuteEvents.Execute(originalButton.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
            UnityEngine.EventSystems.ExecuteEvents.Execute(originalButton.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
            UnityEngine.EventSystems.ExecuteEvents.Execute(originalButton.gameObject, pointer, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
            yield return Settle();
            yield return Capture("U02本人企业板详情与只读预览");
            ClickEnterpriseDetailsReturnSurface(page, expectedProjection, Current,
                observation => Append("企业详情返回指针.jsonl", observation));
            yield return Settle();
            Require(Current(), "企业详情与返回改变了原请求、正式身份或提交了命令");
            Append("U02企业板详情返回.jsonl", new {Frame = Time.frameCount, State.GameId, LocalPlayer, OriginalCandidates = ids,
                page.Draft.Projection, Selected = page.Draft.SelectedIds, CommandCountBefore = before, CommandCountAfter = commands.Count,
                Page = page.GetType().Name, NormalControls = true});
        }

        // 原详情返回面将左键交给弹窗根处理；可见面与处理器分别校验，不改普通按钮的命中规则。
        private static void ClickEnterpriseDetailsReturnSurface(EnterpriseSelectionDialogView page,
            EnterpriseSelectionProjection expected, Func<bool> current, Action<object> record)
        {
            Require(page != null && expected != null && page.IsShowing && page.isActiveAndEnabled,
                "企业详情返回：原页面已隐藏或失效，未调用指针回调");
            var surface = Read(page, "detailsReturnSurface") as GameObject;
            var details = Read(page, "detailPage") as GameObject;
            var graphic = surface == null ? null : surface.GetComponent<Graphic>();
            Require(surface != null && details != null && graphic != null,
                "企业详情返回：源页面缺少详情返回面，未调用指针回调");
            var point = UiPointerPoint(surface);
            var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            void Validate()
            {
                Require(current != null && current() && page != null && page.IsShowing && page.isActiveAndEnabled &&
                    expected.ReadOnly && EnterpriseSelectionDraft.SameContract(expected, page.Draft.Projection) &&
                    expected.Revision == page.Draft.Projection.Revision && !page.Draft.IsPending,
                    "企业详情返回：原请求、观察者、版本或只读页已失效，未调用指针回调");
                Require(details.activeInHierarchy && surface.activeInHierarchy && graphic.IsActive() &&
                    graphic.raycastTarget && !graphic.canvasRenderer.cull &&
                    Time.frameCount != (int)Read(page, "detailsOpenedFrame"),
                    "企业详情返回：详情未打开、返回面不可见或仍在打开当帧，未调用指针回调");
                Require(UnityEngine.EventSystems.EventSystem.current != null &&
                    point.x >= 0 && point.y >= 0 && point.x < Screen.width && point.y < Screen.height,
                    "企业详情返回：实际指针不在视口，未调用指针回调");
                hits.Clear();
                UnityEngine.EventSystems.EventSystem.current.RaycastAll(Pointer(point), hits);
                Require(hits.Count > 0 && hits[0].gameObject == surface &&
                    FirstUiClickTarget(point) == page.gameObject &&
                    UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(surface) == page.gameObject,
                    "企业详情返回：首命中不再是原返回面或原弹窗处理器，未调用指针回调");
            }
            Validate();
            string projection = Newtonsoft.Json.JsonConvert.SerializeObject(page.Draft.Projection);
            var selected = page.Draft.SelectedIds.ToArray();
            string inspected = page.Draft.InspectedId;
            var scroll = (Vector2)Read(page, "detailsScrollPosition");
            record?.Invoke(new { Phase = "命中已核", Frame = Time.frameCount, expected.GameId,
                expected.ViewerPlayerId, expected.Revision, expected.RequestId,
                Surface = ComponentPath(surface.transform), Handler = ComponentPath(page.transform),
                ScreenX = point.x, ScreenY = point.y, RaycastDetails = UiRaycastDetails(point),
                Input = "原返回面首命中处理器的PointerDown/PointerUp/PointerClick，非外部鼠标" });
            var data = Pointer(point);
            data.pointerCurrentRaycast = data.pointerPressRaycast = hits[0];
            data.pointerPress = page.gameObject; data.rawPointerPress = surface;
            void Send<T>(string name, UnityEngine.EventSystems.ExecuteEvents.EventFunction<T> callback)
                where T : UnityEngine.EventSystems.IEventSystemHandler
            {
                Validate();
                bool handled = UnityEngine.EventSystems.ExecuteEvents.Execute(page.gameObject, data, callback);
                record?.Invoke(new { Phase = "原指针事件返回", Frame = Time.frameCount, Callback = name, Handled = handled });
            }
            Send("PointerDown", UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
            Send("PointerUp", UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
            Send("PointerClick", UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
            Require(current() && page.IsShowing && !details.activeSelf && !surface.activeSelf && page.Body.gameObject.activeInHierarchy &&
                Newtonsoft.Json.JsonConvert.SerializeObject(page.Draft.Projection) == projection &&
                page.Draft.SelectedIds.SequenceEqual(selected) && page.Draft.InspectedId == inspected &&
                page.CandidateScroll.content.anchoredPosition == scroll,
                "企业详情返回：原事件未恢复候选页或改变了请求／草稿／滚动状态");
            record?.Invoke(new { Phase = "返回后已核", Frame = Time.frameCount, expected.RequestId,
                DetailsVisible = details.activeSelf, CandidatePageVisible = page.Body.gameObject.activeInHierarchy,
                Selected = page.Draft.SelectedIds.ToArray(), Inspected = page.Draft.InspectedId, SubmissionInvoked = false });
        }
    }
}
#endif
