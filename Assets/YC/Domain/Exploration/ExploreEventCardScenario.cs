using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Exploration
{
    internal sealed class ExploreEventCardScenario : IEventCardScenario
    {
        public const string PathLocationIdsArgument = "pathLocationIds";
        public const string RouteIdsArgument = "routeIds";
        public const string InfluenceSlotIdArgument = "influenceSlotId";
        public const string EventInfluenceSlotIdsArgument = "eventInfluenceSlotIds";
        public const string PaymentRecipientsArgument = "paymentRecipients";
        public const string AllowFacilityEntryArgument = "allowFacilityEntry";

        private readonly ExplorationService explorationService;

        public ExploreEventCardScenario(ExplorationService explorationService)
        {
            this.explorationService = explorationService;
        }

        public string ScenarioId
        {
            get { return CardFlowScenarioIds.ExplorationEvent; }
        }

        public string ChoiceType
        {
            get { return CardFlowChoiceTypes.ExploreEvent; }
        }

        public string ResolvePoolId(GameState state, CardFlowStartRequest request)
        {
            var eventColor = explorationService.GetEventColor(request.TargetId);
            return EventCardPoolIds.FromColor(eventColor);
        }

        public ValidationResult ValidateStart(GameState state, CardFlowStartRequest request, EventCardDefinition previewCard)
        {
            return explorationService.CanExplore(
                state,
                request.PlayerId,
                request.TargetId,
                DecodePath(request.Arguments),
                -1,
                CardFlowArgumentUtility.GetValue(request.Arguments, InfluenceSlotIdArgument),
                DecodePaymentRecipients(request.Arguments),
                null,
                false,
                string.Equals(
                    CardFlowArgumentUtility.GetValue(request.Arguments, AllowFacilityEntryArgument),
                    bool.TrueString,
                    System.StringComparison.OrdinalIgnoreCase));
        }

        public ValidationResult OnCardRevealed(GameState state, CardFlowContext context, EventCardDefinition card)
        {
            return explorationService.RevealExploreCard(
                state,
                context.PlayerId,
                context.TargetId,
                DecodePath(context.ContextData),
                DecodePaymentRecipients(context.ContextData),
                card);
        }

        public ValidationResult ValidateOption(
            GameState state,
            CardFlowContext context,
            EventCardDefinition card,
            int optionIndex,
            IReadOnlyList<StringKeyValuePair> arguments)
        {
            var mergedArguments = MergeArguments(context.ContextData, arguments);
            if (state.PendingCardSession != null)
            {
                return explorationService.ValidateResolvedExploreOption(
                    state,
                    context.PlayerId,
                    context.TargetId,
                    card,
                    optionIndex,
                    CardFlowArgumentUtility.GetValue(mergedArguments, InfluenceSlotIdArgument),
                    DecodeEventInfluenceSlotIds(mergedArguments));
            }

            return explorationService.CanExplore(
                state,
                context.PlayerId,
                context.TargetId,
                DecodePath(mergedArguments),
                optionIndex,
                CardFlowArgumentUtility.GetValue(mergedArguments, InfluenceSlotIdArgument),
                DecodePaymentRecipients(mergedArguments),
                DecodeEventInfluenceSlotIds(mergedArguments));
        }

        public CardFlowApplyResult ApplyOption(
            GameState state,
            CardFlowContext context,
            EventCardDefinition card,
            int optionIndex,
            IReadOnlyList<StringKeyValuePair> arguments)
        {
            var mergedArguments = MergeArguments(context.ContextData, arguments);
            var result = explorationService.ApplyExploreOption(
                state,
                context.PlayerId,
                context.TargetId,
                card,
                optionIndex,
                CardFlowArgumentUtility.GetValue(mergedArguments, InfluenceSlotIdArgument),
                DecodeEventInfluenceSlotIds(mergedArguments));

            return new CardFlowApplyResult
            {
                Validation = result.Validation,
                Reward = result.Reward,
                PrimaryInfluencePlacement = result.InfluencePlacement
            };
        }

        private static MapPath DecodePath(IReadOnlyList<StringKeyValuePair> arguments)
        {
            return new MapPath
            {
                LocationIds = SplitIds(CardFlowArgumentUtility.GetValue(arguments, PathLocationIdsArgument)),
                RouteIds = SplitIds(CardFlowArgumentUtility.GetValue(arguments, RouteIdsArgument))
            };
        }

        private static Dictionary<string, int> DecodePaymentRecipients(IReadOnlyList<StringKeyValuePair> arguments)
        {
            var result = new Dictionary<string, int>();
            var encoded = CardFlowArgumentUtility.GetValue(arguments, PaymentRecipientsArgument);
            if (string.IsNullOrEmpty(encoded))
            {
                return result;
            }

            var entries = encoded.Split(new[] { ';', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < entries.Length; i++)
            {
                var parts = entries[i].Split(new[] { '=', ':' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    continue;
                }

                int playerId;
                if (int.TryParse(parts[1].Trim(), out playerId))
                {
                    result[parts[0].Trim()] = playerId;
                }
            }

            return result;
        }

        private static List<string> DecodeEventInfluenceSlotIds(IReadOnlyList<StringKeyValuePair> arguments)
        {
            return SplitIds(CardFlowArgumentUtility.GetValue(arguments, EventInfluenceSlotIdsArgument));
        }

        private static List<StringKeyValuePair> MergeArguments(
            IReadOnlyList<StringKeyValuePair> baseArguments,
            IReadOnlyList<StringKeyValuePair> overrideArguments)
        {
            var merged = CardFlowArgumentUtility.Clone(baseArguments);
            if (overrideArguments == null)
            {
                return merged;
            }

            for (var i = 0; i < overrideArguments.Count; i++)
            {
                var pair = overrideArguments[i];
                if (pair == null)
                {
                    continue;
                }

                CardFlowArgumentUtility.SetValue(merged, pair.Key, pair.Value);
            }

            return merged;
        }

        private static List<string> SplitIds(string encoded)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(encoded))
            {
                return result;
            }

            var parts = encoded.Split(new[] { ',', ';', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                result.Add(parts[i].Trim());
            }

            return result;
        }
    }
}
