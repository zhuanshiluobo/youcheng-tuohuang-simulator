using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if !YC_WINDOW_EXECUTION_OFFLINE
using NUnit.Framework;
#endif
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    // 仅隔离规则/线程回归；不计正常游戏性能或可见界面验收。
    public sealed partial class WindowLocalExecutionProtocolTests
    {
        private static object R18Field(object source, string name) => source.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(source);

#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void UnchangedPublicationReusesPrivateViewAndCheckpointButNewConfirmationReplacesThem()
        {
            using var group = new NetworkGroup("r18-publication", Payment, hostOwnsWindow: true);
            group.Host.TryReadPublishedSnapshot(out var initial);
            var privateView = R18Field(initial, "view"); var checkpoint = R18Field(initial, "checkpoint");
            group.ReadOnlyActorBarrier(); group.Host.TryReadPublishedSnapshot(out var repeated);
            Require(repeated.Version > initial.Version && repeated.StateRevision == initial.StateRevision, "只读发布未推进或改动了规则版本");
            Require(ReferenceEquals(privateView, R18Field(repeated, "view")) && ReferenceEquals(checkpoint, R18Field(repeated, "checkpoint")), "相同固定点重复复制视图或确认元数据");
            var exposed = repeated.ReadView(); exposed.StateRevision = -999;
            Require(initial.ReadView().StateRevision != -999 && repeated.ReadView().StateRevision != -999, "公开视图泄漏到私有固定图");
            var permissions = repeated.ReadWindowPermissions();
            permissions[0].Window.PrefixDigest = "外部修改";
            Require(repeated.ReadWindowPermissions()[0].Window.PrefixDigest != "外部修改", "公开权限DTO泄漏到固定发布");
            group.Invoke("r18.invoke"); group.AnswerAll("r18.answer."); group.End("r18.end");
            group.Exchange(() => group.AllConfirmed(4)); group.Host.TryReadPublishedSnapshot(out var next);
            Require(!ReferenceEquals(privateView, R18Field(next, "view")) && !ReferenceEquals(checkpoint, R18Field(next, "checkpoint")), "新状态或确认点错误复用旧图");
            Require(next.CreateSaveCheckpointAsync().GetAwaiter().GetResult().Baseline.ConfirmedGlobalSequence == 4, "新确认点仍指向旧保存边界");
            Require(initial.CreateSaveCheckpointAsync().GetAwaiter().GetResult().Baseline.ConfirmedGlobalSequence == 0, "旧发布句柄被新确认点改写");
            group.CheckNoFault();
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void HotseatCaptureDoesNotEnablePerCommandRecoveryAndExplicitConfirmationStillDoes()
        {
            var f = R16LocalFixture("r18-local-capture"); var session = new GameSession(f.State);
            session.RegisterHandler(new InvokeSettlementOptionCommandHandler(f.Registry));
            session.RegisterHandler(new AnswerInteractionCommandHandler(f.Registry, f.Rounds));
            session.ExecutionTimings.Enabled = true;
            var frozen = session.CaptureStableSavePoint(); long captures = session.ExecutionTimings.Snapshot()["recovery.capture.calls"];
            var option = f.Registry.GetSettlementOptions(session.State, 1).Single(o => o.SourceItemId == f.CardId && o.CleanupRequest != null);
            Require(session.Submit(CleanupSourceCommand.Create(1, "r18.local.invoke", option.CleanupRequest, session.State.EffectRuntime.StateRevision)).Succeeded, "真实行动未执行");
            R16Answer(session, "r18.local.pay");
            Require(session.ExecutionTimings.Snapshot()["recovery.capture.calls"] == captures, "热座一次保存开启了后续逐命令联机捕获");
            Require(frozen.Archive.Snapshot.State.FindPlayer(1).Resources.Originium == 4, "后续付款改写了旧稳定捕获");
            session.EnableConfirmedRecoveryCapture(); captures = session.ExecutionTimings.Snapshot()["recovery.capture.calls"];
            R16Answer(session, "r18.local.choice");
            Require(session.ExecutionTimings.Snapshot()["recovery.capture.calls"] == captures + 1 &&
                session.TryReadCommittedRecoveryPoint("r18.local.choice", out _), "显式联机确认捕获被关闭");
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public void ContinuationCaptureOwnsItsCommandResultsAndBeforeSnapshot()
        {
            // 单独验证日志转换所有权，不把人工日志对象算作真实行动覆盖。
            var f = R16LocalFixture("r18-continuation");
            var logType = typeof(GameSession).GetNestedType("PendingActionLog", BindingFlags.NonPublic);
            var log = Activator.CreateInstance(logType, true);
            var beforeType = typeof(GameSession).Assembly.GetType("YC.Application.Sessions.PublicActionStateSnapshot");
            var before = beforeType.GetMethod("Capture").Invoke(null, new object[] { f.State });
            var command = new GameCommand { CommandId = "r18.cont.root", PlayerId = 1, Kind = GameCommandKind.UseCharacterCard };
            command.Parameters["原参数"] = "原值";
            var result = CommandResult.SuccessResult(new List<GameEvent> { new GameEvent { EventId = "r18.event", Data = new Dictionary<string, string> { ["原参数"] = "原值" } } }, "隔离日志");
            void Assign(string name, object value) => logType.GetField(name).SetValue(log, value);
            Assign("PlayerId", 1); Assign("Kind", command.Kind); Assign("Command", command); Assign("InitialResult", result);
            Assign("SettlementResult", result); Assign("Before", before); Assign("Commands", new List<GameCommand> { command });
            var capture = (GameSessionLogContinuationDto)typeof(GameSession).GetMethod("CaptureContinuation", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { log });
            string expected = SimulationConfirmedRecoveryInstaller.SerializeContinuation(capture);
            ((GameCommand)R18Field(log, "Command")).TargetId = "后续改动";
            ((List<GameCommand>)R18Field(log, "Commands"))[0].Parameters.Clear();
            ((GameState)R18Field(before, "ContentState")).GameId = "后续改动";
            if (result.Events.Count > 0) result.Events[0].Data["后续改动"] = "后续改动";
            Require(expected == SimulationConfirmedRecoveryInstaller.SerializeContinuation(capture), "捕获仍引用可变日志字段");
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public async Task LocalSerializationUsesCapturedStateAndCopiedHeaderWhileNextLegalChoiceCompletes()
        {
            var f = R16LocalFixture("r18-local-serialize"); var session = new GameSession(f.State);
            session.RegisterHandler(new InvokeSettlementOptionCommandHandler(f.Registry));
            session.RegisterHandler(new AnswerInteractionCommandHandler(f.Registry, f.Rounds));
            var option = f.Registry.GetSettlementOptions(session.State, 1).Single(o => o.SourceItemId == f.CardId && o.CleanupRequest != null);
            Require(session.Submit(CleanupSourceCommand.Create(1, "r18.async.invoke", option.CleanupRequest, session.State.EffectRuntime.StateRevision)).Succeeded, "真实行动未执行");
            R16Answer(session, "r18.async.pay"); var capture = session.CaptureStableSavePoint(); var source = capture.Archive.Snapshot.State;
            var header = new MatchSaveData { Mode = LaunchMode.Local, GameId = source.GameId, ContentHash = source.ContentPackHash,
                LocalPlayerId = 1, Seats = source.Players.Select(p => new MatchSaveSeat { PlayerId = p.PlayerId, Color = p.Color, PlayerName = p.Name }).ToList() };
            string name = header.Seats[0].PlayerName;
            var pending = MatchSaveData.CreateFromLocalCaptureAsync(header, capture);
            header.Seats[0].PlayerName = "后续改动"; header.GameId = "后续改动";
            R16Answer(session, "r18.async.choice"); var saved = await pending; saved.Validate();
            Require(saved.Seats[0].PlayerName == name && saved.GameId == source.GameId && string.IsNullOrEmpty(saved.SimulationCheckpointJson), "后台保存使用了后续头信息或混入联机上下文");
            var recovered = HostRecoveryService.RecoverJson(saved.ArchiveJson, f.Registry);
            Require(recovered.Succeeded && recovered.State.FindPlayer(1).Resources.PureOriginium == 0 &&
                session.State.FindPlayer(1).Resources.PureOriginium == 1, "后台序列化读到了后续活动状态");
        }

#if !YC_WINDOW_EXECUTION_OFFLINE
        [Test]
#endif
        public async Task SingleArchiveParseStillRejectsChangedConfigurationRandomSeedOrderAndDuplicateFields()
        {
            var f = R16LocalFixture("r18-single-parse");
            // 先经过真实随机流调用；没有流的初始夹具不能检验流状态与 seed 的绑定。
            GameplayRandomRuntime.Next(f.State, "qy008.r18.validation", 2);
            var capture = new GameSession(f.State).CaptureStableSavePoint();
            var source = capture.Archive.Snapshot.State;
            var header = new MatchSaveData { Mode = LaunchMode.Local, GameId = source.GameId, ContentHash = source.ContentPackHash,
                LocalPlayerId = 1, Seats = source.Players.Select(p => new MatchSaveSeat { PlayerId = p.PlayerId, Color = p.Color, PlayerName = p.Name }).ToList() };
            var data = await MatchSaveData.CreateFromLocalCaptureAsync(header, capture); data.Validate(); string original = data.ArchiveJson;
            void Reject(Action<JObject> tamper)
            {
                var archive = JObject.Parse(original); tamper((JObject)archive["Snapshot"]["State"]); data.ArchiveJson = archive.ToString(Formatting.None);
                bool rejected = false; try { data.Validate(); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "归档改动未被拒绝或错误复用了上次校验缓存");
            }
            Reject(s => s["RuleConfiguration"]["ContentPackHash"] = "不同内容");
            Reject(s => s["EventDeckSeed"] = (int)s["EventDeckSeed"] + 1);
            Reject(s => s["RandomRuntime"]["AlgorithmVersion"] = "不同算法");
            Reject(s => { var order = (JArray)s["LaunchPlayerOrder"]; order[1] = order[0].DeepClone(); });
            data.ArchiveJson = original.Replace("\"RuleConfiguration\":", "\"RuleConfiguration\":null,\"RuleConfiguration\":");
            Require(data.ArchiveJson != original, "重复字段夹具未命中实际JSON字段");
            bool duplicateRejected = false; try { data.Validate(); } catch (JsonReaderException) { duplicateRejected = true; }
            Require(duplicateRejected, "单次解析放过了归档重复字段"); data.ArchiveJson = original; data.Validate();
        }
    }
}
