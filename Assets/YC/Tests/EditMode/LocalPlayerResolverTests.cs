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

        [TestCase(GamePhase.Cleanup, true, 2)]
        [TestCase(GamePhase.ActionRound1, true, 2)]
        [TestCase(GamePhase.ResourceCollection, true, 2)]
        [TestCase(GamePhase.Cleanup, false, 1)]
        public void Resolve_PendingCharacterOwnerTakesPriorityOnlyInHotseat(
            GamePhase phase, bool hotseat, int expected)
        {
            var state = CreateState(phase);
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                PlayerId = 2, CardId = "character.liskarm",
                ChoiceType = YC.Domain.Cards.CharacterPendingChoiceTypes.LiskarmCleanupRemoval,
                OptionIds = { "route:A1:0" }
            };
            var resolver = new LocalPlayerResolver();
            Assert.That(resolver.Resolve(state, 1, hotseat), Is.EqualTo(expected));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1), "不能改动共享行动顺序");
            state.PendingCharacterEffect = null;
            Assert.That(resolver.Resolve(state, expected, hotseat), Is.EqualTo(1));
        }

        [TestCase(false, true, 2)]
        [TestCase(true, true, 2)]
        [TestCase(false, false, 1)]
        [TestCase(true, false, 1)]
        public void Resolve_EventChoiceOwnerTakesPriorityOnlyInHotseat(
            bool cardSession, bool hotseat, int expected)
        {
            var state = CreateState(GamePhase.ResourceCollection);
            if (cardSession)
            {
                state.PendingCardSession = new PendingCardSessionState
                {
                    PlayerId = 2, CardId = "event", ScenarioId = "explore",
                    ChoiceType = "explore", OptionIds = { "0" }
                };
            }
            else
            {
                state.PendingChoice = new PendingChoiceState
                {
                    PlayerId = 2, CardId = "event", ChoiceType = "explore", OptionIds = { "0" }
                };
            }
            Assert.That(new LocalPlayerResolver().Resolve(state, 1, hotseat), Is.EqualTo(expected));
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
