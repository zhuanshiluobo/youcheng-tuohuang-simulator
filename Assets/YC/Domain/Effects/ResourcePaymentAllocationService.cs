using YC.Domain.Rules;
using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class ResourcePaymentAllocationService
    {
        public static bool ValidDemand(ResourcePaymentDemandState d) => d != null && d.ProtocolVersion == 1 && PaymentResources.Valid(d.FixedCost) &&
            d.AllowedResourceTypes != null && d.AllowedResourceTypes.Distinct().Count() == d.AllowedResourceTypes.Count && d.AllowedResourceTypes.All(t => Enum.IsDefined(typeof(ResourceType), t)) &&
            (d.Mode == "fixed" ? d.TotalCount == 0 && d.AllowedResourceTypes.Count == 0 : d.Mode == "total" && d.TotalCount >= 0 && d.TotalCount <= ResourcePaymentDemandParser.MaximumTotal && d.AllowedResourceTypes.Count > 0);
        public static bool TryValidate(ResourcePaymentDemandState demand, ResourceSet allocation, out ResourceSet gross, out string reason)
        {
            gross = null; reason = "payment_allocation_invalid";
            if (!ValidDemand(demand) || !PaymentResources.Valid(allocation) || PaymentResources.Types.Any(t => !demand.AllowedResourceTypes.Contains(t) && allocation.Get(t) != 0) ||
                PaymentResources.Types.Sum(t => (long)allocation.Get(t)) != demand.TotalCount) return false;
            try { gross = PaymentResources.Add(demand.FixedCost, allocation); }
            catch (OverflowException) { reason = "payment_cost_overflow"; return false; }
            reason = ""; return true;
        }
        public static IEnumerable<ResourceSet> EnumerateAllocations(ResourcePaymentDemandState demand)
        {
            if (!ValidDemand(demand)) throw new ArgumentException("payment_demand_invalid");
            if (demand.TotalCount == 0) { yield return new ResourceSet(); yield break; }
            foreach (var allocation in Visit(demand.AllowedResourceTypes.OrderBy(t => (int)t).ToArray(), 0, demand.TotalCount, new ResourceSet())) yield return allocation;
        }
        private static IEnumerable<ResourceSet> Visit(ResourceType[] types, int index, int left, ResourceSet current)
        {
            if (index == types.Length) { if (left == 0) yield return current.Clone(); yield break; }
            for (int n = 0; n <= left; n++)
            {
                current.Set(types[index], n);
                foreach (var allocation in Visit(types, index + 1, left - n, current)) yield return allocation;
            }
            current.Set(types[index], 0);
        }
    }
}
