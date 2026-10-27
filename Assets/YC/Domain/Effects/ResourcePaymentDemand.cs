using YC.Domain.Rules;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    [Serializable]
    public sealed class ResourcePaymentDemandState
    {
        public int ProtocolVersion = 1;
        public string Mode = "fixed";
        public ResourceSet FixedCost = new ResourceSet();
        public List<ResourceType> AllowedResourceTypes = new List<ResourceType>();
        public int TotalCount;
    }

    public static class ResourcePaymentDemandParser
    {
        public const int MaximumTotal = 99;
        public static bool IsTotal(NormalizedValue args) => Text(args, "resourceType") == "total";
        public static string TryParse(NormalizedValue args, out ResourcePaymentDemandState demand, out int player)
        {
            demand = null; player = -1;
            var allowed = new[] { "kind", "effectTypeId", "apiVersion", "player", "resourceType", "allowedResourceTypes", "amount", "cost", "allowSubstitution" };
            if (args?.Kind != NormalizedValueKind.Object || !args.IsValid() || args.Properties.Any(p => !allowed.Contains(p.Name)) ||
                args.Properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != args.Properties.Count || !IsTotal(args) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Value(args, "apiVersion").IntegerValue != 2 ||
                Value(args, "player")?.Kind != NormalizedValueKind.String || !TryParsePlayerReference(Text(args, "player"), out player) ||
                Value(args, "allowSubstitution") != null && Value(args, "allowSubstitution").Kind != NormalizedValueKind.Boolean)
                return "payment_total_arguments_invalid";
            var n = Value(args, "amount"); var types = Value(args, "allowedResourceTypes");
            if (n?.Kind != NormalizedValueKind.Integer || n.IntegerValue < 0 || n.IntegerValue > MaximumTotal || types?.Kind != NormalizedValueKind.Array || types.Items.Count == 0)
                return "payment_total_demand_invalid";
            var parsed = new List<ResourceType>();
            foreach (var item in types.Items)
            {
                if (item?.Kind != NormalizedValueKind.String || !Enum.TryParse(item.StringValue, false, out ResourceType type) || !Enum.IsDefined(typeof(ResourceType), type) || type.ToString() != item.StringValue || parsed.Contains(type))
                    return "payment_total_resource_types_invalid";
                parsed.Add(type);
            }
            if (!PaymentResources.TryRead(Value(args, "cost"), out var cost, true)) return "payment_total_fixed_cost_invalid";
            demand = new ResourcePaymentDemandState { Mode = "total", FixedCost = cost, AllowedResourceTypes = parsed.OrderBy(t => (int)t).ToList(), TotalCount = (int)n.IntegerValue };
            return "";
        }

        private static bool TryParsePlayerReference(string value, out int player)
        {
            // Lua 查询直接返回 ctx.playerId（pN），Effect 构树则已规范化为十进制。
            // 统一解析身份，不改写查询原值；调用方仍须与真实来源/付款节点的授权玩家核对。
            string digits = (value ?? "").StartsWith("p", StringComparison.OrdinalIgnoreCase) ? value.Substring(1) : value;
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out player) && player > 0) return true;
            player = -1;
            return false;
        }
    }

    public static class PaymentResources
    {
        public static readonly ResourceType[] Types = Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().ToArray();
        public static bool Valid(ResourceSet value) => value != null && Types.All(t => value.Get(t) >= 0);
        public static bool Same(ResourceSet a, ResourceSet b) => a != null && b != null && Types.All(t => a.Get(t) == b.Get(t));
        public static int Total(ResourceSet value) => checked((int)Types.Sum(t => (long)value.Get(t)));
        public static ResourceSet Add(ResourceSet a, ResourceSet b) { var v = new ResourceSet(); foreach (var t in Types) v.Set(t, checked(a.Get(t) + b.Get(t))); return v; }
        public static ResourceSet Scale(ResourceSet a, int n) { var v = new ResourceSet(); foreach (var t in Types) v.Set(t, checked(a.Get(t) * n)); return v; }
        public static NormalizedValue Write(ResourceSet cost) => NormalizedValue.CreateObject(Types.Select(t => new NormalizedValueEntry { Name = t.ToString(), Value = NormalizedValue.CreateInteger(cost.Get(t)) }).ToList());
        public static bool TryRead(NormalizedValue value, out ResourceSet cost, bool allowMissing = false)
        {
            cost = new ResourceSet(); if (value == null) return allowMissing;
            if (value.Kind != NormalizedValueKind.Object || !value.IsValid()) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.Properties)
            {
                if (!seen.Add(p.Name) || !Enum.TryParse(p.Name, false, out ResourceType type) || !Enum.IsDefined(typeof(ResourceType), type) || type.ToString() != p.Name || p.Value?.Kind != NormalizedValueKind.Integer || p.Value.IntegerValue < 0 || p.Value.IntegerValue > int.MaxValue) return false;
                cost.Set(type, (int)p.Value.IntegerValue);
            }
            return true;
        }
        public static string Candidate(ResourceSet cost) => "pay|" + string.Join("|", new[] { cost.Originium, cost.Iron, cost.OriginiumShard, cost.GoldVoucher, cost.PureOriginium }.Select(n => n.ToString(CultureInfo.InvariantCulture)));
    }
}
