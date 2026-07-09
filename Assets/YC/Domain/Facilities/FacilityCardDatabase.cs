using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public static class FacilityCardDatabase
    {
        public const string CoreCommandTower = "reserve_001";
        public const string BoroughAdministrativeDistrict = "building_001";
        public const string AffiliatedEnergyFacility = "building_004";
        public const string FederalOffice = "building_007";
        public const string SimpleEngineeringCamp = "building_010";
        public const string LogisticsHub = "building_012";
        public const string SourceStoneRefinery = "building_014";
        public const string UrbanizedArea = "building_015";
        public const string IronRefinery = "building_018";
        public const string TradeDistrict = "building_019";
        public const string CityIndustrialDistrict = "building_022";
        public const string HighPerformancePowerFacility = "building_025";
        public const string OriginiumPurificationPlant = "building_027";
        public const string MiningPowerShovel = "building_029";
        public const string MercenaryCommand = "building_034";
        public const string EscortDispatchCenter = "building_037";
        public const string EquipmentWarehouse = "building_039";
        public const string ExtensionHubBlue = "reserve_002";
        public const string ExtensionHubYellow = "reserve_003";
        public const string ExtensionHubRed = "reserve_004";
        public const string EnterpriseOffice = "facility_enterprise_office";

        private const string FacilityImageRoot = "Assets/YC/Presentation/Resources/CardImages/Facilities/";
        private static readonly string[] ManifestPathParts =
        {
            "StreamingAssets",
            "YC",
            "Data",
            "building_cards_manifest.json"
        };

        private static readonly Dictionary<string, FacilityCardDefinition> LegacyDefinitions =
            new Dictionary<string, FacilityCardDefinition>
            {
                {
                    CoreCommandTower,
                    new FacilityCardDefinition
                    {
                        FacilityId = CoreCommandTower,
                        Name = "核心指挥塔",
                        Score = 0,
                        Unique = true,
                        Color = "rainbow",
                        EffectType = "setup",
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/core_command_tower.jpg"
                    }
                },
                {
                    BoroughAdministrativeDistrict,
                    new FacilityCardDefinition
                    {
                        FacilityId = BoroughAdministrativeDistrict,
                        Name = "城邦行政区",
                        Score = 3,
                        Unique = true,
                        Color = "rainbow",
                        ResourceCost = new ResourceSet { Originium = 3, Iron = 3, OriginiumShard = 3 },
                        GoldVoucherCost = 30,
                        EffectType = "unique",
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/borough_administrative_district.jpg"
                    }
                },
                {
                    AffiliatedEnergyFacility,
                    new FacilityCardDefinition
                    {
                        FacilityId = AffiliatedEnergyFacility,
                        Name = "附属能源设施",
                        Score = 1,
                        Unique = true,
                        Color = "rainbow",
                        ResourceCost = new ResourceSet { Originium = 2, Iron = 3, OriginiumShard = 2 },
                        GoldVoucherCost = 23,
                        EffectType = "entry",
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/affiliated_energy_facility.jpg"
                    }
                },
                {
                    FederalOffice,
                    new FacilityCardDefinition
                    {
                        FacilityId = FederalOffice,
                        Name = "联邦理事处",
                        Score = 0,
                        Unique = true,
                        Color = "rainbow",
                        ResourceCost = new ResourceSet { Originium = 4, OriginiumShard = 2 },
                        GoldVoucherCost = 17,
                        EffectType = "cleanup",
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/federal_office.jpg"
                    }
                },
                {
                    SimpleEngineeringCamp,
                    new FacilityCardDefinition
                    {
                        FacilityId = SimpleEngineeringCamp,
                        Name = "简陋工程营",
                        Score = -1,
                        Unique = true,
                        Color = "rainbow",
                        ResourceCost = new ResourceSet { Originium = 2, OriginiumShard = 1 },
                        GoldVoucherCost = 8,
                        EffectType = "special_action",
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/simple_engineering_camp.jpg"
                    }
                },
                {
                    SourceStoneRefinery,
                    new FacilityCardDefinition
                    {
                        FacilityId = SourceStoneRefinery,
                        Name = "源石精炼厂",
                        Color = "blue,yellow",
                        Score = 0,
                        ResourceCost = new ResourceSet { Originium = 1, Iron = 1, OriginiumShard = 1 },
                        GoldVoucherCost = 10,
                        EffectType = "entry",
                        OnBuiltReward = new ResourceSet { OriginiumShard = 6 },
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/source_stone_refinery.jpg"
                    }
                },
                {
                    UrbanizedArea,
                    new FacilityCardDefinition
                    {
                        FacilityId = UrbanizedArea,
                        Name = "城市化区域",
                        Color = "blue",
                        Score = 1,
                        ResourceCost = new ResourceSet { Originium = 3, Iron = 2 },
                        GoldVoucherCost = 16,
                        EffectType = "scoring",
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/urbanized_area.jpg"
                    }
                },
                {
                    IronRefinery,
                    new FacilityCardDefinition
                    {
                        FacilityId = IronRefinery,
                        Name = "异铁冶炼厂",
                        Color = "blue,red",
                        Score = 1,
                        ResourceCost = new ResourceSet { Originium = 2, Iron = 1, OriginiumShard = 2 },
                        GoldVoucherCost = 15,
                        EffectType = "entry",
                        OnBuiltReward = new ResourceSet { Iron = 4 },
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/iron_refinery.jpg"
                    }
                },
                {
                    TradeDistrict,
                    new FacilityCardDefinition
                    {
                        FacilityId = TradeDistrict,
                        Name = "贸易街区",
                        Color = "blue",
                        Score = 0,
                        ResourceCost = new ResourceSet { Originium = 1, Iron = 1 },
                        GoldVoucherCost = 6,
                        EffectType = "special_action",
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/trade_district.jpg"
                    }
                },
                {
                    OriginiumPurificationPlant,
                    new FacilityCardDefinition
                    {
                        FacilityId = OriginiumPurificationPlant,
                        Name = "固源岩提纯厂",
                        Color = "yellow,red",
                        Score = 0,
                        ResourceCost = new ResourceSet { Originium = 1, Iron = 1, OriginiumShard = 1 },
                        GoldVoucherCost = 10,
                        EffectType = "entry",
                        OnBuiltReward = new ResourceSet { Originium = 7 },
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/originium_purification_plant.jpg"
                    }
                },
                {
                    EquipmentWarehouse,
                    new FacilityCardDefinition
                    {
                        FacilityId = EquipmentWarehouse,
                        Name = "载具仓库",
                        Color = "red",
                        Score = 0,
                        ResourceCost = new ResourceSet { Originium = 1, OriginiumShard = 2 },
                        GoldVoucherCost = 9,
                        EffectType = "special_action",
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/equipment_warehouse.jpg"
                    }
                },
                {
                    EnterpriseOffice,
                    new FacilityCardDefinition
                    {
                        FacilityId = EnterpriseOffice,
                        Name = "企业办事处",
                        Color = "rainbow",
                        Score = 1,
                        ResourceCost = new ResourceSet { Originium = 3, Iron = 1, OriginiumShard = 3 },
                        GoldVoucherCost = 23,
                        EffectType = "entry",
                        ImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Facilities/enterprise_office.jpg"
                    }
                }
            };

        private static readonly Dictionary<string, FacilityCardDefinition> Definitions = BuildDefinitions();

        private static Dictionary<string, FacilityCardDefinition> BuildDefinitions()
        {
            var manifestDefinitions = TryBuildDefinitionsFromManifest();
            return manifestDefinitions != null && manifestDefinitions.Count > 0
                ? manifestDefinitions
                : BuildDefinitionsFallback();
        }

        private static Dictionary<string, FacilityCardDefinition> TryBuildDefinitionsFromManifest()
        {
            var manifestPath = ResolveManifestPath();
            if (string.IsNullOrEmpty(manifestPath))
            {
                return null;
            }

            var root = JObject.Parse(File.ReadAllText(manifestPath));
            var result = new Dictionary<string, FacilityCardDefinition>();
            AddManifestCards(result, root["buildingCards"], false);
            AddManifestCards(result, root["reserveCards"], true);
            return result;
        }

        private static string ResolveManifestPath()
        {
            var candidates = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", Path.Combine(ManifestPathParts)),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "Assets", Path.Combine(ManifestPathParts)),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "Assets", Path.Combine(ManifestPathParts)),
                Path.Combine(System.AppContext.BaseDirectory, Path.Combine(ManifestPathParts))
            };

            for (var i = 0; i < candidates.Length; i++)
            {
                var path = Path.GetFullPath(candidates[i]);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            var baseDirectory = System.AppContext.BaseDirectory;
            if (Directory.Exists(baseDirectory))
            {
                var playerDataDirectories = Directory.GetDirectories(baseDirectory, "*_Data");
                for (var i = 0; i < playerDataDirectories.Length; i++)
                {
                    var path = Path.Combine(playerDataDirectories[i], Path.Combine(ManifestPathParts));
                    if (File.Exists(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }
            }

            return string.Empty;
        }

        private static void AddManifestCards(
            Dictionary<string, FacilityCardDefinition> result,
            JToken cards,
            bool reserveOnly)
        {
            if (cards == null)
            {
                return;
            }

            foreach (var card in cards)
            {
                var id = ReadString(card, "id");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var name = ReadString(card, "name");
                var effect = ReadString(card, "effect");
                var unique = effect.Contains("唯一");
                result[id] = new FacilityCardDefinition
                {
                    FacilityId = id,
                    ManifestId = id,
                    Name = name,
                    Color = ReadString(card, "color"),
                    Score = ReadInt(card, "score"),
                    ResourceCost = ToResourceSet(card["resourceCost"]),
                    GoldVoucherCost = ReadInt(card, "goldVoucherCost"),
                    Unique = unique,
                    UniqueGroupId = unique ? name : string.Empty,
                    EffectType = ResolveEffectType(name),
                    Description = ReadString(card, "description"),
                    EffectText = effect,
                    ReserveOnly = reserveOnly,
                    OnBuiltReward = ResolveOnBuiltReward(name),
                    ImageRelativePath = BuildImageRelativePath(id, reserveOnly)
                };
            }
        }

        private static ResourceSet ToResourceSet(JToken resourceCost)
        {
            if (resourceCost == null || resourceCost.Type == JTokenType.Null)
            {
                return new ResourceSet();
            }

            return new ResourceSet
            {
                Originium = ReadInt(resourceCost, "源岩"),
                OriginiumShard = ReadInt(resourceCost, "源石碎片"),
                Iron = ReadInt(resourceCost, "异铁"),
                PureOriginium = ReadInt(resourceCost, "至纯源石"),
                GoldVoucher = ReadInt(resourceCost, "金券")
            };
        }

        private static ResourceSet ResolveOnBuiltReward(string name)
        {
            switch (name)
            {
                case "源石精炼厂":
                    return new ResourceSet { OriginiumShard = 6 };
                case "异铁冶炼厂":
                    return new ResourceSet { Iron = 4 };
                case "固源岩提纯厂":
                    return new ResourceSet { Originium = 7 };
                default:
                    return new ResourceSet();
            }
        }

        private static string ResolveEffectType(string name)
        {
            switch (name)
            {
                case "城邦行政区":
                    return "unique";
                case "联邦理事处":
                    return "cleanup";
                case "简陋工程营":
                case "贸易街区":
                case "高性能动力设施":
                case "载具仓库":
                    return "special_action";
                case "城市化区域":
                    return "scoring";
                case "城邦工业区":
                    return "discount";
                case "佣兵指挥部":
                case "护航调度中心":
                    return "enterprise";
                case "开采电铲":
                    return "entry_choice";
                case "核心指挥塔":
                    return "setup";
                case "延伸枢纽":
                    return "reserve";
                default:
                    return "entry";
            }
        }

        private static string ReadString(JToken token, string propertyName)
        {
            var value = token == null ? null : token[propertyName];
            return value == null || value.Type == JTokenType.Null ? string.Empty : value.Value<string>();
        }

        private static int ReadInt(JToken token, string propertyName)
        {
            var value = token == null ? null : token[propertyName];
            return value == null || value.Type == JTokenType.Null ? 0 : value.Value<int>();
        }

        private static Dictionary<string, FacilityCardDefinition> BuildDefinitionsFallback()
        {
            var result = new Dictionary<string, FacilityCardDefinition>();

            AddCopies(result, LegacyDefinitions[BoroughAdministrativeDistrict], "rainbow", "building_001", "building_002", "building_003");
            AddCopies(result, LegacyDefinitions[AffiliatedEnergyFacility], "rainbow", "building_004", "building_005", "building_006");
            AddCopies(result, LegacyDefinitions[FederalOffice], "rainbow", "building_007", "building_008", "building_009");
            AddCopies(result, LegacyDefinitions[SimpleEngineeringCamp], "rainbow", "building_010", "building_011");
            AddCopies(result, New(LogisticsHub, "物流枢纽", "blue", 2, new ResourceSet { PureOriginium = 1, GoldVoucher = 2 }, 24, true, "entry"), "blue", "building_012", "building_013");
            AddCopies(result, LegacyDefinitions[SourceStoneRefinery], "blue", "building_014");
            AddCopies(result, LegacyDefinitions[UrbanizedArea], "blue", "building_015", "building_016", "building_017");
            AddCopies(result, LegacyDefinitions[IronRefinery], "blue", "building_018");
            AddCopies(result, LegacyDefinitions[TradeDistrict], "blue", "building_019", "building_020", "building_021");
            AddCopies(result, New(CityIndustrialDistrict, "城邦工业区", "yellow", 2, new ResourceSet { Originium = 4, OriginiumShard = 3, GoldVoucher = 2 }, 24, false, "discount"), "yellow", "building_022", "building_023", "building_024");
            AddCopies(result, New(HighPerformancePowerFacility, "高性能动力设施", "yellow", 0, new ResourceSet { OriginiumShard = 4, Iron = 2 }, 20, false, "special_action"), "yellow", "building_025", "building_026");
            AddCopies(result, LegacyDefinitions[OriginiumPurificationPlant], "yellow", "building_027");
            AddCopies(result, LegacyDefinitions[SourceStoneRefinery], "yellow", "building_028");
            AddCopies(result, New(MiningPowerShovel, "开采电铲", "yellow", 0, new ResourceSet { OriginiumShard = 2, Iron = 1 }, 10, false, "entry_choice"), "yellow", "building_029", "building_030", "building_031");
            AddCopies(result, LegacyDefinitions[IronRefinery], "red", "building_032");
            AddCopies(result, LegacyDefinitions[OriginiumPurificationPlant], "red", "building_033");
            AddCopies(result, New(MercenaryCommand, "佣兵指挥部", "red", 1, new ResourceSet { Originium = 2, Iron = 2, GoldVoucher = 4 }, 18, false, "enterprise"), "red", "building_034", "building_035", "building_036");
            AddCopies(result, New(EscortDispatchCenter, "护航调度中心", "red", 0, new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = 2 }, 18, false, "enterprise"), "red", "building_037", "building_038");
            AddCopies(result, LegacyDefinitions[EquipmentWarehouse], "red", "building_039", "building_040", "building_041");

            result[CoreCommandTower] = Clone(LegacyDefinitions[CoreCommandTower], CoreCommandTower, "rainbow", true);
            result[ExtensionHubBlue] = New(ExtensionHubBlue, "延伸枢纽", "blue", -1, new ResourceSet(), 0, false, "reserve", true);
            result[ExtensionHubYellow] = New(ExtensionHubYellow, "延伸枢纽", "yellow", -1, new ResourceSet(), 0, false, "reserve", true);
            result[ExtensionHubRed] = New(ExtensionHubRed, "延伸枢纽", "red", -1, new ResourceSet(), 0, false, "reserve", true);
            result[EnterpriseOffice] = Clone(LegacyDefinitions[EnterpriseOffice], EnterpriseOffice, "rainbow", false);

            return result;
        }

        private static void AddCopies(
            Dictionary<string, FacilityCardDefinition> result,
            FacilityCardDefinition template,
            string color,
            params string[] facilityIds)
        {
            for (var i = 0; i < facilityIds.Length; i++)
            {
                result[facilityIds[i]] = Clone(template, facilityIds[i], color, false);
            }
        }

        private static FacilityCardDefinition Clone(
            FacilityCardDefinition template,
            string facilityId,
            string color,
            bool reserveOnly)
        {
            return new FacilityCardDefinition
            {
                FacilityId = facilityId,
                Name = template.Name,
                Color = color,
                Score = template.Score,
                ResourceCost = template.ResourceCost.Clone(),
                GoldVoucherCost = template.GoldVoucherCost,
                Unique = template.Unique,
                UniqueGroupId = string.IsNullOrEmpty(template.UniqueGroupId) ? template.FacilityId : template.UniqueGroupId,
                EffectType = template.EffectType,
                Description = template.Description,
                EffectText = template.EffectText,
                ManifestId = facilityId,
                ReserveOnly = reserveOnly,
                OnBuiltReward = template.OnBuiltReward.Clone(),
                ImageRelativePath = BuildImageRelativePath(facilityId, reserveOnly)
            };
        }

        private static FacilityCardDefinition New(
            string facilityId,
            string name,
            string color,
            int score,
            ResourceSet resourceCost,
            int goldVoucherCost,
            bool unique,
            string effectType,
            bool reserveOnly = false)
        {
            return new FacilityCardDefinition
            {
                FacilityId = facilityId,
                Name = name,
                Color = color,
                Score = score,
                ResourceCost = resourceCost,
                GoldVoucherCost = goldVoucherCost,
                Unique = unique,
                UniqueGroupId = facilityId,
                EffectType = effectType,
                ManifestId = facilityId,
                ReserveOnly = reserveOnly,
                ImageRelativePath = BuildImageRelativePath(facilityId, reserveOnly)
            };
        }

        private static string BuildImageRelativePath(string facilityId, bool reserveOnly)
        {
            return string.IsNullOrEmpty(facilityId)
                ? string.Empty
                : FacilityImageRoot + facilityId + ".jpg";
        }

        private static string GetBuildingImageFileName(string facilityId)
        {
            switch (facilityId)
            {
                case "building_001": return "building_001_城邦行政区_r01c01.jpg";
                case "building_002": return "building_002_城邦行政区_r01c02.jpg";
                case "building_003": return "building_003_城邦行政区_r01c03.jpg";
                case "building_004": return "building_004_附属能源设施_r01c04.jpg";
                case "building_005": return "building_005_附属能源设施_r01c05.jpg";
                case "building_006": return "building_006_附属能源设施_r01c06.jpg";
                case "building_007": return "building_007_联邦理事处_r01c07.jpg";
                case "building_008": return "building_008_联邦理事处_r01c08.jpg";
                case "building_009": return "building_009_联邦理事处_r01c09.jpg";
                case "building_010": return "building_010_简陋工程营_r02c01.jpg";
                case "building_011": return "building_011_简陋工程营_r02c02.jpg";
                case "building_012": return "building_012_物流枢纽_r02c03.jpg";
                case "building_013": return "building_013_物流枢纽_r02c04.jpg";
                case "building_014": return "building_014_源石精炼厂_r02c05.jpg";
                case "building_015": return "building_015_城市化区域_r02c06.jpg";
                case "building_016": return "building_016_城市化区域_r02c07.jpg";
                case "building_017": return "building_017_城市化区域_r02c08.jpg";
                case "building_018": return "building_018_异铁冶炼厂_r02c09.jpg";
                case "building_019": return "building_019_贸易街区_r03c01.jpg";
                case "building_020": return "building_020_贸易街区_r03c02.jpg";
                case "building_021": return "building_021_贸易街区_r03c03.jpg";
                case "building_022": return "building_022_城邦工业区_r03c05.jpg";
                case "building_023": return "building_023_城邦工业区_r03c06.jpg";
                case "building_024": return "building_024_城邦工业区_r03c07.jpg";
                case "building_025": return "building_025_高性能动力设施_r03c08.jpg";
                case "building_026": return "building_026_高性能动力设施_r03c09.jpg";
                case "building_027": return "building_027_固源岩提纯厂_r04c01.jpg";
                case "building_028": return "building_028_源石精炼厂_r04c02.jpg";
                case "building_029": return "building_029_开采电铲_r04c03.jpg";
                case "building_030": return "building_030_开采电铲_r04c04.jpg";
                case "building_031": return "building_031_开采电铲_r04c05.jpg";
                case "building_032": return "building_032_异铁冶炼厂_r04c07.jpg";
                case "building_033": return "building_033_固源岩提纯厂_r04c08.jpg";
                case "building_034": return "building_034_佣兵指挥部_r04c09.jpg";
                case "building_035": return "building_035_佣兵指挥部_r05c01.jpg";
                case "building_036": return "building_036_佣兵指挥部_r05c02.jpg";
                case "building_037": return "building_037_护航调度中心_r05c03.jpg";
                case "building_038": return "building_038_护航调度中心_r05c04.jpg";
                case "building_039": return "building_039_载具仓库_r05c05.jpg";
                case "building_040": return "building_040_载具仓库_r05c06.jpg";
                case "building_041": return "building_041_载具仓库_r05c07.jpg";
                default: return string.Empty;
            }
        }

        public static readonly List<string> DefaultSupplyIds = new List<string>
        {
            "building_001", "building_002", "building_003", "building_004", "building_005", "building_006",
            "building_007", "building_008", "building_009", "building_010", "building_011", "building_012",
            "building_013", "building_014", "building_015", "building_016", "building_017", "building_018",
            "building_019", "building_020", "building_021", "building_022", "building_023", "building_024",
            "building_025", "building_026", "building_027", "building_028", "building_029", "building_030",
            "building_031", "building_032", "building_033", "building_034", "building_035", "building_036",
            "building_037", "building_038", "building_039", "building_040", "building_041"
        };

        public static readonly List<string> ReserveIds = new List<string>
        {
            CoreCommandTower,
            ExtensionHubBlue,
            ExtensionHubYellow,
            ExtensionHubRed
        };

        public static bool PlayerHasBuiltUniqueFacility(PlayerState player, FacilityCardDefinition facility)
        {
            if (player == null || facility == null || !facility.Unique)
            {
                return false;
            }

            var uniqueGroupId = GetUniqueGroupId(facility);
            for (var i = 0; i < player.BuiltFacilityIds.Count; i++)
            {
                FacilityCardDefinition builtFacility;
                if (!TryGet(player.BuiltFacilityIds[i], out builtFacility) ||
                    !builtFacility.Unique)
                {
                    continue;
                }

                if (GetUniqueGroupId(builtFacility) == uniqueGroupId)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryGet(string facilityId, out FacilityCardDefinition definition)
        {
            if (string.IsNullOrEmpty(facilityId))
            {
                definition = null;
                return false;
            }

            return Definitions.TryGetValue(facilityId, out definition);
        }

        public static FacilityCardDefinition Get(string facilityId)
        {
            FacilityCardDefinition definition;
            return TryGet(facilityId, out definition) ? definition : null;
        }

        private static string GetUniqueGroupId(FacilityCardDefinition facility)
        {
            return string.IsNullOrEmpty(facility.UniqueGroupId)
                ? facility.FacilityId
                : facility.UniqueGroupId;
        }
    }
}
