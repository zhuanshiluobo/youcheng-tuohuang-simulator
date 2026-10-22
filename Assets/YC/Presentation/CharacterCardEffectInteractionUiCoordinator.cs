using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>提交角色激活意图；旧 Pending 展示仅用于尚未迁移的兼容入口。</summary>
    internal sealed class CharacterCardEffectInteractionUiCoordinator
    {
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly CharacterCardPanelPresenter presenter;
        private readonly CharacterCardEffectChoiceDialog dialog;
        private readonly Func<bool> beginPendingInfluenceMove;
        private readonly Action endFacilityEffectSelection;
        private readonly Action<string, IReadOnlyDictionary<string, string>> submitEffect;
        private readonly Action<IReadOnlyDictionary<string, string>> submitPending;
        private readonly Action<string> setPrompt;

        public Func<GameStateView> GetVisibleState { private get; set; }
        public Func<InteractionRequestProjection, string, bool> SubmitSelection { private get; set; }
        private string pendingPresentationKey = string.Empty;
        public Action<string, int, int, string, GameplayTextDocument, Func<bool>> RenderLegacyMessageDocument { private get; set; }
        private string legacyMessageGameId, legacyMessagePendingId;
        private int legacyMessageViewer = -1, legacyMessageRevision = -1;

        public CharacterCardEffectInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            CharacterCardPanelPresenter presenter,
            CharacterCardEffectChoiceDialog dialog,
            Func<bool> beginPendingInfluenceMove,
            Action endFacilityEffectSelection,
            Action<string, IReadOnlyDictionary<string, string>> submitEffect,
            Action<IReadOnlyDictionary<string, string>> submitPending,
            Action<string> setPrompt)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            this.dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            this.beginPendingInfluenceMove = beginPendingInfluenceMove ?? throw new ArgumentNullException(nameof(beginPendingInfluenceMove));
            this.endFacilityEffectSelection = endFacilityEffectSelection ?? throw new ArgumentNullException(nameof(endFacilityEffectSelection));
            this.submitEffect = submitEffect ?? throw new ArgumentNullException(nameof(submitEffect));
            this.submitPending = submitPending ?? throw new ArgumentNullException(nameof(submitPending));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
        }

        public bool TryBeginEffect(string effectMode, CharacterCardEffectKind effect)
        {
            var state = getState();
            if (YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state))
            {
                var view = presenter.BuildProjectedView(state, getLocalPlayerId(), GetVisibleState?.Invoke());
                var candidate = CharacterCardPanelPresenter.ModeSelectionCandidate(view.CurrentUseSelection, effectMode);
                if (SubmitSelection == null || view.CurrentUseSelection?.CandidateIds.Contains(candidate) != true) return false;
                HideDialog();
                return SubmitSelection(view.CurrentUseSelection, candidate);
            }
            HideDialog();
            if (effect == CharacterCardEffectKind.Unsupported ||
                !Enum.IsDefined(typeof(CharacterCardEffectKind), effect)) return false;
            // 所有卡面都先挂激活节点；参数只由 Effect 的唯一交互请求征集。
            SubmitEffect(effectMode, new Dictionary<string, string>());
            return true;
        }

        public bool SynchronizePending()
        {
            var pending = CurrentTinManPending();
            if (pending == null)
            {
                if (!string.IsNullOrEmpty(pendingPresentationKey))
                {
                    HideDialog();
                }
                return false;
            }

            var state = getState();
            var visible = GetVisibleState?.Invoke();
            var visibleScope = visible == null ? "local" : visible.GameId + "|" + visible.ViewerRole + "|" +
                visible.ViewerPlayerId + "|" + visible.ContentPackHash + "|" + visible.StateRevision;
            var presentationKey = state.GameId + "|" + getLocalPlayerId() + "|" +
                (state.EffectRuntime?.StateRevision ?? 0) + "|" + dialog.GameplayTextGeneration + "|" +
                visibleScope + "|" + string.Join(",", pending.OptionIds) + "|" + BuildPendingPresentationKey(pending);
            if (presentationKey == pendingPresentationKey && dialog.IsShowing)
            {
                return true;
            }

            pendingPresentationKey = presentationKey;
            var options = presenter.QueryPendingOptions(state, getLocalPlayerId(), null);
            var presentation = presenter.GetPendingPresentation(state, getLocalPlayerId(), options, visible);
            if (options.Presentation != null && presentation == null)
            {
                HideDialog();
                return false;
            }
            if (pending.ChoiceType == CharacterPendingChoiceTypes.TinManFirstPurchase ||
                pending.ChoiceType == CharacterPendingChoiceTypes.TinManSecondPurchase)
            {
                ShowTinManPurchaseDecision(pending, options, presentation);
                return true;
            }

            var choices = options.Get(CharacterEffectParameterKeys.Choice);
            var dialogOptions = new List<EffectDialogOption>();
            for (var i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                if (choice.Id == CharacterEffectChoiceIds.GainGold)
                {
                    dialogOptions.Add(new EffectDialogOption(choice.DisplayName, () =>
                    {
                        pendingPresentationKey = string.Empty;
                        submitPending(new Dictionary<string, string>
                        {
                            [CharacterEffectParameterKeys.Choice] = CharacterEffectChoiceIds.GainGold
                        });
                    }) { LabelDocument = dialog.ResolveLegacyOptionLabel(presentation, choice.Id) });
                }
                else if (choice.Id == CharacterEffectChoiceIds.MoveInfluence &&
                         options.Get(CharacterEffectParameterKeys.SourceInfluenceSlotId).Count > 0)
                {
                    dialogOptions.Add(new EffectDialogOption(choice.DisplayName, () =>
                    {
                        pendingPresentationKey = string.Empty;
                        if (!beginPendingInfluenceMove())
                        {
                            PresentLegacyPrompt(new GameplayTextRef("core:legacy.character.tin_man.discard.move_unavailable",
                                TextInteger("gold", 5)));
                            SynchronizePending();
                        }
                    }) { LabelDocument = dialog.ResolveLegacyOptionLabel(presentation, choice.Id) });
                }
            }

            dialog.ShowOptions(
                dialog.ResolveLegacyOptionsPrompt(presentation),
                "锡人计谋：人员召集",
                options.SummaryText,
                dialogOptions);
            PresentLegacyPrompt(new GameplayTextRef("core:legacy.character.tin_man.discard.hint"));
            return true;
        }

        public void HideDialog()
        {
            ClearLegacyPrompt();
            pendingPresentationKey = string.Empty;
            dialog.Hide();
            endFacilityEffectSelection();
        }

        private void ShowTinManPurchaseDecision(
            PendingCharacterEffectState pending,
            CharacterCardOptionQueryResult query,
            GameplayInteractionPresentation presentation)
        {
            var firstStep = pending.ChoiceType == CharacterPendingChoiceTypes.TinManFirstPurchase;
            var purchaseChoiceId = firstStep
                ? CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium
                : CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium;
            var choices = query.Get(CharacterEffectParameterKeys.Choice);
            var dialogOptions = new List<EffectDialogOption>();
            for (var i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                if (choice.Id != purchaseChoiceId)
                {
                    continue;
                }

                var capturedChoiceId = choice.Id;
                dialogOptions.Add(new EffectDialogOption(choice.DisplayName, () =>
                {
                    pendingPresentationKey = string.Empty;
                    submitPending(CreateTinManPendingChoiceParameters(pending, capturedChoiceId));
                }) { LabelDocument = dialog.ResolveLegacyOptionLabel(presentation, capturedChoiceId) });
            }

            dialog.ShowOptions(
                dialog.ResolveLegacyOptionsPrompt(presentation),
                firstStep
                    ? "锡人策略：建立威信（第一步）"
                    : "锡人策略：建立威信（第二步）",
                query.SummaryText,
                dialogOptions,
                () =>
                {
                    pendingPresentationKey = string.Empty;
                    submitPending(CreateTinManPendingChoiceParameters(
                        pending,
                        CharacterEffectChoiceIds.TinManFinishPurchasing));
                });
            PresentLegacyPrompt(CreatePurchaseHint(firstStep, dialogOptions.Count > 0, query));
        }

        private static GameplayTextArgument TextInteger(string name, int value) => new GameplayTextArgument
        { Name = name, Kind = "integer", Value = NormalizedValue.CreateInteger(value) };

        private static GameplayTextRef CreatePurchaseHint(bool firstStep, bool hasPurchase, CharacterCardOptionQueryResult query)
        {
            if (!hasPurchase) return new GameplayTextRef("core:legacy.character.tin_man.purchase.unavailable");
            return firstStep
                ? new GameplayTextRef("core:legacy.character.tin_man.first_purchase.hint", TextInteger("score", query.ScoreGain),
                    TextInteger("cost", CharacterCardService.TinManFirstPureOriginiumCost))
                : new GameplayTextRef("core:legacy.character.tin_man.second_purchase.hint",
                    TextInteger("cost", CharacterCardService.TinManSecondPureOriginiumCost));
        }

        private void PresentLegacyPrompt(GameplayTextRef reference)
        {
            var state = getState();
            var pending = CurrentTinManPending();
            if (pending == null || RenderLegacyMessageDocument == null) return;
            var document = dialog.ResolveGameplayText(reference);
            if (document == null) return;
            var gameId = state.GameId;
            var viewer = getLocalPlayerId();
            var revision = state.EffectRuntime?.StateRevision ?? 0;
            var contentHash = state.ContentPackHash;
            var fingerprint = BuildPendingPresentationKey(pending) + "|" + string.Join(",", pending.OptionIds);
            var pendingId = StableIdFactory.Create("legacy-interaction", pending.SourceCommandId ?? string.Empty,
                pending.ChoiceType ?? string.Empty, pending.CardId ?? string.Empty);
            legacyMessageGameId = gameId;
            legacyMessageViewer = viewer;
            legacyMessageRevision = revision;
            legacyMessagePendingId = pendingId;
            RenderLegacyMessageDocument(gameId, viewer, revision, pendingId, document, () =>
            {
                var current = getState();
                var currentPending = CurrentTinManPending();
                return currentPending != null && current?.GameId == gameId && getLocalPlayerId() == viewer &&
                    current.ContentPackHash == contentHash && (current.EffectRuntime?.StateRevision ?? 0) == revision &&
                    StableIdFactory.Create("legacy-interaction", currentPending.SourceCommandId ?? string.Empty,
                        currentPending.ChoiceType ?? string.Empty, currentPending.CardId ?? string.Empty) == pendingId &&
                    BuildPendingPresentationKey(currentPending) + "|" + string.Join(",", currentPending.OptionIds) == fingerprint;
            });
        }

        private void ClearLegacyPrompt()
        {
            if (legacyMessageGameId != null)
                RenderLegacyMessageDocument?.Invoke(legacyMessageGameId, legacyMessageViewer, legacyMessageRevision,
                    legacyMessagePendingId, null, null);
            legacyMessageGameId = legacyMessagePendingId = null;
            legacyMessageViewer = legacyMessageRevision = -1;
        }

        private static IReadOnlyDictionary<string, string> CreateTinManPendingChoiceParameters(
            PendingCharacterEffectState pending,
            string choiceId)
        {
            return new Dictionary<string, string>
            {
                [CharacterEffectParameterKeys.Choice] = choiceId ?? string.Empty,
                [CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId] =
                    pending == null ? string.Empty : pending.SourceCommandId ?? string.Empty
            };
        }

        private void SubmitEffect(string effectMode, IDictionary<string, string> parameters)
        {
            parameters[CharacterEffectParameterKeys.OfferSecondEffect] = "true";
            submitEffect(effectMode, new Dictionary<string, string>(parameters));
        }

        private PendingCharacterEffectState CurrentTinManPending()
        {
            var state = getState();
            var pending = state == null ? null : state.PendingCharacterEffect;
            return pending != null &&
                   pending.IsValid() &&
                   pending.PlayerId == getLocalPlayerId() &&
                   (pending.ChoiceType == CharacterPendingChoiceTypes.TinManDiscard ||
                    pending.ChoiceType == CharacterPendingChoiceTypes.TinManFirstPurchase ||
                    pending.ChoiceType == CharacterPendingChoiceTypes.TinManSecondPurchase)
                ? pending
                : null;
        }

        private static string BuildPendingPresentationKey(PendingCharacterEffectState pending)
        {
            var currentCardId = pending.RemainingCardIds == null || pending.RemainingCardIds.Count == 0
                ? string.Empty
                : pending.RemainingCardIds[0];
            return pending.ChoiceType + "|" +
                   (pending.SourceCommandId ?? string.Empty) + "|" +
                   currentCardId + "|" +
                   (pending.RemainingCardIds == null ? 0 : pending.RemainingCardIds.Count) + "|" +
                   (pending.ResolvedCardIds == null ? 0 : pending.ResolvedCardIds.Count) + "|" +
                   (pending.OptionIds == null ? 0 : pending.OptionIds.Count);
        }

    }
}
