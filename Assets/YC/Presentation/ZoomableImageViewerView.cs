using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ZoomableImageViewerView : MonoBehaviour
    {
        [SerializeField] private ZoomableViewerLayoutProfile layoutProfile;
        [SerializeField] private GameObject canvasObject;
        [SerializeField] private GameObject rootObject;
        [SerializeField] private Image rootBackgroundImage;
        [SerializeField] private RectTransform panelTransform;
        [SerializeField] private GameObject expandedContentObject;
        [SerializeField] private RectTransform viewportTransform;
        [SerializeField] private RectTransform imageTransform;
        [SerializeField] private RawImage image;
        [SerializeField] private Text titleText;
        [SerializeField] private Text pageLabel;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button previousButton;
        [SerializeField] private Button nextButton;
        [SerializeField] private Button primaryActionButton;
        [SerializeField] private Text primaryActionLabel;
        [SerializeField] private Button secondaryActionButton;
        [SerializeField] private Text secondaryActionLabel;
        [SerializeField] private Button collapseToggleButton;
        [SerializeField] private Text collapseToggleLabel;
        [SerializeField] private Text collapsedSummaryText;

        public GameObject CanvasObject => canvasObject;
        public ZoomableViewerLayoutProfile LayoutProfile => layoutProfile;
        public GameObject RootObject => rootObject;
        public Image RootBackgroundImage => rootBackgroundImage;
        public RectTransform PanelTransform => panelTransform;
        public GameObject ExpandedContentObject => expandedContentObject;
        public RectTransform ViewportTransform => viewportTransform;
        public RectTransform ImageTransform => imageTransform;
        public RawImage Image => image;
        public Text TitleText => titleText;
        public Text PageLabel => pageLabel;
        public Button CloseButton => closeButton;
        public Button PreviousButton => previousButton;
        public Button NextButton => nextButton;
        public Button PrimaryActionButton => primaryActionButton;
        public Text PrimaryActionLabel => primaryActionLabel;
        public Button SecondaryActionButton => secondaryActionButton;
        public Text SecondaryActionLabel => secondaryActionLabel;
        public Button CollapseToggleButton => collapseToggleButton;
        public Text CollapseToggleLabel => collapseToggleLabel;
        public Text CollapsedSummaryText => collapsedSummaryText;

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out reason))
            {
                reason = "图片查看器缺少有效的显式布局 Profile：" + reason;
                return false;
            }

            if (canvasObject == null || rootObject == null || rootBackgroundImage == null || panelTransform == null ||
                expandedContentObject == null || viewportTransform == null || imageTransform == null ||
                image == null || titleText == null || pageLabel == null)
            {
                reason = "图片查看器固定层级引用不完整。";
                return false;
            }

            if (closeButton == null || previousButton == null || nextButton == null ||
                primaryActionButton == null || primaryActionLabel == null ||
                secondaryActionButton == null || secondaryActionLabel == null ||
                collapseToggleButton == null || collapseToggleLabel == null ||
                collapsedSummaryText == null)
            {
                reason = "图片查看器控件引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public void ApplyViewerName(string viewerName)
        {
            var safeName = string.IsNullOrEmpty(viewerName) ? "Image" : viewerName;
            canvasObject.name = safeName + " Viewer Canvas";
            rootObject.name = safeName + " Viewer";
            panelTransform.name = safeName + " Panel";
            expandedContentObject.name = safeName + " Expanded Content";
            viewportTransform.name = safeName + " Viewport";
            imageTransform.name = safeName + " Image";
            titleText.name = safeName + " Title";
            pageLabel.name = safeName + " Page Label";
            closeButton.name = "Close " + safeName + " Button";
            previousButton.name = "Previous " + safeName + " Button";
            nextButton.name = "Next " + safeName + " Button";
            primaryActionButton.name = "Primary " + safeName + " Action Button";
            secondaryActionButton.name = "Secondary " + safeName + " Action Button";
            collapseToggleButton.name = safeName + " Collapse Toggle Button";
            collapsedSummaryText.name = safeName + " Collapsed Summary";
        }
    }
}
