using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class PhaseFlowTests
    {
        [Test]
        public void NextPhase_FollowsBaseGamePhaseOrder()
        {
            var state = new GameState { Phase = GamePhase.Setup };

            Assert.That(PhaseFlow.NextPhase(state), Is.EqualTo(GamePhase.Entrance));
            state.Phase = GamePhase.Entrance;
            Assert.That(PhaseFlow.NextPhase(state), Is.EqualTo(GamePhase.RoundStart));
            state.Phase = GamePhase.RoundStart;
            Assert.That(PhaseFlow.NextPhase(state), Is.EqualTo(GamePhase.CharacterCover));
            state.Phase = GamePhase.CharacterCover;
            Assert.That(PhaseFlow.NextPhase(state), Is.EqualTo(GamePhase.ActionRound1));
            state.Phase = GamePhase.ActionRound1;
            Assert.That(PhaseFlow.NextPhase(state), Is.EqualTo(GamePhase.ActionRound2));
            state.Phase = GamePhase.ActionRound2;
            Assert.That(PhaseFlow.NextPhase(state), Is.EqualTo(GamePhase.ResourceCollection));
            state.Phase = GamePhase.ResourceCollection;
            Assert.That(PhaseFlow.NextPhase(state), Is.EqualTo(GamePhase.Cleanup));
        }

        [Test]
        public void NextPhase_AfterMaxRoundCleanup_EntersFinalScoring()
        {
            var state = new GameState
            {
                Phase = GamePhase.Cleanup,
                Round = 8,
                MaxRounds = 8
            };

            Assert.That(PhaseFlow.NextPhase(state), Is.EqualTo(GamePhase.FinalScoring));
        }

        [Test]
        public void NextPhase_BeforeMaxRoundCleanup_ReturnsRoundStart()
        {
            var state = new GameState
            {
                Phase = GamePhase.Cleanup,
                Round = 7,
                MaxRounds = 8
            };

            Assert.That(PhaseFlow.NextPhase(state), Is.EqualTo(GamePhase.RoundStart));
        }

        [Test]
        public void ValidatePhase_WhenCommandIsNotAllowed_ReturnsWrongPhase()
        {
            var state = new GameState { Phase = GamePhase.Setup };

            var result = CommandPhasePolicy.ValidatePhase(state, GameCommandKind.BuildFacility);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo(CommandErrorCode.WrongPhase));
        }

        [Test]
        public void ValidatePhase_WhenCommandIsAllowed_ReturnsSuccess()
        {
            var state = new GameState { Phase = GamePhase.ActionRound1 };

            var result = CommandPhasePolicy.ValidatePhase(state, GameCommandKind.BuildFacility);

            Assert.That(result.IsValid, Is.True);
        }
    }
}
