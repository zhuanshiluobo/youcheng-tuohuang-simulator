using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Domain.Cards;
using YC.Domain.Presentation;

namespace YC.Infrastructure.Lua
{
    /// <summary>消费内容包登记及唯一 Lua Host；只登记元数据和查询资格，不另建 VM、节点或提交事务。</summary>
    public sealed class SettlementLuaCatalog
    {
        private readonly ExternalContentPack pack;
        private readonly MoonSharpLuaRuntimeHost host;
        private readonly Dictionary<string, SettlementAvailabilityResult> availabilityCache =
            new Dictionary<string, SettlementAvailabilityResult>(StringComparer.Ordinal);
        private int cacheRevision = -1;

        // Host只传这些授权快照；各入口的细字段在独立规则脚本和交接报告登记。
        private static readonly HashSet<string> FactRoots = new HashSet<string>(StringComparer.Ordinal)
            { "sourceItems", "source", "currentUse", "payment", "binding", "cleanup" };

        public SettlementLuaCatalog(ExternalContentPack pack, MoonSharpLuaRuntimeHost host = null)
        { this.pack = pack ?? throw new ArgumentNullException(nameof(pack)); this.host = host ?? new MoonSharpLuaRuntimeHost(); }

        public IReadOnlyList<SettlementOfferDefinition> GetOffers(string abilityId, SettlementWindowContext window,
            NormalizedValue authorizedFacts)
        {
            var ability = FindAbility(abilityId);
            if (ability.Settlement.EntryKind != SettlementEntryService.OptionalInsertion)
                throw new ArgumentException("settlement_offer_entry_kind_invalid");
            var result = Invoke(abilityId, ability.Settlement.OfferHandlerId, window, authorizedFacts, "settlementOffers", "SettlementAvailabilityContextChanged");
            if (!result.IsSuccess) throw new InvalidOperationException("settlement_offer_lua_failed:" + result.Diagnostic);
            if (result.Effects.Count != 0 || result.CandidatePatches.Count != 0 || result.SettlementAvailability != null)
                throw new InvalidOperationException("settlement_offer_response_mixed");
            foreach (var offer in result.SettlementOffers)
                if (offer.LocalId != ability.Settlement.LocalId || offer.AvailabilityHandlerId != ability.Settlement.AvailabilityHandlerId)
                    throw new InvalidOperationException("settlement_offer_declaration_mismatch");
            return result.SettlementOffers;
        }

        public void RegisterOffers(SettlementWindowRuntimeState window, string abilityId, NormalizedValue authorizedFacts)
        {
            foreach (var offer in GetOffers(abilityId, window.Context, authorizedFacts))
            {
                var entry = SettlementEntryService.RegisterOffer(window, offer);
                entry.AbilityId = abilityId;
                entry.SourceDefinitionId = pack.ActiveDefinitions.Single(d => d.Abilities.Any(a => a.AbilityId == abilityId)).DefinitionId;
            }
        }

        // 公共装配入口只需调用一次；事实提供者和窗口生命周期仍由 Host 注册。
        public void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            foreach (var ability in pack.ActiveDefinitions.SelectMany(d => d.Abilities).Where(a => a.Settlement != null))
            {
                string abilityId = ability.AbilityId;
                if (ability.Settlement.EntryKind == SettlementEntryService.OptionalInsertion)
                    registry.RegisterSettlementOfferProvider(ability.Settlement.OfferHandlerId,
                        (window, facts) => GetOffers(abilityId, window, facts));
                registry.RegisterSettlementAvailabilityHandler(ability.Settlement.AvailabilityHandlerId,
                    (window, offer, facts) => QueryAvailability(window, new SettlementEntryRuntimeState {
                        AbilityId = abilityId, AvailabilityHandlerId = offer.AvailabilityHandlerId,
                        OptionId = StableIdFactory.Create("settlement_query", window.Id, offer.SourceItemId, offer.LocalId)
                    }, facts));
            }
        }

        // persistent Event 正文在 Event 响应阶段成为真实排序段；绑定窗口也在此转换为真实结算正文。
        public IList<EffectSpec> CreateCleanupResponse(EffectEventHandlerContext context, string abilityId,
            IList<EffectSpec> body, string sourceItemId, string sourceEffectId = "")
        {
            if (context?.State == null || context.Event.EventType != "PlayerCleanupStarted" || body == null)
                throw new ArgumentException("settlement_cleanup_event_invalid");
            var effects = new List<EffectSpec>();
            foreach (var spec in body)
            {
                if (spec != null) effects.Add(spec);
                else throw new ArgumentException("settlement_cleanup_body_invalid");
            }
            if (effects.Count == 0) return effects;
            var root = SettlementEntryService.CreateCleanupSegment(context.Event.PlayerId,
                FindAbility(abilityId).Settlement.LocalId, effects);
            DecorateCleanupRoot(root, abilityId, sourceItemId, sourceEffectId);
            return new List<EffectSpec> { root };
        }

        // TimingBinding/AwardCleanup 必须标记其已有外层根，不能给内部正文另造排序按钮。
        public EffectSpec DecorateCleanupRoot(EffectSpec root, string abilityId, string sourceItemId,
            string sourceEffectId = "")
        {
            var declaration = FindAbility(abilityId).Settlement;
            if (root == null || root.Settlement != null || string.IsNullOrEmpty(sourceItemId) ||
                declaration.EntryKind != SettlementEntryService.CleanupOrder ||
                declaration.WholeEffectPolicy != "optional" && declaration.WholeEffectPolicy != "required")
                throw new ArgumentException("settlement_cleanup_declaration_invalid");
            root.Settlement = new SettlementEffectDefinition {
                EntryKind = declaration.EntryKind, LocalId = declaration.LocalId, AbilityId = abilityId,
                SourceItemId = sourceItemId, SourceEffectId = sourceEffectId ?? "",
                WholeEffectPolicy = declaration.WholeEffectPolicy == "optional" ? SettlementWholeEffectPolicy.Optional : SettlementWholeEffectPolicy.Required,
                Presentation = GameplayPresentationNormalizer.Clone(declaration.Presentation)
            };
            root.SourceId = sourceItemId;
            return root;
        }

        public SettlementAvailabilityResult QueryAvailability(SettlementWindowContext window, SettlementEntryRuntimeState entry,
            NormalizedValue authorizedFacts, bool forceRefresh = false)
        {
            if (entry == null || window == null) throw new ArgumentNullException();
            var ability = FindAbility(entry.AbilityId);
            if (entry.AvailabilityHandlerId != ability.Settlement.AvailabilityHandlerId)
                throw new ArgumentException("settlement_availability_handler_mismatch");
            if (cacheRevision != window.StateRevision) { availabilityCache.Clear(); cacheRevision = window.StateRevision; }
            string key = window.Id + "\u001f" + entry.OptionId + "\u001f" + pack.ContentHash;
            if (!forceRefresh && availabilityCache.TryGetValue(key, out var cached)) return Clone(cached);
            var result = Invoke(entry.AbilityId, entry.AvailabilityHandlerId, window, authorizedFacts,
                "settlementAvailability", "SettlementAvailabilityRequested");
            if (!result.IsSuccess || result.SettlementAvailability == null || result.Effects.Count != 0 ||
                result.CandidatePatches.Count != 0 || result.SettlementOffers.Count != 0)
                throw new InvalidOperationException("settlement_availability_lua_failed:" + result.Diagnostic);
            availabilityCache[key] = Clone(result.SettlementAvailability);
            return Clone(result.SettlementAvailability);
        }

        public void ClearQueryCache() { availabilityCache.Clear(); cacheRevision = -1; }

        private ExternalAbilityDefinition FindAbility(string abilityId)
        {
            var abilities = pack.ActiveDefinitions.SelectMany(d => d.Abilities).Where(a => a.AbilityId == abilityId).ToList();
            if (abilities.Count != 1 || abilities[0].Settlement == null) throw new ArgumentException("settlement_ability_not_registered");
            return abilities[0];
        }

        private LuaInvocationResult Invoke(string abilityId, string handlerId, SettlementWindowContext window,
            NormalizedValue facts, string responseKind, string eventType)
        {
            if (window == null || window.PlayerId < 1 || string.IsNullOrEmpty(window.Id) || string.IsNullOrEmpty(window.OwnerEffectId) ||
                facts?.Kind != NormalizedValueKind.Object || !facts.IsValid() || facts.Properties.Any(e => !FactRoots.Contains(e.Name)))
                throw new ArgumentException("settlement_authorized_context_invalid");
            var handlers = pack.SettlementHandlers.Where(h => h.HandlerId == handlerId).ToList();
            if (handlers.Count != 1 || string.IsNullOrEmpty(handlers[0].EntryPoint)) throw new ArgumentException("settlement_handler_not_registered");
            var handler = handlers[0]; string source = pack.GetScript(handler.Script);
            var definition = new LuaScriptDefinition("content." + handlerId, abilityId, handlerId, handler.Version, source,
                LuaContentHasher.ComputeSha256(source));
            var context = new LuaInvocationContext(window.Id, eventType, "p" + window.PlayerId.ToString(CultureInfo.InvariantCulture),
                window.StateRevision, handler.Version, 1, 0, 0) {
                EntryPoint = handler.EntryPoint, ResponseKind = responseKind,
                SettlementContext = GameStateCloneService.DeepClone(window), SettlementFacts = facts.Clone() };
            return host.Invoke(definition, context);
        }

        private static SettlementAvailabilityResult Clone(SettlementAvailabilityResult value) =>
            new SettlementAvailabilityResult { Enabled = value.Enabled, DisabledReasonKey = value.DisabledReasonKey };
    }
}
