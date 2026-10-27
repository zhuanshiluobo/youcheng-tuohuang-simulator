using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class RoomDraftConfigurationR35Tests
    {
        private ExternalContentPack pack;
        [OneTimeSetUp] public void LoadRealContent() => pack = ExternalContentPack.Load(Path.GetFullPath("Assets/StreamingAssets/Content/core"));
        private IRoomContentCapabilityProvider Provider() => (IRoomContentCapabilityProvider)Activator.CreateInstance(
            Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true),
            new object[] { new Func<ExternalContentPack>(() => pack) });
        private static ChangeRoomConfigurationRequest Change(RoomState r, string mode, params string[] expansions) => new ChangeRoomConfigurationRequest
        {
            CommandId = Guid.NewGuid().ToString("N"), RoomId = r.RoomId, RequestingPlayerId = r.LocalPlayerId,
            ExpectedConfigurationRevision = r.Configuration.ConfigurationRevision, MapId = r.Configuration.MapId,
            RuleModeId = mode, EnabledExpansionIds = expansions.ToList()
        };
        private static void Record(string name, object value)
        {
            string directory = Environment.GetEnvironmentVariable("QY017_R35_OUTPUT") ?? throw new InvalidOperationException("未设置R35输出");
            using (var f = new FileStream(Path.Combine(directory, name + ".json"), FileMode.CreateNew))
            using (var w = new StreamWriter(f)) w.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
        }
        [TestCase(3)] [TestCase(4)]
        public void SingleHostCanSelectEnterpriseThenInfectedThenEntrepreneurBeforeOtherPlayersJoin(int selectedMapPlayers)
        {
            var provider = Provider();
            using (var service = new RoomSessionService(provider, () => throw new Exception("本地房间不启动网络")))
            {
                var room = service.CreateRoomAsync("原房主", selectedMapPlayers).GetAwaiter().GetResult();
                Assert.That(room.PlayerCount, Is.EqualTo(1));
                var steps = new List<object>();
                foreach (var selection in new[] {
                    new { Mode="standard", Expansions=new[]{"core","enterprise"} },
                    new { Mode="standard", Expansions=new[]{"core","enterprise","infected"} },
                    new { Mode="entrepreneur", Expansions=new[]{"core","enterprise","infected"} } })
                {
                    var result = service.ChangeConfigurationAsync(Change(room, selection.Mode, selection.Expansions)).GetAwaiter().GetResult();
                    steps.Add(new { selection.Mode, selection.Expansions, result.Success, result.ReasonCode });
                    if (!result.Success) Record("首拒绝-地图" + selectedMapPlayers, new { room, steps });
                    Assert.That(result.Success, Is.True, result.ReasonCode);
                    room = service.GetCurrentRoom();
                    Assert.That(room.PlayerCount, Is.EqualTo(1), "配置检查不得伪造入座人数");
                    Assert.That(room.Configuration.PlayerCount, Is.EqualTo(1));
                    Assert.That(room.Configuration.RuleModeId, Is.EqualTo(selection.Mode));
                    Assert.That(room.Configuration.EnabledExpansionIds, Is.EquivalentTo(selection.Expansions));
                    Assert.That(provider.TryValidate(room.Configuration, out var reason), Is.True, reason);
                }
                Record("单房主配置成功-地图" + selectedMapPlayers, new { room, steps, gameStarted = room.HasStarted, inputStateInjected = false });
                Assert.That(room.HasStarted, Is.False);
            }
        }
        [Test] public void SingleHostCannotStartAndMissingDependenciesAreStillRejected()
        {
            using (var service = new RoomSessionService(Provider(), () => throw new Exception("不启动网络")))
            {
                var room = service.CreateRoomAsync("原房主", 4).GetAwaiter().GetResult();
                var missingEnterprise = service.ChangeConfigurationAsync(Change(room, "standard", "core", "infected")).GetAwaiter().GetResult();
                Assert.That(missingEnterprise.Success, Is.False); Assert.That(missingEnterprise.ReasonCode, Is.EqualTo("missing_enterprise_dependency"));
                var missingBoth = service.ChangeConfigurationAsync(Change(room, "entrepreneur", "core")).GetAwaiter().GetResult();
                Assert.That(missingBoth.Success, Is.False); Assert.That(missingBoth.ReasonCode, Is.EqualTo("missing_entrepreneur_dependency"));
                var start = service.StartConfiguredGameAsync(new StartConfiguredGameRequest {
                    CommandId=Guid.NewGuid().ToString("N"), RoomId=room.RoomId, RequestingPlayerId=room.LocalPlayerId,
                    ExpectedConfigurationRevision=room.Configuration.ConfigurationRevision }).GetAwaiter().GetResult();
                Assert.That(start.Success, Is.False); Assert.That(service.GetCurrentRoom().HasStarted, Is.False);
                Record("依赖与开局门槛仍拒绝", new { missingEnterprise = missingEnterprise.ReasonCode, missingBoth = missingBoth.ReasonCode, start = start.ReasonCode });
            }
        }
        [Test] public void DraftFingerprintAndInvalidCountCannotBypassValidation()
        {
            var provider = Provider(); var configuration = new GameRuleConfiguration { PlayerCount=1, MapId="map-four-players",
                RuleModeId="standard", EnabledExpansionIds=new List<string>{"core","enterprise","infected"} };
            configuration.ContentPackHash = provider.GetContentPackHash(configuration);
            Assert.That(configuration.ContentPackHash, Is.Not.Empty);
            Assert.That(provider.TryValidate(configuration, out var reason), Is.True, reason);
            configuration.ContentPackHash = "wrong-fingerprint";
            Assert.That(provider.TryValidate(configuration, out reason), Is.False); Assert.That(reason, Is.EqualTo("content_version_mismatch"));
            configuration.ContentPackHash = string.Empty; configuration.PlayerCount = -1;
            Assert.That(provider.TryValidate(configuration, out reason), Is.False); Assert.That(reason, Is.EqualTo("unsupported_player_count"));
        }
        [Test] public void ActualRoomSelectorHasNoImplementationGateAndBothExpansionSwitchesAreInteractive()
        {
            var roomType = Type.GetType("YC.Presentation.StartMenuRoomPanelView, Assembly-CSharp", true);
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
            var asset=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab");
            var root=UnityEngine.Object.Instantiate(asset);
            try
            {
                using (var service=new RoomSessionService(Provider(),()=>throw new Exception("不启动网络")))
                {
                    var room=service.CreateRoomAsync("原房主",4).GetAwaiter().GetResult();
                    var panel=root.GetComponentsInChildren(roomType,true).Single();
                    roomType.GetMethod("SetNetworkingState").Invoke(panel,new object[]{false,true});
                    roomType.GetMethod("SetLocalPlayerAvailability").Invoke(panel,new object[]{service.SupportsLocalPlayers});
                    roomType.GetMethod("Render").Invoke(panel,new object[]{room,room.LocalPlayerId});
                    var switches=((IEnumerable)roomType.GetField("expansions",flags).GetValue(panel)).Cast<object>().ToArray();
                    foreach(var option in switches)
                    {
                        Assert.That(option.GetType().GetField("Implemented"),Is.Null,"选项不再携带未实现锁标记");
                        var id=(string)option.GetType().GetField("Id").GetValue(option);
                        if(id=="core")continue;
                        var button=(Button)option.GetType().GetField("Button").GetValue(option);
                        Assert.That(button.interactable,Is.True,id);
                    }
                    Record("实际房间扩展开关无实现状态锁",new{room.PlayerCount,room.HostPlayerId,room.LocalPlayerId,extraExpansionCount=switches.Length-1,implementationGateRemoved=true});
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(root);}
        }
    }
}
