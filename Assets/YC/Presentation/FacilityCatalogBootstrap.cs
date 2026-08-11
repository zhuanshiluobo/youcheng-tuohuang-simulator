using System;
using UnityEngine;
using YC.Domain.Facilities;

namespace YC.Presentation
{
    [DefaultExecutionOrder(-20000)]
    public sealed class FacilityCatalogBootstrap : MonoBehaviour
    {
        [SerializeField] private FacilityCardCatalog facilityCardCatalog;

        public FacilityCardCatalog Catalog => facilityCardCatalog;

        private void Awake()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[FacilityCatalogBootstrap] " + reason, this);
                throw new InvalidOperationException("FacilityCatalogBootstrap 初始化失败：" + reason);
            }

            FacilityCardDatabase.Initialize(facilityCardCatalog.CreateDefinitions());
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (facilityCardCatalog == null)
            {
                reason = "缺少 FacilityCardCatalog 引用。";
                return false;
            }

            return facilityCardCatalog.TryValidateConfiguration(out reason);
        }
    }
}
