using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Application.Interactions
{
    public static class SpecialActionChoiceCommand
    {
        public const string Intent = "special_action_choice";
        public static GameCommand Create(int player, int revision, string hash) => new GameCommand { Kind = GameCommandKind.InvokeSettlementOption, PlayerId = player,
            Parameters = { ["intent"] = Intent, ["expectedRevision"] = revision.ToString(CultureInfo.InvariantCulture), ["contentHash"] = hash } };
        public static CommandResult Handle(GameState state, EffectRegistry registry, GameCommand command, RoundExecutionService rounds)
        {
            string P(string key) => InvokeSettlementOptionCommandHandler.Parameter(command, key);
            if (command.Parameters.Keys.Any(k => !new[] { "intent", "expectedRevision", "contentHash" }.Contains(k)) ||
                !int.TryParse(P("expectedRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out int revision) || revision < 0 ||
                P("contentHash") != registry.ContentPackHash || string.IsNullOrWhiteSpace(command.CommandId)) return Invalid("special_choice_request_invalid");
            var args = Object("commandId", NormalizedValue.CreateString(command.CommandId), "contentHash", NormalizedValue.CreateString(P("contentHash")),
                "expectedRevision", NormalizedValue.CreateInteger(revision));
            var old = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectTypeId == OrdinarySpecialActionChoice.TypeId && Text(n.NormalizedArguments, "commandId") == command.CommandId);
            var executor = rounds?.CreateCommandExecutor(state) ?? new EffectTreeExecutor(state, registry);
            if (old != null)
            {
                if (old.PlayerId != command.PlayerId || old.NormalizedArguments.ToDeterministicString() != args.ToDeterministicString()) return Invalid("special_choice_command_conflict");
            }
            else
            {
                var phase = CommandPhasePolicy.ValidatePhase(state, GameCommandKind.UseSpecialAction); if (!phase.IsValid) return CommandResult.Invalid(phase);
                if (revision != state.EffectRuntime.StateRevision || !registry.QuerySpecialActionSources(state, command.PlayerId, false).Any(o => o.Enabled)) return Invalid("special_choice_request_expired");
                if (!executor.TryCreatePlayerActionEffect(command.PlayerId, EffectSpec.Create(OrdinarySpecialActionChoice.TypeId, args, command.PlayerId), "", command.CommandId, out _)) return Invalid(executor.LastDiagnostic);
            }
            var report = executor.RunUntilQuiescent();
            if (report.Faulted) return AbilityActionCommand.Paused(state);
            if (rounds != null) { var advanced = rounds.Advance(state); if (!advanced.IsValid) return CommandResult.Invalid(advanced); }
            return CommandResult.SuccessResult(new List<GameEvent>(), "特殊行动选择已受理。");
        }
        private static CommandResult Invalid(string reason) => CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, reason));
    }
}
