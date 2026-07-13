using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class CoverCharacterCardCommandHandler : IGameCommandHandler
    {
        public const string CardIdParameter = "cardId";

        private readonly CharacterCardService service;

        public CoverCharacterCardCommandHandler()
            : this(new CharacterCardService())
        {
        }

        public CoverCharacterCardCommandHandler(CharacterCardService service)
        {
            this.service = service;
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

        private static string GetParameter(GameCommand command, string key)
        {
            string value;
            return command.Parameters != null && command.Parameters.TryGetValue(key, out value) ? value : string.Empty;
        }
    }
}
