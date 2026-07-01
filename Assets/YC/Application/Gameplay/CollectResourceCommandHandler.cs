using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Harvest;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class CollectResourceCommandHandler : IGameCommandHandler
    {
        public const string LocationIdsParameter = "locationIds";
        public const string RouteIdsParameter = "routeIds";
        public const string PaymentRecipientsParameter = "paymentRecipients";

        private readonly ResourceCollectionService resourceCollectionService;
        private readonly RoundAdvanceService roundAdvanceService;

        public CollectResourceCommandHandler(ResourceCollectionService resourceCollectionService)
            : this(resourceCollectionService, new RoundAdvanceService())
        {
        }

        public CollectResourceCommandHandler(
            ResourceCollectionService resourceCollectionService,
            RoundAdvanceService roundAdvanceService)
        {
            this.resourceCollectionService = resourceCollectionService ?? throw new ArgumentNullException(nameof(resourceCollectionService));
            this.roundAdvanceService = roundAdvanceService ?? throw new ArgumentNullException(nameof(roundAdvanceService));
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.CollectResource;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var locationIds = ResolveLocationIds(command);
            var routeIds = SplitIds(GetParameter(command, RouteIdsParameter));
            Dictionary<string, int> paymentRecipients;
            var paymentRecipientsValidation = ResolvePaymentRecipients(command, out paymentRecipients);
            if (!paymentRecipientsValidation.IsValid)
            {
                return CommandResult.Invalid(paymentRecipientsValidation);
            }

            var result = resourceCollectionService.Collect(
                state,
                command.PlayerId,
                locationIds,
                routeIds,
                paymentRecipients);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            if (roundAdvanceService.AllPlayersCollectedResources(state))
            {
                roundAdvanceService.AdvanceResourceCollectionToCleanup(state);
            }

            var message = "Player " + command.PlayerId + " collected resources.";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.ResourceChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = string.Join(",", result.LocationIds.ToArray()),
                    Message = message,
                    Data =
                    {
                        { "locationIds", string.Join(",", result.LocationIds.ToArray()) },
                        { "paymentCount", result.Payments.Count.ToString() },
                        { "rewardGoldVoucher", result.Reward.GoldVoucher.ToString() },
                        { "rewardOriginium", result.Reward.Originium.ToString() },
                        { "rewardOriginiumShard", result.Reward.OriginiumShard.ToString() },
                        { "rewardIron", result.Reward.Iron.ToString() },
                        { "rewardPureOriginium", result.Reward.PureOriginium.ToString() }
                    }
                }
            }, message);
        }

        private static List<string> ResolveLocationIds(GameCommand command)
        {
            var result = SplitIds(GetParameter(command, LocationIdsParameter));
            if (!string.IsNullOrEmpty(command.TargetId))
            {
                result.Add(command.TargetId.Trim());
            }

            if (command.OptionIds != null)
            {
                for (var i = 0; i < command.OptionIds.Count; i++)
                {
                    if (!string.IsNullOrEmpty(command.OptionIds[i]))
                    {
                        result.Add(command.OptionIds[i].Trim());
                    }
                }
            }

            return result;
        }

        private static ValidationResult ResolvePaymentRecipients(
            GameCommand command,
            out Dictionary<string, int> paymentRecipients)
        {
            paymentRecipients = new Dictionary<string, int>();
            var encoded = GetParameter(command, PaymentRecipientsParameter);
            if (!string.IsNullOrEmpty(encoded))
            {
                var entries = encoded.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < entries.Length; i++)
                {
                    var parts = entries[i].Split(new[] { '=', ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || string.IsNullOrEmpty(parts[0].Trim()))
                    {
                        return InvalidPaymentRecipients();
                    }

                    int playerId;
                    if (!int.TryParse(parts[1], out playerId))
                    {
                        return InvalidPaymentRecipients();
                    }

                    paymentRecipients[parts[0].Trim()] = playerId;
                }
            }

            if (command.Parameters != null)
            {
                foreach (var entry in command.Parameters)
                {
                    if (!entry.Key.StartsWith("payment:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int playerId;
                    var routeId = entry.Key.Substring("payment:".Length).Trim();
                    if (string.IsNullOrEmpty(routeId) || !int.TryParse(entry.Value, out playerId))
                    {
                        return InvalidPaymentRecipients();
                    }

                    paymentRecipients[routeId] = playerId;
                }
            }

            return ValidationResult.Success;
        }

        private static List<string> SplitIds(string encoded)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(encoded))
            {
                return result;
            }

            var parts = encoded.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                result.Add(parts[i].Trim());
            }

            return result;
        }

        private static string GetParameter(GameCommand command, string key)
        {
            if (command.Parameters == null)
            {
                return string.Empty;
            }

            string value;
            return command.Parameters.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static ValidationResult InvalidPaymentRecipients()
        {
            return ValidationResult.Failure(
                CommandErrorCode.InvalidTarget,
                "路费接收方参数格式不正确。");
        }
    }
}
