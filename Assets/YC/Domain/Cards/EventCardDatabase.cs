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

        private static EventCardDefinition AddCard(string id, string name, string description,
            EventColor color, ResourceType rt, int amount,
            string descA, ResourceSet rewardA,
            string descB, ResourceSet rewardB,
            string descC = null, ResourceSet rewardC = null,
            ResourceType? representativeResourceType = null, int representativeResourceAmount = 0)
        {
            var card = new EventCardDefinition
            {
                CardId = id,
                Name = name,
                Description = description,
                Color = color,
                ResourceType = rt,
                ResourceAmount = amount,
                RepresentativeResourceType = representativeResourceType ?? rt,
                RepresentativeResourceAmount = representativeResourceAmount > 0 ? representativeResourceAmount : amount,
                ChoiceDescriptions = new List<string> { descA, descB },
                ChoiceRewards = new List<ResourceSet> { rewardA, rewardB },
                ChoicePendingEffects = new List<string> { string.Empty, string.Empty }
            };

            if (descC != null && rewardC != null)
            {
                card.ChoiceDescriptions.Add(descC);
                card.ChoiceRewards.Add(rewardC);
                card.ChoicePendingEffects.Add(string.Empty);
            }

            CardsById[id] = card;
            return card;
        }

        private static void AddGreen()
        {
            AddCard("event_green_01", "中立采石场",
                "带着补给的武装车队旁若无人地驶入了这座早期勘探队伍建立的采石场。“我们只效劳于金券，不会效忠于任何当地竞争者！”这座采石场的领导者向你的人马喊道。",
                EventColor.Green, ResourceType.Originium, 1,
                "“我们只是过来兑换一些燃料，并谈谈生意”：获得 3 源石碎片", R(0, 3, 0, 0, 0),
                "“今天起这里不再由你说了算了”：获得 2 异铁、2 金券", R(0, 0, 2, 0, 2));

            AddCard("event_green_02", "清理虫巢",
                "多方势力垂涎这座外露的异铁矿床已久，但却从未有人下手——因为这里盘踞的源石虫多到足以淹没整座聚落。好在，你的手下正好有这么几个落魄术士，擅长驱赶此类害虫，不久源石虫就被“永久挪了窝”。",
                EventColor.Green, ResourceType.Iron, 1,
                "向当地治安官邀功：获得 7 金券", R(0, 0, 0, 0, 7),
                "附近聚落居民向你表达感谢：获得 3 源石碎片", R(0, 3, 0, 0, 0));

            AddCard("event_green_03", "外露矿床",
                "在不久前的一场小规模天灾洗刷了这片拓荒区的边界，就连安全区也受到了部分波及，但也并非全是坏事，这里新产生的源石晶簇便是最好的证明。",
                EventColor.Green, ResourceType.OriginiumShard, 1,
                "清理出一片场地来建设矿区：获得 4 源岩", R(4, 0, 0, 0, 0),
                "把成堆的粗劣源石带回城：获得 3 源石碎片", R(0, 3, 0, 0, 0));

            AddCard("event_green_04", "富饶岩层",
                "你手拿着荒地人向导倒卖的旧地图，抵达了标记有源石矿藏的位置，但那儿的源石已经不知去向。你注意到了地图下角的绘制日期后，你不禁破口大骂。但鞋底踩上的那些带有小孔的松软土壤，倒也不算是空手而归。",
                EventColor.Green, ResourceType.Originium, 1,
                "现在就装一车源岩回城：获得 4 源岩", R(4, 0, 0, 0, 0),
                "找卖你地图的家伙弥补损失：获得 3 源石碎片", R(0, 3, 0, 0, 0));

            AddCard("event_green_05", "矿业聚落",
                "这里曾经是这片开拓区一处相对繁荣的源石矿区，但简陋的装备和未清理的源石让大部分矿工深陷于矿石病的危害之中，现在这里已经颓废衰败。",
                EventColor.Green, ResourceType.OriginiumShard, 1,
                "用约翰老妈提供的抑制剂换取开采合同：获得 3 源石碎片", R(0, 3, 0, 0, 0),
                "收取费用，并为他们提供一片相对安全的聚居区：获得 7 金券", R(0, 0, 0, 0, 7));

            AddCard("event_green_06", "废弃矿坑（4）",
                "你派去的札拉克探子派上了大用处！他们装神弄鬼吓跑了这座矿井的工人，这让你不费吹灰之力就获得了一处源石矿场。但或许是有些做过头了，矿工们在逃离这里时竟把入口都给炸了，现在想让矿井运转起来成了一道难题。",
                EventColor.Green, ResourceType.OriginiumShard, 1,
                "先把矿井外围留下的资源设备收走：获得 2 异铁、2 金券", R(0, 0, 2, 0, 2),
                "“看看你们干的好事！天黑前要是不把这挖通我就把你们活埋在源岩里！”：获得 4 源岩", R(4, 0, 0, 0, 0));
        }

        private static void AddRed()
        {
            var red01 = AddCard("event_red_01", "险中净土",
                "天灾后，这块盆地的源石尘很快散去，只留下裸露的源岩层。这里成了一些科技公司的科研前哨，但你发现这里并不像想象中的安全。活性源石隐藏在连通外界的轨道里，勘探队很快就收到了遇险者的求救信号。",
                EventColor.Red, ResourceType.Originium, 2,
                "开采源岩，那些学者自有人去救：获得 7 源岩", R(7, 0, 0, 0, 0),
                "在更多人遇害前开始清理源石：获得 6 源石碎片", R(0, 6, 0, 0, 0),
                "你的人搭救了遇险的考察队，他们愿意还这份人情：获得 6 金券；在此资源点或相邻的航道上放置 1 个影响力标识", R(0, 0, 0, 0, 6));
            red01.ChoicePendingEffects[2] = "在此资源点或相邻的航道上放置 1 个影响力标识";

            AddCard("event_red_02", "裂谷矿脉",
                "勘测队深入拓荒区的边界。在那里，他们发现了一条深不见底的裂谷。“良好的源石技艺传导性，极高的矿核密度。我想我们已经找到了跋涉至此的目的。”",
                EventColor.Red, ResourceType.PureOriginium, 1,
                "高纯度源石矿的发现将为你的城邦拉开新的篇章：获得 1 异铁、1 至纯源石", R(0, 0, 1, 1, 0),
                "收集裂隙层的源石碎片：获得 3 源岩、3 源石碎片", R(3, 3, 0, 0, 0));

            AddCard("event_red_03", "采集平台残骸",
                "你的队伍意外发现了惨遭天灾摧毁的采矿平台。它矗立在荒原上，如同无数死难者的纪念碑，这座平台上残留的异铁足够你再建一座区块了。这时，一些虚弱不堪的矿石病人从铁壳中爬出，他们是如何存活至今的？",
                EventColor.Red, ResourceType.Iron, 1,
                "用设备刨开掩埋残骸的障碍，也许有更多的幸存者：获得 4 源岩、3 源石碎片", R(4, 3, 0, 0, 0),
                "他们没救了，继续回收作业：获得 4 异铁、5 金券", R(0, 0, 4, 0, 5));

            AddCard("event_red_04", "高污染环境",
                "“沙滩伞研究所的一项研究发现：富源石尘环境中，源岩层下有丰富的高纯度源石矿”——一派胡言！你的人手在危险的环境下挖掘了数天却一无所获，还加剧了不少人的矿石病病情，现在你需要找人转嫁你的风险。",
                EventColor.Red, ResourceType.Originium, 2,
                "恳求赞助商协助建设矿区：获得 2 源岩、2 源石碎片、2 异铁", R(2, 2, 2, 0, 0),
                "把报告给治安官看看，希望他也会中招：获得 18 金券", R(0, 0, 0, 0, 18),
                "工人冒着风险为你提炼出了高纯度源石矿：获得 1 至纯源石、4 金券", R(0, 0, 0, 1, 4));

            var red05 = AddCard("event_red_05", "深层矿床（4）",
                "一批来历不明的至纯源石突然在黑市流通。你追根溯源找到了产出地，但发现其中的矿物已被开采殆尽。不过，你还是很快占据了这片地区，你专业的拓荒团队证明了你的直觉，地下深层探测到了更丰富的高纯度源石储备！",
                EventColor.Red, ResourceType.PureOriginium, 1,
                "先试着采集一些深层矿物样本：获得 1 源岩、1 至纯源石", R(1, 0, 0, 1, 0),
                "让矿业机械逐步挖开坚硬岩层：获得 3 源石碎片、2 异铁", R(0, 3, 2, 0, 0),
                "向联邦提出拨款请求以协助建设：获得 18 金券；所有对手获得 3 金券", R(0, 0, 0, 0, 18));
            red05.ChoicePendingEffects[2] = "所有对手获得 3 金券";

            AddCard("event_red_06", "地质瑰宝",
                "高纯度源石往往在地下深层伴随着固化源石结晶一起被挖出，但这处巨缝中的高纯度源石被源岩所包裹，相当容易开采。或许这对于源石矿脉研究者来说会是一个有价值的发现。",
                EventColor.Red, ResourceType.PureOriginium, 1,
                "何必多想，这里的宝贝足够帮你解决眼前更棘手的问题：获得 1 源石碎片、1 至纯源石", R(0, 1, 0, 1, 0),
                "帮助地质学家清除矿脉上的杂质，弄不好会有其他有价值的发现？：获得 3 源岩、3 异铁", R(3, 0, 3, 0, 0));
        }

        private static void AddYellow()
        {
            var yellow01 = AddCard("event_yellow_01", "大型源岩场",
                "勘测队的信使迫不及待地将他们的发现带回了城中，新勘探的固源岩样本之纯度震惊了你手下的地质专家。这些优质的固源岩对于聚合剂市场的开拓有不小的价值。",
                EventColor.Yellow, ResourceType.Originium, 1,
                "独享这片矿区，趁其他人还未发现这里：获得 5 源岩", R(5, 0, 0, 0, 0),
                "以部分开采权为代价，与聚合剂厂商合作：获得 4 金券、1 分数", R(0, 0, 0, 0, 4));
            yellow01.ChoicePendingEffects[1] = "获得 1 分数";

            AddCard("event_yellow_02", "洞穴和遗迹",
                "“头儿，爆破小组炸开了一条隐藏的通道，后面竟然是一片……人造建筑的遗址，我们无法确认其年代，也无法确认其建造者。”——信使所带回的信息。",
                EventColor.Yellow, ResourceType.Originium, 1,
                "爆破！我们不是来考古的：获得 5 源岩", R(5, 0, 0, 0, 0),
                "给“梅兰德历史协会”写一封信：获得 10 金券", R(0, 0, 0, 0, 10));

            AddCard("event_yellow_03", "荒地人村落",
                "荒地人对待拓荒者的态度有好有坏，而勘测队眼前的这群显然是友善的。他们愿意拿自己的劳动力换取车队携带的补给品，更有人愿意用源石矿的位置来作为交易的筹码，真是很会变通。",
                EventColor.Yellow, ResourceType.Originium, 1,
                "换取荒地人的服务：获得 3 源岩、1 异铁", R(3, 0, 1, 0, 0),
                "你找到的源石矿储量不多，不过多少有点收获：获得 4 源石碎片", R(0, 4, 0, 0, 0));

            var yellow04 = AddCard("event_yellow_04", "锈锤领地",
                "你的勘测队发现了一处隐藏的源石矿，还找到了一支锈锤聚落，你对这群武装分子的凶猛略有耳闻。若向当地治安官提供关于他们的具体情报应该能换来不少赏金，但不招惹他们也许才是上策。",
                EventColor.Yellow, ResourceType.OriginiumShard, 1,
                "名为岩蹄安保的组织愿意保护矿区，但价格低廉得可疑……：可以支付 4 金券来放置 1 个影响力标识", R(0, 0, 0, 0, 0),
                "真是中了大奖！向治安官举报此事：获得 10 金券", R(0, 0, 0, 0, 10),
                "别招惹这些硬茬，埋头做我们的事：获得 4 源石碎片", R(0, 4, 0, 0, 0));
            yellow04.ChoicePendingEffects[0] = "可以支付 4 金券来放置 1 个影响力标识";

            var yellow05 = AddCard("event_yellow_05", "情报交换（4）",
                "早在第一家采矿公司来到这片荒地时，老杰弗里的驿站就已经屹立于此。其为旅客提供的佳酿据说源自一个古老的国家，那独特的味道曾在拓荒者间广受好评。不过你的特使来此可不是为了品酒的，至少不完全是……",
                EventColor.Yellow, ResourceType.OriginiumShard, 1,
                "杰弗里为你提供了一些当地的人脉：在此资源点相邻的航道上放置 1 个影响力标识", R(0, 0, 0, 0, 0),
                "源石矿脉的位置可比美酒和伙计重要：获得 1 源岩、3 源石碎片", R(1, 3, 0, 0, 0),
                "“杰弗里，这儿归我了，把酒馆开到城里去吧！”：获得 3 异铁", R(0, 0, 3, 0, 0));
            yellow05.ChoicePendingEffects[0] = "在此资源点相邻的航道上放置 1 个影响力标识";

            AddCard("event_yellow_06", "风险任务",
                "源石干扰了电台的通信，“头儿……我们已经……发现了目标源石矿……源石尘浓度极高！车队暴露在活性源石环境中！”",
                EventColor.Yellow, ResourceType.OriginiumShard, 1,
                "在外围挖掘地址样本，谨慎点，我不需要死掉的手下！：获得 3 异铁", R(0, 0, 3, 0, 0),
                "叫你的手下开始搭建矿井，他们之后会有很长的假期：获得 4 源石碎片", R(0, 4, 0, 0, 0));

            var yellow07 = AddCard("event_yellow_07", "异铁开采权",
                "在新的异铁矿被发现后不久，一位大腹便便的治安官来到你的城邦“出于公平竞争原则，联邦政府希望你和其他拓荒者能共享这片矿区所带来的利益，并拨下一笔可观的经费来表彰你的贡献。”显然这家伙就是在觊觎这笔拨款。",
                EventColor.Yellow, ResourceType.Iron, 1,
                "“我可没有义务分享成果”你打发走了治安官：获得 3 源岩、1 异铁", R(3, 0, 1, 0, 0),
                "“联邦万岁！”你向治安官伸出了手：获得 13 金券；所有对手获得 1 异铁", R(0, 0, 0, 0, 13));
            yellow07.ChoicePendingEffects[1] = "所有对手获得 1 异铁";

            var yellow08 = AddCard("event_yellow_08", "敌对生态圈（4）",
                "一支来自萨尔贡的驮兽商队向勘探队提供了一则有趣的情报——一群铜钳铁背的巨磐蟹占据着一片比他们更坚硬的异铁矿。你也许可以占据这些异铁，但是恐怕需要经过其现在的主人的同意。",
                EventColor.Yellow, ResourceType.Iron, 1,
                "派一队佣兵和磐蟹“谈判”：获得 3 异铁", R(0, 0, 3, 0, 0),
                "雇佣萨尔贡术士役使这些大块头：获得 2 源岩、1 分数", R(2, 0, 0, 0, 0));
            yellow08.ChoicePendingEffects[1] = "获得 1 分数";

            AddCard("event_yellow_09", "富异铁区",
                "一伙荒地人袭击了你的车队，但护卫的魔族佬利索地压制了他们，你们发现荒地人的武器虽做工粗糙，但锋刃都由优质异铁制成。不出意料，这里附近一定有一处异铁矿。",
                EventColor.Yellow, ResourceType.Iron, 1,
                "你正好有一批军方订单，先去找找矿脉的位置：获得 3 异铁", R(0, 0, 3, 0, 0),
                "向荒地人村落索要赔偿金：获得 2 源石碎片、5 金券", R(0, 2, 0, 0, 5));

            AddCard("event_yellow_10", "遭弃矿场",
                "“这里附近曾发生过一场天灾。天灾信使在通报的途中不幸遇难，但所幸空中异常的天灾云足够明显，让矿工迅速反应过来并撤离了这里。可笑的是，天灾最终并未直接打击这座矿场，哈哈。”",
                EventColor.Yellow, ResourceType.OriginiumShard, 1,
                "拆除这里的设备，带走有用的材料：获得 3 异铁", R(0, 0, 3, 0, 0),
                "恢复矿场的运行：获得 4 源石碎片", R(0, 4, 0, 0, 0));
        }
    }
}
