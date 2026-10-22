using System;
using UnityEngine;

namespace YC.Presentation
{
    [CreateAssetMenu(menuName = "YC/界面/游戏盒组件贴图")]
    public sealed class GameBoxArtworkCatalog : ScriptableObject
    {
        [Serializable] public sealed class Entry { public string visualKey; public Sprite sprite; }
        [SerializeField] private Entry[] entries = Array.Empty<Entry>();
        public Sprite Find(string key) => Array.Find(entries, e => e != null && e.visualKey == key)?.sprite;
    }
}
