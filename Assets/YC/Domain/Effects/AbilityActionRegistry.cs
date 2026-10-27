using System;
using YC.Domain.Items;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Presentation;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        private List<AbilitySourceDeclaration> abilityDeclarations = new List<AbilitySourceDeclaration>();
        private readonly Dictionary<string, Func<AbilityActionCallContext, IList<EffectSpec>>> abilityBodies = new Dictionary<string, Func<AbilityActionCallContext, IList<EffectSpec>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> abilityHandlerHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, AbilityActionCallPolicy> actionCallPolicies = new Dictionary<string, AbilityActionCallPolicy>(StringComparer.Ordinal);
        private readonly Dictionary<string, IAbilityActionUsageProvider> actionUsageProviders = new Dictionary<string, IAbilityActionUsageProvider>(StringComparer.Ordinal);
        private readonly Dictionary<string, AbilityActionArgumentNormalizer> actionArgumentProtocols = new Dictionary<string, AbilityActionArgumentNormalizer>(StringComparer.Ordinal);
        public void RegisterActionArgumentProtocol(string handlerRef, AbilityActionArgumentNormalizer normalize)
        {
            if (HasAbilityCatalog || !abilityBodies.ContainsKey(handlerRef) || normalize == null) throw new ArgumentException("action_argument_protocol_invalid_or_frozen");
            actionArgumentProtocols.Add(handlerRef, normalize);
        }
        public string NormalizeActionArguments(string actionId, NormalizedValue input, out NormalizedValue normalized)
        {
            normalized = null;
            if (input?.Kind != NormalizedValueKind.Object || !input.IsValid()) return "action_arguments_invalid";
            var action = abilityDeclarations.SelectMany(d => d.Actions).SingleOrDefault(a => a.ActionId == actionId);
            if (action == null) return "action_declaration_missing";
            if (actionArgumentProtocols.TryGetValue(action.BodyHandlerRef, out var normalize)) return normalize(input, out normalized);
            if (input.Properties.Count > 0) return "action_arguments_not_supported";
            normalized = Object(); return "";
        }
        public bool HasAbilityCatalog { get; private set; }
        public readonly Dictionary<string, string> AbilityPresentationNames = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> AbilityPresentationDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        private Func<GameplayTextRef, string> actionTextResolver;
        public void RegisterActionTextResolver(Func<GameplayTextRef, string> resolve)
        {
            if (HasAbilityCatalog || actionTextResolver != null || resolve == null) throw new ArgumentException("action_text_resolver_invalid_or_frozen");
            actionTextResolver = resolve;
        }
        private string ActionText(GameplayTextRef reference, string fallback = "") => reference == null ? fallback :
            actionTextResolver != null ? actionTextResolver(reference) : throw new InvalidOperationException("action_text_resolver_missing");
        private Func<GameState, AbilitySourceRef, AbilityActionDeclaration, IList<EffectSpec>, string> actionCostPreview;
        private Func<GameState, AbilitySourceRef, EffectSpec, string> itemTransferEntryCostPreview;
        // 005最终通用转移合同的纯预览接点；不在调用端重造权限、选物或转移规划。
        public void RegisterItemTransferEntryCostPreview(Func<GameState, AbilitySourceRef, EffectSpec, string> preview)
        { if (preview == null || itemTransferEntryCostPreview != null) throw new ArgumentException("item_transfer_cost_preview_duplicate_or_null"); itemTransferEntryCostPreview = preview; }
        public string PreviewItemTransferEntryCost(GameState state, AbilitySourceRef source, EffectSpec cost)
        {
            if (state == null || source == null || cost?.EffectTypeId != ItemTransferEffectExecutor.TypeId ||
                cost.PlayerId >= 0 && cost.PlayerId != source.AuthorizedPlayerId || !AbilitySourceService.IsCurrent(state, abilityDeclarations, source)) return "entry_cost_source_invalid";
            if (itemTransferEntryCostPreview == null) return "item_transfer_cost_preview_not_registered";
            return itemTransferEntryCostPreview(state, source, cost) ?? throw new InvalidOperationException("item_transfer_cost_preview_invalid");
        }
        public void RegisterActionCostPreview(Func<GameState, AbilitySourceRef, AbilityActionDeclaration, IList<EffectSpec>, string> preview)
        { if (preview == null || actionCostPreview != null) throw new ArgumentException("action_cost_preview_duplicate_or_null"); actionCostPreview = preview; }
        public string ValidateActionEntryCost(GameState state, AbilitySourceRef source, AbilityActionDeclaration action, IList<EffectSpec> costs)
        {
            string shape = AbilityActionEntryCondition.Unwrap(costs, out var payment);
            if (shape.Length > 0) return shape;
            return actionCostPreview != null ? actionCostPreview(state, source, action, payment) :
                AbilityActionExecutor.PreviewCost(state, this, source, payment);
        }
        public IReadOnlyList<AbilitySourceDeclaration> AbilityDeclarations => abilityDeclarations.Select(GameStateCloneService.DeepClone).ToList();
        // 跨层只暴露所需标量/独立集合，完整可变声明继续由公开属性防御复制。
        public string GetAbilitySourceHandlerHash(string abilityId) => GetAbilityHandlerHash(abilityDeclarations.Single(d => d.AbilityId == abilityId).HandlerRef);
        public bool HasAbilitySourceKind(string abilityId, string kind) => abilityDeclarations.Any(d => d.AbilityId == abilityId && d.Kind == kind);
        public IReadOnlyList<string> GetAbilitySourceOperations(string abilityId) =>
            abilityDeclarations.Single(d => d.AbilityId == abilityId).AllowedOperations.ToList().AsReadOnly();
        public ZoneInstanceState QueryAuthorizedCarrierZone(GameState state, AbilitySourceRef source, int player,
            string cardId, string localId, string operation, out List<string> acceptedTypes, out string reason) =>
            GameStateCloneService.DeepClone(CardMarkerCarrier.ResolveWithSource(state, source, player, cardId, localId, operation,
                abilityDeclarations, out acceptedTypes, out reason));

        private readonly Dictionary<string,string> abilityHandlerVersions=new Dictionary<string,string>(StringComparer.Ordinal);
        public string GetAbilityHandlerVersion(string id)=>abilityHandlerVersions.TryGetValue(id,out var v)?v:"";
        public void RegisterAbilityHandler(string id, string hash, Func<AbilityActionCallContext, IList<EffectSpec>> handler,string version="1.0.0")
        {
            if (HasAbilityCatalog || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(hash) || handler == null) throw new ArgumentException("ability_handler_invalid_or_frozen");
            abilityBodies.Add(id, handler); abilityHandlerHashes.Add(id, hash); abilityHandlerVersions.Add(id,version);
        }
        public void RegisterActionCallPolicy(AbilityActionCallPolicy policy)
        {
            if (HasAbilityCatalog || policy == null || string.IsNullOrEmpty(policy.PolicyRef) || string.IsNullOrEmpty(policy.CallerAbilityId) ||
                string.IsNullOrEmpty(policy.TargetActionId) || string.IsNullOrEmpty(policy.CallerHandlerHash)) throw new ArgumentException("action_call_policy_invalid");
            actionCallPolicies.Add(policy.PolicyRef, GameStateCloneService.DeepClone(policy));
        }
        public void RegisterActionUsageProvider(string trackId, IAbilityActionUsageProvider provider)
        {
            if (string.IsNullOrEmpty(trackId) || provider == null || string.IsNullOrEmpty(provider.DefinitionVersion)) throw new ArgumentException("action_usage_provider_invalid");
            actionUsageProviders.Add(trackId, provider);
        }
        public void FreezeAbilityCatalog(IEnumerable<AbilitySourceDeclaration> declarations)
        {
            if (HasAbilityCatalog) throw new InvalidOperationException("ability_catalog_already_frozen");
            var values = declarations.Select(GameStateCloneService.DeepClone).ToList();
            var ids = new HashSet<string>(StringComparer.Ordinal); var actions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in values)
            {
                if (!new[] { "once", "unbounded" }.Contains(d.RepeatPolicy) || d.Kind != "cleanup" && d.RepeatPolicy != "once" ||
                    d.RepeatPolicy == "unbounded" && (d.ObservedEvents == null || !d.ObservedEvents.SequenceEqual(new[] { "PlayerCleanupStarted" }) ||
                        TryGetSettlementSourceContract(d.AbilityId, out var cleanupContract) && cleanupContract.WholeEffectPolicy != SettlementWholeEffectPolicy.Optional))
                    throw new InvalidOperationException("cleanup_repeat_policy_invalid:" + d.AbilityId);
                if (d.AllowedOperations?.Any(p => !CardMarkerCarrier.ValidPermission(p)) == true) throw new InvalidOperationException("carrier_permission_declaration_invalid:" + d.AbilityId);
                if (d.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(d.DefinitionId) || string.IsNullOrWhiteSpace(d.AbilityId) || !ids.Add(d.AbilityId) ||
                    string.IsNullOrWhiteSpace(d.DeclarationVersion) || !new[] { "event", "cleanup", "quick", "special", "ruleQuery", "setup" }.Contains(d.Kind) ||
                    !new[] { "controller", "holder", "owner", "character_covered", "character_subscription", "active_selection", "public_declaration" }.Contains(d.AuthorizationPolicy) ||
                    d.EffectiveZones == null || d.EffectiveZones.Count == 0 || d.EffectiveZones.Distinct().Count() != d.EffectiveZones.Count || d.Actions == null ||
                    d.ObservedEvents == null || d.QueryKinds == null || d.AllowedOperations == null || string.IsNullOrEmpty(d.PresentationRef) || !abilityBodies.ContainsKey(d.HandlerRef) ||
                    d.Kind == "ruleQuery" && !abilityPureHandlers.ContainsKey(d.HandlerRef)) throw new InvalidOperationException("ability_declaration_invalid:" + d.AbilityId);
                foreach (var a in d.Actions)
                {
                    if (a.Presentation != null)
                    {
                        GameplayPresentationNormalizer.Validate(a.Presentation);
                        if (a.Presentation.Name == null || a.Presentation.Description == null || actionTextResolver == null ||
                            a.Presentation.EntryCost != null && a.EntryCostRef.Length == 0 ||
                            a.Presentation.EntryAfterEffects != null && a.EntryAfterEffectsRef.Length == 0)
                            throw new InvalidOperationException("action_presentation_invalid:" + a.ActionId);
                    }
                    if (string.IsNullOrWhiteSpace(a.ActionId) || !actions.Add(a.ActionId) || !new[] { "quick", "special" }.Contains(a.EntryKind) ||
                        !new[] { "unbounded", "once", "round", "action_round", "position" }.Contains(a.LifecyclePolicy) ||
                        !new[] { "none", "character_action_round" }.Contains(a.LockPolicy) || string.IsNullOrEmpty(a.PresentationRef) ||
                        !abilityBodies.ContainsKey(a.BodyHandlerRef) || a.EntryCostRef.Length > 0 && !abilityBodies.ContainsKey(a.EntryCostRef) ||
                        a.EntryAfterEffectsRef.Length > 0 && !abilityBodies.ContainsKey(a.EntryAfterEffectsRef) || a.DirectCallPolicy != "deny" && a.DirectCallPolicy != AbilityLegacyStyleBridge.PolicyRef && !actionCallPolicies.ContainsKey(a.DirectCallPolicy)) throw new InvalidOperationException("action_declaration_invalid:" + a.ActionId);
                }
                foreach (var group in d.Actions.Where(a => a.UsageTrackId.Length > 0).GroupBy(a => a.UsageTrackId))
                    if (group.Select(a => a.BodyHandlerRef).Distinct().Count() != 1) throw new InvalidOperationException("action_shared_body_conflict:" + group.Key);
            }
            foreach (var p in actionCallPolicies.Values)
                if (!values.Any(d => d.AbilityId == p.CallerAbilityId && d.AllowedOperations.Contains("action.invoke:" + p.PolicyRef)) ||
                    !values.SelectMany(d => d.Actions).Any(a => a.ActionId == p.TargetActionId && a.DirectCallPolicy == p.PolicyRef)) throw new InvalidOperationException("action_call_policy_unbound:" + p.PolicyRef);
            abilityDeclarations = values; HasAbilityCatalog = true;
            if (ContentDefinitions != null) ContentDefinitions.AbilitySources = AbilityDeclarations.ToList();
        }
        public IReadOnlyList<AbilitySourceRef> QuerySources(GameState state, int playerId = -1, string kind = "", string eventType = "", string queryKind = "")
            => AbilitySourceService.QuerySources(state, abilityDeclarations, playerId, kind, eventType, queryKind);
        internal AbilitySourceDeclaration SourceDeclaration(AbilitySourceRef source) => abilityDeclarations.Single(d => d.AbilityId == source.AbilityId && d.DefinitionId == source.DefinitionId);
        internal AbilityActionDeclaration ActionDeclaration(string actionId) => abilityDeclarations.SelectMany(d => d.Actions).Single(a => a.ActionId == actionId);
        internal string HandlerHash(string handler) => abilityHandlerHashes.TryGetValue(handler, out string hash) ? hash : "";
        public string GetAbilityHandlerHash(string handler) => HandlerHash(handler);
        // 只读来源事实的调用身份。费用构造发生在 accepted，不能借正文 hash 提前获得正文权限。
        public bool TryResolveSourceFactsCall(AbilityActionCallContext call, string handlerId, string handlerHash,
            out AbilitySourceRef source, out bool includeCallableActions)
        {
            source = null; includeCallableActions = false;
            var state = call?.State;
            if (state?.EffectRuntime == null || call.SourceRef == null || state.ContentPackHash != ContentPackHash ||
                string.IsNullOrEmpty(handlerId) || string.IsNullOrEmpty(handlerHash) || HandlerHash(handlerId) != handlerHash) return false;
            var current = QuerySources(state, call.SourceRef.AuthorizedPlayerId).SingleOrDefault(s => SameFactsSource(s, call.SourceRef));
            if (current == null) return false;
            var declaration = SourceDeclaration(current);
            if (IsSourceFactsEventPreview(call, handlerId, handlerHash))
            {
                source = GameStateCloneService.DeepClone(current); return true;
            }
            if (call.Action != null)
            {
                var action = declaration.Actions.SingleOrDefault(a => a.ActionId == call.Action.ActionId);
                // Action 必须是冻结目录本身的对象，不接受同名的调用方副本。
                if (!ReferenceEquals(action, call.Action)) return false;
                bool cost = handlerId == action.EntryCostRef;
                bool body = handlerId == action.BodyHandlerRef;
                bool after = handlerId == action.EntryAfterEffectsRef;
                if (!cost && !body && !after) return false;
                if (call.OwnerNode == null)
                {
                    if (!cost) return false;
                }
                else
                {
                    var owner = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == call.OwnerNode.EffectId);
                    var invocation = state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(i => i.OwnerEffectId == owner?.EffectId);
                    if (!ReferenceEquals(owner, call.OwnerNode) || owner.PlayerId != current.AuthorizedPlayerId ||
                        owner.EffectTypeId != AbilityActionExecutor.TypeId && owner.EffectTypeId != AbilityActionExecutor.DirectTypeId ||
                        invocation == null || invocation.ActionId != action.ActionId || !SameFactsSource(invocation.SourceRef, current) ||
                        invocation.VersionRef?.ContentPackHash != ContentPackHash || invocation.VersionRef.DefinitionVersion != declaration.DeclarationVersion ||
                        invocation.VersionRef.HandlerHash != HandlerHash(action.BodyHandlerRef) ||
                        invocation.VersionRef.ContractVersion != "QY-COMMON/1.0.1" || invocation.VersionRef.ExecutorVersion != AbilityActionExecutor.Version) return false;
                    bool entryPhase = invocation.Phase == "accepted" && !invocation.ActivationCommitted;
                    bool bodyPhase = invocation.Phase == "activated" && invocation.ActivationCommitted;
                    if (!(cost && entryPhase || (body || after) && bodyPhase)) return false;
                    includeCallableActions = body && bodyPhase;
                }
            }
            else if (call.OwnerNode == null && declaration.Kind == "ruleQuery" && declaration.HandlerRef == handlerId)
            {
                // 私有规则查询入口绑定了真实来源，但没有行动执行能力。
            }
            else if (call.OwnerNode?.EffectTypeId == EntrepreneurFlow.EntryTypeId)
            {
                // 独立即时入口没有action/SourceUse收据；消费正式选择流程自己的立即收据。
                if (call.OwnerNode.Status != EffectNodeStatus.Running || call.OwnerNode.FlowStage != "" ||
                    declaration.HandlerRef != handlerId || !TryResolveImmediateExecutionSource(state, call.OwnerNode, call.OwnerNode.EffectId,
                        out var immediate, out var immediateVersion) || immediateVersion.HandlerHash != handlerHash ||
                    !SameFactsSource(immediate, current)) return false;
            }
            else
            {
                if (call.OwnerNode == null || !ReferenceEquals(call.OwnerNode, state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == call.OwnerNode.EffectId)) ||
                    !TryResolveAbilityExecutionHandler(state, call.OwnerNode.EffectId, current.AbilityId, handlerHash, out var executing, out _) ||
                    !SameFactsSource(executing, current)) return false;
                var due = state.EffectRuntime.CleanupObligations.SingleOrDefault(o => o.InvokeEffectId == call.OwnerNode.EffectId && o.Status == "executing");
                string registeredHandler = due?.HandlerRef ?? ItemTransferEffectExecutor.Text(call.OwnerNode.NormalizedArguments, "handlerRef");
                if (registeredHandler.Length == 0) registeredHandler = declaration.HandlerRef;
                if (registeredHandler != handlerId) return false;
                includeCallableActions = true;
            }
            source = GameStateCloneService.DeepClone(current); return true;
        }
        public bool IsCurrentSourceFactsSource(GameState state, AbilitySourceRef source) => source != null && state.ContentPackHash == ContentPackHash &&
            QuerySources(state, source.AuthorizedPlayerId).Any(s => SameFactsSource(s, source));
        private static bool SameFactsSource(AbilitySourceRef a, AbilitySourceRef b) => a != null && b != null &&
            a.SourceKey == b.SourceKey && a.ItemId == b.ItemId && a.DefinitionId == b.DefinitionId && a.AbilityId == b.AbilityId &&
            a.ActiveFaceId == b.ActiveFaceId && a.DeclarationVersion == b.DeclarationVersion && a.AuthorizedPlayerId == b.AuthorizedPlayerId &&
            a.AuthorizationRef == b.AuthorizationRef && a.EffectiveZoneId == b.EffectiveZoneId && a.RuleOwnerItemId == b.RuleOwnerItemId &&
            a.GrantingSourceRefs != null && b.GrantingSourceRefs != null && a.GrantingSourceRefs.SequenceEqual(b.GrantingSourceRefs);
        internal IAbilityActionUsageProvider UsageProvider(string track) => actionUsageProviders.TryGetValue(track, out var provider) ? provider : null;
        internal bool TryCallPolicy(string id, out AbilityActionCallPolicy policy) => actionCallPolicies.TryGetValue(id ?? "", out policy);
        internal IList<EffectSpec> ConstructAbilityBody(string handler, AbilityActionCallContext context) => abilityBodies.TryGetValue(handler, out var body)
            ? body(context) : throw new InvalidOperationException("ability_handler_missing:" + handler);
        public IReadOnlyList<AbilityActionOption> QueryActions(GameState state, int playerId, string windowId = "", string contentVersion = "")
            => QueryActionOptions(state,playerId,windowId,contentVersion,null);
        private IReadOnlyList<AbilityActionOption> QueryActionOptions(GameState state,int playerId,string windowId,string contentVersion,AbilityActionCallPolicy directPolicy)
        {
            if (!HasAbilityCatalog || state.FindPlayer(playerId) == null) return Array.Empty<AbilityActionOption>();
            if (contentVersion.Length > 0 && contentVersion != ContentPackHash) throw new InvalidOperationException("content_version_mismatch");
            string window = AbilitySourceService.WindowId(state, playerId);
            if (windowId.Length > 0 && windowId != window) throw new InvalidOperationException("action_window_expired");
            var result = new List<AbilityActionOption>();
            foreach (var source in QuerySources(state, playerId))
            foreach (var action in SourceDeclaration(source).Actions)
            {
                if(directPolicy!=null && (action.ActionId!=directPolicy.TargetActionId || action.DirectCallPolicy!=directPolicy.PolicyRef)) continue;
                string binding = "";
                string reason = directPolicy==null ? AbilitySourceService.WindowReason(state, source, action) : ValidateAuthorizedActionEntry(state,source,action,directPolicy);
                if (action.UsageTrackId.Length > 0)
                {
                    string trackReason = UsageProvider(action.UsageTrackId)?.Validate(state, source, action, out binding) ?? "usage_track_unavailable";
                    if (reason.Length == 0) reason = trackReason;
                }
                var call = new AbilityActionCallContext { State = state, SourceRef = source, Action = action, Arguments = Object() };
                if (directPolicy==null && reason.Length == 0 && action.EntryCostRef.Length > 0) reason = ValidateActionEntryCost(state, source, action, ConstructAbilityBody(action.EntryCostRef, call));
                string optionId = StableIdFactory.Create("ability-action", source.SourceKey, action.ActionId);
                int generation = checked(1 + state.EffectRuntime.AbilityActionInvocations.Count(i => i.OptionId == optionId && i.RequestRef.WindowId == window));
                string digest = AbilityActionExecutor.Digest(Object("costReceiptPolicyVersion", NormalizedValue.CreateString(AbilityActionExecutor.CostReceiptPolicyVersion),
                    "callPolicyRef",NormalizedValue.CreateString(directPolicy?.PolicyRef ?? ""),"source", NormalizedValue.CreateString(source.SourceKey), "authorization", NormalizedValue.CreateString(source.AuthorizationRef),
                    "zone", NormalizedValue.CreateString(source.EffectiveZoneId), "face", NormalizedValue.CreateString(source.ActiveFaceId), "grants", Strings(source.GrantingSourceRefs), "window", NormalizedValue.CreateString(window), "binding", NormalizedValue.CreateString(binding),
                    "round", NormalizedValue.CreateInteger(state.Round), "generation", NormalizedValue.CreateInteger(generation), "reason", NormalizedValue.CreateString(reason), "resources", AbilityActionExecutor.CostValue(state.FindPlayer(playerId).Resources)));
                result.Add(new AbilityActionOption { OptionId = optionId, ActionId = action.ActionId, SourceRef = source, EntryKind = action.EntryKind,
                    AuthorizationRef = source.AuthorizationRef, TrackBindingId = binding, Enabled = reason.Length == 0, DisabledReason = reason,
                    PresentationRef = action.PresentationRef, SortOrder = action.SortOrder,
                    DisplayName = ActionText(action.Presentation?.Name, AbilityPresentationNames.TryGetValue(action.PresentationRef, out var name) ? name : action.PresentationRef),
                    Description = ActionText(action.Presentation?.Description, AbilityPresentationDescriptions.TryGetValue(action.PresentationRef, out var description) ? description : ""),
                    EntryCostDescription = ActionText(action.Presentation?.EntryCost), EntryAfterEffectsDescription = ActionText(action.Presentation?.EntryAfterEffects),
                    DisplayNameRef = action.Presentation?.Name?.Clone(), DescriptionRef = action.Presentation?.Description?.Clone(),
                    EntryCostDescriptionRef = action.Presentation?.EntryCost?.Clone(), EntryAfterEffectsDescriptionRef = action.Presentation?.EntryAfterEffects?.Clone(),
                    VersionRef = new AbilityVersionRef { ContentPackHash = ContentPackHash, DefinitionVersion = source.DeclarationVersion, HandlerHash = HandlerHash(action.BodyHandlerRef), ExecutorVersion = AbilityActionExecutor.Version },
                    RequestRef = new AbilityActionRequestRef { InteractionId = StableIdFactory.Create("action-request", optionId, window, digest), WindowId = window,
                        OwnerEffectId = window, AnsweringPlayerId = playerId, FactsDigest = digest, RequestGeneration = generation, AllowedAnswers = new List<string> { optionId } } });
            }
            return result.OrderBy(o => o.SortOrder).ThenBy(o => o.OptionId, StringComparer.Ordinal).ToList();
        }
    }
}
