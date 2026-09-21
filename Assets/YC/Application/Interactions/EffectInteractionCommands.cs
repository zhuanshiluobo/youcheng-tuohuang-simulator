using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;

namespace YC.Application.Interactions
{
    /// <summary>展示层共用的回答协议；只提交投影中的身份与版本，规则验证仍在 Host。</summary>
    public static class EffectInteractionCommands
    {
        public static GameCommand Answer(InteractionRequestProjection request, int playerId,
            IEnumerable<string> candidates, bool decline = false)
        {
            if (request == null || !request.VisibleToViewer || request.AnsweringPlayerId != playerId ||
                request.Status != "open" || string.IsNullOrEmpty(request.InteractionId))
                throw new ArgumentException("只能回答当前玩家可见的开放交互。", nameof(request));
            if (decline && !request.AllowDecline)
                throw new ArgumentException("当前交互不允许取消。", nameof(decline));
            var command = new GameCommand
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Kind = GameCommandKind.AnswerInteraction,
                PlayerId = playerId
            };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.InteractionId;
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] =
                request.StateRevision.ToString(CultureInfo.InvariantCulture);
            if (decline) command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "false";
            else if (candidates != null) command.OptionIds.AddRange(candidates);
            return command;
        }
    }
}
