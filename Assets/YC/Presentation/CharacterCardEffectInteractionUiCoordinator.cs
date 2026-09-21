using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;
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

        private string pendingPresentationKey = string.Empty;

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

            var presentationKey = BuildPendingPresentationKey(pending);
            if (presentationKey == pendingPresentationKey && dialog.IsShowing)
            {
                return true;
            }

            pendingPresentationKey = presentationKey;
            var state = getState();
            var options = presenter.QueryPendingOptions(state, getLocalPlayerId(), null);
            if (pending.ChoiceType == CharacterPendingChoiceTypes.TinManFirstPurchase ||
                pending.ChoiceType == CharacterPendingChoiceTypes.TinManSecondPurchase)
            {
                ShowTinManPurchaseDecision(pending, options);
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
                    }));
                }
                else if (choice.Id == CharacterEffectChoiceIds.MoveInfluence &&
                         options.Get(CharacterEffectParameterKeys.SourceInfluenceSlotId).Count > 0)
                {
                    dialogOptions.Add(new EffectDialogOption(choice.DisplayName + "（转到地图选点）", () =>
                    {
                        pendingPresentationKey = string.Empty;
                        if (!beginPendingInfluenceMove())
                        {
                            setPrompt("当前没有可执行的影响力移动，请改为获得 5 金券。");
                            SynchronizePending();
                        }
                    }));
                }
            }

            dialog.ShowOptions(
                "锡人计谋：人员召集",
                "正在收回「" + CurrentTinManDiscardName(pending) + "」。请选择该牌带来的奖励。",
                dialogOptions);
            setPrompt("请在角色牌结算弹窗中处理当前弃牌。选择移动影响力时将转到地图选点。");
            return true;
        }

        public void HideDialog()
        {
            pendingPresentationKey = string.Empty;
            dialog.Hide();
            endFacilityEffectSelection();
        }

        private void ShowTinManPurchaseDecision(
            PendingCharacterEffectState pending,
            CharacterCardOptionQueryResult query)
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
                }));
            }

            dialog.ShowOptions(
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
            setPrompt(dialogOptions.Count > 0
                ? (firstStep
                    ? "锡人已获得 1 分。请选择是否支付 12 金券；取消会立即结算。"
                    : "锡人第一笔购买已完成。请选择是否再支付 15 金券；取消会按当前结果结算。")
                : "当前金券不足以进行这笔购买，点击取消即可结算。");
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

        private static string CurrentTinManDiscardName(PendingCharacterEffectState pending)
        {
            if (pending == null || pending.RemainingCardIds == null || pending.RemainingCardIds.Count == 0)
            {
                return "未知角色牌";
            }

            return CharacterCardPanelPresenter.ResolveCardDisplayName(pending.RemainingCardIds[0]);
        }

    }
}
