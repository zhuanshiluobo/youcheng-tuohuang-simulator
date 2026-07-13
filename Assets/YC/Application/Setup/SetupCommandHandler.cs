using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Application.Setup
{
    public sealed class SetupCommandHandler : IGameCommandHandler
    {
        private const string EntranceEventChoiceType = CardFlowChoiceTypes.EntranceEvent;

        private readonly IMapQueryService mapQueryService;
        private readonly EventDeckService eventDeckService;
        private readonly ResourceTokenService resourceTokenService;
        private readonly TurnOrderService turnOrderService;
        private readonly CardFlowService cardFlowService;

        public SetupCommandHandler(IMapQueryService mapQueryService)
            : this(mapQueryService, new EventDeckService(), new ResourceTokenService(), new TurnOrderService())
        {
        }

        public SetupCommandHandler(
            IMapQueryService mapQueryService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService,
            TurnOrderService turnOrderService)
        {
            this.mapQueryService = mapQueryService ?? throw new ArgumentNullException(nameof(mapQueryService));
            this.eventDeckService = eventDeckService ?? throw new ArgumentNullException(nameof(eventDeckService));
            this.resourceTokenService = resourceTokenService ?? throw new ArgumentNullException(nameof(resourceTokenService));
            this.turnOrderService = turnOrderService ?? throw new ArgumentNullException(nameof(turnOrderService));
            cardFlowService = new CardFlowService();
        }

        public bool CanHandle(GameCommand command)
        {
            if (command == null)
            {
                return false;
            }

            return command.Kind == GameCommandKind.ChooseStartPlayer ||
                   command.Kind == GameCommandKind.ChooseInitialLocation ||
                   command.Kind == GameCommandKind.ResolveEntranceEvent;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            switch (command.Kind)
            {
                case GameCommandKind.ChooseStartPlayer:
                    return HandleChooseStartPlayer(state, command);
                case GameCommandKind.ChooseInitialLocation:
                    return HandleChooseInitialLocation(state, command);
                case GameCommandKind.ResolveEntranceEvent:
                    return HandleResolveEntranceEvent(state, command);
                default:
                    return CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.UnknownCommand,
                        "设置命令处理器无法处理该命令。"));
            }
        }

        private static CommandResult HandleChooseStartPlayer(GameState state, GameCommand command)
        {
            if (state.Phase != GamePhase.Setup)
            {
                return Invalid(CommandErrorCode.WrongPhase, "只能在设置阶段选择起始玩家。");
            }

            var player = state.FindPlayer(command.PlayerId);
            if (player == null)
            {
                return Invalid(CommandErrorCode.InvalidPlayer, "起始玩家必须存在于游戏中。");
            }

            state.StartPlayerId = command.PlayerId;
            state.CurrentPlayerId = command.PlayerId;
            ScoreTrackService.InitializePlayerMarkers(state);
            state.Phase = GamePhase.Entrance;

            var message = string.Format("Player {0} was chosen as the start player. Entrance phase begins.", command.PlayerId);
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                GameEvent.Log(message)
            }, message);
        }

        private CommandResult HandleChooseInitialLocation(GameState state, GameCommand command)
        {
            if (state.Phase != GamePhase.Entrance)
            {
                return Invalid(CommandErrorCode.WrongPhase, "只能在入场阶段选择初始城市位置。");
            }

            var player = state.FindPlayer(command.PlayerId);
            if (player == null)
            {
                return Invalid(CommandErrorCode.InvalidPlayer, "选择初始城市位置前，玩家必须存在。");
            }

            if (state.HasPendingChoice())
            {
                return Invalid(CommandErrorCode.PendingChoiceRequired, "请先处理待处理入场事件，再选择其他初始城市位置。");
            }

            if (state.CurrentPlayerId >= 0 && state.CurrentPlayerId != command.PlayerId)
            {
                return Invalid(CommandErrorCode.NotCurrentPlayer, "必须按入场顺序选择初始城市位置。");
            }

            if (!string.IsNullOrEmpty(player.CityLocationId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "玩家已经拥有初始城市位置。");
            }

            MapLocationDefinition location;
            try
            {
                location = mapQueryService.GetLocation(command.TargetId);
            }
            catch (ArgumentException)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "初始城市位置目标必须是已知地图地点。");
            }

            if (!location.CanDockCity)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "初始城市位置必须允许城市停靠。");
            }

            if (!IsInitialLocationAllowed(command.TargetId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "初始城市位置必须是允许的入场地点之一。");
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var otherPlayer = state.Players[i];
                if (otherPlayer.PlayerId != command.PlayerId &&
                    otherPlayer.CityLocationId == command.TargetId)
                {
                    return Invalid(CommandErrorCode.OccupiedSlot, "初始城市位置已被其他玩家的城市占用。");
                }
            }

            player.CityLocationId = command.TargetId;
            BuildFacilityService.EnsureInitialCoreCommandTower(state, player);
            if (!state.Map.OpenLocationIds.Contains(command.TargetId))
            {
                state.Map.OpenLocationIds.Add(command.TargetId);
            }

            var message = string.Format("Player {0} placed their initial city at {1}.", command.PlayerId, command.TargetId);
            var events = new List<GameEvent>
            {
                GameEvent.Log(message)
            };

            if (TryOpenEntranceEventChoice(state, command, events))
            {
                return CommandResult.SuccessResult(events, message);
            }

            CompleteEntranceStep(state, command.PlayerId);
            return CommandResult.SuccessResult(events, message);
        }

        private CommandResult HandleResolveEntranceEvent(GameState state, GameCommand command)
        {
            if (state.Phase != GamePhase.Entrance)
            {
                return Invalid(CommandErrorCode.WrongPhase, "入场事件只能在入场阶段处理。");
            }

            var player = state.FindPlayer(command.PlayerId);
            if (player == null)
            {
                return Invalid(CommandErrorCode.InvalidPlayer, "处理入场事件前，玩家必须存在。");
            }

            var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
            if (pendingChoice == null || pendingChoice.ChoiceType != EntranceEventChoiceType)
            {
                return Invalid(CommandErrorCode.PendingChoiceRequired, "当前没有待处理的入场事件。");
            }

            if (pendingChoice.PlayerId != command.PlayerId)
            {
                return Invalid(CommandErrorCode.NotCurrentPlayer, "只有持有待处理入场事件的玩家可以处理它。");
            }

            var selectedOptionId = GetSelectedOptionId(command);
            var choiceIndex = ParseChoiceIndex(selectedOptionId);
            if (choiceIndex < 0)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "入场事件选项必须是有效选项编号。");
            }

            var card = EventCardDatabase.Get(pendingChoice.CardId);
            if (card == null)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "待处理入场事件牌未知。");
            }

            if (!pendingChoice.OptionIds.Contains(selectedOptionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "入场事件选项不可用。");
            }

            var resolveResult = cardFlowService.ResolvePendingChoice(
                state,
                new CardFlowResolveRequest
                {
                    PlayerId = command.PlayerId,
                    SessionId = state.PendingCardSession == null ? string.Empty : state.PendingCardSession.SessionId,
                    OptionIndex = choiceIndex
                },
                new EntranceEventCardScenario(resourceTokenService));
            if (!resolveResult.Succeeded)
            {
                return CommandResult.Invalid(resolveResult.Validation);
            }

            card = resolveResult.Card;
            CompleteEntranceStep(state, command.PlayerId);

            var message = string.Format("Player {0} resolved entrance event {1} with option {2}.", command.PlayerId, card.Name, selectedOptionId);
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.ChoiceResolved,
                    PlayerId = command.PlayerId,
                    SubjectId = card.CardId,
                    Message = message,
                    Data =
                    {
                        { "cardName", card.Name },
                        { "cardDescription", card.Description }
                    }
                }
            }, message);
        }

        private bool TryOpenEntranceEventChoice(GameState state, GameCommand command, List<GameEvent> events)
        {
            if (resourceTokenService.HasResourceToken(state.Map, command.TargetId))
            {
                return false;
            }

            var startResult = cardFlowService.StartPendingChoice(
                state,
                new CardFlowStartRequest
                {
                    PlayerId = command.PlayerId,
                    TargetId = command.TargetId,
                    SourceCommandId = command.CommandId
                },
                new EntranceEventCardScenario(resourceTokenService));
            if (!startResult.Succeeded)
            {
                return false;
            }

            var card = startResult.Card;

            events.Add(new GameEvent
            {
                Kind = GameEventKind.ChoiceOpened,
                PlayerId = command.PlayerId,
                SubjectId = card.CardId,
                Message = "Entrance event choice opened for " + card.Name + ".",
                Data =
                {
                    { "cardName", card.Name },
                    { "cardDescription", card.Description },
                    { "representativeResourceType", card.RepresentativeResourceType.ToString() },
                    { "representativeResourceAmount", card.RepresentativeResourceAmount.ToString() }
                }
            });

            return true;
        }

        private bool IsInitialLocationAllowed(string locationId)
        {
            if (mapQueryService.Map.MapId != StaticMapDefinitions.FourPlayerMapId)
            {
                return true;
            }

            return StaticMapDefinitions.FourPlayerInitialLocationIds.Contains(locationId);
        }

        private static bool AllPlayersHaveInitialCities(GameState state)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                if (string.IsNullOrEmpty(state.Players[i].CityLocationId))
                {
                    return false;
                }
            }

            return state.Players.Count > 0;
        }

        private void CompleteEntranceStep(GameState state, int playerId)
        {
            if (AllPlayersHaveInitialCities(state))
            {
                if (state.StartPlayerId < 0)
                {
                    state.StartPlayerId = playerId;
                }

                GrantInitialGoldVouchers(state);
                state.CurrentPlayerId = GetFirstTurnPlayerId(state);
                state.Phase = GamePhase.CharacterCover;
                state.ActionRound = 0;
                state.Round = 1;
                return;
            }

            state.CurrentPlayerId = FindNextEntrancePlayerId(state, playerId);
        }

        private int FindNextEntrancePlayerId(GameState state, int playerId)
        {
            var turnOrder = turnOrderService.GetTurnOrder(state);
            var currentIndex = 0;
            for (var i = 0; i < turnOrder.Count; i++)
            {
                if (turnOrder[i] == playerId)
                {
                    currentIndex = i;
                    break;
                }
            }

            for (var offset = 1; offset <= turnOrder.Count; offset++)
            {
                var nextPlayerId = turnOrder[(currentIndex + offset) % turnOrder.Count];
                var player = state.FindPlayer(nextPlayerId);
                if (player != null && string.IsNullOrEmpty(player.CityLocationId))
                {
                    return nextPlayerId;
                }
            }

            return playerId;
        }

        private int GetFirstTurnPlayerId(GameState state)
        {
            var turnOrder = turnOrderService.GetTurnOrder(state);
            return turnOrder.Count > 0 ? turnOrder[0] : state.StartPlayerId;
        }

        private void GrantInitialGoldVouchers(GameState state)
        {
            var turnOrder = turnOrderService.GetTurnOrder(state);
            for (var i = 0; i < turnOrder.Count; i++)
            {
                var player = state.FindPlayer(turnOrder[i]);
                if (player == null)
                {
                    continue;
                }

                player.Resources.GoldVoucher += GetInitialGoldVoucherAmount(state.Players.Count, i);
            }
        }

        private static int GetInitialGoldVoucherAmount(int playerCount, int playerOrderIndex)
        {
            if (playerOrderIndex <= 0)
            {
                return 10;
            }

            if (playerCount == 3)
            {
                return playerOrderIndex == 1 ? 12 : 18;
            }

            if (playerCount == 4)
            {
                switch (playerOrderIndex)
                {
                    case 1:
                        return 12;
                    case 2:
                        return 14;
                    default:
                        return 18;
                }
            }

            return playerOrderIndex == 1 ? 18 : 0;
        }

        private static string GetSelectedOptionId(GameCommand command)
        {
            if (command.OptionIds != null && command.OptionIds.Count > 0)
            {
                return command.OptionIds[0];
            }

            return command.TargetId;
        }

        private static int ParseChoiceIndex(string optionId)
        {
            int choiceIndex;
            if (!int.TryParse(optionId, out choiceIndex))
            {
                return -1;
            }

            return choiceIndex;
        }

        private static CommandResult Invalid(CommandErrorCode errorCode, string reason)
        {
            return CommandResult.Invalid(ValidationResult.Failure(errorCode, reason));
        }
    }
}
