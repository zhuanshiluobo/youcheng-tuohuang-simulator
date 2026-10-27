using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
#if !YC_AUTHORITY_PUBLICATION_OFFLINE
using NUnit.Framework;
#endif

namespace YC.Tests.EditMode
{
    // 规则/发布隔离夹具：复用既有legacy地图测试形状和正式主链推进，不计正常UI/存档恢复验收。
    public sealed class HarvestAuthorityPublicationTests
    {
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        internal static GameState HarvestState()
        {
            var state = new GameState { GameId = "r25-authority-publication-unit", ContentPackHash = "r25-unit-content", Phase = GamePhase.ActionRound1,
                Round = 1, CurrentPlayerId = 1, StartPlayerId = 1, UseSeatTurnOrder = true, LaunchPlayerOrder = new List<int> { 1, 2, 3 }, MapId = StaticMapDefinitions.FourPlayerMapId };
            for (int player = 1; player <= 3; player++) state.Players.Add(new PlayerState { PlayerId = player, CityLocationId = new[] { "A-01", "B-01", "C-01" }[player - 1] });
            state.RuleConfiguration = new GameRuleConfiguration { MapId = state.MapId, PlayerCount = 3, ContentPackHash = state.ContentPackHash,
                PlayerStageSchedulingMode = PlayerStageSchedulingMode.Concurrent };
            var registry = new EffectRegistry(); RouteResourceSelectionEffectExecutor.Register(registry); var round = new RoundExecutionService(registry);
            Require(round.PrepareActionWindowForSmoke(state, 1).IsValid, "主链隔离入口失败");
            for (int step = 0; step < 8 && state.Phase != GamePhase.ResourceCollection; step++)
            { var result = round.CompleteMainAction(state, state.CurrentPlayerId); Require(result.IsValid, result.Reason); }
            Require(state.Phase == GamePhase.ResourceCollection && state.EffectRuntime.InteractionRequests.Count(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection") == 3,
                "正式主链未生成三个采集请求");
            return state;
        }
        private sealed class Group : IDisposable
        {
            internal readonly GameSession[] Sessions;
            internal readonly DeterministicSimulationCoordinator[] Owners;
            internal readonly SimulationBoundaryDto Boundary;
            internal bool HoldThirdOrdered;
            internal readonly List<OrderedOperationCommandDto> HeldOrdered = new List<OrderedOperationCommandDto>();
            internal DeterministicSimulationCoordinator Host => Owners[0];
            internal Group(bool permit)
            {
                var original = HarvestState(); Sessions = new GameSession[3]; Owners = new DeterministicSimulationCoordinator[3];
                try
                {
                for (int i = 0; i < 3; i++)
                {
                    var registry = new EffectRegistry(); RouteResourceSelectionEffectExecutor.Register(registry); var round = new RoundExecutionService(registry);
                    var session = Sessions[i] = new GameSession(GameStateCloneService.DeepClone(original));
                    session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
                    Owners[i] = new DeterministicSimulationCoordinator(session, DeterministicSimulationSessionOptions.CreateInitial(i == 0, i + 1, session, new[] { 1, 2, 3 }));
                    Owners[i].ConfigureRequestContractValidation(1); Owners[i].Begin(); Require(!Owners[i].IsStopped, Owners[i].LastFault?.Diagnostic);
                }
                Host.WindowPermitProduced += value => { foreach (var owner in Owners.Skip(1)) owner.ReceiveWindowPermit(value); };
                Host.OrderedCommandProduced += value => {
                    Owners[1].ReceiveOrdered(value);
                    if (HoldThirdOrdered) HeldOrdered.Add(JsonConvert.DeserializeObject<OrderedOperationCommandDto>(JsonConvert.SerializeObject(value)));
                    else Owners[2].ReceiveOrdered(value);
                };
                Host.CommandValidationPrefixAdvanced += value => { foreach (var owner in Owners.Skip(1)) owner.ReceiveValidationPrefix(value); };
                Boundary = Host.ReadBoundary(0);
                foreach (var owner in Owners.Skip(1)) Require(owner.ReceiveBoundary(Boundary), "副本初始边界不同");
                if (permit) { Require(Host.ReceiveWindowReady(2, Ready(2)), "副本2未就绪"); Require(Host.ReceiveWindowReady(3, Ready(3)), "副本3未就绪"); }
                foreach (var owner in Owners) owner.StartBackgroundExecution();
                Await(() => Owners.All(owner => owner.TryReadPublishedSnapshot(out var snapshot) && snapshot.StateRevision == original.EffectRuntime.StateRevision));
                }
                catch { Dispose(); throw; }
            }
            internal SimulationWindowReadyDto Ready(int player) => new SimulationWindowReadyDto { GameId = Boundary.GameId, SimulationEpoch = Boundary.SimulationEpoch,
                MembershipVersion = Boundary.MembershipVersion, PlayerId = player, ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(player), Boundary = Owners[player - 1].ReadBoundary(0) };
            internal void Await(Func<bool> ready)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    foreach (var owner in Owners) owner.DrainMainThreadNotifications(64);
                    if (ready()) return;
                    foreach (var owner in Owners) Require(!owner.IsStopped, owner.LastFault?.Diagnostic);
                    Thread.Sleep(1);
                }
                throw new TimeoutException("发布夹具10秒内未完成");
            }
            internal OperationCommandProposalDto Proposal(int player, string id)
            {
                var request = Sessions[player - 1].State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.AnsweringPlayerId == player && r.InteractionTypeId == "route_resource.selection");
                string scope = request.PromptParameters.Properties.Single(v => v.Name == "scopeKey").Value.StringValue;
                var command = new GameCommand { CommandId = id, Kind = GameCommandKind.AnswerInteraction, PlayerId = player };
                command.Parameters["interactionId"] = request.GetStableInteractionId(); command.Parameters["expectedRevision"] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
                command.OptionIds.Add(RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Commit));
                Require(Owners[player - 1].TryCreateLocalProposal(command, out var proposal, out var validation), validation.Reason); return proposal;
            }
            public void Dispose()
            {
                foreach (var owner in Owners.Where(owner => owner != null)) owner.RequestStop();
                foreach (var owner in Owners.Where(owner => owner != null)) Require(owner.ExecutionStopped.Wait(TimeSpan.FromSeconds(10)), "执行owner未退出");
            }
        }

#if !YC_AUTHORITY_PUBLICATION_OFFLINE
        [Test]
#endif
        public void UnreadyAndStaleMessagesNeverOpenCollectionButCurrentPermitIsDelivered()
        {
            using var group = new Group(false); var host = group.Host;
            Require(host.TryReadPublishedSnapshot(out var first) && first.ReadWindowPermissions().All(p => !p.CanSubmitForLocalPlayer), "缺许可时放行");
            var stale = group.Ready(2); stale.SimulationEpoch++;
            Require(!host.ReceiveWindowReady(2, stale), "接受旧/异代消息");
            Require(host.ReceiveWindowReady(2, group.Ready(2)), "当前副本2拒绝");
            long version = first.Version; group.Await(() => host.TryReadPublishedSnapshot(out var current) && current.Version > version);
            Require(host.ReadWindowPermissions().All(p => !p.CanSubmitForLocalPlayer), "缺副本3就提前放行");
            Require(host.ReceiveWindowReady(3, group.Ready(3)), "当前副本3拒绝");
            group.Await(() => host.ReadWindowPermissions().Any(p => p.CanSubmitForLocalPlayer));
            Require(host.TryReadPublishedSnapshot(out var permitted), "许可未发布");
            var exposed = permitted.ReadWindowPermissions().First(p => p.IsOpen); exposed.AvailablePlayerIds.Clear(); exposed.Window.AuthorizedPlayerIds.Clear();
            Require(permitted.ReadWindowPermissions().Any(p => p.CanSubmitForLocalPlayer && p.AvailablePlayerIds.Count > 0), "公开副本污染冻结许可");
            group.Proposal(1, "许可后本人确认");
        }

#if !YC_AUTHORITY_PUBLICATION_OFFLINE
        [Test]
#endif
        public void OtherActorsKeepOriginalAuthorityWhileCompletedActorAndInvalidCandidateAreRejected()
        {
            using var group = new Group(true); var host = group.Host;
            var first = group.Proposal(1, "采集本人1"); var second = group.Proposal(2, "采集本人2原观察");
            string originalSecond = JsonConvert.SerializeObject(second);
            var invalid = JsonConvert.DeserializeObject<OperationCommandProposalDto>(originalSecond); invalid.Command.CommandId = "非法候选"; invalid.Command.OptionIds = new List<string> { "不存在的候选" };
            Require(host.AdmitProposal(2, invalid).Status == OperationAdmissionStatus.Rejected && host.LastSequencedSequence == 0, "非法候选消耗正式序号");
            Require(host.AdmitProposal(1, first).Status == OperationAdmissionStatus.Accepted, "本人1未准入");
            group.Await(() => group.Owners.All(owner => owner.LastAppliedSequence == 1));
            var stale = JsonConvert.DeserializeObject<OperationCommandProposalDto>(JsonConvert.SerializeObject(first)); stale.Command.CommandId = "已答旧请求"; stale.ClientSubmissionSequence = 2;
            Require(host.AdmitProposal(1, stale).Status == OperationAdmissionStatus.Rejected, "新冻结图仍放行已答请求");
            Require(host.AdmitProposal(2, second).Status == OperationAdmissionStatus.Accepted, "他人原观察有效合同被不必要刷新");
            group.Await(() => group.Owners.All(owner => owner.LastAppliedSequence == 2));
            Require(JsonConvert.SerializeObject(second) == originalSecond, "原参数被重签/修改");
            Require(group.Sessions.Select(session => RuleStateDigestService.Compute(session.State)).Distinct().Count() == 1, "三个副本结果分叉");
            Require(host.AdmitProposal(2, second).Status == OperationAdmissionStatus.Duplicate, "重复输入重新执行");
        }

#if !YC_AUTHORITY_PUBLICATION_OFFLINE
        [Test]
#endif
        public void RecoveryAndStoppedGenerationKeepSubmissionClosed()
        {
            using var group = new Group(true); var host = group.Host;
            host.BeginConfirmedRecovery();
            group.Await(() => host.TryReadPublishedSnapshot(out var snapshot) && snapshot.IsRecoveringConfirmedPrefix);
            Require(host.ReadWindowPermissions().All(p => !p.CanSubmitForLocalPlayer), "恢复期间旧合同导致放行");
            var state = group.Sessions[0].State;
            var request = state.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.AnsweringPlayerId == 1);
            var command = new GameCommand { CommandId = "恢复中禁止提交", Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters["interactionId"] = request.GetStableInteractionId(); command.Parameters["expectedRevision"] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            Require(!host.TryCreateLocalProposal(command, out _, out _), "恢复期间创建新提案");
            host.RequestStop(); Require(host.ExecutionStopped.Wait(TimeSpan.FromSeconds(10)), "停止未完成");
            Require(!host.TryCreateLocalProposal(command, out _, out _), "旧代次退出后重新放行");
        }

#if !YC_AUTHORITY_PUBLICATION_OFFLINE
        [Test]
#endif
        public void OutOfOrderLogAndValidationKeepOriginalResultsAndDuplicateIsNotExecuted()
        {
            using var group = new Group(true); group.HoldThirdOrdered = true;
            var first = group.Proposal(1, "乱序1"); var second = group.Proposal(2, "乱序2");
            Require(group.Host.AdmitProposal(1, first).Status == OperationAdmissionStatus.Accepted, "乱序首笔未接纳");
            group.Await(() => group.Owners.Take(2).All(owner => owner.LastAppliedSequence == 1));
            Require(group.Host.AdmitProposal(2, second).Status == OperationAdmissionStatus.Accepted, "乱序后笔原观察未接纳");
            group.Await(() => group.Owners.Take(2).All(owner => owner.LastAppliedSequence == 2));
            Require(group.HeldOrdered.Count == 2 && group.Owners[2].LastAppliedSequence == 0, "隔离网络未按预期保留原件");
            group.Owners[2].ReceiveOrdered(group.HeldOrdered[1]);
            group.Await(() => group.Owners[2].TryReadPublishedSnapshot(out var snapshot) && snapshot.ReadPendingTail().Any(c => c.GlobalCommandSequence == 2));
            Require(group.Owners[2].LastAppliedSequence == 0, "缺号时跨过正式顺序");
            group.Owners[2].ReceiveOrdered(group.HeldOrdered[0]);
            group.Await(() => group.Owners.All(owner => owner.LastAppliedSequence == 2));
            string digest = RuleStateDigestService.Compute(group.Sessions[2].State);
            Require(group.Owners[2].ReceiveOrdered(group.HeldOrdered[1]).Status == OperationReplicaStatus.Duplicate, "重复原件未识别");
            Require(RuleStateDigestService.Compute(group.Sessions[2].State) == digest && group.Sessions.Select(session => RuleStateDigestService.Compute(session.State)).Distinct().Count() == 1,
                "乱序/重复或提前validation改变原结果");
        }
    }
}
