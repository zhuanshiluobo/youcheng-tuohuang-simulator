using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    /// <summary>正式内容声明到 Host 的单一适配；Lua 只收到本窗口授权的纯事实快照。</summary>
    public static class SettlementHostCatalog
    {
        private static readonly ConditionalWeakTable<EffectRegistry, SettlementLuaCatalog> Catalogs =
            new ConditionalWeakTable<EffectRegistry, SettlementLuaCatalog>();

        public static SettlementLuaCatalog Register(EffectRegistry registry, ExternalContentPack pack)
        {
            if (registry == null || pack == null) throw new ArgumentNullException();
            if (Catalogs.TryGetValue(registry, out var existing))
            {
                if (registry.SettlementCatalogContentHash != pack.ContentHash) throw new InvalidOperationException("settlement_catalog_hash_mismatch");
                return existing;
            }
            var catalog = new SettlementLuaCatalog(pack);
            var declared = pack.ActiveDefinitions.SelectMany(d => d.Abilities.Where(a => a.Settlement != null)
                .Select(a => new { Definition = d, Ability = a })).ToList();
            foreach (var item in declared)
            {
                var declaration = item.Ability.Settlement;
                var policy = declaration.EntryKind == SettlementEntryService.OptionalInsertion ? SettlementWholeEffectPolicy.Optional :
                    declaration.WholeEffectPolicy == "optional" ? SettlementWholeEffectPolicy.Optional :
                    declaration.WholeEffectPolicy == "required" ? SettlementWholeEffectPolicy.Required : SettlementWholeEffectPolicy.Unspecified;
                if (policy == SettlementWholeEffectPolicy.Unspecified) throw new InvalidOperationException("settlement_whole_effect_policy_missing");
                registry.RegisterSettlementSourceContract(new SettlementSourceContract(item.Definition.DefinitionId, item.Ability.AbilityId,
                    declaration.LocalId, declaration.EntryKind, declaration.AvailabilityHandlerId, policy));
            }
            catalog.Register(registry);
            registry.RegisterSettlementCatalogProvider(pack.ContentHash, (state, window) =>
            {
                if (window.IsClosed || window.Context.Kind == SettlementEntryService.PlayerCleanup) return;
                foreach (var item in declared.Where(d => d.Ability.Settlement.EntryKind == SettlementEntryService.OptionalInsertion))
                {
                    var context = SettlementEntryService.CreateQueryContext(state, window);
                    var facts = CreateFacts(state, context, null, item.Ability.AbilityId);
                    foreach (var offer in catalog.GetOffers(item.Ability.AbilityId, context, facts))
                    {
                        var source = state.ItemZones.Items.Find(i => i.ItemId == offer.SourceItemId && i.DefinitionId == item.Definition.DefinitionId &&
                            i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId == context.PlayerId);
                        var activation = FindActivation(state, offer.SourceItemId, item.Ability.AbilityId, context.PlayerId);
                        if (source == null || activation == null || !CharacterLifecycleService.IsSubscriptionActive(state, source.ItemId, item.Ability.AbilityId))
                            throw new InvalidOperationException("settlement_offer_source_not_authorized");
                        var entry = SettlementEntryService.RegisterOffer(window, offer);
                        entry.AbilityId = item.Ability.AbilityId;
                        entry.SourceDefinitionId = item.Definition.DefinitionId;
                        entry.SourceEffectId = activation.EffectId;
                    }
                }
            }, (state, window, entry, forceRefresh) =>
            {
                var context = SettlementEntryService.CreateQueryContext(state, window);
                return catalog.QueryAvailability(context, entry, CreateFacts(state, context, entry, entry.AbilityId), forceRefresh);
            });
            Catalogs.Add(registry, catalog);
            return catalog;
        }

        public static EffectNodeRuntimeState FindActivation(GameState state, string itemId, string abilityId, int playerId) =>
            state.EffectRuntime.EffectNodes.LastOrDefault(n => n.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId &&
                n.PlayerId == playerId && Text(n.NormalizedArguments, "abilityId") == abilityId &&
                Value(n.NormalizedArguments, "cardInstanceId")?.ReferenceId == itemId && n.PendingOutcome == EffectPendingOutcome.Completed);

        private static NormalizedValue CreateFacts(GameState state, SettlementWindowContext window, SettlementEntryRuntimeState entry, string abilityId)
        {
            var player = state.FindPlayer(window.PlayerId);
            var sources = state.ItemZones.Items.Where(i => i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId == window.PlayerId &&
                CharacterLifecycleService.IsSubscriptionActive(state, i.ItemId, abilityId) && FindActivation(state, i.ItemId, abilityId, window.PlayerId) != null)
                .OrderBy(i => i.ItemId, StringComparer.Ordinal).Select(i => Source(state, window, i.ItemId, abilityId, true)).ToList();
            var facts = Object("sourceItems", NormalizedValue.CreateArray(sources));
            if (entry != null)
            {
                // 已挂链段保留登记时的来源证明；离场只停止新订阅，不撤销既成义务。
                bool registeredCleanup = entry.EntryKind == SettlementEntryService.CleanupOrder &&
                    state.EffectRuntime.EffectNodes.Any(n => n.EffectId == entry.TargetEffectId && n.Settlement?.SourceItemId == entry.SourceItemId && n.Settlement.AbilityId == abilityId);
                facts.Properties.Add(new NormalizedValueEntry { Name = "source", Value = Source(state, window, entry.SourceItemId, abilityId,
                    registeredCleanup || sources.Any(v => Text(v, "itemId") == entry.SourceItemId)) });
                var binding = state.FacilityCharacterBindings.FirstOrDefault(b => b.CharacterItemId == entry.SourceItemId &&
                    b.ControllerPlayerId == window.PlayerId && b.Active);
                var facility = state.ItemZones.Items.Find(i => i.ItemId == binding?.FacilityItemId);
                int baseCost = facility == null ? 0 : YC.Domain.Facilities.FacilityCardDatabase.Get(state, facility.DefinitionId).GoldVoucherCost;
                facts.Properties.Add(new NormalizedValueEntry { Name = "binding", Value = Object("active", NormalizedValue.CreateBoolean(binding != null),
                    "goldVoucherCost", NormalizedValue.CreateInteger(baseCost)) });
            }
            var use = state.CharacterUses.Find(u => u.UseId == window.TargetUseId && u.ExecutingPlayerId == window.PlayerId);
            if (use != null) facts.Properties.Add(new NormalizedValueEntry { Name = "currentUse", Value = Object(
                "cardInstanceId", NormalizedValue.CreateString(use.CardInstanceId), "modeStarted", NormalizedValue.CreateBoolean(use.ModeOrder.Count > 0 || use.CompletedModes.Count > 0),
                "sourceConsumed", NormalizedValue.CreateBoolean(use.SourceConsumed), "doubleUseGranted", NormalizedValue.CreateBoolean(use.DoubleUseGranted),
                "hasTwoUsableModes", NormalizedValue.CreateBoolean(!use.Finished && use.Modes.Select(m => m.Mode).Distinct().Count() == 2 &&
                    state.CurrentPlayerId == window.PlayerId && !player.CharacterCardLockedThisTurn)) });
            var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == window.OwnerEffectId);
            var markers = state.ItemZones.Zones.Select(z => ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, window.PlayerId, z.ZoneId))
                .Where(z => z != null).Select(z => Object("eligible", NormalizedValue.CreateBoolean(true), "markerCount",
                    NormalizedValue.CreateInteger(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, z, window.PlayerId).Count))).ToList();
            facts.Properties.Add(new NormalizedValueEntry { Name = "payment", Value = Object(
                "canConfirm", NormalizedValue.CreateBoolean(window.Kind == SettlementEntryService.ResourcePayment && owner?.Payment != null && owner.Payment.BeforePaymentReady && owner.Payment.AllowsSubstitution && !owner.Payment.IsCommitted &&
                    (state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == owner.EffectId && r.AnsweringPlayerId == window.PlayerId && r.Status == "open") ||
                     owner.SettlementWindow?.SuspendedInteractionId?.Length > 0 && owner.SettlementWindow.ActiveTargetEffectId.Length > 0)),
                "isCommitted", NormalizedValue.CreateBoolean(owner?.Payment?.IsCommitted == true), "available", Resources(player.Resources),
                "markerSources", NormalizedValue.CreateArray(markers)) });
            facts.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return facts;
        }

        private static NormalizedValue Source(GameState state, SettlementWindowContext window, string itemId, string abilityId, bool active)
        {
            var zones = state.ItemZones.Zones.Where(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == itemId && z.TemplateId == "core.zone.card_cube_storage").ToList();
            var paymentZone = zones.FirstOrDefault(z => ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, window.PlayerId, z.ZoneId) != null);
            return Object("itemId", NormalizedValue.CreateString(itemId), "abilityId", NormalizedValue.CreateString(abilityId),
                "persistentActive", NormalizedValue.CreateBoolean(active), "markerCount", NormalizedValue.CreateInteger(paymentZone != null
                    ? ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, paymentZone, window.PlayerId).Count
                    : state.ItemZones.Items.Count(i => zones.Any(z => z.ZoneId == i.ZoneId) && i.PhysicalKind == ItemPhysicalKind.Cube)),
                "storageZoneId", NormalizedValue.CreateString(paymentZone?.ZoneId ?? ""));
        }
        private static NormalizedValue Resources(ResourceSet set) => Object("originium", NormalizedValue.CreateInteger(set.Originium),
            "originiumShard", NormalizedValue.CreateInteger(set.OriginiumShard), "iron", NormalizedValue.CreateInteger(set.Iron),
            "pureOriginium", NormalizedValue.CreateInteger(set.PureOriginium), "goldVoucher", NormalizedValue.CreateInteger(set.GoldVoucher));
    }
}
