using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Interactions;
using YC.Domain.State;

namespace YC.Presentation
{
    /// <summary>消费明确的玩家候选标识或与候选对应的 typed player 声明；不猜测普通数字／字符串选项。</summary>
    internal static class PlayerSelectionRequestAdapter
    {
        internal static PlayerSelectionConfig Build(InteractionRequestProjection request, GameState visibleState,
            int viewerId, IReadOnlyCollection<string> selected)
        {
            if (request?.CandidateIds == null || request.CandidateIds.Count == 0 || visibleState?.Players == null ||
                visibleState.Players.Count > 4 || request.MaxSelections > 4 ||
                request.CandidateIds.Any(id => !TryId(request,id,out _))) return null;
            var eligible = new HashSet<int>(request.CandidateIds.Select(id => { TryId(request,id,out var n); return n; }));
            if (eligible.Count != request.CandidateIds.Count || eligible.Any(id => visibleState.FindPlayer(id) == null)) return null;
            return new PlayerSelectionConfig
            {
                RequestId = request.InteractionId, Revision = request.StateRevision,
                Mode = request.MaxSelections == 1 ? PlayerSelectionMode.Single : PlayerSelectionMode.Multiple,
                Min = request.MinSelections, Max = request.MaxSelections, AllowCancel = request.AllowDecline,
                Players = new YC.Domain.Rules.TurnOrderService().GetLaunchPlayerOrder(visibleState)
                    .Select(id => visibleState.FindPlayer(id)).Where(p => p != null).Select(p => new PlayerSelectionOption
                    { Id = p.PlayerId, Name = p.Name, Color = p.Color, IsSelf = p.PlayerId == viewerId, Eligible = eligible.Contains(p.PlayerId) }).ToArray(),
                SelectedIds = (selected ?? Array.Empty<string>()).Where(id => request.CandidateIds.Contains(id) && TryId(request,id,out _))
                    .Select(id => { TryId(request,id,out var n); return n; }).ToArray()
            };
        }
        internal static IReadOnlyList<string> CandidateIds(InteractionRequestProjection request, IReadOnlyList<int> players)
        {
            // 回答始终使用本次请求的原稳定标识，不能把 p4 改写成 player:4。
            return players.Select(player => request.CandidateIds.Single(id => TryId(request,id,out var n) && n == player)).ToArray();
        }
        private static bool TryId(InteractionRequestProjection request, string id, out int playerId)
        {
            if (TryId(id,out playerId)) return true;
            var label = request.Presentation?.Options?.SingleOrDefault(option => option.CandidateId == id)?.Label;
            if (label?.Arguments?.Count != 1) return false;
            var argument = label.Arguments[0]; var value = argument.Value;
            if (argument.Kind != "player" || value?.Kind != NormalizedValueKind.StableReference ||
                value.ReferenceType != "player" || string.IsNullOrEmpty(value.ReferenceId)) return false;
            // 先解码正式 typed 引用，再核候选是同一玩家的稳定标识或十进制别名。
            // 刻俄柏使用数字候选+p引用；含资源或物品身份的复合候选仍属于原效果页。
            var reference = value.ReferenceId;
            var key = reference.StartsWith("p",StringComparison.Ordinal) ? reference.Substring(1) : reference;
            return int.TryParse(key,NumberStyles.None,CultureInfo.InvariantCulture,out playerId) && playerId > 0 &&
                (id == reference || id == playerId.ToString(CultureInfo.InvariantCulture));
        }
        internal static bool TryId(string id, out int playerId)
        {
            playerId = -1;
            return !string.IsNullOrEmpty(id) && id.StartsWith("player:",StringComparison.Ordinal) &&
                int.TryParse(id.Substring(7),out playerId) && playerId >= 0;
        }
    }
}
