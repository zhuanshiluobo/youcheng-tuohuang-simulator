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
        private CardViewer cardImageViewer;
        private EffectDialogShell discardPage;
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

        public void Render(int playerId, CharacterCardPanelViewModel model)
        {
            if (!initialized || cardVisualCatalog == null) return;
            currentPlayerId = playerId;
            currentViewModel = model;
            orderedHand.Clear();
            if (model != null) orderedHand.AddRange(model.HandCards);
            var ids = new List<string>();
            foreach (var card in orderedHand) ids.Add(card.CardId);
            view.HandPile.Render(ids, id => cardVisualCatalog.GetCharacterFront(id),
                id => OpenCardViewer(CharacterCardPanelPresenter.ResolveCardDisplayName(id), id));
            var hud = GetComponentInParent<GameplayInteractionHudView>();
            if (hud != null && hud.MainModules != null)
                hud.MainModules.RenderCharacterPiles(model, cardVisualCatalog);
            if (IsDiscardPreviewOpen) RebuildDiscardPreview();
        }

        public void OpenDiscardPreview()
        {
            if (initialized && currentViewModel != null) ShowDiscardList(false);
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
            if (currentViewModel == null || string.IsNullOrEmpty(currentViewModel.CoveredCardId)) return;
            OpenCardViewer(CharacterCardPanelPresenter.ResolveCardDisplayName(currentViewModel.CoveredCardId),
                currentViewModel.CoveredCardId);
        }

        private void RebuildDiscardPreview() => ShowDiscardList(true);

        private void ShowDiscardList(bool preserveScroll)
        {
            var hud = GetComponentInParent<GameplayInteractionHudView>();
            var registry = hud == null ? FindObjectOfType<GameplayDialogRegistry>() : hud.DialogRegistry;
            if (registry == null || currentViewModel == null) return;
            var copy = registry.EffectDialogLayoutProfile;
            if (discardPage == null) discardPage = new EffectDialogShell(registry);
            var scroll = preserveScroll ? discardPage.ScrollPosition : 1f;
            var ids = new List<string>();
            foreach (var card in currentViewModel.DiscardCards) ids.Add(card.CardId);
            var owner = currentPlayerId;
            bool Current() => initialized && owner == currentPlayerId && currentViewModel != null &&
                ids.TrueForAll(id => ContainsCard(currentViewModel.DiscardCards, id));
            var panel = discardPage.Rebuild((RectTransform)transform, "Discard Card List", "Discard List Panel",
                copy.SelectionPanelSize, copy.CharacterPanelPosition, true, false, true,
                cardPicker: ids.Exists(id => cardVisualCatalog.GetCharacterFront(id) != null));
            EffectDialogShell.AddHeading(panel, string.Format(copy.DiscardListTitleFormat, ids.Count),
                copy.DiscardListDescription);
            discardPage.AddSelection(panel, new EffectDialogSelectionSpec
            {
                ReadOnly = true, IsEffectPage = false,
                Request = new InteractionRequestProjection
                {
                    VisibleToViewer = true, CandidateIds = ids, MinSelections = 0, MaxSelections = 0,
                    AllowDecline = true, InteractionId = "discard-view", Status = "open"
                },
                Label = CharacterCardPanelPresenter.ResolveCardDisplayName,
                CardTexture = id => cardVisualCatalog.GetCharacterFront(id),
                IsCurrent = Current, CanSelect = _ => false,
                OptionNamePrefix = "Discard Card ", Cancel = CloseDiscardPreview,
                CancelLabel = copy.ReadOnlyCloseLabel
            });
            discardPage.RestoreScroll(scroll);
        }

        private static bool ContainsCard(IReadOnlyList<CharacterCardHandItemViewModel> cards, string id)
        {
            for (var i = 0; cards != null && i < cards.Count; i++) if (cards[i].CardId == id) return true;
            return false;
        }

        private void OpenCardViewer(string title, string cardId)
        {
            if (cardVisualCatalog == null || currentViewModel == null ||
                !(cardId == currentViewModel.CoveredCardId || ContainsCard(currentViewModel.HandCards, cardId)))
            {
                return;
            }

            var texture = cardVisualCatalog.GetCharacterFront(cardId);
            if (texture == null)
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
            cardImageViewer.OpenInspect(texture, valid: () => initialized && currentPlayerId == owner &&
                currentViewModel != null && (cardId == currentViewModel.CoveredCardId ||
                    ContainsCard(currentViewModel.HandCards, cardId)));
        }

    }
}
