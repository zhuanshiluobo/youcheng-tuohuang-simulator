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


            FacilityCardDatabase.InitializeExternal(ExternalContentRuntime.Pack.CreateFacilities());
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
