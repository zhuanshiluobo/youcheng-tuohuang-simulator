using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    // 共享事务/私有日志的窄验夹具；生产内容链和运输阶段由各自执行者验证。
    public sealed class GameSessionConfirmedRecoveryTests
    {
        [Test]
        public void CommittedCaptureIsDetachedBoundedAndPinsConfirmedPoint()
        {
            var session = Session(); session.EnableConfirmedRecoveryCapture();
            var command = new GameCommand { CommandId = "confirmed-one", Kind = GameCommandKind.EndAction, PlayerId = 1 };
            Assert.That(session.Submit(command).Succeeded, Is.True);
            Assert.That(session.TryReadCommittedRecoveryPoint("confirmed-one", out var point), Is.True);
            var originalPlayer = session.State.FindPlayer(1);
            command.CommandId = "consumer-mutated"; command.Parameters["consumer"] = "modified";
            for (int i = 2; i <= 4; i++) Assert.That(session.Submit(new GameCommand { CommandId = "later-" + i, Kind = GameCommandKind.EndAction, PlayerId = 1 }).Succeeded, Is.True);
            Assert.That(ReferenceEquals(originalPlayer, session.State.FindPlayer(1)), Is.True);
            originalPlayer.Resources.Iron = 999;
            var exported = session.ExportConfirmedRecoveryPoint(point);
            Assert.That(point.CommandId, Is.EqualTo("confirmed-one"));
            Assert.That(exported.Archive.Snapshot.State.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(exported.Archive.Snapshot.State.Logs.Count, Is.EqualTo(1));
            exported.Archive.Snapshot.State.FindPlayer(1).Resources.Iron = 456;
            Assert.That(session.ExportConfirmedRecoveryPoint(point).Archive.Snapshot.State.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(session.TryReadCommittedRecoveryPoint("later-2", out _), Is.False);
            Assert.That(session.TryReadCommittedRecoveryPoint("later-4", out _), Is.True);
            Assert.Throws<InvalidOperationException>(() => session.ExportConfirmedRecoveryPoint(point));
        }

        [Test]
        public void PendingPrivateLogRoundTripsItsFullBeforeAndOriginalCommand()
        {
            var session = Session(); session.EnableConfirmedRecoveryCapture();
            session.State.FindPlayer(1).DiscardCardIds.Add("old-discard");
            session.State.ContentDefinitions = new GameContentDefinitions { Characters = new List<YC.Domain.Cards.CharacterCardDefinition> {
                new YC.Domain.Cards.CharacterCardDefinition { TemplateId = "character.blue.p1.liskarm", CardId = "character.blue.p1.liskarm", Name = "日志续接夹具" } } };
            var use = new GameCommand { CommandId = "original-use", Kind = GameCommandKind.UseCharacterCard, PlayerId = 1,
                TargetId = "character.blue.p1.liskarm", Parameters = { { "cardId", "character.blue.p1.liskarm" }, { "effectMode", "strategy" } } };
            var useResult = session.Submit(use);
            Assert.That(useResult.Succeeded, Is.True, useResult.Validation?.Reason);
            Assert.That(session.TryReadCommittedRecoveryPoint("original-use", out var point), Is.True);
            var exported = session.ExportConfirmedRecoveryPoint(point);
            var continuation = exported.Continuation;
            Assert.That(continuation.HasPendingActionLog, Is.True);
            Assert.That(continuation.Command.CommandId, Is.EqualTo("original-use"));
            Assert.That(continuation.Before.Players.Single(p => p.PlayerId == 1).DiscardCardIds, Does.Contain("old-discard"));
            Assert.That(continuation.Before.ContentState, Is.Not.Null);
            use.Parameters["effectMode"] = "consumer-mutated";
            Assert.That(continuation.Command.Parameters.Single(p => p.Key == "effectMode").Value, Is.EqualTo("strategy"));
            var restored = exported.Archive.Snapshot.State;
            Assert.That(session.TryPrepareConfirmedRecoveryState(restored, continuation, GameStateViewer.Player(1), out var prepared, out var fault), Is.True, fault);
            var previousRoot = session.State;
            Assert.That(session.TryCommitPreparedRecovery(prepared, out fault), Is.True, fault);
            Assert.That(ReferenceEquals(previousRoot, session.State), Is.False);
            continuation.Command.CommandId = "export-mutated-after-prepare";
            Assert.That(session.Submit(new GameCommand { CommandId = "answer-after-recovery", Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 }).Succeeded, Is.True);
            Assert.That(session.State.Logs.Count, Is.EqualTo(1));
            Assert.That(session.State.Logs[0].CommandId, Is.EqualTo("original-use"));
            var installed = session.State;
            Assert.That(session.TryCommitPreparedRecovery(prepared, out fault), Is.True, fault);
            Assert.That(ReferenceEquals(installed, session.State), Is.True);
            Assert.That(session.State.Logs.Count, Is.EqualTo(1));
        }

        [Test]
        public void InvalidContinuationAndExpiredPreparedLeaveActiveRootsUnchanged()
        {
            var session = Session(); var point = session.EnableConfirmedRecoveryCapture();
            var exported = session.ExportConfirmedRecoveryPoint(point);
            session.RefreshLocalProjection(GameStateViewer.Player(1));
            var before = session.State; var view = session.View;
            exported.Continuation.SchemaVersion = 9;
            Assert.That(session.TryPrepareConfirmedRecoveryState(exported.Archive.Snapshot.State, exported.Continuation, GameStateViewer.Player(1), out _, out _), Is.False);
            Assert.That(ReferenceEquals(before, session.State), Is.True);
            Assert.That(ReferenceEquals(view, session.View), Is.True);
            exported.Continuation.SchemaVersion = 1;
            Assert.That(session.TryPrepareConfirmedRecoveryState(exported.Archive.Snapshot.State, exported.Continuation, GameStateViewer.Player(1), out var prepared, out var fault), Is.True, fault);
            Assert.That(session.Submit(new GameCommand { CommandId = "newer-command", PlayerId = 1, Kind = GameCommandKind.EndAction }).Succeeded, Is.True);
            Assert.That(session.TryCommitPreparedRecovery(prepared, out fault), Is.False);
            Assert.That(fault, Is.EqualTo("recovery_prepared_expired"));
            Assert.That(ReferenceEquals(before, session.State), Is.True);
            Assert.That(ReferenceEquals(view, session.View), Is.True);
            Assert.That(session.State.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(session.State.Logs.Count, Is.EqualTo(1));
        }

        private static GameSession Session()
        {
            var session = new GameSession(new GameState { GameId = "recovery-shared-fixture", Phase = GamePhase.ActionRound1,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "玩家一" }, new PlayerState { PlayerId = 2, Name = "玩家二" } } });
            session.RegisterHandler(new ScenarioHandler()); return session;
        }

        private sealed class ScenarioHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command) => command.Kind == GameCommandKind.EndAction || command.Kind == GameCommandKind.UseCharacterCard || command.Kind == GameCommandKind.ResolvePendingChoice;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                if (command.Kind == GameCommandKind.UseCharacterCard)
                {
                    state.FindPlayer(1).Resources.Iron++;
                    state.PendingChoice = new PendingChoiceState { ChoiceType = "test.recovery", PlayerId = 1, CardId = command.TargetId, OptionIds = { "confirm" } };
                    return CommandResult.SuccessResult(new List<GameEvent>(), "等待本人确认");
                }
                if (command.Kind == GameCommandKind.ResolvePendingChoice) { state.PendingChoice = null; return CommandResult.SuccessResult(new List<GameEvent>(), "确认完成"); }
                state.FindPlayer(1).Resources.Iron++;
                return CommandResult.SuccessResult(new List<GameEvent>(), "资源事务");
            }
        }
    }
}
