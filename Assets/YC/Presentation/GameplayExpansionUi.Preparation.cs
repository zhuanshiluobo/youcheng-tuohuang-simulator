using System;
using System.Linq;
using UnityEngine;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    public sealed partial class GameplayExpansionUi
    {
        private EnterpriseSelectionDialogView preparationPreview;
        private object preparationPreviewBinding;
        private Func<bool> preparationPreviewCurrent;

        internal static bool IsPreparationEnterpriseCandidate(GameStateView view, InteractionRequestProjection request, string candidateId, string definitionId)
        {
            if (!GameplayHudFrame.MatchesSummaryRequest(request, view) || view.Preparation?.Requests == null ||
                view.Preparation.OwnEnterpriseCandidates == null || string.IsNullOrEmpty(candidateId) || string.IsNullOrEmpty(definitionId)) return false;
            var contexts = view.Preparation.Requests.Where(item => item != null && item.InteractionId == request.InteractionId &&
                item.OwnerEffectId == request.OwnerEffectId && item.CandidateSetId == request.CandidateSetId &&
                item.CandidateSetVersion == request.CandidateSetVersion && item.StateRevision == request.StateRevision).ToArray();
            var candidates = view.Preparation.OwnEnterpriseCandidates;
            return contexts.Length == 1 && candidates.Count > 0 && candidates.All(item => item != null &&
                !string.IsNullOrWhiteSpace(item.ItemId) && !string.IsNullOrWhiteSpace(item.DefinitionId)) &&
                candidates.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() == candidates.Count &&
                candidates.Any(item => item.ItemId == candidateId && item.DefinitionId == definitionId);
        }

        private bool TryResolvePreparationRequest(InteractionRequestProjection displayed, out GameStateView view, out InteractionRequestProjection actual)
        {
            view = VisibleView(); actual = null;
            if (displayed == null || view?.ViewerRole != GameStateViewerRole.Player || displayed.VisibleToViewer != true ||
                displayed.Status != "open" || displayed.AnsweringPlayerId != view.ViewerPlayerId) return false;
            var requests = VisibleInteractionRequestSource.Read(null, view, view.ViewerPlayerId)
                .Where(item => item.GetStableInteractionId() == displayed.InteractionId).ToArray();
            if (requests.Length != 1) return false;
            actual = InteractionRequestProjector.ProjectForPlayer(requests[0], view.ViewerPlayerId);
            if (!SamePreparationRequest(view.GameId, actual, displayed)) return false;
            var candidates = view.Preparation?.OwnEnterpriseCandidates;
            var candidateView = view; var actualRequest = actual;
            return candidates != null && candidates.Count > 0 && candidates.All(candidate => candidate != null &&
                definitions?.FindEnterprise(candidate.DefinitionId) != null &&
                IsPreparationEnterpriseCandidate(candidateView, actualRequest, candidate.ItemId, candidate.DefinitionId));
        }

        internal bool CanPreviewPreparationEnterprises(InteractionRequestProjection request) =>
            TryResolvePreparationRequest(request, out _, out _);

        private static bool SamePreparationRequest(string gameId, InteractionRequestProjection actual, InteractionRequestProjection displayed)
        {
            if (actual == null || displayed == null || actual.StateRevision != displayed.StateRevision ||
                actual.CandidateIds == null || displayed.CandidateIds == null ||
                actual.CandidateIds.Count != displayed.CandidateIds.Count ||
                actual.CandidateIds.Distinct(StringComparer.Ordinal).Count() != actual.CandidateIds.Count ||
                displayed.CandidateIds.Distinct(StringComparer.Ordinal).Count() != displayed.CandidateIds.Count ||
                !actual.CandidateIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(displayed.CandidateIds.OrderBy(id => id, StringComparer.Ordinal))) return false;

            // 角色卡展示可能重排候选，以及与候选一一对应的定义数组；其他合同字段仍必须与正式请求一致。
            var canonical = GameStateCloneService.DeepClone(displayed);
            var definitions = canonical.PromptParameters?.Properties?.Find(entry => entry.Name == "candidateDefinitionIds")?.Value;
            if (definitions?.Kind == NormalizedValueKind.Array && definitions.Items?.Count == canonical.CandidateIds.Count)
            {
                var values = definitions.Items.ToArray();
                definitions.Items = actual.CandidateIds.Select(id => values[canonical.CandidateIds.IndexOf(id)]).ToList();
            }
            canonical.CandidateIds = actual.CandidateIds.ToList();
            return InteractionCommandIntent.SameContract(gameId, actual, gameId, canonical);
        }

        internal bool OpenPreparationEnterprisePreview(InteractionRequestProjection request, RectTransform parent,
            Func<bool> sourceCurrent, Action afterReturn)
        {
            if (hud?.DialogRegistry == null || parent == null || sourceCurrent?.Invoke() != true ||
                !TryResolvePreparationRequest(request, out var view, out var actual)) return false;
            ClearPreparationEnterprisePreview();
            var binding = preparationPreviewBinding = new object();
            string game = view.GameId;
            int viewer = view.ViewerPlayerId;
            bool Current() => ReferenceEquals(binding, preparationPreviewBinding) && sourceCurrent() &&
                TryResolvePreparationRequest(request, out var now, out _) && now.GameId == game && now.ViewerPlayerId == viewer;
            preparationPreviewCurrent = Current;
            var boards = view.Preparation.OwnEnterpriseCandidates.Select(candidate =>
                ProjectCandidateBoard(view, candidate.ItemId, candidate.DefinitionId, actual)).ToArray();
            var projection = new EnterpriseSelectionProjection { GameId = game, ViewerPlayerId = viewer,
                RequestId = actual.InteractionId + ":preparation-preview", OwnerEffectId = actual.OwnerEffectId,
                Revision = actual.StateRevision, CandidateSetId = actual.CandidateSetId, CandidateSetVersion = actual.CandidateSetVersion,
                RequestContract = InteractionCommandIntent.ContractKey(actual), Mode = EnterpriseSelectionMode.Company,
                ReadOnly = true, CanCancel = true, SubmissionAvailable = false, MinSelections = 0, MaxSelections = 0,
                Context = EntrepreneurCopy.BrowseHint, Boards = boards,
                Targets = boards.Select(board => new EnterpriseSelectionTarget { Id = board.Id, BoardId = board.Id, CompanyId = board.Id,
                    Label = board.Label, Description = board.Summary, Details = board.Details, Available = false }).ToArray() };
            if (preparationPreview == null) preparationPreview = hud.DialogRegistry.InstantiateEnterpriseSelection(parent, false);
            preparationPreview.BindGameplayText(ResolveEnterpriseDetails, RenderEnterpriseDetails);
            void Leave()
            {
                if (!Current()) return;
                var frame = GameplayHudFrame.Active;
                if (frame != null) frame.CloseInformationPage(preparationPreview.gameObject);
                else preparationPreview.gameObject.SetActive(false);
                preparationPreview.ClearPresentation();
                preparationPreviewBinding = null; preparationPreviewCurrent = null;
                afterReturn?.Invoke();
            }
            preparationPreview.BindSubmission(() => Current() ? projection : null, null, Leave);
            preparationPreview.BindReadOnlyContinuation(Current, Leave, hud.DialogRegistry.EffectDialogLayoutProfile.ReadOnlyCloseLabel, EntrepreneurCopy.PreviewTitle);
            preparationPreview.Present(projection);
            return true;
        }

        private void RefreshPreparationEnterprisePreview()
        {
            if (preparationPreviewBinding != null && (preparationPreview == null || !preparationPreview.IsShowing ||
                preparationPreviewCurrent?.Invoke() != true)) ClearPreparationEnterprisePreview();
        }

        private void ClearPreparationEnterprisePreview()
        {
            preparationPreviewBinding = null; preparationPreviewCurrent = null;
            if (preparationPreview != null) preparationPreview.ClearPresentation();
        }
    }
}
