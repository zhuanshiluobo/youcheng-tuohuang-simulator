using System;
using System.Globalization;
using System.Linq;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class ResourceLossEffectExecutor
    {
        public const string TypeId = "effect.resource.lose";
        public const string DefinitionVersion = "1.0.0";

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (!registry.TryGet(TypeId, out _))
                registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.Atomic, DefinitionVersion)
                { Validator = spec => ValidateArguments(spec?.NormalizedArguments) });
        }

        public static string ValidateArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || !args.IsValid() ||
                args.Properties.Any(entry => !new[] { "apiVersion", "player", "targetPlayer", "resourceType", "amount" }.Contains(entry.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Number(args, "apiVersion") != 1 ||
                !TryReadPlayer(Value(args, "player"), out _) || !TryReadPlayer(Value(args, "targetPlayer"), out _) ||
                !TryReadResourceType(Value(args, "resourceType"), out _) ||
                Value(args, "amount")?.Kind != NormalizedValueKind.Integer ||
                Value(args, "amount").IntegerValue < 0 || Value(args, "amount").IntegerValue > int.MaxValue)
                return "resource_loss_arguments_invalid";
            return string.Empty;
        }

        public static bool TryReadPlayer(NormalizedValue value, out int playerId)
        {
            playerId = -1;
            if (value?.Kind == NormalizedValueKind.Integer)
            {
                if (value.IntegerValue < 1 || value.IntegerValue > int.MaxValue) return false;
                playerId = (int)value.IntegerValue;
                return true;
            }
            string key = value?.Kind == NormalizedValueKind.String ? value.StringValue :
                value?.Kind == NormalizedValueKind.StableReference && value.ReferenceType == "player" ? value.ReferenceId : null;
            if (key == null) return false;
            if (key.StartsWith("p", StringComparison.OrdinalIgnoreCase)) key = key.Substring(1);
            return int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out playerId) && playerId > 0;
        }

        public static bool TryReadResourceType(NormalizedValue value, out ResourceType resourceType)
        {
            resourceType = ResourceType.Originium;
            if (value?.Kind != NormalizedValueKind.String) return false;
            string key = value.StringValue.Replace("-", string.Empty).Replace("_", string.Empty);
            foreach (ResourceType candidate in Enum.GetValues(typeof(ResourceType)))
                if (string.Equals(key, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
                { resourceType = candidate; return true; }
            return false;
        }

        public static int AvailableAmount(GameState state, int playerId, ResourceType resourceType)
        {
            // 托管金券已离开可用钱包；只读取Available，不动托管收据或Owned总额。
            return resourceType == ResourceType.GoldVoucher
                ? RouteTollEscrowService.QueryBalance(state, playerId).Available
                : state.FindPlayer(playerId).Resources.Get(resourceType);
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            if (context?.Node == null || context.State == null) return EffectStepResult.Failed("resource_loss_context_invalid");
            if (context.Node.PendingOutcome == EffectPendingOutcome.Completed)
                return EffectStepResult.Completed(context.Node.NormalizedResult.Clone());
            var args = context.Node.NormalizedArguments;
            string reason = ValidateArguments(args);
            if (reason.Length > 0) return EffectStepResult.Failed(reason);
            TryReadPlayer(Value(args, "player"), out int playerId);
            TryReadPlayer(Value(args, "targetPlayer"), out int targetPlayerId);
            TryReadResourceType(Value(args, "resourceType"), out var resourceType);
            if (playerId != context.Node.PlayerId || context.State.FindPlayer(playerId) == null ||
                context.State.FindPlayer(targetPlayerId)?.Resources == null)
                return EffectStepResult.Failed("resource_loss_player_invalid");
            int available = AvailableAmount(context.State, targetPlayerId, resourceType);
            if (available < 0) return EffectStepResult.Failed("resource_loss_balance_invalid");
            int requested = (int)Value(args, "amount").IntegerValue;
            int amount = Math.Min(requested, available);
            context.State.FindPlayer(targetPlayerId).Resources.Set(resourceType, available - amount);
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(amount > 0),
                "requestedAmount", NormalizedValue.CreateInteger(requested), "amount", NormalizedValue.CreateInteger(amount),
                "targetPlayer", NormalizedValue.CreateInteger(targetPlayerId), "resourceType", NormalizedValue.CreateString(resourceType.ToString())));
        }
    }
}
