using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class NMC009VisibilityRecoveryEditModeTests
    {
        [Test]
        public void PlayerView_HidesOtherPrivateCardsEventsAndCandidates()
        {
            var state = new GameState
            {
                GameId = "visibility-test",
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1, HandCardIds = new List<string> { "private-card-1" } },
                    new PlayerState { PlayerId = 2, HandCardIds = new List<string> { "other-private-card" } }
                }
            };
            state.EffectRuntime.RuleEvents.Add(new RuleEvent
            {
                EventId = "hidden-event-id",
                EventType = "hidden.event.type",
                PlayerId = 1,
                Visibility = GameStateVisibilityPolicy.Owner,
                Payload = NormalizedValue.CreateStableReference("card", "hidden-card-id")
            });
            state.EffectRuntime.InteractionRequests.Add(new InteractionRequest
            {
                InteractionId = "hidden-interaction-id",
                RequestId = "hidden-interaction-id",
                InteractionTypeId = "hidden.interaction.type",
                AnsweringPlayerId = 1,
                Visibility = GameStateVisibilityPolicy.Owner,
                CandidateIds = new List<string> { "hidden-candidate-id" },
                MinSelections = 1,
                MaxSelections = 1
            });

            GameStateView view = GameStateViewProjector.ProjectForPlayer(state, 2);
            string json = JsonUtility.ToJson(view);

            Assert.That(json, Does.Not.Contain("private-card-1"));
            Assert.That(json, Does.Not.Contain("hidden-event-id"));
            Assert.That(json, Does.Not.Contain("hidden.event.type"));
            Assert.That(json, Does.Not.Contain("hidden-candidate-id"));
            Assert.That(json, Does.Not.Contain("hidden-interaction-id"));
            Assert.That(view.WaitingForPlayerIds, Is.EqualTo(new[] { 1 }));
            Assert.That(view.Players[0].HandCardIds, Is.Empty);
            Assert.That(view.Players[1].HandCardIds, Is.EqualTo(new[] { "other-private-card" }));
        }

        [Test]
        public void ApplyView_DoesNotLeaveEffectRuntimeOnClientState()
        {
            var state = new GameState { GameId = "client-view" };
            state.EffectRuntime.RuleEvents.Add(new RuleEvent
            {
                EventId = "private-event",
                EventType = "secret",
                PlayerId = 1,
                Visibility = GameStateVisibilityPolicy.Owner
            });
            var session = new GameSession(state);

            session.ReplaceView(GameStateViewProjector.ProjectForPlayer(state, 2));

            Assert.That(session.State.EffectRuntime, Is.Null);
            Assert.That(session.View, Is.Not.Null);
        }

        [Test]
        public void ConfirmedView_DoesNotBroadcastAnotherPlayersCommandPayload()
        {
            var session = new GameSession(new GameState { GameId = "command-view" });
            var dispatcher = new AuthoritativeCommandDispatcher(session);
            var confirmed = new ConfirmedGameCommandDto
            {
                Sequence = 1,
                Command = new GameCommandDto
                {
                    CommandId = "private-command-id",
                    Kind = GameCommandKind.AnswerInteraction,
                    PlayerId = 1,
                    OptionIds = new List<string> { "private-candidate-id" },
                    Parameters = new List<GameCommandParameterDto>
                    {
                        new GameCommandParameterDto { Key = "answer", Value = "private-answer" }
                    }
                }
            };

            ConfirmedGameStateViewDto otherPlayer = dispatcher.CreateConfirmedStateViewSynchronization(
                confirmed, GameStateViewer.Player(2));
            ConfirmedGameStateViewDto owner = dispatcher.CreateConfirmedStateViewSynchronization(
                confirmed, GameStateViewer.Player(1));

            Assert.That(otherPlayer.Command, Is.Null);
            Assert.That(JsonUtility.ToJson(otherPlayer), Does.Not.Contain("private-command-id"));
            Assert.That(JsonUtility.ToJson(otherPlayer), Does.Not.Contain("private-candidate-id"));
            Assert.That(JsonUtility.ToJson(otherPlayer), Does.Not.Contain("private-answer"));
            Assert.That(owner.Command.CommandId, Is.EqualTo("private-command-id"));
        }

        [Test]
        public void HostSnapshotAndJournal_RecoversCommittedCheckpoint()
        {
            var initial = new GameState { GameId = "recovery-test" };
            HostSessionArchiveDto archive = HostRecoveryService.CreateArchive(initial, "content-v1");
            GameState committed = GameStateCloneService.DeepClone(initial);
            committed.Round = 2;
            RuleCommit.Apply(committed, "command-1", new RuleJournalEntry
            {
                Kind = RuleJournalEntryKind.DomainState,
                EntityId = "round",
                ContentHash = "content-v1"
            });

            HostRecoveryService.AppendJournal(archive, committed, "content-v1");
            HostRecoveryResult result = HostRecoveryService.Recover(archive);

            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            Assert.That(result.State.Round, Is.EqualTo(2));
            Assert.That(result.State.EffectRuntime.StateRevision, Is.EqualTo(1));
            Assert.That(result.State.EffectRuntime.NextCommitSequence, Is.EqualTo(2));
        }

        [Test]
        public void HostRecovery_WhenSnapshotRevisionIsStale_EntersPausedFault()
        {
            HostSessionArchiveDto archive = HostRecoveryService.CreateArchive(new GameState { GameId = "fault-test" });
            archive.Snapshot.StateRevision = 3;

            HostRecoveryResult result = HostRecoveryService.Recover(archive);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FaultCode, Is.EqualTo(HostRecoveryService.SnapshotInvalid));
            Assert.That(result.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
        }
    }
}
