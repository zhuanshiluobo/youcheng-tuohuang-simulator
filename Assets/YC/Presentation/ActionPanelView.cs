using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionPanelView : MonoBehaviour
    {
        [SerializeField] private ActionPanelLayoutProfile layoutProfile;

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

        [Header("盖放角色区")]
        [SerializeField] private RectTransform characterCoverRegion;
        [SerializeField] private RawImage characterCoverPreview;
        [SerializeField] private Button characterCoverConfirmButton;

        [SerializeField] private Button characterCoverCancelButton;
        [SerializeField] private Button characterStrategyButton;
        [SerializeField] private Button characterTacticButton;
        [SerializeField] private Button characterFinishButton;
        [SerializeField] private Button characterPreviewButton;
        [SerializeField] private Text characterRegionHint;
        [SerializeField] private RawImage characterCoveredBack;
        [SerializeField] private GameObject characterEmptyState;
        [SerializeField] private string coverHoverHint = "松手后确认盖放";
        [SerializeField] private string coverPendingHint = "确认盖放此角色牌？";
        [SerializeField] private string characterReadyHint = "选择策略或计谋";
        [SerializeField] private string characterSecondHint = "继续效果或结束使用";
        public Button CharacterCoverCancelButton => characterCoverCancelButton;
        public Button CharacterStrategyButton => characterStrategyButton;
        public Button CharacterTacticButton => characterTacticButton;
        public Button CharacterFinishButton => characterFinishButton;
        public Button CharacterPreviewButton => characterPreviewButton;
        public Text CharacterRegionHint => characterRegionHint;
        public RawImage CharacterCoveredBack => characterCoveredBack;
        public GameObject CharacterEmptyState => characterEmptyState;
        public string CoverHoverHint => coverHoverHint;
        public string CoverPendingHint => coverPendingHint;
        public string CharacterReadyHint => characterReadyHint;
        public string CharacterSecondHint => characterSecondHint;

        public RectTransform CharacterCoverRegion => characterCoverRegion;
        public RawImage CharacterCoverPreview => characterCoverPreview;
        public Button CharacterCoverConfirmButton => characterCoverConfirmButton;

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
        [SerializeField] private Button buildButton;
        [SerializeField] private Button specialButton;
        [SerializeField] private Button endRoundButton;

        public GameObject PanelObject => panelObject;
        public ActionPanelLayoutProfile LayoutProfile => layoutProfile;
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
        public Button BuildButton => buildButton;
        public Button SpecialButton => specialButton;
        public Button EndRoundButton => endRoundButton;

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out reason))
            {
                reason = "行动面板缺少有效的显式布局 Profile：" + reason;
                return false;
            }

            if (characterCoverRegion != null &&
                (characterCoverPreview == null || characterCoverConfirmButton == null ||
                 characterCoverCancelButton == null || characterStrategyButton == null ||
                 characterTacticButton == null || characterFinishButton == null ||
                 characterPreviewButton == null || characterRegionHint == null ||
                 characterCoveredBack == null || characterEmptyState == null))
            {
                reason = "盖放角色区的预览或确认按钮引用不完整。";
                return false;
            }

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
