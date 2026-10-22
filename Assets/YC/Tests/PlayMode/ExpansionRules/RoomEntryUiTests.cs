using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;
using YC.Domain.CardFlows;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode.ExpansionRules
{
    public sealed class RoomEntryUiTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private Component controller, view, panel;
        private RoomSessionService injectedService;
        private static object Field(object owner, string name) => owner.GetType().GetField(name, Private).GetValue(owner);
        private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name).GetValue(owner);
        private static void Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Private).Invoke(owner, args);

        [UnitySetUp] public IEnumerator LoadMainMenu()
        {
            yield return SceneManager.LoadSceneAsync("StartScene");
            yield return null;
            var type = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", true);
            controller = (Component)UnityEngine.Object.FindObjectOfType(type);
            Assert.That(controller, Is.Not.Null);
            view = (Component)Field(controller, "view");
            panel = Property<Component>(view, "RoomPanel");
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(1920, 1080) });
            var deadline = Time.realtimeSinceStartup + 10;
            while ((Screen.width != 1920 || Screen.height != 1080) && Time.realtimeSinceStartup < deadline) yield return null;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            injectedService?.Dispose(); injectedService = null;
            if (controller != null) Call(controller, "HideRoomPanel");
            yield return null;
        }

        [UnityTest] public IEnumerator MainMenuButtonsDirectlyOpenJoinAndCreateLocalRoom()
        {
            var create = Property<Button>(view, "StartGameButton");
            var join = Property<Button>(view, "OnlineModeButton");
            AssertHit(create); AssertHit(join);
            var removedType = Type.GetType("YC.Presentation.StartMenuOnlineModePanelView, Assembly-CSharp", true);
            Assert.That(view.GetComponentsInChildren(removedType, true), Is.Empty, "旧联机选择页应从实际资产移除");
            yield return Capture("主界面-创建与加入房间");
            join.onClick.Invoke(); yield return null;
            var joinPanel = Property<Component>(view, "JoinPanel");
            Assert.That(joinPanel.gameObject.activeInHierarchy, Is.True);
            Assert.That(Property<InputField>(joinPanel, "RoomCodeInput").gameObject.activeInHierarchy, Is.True);
            Assert.That(panel.gameObject.activeInHierarchy, Is.False);
            yield return Capture("加入房间-直接房号输入");
            Property<Button>(joinPanel, "BackButton").onClick.Invoke();
            create.onClick.Invoke(); yield return Idle();
            Assert.That(panel.gameObject.activeInHierarchy, Is.True);
            Assert.That(Property<Component>(view, "MapSelectionPanel").gameObject.activeInHierarchy, Is.False);
            var service = (IOnlineRoomService)Field(controller, "roomService");
            Assert.That(service, Is.InstanceOf<RoomSessionService>());
            Assert.That(((IRoomNetworkingControl)service).IsOnline, Is.False);
            Assert.That(service.GetCurrentRoom().PlayerCount, Is.EqualTo(1));
            var steam = Type.GetType("YC.Infrastructure.Multiplayer.SteamBootstrap, Assembly-CSharp", true);
            Assert.That((bool)steam.GetProperty("IsInitialized").GetValue(null), Is.False, "创建本地房间不初始化Steam");
            Assert.That(Property<Button>(panel, "NetworkingButton").interactable, Is.True);
            Assert.That(Property<Button>(panel, "CopyButton").interactable, Is.False);
            AssertHit(Property<Button>(panel, "NetworkingButton"));
            yield return Capture("创建房间-本地房间与打开联机");
        }

        [UnityTest] public IEnumerator NetworkingToggleWaitsForReplyThenClosesRemoteMembersAndRetainsConfiguration()
        {
            var provider = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            var content = (IRoomContentCapabilityProvider)provider.GetProperty("ContentCapabilities").GetValue(null);
            TestNetwork remote = null;
            injectedService = new RoomSessionService(content, () => remote = new TestNetwork(content));
            controller.GetType().GetField("roomService", Private).SetValue(controller, injectedService);
            injectedService.RoomUpdated += room => Call(controller, "QueueRoomUpdate", room);
            Property<Button>(view, "StartGameButton").onClick.Invoke(); yield return Idle();
            var before = injectedService.GetCurrentRoom();
            var request = new ChangeRoomConfigurationRequest
            {
                CommandId = Guid.NewGuid().ToString("N"), RoomId = before.RoomId, RequestingPlayerId = before.LocalPlayerId,
                ExpectedConfigurationRevision = before.Configuration.ConfigurationRevision,
                MapId = "map-three-players", RuleModeId = "standard", EnabledExpansionIds = before.Configuration.EnabledExpansionIds
            };
            Assert.That(injectedService.ChangeConfigurationAsync(request).Result.Success, Is.True);
            yield return null;
            var toggle = Property<Button>(panel, "NetworkingButton");
            toggle.onClick.Invoke(); yield return null;
            Assert.That(toggle.interactable, Is.False);
            Assert.That(injectedService.IsOnline, Is.False);
            Assert.That(Property<RoomState>(panel, "DisplayedRoom").RoomId, Is.EqualTo(before.RoomId));
            remote.Complete(); yield return Idle();
            Assert.That(injectedService.IsOnline, Is.True);
            Assert.That(((Text)Field(panel, "networkingCaption")).text, Is.EqualTo((string)Field(panel, "disableNetworkingCaption")));
            remote.AddGuest(); yield return null;
            Assert.That(Property<RoomState>(panel, "DisplayedRoom").PlayerCount, Is.EqualTo(2));
            yield return Capture("联机开关-测试运输已开启");
            toggle.onClick.Invoke(); yield return Idle();
            Assert.That(remote.Disposed, Is.True);
            Assert.That(injectedService.IsOnline, Is.False);
            Assert.That(injectedService.GetCurrentRoom().PlayerCount, Is.EqualTo(1));
            Assert.That(injectedService.GetCurrentRoom().Configuration.MapId, Is.EqualTo("map-three-players"));
            Assert.That(panel.gameObject.activeInHierarchy, Is.True);
            Assert.That(((Text)Field(panel, "networkingCaption")).text, Is.EqualTo((string)Field(panel, "enableNetworkingCaption")));
            yield return Capture("关闭联机-保留房主与配置");
            var offlineId = injectedService.GetCurrentRoom().RoomId;
            toggle.onClick.Invoke(); yield return null;
            remote.Fail(); yield return Idle();
            Assert.That(injectedService.IsOnline, Is.False);
            Assert.That(injectedService.GetCurrentRoom().RoomId, Is.EqualTo(offlineId));
            Assert.That(Property<Text>(panel, "StatusText").text, Does.Contain("联机连接失败"));
            Assert.That(toggle.interactable, Is.True);
            yield return Capture("联机开启失败-仍在本地房间");
        }

        private Button SeatButton(int index, string name)
        {
            var seat = ((Array)Field(panel, "seats")).GetValue(index);
            return (Button)seat.GetType().GetField(name).GetValue(seat);
        }

        [UnityTest] public IEnumerator LocalRoomResizesWithoutHorizontalOverflowAndPreservesSeatCommands()
        {
            var evidence = new GameplayPageAcceptanceFixture();
            yield return evidence.Click(Property<Button>(view, "StartGameButton").gameObject, "自适应房间_创建本地房间");
            yield return Idle();
            var body = panel.GetComponentsInChildren(GameplayPageAcceptanceFixture.T("RoomBodyResponsiveLayout"), true)
                .Cast<Component>().Single();
            var members = (RectTransform)GameplayPageAcceptanceFixture.Read(body, "memberPanel");
            var settings = (RectTransform)GameplayPageAcceptanceFixture.Read(body, "settingsPanel");
            var scroll = body.GetComponentInParent<ScrollRect>();
            Assert.That(scroll, Is.Not.Null, "检查实际房间主体滚动范围");
            var settingsSize = settings.rect.size;
            var settingsScale = settings.localScale;
            var managementNames = new[] { "InviteButton", "CloseButton", "HotSeatButton", "BotButton" };
            var allNames = managementNames.Concat(new[] { "SwapButton", "RemoveButton", "OpenButton" }).ToArray();
            float narrowWidth = 0;

            IEnumerator Observe(string label, bool wide)
            {
                yield return GameplayPageAcceptanceFixture.Settle();
                Assert.That(scroll.horizontal, Is.False);
                Assert.That(scroll.horizontalScrollbar == null || !scroll.horizontalScrollbar.gameObject.activeInHierarchy, Is.True);
                Assert.That(scroll.content.rect.width, Is.EqualTo(scroll.viewport.rect.width).Within(1f), "内容宽度必须跟随视口，不能只隐藏横条");
                Assert.That(Vector2.Distance(settings.rect.size, settingsSize), Is.LessThan(.1f), "设置面板保持资产逻辑尺寸");
                Assert.That(settings.localScale, Is.EqualTo(settingsScale));
                var viewport = GameplayPageAcceptanceFixture.ScreenRect(scroll.viewport);
                var memberBounds = GameplayPageAcceptanceFixture.ScreenRect(members);
                Assert.That(memberBounds.xMin, Is.GreaterThanOrEqualTo(viewport.xMin - 1f));
                Assert.That(memberBounds.xMax, Is.LessThanOrEqualTo(viewport.xMax + 1f));
                for (var seat = 0; seat < 4; seat++)
                {
                    var controls = allNames.Select(name => SeatButton(seat, name)).ToArray();
                    var scale = controls[0].transform.localScale;
                    foreach (var button in controls)
                    {
                        Assert.That(button.transform.localScale.x, Is.EqualTo(scale.x).Within(.001f), "七类座位按钮必须同源缩放");
                        Assert.That(button.transform.localScale.y, Is.EqualTo(scale.y).Within(.001f));
                        Assert.That(scale.x, Is.EqualTo(scale.y).Within(.001f));
                    }
                    if (wide)
                    {
                        Assert.That(scale.x, Is.GreaterThan(1f), "足够宽时应放大整组按钮");
                        var row = managementNames.Select(name => GameplayPageAcceptanceFixture.ScreenRect((RectTransform)SeatButton(seat, name).transform)).ToArray();
                        Assert.That(row.Max(r => r.center.y) - row.Min(r => r.center.y), Is.LessThan(1f), "四个管理按钮应同一行");
                        for (var i = 1; i < row.Length; i++) Assert.That(row[i].xMin, Is.GreaterThanOrEqualTo(row[i - 1].xMax));
                    }
                }
                var settledSize = scroll.content.rect.size;
                yield return GameplayPageAcceptanceFixture.Settle();
                Assert.That(Vector2.Distance(scroll.content.rect.size, settledSize), Is.LessThan(.1f), "人数或取消后应自然稳定，不靠测试强制布局");
                evidence.Record("房间自适应实际边界", new { label, member = GameplayPageAcceptanceFixture.Geometry(members),
                    settings = GameplayPageAcceptanceFixture.Geometry(settings), content = GameplayPageAcceptanceFixture.Geometry(scroll.content),
                    viewport = GameplayPageAcceptanceFixture.Geometry(scroll.viewport), players = Property<RoomState>(panel, "DisplayedRoom").PlayerCount });
            }

            foreach (var size in new[] { new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(3840, 1080), new Vector2Int(1280, 720) })
            {
                yield return evidence.Resolution(size);
                yield return Observe("窗口" + size, size.x == 3840);
                if (size.x == 1280 && narrowWidth == 0) narrowWidth = members.rect.width;
                if (size.x == 3840) Assert.That(members.rect.width, Is.GreaterThan(narrowWidth + 1f));
                if (size.x == 1280) Assert.That(members.rect.width, Is.EqualTo(narrowWidth).Within(.1f), "窄宽往返不能留下旧宽度");
            }
            yield return evidence.Resolution(new Vector2Int(1920, 1080));
            yield return evidence.Click(SeatButton(1, "CloseButton").gameObject, "自适应房间_关闭空座"); yield return Idle();
            Assert.That(Property<RoomState>(panel, "DisplayedRoom").SeatSlots[1].Status, Is.EqualTo("closed"));
            yield return evidence.Click(SeatButton(1, "OpenButton").gameObject, "自适应房间_重新开放"); yield return Idle();
            yield return evidence.Click(SeatButton(1, "HotSeatButton").gameObject, "自适应房间_添加热座"); yield return Idle();
            Assert.That(Property<RoomState>(panel, "DisplayedRoom").PlayerCount, Is.EqualTo(2));
            yield return evidence.Click(SeatButton(0, "SwapButton").gameObject, "自适应房间_打开换位");
            yield return evidence.Click(((Button)Field(panel, "swapCloseButton")).gameObject, "自适应房间_取消换位");
            yield return Observe("取消换位后", false);
            yield return evidence.Click(SeatButton(0, "SwapButton").gameObject, "自适应房间_再次换位");
            yield return evidence.Click(((Button[])Field(panel, "swapTargets"))[1].gameObject, "自适应房间_选择热座位置"); yield return Idle();
            Assert.That(Property<RoomState>(panel, "DisplayedRoom").SeatSlots[1].Occupant.PlayerId,
                Is.EqualTo(Property<RoomState>(panel, "DisplayedRoom").HostPlayerId));
            var close = (Button)Field(panel, "swapCloseButton");
            if (close.gameObject.activeInHierarchy) yield return evidence.Click(close.gameObject, "自适应房间_关闭换位页");
            yield return evidence.Click(SeatButton(0, "RemoveButton").gameObject, "自适应房间_移除热座"); yield return Idle();
            Assert.That(Property<RoomState>(panel, "DisplayedRoom").PlayerCount, Is.EqualTo(1));
            yield return Observe("人数变动后", false);
            yield return evidence.Capture("房间窄宽往返与真实命令终态", panel);
        }

        [UnityTest] public IEnumerator HotseatButtonsAddRemoveAndSwapActualLocalPlayers()
        {
            Property<Button>(view, "StartGameButton").onClick.Invoke(); yield return Idle();
            Assert.That(Property<Button>(panel, "StartButton").interactable, Is.False);
            var add = SeatButton(3, "HotSeatButton");
            Assert.That(add.interactable, Is.True); AssertHit(add);
            yield return Capture("热座-添加本地玩家");
            add.onClick.Invoke(); yield return Idle();
            var room = Property<RoomState>(panel, "DisplayedRoom");
            Assert.That(room.PlayerCount, Is.EqualTo(2));
            Assert.That(room.SeatSlots[3].Occupant, Is.Not.Null);
            Assert.That(room.SeatSlots[1].Occupant, Is.Null, "应加入点击的空座而非第一个空座");
            Assert.That(Property<Button>(panel, "StartButton").interactable, Is.True);
            Assert.That(Property<Button>(panel, "NetworkingButton").interactable, Is.False);
            var remove = SeatButton(3, "RemoveButton");
            Assert.That(remove.interactable, Is.True); AssertHit(remove);
            remove.onClick.Invoke(); yield return Idle();
            Assert.That(Property<RoomState>(panel, "DisplayedRoom").PlayerCount, Is.EqualTo(1));
            Assert.That(Property<Button>(panel, "StartButton").interactable, Is.False);
            Assert.That(Property<Button>(panel, "NetworkingButton").interactable, Is.True);
            for (var i = 1; i < 4; i++) { SeatButton(i, "HotSeatButton").onClick.Invoke(); yield return Idle(); }
            room = Property<RoomState>(panel, "DisplayedRoom");
            Assert.That(room.PlayerCount, Is.EqualTo(4));
            Assert.That(room.Configuration.MapId, Is.EqualTo("map-four-players"));
            SeatButton(0, "SwapButton").onClick.Invoke(); yield return null;
            ((Button[])Field(panel, "swapTargets"))[2].onClick.Invoke(); yield return Idle();
            room = Property<RoomState>(panel, "DisplayedRoom");
            Assert.That(room.SeatSlots[2].Occupant.PlayerId, Is.EqualTo(room.HostPlayerId));
            Assert.That(room.PendingSeatSwaps, Is.Empty);
            ((Button)Field(panel, "swapCloseButton")).onClick.Invoke(); yield return null;
            Assert.That(Property<Button>(panel, "StartButton").interactable, Is.True);
            yield return Capture("热座-四位本地玩家可开局");
        }

        [UnityTest] public IEnumerator HotseatRoomStartsTwoToFourPlayersOnBothActualMaps()
        {
            var counts = new[] { 2, 2, 3, 3, 4 };
            for (var scenario = 0; scenario < counts.Length; scenario++)
            {
                if (scenario > 0) yield return LoadMainMenu();
                var count = counts[scenario];
                var map = scenario % 2 == 0 && count < 4 ? "map-three-players" : "map-four-players";
                Property<Button>(view, "StartGameButton").onClick.Invoke(); yield return Idle();
                foreach (var option in (Array)Field(panel, "maps"))
                    if ((string)option.GetType().GetField("Id").GetValue(option) == map)
                        ((Button)option.GetType().GetField("Button").GetValue(option)).onClick.Invoke();
                yield return Idle();
                for (var i = 1; i < count; i++) { SeatButton(i, "HotSeatButton").onClick.Invoke(); yield return Idle(); }
                var frozen = Property<RoomState>(panel, "DisplayedRoom");
                Assert.That(frozen.Configuration.MapId, Is.EqualTo(map));
                var start = Property<Button>(panel, "StartButton");
                Assert.That(start.interactable, Is.True); AssertHit(start);
                start.onClick.Invoke();
                var sceneName = map == "map-three-players" ? "ThreePlayerScene" : "SampleScene";
                var deadline = Time.realtimeSinceStartup + 40;
                while (SceneManager.GetActiveScene().name != sceneName && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo(sceneName));
                yield return WaitForTransition();
                for (var i = 0; i < 10; i++) yield return null;
                var launchType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
                var launch = launchType.GetProperty("Instance").GetValue(null);
                Assert.That(launchType.GetField("Mode").GetValue(launch), Is.EqualTo(LaunchMode.Local));
                var gameplayType = Type.GetType("YC.Presentation.MobileCityInteractionController, Assembly-CSharp", true);
                var gameplay = UnityEngine.Object.FindObjectOfType(gameplayType);
                var session = (GameSession)Field(gameplay, "session");
                Assert.That(session.State.Players.Count, Is.EqualTo(count));
                Assert.That(session.State.RuleConfiguration.MapId, Is.EqualTo(map));
                Assert.That(session.State.RuleConfiguration.ConfigurationRevision, Is.EqualTo(frozen.Configuration.ConfigurationRevision));
                Assert.That(session.State.RuleConfiguration.ContentPackHash, Is.EqualTo(frozen.Configuration.ContentPackHash));
                var presenter = (TurnActionPresenter)Field(gameplay, "turnActionPresenter");
                var currentPlayer = session.State.CurrentPlayerId;
                Assert.That((int)Field(gameplay, "localPlayerId"), Is.EqualTo(currentPlayer));
                var entrances = presenter.BuildInitialPlacementHighlights();
                Assert.That(entrances.Count, Is.GreaterThan(0));
                presenter.PlaceInitialCity(entrances[0].TargetId);
                yield return null;
                // 完成当前玩家的真实入场交互，确认本机控制权自动交给下一位。
                for (var answerCount = 0; session.State.CurrentPlayerId == currentPlayer && answerCount < 12; answerCount++)
                {
                    var request = session.State.EffectRuntime.InteractionRequests.Find(r => r.Status == "open" && !r.IsInternalMainlineInteraction());
                    Assert.That(request, Is.Not.Null, "入场后应前进或等待可回答的真实交互");
                    Assert.That(request.CandidateIds.Count, Is.GreaterThan(0));
                    var answer = YC.Application.Interactions.EffectInteractionCommands.Answer(
                        YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId), request.AnsweringPlayerId, new[] { request.CandidateIds[0] });
                    var result = session.Submit(answer);
                    Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
                    presenter.SynchronizeFromState();
                    yield return null;
                }
                Assert.That(session.State.CurrentPlayerId, Is.Not.EqualTo(currentPlayer));
                presenter.SynchronizeFromState();
                yield return null;
                Assert.That((int)Field(gameplay, "localPlayerId"), Is.EqualTo(session.State.CurrentPlayerId));
                yield return Capture("热座-" + count + "人-" + sceneName + "-轮流入场");
                launchType.GetMethod("ReturnToStartScene").Invoke(null, new object[] { "StartScene" });
                yield return WaitForTransition();
                Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("StartScene"));
            }
        }

        [UnityTest] public IEnumerator TwoPlayerEnterpriseLaterInitialInfluenceUsesActualGeneralMapCandidates()
        {
            var f = new GameplayPageAcceptanceFixture();
            bool observed = false;
            IEnumerator Inspect(YC.Domain.State.InteractionRequest request)
            {
                if (request.InteractionTypeId != "setup.influence.optional") yield break;
                Assert.That(observed, Is.False, "只验证非初始玩家的正式初放请求");
                Assert.That(request.AnsweringPlayerId, Is.Not.EqualTo(f.State.StartPlayerId));
                var map = new YC.Domain.Maps.MapQueryService(YC.Domain.Maps.StaticMapDefinitions.Resolve(f.State.MapId));
                var legal = new YC.Domain.Influence.InfluenceService(map).GetLegalPlacementSlotIds(f.State, request.AnsweringPlayerId);
                Assert.That(request.CandidateIds, Is.EquivalentTo(legal), "初放不得继承城市初始资源点子集");
                var outsideInitialLocations = request.CandidateIds.Where(id => id.StartsWith("route:", StringComparison.Ordinal)).ToArray();
                Assert.That(outsideInitialLocations, Is.Not.Empty, "正式候选应包含初始资源点以外的合法航道");
                Component MapButton(string id) => GameplayPageAcceptanceFixture.Active("MapButtonView").SingleOrDefault(button =>
                    GameplayPageAcceptanceFixture.Read(button, "TargetKind").ToString() == "InfluenceSlot" &&
                    (string)GameplayPageAcceptanceFixture.Read(button, "TargetId") == id && (bool)GameplayPageAcceptanceFixture.Read(button, "IsInteractive"));
                string Visible() => outsideInitialLocations.FirstOrDefault(id => MapButton(id) != null && HotseatUiInputDriver.TryPoint(MapButton(id).gameObject, out _, out _));
                yield return GameplayPageAcceptanceFixture.Wait(() => Visible() != null, "合法初放航道未出现实际可点击高亮");
                var selected = Visible();
                var before = f.State.ItemZones.Items.Count(item => item.SlotId == selected && item.OwnerSubject.PlayerId == request.AnsweringPlayerId);
                f.Record("两人非初始玩家初放候选", new { f.State.MapId, f.State.StartPlayerId, request.AnsweringPlayerId,
                    requestId = request.GetStableInteractionId(), request.CandidateIds, legal, selected });
                yield return f.Click(MapButton(selected).gameObject, "非初始玩家_选择合法航道影响力位");
                if (f.Request?.GetStableInteractionId() == request.GetStableInteractionId())
                {
                    Assert.That((bool)GameplayPageAcceptanceFixture.Read(MapButton(selected), "IsSelected"), Is.True);
                    yield return f.Click(MapButton(selected).gameObject, "非初始玩家_确认正式初放");
                }
                yield return GameplayPageAcceptanceFixture.Wait(() => f.Request == null || f.Request.GetStableInteractionId() != request.GetStableInteractionId(), "初放点击未推进正式请求");
                Assert.That(f.State.ItemZones.Items.Count(item => item.SlotId == selected && item.OwnerSubject.PlayerId == request.AnsweringPlayerId), Is.EqualTo(before + 1));
                observed = true;
                yield return f.Capture("两人企业非初始玩家真实航道初放", f.Gameplay);
            }
            yield return f.Start(2, _ => GameplayPageAcceptanceFixture.Settle(), openingRequestCheck: Inspect, mapId: "map-four-players");
            Assert.That(observed, Is.True, "不得用没有初放请求的开局计通过");
        }

        private static IEnumerator WaitForTransition()
        {
            var type = Type.GetType("YC.Presentation.SceneTransitionContext, Assembly-CSharp", true);
            var property = type.GetProperty("IsTransitionInProgress");
            var deadline = Time.realtimeSinceStartup + 30;
            while ((bool)property.GetValue(null) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That((bool)property.GetValue(null), Is.False, "等待正式转场结束后再继续操作");
        }

        private IEnumerator Idle()
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while (((bool)Field(controller, "roomRequestPending") || (bool)Field(controller, "joiningRoom")) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That((bool)Field(controller, "roomRequestPending"), Is.False);
            Assert.That((bool)Field(controller, "joiningRoom"), Is.False);
            yield return null;
        }
        private static void AssertHit(Button button)
        {
            Canvas.ForceUpdateCanvases();
            var corners = new Vector3[4]; ((RectTransform)button.transform).GetWorldCorners(corners);
            var position = RectTransformUtility.WorldToScreenPoint(null, (corners[0] + corners[2]) * .5f);
            var hits = new System.Collections.Generic.List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = position }, hits);
            Assert.That(hits.Count, Is.GreaterThan(0));
            Assert.That(hits[0].gameObject.GetComponentInParent<Button>(), Is.SameAs(button));
        }
        private static IEnumerator Capture(string name)
        {
            var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("YC_ROOM_ENTRY_EVIDENCE") ?? "Logs/ExpansionValidation/RoomEntry/visual");
            Directory.CreateDirectory(directory);
            for (var i = 0; i < 5; i++) yield return null;
            var path = Path.Combine(directory, name + ".png"); ScreenCapture.CaptureScreenshot(path);
            for (var i = 0; i < 12; i++) yield return null;
            Assert.That(File.Exists(path), Is.True);
        }

        private sealed class TestNetwork : IOnlineRoomService, IPreconfiguredRoomHost
        {
            private readonly IRoomContentCapabilityProvider content;
            private readonly RoomConfigurationService authority;
            private readonly TaskCompletionSource<RoomState> pending = new TaskCompletionSource<RoomState>();
            private RoomState state;
            public bool Disposed;
            public TestNetwork(IRoomContentCapabilityProvider content) { this.content = content; authority = new RoomConfigurationService(content); }
            public bool SupportsFriendInvites => true;
            public bool HasPendingLobbyJoinRequest => false;
            public event Action<RoomState> RoomUpdated;
            public event Action<RoomState> GameStarted { add { } remove { } }
            public event Action RoomDisbanded { add { } remove { } }
            public event Action<string> ErrorOccurred { add { } remove { } }
            public event Action<string> LobbyJoinRequested { add { } remove { } }
            public void Initialize() { }
            public Task<RoomState> CreateRoomFromSnapshotAsync(RoomState source)
            {
                state = RoomHostSnapshot.Create(source, "109775241053911249", new PlayerSeat
                { PlayerId = 1, PlayerName = "验证房主", LobbyMemberPresent = true, SteamId = LocalMirrorIdentity.ForPlayer(1) }, content);
                return pending.Task;
            }
            public void Complete() => pending.SetResult(state.Clone());
            public void Fail() => pending.SetException(new InvalidOperationException("联机连接失败（验证）"));
            public void AddGuest()
            { authority.TryOccupySeat(state, new PlayerSeat { PlayerId = 2, PlayerName = "验证成员", LobbyMemberPresent = true }, out _); RoomUpdated?.Invoke(state.Clone()); }
            public RoomState GetCurrentRoom() => state?.Clone();
            public void Shutdown() { state = null; }
            public void Dispose() { Disposed = true; Shutdown(); }
            public void InviteFriends() { }
            public bool TryConsumePendingLobbyJoinRequest(out string id) { id = null; return false; }
            public Task<RoomState> CreateRoomAsync(string name, int count) => throw new NotSupportedException();
            public Task<RoomState> JoinRoomAsync(string id, string name) => throw new NotSupportedException();
            public Task StartGameAsync() => throw new NotSupportedException();
            public Task<RoomConfigurationResult> ChangeConfigurationAsync(ChangeRoomConfigurationRequest r) => Task.FromResult(authority.ChangeConfiguration(state, r));
            public Task<RoomConfigurationResult> SetReadyAsync(SetRoomReadyRequest r) => Task.FromResult(authority.SetReady(state, r));
            public Task<RoomConfigurationResult> SetSeatStatusAsync(SetRoomSeatStatusRequest r) => Task.FromResult(authority.SetSeatStatus(state, r));
            public Task<RoomConfigurationResult> RequestSeatSwapAsync(RequestRoomSeatSwapRequest r) => Task.FromResult(authority.RequestSeatSwap(state, r));
            public Task<RoomConfigurationResult> ConfirmSeatSwapAsync(ConfirmRoomSeatSwapRequest r) => Task.FromResult(authority.ConfirmSeatSwap(state, r));
            public Task<RoomConfigurationResult> StartConfiguredGameAsync(StartConfiguredGameRequest r) => throw new NotSupportedException();
        }
    }
}
