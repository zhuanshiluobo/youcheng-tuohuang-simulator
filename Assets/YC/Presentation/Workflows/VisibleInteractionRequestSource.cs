using System.Collections.Generic;
using YC.Domain.Interactions;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    /// <summary>展示和提交校验共用的可见请求源；收到客户端 View 后绝不回退读取 Host 对象图。</summary>
    public static class VisibleInteractionRequestSource
    {
        public static IReadOnlyList<InteractionRequest> Read(GameState state, GameStateView view, int playerId)
        {
            var result = new List<InteractionRequest>();
            if (playerId < 0) return result;
            if (view != null)
            {
                if (view.ViewerRole != GameStateViewerRole.Host &&
                    (view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != playerId)) return result;
                if (view.Interactions == null) return result;
                foreach (var item in view.Interactions)
                {
                    if (item == null || item.Status != "open" || item.AnsweringPlayerId != playerId ||
                        !GameStateVisibilityPolicy.IsVisible(item.Visibility, item.AnsweringPlayerId, GameStateViewer.Player(playerId))) continue;
                    result.Add(new InteractionRequest
                    {
                        InteractionId = item.InteractionId, RequestId = item.InteractionId,
                        SourceNodeId = item.SourceNodeId, OwnerEffectId = item.OwnerEffectId,
                        InteractionTypeId = item.Kind, AnsweringPlayerId = item.AnsweringPlayerId,
                        Visibility = item.Visibility, PromptKey = item.PromptKey,
                        PromptParameters = item.PromptParameters?.Clone(), CandidateSetId = item.CandidateSetId,
                        CandidateSetVersion = item.CandidateSetVersion, CandidateIds = item.CandidateIds == null ? new List<string>() : new List<string>(item.CandidateIds),
                        MinSelections = item.MinSelections, MaxSelections = item.MaxSelections,
                        AllowDecline = item.AllowDecline, AnswerSchema = item.AnswerSchema,
                        Status = item.Status, StateRevision = item.StateRevision, NormalizedAnswer = item.Answer?.Clone()
                    });
                }
                return result;
            }
            if (state?.EffectRuntime?.InteractionRequests == null) return result;
            foreach (var item in state.EffectRuntime.InteractionRequests)
            {
                var projected = InteractionRequestProjector.ProjectForPlayer(item, playerId);
                if (projected == null || !projected.VisibleToViewer || projected.Status != "open" || projected.AnsweringPlayerId != playerId) continue;
                result.Add(new InteractionRequest
                {
                    InteractionId = projected.InteractionId, RequestId = projected.InteractionId,
                    SourceNodeId = projected.SourceNodeId, OwnerEffectId = projected.OwnerEffectId,
                    InteractionTypeId = projected.InteractionTypeId, AnsweringPlayerId = projected.AnsweringPlayerId,
                    Visibility = projected.Visibility, PromptKey = projected.PromptKey, PromptParameters = projected.PromptParameters,
                    CandidateSetId = projected.CandidateSetId, CandidateSetVersion = projected.CandidateSetVersion,
                    CandidateIds = projected.CandidateIds, MinSelections = projected.MinSelections, MaxSelections = projected.MaxSelections,
                    AllowDecline = projected.AllowDecline, AnswerSchema = projected.AnswerSchema, Status = projected.Status,
                    StateRevision = projected.StateRevision, NormalizedAnswer = projected.NormalizedAnswer
                });
            }
            return result;
        }
    }
}
