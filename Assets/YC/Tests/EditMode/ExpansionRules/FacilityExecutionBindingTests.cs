using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class FacilityExecutionBindingTests : FacilityLayerTestFixture
    {
        private const string QY010B2ScopeAbility = "qy010.b2.scope", QY010B2ScopeEvent = FacilityBuildJournal.EventType;

        private sealed class QY010B2ScopeCase
        {
            public BindingCase Binding;
            public string SourceChoiceNodeId, BoundChoiceNodeId, GainNodeId, BoundRootId, Site;
            public JObject Archive;
        }
        private void QY010B2InstallScopeLua(bool nested = false)
        {
            const string main = "lua/qy010_fixture_query.lua", module = "lua/facilities/resource_reward.lua";
            string source = File.ReadAllText(Path.Combine(Pack.RootPath, main));
            const string prefix = @"return function(ctx)
 if ctx.eventType=='EffectCompleted' then return {} end
 if ctx.payload.triggerEventType=='FacilityPlaced' then
  if ctx.playerId~='p1' then return {} end
  return {Effect.WithContext(Effect.Choice({player=ctx.playerId,options={'p2'}}),{b2Trigger=ctx.payload.triggerEventId})}
 end
 if ctx.payload.b2Choose then
  return {Effect.WithContext(Effect.Choice({player=ctx.playerId,options={'p2'}}),{b2OriginalBuild=ctx.payload.parentBuildId})}
 end
 if ctx.payload.b2Scope then
  local effects=Shared.entry({playerId='p2',payload={rewardResourceType='iron',rewardAmount=2}})
  table.insert(effects,Effect.Choice({player='p2',options={'QY010B2.scope.done'}}))
  for i,e in ipairs(effects) do effects[i]=Effect.WithContext(e,{executionBinding=ctx.payload.binding,b2Scope='opponent'}) end
  return effects
 end";
            string body = prefix;
            if (nested) body = body.Replace(
                "for i,e in ipairs(effects) do effects[i]=Effect.WithContext(e,{executionBinding=ctx.payload.binding,b2Scope='opponent'}) end\n  return effects",
                "for i,e in ipairs(effects) do effects[i]=Effect.WithContext(e,{b2Scope='opponent-child'}) end\n  return {Effect.WithContext(Effect.Repeat({apiVersion=1,count=1,effects=effects}),{executionBinding=ctx.payload.binding,b2Scope='opponent-root'})}");
            source = source.Replace("return function(ctx)", body);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(Pack.RootPath, "pack.json")));
            string moduleHash = LuaContentHasher.ComputeSha256(File.ReadAllText(Path.Combine(Pack.RootPath, module)));
            var modules = manifest["luaModules"] as JArray ?? new JArray(); manifest["luaModules"] = modules;
            modules.Add(new JObject { ["moduleId"] = "qy010.b2.resource", ["version"] = "1.0.0", ["script"] = module, ["sha256"] = moduleHash });
            var dependencies = manifest["scriptDependencies"] as JArray ?? new JArray(); manifest["scriptDependencies"] = dependencies;
            dependencies.Add(new JObject { ["protocolVersion"] = 1, ["script"] = main,
                ["modules"] = new JArray(new JObject { ["moduleId"] = "qy010.b2.resource", ["version"] = "1.0.0", ["sha256"] = moduleHash, ["alias"] = "entry" }) });
            var token = JObject.Parse(File.ReadAllText(Path.Combine(Pack.RootPath, "component/" + Token + ".json")));
            ((JArray)token["abilitySources"]).Add(JObject.FromObject(new AbilitySourceDeclaration {
                DefinitionId = Token, AbilityId = QY010B2ScopeAbility, Kind = "event", HandlerRef = QY010B2ScopeAbility,
                DeclarationVersion = "1.0.0", EffectiveZones = new List<string> { "core.zone.box_reserve" },
                AuthorizationPolicy = "owner", PresentationRef = Token,
                ObservedEvents = new List<string> { QY010B2ScopeEvent }, AllowedOperations = new List<string> { "execution.bind" } }));
            ((JArray)token["abilityHandlers"]).Add(JObject.FromObject(new ExternalAbilityHandler {
                HandlerId = QY010B2ScopeAbility, Script = main, Version = "1.0.0", CompletionHandlerId = QY010B2ScopeAbility + ".completed" }));
            string root = artifacts.CopyContent(Pack.RootPath, "QY010B2原Build新scope私有完整输入",
                new Dictionary<string, string> { [main] = source, ["pack.json"] = manifest.ToString(), ["component/" + Token + ".json"] = token.ToString() });
            Pack = ExternalContentPack.Load(root);
            artifacts.RecordContent("QY010B2原Build新scope实际装载", root, Pack.ContentHash);
        }
        private LuaInvocationResult QY010B2InvokeScopeLua(BindingCase c, bool choose)
        {
            var payload = new List<LuaSnapshotEntry> {
                new LuaSnapshotEntry(choose ? "b2Choose" : "b2Scope", LuaSnapshotValue.Boolean(true)),
                new LuaSnapshotEntry("parentBuildId", LuaSnapshotValue.String(c.BuildId)) };
            if (!choose) payload.Add(new LuaSnapshotEntry("binding", LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("protocolVersion", LuaSnapshotValue.Integer(1)), new LuaSnapshotEntry("parentBuildId", LuaSnapshotValue.String(c.BuildId)),
                new LuaSnapshotEntry("choiceInteractionId", LuaSnapshotValue.String(c.ChoiceId)), new LuaSnapshotEntry("playerId", LuaSnapshotValue.Integer(2)) })));
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 2, "1.0.0");
            snapshot.EventPayload = LuaSnapshotValue.Object(payload);
            string script = Pack.GetScript("lua/qy010_fixture_query.lua"), hash = LuaContentHasher.ComputeSha256(script);
            Assert.That(hash, Is.EqualTo(c.Version.HandlerHash));
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Token, QY010B2ScopeAbility, QY010B2ScopeAbility, "1.0.0", script, hash),
                new LuaInvocationContext("QY010B2.scope", "AbilityBodyInvoked", "p1", c.State.EffectRuntime.StateRevision, "1.0.0", 2, 0, 0, snapshot));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            return result;
        }
        private LuaEffectCompilationContext QY010B2ScopeContext(BindingCase c) => new LuaEffectCompilationContext {
            State = c.State, Registry = Registry, PlayerId = 1, SourceId = QY010B2ScopeAbility, AbilityId = QY010B2ScopeAbility,
            ContentInstanceId = Token, AuthorizedSource = c.Source, AuthorizedVersion = c.Version };
        private QY010B2ScopeCase QY010B2LegalScope(string site, bool answerChoice = true, bool nested = false)
        {
            QY010B2InstallScopeLua(nested);
            var state = State(); string target = Card(state, Definition(state, d => !d.Unique && !d.HasEntryEffect)).ItemId;
            state = Recover(state, "QY010B2scope合法实体起点");
            var engine = new EffectTreeExecutor(state, Registry);
            string buildId = engine.CreateRoot(Build(state, target, 0));
            for (int guard = 0; engine.GetNode(buildId).FlowStage != "awaiting_entry" && guard < 1000; guard++)
                Assert.That(engine.Advance(), Is.True, state.EffectRuntime.LastFaultMessage);
            Assert.That(FacilityBuildJournal.Read(engine.GetNode(buildId)).AssemblyCommitted, Is.True);
            // 消费本次原Build真实FacilityPlaced事件；生产SourceUse及私有Lua产生原Choice与来源收据。
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            string sourceChoiceId = Open(state).OwnerEffectId;
            var use = state.EffectRuntime.AbilitySourceUses.Single(r => r.AbilityId == QY010B2ScopeAbility && r.PlayerId == 1 &&
                EffectExecutionBinding.Within(state, sourceChoiceId, r.OwnerEffectId));
            var c = new BindingCase { State = state, BuildId = buildId, Source = use.SourceRef, Version = use.VersionRef };
            var compiler = new LuaEffectSpecCompiler(presentationPack: Pack);
            c.ChoiceId = Open(state).GetStableInteractionId();
            var fixture = new QY010B2ScopeCase { Binding = c, SourceChoiceNodeId = sourceChoiceId, Site = site };
            if (!answerChoice) return fixture;
            var question = Open(state);
            Assert.That(question.AnsweringPlayerId, Is.EqualTo(1));
            Assert.That(engine.TrySubmitInteraction(question.GetStableInteractionId(), 1, question.StateRevision,
                NormalizedValue.CreateString("p2"), out string reason), Is.True, reason);
            var archive = HostRecoveryService.CreateArchive(state);
            int opponentBefore = state.FindPlayer(2).Resources.Iron;
            Assert.That(compiler.TryCompile(QY010B2InvokeScopeLua(c, false), QY010B2ScopeContext(c), out var bound, out var fault), Is.True, fault?.Diagnostic);
            Assert.That(bound.Count, Is.EqualTo(nested ? 1 : 2));
            fixture.BoundRootId = engine.CreateChild(sourceChoiceId, bound[0]);
            if (!nested) engine.CreateChild(sourceChoiceId, bound[1]);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(Open(state).AnsweringPlayerId, Is.EqualTo(2));
            fixture.BoundChoiceNodeId = Open(state).OwnerEffectId;
            fixture.GainNodeId = nested ? state.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == fixture.BoundRootId && n.EffectTypeId == "effect.resource.gain").EffectId : fixture.BoundRootId;
            Assert.That(engine.GetNode(fixture.GainNodeId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(opponentBefore + 2), "先证明真实共享Lua收益到账，再测恢复不得重发");
            Assert.That(engine.GetNode(buildId).PlayerId, Is.EqualTo(1));
            Assert.That(engine.GetNode(sourceChoiceId).PlayerId, Is.EqualTo(1));
            Assert.That(engine.GetNode(sourceChoiceId).ContinuationScopeRootEffectId, Is.EqualTo(sourceChoiceId));
            Assert.That(engine.GetNode(fixture.BoundChoiceNodeId).ContinuationScopeRootEffectId, Is.EqualTo(nested ? fixture.BoundRootId : fixture.BoundChoiceNodeId));
            artifacts.WriteLabeledText("V1scope合法归档前完整状态", Newtonsoft.Json.JsonConvert.SerializeObject(state));
            if (site == "stateAfter") HostRecoveryService.AppendJournal(archive, state); else archive = HostRecoveryService.CreateArchive(state);
            fixture.Archive = JObject.Parse(HostRecoveryService.SerializeArchive(archive));
            artifacts.WriteLabeledText("QY010B2scope-" + site + "-合法归档", fixture.Archive.ToString());
            var recovered = HostRecoveryService.RecoverJson(fixture.Archive.ToString(), Registry);
            Assert.That(recovered.Succeeded, Is.True, "损坏前的原Build对手scope必须真实可恢复：" + recovered.Diagnostic);
            QY010B2ScopeConversionControl(fixture);
            return fixture;
        }
        private static Newtonsoft.Json.JsonSerializer QY010B2HostSerializer()
        {
            var field = typeof(HostRecoveryService).GetField("ArchiveSettings", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null); Assert.That(field.IsInitOnly, Is.True);
            return Newtonsoft.Json.JsonSerializer.Create((Newtonsoft.Json.JsonSerializerSettings)field.GetValue(null));
        }
        private void QY010B2ScopeConversionControl(QY010B2ScopeCase fixture)
        {
            var raw = QY010B2ScopeState(fixture.Archive, fixture);
            var copy = GameStateCloneService.DeepClone(fixture.Binding.State);
            copy.EffectRuntime.EffectNodes = raw["EffectRuntime"]["EffectNodes"].ToObject<List<EffectNodeRuntimeState>>(QY010B2HostSerializer());
            copy.EffectRuntime.InteractionRequests = raw["EffectRuntime"]["InteractionRequests"].ToObject<List<InteractionRequest>>(QY010B2HostSerializer());
            bool nodesEqual = JToken.DeepEquals(raw["EffectRuntime"]["EffectNodes"], JArray.FromObject(copy.EffectRuntime.EffectNodes, QY010B2HostSerializer()));
            bool requestsEqual = JToken.DeepEquals(raw["EffectRuntime"]["InteractionRequests"], JArray.FromObject(copy.EffectRuntime.InteractionRequests, QY010B2HostSerializer()));
            string before = RuleStateDigestService.Compute(fixture.Binding.State), after = RuleStateDigestService.Compute(copy);
            bool domain = FacilityLayerRecovery.TryValidateState(copy, Registry, out string domainReason);
            bool context = EffectContinuationContext.TryValidate(copy.EffectRuntime, out string contextReason);
            artifacts.WriteLabeledText("V1scope同通路合法控制-" + fixture.Site,
                Newtonsoft.Json.JsonConvert.SerializeObject(new { nodesEqual, requestsEqual, before, after, domain, domainReason, context, contextReason,
                    nodes = JArray.FromObject(copy.EffectRuntime.EffectNodes, QY010B2HostSerializer()), requests = JArray.FromObject(copy.EffectRuntime.InteractionRequests, QY010B2HostSerializer()) }));
            Assert.That(nodesEqual, Is.True); Assert.That(requestsEqual, Is.True); Assert.That(after, Is.EqualTo(before));
            Assert.That(domain, Is.True, domainReason); Assert.That(context, Is.True, contextReason);
        }
        private static JObject QY010B2ScopeState(JObject archive, QY010B2ScopeCase fixture)
        {
            if (fixture.Site == "snapshot") return (JObject)archive["Snapshot"]["State"];
            var checkpoint = ((JArray)archive["Journal"]["Entries"]).OfType<JObject>().LastOrDefault(e => e["StateAfter"] is JObject state &&
                ((JArray)state["EffectRuntime"]["EffectNodes"]).Any(n => (string)n["EffectId"] == fixture.BoundChoiceNodeId));
            Assert.That(checkpoint, Is.Not.Null, "必须从原AppendJournal形成且含新scope的真实StateAfter变坏");
            return (JObject)checkpoint["StateAfter"];
        }
        private static JObject QY010B2ScopeNode(JObject state, string id) =>
            (JObject)((JArray)state["EffectRuntime"]["EffectNodes"]).Single(n => (string)n["EffectId"] == id);
        private void QY010B2CollectScopeAssertion(Action action, System.Collections.Generic.List<string> failures, string label)
        {
            try { action(); }
            catch (AssertionException error)
            {
                failures.Add(label + ": " + error.Message);
                artifacts.WriteLabeledText("V1矩阵首次断言-" + label, Newtonsoft.Json.JsonConvert.SerializeObject(new { label, error.Message, error.StackTrace }));
            }
        }
        private void QY010B2RecordScopeRejection(QY010B2ScopeCase fixture, JObject bad, string label, bool rawContract, bool domainContract)
        {
            var stateJson = QY010B2ScopeState(bad, fixture); string rawReason = "", layerReason = "", contextReason = "";
            artifacts.WriteLabeledText("QY010B2scope坏档-" + fixture.Site + "-" + label, bad.ToString());
            bool? rawValid = null, layerValid = null, contextValid = null;
            if (rawContract)
            {
                Assert.DoesNotThrow(() => rawValid = FacilityLayerRecovery.TryValidateRaw(stateJson, out rawReason), label);
                Assert.That(rawValid, Is.False, label + " 完整typed授权缺字段/坏类型不得由默认值补齐");
            }
            if (domainContract)
            {
                var copy = GameStateCloneService.DeepClone(fixture.Binding.State);
                // 只由合法归档的节点/交互数组变坏，不手造已付或已完成节点。
                copy.EffectRuntime.EffectNodes = stateJson["EffectRuntime"]["EffectNodes"].ToObject<List<EffectNodeRuntimeState>>(QY010B2HostSerializer());
                copy.EffectRuntime.InteractionRequests = stateJson["EffectRuntime"]["InteractionRequests"].ToObject<List<InteractionRequest>>(QY010B2HostSerializer());
                Assert.DoesNotThrow(() => layerValid = FacilityLayerRecovery.TryValidateState(copy, Registry, out layerReason), label);
                Assert.DoesNotThrow(() => contextValid = EffectContinuationContext.TryValidate(copy.EffectRuntime, out contextReason), label);
                Assert.That(layerValid == false || contextValid == false, Is.True, label + " 绑定或原ContinuationContext保护必须明确拒绝");
            }
            // 故意不对坏状态CreateArchive；从合法序列化原件变坏后实际调用RecoverJson。
            var result = HostRecoveryService.RecoverJson(bad.ToString(), Registry);
            artifacts.WriteLabeledText("QY010B2scope结果-" + fixture.Site + "-" + label,
                Newtonsoft.Json.JsonConvert.SerializeObject(new { label, fixture.Site, rawValid, rawReason, layerValid, layerReason,
                    contextValid, contextReason, result.Succeeded, result.FaultCode, result.Diagnostic }));
            Assert.That(result.Succeeded, Is.False, label + " RecoverJson不得接受跨scope/伪授权：" + result.Diagnostic);
        }

        [TestCase("snapshot")] [TestCase("stateAfter")]
        public void QY010B2ScopeRawAuthorizationMustBeCompleteBeforeDeserialization(string site)
        {
            var failures = new System.Collections.Generic.List<string>();
            var fixture = QY010B2LegalScope(site);
            var fields = new[] { "ExecutionBinding", "ExecutionBinding.ProtocolVersion", "ExecutionBinding.PlayerId",
                "ExecutionBinding.AuthorizedAtRevision", "ExecutionBinding.ParentBuildId", "ExecutionBinding.ChoiceInteractionId",
                "ExecutionBinding.AuthorizationRef", "ExecutionBinding.SourceRef", "ExecutionBinding.VersionRef",
                "ExecutionBinding.SourceRef.SourceKey", "ExecutionBinding.SourceRef.ItemId", "ExecutionBinding.SourceRef.DefinitionId",
                "ExecutionBinding.SourceRef.AbilityId", "ExecutionBinding.SourceRef.ActiveFaceId", "ExecutionBinding.SourceRef.DeclarationVersion",
                "ExecutionBinding.SourceRef.AuthorizedPlayerId", "ExecutionBinding.SourceRef.AuthorizationRef", "ExecutionBinding.SourceRef.EffectiveZoneId",
                "ExecutionBinding.SourceRef.RuleOwnerItemId", "ExecutionBinding.SourceRef.GrantingSourceRefs",
                "ExecutionBinding.VersionRef.ContractVersion", "ExecutionBinding.VersionRef.ContentPackHash",
                "ExecutionBinding.VersionRef.DefinitionVersion", "ExecutionBinding.VersionRef.HandlerHash", "ExecutionBinding.VersionRef.ExecutorVersion" };
            {
                foreach (string field in fields)
                foreach (bool missing in new[] { true, false })
                {
                    var bad = (JObject)fixture.Archive.DeepClone();
                    var node = QY010B2ScopeNode(QY010B2ScopeState(bad, fixture), fixture.BoundChoiceNodeId);
                    var value = node.SelectToken(field);
                    if (missing) ((JProperty)value.Parent).Remove();
                    else value.Replace(value.Type == JTokenType.Integer ? (JToken)new JValue("wrong-integer") :
                        value.Type == JTokenType.String ? new JArray("wrong-string") : new JValue(false));
                    QY010B2CollectScopeAssertion(() => QY010B2RecordScopeRejection(fixture, bad, field + (missing ? ".missing" : ".type"), true, false), failures, "QY010B2ScopeRawAuthorizationMustBeCompleteBeforeDeserialization");
                }
            }
            Assert.That(failures, Is.Empty, "各变体保持全部断言；合法控制在聚合前独立完成");
        }

        [TestCase("snapshot")] [TestCase("stateAfter")]
        public void QY010B2ScopeDomainRejectsNullAuthorizationCrossScopeAndForgedCommittedChoice(string site)
        {
            var failures = new System.Collections.Generic.List<string>();
            var fixture = QY010B2LegalScope(site);
            var cases = new[] { "binding-null", "binding-source-null", "binding-version-null", "binding-player", "binding-parent",
                "source-key", "source-item", "source-authority", "source-player", "version-contract", "version-executor", "version-content", "version-definition", "version-handler",
                "proof", "authorized-before-answer", "choice-missing", "choice-unanswered", "choice-wrong-answerer", "choice-wrong-answer", "choice-wrong-owner",
                "scope-empty", "scope-other-sibling", "scope-original-player", "child-player", "decision-player", "build-player" };
            {
                foreach (string mutation in cases)
                {
                    var bad = (JObject)fixture.Archive.DeepClone(); var stateJson = QY010B2ScopeState(bad, fixture);
                    var node = QY010B2ScopeNode(stateJson, fixture.BoundChoiceNodeId);
                    var binding = (JObject)node["ExecutionBinding"];
                    var choice = ((JArray)stateJson["EffectRuntime"]["InteractionRequests"]).Single(i =>
                        (string)i["InteractionId"] == fixture.Binding.ChoiceId || (string)i["RequestId"] == fixture.Binding.ChoiceId);
                    switch (mutation)
                    {
                        case "binding-null": node["ExecutionBinding"] = JValue.CreateNull(); break;
                        case "binding-source-null": binding["SourceRef"] = JValue.CreateNull(); break;
                        case "binding-version-null": binding["VersionRef"] = JValue.CreateNull(); break;
                        case "binding-player": binding["PlayerId"] = 1; break;
                        case "binding-parent": binding["ParentBuildId"] = fixture.SourceChoiceNodeId; break;
                        case "source-key": binding["SourceRef"]["SourceKey"] = "QY010B2.forged-source"; break;
                        case "source-item": binding["SourceRef"]["ItemId"] = "QY010B2.forged-item"; break;
                        case "source-authority": binding["SourceRef"]["AuthorizationRef"] = "QY010B2.forged-authority"; break;
                        case "source-player": binding["SourceRef"]["AuthorizedPlayerId"] = 2; break;
                        case "version-contract": binding["VersionRef"]["ContractVersion"] = "QY010B2.wrong-contract"; break;
                        case "version-executor": binding["VersionRef"]["ExecutorVersion"] = "QY010B2.wrong-executor"; break;
                        case "version-content": binding["VersionRef"]["ContentPackHash"] = "QY010B2.wrong-content"; break;
                        case "version-definition": binding["VersionRef"]["DefinitionVersion"] = "QY010B2.wrong-version"; break;
                        case "version-handler": binding["VersionRef"]["HandlerHash"] = new string('0', 64); break;
                        case "proof": binding["AuthorizationRef"] = new string('0', 64); break;
                        case "authorized-before-answer": binding["AuthorizedAtRevision"] = 0; break;
                        case "choice-missing": binding["ChoiceInteractionId"] = "QY010B2.missing-choice"; break;
                        case "choice-unanswered": choice["Status"] = "open"; choice["AnsweredAtRevision"] = 0; break;
                        case "choice-wrong-answerer": choice["AnsweringPlayerId"] = 2; break;
                        case "choice-wrong-answer": choice["NormalizedAnswer"] = JObject.FromObject(NormalizedValue.CreateString("p1")); break;
                        case "choice-wrong-owner": choice["OwnerEffectId"] = fixture.BoundChoiceNodeId; break;
                        case "scope-empty": node["ContinuationScopeRootEffectId"] = ""; break;
                        case "scope-other-sibling": node["ContinuationScopeRootEffectId"] = fixture.GainNodeId; break;
                        case "scope-original-player": node["ContinuationScopeRootEffectId"] = fixture.SourceChoiceNodeId; break;
                        case "child-player": node["PlayerId"] = 1; break;
                        case "decision-player": node["DecisionPlayerId"] = 1; break;
                        case "build-player": QY010B2ScopeNode(stateJson, fixture.Binding.BuildId)["PlayerId"] = 2; break;
                    }
                    QY010B2CollectScopeAssertion(() => QY010B2RecordScopeRejection(fixture, bad, mutation, false, true), failures, "QY010B2ScopeDomainRejectsNullAuthorizationCrossScopeAndForgedCommittedChoice");
                }
            }
            Assert.That(failures, Is.Empty, "各变体保持全部断言；合法控制在聚合前独立完成");
        }

        [Test] public void QY010B2ActualLuaChoiceMustCommitAndUnrelatedRootCannotLendItsAnswer()
        {
            var fixture = QY010B2LegalScope("snapshot", false); var c = fixture.Binding;
            var compiler = new LuaEffectSpecCompiler(presentationPack: Pack);
            var result = QY010B2InvokeScopeLua(c, false); string before = RuleStateDigestService.Compute(c.State);
            Assert.That(compiler.TryCompile(result, QY010B2ScopeContext(c), out _, out _), Is.False);
            Assert.That(RuleStateDigestService.Compute(c.State), Is.EqualTo(before));
            var question = Open(c.State); var engine = new EffectTreeExecutor(c.State, Registry);
            Assert.That(engine.TrySubmitInteraction(question.GetStableInteractionId(), 2, question.StateRevision,
                NormalizedValue.CreateString("p2"), out _), Is.False);
            Assert.That(engine.TrySubmitInteraction(question.GetStableInteractionId(), 1, question.StateRevision,
                NormalizedValue.CreateString("p2"), out string reason), Is.True, reason);
            Assert.That(compiler.TryCompile(QY010B2InvokeScopeLua(c, false), QY010B2ScopeContext(c), out var bound, out var fault), Is.True, fault?.Diagnostic);
            var payloadOnly = QY010B2ScopeContext(c); payloadOnly.AuthorizedSource = null; payloadOnly.AuthorizedVersion = null;
            Assert.That(compiler.TryCompile(QY010B2InvokeScopeLua(c, false), payloadOnly, out _, out _), Is.False);
            Assert.That(engine.GetNode(c.BuildId).PlayerId, Is.EqualTo(1));
            Assert.That(engine.GetNode(fixture.SourceChoiceNodeId).PlayerId, Is.EqualTo(1));
            // 同来源的另一个真实Lua Choice作为独立根回答，不是原Build的Choice。
            Assert.That(compiler.TryCompile(QY010B2InvokeScopeLua(c, true), QY010B2ScopeContext(c), out var unrelated, out fault), Is.True, fault?.Diagnostic);
            string unrelatedId = engine.CreateRoot(unrelated.Single());
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, c.State.EffectRuntime.LastFaultMessage);
            var rootQuestion = c.State.EffectRuntime.InteractionRequests.Single(i => i.OwnerEffectId == unrelatedId && i.Status == "open");
            Assert.That(engine.TrySubmitInteraction(rootQuestion.GetStableInteractionId(), 1, rootQuestion.StateRevision,
                NormalizedValue.CreateString("p2"), out reason), Is.True, reason);
            var error = Assert.Throws<InvalidOperationException>(() => EffectExecutionBinding.Create(c.State, Registry,
                c.BuildId, c.Source, rootQuestion.GetStableInteractionId(), 2, c.Version));
            Assert.That(error.Message, Is.EqualTo("effect_execution_choice_invalid"));
            Assert.That(bound.All(s => s.PlayerId == 2), Is.True);
        }

        [TestCase("snapshot")] [TestCase("stateAfter")]
        public void QY010B2NestedScopeValidatesSuppliedPersistentAuthorizationAndInheritsRealScope(string site)
        {
            var failures = new System.Collections.Generic.List<string>();
            var fixture = QY010B2LegalScope(site, nested: true);
            var liveRoot = fixture.Binding.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == fixture.BoundRootId);
            Assert.That(liveRoot.NestedEffects.Count, Is.EqualTo(2));
            Assert.That(liveRoot.NestedEffects.All(s => s.ExecutionBinding != null && s.ExecutionBinding.PlayerId == 2), Is.True);
            Assert.That(liveRoot.ChildEffectIds.All(id => fixture.Binding.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)
                .ContinuationScopeRootEffectId == liveRoot.EffectId), Is.True);
            {
                foreach (string mutation in new[] { "raw-binding-missing", "raw-binding-type", "source", "version", "player", "proof" })
                {
                    var bad = (JObject)fixture.Archive.DeepClone();
                    var root = QY010B2ScopeNode(QY010B2ScopeState(bad, fixture), fixture.BoundRootId);
                    var spec = (JObject)root["NestedEffects"][0];
                    bool raw = mutation.StartsWith("raw-", StringComparison.Ordinal);
                    switch (mutation)
                    {
                        case "raw-binding-missing": spec.Property("ExecutionBinding").Remove(); break;
                        case "raw-binding-type": spec["ExecutionBinding"] = false; break;
                        case "source": spec["ExecutionBinding"]["SourceRef"]["SourceKey"] = "QY010B2.nested.wrong-source"; break;
                        case "version": spec["ExecutionBinding"]["VersionRef"]["HandlerHash"] = new string('0', 64); break;
                        case "player": spec["ExecutionBinding"]["PlayerId"] = 1; break;
                        case "proof": spec["ExecutionBinding"]["AuthorizationRef"] = new string('0', 64); break;
                    }
                    QY010B2CollectScopeAssertion(() => QY010B2RecordScopeRejection(fixture, bad, "nested." + mutation, raw, !raw), failures, "QY010B2NestedScopeValidatesSuppliedPersistentAuthorizationAndInheritsRealScope");
                }
            }
            var recovered = HostRecoveryService.RecoverJson(fixture.Archive.ToString(), Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            var question = Open(recovered.State); var engine = new EffectTreeExecutor(recovered.State, Registry);
            int iron = recovered.State.FindPlayer(2).Resources.Iron;
            Assert.That(engine.TrySubmitInteraction(question.GetStableInteractionId(), 2, question.StateRevision,
                NormalizedValue.CreateString("QY010B2.scope.done"), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, recovered.State.EffectRuntime.LastFaultMessage);
            Assert.That(recovered.State.FindPlayer(2).Resources.Iron, Is.EqualTo(iron));
            Assert.That(engine.GetNode(fixture.Binding.BuildId).PlayerId, Is.EqualTo(1));
            Assert.That(engine.GetNode(fixture.SourceChoiceNodeId).PlayerId, Is.EqualTo(1));
            Assert.That(failures, Is.Empty, "各变体保持全部断言；合法控制在聚合前独立完成");
        }

        [TestCase("snapshot")] [TestCase("stateAfter")]
        public void QY010B2NewScopeRecoversPendingAnswerAndCompletesExactlyOnceWithoutChangingOriginalPlayer(string site)
        {
            var fixture = QY010B2LegalScope(site); var c = fixture.Binding;
            int scored = c.State.FindPlayer(1).Score, ownerIron = c.State.FindPlayer(1).Resources.Iron;
            int opponentIron = c.State.FindPlayer(2).Resources.Iron;
            var first = HostRecoveryService.RecoverJson(fixture.Archive.ToString(), Registry);
            Assert.That(first.Succeeded, Is.True, first.Diagnostic);
            c.State = first.State; var engine = new EffectTreeExecutor(c.State, Registry); var request = Open(c.State);
            Assert.That(request.AnsweringPlayerId, Is.EqualTo(2));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("QY010B2.scope.done"), out _), Is.False);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision,
                NormalizedValue.CreateString("QY010B2.scope.done"), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, c.State.EffectRuntime.LastFaultMessage);
            string completed = engine.GetNode(c.BuildId).CompletedEventId;
            Assert.That(completed, Is.Not.Empty);
            var final = Recover(c.State, "QY010B2对手scope完成后再次恢复");
            Assert.That(new EffectTreeExecutor(final, Registry).RunUntilQuiescent().Faulted, Is.False, final.EffectRuntime.LastFaultMessage);
            Assert.That(final.FindPlayer(1).Score, Is.EqualTo(scored));
            Assert.That(final.FindPlayer(1).Resources.Iron, Is.EqualTo(ownerIron));
            Assert.That(final.FindPlayer(2).Resources.Iron, Is.EqualTo(opponentIron), "对手收益已在待答前到账，恢复/完成不能重发");
            Assert.That(final.EffectRuntime.EffectNodes.Single(n => n.EffectId == c.BuildId).PlayerId, Is.EqualTo(1));
            Assert.That(final.EffectRuntime.EffectNodes.Single(n => n.EffectId == fixture.SourceChoiceNodeId).PlayerId, Is.EqualTo(1));
            Assert.That(final.EffectRuntime.EffectNodes.Single(n => n.EffectId == fixture.BoundChoiceNodeId).PlayerId, Is.EqualTo(2));
            Assert.That(final.EffectRuntime.EffectNodes.Single(n => n.EffectId == c.BuildId).CompletedEventId, Is.EqualTo(completed));
            Assert.That(final.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.EqualTo(1));
        }

        private sealed class BindingCase
        {
            public GameState State;
            public string BuildId, ChoiceId;
            public AbilitySourceRef Source;
            public AbilityVersionRef Version;
        }
        private BindingCase CommittedChoice(bool answer = true, bool withObligation = false)
        {
            var state = State(response:withObligation);
            string target;
            if(withObligation) {
                OwnInfluence(state);string low=Definition(state,d=>!d.Unique && !d.HasEntryEffect);Put(state,Card(state,low).ItemId,0);
                int score=FacilityCardDatabase.Get(state,low).Score;
                target=Card(state,Definition(state,d=>!d.Unique && d.Score>score && d.HasEntryEffect && d.OnBuiltReward.Iron>0)).ItemId;
                const string response="return function(ctx) if ctx.payload.ownerPlayerId~=1 then return {} end; return {Effect.Choice({player=ctx.playerId,options={'removed.response'}})} end";
                new LuaEffectEventDispatcher(Registry).Register(new LuaHandlerDefinition("qy007.layer.real-response","InfluenceRemoved","",
                    new LuaScriptDefinition(Token,QueryAbility,"qy007.layer.real-response","1.0.0",response,LuaContentHasher.ComputeSha256(response)),EffectHandlerRole.Observer));
            } else target = Card(state, Definition(state, d => !d.Unique && !d.HasEntryEffect)).ItemId;
            var source = Registry.FacilityBuildQueries.ResolveBuiltFacts(state, 1).SourceResults.Single();
            Registry.Register(new EffectRegistration("qy010.fixture.choose_player", c => {
                if (c.Node.FlowStage.Length > 0) return EffectStepResult.Completed(c.GetLatestInteractionAnswer());
                var interaction = new EffectInteractionSpec { InteractionTypeId = "qy010.fixture.choose_player",
                    AnsweringPlayerId = 1, PromptKey = "qy010.fixture.choose_player", Visibility = "owner",
                    AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
                interaction.CandidateIds.Add("p2");
                return EffectStepResult.Continue("answer").AddInteraction(interaction);
            }, EffectExecutorKind.IntrinsicFlow));
            var engine = new EffectTreeExecutor(state, Registry); string buildId = engine.CreateRoot(Build(state, target, 0));
            for (int guard = 0; guard < 1000 && engine.GetNode(buildId).FlowStage != "awaiting_entry"; guard++)
                Assert.That(engine.Advance(), Is.True, state.EffectRuntime.LastFaultMessage);
            Assert.That(FacilityBuildJournal.Read(engine.GetNode(buildId)).AssemblyCommitted, Is.True);
            if(withObligation) {
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);var removal=Open(state);
                Assert.That(removal.AllowDecline,Is.False);
                Assert.That(engine.TrySubmitInteraction(removal.GetStableInteractionId(),1,removal.StateRevision,NormalizedValue.CreateString(removal.CandidateIds[0]),out var reason),Is.True,reason);
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
                Assert.That(Open(state).CandidateIds,Is.EqualTo(new[]{"removed.response"}));
                Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Activate && n.FlowStage.Length>0),Is.False,"必做移除真实响应未答前不得原入场");
                state=Recover(state,"原Build已落位计分且必做移除真实响应待答");engine=new EffectTreeExecutor(state,Registry);var response=Open(state);
                Assert.That(engine.TrySubmitInteraction(response.GetStableInteractionId(),1,response.StateRevision,NormalizedValue.CreateString("removed.response"),out reason),Is.True,reason);
            }
            var choose = EffectSpec.Create("qy010.fixture.choose_player", playerId: 1);
            choose.ContinuationContentId = Token; choose.ContinuationContentInstanceId = source.SourceRef.ItemId;
            choose.ContinuationAbilityId = source.SourceRef.AbilityId;
            choose.ContinuationDefinitionVersion = source.VersionRef.DefinitionVersion;
            choose.ContinuationContentHash = source.VersionRef.HandlerHash;
            engine.CreateChild(buildId, choose);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = Open(state);
            if (answer) Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("p2"), out string reason), Is.True, reason);
            return new BindingCase { State = state, BuildId = buildId, ChoiceId = request.GetStableInteractionId(),
                Source = source.SourceRef, Version = source.VersionRef };
        }
        private EffectExecutionBinding Bind(BindingCase c) => EffectExecutionBinding.Create(c.State, Registry,
            c.BuildId, c.Source, c.ChoiceId, 2, c.Version);

        [Test] public void FrozenFacilityLuaRunsForTheChosenOpponentAfterRequiredRemovalResponseAndOriginalEntryInOneBuild()
        {
            const string main="lua/qy010_fixture_query.lua",module="lua/facilities/resource_reward.lua";
            string raw=File.ReadAllText(Path.Combine(Pack.RootPath,main));
            string branch=@"return function(ctx) if ctx.eventType=='EffectCompleted' then return {} end; if ctx.payload.moduleTest then
local effects=Shared.entry({playerId='p2',payload={rewardResourceType='iron',rewardAmount=2}})
table.insert(effects,Effect.Choice({player='p2',options={'module.done'}}))
for i,e in ipairs(effects) do effects[i]=Effect.WithContext(e,{executionBinding=ctx.payload.binding,boundFor='p2'}) end
return effects end";
            raw=raw.Replace("return function(ctx)",branch);
            string moduleHash=LuaContentHasher.ComputeSha256(File.ReadAllText(Path.Combine(Pack.RootPath,module)));
            var manifest=JObject.Parse(File.ReadAllText(Path.Combine(Pack.RootPath,"pack.json")));
            manifest["luaModules"]=new JArray(new JObject{["moduleId"]="qy007.bound.resource",["version"]="1.0.0",["script"]=module,["sha256"]=moduleHash});
            manifest["scriptDependencies"]=new JArray(new JObject{["protocolVersion"]=1,["script"]=main,["modules"]=new JArray(new JObject{["moduleId"]="qy007.bound.resource",["version"]="1.0.0",["sha256"]=moduleHash,["alias"]="entry"})});
            var token=JObject.Parse(File.ReadAllText(Path.Combine(Pack.RootPath,"component/"+Token+".json")));
            token["abilityHandlers"][0]["CompletionHandlerId"]=QueryAbility+".module.completed";
            string root=artifacts.CopyContent(Pack.RootPath,"共享真实设施与对手原Build私有输入",new Dictionary<string,string>{[main]=raw,["pack.json"]=manifest.ToString(),["component/"+Token+".json"]=token.ToString()});
            Pack=ExternalContentPack.Load(root);artifacts.RecordContent("共享设施绑定实际装载",root,Pack.ContentHash);
            var c=CommittedChoice(withObligation:true);var facts=FacilityBuildJournal.Read(c.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==c.BuildId));
            Assert.That(c.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.ObligationEffectId).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(c.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.EntryEffectId).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(c.State.FindPlayer(1).Resources.Iron,Is.GreaterThan(100));int opponent=c.State.FindPlayer(2).Resources.Iron;
            c.State=Recover(c.State,"本人原入场完成并选择对手后冷恢复");
            var snapshot=LuaFacadeSnapshot.FromInvocation("p1",0,0,2,"1.0.0");
            snapshot.EventPayload=LuaSnapshotValue.Object(new[]{new LuaSnapshotEntry("moduleTest",LuaSnapshotValue.Boolean(true)),new LuaSnapshotEntry("binding",LuaSnapshotValue.Object(new[]{
                new LuaSnapshotEntry("protocolVersion",LuaSnapshotValue.Integer(1)),new LuaSnapshotEntry("parentBuildId",LuaSnapshotValue.String(c.BuildId)),
                new LuaSnapshotEntry("choiceInteractionId",LuaSnapshotValue.String(c.ChoiceId)),new LuaSnapshotEntry("playerId",LuaSnapshotValue.Integer(2))}))});
            string script=Pack.GetScript(main),hash=LuaContentHasher.ComputeSha256(script);Assert.That(hash,Is.EqualTo(c.Version.HandlerHash));
            var result=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Token,QueryAbility,QueryAbility,"1.0.0",script,hash),
                new LuaInvocationContext("qy007.bound.module","AbilityBodyInvoked","p1",0,"1.0.0",2,0,0,snapshot));Assert.That(result.IsSuccess,Is.True,result.Diagnostic);
            var compiler=new LuaEffectSpecCompiler(presentationPack:Pack);var context=new LuaEffectCompilationContext {State=c.State,Registry=Registry,PlayerId=1,AbilityId=QueryAbility,ContentInstanceId=Token};
            Assert.That(compiler.TryCompile(result,context,out _,out _),Is.False,"普通payload的绑定字段本身不能授权");
            context.AuthorizedSource=c.Source;context.AuthorizedVersion=c.Version;
            Assert.That(compiler.TryCompile(result,context,out var specs,out var fault),Is.True,fault?.Diagnostic);Assert.That(specs.Count,Is.EqualTo(2));
            var engine=new EffectTreeExecutor(c.State,Registry);var ids=specs.Select(s=>engine.CreateChild(c.BuildId,s)).ToArray();
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,c.State.EffectRuntime.LastFaultMessage);var request=Open(c.State);Assert.That(request.AnsweringPlayerId,Is.EqualTo(2));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString("module.done"),out _),Is.False);
            c.State=Recover(c.State,"共享模块对手真实请求新scope恢复");engine=new EffectTreeExecutor(c.State,Registry);request=Open(c.State);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),2,request.StateRevision,NormalizedValue.CreateString("module.done"),out var reason),Is.True,reason);
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,c.State.EffectRuntime.LastFaultMessage);
            Assert.That(c.State.FindPlayer(2).Resources.Iron,Is.EqualTo(opponent+2));
            Assert.That(c.State.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Build),Is.EqualTo(1));
            foreach(string id in ids) {var n=engine.GetNode(id);Assert.That(n.PlayerId,Is.EqualTo(2));Assert.That(n.ParentEffectId,Is.EqualTo(c.BuildId));Assert.That(n.ContinuationScopeRootEffectId,Is.Not.Empty);Assert.That(n.ContinuationScopeRootEffectId,Is.Not.EqualTo(engine.GetNode(c.BuildId).ContinuationScopeRootEffectId));}
            c.State=Recover(c.State,"共享模块原Build对手完整完成不重执行");
        }

        [Test] public void OnlyCommittedChoiceCanAuthorizeObject()
        {
            var c = CommittedChoice(false);
            Assert.Throws<InvalidOperationException>(() => Bind(c));
            var request = Open(c.State); var engine = new EffectTreeExecutor(c.State, Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("p2"), out string reason), Is.True, reason);
            Assert.That(Bind(c).PlayerId, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => EffectExecutionBinding.Create(c.State, Registry,
                c.BuildId, c.Source, c.ChoiceId, 1, c.Version));
        }

        [Test] public void AuthorizedOpponentExecutesReceivesAndAnswersWithinOriginalBuild()
        {
            var c = CommittedChoice(); var binding = Bind(c);
            // 通用资源原语；没有复制建设、设施入场或建设计分。
            var spec = EffectSpec.Create("effect.resource.gain",
                Object("executingPlayer", NormalizedValue.CreateStableReference("player", "p2"), "resourceType", NormalizedValue.CreateString("GoldVoucher"),
                    "amount", NormalizedValue.CreateInteger(3)), 2);
            EffectExecutionBinding.ApplyToSpec(c.State, Registry, spec, binding, 2, 2, 2);
            Registry.Register(new EffectRegistration("qy010.fixture.bound_answer", context => {
                if (context.Node.FlowStage.Length > 0) return EffectStepResult.Completed(context.GetLatestInteractionAnswer());
                var request = new EffectInteractionSpec { InteractionTypeId = "qy010.fixture.bound_answer",
                    AnsweringPlayerId = context.Node.PlayerId, PromptKey = "qy010.fixture.bound_answer", Visibility = "owner",
                    AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
                request.CandidateIds.Add("answer.ok");
                return EffectStepResult.Continue("answer").AddInteraction(request);
            }, EffectExecutorKind.IntrinsicFlow));
            int before = c.State.FindPlayer(2).Resources.GoldVoucher;
            var engine = new EffectTreeExecutor(c.State, Registry); string child = engine.CreateChild(c.BuildId, spec);
            var answerSpec = EffectSpec.Create("qy010.fixture.bound_answer", playerId: 2);
            EffectExecutionBinding.ApplyToSpec(c.State, Registry, answerSpec, binding, 2, 2, 2);
            engine.CreateChild(c.BuildId, answerSpec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, c.State.EffectRuntime.LastFaultMessage);
            var request = Open(c.State);
            Assert.That(request.AnsweringPlayerId, Is.EqualTo(2));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("answer.ok"), out _), Is.False);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision,
                NormalizedValue.CreateString("answer.ok"), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, c.State.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(child).ParentEffectId, Is.EqualTo(c.BuildId));
            Assert.That(engine.GetNode(child).PlayerId, Is.EqualTo(2));
            Assert.That(c.State.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(before + 3));
            var restored = Recover(c.State, "对手执行对象完成后");
            Assert.That(restored.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(before + 3));
            Assert.That(restored.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.EqualTo(1));
        }

        [Test] public void BindingRejectsAlteredSourceVersionRecipientAndCrossPlayerActivation()
        {
            var c = CommittedChoice(); var binding = Bind(c);
            var spec = EffectSpec.Create("effect.resource.gain", playerId: 2);
            Assert.Throws<InvalidOperationException>(() => EffectExecutionBinding.ApplyToSpec(c.State, Registry, spec, binding, explicitRecipient: 1));
            Assert.Throws<InvalidOperationException>(() => EffectExecutionBinding.ApplyToSpec(c.State, Registry,
                FacilityEntryEffectSpecFactory.Activate(2, "unused", "unused", 0), binding));
            var changed = GameStateCloneService.DeepClone(binding); changed.VersionRef.HandlerHash = "changed";
            Assert.Throws<InvalidOperationException>(() => EffectExecutionBinding.Validate(c.State, Registry, changed));
            changed = GameStateCloneService.DeepClone(binding); changed.AuthorizationRef = "forged";
            Assert.Throws<InvalidOperationException>(() => EffectExecutionBinding.Validate(c.State, Registry, changed));
            changed = GameStateCloneService.DeepClone(binding); changed.ParentBuildId = "another-build";
            Assert.Throws<InvalidOperationException>(() => EffectExecutionBinding.Validate(c.State, Registry, changed));
        }

        [Test] public void BindingCannotBeReusedAsUnrelatedRoot()
        {
            var c = CommittedChoice(); var spec = EffectSpec.Create("qy010.fixture.choose_player", playerId: 2);
            EffectExecutionBinding.ApplyToSpec(c.State, Registry, spec, Bind(c));
            var engine = new EffectTreeExecutor(c.State, Registry);
            int before = c.State.EffectRuntime.EffectNodes.Count;
            var error = Assert.Throws<InvalidOperationException>(() => engine.CreateRoot(spec));
            Assert.That(error.Message, Does.Contain("effect_execution_binding_root_forbidden"));
            Assert.That(c.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(c.State.EffectRuntime.EffectNodes.Count, Is.EqualTo(before));
            Evidence(c.State, "对象授权越出原Build的首次故障");
        }

        [Test] public void RequiredOutcomeRejectsShortCountAndDeclineButAcceptsExactResult()
        {
            var required = new RequiredEffectOutcome { ResultField = "completedCount", ExpectedValue = 1 };
            var node = new EffectNodeRuntimeState { PendingOutcome = EffectPendingOutcome.Completed,
                NormalizedResult = Object("completedCount", NormalizedValue.CreateInteger(0)) };
            Assert.Throws<KernelException>(() => RequiredEffectOutcome.ValidateBeforeTerminal(node, required));
            node.NormalizedResult = Object("completedCount", NormalizedValue.CreateInteger(1));
            Assert.DoesNotThrow(() => RequiredEffectOutcome.ValidateBeforeTerminal(node, required));
            node.AllowDecline = true;
            Assert.Throws<KernelException>(() => RequiredEffectOutcome.ValidateBeforeTerminal(node, required));
            Assert.Throws<KernelException>(() => RequiredEffectOutcome.ValidateInteraction(new EffectInteractionSpec { AllowDecline = true }, required));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void LegacyMigrationAcceptsOnlyUnambiguousSupplyOrders(int members)
        {
            var state = State(); string definition = Definition(state, d => !d.Unique);
            string supply = FindZone(state, "facility_supply_0").ZoneId;
            for (int i = 0; i < members; i++) Transfer(state, Leg(state, Card(state, definition).ItemId, supply, "bottom"));
            var legacy = GameStateCloneService.DeepClone(state);
            // 明确构造旧结构的隔离迁移输入，不修改真实存档或正常验收状态。
            var covered = legacy.ItemZones.Zones.Where(z => z.TemplateId == FacilityLayerQuery.CoveredTemplate).Select(z => z.ZoneId).ToArray();
            legacy.ItemZones.Zones.RemoveAll(z => covered.Contains(z.ZoneId));
            var supplies = legacy.ItemZones.Zones.Where(z => z.TemplateId == FacilityLayerQuery.SupplyTemplate).Select(z => z.ZoneId).ToArray();
            legacy.ItemZones.Orders.RemoveAll(o => covered.Contains(o.ZoneId) || supplies.Contains(o.ZoneId));
            var raw = Newtonsoft.Json.Linq.JObject.FromObject(legacy);
            ((Newtonsoft.Json.Linq.JObject)raw["ItemZones"]).Remove("FacilityLayerVersion");
            ((Newtonsoft.Json.Linq.JObject)raw["ItemZones"]).Remove("FacilityLayerMigrationId");
            var originalIds = legacy.ItemZones.Items.Select(i => i.ItemId).OrderBy(id => id).ToArray();
            bool migrated = FacilityLayerRecovery.TryMigrateLegacy(raw, legacy, out var result, out string reason);
            Assert.That(migrated, Is.EqualTo(members <= 1), reason);
            if (migrated)
            {
                Assert.That(result.ItemZones.Items.Select(i => i.ItemId).OrderBy(id => id), Is.EqualTo(originalIds));
                Assert.That(FacilityLayerQuery.ResolveSupply(result, supply).ItemIds.Count, Is.EqualTo(members));
                Assert.That(result.ItemZones.FacilityLayerMigrationId, Is.Not.Empty);
                Assert.That(FacilityLayerQuery.ResolveCity(result, 1).All(c => c.CoveredItemIds.Count == 0), Is.True);
            }
        }

        [Test] public void RecoveryRejectsMissingRawMetadataAndTamperedBuildReceipt()
        {
            var c = CommittedChoice();
            var raw = Newtonsoft.Json.Linq.JObject.FromObject(c.State);
            ((Newtonsoft.Json.Linq.JObject)raw["EffectRuntime"]["EffectNodes"][0]).Remove("RequiredOutcome");
            Assert.That(FacilityLayerRecovery.TryValidateRaw(raw, out _), Is.False);
            var build = c.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == c.BuildId);
            var facts = Value(build.ContinuationData, "buildFacts");
            facts.Properties.Single(p => p.Name == "scoreReceiptId").Value = NormalizedValue.CreateString("forged");
            Assert.That(FacilityLayerRecovery.TryValidateState(c.State, Registry, out _), Is.False);
        }
    }
}

