using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public sealed class FacilityBuildCandidateOptions
    {
        public string SourceZone { get; set; } = "supply";
        public bool IncludeStoredFacilities { get; set; } = true;
        public string EffectId { get; set; } = "";
        public bool ExcludeRainbow { get; set; }
        public int? MaxGoldVoucherCost { get; set; }
        public string PaymentMode { get; set; } = "auto";
        public bool RequireBuildable { get; set; }
    }

    public sealed class FacilityBuildCandidate
    {
        internal FacilityBuildCandidate(ItemInstanceState item, ZoneInstanceState zone)
        { ItemId = item.ItemId; DefinitionId = item.DefinitionId; SourceZoneId = zone.ZoneId; CarrierItemId = zone.ParentKind == ZoneParentKind.Item ? zone.ParentId : ""; }
        public string ItemId { get; }
        public string DefinitionId { get; }
        public string SourceZoneId { get; }
        public string CarrierItemId { get; }
    }

    public sealed class FacilityBuildCandidateSnapshot
    {
        private readonly CandidateResolutionRecord resolution;
        internal FacilityBuildCandidateSnapshot(CandidateResolutionRecord record, IList<FacilityBuildCandidate> candidates, bool defaultSupply)
        { resolution = record.Clone(); Candidates = new List<FacilityBuildCandidate>(candidates).AsReadOnly(); DefaultItemIds = record.DefaultCandidateIds.AsReadOnly(); FinalItemIds = record.FinalCandidateIds.AsReadOnly(); IsDefaultSupplySet = defaultSupply; }
        public IReadOnlyList<FacilityBuildCandidate> Candidates { get; }
        public IReadOnlyList<string> DefaultItemIds { get; }
        public IReadOnlyList<string> FinalItemIds { get; }
        public bool IsDefaultSupplySet { get; }
        public CandidateResolutionRecord Resolution => resolution.Clone();
    }

    /// <summary>当前查询内索引真实来源，纯候选事件只返回补丁；不持有或修改对局状态。</summary>
    public sealed class FacilityBuildCandidateQuery
    {
        public const string CandidateKind = "facility.build.card";
        private sealed class Policy
        {
            internal string SubscriptionId, AbilityId, Kind;
            internal Func<GameState, CandidateSetDraft, string, IList<CandidatePatch>> Handler;
        }
        private readonly List<Policy> policies = new List<Policy>();
        private static readonly ItemTypeRegistry Types = CreateTypes();
        private static ItemTypeRegistry CreateTypes() { var types = ItemTypeRegistry.CreateBuiltIn(); types.Freeze(); return types; }

        public void RegisterPersistentPolicy(string subscriptionId, string abilityId, string candidateKind,
            Func<GameState, CandidateSetDraft, string, IList<CandidatePatch>> handler)
        {
            if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(abilityId) || candidateKind != CandidateKind || handler == null)
                throw new ArgumentException("facility_candidate_policy_invalid");
            if (policies.Any(policy => policy.SubscriptionId == subscriptionId)) throw new InvalidOperationException("facility_candidate_policy_duplicate");
            policies.Add(new Policy { SubscriptionId = subscriptionId, AbilityId = abilityId, Kind = candidateKind, Handler = handler });
        }

        public FacilityBuildCandidateSnapshot Resolve(GameState state, int playerId, FacilityBuildCandidateOptions options = null,
            string sourceEffectId = "", int stateRevision = 0)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            options = options ?? new FacilityBuildCandidateOptions();
            if (!new[] { "supply", "deck", "reserve" }.Contains(options.SourceZone) || options.MaxGoldVoucherCost < 0 || stateRevision < 0)
                throw new ArgumentException("build_selection_arguments_invalid");
            var items = state.ItemZones.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
            var zones = state.ItemZones.Zones.ToDictionary(zone => zone.ZoneId, StringComparer.Ordinal);
            var active = state.CharacterUses.SelectMany(use => use.Modes.Where(mode => mode.SkillType == "persistent" && mode.Activated && mode.SubscriptionActive)
                .Select(mode => new { use.CardInstanceId, mode.AbilityId })).Distinct().Where(mode =>
                    items.TryGetValue(mode.CardInstanceId, out var card) && card.ControllerSubject.Kind == ItemSubjectKind.Player && card.ControllerSubject.PlayerId == playerId &&
                    zones.TryGetValue(card.ZoneId, out var zone) && zone.TemplateId == "core.zone.character_persistent").ToList();
            var activeCarriers = new HashSet<string>(active.Select(mode => mode.CardInstanceId), StringComparer.Ordinal);
            var defaults = new List<string>(); var pool = new List<string>();
            var candidates = new Dictionary<string, FacilityBuildCandidate>(StringComparer.Ordinal);
            var player = state.FindPlayer(playerId);
            foreach (var item in items.Values)
            {
                if (item.PhysicalKind != ItemPhysicalKind.Card || !Types.IsAssignableTo(item.TypeId, "core.facility_card") || !zones.TryGetValue(item.ZoneId, out var zone)) continue;
                bool standard = options.SourceZone == "supply" ? zone.TemplateId == "core.zone.facility_supply_slot" :
                    options.SourceZone == "deck" ? zone.LocalZoneId == "facility_deck" : zone.TemplateId == "core.zone.box_reserve";
                bool stored = options.SourceZone == "supply" && options.IncludeStoredFacilities && zone.TemplateId == "core.zone.card_facility_storage" && activeCarriers.Contains(zone.ParentId);
                if (!standard && !stored) continue;
                var definition = FacilityCardDatabase.Get(state, item.DefinitionId);
                if (definition == null || options.EffectId.Length > 0 && definition.EffectId != options.EffectId || options.ExcludeRainbow && definition.Color == "rainbow" ||
                    options.MaxGoldVoucherCost.HasValue && definition.GoldVoucherCost > options.MaxGoldVoucherCost.Value) continue;
                if (options.RequireBuildable && (player == null || FacilityCardDatabase.PlayerHasBuiltUniqueFacility(state, player, definition) || BuildFacilityService.FindFirstEmptyCityBoardSlot(state, playerId) < 0 ||
                    options.SourceZone != "reserve" && options.PaymentMode != "free" && BuildFacilityService.ResolvePaymentMode(state, player, definition, new FacilityBuildCostService().GetEffectiveResourceCost(state, player, definition), "auto").Length == 0)) continue;
                pool.Add(item.ItemId); if (standard) defaults.Add(item.ItemId);
                candidates.Add(item.ItemId, new FacilityBuildCandidate(item, zone));
            }
            var draft = new CandidateSetDraft { CandidateSetId = StableIdFactory.Create("candidate", state.GameId, sourceEffectId, CandidateKind, playerId.ToString(), stateRevision.ToString()),
                Version = 1, CandidateKind = CandidateKind, SourceEffectId = sourceEffectId, ExecutingPlayerId = playerId,
                CandidatePoolIds = pool, DefaultCandidateIds = defaults, CurrentIds = new List<string>(defaults),
                Context = ItemTransferEffectExecutor.Object("sourceZone", NormalizedValue.CreateString(options.SourceZone), "includeStoredFacilities", NormalizedValue.CreateBoolean(options.IncludeStoredFacilities),
                    // 读取的是当前本地快照；resolution可登记下一次提交的revision，不能拿未来版本读取Lua。
                    "stateRevision", NormalizedValue.CreateInteger(state.EffectRuntime?.StateRevision ?? stateRevision)) };
            var registry = new CandidatePolicyRegistry();
            foreach (var mode in active)
            foreach (var policy in policies.Where(policy => policy.Kind == CandidateKind && policy.AbilityId == mode.AbilityId))
            {
                string cardId = mode.CardInstanceId;
                registry.Register(new CandidatePolicyRegistration(policy.SubscriptionId + ":" + cardId, CandidateKind,
                    context => policy.Handler(context.State, context.Draft, cardId), sourceContentInstanceId: cardId, sourceAbilityId: policy.AbilityId));
            }
            var resolved = new CandidateSetResolver().Resolve(state, draft, registry, stateRevision);
            if (!resolved.Succeeded) throw new InvalidOperationException(resolved.FaultCode + ":" + resolved.Diagnostic);
            return new FacilityBuildCandidateSnapshot(resolved.Record, resolved.FinalCandidateIds.Select(id => candidates[id]).ToList(),
                options.SourceZone == "supply" && new HashSet<string>(defaults, StringComparer.Ordinal).SetEquals(resolved.FinalCandidateIds));
        }

        public static bool IsActiveStoredFacility(GameState state, ZoneInstanceState zone, int playerId)
        {
            if (zone?.TemplateId != "core.zone.card_facility_storage" || zone.ParentKind != ZoneParentKind.Item) return false;
            var card = state.ItemZones.Items.Find(item => item.ItemId == zone.ParentId);
            return card?.ControllerSubject.Kind == ItemSubjectKind.Player && card.ControllerSubject.PlayerId == playerId &&
                state.ItemZones.Zones.Find(value => value.ZoneId == card.ZoneId)?.TemplateId == "core.zone.character_persistent" &&
                state.CharacterUses.Any(use => use.CardInstanceId == card.ItemId && use.Modes.Any(mode => mode.SkillType == "persistent" && mode.Activated && mode.SubscriptionActive));
        }
    }
}
