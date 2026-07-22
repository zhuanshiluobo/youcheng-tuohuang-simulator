using System.Collections.Generic;
using YC.Domain.CityStyles;
using YC.Domain.State;

namespace YC.Domain.SpecialActions
{
    public static class SpecialActionDatabase
    {
        public const string MilitaryIndustrialArea = "special_action.city_style.military_industrial_area";
        public const string MobilizationSupportSystem = "special_action.city_style.mobilization_support_system";
        public const string CompositePowerSystem = "special_action.city_style.composite_power_system";
        public const string SourceStoneIndustrialHub = "special_action.city_style.source_stone_industrial_hub";
        public const string EfficientMobileManagementSystem = "special_action.city_style.efficient_mobile_management_system";

        private static readonly Dictionary<string, SpecialActionDefinition> Definitions =
            new Dictionary<string, SpecialActionDefinition>
            {
                {
                    MilitaryIndustrialArea,
                    new SpecialActionDefinition
                    {
                        SpecialActionId = MilitaryIndustrialArea,
                        CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                        Name = "军工化区域",
                        Description = "放置尽可能多的影响力；数量等于本方在该样式上的标记数，最多 3 个。",
                        Level = 1,
                        EffectKind = SpecialActionEffectKind.DeployInfluence,
                        MaximumTargetCount = 3
                    }
                },
                {
                    MobilizationSupportSystem,
                    new SpecialActionDefinition
                    {
                        SpecialActionId = MobilizationSupportSystem,
                        CityStyleId = CityStyleDatabase.MobilizationSupportSystem,
                        Name = "动员配套体系",
                        Description = "移除 1 个对手影响力，再尽量在原槽位放置自己的影响力。",
                        Level = 1,
                        EffectKind = SpecialActionEffectKind.ReplaceInfluence,
                        MaximumTargetCount = 1
                    }
                },
                {
                    CompositePowerSystem,
                    new SpecialActionDefinition
                    {
                        SpecialActionId = CompositePowerSystem,
                        CityStyleId = CityStyleDatabase.CompositePowerSystem,
                        Name = "复合动力系统",
                        Description = "支付 1 源石碎片及合计 3 个源岩/异铁，免费移动一次，再在经过的航道放置影响力。",
                        Level = 1,
                        EffectKind = SpecialActionEffectKind.CompositePowerMove,
                        FixedCost = new ResourceSet { OriginiumShard = 1 },
                        FlexibleOriginiumAndIronCost = 3,
                        FreeMoveCount = 1,
                        MaximumTargetCount = 1
                    }
                },
                {
                    SourceStoneIndustrialHub,
                    new SpecialActionDefinition
                    {
                        SpecialActionId = SourceStoneIndustrialHub,
                        CityStyleId = CityStyleDatabase.SourceStoneIndustrialHub,
                        Name = "源石工业中枢",
                        Description = "支付 6 金券，本玩家行动轮获得至多 2 次额外主要行动。",
                        Level = 2,
                        EffectKind = SpecialActionEffectKind.GrantExtraMainActions,
                        FixedCost = new ResourceSet { GoldVoucher = 6 },
                        ExtraMainActionCount = 2,
                        LocksCharacterCard = true
                    }
                },
                {
                    EfficientMobileManagementSystem,
                    new SpecialActionDefinition
                    {
                        SpecialActionId = EfficientMobileManagementSystem,
                        CityStyleId = CityStyleDatabase.EfficientMobileManagementSystem,
                        Name = "高效移动管理体系",
                        Description = "支付 3 源石碎片，连续执行 2 次免费移动城市。",
                        Level = 2,
                        EffectKind = SpecialActionEffectKind.ConsecutiveFreeMoves,
                        FixedCost = new ResourceSet { OriginiumShard = 3 },
                        FreeMoveCount = 2,
                        LocksCharacterCard = true
                    }
                }
            };

        public static IReadOnlyList<SpecialActionDefinition> All
        {
            get
            {
                var result = new List<SpecialActionDefinition>();
                foreach (var pair in Definitions)
                {
                    result.Add(pair.Value.Clone());
                }

                return result;
            }
        }

        public static bool TryGet(string specialActionId, out SpecialActionDefinition definition)
        {
            SpecialActionDefinition stored;
            if (string.IsNullOrEmpty(specialActionId) || !Definitions.TryGetValue(specialActionId, out stored))
            {
                definition = null;
                return false;
            }

            definition = stored.Clone();
            return true;
        }

        public static SpecialActionDefinition Get(string specialActionId)
        {
            SpecialActionDefinition definition;
            return TryGet(specialActionId, out definition) ? definition : null;
        }
    }
}
