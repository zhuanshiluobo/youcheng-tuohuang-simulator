using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;

namespace YC.Domain.State
{
    [Serializable]
    public sealed class PreparationRequestContextView
    {
        public string InteractionId = string.Empty, OwnerEffectId = string.Empty, CandidateSetId = string.Empty;
        public int CandidateSetVersion, StateRevision;
    }

    /// <summary>只包含本人候选和本人当前中间步骤请求，不包含其他席位或未分配企业。</summary>
    [Serializable]
    public sealed class PreparationStateView
    {
        public List<EnterpriseCandidateCardView> OwnEnterpriseCandidates = new List<EnterpriseCandidateCardView>();
        public List<PreparationRequestContextView> Requests = new List<PreparationRequestContextView>();
    }

    public static class PreparationStateProjector
    {
        public static PreparationStateView Project(GameState state, GameStateViewer viewer, IReadOnlyList<InteractionView> visibleRequests)
        {
            var preparation = state?.PreparationRuntime;
            var runtime = state?.EffectRuntime;
            if (viewer.Role != GameStateViewerRole.Player || state?.FindPlayer(viewer.PlayerId) == null ||
                preparation?.Stage != "middle" || preparation.Distribution == null ||
                runtime?.Status != EffectRuntimeStatus.Active || visibleRequests == null ||
                !PreparationFrameworkFlow.TryValidateRuntime(state, null, out _, false)) return null;

            var nodes = runtime.EffectNodes;
            var root = nodes.SingleOrDefault(node => node.EffectId == preparation.OwnerEffectId);
            var middle = nodes.SingleOrDefault(node => node.ParentEffectId == preparation.OwnerEffectId &&
                node.EffectTypeId == PreparationWindowFlow.WindowTypeId &&
                ItemTransferEffectExecutor.Text(node.NormalizedArguments, "window") == PreparationWindowFlow.Middle);
            if (root == null || middle == null || root.FlowStage != "middle" || !root.ChildEffectIds.Contains(middle.EffectId)) return null;

            bool BelongsToMiddle(string ownerId)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var current = nodes.SingleOrDefault(node => node.EffectId == ownerId);
                while (current != null && visited.Add(current.EffectId))
                {
                    if (current.Status == EffectNodeStatus.Completed || current.Status == EffectNodeStatus.Failed ||
                        current.Status == EffectNodeStatus.Faulted) return false;
                    if (current.EffectId == middle.EffectId) return true;
                    var parent = nodes.SingleOrDefault(node => node.EffectId == current.ParentEffectId);
                    if (parent == null || !parent.ChildEffectIds.Contains(current.EffectId)) return false;
                    current = parent;
                }
                return false;
            }

            var result = new PreparationStateView();
            foreach (var request in visibleRequests.Where(request => request != null && request.Status == "open" &&
                request.AnsweringPlayerId == viewer.PlayerId && !string.IsNullOrEmpty(request.InteractionId)))
            {
                var actual = runtime.InteractionRequests.Where(item => item != null && item.Status == "open" &&
                    item.GetStableInteractionId() == request.InteractionId && item.AnsweringPlayerId == viewer.PlayerId &&
                    item.OwnerEffectId == request.OwnerEffectId && item.CandidateSetId == request.CandidateSetId &&
                    item.CandidateSetVersion == request.CandidateSetVersion && item.StateRevision == request.StateRevision).ToList();
                if (actual.Count != 1 || !BelongsToMiddle(actual[0].OwnerEffectId)) continue;
                result.Requests.Add(new PreparationRequestContextView { InteractionId = request.InteractionId,
                    OwnerEffectId = request.OwnerEffectId, CandidateSetId = request.CandidateSetId,
                    CandidateSetVersion = request.CandidateSetVersion, StateRevision = request.StateRevision });
            }
            if (result.Requests.Count == 0) return null;

            var assignment = preparation.Distribution.Assignments.SingleOrDefault(item => item.PlayerId == viewer.PlayerId);
            if (assignment == null || assignment.CandidateItemIds == null || assignment.CandidateItemIds.Count == 0 ||
                assignment.CandidateItemIds.Distinct(StringComparer.Ordinal).Count() != assignment.CandidateItemIds.Count) return null;
            foreach (string id in assignment.CandidateItemIds)
            {
                var item = state.ItemZones.Items.SingleOrDefault(value => value.ItemId == id);
                if (item == null || item.PhysicalKind != ItemPhysicalKind.Card || item.TypeId != "core.board_card" ||
                    string.IsNullOrEmpty(item.DefinitionId) || state.ContentDefinitions?.EnterpriseDefinitionIds?.Contains(item.DefinitionId) != true) return null;
                result.OwnEnterpriseCandidates.Add(new EnterpriseCandidateCardView { ItemId = id, DefinitionId = item.DefinitionId });
            }
            return result;
        }
    }
}
