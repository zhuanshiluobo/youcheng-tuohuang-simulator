using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Interactions
{
    [Serializable]
    public sealed class InteractionRequestProjection
    {
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
        {
            if (request == null) return null;

            // 统一复用 GameStateView 的权限矩阵，避免旧 Interaction 入口拥有第二套
            // 可见性语义；不可见请求仍只返回安全的空壳。
            bool visible = GameStateVisibilityPolicy.IsVisible(
                request.Visibility,
                request.AnsweringPlayerId,
                GameStateViewer.Player(viewerPlayerId));
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
