using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Interactions;
using YC.Domain.State;

namespace YC.Presentation
{
    /// <summary>只将明确的 player: 稳定候选转换为玩家页面，不猜测普通选项或卡牌 ID。</summary>
    internal static class PlayerSelectionRequestAdapter
    {
        internal static PlayerSelectionConfig Build(InteractionRequestProjection request, GameState visibleState,
            int viewerId, IReadOnlyCollection<string> selected)
        {
            if (request?.CandidateIds == null || request.CandidateIds.Count == 0 || visibleState?.Players == null ||
                visibleState.Players.Count > 4 || request.MaxSelections > 4 ||
                request.CandidateIds.Any(id => !TryId(id,out _))) return null;
            var eligible = new HashSet<int>(request.CandidateIds.Select(id => { TryId(id,out var n); return n; }));
            return new PlayerSelectionConfig
            {
                RequestId = request.InteractionId, Revision = request.StateRevision,
                Mode = request.MaxSelections == 1 ? PlayerSelectionMode.Single : PlayerSelectionMode.Multiple,
                Min = request.MinSelections, Max = request.MaxSelections, AllowCancel = request.AllowDecline,
                Players = visibleState.Players.Where(p => p != null).Select(p => new PlayerSelectionOption
                    { Id = p.PlayerId, Name = p.Name, Color = p.Color, IsSelf = p.PlayerId == viewerId, Eligible = eligible.Contains(p.PlayerId) }).ToArray(),
                SelectedIds = (selected ?? Array.Empty<string>()).Where(id => TryId(id,out _))
                    .Select(id => { TryId(id,out var n); return n; }).ToArray()
            };
        }
        internal static bool TryId(string id, out int playerId)
        {
            playerId = -1;
            return !string.IsNullOrEmpty(id) && id.StartsWith("player:",StringComparison.Ordinal) &&
                int.TryParse(id.Substring(7),out playerId) && playerId >= 0;
        }
    }
}
