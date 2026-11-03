using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace YC.Domain.State
{
    [Serializable]
    public sealed class LuaEntryMemoryState
    {
        public string EntryId = "";
        public string EntryPoint = "";
        public string ContentHash = "";
        public NormalizedValue Value = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
    }

    public sealed partial class EffectRuntimeState
    {
        public int LuaMemoryProtocolVersion;
        public List<LuaEntryMemoryState> LuaMemories = new List<LuaEntryMemoryState>();
    }

    // 每局一份入口索引。仅保存纯数据，不保存 VM、闭包或领域对象；不按玩家/卡面/Effect 分桶。
    public static class LuaEntryMemoryStore
    {
        public const int ProtocolVersion = 1;
        public const int MaximumEntries = 4096;

        public static NormalizedValue Read(EffectRuntimeState runtime, string entryId, string entryPoint, string hash)
        {
            if (!TryValidate(runtime, out var reason)) throw new InvalidOperationException(reason);
            var entry = runtime.LuaMemories.SingleOrDefault(e => e.EntryId == entryId && e.EntryPoint == entryPoint);
            if (entry != null && entry.ContentHash != hash) throw new InvalidOperationException("lua_memory_entry_hash_mismatch");
            return entry?.Value.Clone() ?? NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        }

        public static void Replace(EffectRuntimeState runtime, string entryId, string entryPoint, string hash, NormalizedValue value)
        {
            if (!IsValidValue(value) || !ValidText(entryId, false) || !ValidText(entryPoint, true) || !ValidHash(hash))
                throw new InvalidOperationException("lua_memory_data_invalid");
            Read(runtime, entryId, entryPoint, hash);
            var entry = runtime.LuaMemories.SingleOrDefault(e => e.EntryId == entryId && e.EntryPoint == entryPoint);
            if (entry == null)
            {
                if (runtime.LuaMemories.Count >= MaximumEntries) throw new InvalidOperationException("lua_memory_entry_limit_exceeded");
                entry = new LuaEntryMemoryState { EntryId = entryId, EntryPoint = entryPoint, ContentHash = hash };
                runtime.LuaMemories.Add(entry);
                runtime.LuaMemories.Sort((a, b) => { int c = StringComparer.Ordinal.Compare(a.EntryId, b.EntryId); return c != 0 ? c : StringComparer.Ordinal.Compare(a.EntryPoint, b.EntryPoint); });
            }
            entry.Value = value.Clone();
            runtime.LuaMemoryProtocolVersion = ProtocolVersion;
        }

        public static bool TryValidate(EffectRuntimeState runtime, out string reason)
        {
            reason = "lua_memory_state_invalid";
            if (runtime?.LuaMemories == null || runtime.LuaMemories.Count > MaximumEntries ||
                runtime.LuaMemoryProtocolVersion != 0 && runtime.LuaMemoryProtocolVersion != ProtocolVersion ||
                runtime.LuaMemoryProtocolVersion == 0 && runtime.LuaMemories.Count != 0) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in runtime.LuaMemories)
                if (entry == null || !ValidText(entry.EntryId, false) || !ValidText(entry.EntryPoint, true) || !ValidHash(entry.ContentHash) ||
                    !ids.Add(StableIdFactory.Create("lua-memory", entry.EntryId, entry.EntryPoint)) || !IsValidValue(entry.Value)) return false;
            reason = ""; return true;
        }

        public static bool IsValidValue(NormalizedValue value) => value != null &&
            (value.Kind == NormalizedValueKind.Array || value.Kind == NormalizedValueKind.Object) && value.IsValid() && Plain(value);
        public static bool HasRawFields(JObject state)
        {
            var runtime = state?["EffectRuntime"] as JObject;
            if (runtime == null) return false;
            if (runtime["LuaMemoryProtocolVersion"] == null && runtime["LuaMemories"] == null) return true;
            if (runtime["LuaMemoryProtocolVersion"]?.Type != JTokenType.Integer || !(runtime["LuaMemories"] is JArray entries) || entries.Count > MaximumEntries) return false;
            foreach (var token in entries)
            {
                if (!(token is JObject entry) || entry["EntryId"]?.Type != JTokenType.String ||
                    entry["EntryPoint"]?.Type != JTokenType.String || entry["ContentHash"]?.Type != JTokenType.String) return false;
                int count = 0;
                if (!RawValue(entry["Value"], 0, ref count)) return false;
            }
            return true;
        }
        private static bool RawValue(JToken token, int depth, ref int count)
        {
            if (depth >= NormalizedValue.MaxDepth || ++count > NormalizedValue.MaxElements || !(token is JObject value) ||
                new[] { "Kind", "BooleanValue", "IntegerValue", "StringValue", "ReferenceType", "ReferenceId", "Items", "Properties" }.Any(key => value[key] == null)) return false;
            if (value["Items"] is JArray items)
                foreach (var item in items) if (!RawValue(item, depth + 1, ref count)) return false;
            if (value["Properties"] is JArray properties)
                foreach (var property in properties)
                    if (!(property is JObject pair) || pair["Name"]?.Type != JTokenType.String || !RawValue(pair["Value"], depth + 1, ref count)) return false;
            return true;
        }
        private static bool Plain(NormalizedValue value) => value.Kind != NormalizedValueKind.StableReference &&
            (value.Kind != NormalizedValueKind.Array || value.Items.All(Plain)) &&
            (value.Kind != NormalizedValueKind.Object || value.Properties.All(p => Plain(p.Value)));
        private static bool ValidText(string value, bool empty) => value != null && (empty || !string.IsNullOrWhiteSpace(value)) && NormalizedValue.CreateString(value).IsValid();
        private static bool ValidHash(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    }
}
