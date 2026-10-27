using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class MatchSaveRegressionTests
    {
        [Test]
        public void FailedRecovery_DoesNotModifyCurrentSession()
        {
            var state = new GameState { GameId = "live", Round = 9 };
            var session = new GameSession(state);
            // 隔离回归：先建立满足本地实体区域合同的合法存档，再破坏恢复修订号。
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var incoming = new ItemZoneStateFactory().CreateForSetup(new GameState
            {
                GameId = "wrong", Round = 2, MapId = map.MapId, ContentPackHash = "recovery-regression",
                StartPlayerId = 1, CurrentPlayerId = 1, LaunchPlayerOrder = new List<int> { 1, 2 },
                RuleConfiguration = new GameRuleConfiguration
                { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "recovery-regression" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } }
            }, map, new List<ItemRecipeEntry>());
            var archive = HostRecoveryService.CreateArchive(incoming);
            Assert.That(HostRecoveryService.Recover(archive).Succeeded, Is.True);
            archive.Snapshot.StateRevision = 77;
            var result = session.RecoverHost(archive);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.State, Is.Not.Null, "失败结果携带的隔离故障状态也不能覆盖当前会话。");
            Assert.That(session.State, Is.SameAs(state));
            Assert.That(state.GameId, Is.EqualTo("live")); Assert.That(state.Round, Is.EqualTo(9));
        }
        [Test]
        public void CommitNotification_RejectDoesNotNotifyAndObserverFailureDoesNotRejectCommit()
        {
            var session = new GameSession(new GameState());
            var handler = new Handler(); session.RegisterHandler(handler);
            int commits = 0; session.StateCommitted += () => throw new IOException("disk failed");
            session.StateCommitted += () => commits++;
            Assert.That(session.Submit(new GameCommand()).Succeeded, Is.False);
            Assert.That(commits, Is.Zero);
            handler.Accept = true;
            Assert.That(session.Submit(new GameCommand()).Succeeded, Is.True);
            Assert.That(commits, Is.EqualTo(1)); Assert.That(session.State.Round, Is.EqualTo(1));
        }
        private sealed class Handler : IGameCommandHandler
        {
            public bool Accept;
            public bool CanHandle(GameCommand command) => true;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.Round++;
                return Accept ? CommandResult.SuccessResult(null, "committed") : CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.WrongPhase, "rejected"));
            }
        }
    }
}
