using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public enum ExpandableInfoItemKind
    {
        Module,
        Row,
        ImagePreview,
        CardStrip,
        CardThumbnail,
        DragGhost
    }

    public sealed class ExpandableInfoItemView : MonoBehaviour
    {
        [SerializeField] private ExpandableInfoItemKind kind;
        [SerializeField] private RectTransform root;
        [SerializeField] private LayoutElement layoutElement;
        [SerializeField] private Image background;
        [SerializeField] private Outline outline;
        [SerializeField] private Button button;
        [SerializeField] private Text primaryText;
        [SerializeField] private RectTransform contentRoot;
        [SerializeField] private RectTransform previewFrame;
        [SerializeField] private RawImage rawImage;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private CardPointerInteraction pointerInteraction;

        public ExpandableInfoItemKind Kind => kind;
        public RectTransform Root => root;
        public LayoutElement LayoutElement => layoutElement;
        public Image Background => background;
        public Outline Outline => outline;
        public Button Button => button;
        public Text PrimaryText => primaryText;
        public RectTransform ContentRoot => contentRoot;
        public RectTransform PreviewFrame => previewFrame;
        public RawImage RawImage => rawImage;
        public CanvasGroup CanvasGroup => canvasGroup;
        public CardPointerInteraction PointerInteraction => pointerInteraction;

        public bool TryValidateAs(ExpandableInfoItemKind expected, out string reason)
        {
            if (kind != expected || root == null)
            {
                reason = "信息面板模板类型或根引用无效：" + expected;
                return false;
            }

            var valid = false;
            switch (expected)
            {
                case ExpandableInfoItemKind.Module:
                    valid = layoutElement != null && button != null && primaryText != null && contentRoot != null;
                    break;
                case ExpandableInfoItemKind.Row:
                    valid = layoutElement != null && background != null && outline != null &&
                            button != null && primaryText != null;
                    break;
                case ExpandableInfoItemKind.ImagePreview:
                    valid = layoutElement != null && previewFrame != null && rawImage != null;
                    break;
                case ExpandableInfoItemKind.CardStrip:
                    valid = layoutElement != null && contentRoot != null;
                    break;
                case ExpandableInfoItemKind.CardThumbnail:
                    valid = rawImage != null && button != null && outline != null && pointerInteraction != null;
                    break;
                case ExpandableInfoItemKind.DragGhost:
                    valid = rawImage != null && outline != null && canvasGroup != null;
                    break;
            }

            reason = valid ? string.Empty : "信息面板模板引用不完整：" + expected;
            return valid;
        }
    }
}
