using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using YC.Infrastructure.Lua;
using YC.Application.Presentation;
using YC.Domain.Presentation;

namespace YC.Presentation
{
    /// <summary>仅含值类型的低频诊断快照；字节量是缓存条目最后观测的对象估算，不是OS/GPU归属。</summary>
    public struct ArtworkMemorySnapshot
    {
        public bool Enabled, CurrentValuesAvailable;
        public long ObservationEpoch, ClearGeneration, TrackingFailures, LastSampleObservationEpoch;
        public int CachedTextureEntries, CachedSpriteEntries, UnknownTextureEntries;
        public long KnownTextureBytes;
        public long CacheDecodeSuccesses, CacheDecodeFailures, CacheTextureRebuilds;
        public long SpriteCreateSuccesses, SpriteCreateFailures, SpriteRebuilds;
        public long TextureReleaseRequests, SpriteReleaseRequests;
        public long ValidationDecodeSuccesses, ValidationDecodeFailures, ValidationTextureReleaseRequests;
        public long CacheDecodedEncodedBytes, ValidationDecodedEncodedBytes;
        public long PositiveSizeSamples, NonPositiveSizeSamples, MeasurementFailures;
        public bool LastWasValidation;
        public int LastWidth, LastHeight, LastFormat, LastMipmapCount, LastEncodedBytes;
        // 0=尚未测量，1=正值，2=非正值/不可确认能力，3=测量异常；未知时字节字段为0。
        public int LastSizeStatus;
        public long LastNativeBytes;
    }

    internal struct ArtworkTextureMemorySample
    {
        internal int SizeStatus;
        internal long NativeBytes;
    }

    /// <summary>只保存原缓存的字符串键与纯值；不保存纹理、Sprite、编码数据或catalog。</summary>
    internal sealed class ArtworkMemoryCounters
    {
        private ArtworkMemorySnapshot value;
        private readonly Dictionary<string, ArtworkTextureMemorySample> cachedSizes =
            new Dictionary<string, ArtworkTextureMemorySample>(StringComparer.Ordinal);
        internal bool Enabled => value.Enabled;

        internal bool BeginObservation()
        {
            if (value.Enabled) return false;
            cachedSizes.Clear();
            value.KnownTextureBytes = 0; value.UnknownTextureEntries = 0;
            value.Enabled = true; value.CurrentValuesAvailable = true;
            unchecked { value.ObservationEpoch++; }
            return true;
        }
        internal void Disable()
        {
            value.Enabled = false; value.CurrentValuesAvailable = false;
            cachedSizes.Clear();
            value.KnownTextureBytes = 0; value.UnknownTextureEntries = 0;
        }
        internal ArtworkMemorySnapshot Read(int textures, int sprites)
        {
            var result = value;
            if (result.CurrentValuesAvailable)
            { result.CachedTextureEntries = textures; result.CachedSpriteEntries = sprites; }
            return result;
        }
        internal void MarkUnknown(string key) => Store(key, default);
        internal void Store(string key, ArtworkTextureMemorySample sample)
        {
            if (!Enabled) return;
            try
            {
                if (cachedSizes.TryGetValue(key, out var previous))
                {
                    if (previous.SizeStatus == 1) value.KnownTextureBytes -= previous.NativeBytes;
                    else value.UnknownTextureEntries--;
                }
                cachedSizes[key] = sample;
                if (sample.SizeStatus == 1) value.KnownTextureBytes += sample.NativeBytes;
                else value.UnknownTextureEntries++;
            }
            catch (Exception)
            {
                // 诊断元数据失败不能改变原加载/释放的结果；当前值失效，累计观测仍保留。
                unchecked { value.TrackingFailures++; }
                Disable();
            }
        }
        internal void Cleared()
        {
            if (!Enabled) return;
            cachedSizes.Clear();
            value.KnownTextureBytes = 0; value.UnknownTextureEntries = 0;
            unchecked { value.ClearGeneration++; }
        }
        internal ArtworkTextureMemorySample Decoded(Texture2D texture, int encodedBytes, bool validation)
        {
            if (!Enabled) return default;
            unchecked
            {
                if (validation) { value.ValidationDecodeSuccesses++; value.ValidationDecodedEncodedBytes += Math.Max(0, encodedBytes); }
                else { value.CacheDecodeSuccesses++; value.CacheDecodedEncodedBytes += Math.Max(0, encodedBytes); }
            }
            value.LastWasValidation = validation; value.LastEncodedBytes = encodedBytes;
            value.LastSampleObservationEpoch = value.ObservationEpoch;
            value.LastWidth = value.LastHeight = value.LastMipmapCount = 0; value.LastFormat = -1;
            value.LastNativeBytes = 0; value.LastSizeStatus = 0;
            try
            {
                value.LastWidth = texture.width; value.LastHeight = texture.height;
                value.LastFormat = (int)texture.format; value.LastMipmapCount = texture.mipmapCount;
                long bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture);
                if (bytes > 0)
                { value.LastNativeBytes = bytes; value.LastSizeStatus = 1; unchecked { value.PositiveSizeSamples++; } }
                else { value.LastSizeStatus = 2; unchecked { value.NonPositiveSizeSamples++; } }
            }
            catch (Exception)
            { value.LastSizeStatus = 3; unchecked { value.MeasurementFailures++; } }
            return new ArtworkTextureMemorySample { SizeStatus = value.LastSizeStatus, NativeBytes = value.LastNativeBytes };
        }
        internal void DecodeFailed(bool validation)
        {
            if (!Enabled) return;
            unchecked { if (validation) value.ValidationDecodeFailures++; else value.CacheDecodeFailures++; }
        }
        internal void TextureRebuilt(bool existed)
        { if (Enabled && existed) unchecked { value.CacheTextureRebuilds++; } }
        internal void SpriteCreated(bool existed)
        {
            if (!Enabled) return;
            unchecked { value.SpriteCreateSuccesses++; if (existed) value.SpriteRebuilds++; }
        }
        internal void SpriteFailed()
        { if (Enabled) unchecked { value.SpriteCreateFailures++; } }
        internal void ReleaseRequested(bool texture, bool validation = false)
        {
            if (!Enabled) return;
            unchecked
            {
                if (!texture) value.SpriteReleaseRequests++;
                else if (validation) value.ValidationTextureReleaseRequests++;
                else value.TextureReleaseRequests++;
            }
        }
    }

    /// <summary>Unity 只负责内容根路径和贴图解码；规则定义与脚本加载属于 Infrastructure。</summary>
    public static class ExternalContentRuntime
    {
        private static ExternalContentPack pack;
        private static string coreRoot, registryRoot;
        public static string DefaultPresentationRegistryRoot => Path.Combine(UnityEngine.Application.persistentDataPath, "Presentation");
        private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private static ArtworkMemoryCounters memoryCounters;
        /// <summary>主线程显式启用；默认关闭。重复启用不重建基线；禁用冻结累计值，重新启用另起观察代际。</summary>
        public static void SetMemoryDiagnosticsEnabled(bool enabled)
        {
            if (!enabled) { memoryCounters?.Disable(); return; }
            if (memoryCounters == null) memoryCounters = new ArtworkMemoryCounters();
            if (!memoryCounters.BeginObservation()) return;
            // 仅遍历本类现有纹理键一次；已有对象大小未知，不触发Pack或重新测量/解码。
            foreach (var pair in textures) memoryCounters.MarkUnknown(pair.Key);
        }
        /// <summary>主线程O(1)纯值复制。条目数量包含尚未被原路径发现的失效对象；不是存活对象扫描。</summary>
        public static ArtworkMemorySnapshot ReadMemoryDiagnostics() =>
            memoryCounters == null ? default : memoryCounters.Read(textures.Count, sprites.Count);
        // Definitions返回全量深克隆。只在冻结包更换时提取展示字段，不缓存规则、玩家或行动许可。
        private sealed class ArtworkBinding
        {
            public readonly string Path;
            public readonly ExternalArtworkSlice Slice;
            public ArtworkBinding(ExternalContentDefinition definition)
            { Path = definition.Artwork; Slice = definition.ArtworkSlice; }
        }
        private static ExternalContentPack artworkPack;
        private static Dictionary<(string Type, string Id), ArtworkBinding> installedArtwork;

        private static ArtworkBinding FindInstalledArtwork(string type, string id)
        {
            using var lookup = Workflows.UiRegressionMetrics.Measure("artwork.definition_lookup");
            var currentPack = Pack;
            if (!ReferenceEquals(artworkPack, currentPack))
            {
                using var build = Workflows.UiRegressionMetrics.Measure("artwork.definition_index_build");
                var next = new Dictionary<(string Type, string Id), ArtworkBinding>();
                foreach (var definition in currentPack.Definitions)
                {
                    var key = (definition.ContentType, definition.DefinitionId);
                    // 与原FirstOrDefault一致：只读启用声明，保留同键首项；活动别名仍走原回退。
                    if (definition.Enabled && !next.ContainsKey(key)) next.Add(key, new ArtworkBinding(definition));
                }
                installedArtwork = next;
                artworkPack = currentPack;
            }
            return installedArtwork.TryGetValue((type, id), out var result) ? result : null;
        }
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
                    ConfigureRegistered(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"), DefaultPresentationRegistryRoot);
                }
                return pack;
            }
        }
        /// <summary>与首次启动相同的JSON登记入口；成功校验后才替换当前冻结包。</summary>
        public static void ConfigureRegistered(string contentRoot, string presentationRegistryRoot)
        {
            ExternalContentPack.ConfigureRegistered(contentRoot, presentationRegistryRoot, candidate =>
            {
                try { new GameplayTextResolver(candidate.Presentation).Precompile(); }
                catch (GameplayTextFormatException error)
                {
                    candidate.Presentation.TryGetTemplateSource(error.TextKey, out string source);
                    throw new InvalidDataException("presentation_template_invalid:" + source + ":" + error.TextKey + "@" + error.Position + ":" + error.Message, error);
                }
                GameplayIconResourceResolver.Validate(candidate.Presentation);
            });
            Reset(); pack = ExternalContentPack.Current;
            coreRoot = Path.GetFullPath(contentRoot); registryRoot = Path.GetFullPath(presentationRegistryRoot);
        }
        public static void Reload() => ConfigureRegistered(coreRoot ?? Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"),
            registryRoot ?? DefaultPresentationRegistryRoot);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            foreach (var sprite in sprites.Values) Release(sprite);
            sprites.Clear();
            installedArtwork = null; artworkPack = null;
            foreach (var texture in textures.Values) Release(texture);
            textures.Clear(); memoryCounters?.Cleared();
            GameplayIconResourceResolver.Clear(); pack = null; coreRoot = null; registryRoot = null;
        }

        /// <summary>仅供正式离局加载器在旧场景全部卸载、新菜单未激活时调用；保留冻结内容和图标。</summary>
        internal static bool ReleaseUnloadedGameplayArtwork()
        {
            if (sprites.Count == 0 && textures.Count == 0) return false;
            foreach (var sprite in sprites.Values) Release(sprite);
            sprites.Clear();
            foreach (var texture in textures.Values) Release(texture);
            textures.Clear();
            memoryCounters?.Cleared();
            return true;
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
            if (memoryCounters != null && memoryCounters.Enabled)
            {
                if (value is Texture2D) memoryCounters.ReleaseRequested(true);
                else if (value is Sprite) memoryCounters.ReleaseRequested(false);
            }
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
            // 本局实例解析为冻结 definitionId；只读取已安装素材，不改变全局活动牌池。
            var artwork = FindInstalledArtwork(type, id);
            if (artwork != null) return LoadSprite(artwork.Path, artwork.Slice);
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

        /// <summary>只读已安装定义声明的卡面；查看不启用定义或创建实例。</summary>
        public static Sprite GetDeclaredArtworkSprite(string type, string id)
        {
            var artwork = FindInstalledArtwork(type, id);
            return artwork == null ? null : LoadSprite(artwork.Path, artwork.Slice);
        }

        /// <summary>按真实面身份读取已登记正面，不能降级为另一面。</summary>
        public static Sprite GetDeclaredCardArtwork(string definitionId, string faceId = "")
        {
            var definition = Pack.Definitions.FirstOrDefault(d => d.Enabled && d.DefinitionId == definitionId);
            if (definition == null) return null;
            if (string.IsNullOrEmpty(faceId)) return LoadSprite(definition.Artwork, definition.ArtworkSlice);
            var face = definition.Data["faces"]?.FirstOrDefault(f => (string)f["faceId"] == faceId);
            var artwork = (string)face?["artwork"];
            return string.IsNullOrEmpty(artwork) ? null : LoadSprite(artwork, null);
        }

        private static Sprite LoadSprite(string relative, ExternalArtworkSlice slice)
        {
            using var measurement = Workflows.UiRegressionMetrics.Measure("artwork.sprite");
            var key = slice == null ? "full:" + relative : "slice:" + slice.Id;
            bool existed = sprites.TryGetValue(key, out var cached);
            if (existed && cached != null && cached.texture != null) return cached;
            Release(cached);
            var texture = LoadTexture(relative);
            if (slice != null && (texture.width != slice.SourceWidth || texture.height != slice.SourceHeight))
                throw new InvalidDataException("整图实际尺寸与切片清单不一致：" + relative);
            var rect = slice == null ? new Rect(0, 0, texture.width, texture.height) :
                new Rect(slice.X, slice.Y, slice.Width, slice.Height);
            try
            {
                var sprite = Sprite.Create(texture, rect,
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                sprite.name = slice == null ? relative : slice.Id;
                sprite.hideFlags = HideFlags.HideAndDontSave;
                sprites[key] = sprite;
                memoryCounters?.SpriteCreated(existed);
                return sprite;
            }
            catch { memoryCounters?.SpriteFailed(); throw; }
        }
        private static Texture2D LoadTexture(string relative)
        {
            bool existed = textures.TryGetValue(relative, out var texture);
            if (existed && texture != null) return texture;
            if (existed) memoryCounters?.MarkUnknown(relative);
            using var measurement = Workflows.UiRegressionMetrics.Measure("artwork.decode_cold");
            ArtworkTextureMemorySample memorySample = default;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = relative, wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var encoded = Pack.GetArtworkBytes(relative);
                int encodedBytes = memoryCounters != null && memoryCounters.Enabled && encoded != null ? encoded.Length : -1;
                if (!texture.LoadImage(encoded, true))
                {
                    Release(texture);
                    throw new InvalidDataException("无法解码外部贴图：" + relative);
                }
                if (texture.width > SystemInfo.maxTextureSize || texture.height > SystemInfo.maxTextureSize)
                {
                    Release(texture);
                    throw new InvalidDataException("当前设备无法保留整图原始分辨率：" + relative);
                }
                if (memoryCounters != null && memoryCounters.Enabled)
                    memorySample = memoryCounters.Decoded(texture, encodedBytes, false);
            }
            catch { memoryCounters?.DecodeFailed(false); throw; }
            textures[relative] = texture;
            memoryCounters?.Store(relative, memorySample);
            memoryCounters?.TextureRebuilt(existed);
            return texture;
        }
    }
}
