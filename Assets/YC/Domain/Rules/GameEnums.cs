namespace YC.Domain.Rules
{
    public enum PlayerColor
    {
        Red,
        Blue,
        Green,
        Yellow
    }

    public enum ResourceType
    {
        Originium,
        OriginiumShard,
        Iron,
        PureOriginium,
        GoldVoucher
    }

    public enum GamePhase
    {
        Setup,
        Entrance,
        RoundStart,
        CharacterCover,
        ActionRound1,
        ActionRound2,
        ResourceCollection,
        Cleanup,
        FinalScoring,
        GameOver
    }

    public enum EventColor
    {
        Green,
        Yellow,
        Red
    }

    public enum CardType
    {
        Character,
        Event,
        Facility,
        CityStyle
    }

    public enum GameCommandKind
    {
        ChooseStartPlayer,
        ChooseInitialLocation,
        ResolveEntranceEvent,
        CoverCharacterCard,
        DeployInfluence,
        DispatchInfluence,
        ExploreLocation,
        MoveCity,
        BuildFacility,
        UseCharacterCard,
        DeclareCityStyle,
        UseSpecialAction,
        CollectResource,
        ResolvePendingChoice,
        EndAction
    }

    public enum CommandErrorCode
    {
        None,
        WrongPhase,
        NotCurrentPlayer,
        InvalidPlayer,
        InvalidTarget,
        InvalidSource,
        InsufficientResource,
        InsufficientInfluence,
        OccupiedSlot,
        ClosedLocation,
        NoRoute,
        PendingChoiceRequired,
        UnknownCommand
    }

    public enum GameEventKind
    {
        PhaseChanged,
        RoundAdvanced,
        PlayerAdvanced,
        ResourceChanged,
        InfluencePlaced,
        InfluenceMoved,
        CityMoved,
        FacilityBuilt,
        CardMoved,
        ScoreChanged,
        ChoiceOpened,
        ChoiceResolved,
        LogOnly
    }
}
