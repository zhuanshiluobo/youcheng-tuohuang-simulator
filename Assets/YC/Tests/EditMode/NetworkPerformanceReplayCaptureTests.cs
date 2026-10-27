using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if !YC_REPLAY_CAPTURE_OFFLINE
using NUnit.Framework;
#endif
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    // 诊断数据/队列及合法规则隔离夹具；不是旧R2输入补造、正常UI或四端验收。
    public sealed class NetworkPerformanceReplayCaptureTests
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).First(type => type != null);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, All).Invoke(target, args);
        private static object Get(object target, string name) => target.GetType().GetProperty(name, All).GetValue(target);
        private static string NewRoot()
        {
            string path = Path.Combine(Environment.GetEnvironmentVariable("QY008_R24_OUTPUT") ?? Path.GetTempPath(), "R24-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        private static (GameSession, DeterministicSimulationCoordinator, DeterministicSimulationSessionOptions) Owner(bool host = true)
        {
            var state = new GameState { GameId = "r24-diagnostics-unit", ContentPackHash = "r24-test-content" };
            state.Players.Add(new PlayerState { PlayerId = 1 }); var session = new GameSession(state);
            var options = DeterministicSimulationSessionOptions.CreateInitial(host, 1, session, new[] { 1 });
            return (session, new DeterministicSimulationCoordinator(session, options), options);
        }
        private static object Capture(string root, DeterministicSimulationSessionOptions options, long target = 999)
            => Find("YC.Infrastructure.Multiplayer.NetworkPerformanceReplayCapture").GetMethod("Create", All).Invoke(null, new object[] { root, options, target });
        private static string Finish(object capture, string root)
        {
            Call(capture, "CompleteAfter", Task.CompletedTask, "unit_end");
            Require(((Task)Get(capture, "Completion")).Wait(TimeSpan.FromSeconds(10)), "诊断退出没有有界完成");
            return Directory.GetDirectories(root).Single();
        }
        private static OrderedOperationCommandDto Ordered(long sequence)
        {
            var command = new OrderedOperationCommandDto { GameId = "r24-diagnostics-unit", SimulationEpoch = 1, MembershipVersion = 1,
                ClientSubmissionSequence = sequence, ObservedGlobalSequence = sequence - 1, ObservedStateRevision = 348,
                OperationWindowId = "unit.window", WindowGeneration = 1, GlobalCommandSequence = sequence, WindowCommandSequence = sequence,
                Command = new GameCommandDto { CommandId = "unit.command." + sequence, Kind = GameCommandKind.AnswerInteraction, PlayerId = 1,
                    SourceId = "unit.source", TargetId = "unit.target", OptionIds = new List<string> { "candidate.1", "candidate.2" },
                    Parameters = new List<GameCommandParameterDto> { new GameCommandParameterDto { Key = "expectedRevision", Value = "348" },
                        new GameCommandParameterDto { Key = "interactionId", Value = "unit.interaction" }, new GameCommandParameterDto { Key = "CandidateSetVersion", Value = "1" },
                        new GameCommandParameterDto { Key = "version", Value = "unit.original" } } } };
            command.ProposalDigest = OperationCommandCanonicalizer.CanonicalProposalDigest(command);
            command.OrderedCommandDigest = OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(command); return command;
        }
        private static object Begin(DeterministicSimulationCoordinator owner, GameSession session, OrderedOperationCommandDto ordered)
            => typeof(DeterministicSimulationCoordinator).GetNestedType("ProtocolExecutionSample", BindingFlags.NonPublic)
                .GetConstructor(All, null, new[] { typeof(ExecutionTimingCounters), typeof(string), typeof(DeterministicSimulationCoordinator), typeof(OrderedOperationCommandDto) }, null)
                .Invoke(new object[] { session.ExecutionTimings, "pump.apply", owner, ordered });
        private static void End(object scope, OperationCommandReplicaResult result)
            => scope.GetType().GetMethod("Dispose", All, null, new[] { typeof(OperationCommandReplicaResult) }, null).Invoke(scope, new object[] { result });
        private static OperationCommandReplicaResult Result(string message = null)
        {
            var result = new OperationCommandReplicaResult();
            typeof(OperationCommandReplicaResult).GetProperty("Status").SetValue(result, OperationReplicaStatus.Applied);
            if (message != null) typeof(OperationCommandReplicaResult).GetProperty("RuleResult").SetValue(result, CommandResult.SuccessResult(null, message));
            return result;
        }

#if !YC_REPLAY_CAPTURE_OFFLINE
        [Test]
#endif
        public void FullOrderedSnapshotAndOwnerCounterDeltaCoverHostAndClient()
        {
            foreach (bool host in new[] { true, false })
            {
                var (session, owner, options) = Owner(host); string root = NewRoot(); var capture = Capture(root, options);
                var sink = (Func<DeterministicSimulationCoordinator.PerformanceReplaySample, bool>)Delegate.CreateDelegate(
                    typeof(Func<DeterministicSimulationCoordinator.PerformanceReplaySample, bool>), capture, capture.GetType().GetMethod("RecordApply", All));
                owner.ConfigurePerformanceReplayCapture(sink, 999);
                var input = Ordered(1); var expected = JToken.FromObject(input); var scope = Begin(owner, session, input);
                input.Command.Parameters[0].Value = "999"; input.Command.OptionIds.Clear(); input.Command.TargetId = "mutated";
                session.ExecutionTimings.AddSample("submit.clone", 11); session.ExecutionTimings.AddSample("handler.copy", 23);
                var result = Result("unit-original-output");
                End(scope, result); typeof(OperationCommandReplicaResult).GetProperty("RuleResult").SetValue(result, CommandResult.SuccessResult(null, "mutated-after-capture"));
                string directory = Finish(capture, root); var row = JObject.Parse(File.ReadAllLines(Path.Combine(directory, "inputs.jsonl")).Single());
                Require(JToken.DeepEquals(row["Ordered"], expected), "完整DTO字段/答案/摘要被改写或遗漏");
                Require((long)row["CounterDelta"]["submit.clone.calls"] == 1 && (long)row["CounterDelta"]["submit.clone.ticks"] == 11 &&
                    (long)row["CounterDelta"]["handler.copy.ticks"] == 23 && (long)row["CounterDelta"]["pump.apply.calls"] == 1, "已有计数没有按原owner边界留存");
                Require((long)row["EndedTick"] >= (long)row["StartedTick"] && (bool)row["IsHost"] == host &&
                    (string)row["Result"]["RuleResult"]["LogMessage"] == "unit-original-output", "时钟/端身份/独立输出不正确");
                owner.ConfigurePerformanceReplayCapture(null);
            }
        }

#if !YC_REPLAY_CAPTURE_OFFLINE
        [Test]
#endif
        public void InitialFactoryAndInstallerPreserveOriginalEpochAndBytes()
        {
            foreach (bool host in new[] { true, false })
            {
                var (_, _, options) = Owner(host); string root = NewRoot(); var capture = Capture(root, options); const string json = "{\"仅诊断包装单测\":\"原始字节\"}";
                if (host)
                {
                    int calls = 0; var factory = (Func<string>)Call(capture, "WrapInitialFactory", (Func<string>)(() => { calls++; return json; }));
                    Require(factory() == json && calls == 1, "为捕获重复调用初始工厂");
                }
                else
                {
                    var original = new EchoInstaller(); var wrapped = (ISimulationCheckpointInstaller)Call(capture, "WrapInstaller", original);
                    Require(wrapped.TryInstall("unit.initial", json, out _) && original.Json == json && original.Calls == 1, "安装参数/返回值改变");
                }
                var envelope = new SimulationCheckpointEnvelope { GameId = options.GameId, ContentPackHash = options.ContentPackHash, SimulationEpoch = 1,
                    MembershipVersion = 1, GlobalCommandSequence = 0, CheckpointId = "unit.initial" };
                Call(capture, "CaptureInitial", envelope); envelope.SimulationEpoch = 9;
                string directory = Finish(capture, root); var manifest = JObject.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
                Require(File.ReadAllText(Path.Combine(directory, "initial-archive.json")) == json && (long)manifest["InitialEnvelope"]["SimulationEpoch"] == 1,
                    "原epoch或初始字节被修改/引用污染");
            }
        }
        private sealed class EchoInstaller : ISimulationCheckpointInstaller
        {
            internal int Calls; internal string Json;
            public bool TryInstall(string id, string json, out InitialSimulationCheckpointInstallResult result)
            { Calls++; Json = json; result = null; return true; }
        }

#if !YC_REPLAY_CAPTURE_OFFLINE
        [Test]
#endif
        public void DuplicateAndRecordLimitRemainBoundedAndExplicitlyTruncated()
        {
            var (_, _, options) = Owner(); string root = NewRoot(); var capture = Capture(root, options);
            string first = JsonConvert.SerializeObject(Ordered(1));
            for (int i = 0; i < 10; i++) Call(capture, "RecordProtocol", "ordered", first, "unit");
            for (int i = 2; i <= 129; i++) Call(capture, "RecordProtocol", "ordered", JsonConvert.SerializeObject(Ordered(i)), "unit");
            string directory = Finish(capture, root); var manifest = JObject.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            Require((int)manifest["RecordCount"] == 128 && (bool)manifest["Truncated"] && (string)manifest["StopReason"] == "record_limit", "128条/去重/截断约束不成立");
            Require(File.ReadAllLines(Path.Combine(directory, "inputs.jsonl")).Length == 128, "落盘重复/无界增长");
        }

#if !YC_REPLAY_CAPTURE_OFFLINE
        [Test]
#endif
        public void ByteLimitAndIoFailureCannotEscapeDiagnosticBoundary()
        {
            var (_, _, options) = Owner(); string root = NewRoot(); var capture = Capture(root, options);
            Call(capture, "RecordProtocol", "ordered", JsonConvert.SerializeObject(new { Padding = new string('x', 8 * 1024 * 1024) }), "unit.boundary");
            string directory = Finish(capture, root); var manifest = JObject.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            Require((bool)manifest["Truncated"] && Directory.GetFiles(directory).Sum(path => new FileInfo(path).Length) <= 8 * 1024 * 1024, "8MiB上限失效");
            string badRoot = Path.Combine(NewRoot(), "作为文件的输出目录"); File.WriteAllText(badRoot, "unit");
            var bad = Capture(badRoot, options); Call(bad, "RecordProtocol", "ordered", JsonConvert.SerializeObject(Ordered(1)), "unit");
            Call(bad, "CompleteAfter", Task.CompletedTask, "unit_io"); Require(((Task)Get(bad, "Completion")).Wait(TimeSpan.FromSeconds(10)), "IO失败挂住退出");
            Require(Get(bad, "FirstError") != null, "首个IO错误没有留下");
        }

#if !YC_REPLAY_CAPTURE_OFFLINE
        [Test]
#endif
        public void ClosedCaptureAndObserverExceptionDoNotChangeOriginalResult()
        {
            var (session, owner, options) = Owner(); string root = NewRoot(); var capture = Capture(root, options); Finish(capture, root);
            Require(!(bool)Get(capture, "IsRecording"), "结束后仍捕获");
            int calls = 0; owner.ConfigurePerformanceReplayCapture(_ => { calls++; throw new IOException("unit-observer"); }, 999);
            var result = Result();
            var scope = Begin(owner, session, Ordered(1)); End(scope, result);
            Require(calls == 1 && result.Status == OperationReplicaStatus.Applied, "诊断异常改变原结果");
            var second = Begin(owner, session, Ordered(2)); End(second, result); Require(calls == 1, "故障后没有停止诊断");
            string pendingRoot = NewRoot(); var pending = Capture(pendingRoot, options);
            var stopped = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Call(pending, "RecordProtocol", "ordered", JsonConvert.SerializeObject(Ordered(1)), "unit.inflight");
            Call(pending, "CompleteAfter", stopped.Task, "unit_wait_execution_stop");
            Require(!((Task)Get(pending, "Completion")).IsCompleted && (bool)Get(pending, "IsRecording"), "在途执行完成前关闭诊断");
            Call(pending, "RecordProtocol", "ordered", JsonConvert.SerializeObject(Ordered(2)), "unit.inflight");
            stopped.SetResult(null);
            string pendingDirectory = Finish(pending, pendingRoot);
            Require(File.ReadAllLines(Path.Combine(pendingDirectory, "inputs.jsonl")).Length == 2, "等待退出时漏掉在途输入");
        }

#if !YC_REPLAY_CAPTURE_OFFLINE
        [Test]
#endif
        public void DisabledExistingApplySampleAddsNoAllocationOrSnapshots()
        {
            var (session, owner, _) = Owner(); owner.ConfigurePerformanceReplayCapture(null);
            var type = typeof(DeterministicSimulationCoordinator).GetNestedType("ProtocolExecutionSample", BindingFlags.NonPublic);
            var constructor = type.GetConstructor(All, null, new[] { typeof(ExecutionTimingCounters), typeof(string), typeof(DeterministicSimulationCoordinator), typeof(OrderedOperationCommandDto) }, null);
            var method = new DynamicMethod("r24_disabled_scope", typeof(void), new[] { typeof(ExecutionTimingCounters), typeof(DeterministicSimulationCoordinator), typeof(OrderedOperationCommandDto) }, typeof(NetworkPerformanceReplayCaptureTests), true);
            var il = method.GetILGenerator(); var local = il.DeclareLocal(type);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, "pump.apply"); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Newobj, constructor); il.Emit(OpCodes.Stloc, local);
            il.Emit(OpCodes.Ldloca, local); il.Emit(OpCodes.Call, type.GetMethod("Dispose", All, null, Type.EmptyTypes, null)); il.Emit(OpCodes.Ret);
            var invoke = (Action<ExecutionTimingCounters, DeterministicSimulationCoordinator, OrderedOperationCommandDto>)method.CreateDelegate(typeof(Action<ExecutionTimingCounters, DeterministicSimulationCoordinator, OrderedOperationCommandDto>));
            var ordered = Ordered(1); for (int i = 0; i < 10000; i++) invoke(session.ExecutionTimings, owner, ordered);
            long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 100000; i++) invoke(session.ExecutionTimings, owner, ordered);
            Require(GC.GetAllocatedBytesForCurrentThread() == before && !session.ExecutionTimings.Enabled, "默认关闭有新增分配或计数器查询/启用");
        }

#if !YC_REPLAY_CAPTURE_OFFLINE
        [Test]
#endif
        public void CompleteLegalPrefixRebuildsOriginalEpochAndObservedRevisionLedger()
        {
            var state = new GameState { GameId = "r24-legal-prefix", ContentPackHash = "r24-legal-content", Phase = GamePhase.ActionRound1, Round = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                UseSeatTurnOrder = true, LaunchPlayerOrder = new List<int> { 1, 2, 3 }, MapId = StaticMapDefinitions.FourPlayerMapId };
            for (int player = 1; player <= 3; player++) state.Players.Add(new PlayerState { PlayerId = player, CityLocationId = "B-01" });
            state.RuleConfiguration = new GameRuleConfiguration { MapId = state.MapId, PlayerCount = 3, ContentPackHash = state.ContentPackHash };
            state = new ItemZoneStateFactory().CreateForSetup(state, StaticMapDefinitions.CreateFourPlayerMap(), new ItemRecipeEntry[0]);
            var registry = Registry(); var round = new RoundExecutionService(registry);
            Require(round.PrepareActionWindowForSmoke(state, 1).IsValid, "合法测试根窗口未建立"); round.MarkMainActionComplete(state, 1);
            var engine = round.CreateCommandExecutor(state); Require(engine.TryCreatePlayerActionEffect(1, EffectSpec.Create("test.r24.input", playerId: 1), "1", "test.r24", out _), "测试效果未建立");
            Require(!engine.RunUntilQuiescent().Faulted, "测试请求推进故障");
            var initial = GameStateCloneService.DeepClone(state); var source = Build(initial);
            string initialArchive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(initial));
            var recovered = HostRecoveryService.RecoverJson(initialArchive, registry);
            if (!recovered.Succeeded) File.WriteAllText(Path.Combine(NewRoot(), "正式恢复首次反例档案.json"), initialArchive);
            Require(recovered.Succeeded, "正式初始档案恢复失败：" + recovered.FaultCode + ":" + recovered.Diagnostic);
            var replay = Build(recovered.State);
            string captureRoot = NewRoot(); var capture = Capture(captureRoot, source.Coordinator.SessionOptions, 2);
            ((Func<string>)Call(capture, "WrapInitialFactory", (Func<string>)(() => initialArchive)))();
            Call(capture, "CaptureInitial", new SimulationCheckpointEnvelope { GameId = initial.GameId, ContentPackHash = initial.ContentPackHash,
                SimulationEpoch = 1, MembershipVersion = 1, GlobalCommandSequence = 0, CheckpointId = "r24.formal.initial" });
            DeterministicSimulationCoordinator.PerformanceReplaySample targetEvidence = default;
            source.Coordinator.ConfigurePerformanceReplayCapture(sample => { if (sample.Ordered?.GlobalCommandSequence == 2) targetEvidence = sample; return (bool)Call(capture, "RecordApply", sample); }, 2);
            var ordered = new List<OrderedOperationCommandDto>();
            string beforeTargetDigest = null;
            for (int step = 1; step <= 2; step++)
            {
                if (step == 2) beforeTargetDigest = RuleStateDigestService.Compute(source.Session.State);
                Seal(source); var request = source.Session.State.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.InteractionTypeId == "test.r24.answer");
                var answer = new GameCommand { CommandId = "r24-legal-" + step, PlayerId = 1, Kind = GameCommandKind.AnswerInteraction };
                answer.Parameters["interactionId"] = request.GetStableInteractionId(); answer.Parameters["expectedRevision"] = source.Session.State.EffectRuntime.StateRevision.ToString(CultureInfo.InvariantCulture); answer.Parameters["answerValue"] = "true";
                Require(source.Coordinator.TryCreateLocalProposal(answer, out var proposal, out _), "合法提案不可创建");
                var admitted = source.Coordinator.AdmitProposal(1, proposal); Require(admitted.Status == OperationAdmissionStatus.Accepted, "合法提案未准入");
                ordered.Add(JsonConvert.DeserializeObject<OrderedOperationCommandDto>(JsonConvert.SerializeObject(admitted.OrderedCommand)));
                Require(source.Coordinator.Pump(1) == 1, "原合法输入未执行");
            }
            foreach (var input in ordered)
            {
                Seal(replay); var admitted = replay.Coordinator.AdmitProposal(1, input);
                Require(admitted.Status == OperationAdmissionStatus.Accepted && admitted.OrderedCommand.OrderedCommandDigest == input.OrderedCommandDigest, "原epoch/原观察账本不能正式继续");
                Require(replay.Coordinator.Pump(1) == 1, "完整原前缀无法正式重放");
            }
            Require(replay.Coordinator.LastAppliedSequence == 2 && replay.Coordinator.SessionOptions.SimulationEpoch == 1 &&
                RuleStateDigestService.Compute(source.Session.State) == RuleStateDigestService.Compute(replay.Session.State) &&
                JsonConvert.SerializeObject(source.Session.State.RandomRuntime) == JsonConvert.SerializeObject(replay.Session.State.RandomRuntime), "重放状态/随机/原epoch不一致");
            Call(capture, "ObserveApplied", 2L); string captured = Finish(capture, captureRoot);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(captured, "manifest.json")));
            Require((bool)manifest["CompleteOrderedPrefix"] && (int)manifest["ExportedTargetPoints"] == 2 && (bool)manifest["TargetApplied"], "初始图/完整前缀/目标图未形成闭合材料");
            var beforeFile = JObject.Parse(File.ReadAllText(Path.Combine(captured, "target-before.json")));
            var afterFile = JObject.Parse(File.ReadAllText(Path.Combine(captured, "target-after.json")));
            var beforeRecovered = HostRecoveryService.RecoverJson(beforeFile["Archive"].ToString(Formatting.None), registry);
            var afterRecovered = HostRecoveryService.RecoverJson(afterFile["Archive"].ToString(Formatting.None), registry);
            Require(beforeRecovered.Succeeded && afterRecovered.Succeeded && RuleStateDigestService.Compute(beforeRecovered.State) == beforeTargetDigest &&
                RuleStateDigestService.Compute(afterRecovered.State) == RuleStateDigestService.Compute(source.Session.State), "计时外固定图正式恢复不匹配目标前后");
            File.WriteAllText(Path.Combine(NewRoot(), "合法前缀与目标固定图证明.json"), JsonConvert.SerializeObject(new {
                Scope = "正式初始档案恢复后重放两条原合法输入；非R2旧32命令补造/正常验收", OriginalEpoch = 1, InitialArchive = initialArchive, Inputs = ordered,
                BeforeDigest = beforeTargetDigest, AfterDigest = RuleStateDigestService.Compute(source.Session.State),
                ReplayDigest = RuleStateDigestService.Compute(replay.Session.State), CapturedDirectory = captured, TargetOutput = targetEvidence.Result
            }, Formatting.Indented));
            source.Coordinator.ConfigurePerformanceReplayCapture(null);
        }
        private static EffectRegistry Registry()
        {
            var registry = new EffectRegistry(); registry.Register(new EffectRegistration("test.r24.input", context => {
                if (context.GetLatestInteractionAnswer() != null && context.Node.FlowStage == "second") return EffectStepResult.Completed();
                return EffectStepResult.Continue(context.Node.FlowStage == "" ? "first" : "second").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = "test.r24.answer", AnsweringPlayerId = 1, AnswerSchema = "boolean_accept" });
            }, EffectExecutorKind.IntrinsicFlow)); return registry;
        }
        private sealed class Built { internal GameSession Session; internal DeterministicSimulationCoordinator Coordinator; }
        private static Built Build(GameState initial)
        {
            var session = new GameSession(GameStateCloneService.DeepClone(initial)); var registry = Registry(); var round = new RoundExecutionService(registry);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            var coordinator = new DeterministicSimulationCoordinator(session, DeterministicSimulationSessionOptions.CreateInitial(true, 1, session, new[] { 1, 2, 3 }));
            coordinator.ConfigureRequestContractValidation(OperationCommandProtocol.RequestContractVersion);
            Call(session, "BindBackgroundExecution", 9001L, 1); Call(session, "ClaimBackgroundOwner", 9001L); Call(coordinator, "InitializeBackgroundWorker");
            coordinator.Begin(); return new Built { Session = session, Coordinator = coordinator };
        }
        private static void Seal(Built fixture)
        {
            var boundary = fixture.Coordinator.ReadBoundary(fixture.Coordinator.LastAppliedSequence); if (boundary == null) return;
            foreach (int player in new[] { 2, 3 }) Require(fixture.Coordinator.ReceiveWindowReady(player, new SimulationWindowReadyDto {
                GameId = boundary.GameId, SimulationEpoch = boundary.SimulationEpoch, MembershipVersion = boundary.MembershipVersion,
                PlayerId = player, ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(player), Boundary = boundary }), "隔离副本边界未获确认");
        }
    }
}
