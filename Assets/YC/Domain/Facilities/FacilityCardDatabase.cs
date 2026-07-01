using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public static class FacilityCardDatabase
    {
        public const string BoroughAdministrativeDistrict = "facility_borough_administrative_district";
        public const string AffiliatedEnergyFacility = "facility_affiliated_energy";
        public const string FederalOffice = "facility_federal_office";
        public const string SimpleEngineeringCamp = "facility_simple_engineering_camp";
        public const string SourceStoneRefinery = "facility_source_stone_refinery";
        public const string UrbanizedArea = "facility_urbanized_area";
        public const string IronRefinery = "facility_iron_refinery";
        public const string TradeDistrict = "facility_trade_district";
        public const string OriginiumPurificationPlant = "facility_originium_purification";
        public const string EquipmentWarehouse = "facility_equipment_warehouse";
        public const string EnterpriseOffice = "facility_enterprise_office";

        private static readonly Dictionary<string, FacilityCardDefinition> Definitions =
            new Dictionary<string, FacilityCardDefinition>
            {
                {
                    BoroughAdministrativeDistrict,
                    new FacilityCardDefinition
                    {
                        FacilityId = BoroughAdministrativeDistrict,
                        Name = "城邦行政区",
                        Score = 3,
                        Unique = true,
                        ResourceCost = new ResourceSet { Originium = 3, Iron = 3, OriginiumShard = 3 },
                        GoldVoucherCost = 30,
                        EffectType = "unique"
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
                        ResourceCost = new ResourceSet { Originium = 2, Iron = 2, OriginiumShard = 3 },
                        GoldVoucherCost = 23,
                        EffectType = "entry"
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
                        ResourceCost = new ResourceSet { Originium = 4, Iron = 2 },
                        GoldVoucherCost = 17,
                        EffectType = "cleanup"
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
                        ResourceCost = new ResourceSet { Originium = 2, Iron = 1, OriginiumShard = 2 },
                        GoldVoucherCost = 8,
                        EffectType = "special_action"
                    }
                },
                {
                    SourceStoneRefinery,
                    new FacilityCardDefinition
                    {
                        FacilityId = SourceStoneRefinery,
                        Name = "源石精炼厂",
                        Score = 0,
                        ResourceCost = new ResourceSet { Originium = 1, Iron = 1, OriginiumShard = 1 },
                        GoldVoucherCost = 10,
                        EffectType = "entry",
                        OnBuiltReward = new ResourceSet { Iron = 6 }
                    }
                },
                {
                    UrbanizedArea,
                    new FacilityCardDefinition
                    {
                        FacilityId = UrbanizedArea,
                        Name = "城市化区域",
                        Score = 1,
                        ResourceCost = new ResourceSet { Originium = 3, OriginiumShard = 2 },
                        GoldVoucherCost = 16,
                        EffectType = "scoring"
                    }
                },
                {
                    IronRefinery,
                    new FacilityCardDefinition
                    {
                        FacilityId = IronRefinery,
                        Name = "异铁冶炼厂",
                        Score = 1,
                        ResourceCost = new ResourceSet { Originium = 2, Iron = 2, OriginiumShard = 1 },
                        GoldVoucherCost = 15,
                        EffectType = "entry",
                        OnBuiltReward = new ResourceSet { OriginiumShard = 4 }
                    }
                },
                {
                    TradeDistrict,
                    new FacilityCardDefinition
                    {
                        FacilityId = TradeDistrict,
                        Name = "贸易街区",
                        Score = 0,
                        ResourceCost = new ResourceSet { Originium = 1, OriginiumShard = 1 },
                        GoldVoucherCost = 6,
                        EffectType = "special_action"
                    }
                },
                {
                    OriginiumPurificationPlant,
                    new FacilityCardDefinition
                    {
                        FacilityId = OriginiumPurificationPlant,
                        Name = "固源岩提纯厂",
                        Score = 0,
                        ResourceCost = new ResourceSet { Originium = 1, Iron = 1, OriginiumShard = 1 },
                        GoldVoucherCost = 10,
                        EffectType = "entry",
                        OnBuiltReward = new ResourceSet { Originium = 7 }
                    }
                },
                {
                    EquipmentWarehouse,
                    new FacilityCardDefinition
                    {
                        FacilityId = EquipmentWarehouse,
                        Name = "载具仓库",
                        Score = 0,
                        ResourceCost = new ResourceSet { Originium = 1, Iron = 2 },
                        GoldVoucherCost = 9,
                        EffectType = "special_action"
                    }
                },
                {
                    EnterpriseOffice,
                    new FacilityCardDefinition
                    {
                        FacilityId = EnterpriseOffice,
                        Name = "企业办事处",
                        Score = 1,
                        ResourceCost = new ResourceSet { Originium = 3, Iron = 1, OriginiumShard = 3 },
                        GoldVoucherCost = 23,
                        EffectType = "entry"
                    }
                }
            };

        public static readonly List<string> DefaultSupplyIds = new List<string>
        {
            BoroughAdministrativeDistrict,
            BoroughAdministrativeDistrict,
            BoroughAdministrativeDistrict,
            AffiliatedEnergyFacility,
            AffiliatedEnergyFacility,
            AffiliatedEnergyFacility,
            FederalOffice,
            FederalOffice,
            FederalOffice,
            SimpleEngineeringCamp,
            SimpleEngineeringCamp,
            SourceStoneRefinery,
            UrbanizedArea,
            UrbanizedArea,
            UrbanizedArea,
            IronRefinery,
            TradeDistrict,
            TradeDistrict,
            TradeDistrict,
            OriginiumPurificationPlant,
            SourceStoneRefinery,
            EquipmentWarehouse,
            EquipmentWarehouse,
            EquipmentWarehouse,
            EnterpriseOffice,
            EnterpriseOffice,
            EnterpriseOffice
        };

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
    }
}
