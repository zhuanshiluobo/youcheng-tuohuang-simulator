using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Domain.Influence;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class SourceFactsContractTests
    {
        [TestCase("valid")][TestCase("wrong_handler")][TestCase("forged_source")][TestCase("copied_owner")][TestCase("copied_event")]
        public void RealDeparturePreflightIsReadOnlyAndRejectsUnboundContexts(string damage)
        {
            using var f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03"); f.DockCity(f.Location);
            const string ability = "entrepreneur.P09.departure", subscription = "qy005.preflight.probe";
            var declaration = f.Registry.AbilityDeclarations.Single(d => d.AbilityId == ability);
            int observed = 0;
            EffectEventHandlerRegistration registration = null;
            registration = new EffectEventHandlerRegistration(subscription, InfluenceEventTypeIds.InfluenceRemoved, "", declaration.HandlerRef, context =>
            {
                var source = f.Registry.QuerySources(context.State, 1).Single(s => s.AbilityId == ability);
                var spec = AbilitySourceEffectExecutor.Create(source, context.Event, declaration.HandlerRef, false, context.State.Round);
                string before = RuleStateDigestService.Compute(context.State);
                if (damage == "wrong_handler") spec.NormalizedArguments.Properties.Single(p => p.Name == "handlerRef").Value = NormalizedValue.CreateString("unregistered.handler");
                if (damage == "forged_source") source.RuleOwnerItemId = "forged.item";
                var call = damage == "copied_owner" ? new EffectEventHandlerContext(context.State, context.Event, GameStateCloneService.DeepClone(context.OwnerNode), context.Registration) :
                    damage == "copied_event" ? new EffectEventHandlerContext(context.State, GameStateCloneService.DeepClone(context.Event), context.OwnerNode, context.Registration) : context;
                if (damage == "valid") Assert.That(f.Registry.HasEventResponseBody(call, spec, source), Is.True);
                else Assert.That(Assert.Throws<InvalidOperationException>(() => f.Registry.HasEventResponseBody(call, spec, source)).Message,
                    Is.EqualTo(damage == "wrong_handler" ? "response_preview_handler_invalid" : "response_preview_source_invalid"));
                Assert.That(RuleStateDigestService.Compute(context.State), Is.EqualTo(before)); observed++;
                return new List<EffectSpec>();
            }, EffectHandlerRole.Observer, contentInstanceId: declaration.DefinitionId, abilityId: ability,
                definitionVersion: declaration.DeclarationVersion, contentHash: f.Registry.GetAbilityHandlerHash(declaration.HandlerRef), contentId: declaration.DefinitionId);
            f.Registry.RegisterEventHandler(registration);
            string gang = f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
            int count = f.State.Map.Influences.Count(i => i.LocationId == f.Location);
            f.Run(InfluenceEffectSpecFactory.RemoveInfluence(2, gang));
            Assert.That(observed, Is.EqualTo(1));
            Assert.That(f.State.Map.Influences.Count(i => i.LocationId == f.Location), Is.EqualTo(count + 1));
            var fired = f.State.EffectRuntime.DispatchReceipts.Single(r => r.SubscriptionId == subscription);
            var historicalEvent = f.State.EffectRuntime.RuleEvents.Single(e => e.EventId == fired.EventId);
            var historicalOwner = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == historicalEvent.OwnerNodeId);
            var historicalSource = f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == ability);
            Assert.That(Assert.Throws<InvalidOperationException>(() => f.Registry.HasEventResponseBody(
                new EffectEventHandlerContext(f.State, historicalEvent, historicalOwner, registration),
                AbilitySourceEffectExecutor.Create(historicalSource, historicalEvent, declaration.HandlerRef, false, f.State.Round), historicalSource)).Message,
                Is.EqualTo("response_preview_source_invalid"));
            f.Restore("005真实离场预检与补放恢复"); f.Pump();
            Assert.That(f.State.Map.Influences.Count(i => i.LocationId == f.Location), Is.EqualTo(count + 1));
        }
        [Test] public void FormalOnceAndUnboundedSettlementProvidersExposeTheSameAuthorizedSourceDataAndRecover()
        {
            const string scriptPath = "lua/entrepreneurs/tiago.lua";
            using var f = new PaulContentTestFixture(sourceOverrides: root => {
                string source = File.ReadAllText(Path.Combine(root, scriptPath));
                foreach (string name in new[] { "TiagoInvestmentAvailability", "TiagoExchangeAvailability" })
                    source = source.Replace("function " + name + "(ctx)", "function Original" + name + "(ctx)");
                string probe = @"local function source_fact_availability(ctx)
                    assert(Global==nil);assert(Effect==nil)
                    local s=ctx.facts.source;local p=SourceData.GetCurrentSources({itemId=s.itemId,abilityId=s.abilityId})
                    assert(p.totalCount==1);assert(p.items[1].authorizedPlayerId==ctx.playerId)
                    local g=SourceData.GetGrantSources();assert(g.ruleOwnerSourceKey==p.items[1].sourceKey)
                    return {enabled=true,disabledReasonKey=''} end
                    function TiagoInvestmentAvailability(ctx) return source_fact_availability(ctx) end
                    function TiagoExchangeAvailability(ctx) return source_fact_availability(ctx) end
                    ";
                const string marker = "return function(ctx)"; Assert.That(source.Split(new[] { marker }, StringSplitOptions.None).Length, Is.EqualTo(2));
                return new Dictionary<string, string> { [scriptPath] = source.Replace(marker, probe + marker) };
            });
            f.Artifacts.WriteLabeledText("纯来源事实消费者隔离说明", "仅在已封存完整基线派生输入中用纯查询探针消费SourceData；保正式声明和manifest，不代表D卡面已回填或完成。");
            f.Choose(1, "P01");
            f.Registry.Register("source.facts.cleanup", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest {
                EventType = "PlayerCleanupStarted", PlayerId = 2, OwnerNodeId = c.Node.EffectId, SourceEffectId = c.Node.EffectId,
                Payload = ItemTransferEffectExecutor.Object() }));
            f.Run(EffectSpec.Create("source.facts.cleanup", ItemTransferEffectExecutor.Object(), 2));
            void Verify()
            {
                var window = f.State.EffectRuntime.EffectNodes.Select(n => n.SettlementWindow).Single(w => w != null &&
                    !w.IsClosed && w.Context.PlayerId == 2 && w.Context.Kind == SettlementEntryService.PlayerCleanup);
                var once = window.Entries.Single(e => e.AbilityId == "entrepreneur.D09.investment");
                var repeated = window.Entries.Single(e => e.AbilityId == "entrepreneur.D01.exchange");
                Assert.That(once.CleanupSource, Is.Null); Assert.That(repeated.CleanupSource, Is.Not.Null);
                string before = RuleStateDigestService.Compute(f.State);
                Assert.That(f.Registry.QuerySettlementEntry(f.State, window, once, true).Enabled, Is.True);
                Assert.That(f.Registry.QuerySettlementEntry(f.State, window, repeated, true).Enabled, Is.True);
                Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(before));
                f.Artifacts.WriteLabeledText("正式收尾来源事实入口", JsonConvert.SerializeObject(new { window.Context, once.AbilityId,
                    repeatedAbility = repeated.AbilityId, onceFacts = SourceFactsLuaCatalog.ForSettlement(f.State, f.Registry, SettlementEntryService.CreateQueryContext(f.State, window), once),
                    repeatedFacts = SourceFactsLuaCatalog.ForSettlement(f.State, f.Registry, SettlementEntryService.CreateQueryContext(f.State, window), repeated), pure = true }));
            }
            Verify(); f.Restore("真实once与重复收尾来源事实待答恢复"); Verify();
        }

        private static AbilitySourceRef Owner(PaulContentTestFixture f) => f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.P09.place");
        private static LuaInvocationResult Invoke(PaulContentTestFixture f, LuaSnapshotValue facts, string body, string response = "effects", int revision = -1)
        {
            string script = "return function(ctx) " + body + " end";
            var context = new LuaInvocationContext("source.facts.probe", "PureProbe", "p1", revision < 0 ? f.State.EffectRuntime.StateRevision : revision,
                "1.0.0", 2, 0, 0) { SourceFacts = facts, ResponseKind = response };
            if (response == "settlementAvailability") context.SettlementContext = new SettlementWindowContext {
                Id = "source.facts.pure.cleanup", Kind = SettlementEntryService.PlayerCleanup,
                OwnerEffectId = Owner(f).SourceKey, PlayerId = 1, StateRevision = context.StateRevision };
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("source.facts.probe", "source.facts.probe", "source.facts.probe", "1.0.0", script, LuaContentHasher.ComputeSha256(script)), context);
            f.Artifacts.WriteLabeledText("来源事实实际Lua调用", JsonConvert.SerializeObject(new { script, response, revision = context.StateRevision, result.Status, result.Diagnostic }));
            return result;
        }
        private static string Return(string response) => response == "settlementAvailability" ? "return {enabled=true,disabledReasonKey=''}" :
            response == "abilityRuleQuery" ? "return {queryId='probe',basisDigest='basis',items={}}" : "return {}";

        [TestCase(false)][TestCase(true)]
        public void SyntheticOwnerAndPayloadCannotAuthorizeSourceFacts(bool wrongPayloadItem)
        {
            using var f = new PaulContentTestFixture(); f.Choose(5, "P05"); var owner = Owner(f);
            var content = f.Pack.Definitions.Single(d => d.DefinitionId == owner.DefinitionId);
            var declaration = f.Registry.AbilityDeclarations.Single(d => d.AbilityId == owner.AbilityId);
            var handler = content.AbilityHandlers.Single(h => h.HandlerId == declaration.HandlerRef);
            string source = f.Pack.GetScript(handler.Script);
            var definition = new LuaHandlerDefinition(handler.HandlerId, "InfluencePlaceAttaching", "",
                new LuaScriptDefinition(content.DefinitionId, owner.AbilityId, handler.HandlerId, handler.Version, source, LuaContentHasher.ComputeSha256(source)),
                EffectHandlerRole.Primary, contentInstanceId: content.DefinitionId, abilityId: owner.AbilityId);
            var payload = ItemTransferEffectExecutor.Object("sourceItemId", NormalizedValue.CreateString(wrongPayloadItem ?
                f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 2).AbilityItemId : owner.ItemId));
            var eventContext = new EffectEventHandlerContext(f.State, new RuleEvent { EventId = "source.facts.real.query", EventType = definition.EventType,
                PlayerId = 1, Payload = payload }, new EffectNodeRuntimeState { EffectId = owner.SourceKey, PlayerId = 1 }, null);
            var dispatcher = new LuaEffectEventDispatcher(f.Registry);
            var method = typeof(LuaEffectEventDispatcher).GetMethod("CreateInvocationContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var context = (LuaInvocationContext)method.Invoke(dispatcher, new object[] { definition, eventContext });
            var result = Invoke(f, context.SourceFacts, "local p=SourceData.GetCurrentSources();assert(p.ruleOwnerSourceKey~='');return {};");
            Assert.That(result.IsSuccess, Is.False, result.Diagnostic);
            Assert.That(result.Diagnostic, Does.Contain("source_facts_unavailable"));
            f.Artifacts.WriteLabeledText("任意owner和payload不得取得来源scope", JsonConvert.SerializeObject(new { wrongPayloadItem, owner, handler.HandlerId,
                handlerHash = definition.Script.ContentHash, context.SourceFacts, result.Status, result.Diagnostic }));
        }

        private static AbilityActionDeclaration RegisteredAction(EffectRegistry registry, string id) =>
            (AbilityActionDeclaration)typeof(EffectRegistry).GetMethod("ActionDeclaration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(registry, new object[] { id });

        [TestCase("valid")][TestCase("unregistered_handler")][TestCase("wrong_hash")][TestCase("forged_source")]
        [TestCase("copied_action")][TestCase("body_as_preview")][TestCase("stale_source")]
        public void EntryCostFactsRequireRegisteredCurrentCallIdentity(string damage)
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "2");
            const string actionId = "entrepreneur.A08.tactic.quick";
            // 正式 QueryActions 已实际调用费用 Lua；此处再隔离检验授权边界，不注入 SourceFacts。
            string original = RuleStateDigestService.Compute(f.State);
            Assert.That(f.Action(actionId).Enabled, Is.True);
            Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(original));
            var action = RegisteredAction(f.Registry, actionId);
            var source = f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.A08.tactic");
            var call = new AbilityActionCallContext { State = f.State, SourceRef = GameStateCloneService.DeepClone(source), Action = action };
            string handler = action.EntryCostRef, hash = f.Registry.GetAbilityHandlerHash(handler);
            if (damage == "unregistered_handler") handler += ".missing";
            if (damage == "wrong_hash") hash = "unregistered.hash";
            if (damage == "forged_source") call.SourceRef.ItemId = "forged.item";
            if (damage == "copied_action") call.Action = GameStateCloneService.DeepClone(action);
            if (damage == "body_as_preview") { handler = action.BodyHandlerRef; hash = f.Registry.GetAbilityHandlerHash(handler); }
            if (damage == "stale_source") f.State.ItemZones.Items.Single(i => i.ItemId == source.ItemId).ZoneId =
                ItemTransferEffectExecutor.FindZone(f.State, "retired").ZoneId;
            string before = RuleStateDigestService.Compute(f.State);
            bool allowed = f.Registry.TryResolveSourceFactsCall(call, handler, hash, out var resolved, out bool callable);
            Assert.That(allowed, Is.EqualTo(damage == "valid")); Assert.That(callable, Is.False);
            Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(before));
            f.Artifacts.WriteLabeledText("费用来源身份拒绝对照", JsonConvert.SerializeObject(new { damage, handler, hash, allowed, callable, resolved }));
        }

        [Test]
        public void RealActionCostAndBodyFactsUseSeparateCommitPhases()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "2");
            var option = f.Action("entrepreneur.A08.tactic.quick"); var action = RegisteredAction(f.Registry, option.ActionId);
            string id = f.Engine.CreateRoot(AbilityActionExecutor.Create(option, "qy005.source.phase", ItemTransferEffectExecutor.Object()));
            bool accepted = false, activated = false;
            for (int step = 0; step < 2000 && !activated; step++)
            {
                var invocation = f.State.EffectRuntime.AbilityActionInvocations.SingleOrDefault(i => i.OwnerEffectId == id);
                if (invocation != null && (invocation.Phase == "accepted" || invocation.Phase == "activated"))
                {
                    var call = new AbilityActionCallContext { State = f.State, OwnerNode = f.Engine.GetNode(id), SourceRef = invocation.SourceRef, Action = action };
                    string before = RuleStateDigestService.Compute(f.State);
                    bool cost = f.Registry.TryResolveSourceFactsCall(call, action.EntryCostRef, f.Registry.GetAbilityHandlerHash(action.EntryCostRef), out _, out bool costCallable);
                    bool body = f.Registry.TryResolveSourceFactsCall(call, action.BodyHandlerRef, f.Registry.GetAbilityHandlerHash(action.BodyHandlerRef), out _, out bool bodyCallable);
                    Assert.That(cost, Is.EqualTo(invocation.Phase == "accepted")); Assert.That(costCallable, Is.False);
                    Assert.That(body, Is.EqualTo(invocation.Phase == "activated")); Assert.That(bodyCallable, Is.EqualTo(body));
                    Assert.That(invocation.ActivationCommitted, Is.EqualTo(body));
                    Assert.That(f.Stored, Is.EqualTo(body ? 0 : 2));
                    Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(before));
                    call.OwnerNode = GameStateCloneService.DeepClone(call.OwnerNode);
                    Assert.That(f.Registry.TryResolveSourceFactsCall(call, cost ? action.EntryCostRef : action.BodyHandlerRef,
                        f.Registry.GetAbilityHandlerHash(cost ? action.EntryCostRef : action.BodyHandlerRef), out _, out _), Is.False, "同名owner副本不是正在执行的节点");
                    f.Artifacts.WriteLabeledText("真实行动阶段与来源权限", JsonConvert.SerializeObject(new { invocation.Phase, invocation.ActivationCommitted, cost, body, f.Stored }));
                    accepted |= cost; activated |= body;
                    if (activated) break;
                }
                if (f.Engine.Advance()) continue;
                Assert.That(f.State.EffectRuntime.LastFaultMessage, Is.Empty);
                var confirmation = f.State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
                Assert.That(InfluenceDepartureProtocol.IsWithin(f.State, confirmation.OwnerEffectId, id), Is.True);
                Assert.That(confirmation.InteractionTypeId, Is.EqualTo("action.decline_effect"));
                Assert.That(confirmation.CandidateIds, Does.Contain("continue"));
                // 回答当前正式外层确认后逐步推进，不能Pump越过原阶段/付款/越权断言。
                Assert.That(f.Engine.TrySubmitInteraction(confirmation.GetStableInteractionId(), confirmation.AnsweringPlayerId,
                    confirmation.StateRevision, NormalizedValue.CreateString("continue"), out string reason), Is.True, reason);
            }
            Assert.That(accepted, Is.True); Assert.That(activated, Is.True);
            f.Pump(); f.ResolveMapRequests();
            Assert.That(f.Stored, Is.Zero); Assert.That(f.OwnMapCount, Is.EqualTo(2));
        }

        [Test] public void RealHostRecoveryPreservesSourceFactsAndARealGrantRevocationInvalidatesTheOldProof()
        {
            using var f = new PaulContentTestFixture(); f.Choose(5, "P05");
            var original = Owner(f); var before = SourceFactsLuaCatalog.Create(f.State, f.Registry, original);
            f.Restore("来源事实实际Host恢复");
            var restored = SourceFactsLuaCatalog.Create(f.State, f.Registry, Owner(f));
            Assert.That(JsonConvert.SerializeObject(restored), Is.EqualTo(JsonConvert.SerializeObject(before)));
            f.Registry.Register("source.facts.revoke", c => {
                AbilitySourceSelection.GrantSharedRules(c, original.ItemId, Enumerable.Empty<AbilitySourceRef>());
                return EffectStepResult.Completed();
            });
            f.Run(EffectSpec.Create("source.facts.revoke", ItemTransferEffectExecutor.Object(), 1));
            Assert.That(SourceFactsLuaCatalog.Create(f.State, f.Registry, original).Kind, Is.EqualTo(LuaSnapshotValueKind.Null));
            var current = SourceFactsLuaCatalog.Create(f.State, f.Registry, Owner(f));
            Assert.That(current.Get("grants").Items, Is.Empty);
            var stale = Invoke(f, before, "SourceData.GetGrantSources();return {};");
            Assert.That(stale.IsSuccess, Is.False); Assert.That(stale.Diagnostic, Does.Contain("source_facts_scope_stale"));
            var fresh = Invoke(f, current, "local p=SourceData.GetGrantSources();assert(p.totalCount==0);return {};");
            Assert.That(fresh.IsSuccess, Is.True, fresh.Diagnostic);
        }

        [TestCase("effects")][TestCase("abilityRuleQuery")][TestCase("settlementAvailability")]
        public void CurrentSourcesAndExactGrantLinksArePagedReadOnlyInAllThreeSandboxes(string response)
        {
            using var f = new PaulContentTestFixture();
            f.Choose(5, "P05");
            var owner = Owner(f); var sources = f.Registry.QuerySources(f.State, 1);
            var facts = SourceFactsLuaCatalog.Create(f.State, f.Registry, owner);
            Assert.That(sources.Count, Is.GreaterThan(SourceFactsLuaCatalog.PageSize));
            var grantFacts = facts.Get("grants").Items;
            Assert.That(grantFacts.Select(s => s.Get("sourceKey").StringValue), Is.EqualTo(owner.GrantingSourceRefs));
            Assert.That(grantFacts.Count, Is.GreaterThan(0));
            foreach (var grant in grantFacts)
            {
                var actual = sources.Single(s => s.SourceKey == grant.Get("sourceKey").StringValue);
                Assert.That(grant.Get("definitionId").StringValue, Is.EqualTo(actual.DefinitionId));
                Assert.That(grant.Get("itemId").StringValue, Is.EqualTo(actual.ItemId));
                Assert.That(grant.Get("abilityOwnerItemId").StringValue, Is.EqualTo(f.AbilityItem));
                Assert.That(grant.Get("selectionReceipt").Get("requestId").StringValue, Is.Not.Empty);
            }
            string before = RuleStateDigestService.Compute(f.State);
            string checks = @"local cursor=0;local seen={};local count=0
                repeat local p=SourceData.GetCurrentSources({cursor=cursor});assert(#p.items<=4)
                for _,s in ipairs(p.items) do assert(s.authorizedPlayerId=='p1');assert(not seen[s.sourceKey]);seen[s.sourceKey]=true;count=count+1 end
                cursor=p.nextCursor until cursor==-1
                assert(count==" + sources.Count + @")
                local g=SourceData.GetGrantSources();assert(g.totalCount==" + grantFacts.Count + @")
                for _,s in ipairs(g.items) do assert(seen[s.sourceKey]);assert(s.abilityOwnerItemId~='');assert(s.selectionReceipt.requestId~='') end ";
            if (response == "settlementAvailability") checks += "assert(Global==nil);assert(Effect==nil);";
            var result = Invoke(f, facts, checks + Return(response), response);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(before));
            f.Artifacts.WriteLabeledText("来源事实同次精确关联", JsonConvert.SerializeObject(new { owner, sourceCount = sources.Count, grantCount = grantFacts.Count, facts, pure = true }));
        }

        [TestCase("wrong_definition")][TestCase("wrong_item")][TestCase("wrong_player")][TestCase("old_revision")]
        [TestCase("old_content")][TestCase("fractional_cursor")][TestCase("unknown_filter")][TestCase("write_snapshot")]
        public void ForgedOrStaleSourceFactsAreExplicitlyRejectedWithoutChangingState(string damage)
        {
            using var f = new PaulContentTestFixture();
            f.Choose(5, "P05"); var owner = Owner(f); var forged = GameStateCloneService.DeepClone(owner);
            if (damage == "wrong_definition") forged.DefinitionId = "diy.same_face_other_definition";
            if (damage == "wrong_item") forged.ItemId = f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 2).AbilityItemId;
            if (damage == "wrong_player") forged.AuthorizedPlayerId = 2;
            var facts = SourceFactsLuaCatalog.Create(f.State, f.Registry, forged);
            string body = "local p=SourceData.GetCurrentSources(" + (damage == "old_content" ? "{expectedContentHash='old'}" :
                damage == "fractional_cursor" ? "{cursor=0.5}" : damage == "unknown_filter" ? "{invented=true}" : "{}") + ");";
            if (damage == "write_snapshot") body += "p.items[1].definitionId='forged';";
            string before = RuleStateDigestService.Compute(f.State);
            var result = Invoke(f, facts, body + "return {}", revision: damage == "old_revision" ? f.State.EffectRuntime.StateRevision + 1 : -1);
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Diagnostic, Is.Not.Empty);
            Assert.That(result.Effects, Is.Empty);
            Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(before));
            f.Artifacts.WriteLabeledText("损坏来源事实拒绝", JsonConvert.SerializeObject(new { damage, original = owner, supplied = forged, result.Diagnostic, stateUnchanged = true }));
        }
    }
}
