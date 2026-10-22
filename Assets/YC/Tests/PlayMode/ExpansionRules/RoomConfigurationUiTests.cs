using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.PlayMode.ExpansionRules
{
    public sealed class RoomConfigurationUiTests
    {
        private Component controller, panel;
        private TestRoomAdapter adapter;
        private IRoomContentCapabilityProvider content;
        private RoomConfigurationService authority;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [UnitySetUp] public IEnumerator LoadProductionScene()
        {
            yield return SceneManager.LoadSceneAsync("StartScene");
            yield return null;
            controller = Find("YC.Presentation.StartMenuController");
            panel = Find("YC.Presentation.StartMenuRoomPanelView");
            Assert.That(controller, Is.Not.Null);
            Assert.That(panel, Is.Not.Null);
            var provider = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            content = (IRoomContentCapabilityProvider)provider.GetProperty("ContentCapabilities").GetValue(null);
            authority = new RoomConfigurationService(content);
            adapter = new TestRoomAdapter(authority, Room(3));
            Attach(adapter);
            Show(adapter.GetCurrentRoom());
        }

        [UnityTearDown] public IEnumerator ReleaseServices()
        {
            adapter?.Dispose();
            yield return null;
        }
        private static Component Find(string name)
        {
            var type = Type.GetType(name + ", Assembly-CSharp", true);
            foreach (var item in Resources.FindObjectsOfTypeAll(type))
                if (item is Component c && c.gameObject.scene.IsValid()) return c;
            return null;
        }
        private static object Field(object obj, string name) => obj.GetType().GetField(name, Private).GetValue(obj);
        private static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Private | BindingFlags.Public).Invoke(obj, args);
        private static Button ButtonAt(object option) => (Button)option.GetType().GetField("Button").GetValue(option);
        private object Option(string group, int index) => ((Array)Field(panel, group)).GetValue(index);
        private Button SeatButton(int index, string name)
        {
            var seat = ((Array)Field(panel, "seats")).GetValue(index);
            return (Button)seat.GetType().GetField(name).GetValue(seat);
        }
        private RoomState Room(int count)
        {
            var room = new RoomState { RoomId = "000123", HostPlayerId = 1, LocalPlayerId = 1 };
            room.Configuration.ContentPackHash = content.GetContentPackHash(room.Configuration);
            for (var id = 1; id <= count; id++) AddPlayer(room, id);
            return room;
        }
        private void AddPlayer(RoomState room, int id)
        {
            Assert.That(authority.TryOccupySeat(room, new PlayerSeat
            {
                PlayerId = id, PlayerName = "验证玩家 " + id, LobbyMemberPresent = true,
                SteamId = LocalMirrorIdentity.ForPlayer(id), NetworkClientId = (ulong)(id - 1),
                TransportConnected = true, IdentityVerified = true, IsReady = true,
                ContentPackHash = room.Configuration.ContentPackHash,
                RuleContractVersion = room.Configuration.RuleContractVersion
            }, out var reason), Is.True, reason);
        }
        private T Request<T>(RoomState room, int player) where T : RoomConfigurationRequest, new() => new T
        { RoomId = room.RoomId, RequestingPlayerId = player, CommandId = Guid.NewGuid().ToString("N"), ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision };
        private void ReadyGuests(RoomState room)
        {
            foreach (var player in room.Seats)
            {
                if (player.PlayerId == room.HostPlayerId) continue;
                // 临时适配器没有消息封装；模拟真实准备命令携带当前配置的本地内容哈希。
                player.ContentPackHash = content.GetContentPackHash(room.Configuration);
                var request = Request<SetRoomReadyRequest>(room, player.PlayerId); request.IsUserReady = true;
                Assert.That(authority.SetReady(room, request).Success, Is.True);
            }
        }
        private void Attach(TestRoomAdapter service)
        {
            // 替换服务前释放生产服务订阅，避免场景销毁后仍向旧控制器发送通知。
            Call(controller, "UnbindRoomServiceEvents");
            controller.GetType().GetField("roomService", Private).SetValue(controller, service);
            service.RoomUpdated += room => Call(controller, "QueueRoomUpdate", room);
        }
        private void Show(RoomState room) => Call(controller, "ShowRoomPanel", room, room.LocalPlayerId == room.HostPlayerId);

        [UnityTest] public IEnumerator RepeatedRoomRenderingReusesContentButRechecksReadyAndConnectionState()
        {
            ReadyGuests(adapter.State);
            Show(adapter.GetCurrentRoom());
            var count = content.GetType().GetProperty("CatalogBuildCount", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(count, Is.Not.Null);
            var warmBuilds = (int)count.GetValue(content);
            var start = (Button)panel.GetType().GetProperty("StartButton").GetValue(panel);
            Assert.That(start.interactable, Is.True);
            for (var i = 0; i < 20; i++)
            {
                Call(panel, "SetRequestPending", true);
                Assert.That(start.interactable, Is.False);
                Call(panel, "SetRequestPending", false);
                Show(adapter.GetCurrentRoom());
                Assert.That(start.interactable, Is.True);
            }
            adapter.State.Seats[1].TransportConnected = false;
            Show(adapter.GetCurrentRoom());
            Assert.That(start.interactable, Is.False, "内容缓存不能缓存联网就绪结果");
            adapter.State.Seats[1].TransportConnected = true;
            var cancel = Request<SetRoomReadyRequest>(adapter.State, 2);
            Assert.That(authority.SetReady(adapter.State, cancel).Success, Is.True);
            Show(adapter.GetCurrentRoom());
            Assert.That(start.interactable, Is.False, "取消准备必须即时阻止开始");
            Assert.That((int)count.GetValue(content), Is.EqualTo(warmBuilds), "普通 Render/请求反馈/准备态变化不应重建活动目录");
            yield return null;
        }
        private IEnumerator WaitForReply()
        {
            var deadline = Time.realtimeSinceStartup + 8;
            while ((bool)Field(controller, "roomRequestPending") && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That((bool)Field(controller, "roomRequestPending"), Is.False, "请求必须等到 Host 结果");
            yield return null;
        }

        [UnityTest] public IEnumerator ProductionReferencesPreserveRoomNumberAndSnapshot()
        {
            var args = new object[] { null };
            Assert.That((bool)panel.GetType().GetMethod("TryValidateConfiguration").Invoke(panel, args), Is.True, args[0] as string);
            Assert.That(panel.GetComponentsInChildren<Component>(true), Is.All.Not.Null, "生产房间子树不得有丢失脚本");
            foreach(var scroll in panel.GetComponentsInChildren<ScrollRect>(true))
                Assert.That(scroll.viewport.GetComponent<Mask>(), Is.Not.Null, "滚动视口必须有实际裁切组件");
            Assert.That(((Text)panel.GetType().GetProperty("RoomCodeText").GetValue(panel)).text, Does.Contain("000123"));
            ((Button)panel.GetType().GetProperty("CopyButton").GetValue(panel)).onClick.Invoke();
            Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo("000123"));
            var revision = adapter.State.Configuration.ConfigurationRevision;
            ButtonAt(Option("maps", 1)).onClick.Invoke();
            yield return WaitForReply();
            Assert.That(adapter.LastRequest, Is.InstanceOf<ChangeRoomConfigurationRequest>());
            Assert.That(adapter.State.Configuration.MapId, Is.EqualTo("map-four-players"));
            Assert.That(adapter.State.Configuration.ConfigurationRevision, Is.EqualTo(revision + 1));
            var displayed = (RoomState)panel.GetType().GetProperty("DisplayedRoom").GetValue(panel);
            displayed.Configuration.MapId = "map-three-players";
            Assert.That(adapter.State.Configuration.MapId, Is.EqualTo("map-four-players"), "展示副本不能写回权威状态");
        }

        [UnityTest] public IEnumerator GuestSettingsAreReadOnlyAndConfigurationInvalidatesPreparation()
        {
            adapter.State.LocalPlayerId = 2; Show(adapter.GetCurrentRoom());
            Assert.That(ButtonAt(Option("maps", 0)).interactable, Is.False);
            Assert.That(ButtonAt(Option("ruleModes", 0)).interactable, Is.False);
            Assert.That(ButtonAt(Option("expansions", 1)).interactable, Is.False);
            ButtonAt(Option("expansions", 1)).onClick.Invoke();
            Assert.That(adapter.LastRequest, Is.Null, "成员即使程序触发点击也不能发越权配置命令");
            ((Button)panel.GetType().GetProperty("StartButton").GetValue(panel)).onClick.Invoke();
            yield return WaitForReply();
            Assert.That(RoomConfigurationService.FindSlot(adapter.State, 2).IsUserReady, Is.True);
            adapter.State.LocalPlayerId = 1; Show(adapter.GetCurrentRoom());
            ButtonAt(Option("maps", 1)).onClick.Invoke(); yield return WaitForReply();
            Assert.That(RoomConfigurationService.FindSlot(adapter.State, 2).IsUserReady, Is.False);
            Assert.That(RoomConfigurationService.FindSlot(adapter.State, 2).Occupant.IsReady, Is.True, "人工准备失效不清连接身份");
        }

        [UnityTest] public IEnumerator ConfigurationSummaryOmitsRevisionWhileRealChangeInvalidatesReady()
        {
            var evidence = new GameplayPageAcceptanceFixture();
            yield return evidence.Resolution(new Vector2Int(1920, 1080));
            ReadyGuests(adapter.State); Show(adapter.GetCurrentRoom());
            yield return GameplayPageAcceptanceFixture.Settle();
            var revision = adapter.State.Configuration.ConfigurationRevision;
            Assert.That(RoomConfigurationService.FindSlot(adapter.State, 2).IsUserReady, Is.True);
            var summary = (Text)panel.GetType().GetProperty("ValidationText").GetValue(panel);
            var before = summary.text;
            yield return evidence.Click(ButtonAt(Option("maps", 1)).gameObject, "房间摘要_更改真实地图配置");
            yield return WaitForReply();
            Assert.That(adapter.State.Configuration.ConfigurationRevision, Is.EqualTo(revision + 1));
            Assert.That(RoomConfigurationService.FindSlot(adapter.State, 2).IsUserReady, Is.False);
            foreach (var text in new[] { before, summary.text })
            {
                Assert.That(text, Does.Not.Contain("修订"), "只移除修订显示，不移除配置 revision 功能");
                Assert.That(text.ToLowerInvariant(), Does.Not.Contain("revision"));
            }
            yield return evidence.Click(ButtonAt(Option("ruleModes", 1)).gameObject, "房间摘要_验证拒绝原因仍显示");
            yield return WaitForReply();
            Assert.That(adapter.LastResult.Success, Is.False);
            Assert.That(((Text)panel.GetType().GetProperty("StatusText").GetValue(panel)).text,
                Is.EqualTo(Call(panel, "ResolveReason", adapter.LastResult.ReasonCode)));
            evidence.Record("摘要修订显示与权威配置", new { before, after = summary.text, revision,
                actualRevision = adapter.State.Configuration.ConfigurationRevision, adapter.LastResult.ReasonCode });
            yield return evidence.Capture("房间摘要无修订计数且拒绝提示保留", panel);
        }

        [UnityTest] public IEnumerator FourOccupiedSeatsForceMapAndDeparturePreservesSelection()
        {
            AddPlayer(adapter.State, 4); Show(adapter.GetCurrentRoom());
            Assert.That(adapter.State.Configuration.MapId, Is.EqualTo("map-four-players"));
            Assert.That(ButtonAt(Option("maps", 0)).interactable, Is.False);
            Assert.That(authority.VacateSeat(adapter.State, 4), Is.True); Show(adapter.GetCurrentRoom());
            Assert.That(adapter.State.PlayerCount, Is.EqualTo(3));
            Assert.That(adapter.State.Configuration.MapId, Is.EqualTo("map-four-players"));
            Assert.That(ButtonAt(Option("maps", 0)).interactable, Is.True);
            foreach (var image in panel.GetComponentsInChildren<Image>(true))
                if (image.name == "RoomThreePlayerMapImage" || image.name == "RoomFourPlayerMapImage")
                { Assert.That(image.sprite, Is.Not.Null); Assert.That(image.preserveAspect, Is.True); }
            yield return null;
        }

        [UnityTest] public IEnumerator UnsupportedModeAndDependencyReturnHostReasonWithoutOptimisticSelection()
        {
            ButtonAt(Option("ruleModes", 1)).onClick.Invoke(); yield return WaitForReply();
            Assert.That(adapter.LastResult.ReasonCode, Is.EqualTo("rule_mode_not_implemented"));
            Assert.That(adapter.State.Configuration.RuleModeId, Is.EqualTo("standard"));
            Assert.That(((Text)panel.GetType().GetProperty("StatusText").GetValue(panel)).text,
                Is.EqualTo(Call(panel, "ResolveReason", adapter.LastResult.ReasonCode)));
            yield return CaptureCurrent("房间-真实命令拒绝-企业家未实现");
            ButtonAt(Option("expansions", 2)).onClick.Invoke(); yield return WaitForReply();
            Assert.That(adapter.LastResult.ReasonCode, Is.EqualTo("missing_enterprise_dependency"));
            Assert.That(adapter.State.Configuration.EnabledExpansionIds, Does.Not.Contain("infected"));
            yield return CaptureCurrent("房间-真实命令拒绝-感染者缺企业依赖");
            adapter.DelayReply = true;
            ButtonAt(Option("expansions", 1)).onClick.Invoke();
            Assert.That(adapter.State.Configuration.EnabledExpansionIds, Does.Not.Contain("enterprise"));
            var selected = (Image)Option("expansions", 1).GetType().GetField("Selection").GetValue(Option("expansions", 1));
            Assert.That(selected.gameObject.activeSelf, Is.False, "回执前不勾选");
            adapter.CompletePending(); yield return WaitForReply();
            Assert.That(adapter.State.Configuration.EnabledExpansionIds, Does.Contain("enterprise"));
            Assert.That(((Button)panel.GetType().GetProperty("StartButton").GetValue(panel)).interactable, Is.False, "未验收扩展不能开局");
        }

        private IEnumerator CaptureCurrent(string name)
        {
            var directory=Path.GetFullPath(Environment.GetEnvironmentVariable("YC_ROOM_UI_EVIDENCE") ?? "Logs/ExpansionValidation/RoomConfigurationUi/visual");
            Directory.CreateDirectory(directory);
            for(var i=0;i<4;i++) yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(directory,name+".png"));
            for(var i=0;i<12;i++) yield return null;
        }

        [UnityTest] public IEnumerator SeatsUseRealCloseOpenAndConfirmedSwapCommands()
        {
            SeatButton(3, "CloseButton").onClick.Invoke(); yield return WaitForReply();
            Assert.That(adapter.State.SeatSlots[3].Status, Is.EqualTo("closed"));
            SeatButton(3, "OpenButton").onClick.Invoke(); yield return WaitForReply();
            Assert.That(adapter.State.SeatSlots[3].Status, Is.EqualTo("open"));
            var targets = (Button[])Field(panel, "swapTargets"); targets[1].onClick.Invoke(); yield return WaitForReply();
            Assert.That(adapter.State.SeatSlots[0].Occupant.PlayerId, Is.EqualTo(1), "请求不直接交换");
            adapter.State.LocalPlayerId = 2; Show(adapter.GetCurrentRoom());
            ((Button)Field(panel, "swapAcceptButton")).onClick.Invoke(); yield return WaitForReply();
            Assert.That(adapter.State.HostPlayerId, Is.EqualTo(1));
            Assert.That(adapter.State.SeatSlots[1].Occupant.PlayerId, Is.EqualTo(1));
            Assert.That(adapter.State.SeatSlots[1].Occupant.Color, Is.EqualTo(RoomConfigurationService.GetSeatColor(1)));
            Assert.That(adapter.State.PendingSeatSwaps, Is.Empty);
        }

        [UnityTest] public IEnumerator TcpMemberUiPreparationReachesHostAndConfigurationReplyReturns()
        {
            using (var host = new NetworkRoomService(content))
            using (var member = new NetworkRoomService(content))
            {
                var created = host.CreateRoom("TCP 房主", 3);
                member.JoinRoom("127.0.0.1:" + created.RoomId.Split(':')[1], "TCP 成员");
                var deadline = Time.realtimeSinceStartup + 6;
                while ((member.GetCurrentRoom()?.LocalPlayerId <= 0 || host.GetCurrentRoom().PlayerCount != 2 || member.GetCurrentRoom()?.PlayerCount != 2) && Time.realtimeSinceStartup < deadline) yield return null;
                var guest = member.GetCurrentRoom().LocalPlayerId;
                Assert.That(guest, Is.GreaterThan(0));
                host.SetTransportReadiness(1, true, true, 0); host.SetTransportReadiness(guest, true, true, 1);
                deadline = Time.realtimeSinceStartup + 6;
                while (!member.GetCurrentRoom().Seats.TrueForAll(p => p.IsReady) && Time.realtimeSinceStartup < deadline) yield return null;
                adapter = new TestRoomAdapter(authority, member.GetCurrentRoom(), member); Attach(adapter); Show(adapter.GetCurrentRoom());
                var prepareButton = (Button)panel.GetType().GetProperty("StartButton").GetValue(panel);
                var tcpSnapshot = member.GetCurrentRoom();
                var tcpSlot = RoomConfigurationService.FindSlot(tcpSnapshot, guest);
                Assert.That(prepareButton.interactable, Is.True, "TCP 准备条件：" + JsonUtility.ToJson(tcpSlot) + " 配置：" + JsonUtility.ToJson(tcpSnapshot.Configuration));
                prepareButton.onClick.Invoke(); yield return WaitForReply();
                Assert.That(adapter.LastResult, Is.Not.Null);
                Assert.That(adapter.LastResult.Success, Is.True, adapter.LastResult.ReasonCode);
                Assert.That(RoomConfigurationService.FindSlot(host.GetCurrentRoom(), guest).IsUserReady, Is.True);
                adapter = new TestRoomAdapter(authority, host.GetCurrentRoom(), host); Attach(adapter); Show(adapter.GetCurrentRoom());
                ButtonAt(Option("maps", 1)).onClick.Invoke(); yield return WaitForReply();
                Assert.That(host.GetCurrentRoom().Configuration.MapId, Is.EqualTo("map-four-players"));
                Assert.That(RoomConfigurationService.FindSlot(host.GetCurrentRoom(), guest).IsUserReady, Is.False);
                ButtonAt(Option("ruleModes", 1)).onClick.Invoke(); yield return WaitForReply();
                Assert.That(adapter.LastResult.ReasonCode, Is.EqualTo("rule_mode_not_implemented"));
                using(var third=new NetworkRoomService(content))
                using(var fourth=new NetworkRoomService(content))
                {
                    var address="127.0.0.1:"+created.RoomId.Split(':')[1];
                    third.JoinRoom(address,"TCP 第三成员");
                    deadline=Time.realtimeSinceStartup+6;
                    while(host.GetCurrentRoom().PlayerCount!=3 && Time.realtimeSinceStartup<deadline) yield return null;
                    Assert.That(host.GetCurrentRoom().PlayerCount, Is.EqualTo(3));
                    Show(host.GetCurrentRoom());yield return CaptureCurrent("房间-TCP权威快照-3人");
                    fourth.JoinRoom(address,"TCP 第四成员");
                    deadline=Time.realtimeSinceStartup+6;
                    while(host.GetCurrentRoom().PlayerCount!=4 && Time.realtimeSinceStartup<deadline) yield return null;
                    Assert.That(host.GetCurrentRoom().PlayerCount, Is.EqualTo(4));
                    Show(host.GetCurrentRoom());
                    Assert.That(ButtonAt(Option("maps",0)).interactable, Is.False);
                    yield return CaptureCurrent("房间-TCP权威快照-4人强制地图");
                    fourth.Shutdown();deadline=Time.realtimeSinceStartup+6;
                    while(host.GetCurrentRoom().PlayerCount!=3 && Time.realtimeSinceStartup<deadline) yield return null;
                    Assert.That(host.GetCurrentRoom().PlayerCount, Is.EqualTo(3));
                    Assert.That(host.GetCurrentRoom().Configuration.MapId, Is.EqualTo("map-four-players"));
                    var thirdId=third.GetCurrentRoom().LocalPlayerId;
                    host.SetTransportReadiness(thirdId,true,true,2);
                    foreach(var client in new[]{member,third})
                    {
                        deadline=Time.realtimeSinceStartup+6;
                        while(client.GetCurrentRoom().Configuration.ConfigurationRevision!=host.GetCurrentRoom().Configuration.ConfigurationRevision && Time.realtimeSinceStartup<deadline) yield return null;
                        var request=Request<SetRoomReadyRequest>(client.GetCurrentRoom(),client.GetCurrentRoom().LocalPlayerId);request.IsUserReady=true;
                        var task=client.SendConfigurationAsync(request);
                        while(!task.IsCompleted) yield return null;
                        Assert.That(task.Result.Success, Is.True, task.Result.ReasonCode);
                    }
                    Show(host.GetCurrentRoom());
                    ((Button)panel.GetType().GetProperty("StartButton").GetValue(panel)).onClick.Invoke();yield return WaitForReply();
                    Assert.That(adapter.LastRequest, Is.InstanceOf<StartConfiguredGameRequest>());
                    Assert.That(adapter.LastResult.Success, Is.True, adapter.LastResult.ReasonCode);
                    Assert.That(host.GetCurrentRoom().HasStarted, Is.True);
                    Assert.That(host.GetCurrentRoom().Configuration.PlayerCount, Is.EqualTo(3));
                    Assert.That(host.GetCurrentRoom().Configuration.MapId, Is.EqualTo("map-four-players"));
                }
                Debug.Log("房间 UI 实际 TCP：成员准备经 JSON 命令到 Host；Host 配置更改清准备；未实现模式被 Host 拒绝。运输就绪由测试绑定，非双 Steam/Mirror 验收。");
            }
        }

        [UnityTest] public IEnumerator HostStartUsesFrozenConfigurationForEverySupportedPlayerCount()
        {
            foreach(var count in new[]{2,3,4})
            {
                adapter.State=Room(count);ReadyGuests(adapter.State);Show(adapter.GetCurrentRoom());
                var button=(Button)panel.GetType().GetProperty("StartButton").GetValue(panel);
                Assert.That(button.interactable, Is.True);
                button.onClick.Invoke();yield return WaitForReply();
                Assert.That(adapter.LastRequest, Is.InstanceOf<StartConfiguredGameRequest>());
                Assert.That(adapter.LastResult.Success, Is.True, adapter.LastResult.ReasonCode);
                Assert.That(adapter.State.HasStarted, Is.True);
                Assert.That(adapter.LastResult.Snapshot.Configuration.PlayerCount, Is.EqualTo(count));
            }
        }

        [UnityTest] public IEnumerator ProductionSceneVisualMatrixAndLongExpansionScroll()
        {
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("YC_ROOM_UI_EVIDENCE") ?? "Logs/ExpansionValidation/RoomConfigurationUi/visual");
            Directory.CreateDirectory(directory);
            foreach (var size in new[] { new Vector2Int(1920,1080), new Vector2Int(900,600), new Vector2Int(2560,1080) })
            {
                capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,null);
                capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,new object[] {size});
                var deadline = Time.realtimeSinceStartup + 15;
                while ((Screen.width != size.x || Screen.height != size.y) && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(new Vector2Int(Screen.width,Screen.height), Is.EqualTo(size));
                foreach (var count in new[] {2,3,4})
                {
                    adapter.State = Room(count); ReadyGuests(adapter.State); adapter.State.LocalPlayerId = count == 2 ? 2 : 1; Show(adapter.GetCurrentRoom());
                    for (var i=0;i<8;i++) yield return null;
                    Canvas.ForceUpdateCanvases();
                    AssertReachable((Button)panel.GetType().GetProperty("StartButton").GetValue(panel));
                    AssertReachable((Button)panel.GetType().GetProperty("BackButton").GetValue(panel));
                    var path=Path.Combine(directory,"房间-临时绑定-"+count+"人-"+size.x+"x"+size.y+".png");
                    ScreenCapture.CaptureScreenshot(path);
                    for (var i=0;i<12;i++) yield return null;
                    Assert.That(File.Exists(path), Is.True);
                }
                if(size.x==900)
                {
                    var body=panel.transform.Find("RoomBody").GetComponent<ScrollRect>();
                    body.horizontalNormalizedPosition=1;body.verticalNormalizedPosition=0;Canvas.ForceUpdateCanvases();
                    ScreenCapture.CaptureScreenshot(Path.Combine(directory,"房间-临时绑定-窄窗设置滚动.png"));
                    for(var i=0;i<12;i++) yield return null;
                    Assert.That(((Button)panel.GetType().GetProperty("StartButton").GetValue(panel)).gameObject.activeInHierarchy, Is.True);
                }
            }
            // 只在内存修改条目文案验证自然换行和滚动，不修改静态资产或生产定义。
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,new object[]{new Vector2Int(900,600)});
            var narrowDeadline=Time.realtimeSinceStartup+15;
            while(Screen.width!=900 && Time.realtimeSinceStartup<narrowDeadline) yield return null;
            var narrowBody=panel.transform.Find("RoomBody").GetComponent<ScrollRect>();
            narrowBody.horizontalNormalizedPosition=1;
            var longOption=Option("expansions",1);var btn=ButtonAt(longOption);
            btn.GetComponentInChildren<Text>().text = new string('长',150);
            Canvas.ForceUpdateCanvases(); yield return null;Canvas.ForceUpdateCanvases();
            var expansionScroll=panel.GetComponentsInChildren<ScrollRect>(true);
            var scroll=Array.Find(expansionScroll,x=>x.name=="RoomExpansionScroll");
            Assert.That(scroll.content.rect.height, Is.GreaterThan(scroll.viewport.rect.height));
            scroll.verticalNormalizedPosition=0;Canvas.ForceUpdateCanvases();
            ScreenCapture.CaptureScreenshot(Path.Combine(directory,"房间-临时长文案-扩展滚动.png"));
            for(var i=0;i<12;i++) yield return null;
            ((Button)Field(panel,"helpButton")).onClick.Invoke();
            Assert.That(((GameObject)Field(panel,"helpPanel")).activeSelf, Is.True);
            yield return CaptureCurrent("房间-临时绑定-说明弹窗");
            ((Button)Field(panel,"helpCloseButton")).onClick.Invoke();
            Assert.That(((GameObject)Field(panel,"helpPanel")).activeSelf, Is.False);
            SeatButton(0,"SwapButton").onClick.Invoke();
            Assert.That(((GameObject)Field(panel,"swapPanel")).activeSelf, Is.True);
            yield return CaptureCurrent("房间-临时绑定-换座弹窗");
            ((Button)Field(panel,"swapCloseButton")).onClick.Invoke();
            Assert.That(((GameObject)Field(panel,"swapPanel")).activeSelf, Is.False);
            File.WriteAllText(Path.Combine(directory,"截图来源.txt"),"全部截图来自生产 StartScene 的实际 StartMenuRoot 房间资产；使用临时完整房间绑定数据。画面检查不作为 Steam/Mirror 联机或企业玩法通过证据。TCP 命令独立测试仅验证房间 JSON 往返，运输身份就绪由夹具提供。\n");
        }

        private static void AssertReachable(Button button)
        {
            var corners=new Vector3[4];((RectTransform)button.transform).GetWorldCorners(corners);
            var position=RectTransformUtility.WorldToScreenPoint(null,(corners[0]+corners[2])*.5f);
            Assert.That(new Rect(0,0,Screen.width,Screen.height).Contains(position), Is.True, "主要操作必须位于可用窗口内");
            var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=position},hits);
            Assert.That(hits.Count, Is.GreaterThan(0));
            Assert.That(hits[0].gameObject.GetComponentInParent<Button>(), Is.SameAs(button), "主要操作不得被装饰或滚动区遮挡");
        }

        private sealed class TestRoomAdapter : IOnlineRoomService
        {
            private readonly RoomConfigurationService service;
            private readonly NetworkRoomService tcp;
            public RoomState State;
            public RoomConfigurationRequest LastRequest;
            public RoomConfigurationResult LastResult;
            public bool DelayReply;
            private TaskCompletionSource<RoomConfigurationResult> pending;
            private Func<RoomConfigurationResult> pendingAction;
            public TestRoomAdapter(RoomConfigurationService authority, RoomState state, NetworkRoomService network=null) { service=authority;State=state;tcp=network; }
            public bool SupportsFriendInvites=>false;
            public bool HasPendingLobbyJoinRequest=>false;
            public event Action<RoomState> RoomUpdated;
            public event Action<RoomState> GameStarted { add{} remove{} }
            public event Action RoomDisbanded { add{} remove{} }
            public event Action<string> ErrorOccurred { add{} remove{} }
            public event Action<string> LobbyJoinRequested { add{} remove{} }
            public void Initialize(){}
            public RoomState GetCurrentRoom()=>tcp?.GetCurrentRoom() ?? State.Clone();
            public void Shutdown(){}
            public void Dispose(){}
            public void InviteFriends()=>throw new InvalidOperationException("service_unavailable");
            public bool TryConsumePendingLobbyJoinRequest(out string id){id=null;return false;}
            public Task<RoomState> CreateRoomAsync(string name,int count)=>throw new NotSupportedException();
            public Task<RoomState> JoinRoomAsync(string id,string name)=>throw new NotSupportedException();
            public Task StartGameAsync()=>throw new NotSupportedException();
            private async Task<RoomConfigurationResult> Send(RoomConfigurationRequest request,Func<RoomConfigurationResult> action)
            {
                LastRequest=request;
                if(tcp!=null) LastResult=await tcp.SendConfigurationAsync(request);
                else if(DelayReply) { pending=new TaskCompletionSource<RoomConfigurationResult>();pendingAction=action;LastResult=await pending.Task; }
                else LastResult=action();
                if(LastResult.Success) RoomUpdated?.Invoke(LastResult.Snapshot);
                return LastResult;
            }
            public void CompletePending(){DelayReply=false;pending.SetResult(pendingAction());}
            public Task<RoomConfigurationResult> ChangeConfigurationAsync(ChangeRoomConfigurationRequest r)=>Send(r,()=>service.ChangeConfiguration(State,r));
            public Task<RoomConfigurationResult> SetReadyAsync(SetRoomReadyRequest r)=>Send(r,()=>service.SetReady(State,r));
            public Task<RoomConfigurationResult> SetSeatStatusAsync(SetRoomSeatStatusRequest r)=>Send(r,()=>service.SetSeatStatus(State,r));
            public Task<RoomConfigurationResult> RequestSeatSwapAsync(RequestRoomSeatSwapRequest r)=>Send(r,()=>service.RequestSeatSwap(State,r));
            public Task<RoomConfigurationResult> ConfirmSeatSwapAsync(ConfirmRoomSeatSwapRequest r)=>Send(r,()=>service.ConfirmSeatSwap(State,r));
            public Task<RoomConfigurationResult> StartConfiguredGameAsync(StartConfiguredGameRequest r)=>Send(r,()=>
            {var ok=service.TryFreezeForLaunch(State,r,out _,out var reason);return new RoomConfigurationResult{Success=ok,ReasonCode=reason,Snapshot=State.Clone()};});
        }
    }
}
