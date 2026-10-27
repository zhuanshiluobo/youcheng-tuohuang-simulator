using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        private LuaInvocationResult NormalizeAbilityQueryResponse(LuaScriptDefinition definition, DynValue raw)
        {
            try
            {
                if (!InspectTables(raw, 0, new HashSet<Table>(TableReferenceComparer.Instance), out var code, out string diagnostic)) return LuaInvocationResult.Failure(definition, code, diagnostic);
                var value = ReadPresentationValue(raw, 0);
                if (value.Kind != NormalizedValueKind.Object || value.Properties.Count != 3 || value.Properties.Any(p => p.Name != "queryId" && p.Name != "basisDigest" && p.Name != "items")) throw new ArgumentException("纯查询只允许queryId/basisDigest/items。");
                var items = value.Properties.Single(p => p.Name == "items");
                if (items.Value.Kind == NormalizedValueKind.Object && items.Value.Properties.Count == 0) items.Value = NormalizedValue.CreateArray(new List<NormalizedValue>());
                if (items.Value.Kind != NormalizedValueKind.Array || !value.IsValid()) throw new ArgumentException("纯查询items必须为有限数组。");
                return LuaInvocationResult.QuerySuccess(definition, value);
            }
            catch (Exception error) when (error is ArgumentException || error is OverflowException || error is ScriptRuntimeException)
            { return LuaInvocationResult.Failure(definition, LuaFailureCode.InvalidReturnShape, SanitizeDiagnostic(error.Message)); }
        }
    }
}
