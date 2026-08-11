using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionPanelView : MonoBehaviour
    {
        [Header("Faces")]
        [SerializeField] private GameObject panelObject;
        [SerializeField] private GameObject mainFaceObject;
        [SerializeField] private GameObject cardFaceObject;

        [Header("Card face")]
        [SerializeField] private RectTransform cardImageContainer;
        [SerializeField] private Image cardImageContainerBackground;
        [SerializeField] private RawImage cardImage;
        [SerializeField] private Button cardImageButton;
        [SerializeField] private Image cardShade;
        [SerializeField] private Outline cardFaceOutline;
        [SerializeField] private Text cardPlaceholderText;
        [SerializeField] private Text cardTitleText;
        [SerializeField] private Text cardHintText;
        [SerializeField] private Button cardPrimaryButton;
        [SerializeField] private Text cardPrimaryLabel;
        [SerializeField] private Button cardSecondaryButton;
        [SerializeField] private Text cardSecondaryLabel;
        [SerializeField] private Texture2D hintCardTexture;

        [Header("Main face")]
        [SerializeField] private Button flipButton;
        [SerializeField] private Text currentPlayerText;
        [SerializeField] private Text phaseText;
        [SerializeField] private Image localPlayerColorSwatch;
        [SerializeField] private Text remainingInfluenceText;
        [SerializeField] private Text statusText;
        [SerializeField] private Button useCharacterButton;
        [SerializeField] private Button declareCityStyleButton;
        [SerializeField] private Button deployButton;
        [SerializeField] private Button dispatchButton;
        [SerializeField] private Button exploreButton;
        [SerializeField] private Button moveCityButton;
        [SerializeField] private Button endRoundButton;

        public GameObject PanelObject => panelObject;
        public GameObject MainFaceObject => mainFaceObject;
        public GameObject CardFaceObject => cardFaceObject;
        public RectTransform CardImageContainer => cardImageContainer;
        public Image CardImageContainerBackground => cardImageContainerBackground;
        public RawImage CardImage => cardImage;
        public Button CardImageButton => cardImageButton;
        public Image CardShade => cardShade;
        public Outline CardFaceOutline => cardFaceOutline;
        public Text CardPlaceholderText => cardPlaceholderText;
        public Text CardTitleText => cardTitleText;
        public Text CardHintText => cardHintText;
        public Button CardPrimaryButton => cardPrimaryButton;
        public Text CardPrimaryLabel => cardPrimaryLabel;
        public Button CardSecondaryButton => cardSecondaryButton;
        public Text CardSecondaryLabel => cardSecondaryLabel;
        public Texture2D HintCardTexture => hintCardTexture;
        public Button FlipButton => flipButton;
        public Text CurrentPlayerText => currentPlayerText;
        public Text PhaseText => phaseText;
        public Image LocalPlayerColorSwatch => localPlayerColorSwatch;
        public Text RemainingInfluenceText => remainingInfluenceText;
        public Text StatusText => statusText;
        public Button UseCharacterButton => useCharacterButton;
        public Button DeclareCityStyleButton => declareCityStyleButton;
        public Button DeployButton => deployButton;
        public Button DispatchButton => dispatchButton;
        public Button ExploreButton => exploreButton;
        public Button MoveCityButton => moveCityButton;
        public Button EndRoundButton => endRoundButton;

        public bool TryValidateConfiguration(out string reason)
        {
            if (panelObject == null || mainFaceObject == null || cardFaceObject == null)
            {
                reason = "行动面板固定面引用不完整。";
                return false;
            }

            if (cardImageContainer == null || cardImageContainerBackground == null || cardImage == null ||
                cardImageButton == null || cardShade == null || cardFaceOutline == null ||
                cardPlaceholderText == null || cardTitleText == null || cardHintText == null ||
                cardPrimaryButton == null || cardPrimaryLabel == null ||
                cardSecondaryButton == null || cardSecondaryLabel == null || hintCardTexture == null)
            {
                reason = "行动面板卡面引用或固定提示卡纹理不完整。";
                return false;
            }

            if (flipButton == null || currentPlayerText == null || phaseText == null ||
                localPlayerColorSwatch == null || remainingInfluenceText == null || statusText == null ||
                useCharacterButton == null || declareCityStyleButton == null || deployButton == null ||
                dispatchButton == null || exploreButton == null || moveCityButton == null || endRoundButton == null)
            {
                reason = "行动面板主面按钮或状态引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
