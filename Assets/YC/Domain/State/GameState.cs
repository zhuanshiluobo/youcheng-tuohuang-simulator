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
        public int CurrentPlayerId = -1;
        public int ActionRound = 0;
        public string MapId = string.Empty;
        public List<PlayerState> Players = new List<PlayerState>();
        public MapRuntimeState Map = new MapRuntimeState();
        public DeckRuntimeState Decks = new DeckRuntimeState();
        public PendingChoiceState PendingChoice;
        public List<GameLogEntry> Logs = new List<GameLogEntry>();

        public PlayerState FindPlayer(int playerId)
        {
            return Players.Find(player => player.PlayerId == playerId);
        }

        public bool HasPendingChoice()
        {
            return PendingChoice != null;
        }
    }

    [Serializable]
    public sealed class PlayerState
    {
        public int PlayerId;
        public string Name = string.Empty;
        public PlayerColor Color;
        public int Score;
        public int InfluenceSupply = 30;
        public string CityLocationId = string.Empty;
        public bool HasMovedCityThisRound;
        public bool ActedMainActionThisTurn;
        public bool UsedCharacterThisRound;
        public ResourceSet Resources = new ResourceSet();
        public List<string> HandCardIds = new List<string>();
        public List<string> CoveredCharacterCardIds = new List<string>();
        public List<string> DiscardCardIds = new List<string>();
        public List<string> BuiltFacilityIds = new List<string>();
        public List<string> DeclaredCityStyleIds = new List<string>();
        public List<string> UsedSpecialActionIdsThisRound = new List<string>();
    }

    [Serializable]
    public sealed class MapRuntimeState
    {
        public List<string> OpenLocationIds = new List<string>();
        public List<string> RoadRouteIds = new List<string>();
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
        public List<string> EventDeck = new List<string>();
        public List<string> EventDiscard = new List<string>();
        public List<string> FacilitySupply = new List<string>();
        public List<string> CityStyleSupply = new List<string>();
    }

    [Serializable]
    public sealed class PendingChoiceState
    {
        public string ChoiceId = string.Empty;
        public int PlayerId;
        public string ChoiceType = string.Empty;
        public List<string> OptionIds = new List<string>();
        public string SourceCommandId = string.Empty;
    }

    [Serializable]
    public sealed class GameLogEntry
    {
        public int Sequence;
        public string CommandId = string.Empty;
        public int PlayerId = -1;
        public string Message = string.Empty;
    }
}
