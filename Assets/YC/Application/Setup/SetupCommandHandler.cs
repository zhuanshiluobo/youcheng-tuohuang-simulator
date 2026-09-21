using System;
using System.Collections.Generic;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Application.Setup
{
    /// <summary>入场命令只回答主链交互；城市、资源、抽牌和玩家推进全部由 Effect 驱动。</summary>
    public sealed class SetupCommandHandler : IGameCommandHandler
    {
        private readonly RoundExecutionService round;
        private readonly IMapQueryService map;
        public EffectRegistry EffectRegistry => round.EffectRegistry;
        public SetupCommandHandler(IMapQueryService map)
            : this(map, new EventDeckService(), new ResourceTokenService(), new TurnOrderService()) { }
        public SetupCommandHandler(IMapQueryService map, EventDeckService decks, ResourceTokenService tokens, TurnOrderService order)
            : this(map, decks, tokens, order, null) { }
        public SetupCommandHandler(IMapQueryService map, EventDeckService decks, ResourceTokenService tokens,
            TurnOrderService order, RoundExecutionService sharedRoundExecutionService)
        {
            this.map = map ?? throw new ArgumentNullException(nameof(map));
            round = sharedRoundExecutionService ?? new RoundExecutionService(order);
            PlayerEntranceEffectExecutor.Register(EffectRegistry, map);
            InfluenceEffectExecutor.Register(EffectRegistry, new InfluenceService(map));
            EventCardEffectExecutor.Register(EffectRegistry, map, new InfluenceService(map), decks, tokens);
        }
        public bool CanHandle(GameCommand command) => command != null &&
            (command.Kind == GameCommandKind.ChooseStartPlayer || command.Kind == GameCommandKind.ChooseInitialLocation ||
             command.Kind == GameCommandKind.ResolveEntranceEvent);

        public CommandResult Handle(GameState state, GameCommand command)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!CanHandle(command)) return Invalid(CommandErrorCode.UnknownCommand, "入场命令类型无效。");
            if (command.Kind == GameCommandKind.ChooseStartPlayer)
            {
                if (state.Phase != GamePhase.Setup) return Invalid(CommandErrorCode.WrongPhase, "只能在设置阶段选择起始玩家。");
                if (state.FindPlayer(command.PlayerId) == null) return Invalid(CommandErrorCode.InvalidPlayer, "起始玩家不存在。");
                new RoundExecutionProjector().ProjectEntrance(state, command.PlayerId, command.PlayerId);
                ScoreTrackService.InitializePlayerMarkers(state);
                var start = round.StartEntrance(state);
                return start.IsValid ? CommandResult.SuccessResult(new List<GameEvent> { GameEvent.Log("已确定起始玩家，开始入场。") }, "已确定起始玩家，开始入场。") : CommandResult.Invalid(start);
            }
            if (state.Phase != GamePhase.Entrance) return Invalid(CommandErrorCode.WrongPhase, "当前不是入场阶段。");
            var player = state.FindPlayer(command.PlayerId);
            if (player == null) return Invalid(CommandErrorCode.InvalidPlayer, "入场玩家不存在。");
            if (state.CurrentPlayerId > 0 && state.CurrentPlayerId != command.PlayerId)
                return Invalid(CommandErrorCode.NotCurrentPlayer, "必须按入场主链顺序操作。");
            if (command.Kind == GameCommandKind.ChooseInitialLocation)
            {
                if (!string.IsNullOrEmpty(player.CityLocationId)) return Invalid(CommandErrorCode.InvalidTarget, "玩家已经完成入场选点。");
                MapLocationDefinition location;
                try { location = map.GetLocation(command.TargetId); }
                catch (ArgumentException) { return Invalid(CommandErrorCode.InvalidTarget, "未知入场位置。"); }
                if (!location.CanDockCity || (map.Map.MapId == StaticMapDefinitions.FourPlayerMapId &&
                    !StaticMapDefinitions.FourPlayerInitialLocationIds.Contains(location.LocationId)))
                    return Invalid(CommandErrorCode.InvalidTarget, "该位置不能入场。");
                if (state.Players.Exists(other => other.PlayerId != player.PlayerId && other.CityLocationId == command.TargetId))
                    return Invalid(CommandErrorCode.OccupiedSlot, "入场位置已被占用。");
                if (string.IsNullOrEmpty(state.EffectRuntime.ActiveMainNodeId))
                {
                    if (state.StartPlayerId <= 0) new RoundExecutionProjector().ProjectEntrance(state, player.PlayerId, player.PlayerId);
                    var start = round.StartEntrance(state);
                    if (!start.IsValid) return CommandResult.Invalid(start);
                }
            }
            string type = command.Kind == GameCommandKind.ChooseInitialLocation ? PlayerEntranceEffectExecutor.InteractionTypeId : EventCardEffectExecutor.OptionInteractionTypeId;
            var request = state.EffectRuntime.InteractionRequests.Find(item => item.Status == "open" &&
                item.AnsweringPlayerId == command.PlayerId && item.InteractionTypeId == type);
            if (request == null) return Invalid(CommandErrorCode.PendingChoiceRequired, "没有可回答的入场交互，请先完成当前效果。");
            string candidate = command.OptionIds.Count > 0 ? command.OptionIds[0] : command.TargetId;
            if (command.Kind == GameCommandKind.ResolveEntranceEvent && int.TryParse(candidate, out int index))
            {
                if (index < 0 || index >= request.CandidateIds.Count) return Invalid(CommandErrorCode.InvalidTarget, "事件牌选项无效。");
                candidate = request.CandidateIds[index];
            }
            var answer = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = command.PlayerId, CommandId = command.CommandId };
            answer.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.InteractionId;
            answer.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            answer.OptionIds.Add(candidate);
            return new AnswerInteractionCommandHandler(EffectRegistry, round).Handle(state, answer);
        }
        private static CommandResult Invalid(CommandErrorCode code, string reason) => CommandResult.Invalid(ValidationResult.Failure(code, reason));
    }
}
