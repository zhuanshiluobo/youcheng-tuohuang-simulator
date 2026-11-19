using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        private LuaInvocationResult NormalizeOperatorDispatchPatch(LuaScriptDefinition definition, DynValue raw)
        {
            try
            {
                if (!InspectTables(raw, 0, new HashSet<Table>(TableReferenceComparer.Instance), out var code, out string diagnostic))
                    return LuaInvocationResult.Failure(definition, code, diagnostic);
                var value = ReadPresentationValue(raw, 0);
                if (value.Kind != NormalizedValueKind.Object || value.Properties.Count > 1 || value.Properties.Any(p => p.Name != "drawCountDelta"))
                    throw new ArgumentException("dispatch_patch_shape_invalid");
                var delta = Value(value, "drawCountDelta");
                if (delta != null && (delta.Kind != NormalizedValueKind.Integer || delta.IntegerValue < -99 || delta.IntegerValue > 99))
                    throw new ArgumentException("dispatch_patch_delta_invalid");
                return LuaInvocationResult.DispatchPatchSuccess(definition, delta == null ? 0 : (int)delta.IntegerValue);
            }
            catch (Exception error) when (error is ArgumentException || error is OverflowException || error is ScriptRuntimeException)
            { return LuaInvocationResult.Failure(definition, LuaFailureCode.InvalidReturnShape, SanitizeDiagnostic(error.Message)); }
        }
    }

    public sealed partial class LuaEffectEventDispatcher
    {
        internal int InvokeOperatorDispatchPatch(LuaHandlerDefinition definition, EffectExecutionContext context,
            NormalizedValue facts, int respondingPlayer = -1, string sourceItemId = "", string cardInstanceId = "")
        {
            var payload = facts.Clone();
            if (sourceItemId.Length > 0) payload.Properties.Add(new NormalizedValueEntry { Name = "sourceItemId", Value = NormalizedValue.CreateString(sourceItemId) });
            if (cardInstanceId.Length > 0) payload.Properties.Add(new NormalizedValueEntry { Name = "cardInstanceRef", Value = NormalizedValue.CreateString(cardInstanceId) });
            payload.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            var ruleEvent = new RuleEvent { EventId = StableIdFactory.Create("dispatch-count-query", context.Node.EffectId),
                EventType = OperatorDispatchFlow.StartingEvent, PlayerId = respondingPlayer < 0 ? context.Node.PlayerId : respondingPlayer,
                OwnerNodeId = context.Node.EffectId, SourceEffectId = context.Node.EffectId,
                ResponseKind = RuleEventResponseKind.OperatorDispatchPatches, Payload = payload };
            var invocation = CreateInvocationContextCore(definition, new EffectEventHandlerContext(context.State, ruleEvent, context.Node, null), true);
            invocation.ResponseKind = "operatorDispatchPatches";
            var result = InvokeScript(definition.Script, invocation);
            if (!result.IsSuccess) throw new InvalidOperationException("dispatch_patch_lua_invalid:" + result.Diagnostic);
            return result.OperatorDispatchCountDelta;
        }
    }
}
