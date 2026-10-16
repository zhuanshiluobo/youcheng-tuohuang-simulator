using System;
using System.Collections;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.Presentation;
using YC.Presentation.Workflows;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 只从当前磁盘场景进入；不生成、重建或保存 UI 资产。每段失败保留真实入口现场。
    [Timeout(300000)]
    public sealed class GameplayInformationNavigationUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private string stage;
        private bool completed;
        private int Viewer => (int)Read(f.Gameplay, "localPlayerId");
        private object Modules => Read(f.Hud, "MainModules");
        private object Presenter => Read(f.Gameplay, "turnActionPresenter");
        private Component City => (Component)Read(f.Gameplay, "buildInfoPanel");
        private GameStateView Visible => GameStateViewProjector.ProjectForPlayer(f.State, Viewer);

        [UnityTearDown]
        public IEnumerator RecordTerminalStage()
        {
            if (f?.Gameplay != null)
            {
                f.Record("入口验收终态", new { stage, completed });
                if (!completed) yield return f.Capture("入口失败现场_" + stage, (Component)f.Hud);
            }
        }

        [UnityTest]
        public IEnumerator RoomLaunchOrderAndInformationVisitsPreserveBuildAndPendingRequests()
        {
            f = new GameplayPageAcceptanceFixture(); completed = false;
            Stage("正式房间启动与身份顺序");
            yield return f.Start(4, _ => Settle());
            CheckScene("SampleScene");
            CheckLaunchOrder();
            yield return f.Capture("冻结顺序与当前实际HUD", (Component)f.Hud);

            Stage("头像查看他人城市并返回");
            yield return InspectCityAndReturn();

            Stage("主要行动建设选择");
            var tabs = Active("UiMainActionTabs").Single();
            yield return f.Click(((Button[])Read(tabs, "tabs"))[0].gameObject, "主要行动页签");
            yield return f.Click(ButtonObject(Read(f.Hud, "ActionPanelView"), "BuildButton"), "实际建设入口");
            var buildPage = (Component)Read(f.Gameplay, "facilityBuildPage");
            Assert.That(buildPage != null && buildPage.gameObject.activeInHierarchy, Is.True, stage);
            var ids = ((IEnumerable)Read(buildPage, "SupplyItemIds")).Cast<string>().Where(id => !string.IsNullOrEmpty(id)).ToArray();
            Assert.That(ids, Is.Not.Empty);
            var candidate = ids.Select(id => HotseatUiInputDriver.Marker("build.candidate." + id))
                .FirstOrDefault(button => button != null && button.GetComponent<Button>()?.interactable == true);
            Assert.That(candidate, Is.Not.Null, "实际建设页没有可选候选入口");
            var selectedItemId = (string)Read(candidate.GetComponent(HotseatUiInputDriver.RuntimeType("YC.PlayerJourney.PlayerAutomationId")), "Id");
            selectedItemId = selectedItemId.Substring("build.candidate.".Length);
            var selectedDefinition = f.State.ItemZones.Items.Single(item => item.ItemId == selectedItemId).DefinitionId;
            yield return f.Click(candidate, "选择真实设施候选");
            var selection = (BuildFacilitySelectionController)Read(Read(Presenter, "BuildInteraction"), "selection");
            Assert.That(selection.IsActive, Is.True);
            Assert.That((string)Read(f.Gameplay, "selectedBuildFacilityId"), Is.EqualTo(selectedDefinition), "实际选中的报价卡必须保留");
            f.Record("真实建设报价选择", new { selectedItemId, selectedDefinition, selection.FacilityId,
                note = "资源不足仍能选择报价卡；可支付草稿FacilityId允许为空，返回时分别核对两者不变" });
            var draft = DraftSnapshot(selection);
            var state = StateFingerprint();
            yield return f.Capture("资料访问前原建设草稿", buildPage);

            Stage("企业资料关闭恢复建设草稿");
            using (var submits = new RuleSubmitCounter())
            {
                var expansion = Read(f.Hud, "ExpansionUi");
                var enterpriseEntry = ButtonObject(expansion, "cooperationOpenButton");
                yield return f.Click(enterpriseEntry, "建设中查看右侧企业资料", VisibleButtonPoint(enterpriseEntry, "企业资料可见边缘"));
                var enterprise = Active("EnterpriseSelectionDialogView").Single();
                var projection = ((EnterpriseSelectionDraft)Read(enterprise, "Draft")).Projection;
                Assert.That(projection.ReadOnly, Is.True);
                var target = projection.Targets.First(t => projection.Boards.Any(b => b.Id == t.BoardId && b.ActiveDepartment != null));
                yield return f.Click(Target(enterprise, target.Id), "企业右键进入科室资料", button: PointerEventData.InputButton.Right);
                var department = Active("EnterpriseSelectionDialogView").Single();
                var departmentProjection = ((EnterpriseSelectionDraft)Read(department, "Draft")).Projection;
                Assert.That(departmentProjection.Mode, Is.EqualTo(EnterpriseSelectionMode.BrowseDepartment));
                Assert.That(departmentProjection.ReadOnly, Is.True);
                Assert.That(departmentProjection.SubmissionAvailable, Is.False);
                yield return f.Capture("建设中企业科室只读资料", department);
                yield return f.Click(ButtonObject(department, "browseReturnButton"), "科室资料正常返回企业列表");
                Assert.That(Active("EnterpriseSelectionDialogView").Single(), Is.SameAs(enterprise));
                yield return f.Click(ButtonObject(enterprise, "closeButton"), "正常关闭企业资料");
                Assert.That(Read(f.Gameplay, "facilityBuildPage"), Is.SameAs(buildPage));
                Assert.That(buildPage.gameObject.activeInHierarchy, Is.True, "正常关闭资料必须恢复同一建设页");
                Assert.That(Read(Read(Presenter, "BuildInteraction"), "selection"), Is.SameAs(selection));
                Assert.That(DraftSnapshot(selection), Is.EqualTo(draft));
                Assert.That(((IEnumerable)Read(buildPage, "SupplyItemIds")).Cast<string>().Where(id => !string.IsNullOrEmpty(id)), Is.EqualTo(ids));
                Assert.That(StateFingerprint(), Is.EqualTo(state));
                Assert.That(submits.Calls, Is.Zero);
                yield return f.Capture("企业正常关闭恢复同一建设选择", buildPage);
            }
            // 只取消尚未提交的本地草稿，下一段不以清 Busy 或伪造请求绕过原入口。
            yield return f.Click(ButtonObject(buildPage, "cancel"), "正常取消未提交建设草稿");
            Assert.That(selection.IsActive, Is.False);

            Stage("真实未结算请求中查看城市样式");
            PrepareEnterpriseRequest();
            yield return Wait(() => f.RequestPage() != null, "正式企业请求页没有就绪");
            yield return Settle();
            var request = f.Request;
            var requestPage = f.RequestPage();
            var requestId = request.GetStableInteractionId();
            var candidates = request.CandidateIds.ToArray();
            var revision = request.StateRevision;
            state = StateFingerprint();
            using (var submits = new RuleSubmitCounter())
            {
                yield return f.Click(ButtonObject(f.Frame, "FoldButton"), "实际收起未回答企业请求");
                Assert.That(requestPage.gameObject.activeInHierarchy, Is.False);
                Assert.That(f.Request.GetStableInteractionId(), Is.EqualTo(requestId));
                f.Record("未回答请求经正式顶栏收起", new { requestId, revision,
                    note = "保留正式模态边界；只收起显示，不回答、不取消请求" });
                var styleTab = ((Button[])Read(tabs, "tabs"))[2].gameObject;
                yield return f.Click(styleTab, "未结算时城市样式页签", VisibleButtonPoint(styleTab, "未结算样式页签可见边缘"));
                var styleEntry = ButtonObject(Read(f.Hud, "ActionPanelView"), "DeclareCityStyleButton");
                yield return f.Click(styleEntry, "未结算时实际城市样式入口", VisibleButtonPoint(styleEntry, "未结算样式入口可见边缘"));
                var stylePage = Active("CityStyleDeclarationPreviewView").Single();
                var dialog = Read(Read(f.Gameplay, "workflowView"), "cityStyleDeclarationDialog");
                var model = (CityStyleOptionsViewModel)Read(dialog, "model");
                Assert.That(model.DeclareMode, Is.False);
                Assert.That(model.ConfirmSelection, Is.Null);
                Assert.That(model.TryUseSpecialAction, Is.Null);
                yield return f.Capture("真实未结算请求中的只读城市样式", stylePage);
                yield return f.Click(ButtonObject(stylePage, "CloseButton"), "正常关闭只读城市样式");
                bool remainedFolded = !requestPage.gameObject.activeInHierarchy;
                f.Record("资料关闭保留请求显示状态", new { requestId, remainedFolded });
                if (remainedFolded)
                    yield return f.Click(ButtonObject(f.Frame, "FoldButton"), "实际展开原未回答企业请求");
                Assert.That(f.Request.GetStableInteractionId(), Is.EqualTo(requestId));
                Assert.That(f.Request.StateRevision, Is.EqualTo(revision));
                Assert.That(f.Request.CandidateIds, Is.EqualTo(candidates));
                Assert.That(f.RequestPage(), Is.SameAs(requestPage));
                Assert.That(requestPage.gameObject.activeInHierarchy, Is.True);
                Assert.That(StateFingerprint(), Is.EqualTo(state));
                Assert.That(submits.Calls, Is.Zero);
                yield return f.Capture("城市样式关闭恢复原未结算请求", requestPage);
            }
            completed = true;
        }

        [UnityTest]
        public IEnumerator GameBoxEventsUseIndependentLandscapeBrowsingAndCityReturnHint()
        {
            f = new GameplayPageAcceptanceFixture(); completed = false;
            Stage("三人实际场景与窄窗口城市提示");
            yield return f.Start(3, _ => Settle());
            yield return f.Resolution(new Vector2Int(1366, 768));
            CheckScene("ThreePlayerScene");
            CheckLaunchOrder();
            yield return InspectCityAndReturn();

            Stage("实际事件牌堆");
            var expected = Visible.GameBox.Cards.Where(c => c.Kind == GameBoxCardKind.Event).ToArray();
            Assert.That(expected, Is.Not.Empty, "正式开局须已有公开事件；无事件时保留现场，不补造盒内DTO");
            var information = ((Component)f.Hud).GetComponent(T("GameplayInformationPages"));
            var state = StateFingerprint();
            using (var submits = new RuleSubmitCounter())
            {
                yield return f.Click(ButtonObject(information, "GameBoxButton"), "实际游戏盒入口");
                var box = (Component)Read(information, "GameBoxPage");
                var cards = ((IEnumerable)Call(box, "Cards", GameBoxCardKind.Event)).Cast<GameBoxCardState>().ToArray();
                Assert.That(cards.Select(c => c.InstanceId), Is.EqualTo(expected.Select(c => c.InstanceId)));
                var pile = Read(box, "EventPile");
                var backend = (Component)Read(pile, "eventPile");
                Assert.That(backend, Is.Not.Null);
                Assert.That(Read(pile, "pile"), Is.Null, "事件不得沿角色牌堆后端");
                var pileItems = backend.GetComponentsInChildren(T("GameBoxEventItemView"), false).Cast<Component>().ToArray();
                Assert.That(pileItems.Length, Is.EqualTo(Math.Min(5, cards.Length)));
                foreach (var item in pileItems) CheckEventArtwork(item);
                yield return f.Capture("实际横向事件牌堆", box);

                Stage("独立事件列表与单面详情");
                yield return f.Click(ButtonObject(pile, "InspectButton"), "实际事件牌堆查看入口");
                var list = Active("GameBoxEventListView").Single();
                Assert.That(list.GetComponentsInChildren(T("CardPickerRowLayout"), true), Is.Empty);
                Assert.That(list.GetComponentsInChildren(T("FacilityEffectCardView"), true), Is.Empty);
                var items = list.GetComponentsInChildren(T("GameBoxEventItemView"), false).Cast<Component>().ToArray();
                Assert.That(items.Length, Is.EqualTo(cards.Length));
                for (int i = 0; i < items.Length; i++)
                {
                    Assert.That(HotseatUiInputDriver.Marker("gamebox.event." + cards[i].InstanceId).transform.IsChildOf(list.transform), Is.True);
                    Assert.That(Read(items[i], "Sprite"), Is.SameAs(Call(Read(f.Registry, "CardVisualCatalog"), "GetEvent", cards[i].CardId)));
                    CheckEventArtwork(items[i]);
                }
                yield return f.Capture("独立事件横向列表", list);
                var scroll = (ScrollRect)Read(list, "scroll");
                var last = items.Last();
                yield return ScrollTo(last.gameObject, scroll);
                float position = (float)Read(list, "ScrollPosition");
                yield return f.Click(ButtonObject(last, "inspectButton"), "事件原右键单面详情", button: PointerEventData.InputButton.Right);
                var viewer = Active("CardViewer").Single();
                Assert.That(Read(viewer, "Mode").ToString(), Is.EqualTo("Inspect"));
                Assert.That(Read(viewer, "DisplayedSprite"), Is.SameAs(Read(last, "Sprite")));
                Assert.That(((RawImage)Read(viewer, "GameBoxBackImage")).gameObject.activeInHierarchy, Is.False);
                CheckImage((RawImage)Read(viewer, "CardImage"), (Sprite)Read(last, "Sprite"));
                yield return f.Capture("事件正确切片单面详情", viewer);
                yield return f.Click(viewer.gameObject, "事件详情实际返回", ScreenRect((RectTransform)Read(viewer, "Window")).center);
                Assert.That(Active("GameBoxEventListView").Single(), Is.SameAs(list));
                Assert.That((float)Read(list, "ScrollPosition"), Is.EqualTo(position).Within(.001f));
                f.Record("事件详情实际返回保持列表", new { before = position, after = (float)Read(list, "ScrollPosition"),
                    contentWidth = scroll.content.rect.width, viewportWidth = scroll.viewport.rect.width });
                yield return f.Capture("事件详情返回同一列表", list);

                Stage("事件列表与游戏盒正常返回");
                yield return PressSurface((Component)Read(list, "dismiss"), "事件列表空白返回游戏盒");
                Assert.That(box.gameObject.activeInHierarchy, Is.True);
                Assert.That((bool)Read(list, "IsShowing"), Is.False);
                yield return PressSurface((Component)Read(box, "dismiss"), "游戏盒空白正常关闭");
                Assert.That(box.gameObject.activeInHierarchy, Is.False);
                Assert.That(StateFingerprint(), Is.EqualTo(state));
                Assert.That(submits.Calls, Is.Zero, "事件查看和返回不得穿透为规则提交");
            }
            yield return f.Capture("窄窗口资料往返终态", (Component)f.Hud);
            completed = true;
        }

        private void Stage(string name)
        {
            stage = name;
            f.Record("待验入口阶段", new { stage });
        }

        private void CheckScene(string expected)
        {
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo(expected));
            Assert.That(((Component)f.Hud).gameObject.scene.name, Is.EqualTo(expected));
            f.Record("当前场景实际来源", new { scene = SceneManager.GetActiveScene().path,
                hud = HotseatUiInputDriver.PathOf(((Component)f.Hud).gameObject),
                scope = "当前磁盘场景实例及其已有覆盖；不生成、不重新实例化源HUD、不保存覆盖" });
        }

        private void CheckLaunchOrder()
        {
            var context = T("GameLaunchContext").GetProperty("Instance").GetValue(null);
            var mode = (LaunchMode)Read(context, "Mode");
            var provider = HotseatUiInputDriver.RuntimeType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider");
            var service = provider.GetMethod("GetActive").Invoke(null, null);
            Assert.That(service, Is.Not.Null);
            var room = (RoomState)Call(service, "GetCurrentRoom");
            Assert.That(room, Is.Not.Null, "正式房间权威状态必须仍可读取");
            Assert.That(room.HasStarted, Is.True);
            Assert.That(room.LocalPlayerId, Is.EqualTo(room.HostPlayerId));
            Assert.That(mode, Is.Not.EqualTo(LaunchMode.Client), "本轮必须走有正式配方装配的房主路径");
            var order = ((IEnumerable)Read(context, "LaunchPlayerOrder")).Cast<int>().ToArray();
            Assert.That(room.LaunchPlayerOrder, Is.EqualTo(order));
            Assert.That(order, Is.EquivalentTo(f.State.Players.Select(p => p.PlayerId)));
            Assert.That(((IEnumerable)Read(f.State, "LaunchPlayerOrder")).Cast<int>(), Is.EqualTo(order));
            Assert.That(((IEnumerable)Read(Visible, "LaunchPlayerOrder")).Cast<int>(), Is.EqualTo(order));
            Assert.That(f.State.StartPlayerId, Is.EqualTo(order[0]));
            var seats = ((IEnumerable)Read(context, "Players")).Cast<PlayerSeat>().ToArray();
            foreach (var seat in seats)
            {
                var player = f.State.FindPlayer(seat.PlayerId);
                Assert.That(player.Name, Is.EqualTo(seat.PlayerName));
                Assert.That(player.Color, Is.EqualTo(seat.Color));
            }
            var opponents = order.Where(id => id != Viewer).ToArray();
            Assert.That(((int[])Read(Modules, "opponentIds")).Where(id => id >= 0), Is.EqualTo(opponents));
            var names = (Text[])Read(Modules, "opponentName");
            var colors = (Image[])Read(Modules, "opponentColor");
            for (int i = 0; i < opponents.Length; i++)
            {
                var player = f.State.FindPlayer(opponents[i]);
                Assert.That(names[i].text, Is.EqualTo(player.Name));
                var color = (Color)T("UiTheme").GetMethod("GetPlayerColor").Invoke(null, new object[] { player.Color, 1f });
                Assert.That(colors[i].color, Is.EqualTo(color));
            }
            f.Record("冻结环序身份同步", new { order, viewer = Viewer, opponents, mode = mode.ToString(),
                room.HostPlayerId, room.LocalPlayerId, room.HasStarted, f.State.ContentPackHash,
                scope = "实际房间权威开始→非Client正式配方→当前场景；具体模式如实记录，不代替Steam双端验收",
                players = f.State.Players.Select(p => new { p.PlayerId, p.Name, color = p.Color.ToString() }).ToArray() });
        }

        private IEnumerator InspectCityAndReturn()
        {
            int viewer = Viewer;
            var session = f.Session;
            var state = StateFingerprint();
            var privateSet = JsonConvert.SerializeObject(Visible.GameBox);
            var portraits = (Button[])Read(Modules, "opponentPortraits");
            var ids = (int[])Read(Modules, "opponentIds");
            int index = Array.FindIndex(ids, id => id >= 0 && id != viewer);
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            using (var submits = new RuleSubmitCounter())
            {
                yield return f.Click(portraits[index].gameObject, "实际对手头像");
                Assert.That((int)Read(City, "displayedCityPlayerId"), Is.EqualTo(ids[index]));
                Assert.That((bool)Read(City, "IsOwnCityDisplayed"), Is.False);
                Assert.That(Viewer, Is.EqualTo(viewer));
                CheckPublicCityBinding(ids[index]);
                var hint = (GameObject)Read(Modules, "viewedCityReturnHint");
                Assert.That(hint.activeInHierarchy, Is.True);
                var text = hint.GetComponent<Text>();
                Assert.That(text, Is.Not.Null);
                Assert.That(text.preferredWidth, Is.LessThanOrEqualTo(text.rectTransform.rect.width + 1), "当前城市返回提示被横向截断");
                Assert.That(text.preferredHeight, Is.LessThanOrEqualTo(text.rectTransform.rect.height + 1), "当前城市返回提示被纵向截断");
                yield return f.Capture("实际对手城市与返回提示", (Component)f.Hud);
                yield return PressSurface((Component)Read(Modules, "cityInspectionInput"), "实际城市区域点击返回本人");
                Assert.That((bool)Read(City, "IsOwnCityDisplayed"), Is.True);
                Assert.That(hint.activeInHierarchy, Is.False);
                Assert.That(Viewer, Is.EqualTo(viewer));
                Assert.That(f.Session, Is.SameAs(session));
                Assert.That(JsonConvert.SerializeObject(Visible.GameBox), Is.EqualTo(privateSet));
                Assert.That(StateFingerprint(), Is.EqualTo(state));
                Assert.That(submits.Calls, Is.Zero, "城市观察和返回不得穿透为本人操作");
                yield return f.Capture("城市点击返回本人保持身份", (Component)f.Hud);
            }
        }

        private void CheckPublicCityBinding(int playerId)
        {
            var visible = Visible;
            var bindings = ((IEnumerable)Read(City, "cityBoardSlotBindings")).Cast<object>().ToArray();
            Assert.That(bindings, Is.Not.Empty);
            foreach (var binding in bindings)
            {
                var slot = (int)Read(binding, "SlotIndex");
                var expected = visible.Map.Facilities.SingleOrDefault(p => p.PlayerId == playerId && p.CityBoardSlotIndex == slot);
                Assert.That((int)Read(binding, "PlayerId"), Is.EqualTo(playerId));
                Assert.That((string)Read(binding, "ContentInstanceId"), Is.EqualTo(expected?.ContentInstanceId ?? string.Empty));
                Assert.That((string)Read(binding, "FacilityId"), Is.EqualTo(expected?.FacilityCardId ?? string.Empty));
                Assert.That(((Behaviour)Read(Read(binding, "Slot"), "DropTarget")).enabled, Is.False);
                var item = (Component)Read(binding, "Item");
                Assert.That(item != null && item.gameObject.activeInHierarchy, Is.EqualTo(expected != null));
            }
            f.Record("他人城市公开设施绑定", new { playerId,
                facilities = visible.Map.Facilities.Where(p => p.PlayerId == playerId)
                    .Select(p => new { p.ContentInstanceId, p.FacilityCardId, p.CityBoardSlotIndex }).ToArray(),
                usedSlots = visible.Players.Single(p => p.PlayerId == playerId).CityStyleUsedSlotIndexes.ToArray(),
                scope = "核对实际公开设施绑定与禁用落点；当前开局没有宣告时不冒称已用槽位显示通过" });
        }

        private void PrepareEnterpriseRequest()
        {
            Assert.That(f.Request, Is.Null);
            var registry = (EffectRegistry)Read(((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>()
                .OfType<AnswerInteractionCommandHandler>().Single(), "registry");
            var ids = new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetEnterpriseItemIds(f.State).ToArray();
            var args = ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(1),
                "player", NormalizedValue.CreateString(Viewer.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "enterpriseItemIds", ItemTransferEffectExecutor.Strings(ids), "levels", NormalizedValue.CreateInteger(1));
            var engine = new EffectTreeExecutor(f.State, registry);
            var root = engine.CreateRoot(EffectSpec.Create(LuaDomainEffectTypeIds.UpgradeEnterprise, args, Viewer));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            Assert.That(f.Request, Is.Not.Null);
            f.Record("真实请求受控前置边界", new { root, f.Request.InteractionTypeId,
                note = "只建立正式未结算请求，资料入口及关闭全走实际鼠标；不声称支付建设到该请求的整链" });
        }

        private string DraftSnapshot(BuildFacilitySelectionController selection) => JsonConvert.SerializeObject(new {
            selection.Phase, selection.PlayerId, selection.FacilityId, selection.CityBoardSlotIndex,
            selection.PaymentMode, selection.IsSupplySource, selection.IsActive,
            selected = Read(f.Gameplay, "selectedBuildFacilityId") });

        private string StateFingerprint()
        {
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(f.State))));
        }

        private void CheckEventArtwork(Component item) => CheckImage((RawImage)Read(item, "Artwork"), (Sprite)Read(item, "Sprite"));

        private void CheckImage(RawImage image, Sprite sprite)
        {
            Assert.That(sprite != null && sprite.rect.width > 0 && sprite.rect.height > 0, Is.True);
            Assert.That(image.texture, Is.SameAs(sprite.texture));
            Assert.That(image.rectTransform.rect.width / image.rectTransform.rect.height,
                Is.EqualTo(sprite.rect.width / sprite.rect.height).Within(.015f), "实际图面比例必须跟随单张Sprite，不跟随整张atlas或竖牌模板");
            var uv = image.uvRect; var crop = sprite.textureRect;
            Assert.That(uv.x * sprite.texture.width, Is.EqualTo(crop.x).Within(1.1f));
            Assert.That(uv.y * sprite.texture.height, Is.EqualTo(crop.y).Within(1.1f));
            Assert.That(uv.width * sprite.texture.width, Is.EqualTo(crop.width).Within(1.1f));
            Assert.That(uv.height * sprite.texture.height, Is.EqualTo(crop.height).Within(1.1f));
            f.Record("正式事件图面与切片", new { sprite = sprite.name,
                source = new[] { crop.x, crop.y, crop.width, crop.height }, displayed = new[] { image.rectTransform.rect.width, image.rectTransform.rect.height },
                uv = new[] { uv.x, uv.y, uv.width, uv.height } });
        }

        private IEnumerator ScrollTo(GameObject target, ScrollRect scroll)
        {
            for (int step = 0; step < 48 && !HotseatUiInputDriver.TryPoint(target, out _, out _); step++)
            {
                var center = ScreenRect(scroll.viewport).center; var hit = Hit(center);
                var handler = ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject);
                Assert.That(handler, Is.SameAs(scroll.gameObject));
                var bounds = ScreenRect((RectTransform)target.transform);
                ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current) { position = center,
                    scrollDelta = new Vector2(bounds.center.x > center.x ? -1 : 1, 0), pointerCurrentRaycast = hit }, ExecuteEvents.scrollHandler);
                yield return null; yield return null;
            }
            Assert.That(HotseatUiInputDriver.TryPoint(target, out _, out _), Is.True);
        }

        private Vector2 VisibleButtonPoint(GameObject target, string label)
        {
            f.Record("实际入口可见性", new { label, target = HotseatUiInputDriver.PathOf(target),
                active = target != null && target.activeInHierarchy,
                interactable = target != null && target.GetComponent<Button>()?.IsInteractable() == true });
            Assert.That(target != null && target.activeInHierarchy, Is.True, label);
            Assert.That(target.GetComponent<Button>().IsInteractable(), Is.True, label);
            var rect = ScreenRect((RectTransform)target.transform);
            var expected = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            var attempts = new System.Collections.Generic.List<object>();
            foreach (float y in new[] { .5f, .99f, .01f, .95f, .05f, .8f, .2f })
            foreach (float x in new[] { .5f, .99f, .01f, .95f, .05f, .8f, .2f })
            {
                var point = new Vector2(Mathf.Lerp(rect.xMin, rect.xMax, x), Mathf.Lerp(rect.yMin, rect.yMax, y));
                if (point.x < 0 || point.y < 0 || point.x >= Screen.width || point.y >= Screen.height) continue;
                var hits = new System.Collections.Generic.List<RaycastResult>();
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
                var hit = hits.Count == 0 ? null : hits[0].gameObject;
                var actual = hit == null ? null : ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
                attempts.Add(new { point = new[] { point.x, point.y }, hit = HotseatUiInputDriver.PathOf(hit),
                    handler = HotseatUiInputDriver.PathOf(actual) });
                if (actual != expected) continue;
                f.Record("真实入口可见边缘射线", new { label, target = HotseatUiInputDriver.PathOf(target), attempts });
                return point;
            }
            f.Record("真实入口边缘未找到", new { label, target = HotseatUiInputDriver.PathOf(target), attempts });
            Assert.Fail(label + "没有顶层射线命中的可见点击点");
            return default;
        }

        private IEnumerator PressSurface(Component surface, string label)
        {
            Assert.That(surface != null && surface.gameObject.activeInHierarchy, Is.True, label);
            var rect = ScreenRect((RectTransform)surface.transform);
            Vector2 point = default; RaycastResult ray = default; bool found = false;
            foreach (float y in new[] { .5f, .1f, .9f, .025f, .975f })
            {
                foreach (float x in new[] { .5f, .1f, .9f, .025f, .975f })
                {
                    var candidate = new Vector2(Mathf.Lerp(rect.xMin, rect.xMax, x), Mathf.Lerp(rect.yMin, rect.yMax, y));
                    var hits = new System.Collections.Generic.List<RaycastResult>();
                    EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = candidate }, hits);
                    if (hits.Count == 0 || ExecuteEvents.GetEventHandler<IPointerDownHandler>(hits[0].gameObject) != surface.gameObject) continue;
                    point = candidate; ray = hits[0]; found = true; break;
                }
                if (found) break;
            }
            Assert.That(found, Is.True, label + "实际完整点击表面不可达");
            f.Record("实际按下抬起命中", new { label, point = new[] { point.x, point.y }, hit = HotseatUiInputDriver.PathOf(ray.gameObject) });
            var data = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left,
                position = point, pressPosition = point, pointerCurrentRaycast = ray, pointerPressRaycast = ray, eligibleForClick = true };
            ExecuteEvents.Execute(surface.gameObject, data, ExecuteEvents.pointerDownHandler); yield return null;
            data.pointerCurrentRaycast = Hit(point);
            Assert.That(ExecuteEvents.GetEventHandler<IPointerDownHandler>(data.pointerCurrentRaycast.gameObject), Is.SameAs(surface.gameObject));
            ExecuteEvents.Execute(surface.gameObject, data, ExecuteEvents.pointerUpHandler);
            // 同一输入只向原表面释放，不重新射线分发到已显露的底层按钮。
            yield return Settle();
        }
    }
}
