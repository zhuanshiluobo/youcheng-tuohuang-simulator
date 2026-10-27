using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using YC.Domain.Influence;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.State
{
    [Serializable] public sealed class InfluencePlaceMapping
    {
        public int ProtocolVersion = 1;
        public string LogicalOperationId = "", ActualRootEffectId = "", ParentEffectId = "", SelectedLeafEffectId = "";
        public string ResultContract = "place_result", PermissionContract = "inherit_original";
        public EffectSpecRuntimeState OriginalSpec;
        public List<YC.Domain.Effects.AbilitySourceRef> Sources = new List<YC.Domain.Effects.AbilitySourceRef>();
        public List<string> HandlerHashes = new List<string>();
        public NormalizedValue ResultReceipt;
    }
    public sealed partial class EffectRuntimeState
    { public List<InfluencePlaceMapping> InfluencePlaceMappings = new List<InfluencePlaceMapping>(); }
}
namespace YC.Domain.Effects
{
    public sealed class InfluencePlaceRewrite
    {
        public string Operation = "keep", ResultContract = "", PermissionContract = "";
        public EffectSpec Effect;
    }
    public sealed partial class EffectRegistry
    {
        private readonly Dictionary<string, Func<GameState, AbilitySourceRef, NormalizedValue, InfluencePlaceRewrite>> influencePlaceHandlers = new Dictionary<string, Func<GameState, AbilitySourceRef, NormalizedValue, InfluencePlaceRewrite>>(StringComparer.Ordinal);
        public void RegisterInfluencePlaceHandler(string handlerRef, Func<GameState, AbilitySourceRef, NormalizedValue, InfluencePlaceRewrite> handler)
        { if (HasAbilityCatalog || handler == null || influencePlaceHandlers.ContainsKey(handlerRef)) throw new InvalidOperationException("influence_place_handler_duplicate"); influencePlaceHandlers.Add(handlerRef, handler); }
        private sealed class EventResponsePreview
        {
            internal RuleEvent Trigger;
            internal string HandlerId, HandlerHash;
        }
        private readonly ConditionalWeakTable<AbilityActionCallContext, EventResponsePreview> eventResponsePreviews =
            new ConditionalWeakTable<AbilityActionCallContext, EventResponsePreview>();
        public bool TryGetSourceFactsPreviewEvent(AbilityActionCallContext call, out RuleEvent trigger)
        {
            trigger = null;
            if (call == null || !eventResponsePreviews.TryGetValue(call, out var preview)) return false;
            trigger = preview.Trigger; return true;
        }
        private bool IsSourceFactsEventPreview(AbilityActionCallContext call, string handlerId, string handlerHash) =>
            eventResponsePreviews.TryGetValue(call, out var preview) && preview.HandlerId == handlerId && preview.HandlerHash == handlerHash &&
            call.State.EffectRuntime.RuleEvents.Any(e => ReferenceEquals(e, preview.Trigger)) &&
            call.State.EffectRuntime.EffectNodes.Any(n => ReferenceEquals(n, call.OwnerNode) && n.EffectId == preview.Trigger.OwnerNodeId);
        public bool HasEventResponseBody(EffectEventHandlerContext observer, EffectSpec spec, AbilitySourceRef source) => HasEventResponseBody(observer, spec, source, false);
        internal bool HasSettledEventResponseBody(EffectEventHandlerContext observer, EffectSpec spec, AbilitySourceRef source) => HasEventResponseBody(observer, spec, source, true);
        private bool HasEventResponseBody(EffectEventHandlerContext observer, EffectSpec spec, AbilitySourceRef source, bool settled)
        {
            var state = observer?.State; var registration = observer?.Registration;
            if (state?.EffectRuntime == null || source == null || spec == null || registration == null || observer.Event == null || observer.OwnerNode == null ||
                !ReferenceEquals(registration, FindEventHandler(registration.SubscriptionId)) || registration.Role != EffectHandlerRole.Observer ||
                !registration.Matches(observer.Event, observer.OwnerNode, false) ||
                !state.EffectRuntime.RuleEvents.Any(e => ReferenceEquals(e, observer.Event)) ||
                !state.EffectRuntime.EffectNodes.Any(n => ReferenceEquals(n, observer.OwnerNode) && n.EffectId == observer.Event.OwnerNodeId) ||
                (settled ? !HasRecordedSettledResponse(observer, source) :
                    state.EffectRuntime.DispatchReceipts.Any(r => r.EventId == observer.Event.EventId && r.SubscriptionId == registration.SubscriptionId)) ||
                spec.EffectTypeId != AbilitySourceEffectExecutor.TypeId || Text(spec.NormalizedArguments, "sourceKey") != source.SourceKey ||
                !IsCurrentSourceFactsSource(state, source) || Text(spec.NormalizedArguments, "triggerEventId") != observer.Event.EventId)
                throw new InvalidOperationException("response_preview_source_invalid");
            var declaration = SourceDeclaration(source);
            string handler = Text(spec.NormalizedArguments, "handlerRef");
            if (declaration.Kind != "event" || !declaration.ObservedEvents.Contains(observer.Event.EventType) ||
                handler != declaration.HandlerRef || registration.HandlerId != handler || registration.AbilityId != source.AbilityId ||
                registration.DefinitionVersion != declaration.DeclarationVersion || registration.ContentHash != HandlerHash(handler))
                throw new InvalidOperationException("response_preview_handler_invalid");
            if (spec.PlayerId != source.AuthorizedPlayerId || spec.NormalizedArguments.ToDeterministicString() !=
                AbilitySourceEffectExecutor.Create(source, observer.Event, handler, false, state.Round).NormalizedArguments.ToDeterministicString())
                throw new InvalidOperationException("response_preview_source_invalid");
            var view = GameStateCloneService.DeepClone(state);
            var trigger = view.EffectRuntime.RuleEvents.Single(e => e.EventId == observer.Event.EventId);
            var call = new AbilityActionCallContext { State = view, OwnerNode = view.EffectRuntime.EffectNodes.Single(n => n.EffectId == trigger.OwnerNodeId),
                SourceRef = GameStateCloneService.DeepClone(source), Arguments = Object() };
            string before = RuleStateDigestService.Compute(view);
            eventResponsePreviews.Add(call, new EventResponsePreview { Trigger = trigger, HandlerId = handler, HandlerHash = HandlerHash(handler) });
            try
            {
                // 只构造并丢弃返回式，不建立执行器、请求或收据。
                var body = ConstructAbilityBody(handler, call);
                if (RuleStateDigestService.Compute(view) != before) throw new InvalidOperationException("response_preview_mutated_state");
                return body.Count > 0;
            }
            finally { eventResponsePreviews.Remove(call); }
        }
        // 只允许真实已分发的可选响应/offer沿原事件做延后预检，不开放任意历史事件或来源预演。
        private bool HasRecordedSettledResponse(EffectEventHandlerContext observer, AbilitySourceRef source)
        {
            var runtime = observer.State.EffectRuntime;
            var receipt = runtime.DispatchReceipts.SingleOrDefault(r => r.EventId == observer.Event.EventId && r.SubscriptionId == observer.Registration.SubscriptionId && r.Succeeded);
            if (receipt == null || !InfluenceDepartureProtocol.DestinationsSettled(observer.State, new[] { observer.Event.EventId })) return false;
            return runtime.InfluenceDepartureBatches.Any(b => b.Sealed && b.Offers.Any(o => o.SourceKey == source.SourceKey &&
                o.SubscriptionId == receipt.SubscriptionId && o.EventIds.Contains(observer.Event.EventId) && Text(o.Body?.NormalizedArguments, "sourceKey") == source.SourceKey)) ||
                runtime.DeferredResponses.Any(response => response.ProtocolVersion == 1 && response.Kind == "optional_effect" && response.Status == DeferredResponseStatus.Queued &&
                    response.SourceKey == source.SourceKey && response.SourceEventId == observer.Event.EventId && receipt.DeferredResponseIds.Contains(response.DeferredResponseId));
        }
        internal InfluencePlaceRewrite RewriteInfluencePlace(GameState state, AbilitySourceRef source, NormalizedValue facts)
        { if (!influencePlaceHandlers.TryGetValue(AbilityDeclarations.Single(d => d.AbilityId == source.AbilityId).HandlerRef, out var h)) throw new InvalidOperationException("influence_place_handler_missing"); return h(GameStateCloneService.DeepClone(state), GameStateCloneService.DeepClone(source), facts.Clone()); }
    }
    public static class InfluencePlaceAttachment
    {
        public const string Version = "1.0.0";
        public static EffectNodeRuntimeState ResultNode(GameState state, string rootId)
        {
            var map = state.EffectRuntime.InfluencePlaceMappings.SingleOrDefault(m => m.ActualRootEffectId == rootId);
            if (map == null) return state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == rootId && n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence);
            var leaves = state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence && InfluenceDepartureProtocol.IsWithin(state, n.EffectId, rootId)).ToList();
            if (leaves.Count > 1) throw new InvalidOperationException("logical_place_multiple_leaves");
            return leaves.SingleOrDefault();
        }
        public static NormalizedValue LogicalProjection(GameState state, string rootId)
        {
            var m = state.EffectRuntime.InfluencePlaceMappings.SingleOrDefault(v => v.ActualRootEffectId == rootId);
            return m == null ? NormalizedValue.CreateNull() : Object("protocolVersion", NormalizedValue.CreateInteger(1), "logicalOperationId", NormalizedValue.CreateString(m.LogicalOperationId),
                "effectTypeId", NormalizedValue.CreateString(m.OriginalSpec.EffectTypeId), "arguments", m.OriginalSpec.NormalizedArguments.Clone(), "playerId", NormalizedValue.CreateInteger(m.OriginalSpec.PlayerId),
                "actualRootEffectId", NormalizedValue.CreateString(m.ActualRootEffectId), "selectedLeafEffectId", NormalizedValue.CreateString(m.SelectedLeafEffectId));
        }
        public static bool Validate(GameState state, EffectRegistry registry, out string reason)
        {
            reason = "influence_place_mapping_invalid";
            if (state.EffectRuntime.InfluencePlaceMappings == null) return false;
            var ids = new HashSet<string>();
            foreach (var m in state.EffectRuntime.InfluencePlaceMappings)
            {
                if (m == null || m.ProtocolVersion != 1 || m.LogicalOperationId != m.ActualRootEffectId || !ids.Add(m.LogicalOperationId) || m.OriginalSpec?.EffectTypeId != InfluenceEffectTypeIds.PlaceInfluence ||
                    m.Sources == null || m.HandlerHashes == null || m.Sources.Count == 0 || m.Sources.Count != m.HandlerHashes.Count || m.ResultContract != "place_result" || m.PermissionContract != "inherit_original") return false;
                var root = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == m.ActualRootEffectId && n.ParentEffectId == m.ParentEffectId && n.PlayerId == m.OriginalSpec.PlayerId);
                if (root == null || root.SourceId != m.OriginalSpec.SourceId || m.Sources.Select(s=>s?.SourceKey).Distinct().Count()!=m.Sources.Count) return false;
                try
                {
                    var original=EffectRegistry.FromRuntimeSpec(m.OriginalSpec);
                    if(registry!=null) { if(!registry.TryGet(original.EffectTypeId,out var definition)) return false; definition.Validate(original); }
                    EffectSpec FromNode(EffectNodeRuntimeState n) => EffectRegistry.FromRuntimeSpec(new EffectSpecRuntimeState { EffectTypeId=n.EffectTypeId, NormalizedArguments=n.NormalizedArguments,
                        PlayerId=n.PlayerId, AllowDecline=n.AllowDecline, DecisionPlayerId=n.DecisionPlayerId, NestedEffects=n.NestedEffects, NestedGroupSizes=n.NestedGroupSizes });
                    EffectTreeExecutor.ValidatePlaceReplacement(original,FromNode(root));
                    foreach(var leaf in state.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence && InfluenceDepartureProtocol.IsWithin(state,n.EffectId,root.EffectId)))
                        EffectTreeExecutor.ValidatePlaceReplacement(original,FromNode(leaf));
                }
                catch(InvalidOperationException) { return false; }
                catch(ArgumentException) { return false; }
                for (int i = 0; i < m.Sources.Count; i++)
                {
                    var source=m.Sources[i];
                    if(source==null || source.AuthorizedPlayerId!=m.OriginalSpec.PlayerId || source.RuleOwnerItemId!=source.ItemId ||
                        source.SourceKey!=StableIdFactory.Create("ability-source",source.ItemId,source.AbilityId,source.ActiveFaceId,source.DeclarationVersion,source.AuthorizedPlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
                        !state.ItemZones.Items.Any(item=>item.ItemId==source.ItemId && item.DefinitionId==source.DefinitionId) || m.HandlerHashes[i].Length==0) return false;
                    if(registry!=null)
                    {
                        var declaration=registry.AbilityDeclarations.SingleOrDefault(d=>d.AbilityId==source.AbilityId && d.DefinitionId==source.DefinitionId);
                        if(declaration==null || declaration.DeclarationVersion!=source.DeclarationVersion || source.ActiveFaceId!=declaration.ActiveFaceId || !declaration.QueryKinds.Contains("influence_place_attaching.v1") ||
                            registry.HandlerHash(declaration.HandlerRef)!=m.HandlerHashes[i]) return false;
                    }
                }
                if (m.SelectedLeafEffectId.Length > 0)
                { var leaf = ResultNode(state, root.EffectId); if (leaf == null || leaf.EffectId != m.SelectedLeafEffectId || m.ResultReceipt == null || leaf.NormalizedResult.ToDeterministicString() != m.ResultReceipt.ToDeterministicString()) return false; }
            }
            reason = ""; return true;
        }
    }
    public partial class EffectTreeExecutor
    {
        private EffectSpec PrepareInfluenceAttachment(GameState work, EffectSpec original, string id, string parent)
        {
            if (original.EffectTypeId != InfluenceEffectTypeIds.PlaceInfluence) return original;
            var processed = new HashSet<string>();
            string ancestor = parent;
            while (!string.IsNullOrEmpty(ancestor))
            {
                var m = work.EffectRuntime.InfluencePlaceMappings.SingleOrDefault(v => v.ActualRootEffectId == ancestor);
                if (m != null) foreach (var source in m.Sources) processed.Add(source.SourceKey);
                var node = work.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == ancestor);
                if (node == null || node.EffectTypeId != LuaDomainEffectTypeIds.Choice) break;
                ancestor = node.ParentEffectId;
            }
            var facts = Object("logicalOperationId", NormalizedValue.CreateString(id), "parentEffectId", NormalizedValue.CreateString(parent), "playerId", NormalizedValue.CreateInteger(original.PlayerId),
                "sourceId", NormalizedValue.CreateString(original.SourceId), "arguments", PlacementInvariantArguments(original.NormalizedArguments), "processedSourceKeys", Strings(processed.OrderBy(v => v, StringComparer.Ordinal)));
            var originalSource = parent.Length == 0 ? null : CardMarkerCarrier.Source(work, work.EffectRuntime.EffectNodes.Single(n => n.EffectId == parent), registry.AbilityDeclarations, registry);
            InfluenceDestinationResolver.Set(facts, "originalSourceRef", originalSource == null ? NormalizedValue.CreateNull() : Object("sourceKey", NormalizedValue.CreateString(originalSource.SourceKey), "itemId", NormalizedValue.CreateString(originalSource.ItemId), "abilityId", NormalizedValue.CreateString(originalSource.AbilityId), "declarationVersion", NormalizedValue.CreateString(originalSource.DeclarationVersion)));
            EffectSpec replacement = null; string signature = "";
            var mapping = new InfluencePlaceMapping { LogicalOperationId = id, ActualRootEffectId = id, ParentEffectId = parent, OriginalSpec = original.ToRuntimeState() };
            foreach (var source in registry.QuerySources(work, original.PlayerId, "ruleQuery").Where(s => registry.AbilityDeclarations.Single(d => d.AbilityId == s.AbilityId).QueryKinds.Contains("influence_place_attaching.v1") && !processed.Contains(s.SourceKey)))
            {
                // 每个来源接收同一草稿的副本。回调只构造替代模板，不取得执行上下文。
                var result = registry.RewriteInfluencePlace(work, source, facts.Clone());
                if (result == null || !new[] { "keep", "replace" }.Contains(result.Operation)) throw new InvalidOperationException("influence_place_rewrite_invalid");
                if (result.Operation == "keep") { if (result.Effect != null || result.ResultContract.Length > 0 || result.PermissionContract.Length > 0) throw new InvalidOperationException("influence_place_keep_fields_invalid"); continue; }
                if (result.Effect == null || result.ResultContract != "place_result" || result.PermissionContract != "inherit_original") throw new InvalidOperationException("influence_place_contract_invalid");
                ValidatePlaceReplacement(original, result.Effect);
                string digest = PlaceSemanticSignature(result.Effect);
                if (replacement != null && digest != signature) throw new InvalidOperationException("influence_place_rewrite_conflict");
                replacement = result.Effect; signature = digest; mapping.Sources.Add(GameStateCloneService.DeepClone(source));
                mapping.HandlerHashes.Add(registry.HandlerHash(registry.AbilityDeclarations.Single(d => d.AbilityId == source.AbilityId).HandlerRef));
            }
            if (replacement == null) return original;
            // 原完成续接只保留在替代根；叶节点始终发布真实Placed。
            replacement = GameStateCloneService.DeepClone(replacement);
            ClearReplacementContinuations(replacement);
            replacement.SourceId = original.SourceId; replacement.StableKey = original.StableKey; replacement.PlayerId = original.PlayerId;
            replacement.PersistentSourceFacts = GameStateCloneService.DeepClone(original.PersistentSourceFacts);
            replacement.CompletionHandlerId = original.CompletionHandlerId; replacement.ContinuationContext = original.ContinuationContext?.Clone();
            replacement.ContinuationAbilityId = original.ContinuationAbilityId; replacement.ContinuationContentId = original.ContinuationContentId;
            replacement.ContinuationContentInstanceId = original.ContinuationContentInstanceId; replacement.ContinuationContentHash = original.ContinuationContentHash;
            replacement.ContinuationDefinitionVersion = original.ContinuationDefinitionVersion; replacement.Settlement = GameStateCloneService.DeepClone(original.Settlement);
            ValidateSpec(replacement, 0, 0); work.EffectRuntime.InfluencePlaceMappings.Add(mapping);
            return replacement;
        }
        private static NormalizedValue PlacementInvariantArguments(NormalizedValue value)
        {
            var result = value.Clone();
            void Subject(NormalizedValue subject)
            {
                if (subject?.Kind != NormalizedValueKind.Object) return;
                if (Text(subject, "subjectType") == "player") subject.Properties.RemoveAll(p => p.Name == "definitionId" && p.Value.StringValue == "");
            }
            Subject(Value(result, "ownerSubject"));
            var source = Value(result, "influenceSource");
            if (source?.Kind == NormalizedValueKind.Object)
            {
                Subject(Value(source, "subject"));
                if (Text(source, "sourceId").Length == 0) InfluenceDestinationResolver.Set(source, "sourceId", NormalizedValue.CreateString(Text(source, "kind")));
            }
            return result;
        }
        private static void ClearReplacementContinuations(EffectSpec spec)
        {
            spec.CompletionHandlerId = ""; spec.ContinuationContext = null; spec.ContinuationAbilityId = ""; spec.ContinuationContentHash = "";
            foreach (var child in spec.Children.Concat(spec.NestedEffectGroups.SelectMany(g => g))) ClearReplacementContinuations(child);
        }
        private static string PlaceSemanticSignature(EffectSpec spec) => spec.EffectTypeId + ":" + spec.PlayerId + ":" + spec.AllowDecline + ":" + spec.DecisionPlayerId + ":" + (spec.Presentation == null ? "" : YC.Domain.Presentation.GameplayPresentationNormalizer.ToNormalized(spec.Presentation).ToDeterministicString()) + ":" + spec.NormalizedArguments.ToDeterministicString() + "[" + string.Join(",", spec.NestedEffectGroups.Select(g => string.Join(";", g.Select(PlaceSemanticSignature)))) + "]";
        internal static void ValidatePlaceReplacement(EffectSpec original, EffectSpec replacement)
        {
            if (replacement.PlayerId != original.PlayerId || replacement.Children.Count > 0) throw new InvalidOperationException("influence_place_rewrite_permission_denied");
            if (replacement.EffectTypeId == LuaDomainEffectTypeIds.Choice)
            {
                if (replacement.AllowDecline || replacement.NestedEffectGroups.Count < 1 || replacement.NestedEffectGroups.Any(g => g.Count != 1) ||
                    Text(replacement.NormalizedArguments, "interactionType") != "effect.choice.execute" || replacement.NormalizedArguments.Properties.Any(p => !new[] { "options", "promptKey", "interactionType", "player", "minSelections", "maxSelections" }.Contains(p.Name)) ||
                    Number(replacement.NormalizedArguments, "minSelections", 1) != 1 || Number(replacement.NormalizedArguments, "maxSelections", 1) != 1) throw new InvalidOperationException("influence_place_choice_invalid");
                foreach (var child in replacement.NestedEffectGroups.SelectMany(g => g)) ValidatePlaceReplacement(original, child);
                return;
            }
            if (replacement.EffectTypeId != InfluenceEffectTypeIds.PlaceInfluence || replacement.NestedEffectGroups.Count > 0 || replacement.AllowDecline != original.AllowDecline) throw new InvalidOperationException("influence_place_leaf_required");
            var args = replacement.NormalizedArguments.Clone(); var prior = original.NormalizedArguments.Clone();
            string originalType = Text(prior, "itemTypeId"), newType = Text(args, "itemTypeId");
            if (newType.Length == 0 || originalType.Length > 0 && originalType != newType) throw new InvalidOperationException("influence_place_type_required");
            args.Properties.RemoveAll(p => p.Name == "itemTypeId"); prior.Properties.RemoveAll(p => p.Name == "itemTypeId");
            if (PlacementInvariantArguments(args).ToDeterministicString() != PlacementInvariantArguments(prior).ToDeterministicString()) throw new InvalidOperationException("influence_place_original_parameters_changed");
        }
        private static void ProjectLogicalPlaceResult(GameState work, EffectNodeRuntimeState node)
        {
            var m = work.EffectRuntime.InfluencePlaceMappings.SingleOrDefault(v => v.ActualRootEffectId == node.EffectId);
            if (m == null || node.PendingOutcome == EffectPendingOutcome.None) return;
            var leaf = InfluencePlaceAttachment.ResultNode(work, node.EffectId);
            if (leaf == null) { if (node.PendingOutcome == EffectPendingOutcome.Completed) throw new InvalidOperationException("logical_place_result_missing"); return; }
            if (!IsTerminal(leaf.Status) && leaf.EffectId != node.EffectId) return;
            m.SelectedLeafEffectId = leaf.EffectId; m.ResultReceipt = leaf.NormalizedResult.Clone(); node.NormalizedResult = m.ResultReceipt.Clone();
            if (leaf.Status == EffectNodeStatus.Failed) { node.PendingOutcome = EffectPendingOutcome.Failed; node.FailureReason = leaf.FailureReason; }
        }
    }
}
