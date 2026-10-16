using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;

namespace YC.Infrastructure.Lua
{
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        private static LuaSnapshotValue QueryCollection(LuaSnapshotValue root, string name, string api)
        {
            var value = root?.Get(name);
            if (value == null || value.Kind == LuaSnapshotValueKind.Null)
                throw new ScriptRuntimeException("nmc_query_snapshot_unavailable:" + api);
            return value;
        }

        private static void QueryStringArguments(CallbackArguments args, int count, string api)
        {
            if (args.Count != count) throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api);
            for (int i = 0; i < args.Count; i++)
                if (args[i].Type != DataType.String || args[i].String.Length > LuaQueryLimits.MaximumStringLength)
                    throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api);
        }

        private static DynValue QueryRoutesBetween(Script script, LuaSnapshotValue root, CallbackArguments args, ExecutionGuard guard)
        {
            const string api = "Global.Map.GetRoutesBetween";
            guard.ThrowIfTimeExceeded(); QueryStringArguments(args, 2, api);
            var collection = QueryCollection(root, "routesBetween", api);
            var result = collection.Get(args[0].String).Get(args[1].String);
            return SnapshotToDynValue(script, result.Kind == LuaSnapshotValueKind.Null ? LuaSnapshotValue.Array(null) : result, guard, "nmc_read_only_snapshot");
        }

        private static DynValue QueryFilteredCollection(Script script, LuaSnapshotValue root, CallbackArguments args,
            string name, string api, ExecutionGuard guard)
        {
            guard.ThrowIfTimeExceeded();
            string[] allowed = name == "instances" ? new[] { "id", "definitionId", "typeId", "contentType", "zoneId", "owner", "controller", "slotId", "tag", "includeDescendants", "limit" } :
                name == "zones" ? new[] { "id", "templateId", "localZoneId", "parentId", "owner", "includeDescendants", "limit" } :
                new[] { "influenceId", "ownerPlayerId", "owner", "locationId", "routeId", "slotId", "slotIds", "limit" };
            var filters = ReadQueryFilter(args, api, guard, allowed);
            bool descendants = filters.TryGetValue("includeDescendants", out var include) && include.BooleanValue;
            string scopeKey = name == "instances" ? "zoneId" : "parentId";
            if (filters.ContainsKey("includeDescendants") && !filters.ContainsKey(scopeKey))
                throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api + ":includeDescendants");
            var values = QueryCollection(root, name, api);
            if (values.Kind != LuaSnapshotValueKind.Array) throw new ScriptRuntimeException("nmc_query_snapshot_unavailable:" + api);
            var zones = descendants ? QueryCollection(root, "zones", api).Items.ToDictionary(z => z.Get("id").StringValue, StringComparer.Ordinal) : null;
            var instances = descendants ? QueryCollection(root, "instances", api).Items.ToDictionary(i => i.Get("id").StringValue, StringComparer.Ordinal) : null;
            var result = new List<LuaSnapshotValue>();
            int limit = filters.TryGetValue("limit", out var maximum) ? (int)maximum.IntegerValue : LuaQueryLimits.MaximumResults;
            foreach (var value in values.Items)
            {
                guard.ThrowIfTimeExceeded(); bool matches = true;
                foreach (var filter in filters)
                {
                    if (filter.Key == "limit" || filter.Key == "includeDescendants") continue;
                    if (filter.Key == "slotIds") matches = filter.Value.Items.Any(id => id.StringValue == value.Get("slotId").StringValue);
                    else if (filter.Key == "tag")
                        matches = InstanceDefinitionValue(root, value, "tags").Items.Any(tag => tag.StringValue == filter.Value.StringValue);
                    else if (descendants && filter.Key == scopeKey)
                        matches = name == "instances" ? IsWithinQueryZone(value.Get("zoneId").StringValue, filter.Value.StringValue, zones, instances, guard) :
                            value.Get("id").StringValue != filter.Value.StringValue && IsWithinQueryZone(value.Get("id").StringValue, filter.Value.StringValue, zones, instances, guard);
                    else
                    {
                        var actual = filter.Key == "contentType" ? InstanceDefinitionValue(root, value, "contentType") : value.Get(filter.Key);
                        matches = actual.Kind == LuaSnapshotValueKind.String && actual.StringValue == filter.Value.StringValue;
                    }
                    if (!matches) break;
                }
                if (!matches) continue;
                if (result.Count == limit)
                {
                    if (filters.ContainsKey("limit")) break;
                    throw new ScriptRuntimeException("nmc_query_result_limit:" + api);
                }
                result.Add(value);
            }
            return SnapshotToDynValue(script, LuaSnapshotValue.Array(result), guard, "nmc_read_only_snapshot");
        }

        private static LuaSnapshotValue InstanceDefinitionValue(LuaSnapshotValue root, LuaSnapshotValue instance, string field)
        {
            var value = instance.Get(field);
            if (value.Kind != LuaSnapshotValueKind.Null) return value;
            var definition = root.Get("definitions").Items.FirstOrDefault(d => d.Get("id").StringValue == instance.Get("definitionId").StringValue);
            return definition?.Get(field) ?? LuaSnapshotValue.Null;
        }

        private static bool IsWithinQueryZone(string current, string ancestor, Dictionary<string, LuaSnapshotValue> zones,
            Dictionary<string, LuaSnapshotValue> instances, ExecutionGuard guard)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(current) && visited.Add(current))
            {
                guard.ThrowIfTimeExceeded();
                if (current == ancestor) return true;
                if (!zones.TryGetValue(current, out var zone)) return false;
                string parent = zone.Get("parentId").StringValue;
                if (zone.Get("parentKind").StringValue == "Item")
                {
                    if (parent == ancestor) return true;
                    current = instances.TryGetValue(parent, out var carrier) ? carrier.Get("zoneId").StringValue : null;
                }
                else current = zones.ContainsKey(parent ?? "") ? parent : null;
            }
            return false;
        }

        private static Dictionary<string, LuaSnapshotValue> ReadQueryFilter(CallbackArguments args, string api, ExecutionGuard guard, string[] allowed)
        {
            var result = new Dictionary<string, LuaSnapshotValue>(StringComparer.Ordinal);
            if (args.Count == 0) return result;
            if (args.Count != 1 || args[0].Type != DataType.Table) throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api);
            foreach (var pair in SnapshotValues(args[0].Table).Pairs)
            {
                guard.ThrowIfTimeExceeded();
                if (pair.Key.Type != DataType.String) throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api);
                string key = pair.Key.String;
                if (!allowed.Contains(key)) throw new ScriptRuntimeException("nmc_query_unknown_filter:" + api + ":" + key);
                if (result.Count >= 16) throw new ScriptRuntimeException("nmc_query_filter_limit:" + api);
                var value = pair.Value;
                if (key == "includeDescendants")
                {
                    if (value.Type != DataType.Boolean) throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api + ":" + key);
                    result.Add(key, LuaSnapshotValue.Boolean(value.Boolean));
                }
                else if (key == "limit")
                {
                    if (value.Type != DataType.Number || value.Number != Math.Floor(value.Number) || value.Number < 1 || value.Number > LuaQueryLimits.MaximumResults)
                        throw new ScriptRuntimeException("nmc_query_filter_limit:" + api + ":limit");
                    result.Add(key, LuaSnapshotValue.Integer((int)value.Number));
                }
                else if (key == "slotIds")
                {
                    if (value.Type != DataType.Table) throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api + ":slotIds");
                    var table = SnapshotValues(value.Table); var seen = new HashSet<string>(StringComparer.Ordinal); var items = new List<LuaSnapshotValue>();
                    foreach (var item in table.Pairs)
                    {
                        guard.ThrowIfTimeExceeded();
                        if (item.Key.Type != DataType.Number || item.Key.Number != Math.Floor(item.Key.Number) || item.Key.Number < 1 || item.Key.Number > table.Length ||
                            item.Value.Type != DataType.String || item.Value.String.Length > LuaQueryLimits.MaximumStringLength || !seen.Add(item.Value.String))
                            throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api + ":slotIds");
                        if (seen.Count > LuaQueryLimits.MaximumFilterValues) throw new ScriptRuntimeException("nmc_query_filter_limit:" + api + ":slotIds");
                    }
                    if (seen.Count != table.Length) throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api + ":slotIds");
                    for (int i = 1; i <= table.Length; i++) items.Add(LuaSnapshotValue.String(table.Get(i).String));
                    result.Add(key, LuaSnapshotValue.Array(items));
                }
                else
                {
                    if (value.Type != DataType.String || value.String.Length > LuaQueryLimits.MaximumStringLength)
                        throw new ScriptRuntimeException("nmc_query_invalid_arguments:" + api + ":" + key);
                    result.Add(key, LuaSnapshotValue.String(value.String));
                }
            }
            return result;
        }
    }
}
