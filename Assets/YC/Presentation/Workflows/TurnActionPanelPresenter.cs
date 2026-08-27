using System;
using YC.Application.Gameplay;
using YC.Domain.CardFlows;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class TurnActionPanelPresenter
    {
        private static readonly MainActionBudgetService MainActionBudgetService =
            new MainActionBudgetService();

        private readonly IWritableGameplayContext context;
        private readonly ITurnActionView view;
        private readonly InteractionFlowCoordinator flowCoordinator;
        private readonly ResourceCollectionPresenter resourceCollectionPresenter;
        private readonly InfluenceActionPresenter influenceActionPresenter;
        private readonly ExplorationEventPresenter explorationEventPresenter;
        private readonly BuildInteraction buildInteraction;
        private readonly Func<string> getCompletedActionName;
        private readonly Func<bool> canEndCurrentAction;
        private readonly Func<bool> isSelectingMoveTarget;

        public TurnActionPanelPresenter(
            IWritableGameplayContext context,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            ResourceCollectionPresenter resourceCollectionPresenter,
            InfluenceActionPresenter influenceActionPresenter,
            ExplorationEventPresenter explorationEventPresenter,
            BuildInteraction buildInteraction,
            Func<string> getCompletedActionName,
            Func<bool> canEndCurrentAction,
            Func<bool> isSelectingMoveTarget)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.flowCoordinator = flowCoordinator ??
                                   throw new ArgumentNullException(nameof(flowCoordinator));
            this.resourceCollectionPresenter = resourceCollectionPresenter ??
                                               throw new ArgumentNullException(
                                                   nameof(resourceCollectionPresenter));
            this.influenceActionPresenter = influenceActionPresenter ??
                                            throw new ArgumentNullException(
                                                nameof(influenceActionPresenter));
            this.explorationEventPresenter = explorationEventPresenter ??
                                             throw new ArgumentNullException(
                                                 nameof(explorationEventPresenter));
            this.buildInteraction = buildInteraction ??
                                    throw new ArgumentNullException(nameof(buildInteraction));
            this.getCompletedActionName = getCompletedActionName ??
                                          throw new ArgumentNullException(
                                              nameof(getCompletedActionName));
            this.canEndCurrentAction = canEndCurrentAction ??
                                       throw new ArgumentNullException(
                                           nameof(canEndCurrentAction));
            this.isSelectingMoveTarget = isSelectingMoveTarget ??
                                         throw new ArgumentNullException(
                                             nameof(isSelectingMoveTarget));
        }

        public ActionPanelViewModel BuildViewModel()
        {
            var state = context.CurrentState;
            if (state == null)
            {
                return EmptyViewModel();
            }

            var player = state.FindPlayer(context.LocalPlayerId);
            var isActionPhase = state.Phase == GamePhase.ActionRound1 ||
                                state.Phase == GamePhase.ActionRound2;
            var isLocalTurn = state.CurrentPlayerId == context.LocalPlayerId;
            var hasPendingChoice = state.HasPendingChoice();
            var mainActionDone = player != null &&
                                 !MainActionBudgetService.HasAvailableMainAction(
                                     state,
                                     context.LocalPlayerId);
            var hasLocalBuildDraft = buildInteraction.IsActive;
            var canChooseQuickAction = isActionPhase &&
                                       isLocalTurn &&
                                       !hasPendingChoice &&
                                       !hasLocalBuildDraft;
            var canChooseMainAction = canChooseQuickAction && !mainActionDone;
            var pendingCharacterEffect = state.PendingCharacterEffect;
            var hasUnfinishedCharacterUse = pendingCharacterEffect != null &&
                                            pendingCharacterEffect.IsValid() &&
                                            pendingCharacterEffect.PlayerId ==
                                            context.LocalPlayerId &&
                                            (pendingCharacterEffect.ChoiceType ==
                                             CharacterPendingChoiceTypes.SecondEffectDecision ||
                                             pendingCharacterEffect.ChoiceType ==
                                             CharacterPendingChoiceTypes.SecondEffectExecution);
            var canOpenUnfinishedCharacterUse = isActionPhase &&
                                                isLocalTurn &&
                                                !hasLocalBuildDraft &&
                                                hasUnfinishedCharacterUse;
            var displayedMode = ResolveDisplayedMode(
                state,
                player,
                isActionPhase,
                isLocalTurn,
                hasPendingChoice,
                mainActionDone);
            var waiting = displayedMode == InteractionMode.WaitingForNextPlayer ||
                          (hasPendingChoice &&
                           CardFlowStateAdapter.GetPendingChoiceView(state)?.PlayerId !=
                           context.LocalPlayerId);

            return new ActionPanelViewModel(
                displayedMode,
                "本机：" + GetPlayerDisplayName(context.LocalPlayerId) +
                " / 行动：" + GetPlayerDisplayName(state.CurrentPlayerId),
                "第 " + state.Round + " 回合 / 行动轮 " + state.ActionRound,
                BuildStatus(
                    state,
                    player,
                    isActionPhase,
                    isLocalTurn,
                    hasPendingChoice,
                    mainActionDone),
                player != null,
                player == null ? PlayerColor.Red : player.Color,
                player == null ? 0 : player.InfluenceSupply,
                player != null &&
                !player.UsedCharacterThisRound &&
                !string.IsNullOrEmpty(player.CoveredCharacterCardId) &&
                (canOpenUnfinishedCharacterUse ||
                 (canChooseQuickAction && !player.CharacterCardLockedThisTurn)),
                canChooseQuickAction,
                canChooseMainAction,
                canChooseMainAction,
                canChooseMainAction,
                canChooseMainAction && player != null,
                canChooseMainAction,
                canEndCurrentAction() && !RoundTrackRule.IsFinalState(state),
                waiting);
        }

        public bool CanStartMainAction()
        {
            return CanStartAction(false);
        }

        public bool CanStartQuickAction()
        {
            return CanStartAction(true);
        }

        public string GetQuickActionUnavailableReason()
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null)
            {
                return "当前玩家不存在。";
            }

            if (buildInteraction.IsActive)
            {
                return "请先完成或取消当前建设草稿。";
            }

            if (state.CurrentPlayerId != context.LocalPlayerId)
            {
                return "尚未轮到本机玩家行动。";
            }

            if (state.HasPendingChoice())
            {
                return "请先处理待选择项。";
            }

            if (state.Phase != GamePhase.ActionRound1 &&
                state.Phase != GamePhase.ActionRound2)
            {
                return "当前阶段不能宣告城市样式。";
            }

            if (flowCoordinator.CurrentMode != InteractionMode.ChooseAction &&
                flowCoordinator.CurrentMode != InteractionMode.Hidden)
            {
                return "请先完成或取消当前正在进行的行动。";
            }

            return string.Empty;
        }

        public string GetUnavailableEndActionPrompt()
        {
            var state = context.CurrentState;
            if (state == null)
            {
                return "当前没有可结束的回合。";
            }

            if (state.Phase == GamePhase.ResourceCollection)
            {
                return "当前玩家已经提交过采集，等待其他玩家。";
            }

            if (state.Phase == GamePhase.Cleanup)
            {
                return "当前只有起始玩家可以结束收尾阶段。";
            }

            return "完成主要行动后才能结束本回合。";
        }

        public string BuildEndActionPrompt(GameState state)
        {
            return state == null
                ? "当前没有可结束的回合。"
                : BuildViewModel().StatusText;
        }

        public static string BuildCompletedMainActionMessage(string actionName)
        {
            return string.IsNullOrEmpty(actionName)
                ? "主要行动已完成"
                : actionName + "已完成";
        }

        private bool CanStartAction(bool quickAction)
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null)
            {
                view.ShowPrompt("当前玩家不存在。");
                return false;
            }

            if (buildInteraction.IsActive)
            {
                view.ShowPrompt("请先完成或取消当前建设草稿。");
                return false;
            }

            if (quickAction &&
                flowCoordinator.CurrentMode != InteractionMode.ChooseAction &&
                flowCoordinator.CurrentMode != InteractionMode.Hidden)
            {
                view.ShowPrompt("请先完成或取消当前正在进行的行动，再执行快速行动。");
                return false;
            }

            if (state.CurrentPlayerId != context.LocalPlayerId)
            {
                view.ShowPrompt("等待玩家 " + state.CurrentPlayerId + " 行动。");
                return false;
            }

            if (state.HasPendingChoice())
            {
                view.ShowPrompt("请先处理待选择项。");
                return false;
            }

            if (!quickAction)
            {
                var budgetValidation = MainActionBudgetService.ValidateCanSpend(
                    state,
                    context.LocalPlayerId);
                if (!budgetValidation.IsValid)
                {
                    view.ShowPrompt(budgetValidation.Reason);
                    return false;
                }
            }

            if (state.Phase != GamePhase.ActionRound1 &&
                state.Phase != GamePhase.ActionRound2)
            {
                view.ShowPrompt(
                    quickAction
                        ? "当前阶段不能执行快速行动。"
                        : "当前阶段不能执行行动。");
                return false;
            }

            return true;
        }

        private InteractionMode ResolveDisplayedMode(
            GameState state,
            PlayerState player,
            bool isActionPhase,
            bool isLocalTurn,
            bool hasPendingChoice,
            bool mainActionDone)
        {
            if (hasPendingChoice)
            {
                var choice = CardFlowStateAdapter.GetPendingChoiceView(state);
                return choice != null && choice.PlayerId != context.LocalPlayerId
                    ? InteractionMode.WaitingForNextPlayer
                    : InteractionMode.Busy;
            }

            if (state.Phase == GamePhase.ResourceCollection)
            {
                return player != null && !player.HasCollectedResourcesThisRound
                    ? InteractionMode.Busy
                    : InteractionMode.WaitingForNextPlayer;
            }

            if (buildInteraction.IsActive)
            {
                return InteractionMode.Busy;
            }

            if (state.Phase == GamePhase.Cleanup ||
                (isActionPhase && (!isLocalTurn || mainActionDone)))
            {
                return InteractionMode.WaitingForNextPlayer;
            }

            return flowCoordinator.CurrentMode;
        }

        private string BuildStatus(
            GameState state,
            PlayerState player,
            bool isActionPhase,
            bool isLocalTurn,
            bool hasPendingChoice,
            bool mainActionDone)
        {
            if (state.Phase == GamePhase.Entrance)
            {
                return "入场阶段：选择初始移动城市位置";
            }

            if (state.Phase == GamePhase.CharacterCover)
            {
                return player != null &&
                       state.CurrentPlayerId == context.LocalPlayerId &&
                       string.IsNullOrEmpty(player.CoveredCharacterCardId)
                    ? "拖动手牌到右侧面板盖放"
                    : "入场阶段：等待当前玩家盖放角色卡";
            }

            if (state.Phase == GamePhase.ResourceCollection)
            {
                return player == null
                    ? "采集阶段：当前玩家不存在"
                    : player.HasCollectedResourcesThisRound
                        ? "采集阶段：已提交采集，等待其他玩家"
                        : resourceCollectionPresenter.BuildStatus();
            }

            if (state.Phase == GamePhase.Cleanup)
            {
                if (state.PendingCharacterEffect != null &&
                    state.PendingCharacterEffect.IsValid() &&
                    state.PendingCharacterEffect.ChoiceType ==
                    CharacterPendingChoiceTypes.LiskarmCleanupRemoval)
                {
                    return "结束阶段：雷蛇要求移除 1 个己方影响力。请点击地图上高亮的影响力；选择完成前不能结束本回合";
                }

                return canEndCurrentAction()
                    ? "收尾阶段：点击结束本回合进入下一回合"
                    : "收尾阶段：等待起始玩家结束本回合";
            }

            if (!isActionPhase)
            {
                return "当前阶段不能执行行动";
            }

            if (hasPendingChoice)
            {
                var choice = CardFlowStateAdapter.GetPendingChoiceView(state);
                return choice != null && choice.PlayerId != context.LocalPlayerId
                    ? "等待玩家 " + choice.PlayerId + " 处理事件选择"
                    : "请先处理事件选择";
            }

            if (!isLocalTurn)
            {
                return "等待玩家 " + state.CurrentPlayerId + " 行动";
            }

            if (buildInteraction.IsActive)
            {
                return buildInteraction.BuildPresentation().PromptText;
            }

            if (isSelectingMoveTarget())
            {
                return "城市移动：选择高亮资源点";
            }

            if (flowCoordinator.IsActive(explorationEventPresenter))
            {
                return explorationEventPresenter.CurrentPrompt;
            }

            if (flowCoordinator.IsActive(influenceActionPresenter))
            {
                return influenceActionPresenter.CurrentPrompt;
            }

            if (flowCoordinator.IsActive(resourceCollectionPresenter))
            {
                return resourceCollectionPresenter.BuildStatus();
            }

            if (player != null &&
                player.CompletedMainActionsThisTurn > 0 &&
                player.RemainingMainActionsThisTurn > 0)
            {
                return "剩余额外主要行动：" + player.RemainingMainActionsThisTurn +
                       "，可继续主要/快速行动或结束行动";
            }

            if (mainActionDone)
            {
                return BuildCompletedMainActionMessage(getCompletedActionName());
            }

            return player == null ? "未知玩家" : "请选择一项主要行动";
        }

        private string GetPlayerDisplayName(int playerId)
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(playerId);
            return player == null
                ? playerId.ToString()
                : string.IsNullOrEmpty(player.Name)
                    ? "Player " + playerId
                    : player.Name;
        }

        private static ActionPanelViewModel EmptyViewModel()
        {
            return new ActionPanelViewModel(
                InteractionMode.Hidden,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                PlayerColor.Red,
                0,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false);
        }
    }
}
