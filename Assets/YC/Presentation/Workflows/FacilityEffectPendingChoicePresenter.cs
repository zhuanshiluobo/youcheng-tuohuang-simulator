using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    /// <summary>
    /// 将设施待选会话转换为提交命令。这里只复制玩家选择，不判断设施规则是否合法。
    /// </summary>
    public sealed class FacilityEffectPendingChoicePresenter
    {
        public bool TryGetPending(GameState state, int localPlayerId, out PendingCardSessionState pending)
        {
            pending = state == null ? null : state.PendingCardSession;
            return pending != null &&
                   pending.IsValid() &&
                   pending.PlayerId == localPlayerId &&
                   string.Equals(
                       pending.ScenarioId,
                       FacilityPendingChoiceTypes.ScenarioId,
                       StringComparison.Ordinal);
        }

        public GameCommand CreateResolveCommand(
            PendingCardSessionState pending,
            int playerId,
            string optionId,
            IReadOnlyDictionary<string, string> parameters = null)
        {
            if (pending == null || !pending.IsValid() ||
                !string.Equals(pending.ScenarioId, FacilityPendingChoiceTypes.ScenarioId, StringComparison.Ordinal))
            {
                throw new ArgumentException("当前没有有效的设施待选会话。", nameof(pending));
            }

            var command = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = playerId,
                SourceId = pending.CardId
            };
            command.Parameters[ResolveFacilityEffectCommandHandler.PendingSessionIdParameter] = pending.SessionId;

            if (!string.IsNullOrEmpty(optionId))
            {
                command.OptionIds.Add(optionId);
                command.Parameters[ResolveFacilityEffectCommandHandler.OptionIdParameter] = optionId;
            }

            if (parameters != null)
            {
                foreach (var entry in parameters)
                {
                    command.Parameters[entry.Key] = entry.Value ?? string.Empty;
                }
            }

            return command;
        }
    }
}
