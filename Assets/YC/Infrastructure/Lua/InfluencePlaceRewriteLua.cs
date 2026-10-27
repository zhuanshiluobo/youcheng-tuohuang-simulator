using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        private LuaInvocationResult NormalizePlaceRewrite(LuaScriptDefinition definition, LuaInvocationContext context, DynValue raw)
        {
            if (raw.Type != DataType.Table) return LuaInvocationResult.Failure(definition, LuaFailureCode.InvalidReturnShape, "挂接改写必须返回Keep或Replace对象。");
            var table = raw.Table;
            if (table.Pairs.Any(p => p.Key.Type != DataType.String || !new[] { "operation", "effect", "resultContract", "permissionContract" }.Contains(p.Key.String)))
                return LuaInvocationResult.Failure(definition, LuaFailureCode.UnexpectedField, "挂接改写字段无效。");
            string operation = table.Get("operation").String;
            if (operation == "keep")
            {
                if (table.Pairs.Count() != 1) return LuaInvocationResult.Failure(definition, LuaFailureCode.InvalidReturnShape, "Keep不能携带副作用或替代树。");
                return LuaInvocationResult.RewriteSuccess(definition, "keep", Array.Empty<LuaEffectSpec>());
            }
            if (operation != "replace" || table.Get("resultContract").String != "place_result" || table.Get("permissionContract").String != "inherit_original")
                return LuaInvocationResult.Failure(definition, LuaFailureCode.InvalidReturnShape, "Replace的结果与权限合同无效。");
            var array = new Table(table.OwnerScript); array.Set(1, table.Get("effect"));
            if (!TryNormalizeResult(DynValue.NewTable(array), context, out var effects, out var code, out var diagnostic) || effects.Count != 1)
                return LuaInvocationResult.Failure(definition, code, diagnostic);
            return LuaInvocationResult.RewriteSuccess(definition, "replace", effects);
        }
    }
    public sealed partial class LuaEffectEventDispatcher
    {
        internal InfluencePlaceRewrite InvokePlaceRewrite(LuaHandlerDefinition definition, EffectEventHandlerContext handlerContext)
        {
            var context = CreateInvocationContext(definition, handlerContext); context.ResponseKind = "influencePlaceAttaching";
            var result = host.Invoke(definition.Script, context);
            if (!result.IsSuccess) throw new InvalidOperationException("influence_place_lua_invalid:" + result.Diagnostic);
            if (result.RewriteOperation == "keep") return new InfluencePlaceRewrite();
            var compile = new LuaEffectCompilationContext { PlayerId = handlerContext.Event.PlayerId, SourceId = definition.Script.ContentId,
                ContentInstanceId = definition.ContentInstanceId, AbilityId = definition.AbilityId, StableKeyPrefix = definition.SubscriptionId };
            if (!compiler.TryCompile(result, compile, out var specs, out var fault) || specs.Count != 1) throw new InvalidOperationException(fault?.Diagnostic ?? "influence_place_root_count_invalid");
            return new InfluencePlaceRewrite { Operation = "replace", Effect = specs[0], ResultContract = "place_result", PermissionContract = "inherit_original" };
        }
    }
}
