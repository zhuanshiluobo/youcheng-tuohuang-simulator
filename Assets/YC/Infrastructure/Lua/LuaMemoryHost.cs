using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        private bool TryUnwrapMemoryResult(DynValue raw, out DynValue effects, out NormalizedValue memory,
            out LuaFailureCode code, out string diagnostic)
        {
            effects = raw; memory = null; code = LuaFailureCode.None; diagnostic = "";
            if (raw.Type != DataType.Table || raw.Table.Get("effects").IsNil() && raw.Table.Get("memory").IsNil()) return true;
            if (raw.Table.Pairs.Any(p => p.Key.Type != DataType.String || p.Key.String != "effects" && p.Key.String != "memory") ||
                raw.Table.Get("effects").Type != DataType.Table)
            {
                code = LuaFailureCode.InvalidReturnShape; diagnostic = "lua_memory_return_requires_effects_table"; return false;
            }
            effects = raw.Table.Get("effects");
            var value = raw.Table.Get("memory");
            if (value.IsNil()) return true;
            if (value.Type != DataType.Table)
            {
                code = LuaFailureCode.InvalidFieldType; diagnostic = "lua_memory_requires_table"; return false;
            }
            // 独立检查普通数据；不把其中的 kind/presentation 等业务字段当效果声明。
            int count = 0;
            if (!InspectMemory(value, 0, new HashSet<Table>(TableReferenceComparer.Instance), ref count))
            {
                code = LuaFailureCode.InvalidReturnShape; diagnostic = "lua_memory_data_invalid"; return false;
            }
            memory = ToNormalizedValue(value, 0, out code, out diagnostic);
            if (memory == null) return false;
            if (!LuaEntryMemoryStore.IsValidValue(memory))
            {
                memory = null; code = LuaFailureCode.InvalidReturnShape; diagnostic = "lua_memory_data_invalid"; return false;
            }
            return true;
        }

        private bool InspectMemory(DynValue value, int depth, HashSet<Table> active, ref int count)
        {
            if (depth >= Math.Min(_budget.MaxTableDepth, NormalizedValue.MaxDepth) || ++count > NormalizedValue.MaxElements) return false;
            if (value.Type == DataType.Number) return !double.IsNaN(value.Number) && !double.IsInfinity(value.Number) &&
                Math.Truncate(value.Number) == value.Number && value.Number >= NormalizedValue.MinInteger && value.Number <= NormalizedValue.MaxInteger;
            if (value.Type == DataType.String || value.Type == DataType.Boolean || value.IsNil()) return true;
            if (value.Type != DataType.Table) return false;
            var table = SnapshotValues(value.Table);
            if (table.MetaTable != null || !active.Add(table)) return false;
            int entries = 0;
            foreach (var pair in table.Pairs)
            {
                if (++entries > _budget.MaxTableEntries ||
                    pair.Key.Type != DataType.String && (pair.Key.Type != DataType.Number || pair.Key.Number < 1 ||
                        pair.Key.Number > NormalizedValue.MaxElements || Math.Truncate(pair.Key.Number) != pair.Key.Number) ||
                    !InspectMemory(pair.Value, depth + 1, active, ref count)) return false;
            }
            active.Remove(table);
            return true;
        }

        private static DynValue MemoryToLua(Script script, NormalizedValue value)
        {
            if (!LuaEntryMemoryStore.IsValidValue(value)) throw new ArgumentException("lua_memory_data_invalid");
            return MemoryValueToLua(script, value);
        }

        private static DynValue MemoryValueToLua(Script script, NormalizedValue value)
        {
            switch (value.Kind)
            {
                case NormalizedValueKind.Null: return DynValue.Nil;
                case NormalizedValueKind.Boolean: return DynValue.NewBoolean(value.BooleanValue);
                case NormalizedValueKind.Integer: return DynValue.NewNumber(value.IntegerValue);
                case NormalizedValueKind.String: return DynValue.NewString(value.StringValue);
                default:
                    var table = new Table(script);
                    if (value.Kind == NormalizedValueKind.Array)
                        for (int i = 0; i < value.Items.Count; i++) table.Set(i + 1, MemoryValueToLua(script, value.Items[i]));
                    else foreach (var pair in value.Properties) table.Set(pair.Name, MemoryValueToLua(script, pair.Value));
                    return DynValue.NewTable(table);
            }
        }
    }
}
