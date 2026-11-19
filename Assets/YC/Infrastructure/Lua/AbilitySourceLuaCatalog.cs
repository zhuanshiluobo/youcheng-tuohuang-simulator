using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    /// <summary>所有物理类型共享来源目录；这里只装配冻结处理器，不执行收益。</summary>
    public static class AbilitySourceLuaCatalog
    {
        // 完成处理器按完整来源和版本身份登记；变更时进入对局内容指纹。
        public const string ContinuationBindingVersion = "2.0.0";
        public static void ValidateCarrierPermissions(IEnumerable<ExternalContentDefinition> definitions)
        {
            var all = definitions.ToList(); var types = ItemTypeRegistry.CreateBuiltIn();
            foreach (var content in all)
            foreach (var source in content.AbilitySources.Where(s => s.AllowedOperations.Any(p => p.StartsWith(AbilitySourceService.SubscriptionLinkPrefix, StringComparison.Ordinal))))
                if (source.AuthorizationPolicy != "character_subscription" || source.Kind != "ruleQuery" ||
                    !content.Abilities.Any(a => a.AbilityId == AbilitySourceService.SubscriptionAbility(source)))
                    throw new InvalidDataException("character_subscription_link_invalid:" + source.AbilityId);
            foreach (var source in all.SelectMany(d => d.AbilitySources))
            foreach (string permission in source.AllowedOperations.Where(p => p.StartsWith("reward.grant", StringComparison.Ordinal)))
            {
                if (!RewardGrantEffectExecutor.ValidPermission(permission)) throw new InvalidDataException("reward_grant_permission_invalid:" + source.AbilityId);
                var p = permission.Split(':'); var target = all.SingleOrDefault(d => d.DefinitionId == p[1]);
                if (target?.ContentType != "award" || target.ItemTypeId != p[2] || (string)target.Data["acquisitionPolicy"] != "content_granted")
                    throw new InvalidDataException("reward_grant_permission_target_invalid:" + source.AbilityId);
            }
            foreach (var source in all.SelectMany(d => d.AbilitySources))
            foreach (string permission in source.AllowedOperations.Where(p => p.StartsWith("carrier.", StringComparison.Ordinal)))
            {
                if (!CardMarkerCarrier.ValidPermission(permission)) throw new InvalidDataException("carrier_permission_declaration_invalid:" + source.AbilityId);
                var p = permission.Split(':'); var target = all.SingleOrDefault(d => d.DefinitionId == p[1]);
                var zone = target?.CardZones.SingleOrDefault(z => z.LocalZoneId == p[2]);
                if (zone?.ZoneTemplateId != "core.zone.card_cube_storage" || !zone.AcceptedTypeIds.Any(t => types.IsAssignableTo(t, p[3]) || types.IsAssignableTo(p[3], t)))
                    throw new InvalidDataException("carrier_permission_target_invalid:" + source.AbilityId);
            }
        }
        private static readonly string[] Events = { "PlayerCleanupStarted", "RoundStarted", "RoundEnded", "CharacterCardCovered", "CharacterCoverCompleted", "CharacterEffectCompleted", "CharacterUseFinished", "EffectCompleted",
            "CollectionStarted", "OperatorDispatchStarting", "CityMoveCompleted", "InfluenceRemoved", "InfluenceReplaced", "FacilityPlaced", "FinalScoringStarted", "CandidateSetBuilding" };
        private static readonly string[] Queries = { "scoringModifiers.v1", "collection_free_locations.v1", "city_destination.v1", "built_facilities.v1", "payment_substitution.v1", "influence_place_attaching.v1",
            ResourcePaymentSourceService.QueryKind, ResourcePaymentCostPreview.DemandQueryKind,
            YC.Domain.Facilities.FacilityBuildQueryService.SlotKind, YC.Domain.Facilities.FacilityBuildQueryService.SourceKind };
        public static void ValidateRaw(Newtonsoft.Json.Linq.JObject json)
        {
            void Check(Newtonsoft.Json.Linq.JToken value, Type type)
            {
                var fields = new HashSet<string>(type.GetFields().Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                if (value is Newtonsoft.Json.Linq.JObject fieldsObject && fieldsObject.Properties().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fieldsObject.Properties().Count()) throw new InvalidDataException("ability_declaration_duplicate_field:" + type.Name);
                if (value is not Newtonsoft.Json.Linq.JObject obj || obj.Properties().Any(p => !fields.Contains(p.Name))) throw new InvalidDataException("ability_declaration_unknown_field:" + type.Name);
                string[] required = type == typeof(AbilitySourceDeclaration) ? new[] { "ProtocolVersion", "AbilityId", "Kind", "HandlerRef", "DeclarationVersion", "AuthorizationPolicy", "EffectiveZones", "PresentationRef" } :
                    type == typeof(AbilityActionDeclaration) ? new[] { "ActionId", "EntryKind", "BodyHandlerRef", "PresentationRef" } :
                    new[] { "HandlerId", "Script", "Version" };
                if (required.Any(name => obj.GetValue(name, StringComparison.OrdinalIgnoreCase) == null || obj.GetValue(name, StringComparison.OrdinalIgnoreCase).Type == Newtonsoft.Json.Linq.JTokenType.Null))
                    throw new InvalidDataException("ability_declaration_required_field:" + type.Name);
            }
            foreach (var pair in new[] { new { Name = "abilitySources", Type = typeof(AbilitySourceDeclaration) }, new { Name = "abilityHandlers", Type = typeof(ExternalAbilityHandler) } })
            {
                if (json[pair.Name] == null) continue;
                if (json[pair.Name] is not Newtonsoft.Json.Linq.JArray array) throw new InvalidDataException("ability_declaration_array_required:" + pair.Name);
                foreach (var value in array)
                {
                    Check(value, pair.Type);
                    if (pair.Name == "abilitySources" && ((Newtonsoft.Json.Linq.JObject)value).GetValue("repeatPolicy", StringComparison.OrdinalIgnoreCase) is Newtonsoft.Json.Linq.JToken repeat &&
                        (repeat.Type != Newtonsoft.Json.Linq.JTokenType.String || (string)repeat != "once" && (string)repeat != "unbounded"))
                        throw new InvalidDataException("cleanup_repeat_policy_invalid");
                    if (pair.Name == "abilitySources") foreach (var action in value["actions"] ?? value["Actions"] ?? new Newtonsoft.Json.Linq.JArray()) Check(action, typeof(AbilityActionDeclaration));
                    if (pair.Name == "abilitySources" && ((Newtonsoft.Json.Linq.JObject)value).GetValue("completedEffectTypes", StringComparison.OrdinalIgnoreCase) is Newtonsoft.Json.Linq.JToken completed &&
                        (completed is not Newtonsoft.Json.Linq.JArray types || types.Any(t => t.Type != Newtonsoft.Json.Linq.JTokenType.String || string.IsNullOrWhiteSpace((string)t))))
                        throw new InvalidDataException("ability_completion_filter_invalid");
                }
            }
        }
        public static void ValidateAndLoad(ExternalContentPack pack, ExternalContentDefinition content)
        {
            if (content.AbilitySources == null || content.AbilityHandlers == null) throw new InvalidDataException("ability_source_list_missing:" + content.DefinitionId);
            var zones = ZoneTemplateRegistry.CreateBuiltIn(ItemTypeRegistry.CreateBuiltIn());
            foreach (var source in content.AbilitySources)
            {
                foreach(string permission in source.AllowedOperations.Where(p=>p.StartsWith("cleanup.defer",StringComparison.Ordinal)))
                    if(!permission.StartsWith("cleanup.defer:",StringComparison.Ordinal) || !content.AbilityHandlers.Any(h=>h.HandlerId==permission.Substring("cleanup.defer:".Length))) throw new InvalidDataException("cleanup_obligation_handler_not_owned:"+source.AbilityId);
                if (source.DefinitionId.Length > 0 && source.DefinitionId != content.DefinitionId) throw new InvalidDataException("ability_source_definition_mismatch");
                source.DefinitionId = content.DefinitionId;
                if (source.ProtocolVersion != 1 || string.IsNullOrEmpty(source.AbilityId) || string.IsNullOrEmpty(source.DeclarationVersion) ||
                    source.EffectiveZones == null || source.EffectiveZones.Count == 0 || source.EffectiveZones.Any(z => zones.Find(z) == null) ||
                    source.ObservedEvents == null || source.ObservedEvents.Any(e => !Events.Contains(e)) || source.QueryKinds == null || source.QueryKinds.Any(q => !Queries.Contains(q)) ||
                    source.Actions == null || string.IsNullOrEmpty(source.PresentationRef) || !PresentationExists(content, source.PresentationRef)) throw new InvalidDataException("ability_source_declaration_invalid:" + source.AbilityId);
                if(!new[]{"always","when_available"}.Contains(source.CleanupInjectionPolicy) || source.CleanupInjectionPolicy=="when_available" && (source.Kind!="cleanup" || source.RepeatPolicy!="once")) throw new InvalidDataException("cleanup_injection_policy_invalid:"+source.AbilityId);
                if (source.CompletedEffectTypes == null || source.CompletedEffectTypes.Distinct(StringComparer.Ordinal).Count() != source.CompletedEffectTypes.Count ||
                    source.CompletionPolicy != "succeeded" && source.CompletionPolicy != "terminal" ||
                    source.ObservedEvents.Contains("EffectCompleted") != (source.CompletedEffectTypes.Count > 0) ||
                    source.CompletedEffectTypes.Count > 0 && source.Kind != "event" || source.CompletedEffectTypes.Any(t =>
                        string.IsNullOrWhiteSpace(t) || t.Length > 256 || t == AbilitySourceEffectExecutor.TypeId || t.Any(c => !char.IsLetterOrDigit(c) && c != '.' && c != '_' && c != '-')))
                    throw new InvalidDataException("ability_completion_filter_invalid:" + source.AbilityId);
                foreach (var action in source.Actions)
                {
                    if (string.IsNullOrEmpty(action.PresentationRef) || !PresentationExists(content, action.PresentationRef)) throw new InvalidDataException("ability_action_presentation_missing:" + action.ActionId);
                    pack.Presentation.ValidateReferences(action.Presentation);
                }
            }
            var handlers = new HashSet<string>();
            foreach (var handler in content.AbilityHandlers)
            {
                if (handler == null || string.IsNullOrEmpty(handler.HandlerId) || !handlers.Add(handler.HandlerId) || string.IsNullOrEmpty(handler.Version) || string.IsNullOrEmpty(handler.Script)) throw new InvalidDataException("ability_handler_invalid:" + content.DefinitionId);
                pack.LoadScript(handler.Script);
            }
        }
        private static bool PresentationExists(ExternalContentDefinition content, string reference) => reference == content.DefinitionId ||
            content.Abilities.Any(a => a.AbilityId == reference) || content.Data["playerDetails"]?.Any(p => (string)p["abilityId"] == reference) == true;
        private static void RegisterPresentation(EffectRegistry registry, ExternalContentDefinition content,
            YC.Domain.Presentation.IGameplayTextCatalog catalog, Func<YC.Domain.Presentation.GameplayTextRef, string> resolve)
        {
            registry.AbilityPresentationNames[content.DefinitionId] = content.DisplayName;
            // 定义级仅保留明确的旧特殊行动声明，不能借任意效果首项充当其它来源的正文。
            registry.AbilityPresentationDescriptions[content.DefinitionId] =
                (string)(content.Data["specialAction"] as Newtonsoft.Json.Linq.JObject)?["description"] ?? "";
            foreach (var ability in content.Abilities) registry.AbilityPresentationNames[ability.AbilityId] = content.DisplayName;
            foreach (var group in PlayerDetailDisplayCatalog.Read(new[] { content }, catalog)
                .Where(detail => !string.IsNullOrEmpty(detail.AbilityId)).GroupBy(detail => detail.AbilityId, StringComparer.Ordinal))
            {
                var names = group.Select(detail => detail.DisplayName).Distinct(StringComparer.Ordinal).ToArray();
                registry.AbilityPresentationNames[group.Key] = names.Length == 1 ? names[0] : content.DisplayName;
                var descriptions = group.Select(detail => detail.EffectTextRef != null ? resolve(detail.EffectTextRef) : detail.EffectText)
                    .Distinct(StringComparer.Ordinal).ToArray();
                // 该旧字符串合同没有激活时机维度；不同段落须由各自显式表现引用消费。
                if (descriptions.Length == 1) registry.AbilityPresentationDescriptions[group.Key] = descriptions[0];
            }
        }
        public static List<AbilitySourceDeclaration> CreateDeclarations(ExternalContentPack pack) => pack.ActiveDefinitions.SelectMany(d => d.AbilitySources).Select(GameStateCloneService.DeepClone).ToList();
        public static void Register(EffectRegistry registry, ExternalContentPack pack)
        {
            if (registry.HasAbilityCatalog) return;
            var textResolver = new YC.Application.Presentation.GameplayTextResolver(pack.Presentation);
            var textContext = new YC.Domain.Presentation.GameplayTextContext { Catalog = pack.Presentation, ContentHash = pack.ContentHash,
                Locale = pack.Presentation.Locale, DefinitionNames = pack.ActiveDefinitions.ToDictionary(d => d.DefinitionId, d => d.DisplayName, StringComparer.Ordinal) };
            var actionTexts = pack.ActiveDefinitions.SelectMany(d => d.AbilitySources).SelectMany(s => s.Actions)
                .Where(a => a.Presentation != null).SelectMany(a => new[] { a.Presentation.Name, a.Presentation.Description, a.Presentation.EntryCost, a.Presentation.EntryAfterEffects })
                .Where(r => r != null).Select(r => r.Key).Distinct(StringComparer.Ordinal);
            textResolver.Precompile(actionTexts);
            registry.RegisterActionTextResolver(reference => textResolver.Resolve(reference, textContext).ToPlainText());
            CleanupObligation.Register(registry);
            AbilityActionExecutor.Register(registry); AbilitySourceEffectExecutor.Register(registry); CityStyleSpecialActionEffectExecutor.RegisterPrimitives(registry);
            var compiler = new LuaEffectSpecCompiler(presentationPack: pack);
            var dispatcher = new LuaEffectEventDispatcher(registry, compiler: compiler);
            registry.RegisterAbilityHandler("host.character.use", "host.character.use/2.0.1", call =>
            {
                string error = CharacterLifecycleService.NormalizeActionArguments(call.Arguments, out var arguments);
                if (error.Length > 0) throw new InvalidOperationException(error);
                return new List<EffectSpec> { CharacterLifecycleService.CreateQuickActionSpec(call.SourceRef.AuthorizedPlayerId, call.SourceRef.ItemId,
                    Text(arguments, "mode"), Text(arguments, "order"), Flag(arguments, "offerSecondEffect")) };
            });
            registry.RegisterActionArgumentProtocol("host.character.use", CharacterLifecycleService.NormalizeActionArguments);
            // C05规定复合动力在007接通total付款后撤下此既有报价适配；不扩展新内容的费用API。
            registry.RegisterAbilityHandler("host.style.compat_cost", "legacy.style.payment_choice/1.0.0", call =>
            {
                var definition = YC.Domain.SpecialActions.SpecialActionDatabase.Get(call.State, call.Action.ActionId);
                if (definition == null || definition.FlexibleOriginiumAndIronCost <= 0) throw new InvalidOperationException("legacy_style_cost_invalid");
                var options = new List<ResourceSet>();
                for (int rock = 0; rock <= definition.FlexibleOriginiumAndIronCost; rock++)
                { var cost = definition.FixedCost.Clone(); cost.Originium += rock; cost.Iron += definition.FlexibleOriginiumAndIronCost - rock; options.Add(cost); }
                return new List<EffectSpec> { ResourcePaymentChoiceEffectExecutor.Create(call.SourceRef.AuthorizedPlayerId, options, "city_style.composite.payment") };
            });
            var registrations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var content in pack.ActiveDefinitions)
            {
                RegisterPresentation(registry, content, pack.Presentation,
                    reference => textResolver.Resolve(reference, textContext).ToPlainText());
                var handlers = content.AbilityHandlers.Concat(content.Abilities.Select(a => new ExternalAbilityHandler { HandlerId = a.SubscriptionId, Script = a.Script, Version = a.Version, CompletionHandlerId = a.CompletionHandlerId }))
                    .GroupBy(h => h.HandlerId, StringComparer.Ordinal).Select(group =>
                    {
                        if (group.Select(h => h.Script + "\u001f" + h.Version + "\u001f" + h.CompletionHandlerId).Distinct().Count() != 1) throw new InvalidOperationException("ability_handler_conflict:" + group.Key);
                        return group.First();
                    });
                foreach (var handler in handlers)
                {
                    var owningSource = content.AbilitySources.FirstOrDefault(s => s.HandlerRef == handler.HandlerId || s.AllowedOperations.Contains("cleanup.defer:"+handler.HandlerId) || s.Actions.Any(a => a.BodyHandlerRef == handler.HandlerId || a.EntryCostRef == handler.HandlerId || a.EntryAfterEffectsRef == handler.HandlerId));
                    var owningAbility = content.Abilities.FirstOrDefault(a => a.SubscriptionId == handler.HandlerId);
                    bool eventCompletionOnly = content.ContentType == "event" && owningSource == null && owningAbility == null &&
                        (content.Data["choices"] as Newtonsoft.Json.Linq.JArray)?.Any(choice => (string)choice["completionHandlerId"] == handler.HandlerId) == true;
                    if (eventCompletionOnly) continue; // 由事件选择目录按冻结定义和完整续接身份登记。
                    if (!registrations.Add(handler.HandlerId)) throw new InvalidOperationException("ability_handler_duplicate:" + handler.HandlerId);
                    string source = pack.GetScript(handler.Script), hash = LuaContentHasher.ComputeSha256(source);
                    string ability = owningSource?.AbilityId ?? owningAbility?.AbilityId ?? throw new InvalidOperationException("ability_handler_source_missing:" + handler.HandlerId);
                    string contentId = content.ContentType == "city_style" ? "content." + handler.HandlerId : content.DefinitionId;
                    string instanceId = content.ContentType == "city_style" ? CityStyleLuaCatalog.ContentInstanceId : content.DefinitionId;
                    var definition = new LuaHandlerDefinition(handler.HandlerId, "AbilityInvoked", "", new LuaScriptDefinition(contentId, ability, handler.HandlerId, handler.Version, source, hash, pack.GetScriptEntryId(handler.Script)),
                        EffectHandlerRole.Primary, contentInstanceId: instanceId, abilityId: ability, completionHandlerId: handler.CompletionHandlerId);
                    if (owningSource != null && handler.CompletionHandlerId.Length > 0 &&
                        !registry.TryGetContinuation(handler.CompletionHandlerId, "EffectCompleted", contentId, instanceId,
                            ability, handler.Version, hash, out _))
                    {
                        // 旧城市样式路由和来源能力可能使用同一个完成处理器名但不同入口版本；
                        // 为各自的完整内容身份登记，不能因为名字相同而丢弃正式来源的续接。
                        string subscription = registry.HasEventHandler(handler.CompletionHandlerId)
                            ? StableIdFactory.Create("ability-source-continuation", contentId, instanceId, ability,
                                handler.CompletionHandlerId, handler.Version, hash)
                            : handler.CompletionHandlerId;
                        dispatcher.RegisterContinuation(new LuaHandlerDefinition(subscription, "EffectCompleted", "", new LuaScriptDefinition(contentId, ability, handler.CompletionHandlerId, handler.Version, source, hash, definition.Script.EntryId),
                            EffectHandlerRole.Continuation, contentInstanceId: instanceId, abilityId: ability));
                    }
                    registry.RegisterNonRetainedSourceHandler(handler.HandlerId, hash, call =>
                    {
                        var bound = BindSource(definition, call.SourceRef.AbilityId);
                        return dispatcher.Invoke(bound, Context(call, bound, registry));
                    },handler.Version);
                    if (content.AbilitySources.Any(s => s.Kind == "event" && s.HandlerRef == handler.HandlerId && s.ObservedEvents.Contains(YC.Domain.Cards.OperatorDispatchFlow.StartingEvent)))
                        registry.RegisterOperatorDispatchCountPolicy("ability-source.dispatch-count." + handler.HandlerId,
                            LuaEffectEventDispatcher.CandidateSourceIdentity(definition.Script), (context, payload) =>
                            registry.QuerySources(context.State, -1, "event", YC.Domain.Cards.OperatorDispatchFlow.StartingEvent)
                                .Where(sourceRef => registry.GetAbilitySourceDeclaration(sourceRef.AbilityId).HandlerRef == handler.HandlerId)
                                .OrderBy(sourceRef => sourceRef.SourceKey, StringComparer.Ordinal)
                                .Select(sourceRef => dispatcher.InvokeOperatorDispatchPatch(BindSource(definition, sourceRef.AbilityId), context, payload,
                                    sourceRef.AuthorizedPlayerId, sourceRef.ItemId)).ToArray());
                    if (content.AbilitySources.Any(s => s.Kind == "ruleQuery" && s.HandlerRef == handler.HandlerId && s.QueryKinds.Contains("influence_place_attaching.v1")))
                        registry.RegisterInfluencePlaceHandler(handler.HandlerId, (state, sourceRef, facts) =>
                        {
                            var bound = BindSource(definition, sourceRef.AbilityId);
                            var context = Context(new AbilityActionCallContext { State = state, SourceRef = sourceRef, Arguments = Object() }, bound, registry);
                            context.Event.EventType = "InfluencePlaceAttaching";
                            context.Event.Payload.Properties.Add(new NormalizedValueEntry { Name = "facts", Value = facts.Clone() });
                            context.Event.Payload.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
                            return dispatcher.InvokePlaceRewrite(bound, context);
                        });
                    if (content.AbilitySources.Any(s => s.Kind == "ruleQuery" && s.HandlerRef == handler.HandlerId)) registry.RegisterNonRetainedAbilityPureHandler(handler.HandlerId, (call, query) =>
                    {
                        var bound = BindSource(definition, call.SourceRef.AbilityId);
                        var context = Context(call, bound, registry);
                        context.Event.Payload.Properties.AddRange(new[] {
                            new NormalizedValueEntry { Name = "queryId", Value = NormalizedValue.CreateString(query.QueryId) },
                            new NormalizedValueEntry { Name = "queryKind", Value = NormalizedValue.CreateString(query.QueryKind) },
                            new NormalizedValueEntry { Name = "basisDigest", Value = NormalizedValue.CreateString(query.FactsDigest) },
                            new NormalizedValueEntry { Name = "facts", Value = query.Facts.Clone() },
                            new NormalizedValueEntry { Name = "purpose", Value = NormalizedValue.CreateString(query.Purpose) },
                            new NormalizedValueEntry { Name = "subjectPlayerId", Value = NormalizedValue.CreateInteger(query.SubjectPlayerId) } });
                        context.Event.Payload.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                        return dispatcher.InvokeAbilityQuery(bound, context);
                    });
                }
            }
            UsageTracks.Register(registry, pack.ActiveDefinitions.SelectMany(d => d.UsageTracks));
            UsageTracks.RegisterResetBinding(registry);
            registry.FreezeAbilityCatalog(CreateDeclarations(pack));
            registry.RegisterActionCostPreview((state, source, action, costs) =>
                ResourcePaymentCostPreview.PreviewActionCosts(state, registry, source, action, costs));
            DepartureSourceLuaCatalog.Register(registry, pack);
            var settlements = SettlementHostCatalog.Register(registry, pack);
            CleanupSourceLuaCatalog.Register(registry, settlements);
            foreach (string eventType in Events.Where(e => e != "CandidateSetBuilding" && e != YC.Domain.Cards.OperatorDispatchFlow.StartingEvent))
            {
                if (eventType == "EffectCompleted" && AbilityCompletionSourceCatalog.TryRegister(registry, pack.ContentHash)) continue;
                string subscription = "ability-source.event." + eventType;
                registry.RegisterEventHandler(new EffectEventHandlerRegistration(subscription, eventType, "", subscription, context =>
                {
                    var effects = new List<EffectSpec>();
                    foreach (var source in registry.QuerySources(context.State, eventType == "PlayerCleanupStarted" ? context.Event.PlayerId : -1,
                        eventType == "PlayerCleanupStarted" ? "cleanup" : "event", eventType))
                    {
                        var declaration = registry.GetAbilitySourceDeclaration(source.AbilityId);
                        if (!AbilityEventLuaFacts.MatchesCompletion(context.State, context.Event, declaration)) continue;
                        if (declaration.Kind == "event" && declaration.ResponseKinds.Count > 0) continue;
                        EffectSpec root;
                        if (declaration.Kind == "cleanup" && declaration.RepeatPolicy == "unbounded")
                            root = registry.CreateCleanupSourceRegistration(source, context.Event);
                        else
                        {
                            if (AbilitySourceEffectExecutor.IsConsumed(context.State, source, declaration.Kind == "cleanup" ? "cleanup:" + context.State.Round : "event:" + context.Event.EventId)) continue;
                            root = AbilitySourceEffectExecutor.Create(source, context.Event, declaration.HandlerRef, declaration.Kind == "cleanup", context.State.Round);
                        }
                        if (declaration.Kind == "cleanup") root = settlements.DecorateCleanupRoot(root, source.AbilityId, source.ItemId,
                            ContentLifecycleEffectExecutor.Acquisition(context.State, source.ItemId)?.EffectId ?? context.Event.OwnerNodeId);
                        if(declaration.CleanupInjectionPolicy=="when_available" && !SettlementHostCatalog.CanInjectCleanup(context.State,registry,settlements,source,context.Event)) continue;
                        effects.Add(root);
                    }
                    return effects;
                }, EffectHandlerRole.Observer, definitionVersion: "1.0.0", contentHash: pack.ContentHash, contentId: "ability-source.catalog")
                    // 这些路径只从真实来源构造脱离原图的Spec；可用性回调及收尾注入不作此声明。
                    .WithNonRetainedStateContract(() => eventType != "PlayerCleanupStarted" && !registry.HasAvailabilityControlledEventSource(eventType)));
            }
            // 收尾效果执行完后，只补入当前窗此前没有登记的新来源；不重发收尾开始事件。
            registry.RegisterEventHandler(new EffectEventHandlerRegistration(CleanupSourceRecovery.ReconcileHandler,
                CleanupSourceRecovery.ReconcileEvent, "", CleanupSourceRecovery.ReconcileHandler, context =>
                {
                    var window = context.OwnerNode.SettlementWindow;
                    if (window == null || window.IsClosed || !CleanupSourceRecovery.IsReconcileEvent(context.State, window, context.Event))
                        throw new InvalidOperationException("cleanup_source_reconcile_scope_invalid");
                    var trigger = context.State.EffectRuntime.RuleEvents.Single(e => e.EventId == window.Context.TriggerId);
                    var effects = new List<EffectSpec>();
                    foreach (var source in registry.QuerySources(context.State, window.Context.PlayerId, "cleanup", "PlayerCleanupStarted"))
                    {
                        if (window.Entries.Any(entry => entry.SourceItemId == source.ItemId && entry.AbilityId == source.AbilityId)) continue;
                        var declaration = registry.GetAbilitySourceDeclaration(source.AbilityId);
                        if (declaration.RepeatPolicy != "unbounded" && AbilitySourceEffectExecutor.IsConsumed(context.State, source, "cleanup:" + context.State.Round)) continue;
                        if (declaration.CleanupInjectionPolicy == "when_available" && !SettlementHostCatalog.CanInjectCleanup(context.State, registry, settlements, source, trigger)) continue;
                        var root = declaration.RepeatPolicy == "unbounded" ? registry.CreateCleanupSourceRegistration(source, trigger) :
                            AbilitySourceEffectExecutor.Create(source, context.Event, declaration.HandlerRef, true, context.State.Round);
                        effects.Add(settlements.DecorateCleanupRoot(root, source.AbilityId, source.ItemId,
                            ContentLifecycleEffectExecutor.Acquisition(context.State, source.ItemId)?.EffectId ?? trigger.OwnerNodeId));
                    }
                    return effects;
                }, EffectHandlerRole.Observer, definitionVersion: "1.0.0", contentHash: pack.ContentHash, contentId: "ability-source.catalog"));
        }
        private static LuaHandlerDefinition BindSource(LuaHandlerDefinition definition, string abilityId) => new LuaHandlerDefinition(definition.SubscriptionId,
            definition.EventType, definition.RouteKey, new LuaScriptDefinition(definition.Script.ContentId, abilityId, definition.Script.HandlerId,
                definition.Script.DefinitionVersion, definition.Script.Source, definition.Script.ContentHash, definition.Script.EntryId), definition.Role, definition.Priority,
            definition.ContentInstanceId, abilityId, definition.CompletionHandlerId, definition.AllowOtherPlayerReads, definition.ResponseKind);
        private static EffectEventHandlerContext Context(AbilityActionCallContext call, LuaHandlerDefinition definition, EffectRegistry registry)
        {
            var source = call.SourceRef;
            var declarations = call.State.FindPlayer(source.AuthorizedPlayerId)?.DeclaredCityStyles.Where(d => d.CityStyleId == source.DefinitionId && d.MarkerArea.Length > 0).ToList();
            var payload = Object("sourceItemId", NormalizedValue.CreateString(source.ItemId), "rewardItemId", NormalizedValue.CreateString(source.ItemId),
                "authorizedPlayerId", NormalizedValue.CreateInteger(source.AuthorizedPlayerId),
                "abilityId", NormalizedValue.CreateString(source.AbilityId), "actionId", NormalizedValue.CreateString(call.Action?.ActionId ?? ""),
                "authorizationRef", NormalizedValue.CreateString(source.AuthorizationRef), "activeFaceId", NormalizedValue.CreateString(source.ActiveFaceId), "grantingSourceRefs", Strings(source.GrantingSourceRefs), "arguments", call.Arguments?.Clone() ?? Object(),
                "declarationMarkerCount", NormalizedValue.CreateInteger(declarations?.Count ?? 0), "influenceSupply", NormalizedValue.CreateInteger(call.State.FindPlayer(source.AuthorizedPlayerId).InfluenceSupply),
                "candidateIds", Strings(call.State.Map?.Influences.Where(i => i.PlayerId != source.AuthorizedPlayerId).Select(i => i.InfluenceId).OrderBy(i => i, StringComparer.Ordinal) ?? Enumerable.Empty<string>()));
            bool isPreview = registry.TryGetSourceFactsPreviewEvent(call, out var previewEvent);
            var prior = isPreview || call.OwnerNode == null ? null : Value(call.OwnerNode.NormalizedArguments, "triggerPayload");
            var trigger = isPreview ? previewEvent :
                (call.OwnerNode?.EffectTypeId == AbilitySourceEffectExecutor.TypeId || call.OwnerNode?.EffectTypeId == CleanupSourceInvocationExecutor.TypeId || call.OwnerNode?.EffectTypeId==CleanupObligation.InvokeType) ? call.State.EffectRuntime.RuleEvents.Find(e =>
                e.EventId == Text(call.OwnerNode.NormalizedArguments, "triggerEventId")) : null;
            if (trigger != null) prior = AbilityEventLuaFacts.Enrich(call.State, trigger, prior ?? trigger.Payload);
            if (trigger != null) payload.Properties.AddRange(new[] {
                new NormalizedValueEntry { Name = "triggerEventId", Value = NormalizedValue.CreateString(trigger.EventId) },
                new NormalizedValueEntry { Name = "triggerEventType", Value = NormalizedValue.CreateString(trigger.EventType) },
                new NormalizedValueEntry { Name = "triggeringPlayerId", Value = NormalizedValue.CreateInteger(trigger.PlayerId) },
                new NormalizedValueEntry { Name = "triggerOwnerEffectId", Value = NormalizedValue.CreateString(trigger.OwnerNodeId) },
                new NormalizedValueEntry { Name = "triggerSourceEffectId", Value = NormalizedValue.CreateString(trigger.SourceEffectId) } });
            if (call.OwnerNode?.EffectTypeId == CleanupSourceInvocationExecutor.TypeId)
            {
                var invocation = CleanupSourceService.Invocations(call.State).Single(i => i.TargetEffectId == call.OwnerNode.EffectId);
                payload.Properties.Add(new NormalizedValueEntry { Name = "cleanup", Value = Object("sourceItemId", NormalizedValue.CreateString(source.ItemId),
                    "invocationId", NormalizedValue.CreateString(invocation.InvocationId), "windowId", NormalizedValue.CreateString(invocation.RequestRef.WindowId),
                    "sequence", NormalizedValue.CreateInteger(invocation.Sequence)) });
            }
            var callable = new List<NormalizedValue>();
            if (call.OwnerNode != null && registry.TryResolveSourceFactsCall(call, definition.Script.HandlerId, definition.Script.ContentHash, out _, out bool includeCallableActions) && includeCallableActions)
                foreach (var action in registry.QueryAuthorizedActions(call.State, call.OwnerNode.EffectId, new AbilityActionExecutionOptions { OmitEntryCost = true, OmitMainAction = true }))
                    callable.Add(Object("optionId", NormalizedValue.CreateString(action.OptionId), "itemId", NormalizedValue.CreateString(action.SourceRef.ItemId), "actionId", NormalizedValue.CreateString(action.ActionId),
                        "enabled", NormalizedValue.CreateBoolean(action.Enabled), "disabledReason", NormalizedValue.CreateString(action.DisabledReason)));
            // 旧调用兼容仅限完整集合不超过一页；更大的集合必须使用只读分页，绝不广播截断候选。
            payload.Properties.Add(new NormalizedValueEntry { Name = "callableActions", Value = NormalizedValue.CreateArray(callable.Count<=SourceFactsLuaCatalog.PageSize ? callable : new List<NormalizedValue>()) });
            payload.Properties.Add(new NormalizedValueEntry { Name="callableActionsRequirePaging",Value=NormalizedValue.CreateBoolean(callable.Count>SourceFactsLuaCatalog.PageSize) });
            if (prior?.Kind == NormalizedValueKind.Object) foreach (var entry in prior.Properties.Where(e => payload.Properties.All(p => p.Name != e.Name))) payload.Properties.Add(GameStateCloneService.DeepClone(entry));
            payload.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            var owner = call.OwnerNode ?? new EffectNodeRuntimeState { EffectId = source.SourceKey, PlayerId = source.AuthorizedPlayerId };
            var ruleEvent = new RuleEvent { EventId = trigger?.EventId ?? StableIdFactory.Create("ability-event", owner.EffectId, definition.SubscriptionId), EventType = trigger?.EventType ?? "AbilityInvoked",
                PlayerId = source.AuthorizedPlayerId, OwnerNodeId = owner.EffectId, SourceEffectId = owner.EffectId, RouteKey = source.AbilityId, Payload = payload };
            var context = new EffectEventHandlerContext(call.State, ruleEvent, owner, null);
            SourceFactsLuaCatalog.BindHostCall(context, call, definition, registry);
            return context;
        }
    }
}
