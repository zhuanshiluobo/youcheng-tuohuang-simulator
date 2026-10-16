using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public sealed class SettlementSourceContract
    {
        public string DefinitionId { get; }
        public string AbilityId { get; }
        public string LocalId { get; }
        public string EntryKind { get; }
        public string AvailabilityHandlerId { get; }
        public SettlementWholeEffectPolicy WholeEffectPolicy { get; }
        public SettlementSourceContract(string definitionId, string abilityId, string localId, string entryKind, string availabilityHandlerId, SettlementWholeEffectPolicy policy)
        { DefinitionId = definitionId; AbilityId = abilityId; LocalId = localId; EntryKind = entryKind; AvailabilityHandlerId = availabilityHandlerId; WholeEffectPolicy = policy; }
    }
    public sealed partial class EffectRegistry
    {
        private readonly Dictionary<string, SettlementSourceContract> settlementSourceContracts = new Dictionary<string, SettlementSourceContract>(StringComparer.Ordinal);
        private Func<GameState, SettlementWindowRuntimeState, SettlementEntryRuntimeState, bool, SettlementAvailabilityResult> settlementQueryProvider;
        private Action<GameState, SettlementWindowRuntimeState> settlementOffersProvider;
        public string SettlementCatalogContentHash { get; private set; } = string.Empty;
        public void RegisterSettlementSourceContract(SettlementSourceContract contract)
        {
            if (contract == null || string.IsNullOrEmpty(contract.DefinitionId) || string.IsNullOrEmpty(contract.AbilityId) || string.IsNullOrEmpty(contract.LocalId)) throw new ArgumentException("settlement_source_contract_invalid");
            settlementSourceContracts.Add(contract.AbilityId, contract);
        }
        public bool TryGetSettlementSourceContract(string abilityId, out SettlementSourceContract contract) => settlementSourceContracts.TryGetValue(abilityId ?? "", out contract);
        public void RegisterSettlementCatalogProvider(string contentHash,
            Action<GameState, SettlementWindowRuntimeState> offers,
            Func<GameState, SettlementWindowRuntimeState, SettlementEntryRuntimeState, bool, SettlementAvailabilityResult> query)
        {
            if (string.IsNullOrEmpty(contentHash) || offers == null || query == null || settlementQueryProvider != null) throw new ArgumentException("settlement_catalog_provider_invalid");
            SettlementCatalogContentHash = contentHash; settlementOffersProvider = offers; settlementQueryProvider = query;
        }
        public void RefreshSettlementOffers(GameState state, SettlementWindowRuntimeState window) => settlementOffersProvider?.Invoke(state, window);
        private Func<GameState, SettlementWindowContext, SettlementEntryRuntimeState, NormalizedValue> settlementFactsProvider;
        public void RegisterSettlementFactsProvider(Func<GameState, SettlementWindowContext, SettlementEntryRuntimeState, NormalizedValue> provider)
        { if (provider == null || settlementFactsProvider != null) throw new ArgumentException("结算授权事实提供者为空或重复。"); settlementFactsProvider = provider; }
        public SettlementAvailabilityResult QuerySettlementEntry(GameState state, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry, bool forceRefresh = false)
        {
            if (settlementQueryProvider != null) return settlementQueryProvider(state, window, entry, forceRefresh);
            if (string.IsNullOrEmpty(entry.AvailabilityHandlerId)) return new SettlementAvailabilityResult { Enabled = entry.EntryKind == SettlementEntryService.CleanupOrder && entry.WholeEffectPolicy == SettlementWholeEffectPolicy.Required };
            if (settlementFactsProvider == null || !TryGetSettlementAvailabilityHandler(entry.AvailabilityHandlerId, out var query)) return new SettlementAvailabilityResult { Enabled = false };
            var facts = settlementFactsProvider(state, window.Context, entry);
            if (facts == null || facts.Kind != NormalizedValueKind.Object || !facts.IsValid()) throw new ArgumentException("结算授权事实无效。");
            var offer = new SettlementOfferDefinition { PlayerId = window.Context.PlayerId, WindowId = window.Context.Id, OwnerEffectId = window.Context.OwnerEffectId, LocalId = entry.LocalId, SourceItemId = entry.SourceItemId,
                MaxUses = entry.MaxUses, AvailabilityHandlerId = entry.AvailabilityHandlerId, Visibility = entry.Visibility, Presentation = GameStateCloneService.DeepClone(entry.Presentation), Effects = entry.Effects.Select(e => e.Clone()).ToList() };
            return query(SettlementEntryService.CreateQueryContext(state, window), offer, facts.Clone());
        }
        public IReadOnlyList<YC.Domain.Cards.CharacterSettlementOption> GetSettlementOptions(GameState state, int viewerPlayerId)
        {
            var window = state?.EffectRuntime?.EffectNodes.Select(n => n.SettlementWindow).FirstOrDefault(w => w?.Context?.Id == state.EffectRuntime.ActiveSettlementWindowId && !w.IsClosed);
            if (window == null) return Array.Empty<YC.Domain.Cards.CharacterSettlementOption>();
            var options = SettlementEntryService.GetOptions(state, window, viewerPlayerId, e => QuerySettlementEntry(state, window, e))
                .Where(o => o.SettlementState != "resolved" && o.SettlementState != "declined").ToList();
            foreach (var option in options)
            {
                var entry = window.Entries.Find(e => e.OptionId == option.OptionId);
                option.SourceDefinitionId = entry.SourceDefinitionId;
                if (string.IsNullOrEmpty(option.SourceDefinitionId) && TryGetSettlementSourceContract(entry.AbilityId, out var contract)) option.SourceDefinitionId = contract.DefinitionId;
                if (YC.Domain.Presentation.GameplayPresentationNormalizer.IsKey(option.DisabledReasonCode)) option.DisabledReason = new YC.Domain.Presentation.GameplayTextRef(option.DisabledReasonCode);
            }
            return options;
        }
        private readonly Dictionary<string, Func<SettlementWindowContext, NormalizedValue, IReadOnlyList<SettlementOfferDefinition>>> settlementOfferProviders =
            new Dictionary<string, Func<SettlementWindowContext, NormalizedValue, IReadOnlyList<SettlementOfferDefinition>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<SettlementWindowContext, SettlementOfferDefinition, NormalizedValue, SettlementAvailabilityResult>> settlementAvailabilityHandlers =
            new Dictionary<string, Func<SettlementWindowContext, SettlementOfferDefinition, NormalizedValue, SettlementAvailabilityResult>>(StringComparer.Ordinal);
        public void RegisterSettlementOfferProvider(string handlerId, Func<SettlementWindowContext, NormalizedValue, IReadOnlyList<SettlementOfferDefinition>> provider)
        {
            if (string.IsNullOrWhiteSpace(handlerId) || provider == null) throw new ArgumentException("结算来源提供者缺少身份或函数。");
            settlementOfferProviders.Add(handlerId, provider);
        }
        public void RegisterSettlementAvailabilityHandler(string handlerId, Func<SettlementWindowContext, SettlementOfferDefinition, NormalizedValue, SettlementAvailabilityResult> query)
        {
            if (string.IsNullOrWhiteSpace(handlerId) || query == null) throw new ArgumentException("结算资格提供者缺少身份或函数。");
            settlementAvailabilityHandlers.Add(handlerId, query);
        }
        public bool TryGetSettlementOfferProvider(string handlerId, out Func<SettlementWindowContext, NormalizedValue, IReadOnlyList<SettlementOfferDefinition>> provider)
            => settlementOfferProviders.TryGetValue(handlerId ?? string.Empty, out provider);
        public bool TryGetSettlementAvailabilityHandler(string handlerId, out Func<SettlementWindowContext, SettlementOfferDefinition, NormalizedValue, SettlementAvailabilityResult> query)
            => settlementAvailabilityHandlers.TryGetValue(handlerId ?? string.Empty, out query);
    }
}
