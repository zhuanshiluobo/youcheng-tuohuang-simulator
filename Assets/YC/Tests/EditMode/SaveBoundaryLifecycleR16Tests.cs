using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if !YC_WINDOW_EXECUTION_OFFLINE
using NUnit.Framework;
#endif
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 真实后台命令及确认接线的隔离回归；不计正常菜单/四端可见验收。
    public sealed partial class WindowLocalExecutionProtocolTests
    {
#if YC_WINDOW_EXECUTION_OFFLINE
        public static Action<MatchSaveData,WindowAutoSaveQueue,SimulationWindowSavePoint> OfflinePersistenceProbe;
#endif
#if !YC_WINDOW_EXECUTION_OFFLINE
        [TestCase(false,2)] [TestCase(true,2)] [TestCase(false,0)] [TestCase(true,0)]
#endif
        public void FormalWindowAutoSaveWaitsForExactConfirmationAndDeduplicates(bool hostOwner,int version)
        {
            using var group=new NetworkGroup("r16-save-"+hostOwner+"-v"+version,Payment,hostOwnsWindow:hostOwner,windowVersion:version);
            var queue=new WindowAutoSaveQueue();int notices=0;
            Require(group.AutomaticSavePoints.Count==0,"初始局没有正式窗口完成就触发自动保存");
            group.Host.AutomaticSavePointConfirmed+=p=>{notices++;queue.Enqueue(p);};
            group.Invoke("r16.invoke");group.AnswerAll("r16.answer.");
            group.Exchange(()=>group.Host.LastAppliedSequence==3&&group.Peer.LastAppliedSequence==3);
            Require(notices==0&&queue.Count==0,"同一根窗口的付款/选择/前缀推进触发了自动保存");
            group.HoldReady=true;group.End("r16.end");
            group.Exchange(()=>group.Host.LastAppliedSequence==4&&group.Peer.LastAppliedSequence==4);
            Require(notices==0&&queue.Count==0,"窗口结束但规定副本未确认就触发保存");
            group.HoldReady=false;group.Exchange(()=>queue.Count==1&&group.AllConfirmed(4));
            Require(notices==1&&queue.TryPeek(out var point)&&point.GlobalSequence==4,"真实根窗口关闭确认没有恰当触发一次");
            queue.TryPeek(out point);group.ReplayBoundaryReady(4);group.Exchange(()=>group.AllConfirmed(4));
            Require(notices==1&&queue.Count==1&&!queue.Enqueue(point),"重复确认/重复事件导致重复保存");
            // 不Complete模拟写盘失败；固定事件必须原样保留供下一次写盘重试。
            Require(queue.TryPeek(out var retry)&&ReferenceEquals(point,retry),"失败丢失或替换了固定保存点");
            group.EndOtherPlayer(2,"r16.next.end");group.Exchange(()=>group.Host.LastAppliedSequence>=5);
            var checkpoint=point.CreateCheckpointAsync().GetAwaiter().GetResult();
            Require(checkpoint.Baseline.ConfirmedGlobalSequence==4&&checkpoint.Archive.Snapshot.State.FindPlayer(1).Resources.PureOriginium==1,
                "后续窗口推进把原保存点替换成当前状态");
            var header=R16Header(checkpoint,point.LocalPlayerId);
            var data=MatchSaveData.CreateFromWindowSavePointAsync(header,point).GetAwaiter().GetResult();data.Validate();
            Require(SimulationConfirmedRecoveryInstaller.ReadBaseline(data.SimulationCheckpointJson).ConfirmedGlobalSequence==4&&data.Version==2,
                "正式存档未保留原确认边界/原格式");
#if YC_WINDOW_EXECUTION_OFFLINE
            OfflinePersistenceProbe?.Invoke(data,queue,point);
#endif
            queue.Complete(point.Id);Require(!queue.Enqueue(point),"成功写盘后的旧事件再次入队");
            group.Write("R16固定窗口保存点",new{hostOwner,version,notices,point.Id,point.GlobalSequence,point.ClosedWindowIds,actualSavedPrefix=4,
                livePrefix=group.Host.LastAppliedSequence,checkpoint});group.CheckNoFault();
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void RestoredWindowsSaveNextCompletionAndFinalRoundSavesConfirmedTerminal()
        {
            SimulationRecoveryCheckpointDto checkpoint;
            using(var first=new NetworkGroup("r16-restore-source",Payment))
            {
                first.Invoke("r16.restore.invoke");first.AnswerAll("r16.restore.answer.");first.End("r16.restore.end");
                first.Exchange(()=>first.AutomaticSavePoints.Count==1&&first.AllConfirmed(4));
                checkpoint=first.AutomaticSavePoints[0].CreateCheckpointAsync().GetAwaiter().GetResult();
            }
            using(var restored=new NetworkGroup("r16-restored",Payment,restore:checkpoint))
            {
                Require(restored.AutomaticSavePoints.Count==0,"恢复安装重发了已保存的窗口完成");
                restored.EndOtherPlayer(2,"r16.restored.p2.end");restored.Exchange(()=>restored.AutomaticSavePoints.Count==1&&restored.AllConfirmed(5));
                Require(restored.AutomaticSavePoints[0].SimulationEpoch==2&&restored.AutomaticSavePoints[0].GlobalSequence==5,"恢复后没有按新epoch的下一个窗口结束保存");
                restored.AutomaticSavePoints[0].CreateCheckpointAsync().GetAwaiter().GetResult();restored.CheckNoFault();
            }
            using(var terminal=new NetworkGroup("r16-final-round",finalRound:true))
            {
                terminal.End("r16.terminal.p1");terminal.Exchange(()=>terminal.AllConfirmed(1));
                terminal.EndOtherPlayer(2,"r16.terminal.p2");terminal.Exchange(()=>terminal.AllConfirmed(2));
                terminal.HoldReady=true;terminal.EndOtherPlayer(3,"r16.terminal.p3");
                Require(terminal.AutomaticSavePoints.Count==2,"终局尚未全体确认就触发终局保存");
                terminal.HoldReady=false;terminal.Exchange(()=>terminal.AutomaticSavePoints.Count==3&&terminal.AllConfirmed(3));
                var point=terminal.AutomaticSavePoints.Last();var saved=point.CreateCheckpointAsync().GetAwaiter().GetResult();
                Require(point.IsTerminal&&saved.Archive.Snapshot.State.FinalScoring.IsResolved&&terminal.Host.TryReadPublishedSnapshot(out var s)&&s.IsCurrentBoundaryConfirmed,
                    "终局档案未与原完整确认边界一致");
                terminal.Write("R16终局固定保存点",saved);terminal.CheckNoFault();
            }
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void ForeignRequiredAnswerAndGrantHandoffsDoNotFinishFormalWindow()
        {
            const string body="return function(ctx) return {Effect.Choice({player='P2',options={'other'},minSelections=1,maxSelections=1}),Effect.GainResource({player=ctx.playerId,resourceType='pure_originium',amount=1})} end";
            using var group=new NetworkGroup("r16-subrequest",body);var points=new List<SimulationWindowSavePoint>();
            group.Host.AutomaticSavePointConfirmed+=points.Add;
            group.Invoke("r16.foreign.invoke");
            group.Exchange(()=>group.Host.ValidatedThroughSequence==1&&group.Peer.ReadWindowPermissions().Any(w=>w.CanSubmitForLocalPlayer));
            Require(points.Count==0,"根窗的临时grant封尾被当作窗口结束");
            group.SubmitForeignAnswer();
            group.Exchange(()=>group.Actor.TryReadPublishedSnapshot(out var s)&&s.WindowExecution?.IsActive==true&&s.WindowExecution.OwnerPlayerId==1&&group.Actor.ValidatedThroughSequence==2);
            Require(points.Count==0,"必要子选择关闭/重新grant被当作根窗口结束");
            group.End("r16.foreign.end");group.Exchange(()=>points.Count==1&&group.AllConfirmed(3));
            Require(points[0].GlobalSequence==3,"根窗口真正结束后没有保存准确边界");
            points[0].CreateCheckpointAsync().GetAwaiter().GetResult();group.Write("R16子选择与grant交接",points.Select(p=>new{p.Id,p.GlobalSequence,p.ClosedWindowIds}));group.CheckNoFault();
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [TestCase(0)] [TestCase(2)]
#endif
        public void MultipleWritersShareOneFormalCollectionSaveBoundary(int version)
        {
            using var group=new NetworkGroup("r16-multiple-writers-v"+version,Payment,windowVersion:version,concurrent:true);
            var points=new List<SimulationWindowSavePoint>();group.Host.AutomaticSavePointConfirmed+=points.Add;
            group.CommitCollection(2,"r16.collection.p2");group.CommitCollection(3,"r16.collection.p3");
            Require(points.Count==0,"多writer中单个玩家任务完成被当作正式采集窗口关闭");
            group.HoldReady=true;group.CommitCollection(1,"r16.collection.p1");
            Require(points.Count==0,"共同采集窗口结束未经规定副本确认就保存");
            group.HoldReady=false;group.Exchange(()=>points.Count==1&&group.AllConfirmed(3));
            Require(points[0].GlobalSequence==3,"多writer共同窗口没有在实际完成边界保存");
            foreach(var point in points)
                Require(point.CreateCheckpointAsync().GetAwaiter().GetResult().Baseline.ConfirmedGlobalSequence==point.GlobalSequence,"旧边界捕获读取了后续状态");
            group.Write("R16多writer旧边界",points.Select(p=>new{p.Id,p.GlobalSequence,p.ClosedWindowIds}));group.CheckNoFault();
        }

        private static MatchSaveData R16Header(SimulationRecoveryCheckpointDto point,int local)
            => new MatchSaveData{Mode=LaunchMode.Host,GameId=point.Archive.Snapshot.State.GameId,LocalPlayerId=local,HostSteamId=(ulong)(100+local),
                Seats=point.Archive.Snapshot.State.Players.Select(p=>new MatchSaveSeat{PlayerId=p.PlayerId,Color=p.Color,PlayerName=p.Name,
                    SteamId=(ulong)(100+p.PlayerId),OperatorId="r16.operator."+p.PlayerId}).ToList()};

#if !YC_WINDOW_EXECUTION_OFFLINE
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator HotseatLeafAndPublicationRejectCaptureThenDeferredCaptureResumes()
            => R16AwaitNative(HotseatLeafAndPublicationRejectCaptureThenDeferredCaptureResumesAsync());
        private async Task HotseatLeafAndPublicationRejectCaptureThenDeferredCaptureResumesAsync()
#else
        public async Task HotseatLeafAndPublicationRejectCaptureThenDeferredCaptureResumes()
#endif
        {
            var fixture=R16LocalFixture("r16-local-leaf");var session=new GameSession(fixture.State);
            var old=session.EnableConfirmedRecoveryCapture();int leaf=0,rejections=0,publications=0;bool publicationProtected=false;
            Task<GameSessionRecoveryExport> pending=null;
            fixture.Registry.Register(new EffectRegistration("qy008.r16.leaf",ctx=>{
                leaf++;
                Require(session.ReadSaveCaptureSafety()==SaveCaptureSafety.Executing,"叶子effect执行中被判为稳定");
                rejections+=R16Reject(()=>session.CaptureStableSavePoint());
                rejections+=R16Reject(()=>session.EnableConfirmedRecoveryCapture());
                rejections+=R16Reject(()=>session.ExportConfirmedRecoveryPoint(old));
                rejections+=R16Reject(()=>session.CreateHostArchive());
                pending=session.CaptureStableSavePointAsync();Require(!pending.IsCompleted,"异步保存没有等待叶子退出");
                ctx.State.FindPlayer(1).Resources.Originium++;return EffectStepResult.Completed();
            },EffectExecutorKind.Atomic));
            var engine=fixture.Rounds.CreateCommandExecutor(session.State);engine.CreateRoot(EffectSpec.Create("qy008.r16.leaf",playerId:1));
            Require(session.ReadSaveCaptureSafety()==SaveCaptureSafety.PendingProgress,"尚可执行的末端节点没有阻止保存");
            Require(R16Reject(()=>session.CaptureStableSavePoint())==1,"待推进effect被捕获");
            Require(!engine.RunUntilQuiescent().Faulted,"隔离叶子结算失败");
            var frozen=await pending;Require(leaf==1&&rejections==4&&frozen.Archive.Snapshot.State.FindPlayer(1).Resources.Originium==5,"延后捕获丢失末端完成状态");
            session.RegisterHandler(new InvokeSettlementOptionCommandHandler(fixture.Registry));
            session.StateCommitted+=()=>{publications++;publicationProtected=session.ReadSaveCaptureSafety()==SaveCaptureSafety.Executing&&R16Reject(()=>session.CaptureStableSavePoint())==1;};
            var option=fixture.Registry.GetSettlementOptions(session.State,1).Single(o=>o.SourceItemId==fixture.CardId&&o.CleanupRequest!=null);
            var result=session.Submit(CleanupSourceCommand.Create(1,"r16.local.publish",option.CleanupRequest,session.State.EffectRuntime.StateRevision));
            Require(result.Succeeded&&publications==1&&publicationProtected,"真实事务发布未完成或发布调用栈允许捕获:"+result.Validation?.Reason);
            Require(session.ReadSaveCaptureSafety()==SaveCaptureSafety.Safe,"合法待选择没有恢复安全捕获");
            var stable=session.CaptureStableSavePoint();
            fixture.Artifacts.WriteLabeledText("R16叶子与事务保存门禁",Newtonsoft.Json.JsonConvert.SerializeObject(new{leaf,rejections,publications,stable}));
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void HotseatStablePaidChoiceRestoresContinuationWithoutRepeatedPayment()
        {
            var fixture=R16LocalFixture("r16-local-choice");var session=new GameSession(fixture.State);
            session.RegisterHandler(new InvokeSettlementOptionCommandHandler(fixture.Registry));
            session.RegisterHandler(new AnswerInteractionCommandHandler(fixture.Registry,fixture.Rounds));
            var option=fixture.Registry.GetSettlementOptions(session.State,1).Single(o=>o.SourceItemId==fixture.CardId&&o.CleanupRequest!=null);
            Require(session.Submit(CleanupSourceCommand.Create(1,"r16.local.invoke",option.CleanupRequest,session.State.EffectRuntime.StateRevision)).Succeeded,"隔离真实付款来源未执行");
            R16Answer(session,"r16.local.pay");
            Require(session.State.FindPlayer(1).Resources.Originium==2&&session.State.FindPlayer(1).Resources.PureOriginium==0,"未停在真实付款后的选择");
            var originalContinuation=session.ExportConfirmedRecoveryPoint(session.EnableConfirmedRecoveryCapture()).Continuation;
            var saved=session.CaptureStableSavePoint();
            Require(saved.Archive.Snapshot.State.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open")&&
                SimulationConfirmedRecoveryInstaller.SerializeContinuation(saved.Continuation)==SimulationConfirmedRecoveryInstaller.SerializeContinuation(originalContinuation),
                "稳定待选未保留原请求和日志续接");
            var recovered=HostRecoveryService.Recover(saved.Archive,fixture.Registry);
            Require(recovered.Succeeded,"固定待选档案未通过正式恢复:"+recovered.Diagnostic);
            var restored=new GameSession(recovered.State);
            restored.RegisterHandler(new InvokeSettlementOptionCommandHandler(fixture.Registry));restored.RegisterHandler(new AnswerInteractionCommandHandler(fixture.Registry,fixture.Rounds));
            Require(restored.TryPrepareLocalRecoveryState(recovered.State,saved.Continuation,out var prepared,out var fault),fault);
            Require(restored.TryCommitPreparedRecovery(prepared,out fault),fault);
            R16Answer(restored,"r16.local.restored.choice");
            Require(restored.State.FindPlayer(1).Resources.Originium==2&&restored.State.FindPlayer(1).Resources.PureOriginium==1,
                "恢复后重复付款或丢失/重复收益");
            restored.CaptureStableSavePoint();fixture.Artifacts.WriteLabeledText("R16待选续接恢复",Newtonsoft.Json.JsonConvert.SerializeObject(new{saved,resources=restored.State.FindPlayer(1).Resources}));
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator HotseatFaultAndCancellationPreservePreviousHealthyCapture()
            => R16AwaitNative(HotseatFaultAndCancellationPreservePreviousHealthyCaptureAsync());
        private async Task HotseatFaultAndCancellationPreservePreviousHealthyCaptureAsync()
#else
        public async Task HotseatFaultAndCancellationPreservePreviousHealthyCapture()
#endif
        {
            var fixture=R16LocalFixture("r16-local-cancel");var session=new GameSession(fixture.State);var healthy=session.CaptureStableSavePoint();
            var engine=fixture.Rounds.CreateCommandExecutor(session.State);
            fixture.Registry.Register(new EffectRegistration("qy008.r16.wait",ctx=>EffectStepResult.Completed(),EffectExecutorKind.Atomic));
            engine.CreateRoot(EffectSpec.Create("qy008.r16.wait",playerId:1));
            using(var cancel=new CancellationTokenSource())
            {var task=session.CaptureStableSavePointAsync(cancel.Token);cancel.Cancel();bool cancelled=false;try{await task;}catch(OperationCanceledException){cancelled=true;}Require(cancelled,"待保存取消未生效");}
            Require(!engine.RunUntilQuiescent().Faulted,"隔离effect失败");
            session.State.EffectRuntime.Status=EffectRuntimeStatus.PausedFault;
            Require(R16Reject(()=>session.CaptureStableSavePoint())==1,"故障态覆盖了健康保存点");
            bool rejected=false;try{await session.CaptureStableSavePointAsync();}catch(InvalidOperationException){rejected=true;}
            Require(rejected&&healthy.Archive.Snapshot.State.EffectRuntime.Status==EffectRuntimeStatus.Active,"故障延后保存未明确拒绝或污染旧档");
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        private static readonly string[] R16NativeSaveEntries = { "manual", "automatic", "leave" };
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator NativeHotseatSaveEntrypointsDeferLeafCapture([ValueSource(nameof(R16NativeSaveEntries))] string entry)
            => R16AwaitNative(NativeHotseatSaveEntrypointsDeferLeafCaptureAsync(entry));
        private async Task NativeHotseatSaveEntrypointsDeferLeafCaptureAsync(string entry)
        {
            // 本用例只写IsolatedTestArtifacts专属目录；不访问正式存档，不打开任何界面。
            var fixture=R16LocalFixture("r16-native-"+entry);var session=new GameSession(fixture.State);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
            var controllerType=Type.GetType("YC.Presentation.MatchSaveController, Assembly-CSharp",true);
            var launchType=Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp",true);
            Require(controllerType.GetProperty("Instance").GetValue(null)==null&&launchType.GetProperty("Instance").GetValue(null)==null,"本原生隔离用例要求无活动对局及保存控制器，不能接管已有会话");
            var saveObject=new UnityEngine.GameObject("R16隔离保存控制器");var launchObject=new UnityEngine.GameObject("R16隔离启动上下文");
            try
            {
                var launch=launchObject.AddComponent(launchType);launchType.GetField("Mode").SetValue(launch,LaunchMode.Local);
                launchType.GetField("LocalPlayerId").SetValue(launch,1);launchType.GetField("GameId").SetValue(launch,session.State.GameId);
                launchType.GetField("Players").SetValue(launch,session.State.Players.Select(p=>new PlayerSeat{PlayerId=p.PlayerId,Color=p.Color,PlayerName=p.Name}).ToList());
                var controller=saveObject.AddComponent(controllerType);var storeType=Type.GetType("YC.Infrastructure.Persistence.MatchSaveStore, Assembly-CSharp",true);
                string target=System.IO.Path.Combine(fixture.Artifacts.ResultsDirectory,"R16隔离存档");
                var store=Activator.CreateInstance(storeType,new object[]{target});controllerType.GetField("store",flags).SetValue(controller,store);
                controllerType.GetMethod("Bind").Invoke(controller,new[]{(object)session,launch});
                Task<bool> manual=null;bool left=false,insideProtected=false;
                fixture.Registry.Register(new EffectRegistration("qy008.r16.native.leaf",ctx=>{
                    if(entry=="manual")manual=(Task<bool>)controllerType.GetMethod("Save").Invoke(controller,new object[]{0});
                    else if(entry=="automatic")controllerType.GetMethod("LateUpdate",flags).Invoke(controller,null);
                    else controllerType.GetMethod("RequestLeave").Invoke(controller,new object[]{(Action)(()=>left=true)});
                    insideProtected=!(bool)storeType.GetMethod("HasSaves").Invoke(store,null)&&!left&&(manual==null||!manual.IsCompleted);
                    ctx.State.FindPlayer(1).Resources.Originium++;return EffectStepResult.Completed();
                },EffectExecutorKind.Atomic));
                var engine=fixture.Rounds.CreateCommandExecutor(session.State);engine.CreateRoot(EffectSpec.Create("qy008.r16.native.leaf",playerId:1));
                Require(!engine.RunUntilQuiescent().Faulted&&insideProtected,"真实保存入口在末端effect内部捕获/完成或结算失败");
                if(manual!=null)Require(await manual,"手动延后保存失败");
                else
                {
                    var timer=System.Diagnostics.Stopwatch.StartNew();
                    while(entry=="leave"?!left:(bool)controllerType.GetField("saving",flags).GetValue(controller))
                    {Require(timer.Elapsed.TotalSeconds<15,"自动/退出延后保存未完成");await Task.Yield();}
                }
                // 2026-10-26用户新要求：返回主菜单不补存；只等待当前结算调用栈结束后退出。
                if(entry=="leave")
                {
                    Require(left&&!(bool)storeType.GetMethod("HasSaves").Invoke(store,null)&&session.State.FindPlayer(1).Resources.Originium==5,
                        "返回主菜单发起保存、未等当前结算完成或未执行退出回调");
                    fixture.Artifacts.WriteLabeledText("R18退出不补存",Newtonsoft.Json.JsonConvert.SerializeObject(new{entry,insideProtected,left,target,written=false}));
                    return;
                }
                Require((bool)storeType.GetMethod("HasSaves").Invoke(store,null),"安全停点后未实际写盘");
                var listTask=(Task)storeType.GetMethod("ListAsync").Invoke(store,null);await listTask;
                var slots=(System.Collections.IEnumerable)listTask.GetType().GetProperty("Result").GetValue(listTask);
                MatchSaveData written=null;
                foreach(var slot in slots)if((bool)slot.GetType().GetField("Exists").GetValue(slot))written=(MatchSaveData)slot.GetType().GetField("Data").GetValue(slot);
                Require(written!=null&&(int)written.ReadArchiveState()["Players"][0]["Resources"]["Originium"]==5,"落盘没有捕获effect完整完成后的状态");
                fixture.Artifacts.WriteLabeledText("R16原生保存入口",Newtonsoft.Json.JsonConvert.SerializeObject(new{entry,insideProtected,left,target,written}));
            }
            finally {UnityEngine.Object.DestroyImmediate(saveObject);UnityEngine.Object.DestroyImmediate(launchObject);}
        }

        private static System.Collections.IEnumerator R16AwaitNative(Task task)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                Require(timer.Elapsed.TotalSeconds < 60, "原生隔离保存检查未在限定时间完成");
                yield return null;
            }
            task.GetAwaiter().GetResult();
        }
#endif

        private static int R16Reject(Action action)
        {try{action();return 0;}catch(InvalidOperationException e)when(e.Message.StartsWith("save_capture_",StringComparison.Ordinal)){return 1;}}
        private static void R16Answer(GameSession session,string id)
        {
            var r=session.State.EffectRuntime.InteractionRequests.Last(q=>q.Status=="open"&&q.AnsweringPlayerId==1);
            var command=new GameCommand{CommandId=id,PlayerId=1,Kind=GameCommandKind.AnswerInteraction,
                Parameters=new Dictionary<string,string>{["interactionId"]=r.GetStableInteractionId(),["expectedRevision"]=r.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
            if(r.AnswerSchema=="boolean_accept")command.Parameters["answerValue"]="true";
            else command.OptionIds.Add(r.CandidateIds.Contains("resource")?"resource":r.CandidateIds.Contains("continue")?"continue":r.CandidateIds.First());
            var result=session.Submit(command);Require(result.Succeeded,"真实待选回答失败:"+result.Validation?.Reason);
        }
        private static CleanupSourceTestFixture R16LocalFixture(string label)
        {
#if YC_WINDOW_EXECUTION_OFFLINE
            var artifacts=new IsolatedTestArtifacts(System.IO.Path.Combine(OfflineOutput,label));
#else
            var artifacts=IsolatedTestArtifacts.BeginCase();(currentArtifacts??=new List<IsolatedTestArtifacts>()).Add(artifacts);
#endif
            var f=new CleanupSourceTestFixture(artifacts,bodyOverride:Payment,inMemoryInput:true);f.State.RandomRuntime??=GameplayRandomRuntime.Create(f.State.GameId);
            foreach(var p in f.State.Players)p.Color=(PlayerColor)(p.PlayerId-1);return f;
        }
    }
}
