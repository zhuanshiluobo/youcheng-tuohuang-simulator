using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Harvest;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class ResourceCollectionPresenter : IInteractionWorkflow, IInteraction
    {
        private readonly IGameplayContext context;
        private readonly CommandGateway commandGateway;
        private readonly IResourceCollectionView view;
        private readonly IMapQueryService mapQuery;
        private readonly ResourceCollectionService resourceCollectionService;
        private readonly ResourceCollectionSelectionController selection;
        private readonly Dictionary<string, ResourceCollectionRouteOption> paymentOptionsByRouteId =
            new Dictionary<string, ResourceCollectionRouteOption>(StringComparer.Ordinal);
        private ResourceCollectionSelectionQuery selectionQuery;

        public ResourceCollectionPresenter(
            IGameplayContext context,
            IGameCommandPort commandPort,
            IResourceCollectionView view,
            IMapQueryService mapQuery,
            ResourceCollectionService resourceCollectionService)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            commandGateway = new CommandGateway(
                commandPort ?? throw new ArgumentNullException(nameof(commandPort)));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.resourceCollectionService = resourceCollectionService ??
                                             throw new ArgumentNullException(nameof(resourceCollectionService));
            selection = new ResourceCollectionSelectionController();
        }

        public InteractionMode Mode
        {
            get { return InteractionMode.Busy; }
        }

        public string Id
        {
            get { return "active.resource-collection"; }
        }

        public InteractionPriority Priority
        {
            get { return InteractionPriority.ActiveAction; }
        }

        public bool IsActive
        {
            get
            {
                var state = context.CurrentState;
                var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
                return state != null &&
                       state.Phase == GamePhase.ResourceCollection &&
                       player != null &&
                       !player.HasCollectedResourcesThisRound;
            }
        }

        public ResourceCollectionSelectionQuery CurrentQuery
        {
            get { return selectionQuery; }
        }

        public IReadOnlyCollection<string> SelectedLocationIds
        {
            get { return selection.SelectedLocationIds; }
        }

        public IReadOnlyCollection<string> PaidRouteIds
        {
            get { return selection.PaidRouteIds; }
        }

        public void Activate()
        {
            Begin();
        }

        public void Begin()
        {
            selection.Clear();
            paymentOptionsByRouteId.Clear();
            selectionQuery = null;

            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null ||
                player.HasCollectedResourcesThisRound ||
                string.IsNullOrEmpty(player.CityLocationId))
            {
                view.ClearHighlights();
                view.RefreshSelectionView();
                view.ShowPrompt(BuildStatus());
                return;
            }

            RefreshPresentation();
            view.ShowPrompt(BuildStatus());
        }

        public void Cancel()
        {
            selection.Clear();
            paymentOptionsByRouteId.Clear();
            selectionQuery = null;
            view.ClearHighlights();
            view.RefreshSelectionView();
        }

        public InteractionResult OnLocationClicked(string locationId)
        {
            return InteractionResult.Passthrough;
        }

        public InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            return InteractionResult.Passthrough;
        }

        public InteractionResult OnMobileCityClicked()
        {
            return InteractionResult.Passthrough;
        }

        public InteractionResult OnEscape()
        {
            return InteractionResult.Passthrough;
        }

        public InteractionPresentation BuildPresentation()
        {
            return IsActive
                ? new InteractionPresentation(
                    null,
                    BuildStatus(),
                    InteractionMode.Busy,
                    false)
                : InteractionPresentation.Empty;
        }

        public void NotifyCommandSettled(string commandId)
        {
        }

        public void SelectLocation(string locationId)
        {
            var result = selection.ToggleLocation(locationId, IsLocationAvailable);
            RefreshPresentation();

            switch (result)
            {
                case CollectionToggleResult.NotCandidate:
                    view.ShowPrompt("\u8be5\u8d44\u6e90\u70b9\u672c\u6b21\u4e0d\u80fd\u91c7\u96c6\u3002");
                    break;
                case CollectionToggleResult.Unavailable:
                    view.ShowPrompt("\u8bf7\u5148\u652f\u4ed8\u901a\u5f80\u8be5\u8d44\u6e90\u70b9\u6240\u9700\u7684\u822a\u9053\u8def\u8d39\u3002");
                    break;
                case CollectionToggleResult.Removed:
                    view.ShowPrompt("\u5df2\u4ece\u672c\u6b21\u91c7\u96c6\u4e2d\u79fb\u9664\u8d44\u6e90\u70b9 " + locationId + "\u3002");
                    break;
                case CollectionToggleResult.Added:
                    view.ShowPrompt("\u5df2\u52a0\u5165\u672c\u6b21\u91c7\u96c6\u8d44\u6e90\u70b9 " + locationId + "\u3002");
                    break;
            }
        }

        public void SelectRoutePayment(string routeId)
        {
            ResourceCollectionRouteOption option;
            if (selectionQuery == null ||
                string.IsNullOrEmpty(routeId) ||
                !paymentOptionsByRouteId.TryGetValue(routeId, out option))
            {
                view.ShowPrompt("\u8be5\u822a\u9053\u672c\u6b21\u91c7\u96c6\u65e0\u9700\u652f\u4ed8\u8def\u8d39\u3002");
                return;
            }

            if (!option.CanAfford)
            {
                view.ShowPrompt("\u9636\u6bb5\u5f00\u59cb\u65f6\u7684\u91d1\u5238\u4e0d\u8db3\uff0c\u65e0\u6cd5\u518d\u652f\u4ed8\u8be5\u822a\u9053\u8def\u8d39\u3002");
                return;
            }

            view.ShowRoutePaymentOptions(routeId, option.Cost, option.OpponentOwnerPlayerIds);
        }

        public void ConfirmRoutePayment(string routeId, int receiverPlayerId)
        {
            ResourceCollectionRouteOption option;
            if (selectionQuery == null ||
                !paymentOptionsByRouteId.TryGetValue(routeId, out option) ||
                (receiverPlayerId > 0 && !ContainsPlayer(option.OpponentOwnerPlayerIds, receiverPlayerId)))
            {
                view.ShowPrompt("\u8def\u8d39\u63a5\u6536\u65b9\u4e0d\u53ef\u7528\u3002");
                return;
            }

            selection.ConfirmRoutePayment(routeId, receiverPlayerId);
            RefreshPresentation();
            view.ShowPrompt(receiverPlayerId > 0
                ? "\u822a\u9053 " + routeId + " \u7684\u8def\u8d39\u5c06\u652f\u4ed8\u7ed9" +
                  view.GetPlayerDisplayName(receiverPlayerId) + "\u3002"
                : "\u822a\u9053 " + routeId + " \u7684\u8def\u8d39\u5c06\u652f\u4ed8\u7ed9\u94f6\u884c\u3002");
        }

        public void CancelRoutePayment()
        {
            view.ShowPrompt(BuildStatus());
        }

        public void Submit()
        {
            if (!RefreshQuery())
            {
                return;
            }

            selection.RefreshSelection(IsLocationAvailable);
            var locationIds = selection.BuildSelectedLocationIds(mapQuery.Map.Locations);
            var selectedRouteIds = selection.BuildSelectedRouteIds(locationIds);
            var paymentRecipients = selection.EncodePaymentRecipients(selectedRouteIds);
            var command = new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = context.LocalPlayerId
            };
            command.Parameters[CollectResourceCommandHandler.LocationIdsParameter] = EncodeIds(locationIds);
            command.Parameters[CollectResourceCommandHandler.RouteIdsParameter] =
                EncodeIds(new List<string>(selectedRouteIds));
            if (!string.IsNullOrEmpty(paymentRecipients))
            {
                command.Parameters[CollectResourceCommandHandler.PaymentRecipientsParameter] = paymentRecipients;
            }

            commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("\u91c7\u96c6\u547d\u4ee4"))
                {
                    MissingResultPrompt =
                        CommandGateway.BuildMissingResultPrompt("\u91c7\u96c6\u547d\u4ee4"),
                    OnAppliedLocally = result =>
                    {
                        view.RefreshFromState();
                        view.ShowPrompt(BuildResultPrompt(result));
                    }
                });
        }

        public string BuildStatus()
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null)
            {
                return "\u91c7\u96c6\u9636\u6bb5\uff1a\u5f53\u524d\u73a9\u5bb6\u4e0d\u5b58\u5728";
            }

            if (player.HasCollectedResourcesThisRound)
            {
                return "\u91c7\u96c6\u9636\u6bb5\uff1a\u5df2\u63d0\u4ea4\u91c7\u96c6\uff0c\u7b49\u5f85\u5176\u4ed6\u73a9\u5bb6";
            }

            return selection.BuildStatus(IsRoutePayable);
        }

        private void RefreshPresentation()
        {
            if (!RefreshQuery())
            {
                view.ClearHighlights();
                view.RefreshSelectionView();
                return;
            }

            selection.RefreshSelection(IsLocationAvailable);
            var highlights = new List<WorkflowHighlight>();
            foreach (var pair in paymentOptionsByRouteId)
            {
                var routeId = pair.Key;
                var option = pair.Value;

                if (!ContainsString(selection.PaidRouteIds, routeId) ||
                    option.OpponentOwnerPlayerIds.Count > 1)
                {
                    highlights.Add(new WorkflowHighlight(
                        WorkflowHighlightTargetKind.Route,
                        routeId,
                        WorkflowHighlightSemantic.CollectionPaymentRequired));
                }
            }

            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                if (!selection.HasCandidateLocation(locationId) || !IsLocationAvailable(locationId))
                {
                    continue;
                }

                highlights.Add(new WorkflowHighlight(
                    WorkflowHighlightTargetKind.Location,
                    locationId,
                    ContainsString(selection.SelectedLocationIds, locationId)
                        ? WorkflowHighlightSemantic.CollectionSelected
                        : WorkflowHighlightSemantic.CollectionCandidate));
            }

            view.SetHighlights(highlights);
            view.RefreshSelectionView();
        }

        private bool RefreshQuery()
        {
            var state = context.CurrentState;
            if (state == null)
            {
                selectionQuery = null;
                view.ShowPrompt("\u5f53\u524d\u6ca1\u6709\u53ef\u7528\u7684\u6e38\u620f\u72b6\u6001\u3002");
                return false;
            }

            selectionQuery = resourceCollectionService.QuerySelection(
                state,
                context.LocalPlayerId,
                selection.PaidRouteIds);
            RemoveUnconfirmedPaymentOptions();
            foreach (var pair in selectionQuery.RouteOptionsById)
            {
                paymentOptionsByRouteId[pair.Key] = pair.Value;
            }
            selection.ApplyQuery(selectionQuery);
            if (selectionQuery.IsValid)
            {
                return true;
            }

            view.ShowPrompt(selectionQuery.Validation.Reason);
            return false;
        }

        private void RemoveUnconfirmedPaymentOptions()
        {
            var routeIdsToRemove = new List<string>();
            foreach (var pair in paymentOptionsByRouteId)
            {
                if (!ContainsString(selection.PaidRouteIds, pair.Key))
                {
                    routeIdsToRemove.Add(pair.Key);
                }
            }

            for (var i = 0; i < routeIdsToRemove.Count; i++)
            {
                paymentOptionsByRouteId.Remove(routeIdsToRemove[i]);
            }
        }

        private bool IsLocationAvailable(string locationId)
        {
            MapPath ignored;
            return selection.TryGetPath(locationId, out ignored);
        }

        private bool IsRoutePayable(string routeId)
        {
            ResourceCollectionRouteOption ignored;
            return selectionQuery != null && selectionQuery.TryGetRouteOption(routeId, out ignored);
        }

        private static string EncodeIds(IReadOnlyList<string> ids)
        {
            return ids == null || ids.Count == 0 ? string.Empty : string.Join(",", new List<string>(ids).ToArray());
        }

        private static string BuildResultPrompt(CommandResult result)
        {
            if (result == null || result.Events == null || result.Events.Count == 0)
            {
                return "\u91c7\u96c6\u5df2\u7ed3\u7b97\u3002";
            }

            var data = result.Events[0].Data;
            var parts = new List<string>();
            AddRewardPart(parts, data, "rewardGoldVoucher", "\u91d1\u5238");
            AddRewardPart(parts, data, "rewardOriginium", "\u6e90\u5ca9");
            AddRewardPart(parts, data, "rewardOriginiumShard", "\u6e90\u77f3\u788e\u7247");
            AddRewardPart(parts, data, "rewardIron", "\u5f02\u94c1");
            AddRewardPart(parts, data, "rewardPureOriginium", "\u81f3\u7eaf\u6e90\u77f3");

            var summary = parts.Count == 0 ? "\u65e0\u8d44\u6e90" : string.Join("\u3001", parts.ToArray());
            string locations;
            data.TryGetValue("locationIds", out locations);
            return string.IsNullOrEmpty(locations)
                ? "\u672c\u6b21\u91c7\u96c6\u83b7\u5f97\uff1a" + summary + "\u3002"
                : "\u672c\u6b21\u91c7\u96c6 " + locations + "\uff0c\u83b7\u5f97\uff1a" + summary + "\u3002";
        }

        private static void AddRewardPart(
            ICollection<string> parts,
            IReadOnlyDictionary<string, string> data,
            string key,
            string label)
        {
            string value;
            int amount;
            if (data != null && data.TryGetValue(key, out value) && int.TryParse(value, out amount) && amount > 0)
            {
                parts.Add(label + " +" + amount);
            }
        }

        private static bool ContainsPlayer(IReadOnlyList<int> values, int target)
        {
            if (values == null)
            {
                return false;
            }

            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] == target)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsString(IEnumerable<string> values, string target)
        {
            if (values == null)
            {
                return false;
            }

            foreach (var value in values)
            {
                if (value == target)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
