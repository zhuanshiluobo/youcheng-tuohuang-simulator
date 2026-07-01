using System;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public sealed class CardFlowService
    {
        private readonly ICardPoolService cardPoolService;

        public CardFlowService() : this(new CardPoolService())
        {
        }

        public CardFlowService(ICardPoolService cardPoolService)
        {
            this.cardPoolService = cardPoolService ?? throw new ArgumentNullException(nameof(cardPoolService));
        }

        public CardFlowStartResult StartPendingChoice(
            GameState state,
            CardFlowStartRequest request,
            IEventCardScenario scenario)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            var preview = Preview(state, request, scenario);
            if (!preview.Validation.IsValid)
            {
                return FailureStart(preview.Validation);
            }

            var cardId = cardPoolService.Draw(state.Decks, preview.PoolId);
            var card = EventCardDatabase.Get(cardId);
            var context = CardFlowStateAdapter.BuildContext(scenario, preview.PoolId, card, request);
            var revealValidation = scenario.OnCardRevealed(state, context, card);
            if (!revealValidation.IsValid)
            {
                return FailureStart(revealValidation);
            }

            CardFlowStateAdapter.OpenPendingSession(state, context, card);
            return new CardFlowStartResult
            {
                Succeeded = true,
                Validation = ValidationResult.Success,
                Context = context,
                Card = card
            };
        }

        public CardFlowResolveResult ExecuteImmediate(
            GameState state,
            CardFlowExecuteRequest request,
            IEventCardScenario scenario)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            var startRequest = new CardFlowStartRequest
            {
                PlayerId = request.PlayerId,
                TargetId = request.TargetId,
                SourceCommandId = request.SourceCommandId,
                Arguments = CardFlowArgumentUtility.Clone(request.Arguments)
            };

            var preview = Preview(state, startRequest, scenario);
            if (!preview.Validation.IsValid)
            {
                return FailureResolve(preview.Validation);
            }

            var optionValidation = ValidateOptionCore(
                state,
                scenario,
                new CardFlowContext
                {
                    ScenarioId = scenario.ScenarioId,
                    ChoiceType = scenario.ChoiceType,
                    PoolId = preview.PoolId,
                    CardId = preview.Card.CardId,
                    PlayerId = request.PlayerId,
                    TargetId = request.TargetId,
                    SourceCommandId = request.SourceCommandId,
                    ContextData = CardFlowArgumentUtility.Clone(request.Arguments)
                },
                preview.Card,
                request.OptionIndex,
                request.Arguments);
            if (!optionValidation.IsValid)
            {
                return FailureResolve(optionValidation);
            }

            var cardId = cardPoolService.Draw(state.Decks, preview.PoolId);
            var card = EventCardDatabase.Get(cardId);
            var context = CardFlowStateAdapter.BuildContext(scenario, preview.PoolId, card, startRequest);
            var revealValidation = scenario.OnCardRevealed(state, context, card);
            if (!revealValidation.IsValid)
            {
                return FailureResolve(revealValidation);
            }

            var applyResult = scenario.ApplyOption(state, context, card, request.OptionIndex, request.Arguments);
            if (applyResult == null || !applyResult.Validation.IsValid)
            {
                return FailureResolve(applyResult != null
                    ? applyResult.Validation
                    : ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Card flow apply failed."));
            }

            return new CardFlowResolveResult
            {
                Succeeded = true,
                Validation = ValidationResult.Success,
                Context = context,
                Card = card,
                OptionIndex = request.OptionIndex,
                Reward = applyResult.Reward,
                PrimaryInfluencePlacement = applyResult.PrimaryInfluencePlacement
            };
        }

        public CardFlowResolveResult ResolvePendingChoice(
            GameState state,
            CardFlowResolveRequest request,
            IEventCardScenario scenario)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            var session = state.PendingCardSession;
            if (session == null || !session.IsValid() || session.ScenarioId != scenario.ScenarioId)
            {
                return FailureResolve(ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "No pending card choice is available."));
            }

            if (session.PlayerId != request.PlayerId)
            {
                return FailureResolve(ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "Only the active player can resolve the pending card choice."));
            }

            if (!string.IsNullOrEmpty(request.SessionId) && request.SessionId != session.SessionId)
            {
                return FailureResolve(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Pending card session does not match."));
            }

            var context = CardFlowStateAdapter.BuildContext(session);
            var card = EventCardDatabase.Get(session.CardId);
            if (card == null)
            {
                return FailureResolve(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Pending card does not exist."));
            }

            var optionValidation = ValidateOptionCore(
                state,
                scenario,
                context,
                card,
                request.OptionIndex,
                request.Arguments);
            if (!optionValidation.IsValid)
            {
                return FailureResolve(optionValidation);
            }

            var applyResult = scenario.ApplyOption(state, context, card, request.OptionIndex, request.Arguments);
            if (applyResult == null || !applyResult.Validation.IsValid)
            {
                return FailureResolve(applyResult != null
                    ? applyResult.Validation
                    : ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Card flow apply failed."));
            }

            CardFlowStateAdapter.ClearPendingSession(state);
            return new CardFlowResolveResult
            {
                Succeeded = true,
                Validation = ValidationResult.Success,
                Context = context,
                Card = card,
                OptionIndex = request.OptionIndex,
                Reward = applyResult.Reward,
                PrimaryInfluencePlacement = applyResult.PrimaryInfluencePlacement
            };
        }

        private PreviewResult Preview(GameState state, CardFlowStartRequest request, IEventCardScenario scenario)
        {
            var poolId = scenario.ResolvePoolId(state, request);
            if (string.IsNullOrEmpty(poolId))
            {
                return new PreviewResult
                {
                    Validation = ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Card flow pool could not be resolved.")
                };
            }

            if (cardPoolService.RemainingCount(state.Decks, poolId) <= 0)
            {
                return new PreviewResult
                {
                    Validation = ValidationResult.Failure(CommandErrorCode.InvalidTarget, "No cards remain in the selected card pool.")
                };
            }

            var cardId = cardPoolService.Peek(state.Decks, poolId);
            var card = EventCardDatabase.Get(cardId);
            if (card == null)
            {
                return new PreviewResult
                {
                    Validation = ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Card data does not exist.")
                };
            }

            if (card.ChoiceRewards == null || card.ChoiceRewards.Count <= 0)
            {
                return new PreviewResult
                {
                    Validation = ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Card has no available options.")
                };
            }

            var validation = scenario.ValidateStart(state, request, card);
            return new PreviewResult
            {
                Validation = validation,
                PoolId = poolId,
                Card = card
            };
        }

        private static ValidationResult ValidateOptionCore(
            GameState state,
            IEventCardScenario scenario,
            CardFlowContext context,
            EventCardDefinition card,
            int optionIndex,
            System.Collections.Generic.IReadOnlyList<StringKeyValuePair> arguments)
        {
            if (card == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Card does not exist.");
            }

            if (optionIndex < 0 || optionIndex >= card.ChoiceRewards.Count)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Card option is invalid.");
            }

            return scenario.ValidateOption(state, context, card, optionIndex, arguments);
        }

        private static CardFlowStartResult FailureStart(ValidationResult validation)
        {
            return new CardFlowStartResult
            {
                Succeeded = false,
                Validation = validation
            };
        }

        private static CardFlowResolveResult FailureResolve(ValidationResult validation)
        {
            return new CardFlowResolveResult
            {
                Succeeded = false,
                Validation = validation
            };
        }

        private sealed class PreviewResult
        {
            public ValidationResult Validation;
            public string PoolId = string.Empty;
            public EventCardDefinition Card;
        }
    }
}
