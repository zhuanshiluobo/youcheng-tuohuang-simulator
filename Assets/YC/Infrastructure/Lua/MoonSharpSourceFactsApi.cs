using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;

namespace YC.Infrastructure.Lua
{
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        private static void InstallSourceFactsApi(Script script, LuaInvocationContext context, ExecutionGuard guard)
        {
            var api = new Table(script);
            api.Set("GetCurrentSources", DynValue.NewCallback((_, args) => SourceFactsPage(script, context, args, "sources", guard), "SourceData.GetCurrentSources"));
            api.Set("GetGrantSources", DynValue.NewCallback((_, args) => SourceFactsPage(script, context, args, "grants", guard), "SourceData.GetGrantSources"));
            api.Set("GetCallableActions",DynValue.NewCallback((_,args)=>SourceFactsPage(script,context,args,"callableActions",guard),"SourceData.GetCallableActions"));
            api.Set("GetTransferableTokens",DynValue.NewCallback((_,args)=>SourceFactsPage(script,context,args,"transferableTokens",guard),"SourceData.GetTransferableTokens"));
            api.Set("GetTransferReceiptPage", DynValue.NewCallback((_, args) => TransferReceiptPage(script, context, args, guard), "SourceData.GetTransferReceiptPage"));
            script.Globals.Set("SourceData", DynValue.NewTable(CreateReadOnlyProxy(script, api, "nmc_read_only_snapshot")));
        }

        private static DynValue TransferReceiptPage(Script script, LuaInvocationContext context, CallbackArguments args, ExecutionGuard guard)
        {
            var snapshot = context.TransferReceipts;
            if (snapshot?.Kind != LuaSnapshotValueKind.Object || snapshot.Get("version").StringValue != TransferReceiptLuaCatalog.Version ||
                snapshot.Get("stateRevision").IntegerValue != context.StateRevision || snapshot.Get("authorizedPlayerId").StringValue != context.PlayerId) throw new ScriptRuntimeException("transfer_receipt_scope_unavailable");
            if (args.Count != 1 || args[0].Type != DataType.Table) throw new ScriptRuntimeException("transfer_receipt_page_arguments_invalid");
            var input = SnapshotValues(args[0].Table);
            if (input.Pairs.Count() != 3 || input.Pairs.Any(p => p.Key.Type != DataType.String || !new[] { "receiptId", "offset", "limit" }.Contains(p.Key.String))) throw new ScriptRuntimeException("transfer_receipt_page_arguments_invalid");
            var id = input.Get("receiptId"); var start = input.Get("offset"); var count = input.Get("limit");
            if (id.Type != DataType.String || string.IsNullOrWhiteSpace(id.String) || start.Type != DataType.Number || start.Number < 0 || start.Number > int.MaxValue || start.Number % 1 != 0 ||
                count.Type != DataType.Number || count.Number < 1 || count.Number > 8 || count.Number % 1 != 0) throw new ScriptRuntimeException("transfer_receipt_page_arguments_invalid");
            var receipt = snapshot.Get("receipts").Items.SingleOrDefault(r => r.Get("receiptId").StringValue == id.String);
            if (receipt == null) throw new ScriptRuntimeException("transfer_receipt_not_authorized");
            var rows = receipt.Get("items").Items; int offset = (int)start.Number, limit = (int)count.Number;
            if (offset > rows.Count) throw new ScriptRuntimeException("transfer_receipt_page_offset_invalid");
            var items = rows.Skip(offset).Take(limit).ToList();
            var page = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("receiptId", receipt.Get("receiptId")), new LuaSnapshotEntry("items", LuaSnapshotValue.Array(items)),
                new LuaSnapshotEntry("nextOffset", LuaSnapshotValue.Integer(offset + items.Count < rows.Count ? offset + items.Count : -1)), new LuaSnapshotEntry("total", LuaSnapshotValue.Integer(rows.Count)) });
            var result = SnapshotToDynValue(script, page, guard, "nmc_read_only_snapshot");
            if (ToNormalizedValue(result, 0, out _, out _) == null) throw new ScriptRuntimeException("transfer_receipt_page_budget_exceeded");
            return result;
        }

        private static DynValue SourceFactsPage(Script script, LuaInvocationContext context, CallbackArguments args,
            string collection, ExecutionGuard guard)
        {
            var snapshot = context.SourceFacts;
            if (snapshot == null || snapshot.Kind != LuaSnapshotValueKind.Object || snapshot.Get("protocolVersion").IntegerValue != 1)
                throw new ScriptRuntimeException("source_facts_unavailable");
            if (snapshot.Get("stateRevision").IntegerValue != context.StateRevision || snapshot.Get("authorizedPlayerId").StringValue != context.PlayerId)
                throw new ScriptRuntimeException("source_facts_scope_stale");
            if (collection == "transferableTokens" && snapshot.Get(collection).Kind != LuaSnapshotValueKind.Array)
                throw new ScriptRuntimeException("source_facts_token_inventory_unavailable");
            if (args.Count > 1 || args.Count == 1 && args[0].Type != DataType.Table)
                throw new ScriptRuntimeException("source_facts_arguments_invalid");
            Table input = args.Count == 1 ? args[0].Table : new Table(script);
            var fields = new HashSet<string>(StringComparer.Ordinal) { "cursor", "definitionId", "abilityId", "itemId", "activeFaceId", "sourceKey", "expectedStateRevision", "expectedContentHash" };
            if(collection=="callableActions") fields=new HashSet<string>(StringComparer.Ordinal){"cursor","itemId","sourceKey","actionId","callPolicyRef","expectedStateRevision","expectedContentHash"};
            if(collection=="transferableTokens") fields=new HashSet<string>(StringComparer.Ordinal){"cursor","itemId","typeId","zoneId","carrierItemId","localZoneId","expectedStateRevision","expectedContentHash"};
            foreach (var pair in input.Pairs)
                if (pair.Key.Type != DataType.String || !fields.Contains(pair.Key.String)) throw new ScriptRuntimeException("source_facts_arguments_invalid");
            DynValue expected = input.Get("expectedStateRevision");
            if (!expected.IsNil() && (expected.Type != DataType.Number || expected.Number != context.StateRevision))
                throw new ScriptRuntimeException("source_facts_scope_stale");
            expected = input.Get("expectedContentHash");
            if (!expected.IsNil() && (expected.Type != DataType.String || expected.String != snapshot.Get("contentHash").StringValue))
                throw new ScriptRuntimeException("source_facts_content_mismatch");
            DynValue rawCursor = input.Get("cursor");
            if (!rawCursor.IsNil() && (rawCursor.Type != DataType.Number || rawCursor.Number < 0 || rawCursor.Number > int.MaxValue || rawCursor.Number % 1 != 0))
                throw new ScriptRuntimeException("source_facts_cursor_invalid");
            int cursor = rawCursor.IsNil() ? 0 : (int)rawCursor.Number;
            var values = snapshot.Get(collection).Items.AsEnumerable();
            foreach (string field in collection=="transferableTokens" ? new[]{"itemId","typeId","zoneId","carrierItemId","localZoneId"} : collection=="callableActions" ? new[]{"itemId","sourceKey","actionId","callPolicyRef"} : new[] { "definitionId", "abilityId", "itemId", "activeFaceId", "sourceKey" })
            {
                var filter = input.Get(field);
                if (filter.IsNil()) continue;
                if (filter.Type != DataType.String || filter.String.Length == 0) throw new ScriptRuntimeException("source_facts_filter_invalid");
                string text = filter.String;
                values = values.Where(v => v.Get(field).StringValue == text);
            }
            var found = values.ToList();
            if (cursor > found.Count) throw new ScriptRuntimeException("source_facts_cursor_invalid");
            var page = found.Skip(cursor).Take(SourceFactsLuaCatalog.PageSize).ToList();
            var entries = new List<LuaSnapshotEntry>();
            foreach (string key in new[] { "protocolVersion", "stateRevision", "contentHash", "authorizedPlayerId", "ruleOwnerSourceKey", "ruleOwnerItemId" })
                entries.Add(new LuaSnapshotEntry(key, snapshot.Get(key)));
            entries.Add(new LuaSnapshotEntry("items", LuaSnapshotValue.Array(page)));
            if(collection=="callableActions") { entries.Add(new LuaSnapshotEntry("enabledCount",LuaSnapshotValue.Integer(found.Count(v=>v.Get("enabled").BooleanValue)))); entries.Add(new LuaSnapshotEntry("payableMarkerCount",snapshot.Get("payableMarkerCount"))); entries.Add(new LuaSnapshotEntry("transferableTokenCount",snapshot.Get("transferableTokenCount"))); }
            entries.Add(new LuaSnapshotEntry("totalCount", LuaSnapshotValue.Integer(found.Count)));
            entries.Add(new LuaSnapshotEntry("nextCursor", LuaSnapshotValue.Integer(cursor + page.Count < found.Count ? cursor + page.Count : -1)));
            return SnapshotToDynValue(script, LuaSnapshotValue.Object(entries), guard, "nmc_read_only_snapshot");
        }
    }
}
