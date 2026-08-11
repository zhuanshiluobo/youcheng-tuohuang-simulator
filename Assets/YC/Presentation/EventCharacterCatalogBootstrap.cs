using System;
using UnityEngine;
using YC.Domain.Cards;

namespace YC.Presentation
{
    [DefaultExecutionOrder(-22000)]
    public sealed class EventCharacterCatalogBootstrap : MonoBehaviour
    {
        [SerializeField] private EventCharacterCardCatalog catalog;

        public EventCharacterCardCatalog Catalog => catalog;

        private void Awake()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[EventCharacterCatalogBootstrap] " + reason, this);
                throw new InvalidOperationException(
                    "EventCharacterCatalogBootstrap 初始化失败：" + reason);
            }

            EventCardDatabase.Initialize(catalog.CreateEventDefinitions());
            CharacterCardDatabase.Initialize(catalog.CreateCharacterDefinitions());
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (catalog == null)
            {
                reason = "缺少 EventCharacterCardCatalog 引用。";
                return false;
            }

            return catalog.TryValidateConfiguration(out reason);
        }
    }
}
