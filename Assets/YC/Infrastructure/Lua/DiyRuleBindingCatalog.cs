using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    /// <summary>把已选模式/扩展绑定到现有效果事件或只读候选策略。</summary>
    public static class DiyRuleBindingCatalog
    {
        public const string Version = "diy.rule_bindings.v7";
        internal static bool SupportsPatchEvent(string eventType) => eventType == YC.Domain.Cards.OperatorDispatchFlow.StartingEvent;
        internal static bool SupportsEffectEvent(string eventType)
        {
            switch (eventType)
            {
                case "PlayerCleanupStarted": case "RoundStarted": case "RoundEnded":
                case "CharacterCardCovered": case "CharacterCoverCompleted": case "CharacterUseFinished":
                case "CollectionStarted": case "CityMoveCompleted": case "FacilityPlaced": case "FinalScoringStarted":
                    return true;
                default: return false;
            }
        }
        private static readonly ConditionalWeakTable<EffectRegistry, Dictionary<string, string>> Bindings =
            new ConditionalWeakTable<EffectRegistry, Dictionary<string, string>>();
        public static void Register(EffectRegistry registry, ExternalContentPack pack)
        {
            if (registry == null || pack == null) throw new ArgumentNullException();
            if (pack.DiyComposition == null) return;
            var records = pack.Manifest["diyRuleBindings"] as JArray ?? throw new InvalidDataException("diy_rule_bindings_missing");
            var dispatcher = new LuaEffectEventDispatcher(registry, compiler: new LuaEffectSpecCompiler(presentationPack: pack));
            foreach (var token in records)
            {
                if (!(token is JObject item)) throw new InvalidDataException("diy_rule_binding_invalid");
                string owner = (string)item["packId"], scope = (string)item["scopeId"], id = (string)item["bindingId"], eventType = (string)item["eventType"];
                string path = (string)item["script"], version = (string)item["version"];
                if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(id) ||
                    (!SupportsEffectEvent(eventType) && !SupportsPatchEvent(eventType) && eventType != "CandidateSetBuilding" && eventType != "EffectCompleted") ||
                    string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("diy_rule_binding_invalid");
                string subscription = StableIdFactory.Create("diy-rule-binding", owner, scope, id);
                string source = pack.GetScript(path);
                var body = new LuaScriptDefinition(scope, id, id, version, source, LuaContentHasher.ComputeSha256(source), pack.GetScriptEntryId(path));
                if (SupportsPatchEvent(eventType))
                {
                    if (item["completion"] != null || item["candidateKind"] != null || item["completedEffectTypes"] != null || item["completionPolicy"] != null)
                        throw new InvalidDataException("diy_rule_binding_dispatch_patch_invalid");
                    dispatcher.Register(new LuaHandlerDefinition(subscription, eventType, "", body, contentInstanceId: scope, abilityId: id,
                        responseKind: "operatorDispatchPatches"));
                    continue;
                }
                if (eventType == "CandidateSetBuilding")
                {
                    string kind = (string)item["candidateKind"];
                    if (item["completion"] != null)
                        throw new InvalidDataException("diy_rule_binding_candidate_invalid");
                    var policies = kind == YC.Domain.Movement.CityDestinationQueryService.CandidateKind ? registry.CityDestinationCandidatePolicies :
                        kind == YC.Domain.Facilities.FacilityBuildCandidateQuery.CandidateKind ? registry.FacilityBuildCandidates.GameplayCandidatePolicies :
                        throw new InvalidDataException("diy_rule_binding_candidate_invalid");
                    string candidateSignature = StableIdFactory.Create("diy-rule-binding-candidate-signature", kind, body.EntryId,
                        body.DefinitionVersion, body.ContentHash);
                    var registeredCandidates = Bindings.GetOrCreateValue(registry);
                    lock (registeredCandidates)
                    {
                        var existing = policies.FindRegistration(subscription);
                        if (registeredCandidates.TryGetValue(subscription, out string prior))
                        {
                            if (prior != candidateSignature || existing == null || existing.CandidateKind != kind ||
                                existing.SourceVersionIdentity != LuaEffectEventDispatcher.CandidateSourceIdentity(body))
                                throw new InvalidDataException("diy_rule_binding_candidate_conflict:" + subscription);
                            continue;
                        }
                        if (existing != null || registry.HasEventHandler(subscription))
                            throw new InvalidDataException("diy_rule_binding_candidate_conflict:" + subscription);
                        dispatcher.RegisterCandidatePolicy(policies, new LuaHandlerDefinition(subscription, eventType, "", body,
                            contentInstanceId: scope, abilityId: id, responseKind: "candidatePatches"), kind);
                        registeredCandidates.Add(subscription, candidateSignature);
                    }
                    continue;
                }
                if (item["candidateKind"] != null) throw new InvalidDataException("diy_rule_binding_invalid");
                var completedTypes = eventType == "EffectCompleted" && item["completedEffectTypes"] is JArray completed
                    ? completed.Values<string>().OrderBy(value => value, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
                string completionPolicy = eventType == "EffectCompleted" ? (string)item["completionPolicy"] : "";
                if (eventType == "EffectCompleted" && (!DiyRuleBinding.ValidCompletedTypes(completedTypes) ||
                    completionPolicy != "succeeded" && completionPolicy != "terminal") ||
                    eventType != "EffectCompleted" && (item["completedEffectTypes"] != null || item["completionPolicy"] != null))
                    throw new InvalidDataException("diy_rule_binding_completion_filter_invalid");
                LuaScriptDefinition completion = null; string completionId = "";
                if (item["completion"] is JObject end)
                {
                    completion = CreateCompletionDefinition(pack, item);
                    completionId = completion.HandlerId;
                }
                string signature = StableIdFactory.Create("diy-rule-binding-signature", eventType, body.EntryId, body.DefinitionVersion,
                    body.ContentHash, completionId, completion?.EntryId ?? "", completion?.DefinitionVersion ?? "", completion?.ContentHash ?? "",
                    string.Join("\u001f", completedTypes), completionPolicy ?? "");
                var observerIds = completedTypes.Select(type => StableIdFactory.Create("diy-rule-completion-observer", subscription, type)).ToArray();
                var registered = Bindings.GetOrCreateValue(registry);
                lock (registered)
                {
                    if (registered.TryGetValue(subscription, out string existing))
                    {
                        if (existing != signature || (eventType == "EffectCompleted"
                            ? observerIds.Any(observerId => !registry.HasEventHandler(observerId))
                            : !registry.HasEventHandler(subscription)))
                            throw new InvalidDataException("diy_rule_binding_effect_conflict:" + subscription);
                        continue;
                    }
                    if (registry.HasEventHandler(subscription) || observerIds.Any(registry.HasEventHandler))
                        throw new InvalidDataException("diy_rule_binding_effect_conflict:" + subscription);
                    if (completion != null)
                    {
                        string continuation = StableIdFactory.Create("diy-rule-binding-continuation", owner, scope, id, completionId);
                        if (registry.HasEventHandler(continuation)) throw new InvalidDataException("diy_rule_binding_effect_conflict:" + continuation);
                        dispatcher.RegisterContinuation(new LuaHandlerDefinition(continuation, "EffectCompleted", "", completion,
                            EffectHandlerRole.Continuation, contentInstanceId: scope, abilityId: id));
                    }
                    if (eventType == "EffectCompleted")
                    {
                        var filter = new AbilitySourceDeclaration { CompletedEffectTypes = new List<string>(completedTypes), CompletionPolicy = completionPolicy };
                        for (int index = 0; index < completedTypes.Length; index++)
                            dispatcher.RegisterCompletionObserver(new LuaHandlerDefinition(observerIds[index], eventType, "", body,
                                EffectHandlerRole.Observer, contentInstanceId: scope, abilityId: id,
                                completionHandlerId: completionId, continuationTarget: completion), completedTypes[index], filter);
                    }
                    else dispatcher.Register(new LuaHandlerDefinition(subscription, eventType, "", body, EffectHandlerRole.Observer,
                        contentInstanceId: scope, abilityId: id, completionHandlerId: completionId, continuationTarget: completion));
                    registered.Add(subscription, signature);
                }
            }
        }

        internal static LuaScriptDefinition CreateCompletionDefinition(ExternalContentPack pack, JObject item)
        {
            if (!(item?["completion"] is JObject end)) throw new InvalidDataException("diy_rule_binding_completion_invalid");
            string scope = (string)item["scopeId"], id = (string)item["bindingId"], handler = (string)end["handlerId"];
            string path = (string)end["script"], version = (string)end["version"];
            if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(handler) ||
                string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("diy_rule_binding_completion_invalid");
            string source = pack.GetScript(path);
            return new LuaScriptDefinition(scope, id, handler, version, source, LuaContentHasher.ComputeSha256(source), pack.GetScriptEntryId(path));
        }
    }
}
