using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class HotseatInteractionAcknowledgmentTests
    {
        private sealed class DecisionHandler : IGameCommandHandler
        {
            public bool Accepted;
            public bool CanHandle(GameCommand command) => command.Kind == GameCommandKind.AnswerInteraction;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                if (!Accepted) return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "准确拒绝本次选择"));
                state.FindPlayer(command.PlayerId).Resources.GoldVoucher++;
                return CommandResult.SuccessResult(new List<GameEvent>(), string.Empty);
            }
        }

        [TestCase(true), TestCase(false)]
        public void SecondPlayerLocalResultReleasesTheActualSubmissionIntent(bool accepted)
        {
            var session = new GameSession(new GameState { GameId = "热座回执局", MapId = StaticMapDefinitions.ThreePlayerMapId,
                CurrentPlayerId = 2, Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1,
                Players = { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } });
            var handler = new DecisionHandler { Accepted = accepted }; session.RegisterHandler(handler);
            var intent = new InteractionCommandIntent();
            var request = new InteractionRequestProjection { InteractionId = "第二玩家模式", OwnerEffectId = "第二玩家能力",
                SourceNodeId = "第二玩家能力", InteractionTypeId = "character.use.mode", AnsweringPlayerId = 2,
                VisibleToViewer = true, Status = "open", Visibility = "owner", AnswerSchema = "candidate_id",
                CandidateSetId = "模式候选", CandidateSetVersion = 1, MinSelections = 1, MaxSelections = 1,
                CandidateIds = new List<string> { "strategy", "tactic" }, StateRevision = 0 };
            var command = EffectInteractionCommands.Answer(request, 2, new[] { "strategy" });
            Assert.That(intent.Begin(session.State.GameId, request, command, false), Is.True);
            int notifications = 0; string resultGame = null, resultCommand = null; bool? resultAccepted = null;
            CommandErrorCode resultError = CommandErrorCode.UnknownCommand;
            Action<string, string, bool, CommandErrorCode> resolved = (game, id, success, error) =>
            {
                notifications++; resultGame = game; resultCommand = id; resultAccepted = success; resultError = error;
                Assert.That(intent.Resolve(game, id, success, error), Is.True);
            };
            // 直接装配真实提交入口；Unity对象和联网运输在这条本地路径上不参与。
            var type = Type.GetType("YC.Presentation.CommandSubmissionController, Assembly-CSharp", true);
            using (var controller = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { session, null, 1, null, (Action)(() => { }), (Action<string>)(_ => { }),
                    (Action<string>)(_ => { }), null, resolved }, null))
            {
                type.GetMethod("Initialize", Type.EmptyTypes).Invoke(controller, null);
                var arguments = new object[] { command, false };
                var result = (CommandResult)type.GetMethod("Submit").Invoke(controller, arguments);
                Assert.That(result.Succeeded, Is.EqualTo(accepted));
                Assert.That(arguments[1], Is.EqualTo(accepted));
                Assert.That(notifications, Is.EqualTo(1));
                Assert.That(resultGame, Is.EqualTo(session.State.GameId));
                Assert.That(resultCommand, Is.EqualTo(command.CommandId));
                Assert.That(resultAccepted, Is.EqualTo(accepted));
                Assert.That(resultError, Is.EqualTo(accepted ? CommandErrorCode.None : CommandErrorCode.InvalidTarget));
                Assert.That(intent.IsBusy, Is.False);
                Assert.That(session.State.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(accepted ? 1 : 0));
            }
        }
    }
}
