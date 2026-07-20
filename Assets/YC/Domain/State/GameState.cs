using System;
using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Domain.State
{
    [Serializable]
    public sealed class GameState
    {
        public string GameId = string.Empty;
        public GamePhase Phase = GamePhase.Setup;
        public int Round = 0;
        public int MaxRounds = 8;
        public int StartPlayerId = -1;
        public int LastFederalCouncilBuilderThisRoundPlayerId = -1;
        public int CurrentPlayerId = -1;
        public int ActionRound = 0;
        public bool UseSeatTurnOrder;
        public string MapId = string.Empty;
        public int EventDeckSeed = YC.Domain.Cards.EventDeckService.DefaultSeed;
        public List<PlayerState> Players = new List<PlayerState>();
        public MapRuntimeState Map = new MapRuntimeState();
        public DeckRuntimeState Decks = new DeckRuntimeState();
        public PendingChoiceState PendingChoice;
        public PendingCardSessionState PendingCardSession;
        public PendingCharacterEffectState PendingCharacterEffect;
        public List<DelayedCharacterEffectState> DelayedCharacterEffects = new List<DelayedCharacterEffectState>();
        public FinalScoringState FinalScoring;
        public List<GameLogEntry> Logs = new List<GameLogEntry>();

        public PlayerState FindPlayer(int playerId)
        {
            return Players.Find(player => player.PlayerId == playerId);
        }

        public bool HasPendingChoice()
        {
            return (PendingChoice != null && PendingChoice.IsValid()) ||
                   (PendingCardSession != null && PendingCardSession.IsValid()) ||
                   (PendingCharacterEffect != null && PendingCharacterEffect.IsValid());
        }
    }

    [Serializable]
    public sealed class PendingCharacterEffectState
    {
        public string ChoiceType = string.Empty;
        public int PlayerId;
        public string CardId = string.Empty;
        public string SourceCommandId = string.Empty;
        public List<string> RemainingCardIds = new List<string>();
        public List<string> ResolvedCardIds = new List<string>();
        public List<string> OptionIds = new List<string>();
        public bool ResolveTinManStrategyAfterRecall;
        public bool ResolveTinManTacticAfterStrategy;
        public bool TinManPurchasePureOriginium12;
        public bool TinManPurchasePureOriginium15;
        public string RemainingEffectMode = string.Empty;
        public bool IsSecondEffect;

        public bool IsValid()
        {
            return PlayerId > 0 &&
                   !string.IsNullOrEmpty(ChoiceType) &&
                   !string.IsNullOrEmpty(CardId) &&
                   OptionIds != null &&
                   OptionIds.Count > 0 &&
                   (ChoiceType != YC.Domain.Cards.CharacterPendingChoiceTypes.TinManDiscard ||
                    (RemainingCardIds != null && RemainingCardIds.Count > 0));
        }
    }

    [Serializable]
    public sealed class DelayedCharacterEffectState
    {
        public string EffectType = string.Empty;
        public int PlayerId;
        public string CardId = string.Empty;
    }

    [Serializable]
    public sealed class PlayerState
    {
        public int PlayerId;
        public string Name = string.Empty;
        public PlayerColor Color;
        public int Score;
        public int InfluenceSupply = 30;
        public bool HasScoreTrackMarker;
        public string CityLocationId = string.Empty;
        public bool HasMovedCityThisRound;
        public bool HasCollectedResourcesThisRound;
        public int ResourceCollectionStartGoldVoucher = -1;
        public bool ActedMainActionThisTurn;
        public bool UsedCharacterThisRound;
        public ResourceSet Resources = new ResourceSet();
        public List<string> HandCardIds = new List<string>();
        public List<string> CoveredCharacterCardIds = new List<string>();
        public List<string> DiscardCardIds = new List<string>();
        public List<string> BuiltFacilityIds = new List<string>();
        public List<string> DeclaredCityStyleIds = new List<string>();
        public List<CityStyleDeclarationState> DeclaredCityStyles = new List<CityStyleDeclarationState>();
        public List<string> UsedSpecialActionIdsThisRound = new List<string>();
        public string CoveredCharacterCardId = string.Empty;
    }

    [Serializable]
    public sealed class CityStyleDeclarationState
    {
        public string InfluenceMarkerId = string.Empty;
        public string CityStyleId = string.Empty;
        public string MarkerArea = "declared";
        public string UnlockedSpecialActionId = string.Empty;
        public int RemainingSpecialActionUses;
        public List<string> UsedFacilityIds = new List<string>();
        public List<int> UsedCityBoardSlotIndexes = new List<int>();
    }

    [Serializable]
    public sealed class MapRuntimeState
    {
        public List<string> OpenLocationIds = new List<string>();
        public List<string> RoadRouteIds = new List<string>();
        public List<string> RemovedFromGameCardIds = new List<string>();
        public List<InfluencePlacement> Influences = new List<InfluencePlacement>();
        public List<FacilityPlacement> Facilities = new List<FacilityPlacement>();
        public List<ResourceTokenState> ResourceTokens = new List<ResourceTokenState>();
    }

    [Serializable]
    public sealed class InfluencePlacement
    {
        public int PlayerId;
        public string SlotId = string.Empty;
        public string LocationId = string.Empty;
        public string RouteId = string.Empty;
    }

    [Serializable]
    public sealed class FacilityPlacement
    {
        public int PlayerId;
        public string FacilityCardId = string.Empty;
        public string LocationId = string.Empty;
        public int CityBoardSlotIndex = -1;
    }

    [Serializable]
    public sealed class ResourceTokenState
    {
        public string LocationId = string.Empty;
        public ResourceType ResourceType;
        public int Amount = 1;
    }

    [Serializable]
    public sealed class DeckRuntimeState
    {
        public List<string> CharacterDeck = new List<string>();
        public List<string> CharacterDiscard = new List<string>();
        public List<string> EventDeckGreen = new List<string>();
        public List<string> EventDeckYellow = new List<string>();
        public List<string> EventDeckRed = new List<string>();
        public List<CardPoolState> CardPools = new List<CardPoolState>();
        public List<string> FacilityDeck = new List<string>();
        public List<string> FacilitySupply = new List<string>();
        public List<string> CityStyleSupply = new List<string>();
    }

    [Serializable]
    public sealed class PendingChoiceState
    {
        public string ChoiceId = string.Empty;
        public int PlayerId;
        public string ChoiceType = string.Empty;
        public string CardId = string.Empty;
        public string TargetId = string.Empty;
        public List<string> OptionIds = new List<string>();
        public string SourceCommandId = string.Empty;

        public bool IsValid()
        {
            return PlayerId > 0 &&
                   !string.IsNullOrEmpty(ChoiceType) &&
                   !string.IsNullOrEmpty(CardId) &&
                   OptionIds != null &&
                   OptionIds.Count > 0;
        }
    }

    [Serializable]
    public sealed class CardPoolState
    {
        public string PoolId = string.Empty;
        public List<string> RemainingCardIds = new List<string>();
    }

    [Serializable]
    public sealed class PendingCardSessionState
    {
        public string SessionId = string.Empty;
        public string ScenarioId = string.Empty;
        public string ChoiceType = string.Empty;
        public string PoolId = string.Empty;
        public string CardId = string.Empty;
        public int PlayerId;
        public string TargetId = string.Empty;
        public List<string> OptionIds = new List<string>();
        public string SourceCommandId = string.Empty;
        public List<StringKeyValuePair> ContextData = new List<StringKeyValuePair>();

        public bool IsValid()
        {
            return PlayerId > 0 &&
                   !string.IsNullOrEmpty(ScenarioId) &&
                   !string.IsNullOrEmpty(ChoiceType) &&
                   !string.IsNullOrEmpty(CardId) &&
                   OptionIds != null &&
                   OptionIds.Count > 0;
        }
    }

    [Serializable]
    public sealed class StringKeyValuePair
    {
        public string Key = string.Empty;
        public string Value = string.Empty;
    }

    [Serializable]
    public sealed class GameLogEntry
    {
        public int Sequence;
        public string CommandId = string.Empty;
        public int PlayerId = -1;
        public string Message = string.Empty;
    }

    [Serializable]
    public sealed class FinalScoringState
    {
        public bool IsResolved;
        public List<FinalPlayerScoreState> PlayerScores = new List<FinalPlayerScoreState>();
        public List<FinalRegionScoreState> RegionScores = new List<FinalRegionScoreState>();
        public List<int> WinnerPlayerIds = new List<int>();
        public string TiebreakSummary = string.Empty;
    }

    [Serializable]
    public sealed class FinalPlayerScoreState
    {
        public int PlayerId;
        public int BaseScore;
        public int RegionScore;
        public int ResourceScore;
        public int FacilityScore;
        public int CityStyleScore;
        public int TotalScore;
        public int GoldVoucherTiebreaker;
        public int PureOriginiumTiebreaker;
        public ResourceSet RemainingResources = new ResourceSet();
        public List<string> ControlledRegionIds = new List<string>();
    }

    [Serializable]
    public sealed class FinalRegionScoreState
    {
        public string RegionId = string.Empty;
        public int ScoreValue;
        public int ControllerPlayerId = -1;
        public List<PlayerInfluenceCountState> InfluenceCounts = new List<PlayerInfluenceCountState>();
    }

    [Serializable]
    public sealed class PlayerInfluenceCountState
    {
        public int PlayerId;
        public int Count;
    }
}
