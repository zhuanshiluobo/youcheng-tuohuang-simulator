using System;
using System.Linq;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // API-41：实际执行时读取一位玩家的库存；付款／收益仍交给原通用执行器。
    public static class ResourceExchangeFlow
    {
        public const string TypeId = "flow.resource.exchange";
        public const string DefinitionVersion = "1.0.0";
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute,
                EffectExecutorKind.IntrinsicFlow, DefinitionVersion) { Validator = spec => ValidateArguments(spec?.NormalizedArguments) });
        }
        public static string ValidateArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || !args.IsValid() ||
                args.Properties.Any(p => !new[] { "kind", "effectTypeId", "apiVersion", "player", "targetPlayer", "resourceType", "receiveResourceType", "amount", "rate", "reasonId" }.Contains(p.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Value(args, "apiVersion").IntegerValue != 1 ||
                !ResourceLossEffectExecutor.TryReadPlayer(Value(args, "player"), out _) ||
                !ResourceLossEffectExecutor.TryReadPlayer(Value(args, "targetPlayer"), out _) ||
                !ResourceLossEffectExecutor.TryReadResourceType(Value(args, "resourceType"), out var from) ||
                !ResourceLossEffectExecutor.TryReadResourceType(Value(args, "receiveResourceType"), out var to) || from == to ||
                Text(args, "amount") != "all" || Value(args, "rate")?.Kind != NormalizedValueKind.Integer ||
                Value(args, "rate").IntegerValue < 1 || Value(args, "rate").IntegerValue > 100 ||
                Value(args, "reasonId") is NormalizedValue reason && reason.Kind != NormalizedValueKind.String)
                return "resource_exchange_arguments_invalid";
            return string.Empty;
        }
        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var node = context.Node; string error = ValidateArguments(node.NormalizedArguments);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            ResourceLossEffectExecutor.TryReadPlayer(Value(node.NormalizedArguments, "player"), out int actor);
            ResourceLossEffectExecutor.TryReadPlayer(Value(node.NormalizedArguments, "targetPlayer"), out int target);
            if (actor != node.PlayerId || context.State.FindPlayer(actor) == null || context.State.FindPlayer(target) == null)
                return EffectStepResult.Failed("resource_exchange_player_invalid");
            if (node.PendingOutcome == EffectPendingOutcome.Completed) return EffectStepResult.Completed(node.NormalizedResult.Clone());
            if (node.FlowStage == "")
            {
                ResourceLossEffectExecutor.TryReadResourceType(Value(node.NormalizedArguments, "resourceType"), out var type);
                int stock = ResourceLossEffectExecutor.AvailableAmount(context.State, target, type);
                if (stock < 0) return EffectStepResult.Failed("resource_exchange_balance_invalid");
                if (stock > int.MaxValue / Number(node.NormalizedArguments, "rate")) return EffectStepResult.Failed("resource_exchange_receive_overflow");
                foreach (string key in new[] { "initialAmount", "remaining" }) Set(node, key, NormalizedValue.CreateInteger(stock));
                foreach (string key in new[] { "paid", "received", "batch" }) Set(node, key, NormalizedValue.CreateInteger(0));
                return NextPayment(context, target);
            }
            if (!ValidateSaved(context.State, node, out error)) return EffectStepResult.Failed(error);
            var child = context.ChildNodes.Last();
            if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                return EffectStepResult.Failed(child.FailureReason, Result(node,
                    node.FlowStage == "await_gain" || child.Payment?.IsCommitted == true));
            if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待实际资源兑换子效果。");
            int batch = Number(node.ContinuationData, "batch");
            int rate = Number(node.NormalizedArguments, "rate");
            if (node.FlowStage == "await_payment")
            {
                if (child.Payment?.IsCommitted != true) return EffectStepResult.Failed("resource_exchange_payment_uncommitted");
                ResourceLossEffectExecutor.TryReadResourceType(Value(node.NormalizedArguments, "receiveResourceType"), out var to);
                if (context.State.FindPlayer(target).Resources.Get(to) > int.MaxValue - batch * rate)
                    return EffectStepResult.Failed("resource_exchange_receive_overflow", Result(node, true));
                var gain = ResourceEffectSpecFactory.Gain(target, to, batch * rate, node.SourceId);
                gain.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "reasonId", Value = NormalizedValue.CreateString(Text(node.NormalizedArguments, "reasonId")) });
                gain.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                gain.StableKey = "receive:" + Number(node.ContinuationData, "paid");
                return EffectStepResult.Continue("await_gain").AddChild(gain);
            }
            Set(node, "remaining", NormalizedValue.CreateInteger(Number(node.ContinuationData, "remaining") - batch));
            Set(node, "paid", NormalizedValue.CreateInteger(Number(node.ContinuationData, "paid") + batch));
            Set(node, "received", NormalizedValue.CreateInteger(Number(node.ContinuationData, "received") + batch * rate));
            return NextPayment(context, target);
        }
        private static EffectStepResult NextPayment(EffectExecutionContext context, int target)
        {
            var node = context.Node; int remaining = Number(node.ContinuationData, "remaining");
            if (remaining == 0) return EffectStepResult.Completed(Result(node)).WithFlowStage("finished");
            int rate = Number(node.NormalizedArguments, "rate");
            ResourceLossEffectExecutor.TryReadResourceType(Value(node.NormalizedArguments, "resourceType"), out var from);
            ResourceLossEffectExecutor.TryReadResourceType(Value(node.NormalizedArguments, "receiveResourceType"), out var to);
            int batch = Math.Min(remaining, Math.Min(50, 100 / rate));
            if (context.State.FindPlayer(target).Resources.Get(to) > int.MaxValue - batch * rate)
                return EffectStepResult.Failed("resource_exchange_receive_overflow", Result(node));
            Set(node, "batch", NormalizedValue.CreateInteger(batch));
            var cost = new ResourceSet(); cost.Set(from, batch);
            var payment = ResourceEffectSpecFactory.Pay(target, cost, node.SourceId);
            payment.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "allowSubstitution", Value = NormalizedValue.CreateBoolean(false) });
            payment.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            payment.StableKey = "payment:" + Number(node.ContinuationData, "paid");
            return EffectStepResult.Continue("await_payment").AddChild(payment);
        }
        private static NormalizedValue Result(EffectNodeRuntimeState node, bool currentPaymentCommitted = false) => Object(
            "changed", NormalizedValue.CreateBoolean(Number(node.ContinuationData, "paid") > 0 || currentPaymentCommitted),
            "paidAmount", NormalizedValue.CreateInteger(Number(node.ContinuationData, "paid") + (currentPaymentCommitted ? Number(node.ContinuationData, "batch") : 0)),
            "receivedAmount", NormalizedValue.CreateInteger(Number(node.ContinuationData, "received")),
            "targetPlayer", Value(node.NormalizedArguments, "targetPlayer").Clone());

        public static bool ValidateSaved(GameState state, EffectNodeRuntimeState node, out string diagnostic)
        {
            diagnostic = ValidateArguments(node.NormalizedArguments);
            if (diagnostic.Length > 0) return false;
            if (node.DefinitionVersion != DefinitionVersion) { diagnostic = "resource_exchange_definition_version_mismatch"; return false; }
            ResourceLossEffectExecutor.TryReadPlayer(Value(node.NormalizedArguments, "player"), out int actor);
            ResourceLossEffectExecutor.TryReadPlayer(Value(node.NormalizedArguments, "targetPlayer"), out int target);
            if (actor != node.PlayerId || state.FindPlayer(actor) == null || state.FindPlayer(target) == null)
            { diagnostic = "resource_exchange_player_invalid"; return false; }
            if (node.FlowStage == "") return true;
            var data = node.ContinuationData;
            if (data?.Kind != NormalizedValueKind.Object || !data.IsValid() || data.Properties.Count != 5 ||
                data.Properties.Any(p => !new[] { "initialAmount", "remaining", "paid", "received", "batch" }.Contains(p.Name) ||
                    p.Value?.Kind != NormalizedValueKind.Integer || p.Value.IntegerValue < 0 || p.Value.IntegerValue > int.MaxValue))
            { diagnostic = "resource_exchange_progress_invalid"; return false; }
            int initial = Number(data, "initialAmount"), remaining = Number(data, "remaining"), paid = Number(data, "paid"), batch = Number(data, "batch"), rate = Number(node.NormalizedArguments, "rate");
            if (remaining > initial || paid != initial - remaining || (long)paid * rate != Number(data, "received"))
            { diagnostic = "resource_exchange_progress_invalid"; return false; }
            if (node.FlowStage == "finished") return remaining == 0 &&
                (node.PendingOutcome == EffectPendingOutcome.Completed || node.Status == EffectNodeStatus.Completed) || Invalid(out diagnostic);
            if (node.FlowStage != "await_payment" && node.FlowStage != "await_gain" || batch < 1 || batch > remaining || batch > Math.Min(50, 100 / rate))
                return Invalid(out diagnostic);
            string childId = node.ChildEffectIds.LastOrDefault();
            var child = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == childId);
            if (child == null || child.ParentEffectId != node.EffectId || child.PlayerId != target) return Invalid(out diagnostic);
            ResourceLossEffectExecutor.TryReadResourceType(Value(node.NormalizedArguments, "resourceType"), out var from);
            if (node.FlowStage == "await_payment")
            {
                if (!ValidPayment(child, target, from, batch)) return Invalid(out diagnostic);
            }
            else
            {
                ResourceLossEffectExecutor.TryReadResourceType(Value(node.NormalizedArguments, "receiveResourceType"), out var to);
                if (child.EffectTypeId != ResourceEffectTypeIds.Gain || Number(child.NormalizedArguments, "amount") != batch * rate ||
                    Text(child.NormalizedArguments, "resourceType") != to.ToString()) return Invalid(out diagnostic);
                var payment = state.EffectRuntime.EffectNodes.LastOrDefault(n => n.ParentEffectId == node.EffectId && n.EffectTypeId == ResourceEffectTypeIds.Pay);
                if (payment?.Status != EffectNodeStatus.Completed || payment.Payment?.IsCommitted != true || !ValidPayment(payment, target, from, batch)) return Invalid(out diagnostic);
            }
            return true;
        }
        private static bool ValidPayment(EffectNodeRuntimeState child, int target, ResourceType type, int batch)
        {
            var args = child.NormalizedArguments; var cost = Value(args, "cost");
            return child.EffectTypeId == ResourceEffectTypeIds.Pay && child.PlayerId == target &&
                ResourceEffectExecutor.ValidateCompositePayment(args).Length == 0 &&
                ResourceLossEffectExecutor.TryReadPlayer(Value(args, "player"), out int player) && player == target &&
                cost?.Kind == NormalizedValueKind.Object && Number(cost, type.ToString()) == batch &&
                cost.Properties.All(p => p.Name == type.ToString() || p.Value.IntegerValue == 0) &&
                Value(args, "allowSubstitution")?.Kind == NormalizedValueKind.Boolean && !Flag(args, "allowSubstitution");
        }
        private static bool Invalid(out string diagnostic) { diagnostic = "resource_exchange_progress_invalid"; return false; }
    }
}
