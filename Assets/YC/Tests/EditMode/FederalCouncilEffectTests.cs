using NUnit.Framework;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class FederalCouncilEffectTests
    {
        [Test]
        public void RecordLatestBuilder_WhenSeveralPlayersBuildInOneRound_KeepsLastBuilder()
        {
            var state = CreateState();

            FederalCouncilEffectService.RecordLatestBuilder(state, 2);
            FederalCouncilEffectService.RecordLatestBuilder(state, 3);

            Assert.That(state.LastFederalCouncilBuilderThisRoundPlayerId, Is.EqualTo(3));
        }

        [Test]
        public void EndCleanup_WithFederalCouncilBuilder_GivesMarkerToLastBuilderAndClearsRecord()
        {
            var state = CreateState();
            state.Phase = GamePhase.Cleanup;
            state.CurrentPlayerId = state.StartPlayerId;
            FederalCouncilEffectService.RecordLatestBuilder(state, 3);

            var result = new RoundAdvanceService().EndCompletedAction(state, state.StartPlayerId);

            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.StartPlayerId, Is.EqualTo(3));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
            Assert.That(state.LastFederalCouncilBuilderThisRoundPlayerId, Is.EqualTo(-1));
        }

        [Test]
        public void EndCleanup_WithoutFederalCouncilBuilder_KeepsNormalMarkerPassing()
        {
            var state = CreateState();
            state.Phase = GamePhase.Cleanup;
            state.CurrentPlayerId = state.StartPlayerId;

            var result = new RoundAdvanceService().EndCompletedAction(state, state.StartPlayerId);

            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(state.StartPlayerId, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(state.LastFederalCouncilBuilderThisRoundPlayerId, Is.EqualTo(-1));
        }

        [Test]
        public void EndCleanup_OnFinalRound_ClearsFederalCouncilRecordWithoutStartingAnotherRound()
        {
            var state = CreateState();
            state.Round = state.MaxRounds;
            state.Phase = GamePhase.Cleanup;
            state.CurrentPlayerId = state.StartPlayerId;
            FederalCouncilEffectService.RecordLatestBuilder(state, 3);

            var result = new RoundAdvanceService().EndCompletedAction(state, state.StartPlayerId);

            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.FinalScoring));
            Assert.That(state.StartPlayerId, Is.EqualTo(1));
            Assert.That(state.LastFederalCouncilBuilderThisRoundPlayerId, Is.EqualTo(-1));
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Round = 1,
                MaxRounds = 8,
                UseSeatTurnOrder = true,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue },
                    new PlayerState { PlayerId = 3, Color = PlayerColor.Green }
                }
            };
        }
    }
}
