using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    [Serializable]
    public sealed class ComponentContentDefinition
    {
        public string DefinitionId = "", TypeId = "", QuantityPolicy = "";
        public CubeAppearanceDeclaration CubeAppearance;
    }
    [Serializable]
    public sealed class AwardContentDefinition
    {
        public string DefinitionId = "", Lifecycle = "", TitleKey = "", DescriptionKey = "";
        public bool RandomAwardPool = true;
        public string TypeId = "", DefinitionVersion = "", AcquisitionPolicy = "";
        public List<CardZoneDefinition> CardZones = new List<CardZoneDefinition>();
    }
    [Serializable]
    public sealed class GameContentDefinitions
    {
        public List<EntrepreneurDefinition> Entrepreneurs = new List<EntrepreneurDefinition>();
        public List<YC.Domain.Effects.UsageTrackDeclaration> UsageTracks = new List<YC.Domain.Effects.UsageTrackDeclaration>();
        public List<YC.Domain.Effects.AbilitySourceDeclaration> AbilitySources = new List<YC.Domain.Effects.AbilitySourceDeclaration>();
        // 本局冻结的企业语义分类；不保存位置，旧档缺失时由匹配内容包的恢复入口补齐。
        public List<string> EnterpriseDefinitionIds = new List<string>();
        public List<AwardContentDefinition> Awards = new List<AwardContentDefinition>();
        public List<ComponentContentDefinition> Components = new List<ComponentContentDefinition>();
        public List<CharacterCardDefinition> Characters = new List<CharacterCardDefinition>();
        public List<FacilityCardDefinition> Facilities = new List<FacilityCardDefinition>();
        public List<EventCardDefinition> Events = new List<EventCardDefinition>();
        public List<CityStyleDefinition> CityStyles = new List<CityStyleDefinition>();
        public List<YC.Domain.SpecialActions.SpecialActionDefinition> SpecialActions = new List<YC.Domain.SpecialActions.SpecialActionDefinition>();
        public static string ResolveDefinitionId(GameState state, string id) => state?.ItemZones?.Items.Find(item => item.ItemId == id)?.DefinitionId ?? id;
        public static GameContentDefinitions CreateRegisteredStandard() => new GameContentDefinitions
        {
            Characters = new[] { "liskarm", "elysium", "texas", "cannot", "tin-man" }.Select(CharacterCardDatabase.Get).ToList(),
            Facilities = FacilityCardDatabase.All.ToList(), Events = new[] { EventColor.Green, EventColor.Red, EventColor.Yellow }.SelectMany(EventCardDatabase.GetCardIds).Select(EventCardDatabase.Get).ToList(),
            CityStyles = CityStyleDatabase.DefaultSupplyIds.Select(CityStyleDatabase.Get).ToList(),
            SpecialActions = CityStyleDatabase.DefaultSupplyIds.Select(CityStyleDatabase.Get).Where(style => !string.IsNullOrEmpty(style.SpecialActionId)).Select(style => YC.Domain.SpecialActions.SpecialActionDatabase.Get(style.SpecialActionId)).ToList()
        };
    }
    [Serializable]
    public sealed class SetupRecipeCondition
    {
        public string Kind = "always";
        public string RuntimeId = string.Empty;
        public string AbilityId = string.Empty;
    }

    [Serializable]
    public sealed class SetupRecipeEntry
    {
        public string EntryId = string.Empty;
        public string DefinitionId = string.Empty;
        public string TypeId = string.Empty;
        public int Quantity;
        public bool PerPlayer;
        public string InitialZoneRole = string.Empty;
        public SetupRecipeCondition Condition = new SetupRecipeCondition();
    }

    /// <summary>冻结的最终配方；实例和随机结果只保存在 ItemZones，恢复不重新装配。</summary>
    [Serializable]
    public sealed class GameSetupRecipe
    {
        public int SchemaVersion = 1;
        public string RecipeId = "standard";
        public List<string> ApplicableModes = new List<string>();
        public List<string> RequiredCapabilities = new List<string>();
        public int InitialBasicCharacterCount = 5;
        public int BasicCharacterKeepCount = 5;
        public int MaxRetainedBasicCharacterCount = 5;
        public int CityStyleLevelOneDrawCount = 4;
        public int CityStyleLevelTwoDrawCount = 2;
        public bool EnterpriseEntrance;
        public int OperatorDrawCount = 2;
        public List<SetupRecipeEntry> Entries = new List<SetupRecipeEntry>();
        public List<ItemCardZoneDeclaration> CardZoneDeclarations = new List<ItemCardZoneDeclaration>();

        public void Validate()
        {
            if (SchemaVersion != 1 || InitialBasicCharacterCount < BasicCharacterKeepCount || BasicCharacterKeepCount != 5 ||
                MaxRetainedBasicCharacterCount != 5 || Entries == null || Entries.Any(e => e == null || string.IsNullOrWhiteSpace(e.EntryId) ||
                string.IsNullOrWhiteSpace(e.DefinitionId) || string.IsNullOrWhiteSpace(e.InitialZoneRole) || e.Quantity <= 0 || e.Condition == null ||
                e.Condition.Kind == "enterprise_selected" && string.IsNullOrWhiteSpace(e.Condition.RuntimeId) || (e.Condition.Kind == "ability_granted" || e.Condition.Kind == "entrepreneur_selected") && string.IsNullOrWhiteSpace(e.Condition.AbilityId) ||
                !new[] { "always", "enterprise_selected", "ability_granted", "entrepreneur_selected" }.Contains(e.Condition.Kind)) ||
                Entries.Select(e => e.EntryId).Distinct(StringComparer.Ordinal).Count() != Entries.Count)
                throw new InvalidOperationException("setup_recipe_invalid");
            var types = ItemTypeRegistry.CreateBuiltIn();
            if (Entries.Any(e => types.Find(e.TypeId) == null)) throw new InvalidOperationException("setup_recipe_type_unknown");
            if (Entries.Where(e => e.PerPlayer && e.InitialZoneRole == "hand" && types.IsAssignableTo(e.TypeId, "core.basic_character_card")).Sum(e => e.Quantity) != InitialBasicCharacterCount)
                throw new InvalidOperationException("initial_basic_character_count_mismatch");
        }

        public GameState Assemble(GameState source, GameMapDefinition map, int hostSeed)
        {
            Validate();
            // 选人专属组件只保留冻结配额，确定真实所有者时才按本配方创建一次。
            var entries = Entries.Where(e => e.Condition.Kind != "entrepreneur_selected").Select(e => new ItemRecipeEntry { EntryId = e.EntryId, DefinitionId = e.DefinitionId, TypeId = e.TypeId,
                Quantity = e.Quantity, PerPlayer = e.PerPlayer, InitialZoneRole = e.Condition.Kind == "always" ? e.InitialZoneRole : "reserve" }).ToList();
            source.SetupRecipe = GameStateCloneService.DeepClone(this);
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, entries, CardZoneDeclarations);
            if (state.RandomRuntime == null)
            {
                if (string.IsNullOrEmpty(state.GameId)) state.GameId = "explicit-seed";
                state.EventDeckSeed = hostSeed;
                state.RandomRuntime = GameplayRandomRuntime.Create(state.GameId);
            }
            if (state.EventDeckSeed != hostSeed) throw new InvalidOperationException("random_seed_conflict");
            foreach (var order in state.ItemZones.Orders.Where(o => state.ItemZones.Zones.Find(z => z.ZoneId == o.ZoneId)?.TemplateId == "core.zone.card_deck"))
            {
                GameplayRandomRuntime.Shuffle(order.ItemIds, state, GameplayRandomRuntime.SetupDecks);
                order.OrderRevision++;
            }
            var types = ItemTypeRegistry.CreateBuiltIn();
            var styles = state.ItemZones.Items.Where(i => i.TypeId == "core.board_card" && Entries.Any(e => e.DefinitionId == i.DefinitionId && e.InitialZoneRole == "city_styles")).ToList();
            foreach (int level in new[] { 1, 2 })
            {
                var candidates = styles.Where(i => CityStyleDatabase.Get(state, i.DefinitionId).Level == level).ToList();
                GameplayRandomRuntime.Shuffle(candidates, state, GameplayRandomRuntime.SetupStyles);
                int count = level == 1 ? CityStyleLevelOneDrawCount : CityStyleLevelTwoDrawCount;
                if (candidates.Count < count) throw new InvalidOperationException("content_not_ready");
                foreach (var item in candidates.Skip(count)) ItemZoneStateMigrator.SetPosition(state, item, ItemZoneStateFactory.Zone(state, "reserve").ZoneId);
            }
            var deck = ItemZoneStateFactory.Zone(state, "facility_deck");
            var deckOrder = state.ItemZones.Orders.Single(o => o.ZoneId == deck.ZoneId);
            for (int slot = 0; slot < FacilitySupplyService.SupplySize && deckOrder.ItemIds.Count > 0; slot++)
                ItemZoneStateMigrator.SetPosition(state, state.ItemZones.Items.Single(i => i.ItemId == deckOrder.ItemIds[0]), ItemZoneStateFactory.Zone(state, "facility_supply_" + slot).ZoneId);
            FacilityInstanceStateService.EnsureAll(state);
            return state;
        }

        [Obsolete("请使用 MapId 重载；人数不能决定所选地图的事件牌池。")]
        public static GameSetupRecipe CreateRegisteredStandard(int playerCount)
            => CreateRegisteredStandard(playerCount == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId);

        public static GameSetupRecipe CreateRegisteredStandard(string mapId)
        {
            if (mapId != StaticMapDefinitions.ThreePlayerMapId && mapId != StaticMapDefinitions.FourPlayerMapId)
                throw new ArgumentException("未知的正式地图 ID：" + mapId, nameof(mapId));
            var recipe = new GameSetupRecipe();
            foreach (string id in new[] { "liskarm", "elysium", "texas", "cannot", "tin-man" }) recipe.Add(id, id, "core.basic_character_card", "hand", true);
            foreach (string id in FacilityCardDatabase.DefaultSupplyIds) recipe.Add("facility_" + id, id, "core.facility_card", "facility_deck");
            foreach (string id in CityStyleDatabase.DefaultSupplyIds) recipe.Add("style_" + id, id, "core.board_card", "city_styles");
            foreach (var color in new[] { EventColor.Green, EventColor.Yellow, EventColor.Red })
                foreach (string id in EventCardDatabase.GetCardIds(color).Where(id => mapId == StaticMapDefinitions.FourPlayerMapId || !EventCardDatabase.IsFourPlayerOnly(id))) recipe.Add("event_" + id, id, "core.event_card", "event_" + color.ToString().ToLowerInvariant());
            recipe.AddAccessories();
            return recipe;
        }

        public void AddAccessories()
        {
            // 配方业务键保持稳定；仅区域语义迁为supply，实例ID不因业务键重命名漂移。
            Add("player_markers", "core.player_marker", "core.player_marker", "supply", true, 29);
            Add("score_marker", "core.player_marker", "core.player_marker", "score_track", true);
            Add("mobile_city", "core.mobile_city", "core.mobile_city", "city_reserve", true);
            Add("core_command_tower", FacilityCardDatabase.CoreCommandTower, "core.facility_card", "city_cell_7", true);
        }
        public void Add(string entry, string definition, string type, string role, bool perPlayer = false, int quantity = 1) => Entries.Add(new SetupRecipeEntry { EntryId = entry, DefinitionId = definition, TypeId = type, InitialZoneRole = role, PerPlayer = perPlayer, Quantity = quantity });
        public static void Shuffle<T>(IList<T> values, Random random) { for (int i = values.Count - 1; i > 0; i--) { int next = random.Next(i + 1); T value = values[i]; values[i] = values[next]; values[next] = value; } }
    }
}
