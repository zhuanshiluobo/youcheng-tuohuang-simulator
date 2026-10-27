using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
#if !YC_WINDOW_EXECUTION_OFFLINE
using NUnit.Framework;
#endif
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Tests.EditMode.ExpansionRules;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    // 同一测试源既可由原生EditMode运行，也由本轮非Unity工具直接调用；后者不计原生通过。
    public sealed partial class WindowLocalExecutionProtocolTests
    {
        public static string OfflineOutput;
#if !YC_WINDOW_EXECUTION_OFFLINE
        [ThreadStatic] private static List<IsolatedTestArtifacts> currentArtifacts;
        [TearDown] public void FinishNativeEvidence()
        { if(currentArtifacts==null)return;foreach(var a in currentArtifacts)a.Finish();currentArtifacts=null; }
        [Test]
#endif
        public void WindowWireVersionsRejectMixedPeersAndPreserveSavedCheckpointIdentity()
        {
            var initial=new SimulationCheckpointEnvelope{GameId="qy008.wire",ContentPackHash="fixture.content",CheckpointId="initial.fixture",SimulationEpoch=1,MembershipVersion=1,
                ProtocolVersion=SimulationCheckpointEnvelope.WindowInitialProtocolVersion,WindowExecutionVersion=WindowExecutionGrant.Version};
            Require(initial.HasValidIdentity(),"窗口初始信封无效");var legacy=initial.Snapshot();legacy.ProtocolVersion=SimulationCheckpointEnvelope.CurrentProtocolVersion;legacy.WindowExecutionVersion=0;
            Require(legacy.HasValidIdentity()&&!SimulationCheckpointEnvelope.SameIdentity(initial,legacy),"旧对端被默认为窗口协议");
            var missing=initial.Snapshot();missing.WindowExecutionVersion=0;Require(!missing.HasValidIdentity(),"缺少窗口能力仍被接纳");
            var saved=initial.Snapshot();saved.ProtocolVersion=SimulationCheckpointEnvelope.WindowSavedProtocolVersion;saved.Purpose=SimulationCheckpointEnvelope.SavedSimulation;
            saved.SourceSimulationEpoch=1;saved.SimulationEpoch=2;saved.CheckpointId="recovery."+new string('a',64);
            Require(saved.HasValidIdentity()&&saved.CheckpointId=="recovery."+new string('a',64),"网络能力改写了正式恢复点ID");
        }
        private const string Payment = @"return function(ctx) return {Effect.Condition({leftEffects={Effect.PayResource({player=ctx.playerId,resourceType='originium',amount=2})},rightEffects={
Effect.Choice({interactionType='effect.choice.execute',player=ctx.playerId,branches={{id='resource',effects={Effect.GainResource({player=ctx.playerId,resourceType='pure_originium',amount=1})}},{id='score',effects={Effect.GainScore({player=ctx.playerId,amount=1})}}}})}})} end";
        private static void Require(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void BackgroundMainActionPaysThenChoosesBeforeAnyHostRoundTrip()
        {
            using var group = new NetworkGroup("production-main-action", Payment, true);
            group.AnswerAll("action.answer.");
            var own = group.View(1).Players.Single(p=>p.PlayerId==1);
            Require(own.Resources.Originium==2 && own.Resources.PureOriginium==1 && group.Host.LastAppliedSequence==0,
                "主要行动窗口的真实付款/选择后继仍依赖Host");
            Require(group.ActorApplied.Count>=2, "主要行动付款后没有执行真实后继选择");group.CheckNoFault();
        }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void BackgroundSameEffectReplacesRequestIdentityAndRejectsPreviousRevision()
        {
            using var group=new NetworkGroup("production-same-effect",mainAction:true,sameEffect:true);
            var first=group.View(1).Interactions.Single(r=>r.Status=="open"&&r.AnsweringPlayerId==1&&r.Kind.StartsWith("qy008.window.",StringComparison.Ordinal));
            var old=group.Answer("same.first");group.Submit(old);
            var second=group.View(1).Interactions.Single(r=>r.Status=="open"&&r.AnsweringPlayerId==1&&r.Kind.StartsWith("qy008.window.",StringComparison.Ordinal));
            Require(first.OwnerEffectId==second.OwnerEffectId&&first.InteractionId!=second.InteractionId&&first.Kind!=second.Kind&&second.StateRevision>first.StateRevision,
                "同一效果没有按原合同切换请求身份与版本");
            var stale=GameCommandDto.FromCommand(old).ToCommand();stale.CommandId="same.stale";
            Require(group.Actor.TrySubmitWindowExecution(stale,out var rejected)&&!rejected.Succeeded&&rejected.Validation.ErrorCode==CommandErrorCode.StateRevisionMismatch,
                "旧请求回答被重新签名或执行");
            group.Submit(group.Answer("same.second"));Require(group.ActorApplied.Count==2&&group.Host.LastAppliedSequence==0,"同一效果新请求仍等待Host或重复执行");
            long tail=group.Actor.LastAppliedSequence;group.Exchange(()=>group.Host.LastAppliedSequence==tail&&group.Peer.LastAppliedSequence==tail);
            Require(group.Host.ValidatedThroughSequence==0,"本人窗口仍开放时提前提升确认前缀");
            group.CheckNoFault();
        }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void BackgroundPaymentGeneratesRealSuccessorsBeforeHostAndSavesOnlyConfirmedBoundary()
        {
            using var group = new NetworkGroup("production-payment", Payment);
            group.Invoke("payment.invoke"); group.AnswerAll("payment.answer.");
            var actor = group.View(1); Require(actor.Players.Single(p => p.PlayerId == 1).Resources.Originium == 2, "真实付款未扣除");
            Require(actor.Players.Single(p => p.PlayerId == 1).Resources.PureOriginium == 1, "付款后的真实选择未执行收益");
            Require(group.Host.LastAppliedSequence == 0 && group.Peer.LastAppliedSequence == 0, "窗内后继依赖Host或其它副本");
            var earlier = group.Host.CreateSaveCheckpointAsync().GetAwaiter().GetResult();
            Require(group.Actor.ValidatedThroughSequence == 0 && earlier.Baseline.ConfirmedGlobalSequence == 0 &&
                earlier.Archive.Snapshot.State.FindPlayer(1).Resources.PureOriginium == 0, "未确认尾巴被当作正式保存点");
            group.End("payment.end"); long tail = group.Actor.LastAppliedSequence;
            Require(group.Actor.TryReadPublishedSnapshot(out var snapshot) && snapshot.WindowExecution.BoundaryPending, "结束没有锁定真实交权边界");
            group.Exchange(() => group.Host.ValidatedThroughSequence == tail && group.Actor.ValidatedThroughSequence == tail && group.Peer.ValidatedThroughSequence == tail);
            var checkpoint = group.Host.CreateSaveCheckpointAsync().GetAwaiter().GetResult();
            Require(checkpoint.Baseline.ConfirmedGlobalSequence == tail && checkpoint.Archive.Snapshot.State.FindPlayer(1).Resources.PureOriginium == 1, "准确确认点不含真实收益");
            group.Write("checkpoint", checkpoint); group.CheckNoFault();
        }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void BackgroundRepeatCleanupUsesCurrentRequestsAndEchoNeverExecutesAgain()
        {
            using var group = new NetworkGroup("production-repeat");
            string firstRequest = group.Option().CleanupRequest.InteractionId;
            group.Invoke("repeat.1"); group.AnswerAll("repeat.1.answer.");
            Require(group.Option().IsEnabled && group.Option().CleanupRequest.InteractionId != firstRequest, "真实来源后继请求未更新");
            group.Invoke("repeat.2"); group.AnswerAll("repeat.2.answer.");
            Require(!group.Option().IsEnabled && group.View(1).Players.Single(p => p.PlayerId == 1).Resources.PureOriginium == 2, "两次实际付款与收益不符");
            Require(group.Host.LastAppliedSequence == 0 && group.ActorApplied.Count == 6, "后继操作等待远端或被重复发表");
            long tail = group.Actor.LastAppliedSequence; group.Exchange(() => group.Host.LastAppliedSequence == tail && group.Peer.LastAppliedSequence == tail);
            Require(group.ActorApplied.Count == 6 && group.Actor.LastAppliedSequence == tail && group.Actor.ValidatedThroughSequence == 0, "回显重复执行或过早确认");
            group.End("repeat.end"); tail = group.Actor.LastAppliedSequence;
            group.Exchange(() => group.Host.ValidatedThroughSequence == tail && group.Actor.ValidatedThroughSequence == tail && group.Peer.ValidatedThroughSequence == tail);
            group.CheckNoFault();
        }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void BackgroundInvalidLocalCandidateDoesNotSendAndValidRetryContinues()
        {
            using var group = new NetworkGroup("production-invalid");group.Invoke("invalid.invoke");
            var valid = group.Answer("invalid.answer"); var bad = GameCommandDto.FromCommand(valid).ToCommand();bad.OptionIds.Clear();bad.OptionIds.Add("invalid.option");
            long tail = group.Actor.LastAppliedSequence; int sent = group.ActorRecords.Count;
            Require(group.Actor.TrySubmitWindowExecution(bad, out var queued) && queued.Succeeded, "本地提交未进入原规则队列");
            group.Wait(() => group.Rejected.Count == 1 && !group.Actor.HasPendingLocalApplication);
            Require(group.Actor.LastAppliedSequence == tail && group.ActorRecords.Count == sent && !group.Actor.IsStopped, "非法命令发送/占号/破坏合法重试");
            group.Submit(valid);Require(group.Actor.LastAppliedSequence == tail + 1, "合法重试未继续");group.CheckNoFault();
        }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void BackgroundOutOfOrderGapResendsOriginalRecordAndBoundaryWaitsSlowReplica()
        {
            using var group = new NetworkGroup("production-gap");group.Invoke("gap.invoke");group.AnswerAll("gap.answer.");group.End("gap.end");
            long tail = group.Actor.LastAppliedSequence; var records = group.ActorRecords.ToArray();
            group.Messages.RemoveAll(m => m.Kind == "stream-record" && m.Target == 0);
            group.Host.ReceiveWindowExecutionRecord(1, false, records.Last());
            group.HoldPeer = true;
            group.Exchange(() => group.Host.LastAppliedSequence == tail);
            Require(group.Host.ValidatedThroughSequence == 0 && group.Peer.LastAppliedSequence == 0, "慢副本尚未重放却提升了确认前缀");
            Require(group.MissingCount > 0 && group.RangeCount > 0, "乱序缺包未走原号补发");
            group.HoldPeer = false;
            group.Exchange(() => group.Host.ValidatedThroughSequence == tail && group.Actor.ValidatedThroughSequence == tail && group.Peer.ValidatedThroughSequence == tail);
            Require(group.ActorApplied.Count == tail, "补发回显触发二次本地执行");group.CheckNoFault();
        }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void BackgroundDisconnectRetainsUnconfirmedTailAndLastConfirmedCheckpoint()
        {
            using var group = new NetworkGroup("production-disconnect");group.Invoke("disconnect.invoke");group.AnswerAll("disconnect.answer.");
            long tail = group.Actor.LastAppliedSequence;group.Exchange(() => group.Host.LastAppliedSequence == tail && group.Peer.LastAppliedSequence == tail);
            group.Host.PeerDisconnected(2);group.Wait(() => group.Host.IsStopped);
            Require(group.Host.ValidatedThroughSequence == 0 && group.Host.ReadPendingTail().Count == tail, "断线清掉未确认原件或提升前缀");
            group.Host.BeginConfirmedRecovery();group.Wait(() => group.Host.IsRecoveringConfirmedPrefix);
            var point = group.Host.CreateConfirmedRecoveryCheckpointAsync(0, 2).GetAwaiter().GetResult();
            Require(point.Baseline.ConfirmedGlobalSequence == 0 && point.Archive.Snapshot.State.FindPlayer(1).Resources.PureOriginium == 0, "未确认窗口尾巴混入恢复点");
            group.Write("retained-confirmed-recovery", point);
        }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void BackgroundLogCapacityStopsWithoutClearingOrConfirmingTail()
        {
            using var group=new NetworkGroup("production-capacity",maximumLogCommands:3);group.Invoke("capacity.first");group.AnswerAll("capacity.answer.");
            Require(group.Actor.LastAppliedSequence==3,"容量夹具未产生3个真实依赖操作");
            var command=CleanupSourceCommand.Create(1,"capacity.next",group.Option().CleanupRequest,group.View(1).StateRevision);
            Require(group.Actor.TrySubmitWindowExecution(command,out var queued)&&queued.Succeeded,"容量耗尽操作未进入本地检查");group.Wait(()=>group.Actor.IsStopped);
            Require(group.Actor.LastFault.FaultCode=="window_execution_log_capacity"&&group.Actor.ReadPendingTail().Count==3&&group.Actor.ValidatedThroughSequence==0,
                "容量耗尽清空原件、越过确认点或没有明确停止");
        }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void BackgroundForeignAnswerWaitsForBoundaryAndReturnsToOriginalOwner()
        {
            const string other="return function(ctx) return {Effect.Choice({player='P2',options={'other'},minSelections=1,maxSelections=1})} end";
            using var group = new NetworkGroup("production-other-answer", other);group.Invoke("other.invoke");long fence=group.Actor.LastAppliedSequence;
            Require(group.View(1).Interactions.Where(r=>r.Visibility=="private").All(r=>r.AnsweringPlayerId!=2 || r.CandidateIds.Count==0),"他人私有候选泄漏给本人");
            Require(!group.Actor.ReadWindowPermissions().Any(w=>w.CanSubmitForLocalPlayer),"必要他人应答未锁边界");
            group.Exchange(()=>group.Host.ValidatedThroughSequence==fence && group.Peer.ReadWindowPermissions().Any(w=>w.CanSubmitForLocalPlayer));
            group.SubmitForeignAnswer();
            group.Exchange(()=>group.Actor.TryReadPublishedSnapshot(out var snapshot)&&snapshot.WindowExecution?.IsActive==true&&snapshot.WindowExecution.OwnerPlayerId==1&&
                group.Actor.ReadWindowPermissions().Any(w=>w.CanSubmitForLocalPlayer));
            group.CheckNoFault();
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [TestCase(false)] [TestCase(true)]
#endif
        public void HistoricalConfirmedSealAfterRegrantKeepsIdentityAndRejectsConflict(bool conflicting)
        {
            const string body="return function(ctx) return {Effect.Choice({player='P2',options={'other'},minSelections=1,maxSelections=1}),Effect.GainResource({player=ctx.playerId,resourceType='pure_originium',amount=1})} end";
            using var group=new NetworkGroup(conflicting?"r15-old-seal-conflict":"r15-old-seal-identical",body);
            WindowExecutionSeal original=null;group.Actor.WindowExecutionSealProduced+=value=>original??=WindowExecutionCodec.Copy(value);
            group.Invoke("r15.old.invoke");
            group.Exchange(()=>group.Host.ValidatedThroughSequence==1&&group.Peer.ReadWindowPermissions().Any(w=>w.CanSubmitForLocalPlayer));
            Require(original!=null&&original.OperationCount==1,"未保留第一次真实已确认封尾");
            group.SubmitForeignAnswer();
            group.Exchange(()=>group.Actor.TryReadPublishedSnapshot(out var s)&&s.WindowExecution?.IsActive==true&&s.WindowExecution.OwnerPlayerId==1&&
                s.WindowExecution.GrantId!=original.GrantId&&group.Actor.ValidatedThroughSequence==2);
            group.Actor.TryReadPublishedSnapshot(out var before);long applied=group.Actor.LastAppliedSequence,confirmed=group.Actor.ValidatedThroughSequence;
            int events=group.ActorApplied.Count;var resources=group.View(1).Players.Single(p=>p.PlayerId==1).Resources;
            Require(resources.PureOriginium==1,"合法他人应答后的真实后继收益未产生");
            var duplicate=WindowExecutionCodec.Copy(original);
            if(conflicting){duplicate.RuleDigest=new string('0',64);duplicate.SealDigest=WindowExecutionCodec.SealDigest(duplicate);}
            group.Actor.ReceiveWindowExecutionSeal(0,true,duplicate);group.ReadOnlyActorBarrier();
            Require(group.Actor.LastAppliedSequence==applied&&group.Actor.ValidatedThroughSequence==confirmed&&group.ActorApplied.Count==events,
                "旧封尾改变Applied或确认前缀");
            Require(JsonConvert.SerializeObject(group.View(1).Players.Single(p=>p.PlayerId==1).Resources)==JsonConvert.SerializeObject(resources),"旧封尾重复或撤销收益");
            if(conflicting)
            {
                Require(group.Actor.IsStopped&&group.Actor.LastFault.FaultCode=="window_execution_seal_conflict","历史同grant不同封尾被静默吞掉");
                string first=group.Actor.LastFault.Diagnostic;group.Actor.ReceiveWindowExecutionSeal(0,true,original);group.ReadOnlyActorBarrier();
                Require(group.Actor.LastFault.Diagnostic==first,"历史冲突的首故障被覆盖");
            }
            else
            {
                Require(!group.Actor.IsStopped,"相同历史封尾错误停止："+group.Actor.LastFault?.Diagnostic);
                group.Actor.TryReadPublishedSnapshot(out var after);Require(after.WindowExecution.GrantId==before.WindowExecution.GrantId,"旧封尾改写当前授予");group.CheckNoFault();
            }
            group.Write("R15历史封尾重投",new{conflicting,original,replayed=duplicate,applied,confirmed,events,resources,currentGrant=before.WindowExecution.GrantId,fault=group.Actor.LastFault});
        }
#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void HostOwnerPaymentChoiceAndEndAreAppliedOnceAndSavedAtExactTail()
        {
            using var group=new NetworkGroup("r15-host-owner",Payment,hostOwnsWindow:true);
            group.Invoke("host-own.invoke");group.AnswerAll("host-own.answer.");group.End("host-own.end");
            group.Exchange(()=>group.AllConfirmed(4));
            var ids=group.ActorApplied.Select(r=>r.Command.CommandId).ToArray();
            Require(ids.SequenceEqual(new[]{"host-own.invoke","host-own.answer.0","host-own.answer.1","host-own.end"}),"Host本人发生重复或遗漏Applied");
            var resources=group.View(0).Players.Single(p=>p.PlayerId==1).Resources;
            Require(resources.Originium==2&&resources.PureOriginium==1,"Host付款或后继收益不符");
            var saved=group.Host.CreateSaveCheckpointAsync().GetAwaiter().GetResult();
            Require(saved.Baseline.ConfirmedGlobalSequence==4&&saved.Archive.Snapshot.State.FindPlayer(1).Resources.PureOriginium==1,"Host保存未对应准确确认尾号");
            group.Write("R15Host本人确认",new{applied=ids,savedPrefix=saved.Baseline.ConfirmedGlobalSequence,resources});group.CheckNoFault();
        }

        private sealed class NetworkGroup : IDisposable
        {
            internal sealed class Message { internal string Kind; internal int Target; internal Action Deliver; }
            internal readonly List<Message> Messages = new List<Message>();
            internal readonly List<WindowExecutionRecord> ActorRecords = new List<WindowExecutionRecord>();
            internal readonly List<SimulationCommandAppliedDto> ActorApplied = new List<SimulationCommandAppliedDto>();
            internal readonly List<SimulationProposalResultDto> Rejected = new List<SimulationProposalResultDto>();
            internal readonly List<object> Timings = new List<object>();
            internal readonly List<SimulationWindowSavePoint> AutomaticSavePoints = new List<SimulationWindowSavePoint>();
            private readonly List<IsolatedTestArtifacts> artifacts = new List<IsolatedTestArtifacts>();
            private readonly CleanupSourceTestFixture[] fixtures;
            private readonly GameSession[] sessions;
            private readonly DeterministicSimulationCoordinator[] nodes;
            private readonly string label;
            private readonly int actorIndex;
            private readonly int[] playerIds;
            internal bool HoldPeer, HoldReady;
            internal int MissingCount, RangeCount;
            internal DeterministicSimulationCoordinator Host => nodes[0];
            internal DeterministicSimulationCoordinator Actor => nodes[actorIndex];
            internal DeterministicSimulationCoordinator Peer => nodes[actorIndex==0?1:2];
            internal NetworkGroup(string label, string body = null, bool mainAction = false, bool sameEffect = false, int maximumLogCommands = 4096,bool hostOwnsWindow=false, int windowVersion=WindowExecutionGrant.Version, bool concurrent=false,
                SimulationRecoveryCheckpointDto restore=null, bool finalRound=false)
            {
                this.label = label;
                actorIndex=hostOwnsWindow?0:1;playerIds=hostOwnsWindow?new[]{1,2,3}:new[]{3,1,2};
                fixtures = Enumerable.Range(0,3).Select(i => {
#if YC_WINDOW_EXECUTION_OFFLINE
                    var a = new IsolatedTestArtifacts(Path.Combine(OfflineOutput,label,"fixture-"+i));
#else
                    var a = IsolatedTestArtifacts.BeginCase();
                    (currentArtifacts??=new List<IsolatedTestArtifacts>()).Add(a);
#endif
                    artifacts.Add(a); var f = new CleanupSourceTestFixture(a, bodyOverride:body, inMemoryInput:true, concurrent:concurrent);
                    // 隔离输入补全正式存档的席位颜色前置，不改变任何规则/命令结果。
                    foreach(var p in f.State.Players) p.Color=(PlayerColor)(p.PlayerId-1);
                    f.State.RandomRuntime ??= GameplayRandomRuntime.Create(f.State.GameId);
                    if(finalRound)f.State.MaxRounds=1;
                    if(restore!=null)
                    {var recovered=HostRecoveryService.Recover(GameStateCloneService.DeepClone(restore.Archive),f.Registry);Require(recovered.Succeeded,recovered.Diagnostic);f.State=recovered.State;}
                    if(concurrent)
                    {
                        // 隔离采集前序，仅安装正式采集主节点与真实独立玩家任务。
                        var map=StaticMapDefinitions.Resolve(f.State.MapId);
                        var initial=new GameState{GameId=f.State.GameId,ContentPackHash=f.State.ContentPackHash,MapId=f.State.MapId,Round=1,MaxRounds=8,StartPlayerId=1,
                            RuleConfiguration=GameStateCloneService.DeepClone(f.State.RuleConfiguration),ContentDefinitions=GameStateCloneService.DeepClone(f.State.ContentDefinitions),
                            Players=f.State.Players.Select(p=>new PlayerState{PlayerId=p.PlayerId,Color=p.Color,Name=p.Name,Resources=new ResourceSet{GoldVoucher=20}}).ToList(),
                            LaunchPlayerOrder=new List<int>{1,2,3},RandomRuntime=GameplayRandomRuntime.Create(f.State.GameId)};
                        f.State=new ItemZoneStateFactory().CreateForSetup(initial,map,new[]{
                            new ItemRecipeEntry{EntryId="characters",DefinitionId="texas",TypeId="core.basic_character_card",Quantity=2,PerPlayer=true,InitialZoneRole="hand"},
                            new ItemRecipeEntry{EntryId="cities",DefinitionId="core.mobile_city",TypeId="core.mobile_city",Quantity=1,PerPlayer=true,InitialZoneRole="city_reserve"},
                            new ItemRecipeEntry{EntryId="markers",DefinitionId="core.player_marker",TypeId="core.player_marker",Quantity=6,PerPlayer=true,InitialZoneRole="supply"}});
                        var locations=map.Locations.Where(l=>!l.IsRedZone).Take(3).ToArray();
                        foreach(var p in f.State.Players)
                        {
                            var city=f.State.ItemZones.Items.Single(i=>i.PhysicalKind==ItemPhysicalKind.MobileCity&&i.OwnerSubject.PlayerId==p.PlayerId);
                            var location=f.State.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.map_location"&&z.LocalZoneId==locations[p.PlayerId-1].LocationId);
                            var dock=f.State.ItemZones.Zones.Single(z=>z.ParentId==location.ZoneId&&z.TemplateId=="core.zone.city_dock");
                            typeof(ItemZoneStateMigrator).GetMethod("SetPosition",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,new object[]{f.State,city,dock.ZoneId});
                        }
                        f.State.EffectRuntime=new EffectRuntimeState();f.State.Phase=GamePhase.ResourceCollection;f.State.CurrentPlayerId=-1;
                        foreach(var p in f.State.Players)p.ResourceCollectionStartGoldVoucher=p.Resources.GoldVoucher;
                        var chain=new RoundMainChainBuilder().Build(f.State.GameId,"",1,new[]{1,2,3},1);
                        var main=chain.Nodes.Single(n=>n.NodeTypeId==RoundMainlineNodeTypeIds.Collection);
                        f.State.EffectRuntime.MainNodes.AddRange(chain.Nodes);
                        foreach(var n in chain.Nodes.Where(n=>n.MainlineIndex<main.MainlineIndex))n.Status=EffectNodeStatus.Completed;
                        var rt=f.State.EffectRuntime;rt.ActiveMainNodeId=main.NodeId;rt.FirstMainNodeId=chain.FirstNodeId;rt.CurrentRoundExecutionId=chain.RoundExecutionId;
                        rt.CurrentRoundNumber=1;rt.CurrentRoundStartPlayerId=1;rt.PlayerOrderSnapshot=new List<int>{1,2,3};
                        f.RootId=f.Engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                            YC.Domain.Effects.ItemTransferEffectExecutor.Object("mainNodeId",NormalizedValue.CreateString(main.NodeId)),1));
                        main.ExecutionEffectId=f.RootId;main.Status=EffectNodeStatus.Running;f.Drain();
                    }
                    if(mainAction)
                    {
                        // 显式隔离前序：正常主行动主节点及真实Lua付款树。不是正常菜单/页面验收。
                        f.State.EffectRuntime=new EffectRuntimeState();f.State.Phase=GamePhase.ActionRound1;f.State.ActionRound=1;f.State.CurrentPlayerId=1;
                        Require(f.Rounds.PrepareActionWindowForSmoke(f.State,1).IsValid,"隔离主要行动窗未打开");f.Rounds.MarkMainActionComplete(f.State,1);
                        const string type="qy008.window.action.trigger";
                        f.Registry.Register(new EffectRegistration(type,ctx=>{
                            if(!sameEffect||ctx.GetLatestInteractionAnswer()!=null&&ctx.Node.FlowStage=="second")return EffectStepResult.Completed();
                            bool first=ctx.Node.FlowStage=="";return EffectStepResult.Continue(first?"first":"second").AddInteraction(new EffectInteractionSpec{
                                InteractionTypeId=first?"qy008.window.first":"qy008.window.second",AnsweringPlayerId=1,AnswerSchema="boolean_accept"});
                        },EffectExecutorKind.IntrinsicFlow));
                        if(!sameEffect)
                        {
                            string script="return function(ctx) if ctx.payload.effectTypeId~='"+type+"' then return {} end "+(body??Payment).Replace("return function(ctx)","").Replace("end","")+" end";
                            new LuaEffectEventDispatcher(f.Registry).Register(new LuaHandlerDefinition("qy008.window.action.reward","EffectCompleted","",new LuaScriptDefinition(
                                "qy008.window.action.reward","qy008.window.action.reward","qy008.window.action.reward","1.0.0",script,LuaContentHasher.ComputeSha256(script)),EffectHandlerRole.Observer));
                        }
                        var engine=f.Rounds.CreateCommandExecutor(f.State);Require(engine.TryCreatePlayerActionEffect(1,EffectSpec.Create(type,playerId:1),"qy008.action","qy008.action",out _),"主行动效果未挂接");
                        Require(!engine.RunUntilQuiescent().Faulted,f.State.EffectRuntime.LastFaultMessage);
                    }
                    return f;
                }).ToArray();
                sessions = fixtures.Select(f => { var s = new GameSession(f.State);s.RegisterHandler(new AnswerInteractionCommandHandler(f.Registry,f.Rounds));
                    s.RegisterHandler(new InvokeSettlementOptionCommandHandler(f.Registry));s.RegisterHandler(new EndActionCommandHandler(new RoundAdvanceService(f.Rounds),
                        new FinalScoringService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId))),f.Registry,f.Rounds));
                    if(restore!=null){Require(s.TryPrepareLocalRecoveryState(f.State,restore.Continuation,out var prepared,out var fault),fault);Require(s.TryCommitPreparedRecovery(prepared,out fault),fault);}return s; }).ToArray();
                nodes = Enumerable.Range(0,3).Select(i=>{var options=restore==null?DeterministicSimulationSessionOptions.CreateInitial(i==0,playerIds[i],sessions[i],new[]{1,2,3}):MatchSaveData.OptionsFor(restore.Baseline,i==0,playerIds[i]);
                    options.MaximumLogCommands=maximumLogCommands;return restore==null?new DeterministicSimulationCoordinator(sessions[i],options):DeterministicSimulationCoordinator.FromConfirmedRecovery(sessions[i],options,restore.Baseline,sessions[i].State);}).ToArray();
                Host.AutomaticSavePointConfirmed+=AutomaticSavePoints.Add;
                Host.BoundaryProduced += value => Broadcast("boundary",i=>nodes[i].ReceiveBoundary(value));
                Host.OrderedCommandProduced += value => Broadcast("ordered",i=>nodes[i].ReceiveOrdered(value));
                Host.WindowPermitProduced += value => Broadcast("permit",i=>nodes[i].ReceiveWindowPermit(value));
                Host.WindowExecutionGranted += value => Broadcast("grant",i=>nodes[i].ReceiveWindowExecutionGrant(value));
                Host.CommandValidationPrefixAdvanced += value => Broadcast("validation",i=>nodes[i].ReceiveValidationPrefix(value));
                Host.WindowExecutionConfirmed += value => Broadcast("confirmed",i=>nodes[i].ReceiveWindowExecutionConfirmation(value));
                Host.WindowExecutionRecordProduced += value => Broadcast("stream-record",i=>nodes[i].ReceiveWindowExecutionRecord(0,true,value));
                Host.WindowExecutionSealProduced += value => Broadcast("seal",i=>nodes[i].ReceiveWindowExecutionSeal(0,true,value));
                Actor.WindowExecutionRecordProduced += value => { ActorRecords.Add(value);if(!hostOwnsWindow)Enqueue("stream-record",0,()=>Host.ReceiveWindowExecutionRecord(1,false,value)); };
                if(!hostOwnsWindow)Actor.WindowExecutionSealProduced += value => Enqueue("seal",0,()=>Host.ReceiveWindowExecutionSeal(1,false,value));
                Actor.SimulationCommandApplied += value => ActorApplied.Add(value);
                Actor.SimulationProposalRejected += value => Rejected.Add(value);
                for(int i=0;i<3;i++)
                {
                    int index=i,player=playerIds[i];
                    if(i!=0){nodes[i].WindowReadyProduced += value=>Enqueue("ready",0,()=>Host.ReceiveWindowReady(player,value));
                        nodes[i].WindowExecutionReadyProduced += value=>Enqueue("stream-ready",0,()=>Host.ReceiveWindowExecutionReady(player,value));
                        if(i!=actorIndex){nodes[i].WindowExecutionRecordProduced+=value=>Enqueue("stream-record",0,()=>Host.ReceiveWindowExecutionRecord(player,false,value));
                            nodes[i].WindowExecutionSealProduced+=value=>Enqueue("seal",0,()=>Host.ReceiveWindowExecutionSeal(player,false,value));}}
                    nodes[i].WindowExecutionMissingRequested += value=>{MissingCount++;int target=index==0?Array.IndexOf(playerIds,value.TargetPlayerId):0;
                        Enqueue("missing",target,()=>nodes[target].ReceiveWindowExecutionMissing(index==0?0:player,index==0,value));};
                    nodes[i].WindowExecutionRangeProduced += value=>{RangeCount++;int target=index==0?Array.IndexOf(playerIds,value.TargetPlayerId):0;
                        Enqueue("range",target,()=>nodes[target].ReceiveWindowExecutionRange(index==0?0:player,index==0,value));};
                    nodes[i].ConfigureRequestContractValidation(OperationCommandProtocol.RequestContractVersion);nodes[i].ConfigureWindowExecution(windowVersion);nodes[i].StartBackgroundExecution();nodes[i].Begin();
                }
                // 三个副本均已用正式归档校验及continuation安装完成，才恢复真实边界协议。
                if(restore!=null)foreach(var node in nodes)node.CompleteConfirmedRecovery(true);
                try { Exchange(()=>windowVersion==0||concurrent ? nodes.All(n=>n.ReadWindowPermissions().Any(w=>w.IsPermitted)) : nodes.All(n=>n.TryReadPublishedSnapshot(out var s)&&s.WindowExecution?.IsActive==true));CheckNoFault(); }
                catch { Dispose();throw; }
            }
            internal GameStateView View(int index) { Require(nodes[index].TryReadPublishedSnapshot(out var s),"缺少固定投影");return s.ReadView(); }
            internal YC.Domain.Cards.CharacterSettlementOption Option()=>View(actorIndex).CharacterSettlementOptions.Single(o=>o.SourceItemId==fixtures[actorIndex].CardId && o.CleanupRequest!=null);
            internal void Invoke(string id)=>Submit(CleanupSourceCommand.Create(1,id,Option().CleanupRequest,View(actorIndex).StateRevision));
            internal GameCommand Answer(string id)
            {
                var v=View(actorIndex);var open=v.Interactions.Where(r=>r.Status=="open"&&r.AnsweringPlayerId==1).ToArray();
                var r=open.LastOrDefault(r=>r.Kind.StartsWith("qy008.window.",StringComparison.Ordinal))??open.Last();
                var c=new GameCommand{PlayerId=1,CommandId=id,Kind=GameCommandKind.AnswerInteraction};c.Parameters["interactionId"]=r.InteractionId;
                c.Parameters["expectedRevision"]=v.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if(r.AnswerSchema=="boolean_accept")c.Parameters["answerValue"]="true";
                else c.OptionIds.Add(r.CandidateIds.Contains("resource")?"resource":r.CandidateIds.Contains("continue")?"continue":r.CandidateIds.First());return c;
            }
            internal void AnswerAll(string prefix){for(int i=0;i<12&&View(actorIndex).Interactions.Any(r=>r.Status=="open"&&r.AnsweringPlayerId==1);i++)Submit(Answer(prefix+i));}
            internal void End(string id)
            {
                var v=View(actorIndex);var end=v.SettlementEndAction;Require(end!=null&&end.CanCloseCleanupWindow,"原页未许可结束收尾");
                Submit(new GameCommand{PlayerId=1,CommandId=id,Kind=GameCommandKind.EndAction,Parameters=new Dictionary<string,string>{
                    ["windowId"]=end.WindowId,["currentNodeEffectId"]=end.CurrentNodeEffectId,["interactionId"]=end.InteractionId,
                    ["expectedRevision"]=end.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}});
            }
            internal void SubmitForeignAnswer()
            {
                var view=View(2);var r=view.Interactions.Last(r=>r.Status=="open"&&r.AnsweringPlayerId==2);
                var command=new GameCommand{CommandId="other.own.answer",PlayerId=2,Kind=GameCommandKind.AnswerInteraction,OptionIds=new List<string>{r.CandidateIds.First()},
                    Parameters=new Dictionary<string,string>{["interactionId"]=r.InteractionId,["expectedRevision"]=view.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                Require(!Peer.TrySubmitWindowExecution(command,out _),"必要他人应答被误当本人根窗授权");
                Require(Peer.TryCreateLocalProposal(command,out var proposal,out var invalid),invalid?.Reason);
                var admitted=Host.AdmitProposal(2,proposal);Peer.ReceiveProposalResult(admitted);
                Require(admitted.Status==OperationAdmissionStatus.Accepted||admitted.Status==OperationAdmissionStatus.PendingValidation,"原他人应答未准入:"+admitted.Reason);
            }
            internal void Submit(GameCommand command)
            {
                var timer=Stopwatch.StartNew();long before=Actor.LastAppliedSequence;int rejected=Rejected.Count;
                bool local=Actor.TrySubmitWindowExecution(command,out var queued);
                if(local) Require(queued.Succeeded,"命令没有进入本地窗口:"+queued?.Validation?.Reason);
                else
                {
                    Require(Actor.TryCreateLocalProposal(command,out var proposal,out var invalid),invalid?.Reason);
                    var admitted=Host.AdmitProposal(command.PlayerId,proposal);Actor.ReceiveProposalResult(admitted);
                    Require(admitted.Status==OperationAdmissionStatus.Accepted||admitted.Status==OperationAdmissionStatus.PendingValidation,"原编序拒绝:"+admitted.Reason);
                }
                if(local) Wait(()=>Actor.LastAppliedSequence==before+1&&!Actor.HasPendingLocalApplication||Rejected.Count>rejected);
                else Exchange(()=>Actor.LastAppliedSequence==before+1&&!Actor.HasPendingLocalApplication||Rejected.Count>rejected);
                Require(Rejected.Count==rejected,"原规则拒绝:"+Rejected.LastOrDefault()?.Reason);
                Timings.Add(new{command=command.CommandId,localRulePublicationAndSuccessorMs=timer.Elapsed.TotalMilliseconds,hostApplied=Host.LastAppliedSequence,
                    peerApplied=Peer.LastAppliedSequence,actorApplied=Actor.LastAppliedSequence,confirmed=Actor.ValidatedThroughSequence,
                    requests=View(actorIndex).Interactions.Where(r=>r.Status=="open").Select(r=>new{r.InteractionId,r.OwnerEffectId,r.Kind,r.AnsweringPlayerId,r.StateRevision})});
            }
            internal bool AllConfirmed(long tail)=>nodes.All(n=>n.ValidatedThroughSequence==tail);
            internal void EndOtherPlayer(int player, string id)
            {
                int index=Array.IndexOf(playerIds,player);var node=nodes[index];
                Exchange(()=>node.ReadWindowPermissions().Any(w=>w.CanSubmitForLocalPlayer)&&node.TryReadPublishedSnapshot(out var s)&&
                    (s.WindowExecution==null||s.WindowExecution.IsActive&&s.WindowExecution.OwnerPlayerId==player&&!s.WindowExecution.BlocksSubmission));
                var view=View(index);var end=view.SettlementEndAction;
                Require(end!=null&&end.CanCloseCleanupWindow,"另一玩家未获真实收尾许可");
                var command=new GameCommand{PlayerId=player,CommandId=id,Kind=GameCommandKind.EndAction,Parameters=new Dictionary<string,string>{
                    ["windowId"]=end.WindowId,["currentNodeEffectId"]=end.CurrentNodeEffectId,["interactionId"]=end.InteractionId,
                    ["expectedRevision"]=end.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                long before=node.LastAppliedSequence;
                if(node.TrySubmitWindowExecution(command,out var queued))Require(queued.Succeeded,"另一玩家窗口提交失败");
                else
                {
                    Require(node.TryCreateLocalProposal(command,out var proposal,out var invalid),invalid?.Reason);
                    var result=Host.AdmitProposal(player,proposal);node.ReceiveProposalResult(result);
                    Require(result.Status==OperationAdmissionStatus.Accepted||result.Status==OperationAdmissionStatus.PendingValidation,"另一玩家原编序拒绝:"+result.Reason);
                }
                Exchange(()=>node.LastAppliedSequence==before+1&&!node.HasPendingLocalApplication);
            }
            internal void ReplayBoundaryReady(long fence)
            {
                for(int i=1;i<3;i++)
                {
                    var b=nodes[i].ReadBoundary(fence);Require(b!=null,"缺少真实已确认边界原件");var o=nodes[i].SessionOptions;
                    Host.ReceiveWindowReady(playerIds[i],new SimulationWindowReadyDto{GameId=o.GameId,SimulationEpoch=o.SimulationEpoch,
                        MembershipVersion=o.MembershipVersion,PlayerId=playerIds[i],ReplicaId=DeterministicSimulationSessionOptions.ReplicaIdForPlayer(playerIds[i]),Boundary=b});
                }
                ReadOnlyActorBarrier();
            }
            internal void CommitCollection(int player,string id)
            {
                int index=Array.IndexOf(playerIds,player);var view=View(index);var node=nodes[index];
                var r=view.Interactions.Single(q=>q.Status=="open"&&q.AnsweringPlayerId==player&&q.Kind=="route_resource.selection");
                string scope=r.PromptParameters.Properties.Single(p=>p.Name=="scopeKey").Value.StringValue;
                string commit=YC.Domain.Travel.RouteSelectionAnswerCodec.Encode(scope,YC.Domain.Travel.RouteSelectionCandidateKind.Commit);
                Require(r.CandidateIds.Contains(commit),"当前正式候选没有空采集提交");
                var command=new GameCommand{PlayerId=player,CommandId=id,Kind=GameCommandKind.AnswerInteraction,OptionIds=new List<string>{commit},
                    Parameters=new Dictionary<string,string>{["interactionId"]=r.InteractionId,["expectedRevision"]=view.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                Require(!node.TrySubmitWindowExecution(command,out _),"多writer不应得到单writer连续授权");
                Require(node.TryCreateLocalProposal(command,out var proposal,out var invalid),invalid?.Reason??"采集编序前置未满足");
                var admitted=Host.AdmitProposal(player,proposal);node.ReceiveProposalResult(admitted);
                Require(admitted.Status==OperationAdmissionStatus.Accepted||admitted.Status==OperationAdmissionStatus.PendingValidation,"采集准入失败:"+admitted.Reason);
                long tail=Host.LastAppliedSequence+1;Exchange(()=>nodes.All(n=>n.LastAppliedSequence>=tail));
            }
            internal void ReadOnlyActorBarrier()
            {
                bool replied=false;
                Require(Actor.TryQueryPaymentSourcePage(1,new PaymentSourcePageRequest{RequestId="r15.read-only-barrier",Query=new PaymentSourceQuery{PlayerId=1}},_=>replied=true),"只读队列屏障未排入");
                Wait(()=>replied||Actor.IsStopped);
            }
            private void Enqueue(string kind,int target,Action action)=>Messages.Add(new Message{Kind=kind,Target=target,Deliver=action});
            private void Broadcast(string kind,Action<int> action){foreach(int i in new[]{1,2}){int target=i;Enqueue(kind,target,()=>action(target));}}
            internal void Wait(Func<bool> condition)=>Loop(condition,false);
            internal void Exchange(Func<bool> condition)
            {
                bool boundary=Actor.TryReadPublishedSnapshot(out var before)&&before.WindowExecution?.BoundaryPending==true;
                long startApplied=Actor.LastAppliedSequence,startConfirmed=Actor.ValidatedThroughSequence;var timer=Stopwatch.StartNew();Loop(condition,true);
                Timings.Add(new{networkExchangeMs=timer.Elapsed.TotalMilliseconds,boundaryAtStart=boundary,actorApplied=startApplied,confirmedAtStart=startConfirmed,confirmedAfter=Actor.ValidatedThroughSequence});
            }
            private void Loop(Func<bool> condition,bool exchange)
            {
                var timer=Stopwatch.StartNew();
                while(!condition())
                {
                    foreach(var node in nodes)node.DrainMainThreadNotifications(64);
                    if(exchange)foreach(var message in Messages.Where(m=>(!HoldPeer||m.Target!=2)&&(!HoldReady||m.Kind!="ready"&&m.Kind!="stream-ready")).ToArray()){Messages.Remove(message);message.Deliver();}
                    if(!condition()&&nodes.Any(n=>n.BackgroundExecutionException!=null||n.IsStopped))
                        throw new InvalidOperationException("后台首故障:"+string.Join(";",nodes.Select(n=>n.LastFault?.Diagnostic??n.BackgroundExecutionException?.Message)));
                    if(timer.Elapsed.TotalSeconds>20)throw new TimeoutException("后台/网络用例未完成:"+label+" tails="+string.Join(",",nodes.Select(n=>n.LastAppliedSequence))+" faults="+string.Join(",",nodes.Select(n=>n.LastFault?.Diagnostic)));
                    Thread.Sleep(1);
                }
                foreach(var node in nodes)node.DrainMainThreadNotifications(64);
            }
            internal void CheckNoFault()=>Require(nodes.All(n=>!n.IsStopped&&n.BackgroundExecutionException==null),"后台协议有故障");
            internal void Write(string name,object data)=>artifacts[0].WriteLabeledText(name,JsonConvert.SerializeObject(data,Formatting.Indented));
            public void Dispose()
            {
                foreach(var node in nodes)node.RequestStop();System.Threading.Tasks.Task.WhenAll(nodes.Select(n=>n.ExecutionStopped)).GetAwaiter().GetResult();
                Write("实际后台窗口与首现场",new{label,actorRecords=ActorRecords,actorApplied=ActorApplied,rejected=Rejected,timings=Timings,MissingCount,RangeCount,
                    faults=nodes.Select(n=>n.LastFault),states=sessions.Select(s=>s.State),queued=Messages.Select(m=>new{m.Kind,m.Target})});
            }
        }
    }
}
