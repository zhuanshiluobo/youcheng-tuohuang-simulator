using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Tests.EditMode
{
    // 隔离规则和线程测试源码；使用正式开局及真实规则请求，不计作正常可见程序验收。
    // 本批只编写、静态自审，未编译或运行。
    public sealed class ConcurrentInteractionValidityTests
    {
        [TestCase(InteractionRequestValidity.NormalCover)]
        [TestCase(InteractionRequestValidity.InitialEnterprise)]
        [TestCase(InteractionRequestValidity.NormalHarvest)]
        public void IndependentAnswerKeepsOriginalContractButOrdinarySubmitRemainsStrict(string profile)
        {
            var fixture = BuildProfile(profile);
            var command = Answer(Open(fixture, 1), "原确认");
            string payload = JsonConvert.SerializeObject(command);
            Assert.That(InteractionRequestValidity.TryReadRequestAuthority(fixture.State, command, out var original, out var validation), Is.True, validation.Reason);
            Assert.That(fixture.Session.Submit(Answer(Open(fixture, 2), "他人确认")).Succeeded, Is.True);
            Assert.That(InteractionRequestValidity.TryReadRequestAuthority(fixture.State, command, out var current, out validation), Is.True, validation.Reason);
            Assert.That(current.Digest, Is.EqualTo(original.Digest));
            Assert.That(InteractionRequestValidity.TryValidateAnswer(fixture.State, command, original.Digest, out validation), Is.True, validation.Reason);
            string before = JsonConvert.SerializeObject(fixture.State);
            // 即使普通调用者附上新标记，公开Submit仍不能选择新有序入口。
            AddMarker(command, original.Digest);
            var rejected = fixture.Session.Submit(command);
            Assert.That(rejected.Succeeded, Is.False);
            Assert.That(rejected.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before));
            command.Parameters.Remove(InteractionRequestValidity.VersionParameter);
            command.Parameters.Remove(InteractionRequestValidity.DigestParameter);
            Assert.That(JsonConvert.SerializeObject(command), Is.EqualTo(payload), "原CommandId、版本和答案不能被刷新或改签");
        }

        [TestCase(InteractionRequestValidity.NormalCover)]
        [TestCase(InteractionRequestValidity.InitialEnterprise)]
        [TestCase(InteractionRequestValidity.NormalHarvest)]
        public void ActualCandidateOwnershipOrEligibilityChangeRejectsOriginalContract(string profile)
        {
            var fixture = BuildProfile(profile);
            var command = Answer(Open(fixture, 1), "真实失效");
            Assert.That(InteractionRequestValidity.TryReadRequestAuthority(fixture.State, command, out var original, out _), Is.True);
            var changed = GameStateCloneService.DeepClone(fixture.State);
            if (profile == InteractionRequestValidity.NormalHarvest)
                changed.FindPlayer(1).ResourceCollectionStartGoldVoucher++;
            else
                changed.ItemZones.Items.Single(i => i.ItemId == command.OptionIds[0]).ControllerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = 2 };
            string before = JsonConvert.SerializeObject(changed);
            Assert.That(InteractionRequestValidity.TryValidateAnswer(changed, command, original.Digest, out var validation), Is.False);
            Assert.That(validation.IsValid, Is.False);
            Assert.That(JsonConvert.SerializeObject(changed), Is.EqualTo(before), "只读预检不得变更权威状态");
            var sequential = GameStateCloneService.DeepClone(fixture.State);
            sequential.RuleConfiguration.PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential;
            Assert.That(InteractionRequestValidity.TryReadRequestAuthority(sequential, command, out _, out _), Is.False);
        }

        [Test]
        public void NormalHarvestRetainsFrozenBudgetAndRejectsUnadvertisedRecipientAndScope()
        {
            var fixture = BuildProfile(InteractionRequestValidity.NormalHarvest);
            var request = Open(fixture, 1);
            var command = Answer(request, "正常采集原计划");
            Assert.That(InteractionRequestValidity.TryReadRequestAuthority(fixture.State, command, out var original, out _), Is.True);
            int start = fixture.State.FindPlayer(1).ResourceCollectionStartGoldVoucher;
            fixture.State.FindPlayer(1).Resources.GoldVoucher += 20;
            Assert.That(InteractionRequestValidity.TryValidateAnswer(fixture.State, command, original.Digest, out var validation), Is.True, validation.Reason);
            Assert.That(fixture.State.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(start));
            string scope = Text(request.PromptParameters, "scopeKey");
            command.OptionIds.Add(RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.RouteAndRecipient, "不存在的路线", "player:999"));
            Assert.That(InteractionRequestValidity.TryValidateAnswer(fixture.State, command, original.Digest, out validation), Is.False);
            command.OptionIds.Clear();
            command.OptionIds.Add(RouteSelectionAnswerCodec.Encode(new string('0', 64), RouteSelectionCandidateKind.Commit));
            Assert.That(InteractionRequestValidity.TryValidateAnswer(fixture.State, command, original.Digest, out validation), Is.False);

            // 缺少可信新接受事实时，旧计划公开入口保持原facts严格匹配。
            var owner = fixture.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
            var authority = new RouteSelectionSessionAuthority();
            Assert.That(authority.TryRestore(fixture.State, owner, out var session, out var binding).IsValid, Is.True);
            fixture.State.CurrentPlayerId = fixture.State.CurrentPlayerId == 3 ? 2 : 3;
            var map = new MapQueryService(StaticMapDefinitions.Resolve(fixture.State.MapId));
            var eligible = RouteResourceSelectionEffectExecutor.GetSelectableLocations(fixture.State, session, map)
                .Where(id => session.SelectableLocationUpperBound.Contains(id)).ToArray();
            var draft = new RouteSelectionDraft(session.SessionId, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RouteRecipientChoice>(), confirmEmptySelection: true);
            Assert.That(new RouteResourceSelectionService().TryValidatePlan(fixture.State, session, binding, draft, eligible, out _).IsValid, Is.False);
        }

        [TestCase(InteractionRequestValidity.NormalCover)]
        [TestCase(InteractionRequestValidity.InitialEnterprise)]
        [TestCase(InteractionRequestValidity.NormalHarvest)]
        public void OrderedEntryAppliesOriginalOldObservationOnceForEachRealProfile(string profile)
        {
            var fixture = BuildProfile(profile);
            var host = ReadyHost(fixture);
            host.StartBackgroundExecution();
            try
            {
                Await(host, s => s.ReadWindowPermissions().Any(w => w.IsPermitted));
                var local = Answer(Open(fixture, 1), "本人一次确认");
                string originalRevision = local.Parameters["expectedRevision"];
                Assert.That(host.TryCreateLocalProposal(local, out var original, out var validation), Is.True, validation.Reason);
                string raw = JsonConvert.SerializeObject(original);
                var other = Proposal(fixture.State, Answer(Open(fixture, 2), "先执行他人确认"));
                Assert.That(host.AdmitProposal(2, other).Status, Is.EqualTo(OperationAdmissionStatus.Accepted));
                Await(host, s => s.LastAppliedSequence == 1);
                var admitted = host.AdmitProposal(1, original);
                host.ReceiveProposalResult(admitted);
                Assert.That(admitted.Status, Is.EqualTo(OperationAdmissionStatus.Accepted), admitted.Reason);
                Await(host, s => s.LastAppliedSequence == 2);
                Assert.That(host.HasPendingLocalApplication, Is.False);
                Assert.That(host.AdmitProposal(1, original).Status, Is.EqualTo(OperationAdmissionStatus.Duplicate));
                Assert.That(JsonConvert.SerializeObject(original), Is.EqualTo(raw));
                Assert.That(original.Command.ToCommand().Parameters["expectedRevision"], Is.EqualTo(originalRevision));
                Assert.That(original.ObservedGlobalSequence, Is.Zero);
                Assert.That(fixture.State.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == local.Parameters["interactionId"]).Status, Is.EqualTo("answered"));
                if (profile == InteractionRequestValidity.NormalHarvest)
                    Assert.That(fixture.State.FindPlayer(1).HasCollectedResourcesThisRound, Is.True, "仅回答成功但重开选路请求不算实际采集完成");
                else if (profile == InteractionRequestValidity.NormalCover)
                    Assert.That(fixture.State.FindPlayer(1).CoveredCharacterCardId, Is.Not.Empty);
                else
                    Assert.That(fixture.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == RequestOwner(original, fixture.State)).FlowStage, Is.EqualTo("finished"));
            }
            finally { Stop(host); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PendingOriginalPayloadDoesNotConsumeNumbersAndEventuallyAppliesOrRetires(bool retireForRecovery)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var fixture = BuildProfile(InteractionRequestValidity.NormalCover);
            fixture.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.contract.hold", "CharacterCardCovered", "*", "test.contract.hold.handler", context => {
                if (context.Event.PlayerId == 2) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("隔离夹具未释放真实盖放业务"); }
                return new List<EffectSpec>();
            }));
            var host = ReadyHost(fixture);
            var finals = new List<SimulationProposalResultDto>();
            int rejected = 0;
            host.SimulationProposalResultProduced += r => finals.Add(r);
            host.SimulationProposalRejected += _ => rejected++;
            host.StartBackgroundExecution();
            try
            {
                Await(host, s => s.ReadWindowPermissions().Any(w => w.IsPermitted));
                Assert.That(host.TryCreateLocalProposal(Answer(Open(fixture, 1), "原待准入"), out var original, out _), Is.True);
                string raw = JsonConvert.SerializeObject(original);
                Assert.That(host.AdmitProposal(2, Proposal(fixture.State, Answer(Open(fixture, 2), "阻塞中的他人"))).Status, Is.EqualTo(OperationAdmissionStatus.Accepted));
                Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                var waiting = host.AdmitProposal(1, original); host.ReceiveProposalResult(waiting);
                Assert.That(waiting.Status, Is.EqualTo(OperationAdmissionStatus.PendingValidation));
                Assert.That(host.AdmitProposal(1, original).Status, Is.EqualTo(OperationAdmissionStatus.PendingValidation));
                Assert.That(host.LastSequencedSequence, Is.EqualTo(1));
                Assert.That(waiting.ExpectedClientSubmissionSequence, Is.EqualTo(1));
                Assert.That(host.HasPendingLocalApplication, Is.True);
                Assert.That(rejected, Is.Zero); Assert.That(finals, Is.Empty);
                if (retireForRecovery)
                {
                    var remote = Proposal(fixture.State, Answer(Open(fixture, 3), "远端原待准入"));
                    Assert.That(host.AdmitProposal(3, remote).Status, Is.EqualTo(OperationAdmissionStatus.PendingValidation));
                    host.BeginConfirmedRecovery();
                    Assert.That(finals.Count, Is.EqualTo(2));
                    Assert.That(finals.All(r => r.Status == OperationAdmissionStatus.Rejected && r.RequiresRefresh), Is.True);
                    Assert.That(finals.Single(r => r.Proposal.Command.PlayerId == 1).ExpectedClientSubmissionSequence, Is.EqualTo(1));
                    Assert.That(rejected, Is.EqualTo(1));
                    Assert.That(host.HasPendingLocalApplication, Is.False);
                }
                else
                {
                    release.Set(); Await(host, s => s.LastAppliedSequence == 2);
                    Assert.That(finals.Count, Is.EqualTo(1));
                    Assert.That(finals[0].Status, Is.EqualTo(OperationAdmissionStatus.Accepted));
                    Assert.That(finals[0].OrderedCommand.ClientSubmissionSequence, Is.EqualTo(1));
                    Assert.That(rejected, Is.Zero);
                }
                Assert.That(JsonConvert.SerializeObject(original), Is.EqualTo(raw));
            }
            finally { release.Set(); Stop(host); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StopOrPayloadConflictRetiresPendingAndInvalidObservationNeverGetsBusinessNumber(bool conflict)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var fixture = BuildProfile(InteractionRequestValidity.NormalCover);
            fixture.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.contract.stop", "CharacterCardCovered", "*", "test.contract.stop.handler", context => {
                if (context.Event.PlayerId == 2) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
                return new List<EffectSpec>();
            }));
            var host = ReadyHost(fixture); var finals = new List<SimulationProposalResultDto>();
            host.SimulationProposalResultProduced += finals.Add;
            host.StartBackgroundExecution();
            try
            {
                Await(host, s => s.ReadWindowPermissions().Any(w => w.IsPermitted));
                Assert.That(host.TryCreateLocalProposal(Answer(Open(fixture, 1), "停止中的原确认"), out var original, out _), Is.True);
                var future = SnapshotProposal(original); future.ObservedGlobalSequence = 100;
                Assert.That(host.AdmitProposal(1, future).Status, Is.EqualTo(OperationAdmissionStatus.Rejected));
                var forged = SnapshotProposal(original); forged.ObservedStateRevision++;
                Assert.That(host.AdmitProposal(1, forged).Status, Is.EqualTo(OperationAdmissionStatus.Rejected));
                var wrongWindow = SnapshotProposal(original); wrongWindow.OperationWindowId += ".invalid";
                Assert.That(host.AdmitProposal(1, wrongWindow).Status, Is.EqualTo(OperationAdmissionStatus.Rejected));
                Assert.That(host.LastSequencedSequence, Is.Zero);
                host.AdmitProposal(2, Proposal(fixture.State, Answer(Open(fixture, 2), "停止夹具他人")));
                Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                host.ReceiveProposalResult(host.AdmitProposal(1, original));
                var gap = SnapshotProposal(original); gap.ClientSubmissionSequence++;
                Assert.That(host.AdmitProposal(1, gap).Status, Is.EqualTo(OperationAdmissionStatus.MissingSubmission));
                if (conflict)
                {
                    var changed = SnapshotProposal(original); changed.Command.TargetId = "冲突载荷";
                    Assert.That(host.AdmitProposal(1, changed).Status, Is.EqualTo(OperationAdmissionStatus.Rejected));
                }
                else host.RequestStop();
                host.RequestStop();
                Assert.That(finals.Count, Is.EqualTo(1));
                Assert.That(finals[0].Proposal.Command.CommandId, Is.EqualTo(original.Command.CommandId));
                Assert.That(finals[0].Status, Is.EqualTo(OperationAdmissionStatus.Rejected));
                Assert.That(host.LastSequencedSequence, Is.EqualTo(1));
            }
            finally { release.Set(); Stop(host); }
        }

        [Test]
        public void LegacyProposalCopyDoesNotAddMarkersAndMarkerCannotBeHalfPresent()
        {
            var fixture = BuildProfile(InteractionRequestValidity.NormalCover);
            var command = Answer(Open(fixture, 1), "旧载荷");
            var old = Proposal(fixture.State, command, false);
            string digest = OperationCommandCanonicalizer.CanonicalProposalDigest(old);
            string raw = JsonConvert.SerializeObject(old);
            Assert.That(InteractionRequestValidity.TryReadMarker(old.Command.ToCommand(), out bool present, out _, out _), Is.True);
            Assert.That(present, Is.False);
            Assert.That(OperationCommandCanonicalizer.CanonicalProposalDigest(SnapshotProposal(old)), Is.EqualTo(digest));
            Assert.That(JsonConvert.SerializeObject(old), Is.EqualTo(raw));
            command.Parameters[InteractionRequestValidity.VersionParameter] = "1";
            Assert.That(InteractionRequestValidity.TryReadMarker(command, out present, out _, out _), Is.False);
            Assert.That(present, Is.True);
            Assert.That(OperationCommandProtocol.Version, Is.EqualTo(1));
        }

        // 测试程序集只经准确签名调用真实internal快照，不扩大生产API或另造复制规则。
        private static OperationCommandProposalDto SnapshotProposal(OperationCommandProposalDto source)
        {
            var method = typeof(OperationCommandCanonicalizer).GetMethod("SnapshotProposal",
                BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(OperationCommandProposalDto) }, null);
            Assert.That(method, Is.Not.Null, "真实internal提案快照入口必须存在");
            return (OperationCommandProposalDto)method.Invoke(null, new object[] { source });
        }

        private sealed class Fixture
        {
            internal GameSession Session;
            internal EffectRegistry Registry;
            internal GameState State => Session.State;
        }

        private static Fixture BuildProfile(string profile)
        {
            var fixture = Build(profile == InteractionRequestValidity.InitialEnterprise);
            if (profile == InteractionRequestValidity.InitialEnterprise) return fixture;
            for (int guard = 0; fixture.State.Phase != GamePhase.CharacterCover && guard < 100; guard++)
            {
                var request = fixture.State.EffectRuntime.InteractionRequests.FirstOrDefault(r => r.Status == "open" && !r.IsInternalMainlineCompletion());
                Assert.That(request, Is.Not.Null, "正式开局必须经真实请求进入盖放");
                var result = fixture.Session.Submit(Answer(request, "开局必要输入-" + guard));
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            }
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.CharacterCover));
            if (profile == InteractionRequestValidity.NormalCover) return fixture;
            while (fixture.State.Phase == GamePhase.CharacterCover)
            {
                var request = fixture.State.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId);
                var result = fixture.Session.Submit(Answer(request, "采集夹具盖放-" + request.AnsweringPlayerId));
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            }
            var round = new RoundExecutionService(fixture.Registry);
            for (int guard = 0; (fixture.State.Phase == GamePhase.ActionRound1 || fixture.State.Phase == GamePhase.ActionRound2) && guard < 12; guard++)
            {
                var advanced = round.CompleteMainAction(fixture.State, fixture.State.CurrentPlayerId);
                Assert.That(advanced.IsValid, Is.True, advanced.Reason);
            }
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(Open(fixture, 1).InteractionTypeId, Is.EqualTo("route_resource.selection"));
            return fixture;
        }

        private static Fixture Build(bool enterprise)
        {
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var instance = contextType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var previous = instance.GetValue(null); var setter = instance.GetSetMethod(true); GameObject root = null;
            try
            {
                setter.Invoke(null, new object[] { null }); root = new GameObject("并发请求有效性隔离夹具");
                var context = root.AddComponent(contextType);
                var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3,
                    EnabledExpansionIds = enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" } };
                var order = new[] { 1, 2, 3 };
                var seats = order.Select(p => new PlayerSeat { PlayerId = p, PlayerName = "玩家" + p }).ToList();
                const string game = "并发请求有效性隔离局";
                contextType.GetMethods().Single(m => m.Name == "Configure" && m.GetParameters().Length == 9).Invoke(context,
                    new object[] { LaunchMode.Host, 1, "并发隔离房间", seats, config, order, game, 42, GameplayRandomRuntime.Create(game) });
                var result = bootstrapper.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Single(m => m.Name == "Build" && m.GetParameters().Length == 3).Invoke(null, new object[] { context, false, false });
                return new Fixture { Session = (GameSession)result.GetType().GetField("Session").GetValue(result), Registry = (EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result) };
            }
            finally { if (root != null) UnityEngine.Object.DestroyImmediate(root); setter.Invoke(null, new[] { previous }); }
        }

        private static InteractionRequest Open(Fixture fixture, int player) => fixture.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" &&
            r.AnsweringPlayerId == player && (r.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId || r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction || r.InteractionTypeId == "route_resource.selection"));
        private static string RequestOwner(OperationCommandProposalDto proposal, GameState state) => state.EffectRuntime.InteractionRequests
            .Single(r => r.GetStableInteractionId() == proposal.Command.ToCommand().Parameters["interactionId"]).OwnerEffectId;
        private static string Text(NormalizedValue value, string key) => value.Properties.Single(p => p.Name == key).Value.StringValue;
        private static GameCommand Answer(InteractionRequest request, string id)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, CommandId = id, PlayerId = request.AnsweringPlayerId };
            command.Parameters["interactionId"] = request.GetStableInteractionId(); command.Parameters["expectedRevision"] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            if (request.AnswerSchema == "boolean_accept") command.Parameters["answerValue"] = request.AllowDecline ? "false" : "true";
            else if (request.InteractionTypeId == "route_resource.selection") command.OptionIds.Add(RouteSelectionAnswerCodec.Encode(Text(request.PromptParameters, "scopeKey"), RouteSelectionCandidateKind.Commit));
            else command.OptionIds.AddRange(request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
            return command;
        }
        private static void AddMarker(GameCommand command, string digest)
        { command.Parameters[InteractionRequestValidity.VersionParameter] = "1"; command.Parameters[InteractionRequestValidity.DigestParameter] = digest; }
        private static OperationCommandProposalDto Proposal(GameState state, GameCommand source, bool marker = true)
        {
            var command = GameCommandDto.FromCommand(source).ToCommand();
            Assert.That(OperationWindowService.TryResolveCommandContext(state, command, out var context, out var validation), Is.True, validation.Reason);
            if (marker) { Assert.That(InteractionRequestValidity.TryReadRequestAuthority(state, command, out var authority, out validation), Is.True, validation.Reason); AddMarker(command, authority.Digest); }
            return new OperationCommandProposalDto { GameId = state.GameId, SimulationEpoch = 1, MembershipVersion = 1, ClientSubmissionSequence = 1,
                ObservedGlobalSequence = 0, ObservedStateRevision = state.EffectRuntime.StateRevision, OperationWindowId = context.OperationWindowId,
                WindowGeneration = context.WindowGeneration, Command = GameCommandDto.FromCommand(command) };
        }
        private static DeterministicSimulationCoordinator ReadyHost(Fixture fixture)
        {
            var players = new[] { 1, 2, 3 };
            var host = new DeterministicSimulationCoordinator(fixture.Session, DeterministicSimulationSessionOptions.CreateInitial(true, 1, fixture.Session, players));
            host.ConfigureRequestContractValidation(1); host.Begin();
            var boundary = host.ReadBoundary(0);
            foreach (int player in players.Skip(1))
            {
                // 副本也从同一真实规则状态计算边界；不手填窗口或伪造许可事实。
                var session = new GameSession(GameStateCloneService.DeepClone(fixture.State));
                var replica = new DeterministicSimulationCoordinator(session, DeterministicSimulationSessionOptions.CreateInitial(false, player, session, players));
                replica.Begin(); Assert.That(replica.ReceiveBoundary(boundary), Is.True);
                Assert.That(host.ReceiveWindowReady(player, new SimulationWindowReadyDto { GameId = boundary.GameId, SimulationEpoch = 1, MembershipVersion = 1,
                    PlayerId = player, ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(player), Boundary = replica.ReadBoundary(0) }), Is.True);
            }
            return host;
        }
        private static SimulationPublishedSnapshot Await(DeterministicSimulationCoordinator host, Func<SimulationPublishedSnapshot, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                host.DrainMainThreadNotifications(64);
                if (host.TryReadPublishedSnapshot(out var snapshot) && condition(snapshot)) return snapshot;
                Assert.That(host.IsStopped, Is.False, host.LastFault?.Diagnostic); Thread.Sleep(1);
            }
            Assert.Fail("固定发布未在夹具有界时间内达到期望事实"); return null;
        }
        private static void Stop(DeterministicSimulationCoordinator host)
        { host.RequestStop(); Assert.That(host.ExecutionStopped.Wait(TimeSpan.FromSeconds(10)), Is.True); }
    }
}
