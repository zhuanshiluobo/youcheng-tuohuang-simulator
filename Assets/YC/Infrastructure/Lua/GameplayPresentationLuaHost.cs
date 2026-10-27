using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using MoonSharp.Interpreter;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        private static void InstallTextConstructors(Script script, ExecutionGuard guard)
        {
            var text = new Table(script);
            foreach (string kind in new[] { "player", "item", "definition", "resource", "location" })
            {
                string referenceKind = kind;
                string name = char.ToUpperInvariant(kind[0]) + kind.Substring(1);
                text.Set(name, DynValue.NewCallback((_, args) =>
                {
                    guard.ThrowIfTimeExceeded();
                    if (args.Count != 1 || args[0].Type != DataType.String && args[0].Type != DataType.Number) throw new ScriptRuntimeException("文案实体引用应为稳定 ID。");
                    string id = args[0].Type == DataType.String ? args[0].String : args[0].Number.ToString(CultureInfo.InvariantCulture);
                    if (referenceKind == "player" && args[0].Type == DataType.Number)
                    {
                        if (Math.Truncate(args[0].Number) != args[0].Number || args[0].Number <= 0) throw new ScriptRuntimeException("玩家 ID 应为正整数。");
                        id = "p" + id;
                    }
                    var reference = NormalizedValue.CreateStableReference(referenceKind, id);
                    return PresentationValueToLua(script, reference, guard);
                }, "Text." + name));
            }
            text.Set("Ref", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                if (args.Count < 1 || args.Count > 2 || args[0].Type != DataType.String || args.Count == 2 && args[1].Type != DataType.Table)
                    throw new ScriptRuntimeException("Text.Ref 需要 key 和可选具名参数表。");
                var reference = new GameplayTextRef(args[0].String);
                if (args.Count == 2)
                    foreach (var pair in SnapshotValues(args[1].Table).Pairs.OrderBy(p => p.Key.String, StringComparer.Ordinal))
                    {
                        if (pair.Key.Type != DataType.String) throw new ScriptRuntimeException("文案参数必须具名。");
                        var value = ReadPresentationValue(pair.Value, 0);
                        string kind = value.Kind == NormalizedValueKind.Integer ? "integer" : value.Kind == NormalizedValueKind.String ? "text" :
                            value.Kind == NormalizedValueKind.StableReference ? value.ReferenceType : string.Empty;
                        reference.Arguments.Add(new GameplayTextArgument { Name = pair.Key.String, Kind = kind, Value = value });
                    }
                return PresentationValueToLua(script, GameplayPresentationNormalizer.ToNormalized(reference), guard);
            }, "Text.Ref"));
            script.Globals.Set("Text", DynValue.NewTable(CreateReadOnlyProxy(script, text, "nmc_read_only_text_api")));
        }
        private static void InstallPresentationDecorator(Script script, Table effect, ExecutionGuard guard)
        {
            effect.Set("WithPresentation", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                if (args.Count != 2 || args[0].Type != DataType.Table || args[1].Type != DataType.Table || SnapshotValues(args[0].Table).Get("kind").String != EffectSpecKind)
                    throw new ScriptRuntimeException("Effect.WithPresentation 需要已有 EffectSpec 和显示声明。");
                var presentation = GameplayPresentationNormalizer.FromStructuredValue<EffectPresentationDefinition>(ReadPresentationValue(args[1], 0));
                var result = new Table(script);
                foreach (var pair in SnapshotValues(args[0].Table).Pairs) result.Set(pair.Key, pair.Value);
                result.Set("presentation", PresentationValueToLua(script, GameplayPresentationNormalizer.ToStructuredValue(presentation), guard));
                return DynValue.NewTable(result); // 同一业务 spec 的副本，没有新节点或业务身份。
            }, "Effect.WithPresentation"));
            effect.Set("WithContext", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                if (args.Count != 2 || args[0].Type != DataType.Table || args[1].Type != DataType.Table || SnapshotValues(args[0].Table).Get("kind").String != EffectSpecKind)
                    throw new ScriptRuntimeException("Effect.WithContext 需要已有Effect与封闭续接对象。");
                var value = ReadPresentationValue(args[1], 0);
                if (value.Kind == NormalizedValueKind.Array && value.Items.Count == 0) value = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
                var required = YC.Domain.Effects.ItemTransferEffectExecutor.Value(value, "requiredOutcome");
                var binding = YC.Domain.Effects.ItemTransferEffectExecutor.Value(value, "executionBinding");
                YC.Domain.Effects.RequiredEffectOutcome.FromNormalized(required);
                if (binding != null && !YC.Domain.Effects.EffectExecutionBinding.IsRequestValid(binding)) throw new ScriptRuntimeException("effect_execution_binding_request_invalid");
                var saved = NormalizedValue.CreateObject(value.Properties.Where(p => p.Name != "requiredOutcome" && p.Name != "executionBinding").Select(p => p.Clone()).ToList());
                value = saved.Properties.Count == 0 ? null : saved;
                if (!YC.Domain.Effects.EffectContinuationContext.IsValid(value)) throw new ScriptRuntimeException("effect_continuation_context_invalid");
                var result = new Table(script);
                foreach (var pair in SnapshotValues(args[0].Table).Pairs) result.Set(pair.Key, pair.Value);
                if (value != null) result.Set("continuationContext", PresentationValueToLua(script, value, guard));
                if (required != null) result.Set("requiredOutcome", PresentationValueToLua(script, required, guard));
                if (binding != null) result.Set("executionBinding", PresentationValueToLua(script, binding, guard));
                return DynValue.NewTable(result);
            }, "Effect.WithContext"));
        }
        private static bool TryNormalizeEffect(DynValue value, LuaInvocationContext context, out LuaEffectSpec effect, out LuaFailureCode code, out string diagnostic)
        {
            effect = null; code = LuaFailureCode.None; diagnostic = string.Empty;
            if (value.Type != DataType.Table) return TryNormalizeBusinessEffect(value, context, out effect, out code, out diagnostic);
            var input = SnapshotValues(value.Table);
            var presentation = input.Get("presentation");
            var settlement = input.Get("settlement");
            var continuation = input.Get("continuationContext");
            var requiredValue = input.Get("requiredOutcome"); var bindingValue = input.Get("executionBinding");
            if (presentation.IsNil() && settlement.IsNil() && continuation.IsNil() && requiredValue.IsNil() && bindingValue.IsNil()) return TryNormalizeBusinessEffect(value, context, out effect, out code, out diagnostic);
            try
            {
                var metadata = presentation.IsNil() ? null : GameplayPresentationNormalizer.FromStructuredValue<EffectPresentationDefinition>(ReadPresentationValue(presentation, 0));
                var segment = settlement.IsNil() ? null : ReadSettlementEffect(settlement);
                var savedContext = continuation.IsNil() ? null : ReadPresentationValue(continuation, 0);
                if (savedContext?.Kind == NormalizedValueKind.Array && savedContext.Items.Count == 0) savedContext = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
                if (!YC.Domain.Effects.EffectContinuationContext.IsValid(savedContext)) throw new ArgumentException("effect_continuation_context_invalid");
                var requiredOutcome = YC.Domain.Effects.RequiredEffectOutcome.FromNormalized(requiredValue.IsNil() ? null : ReadPresentationValue(requiredValue, 0));
                var executionRequest = bindingValue.IsNil() ? null : ReadPresentationValue(bindingValue, 0);
                if (executionRequest != null && !YC.Domain.Effects.EffectExecutionBinding.IsRequestValid(executionRequest)) throw new ArgumentException("effect_execution_binding_request_invalid");
                var businessContext = executionRequest == null ? context : context.ForEffectNormalization("p" + YC.Domain.Effects.ItemTransferEffectExecutor.Number(executionRequest, "playerId").ToString(CultureInfo.InvariantCulture));
                var business = new Table(input.OwnerScript);
                foreach (var pair in input.Pairs) if (pair.Key.Type != DataType.String || pair.Key.String != "presentation" && pair.Key.String != "settlement" && pair.Key.String != "continuationContext" && pair.Key.String != "requiredOutcome" && pair.Key.String != "executionBinding") business.Set(pair.Key, pair.Value);
                if (!TryNormalizeBusinessEffect(DynValue.NewTable(business), businessContext, out effect, out code, out diagnostic)) return false;
                effect.Presentation = metadata;
                effect.Settlement = segment;
                effect.ContinuationContext = savedContext;
                effect.RequiredOutcome = requiredOutcome; effect.ExecutionBindingRequest = executionRequest;
                return true;
            }
            catch (Exception error) when (error is ArgumentException || error is OverflowException || error is ScriptRuntimeException || error is InvalidOperationException)
            { code = LuaFailureCode.InvalidEffectSpec; diagnostic = SanitizeDiagnostic(error.Message); return false; }
        }
        // 显示脚本只安装 Text。没有 Effect、Flow、Query、GameData 或 PlayerData。
        public bool TryPresent(LuaScriptDefinition definition, GameplayPresentationContext context,
            out GameplayPresentationResult result, out LuaFailureCode code, out string diagnostic)
        {
            result = null; code = LuaFailureCode.None; diagnostic = string.Empty;
            var validation = new LuaInvocationContext("presentation", "presentation", "viewer", context?.StateRevision ?? 0, definition?.DefinitionVersion ?? string.Empty, 0, 0, 0);
            if (!TryValidateDefinition(definition, validation, out code, out diagnostic)) return false;
            try
            {
                GameplayPresentationNormalizer.Validate(context);
                if (context == null) throw new ArgumentException("显示上下文不能为空。");
                var timer = Stopwatch.StartNew(); var guard = new ExecutionGuard(_budget.MaxInstructions, _budget.MaxMilliseconds, timer);
                var script = new Script(SandboxedModules); script.Options.DebugPrint = _ => { };
                InstallForbiddenGlobals(script, guard); InstallTextConstructors(script, guard);
                ResumeWithBudget(script.CreateCoroutine(script.LoadString(definition.Source, null, definition.ContentId)).Coroutine, Array.Empty<DynValue>(), guard);
                DynValue handler = script.Globals.Get("Present");
                if (handler.Type != DataType.Function) { code = LuaFailureCode.InvalidHandler; diagnostic = "显示脚本必须声明 Present(context)。"; return false; }
                var raw = ResumeWithBudget(script.CreateCoroutine(handler).Coroutine,
                    new[] { PresentationValueToLua(script, GameplayPresentationNormalizer.ToStructuredValue(context), guard) }, guard);
                if (!InspectTables(raw, 0, new HashSet<Table>(TableReferenceComparer.Instance), out code, out diagnostic)) return false;
                result = GameplayPresentationNormalizer.FromStructuredValue<GameplayPresentationResult>(ReadPresentationValue(raw, 0));
                if (result == null) throw new ArgumentException("Present 必须返回显示结果对象。");
                ValidatePresentationResultScope(result, context.Facts);
                return true;
            }
            catch (LuaLimitException error) { code = error.Code; diagnostic = SanitizeDiagnostic(error.Message); }
            catch (Exception error) when (error is InterpreterException || error is ArgumentException || error is OverflowException)
            { code = LuaFailureCode.InvalidReturnShape; diagnostic = SanitizeDiagnostic(error.Message); }
            result = null; return false;
        }
        private static void ValidatePresentationResultScope(GameplayPresentationResult result, NormalizedValue facts)
        {
            if (result.Interaction == null) return;
            var candidates = facts.Properties.Find(p => p.Name == "candidateIds")?.Value;
            var rows = facts.Properties.Find(p => p.Name == "rowIds")?.Value;
            if (result.Interaction.Options.Any(option => candidates == null || candidates.Kind != NormalizedValueKind.Array || !candidates.Items.Any(v => v.StringValue == option.CandidateId || v.ReferenceId == option.CandidateId)) ||
                result.Interaction.QuantityRows.Any(row => rows == null || rows.Kind != NormalizedValueKind.Array || !rows.Items.Any(v => v.StringValue == row.RowId || v.ReferenceId == row.RowId)))
                throw new ArgumentException("显示函数不得新增合法集合外的候选或数量行。");
        }
        private static NormalizedValue ReadPresentationValue(DynValue value, int depth, bool arrayExpected = false)
        {
            if (depth > NormalizedValue.MaxDepth) throw new ArgumentException("显示声明嵌套过深。");
            if (value.Type != DataType.Table)
            {
                LuaFailureCode code; string diagnostic;
                var result = ToNormalizedValue(value, depth, out code, out diagnostic);
                if (result == null) throw new ArgumentException(diagnostic);
                return result;
            }
            var table = SnapshotValues(value.Table);
            if (table.Get("kind").String == "stable_reference")
            {
                if (table.Pairs.Count() != 3 || table.Get("referenceType").Type != DataType.String || table.Get("referenceId").Type != DataType.String)
                    throw new ArgumentException("显示实体引用字段无效。");
                return NormalizedValue.CreateStableReference(table.Get("referenceType").String, table.Get("referenceId").String);
            }
            var pairs = table.Pairs.ToList();
            if (pairs.Count > NormalizedValue.MaxElements) throw new ArgumentException("显示表项过多。");
            bool array = pairs.Count == 0 ? arrayExpected : pairs.All(p => p.Key.Type == DataType.Number);
            if (array)
            {
                if (pairs.Count != table.Length || pairs.Any(p => p.Key.Number < 1 || p.Key.Number > pairs.Count || Math.Truncate(p.Key.Number) != p.Key.Number)) throw new ArgumentException("显示数组必须连续。");
                var items = new List<NormalizedValue>(); for (int i = 1; i <= table.Length; i++) items.Add(ReadPresentationValue(table.Get(i), depth + 1));
                return NormalizedValue.CreateArray(items);
            }
            var entries = new List<NormalizedValueEntry>();
            foreach (var pair in pairs)
            {
                if (pair.Key.Type != DataType.String) throw new ArgumentException("显示对象字段必须具名。");
                entries.Add(new NormalizedValueEntry { Name = pair.Key.String, Value = ReadPresentationValue(pair.Value, depth + 1,
                    new[] { "arguments", "options", "quantityRows", "details", "candidateIds", "rowIds" }.Contains(pair.Key.String)) });
            }
            return NormalizedValue.CreateObject(entries);
        }
        private static DynValue PresentationValueToLua(Script script, NormalizedValue value, ExecutionGuard guard)
        {
            guard.ThrowIfTimeExceeded();
            if (value == null || value.Kind == NormalizedValueKind.Null) return DynValue.Nil;
            switch (value.Kind)
            {
                case NormalizedValueKind.Boolean: return DynValue.NewBoolean(value.BooleanValue);
                case NormalizedValueKind.Integer: return DynValue.NewNumber(value.IntegerValue);
                case NormalizedValueKind.String: return DynValue.NewString(value.StringValue);
                case NormalizedValueKind.StableReference:
                    var reference = new Table(script); reference.Set("kind", DynValue.NewString("stable_reference")); reference.Set("referenceType", DynValue.NewString(value.ReferenceType)); reference.Set("referenceId", DynValue.NewString(value.ReferenceId));
                    return DynValue.NewTable(CreateReadOnlyProxy(script, reference, "nmc_read_only_context"));
                case NormalizedValueKind.Array:
                    var items = new List<DynValue>(); foreach (var item in value.Items) items.Add(PresentationValueToLua(script, item, guard));
                    return CreateReadOnlyArray(script, items, "nmc_read_only_context");
                case NormalizedValueKind.Object:
                    var table = new Table(script); foreach (var entry in value.Properties) table.Set(entry.Name, PresentationValueToLua(script, entry.Value, guard));
                    return DynValue.NewTable(CreateReadOnlyProxy(script, table, "nmc_read_only_context"));
                default: throw new ArgumentException("未知显示归一化值。");
            }
        }
    }
}
