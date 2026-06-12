using System;
using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Domain.State
{
    [Serializable]
    public sealed class ResourceSet
    {
        public int Originium;
        public int OriginiumShard;
        public int Iron;
        public int PureOriginium;
        public int GoldVoucher;

        public int Get(ResourceType type)
        {
            switch (type)
            {
                case ResourceType.Originium:
                    return Originium;
                case ResourceType.OriginiumShard:
                    return OriginiumShard;
                case ResourceType.Iron:
                    return Iron;
                case ResourceType.PureOriginium:
                    return PureOriginium;
                case ResourceType.GoldVoucher:
                    return GoldVoucher;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        public void Set(ResourceType type, int amount)
        {
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "Resource amount cannot be negative.");
            }

            switch (type)
            {
                case ResourceType.Originium:
                    Originium = amount;
                    break;
                case ResourceType.OriginiumShard:
                    OriginiumShard = amount;
                    break;
                case ResourceType.Iron:
                    Iron = amount;
                    break;
                case ResourceType.PureOriginium:
                    PureOriginium = amount;
                    break;
                case ResourceType.GoldVoucher:
                    GoldVoucher = amount;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        public bool CanPay(ResourceSet cost)
        {
            return Originium >= cost.Originium
                && OriginiumShard >= cost.OriginiumShard
                && Iron >= cost.Iron
                && PureOriginium >= cost.PureOriginium
                && GoldVoucher >= cost.GoldVoucher;
        }

        public bool TryPay(ResourceSet cost)
        {
            if (!CanPay(cost))
            {
                return false;
            }

            Originium -= cost.Originium;
            OriginiumShard -= cost.OriginiumShard;
            Iron -= cost.Iron;
            PureOriginium -= cost.PureOriginium;
            GoldVoucher -= cost.GoldVoucher;
            return true;
        }

        public void Add(ResourceSet gain)
        {
            Originium += gain.Originium;
            OriginiumShard += gain.OriginiumShard;
            Iron += gain.Iron;
            PureOriginium += gain.PureOriginium;
            GoldVoucher += gain.GoldVoucher;
        }

        public IEnumerable<KeyValuePair<ResourceType, int>> Enumerate()
        {
            yield return new KeyValuePair<ResourceType, int>(ResourceType.Originium, Originium);
            yield return new KeyValuePair<ResourceType, int>(ResourceType.OriginiumShard, OriginiumShard);
            yield return new KeyValuePair<ResourceType, int>(ResourceType.Iron, Iron);
            yield return new KeyValuePair<ResourceType, int>(ResourceType.PureOriginium, PureOriginium);
            yield return new KeyValuePair<ResourceType, int>(ResourceType.GoldVoucher, GoldVoucher);
        }

        public ResourceSet Clone()
        {
            return new ResourceSet
            {
                Originium = Originium,
                OriginiumShard = OriginiumShard,
                Iron = Iron,
                PureOriginium = PureOriginium,
                GoldVoucher = GoldVoucher
            };
        }
    }
}
