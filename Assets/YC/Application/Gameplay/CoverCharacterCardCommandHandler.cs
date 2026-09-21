using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class CoverCharacterCardCommandHandler : IGameCommandHandler
    {
        public const string CardIdParameter = "cardId";

        private readonly CharacterCardService service;
        private readonly EffectRegistry effectRegistry;

        public CoverCharacterCardCommandHandler()
            : this(new CharacterCardService(), new EffectRegistry())
        {
        }

        public CoverCharacterCardCommandHandler(CharacterCardService service)
            : this(service, null)
        {
        }

        public CoverCharacterCardCommandHandler(
            CharacterCardService service,
            EffectRegistry effectRegistry)
        {
            this.service = service;
            this.effectRegistry = effectRegistry;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.CoverCharacterCard;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var cardId = GetParameter(command, CardIdParameter);
            if (string.IsNullOrEmpty(cardId))
            {
                cardId = command.TargetId;
            }

            if (TryHandleUnifiedCover(state, command.PlayerId, cardId, out var unifiedResult))
            {
                return unifiedResult;
            }

            var validation = service.Cover(state, command.PlayerId, cardId);
            if (!validation.IsValid)
            {
                return CommandResult.Invalid(validation);
            }

            var message = "玩家 " + command.PlayerId + " 已盖放角色牌。";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                // 盖放牌是隐藏信息，成功事件不得携带实际牌 ID。
                new GameEvent { Kind = GameEventKind.CardMoved, PlayerId = command.PlayerId, Message = message }
            }, message);
        }

        private bool TryHandleUnifiedCover(
            GameState state,
            int playerId,
            string cardId,
            out CommandResult result)
        {
            result = null;
            if (effectRegistry == null || state == null || state.EffectRuntime == null ||
                state.EffectRuntime.MainNodes == null ||
                (string.IsNullOrEmpty(state.EffectRuntime.ActiveMainNodeId) &&
                 state.Phase != GamePhase.CharacterCover))
            {
                return false;
            }

            var roundService = new RoundExecutionService(effectRegistry);
            var mainNode = state.EffectRuntime.MainNodes.Find(node =>
                node != null && node.NodeId == state.EffectRuntime.ActiveMainNodeId);
            if (mainNode == null && state.Phase == GamePhase.CharacterCover)
            {
                var bootstrapValidation = roundService.CreateRound(
                    state,
                    state.Round > 0 ? state.Round : 1,
                    state.StartPlayerId > 0 ? state.StartPlayerId : playerId);
                if (!bootstrapValidation.IsValid)
                {
                    result = CommandResult.Invalid(bootstrapValidation);
                    return true;
                }

                mainNode = state.EffectRuntime.MainNodes.Find(node =>
                    node != null && node.NodeId == state.EffectRuntime.ActiveMainNodeId);
            }

            if (mainNode == null || mainNode.NodeTypeId != RoundMainlineNodeTypeIds.CharacterCover)
            {
                return false;
            }

            var request = state.EffectRuntime.InteractionRequests.Find(candidate =>
                candidate != null && candidate.Status == "open" &&
                candidate.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId &&
                candidate.AnsweringPlayerId == playerId);
            if (request == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(cardId))
            {
                result = CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "请选择一张角色牌盖放。"));
                return true;
            }

            var executor = roundService.CreateCommandExecutor(state);
            string diagnostic;
            if (!executor.TrySubmitInteraction(
                    request.InteractionId,
                    playerId,
                    request.StateRevision,
                    NormalizedValue.CreateStableReference("candidate", cardId),
                    out diagnostic))
            {
                result = CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    diagnostic));
                return true;
            }

            var report = executor.RunUntilQuiescent();
            if (report.Faulted)
            {
                result = CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "盖放角色牌后主链无法继续：" + report.FaultCode));
                return true;
            }

            var advance = roundService.Advance(state);
            if (!advance.IsValid)
            {
                result = CommandResult.Invalid(advance);
                return true;
            }
            new RoundExecutionProjector().Project(state);

            string message = "玩家 " + playerId + " 已盖放角色牌。";
            result = CommandResult.SuccessResult(new List<GameEvent>
            {
                // 角色牌身份只存在于 Host 权威 Effect/Event 状态，命令结果保持公共遮蔽。
                new GameEvent { Kind = GameEventKind.CardMoved, PlayerId = playerId, Message = message }
            }, message);
            return true;
        }

        private static string GetParameter(GameCommand command, string key)
        {
            string value;
            return command.Parameters != null && command.Parameters.TryGetValue(key, out value) ? value : string.Empty;
        }
    }
}
