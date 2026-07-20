using System;
using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.CityStyles
{
    [Serializable]
    public sealed class CityStyleDefinition
    {
        public string CityStyleId = string.Empty;
        public string Name = string.Empty;
        public int Level;
        public int Score;
        public string Description = string.Empty;
        public int MaxDeclarationsPerPlayer = 1;
        public string SpecialActionId = string.Empty;
        public ResourceSet DeclarationReward = new ResourceSet();
        public CityStyleRequirement DeclarationRequirement = new CityStyleRequirement();
    }

    public static class CityStyleMarkerAreas
    {
        public const string Declared = "declared";
        public const string Unused = "unused";
        public const string Used = "used";
        public const string UsesTwo = "2";
        public const string UsesOne = "1";
        public const string UsesZero = "0";
    }

    [Serializable]
    public sealed class CityStyleRequirement
    {
        public int RequiredFacilityCount;
        public List<string> RequiredEffectTypes = new List<string>();
        public List<ResourceType> RequiredResourceTypes = new List<ResourceType>();
        public List<int> RequiredCityBoardSlotIndexes = new List<int>();
        public List<CityStylePatternCell> RequiredPatternCells = new List<CityStylePatternCell>();
        public bool RequireSameCityBoardRow;
    }

    [Serializable]
    public sealed class CityStylePatternCell
    {
        public int RowOffset;
        public int ColumnOffset;
        public List<string> AllowedFacilityColors = new List<string>();
    }
}
