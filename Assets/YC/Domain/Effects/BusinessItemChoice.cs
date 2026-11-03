using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // 业务流程拥有选择；MoveItem 只接收已经确定的实例和源位置。
    internal static class BusinessItemChoice
    {
        internal static EffectInteractionSpec Create(EffectExecutionContext context, IEnumerable<string> candidates,
            string semantic, string prompt, string kind = "lua.choice")
        {
            var supplied = GameplayPresentationNormalizer.Clone(context.Node.Presentation?.Interaction);
            if (!string.IsNullOrEmpty(supplied?.Prompt?.Key)) prompt = supplied.Prompt.Key;
            var request = new EffectInteractionSpec
            {
                InteractionTypeId = kind, AnsweringPlayerId = context.Node.PlayerId,
                Visibility = GameStateVisibilityPolicy.Owner, PromptKey = prompt,
                MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id",
                PromptParameters = Object("interactionSemantic", NormalizedValue.CreateString(semantic)),
                Presentation = supplied ?? new GameplayInteractionPresentation { Prompt = new GameplayTextRef(prompt) }
            };
            request.CandidateIds.AddRange(candidates.Distinct().OrderBy(id => id, StringComparer.Ordinal));
            var prepared=PrepareInteraction(context,request);
            // 完整候选在类型化请求中；超出NormalizedValue的定义投影不复制到提示参数。
            // 现有候选页可从本人可见的真实物品解析定义，不能裁剪候选全集。
            if(!prepared.PromptParameters.IsValid())prepared.PromptParameters=Object("interactionSemantic",NormalizedValue.CreateString(semantic));
            return prepared;
        }

        internal static bool TryGet(GameState state, EffectNodeRuntimeState node, string semantic,
            out InteractionRequest request, string kind = "lua.choice")
        {
            var requests = state.EffectRuntime.InteractionRequests.Where(r => r.OwnerEffectId == node.EffectId).ToList();
            request = requests.Count == 1 ? requests[0] : null;
            if (request == null) return false;
            var current = request;
            var set = state.EffectRuntime.CandidateResolutions.SingleOrDefault(s => s.CandidateSetId == current.CandidateSetId && s.Version == current.CandidateSetVersion);
            return request.InteractionTypeId == kind && request.SourceNodeId == node.EffectId &&
                request.AnsweringPlayerId == node.PlayerId && request.Visibility == GameStateVisibilityPolicy.Owner &&
                request.MinSelections == 1 && request.MaxSelections == 1 && !request.AllowDecline && request.AnswerSchema == "candidate_id" &&
                Text(request.PromptParameters, "interactionSemantic") == semantic &&
                request.CandidateSetId == StableIdFactory.Create("candidate", node.EffectId, kind, "0") &&
                request.CandidateResolutionId == request.CandidateSetId && request.CandidateSetVersion == 1 &&
                request.CandidateIds.Count > 0 && request.CandidateIds.Distinct().Count() == request.CandidateIds.Count &&
                set?.SourceEffectId == node.EffectId && set.ExecutingPlayerId == node.PlayerId && set.CandidateKind == kind &&
                set.FinalCandidateIds.SequenceEqual(request.CandidateIds);
        }

        internal static string Answer(InteractionRequest request)
        {
            if (request?.Status != "answered") return "";
            var answer = request.NormalizedAnswer;
            if (answer?.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
            string id = answer?.Kind == NormalizedValueKind.String ? answer.StringValue :
                answer?.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : "";
            return request.CandidateIds.Contains(id) ? id : "";
        }
    }
}
