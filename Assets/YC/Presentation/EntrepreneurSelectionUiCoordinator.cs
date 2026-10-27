using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>模式准备/选面只消费正式请求；规则时序与实际物品由原Host处理。</summary>
    internal sealed class EntrepreneurSelectionUiCoordinator : InteractionBase, IInteractionRequestRenderer
    {
        private readonly GameplayExpansionUi adapter;
        private readonly GameplayDialogRegistry registry;
        private readonly Func<int> player;
        private readonly Func<bool> canSubmit;
        private readonly Action<GameCommand> submit;
        private readonly RectTransform parent;
        private readonly EffectDialogShell selection;
        private readonly CardCollectionBrowser browser;
        private EnterpriseSelectionDialogView enterprisePreview;
        private InteractionRequestProjection shown;
        private string identity, selected = "", inFlight = "";
        private bool browsing;
        private float position = 1f;
        public EntrepreneurSelectionUiCoordinator(GameplayExpansionUi adapter, GameplayDialogRegistry registry,
            RectTransform parent, Func<int> player, Action<GameCommand> submit, Func<bool> canSubmit)
        {
            this.adapter = adapter; this.registry = registry; this.parent = parent; this.player = player;
            this.submit = submit; this.canSubmit = canSubmit;
            selection = new EffectDialogShell(registry); browser = new CardCollectionBrowser(registry, parent);
        }
        public override string Id => "mode.card.selection.ui";
        public override InteractionPriority Priority => InteractionPriority.PendingResolution;
        int IInteractionRequestRenderer.Priority => 350;
        private static bool Handles(string type) => type == EntrepreneurFlow.PreviewInteraction ||
            type == EntrepreneurFlow.ChooseInteraction || type == EntrepreneurFlow.FaceInteraction;
        private string Key(InteractionRequestProjection request, GameStateView view) => view.GameId + ":" + view.ViewerPlayerId + ":" + request.InteractionId + ":" + request.OwnerEffectId + ":" +
            request.StateRevision + ":" + InteractionCommandIntent.ContractKey(request) + ":" +
            string.Join(",", view.Entrepreneurs?.OwnEnterpriseCandidateItemIds ?? new List<string>()) + ":" +
            string.Join(",", (view.Entrepreneurs?.OwnEnterpriseCandidates ?? new List<EnterpriseCandidateCardView>())
                .Select(c => c == null ? "" : c.ItemId + "=" + c.DefinitionId));
        public bool CanRender(InteractionRequestProjection request)
        {
            var view = adapter.VisibleView();
            return request != null && request.VisibleToViewer && request.Status == "open" && Handles(request.InteractionTypeId) &&
                request.AnsweringPlayerId == player() && view?.ViewerRole == GameStateViewerRole.Player && view.ViewerPlayerId == player() && request.StateRevision == view.StateRevision &&
                request.AnswerSchema == "candidate_ids" && request.MinSelections == 1 && request.MaxSelections == 1;
        }
        private bool Current()
        {
            var view = adapter.VisibleView();
            if (shown == null || view == null || !canSubmit() || inFlight.Length > 0) return false;
            var actual = VisibleInteractionRequestSource.Read(null, view, player()).FirstOrDefault(r => r.InteractionId == shown.InteractionId);
            var projection = actual == null ? null : InteractionRequestProjector.ProjectForPlayer(actual, player());
            return CanRender(projection) && identity == Key(projection, view);
        }
        public override bool IsActive => Current();
        public void Render(InteractionRequestProjection request)
        {
            if (!CanRender(request)) { Clear(); return; }
            var key = Key(request, adapter.VisibleView());
            if (identity == key && (browsing || selection.IsShowing || browser.IsShowing || enterprisePreview?.IsShowing == true)) return;
            if (identity != key) { Clear(); identity = key; selected = ""; position = 1f; }
            shown = request; Draw();
        }
        private IReadOnlyList<EntrepreneurCardPresentation> Candidates()
        {
            var view = adapter.VisibleView();
            if (shown.InteractionTypeId == EntrepreneurFlow.ChooseInteraction)
                return shown.CandidateIds.Select(EntrepreneurCardPresentation.Ability).Where(c => c != null).ToArray();
            var cards = view.Entrepreneurs.Players.Where(p => p.PlayerId == player()).SelectMany(p => p.RoundSlots)
                .SelectMany(s => s.Candidates).Where(c => shown.CandidateIds.Contains(c.CandidateId)).Select(EntrepreneurCardPresentation.Candidate).ToArray();
            // Host的正式顺序保持；不截断DIY候选，也不猜A/B。
            return shown.CandidateIds.Select(id => cards.FirstOrDefault(c => c.Id == id)).Where(c => c != null).ToArray();
        }
        private static IReadOnlyList<EntrepreneurCardPresentation> OwnEnterprises(GameStateView view)
        {
            var ids = view?.Entrepreneurs?.OwnEnterpriseCandidateItemIds;
            var candidates = view?.Entrepreneurs?.OwnEnterpriseCandidates;
            if (view?.ViewerRole != GameStateViewerRole.Player || ids == null || candidates == null ||
                ids.Count == 0 || ids.Count != candidates.Count || ids.Any(string.IsNullOrWhiteSpace) ||
                ids.Distinct(StringComparer.Ordinal).Count() != ids.Count ||
                candidates.Any(c => c == null || string.IsNullOrWhiteSpace(c.ItemId) || string.IsNullOrWhiteSpace(c.DefinitionId)) ||
                !ids.SequenceEqual(candidates.Select(c => c.ItemId), StringComparer.Ordinal))
                return Array.Empty<EntrepreneurCardPresentation>();
            var result = new List<EntrepreneurCardPresentation>();
            var definitions = ExternalContentRuntime.Pack.Definitions;
            foreach (var candidate in candidates)
            {
                // 只消费本人显示投影与公开内容定义，不读取隐藏物品或从实例ID猜定义。
                var definition = definitions.FirstOrDefault(d => d.Enabled &&
                    d.ContentType == "enterprise" && d.DefinitionId == candidate.DefinitionId);
                if (definition == null || string.IsNullOrWhiteSpace(definition.DisplayName) || string.IsNullOrWhiteSpace(definition.Artwork))
                    return Array.Empty<EntrepreneurCardPresentation>();
                result.Add(new EntrepreneurCardPresentation { Id = candidate.ItemId, DefinitionId = candidate.DefinitionId,
                    FaceId = "", Label = definition.DisplayName });
            }
            return result;
        }
        private void Draw()
        {
            using var inputRead = registry.BeginInputRead?.Invoke();
            if (!Current()) return;
            var copy = adapter.EntrepreneurCopy;
            var key = identity;
            bool Valid() => identity == key && Current();
            if (shown.InteractionTypeId == EntrepreneurFlow.PreviewInteraction)
            {
                ShowOwnEnterprises(false);
                return;
            }
            var cards = Candidates();
            var layout = registry.EffectDialogLayoutProfile;
            var panel = selection.PreparePage(parent, "Mode Card Selection", "Mode Card Selection", layout.SelectionPanelSize,
                layout.CharacterPanelPosition, true, false, false, true);
            EffectDialogShell.AddHeading(panel, shown.InteractionTypeId == EntrepreneurFlow.ChooseInteraction ? copy.ChooseTitle : copy.FaceTitle, copy.SelectionHint);
            var selectionSpec = new EffectDialogSelectionSpec {
                Request = shown, IsEffectPage = false, AllowStageFolding = true, SelectedIds = selected.Length == 0 ? Array.Empty<string>() : new[] { selected },
                Label = id => cards.FirstOrDefault(c => c.Id == id)?.Label ?? id,
                CardSprite = id => cards.FirstOrDefault(c => c.Id == id)?.Artwork,
                CanSelect = id => cards.Any(c => c.Id == id), IsCurrent = Valid,
                Confirm = () => { if (Valid()) Answer(selected); },
                Inspect = shown.InteractionTypeId == EntrepreneurFlow.ChooseInteraction ? (Action<string>)(id => {
                    if (!Valid()) return; Browse(copy.DeckTitle, EntrepreneurCardPresentation.Deck(id));
                }) : null
            };
            selectionSpec.Select = id => {
                if (!Valid()) return;
                selected = id; position = selection.ScrollPosition;
                selectionSpec.SelectedIds = new[] { selected };
                selectionSpec.RefreshSelection?.Invoke();
            };
            selection.AddSelection(panel, selectionSpec);
            selection.RestoreScroll(position);
            if (shown.InteractionTypeId == EntrepreneurFlow.ChooseInteraction)
            {
                var inspect = EffectDialogShell.CreateButton(panel, "Inspect Assigned Enterprises", copy.OwnEnterpriseLabel, 18, false);
                inspect.onClick.AddListener(() => {
                    using var inspectRead = registry.BeginInputRead?.Invoke();
                    if (Valid()) ShowOwnEnterprises(true);
                });
            }
        }
        private void ShowOwnEnterprises(bool returnToSelection)
        {
            using var inputRead = registry.BeginInputRead?.Invoke();
            if (!Current()) return;
            var view = adapter.VisibleView(); var copy = adapter.EntrepreneurCopy;
            var cards = OwnEnterprises(view);
            var expected = view.Entrepreneurs.OwnEnterpriseCandidateItemIds.Count;
            bool complete = expected > 0 && cards.Count == expected;
            var key = identity;
            bool Valid() => identity == key && Current();
            var boards = cards.Select(c => adapter.ProjectCandidateBoard(view, c.Id, c.DefinitionId, shown)).ToArray();
            var projection = new EnterpriseSelectionProjection { GameId = view.GameId, ViewerPlayerId = view.ViewerPlayerId,
                RequestId = shown.InteractionId + ":assigned-enterprise-preview", OwnerEffectId = shown.OwnerEffectId, Revision = view.StateRevision,
                CandidateSetId = shown.CandidateSetId, CandidateSetVersion = shown.CandidateSetVersion,
                RequestContract = InteractionCommandIntent.ContractKey(shown), Mode = EnterpriseSelectionMode.Company, ReadOnly = true,
                MinSelections = 0, MaxSelections = 0, CanCancel = returnToSelection, SubmissionAvailable = false,
                Context = complete ? copy.BrowseHint : string.Format(copy.MissingEnterpriseCandidateFormat, expected - cards.Count),
                Boards = boards, Targets = boards.Select(b => new EnterpriseSelectionTarget { Id = b.Id, BoardId = b.Id, CompanyId = b.Id,
                    Label = b.Label, Description = b.Summary, Details = b.Details, Available = false }).ToArray() };
            if (enterprisePreview == null) enterprisePreview = registry.InstantiateEnterpriseSelection(parent, false);
            enterprisePreview.ClearPresentation();
            Action leave = () => {
                if (!Valid()) return;
                enterprisePreview.ClearPresentation(); browsing = false;
                if (returnToSelection) Draw(); else if (complete) Answer("continue");
            };
            position = selection.ScrollPosition; browsing = returnToSelection; selection.Hide();
            enterprisePreview.BindGameplayText(adapter.ResolveEnterpriseDetails, adapter.RenderEnterpriseDetails);
            enterprisePreview.BindSubmission(() => Valid() ? projection : null, null, returnToSelection ? leave : null);
            enterprisePreview.BindReadOnlyContinuation(() => Valid() && complete, leave,
                returnToSelection ? registry.EffectDialogLayoutProfile.ReadOnlyCloseLabel : copy.ContinueLabel, copy.PreviewTitle);
            enterprisePreview.Present(projection);
        }
        private void Browse(string title, IReadOnlyList<EntrepreneurCardPresentation> cards)
        {
            var key = identity; position = selection.ScrollPosition; browsing = true;
            browser.Show(title, adapter.EntrepreneurCopy.BrowseHint, cards, () => identity == key && Current(),
                () => { browsing = false; if (identity == key && Current()) Draw(); });
        }
        private void Answer(string id)
        {
            if (!Current() || !shown.CandidateIds.Contains(id)) return;
            var command = EffectInteractionCommands.Answer(shown, player(), new[] { id }, false);
            inFlight = command.CommandId; selection.Hide(); browser.Hide(); enterprisePreview?.ClearPresentation();
            try { submit(command); } catch { inFlight = ""; Draw(); throw; }
        }
        public override void NotifyCommandSettled(string commandId)
        { if (inFlight != commandId) return; inFlight = ""; Clear(); }
        public void Clear() { selection.Hide(); browser.Hide(); enterprisePreview?.ClearPresentation(); shown = null; identity = null; selected = ""; browsing = false; }
        public override void Cancel() => Clear();
        public override InteractionResult OnEscape()
        { if (!IsActive) return InteractionResult.Passthrough; if (browsing) { browser.Hide(); enterprisePreview?.ClearPresentation(); browsing = false; Draw(); } return InteractionResult.Consumed; }
        public override InteractionResult OnLocationClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnInfluenceSlotClicked(string id) => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnMobileCityClicked() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionPresentation BuildPresentation() => IsActive ? InteractionPresentation.Busy : InteractionPresentation.Empty;
    }
}
