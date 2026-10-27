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
    /// <summary>提交现行角色激活及模式选择意图；旧Pending展示已退役。</summary>
    internal sealed class CharacterCardEffectInteractionUiCoordinator
    {
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly CharacterCardPanelPresenter presenter;
        private readonly CharacterCardEffectChoiceDialog dialog;
        private readonly Action endFacilityEffectSelection;
        private readonly Action<string, IReadOnlyDictionary<string, string>> submitEffect;

        public Func<GameStateView> GetVisibleState { private get; set; }
        public Func<InteractionRequestProjection, string, bool> SubmitSelection { private get; set; }
        public Action<string, int, int, string, GameplayTextDocument, Func<bool>> RenderLegacyMessageDocument { private get; set; }

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
            _ = beginPendingInfluenceMove ?? throw new ArgumentNullException(nameof(beginPendingInfluenceMove));
            this.endFacilityEffectSelection = endFacilityEffectSelection ?? throw new ArgumentNullException(nameof(endFacilityEffectSelection));
            this.submitEffect = submitEffect ?? throw new ArgumentNullException(nameof(submitEffect));
            _ = submitPending ?? throw new ArgumentNullException(nameof(submitPending));
            _ = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
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

        public void HideDialog()
        {
            dialog.Hide();
            endFacilityEffectSelection();
        }

        private void SubmitEffect(string effectMode, IDictionary<string, string> parameters)
        {
            parameters[CharacterEffectParameterKeys.OfferSecondEffect] = "true";
            submitEffect(effectMode, new Dictionary<string, string>(parameters));
        }

    }
}
