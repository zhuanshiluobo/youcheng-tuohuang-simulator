using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class ActivePresentationUiTests
    {
        private const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        private static Type T(string n)=>Type.GetType("YC.Presentation."+n+", Assembly-CSharp",true);
        private static object Read(object o,string n)=>o.GetType().GetField(n,F)?.GetValue(o)??o.GetType().GetProperty(n,F)?.GetValue(o);
        private static object Call(object o,string n,params object[] a)=>o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);
        private static MatchSaveData Save(GameSession session)
        {
            var point=session.ExportConfirmedRecoveryPoint(session.EnableConfirmedRecoveryCapture());var state=point.Archive.Snapshot.State;
            return new MatchSaveData {SavedUtcTicks=DateTime.UtcNow.Ticks,Mode=LaunchMode.Local,LocalPlayerId=1,HostSteamId=101,
                MapId=state.MapId,GameId=state.GameId,Round=state.Round,ContentHash=state.ContentPackHash,
                ArchiveJson=HostRecoveryService.SerializeArchive(point.Archive),
                ContinuationJson=SimulationConfirmedRecoveryInstaller.SerializeContinuation(point.Continuation),
                Seats=state.Players.Select(p=>new MatchSaveSeat {PlayerId=p.PlayerId,Color=p.Color,PlayerName=p.Name,
                    SteamId=(ulong)(100+p.PlayerId),OperatorId="operator-"+p.PlayerId}).ToList()};
        }
        [TestCase(LaunchMode.Host)] [TestCase(LaunchMode.Client)]
        public void FormalNetworkAssemblyPublishesMatchingReadOnlyPresentation(LaunchMode mode)
        {
            var artifacts=ExpansionRules.IsolatedTestArtifacts.BeginCase();using var catalogs=new SceneRuntimeCatalogScope();
            var instance=T("GameLaunchContext").GetProperty("Instance",F);var previous=instance.GetValue(null);
            instance.GetSetMethod(true).Invoke(null,new object[]{null});GameObject launchRoot=null;
            try
            {
                launchRoot=new GameObject("活动展示联机装配隔离上下文");var launch=launchRoot.AddComponent(T("GameLaunchContext"));
                string gameId="active-presentation-"+Guid.NewGuid().ToString("N");
                var config=new GameRuleConfiguration {PlayerCount=2,MapId="map-three-players",RuleModeId="entrepreneur",
                    EnabledExpansionIds=new System.Collections.Generic.List<string>{"core","enterprise","infected"}};
                T("GameLaunchContext").GetMethods(F).Single(m=>m.Name=="Configure"&&m.GetParameters().Length==9).Invoke(launch,
                    new object[]{mode,1,"活动展示隔离房间",new System.Collections.Generic.List<PlayerSeat>{
                        new PlayerSeat{PlayerId=1,PlayerName="隔离甲",Color=(PlayerColor)0},new PlayerSeat{PlayerId=2,PlayerName="隔离乙",Color=(PlayerColor)1}},
                        config,new[]{1,2},gameId,42,GameplayRandomRuntime.Create(gameId)});
                var built=T("GameSessionBootstrapper").GetMethod("BuildForGameplay",F).Invoke(null,new object[]{launch,false,false});
                var session=(GameSession)Read(built,"Session");var binding=Read(built,"ActivePresentation");
                var view=GameStateViewProjector.ProjectForPlayer(session.State,1);
                Assert.That(binding,Is.Not.Null);
                Assert.That(Call(binding,"Matches",session,view,ExternalContentPack.Current.ContentHash),Is.EqualTo(true));
                var content=(IActiveContentPresentation)Read(binding,"Content");
                Assert.That(content.ContentPackHash,Is.EqualTo(session.State.ContentPackHash));
                Assert.That(session.State.RuleConfiguration.PlayerStageSchedulingMode,Is.EqualTo(PlayerStageSchedulingMode.Concurrent));
                artifacts.WriteLabeledText("HostClient正式只读装配身份",JsonConvert.SerializeObject(new {
                    mode,view.GameId,view.ContentPackHash,view.RuleConfiguration,content.InstalledContentHash,content.PresentationContentHash,
                    scope="正式BuildForGameplay入口；Client保留延后规则初始化，仅验证装配绑定，未冒网络检查点/正常局验收"}));
            }
            finally {if(launchRoot!=null)UnityEngine.Object.DestroyImmediate(launchRoot);
                instance.GetSetMethod(true).Invoke(null,new[]{previous});artifacts.Finish();}
        }
        [Test]public void FormalRestoreRebindsAndWarmDetailsStillCheckCurrentPrivateCandidates()
        {
            var artifacts=ExpansionRules.IsolatedTestArtifacts.BeginCase();using var catalogs=new SceneRuntimeCatalogScope();
            var instance=T("GameLaunchContext").GetProperty("Instance",F);var previous=instance.GetValue(null);
            instance.GetSetMethod(true).Invoke(null,new object[]{null});
            GameObject launchRoot=null,hud=null;
            try
            {
                launchRoot=new GameObject("活动展示正式恢复隔离上下文");var launch=launchRoot.AddComponent(T("GameLaunchContext"));
                var config=new GameRuleConfiguration {PlayerCount=2,MapId="map-three-players",RuleModeId="entrepreneur",
                    EnabledExpansionIds=new System.Collections.Generic.List<string>{"core","enterprise","infected"}};
                T("GameLaunchContext").GetMethod("ConfigureNewLocal").Invoke(launch,new object[]{1,"",new System.Collections.Generic.List<PlayerSeat>{
                    new PlayerSeat{PlayerId=1,PlayerName="隔离甲",Color=(PlayerColor)0},new PlayerSeat{PlayerId=2,PlayerName="隔离乙",Color=(PlayerColor)1}},config,null,null});
                var built=T("GameSessionBootstrapper").GetMethod("Build",new[]{T("GameLaunchContext"),typeof(bool),typeof(bool)}).Invoke(null,new object[]{launch,false,false});
                var live=(GameSession)Read(built,"Session");int viewer=1;
                GameStateView Visible()=>GameStateViewProjector.ProjectForPlayer(live.State,viewer);
                var theme=AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset",T("UiThemeCatalog"));T("UiTheme").GetMethod("Initialize").Invoke(null,new[]{theme});
                hud=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
                var frame=Read(hud.GetComponent(T("GameplayInteractionHudView")),"Frame");Call(frame,"Awake");
                Call(frame,"ConfigureGameplayPresentation",Read(built,"GameplayTextCatalog"),Read(built,"GameplayDefinitionNames"));
                var adapter=hud.GetComponent(T("GameplayExpansionUi"));
                Call(adapter,"Configure",new Func<GameStateView>(Visible),new Func<int>(()=>viewer),Read(built,"EnterpriseDefinitions"),Read(built,"CharacterModes"),
                    new Action<YC.Domain.Commands.GameCommand>(_=>Assert.Fail("只读详情不能提交")));
                Call(adapter,"BindSessionPresentation",Read(built,"ActivePresentation"),new Func<GameSession>(()=>live));
                InteractionRequestProjection Request(GameStateView v)=>InteractionRequestProjector.ProjectForPlayer(
                    YC.Presentation.Workflows.VisibleInteractionRequestSource.Read(null,v,viewer).Single(r=>r.Status=="open"&&r.AnsweringPlayerId==viewer&&r.InteractionTypeId==EntrepreneurFlow.PreviewInteraction),viewer);
                var initial=Visible();var initialRequest=Request(initial);var candidate=initial.Entrepreneurs.OwnEnterpriseCandidates.First();
                var board=(YC.Presentation.Workflows.EnterpriseBoardProjection)Call(adapter,"ProjectCandidateBoard",initial,candidate.ItemId,candidate.DefinitionId,initialRequest);
                Assert.That(board.Details,Is.Not.Empty);string original=JsonConvert.SerializeObject(board.Details);board.Details[0].Key="caller_modified";
                var again=(YC.Presentation.Workflows.EnterpriseBoardProjection)Call(adapter,"ProjectCandidateBoard",initial,candidate.ItemId,candidate.DefinitionId,Request(initial));
                Assert.That(JsonConvert.SerializeObject(again.Details),Is.EqualTo(original));
                viewer=2;var other=Visible();
                // 本地顺序准备时玩家2没有当前预览请求；用原玩家1请求核换人后拒绝，不能假设另一个正式请求已存在。
                var denied=(YC.Presentation.Workflows.EnterpriseBoardProjection)Call(adapter,"ProjectCandidateBoard",other,candidate.ItemId,candidate.DefinitionId,initialRequest);
                Assert.That(denied.Details,Is.Empty,"同revision换玩家不能沿用本人私有候选详情");viewer=1;
                var data=Save(live);
                artifacts.WriteLabeledText("正式恢复原归档",data.ArchiveJson);
                var restored=T("GameSessionBootstrapper").GetMethod("Restore").Invoke(null,new object[]{launch,data});
                live=(GameSession)Read(restored,"Session");var current=Visible();
                var oldBinding=(YC.Presentation.Workflows.EnterpriseBoardProjection)Call(adapter,"ProjectCandidateBoard",current,candidate.ItemId,candidate.DefinitionId,Request(current));
                Assert.That(oldBinding.Details,Is.Empty,"新会话必须显式重绑");
                Call(adapter,"RebindSessionPresentation",current);
                var rebound=(YC.Presentation.Workflows.EnterpriseBoardProjection)Call(adapter,"ProjectCandidateBoard",current,candidate.ItemId,candidate.DefinitionId,Request(current));
                Assert.That(JsonConvert.SerializeObject(rebound.Details),Is.EqualTo(original));
                artifacts.WriteLabeledText("活动展示恢复与私有候选检查",JsonConvert.SerializeObject(new {live.State.GameId,live.State.ContentPackHash,candidate.ItemId,original}));
                Call(adapter,"BeginShutdown");
            }
            finally {if(hud!=null)UnityEngine.Object.DestroyImmediate(hud);if(launchRoot!=null)UnityEngine.Object.DestroyImmediate(launchRoot);
                instance.GetSetMethod(true).Invoke(null,new[]{previous});artifacts.Finish();}
        }
    }
}
