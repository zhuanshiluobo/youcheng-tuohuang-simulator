using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Presentation.Workflows
{
    /// <summary>一个本地草稿；查询、剪枝与费用全部来自领域投影，最终只生成一次候选答案。</summary>
    public sealed class RouteResourceSelectionPresenter
    {
        private readonly Func<GameState> state;
        private readonly IRouteResourceSelectionService service;
        private readonly HashSet<string> deselectedLocations = new HashSet<string>(StringComparer.Ordinal);
        private IReadOnlyList<string> selectableLocations = Array.Empty<string>();

        public RouteResourceSelectionPresenter(Func<GameState> state, IRouteResourceSelectionService service)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public RouteSelectionSession Session { get; private set; }
        public RouteSelectionRequestBinding RequestBinding { get; private set; }
        public RouteSelectionDraft Draft { get; private set; }
        public RouteSelectionProjection Projection { get; private set; }
        public bool IsActive => Session != null;
        public bool CanConfirm => Projection != null && Projection.Validation.IsValid &&
            (Projection.Preview != null && Projection.Preview.FinalizationValidation.IsValid ||
             Session.MinimumLocations == 0 && Draft.SelectedLocationIds.Count == 0);

        public void BeginLocalPreview(RouteSelectionSession session, RouteSelectionDraft draft, RouteSelectionProjection projection)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Draft = draft ?? throw new ArgumentNullException(nameof(draft));
            Projection = projection ?? throw new ArgumentNullException(nameof(projection));
            RequestBinding = null;
            selectableLocations = session.SelectableLocationUpperBound;
            deselectedLocations.Clear();
        }

        public void Begin(RouteSelectionSession session, RouteSelectionRequestBinding binding,
            IReadOnlyList<string> selectableLocationIds)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            RequestBinding = binding ?? throw new ArgumentNullException(nameof(binding));
            if (binding.SessionId != session.SessionId) throw new ArgumentException(nameof(binding));
            selectableLocations = Array.AsReadOnly((selectableLocationIds ?? throw new ArgumentNullException(nameof(selectableLocationIds))).ToArray());
            deselectedLocations.Clear();
            Draft = new RouteSelectionDraft(session.SessionId, 0, Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<RouteRecipientChoice>());
            RefreshFromState();
        }

        public void Clear()
        {
            Session = null; RequestBinding = null; Draft = null; Projection = null;
            selectableLocations = Array.Empty<string>();
            deselectedLocations.Clear();
        }

        public bool TryRefreshOpenBinding(RouteSelectionSession session, RouteSelectionRequestBinding binding,
            IReadOnlyList<string> selectableLocationIds, out string diagnostic)
        {
            diagnostic = "route_selection_binding_changed";
            if (Session == null || RequestBinding == null || session == null || binding == null || selectableLocationIds == null ||
                Session.SessionId != session.SessionId || Session.OwnerEffectId != session.OwnerEffectId || Session.PlayerId != session.PlayerId ||
                Session.Purpose != session.Purpose || Session.MapId != session.MapId || Session.StartLocationId != session.StartLocationId ||
                Session.ActionContextId != session.ActionContextId || Session.StartGoldVoucherAmount != session.StartGoldVoucherAmount ||
                Session.BudgetBindingId != session.BudgetBindingId || Session.MinimumLocations != session.MinimumLocations ||
                Session.MaximumLocations != session.MaximumLocations || Session.AllowCancelBeforeCommit != session.AllowCancelBeforeCommit ||
                Session.Stage != session.Stage || Session.SchemaVersion != session.SchemaVersion ||
                Session.OpenedAtStateRevision != session.OpenedAtStateRevision || RequestBinding.SchemaVersion != binding.SchemaVersion ||
                !Session.SelectableLocationUpperBound.SequenceEqual(session.SelectableLocationUpperBound) ||
                RequestBinding.SessionId != binding.SessionId || RequestBinding.CandidateSetId != binding.CandidateSetId ||
                RequestBinding.CandidateSetVersion != binding.CandidateSetVersion || RequestBinding.ScopeKey != binding.ScopeKey ||
                RequestBinding.FrozenFactsDigest != binding.FrozenFactsDigest || binding.RequestStateRevision < RequestBinding.RequestStateRevision ||
                !selectableLocations.SequenceEqual(selectableLocationIds)) return false;
            Session = session; RequestBinding = binding;
            RefreshFromState();
            diagnostic = Projection?.Validation.Reason ?? string.Empty;
            return true;
        }

        public void RefreshFromState()
        {
            if (!IsActive || state() == null) return;
            Projection = service.Query(state(), Session, Draft, selectableLocations);
            // 撤销一段路后，按领域给出的可达结果清理失去连接的草稿内容，不在 UI 另建图。
            if (Projection.ReachableLocationIds == null || Projection.RouteOptions == null) return;
            var reachable = new HashSet<string>(Projection.ReachableLocationIds, StringComparer.Ordinal);
            var connectedRoutes = new HashSet<string>(Projection.RouteOptions
                .Where(option => option.CoveredLocationIds.Any(reachable.Contains))
                .Select(option => option.RouteId), StringComparer.Ordinal);
            var routes = Draft.ConfirmedRouteIds.Where(connectedRoutes.Contains).ToArray();
            var locations = Draft.SelectedLocationIds.Where(reachable.Contains).ToList();
            if (Session.Purpose != RouteSelectionPurpose.Exploration)
            {
                foreach (var option in Projection.LocationOptions)
                    if (option.IsReachableInOriginalDraft && !deselectedLocations.Contains(option.LocationId) &&
                        !locations.Contains(option.LocationId) && locations.Count < Session.MaximumLocations)
                        locations.Add(option.LocationId);
            }
            if (!routes.SequenceEqual(Draft.ConfirmedRouteIds) || !locations.SequenceEqual(Draft.SelectedLocationIds))
            {
                var routeSet = new HashSet<string>(routes, StringComparer.Ordinal);
                Draft = Draft.WithSelection(routes, locations,
                    Draft.RecipientChoices.Where(choice => routeSet.Contains(choice.RouteId)).ToArray());
                Projection = service.Query(state(), Session, Draft, selectableLocations);
            }
        }

        public RouteSelectionRouteOption SelectRoute(string routeId)
        {
            var option = FindRoute(routeId);
            if (option == null || option.FreeReason != RouteFreeReason.None ||
                (!option.IsFrontier && !option.IsOriginallyConfirmed)) return null;
            Draft = Draft.WithSelection(Draft.ConfirmedRouteIds, Draft.SelectedLocationIds,
                Draft.RecipientChoices, routeId);
            Projection = service.Query(state(), Session, Draft, selectableLocations);
            return FindRoute(routeId);
        }

        public bool ConfirmRecipient(string routeId, string subjectKey)
        {
            var option = FindRoute(routeId);
            if (option == null || Draft.EditingRouteId != routeId ||
                option.FreeReason != RouteFreeReason.None ||
                (!option.IsFrontier && !option.IsOriginallyConfirmed) ||
                !option.EligibleRecipients.Any(recipient => recipient.SubjectKey == subjectKey)) return false;
            var routes = Draft.ConfirmedRouteIds.ToList();
            if (!routes.Contains(routeId)) routes.Add(routeId);
            var recipients = Draft.RecipientChoices.Where(choice => choice.RouteId != routeId).ToList();
            recipients.Add(new RouteRecipientChoice(routeId, subjectKey));
            Draft = Draft.WithSelection(routes, Draft.SelectedLocationIds, recipients);
            RefreshFromState();
            return true;
        }

        public void CancelRouteEdit()
        {
            if (!IsActive) return;
            Draft = Draft.WithSelection(Draft.ConfirmedRouteIds, Draft.SelectedLocationIds, Draft.RecipientChoices);
            RefreshFromState();
        }

        public bool RemoveRoute(string routeId)
        {
            if (!IsActive || !Draft.ConfirmedRouteIds.Contains(routeId)) return false;
            Draft = Draft.WithSelection(Draft.ConfirmedRouteIds.Where(id => id != routeId).ToArray(),
                Draft.SelectedLocationIds, Draft.RecipientChoices.Where(choice => choice.RouteId != routeId).ToArray());
            RefreshFromState();
            return true;
        }

        public bool ToggleLocation(string locationId)
        {
            if (Projection?.LocationOptions == null || !Projection.LocationOptions.Any(option =>
                option.LocationId == locationId && option.IsReachableInOriginalDraft)) return false;
            var selected = Draft.SelectedLocationIds.ToList();
            if (selected.Remove(locationId)) deselectedLocations.Add(locationId);
            else
            {
                if (Session.MaximumLocations == 1) selected.Clear();
                if (selected.Count >= Session.MaximumLocations) return false;
                selected.Add(locationId); deselectedLocations.Remove(locationId);
            }
            Draft = Draft.WithSelection(Draft.ConfirmedRouteIds, selected, Draft.RecipientChoices);
            RefreshFromState();
            return true;
        }

        public bool TryBuildAnswer(IReadOnlyList<string> candidateIds, bool cancel,
            out IReadOnlyList<string> selectedCandidateIds)
        {
            selectedCandidateIds = Array.Empty<string>();
            if (!IsActive || candidateIds == null) return false;
            var answer = new List<string>();
            if (cancel)
            {
                if (!Session.AllowCancelBeforeCommit) return false;
                answer.Add(RouteSelectionAnswerCodec.Encode(RequestBinding.ScopeKey, RouteSelectionCandidateKind.Cancel));
            }
            else
            {
                var shownDigest = Projection?.Preview?.PlanDigest;
                if (Draft.SelectedLocationIds.Count == 0)
                    Draft = Draft.WithSelection(Draft.ConfirmedRouteIds, Draft.SelectedLocationIds,
                        Draft.RecipientChoices, confirmEmptySelection: true);
                RefreshFromState();
                if (!CanConfirm || Projection.Preview == null || !Projection.Preview.FinalizationValidation.IsValid ||
                    (shownDigest != null && shownDigest != Projection.Preview.PlanDigest)) return false;
                // 原选择与剪枝结果分别保留，由 Host 对同一草稿重算，不能仅传见证路径。
                foreach (var routeId in Draft.ConfirmedRouteIds)
                {
                    var choice = Draft.RecipientChoices.FirstOrDefault(value => value.RouteId == routeId);
                    if (choice == null) return false;
                    answer.Add(RouteSelectionAnswerCodec.Encode(RequestBinding.ScopeKey,
                        RouteSelectionCandidateKind.RouteAndRecipient, routeId, choice.ReceiverSubjectKey));
                }
                foreach (var location in Draft.SelectedLocationIds)
                    answer.Add(RouteSelectionAnswerCodec.Encode(RequestBinding.ScopeKey, RouteSelectionCandidateKind.Location, location));
                answer.Add(RouteSelectionAnswerCodec.Encode(RequestBinding.ScopeKey, RouteSelectionCandidateKind.Commit));
            }
            var allowed = new HashSet<string>(candidateIds, StringComparer.Ordinal);
            if (answer.Count > 127 || answer.Any(id => !allowed.Contains(id))) return false;
            selectedCandidateIds = answer.AsReadOnly();
            return true;
        }

        public bool TryValidateConfirmedIntent(out string planDigest)
        {
            planDigest = null;
            if (!IsActive) return false;
            var current = state();
            if (current == null || !service.TryValidatePlan(current, Session, RequestBinding,
                Draft, selectableLocations, out var preview).IsValid) return false;
            planDigest = preview.PlanDigest;
            return true;
        }

        public IReadOnlyList<WorkflowHighlight> BuildHighlights()
        {
            var result = new List<WorkflowHighlight>();
            if (Projection?.RouteOptions == null || Projection.LocationOptions == null) return result;
            foreach (var option in Projection.RouteOptions)
            {
                var selected = Projection.Preview == null
                    ? option.IsOriginallyConfirmed || option.IsAutomaticallySelectedFreeRoute
                    : option.IsRetainedInPreview;
                var editable = option.FreeReason == RouteFreeReason.None && (option.IsFrontier || option.IsOriginallyConfirmed);
                if (!selected && !editable) continue;
                result.Add(new WorkflowHighlight(WorkflowHighlightTargetKind.Route, option.RouteId,
                    selected ? WorkflowHighlightSemantic.CollectionPaymentSelected : WorkflowHighlightSemantic.CollectionPaymentRequired,
                    selected ? WorkflowHighlightState.Selected : WorkflowHighlightState.Available, editable));
            }
            foreach (var option in Projection.LocationOptions)
            {
                if (!option.IsReachableInOriginalDraft) continue;
                result.Add(new WorkflowHighlight(WorkflowHighlightTargetKind.Location, option.LocationId,
                    option.IsSelected ? WorkflowHighlightSemantic.CollectionSelected :
                    Session.Purpose == RouteSelectionPurpose.Exploration ? WorkflowHighlightSemantic.ExploreTarget : WorkflowHighlightSemantic.CollectionCandidate,
                    option.IsSelected ? WorkflowHighlightState.Selected : WorkflowHighlightState.Available));
            }
            return result.AsReadOnly();
        }

        private RouteSelectionRouteOption FindRoute(string routeId) =>
            Projection?.RouteOptions?.FirstOrDefault(option => option.RouteId == routeId);
    }
}
