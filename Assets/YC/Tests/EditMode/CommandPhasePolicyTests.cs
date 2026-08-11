using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class CommandPhasePolicyTests
    {
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
