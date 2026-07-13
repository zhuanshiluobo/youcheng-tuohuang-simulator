using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class UseCharacterCardCommandHandler : IGameCommandHandler
    {
        public const string CardIdParameter = "cardId";
        public const string EffectModeParameter = "effectMode";
        public const string EffectOrderParameter = "effectOrder";
        public const string Strategy = CharacterEffectModes.Strategy;
        public const string Tactic = CharacterEffectModes.Tactic;
        public const string Both = CharacterEffectModes.Both;
        public const string StrategyFirst = CharacterEffectOrders.StrategyFirst;
        public const string TacticFirst = CharacterEffectOrders.TacticFirst;
        public const string ChoiceParameter = CharacterEffectParameterKeys.Choice;
        public const string SourceInfluenceSlotIdParameter = CharacterEffectParameterKeys.SourceInfluenceSlotId;
        public const string TargetInfluenceSlotIdParameter = CharacterEffectParameterKeys.TargetInfluenceSlotId;

        private readonly CharacterCardService service;

        public UseCharacterCardCommandHandler()
            : this(new CharacterCardService())
        {
        }

        public UseCharacterCardCommandHandler(CharacterCardService service)
        {
            this.service = service;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null &&
                   (command.Kind == GameCommandKind.UseCharacterCard ||
                    command.Kind == GameCommandKind.ResolvePendingChoice);
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            if (command.Kind == GameCommandKind.ResolvePendingChoice)
            {
                return HandleResolvePendingCharacterEffect(state, command);
            }

            var cardId = GetParameter(command, CardIdParameter);
            if (string.IsNullOrEmpty(cardId))
            {
                cardId = command.TargetId;
            }

            var mode = GetParameter(command, EffectModeParameter);
            var order = GetParameter(command, EffectOrderParameter);
            var validation = service.Use(state, command.PlayerId, cardId, mode, order, command.Parameters);
            if (!validation.IsValid)
            {
                return CommandResult.Invalid(validation);
            }

            var definition = CharacterCardDatabase.Get(cardId);
            if (state.PendingCharacterEffect != null && state.PendingCharacterEffect.IsValid())
            {
                return CommandResult.SuccessResult(new List<GameEvent>(), string.Empty);
            }

            var message = BuildSettlementMessage(command.PlayerId, definition == null ? string.Empty : definition.Name);
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent { Kind = GameEventKind.CardMoved, PlayerId = command.PlayerId, SubjectId = cardId, Message = message }
            }, message);
        }

        private CommandResult HandleResolvePendingCharacterEffect(GameState state, GameCommand command)
        {
            var pending = state.PendingCharacterEffect;
            var cardId = pending == null ? string.Empty : pending.CardId;
            var wasDelayedCleanup = pending != null && pending.ChoiceType == CharacterPendingChoiceTypes.LiskarmCleanupRemoval;
            var validation = service.ResolvePendingChoice(
                state,
                command.PlayerId,
                GetParameter(command, ChoiceParameter),
                GetParameter(command, SourceInfluenceSlotIdParameter),
                GetParameter(command, TargetInfluenceSlotIdParameter));
            if (!validation.IsValid)
            {
                return CommandResult.Invalid(validation);
            }

            if (state.PendingCharacterEffect != null && state.PendingCharacterEffect.IsValid())
            {
                return CommandResult.SuccessResult(new List<GameEvent>(), string.Empty);
            }

            var definition = CharacterCardDatabase.Get(cardId);
            var cardName = definition == null ? string.Empty : definition.Name;
            var message = wasDelayedCleanup
                ? "玩家 " + command.PlayerId + " 完成了角色牌“" + cardName + "”的收尾结算。"
                : BuildSettlementMessage(command.PlayerId, cardName);
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent { Kind = GameEventKind.CardMoved, PlayerId = command.PlayerId, SubjectId = cardId, Message = message }
            }, message);
        }

        private static string BuildSettlementMessage(int playerId, string cardName)
        {
            return "玩家 " + playerId + " 的角色牌“" +
                   (string.IsNullOrEmpty(cardName) ? "未知角色牌" : cardName) +
                   "”已完成全部结算。";
        }

        private static string GetParameter(GameCommand command, string key)
        {
            string value;
            return command.Parameters != null && command.Parameters.TryGetValue(key, out value) ? value : string.Empty;
        }
    }
}
