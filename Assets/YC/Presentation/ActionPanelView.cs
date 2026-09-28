using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionPanelView : MonoBehaviour
    {

        [Header("行动面板")]
        [SerializeField] private GameObject panelObject;
        [SerializeField] private GameObject mainFaceObject;


        [Header("Main face")]
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
        [SerializeField] private Text buildButtonLabel;
        [SerializeField] private string buildActionLabel = "建设";
        [SerializeField] private string viewFacilitySupplyLabel = "查看设施供应区";
        [SerializeField] private Button specialButton;
        [SerializeField] private Button endRoundButton;

        public GameObject PanelObject => panelObject;
        public GameObject MainFaceObject => mainFaceObject;
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

        public void SetBuildSupplyMode(bool viewing)
        {
            if (buildButtonLabel != null)
                buildButtonLabel.text = viewing ? viewFacilitySupplyLabel : buildActionLabel;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;

            if (panelObject == null || mainFaceObject == null)
            {
                reason = "行动面板固定面引用不完整。";
                return false;
            }

            if (currentPlayerText == null || phaseText == null ||
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
