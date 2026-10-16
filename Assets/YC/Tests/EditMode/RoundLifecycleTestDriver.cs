using System.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

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
            // 调用方可能在采集请求开放后执行了设施效果；由主链刷新版本，不手改请求。
            var refreshed = round.Advance(state);
            Assert.That(refreshed.IsValid, Is.True, refreshed.Reason);
            var ids = state.Players.Where(player => !player.HasCollectedResourcesThisRound)
                .Select(player => player.PlayerId).ToArray();
            foreach (var id in ids) SubmitEmptyCollection(state, round, id);
            Assert.That(state.Phase, Is.Not.EqualTo(GamePhase.ResourceCollection));
        }

        // 只推进无需采集奖励的规则夹具；从真实请求提交空采集，不手写完成标记。
        public static void SubmitEmptyCollection(GameState state, RoundExecutionService round, int playerId)
        {
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            var request = state.EffectRuntime.InteractionRequests.Single(r =>
                r.Status == "open" && r.InteractionTypeId == "route_resource.selection" &&
                r.AnsweringPlayerId == playerId &&
                r.PromptParameters.Properties.Any(p => p.Name == "purpose" &&
                    p.Value.IntegerValue == (int)RouteSelectionPurpose.NormalHarvest));
            var scope = request.PromptParameters.Properties.Single(p => p.Name == "scopeKey").Value.StringValue;
            var commit = RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Commit);
            Assert.That(request.CandidateIds, Does.Contain(commit));
            var requestId = request.GetStableInteractionId();
            var ownerEffectId = request.OwnerEffectId;
            var projection = InteractionRequestProjector.ProjectForPlayer(request, playerId);
            var command = EffectInteractionCommands.Answer(projection, playerId, new[] { commit });
            var result = new AnswerInteractionCommandHandler(round.EffectRegistry, round).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation == null ? string.Empty : result.Validation.Reason);
            Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == requestId).Status,
                Is.EqualTo("answered"));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerEffectId).Status,
                Is.EqualTo(EffectNodeStatus.Completed));
        }
    }
}
