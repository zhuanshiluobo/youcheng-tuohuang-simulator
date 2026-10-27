using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    // 原房间View+实际成员初始化和正式配置消息通道；不启动Steam/联网，不计双账号实机验收。
    public sealed class SteamMemberReadyR41Tests
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        GameObject root;
        object panel;
        Type panelType;
        RoomSessionService local;
        RoomConfigurationService authority;
        RoomState room;
        IRoomContentCapabilityProvider content;
        Button Ready=>(Button)panelType.GetField("startButton",Flags).GetValue(panel);
        PlayerSeat Guest=>RoomConfigurationService.FindSlot(room,2).Occupant;
        [SetUp] public void Prepare()
        {
            var provider=Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp",true);
            content=(IRoomContentCapabilityProvider)provider.GetProperty("ContentCapabilities").GetValue(null);
            local=new RoomSessionService(content,()=>throw new InvalidOperationException("本检查不启动网络"));
            room=local.CreateRoomAsync("房主",3).GetAwaiter().GetResult();
            authority=new RoomConfigurationService(content,localPlayers:false);
            // 与SteamRoomService.AssignStableSeats相同：新成员尚未声明内容和规则版本。
            Assert.That(authority.TryOccupySeat(room,new PlayerSeat{PlayerId=2,SteamId=202,LobbyMemberPresent=true,PlayerName="非房主"},out var reason),Is.True,reason);
            Guest.IsReady=Guest.TransportConnected=Guest.IdentityVerified=true;
            Assert.That(Guest.RuleContractVersion,Is.Empty);Assert.That(Guest.ContentPackHash,Is.Empty);
            root=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab"));
            panelType=Type.GetType("YC.Presentation.StartMenuRoomPanelView, Assembly-CSharp",true);
            panel=root.GetComponentsInChildren(panelType,true).Single();
            panelType.GetMethod("SetNetworkingState").Invoke(panel,new object[]{true,true});
            panelType.GetMethod("SetLocalPlayerAvailability").Invoke(panel,new object[]{false});
        }
        void Render()=>panelType.GetMethod("Render").Invoke(panel,new object[]{room.Clone(),2});
        SetRoomReadyRequest Request(bool ready)=>new SetRoomReadyRequest{CommandId=Guid.NewGuid().ToString("N"),RoomId=room.RoomId,RequestingPlayerId=2,ExpectedConfigurationRevision=room.Configuration.ConfigurationRevision,IsUserReady=ready};
        [Test] public void FreshSteamMemberCanPrepareAndCancelThroughOriginalButton()
        {
            int calls=0;
            Action<bool> send=ready=>{
                calls++;
                var message=RoomConfigurationMessage.Create(Request(ready),content,room.Configuration);
                var result=message.Execute(room,2,authority);
                Assert.That(result.Success,Is.True,result.ReasonCode);
                Render();
            };
            panelType.GetEvent("ReadyRequested").AddEventHandler(panel,send);
            Render();Assert.That(Ready.interactable,Is.True,"首次准备不能要求尚未提交的成员确认版本");
            Ready.onClick.Invoke();Assert.That(calls,Is.EqualTo(1));
            var seat=RoomConfigurationService.FindSlot(room,2);
            Assert.That(seat.IsUserReady,Is.True);Assert.That(seat.ReadyConfigurationRevision,Is.EqualTo(room.Configuration.ConfigurationRevision));
            Assert.That(Guest.ContentPackHash,Is.EqualTo(room.Configuration.ContentPackHash));
            Assert.That(Guest.RuleContractVersion,Is.EqualTo(GameRuleConfiguration.CurrentRuleContractVersion));
            Assert.That(Ready.interactable,Is.True);Ready.onClick.Invoke();
            Assert.That(calls,Is.EqualTo(2));Assert.That(seat.IsUserReady,Is.False);
        }
        [Test] public void OldMemberAcknowledgementDoesNotPreventPreparingCurrentCompatibleConfiguration()
        {
            Guest.ContentPackHash="上次配置的指纹";Guest.RuleContractVersion="上次确认的规则版本";
            Render();Assert.That(Ready.interactable,Is.True);
            var result=RoomConfigurationMessage.Create(Request(true),content,room.Configuration).Execute(room,2,authority);
            Assert.That(result.Success,Is.True,result.ReasonCode);Assert.That(Guest.RuleContractVersion,Is.EqualTo(room.Configuration.RuleContractVersion));
        }
        [TestCase("not_ready")][TestCase("transport")][TestCase("identity")]
        [TestCase("content")][TestCase("contract")][TestCase("pending")]
        public void IncompatibleOrUnverifiedMemberStillCannotPrepare(string invalid)
        {
            if(invalid=="not_ready")Guest.IsReady=false;
            if(invalid=="transport")Guest.TransportConnected=false;
            if(invalid=="identity")Guest.IdentityVerified=false;
            if(invalid=="content")room.Configuration.ContentPackHash="不匹配的房间内容指纹";
            if(invalid=="contract")room.Configuration.RuleContractVersion="unsupported-contract";
            if(invalid=="pending")panelType.GetMethod("SetRequestPending").Invoke(panel,new object[]{true});
            Render();Assert.That(Ready.interactable,Is.False,invalid);
        }
        [TestCase("content")][TestCase("contract")][TestCase("identity")][TestCase("player")]
        public void HostStillRejectsUntrustedReadyMessageWithoutAcknowledgingOrPreparing(string invalid)
        {
            var message=RoomConfigurationMessage.Create(Request(true),content,room.Configuration);
            if(invalid=="content")message.ContentPackHash="错误内容指纹";
            if(invalid=="contract")message.RuleContractVersion="wrong-contract";
            if(invalid=="identity")Guest.IdentityVerified=false;
            var result=message.Execute(room,invalid=="player"?room.HostPlayerId:2,authority);
            Assert.That(result.Success,Is.False);Assert.That(RoomConfigurationService.FindSlot(room,2).IsUserReady,Is.False);
            Assert.That(Guest.ContentPackHash,Is.Empty);Assert.That(Guest.RuleContractVersion,Is.Empty);
        }
        [TearDown] public void Cleanup(){if(root!=null)UnityEngine.Object.DestroyImmediate(root);local?.Dispose();}
    }
}
