using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.SpecialActions
{
    public sealed class SpecialActionService
    {
        private readonly SpecialActionOptionQueryService optionQuery;
        private readonly SpecialActionLifecycleService lifecycleService;
        private readonly InfluenceService influenceService;
        private readonly FacilityInfluenceEffectService replacementService;
        private readonly MainActionBudgetService mainActionBudgetService;

        public SpecialActionService(
            SpecialActionOptionQueryService optionQuery,
            SpecialActionLifecycleService lifecycleService,
            InfluenceService influenceService,
            FacilityInfluenceEffectService replacementService,
            MainActionBudgetService mainActionBudgetService)
        {
            this.optionQuery = optionQuery ?? throw new ArgumentNullException(nameof(optionQuery));
            this.lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            this.replacementService = replacementService ?? throw new ArgumentNullException(nameof(replacementService));
            this.mainActionBudgetService = mainActionBudgetService ?? throw new ArgumentNullException(nameof(mainActionBudgetService));
        }

        public SpecialActionOperationResult Begin(
            GameState state,
            int playerId,
            string specialActionId,
            string declarationMarkerId,
            string sourceCommandId)
        {
            return Begin(
                state,
                playerId,
                specialActionId,
                declarationMarkerId,
                sourceCommandId,
                -1,
                -1);
        }

        public SpecialActionOperationResult Begin(
            GameState state,
            int playerId,
            string specialActionId,
            string declarationMarkerId,
            string sourceCommandId,
            int originiumAmount,
            int ironAmount)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var definition = SpecialActionDatabase.Get(specialActionId);
            if (definition == null)
            {
                return Failure(CommandErrorCode.InvalidTarget, "未知的特殊行动。");
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return Failure(CommandErrorCode.InvalidPlayer, "玩家不存在。");
            }

            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
            {
                return Failure(CommandErrorCode.WrongPhase, "特殊行动只能在行动轮阶段发动。");
            }

            if (state.CurrentPlayerId != playerId)
            {
                return Failure(CommandErrorCode.NotCurrentPlayer, "当前不是该玩家的行动轮。");
            }

            if (state.HasPendingChoice())
            {
                return Failure(CommandErrorCode.PendingChoiceRequired, "请先完成当前待处理选择。");
            }

            var budgetValidation = mainActionBudgetService.ValidateCanSpend(state, playerId);
            if (!budgetValidation.IsValid)
            {
                return SpecialActionOperationResult.Failure(budgetValidation);
            }

            var option = optionQuery.Query(state, playerId).Find(specialActionId, declarationMarkerId);
            if (option == null)
            {
                return Failure(CommandErrorCode.InvalidSource, "所选城市样式标记未解锁该特殊行动。");
            }

            var markerValidation = lifecycleService.ValidateAvailable(player, definition, declarationMarkerId);
            if (!markerValidation.IsValid)
            {
                return SpecialActionOperationResult.Failure(markerValidation);
            }

            if (player.UsedSpecialActionIdsThisRound != null &&
                player.UsedSpecialActionIdsThisRound.Contains(definition.SpecialActionId))
            {
                return Failure(CommandErrorCode.InvalidSource, "该特殊行动本回合已经使用过。");
            }

            if (definition.LocksCharacterCard && player.UsedCharacterThisTurn)
            {
                return Failure(CommandErrorCode.InvalidTarget, "本玩家行动轮已使用角色牌，不能发动该特殊行动。");
            }

            ResourceSet compositeCost = null;
            if (definition.EffectKind == SpecialActionEffectKind.CompositePowerMove)
            {
                if (option.PaymentOptions.Count == 0)
                {
                    return Failure(CommandErrorCode.InsufficientResource, "无法支付 1 个源石碎片及合计 3 个源岩/异铁。");
                }

                if (originiumAmount < 0 || ironAmount < 0 ||
                    originiumAmount + ironAmount != definition.FlexibleOriginiumAndIronCost)
                {
                    return Failure(CommandErrorCode.InvalidTarget, "必须明确提交非负的源岩与异铁支付数量，且合计 3 个。");
                }

                compositeCost = new ResourceSet
                {
                    Originium = originiumAmount,
                    OriginiumShard = definition.FixedCost == null
                        ? 0
                        : definition.FixedCost.OriginiumShard,
                    Iron = ironAmount
                };
                if (!player.Resources.CanPay(compositeCost))
                {
                    return Failure(CommandErrorCode.InsufficientResource, "无法支付所选的复合动力系统费用组合。");
                }
            }
            else if (definition.FixedCost != null && !player.Resources.CanPay(definition.FixedCost))
            {
                return Failure(CommandErrorCode.InsufficientResource, "资源不足，无法支付特殊行动费用。");
            }

            if (!option.CanUse)
            {
                return Failure(CommandErrorCode.InvalidTarget, option.DisabledReason);
            }

            var paysFixedCostImmediately =
                definition.EffectKind == SpecialActionEffectKind.GrantExtraMainActions ||
                definition.EffectKind == SpecialActionEffectKind.ConsecutiveFreeMoves;
            if (paysFixedCostImmediately && !player.Resources.TryPay(definition.FixedCost))
            {
                return Failure(CommandErrorCode.InsufficientResource, "特殊行动固定费用支付失败。");
            }

            if (compositeCost != null && !player.Resources.TryPay(compositeCost))
            {
                return Failure(CommandErrorCode.InsufficientResource, "复合动力系统费用支付失败。");
            }

            lifecycleService.MarkActivated(player, definition, declarationMarkerId);
            MarkUsedThisRound(player, definition.SpecialActionId);

            switch (definition.EffectKind)
            {
                case SpecialActionEffectKind.DeployInfluence:
                    if (option.RequiredTargetCount <= 0)
                    {
                        return CompleteWithoutPending(state, playerId, definition, "军工化区域当前没有可放置目标，放置步骤已跳过。");
                    }

                    state.PendingSpecialAction = CreatePending(
                        playerId,
                        definition,
                        declarationMarkerId,
                        sourceCommandId,
                        SpecialActionPendingSteps.AwaitMilitaryTargets,
                        0);
                    return SpecialActionOperationResult.Success(definition, false, "请选择军工化区域要放置的影响力槽位。");

                case SpecialActionEffectKind.ReplaceInfluence:
                    if (option.RequiredTargetCount <= 0)
                    {
                        return CompleteWithoutPending(state, playerId, definition, "动员配套体系当前没有对手影响力，替换步骤已跳过。");
                    }

                    state.PendingSpecialAction = CreatePending(
                        playerId,
                        definition,
                        declarationMarkerId,
                        sourceCommandId,
                        SpecialActionPendingSteps.AwaitMobilizationTarget,
                        0);
                    return SpecialActionOperationResult.Success(definition, false, "请选择动员配套体系要替换的对手影响力。");

                case SpecialActionEffectKind.CompositePowerMove:
                    if (option.LegalMoveTargetIds.Count == 0)
                    {
                        return CompleteWithoutPending(
                            state,
                            playerId,
                            definition,
                            "费用已支付；当前没有合法移动目标，移动及航道放置步骤已跳过。");
                    }

                    state.PendingSpecialAction = CreatePending(
                        playerId,
                        definition,
                        declarationMarkerId,
                        sourceCommandId,
                        SpecialActionPendingSteps.AwaitFreeMoveTarget,
                        definition.FreeMoveCount);
                    state.PendingSpecialAction.PaidOriginium = originiumAmount;
                    state.PendingSpecialAction.PaidOriginiumShard = compositeCost.OriginiumShard;
                    state.PendingSpecialAction.PaidIron = ironAmount;
                    return SpecialActionOperationResult.Success(definition, false, "费用已支付，请选择免费移动的目标地点。");

                case SpecialActionEffectKind.GrantExtraMainActions:
                    mainActionBudgetService.SpendCompletedMainAction(state, playerId);
                    mainActionBudgetService.GrantAdditionalMainActions(state, playerId, definition.ExtraMainActionCount);
                    mainActionBudgetService.LockCharacterCard(state, playerId);
                    return SpecialActionOperationResult.Success(definition, true, "支付 6 金券并获得至多 2 次额外主要行动。");

                case SpecialActionEffectKind.ConsecutiveFreeMoves:
                    mainActionBudgetService.LockCharacterCard(state, playerId);
                    if (option.LegalMoveTargetIds.Count == 0)
                    {
                        return CompleteWithoutPending(state, playerId, definition, "当前没有合法移动目标，两次免费移动均已跳过。");
                    }

                    state.PendingSpecialAction = CreatePending(
                        playerId,
                        definition,
                        declarationMarkerId,
                        sourceCommandId,
                        SpecialActionPendingSteps.AwaitFreeMoveTarget,
                        definition.FreeMoveCount);
                    return SpecialActionOperationResult.Success(definition, false, "请选择第一段免费移动的目标地点。");

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public SpecialActionOperationResult ResolveMilitaryTargets(
            GameState state,
            int playerId,
            IReadOnlyList<string> selectedSlotIds)
        {
            var validation = ValidatePending(state, playerId, SpecialActionPendingSteps.AwaitMilitaryTargets);
            if (!validation.IsValid)
            {
                return SpecialActionOperationResult.Failure(validation);
            }

            var requiredCount = optionQuery.GetRequiredMilitaryPlacementCount(state, playerId);
            var selected = selectedSlotIds == null ? new List<string>() : new List<string>(selectedSlotIds);
            if (selected.Count != requiredCount)
            {
                return Failure(CommandErrorCode.InvalidTarget, "必须选择当前状态下尽可能多的影响力槽位，共 " + requiredCount + " 个。");
            }

            if (requiredCount > 0)
            {
                var placement = influenceService.PlaceAtomically(state, playerId, selected);
                if (!placement.Succeeded)
                {
                    return SpecialActionOperationResult.Failure(placement.Validation);
                }

                state.PendingSpecialAction.ResolvedTargetIds.AddRange(selected);
            }

            return CompletePending(state, playerId, "军工化区域放置了 " + requiredCount + " 个影响力。");
        }

        public SpecialActionOperationResult ResolveMobilizationTarget(
            GameState state,
            int playerId,
            string targetInfluenceSlotId)
        {
            var validation = ValidatePending(state, playerId, SpecialActionPendingSteps.AwaitMobilizationTarget);
            if (!validation.IsValid)
            {
                return SpecialActionOperationResult.Failure(validation);
            }

            var legalTargets = optionQuery.GetReplaceableInfluenceSlotIds(state, playerId);
            if (legalTargets.Count == 0)
            {
                return CompletePending(state, playerId, "当前没有对手影响力，替换步骤已跳过。");
            }

            if (string.IsNullOrEmpty(targetInfluenceSlotId) || !legalTargets.Contains(targetInfluenceSlotId))
            {
                return Failure(CommandErrorCode.InvalidTarget, "所选对手影响力已经不是合法替换目标。");
            }

            var replacement = replacementService.ReplaceOnce(state, playerId, targetInfluenceSlotId);
            if (!replacement.Succeeded)
            {
                return SpecialActionOperationResult.Failure(replacement.Validation);
            }

            state.PendingSpecialAction.ResolvedTargetIds.Add(targetInfluenceSlotId);
            var summary = replacement.ReplacementPlaced
                ? "动员配套体系移除对手影响力并在原槽位放置了自己的影响力。"
                : "动员配套体系移除了对手影响力；原槽位无法放置己方影响力。";
            return CompletePending(state, playerId, summary);
        }

        public SpecialActionOperationResult ResolveCompositePayment(
            GameState state,
            int playerId,
            int originiumAmount,
            int ironAmount)
        {
            var validation = ValidatePending(state, playerId, SpecialActionPendingSteps.AwaitCompositePayment);
            if (!validation.IsValid)
            {
                return SpecialActionOperationResult.Failure(validation);
            }

            if (originiumAmount < 0 || ironAmount < 0 || originiumAmount + ironAmount != 3)
            {
                return Failure(CommandErrorCode.InvalidTarget, "源岩与异铁的支付数量必须均为非负数且合计 3 个。");
            }

            var cost = new ResourceSet
            {
                Originium = originiumAmount,
                OriginiumShard = 1,
                Iron = ironAmount
            };
            var player = state.FindPlayer(playerId);
            if (!player.Resources.CanPay(cost))
            {
                return Failure(CommandErrorCode.InsufficientResource, "无法支付所选的复合动力系统费用组合。");
            }

            player.Resources.TryPay(cost);
            state.PendingSpecialAction.PaidOriginium = originiumAmount;
            state.PendingSpecialAction.PaidOriginiumShard = 1;
            state.PendingSpecialAction.PaidIron = ironAmount;

            if (optionQuery.GetLegalFreeMoveTargetIds(state, playerId).Count == 0)
            {
                return CompletePending(state, playerId, "费用已支付；当前没有合法移动目标，移动及航道放置步骤已跳过。");
            }

            state.PendingSpecialAction.Step = SpecialActionPendingSteps.AwaitFreeMoveTarget;
            return CurrentSuccess(state, false, "费用已支付，请选择免费移动的目标地点。");
        }

        public SpecialActionOperationResult SkipFreeMoveWhenNoTarget(GameState state, int playerId)
        {
            var validation = ValidatePending(state, playerId, SpecialActionPendingSteps.AwaitFreeMoveTarget);
            if (!validation.IsValid)
            {
                return SpecialActionOperationResult.Failure(validation);
            }

            if (optionQuery.GetLegalFreeMoveTargetIds(state, playerId).Count > 0)
            {
                return Failure(CommandErrorCode.InvalidTarget, "仍有合法移动目标，不能主动跳过免费移动。");
            }

            state.PendingSpecialAction.RemainingRepetitions = 0;
            return CompletePending(state, playerId, "当前没有合法移动目标，剩余免费移动已跳过。");
        }

        public SpecialActionOperationResult RecordFreeMoveCompleted(
            GameState state,
            int playerId,
            string traversedRouteId,
            bool awaitingMoveEvent)
        {
            var validation = ValidatePending(state, playerId, SpecialActionPendingSteps.AwaitFreeMoveTarget);
            if (!validation.IsValid)
            {
                return SpecialActionOperationResult.Failure(validation);
            }

            if (string.IsNullOrEmpty(traversedRouteId))
            {
                return Failure(CommandErrorCode.InvalidTarget, "免费移动未返回经过的航道。");
            }

            state.PendingSpecialAction.TraversedRouteId = traversedRouteId;
            state.PendingSpecialAction.RemainingRepetitions = Math.Max(
                0,
                state.PendingSpecialAction.RemainingRepetitions - 1);
            if (awaitingMoveEvent)
            {
                state.PendingSpecialAction.Step = SpecialActionPendingSteps.AwaitMoveEvent;
                return CurrentSuccess(state, false, "请先结算本次移动触发的事件。");
            }

            return AdvanceAfterSettledMove(state, playerId);
        }

        public SpecialActionOperationResult ResumeAfterMoveEvent(GameState state, int playerId)
        {
            var validation = ValidatePending(state, playerId, SpecialActionPendingSteps.AwaitMoveEvent);
            if (!validation.IsValid)
            {
                return SpecialActionOperationResult.Failure(validation);
            }

            if (state.PendingCardSession != null && state.PendingCardSession.IsValid())
            {
                return Failure(CommandErrorCode.PendingChoiceRequired, "请先完成移动事件的全部选择。");
            }

            return AdvanceAfterSettledMove(state, playerId);
        }

        public SpecialActionOperationResult ResolveRouteInfluence(
            GameState state,
            int playerId,
            string routeInfluenceSlotId)
        {
            var validation = ValidatePending(state, playerId, SpecialActionPendingSteps.AwaitRouteInfluence);
            if (!validation.IsValid)
            {
                return SpecialActionOperationResult.Failure(validation);
            }

            var legalSlots = optionQuery.GetLegalRouteInfluenceSlotIds(
                state,
                playerId,
                state.PendingSpecialAction.TraversedRouteId);
            if (legalSlots.Count == 0)
            {
                return CompletePending(state, playerId, "经过的航道没有可用槽位，航道影响力放置已跳过。");
            }

            if (string.IsNullOrEmpty(routeInfluenceSlotId) || !legalSlots.Contains(routeInfluenceSlotId))
            {
                return Failure(CommandErrorCode.InvalidTarget, "所选航道影响力槽位当前不可用。");
            }

            var placement = influenceService.Place(state, playerId, routeInfluenceSlotId);
            if (!placement.Succeeded)
            {
                return SpecialActionOperationResult.Failure(placement.Validation);
            }

            state.PendingSpecialAction.ResolvedTargetIds.Add(routeInfluenceSlotId);
            return CompletePending(state, playerId, "复合动力系统在经过的航道放置了 1 个影响力。");
        }

        private SpecialActionOperationResult AdvanceAfterSettledMove(GameState state, int playerId)
        {
            var pending = state.PendingSpecialAction;
            var definition = SpecialActionDatabase.Get(pending.SpecialActionId);
            if (definition.EffectKind == SpecialActionEffectKind.CompositePowerMove)
            {
                var legalRouteSlots = optionQuery.GetLegalRouteInfluenceSlotIds(state, playerId, pending.TraversedRouteId);
                if (legalRouteSlots.Count == 0)
                {
                    return CompletePending(state, playerId, "免费移动已完成；经过的航道无法放置影响力，该步骤已跳过。");
                }

                pending.Step = SpecialActionPendingSteps.AwaitRouteInfluence;
                return CurrentSuccess(state, false, "免费移动及事件已完成，请在经过的航道放置 1 个影响力。");
            }

            if (definition.EffectKind == SpecialActionEffectKind.ConsecutiveFreeMoves &&
                pending.RemainingRepetitions > 0 &&
                optionQuery.GetLegalFreeMoveTargetIds(state, playerId).Count > 0)
            {
                pending.Step = SpecialActionPendingSteps.AwaitFreeMoveTarget;
                return CurrentSuccess(state, false, "本段移动及事件已完成，请选择下一段免费移动。");
            }

            return CompletePending(
                state,
                playerId,
                pending.RemainingRepetitions > 0
                    ? "本段移动已完成；当前没有下一段合法目标，剩余免费移动已跳过。"
                    : "连续免费移动已完成。");
        }

        private SpecialActionOperationResult CompleteWithoutPending(
            GameState state,
            int playerId,
            SpecialActionDefinition definition,
            string summary)
        {
            mainActionBudgetService.SpendCompletedMainAction(state, playerId);
            return SpecialActionOperationResult.Success(definition, true, summary);
        }

        private SpecialActionOperationResult CompletePending(GameState state, int playerId, string summary)
        {
            var definition = SpecialActionDatabase.Get(state.PendingSpecialAction.SpecialActionId);
            state.PendingSpecialAction = null;
            mainActionBudgetService.SpendCompletedMainAction(state, playerId);
            return SpecialActionOperationResult.Success(definition, true, summary);
        }

        private static PendingSpecialActionState CreatePending(
            int playerId,
            SpecialActionDefinition definition,
            string declarationMarkerId,
            string sourceCommandId,
            string step,
            int remainingRepetitions)
        {
            return new PendingSpecialActionState
            {
                SessionId = Guid.NewGuid().ToString("N"),
                PlayerId = playerId,
                SpecialActionId = definition.SpecialActionId,
                DeclarationMarkerId = declarationMarkerId,
                SourceCommandId = sourceCommandId ?? string.Empty,
                Step = step,
                RemainingRepetitions = remainingRepetitions
            };
        }

        private static void MarkUsedThisRound(PlayerState player, string specialActionId)
        {
            if (player.UsedSpecialActionIdsThisRound == null)
            {
                player.UsedSpecialActionIdsThisRound = new List<string>();
            }

            if (!player.UsedSpecialActionIdsThisRound.Contains(specialActionId))
            {
                player.UsedSpecialActionIdsThisRound.Add(specialActionId);
            }
        }

        private static ValidationResult ValidatePending(GameState state, int playerId, string expectedStep)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var pending = state.PendingSpecialAction;
            if (pending == null || !pending.IsValid())
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "当前没有待处理的特殊行动会话。");
            }

            if (pending.PlayerId != playerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "只能由发动特殊行动的玩家继续结算。");
            }

            if (pending.Step != expectedStep)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "当前特殊行动不接受该类选择。");
            }

            return ValidationResult.Success;
        }

        private static SpecialActionOperationResult CurrentSuccess(GameState state, bool completed, string summary)
        {
            var definition = state.PendingSpecialAction == null
                ? null
                : SpecialActionDatabase.Get(state.PendingSpecialAction.SpecialActionId);
            return SpecialActionOperationResult.Success(definition, completed, summary);
        }

        private static SpecialActionOperationResult Failure(CommandErrorCode errorCode, string reason)
        {
            return SpecialActionOperationResult.Failure(ValidationResult.Failure(errorCode, reason));
        }
    }
}
