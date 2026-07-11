using System;
using System.Collections.Generic;
using YC.Domain.Rules;

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
        public CityStyleRequirement DeclarationRequirement = new CityStyleRequirement();
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
