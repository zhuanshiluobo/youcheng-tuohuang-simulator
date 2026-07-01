using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public static class CardFlowStateAdapter
    {
        public static CardFlowContext BuildContext(
            IEventCardScenario scenario,
            string poolId,
            EventCardDefinition card,
            CardFlowStartRequest request)
        {
            return new CardFlowContext
            {
                SessionId = Guid.NewGuid().ToString("N"),
                ScenarioId = scenario.ScenarioId,
                ChoiceType = scenario.ChoiceType,
                PoolId = poolId ?? string.Empty,
                CardId = card != null ? card.CardId : string.Empty,
                PlayerId = request != null ? request.PlayerId : 0,
                TargetId = request != null ? request.TargetId ?? string.Empty : string.Empty,
                SourceCommandId = request != null ? request.SourceCommandId ?? string.Empty : string.Empty,
                ContextData = CardFlowArgumentUtility.Clone(request != null ? request.Arguments : null)
            };
        }

        public static CardFlowContext BuildContext(PendingCardSessionState session)
        {
            if (session == null)
            {
                return null;
            }

            return new CardFlowContext
            {
                SessionId = session.SessionId ?? string.Empty,
                ScenarioId = session.ScenarioId ?? string.Empty,
                ChoiceType = session.ChoiceType ?? string.Empty,
                PoolId = session.PoolId ?? string.Empty,
                CardId = session.CardId ?? string.Empty,
                PlayerId = session.PlayerId,
                TargetId = session.TargetId ?? string.Empty,
                SourceCommandId = session.SourceCommandId ?? string.Empty,
                ContextData = CardFlowArgumentUtility.Clone(session.ContextData)
            };
        }

        public static PendingCardChoiceView GetPendingChoiceView(GameState state)
        {
            if (state == null)
            {
                return null;
            }

            var session = state.PendingCardSession;
            if (session != null && session.IsValid())
            {
                return new PendingCardChoiceView
                {
                    SessionId = session.SessionId ?? string.Empty,
                    ScenarioId = session.ScenarioId ?? string.Empty,
                    ChoiceType = session.ChoiceType ?? string.Empty,
                    CardId = session.CardId ?? string.Empty,
                    PlayerId = session.PlayerId,
                    TargetId = session.TargetId ?? string.Empty,
                    OptionIds = CloneOptionIds(session.OptionIds),
                    SourceCommandId = session.SourceCommandId ?? string.Empty,
                    IsFromPendingCardSession = true
                };
            }

            var choice = state.PendingChoice;
            if (choice != null && choice.IsValid())
            {
                return new PendingCardChoiceView
                {
                    ChoiceType = choice.ChoiceType ?? string.Empty,
                    CardId = choice.CardId ?? string.Empty,
                    PlayerId = choice.PlayerId,
                    TargetId = choice.TargetId ?? string.Empty,
                    OptionIds = CloneOptionIds(choice.OptionIds),
                    SourceCommandId = choice.SourceCommandId ?? string.Empty,
                    IsFromPendingCardSession = false
                };
            }

            return null;
        }

        public static void OpenPendingSession(GameState state, CardFlowContext context, EventCardDefinition card)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var optionIds = new List<string>();
            if (card != null && card.ChoiceRewards != null)
            {
                for (var i = 0; i < card.ChoiceRewards.Count; i++)
                {
                    optionIds.Add(i.ToString());
                }
            }

            state.PendingCardSession = new PendingCardSessionState
            {
                SessionId = context.SessionId,
                ScenarioId = context.ScenarioId,
                ChoiceType = context.ChoiceType,
                PoolId = context.PoolId,
                CardId = context.CardId,
                PlayerId = context.PlayerId,
                TargetId = context.TargetId,
                OptionIds = optionIds,
                SourceCommandId = context.SourceCommandId,
                ContextData = CardFlowArgumentUtility.Clone(context.ContextData)
            };

            state.PendingChoice = new PendingChoiceState
            {
                ChoiceId = context.ChoiceType + ":" + context.CardId,
                PlayerId = context.PlayerId,
                ChoiceType = context.ChoiceType,
                CardId = context.CardId,
                TargetId = context.TargetId,
                OptionIds = optionIds,
                SourceCommandId = context.SourceCommandId
            };
        }

        public static void ClearPendingSession(GameState state)
        {
            if (state == null)
            {
                return;
            }

            state.PendingCardSession = null;
            state.PendingChoice = null;
        }

        private static List<string> CloneOptionIds(IReadOnlyList<string> optionIds)
        {
            var result = new List<string>();
            if (optionIds == null)
            {
                return result;
            }

            for (var i = 0; i < optionIds.Count; i++)
            {
                result.Add(optionIds[i] ?? string.Empty);
            }

            return result;
        }
    }

    public sealed class PendingCardChoiceView
    {
        public string SessionId = string.Empty;
        public string ScenarioId = string.Empty;
        public string ChoiceType = string.Empty;
        public string CardId = string.Empty;
        public int PlayerId;
        public string TargetId = string.Empty;
        public List<string> OptionIds = new List<string>();
        public string SourceCommandId = string.Empty;
        public bool IsFromPendingCardSession;
    }
}
