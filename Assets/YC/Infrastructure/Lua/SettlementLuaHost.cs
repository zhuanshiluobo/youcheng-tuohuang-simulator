using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MoonSharp.Interpreter;
using YC.Domain.Effects;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        private static readonly HashSet<string> OfferFields = new HashSet<string> { "apiVersion", "localId", "player", "sourceItemId", "windowId", "ownerEffectId", "maxUses", "visibility", "availabilityHandlerId", "presentation", "effects" };
        private static void InstallSettlementConstructors(Script script, ExecutionGuard guard)
        {
            var settlement = new Table(script);
            settlement.Set("Offer", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                if (args.Count != 1 || args[0].Type != DataType.Table) throw new ScriptRuntimeException("Settlement.Offer 需要元数据表。");
                var result = new Table(script); result.Set("kind", DynValue.NewString("settlement_offer"));
                foreach (var pair in SnapshotValues(args[0].Table).Pairs)
                { if (pair.Key.Type != DataType.String || !OfferFields.Contains(pair.Key.String)) throw new ScriptRuntimeException("未知 Settlement.Offer 字段。"); result.Set(pair.Key, pair.Value); }
                return DynValue.NewTable(result);
            }, "Settlement.Offer"));
            settlement.Set("WithPresentation", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                if (args.Count != 2 || args[0].Type != DataType.Table || args[1].Type != DataType.Table || SnapshotValues(args[0].Table).Get("kind").String != EffectSpecKind) throw new ScriptRuntimeException("Settlement.WithPresentation 需要已有段 spec 和段元数据。");
                ReadSettlementEffect(args[1]);
                var result = new Table(script); foreach (var pair in SnapshotValues(args[0].Table).Pairs) result.Set(pair.Key, pair.Value);
                result.Set("settlement", args[1]); return DynValue.NewTable(result);
            }, "Settlement.WithPresentation"));
            script.Globals.Set("Settlement", DynValue.NewTable(CreateReadOnlyProxy(script, settlement, "nmc_read_only_settlement_api")));
        }
        private static DynValue CreateSettlementContext(Script script, LuaInvocationContext context, ExecutionGuard guard)
        {
            if (context.SettlementContext == null || context.SettlementFacts == null || context.SettlementFacts.Kind != NormalizedValueKind.Object || !context.SettlementFacts.IsValid()) throw new ArgumentException("结算上下文或授权事实缺失。");
            var value = context.SettlementContext;
            var window = new Table(script);
            window.Set("id", DynValue.NewString(value.Id)); window.Set("kind", DynValue.NewString(value.Kind)); window.Set("ownerEffectId", DynValue.NewString(value.OwnerEffectId));
            window.Set("playerId", DynValue.NewNumber(value.PlayerId)); window.Set("targetUseId", DynValue.NewString(value.TargetUseId)); window.Set("triggerId", DynValue.NewString(value.TriggerId));
            window.Set("stateRevision", DynValue.NewNumber(value.StateRevision)); window.Set("paymentId", DynValue.NewString(value.PaymentId));
            if (value.PaymentCost != null)
            {
                var cost = new Table(script);
                cost.Set("originium", DynValue.NewNumber(value.PaymentCost.Originium)); cost.Set("originiumShard", DynValue.NewNumber(value.PaymentCost.OriginiumShard)); cost.Set("iron", DynValue.NewNumber(value.PaymentCost.Iron));
                cost.Set("pureOriginium", DynValue.NewNumber(value.PaymentCost.PureOriginium)); cost.Set("goldVoucher", DynValue.NewNumber(value.PaymentCost.GoldVoucher));
                window.Set("paymentCost", DynValue.NewTable(CreateReadOnlyProxy(script, cost, "nmc_read_only_context")));
            }
            var table = new Table(script); table.Set("playerId", DynValue.NewString(context.PlayerId)); table.Set("stateRevision", DynValue.NewNumber(context.StateRevision));
            table.Set("window", DynValue.NewTable(CreateReadOnlyProxy(script, window, "nmc_read_only_context")));
            table.Set("facts", PresentationValueToLua(script, context.SettlementFacts, guard));
            return DynValue.NewTable(CreateReadOnlyProxy(script, table, "nmc_read_only_context"));
        }
        private LuaInvocationResult NormalizeSettlementResponse(LuaScriptDefinition definition, LuaInvocationContext context, DynValue raw)
        {
            try
            {
                LuaFailureCode code; string diagnostic;
                if (!InspectTables(raw, 0, new HashSet<Table>(TableReferenceComparer.Instance), out code, out diagnostic)) return LuaInvocationResult.Failure(definition, code, diagnostic);
                if (raw.Type != DataType.Table) throw new ArgumentException("结算响应必须是表。");
                var table = SnapshotValues(raw.Table);
                if (context.ResponseKind == "settlementAvailability")
                {
                    var pairs = table.Pairs.ToList();
                    if (pairs.Any(p => p.Key.Type != DataType.String || p.Key.String != "enabled" && p.Key.String != "disabledReasonKey") || table.Get("enabled").Type != DataType.Boolean ||
                        !table.Get("disabledReasonKey").IsNil() && table.Get("disabledReasonKey").Type != DataType.String) throw new ArgumentException("结算资格只允许 enabled/disabledReasonKey。");
                    string key = table.Get("disabledReasonKey").IsNil() ? string.Empty : table.Get("disabledReasonKey").String;
                    if (key.Length > 0 && !GameplayPresentationNormalizer.IsKey(key)) throw new ArgumentException("禁用理由必须是统一文案 Key。");
                    return LuaInvocationResult.SettlementSuccess(definition, null, new SettlementAvailabilityResult { Enabled = table.Get("enabled").Boolean, DisabledReasonKey = key });
                }
                var offers = new List<SettlementOfferDefinition>();
                if (table.Pairs.Count() != table.Length || table.Length > _budget.MaxEffectSpecs || table.Pairs.Any(p => p.Key.Type != DataType.Number || p.Key.Number < 1 || p.Key.Number > table.Length || Math.Truncate(p.Key.Number) != p.Key.Number)) throw new ArgumentException("结算来源响应必须是连续 Offer 数组。");
                for (int i = 1; i <= table.Length; i++) offers.Add(ReadSettlementOffer(table.Get(i), definition, context));
                if (offers.Select(o => o.SourceItemId + "\u001f" + o.LocalId).Distinct(StringComparer.Ordinal).Count() != offers.Count) throw new ArgumentException("同窗口来源/localId 重复。");
                return LuaInvocationResult.SettlementSuccess(definition, offers.AsReadOnly(), null);
            }
            catch (Exception error) when (error is ArgumentException || error is OverflowException || error is ScriptRuntimeException)
            { return LuaInvocationResult.Failure(definition, LuaFailureCode.InvalidReturnShape, SanitizeDiagnostic(error.Message)); }
        }
        private static SettlementOfferDefinition ReadSettlementOffer(DynValue raw, LuaScriptDefinition definition, LuaInvocationContext context)
        {
            if (raw.Type != DataType.Table) throw new ArgumentException("Offer 必须是元数据表。");
            var table = SnapshotValues(raw.Table);
            if (table.Get("kind").String != "settlement_offer" || table.Pairs.Any(p => p.Key.Type != DataType.String || p.Key.String != "kind" && !OfferFields.Contains(p.Key.String))) throw new ArgumentException("Offer 字段或 kind 无效。");
            string player; bool stable; LuaFailureCode code; string diagnostic;
            if (!TryReadPlayerReference(table.Get("player"), "player", out player, out stable, out code, out diagnostic)) throw new ArgumentException(diagnostic);
            int playerId = ParsePlayerKey(player);
            var offer = new SettlementOfferDefinition { ApiVersion = RequiredInt(table, "apiVersion"), PlayerId = playerId, MaxUses = RequiredInt(table, "maxUses"),
                LocalId = RequiredText(table, "localId"), SourceItemId = RequiredText(table, "sourceItemId"), WindowId = RequiredText(table, "windowId"), OwnerEffectId = RequiredText(table, "ownerEffectId"),
                AvailabilityHandlerId = RequiredText(table, "availabilityHandlerId"), Visibility = RequiredText(table, "visibility"),
                Presentation = GameplayPresentationNormalizer.FromNormalized<SettlementPresentationDefinition>(ReadPresentationValue(table.Get("presentation"), 0)) };
            if (offer.ApiVersion != 1 || offer.MaxUses != 1 || offer.Visibility != "owner" || context.SettlementContext == null || offer.PlayerId != context.SettlementContext.PlayerId ||
                offer.WindowId != context.SettlementContext.Id || offer.OwnerEffectId != context.SettlementContext.OwnerEffectId || !offer.LocalId.StartsWith(definition.AbilityId + ".", StringComparison.Ordinal)) throw new ArgumentException("Offer 版本、窗口、归属、次数或 localId 前缀无效。");
            var effects = new List<LuaEffectSpec>();
            DynValue body = table.Get("effects");
            if (body.Type != DataType.Table) throw new ArgumentException("Offer 正文必须是连续定义数组。");
            var bodyTable = SnapshotValues(body.Table);
            if (bodyTable.Pairs.Count() != bodyTable.Length || bodyTable.Pairs.Any(p => p.Key.Type != DataType.Number ||
                p.Key.Number < 1 || p.Key.Number > bodyTable.Length || Math.Truncate(p.Key.Number) != p.Key.Number))
                throw new ArgumentException("Offer 正文数组包含未知键或空洞。");
            if (!TryReadNestedEffectArray(body, context, "effects", out effects, out code, out diagnostic)) throw new ArgumentException(diagnostic);
            if (effects.Count == 0) throw new ArgumentException("可选插入必须提供尚未实例化的正文定义。");
            var compiler = new LuaEffectSpecCompiler(); List<EffectSpec> specs; LuaEffectCompilationFault fault;
            if (!compiler.TryCompile(LuaInvocationResult.Success(definition, effects), new LuaEffectCompilationContext { PlayerId = playerId, AbilityId = definition.AbilityId, SourceId = offer.SourceItemId, StableKeyPrefix = offer.LocalId }, out specs, out fault)) throw new ArgumentException(fault.Diagnostic);
            offer.Effects = specs.Select(s => s.ToPersistentDefinition()).ToList(); return offer;
        }
        private static string RequiredText(Table table, string name)
        { var value = table.Get(name); if (value.Type != DataType.String || string.IsNullOrWhiteSpace(value.String) || value.String.Length > 128) throw new ArgumentException("结算字段无效：" + name); return value.String; }
        private static int RequiredInt(Table table, string name)
        { var value = table.Get(name); if (value.Type != DataType.Number || Math.Truncate(value.Number) != value.Number || value.Number < 0 || value.Number > 99) throw new ArgumentException("结算整数无效：" + name); return (int)value.Number; }
        private static SettlementEffectDefinition ReadSettlementEffect(DynValue raw)
        {
            if (raw.Type != DataType.Table) throw new ArgumentException("收尾段显示元数据必须是表。");
            var table = SnapshotValues(raw.Table);
            var allowed = new[] { "entryKind", "localId", "wholeEffectPolicy", "sourceItemId", "sourceEffectId", "abilityId", "presentation" };
            if (table.Pairs.Any(p => p.Key.Type != DataType.String || !allowed.Contains(p.Key.String))) throw new ArgumentException("未知收尾段元数据。");
            string policy = RequiredText(table, "wholeEffectPolicy");
            if (policy != "optional" && policy != "required" && policy != "unspecified") throw new ArgumentException("整段政策必须显式声明。");
            var result = new SettlementEffectDefinition { EntryKind = RequiredText(table, "entryKind"), LocalId = RequiredText(table, "localId"),
                SourceItemId = RequiredText(table, "sourceItemId"), SourceEffectId = table.Get("sourceEffectId").IsNil() ? string.Empty : RequiredText(table, "sourceEffectId"), AbilityId = RequiredText(table, "abilityId"),
                WholeEffectPolicy = policy == "optional" ? SettlementWholeEffectPolicy.Optional : policy == "required" ? SettlementWholeEffectPolicy.Required : SettlementWholeEffectPolicy.Unspecified,
                Presentation = GameplayPresentationNormalizer.FromNormalized<SettlementPresentationDefinition>(ReadPresentationValue(table.Get("presentation"), 0)) };
            if (result.EntryKind != SettlementEntryService.CleanupOrder) throw new ArgumentException("spec 段元数据只允许 cleanup_order。");
            return result;
        }
    }
}
