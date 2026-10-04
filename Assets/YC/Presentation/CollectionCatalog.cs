using System.Collections.Generic;
using UnityEngine;
namespace YC.Presentation
{
    [CreateAssetMenu(fileName = "CollectionCatalog", menuName = "游城/收藏室/藏品目录")]
    public sealed class CollectionCatalog : ScriptableObject
    {
        [SerializeField] private CollectionItemDefinition[] items = new CollectionItemDefinition[0];
        public IReadOnlyList<CollectionItemDefinition> Items => items;
    }
}
