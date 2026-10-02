using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using YC.Infrastructure.Lua;

namespace YC.Presentation
{
    /// <summary>Unity 只负责内容根路径和贴图解码；规则定义与脚本加载属于 Infrastructure。</summary>
    public static class ExternalContentRuntime
    {
        private static ExternalContentPack pack;
        private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterEditorLifetime()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Reset;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Reset;
            UnityEditor.EditorApplication.quitting -= Reset;
            UnityEditor.EditorApplication.quitting += Reset;
        }
#endif
        public static ExternalContentPack Pack
        {
            get
            {
                if (pack == null)
                {
                    ExternalContentPack.Configure(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
                    pack = ExternalContentPack.Current;
                }
                return pack;
            }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            foreach (var sprite in sprites.Values) Release(sprite);
            sprites.Clear();
            foreach (var texture in textures.Values) Release(texture);
            textures.Clear(); pack = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterLifetime()
        {
            UnityEngine.Application.quitting -= Reset;
            UnityEngine.Application.quitting += Reset;
        }

        private static void Release(UnityEngine.Object value)
        {
            if (value == null) return;
            if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
        public static Texture2D GetSharedArtwork(string id)
        {
            string relative = Pack.FindSharedArtwork(id);
            return relative == null ? null : LoadTexture(relative);
        }
        public static Sprite GetArtworkSprite(string type, string id)
        {
            string relative = Pack.FindArtwork(type, id);
            if (relative == null) return null;
            var slice = Pack.FindArtworkSlice(type, id);
            return LoadSprite(relative, slice);
        }

        /// <summary>仅供素材预览使用，不据此激活任何规则定义或牌池。</summary>
        public static Sprite GetRegisteredArtworkSprite(string spriteId)
        {
            var slice = Pack.FindRegisteredArtworkSlice(spriteId);
            return slice == null ? null : LoadSprite(slice.Artwork, slice);
        }

        private static Sprite LoadSprite(string relative, ExternalArtworkSlice slice)
        {
            var key = slice == null ? "full:" + relative : "slice:" + slice.Id;
            if (sprites.TryGetValue(key, out var cached) && cached != null && cached.texture != null) return cached;
            Release(cached);
            var texture = LoadTexture(relative);
            if (slice != null && (texture.width != slice.SourceWidth || texture.height != slice.SourceHeight))
                throw new InvalidDataException("整图实际尺寸与切片清单不一致：" + relative);
            var rect = slice == null ? new Rect(0, 0, texture.width, texture.height) :
                new Rect(slice.X, slice.Y, slice.Width, slice.Height);
            var sprite = Sprite.Create(texture, rect,
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.name = slice == null ? relative : slice.Id;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            sprites[key] = sprite;
            return sprite;
        }
        private static Texture2D LoadTexture(string relative)
        {
            if (textures.TryGetValue(relative, out var texture) && texture != null) return texture;
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = relative, wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            if (!texture.LoadImage(Pack.GetArtworkBytes(relative), true))
            {
                Release(texture);
                throw new InvalidDataException("无法解码外部贴图：" + relative);
            }
            if (texture.width > SystemInfo.maxTextureSize || texture.height > SystemInfo.maxTextureSize)
            {
                Release(texture);
                throw new InvalidDataException("当前设备无法保留整图原始分辨率：" + relative);
            }
            textures[relative] = texture;
            return texture;
        }
    }
}
