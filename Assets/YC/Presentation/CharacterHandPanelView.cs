using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class CharacterHandPanelView : MonoBehaviour
    {
        [SerializeField] private CharacterHandPanel controller;
        [SerializeField] private RectTransform root;
        [SerializeField] private RectTransform handCardsRoot;
        [SerializeField] private RectTransform handDropArea;
        [SerializeField] private Button discardButton;
        [SerializeField] private Text discardCountText;
        [SerializeField] private GameObject discardOverlayObject;
        [SerializeField] private RectTransform discardOverlayPanel;
        [SerializeField] private Button discardCloseButton;
        [SerializeField] private WindowCloseInputHandler discardCloseInputHandler;
        [SerializeField] private Text overlayHandTitle;
        [SerializeField] private Text overlayDiscardTitle;
        [SerializeField] private RectTransform overlayHandContent;
        [SerializeField] private RectTransform overlayDiscardContent;
        [SerializeField] private GridLayoutGroup overlayHandGrid;
        [SerializeField] private GridLayoutGroup overlayDiscardGrid;
        [SerializeField] private GameObject overlayHandEmptyObject;
        [SerializeField] private GameObject overlayDiscardEmptyObject;
        [SerializeField] private CharacterHandCardView handCardTemplate;
        [SerializeField] private CharacterHandCardView overlayCardTemplate;
        [SerializeField] private RectTransform dragGhostTemplate;
        [SerializeField] private RawImage dragGhostImage;
        [SerializeField] private CanvasGroup dragGhostCanvasGroup;
        [SerializeField] private Material discardGrayscaleMaterial;

        public CharacterHandPanel Controller => controller;
        public RectTransform Root => root;
        public RectTransform HandCardsRoot => handCardsRoot;
        public Animation HandCardsAnimation =>
            handCardsRoot != null ? handCardsRoot.GetComponent<Animation>() : null;
        public RectTransform HandDropArea => handDropArea;
        public Button DiscardButton => discardButton;
        public Text DiscardCountText => discardCountText;
        public GameObject DiscardOverlayObject => discardOverlayObject;
        public RectTransform DiscardOverlayPanel => discardOverlayPanel;
        public Button DiscardCloseButton => discardCloseButton;
        public WindowCloseInputHandler DiscardCloseInputHandler => discardCloseInputHandler;
        public Text OverlayHandTitle => overlayHandTitle;
        public Text OverlayDiscardTitle => overlayDiscardTitle;
        public RectTransform OverlayHandContent => overlayHandContent;
        public RectTransform OverlayDiscardContent => overlayDiscardContent;
        public GridLayoutGroup OverlayHandGrid => overlayHandGrid;
        public GridLayoutGroup OverlayDiscardGrid => overlayDiscardGrid;
        public GameObject OverlayHandEmptyObject => overlayHandEmptyObject;
        public GameObject OverlayDiscardEmptyObject => overlayDiscardEmptyObject;
        public CharacterHandCardView HandCardTemplate => handCardTemplate;
        public CharacterHandCardView OverlayCardTemplate => overlayCardTemplate;
        public RectTransform DragGhostTemplate => dragGhostTemplate;
        public RawImage DragGhostImage => dragGhostImage;
        public CanvasGroup DragGhostCanvasGroup => dragGhostCanvasGroup;
        public Material DiscardGrayscaleMaterial => discardGrayscaleMaterial;

        public bool IsBoundTo(CharacterHandPanel candidate)
        {
            return candidate != null && controller == candidate;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (controller == null || root == null || handCardsRoot == null ||
                HandCardsAnimation == null || HandCardsAnimation.clip == null ||
                HandCardsAnimation.GetClip("CharacterHandReturn") == null || handDropArea == null ||
                discardButton == null || discardCountText == null || discardOverlayObject == null ||
                discardOverlayPanel == null || discardCloseButton == null || discardCloseInputHandler == null ||
                overlayHandTitle == null || overlayDiscardTitle == null || overlayHandContent == null ||
                overlayDiscardContent == null || overlayHandGrid == null || overlayDiscardGrid == null ||
                overlayHandEmptyObject == null || overlayDiscardEmptyObject == null ||
                handCardTemplate == null || overlayCardTemplate == null || dragGhostTemplate == null ||
                dragGhostImage == null || dragGhostCanvasGroup == null || discardGrayscaleMaterial == null)
            {
                reason = "手牌面板固定 View 引用不完整。";
                return false;
            }

            if (!handCardTemplate.TryValidateConfiguration(out reason) ||
                !overlayCardTemplate.TryValidateConfiguration(out reason))
            {
                return false;
            }

            if (handCardTemplate.gameObject.activeSelf || overlayCardTemplate.gameObject.activeSelf ||
                dragGhostTemplate.gameObject.activeSelf || discardOverlayObject.activeSelf)
            {
                reason = "手牌动态模板和弃牌遮罩必须在 Prefab 中默认禁用。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
