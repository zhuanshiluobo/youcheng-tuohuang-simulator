using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Domain.Interactions
{
    [Serializable]
    public sealed class InteractionRequestProjection
    {
        public YC.Domain.Presentation.GameplayInteractionPresentation Presentation;
        public bool VisibleToViewer;
        public string InteractionId = string.Empty;
        public string SourceNodeId = string.Empty;
        public string InteractionTypeId = string.Empty;
        public string OwnerEffectId = string.Empty;
        public int AnsweringPlayerId = -1;
        public string Visibility = string.Empty;
        public string PromptKey = string.Empty;
        public NormalizedValue PromptParameters = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        public string CandidateSetId = string.Empty;
        public int CandidateSetVersion;
        public List<string> CandidateIds = new List<string>();
        public int MinSelections;
        public int MaxSelections;
        public bool AllowDecline;
        public string AnswerSchema = string.Empty;
        public string Status = string.Empty;
        public int StateRevision;
        public NormalizedValue NormalizedAnswer;
    }

    public static class InteractionRequestProjector
    {
        public static InteractionRequestProjection ProjectForPlayer(
            InteractionRequest request,
            int viewerPlayerId)
            => ProjectForPlayerCore(request, viewerPlayerId, null);

        public static InteractionRequestProjection ProjectForPlayer(
            InteractionRequest request, int viewerPlayerId, GameStateView visibleView, string expectedGameId)
        {
            Func<YC.Domain.Presentation.GameplayTextArgument, bool> canSee = null;
            if (request != null && visibleView?.Interactions != null && !string.IsNullOrEmpty(expectedGameId) &&
                visibleView.GameId == expectedGameId && visibleView.ViewerRole == GameStateViewerRole.Player &&
                visibleView.ViewerPlayerId == viewerPlayerId && visibleView.StateRevision >= request.StateRevision)
            {
                var matches = visibleView.Interactions.Where(value => value != null &&
                    value.InteractionId == request.GetStableInteractionId()).Take(2).ToArray();
                if (matches.Length == 1)
                {
                    var match = matches[0];
                    if (match.SourceNodeId == request.SourceNodeId && match.OwnerEffectId == request.OwnerEffectId &&
                        match.Kind == request.InteractionTypeId && match.AnsweringPlayerId == request.AnsweringPlayerId &&
                        match.Visibility == request.Visibility && match.StateRevision == request.StateRevision &&
                        match.CandidateSetId == request.CandidateSetId && match.CandidateSetVersion == request.CandidateSetVersion &&
                        match.MinSelections == request.MinSelections && match.MaxSelections == request.MaxSelections &&
                        match.AllowDecline == request.AllowDecline && match.AnswerSchema == request.AnswerSchema && match.Status == request.Status &&
                        match.CandidateIds != null && request.CandidateIds != null && match.CandidateIds.SequenceEqual(request.CandidateIds))
                        canSee = GameStateViewProjector.CreateGameplayTextArgumentVisibility(visibleView);
                }
            }
            return ProjectForPlayerCore(request, viewerPlayerId, canSee);
        }

        private static InteractionRequestProjection ProjectForPlayerCore(InteractionRequest request, int viewerPlayerId,
            Func<YC.Domain.Presentation.GameplayTextArgument, bool> canSeeArgument)
        {
            if (request == null) return null;

            // 统一复用 GameStateView 的权限矩阵，避免旧 Interaction 入口拥有第二套
            // 可见性语义；不可见请求仍只返回安全的空壳。
            bool visible = GameStateVisibilityPolicy.IsVisible(
                request.Visibility,
                request.AnsweringPlayerId,
                GameStateViewer.Player(viewerPlayerId));
            var rows = visible ? ItemTransferEffectExecutor.Value(request.PromptParameters, "rowIds") : null;
            IEnumerable<string> rowIds = rows?.Kind == NormalizedValueKind.Array
                ? rows.Items.Where(value => value != null).Select(value => value.Kind == NormalizedValueKind.StableReference
                    ? value.ReferenceId : value.Kind == NormalizedValueKind.String ? value.StringValue : null)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                : Array.Empty<string>();
            var projection = new InteractionRequestProjection
            {
                VisibleToViewer = visible,
                // 不可见请求不返回稳定 ID；否则即使候选正文被清空，也会暴露隐藏流程的
                // 数量和生命周期。GameStateViewProjector 会把它聚合成等待玩家状态。
                InteractionId = visible ? request.GetStableInteractionId() : string.Empty,
                SourceNodeId = visible ? request.SourceNodeId : string.Empty,
                InteractionTypeId = visible ? request.InteractionTypeId : string.Empty,
                OwnerEffectId = visible ? request.OwnerEffectId : string.Empty,
                AnsweringPlayerId = visible ? request.AnsweringPlayerId : -1,
                Visibility = visible ? request.Visibility ?? string.Empty : GameStateVisibilityPolicy.Public,
                PromptKey = visible ? request.PromptKey : string.Empty,
                Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.ProjectInteraction(request.Presentation, visible, request.CandidateIds, rowIds, canSeeArgument),
                PromptParameters = visible && request.PromptParameters != null
                    ? request.PromptParameters.Clone()
                    : NormalizedValue.CreateObject(new List<NormalizedValueEntry>()),
                CandidateSetId = visible ? request.CandidateSetId : string.Empty,
                CandidateSetVersion = visible ? request.CandidateSetVersion : 0,
                CandidateIds = visible && request.CandidateIds != null
                    ? new List<string>(request.CandidateIds)
                    : new List<string>(),
                MinSelections = visible ? request.MinSelections : 0,
                MaxSelections = visible ? request.MaxSelections : 0,
                AllowDecline = visible && request.AllowDecline,
                AnswerSchema = visible ? request.AnswerSchema : string.Empty,
                Status = visible ? request.Status ?? string.Empty : string.Empty,
                StateRevision = visible ? request.StateRevision : 0,
                NormalizedAnswer = visible && request.AnsweringPlayerId == viewerPlayerId &&
                                    request.NormalizedAnswer != null
                    ? request.NormalizedAnswer.Clone()
                    : null
            };
            return projection;
        }

    }
}
