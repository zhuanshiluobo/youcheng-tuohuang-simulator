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
            foreach (var texture in textures.Values) if (texture != null) UnityEngine.Object.Destroy(texture);
            textures.Clear(); pack = null;
        }
        public static Texture2D GetArtwork(string type, string id)
        {
            string relative = Pack.FindArtwork(type, id);
            return relative == null ? null : LoadTexture(relative);
        }
        public static Texture2D GetSharedArtwork(string id)
        {
            string relative = Pack.FindSharedArtwork(id);
            return relative == null ? null : LoadTexture(relative);
        }
        private static Texture2D LoadTexture(string relative)
        {
            if (textures.TryGetValue(relative, out var texture) && texture != null) return texture;
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = relative, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (!texture.LoadImage(Pack.GetArtworkBytes(relative), true))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidDataException("无法解码外部贴图：" + relative);
            }
            textures[relative] = texture;
            return texture;
        }
    }
}
