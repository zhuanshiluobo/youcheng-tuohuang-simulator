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


            var pack = ExternalContentRuntime.Pack;
            EventCardDatabase.InitializeExternal(pack.CreateEvents());
            CharacterCardDatabase.Initialize(pack.CreateCharacters());
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
