using UnityEngine;
using YC.Domain.Rules;

namespace YC.Presentation
{
    public sealed class ResourceCounterBoardView : MonoBehaviour
    {
        private static readonly ResourceType[] GearOrder =
        {
            ResourceType.Originium,
            ResourceType.OriginiumShard,
            ResourceType.Iron
        };

        private static readonly ResourceType[] AuxiliaryOrder =
        {
            ResourceType.PureOriginium,
            ResourceType.GoldVoucher
        };

        [SerializeField] private ResourceCounterBoard controller;
        [SerializeField] private RectTransform root;
        [SerializeField] private ResourceGearCounterView[] gearCounters;
        [SerializeField] private ResourceAuxiliaryCounterView[] auxiliaryCounters;
        [SerializeField] private ResourceCounterVisualLibrary visualLibrary;

        public ResourceCounterBoard Controller => controller;
        public RectTransform Root => root;
        public ResourceGearCounterView[] GearCounters => gearCounters;
        public ResourceAuxiliaryCounterView[] AuxiliaryCounters => auxiliaryCounters;
        public ResourceCounterVisualLibrary VisualLibrary => visualLibrary;

        public bool IsBoundTo(ResourceCounterBoard candidate)
        {
            return candidate != null && controller == candidate;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;
            if (controller == null || root == null || gearCounters == null || gearCounters.Length != 3 ||
                auxiliaryCounters == null || auxiliaryCounters.Length != 2 || visualLibrary == null ||
                !visualLibrary.TryValidateConfiguration(out reason))
            {
                reason = string.IsNullOrEmpty(reason) ? "资源卡板固定 View 引用不完整。" : reason;
                return false;
            }

            for (var i = 0; i < gearCounters.Length; i++)
            {
                if (gearCounters[i] == null || gearCounters[i].ResourceType != GearOrder[i] ||
                    !gearCounters[i].TryValidateConfiguration(out reason))
                {
                    reason = string.IsNullOrEmpty(reason) ? "齿轮资源顺序必须为源岩、源石、异铁。" : reason;
                    return false;
                }
            }

            for (var i = 0; i < auxiliaryCounters.Length; i++)
            {
                if (auxiliaryCounters[i] == null || auxiliaryCounters[i].ResourceType != AuxiliaryOrder[i] ||
                    !auxiliaryCounters[i].TryValidateConfiguration(out reason))
                {
                    reason = string.IsNullOrEmpty(reason) ? "底部资源槽顺序必须为至纯源石、金券。" : reason;
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }
    }
}
