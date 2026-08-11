using System;
using UnityEngine;
using YC.Domain.CityStyles;
using YC.Domain.SpecialActions;

namespace YC.Presentation
{
    [DefaultExecutionOrder(-21000)]
    public sealed class CityStyleSpecialActionCatalogBootstrap : MonoBehaviour
    {
        [SerializeField] private CityStyleSpecialActionCatalog catalog;

        public CityStyleSpecialActionCatalog Catalog => catalog;

        private void Awake()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[CityStyleSpecialActionCatalogBootstrap] " + reason, this);
                throw new InvalidOperationException(
                    "CityStyleSpecialActionCatalogBootstrap 初始化失败：" + reason);
            }

            var definitions = catalog.CreateDefinitions();
            CityStyleDatabase.Initialize(definitions.CityStyles);
            SpecialActionDatabase.Initialize(definitions.SpecialActions);
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (catalog == null)
            {
                reason = "缺少 CityStyleSpecialActionCatalog 引用。";
                return false;
            }

            return catalog.TryValidateConfiguration(out reason);
        }
    }
}
