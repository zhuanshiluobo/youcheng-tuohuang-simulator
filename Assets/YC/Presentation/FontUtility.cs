using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public static class FontUtility
    {
        private const string BuiltinArialResourceName = "Arial.ttf";
        private static readonly string[] DefaultCjkProjectFontResourcePaths =
        {
            "Fonts/CJK/NotoSansCJKsc-Regular",
            "Fonts/Latin/NotoSans-Regular"
        };
        private static readonly string[] DefaultLatinProjectFontResourcePaths =
        {
            "Fonts/Latin/NotoSans-Regular",
            "Fonts/CJK/NotoSansCJKsc-Regular"
        };
        private static readonly string[] CjkFallback =
        {
            "SimHei",
            "Microsoft YaHei",
            "Arial Unicode MS",
            "Arial"
        };
        private static readonly string[] LatinFallback =
        {
            "Arial",
            "Segoe UI",
            "Microsoft YaHei",
            "SimHei"
        };
        private static readonly Dictionary<Font, FontFamily> ManagedFontFamilies = new Dictionary<Font, FontFamily>();
        private static readonly Dictionary<Font, string> ManagedFontOrigins = new Dictionary<Font, string>();
        private static string[] cjkProjectFontResourcePaths = DefaultCjkProjectFontResourcePaths;
        private static string[] latinProjectFontResourcePaths = DefaultLatinProjectFontResourcePaths;
        private static Font cjkFont;
        private static Font latinFont;
        private static FontRefreshDriver refreshDriver;
        private static bool runtimeHooksInstalled;
        private static bool refreshingTextRenderers;
        private static bool pendingManagedTextRefresh;
        private static bool pendingManagedFontRecreate;

        public static Font GetCjkFont(int fontSize)
        {
            return GetFont(ref cjkFont, cjkProjectFontResourcePaths, CjkFallback, FontFamily.Cjk, fontSize);
        }

        public static Font GetLatinFont(int fontSize)
        {
            return GetFont(ref latinFont, latinProjectFontResourcePaths, LatinFallback, FontFamily.Latin, fontSize);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeState()
        {
            Font.textureRebuilt -= OnFontTextureRebuilt;
            cjkProjectFontResourcePaths = DefaultCjkProjectFontResourcePaths;
            latinProjectFontResourcePaths = DefaultLatinProjectFontResourcePaths;
            cjkFont = null;
            latinFont = null;
            ManagedFontFamilies.Clear();
            ManagedFontOrigins.Clear();
            refreshDriver = null;
            runtimeHooksInstalled = false;
            refreshingTextRenderers = false;
            pendingManagedTextRefresh = false;
            pendingManagedFontRecreate = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallRuntimeHooks()
        {
            if (runtimeHooksInstalled)
            {
                return;
            }

            Font.textureRebuilt += OnFontTextureRebuilt;

            var driverObject = new GameObject("YC Font Refresh Driver");
            Object.DontDestroyOnLoad(driverObject);
            refreshDriver = driverObject.AddComponent<FontRefreshDriver>();
            runtimeHooksInstalled = true;
        }

        private static Font GetFont(
            ref Font cachedFont,
            string[] projectFontResourcePaths,
            string[] fallbackNames,
            FontFamily family,
            int fontSize)
        {
            fontSize = Mathf.Max(1, fontSize);
            if (cachedFont != null)
            {
                return cachedFont;
            }

            string origin;
            cachedFont = LoadProjectFont(projectFontResourcePaths, out origin);
            if (cachedFont == null)
            {
                cachedFont = Font.CreateDynamicFontFromOSFont(fallbackNames, fontSize);
                if (cachedFont != null)
                {
                    origin = "SystemDynamic:" + string.Join(" > ", fallbackNames);
                }
            }

            if (cachedFont == null)
            {
                cachedFont = Resources.GetBuiltinResource<Font>(BuiltinArialResourceName);
                if (cachedFont != null)
                {
                    origin = "Builtin:" + BuiltinArialResourceName;
                }
            }

            if (cachedFont != null)
            {
                ManagedFontFamilies[cachedFont] = family;
                ManagedFontOrigins[cachedFont] = origin;
            }

            return cachedFont;
        }

        private static void OnFontTextureRebuilt(Font font)
        {
            if (refreshingTextRenderers || font == null || !ManagedFontFamilies.ContainsKey(font))
            {
                return;
            }

            RequestManagedTextRefresh(false);
        }

        internal static void RecreateManagedFonts()
        {
            if (UnityEngine.Application.isPlaying)
            {
                RequestManagedTextRefresh(true);
                return;
            }

            RefreshManagedTextRenderers(true);
        }

        internal static bool HasInvalidManagedFontTexture()
        {
            foreach (var font in ManagedFontFamilies.Keys)
            {
                if (!IsFontTextureValid(font))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string GetManagedFontOrigin(Font font)
        {
            if (font == null)
            {
                return string.Empty;
            }

            string origin;
            return ManagedFontOrigins.TryGetValue(font, out origin) ? origin : string.Empty;
        }

        internal static ManagedFontHealthCheckReport RunManagedFontHealthCheck(bool simulateInvalidTexture)
        {
            var before = CaptureManagedFontSnapshot("BeforeSnapshot");
            var invalidTextureDetected = HasInvalidManagedFontTexture();
            var recreateFonts = simulateInvalidTexture || invalidTextureDetected;
            if (recreateFonts)
            {
                RecreateManagedFonts();
            }

            var after = CaptureManagedFontSnapshot("AfterSnapshot");
            var snapshotBuilder = new StringBuilder();
            snapshotBuilder.AppendLine("长挂机字体自检快照");
            snapshotBuilder.AppendLine("SimulatedInvalidTexture: " + simulateInvalidTexture);
            snapshotBuilder.AppendLine("InvalidManagedTextureDetected: " + invalidTextureDetected);
            snapshotBuilder.AppendLine("RecreatedManagedFonts: " + recreateFonts);
            snapshotBuilder.AppendLine("ManagedFontCountBefore: " + before.ManagedFontCount);
            snapshotBuilder.AppendLine("ManagedTextCountBefore: " + before.ManagedTextCount);
            snapshotBuilder.AppendLine("ManagedFontCountAfter: " + after.ManagedFontCount);
            snapshotBuilder.AppendLine("ManagedTextCountAfter: " + after.ManagedTextCount);
            snapshotBuilder.Append(before.Text);
            snapshotBuilder.Append(after.Text);

            return new ManagedFontHealthCheckReport
            {
                SimulatedInvalidTexture = simulateInvalidTexture,
                InvalidManagedTextureDetected = invalidTextureDetected,
                RecreatedManagedFonts = recreateFonts,
                ManagedFontCountBefore = before.ManagedFontCount,
                ManagedTextCountBefore = before.ManagedTextCount,
                ManagedFontCountAfter = after.ManagedFontCount,
                ManagedTextCountAfter = after.ManagedTextCount,
                Snapshot = snapshotBuilder.ToString()
            };
        }

        internal static void RefreshManagedTextRenderers(bool recreateFonts)
        {
            if (refreshingTextRenderers)
            {
                return;
            }

            refreshingTextRenderers = true;
            try
            {
                var texts = Object.FindObjectsOfType<Text>(true);
                var textCount = texts == null ? 0 : texts.Length;
                var families = textCount > 0 ? new FontFamily[textCount] : null;
                var managed = textCount > 0 ? new bool[textCount] : null;

                for (var i = 0; i < textCount; i++)
                {
                    var text = texts[i];
                    FontFamily family;
                    if (text != null && text.font != null && ManagedFontFamilies.TryGetValue(text.font, out family))
                    {
                        managed[i] = true;
                        families[i] = family;
                    }
                }

                if (recreateFonts)
                {
                    cjkFont = null;
                    latinFont = null;
                    ManagedFontFamilies.Clear();
                    ManagedFontOrigins.Clear();
                }

                if (textCount == 0)
                {
                    return;
                }

                for (var i = 0; i < textCount; i++)
                {
                    if (!managed[i] || texts[i] == null)
                    {
                        continue;
                    }

                    var text = texts[i];
                    if (recreateFonts)
                    {
                        text.font = families[i] == FontFamily.Latin
                            ? GetLatinFont(text.fontSize)
                            : GetCjkFont(text.fontSize);
                    }

                    text.FontTextureChanged();
                    text.SetAllDirty();
                }

                Canvas.ForceUpdateCanvases();
            }
            finally
            {
                refreshingTextRenderers = false;
            }
        }

        internal static void FlushPendingManagedTextRefresh()
        {
            if (!pendingManagedTextRefresh || refreshingTextRenderers)
            {
                return;
            }

            var recreateFonts = pendingManagedFontRecreate;
            pendingManagedTextRefresh = false;
            pendingManagedFontRecreate = false;
            RefreshManagedTextRenderers(recreateFonts);
        }

        private static void RequestManagedTextRefresh(bool recreateFonts)
        {
            pendingManagedTextRefresh = true;
            pendingManagedFontRecreate |= recreateFonts;
        }
        private static ManagedFontSnapshot CaptureManagedFontSnapshot(string label)
        {
            var snapshot = new ManagedFontSnapshot();
            snapshot.ManagedFontCount = ManagedFontFamilies.Count;

            var builder = new StringBuilder();
            builder.AppendLine(label + ":");
            builder.AppendLine("TrackedFonts:");

            var listedFonts = new HashSet<Font>();
            AppendTrackedFont(builder, "CjkCache", cjkFont, listedFonts);
            AppendTrackedFont(builder, "LatinCache", latinFont, listedFonts);
            foreach (var pair in ManagedFontFamilies)
            {
                if (pair.Key == null || listedFonts.Contains(pair.Key))
                {
                    continue;
                }

                AppendTrackedFont(builder, "ManagedOnly", pair.Key, listedFonts);
            }

            if (listedFonts.Count == 0)
            {
                builder.AppendLine("- <none>");
            }

            builder.AppendLine("ManagedTexts:");
            var texts = Object.FindObjectsOfType<Text>(true);
            if (texts != null)
            {
                for (var i = 0; i < texts.Length; i++)
                {
                    var text = texts[i];
                    FontFamily family;
                    if (text == null || text.font == null || !ManagedFontFamilies.TryGetValue(text.font, out family))
                    {
                        continue;
                    }

                    snapshot.ManagedTextCount++;
                    builder.AppendLine("- path=" + GetHierarchyPath(text.transform) +
                                       ", family=" + family +
                                       ", fontSize=" + text.fontSize +
                                       ", bestFit=" + text.resizeTextForBestFit +
                                       ", enabled=" + text.enabled +
                                       ", activeInHierarchy=" + text.gameObject.activeInHierarchy +
                                       ", fontId=" + text.font.GetInstanceID() +
                                       ", texture=" + DescribeTexture(GetFontTexture(text.font)) +
                                       ", text=\"" + FormatTextSample(text.text) + "\"");
                }
            }

            if (snapshot.ManagedTextCount == 0)
            {
                builder.AppendLine("- <none>");
            }

            snapshot.Text = builder.ToString();
            return snapshot;
        }

        private static void AppendTrackedFont(StringBuilder builder, string source, Font font, HashSet<Font> listedFonts)
        {
            if (font == null)
            {
                builder.AppendLine("- source=" + source + ", font=<null>");
                return;
            }

            listedFonts.Add(font);
            FontFamily family;
            var tracked = ManagedFontFamilies.TryGetValue(font, out family);
            builder.AppendLine("- source=" + source +
                               ", tracked=" + tracked +
                               ", family=" + (tracked ? family.ToString() : "Unknown") +
                               ", origin=" + GetManagedFontOrigin(font) +
                               ", name=" + font.name +
                               ", id=" + font.GetInstanceID() +
                               ", dynamic=" + font.dynamic +
                               ", material=" + DescribeMaterial(font.material) +
                               ", texture=" + DescribeTexture(GetFontTexture(font)));
        }

        private static bool IsFontTextureValid(Font font)
        {
            return font != null && font.material != null && font.material.mainTexture != null;
        }

        private static Font LoadProjectFont(string[] projectFontResourcePaths, out string origin)
        {
            origin = null;
            if (projectFontResourcePaths == null)
            {
                return null;
            }

            for (var i = 0; i < projectFontResourcePaths.Length; i++)
            {
                var path = projectFontResourcePaths[i];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var font = Resources.Load<Font>(path);
                if (font == null)
                {
                    continue;
                }

                origin = "ProjectResource:" + path;
                return font;
            }

            return null;
        }

        private static Texture GetFontTexture(Font font)
        {
            return font != null && font.material != null
                ? font.material.mainTexture
                : null;
        }

        private static string DescribeMaterial(Material material)
        {
            return material == null
                ? "null"
                : material.name + "#" + material.GetInstanceID();
        }

        private static string DescribeTexture(Texture texture)
        {
            if (texture == null)
            {
                return "null";
            }

            return texture.name +
                   "#" + texture.GetInstanceID() +
                   " (" + texture.width + "x" + texture.height + ")";
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return "<null>";
            }

            var builder = new StringBuilder(transform.name);
            var current = transform.parent;
            while (current != null)
            {
                builder.Insert(0, current.name + "/");
                current = current.parent;
            }

            return builder.ToString();
        }

        private static string FormatTextSample(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var normalized = value.Replace('\r', ' ').Replace('\n', ' ');
            return normalized.Length <= 64
                ? normalized
                : normalized.Substring(0, 64) + "...";
        }

        private enum FontFamily
        {
            Cjk,
            Latin
        }

        private sealed class ManagedFontSnapshot
        {
            public int ManagedFontCount;
            public int ManagedTextCount;
            public string Text = string.Empty;
        }
    }

    public sealed class ManagedFontHealthCheckReport
    {
        public bool SimulatedInvalidTexture { get; internal set; }
        public bool InvalidManagedTextureDetected { get; internal set; }
        public bool RecreatedManagedFonts { get; internal set; }
        public int ManagedFontCountBefore { get; internal set; }
        public int ManagedTextCountBefore { get; internal set; }
        public int ManagedFontCountAfter { get; internal set; }
        public int ManagedTextCountAfter { get; internal set; }
        public string Snapshot { get; internal set; }
    }

    internal sealed class FontRefreshDriver : MonoBehaviour
    {
        private const float HealthCheckSeconds = 30f;
        private float nextHealthCheckAt;

        private void Update()
        {
            FontUtility.FlushPendingManagedTextRefresh();

            if (Time.unscaledTime < nextHealthCheckAt)
            {
                return;
            }

            nextHealthCheckAt = Time.unscaledTime + HealthCheckSeconds;
            if (FontUtility.HasInvalidManagedFontTexture())
            {
                FontUtility.RecreateManagedFonts();
            }

            FontUtility.FlushPendingManagedTextRefresh();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                FontUtility.RecreateManagedFonts();
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!pauseStatus)
            {
                FontUtility.RecreateManagedFonts();
            }
        }
    }
}
