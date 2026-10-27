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
            ResourcePaymentSourceService.Register(registry);
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
                    // 所有付款版本均消费内容声明的真实插入Offer；只读来源查询不替代结算入口。
                    var context = SettlementEntryService.CreateQueryContext(state, window);
                    var facts = CreateFacts(state, context, null, item.Ability.AbilityId, registry);
                    foreach (var offer in catalog.GetOffers(item.Ability.AbilityId, context, facts))
                    {
                        if (ResourcePaymentInsertionService.SupersedesLegacyOffer(state, registry, window, offer)) continue;
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
                return catalog.QueryAvailability(context, entry, CreateFacts(state, context, entry, entry.AbilityId, registry), forceRefresh,
                    SourceFactsLuaCatalog.ForSettlement(state, registry, context, entry));
            });
            Catalogs.Add(registry, catalog);
            return catalog;
        }

        public const string EligibilityVersion="cleanup_eligibility.v1.0.0";
        public static bool CanInjectCleanup(GameState state,EffectRegistry registry,SettlementLuaCatalog catalog,AbilitySourceRef source,RuleEvent trigger)
        {
            if(trigger.EventType!="PlayerCleanupStarted" || trigger.PlayerId!=source.AuthorizedPlayerId) throw new InvalidOperationException("cleanup_injection_scope_invalid");
            var declaration=state.ContentDefinitions.AbilitySources.Single(d=>d.AbilityId==source.AbilityId);
            if(declaration.Kind!="cleanup" || declaration.RepeatPolicy!="once") throw new InvalidOperationException("cleanup_injection_policy_invalid");
            var contract=registry.TryGetSettlementSourceContract(source.AbilityId,out var c) ? c : throw new InvalidOperationException("settlement_ability_not_registered");
            var window=new SettlementWindowContext { Id=StableIdFactory.Create("cleanup-injection",trigger.EventId,source.SourceKey),Kind=SettlementEntryService.PlayerCleanup,
                TriggerId=trigger.EventId,OwnerEffectId=trigger.OwnerNodeId,PlayerId=source.AuthorizedPlayerId,StateRevision=state.EffectRuntime.StateRevision };
            var entry=new SettlementEntryRuntimeState { AbilityId=source.AbilityId,SourceItemId=source.ItemId,SourceDefinitionId=source.DefinitionId,
                EntryKind=SettlementEntryService.CleanupOrder,AvailabilityHandlerId=contract.AvailabilityHandlerId,OptionId=window.Id };
            return catalog.QueryAvailability(window,entry,CreateFacts(state,window,entry,source.AbilityId,registry),true,SourceFactsLuaCatalog.Create(state,registry,source)).Enabled;
        }
        public static EffectNodeRuntimeState FindActivation(GameState state, string itemId, string abilityId, int playerId) =>
            state.EffectRuntime.EffectNodes.LastOrDefault(n => n.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId &&
                n.PlayerId == playerId && Text(n.NormalizedArguments, "abilityId") == abilityId &&
                Value(n.NormalizedArguments, "cardInstanceId")?.ReferenceId == itemId && n.PendingOutcome == EffectPendingOutcome.Completed);

        private static NormalizedValue CreateFacts(GameState state, SettlementWindowContext window, SettlementEntryRuntimeState entry, string abilityId, EffectRegistry registry)
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
            if (owner?.Payment?.PaymentProtocolVersion == 2)
                facts.Properties.Add(new NormalizedValueEntry { Name = "paymentSourceQuery", Value = ResourcePaymentDraftHost.SourceQuery(state, registry, owner) });
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
            var source = state.ContentDefinitions.AbilitySources.SingleOrDefault(d => d.AbilityId == abilityId);
            var item = state.ItemZones.Items.Find(i => i.ItemId == itemId);
            var permissions = source?.AllowedOperations.Where(p => p.StartsWith("carrier.transfer.", StringComparison.Ordinal) && CardMarkerCarrier.ValidPermission(p)).Select(p => p.Split(':'))
                .Where(p => p[1] == item?.DefinitionId).ToList() ?? new List<string[]>();
            int declaredPayable = state.ItemZones.Items.Count(i => zones.Any(z => z.ZoneId == i.ZoneId &&
                CardMarkerCarrier.AcceptsTransfer(state, z, i, window.PlayerId, permissions.Where(p => p[2] == z.LocalZoneId).Select(p => p[3]))));
            var current = AbilitySourceService.QuerySources(state, state.ContentDefinitions.AbilitySources, window.PlayerId)
                .SingleOrDefault(s => s.ItemId == itemId && s.AbilityId == abilityId);
            var transferable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permission in permissions)
            {
                var zone = CardMarkerCarrier.ResolveWithSource(state, current, window.PlayerId, itemId, permission[2],
                    permission[0].Substring("carrier.".Length), state.ContentDefinitions.AbilitySources, out var types, out _);
                if (zone != null) foreach (var token in state.ItemZones.Items.Where(i => i.ZoneId == zone.ZoneId &&
                    CardMarkerCarrier.AcceptsTransfer(state, zone, i, window.PlayerId, types))) transferable.Add(token.ItemId);
            }
            return Object("itemId", NormalizedValue.CreateString(itemId), "abilityId", NormalizedValue.CreateString(abilityId),
                "transferableTokenCount", NormalizedValue.CreateInteger(transferable.Count),
                "persistentActive", NormalizedValue.CreateBoolean(active), "markerCount", NormalizedValue.CreateInteger(window.Kind == SettlementEntryService.ResourcePayment
                    ? paymentZone == null ? 0 : ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, paymentZone, window.PlayerId).Count
                    : permissions.Count > 0 ? declaredPayable : state.ItemZones.Items.Count(i => zones.Any(z => z.ZoneId == i.ZoneId) && i.PhysicalKind == ItemPhysicalKind.Cube)),
                "storageZoneId", NormalizedValue.CreateString(paymentZone?.ZoneId ?? ""));
        }
        private static NormalizedValue Resources(ResourceSet set) => Object("originium", NormalizedValue.CreateInteger(set.Originium),
            "originiumShard", NormalizedValue.CreateInteger(set.OriginiumShard), "iron", NormalizedValue.CreateInteger(set.Iron),
            "pureOriginium", NormalizedValue.CreateInteger(set.PureOriginium), "goldVoucher", NormalizedValue.CreateInteger(set.GoldVoucher));
    }
}
