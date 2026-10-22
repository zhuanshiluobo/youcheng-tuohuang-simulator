using System.Collections.Generic;
using UnityEngine;
using YC.Presentation.Workflows;
using YC.Domain.Interactions;

namespace YC.Presentation
{
    /// <summary>主界面手牌与现有卡牌查看流程的连接点。</summary>
    public sealed class CharacterHandPanel : MonoBehaviour
    {
        [SerializeField] private CharacterHandPanelView view;
        private CardVisualCatalog cardVisualCatalog;
        private CharacterCardPanelViewModel currentViewModel;
        private ViewedCharacterPilesViewModel inspectionView;
        private string displayGameId;
        private int displayViewerId = -1, displayRevision = -1, displayGeneration;
        private bool inspectingOtherPlayer;
        private CardViewer cardImageViewer;
        private EffectDialogShell discardPage;
        private bool coveredPreview;
        [SerializeField] private string coveredListTitleFormat = "盖放角色 · {0} 张";
        [SerializeField] private string coveredListDescription = "查看当前盖放的角色牌";
        private int currentPlayerId;
        private bool initialized;
        private readonly List<CharacterCardHandItemViewModel> orderedHand = new List<CharacterCardHandItemViewModel>();
        public CharacterHandPanelView View => view;
        public IReadOnlyList<CharacterCardHandItemViewModel> OrderedHand => orderedHand;
        public bool IsDiscardPreviewOpen => initialized && discardPage != null && discardPage.IsVisible;

        private void Awake()
        {
            if (!Bind(view)) enabled = false;
        }

        private void OnDestroy()
        {
            initialized = false;
            discardPage?.Hide();
            if (cardImageViewer != null) cardImageViewer.Close();
        }

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;
            if (view == null || !view.IsBoundTo(this))
            {
                reason = "手牌面板缺少固定 View 引用。";
                return false;
            }
            return view.TryValidateConfiguration(out reason);
        }

        public bool Bind(CharacterHandPanelView configuredView)
        {
            if (initialized) return view == configuredView;
            view = configuredView;
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[CharacterHandPanel] " + reason, this);
                return false;
            }
            initialized = true;
            return true;
        }

        public bool Configure(CardVisualCatalog configuredCatalog)
        {
            if (!Bind(view)) return false;
            if (configuredCatalog == null || !configuredCatalog.TryValidateConfiguration(out _)) return false;
            cardVisualCatalog = configuredCatalog;
            return true;
        }

        public void Render(int playerId, CharacterCardPanelViewModel model,
            ViewedCharacterPilesViewModel physicalPiles = null)
        {
            if (!initialized || cardVisualCatalog == null) return;
            SetDisplayContext(playerId, physicalPiles, false);
            inspectionView = null;
            currentViewModel = model;
            orderedHand.Clear();
            if (model != null) orderedHand.AddRange(model.HandCards);
            var ids = new List<string>();
            foreach (var card in orderedHand) ids.Add(card.CardId);
            view.HandPile.RenderSprites(ids, id => cardVisualCatalog.GetCharacterFront(model.ResolveDefinitionId(id)),
                id => OpenCardViewer(ResolveCardName(id), id));
            var hud = GetComponentInParent<GameplayInteractionHudView>();
            if (hud != null && hud.MainModules != null)
                hud.MainModules.RenderCharacterPiles(model, cardVisualCatalog, physicalPiles);
            if (IsDiscardPreviewOpen) RebuildDiscardPreview();
        }

        public void RenderInspection(ViewedCharacterPilesViewModel model)
        {
            if (!initialized || cardVisualCatalog == null) return;
            SetDisplayContext(model?.PlayerId ?? -1, model, true);
            currentViewModel = null;
            inspectionView = model;
            orderedHand.Clear();
            view.HandPile.RenderTextures(model == null ? (IReadOnlyList<string>)System.Array.Empty<string>() : model.HandBackIds,
                id => cardVisualCatalog.GetCharacterBack(model.ResolveBackVisualKey(id)), null);
            var hud = GetComponentInParent<GameplayInteractionHudView>();
            if (hud != null && hud.MainModules != null)
                hud.MainModules.RenderInspectionPiles(model, cardVisualCatalog);
            if (IsDiscardPreviewOpen) RebuildDiscardPreview();
        }

        private void SetDisplayContext(int playerId, ViewedCharacterPilesViewModel model, bool inspecting)
        {
            var gameId = model?.GameId;
            var viewerId = model?.ViewerPlayerId ?? playerId;
            var revision = model?.StateRevision ?? -1;
            if (currentPlayerId != playerId || displayGameId != gameId || displayViewerId != viewerId ||
                displayRevision != revision || inspectingOtherPlayer != inspecting)
            {
                displayGeneration++;
                // 仅退出本面板拥有的资料页，不调用本人行动的取消/提交或旧资料返回回调。
                if (cardImageViewer != null) cardImageViewer.Dismiss();
                CloseDiscardPreview();
                coveredPreview = false;
            }
            currentPlayerId = playerId;
            displayGameId = gameId;
            displayViewerId = viewerId;
            displayRevision = revision;
            inspectingOtherPlayer = inspecting;
        }

        public void OpenDiscardPreview()
        {
            coveredPreview = false;
            if (initialized && (currentViewModel != null || inspectionView != null)) ShowDiscardList(false);
        }
        public bool OwnsDiscardPage(GameObject page) => discardPage != null && discardPage.OwnsPage(page);
        public void CloseDiscardPreview() => discardPage?.Hide();
        public void CloseCharacterCardViewer()
        {
            if (cardImageViewer != null) cardImageViewer.Close();
        }
        public bool TryHandleEscape()
        {
            if (!IsDiscardPreviewOpen) return false;
            if (CardViewer.WasEscapeConsumedThisFrame() || (cardImageViewer != null && cardImageViewer.IsShowing) ||
                ZoomableImageViewerController.HasOpenViewer() || ZoomableImageViewerController.WasEscapeConsumedThisFrame())
                return true;
            CloseDiscardPreview();
            return true;
        }
        public void OpenCoveredCharacterCardViewer()
        {
            if (inspectingOtherPlayer || currentViewModel == null || currentViewModel.CoveredCards.Count == 0) return;
            coveredPreview = true;
            ShowDiscardList(false);
        }

        private void RebuildDiscardPreview() => ShowDiscardList(true);

        private void ShowDiscardList(bool preserveScroll)
        {
            var hud = GetComponentInParent<GameplayInteractionHudView>();
            var registry = hud == null ? FindObjectOfType<GameplayDialogRegistry>() : hud.DialogRegistry;
            if (registry == null || (currentViewModel == null && inspectionView == null) ||
                (coveredPreview && inspectingOtherPlayer)) return;
            var copy = registry.EffectDialogLayoutProfile;
            if (discardPage == null) discardPage = new EffectDialogShell(registry);
            var scroll = preserveScroll ? discardPage.ScrollPosition : 1f;
            var ids = new List<string>();
            var previewCovered = coveredPreview;
            foreach (var card in DisplayedCards(previewCovered)) ids.Add(card.CardId);
            var owner = currentPlayerId;
            var generation = displayGeneration;
            var useCardPicker = ids.Exists(id => cardVisualCatalog.GetCharacterFront(ResolveDefinitionId(id)) != null);
            bool Current() => initialized && generation == displayGeneration && owner == currentPlayerId &&
                previewCovered == coveredPreview && !(previewCovered && inspectingOtherPlayer) &&
                ids.TrueForAll(id => ContainsCard(DisplayedCards(previewCovered), id));
            var panel = discardPage.PreparePage((RectTransform)transform, "Discard Card List", "Discard List Panel",
                copy.SelectionPanelSize, copy.CharacterPanelPosition, true, false, true,
                cardPicker: useCardPicker);
            EffectDialogShell.AddCountedHeading(panel, coveredPreview ? coveredListTitleFormat : copy.DiscardListTitleFormat, ids.Count,
                useCardPicker ? string.Empty : (coveredPreview ? coveredListDescription : copy.DiscardListDescription));
            discardPage.AddSelection(panel, new EffectDialogSelectionSpec
            {
                ReadOnly = true, IsEffectPage = false, CloseOnBackgroundClick = true,
                CardPickerHint = (coveredPreview ? coveredListDescription : copy.DiscardListDescription),
                Request = new InteractionRequestProjection
                {
                    VisibleToViewer = true, CandidateIds = ids, MinSelections = 0, MaxSelections = 0,
                    AllowDecline = true, InteractionId = "discard-view", Status = "open"
                },
                Label = ResolveCardName,
                CardSprite = id => cardVisualCatalog.GetCharacterFront(ResolveDefinitionId(id)),
                IsCurrent = Current, CanSelect = _ => false,
                OptionNamePrefix = "Discard Card ", Cancel = CloseDiscardPreview,
                CancelLabel = copy.ReadOnlyCloseLabel
            });
            discardPage.RestoreScroll(scroll);
        }

        private IReadOnlyList<CharacterCardHandItemViewModel> DisplayedCards(bool covered) =>
            inspectingOtherPlayer
                ? (covered || inspectionView == null ? (IReadOnlyList<CharacterCardHandItemViewModel>)System.Array.Empty<CharacterCardHandItemViewModel>() : inspectionView.DiscardCards)
                : (currentViewModel == null ? (IReadOnlyList<CharacterCardHandItemViewModel>)System.Array.Empty<CharacterCardHandItemViewModel>() :
                    covered ? currentViewModel.CoveredCards : currentViewModel.DiscardCards);

        private string ResolveDefinitionId(string cardId) => inspectingOtherPlayer
            ? inspectionView?.ResolveDefinitionId(cardId) : currentViewModel?.ResolveDefinitionId(cardId);

        private static bool ContainsCard(IReadOnlyList<CharacterCardHandItemViewModel> cards, string id)
        {
            for (var i = 0; cards != null && i < cards.Count; i++) if (cards[i].CardId == id) return true;
            return false;
        }

        private string ResolveCardName(string cardId)
        {
            if (inspectingOtherPlayer)
            {
                foreach (var card in DisplayedCards(false)) if (card.CardId == cardId) return card.DisplayName;
                return string.Empty;
            }
            if (currentViewModel == null) return string.Empty;
            foreach (var card in currentViewModel.CoveredCards) if (card.CardId == cardId) return card.DisplayName;
            foreach (var card in currentViewModel.HandCards) if (card.CardId == cardId) return card.DisplayName;
            foreach (var card in currentViewModel.DiscardCards) if (card.CardId == cardId) return card.DisplayName;
            return CharacterCardPanelPresenter.ResolveCardDisplayName(cardId);
        }

        private void OpenCardViewer(string title, string cardId)
        {
            if (inspectingOtherPlayer || cardVisualCatalog == null || currentViewModel == null ||
                !(ContainsCard(currentViewModel.CoveredCards, cardId) || ContainsCard(currentViewModel.HandCards, cardId)))
            {
                return;
            }

            var sprite = cardVisualCatalog.GetCharacterFront(currentViewModel.ResolveDefinitionId(cardId));
            if (sprite == null)
            {
                return;
            }

            if (cardImageViewer == null)
            {
                cardImageViewer = CardViewer.InstantiateFor(transform);
            }

            if (cardImageViewer == null)
            {
                return;
            }

            var owner = currentPlayerId;
            var generation = displayGeneration;
            cardImageViewer.OpenInspect(sprite, valid: () => initialized && !inspectingOtherPlayer &&
                generation == displayGeneration && currentPlayerId == owner &&
                currentViewModel != null && (ContainsCard(currentViewModel.CoveredCards, cardId) ||
                    ContainsCard(currentViewModel.HandCards, cardId)));
        }

    }
}
