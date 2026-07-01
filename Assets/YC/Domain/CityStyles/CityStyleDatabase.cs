using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Domain.CityStyles
{
    public static class CityStyleDatabase
    {
        public const string SourceStoneIndustrialHub = "city_style_source_stone_industrial_hub";

        public static readonly List<string> DefaultSupplyIds = new List<string>
        {
            SourceStoneIndustrialHub
        };

        private static readonly Dictionary<string, CityStyleDefinition> Definitions =
            new Dictionary<string, CityStyleDefinition>
            {
                {
                    SourceStoneIndustrialHub,
                    new CityStyleDefinition
                    {
                        CityStyleId = SourceStoneIndustrialHub,
                        Name = "源石工业中枢",
                        Level = 2,
                        Score = 6,
                        Description = "同一城市面板横排中至少有 3 个包含源石类成本的设施，并且其中至少 1 个是入场型设施。",
                        DeclarationRequirement = new CityStyleRequirement
                        {
                            RequiredFacilityCount = 3,
                            RequireSameCityBoardRow = true,
                            RequiredEffectTypes = { "entry" },
                            RequiredResourceTypes = { ResourceType.OriginiumShard }
                        }
                    }
                }
            };

        public static IReadOnlyList<CityStyleDefinition> All
        {
            get
            {
                var result = new List<CityStyleDefinition>();
                foreach (var pair in Definitions)
                {
                    result.Add(pair.Value);
                }

                return result;
            }
        }

        public static bool TryGet(string cityStyleId, out CityStyleDefinition definition)
        {
            if (string.IsNullOrEmpty(cityStyleId))
            {
                definition = null;
                return false;
            }

            return Definitions.TryGetValue(cityStyleId, out definition);
        }

        public static CityStyleDefinition Get(string cityStyleId)
        {
            CityStyleDefinition definition;
            return TryGet(cityStyleId, out definition) ? definition : null;
        }
    }
}
