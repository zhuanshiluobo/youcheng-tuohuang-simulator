using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public static class EventCardDatabase
    {
        public static readonly List<string> GreenCardIds = new List<string>
        {
            "event_green_01", "event_green_02", "event_green_03",
            "event_green_04", "event_green_05", "event_green_06"
        };

        public static readonly List<string> RedCardIds = new List<string>
        {
            "event_red_01", "event_red_02", "event_red_03",
            "event_red_04", "event_red_05", "event_red_06"
        };

        public static readonly List<string> YellowCardIds = new List<string>
        {
            "event_yellow_01", "event_yellow_02", "event_yellow_03",
            "event_yellow_04", "event_yellow_05", "event_yellow_06",
            "event_yellow_07", "event_yellow_08", "event_yellow_09",
            "event_yellow_10"
        };

        private static readonly Dictionary<string, EventCardDefinition> CardsById =
            new Dictionary<string, EventCardDefinition>();

        static EventCardDatabase()
        {
            AddGreen();
            AddRed();
            AddYellow();
        }

        public static EventCardDefinition Get(string cardId)
        {
            CardsById.TryGetValue(cardId, out var card);
            return card;
        }

        private static ResourceSet R(int originium, int shard, int iron, int pure, int gold)
        {
            return new ResourceSet
            {
                Originium = originium,
                OriginiumShard = shard,
                Iron = iron,
                PureOriginium = pure,
                GoldVoucher = gold
            };
        }

        private static void AddCard(string id, EventColor color, ResourceType rt, int amount,
            string descA, ResourceSet rewardA,
            string descB, ResourceSet rewardB,
            string descC = null, ResourceSet rewardC = null)
        {
            var card = new EventCardDefinition
            {
                CardId = id,
                Color = color,
                ResourceType = rt,
                ResourceAmount = amount,
                ChoiceDescriptions = new List<string> { descA, descB },
                ChoiceRewards = new List<ResourceSet> { rewardA, rewardB }
            };

            if (descC != null && rewardC != null)
            {
                card.ChoiceDescriptions.Add(descC);
                card.ChoiceRewards.Add(rewardC);
            }

            CardsById[id] = card;
        }

        private static void AddGreen()
        {
            // 1. 中立采石场 - 源岩
            AddCard("event_green_01", EventColor.Green, ResourceType.Originium, 1,
                "获得 3 源石", R(0, 3, 0, 0, 0),
                "获得 2 异铁、2 金券", R(0, 0, 2, 0, 2));

            // 2. 清理虫巢 - 异铁
            AddCard("event_green_02", EventColor.Green, ResourceType.Iron, 1,
                "获得 7 金券", R(0, 0, 0, 0, 7),
                "获得 3 源石", R(0, 3, 0, 0, 0));

            // 3. 外露矿床 - 源石碎片
            AddCard("event_green_03", EventColor.Green, ResourceType.OriginiumShard, 1,
                "获得 4 源岩", R(4, 0, 0, 0, 0),
                "获得 3 源石", R(0, 3, 0, 0, 0));

            // 4. 富饶岩层 - 源岩
            AddCard("event_green_04", EventColor.Green, ResourceType.Originium, 1,
                "获得 4 源岩", R(4, 0, 0, 0, 0),
                "获得 3 源石", R(0, 3, 0, 0, 0));

            // 5. 矿业聚落 - 源石碎片
            AddCard("event_green_05", EventColor.Green, ResourceType.OriginiumShard, 1,
                "获得 3 源石", R(0, 3, 0, 0, 0),
                "获得 7 金券", R(0, 0, 0, 0, 7));

            // 6. 废弃矿坑(4) - 源石碎片
            AddCard("event_green_06", EventColor.Green, ResourceType.OriginiumShard, 1,
                "获得 2 异铁、2 金券", R(0, 0, 2, 0, 2),
                "获得 4 源岩", R(4, 0, 0, 0, 0));
        }

        private static void AddRed()
        {
            // 1. 险中净土 - 源岩x2
            AddCard("event_red_01", EventColor.Red, ResourceType.Originium, 2,
                "获得 6 源石", R(0, 6, 0, 0, 0),
                "获得 7 源岩", R(7, 0, 0, 0, 0),
                "获得 6 金券；在此放置1影响力", R(0, 0, 0, 0, 6));

            // 2. 裂谷矿脉 - 至纯源石
            AddCard("event_red_02", EventColor.Red, ResourceType.PureOriginium, 1,
                "获得 1 异铁、1 至纯源石", R(0, 0, 1, 1, 0),
                "获得 3 源岩、3 源石", R(3, 3, 0, 0, 0));

            // 3. 采集平台残骸 - 异铁
            AddCard("event_red_03", EventColor.Red, ResourceType.Iron, 1,
                "获得 4 异铁、5 金券", R(0, 0, 4, 0, 5),
                "获得 4 源岩、3 源石", R(4, 3, 0, 0, 0));

            // 4. 高污染环境 - 源岩x2
            AddCard("event_red_04", EventColor.Red, ResourceType.Originium, 2,
                "获得 2 源岩、2 源石、2 异铁", R(2, 2, 2, 0, 0),
                "获得 18 金券", R(0, 0, 0, 0, 18),
                "获得 1 至纯源石、4 金券", R(0, 0, 0, 1, 4));

            // 5. 深层矿床(4) - 至纯源石
            AddCard("event_red_05", EventColor.Red, ResourceType.PureOriginium, 1,
                "获得 1 至纯源石、1 源岩", R(1, 0, 0, 1, 0),
                "获得 3 源石、2 异铁", R(0, 3, 2, 0, 0),
                "获得 18 金券；所有对手获得 3 金券", R(0, 0, 0, 0, 18));

            // 6. 地质瑰宝 - 至纯源石
            AddCard("event_red_06", EventColor.Red, ResourceType.PureOriginium, 1,
                "获得 1 源石、1 至纯源石", R(0, 1, 0, 1, 0),
                "获得 3 源岩、3 异铁", R(3, 0, 3, 0, 0));
        }

        private static void AddYellow()
        {
            // 1. 大型源岩场 - 源岩
            AddCard("event_yellow_01", EventColor.Yellow, ResourceType.Originium, 1,
                "获得 5 源岩", R(5, 0, 0, 0, 0),
                "获得 4 金券", R(0, 0, 0, 0, 4));

            // 2. 洞穴遗迹 - 源岩
            AddCard("event_yellow_02", EventColor.Yellow, ResourceType.Originium, 1,
                "获得 5 源岩", R(5, 0, 0, 0, 0),
                "获得 10 金券", R(0, 0, 0, 0, 10));

            // 3. 荒地人村落 - 源岩
            AddCard("event_yellow_03", EventColor.Yellow, ResourceType.Originium, 1,
                "获得 3 源岩、1 异铁", R(3, 0, 1, 0, 0),
                "获得 4 源石", R(0, 4, 0, 0, 0));

            // 4. 锈锤领地 - 源石碎片
            AddCard("event_yellow_04", EventColor.Yellow, ResourceType.OriginiumShard, 1,
                "获得 10 金券", R(0, 0, 0, 0, 10),
                "获得 4 源石", R(0, 4, 0, 0, 0));

            // 5. 情报交换(4) - 源石碎片
            AddCard("event_yellow_05", EventColor.Yellow, ResourceType.OriginiumShard, 1,
                "获得 1 源岩、3 源石", R(1, 3, 0, 0, 0),
                "获得 3 异铁", R(0, 0, 3, 0, 0));

            // 6. 风险任务 - 源石碎片
            AddCard("event_yellow_06", EventColor.Yellow, ResourceType.OriginiumShard, 1,
                "获得 3 异铁", R(0, 0, 3, 0, 0),
                "获得 4 源石", R(0, 4, 0, 0, 0));

            // 7. 异铁开采权 - 异铁
            AddCard("event_yellow_07", EventColor.Yellow, ResourceType.Iron, 1,
                "获得 1 异铁、3 源岩", R(3, 0, 1, 0, 0),
                "获得 13 金券", R(0, 0, 0, 0, 13));

            // 8. 敌对生态圈(4) - 异铁
            AddCard("event_yellow_08", EventColor.Yellow, ResourceType.Iron, 1,
                "获得 3 异铁", R(0, 0, 3, 0, 0),
                "获得 2 源岩", R(2, 0, 0, 0, 0));

            // 9. 富异铁区 - 异铁
            AddCard("event_yellow_09", EventColor.Yellow, ResourceType.Iron, 1,
                "获得 3 异铁", R(0, 0, 3, 0, 0),
                "获得 2 源石、5 金券", R(0, 2, 0, 0, 5));

            // 10. 遗弃矿场 - 源石碎片
            AddCard("event_yellow_10", EventColor.Yellow, ResourceType.OriginiumShard, 1,
                "获得 3 异铁", R(0, 0, 3, 0, 0),
                "获得 4 源石", R(0, 4, 0, 0, 0));
        }
    }
}
