using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Economy;
using YC.Domain.State;
using YC.Presentation.Workflows;
using UnityEngine;

namespace YC.Presentation
{
    /// <summary>把角色牌效果分流到独立弹窗或地图选点，不让信息面板承担结算状态。</summary>
    internal sealed class CharacterCardEffectInteractionUiCoordinator
    {
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly Func<RectTransform> getCanvas;
        private readonly CharacterCardPanelPresenter presenter;
        private readonly CharacterCardEffectChoiceDialog dialog;
        private readonly Func<string, CharacterCardEffectKind, bool> beginMapEffect;
        private readonly Func<bool> beginPendingInfluenceMove;
        private readonly Func<IReadOnlyList<string>, Action<string>, Action, bool> beginFacilityEffectSelection;
        private readonly Action endFacilityEffectSelection;
        private readonly Action<string, IReadOnlyDictionary<string, string>> submitEffect;
        private readonly Action<IReadOnlyDictionary<string, string>> submitPending;
        private readonly Action<string> setPrompt;

        private string pendingPresentationKey = string.Empty;

        public CharacterCardEffectInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            Func<RectTransform> getCanvas,
            CharacterCardPanelPresenter presenter,
            CharacterCardEffectChoiceDialog dialog,
            Func<string, CharacterCardEffectKind, bool> beginMapEffect,
            Func<bool> beginPendingInfluenceMove,
            Func<IReadOnlyList<string>, Action<string>, Action, bool> beginFacilityEffectSelection,
            Action endFacilityEffectSelection,
            Action<string, IReadOnlyDictionary<string, string>> submitEffect,
            Action<IReadOnlyDictionary<string, string>> submitPending,
            Action<string> setPrompt)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.getCanvas = getCanvas ?? throw new ArgumentNullException(nameof(getCanvas));
            this.presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            this.dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            this.beginMapEffect = beginMapEffect ?? throw new ArgumentNullException(nameof(beginMapEffect));
            this.beginPendingInfluenceMove = beginPendingInfluenceMove ?? throw new ArgumentNullException(nameof(beginPendingInfluenceMove));
            this.beginFacilityEffectSelection = beginFacilityEffectSelection ?? throw new ArgumentNullException(nameof(beginFacilityEffectSelection));
            this.endFacilityEffectSelection = endFacilityEffectSelection ?? throw new ArgumentNullException(nameof(endFacilityEffectSelection));
            this.submitEffect = submitEffect ?? throw new ArgumentNullException(nameof(submitEffect));
            this.submitPending = submitPending ?? throw new ArgumentNullException(nameof(submitPending));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
        }

        public bool TryBeginEffect(string effectMode, CharacterCardEffectKind effect)
        {
            HideDialog();
            if (beginMapEffect(effectMode, effect))
            {
                return true;
            }

            switch (effect)
            {
                case CharacterCardEffectKind.ElysiumLogistics:
                    return ShowSingleChoiceEffect(
                        effectMode,
                        effect,
                        CharacterEffectParameterKeys.ResourceType,
                        "极境策略：后勤调遣",
                        "选择一种自己持有数量并列最少的基础资源，获得 4 个。",
                        option => "获得 4 个" + option.DisplayName);
                case CharacterCardEffectKind.TexasSpecialDelivery:
                    return BeginTexasSpecialDelivery(effectMode, effect);
                case CharacterCardEffectKind.CannotRequisition:
                    return ShowSingleChoiceEffect(
                        effectMode,
                        effect,
                        CharacterEffectParameterKeys.ResourceType,
                        "坎诺特计谋：征收物资",
                        "选择一种基础资源；所有玩家以每个 2 金券出售该资源至 0，你获得 1 分。",
                        option => "征收" + option.DisplayName);
                case CharacterCardEffectKind.CannotTradeChannel:
                    return ShowCannotTrade(effectMode);
                case CharacterCardEffectKind.TinManEstablishPrestige:
                    return ShowTinManStrategy(effectMode);
                case CharacterCardEffectKind.TinManDeepPlanning:
                    SubmitEffect(effectMode, new Dictionary<string, string>());
                    return true;
                default:
                    return false;
            }
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
                getCanvas(),
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

        private bool BeginTexasSpecialDelivery(string effectMode, CharacterCardEffectKind effect)
        {
            var state = getState();
            if (state == null)
            {
                setPrompt("当前游戏状态不可用，无法结算德克萨斯策略。");
                return true;
            }

            var result = presenter.QueryOptions(state, getLocalPlayerId(), effect, null);
            var choices = result.Get(CharacterEffectParameterKeys.FacilityCardId);
            if (choices.Count == 0)
            {
                setPrompt("设施供应区当前没有可由德克萨斯选择的设施牌。");
                return true;
            }

            var facilityIds = new List<string>();
            for (var i = 0; i < choices.Count; i++)
            {
                if (!string.IsNullOrEmpty(choices[i].Id))
                {
                    facilityIds.Add(choices[i].Id);
                }
            }

            var begun = beginFacilityEffectSelection(
                facilityIds.AsReadOnly(),
                facilityId => SubmitEffect(effectMode, new Dictionary<string, string>
                {
                    [CharacterEffectParameterKeys.FacilityCardId] = facilityId
                }),
                CancelInitialSelection);
            setPrompt(begun
                ? "德克萨斯策略：请点击左上设施供应区中高亮的设施牌；按 Esc 取消。"
                : "设施供应区当前无法进入德克萨斯选牌状态。");
            return true;
        }

        private bool ShowSingleChoiceEffect(
            string effectMode,
            CharacterCardEffectKind effect,
            string parameterKey,
            string title,
            string description,
            Func<CharacterCardOption, string> formatLabel)
        {
            var state = getState();
            if (state == null)
            {
                setPrompt("当前游戏状态不可用，无法结算角色牌。");
                return true;
            }

            var result = presenter.QueryOptions(state, getLocalPlayerId(), effect, null);
            var choices = result.Get(parameterKey);
            if (choices.Count == 0)
            {
                setPrompt("当前角色牌效果没有合法选项。");
                return true;
            }

            var options = new List<EffectDialogOption>();
            for (var i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                var capturedChoice = choice;
                options.Add(new EffectDialogOption(formatLabel(choice), () =>
                {
                    SubmitEffect(effectMode, new Dictionary<string, string>
                    {
                        [parameterKey] = capturedChoice.Id
                    });
                }));
            }

            dialog.ShowOptions(getCanvas(), title, description, options, CancelInitialSelection);
            setPrompt("请在角色牌结算弹窗中选择效果参数。");
            return true;
        }

        private bool ShowCannotTrade(string effectMode)
        {
            var state = getState();
            var player = state == null ? null : state.FindPlayer(getLocalPlayerId());
            if (player == null || player.Resources == null)
            {
                setPrompt("当前玩家资源状态不可用，无法结算坎诺特策略。");
                return true;
            }

            dialog.ShowResourceSale(
                getCanvas(),
                new[] { "源岩", "源石碎片", "异铁", "至纯源石" },
                new[]
                {
                    player.Resources.Originium,
                    player.Resources.OriginiumShard,
                    player.Resources.Iron,
                    player.Resources.PureOriginium
                },
                new[]
                {
                    ResourceSaleService.OriginiumUnitPrice,
                    ResourceSaleService.OriginiumShardUnitPrice,
                    ResourceSaleService.IronUnitPrice,
                    ResourceSaleService.PureOriginiumUnitPrice
                },
                values =>
                {
                    SubmitEffect(effectMode, new Dictionary<string, string>
                    {
                        [CharacterEffectParameterKeys.SaleOriginium] = ValueAt(values, 0).ToString(),
                        [CharacterEffectParameterKeys.SaleOriginiumShard] = ValueAt(values, 1).ToString(),
                        [CharacterEffectParameterKeys.SaleIron] = ValueAt(values, 2).ToString(),
                        [CharacterEffectParameterKeys.SalePureOriginium] = ValueAt(values, 3).ToString()
                    });
                },
                CancelInitialSelection);
            setPrompt("请在角色牌结算弹窗中选择要出售的资源数量。");
            return true;
        }

        private bool ShowTinManStrategy(string effectMode)
        {
            var state = getState();
            var player = state == null ? null : state.FindPlayer(getLocalPlayerId());
            if (player == null || player.Resources == null)
            {
                setPrompt("当前玩家资源状态不可用，无法结算锡人策略。");
                return true;
            }

            setPrompt("正在结算锡人策略：固定获得 1 分后进入第一笔购买选择。");
            SubmitEffect(effectMode, new Dictionary<string, string>());
            return true;
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
                getCanvas(),
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

        private void CancelInitialSelection()
        {
            setPrompt("已取消本次角色牌效果选择，尚未提交结算。");
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

        private static int ValueAt(IReadOnlyList<int> values, int index)
        {
            return values != null && index >= 0 && index < values.Count ? values[index] : 0;
        }
    }
}
