using System.Collections.Generic;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Application.Interactions
{
    public static class AbilityActionCommand
    {
        public const string Intent = "ability_action";
        public static GameCommand Create(int player, AbilityActionOption option) => new GameCommand { Kind = GameCommandKind.InvokeSettlementOption, PlayerId = player,
            Parameters = { ["intent"] = Intent, ["optionId"] = option.OptionId, ["requestId"] = option.RequestRef.InteractionId,
                ["factsDigest"] = option.RequestRef.FactsDigest, ["contentHash"] = option.VersionRef.ContentPackHash } };
        public static CommandResult Handle(GameState state, EffectRegistry registry, GameCommand command, RoundExecutionService rounds = null)
        {
            string P(string key) => InvokeSettlementOptionCommandHandler.Parameter(command, key);
            var allowed = new[] { "intent", "optionId", "requestId", "factsDigest", "contentHash", "mode", "order", "offerSecondEffect" };
            if (command.Parameters.Keys.Any(key => !allowed.Contains(key))) return Invalid("action_command_unknown_field");
            var argumentFields = new List<NormalizedValueEntry>();
            foreach (string field in new[] { "mode", "order", "offerSecondEffect" })
            {
                if (!command.Parameters.TryGetValue(field, out string value)) continue;
                if (value == null) return Invalid("action_argument_type_invalid");
                if (field == "offerSecondEffect" && value != "true" && value != "false") return Invalid("action_argument_boolean_invalid");
                argumentFields.Add(new NormalizedValueEntry { Name = field, Value = field == "offerSecondEffect" ? NormalizedValue.CreateBoolean(value == "true") : NormalizedValue.CreateString(value) });
            }
            var args = NormalizedValue.CreateObject(argumentFields);
            var phase = CommandPhasePolicy.ValidatePhase(state, command.Kind); if (!phase.IsValid) return CommandResult.Invalid(phase);
            var executor = rounds?.CreateCommandExecutor(state) ?? new EffectTreeExecutor(state, registry);
            if (!executor.TryInvokeAbilityAction(command.PlayerId, P("optionId"), P("requestId"), P("factsDigest"), P("contentHash"), command.CommandId, args, out var ownerId, out string reason)) return Invalid(reason);
            var report = executor.RunUntilQuiescent();
            if (report.Faulted) return Paused(state);
            var owner = executor.GetNode(ownerId);
            if (owner?.Status == EffectNodeStatus.Failed) return CommandResult.SuccessResult(new List<GameEvent>(), AbilityActionReasons.Display(owner.FailureReason));
            if (rounds != null) { var advanced = rounds.Advance(state); if (!advanced.IsValid) return IsNativeFault(state) ? Paused(state) : CommandResult.Invalid(advanced); }
            return CommandResult.SuccessResult(new List<GameEvent>(), "行动已受理。");
        }
        private static CommandResult Invalid(string reason) => CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, reason));
        public static bool IsNativeFault(GameState state) => state.EffectRuntime.Status == EffectRuntimeStatus.PausedFault &&
            (state.EffectRuntime.AbilityActionInvocations.Any(i => i.Phase == "faulted") || state.EffectRuntime.AbilitySourceUses.Count > 0);
        public static CommandResult Paused(GameState state) => CommandResult.SuccessResult(new List<GameEvent>(), "行动结算已暂停：" + state.EffectRuntime.LastFaultMessage);
    }
}
