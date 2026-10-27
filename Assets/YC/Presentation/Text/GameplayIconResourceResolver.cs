using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using YC.Domain.Presentation;

namespace YC.Presentation
{
    /// <summary>正文与HUD共用的冻结资源适配；不读磁盘，不改主题资产。</summary>
    public static class GameplayIconResourceResolver
    {
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private static ArtworkMemoryCounters memoryCounters;
        /// <summary>主线程显式启用。已有Sprite键对应的纹理大小记未知；不触发catalog读取或纹理解码。</summary>
        public static void SetMemoryDiagnosticsEnabled(bool enabled)
        {
            if (!enabled) { memoryCounters?.Disable(); return; }
            if (memoryCounters == null) memoryCounters = new ArtworkMemoryCounters();
            if (!memoryCounters.BeginObservation()) return;
            foreach (var pair in sprites) memoryCounters.MarkUnknown(pair.Key);
        }
        /// <summary>每个缓存Sprite拥有一张独立解码纹理；值为条目最后观测估算，不是实时存活扫描。</summary>
        public static ArtworkMemorySnapshot ReadMemoryDiagnostics() =>
            memoryCounters == null ? default : memoryCounters.Read(sprites.Count, sprites.Count);
        public static int Revision { get; private set; }
        private static string scope;
        public static void UseCatalog(IGameplayTextCatalog catalog)
        {
            if (scope == catalog?.ContentHash) return;
            Clear(); scope = catalog?.ContentHash;
        }
        public static bool TryResolve(IGameplayTextCatalog catalog, GameplayIconCatalog theme,
            GameplayIconDefinition definition, GameplayIconTone tone, out Sprite sprite)
        {
            sprite = null;
            if (definition == null) return false;
            string key = GameplayIconCatalog.AssetKeyFor(definition, tone);
            if (catalog is IGameplayIconResourceCatalog resources && resources.TryGetIconResource(key, out var resource))
            {
                string identity = catalog.ContentHash + "\n" + resource.OwnerPackId + "\n" + resource.AssetKey + "\n" + resource.Sha256;
                bool existed = sprites.TryGetValue(identity, out sprite);
                if (existed && sprite != null && sprite.texture != null) return true;
                if (existed) memoryCounters?.MarkUnknown(identity);
                if (sprite != null) { var previousTexture = sprite.texture; Release(sprite); Release(previousTexture); }
                var texture = Decode(resource, false, out var memorySample);
                try
                {
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect);
                    sprite.name = resource.AssetKey; sprite.hideFlags = HideFlags.HideAndDontSave;
                    sprites[identity] = sprite;
                    memoryCounters?.Store(identity, memorySample);
                    memoryCounters?.TextureRebuilt(existed);
                    memoryCounters?.SpriteCreated(existed);
                    return true;
                }
                catch { memoryCounters?.SpriteFailed(); Release(texture); throw; }
            }
            return theme != null && theme.TryResolve(definition, tone, out sprite);
        }
        public static void Validate(IGameplayTextCatalog catalog)
        {
            if (!(catalog is IGameplayIconResourceCatalog resources)) return;
            foreach (var resource in resources.IconResources) Release(Decode(resource, true, out _), true);
        }
        private static Texture2D Decode(GameplayIconResource resource, bool validation, out ArtworkTextureMemorySample memorySample)
        {
            memorySample = default;
            Texture2D texture;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = resource.AssetKey,
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            }
            // 构造/初始化失败原本直接抛出；这里只记数，不补销毁或包装异常。
            catch { memoryCounters?.DecodeFailed(validation); throw; }
            try
            {
                var encoded = resource.CopyBytes();
                int encodedBytes = memoryCounters != null && memoryCounters.Enabled && encoded != null ? encoded.Length : -1;
                if (!texture.LoadImage(encoded, true) || texture.width > SystemInfo.maxTextureSize || texture.height > SystemInfo.maxTextureSize)
                    throw new InvalidDataException("presentation_image_decode_failed:" + resource.OwnerPackId + "/" + resource.RelativePath + ":" + resource.AssetKey);
                if (memoryCounters != null && memoryCounters.Enabled)
                    memorySample = memoryCounters.Decoded(texture, encodedBytes, validation);
                return texture;
            }
            catch (Exception error)
            {
                memoryCounters?.DecodeFailed(validation);
                Release(texture, validation);
                if (error is InvalidDataException) throw;
                throw new InvalidDataException("presentation_image_decode_failed:" + resource.OwnerPackId + "/" + resource.RelativePath + ":" + resource.AssetKey, error);
            }
        }
        public static void Clear()
        {
            foreach (var sprite in sprites.Values)
            { if (sprite != null) { var texture = sprite.texture; Release(sprite); Release(texture); } }
            sprites.Clear(); scope = null; unchecked { Revision++; }
            memoryCounters?.Cleared();
        }
        private static void Release(UnityEngine.Object value, bool validation = false)
        {
            if (value == null) return;
            if (memoryCounters != null && memoryCounters.Enabled)
            {
                if (value is Texture2D) memoryCounters.ReleaseRequested(true, validation);
                else if (value is Sprite) memoryCounters.ReleaseRequested(false);
            }
            if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
