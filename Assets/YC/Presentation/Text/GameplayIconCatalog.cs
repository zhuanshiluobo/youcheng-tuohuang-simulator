using System;
using System.Collections.Generic;
using YC.Domain.Presentation;
using UnityEngine;

namespace YC.Presentation
{
    public enum GameplayIconTone { Dark, Light, Muted }
    [Serializable] public sealed class GameplayIconSpriteBinding
    { public string AssetKey = ""; public Sprite Sprite; }

    /// <summary>共享语义目录的资源适配，只解析精确 AssetKey，无磁盘扫描或相似图回退。</summary>
    [CreateAssetMenu(fileName = "GameplayIconCatalog", menuName = "YC/UI/玩法图标目录")]
    public sealed class GameplayIconCatalog : ScriptableObject
    {
        [SerializeField] private List<GameplayIconSpriteBinding> bindings = new List<GameplayIconSpriteBinding>();
        private Dictionary<string, Sprite> index;
        private int revision;
        public int Revision => revision;
        public void ConfigureBindings(IEnumerable<GameplayIconSpriteBinding> values)
        { bindings = new List<GameplayIconSpriteBinding>(); if (values != null) foreach (var value in values) if (value != null) bindings.Add(new GameplayIconSpriteBinding { AssetKey = value.AssetKey, Sprite = value.Sprite }); NotifyChanged(); }
        public void NotifyChanged() { index = null; unchecked { revision++; } }
        private void OnValidate() => NotifyChanged();
        public bool TryResolve(GameplayIconDefinition definition, GameplayIconTone tone, out Sprite sprite)
        {
            sprite = null;
            if (definition == null) return false;
            var key = !string.IsNullOrEmpty(definition.AssetKey) ? definition.AssetKey :
                tone == GameplayIconTone.Light ? definition.Light : tone == GameplayIconTone.Muted ? definition.Muted : definition.Dark;
            if (string.IsNullOrEmpty(key)) return false;
            if (index == null)
            {
                index = new Dictionary<string, Sprite>(StringComparer.Ordinal);
                foreach (var binding in bindings)
                {
                    if (binding == null || string.IsNullOrEmpty(binding.AssetKey)) continue;
                    if (index.ContainsKey(binding.AssetKey)) throw new InvalidOperationException("图标资源键重复：" + binding.AssetKey);
                    index.Add(binding.AssetKey, binding.Sprite);
                }
            }
            return index.TryGetValue(key, out sprite) && sprite != null;
        }
    }
}
