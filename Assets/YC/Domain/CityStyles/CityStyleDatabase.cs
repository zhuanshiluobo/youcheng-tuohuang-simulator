using System.Collections.Generic;

namespace YC.Domain.CityStyles
{
    public static class CityStyleDatabase
    {
        public const string MilitaryIndustrialArea = "city_style_military_industrial_area";
        public const string MobilizationSupportSystem = "city_style_mobilization_support_system";
        public const string CompositePowerSystem = "city_style_composite_power_system";
        public const string MaterialRelayStation = "city_style_material_relay_station";
        public const string SourceStoneIndustrialHub = "city_style_source_stone_industrial_hub";
        public const string EfficientMobileManagementSystem = "city_style_efficient_mobile_management_system";

        public static readonly List<string> DefaultSupplyIds = new List<string>
        {
            MilitaryIndustrialArea,
            MobilizationSupportSystem,
            CompositePowerSystem,
            MaterialRelayStation,
            SourceStoneIndustrialHub,
            EfficientMobileManagementSystem
        };

        public static readonly List<string> PresentationSupplyIds = new List<string>
        {
            MilitaryIndustrialArea,
            MobilizationSupportSystem,
            CompositePowerSystem,
            MaterialRelayStation,
            SourceStoneIndustrialHub,
            EfficientMobileManagementSystem
        };

        private static CityStylePatternCell Cell(int rowOffset, int columnOffset, params string[] allowedFacilityColors)
        {
            return new CityStylePatternCell
            {
                RowOffset = rowOffset,
                ColumnOffset = columnOffset,
                AllowedFacilityColors = new List<string>(allowedFacilityColors)
            };
        }

        private static readonly Dictionary<string, CityStyleDefinition> Definitions =
            new Dictionary<string, CityStyleDefinition>
            {
                {
                    MilitaryIndustrialArea,
                    new CityStyleDefinition
                    {
                        CityStyleId = MilitaryIndustrialArea,
                        Name = "军工化区域",
                        Level = 2,
                        Score = 2,
                        Description = "同一横排相邻布局：蓝/黄设施 + 红色设施。",
                        MaxDeclarationsPerPlayer = int.MaxValue,
                        DeclarationRequirement = new CityStyleRequirement
                        {
                            RequiredFacilityCount = 2,
                            RequiredPatternCells =
                            {
                                Cell(0, 0, "blue", "yellow"),
                                Cell(0, 1, "red")
                            }
                        }
                    }
                },
                {
                    MobilizationSupportSystem,
                    new CityStyleDefinition
                    {
                        CityStyleId = MobilizationSupportSystem,
                        Name = "动员配套体系",
                        Level = 2,
                        Score = 3,
                        Description = "同一横排相邻布局：黄色设施 + 黄色设施 + 红色设施。",
                        MaxDeclarationsPerPlayer = int.MaxValue,
                        DeclarationRequirement = new CityStyleRequirement
                        {
                            RequiredFacilityCount = 3,
                            RequiredPatternCells =
                            {
                                Cell(0, 0, "yellow"),
                                Cell(0, 1, "yellow"),
                                Cell(0, 2, "red")
                            }
                        }
                    }
                },
                {
                    CompositePowerSystem,
                    new CityStyleDefinition
                    {
                        CityStyleId = CompositePowerSystem,
                        Name = "复合动力系统",
                        Level = 2,
                        Score = 3,
                        Description = "2x2 局部布局：上方黄色；下方红色 + 黄色。",
                        MaxDeclarationsPerPlayer = int.MaxValue,
                        DeclarationRequirement = new CityStyleRequirement
                        {
                            RequiredFacilityCount = 3,
                            RequiredPatternCells =
                            {
                                Cell(0, 0, "yellow"),
                                Cell(1, 0, "red"),
                                Cell(1, 1, "yellow")
                            }
                        }
                    }
                },
                {
                    MaterialRelayStation,
                    new CityStyleDefinition
                    {
                        CityStyleId = MaterialRelayStation,
                        Name = "物资中继站",
                        Level = 2,
                        Score = 2,
                        Description = "同一横排相邻布局：蓝/红设施 + 黄色设施。",
                        MaxDeclarationsPerPlayer = 1,
                        DeclarationRequirement = new CityStyleRequirement
                        {
                            RequiredFacilityCount = 2,
                            RequiredPatternCells =
                            {
                                Cell(0, 0, "blue", "red"),
                                Cell(0, 1, "yellow")
                            }
                        }
                    }
                },
                {
                    SourceStoneIndustrialHub,
                    new CityStyleDefinition
                    {
                        CityStyleId = SourceStoneIndustrialHub,
                        Name = "源石工业中枢",
                        Level = 2,
                        Score = 6,
                        Description = "3 行阶梯布局：上方蓝色；中间红色 + 蓝色；下方黄色 + 黄色 + 红色。",
                        MaxDeclarationsPerPlayer = 2,
                        DeclarationRequirement = new CityStyleRequirement
                        {
                            RequiredFacilityCount = 6,
                            RequiredPatternCells =
                            {
                                Cell(0, 0, "blue"),
                                Cell(1, 0, "red"),
                                Cell(1, 1, "blue"),
                                Cell(2, 0, "yellow"),
                                Cell(2, 1, "yellow"),
                                Cell(2, 2, "red")
                            }
                        }
                    }
                },
                {
                    EfficientMobileManagementSystem,
                    new CityStyleDefinition
                    {
                        CityStyleId = EfficientMobileManagementSystem,
                        Name = "高效移动管理体系",
                        Level = 2,
                        Score = 7,
                        Description = "3 行阶梯布局：上方黄色；中间红色 + 黄色；下方蓝色 + 蓝色 + 红色。",
                        MaxDeclarationsPerPlayer = 2,
                        DeclarationRequirement = new CityStyleRequirement
                        {
                            RequiredFacilityCount = 6,
                            RequiredPatternCells =
                            {
                                Cell(0, 0, "yellow"),
                                Cell(1, 0, "red"),
                                Cell(1, 1, "yellow"),
                                Cell(2, 0, "blue"),
                                Cell(2, 1, "blue"),
                                Cell(2, 2, "red")
                            }
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
