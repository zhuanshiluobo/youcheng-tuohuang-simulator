using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    // 规则测试的主链夹具；玩家黑盒测试不引用此类。
    internal static class RoundLifecycleTestDriver
    {
        public static RoundExecutionService EnterCollection(GameState state, RoundExecutionService round = null)
        {
            round = round ?? new RoundExecutionService();
            foreach (var player in state.Players)
                if (player.HandCardIds.Count == 0) CharacterCardDatabase.InitializePlayerHand(player);
            var created = round.CreateRound(state, state.Round > 0 ? state.Round : 1, state.StartPlayerId);
            Assert.That(created.IsValid, Is.True, created.Reason);
            for (int guard = 0; state.Phase == GamePhase.CharacterCover && guard < 16; guard++)
            {
                var player = state.FindPlayer(state.CurrentPlayerId);
                var command = new GameCommand { Kind = GameCommandKind.CoverCharacterCard, PlayerId = player.PlayerId };
                command.Parameters[CoverCharacterCardCommandHandler.CardIdParameter] = player.HandCardIds[0];
                var result = new CoverCharacterCardCommandHandler(new CharacterCardService(), round.EffectRegistry).Handle(state, command);
                Assert.That(result.Succeeded, Is.True, result.Validation == null ? "" : result.Validation.Reason);
            }
            for (int guard = 0; (state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2) && guard < 32; guard++)
            {
                var result = round.CompleteMainAction(state, state.CurrentPlayerId);
                Assert.That(result.IsValid, Is.True, result.Reason);
            }
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            return round;
        }

        public static void FinishCollection(GameState state, RoundExecutionService round)
        {
            var ids = state.Players.ConvertAll(player => player.PlayerId);
            foreach (var id in ids)
            {
                state.FindPlayer(id).HasCollectedResourcesThisRound = true;
                var result = round.CompleteResourceCollection(state, id);
                Assert.That(result.IsValid, Is.True, result.Reason);
            }
        }
    }
}
