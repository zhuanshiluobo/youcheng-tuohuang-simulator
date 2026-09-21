using System;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    /// <summary>
    /// 旧回合 API 的兼容适配器。所有推进都委托给 RoundExecutionService；本类型不再判断
    /// 阶段、玩家顺序或自行写入任何兼容字段。
    /// </summary>
    public sealed class RoundAdvanceService
    {
        private readonly RoundExecutionService roundExecutionService;

        public RoundAdvanceService()
            : this(new RoundExecutionService())
        {
        }

        public RoundAdvanceService(TurnOrderService turnOrderService)
            : this(new RoundExecutionService(
                turnOrderService,
                new CharacterCardService(turnOrderService),
                new MainActionBudgetService(),
                new SpecialActionLifecycleService()))
        {
        }

        public RoundAdvanceService(TurnOrderService turnOrderService, CharacterCardService characterCardService)
            : this(new RoundExecutionService(
                turnOrderService,
                characterCardService,
                new MainActionBudgetService(),
                new SpecialActionLifecycleService()))
        {
        }

        public RoundAdvanceService(
            TurnOrderService turnOrderService,
            CharacterCardService characterCardService,
            MainActionBudgetService mainActionBudgetService)
            : this(new RoundExecutionService(
                turnOrderService,
                characterCardService,
                mainActionBudgetService,
                new SpecialActionLifecycleService()))
        {
        }

        public RoundAdvanceService(
            TurnOrderService turnOrderService,
            CharacterCardService characterCardService,
            MainActionBudgetService mainActionBudgetService,
            SpecialActionLifecycleService specialActionLifecycleService)
            : this(new RoundExecutionService(
                turnOrderService,
                characterCardService,
                mainActionBudgetService,
                specialActionLifecycleService))
        {
        }

        public RoundAdvanceService(RoundExecutionService roundExecutionService)
        {
            this.roundExecutionService = roundExecutionService ??
                throw new ArgumentNullException(nameof(roundExecutionService));
        }

        public RoundExecutionService Execution
        {
            get { return roundExecutionService; }
        }

        public void CompleteMainAction(GameState state, int playerId)
        {
            roundExecutionService.CompleteMainAction(state, playerId);
        }

        public void MarkMainActionComplete(GameState state, int playerId)
        {
            roundExecutionService.MarkMainActionComplete(state, playerId);
        }

        public ValidationResult EndCompletedAction(GameState state, int playerId)
        {
            return roundExecutionService.EndCurrentPlayerWindow(state, playerId);
        }

        public void ResetActionFlags(GameState state)
        {
            roundExecutionService.ResetActionFlags(state);
        }

        public bool AllPlayersCollectedResources(GameState state)
        {
            return roundExecutionService.AllPlayersCollectedResources(state);
        }

        public void AdvanceResourceCollectionToCleanup(GameState state)
        {
            roundExecutionService.AdvanceResourceCollectionToCleanup(state);
        }

        public ValidationResult CompleteResourceCollection(GameState state, int playerId)
        {
            return roundExecutionService.CompleteResourceCollection(state, playerId);
        }
    }
}
