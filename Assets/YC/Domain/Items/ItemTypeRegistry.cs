using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Items
{
    [Serializable]
    public sealed class ItemTypeDefinition
    {
        public string TypeId = string.Empty;
        public string BaseTypeId = string.Empty;
        public ItemPhysicalKind PhysicalKind;
        public List<string> OperationProfileIds = new List<string>();
    }

    public sealed class ItemTypeRegistry
    {
        public const string ContractTokenTypeId = "core.contract_token";
        public const string RewardTokenAcquisitionProfileId = "core.reward_token_acquire";
        private Dictionary<string, ItemTypeDefinition> definitions;
        private Dictionary<string, HashSet<string>> ancestors;
        private Dictionary<string, HashSet<string>> inheritedProfiles;
        private bool sharesBuiltInStorage;
        public ItemTypeRegistry()
        {
            definitions = new Dictionary<string, ItemTypeDefinition>(StringComparer.Ordinal);
            ancestors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            inheritedProfiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        }
        private ItemTypeRegistry(ItemTypeRegistry source)
        {
            definitions = source.definitions; ancestors = source.ancestors; inheritedProfiles = source.inheritedProfiles;
            sharesBuiltInStorage = true;
        }
        // 注册时复制、读出口继续复制；这些私有内建定义没有任何可变引用出口。
        // 每个新注册器独立管理frozen标志，首次追加时分离索引，不污染其他注册器。
        private static readonly Lazy<ItemTypeRegistry> BuiltIn = new Lazy<ItemTypeRegistry>(BuildBuiltIn);
        private bool frozen;
        public IReadOnlyList<ItemTypeDefinition> Definitions => new List<ItemTypeDefinition>(System.Linq.Enumerable.Select(definitions.Values, value => GameStateCloneService.DeepClone(value))).AsReadOnly();
        private static readonly Dictionary<string, ItemPhysicalKind> Profiles = new Dictionary<string, ItemPhysicalKind>(StringComparer.Ordinal)
        {
            { "core.entrepreneur_card_transfer", ItemPhysicalKind.Card },
            { "core.cube_storage", ItemPhysicalKind.Cube }, { "core.player_marker_storage", ItemPhysicalKind.Cube },
            { "core.influence_cube", ItemPhysicalKind.Cube }, { "core.facility_card_transfer", ItemPhysicalKind.Card },
            { "core.character_card_transfer", ItemPhysicalKind.Card }, { "core.character_card_use", ItemPhysicalKind.Card },
            { "core.reward_card_transfer", ItemPhysicalKind.Card }, { "core.deck_shuffle", ItemPhysicalKind.Card }, { "core.mobile_city_move", ItemPhysicalKind.MobileCity },
            { "core.adjacent_location_token_move", ItemPhysicalKind.Token }, { "core.token_storage", ItemPhysicalKind.Token },
            { RewardTokenAcquisitionProfileId, ItemPhysicalKind.Token }
        };

        public bool TryRegister(ItemTypeDefinition definition, out string reasonCode)
        {
            reasonCode = string.Empty;
            if (frozen) return Reject("registry_frozen", out reasonCode);
            if (definition == null || string.IsNullOrWhiteSpace(definition.TypeId)) return Reject("typeId_required", out reasonCode);
            if (definitions.ContainsKey(definition.TypeId)) return Reject("duplicate_type_id", out reasonCode);
            if (!Enum.IsDefined(typeof(ItemPhysicalKind), definition.PhysicalKind)) return Reject("physical_kind_mismatch", out reasonCode);
            if (definition.BaseTypeId == definition.TypeId) return Reject("type_cycle", out reasonCode);
            if (string.IsNullOrEmpty(definition.BaseTypeId) && definition.TypeId != "core.cube" && definition.TypeId != "core.card" && definition.TypeId != "core.mobile_city" && definition.TypeId != "core.token")
                return Reject("unknown_base_type", out reasonCode);
            ItemTypeDefinition parent = null;
            if (!string.IsNullOrEmpty(definition.BaseTypeId) && !definitions.TryGetValue(definition.BaseTypeId, out parent))
                return Reject("unknown_base_type", out reasonCode);
            if (parent != null && parent.PhysicalKind != definition.PhysicalKind) return Reject("physical_kind_mismatch", out reasonCode);
            if (definition.OperationProfileIds == null) return Reject("operationProfileIds_required", out reasonCode);
            foreach (string profile in definition.OperationProfileIds)
            {
                ItemPhysicalKind kind;
                if (profile == null || !Profiles.TryGetValue(profile, out kind) || kind != definition.PhysicalKind ||
                    (profile == "core.player_marker_storage" && definition.TypeId != "core.player_marker" && !IsAssignableTo(definition.BaseTypeId, "core.player_marker")) ||
                    (profile == "core.entrepreneur_card_transfer" && definition.TypeId != "entrepreneur.ability_card" && definition.TypeId != "entrepreneur.round_card" && !IsAssignableTo(definition.BaseTypeId, "entrepreneur.ability_card") && !IsAssignableTo(definition.BaseTypeId, "entrepreneur.round_card")) ||
                    (profile.StartsWith("core.character_card_", StringComparison.Ordinal) && definition.TypeId != "core.character_card" && !IsAssignableTo(definition.BaseTypeId, "core.character_card")) ||
                    (profile == "core.reward_card_transfer" && definition.TypeId != "core.award_card" && definition.TypeId != "core.contract_card" && !IsAssignableTo(definition.BaseTypeId, "core.award_card") && !IsAssignableTo(definition.BaseTypeId, "core.contract_card")) ||
                    (profile == RewardTokenAcquisitionProfileId && definition.TypeId != ContractTokenTypeId && !IsAssignableTo(definition.BaseTypeId, ContractTokenTypeId)) ||
                    (profile == "core.facility_card_transfer" && definition.TypeId != "core.facility_card" && !IsAssignableTo(definition.BaseTypeId, "core.facility_card")))
                    return Reject("operation_profile_rejected", out reasonCode);
            }
            var stored = GameStateCloneService.DeepClone(definition);
            var parents = string.IsNullOrEmpty(stored.BaseTypeId) ? new HashSet<string>(StringComparer.Ordinal) :
                new HashSet<string>(ancestors[stored.BaseTypeId], StringComparer.Ordinal);
            parents.Add(stored.TypeId);
            var profiles = new HashSet<string>(stored.OperationProfileIds, StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(stored.BaseTypeId)) profiles.UnionWith(inheritedProfiles[stored.BaseTypeId]);
            if(sharesBuiltInStorage)
            {
                definitions=new Dictionary<string,ItemTypeDefinition>(definitions,StringComparer.Ordinal);
                ancestors=new Dictionary<string,HashSet<string>>(ancestors,StringComparer.Ordinal);
                inheritedProfiles=new Dictionary<string,HashSet<string>>(inheritedProfiles,StringComparer.Ordinal);
                sharesBuiltInStorage=false;
            }
            definitions.Add(stored.TypeId, stored);
            ancestors.Add(stored.TypeId, parents); inheritedProfiles.Add(stored.TypeId, profiles);
            return true;
        }

        public ItemTypeDefinition Find(string typeId) => typeId != null && definitions.TryGetValue(typeId, out var value) ? GameStateCloneService.DeepClone(value) : null;
        internal ItemPhysicalKind? FindPhysicalKind(string typeId) =>
            typeId != null && definitions.TryGetValue(typeId, out var value) ? value.PhysicalKind : (ItemPhysicalKind?)null;
        public bool IsAssignableTo(string typeId, string baseTypeId)
        {
            return !string.IsNullOrEmpty(typeId) && ancestors.TryGetValue(typeId, out var values) && values.Contains(baseTypeId);
        }
        public bool HasOperationProfile(string typeId, string profileId)
        {
            return !string.IsNullOrEmpty(typeId) && inheritedProfiles.TryGetValue(typeId, out var values) && values.Contains(profileId);
        }
        public void Freeze() { frozen = true; }
        private static bool Reject(string code, out string reasonCode) { reasonCode = code; return false; }

        // 私有集合只在注册时创建，之后不修改；新注册器首次追加时复制索引。
        // 不缓存任意查询键，也不持有对局状态。公开Find/Definitions继续返回独立副本。
        public static ItemTypeRegistry CreateBuiltIn() => new ItemTypeRegistry(BuiltIn.Value);
        private static ItemTypeRegistry BuildBuiltIn()
        {
            var registry = new ItemTypeRegistry();
            registry.Add("core.cube", "", ItemPhysicalKind.Cube, "core.cube_storage");
            registry.Add("core.player_marker", "core.cube", ItemPhysicalKind.Cube, "core.player_marker_storage", "core.influence_cube");
            registry.Add("entrepreneur.gang_marker", "core.cube", ItemPhysicalKind.Cube, "core.influence_cube");
            registry.Add("infected.company_marker", "core.cube", ItemPhysicalKind.Cube, "core.influence_cube");
            registry.Add("core.card", "", ItemPhysicalKind.Card, "core.deck_shuffle");
            registry.Add("entrepreneur.ability_card", "core.card", ItemPhysicalKind.Card, "core.entrepreneur_card_transfer");
            registry.Add("entrepreneur.round_card", "core.card", ItemPhysicalKind.Card, "core.entrepreneur_card_transfer");
            registry.Add("core.facility_card", "core.card", ItemPhysicalKind.Card, "core.facility_card_transfer");
            registry.Add("core.character_card", "core.card", ItemPhysicalKind.Card, "core.character_card_transfer", "core.character_card_use");
            foreach (string type in new[] { "core.basic_character_card", "core.operator_card", "core.generated_character_card" }) registry.Add(type, "core.character_card", ItemPhysicalKind.Card);
            foreach (string type in new[] { "core.event_card", "core.board_card" }) registry.Add(type, "core.card", ItemPhysicalKind.Card);
            foreach (string type in new[] { "core.award_card", "core.contract_card" }) registry.Add(type, "core.card", ItemPhysicalKind.Card, "core.reward_card_transfer");
            registry.Add("core.mobile_city", "", ItemPhysicalKind.MobileCity, "core.mobile_city_move");
            registry.Add("core.token", "", ItemPhysicalKind.Token, "core.token_storage");
            registry.Add(ContractTokenTypeId, "core.token", ItemPhysicalKind.Token, RewardTokenAcquisitionProfileId);
            registry.Add("core.die", "core.token", ItemPhysicalKind.Token);
            foreach (string type in new[] { "core.road_token", "core.resource_point_token", "core.start_player_token", "infected.influence_lock_token" }) registry.Add(type, "core.token", ItemPhysicalKind.Token);
            registry.Add("infected.gavial_token", "core.token", ItemPhysicalKind.Token, "core.adjacent_location_token_move");
            return registry;
        }
        private void Add(string id, string parent, ItemPhysicalKind kind, params string[] profiles)
        {
            if (!TryRegister(new ItemTypeDefinition { TypeId = id, BaseTypeId = parent, PhysicalKind = kind, OperationProfileIds = new List<string>(profiles) }, out var reason))
                throw new InvalidOperationException(reason + ": " + id);
        }
    }
}
