using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public enum FontHealthCheckMode
    {
        CheckOnly,
        SimulateRecreate
    }

    public sealed class FontHealthCheckResult
    {
        public FontHealthCheckMode Mode { get; internal set; }
        public bool ProbeObjectsCreated { get; internal set; }
        public bool InvalidManagedTextureDetected { get; internal set; }
        public bool RecreatedManagedFonts { get; internal set; }
        public string Snapshot { get; internal set; }
    }

    public static class FontHealthCheckRunner
    {
        public static FontHealthCheckResult Run(FontHealthCheckMode mode = FontHealthCheckMode.CheckOnly)
        {
            using (var probeScope = FontHealthProbeScope.Create())
            {
                var report = FontUtility.RunManagedFontHealthCheck(mode == FontHealthCheckMode.SimulateRecreate);
                return new FontHealthCheckResult
                {
                    Mode = mode,
                    ProbeObjectsCreated = probeScope.Created,
                    InvalidManagedTextureDetected = report.InvalidManagedTextureDetected,
                    RecreatedManagedFonts = report.RecreatedManagedFonts,
                    Snapshot = BuildSnapshot(mode, probeScope.Created, report)
                };
            }
        }

        private static string BuildSnapshot(
            FontHealthCheckMode mode,
            bool probeCreated,
            ManagedFontHealthCheckReport report)
        {
            return "FontHealthCheckMode: " + mode + "\n" +
                   "ProbeObjectsCreated: " + probeCreated + "\n" +
                   report.Snapshot;
        }

        private sealed class FontHealthProbeScope : System.IDisposable
        {
            private GameObject root;

            public bool Created
            {
                get { return root != null; }
            }

            public static FontHealthProbeScope Create()
            {
                var scope = new FontHealthProbeScope();
                scope.Initialize();
                return scope;
            }

            public void Dispose()
            {
                if (root == null)
                {
                    return;
                }

                if (UnityEngine.Application.isPlaying)
                {
                    Object.Destroy(root);
                }
                else
                {
                    Object.DestroyImmediate(root);
                }

                root = null;
            }

            private void Initialize()
            {
                root = new GameObject("YC Font Health Probe");
                if (Object.FindObjectOfType<EventSystem>() == null)
                {
                    new GameObject("YC Font Health Probe EventSystem", typeof(EventSystem), typeof(StandaloneInputModule))
                        .transform.SetParent(root.transform, false);
                }

                var promptPresenter = PromptPresenter.Build(root.transform);
                promptPresenter.SetPrompt("长挂机字体自检探针：汉字 ABC 123");
                CreateProbeText(
                    promptPresenter.Canvas.transform,
                    "Best Fit Probe",
                    new Vector2(0f, -148f),
                    "长挂机字体健康检查 Best Fit",
                    FontUtility.GetCjkFont(28),
                    true,
                    TextAnchor.MiddleCenter);
                CreateProbeText(
                    promptPresenter.Canvas.transform,
                    "Latin Probe",
                    new Vector2(0f, -228f),
                    "Font health snapshot LATIN 123",
                    FontUtility.GetLatinFont(24),
                    false,
                    TextAnchor.MiddleLeft);
                Canvas.ForceUpdateCanvases();
            }

            private static void CreateProbeText(
                Transform parent,
                string objectName,
                Vector2 anchoredPosition,
                string value,
                Font font,
                bool bestFit,
                TextAnchor alignment)
            {
                var textObject = new GameObject(objectName, typeof(RectTransform), typeof(Text));
                textObject.transform.SetParent(parent, false);

                var rect = textObject.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 1f);
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.sizeDelta = new Vector2(840f, 56f);
                rect.anchoredPosition = anchoredPosition;

                var text = textObject.GetComponent<Text>();
                text.text = value;
                text.alignment = alignment;
                text.color = UiTheme.GoldText;
                text.fontSize = bestFit ? 28 : 24;
                text.resizeTextForBestFit = bestFit;
                text.resizeTextMinSize = 18;
                text.resizeTextMaxSize = bestFit ? 32 : 24;
                text.font = font;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
            }
        }
    }
}