using System;
using System.Linq;
using YC.Domain.State;
using YC.Domain.Rules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    /// <summary>Host 掷骰；点数与节点完成事实在同一规则提交内保存。</summary>
    public static class DiceRollEffectExecutor
    {
        public static string ValidateArguments(NormalizedValue args) => args?.Kind == NormalizedValueKind.Object &&
            args.Properties.All(p => new[] { "kind", "effectTypeId", "apiVersion", "player", "count", "sides" }.Contains(p.Name)) &&
            Value(args,"apiVersion")?.Kind == NormalizedValueKind.Integer && Value(args,"count")?.Kind == NormalizedValueKind.Integer && Value(args,"sides")?.Kind == NormalizedValueKind.Integer && Number(args, "apiVersion") == 1 && Number(args, "count") == 1 && Number(args, "sides") == 6 &&
            YC.Domain.Cards.CharacterLifecycleService.ParsePlayer(Text(args, "player")) > 0 ? "" : "unsupported_dice_spec";

        public static void Register(EffectRegistry registry, Func<int, int> next = null)
        {
            if (registry.TryGet(LuaDomainEffectTypeIds.RollDice, out _)) return;
            var hostNext = next;
            registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.RollDice, context =>
            {
                string error = ValidateArguments(context.Node.NormalizedArguments);
                if (error.Length > 0) return EffectStepResult.Failed(error);
                if (Text(context.Node.NormalizedArguments, "player") != context.Node.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)) return EffectStepResult.Failed("invalid_player");
                int value = Number(context.Node.ContinuationData, "diceResult");
                if (Value(context.Node.ContinuationData, "diceResult") == null)
                {
                    value = checked((hostNext == null ? GameplayRandomRuntime.Next(context.State, GameplayRandomRuntime.Dice, 6) : hostNext(6)) + 1);
                    if (value < 1 || value > 6) throw new InvalidOperationException("host_dice_result_invalid");
                    Set(context.Node, "diceResult", NormalizedValue.CreateInteger(value));
                }
                if (value < 1 || value > 6) throw new InvalidOperationException("dice_receipt_invalid");
                return EffectStepResult.Completed(Object("diceResult", NormalizedValue.CreateInteger(value)));
            }, EffectExecutorKind.Atomic, "v1") { Validator = spec => ValidateArguments(spec.NormalizedArguments) });
        }
        public static bool ValidateState(GameState state) => state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice).All(n =>
            ValidateArguments(n.NormalizedArguments).Length == 0 && n.FlowStage.Length==0 &&
            n.ContinuationData.Properties.All(p=>p.Name=="diceResult") &&
            (Value(n.ContinuationData,"diceResult")==null || Value(n.ContinuationData,"diceResult").Kind==NormalizedValueKind.Integer && Number(n.ContinuationData,"diceResult")>=1 && Number(n.ContinuationData,"diceResult")<=6) &&
            (n.Status != EffectNodeStatus.Completed || Number(n.ContinuationData, "diceResult") >= 1 && Number(n.ContinuationData, "diceResult") <= 6 &&
                Value(n.NormalizedResult,"diceResult")?.Kind==NormalizedValueKind.Integer && Number(n.NormalizedResult, "diceResult") == Number(n.ContinuationData, "diceResult")));
    }
}
