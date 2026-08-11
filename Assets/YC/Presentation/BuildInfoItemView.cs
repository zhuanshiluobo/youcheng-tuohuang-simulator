using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public enum BuildInfoItemKind
    {
        CardContent,
        CityStyleCard,
        InfluenceMarker,
        DragGhost,
        PendingBuildGhost
    }

    public sealed class BuildInfoItemView : MonoBehaviour
    {
        [SerializeField] private BuildInfoItemKind kind;
        [SerializeField] private RectTransform root;
        [SerializeField] private Image background;
        [SerializeField] private Button button;
        [SerializeField] private Outline outline;
        [SerializeField] private RawImage rawImage;
        [SerializeField] private Text fallbackText;
        [SerializeField] private RectTransform markerRoot;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private Canvas canvas;
        [SerializeField] private CardPointerInteraction pointerInteraction;

        public BuildInfoItemKind Kind => kind;
        public RectTransform Root => root;
        public Image Background => background;
        public Button Button => button;
        public Outline Outline => outline;
        public RawImage RawImage => rawImage;
        public Text FallbackText => fallbackText;
        public RectTransform MarkerRoot => markerRoot;
        public CanvasGroup CanvasGroup => canvasGroup;
        public Canvas Canvas => canvas;
        public CardPointerInteraction PointerInteraction => pointerInteraction;

        public bool TryValidateAs(BuildInfoItemKind expected, out string reason)
        {
            if (kind != expected || root == null)
            {
                reason = "建设面板模板类型或根引用无效：" + expected;
                return false;
            }

            var valid = false;
            switch (expected)
            {
                case BuildInfoItemKind.CardContent:
                    valid = rawImage != null && fallbackText != null;
                    break;
                case BuildInfoItemKind.CityStyleCard:
                    valid = background != null && button != null && outline != null && rawImage != null &&
                            fallbackText != null && markerRoot != null && pointerInteraction != null;
                    break;
                case BuildInfoItemKind.InfluenceMarker:
                    valid = background != null && background.sprite != null && outline != null;
                    break;
                case BuildInfoItemKind.DragGhost:
                    valid = rawImage != null && fallbackText != null && canvasGroup != null && canvas != null;
                    break;
                case BuildInfoItemKind.PendingBuildGhost:
                    valid = button != null && rawImage != null && canvasGroup != null && pointerInteraction != null;
                    break;
            }

            reason = valid ? string.Empty : "建设面板模板引用不完整：" + expected;
            return valid;
        }
    }
}
