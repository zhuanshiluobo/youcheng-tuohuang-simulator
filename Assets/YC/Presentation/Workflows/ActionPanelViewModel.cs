using YC.Domain.Rules;

namespace YC.Presentation.Workflows
{
    public sealed class ActionPanelViewModel
    {
        public ActionPanelViewModel(
            InteractionMode mode,
            string currentPlayerLabel,
            string phaseLabel,
            string statusText,
            bool hasLocalPlayer,
            PlayerColor localPlayerColor,
            int remainingInfluence,
            bool canUseCharacter,
            bool canDeclareCityStyle,
            bool canDeploy,
            bool canDispatch,
            bool canExplore,
            bool canMoveCity,
            bool canBuild,
            bool canUseSpecialAction,
            bool canEndAction,
            bool isWaitingForOtherPlayers)
        {
            Mode = mode;
            CurrentPlayerLabel = currentPlayerLabel ?? string.Empty;
            PhaseLabel = phaseLabel ?? string.Empty;
            StatusText = statusText ?? string.Empty;
            HasLocalPlayer = hasLocalPlayer;
            LocalPlayerColor = localPlayerColor;
            RemainingInfluence = remainingInfluence;
            CanUseCharacter = canUseCharacter;
            CanDeclareCityStyle = canDeclareCityStyle;
            CanDeploy = canDeploy;
            CanDispatch = canDispatch;
            CanExplore = canExplore;
            CanMoveCity = canMoveCity;
            CanBuild = canBuild;
            CanUseSpecialAction = canUseSpecialAction;
            CanEndAction = canEndAction;
            IsWaitingForOtherPlayers = isWaitingForOtherPlayers;
        }

        public InteractionMode Mode { get; private set; }
        public string CurrentPlayerLabel { get; private set; }
        public string PhaseLabel { get; private set; }
        public string StatusText { get; private set; }
        public bool HasLocalPlayer { get; private set; }
        public PlayerColor LocalPlayerColor { get; private set; }
        public int RemainingInfluence { get; private set; }
        public bool CanUseCharacter { get; private set; }
        public bool CanDeclareCityStyle { get; private set; }
        public bool CanDeploy { get; private set; }
        public bool CanDispatch { get; private set; }
        public bool CanExplore { get; private set; }
        public bool CanMoveCity { get; private set; }
        public bool CanBuild { get; private set; }
        public bool CanUseSpecialAction { get; private set; }
        public bool CanEndAction { get; private set; }
        public bool IsWaitingForOtherPlayers { get; private set; }
    }
}
