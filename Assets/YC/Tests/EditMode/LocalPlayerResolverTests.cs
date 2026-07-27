using NUnit.Framework;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class LocalPlayerResolverTests
    {
        [Test]
        public void Resolve_NetworkedContextKeepsConfiguredLocalPlayer()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.CurrentPlayerId = 2;

            var resolved = new LocalPlayerResolver().Resolve(state, 1, false);

            Assert.That(resolved, Is.EqualTo(1));
        }

        [Test]
        public void Resolve_HotseatActionUsesCurrentPlayer()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.CurrentPlayerId = 2;

            var resolved = new LocalPlayerResolver().Resolve(state, 1, true);

            Assert.That(resolved, Is.EqualTo(2));
        }

        [Test]
        public void Resolve_HotseatCollectionUsesFirstUncollectedPlayerInTurnOrder()
        {
            var state = CreateState(GamePhase.ResourceCollection);
            state.FindPlayer(1).HasCollectedResourcesThisRound = true;
            state.FindPlayer(2).HasCollectedResourcesThisRound = false;

            var resolved = new LocalPlayerResolver().Resolve(state, 1, true);

            Assert.That(resolved, Is.EqualTo(2));
        }

        [Test]
        public void Resolve_HotseatCollectionKeepsConfiguredPlayerWhenEveryoneFinished()
        {
            var state = CreateState(GamePhase.ResourceCollection);
            state.FindPlayer(1).HasCollectedResourcesThisRound = true;
            state.FindPlayer(2).HasCollectedResourcesThisRound = true;

            var resolved = new LocalPlayerResolver().Resolve(state, 1, true);

            Assert.That(resolved, Is.EqualTo(1));
        }

        private static GameState CreateState(GamePhase phase)
        {
            return new GameState
            {
                Phase = phase,
                Round = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue }
                }
            };
        }
    }
}
