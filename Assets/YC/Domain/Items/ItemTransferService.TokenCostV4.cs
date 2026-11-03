using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    public sealed partial class ItemTransferService
    {
        // 付款是带来源资格和足额原子性的独立合同；物品参数使用同一v4显式语法。
        // 有限旧输入只在边界纯归一化，费用再限定库存子集；绝不逆投影为旧请求或创建旧执行节点。
        public static bool TryTokenCostArguments(NormalizedValue input, out NormalizedValue args, out string reason)
        {
            args = input;
            if (ItemMoveInputNormalization.IsSupportedLegacy(TypeId, input))
            {
                if (Number(input, "apiVersion") == 3) { reason = ValidateTokenArguments(input); if (reason.Length > 0) return false; }
                var spec = new EffectSpec(TypeId, input.Clone());
                if (!ItemMoveInputNormalization.TryNormalize(spec, out reason)) return false;
                args = spec.NormalizedArguments;
            }
            reason = ItemMovementV4.ValidateArguments(args); if (reason.Length > 0) return false;
            var selector = Value(args, "selector"); var from = Value(selector, "expectedSource");
            if (!ItemMovementV4.IsV4(TypeId, args) || Text(args, "operation") != "transfer" ||
                Text(selector, "mode") != "explicit" || !IsTokenProfile(Text(args, "operationProfileId")) ||
                Text(Value(args, "insertion"), "kind") != "unordered" || Text(from, "slotId").Length > 0 ||
                Flag(Value(args, "destination"), "mustBeEmpty"))
            { reason = "token_cost_requires_explicit_inventory_transfer"; return false; }
            reason = ""; return true;
        }
        private static NormalizedValue TokenCostIds(NormalizedValue args) => Value(Value(args, "selector"), "itemIds");
        private static string TokenCostSource(NormalizedValue args) => Text(Value(Value(args, "selector"), "expectedSource"), "zoneId");
    }
}
