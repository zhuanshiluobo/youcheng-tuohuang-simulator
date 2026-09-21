using System;
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
        private readonly EffectRegistry effectRegistry;

        public UseCharacterCardCommandHandler()
            : this(new CharacterCardService(), null)
        {
        }

        public UseCharacterCardCommandHandler(CharacterCardService service)
            : this(service, null)
        {
        }

        public UseCharacterCardCommandHandler(
            CharacterCardService service,
            EffectRegistry effectRegistry)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.effectRegistry = effectRegistry;
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

            CommandResult unifiedResult;
            if (TryHandleUnifiedAbility(
                    state,
                    command,
                    cardId,
                    mode,
                    order,
                    out unifiedResult))
            {
                return unifiedResult;
            }

            var validation = service.Use(state, command.PlayerId, cardId, mode, order, command.Parameters);
            if (!validation.IsValid)
            {
                return CommandResult.Invalid(validation);
            }

            BindTinManPendingContext(state, command);
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

        private bool TryHandleUnifiedAbility(
            GameState state,
            GameCommand command,
            string cardId,
            string mode,
            string order,
            out CommandResult result)
        {
            result = null;
            if (effectRegistry == null || state == null || command == null ||
                (mode != CharacterEffectModes.Strategy && mode != CharacterEffectModes.Tactic &&
                 mode != CharacterEffectModes.Both) ||
                (mode != CharacterEffectModes.Both && !string.IsNullOrEmpty(order)) ||
                string.IsNullOrEmpty(cardId))
            {
                return false;
            }

            var definition = CharacterCardDatabase.Get(cardId);
            string strategyAbilityId = definition == null ? string.Empty : definition.StrategyAbilityId;
            string tacticAbilityId = definition == null ? string.Empty : definition.TacticAbilityId;
            string abilityId = mode == CharacterEffectModes.Strategy
                ? strategyAbilityId
                : tacticAbilityId;
            if (definition == null ||
                (mode == CharacterEffectModes.Both
                    ? !effectRegistry.CharacterActivationSubscriptions.ContainsKey(strategyAbilityId) ||
                      !effectRegistry.CharacterActivationSubscriptions.ContainsKey(tacticAbilityId)
                    : !effectRegistry.CharacterActivationSubscriptions.ContainsKey(abilityId)))
            {
                return false;
            }

            if (mode == CharacterEffectModes.Both &&
                order != CharacterEffectOrders.StrategyFirst &&
                order != CharacterEffectOrders.TacticFirst)
            {
                result = CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "同时执行角色牌效果时必须明确 effectOrder。"));
                return true;
            }

            var player = state.FindPlayer(command.PlayerId);
            var validation = ValidateUnifiedActivation(state, player, cardId);
            if (!validation.IsValid)
            {
                result = CommandResult.Invalid(validation);
                return true;
            }

            string stableKey = "command:" + (string.IsNullOrEmpty(command.CommandId)
                ? cardId + ":" + state.EffectRuntime.StateRevision
                : command.CommandId);
            EffectSpec spec;
            if (mode == CharacterEffectModes.Both)
            {
                string firstAbilityId = order == CharacterEffectOrders.StrategyFirst
                    ? strategyAbilityId
                    : tacticAbilityId;
                string secondAbilityId = order == CharacterEffectOrders.StrategyFirst
                    ? tacticAbilityId
                    : strategyAbilityId;
                spec = CharacterAbilityEffectExecutor.CreateSequenceSpec(
                    cardId,
                    firstAbilityId,
                    secondAbilityId,
                    order,
                    command.PlayerId,
                    stableKey);
            }
            else
            {
                spec = CharacterAbilityEffectExecutor.CreateActivationSpec(
                    cardId,
                    abilityId,
                    mode,
                    order,
                    command.PlayerId,
                    stableKey);
            }
            var executor = new EffectTreeExecutor(state, effectRegistry);
            string effectId;
            if (!executor.TryCreatePlayerActionEffect(
                    command.PlayerId,
                    spec,
                    state.EffectRuntime.CurrentRoundExecutionId ?? string.Empty,
                    command.CommandId ?? string.Empty,
                    out effectId))
            {
                result = CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "角色牌 Effect 创建失败：" + executor.LastDiagnostic));
                return true;
            }

            var report = executor.RunUntilQuiescent();
            if (report.Faulted)
            {
                result = CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "角色牌 Lua/Effect 无法继续：" + report.FaultCode));
                return true;
            }

            result = CommandResult.SuccessResult(
                new List<GameEvent>(),
                report.WaitingForInput ? "角色牌效果已进入交互。" : "角色牌效果已完成。");
            return true;
        }

        private static ValidationResult ValidateUnifiedActivation(
            GameState state,
            PlayerState player,
            string cardId)
        {
            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "只能在行动轮使用角色牌。");
            }

            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "使用角色牌的玩家不存在。");
            }

            if (state.CurrentPlayerId != player.PlayerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "只能在自己的行动窗口使用角色牌。");
            }

            if (state.HasPendingChoice() || state.HasOpenActionableInteraction())
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择效果。");
            }

            if (player.CharacterCardLockedThisTurn || player.UsedCharacterThisRound)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "当前行动轮不能再次使用角色牌。");
            }

            if (player.CoveredCharacterCardId != cardId)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "只能使用本回合盖放的角色牌。");
            }

            return ValidationResult.Success;
        }

        private CommandResult HandleResolvePendingCharacterEffect(GameState state, GameCommand command)
        {
            var pending = state.PendingCharacterEffect;
            if (IsTinManPurchasePending(pending) &&
                !string.IsNullOrEmpty(pending.SourceCommandId) &&
                GetParameter(command, CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId) !=
                pending.SourceCommandId)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "该锡人购买命令属于已经结束的结算，不能用于当前发动。"));
            }

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

        private static void BindTinManPendingContext(GameState state, GameCommand command)
        {
            var pending = state == null ? null : state.PendingCharacterEffect;
            if (!IsTinManPending(pending))
            {
                return;
            }

            if (string.IsNullOrEmpty(command.CommandId))
            {
                command.CommandId = Guid.NewGuid().ToString("N");
            }

            pending.SourceCommandId = command.CommandId;
        }

        private static bool IsTinManPending(PendingCharacterEffectState pending)
        {
            return pending != null &&
                   pending.IsValid() &&
                   (pending.ChoiceType == CharacterPendingChoiceTypes.TinManDiscard ||
                    IsTinManPurchasePending(pending));
        }

        private static bool IsTinManPurchasePending(PendingCharacterEffectState pending)
        {
            return pending != null &&
                   (pending.ChoiceType == CharacterPendingChoiceTypes.TinManFirstPurchase ||
                    pending.ChoiceType == CharacterPendingChoiceTypes.TinManSecondPurchase);
        }

        private static string GetParameter(GameCommand command, string key)
        {
            string value;
            return command.Parameters != null && command.Parameters.TryGetValue(key, out value) ? value : string.Empty;
        }
    }
}
