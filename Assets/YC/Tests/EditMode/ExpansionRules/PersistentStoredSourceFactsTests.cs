using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 66/P02-P04通用协议隔离。私有配方/探针明确登记，不计正常牌池、正常对局或UI验收。
    public sealed class PersistentStoredSourceFactsTests
    {
        private const string Ability = "operator.dorothy.tactic";
        private const string Script = "lua/characters/operator.dorothy.tactic.lua";
        private const string Installed = "Assets/StreamingAssets/Content/core";

        [Test]
        public void RealPersistentObserverFactsRequireRegisteredVersionAndTrustedContext()
        {
            using var f = new DoublePersistentFixture(false);
            var find = typeof(EffectRegistry).GetMethod("FindEventHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var actual = (EffectEventHandlerRegistration)find.Invoke(f.Registry, new object[] { Ability + ".persistent." + InfluenceEventTypeIds.InfluenceRemoved });
            Assert.That(actual, Is.Not.Null);
            var invoke = typeof(SourceFactsLuaCatalog).GetMethod("ForInvocation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            string scriptSource = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), Installed)).GetScript(Script);
            Assert.That(LuaContentHasher.ComputeSha256(scriptSource), Is.EqualTo(actual.ContentHash));
            const string eventType = "QY002ObserverVersionProbe";
            int observed = 0;
            // 仅隔离探针事件；生产RegisterPersistentObserver从真实激活实例产生scoped上下文和受信token。
            CharacterLifecycleService.RegisterPersistentObserver(f.Registry, Ability, eventType, actual.DefinitionVersion, actual.ContentHash, context =>
            {
                Assert.That(CharacterLifecycleService.TryResolvePersistentSourceFacts(f.Registry, context, Ability, actual.ContentHash, out var source), Is.True);
                Assert.That(context.Registration.DefinitionVersion, Is.EqualTo(actual.DefinitionVersion));
                Assert.That(ReferenceEquals(context.Registration, find.Invoke(f.Registry, new object[] { Ability + ".persistent." + eventType })), Is.True);
                LuaHandlerDefinition Definition(string version, string hash) => new LuaHandlerDefinition(context.Registration.SubscriptionId, eventType, "*",
                    new LuaScriptDefinition("operator.dorothy", Ability, actual.HandlerId, version, scriptSource, hash), EffectHandlerRole.Observer, abilityId: Ability);
                LuaSnapshotValue Facts(LuaHandlerDefinition definition, EffectEventHandlerContext call, int player) =>
                    (LuaSnapshotValue)invoke.Invoke(null, new object[] { call.State, f.Registry, definition, call, player });
                var valid = Definition(actual.DefinitionVersion, actual.ContentHash);
                string before = RuleStateDigestService.Compute(context.State);
                var snapshot = Facts(valid, context, source.AuthorizedPlayerId);
                Assert.That(snapshot.Kind, Is.EqualTo(LuaSnapshotValueKind.Object));
                Assert.That(snapshot.Get("ruleOwnerSourceKey").StringValue, Is.EqualTo(source.SourceKey));
                // 保同一个真实受信context及hash，单改处理器版本；这项专门命中恢复的版本guard。
                Assert.That(Facts(Definition(actual.DefinitionVersion + ".wrong", actual.ContentHash), context, source.AuthorizedPlayerId).Kind, Is.EqualTo(LuaSnapshotValueKind.Null));
                Assert.That(CharacterLifecycleService.TryResolvePersistentSourceFacts(f.Registry, context, Ability, actual.ContentHash, out _), Is.True);
                Assert.That(Facts(Definition(actual.DefinitionVersion, new string('0', 64)), context, source.AuthorizedPlayerId).Kind, Is.EqualTo(LuaSnapshotValueKind.Null));
                Assert.That(Facts(valid, new EffectEventHandlerContext(context.State, context.Event, context.OwnerNode, context.Registration), source.AuthorizedPlayerId).Kind,
                    Is.EqualTo(LuaSnapshotValueKind.Null), "重建同字段context没有生产登记的受信token");
                Assert.That(Facts(valid, context, source.AuthorizedPlayerId + 1).Kind, Is.EqualTo(LuaSnapshotValueKind.Null));
                Assert.That(RuleStateDigestService.Compute(context.State), Is.EqualTo(before));
                f.Evidence.WriteLabeledText("002真实Observer版本与受信上下文", Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    actual.DefinitionVersion, actual.ContentHash, source.SourceKey, context.Registration.SubscriptionId,
                    valid = true, wrongVersionRejected = true, wrongHashRejected = true, copiedContextRejected = true, otherPlayerRejected = true }));
                observed++;
                return new List<EffectSpec>();
            });
            f.Registry.Register("qy002.observer.version.signal", context => EffectStepResult.Completed().AddEvent(new EffectEventRequest {
                EventType = eventType, PlayerId = 1, OwnerNodeId = context.Node.EffectId, SourceEffectId = context.Node.EffectId, Payload = Object() }));
            f.Engine.CreateRoot(EffectSpec.Create("qy002.observer.version.signal", playerId: 1)); f.Pump();
            Assert.That(observed, Is.EqualTo(2), "两个来源必须各自用生产激活收据接受/拒绝同一组版本边界");
        }

        [TestCase("original")][TestCase("missing_queued")][TestCase("cross_source")][TestCase("missing_active_copy")][TestCase("nested_cross_source")]
        public void RealQueuedPersistentBodiesValidateTheirOwnStoredSource(string variant)
        {
            using var f = new DoublePersistentFixture(false);
            var responses = f.StartDeparture();
            var active = responses.Single(r => r.Status == DeferredResponseStatus.Pending);
            var queued = responses.Single(r => r.Status == DeferredResponseStatus.Queued);
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == queued.ResponseRootEffectId).ChildEffectIds, Is.Empty);
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State)));
            var rawResponses = (JArray)archive["Snapshot"]["State"]["EffectRuntime"]["DeferredResponses"];
            var rawActive = rawResponses.Single(r => (string)r["DeferredResponseId"] == active.DeferredResponseId);
            var rawQueued = rawResponses.Single(r => (string)r["DeferredResponseId"] == queued.DeferredResponseId);
            if (variant == "missing_queued") rawQueued["PendingBody"][0]["PersistentSourceFacts"] = null;
            if (variant == "missing_active_copy") rawActive["PendingBody"][0]["PersistentSourceFacts"] = null;
            if (variant == "cross_source") rawQueued["PendingBody"][0]["PersistentSourceFacts"] = rawActive["PendingBody"][0]["PersistentSourceFacts"].DeepClone();
            if (variant == "nested_cross_source") rawQueued["PendingBody"][0]["NestedEffects"][0]["PersistentSourceFacts"] = rawActive["PendingBody"][0]["PersistentSourceFacts"].DeepClone();
            f.Evidence.WriteLabeledText("005保存正文-" + variant, archive.ToString());
            var restored = HostRecoveryService.RecoverJson(archive.ToString(), f.Registry);
            Assert.That(restored.Succeeded, Is.EqualTo(variant == "original"), restored.Diagnostic);
            if (variant != "original")
            {
                Assert.That(restored.Diagnostic, Does.Contain(variant.StartsWith("missing", StringComparison.Ordinal) ? "source_facts_binding_missing" : "source_facts_binding_invalid"));
                return;
            }
            f.State = restored.State;
            f.AnswerResponse(active.DeferredResponseId, "decline");
            f.Restore("005第一响应完成第二响应待答");
            Assert.That(f.State.EffectRuntime.DeferredResponses.Single(r => r.DeferredResponseId == queued.DeferredResponseId).Status, Is.EqualTo(DeferredResponseStatus.Pending));
            f.AnswerResponse(queued.DeferredResponseId, "store");
            f.Restore("005第二响应存入后恢复"); f.Pump();
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == f.DepartedItem).ZoneId, Is.EqualTo(f.Storage(queued.SourceCardInstanceId)));
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Count(d => d.ItemId == f.DepartedItem), Is.EqualTo(1));
        }

        [TestCase("original")][TestCase("revoke_active")]
        public void TwoRealPersistentContinuationsCannotBorrowAnotherSource(string variant)
        {
            using var f = new DoublePersistentFixture(true);
            var responses = f.StartDeparture();
            var active = responses.Single(r => r.Status == DeferredResponseStatus.Pending);
            var queued = responses.Single(r => r.Status == DeferredResponseStatus.Queued);
            Assert.That(active.PendingBody[0].PersistentSourceFacts.SourceRef.SourceKey,
                Is.Not.EqualTo(queued.PendingBody[0].PersistentSourceFacts.SourceRef.SourceKey));
            if (variant == "revoke_active")
            {
                // 只撤销一个实际use的订阅作为负例；不改另一来源、不补造任何来源/收据。
                var archive = HostRecoveryService.CreateArchive(f.State);
                var binding = active.PendingBody[0].PersistentSourceFacts;
                archive.Snapshot.State.CharacterUses.Single(u => u.UseId == binding.UseId).Modes.Single(m => m.AbilityId == Ability).SubscriptionActive = false;
                string json = HostRecoveryService.SerializeArchive(archive);
                f.Evidence.WriteLabeledText("005单来源撤销负例", json);
                var recovered = HostRecoveryService.RecoverJson(json, f.Registry);
                Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); f.State = recovered.State;
                Assert.That(f.Registry.IsCurrentSourceFactsSource(f.State, queued.PendingBody[0].PersistentSourceFacts.SourceRef), Is.True);
                var request = f.ResponseRequest(active.DeferredResponseId);
                Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                    NormalizedValue.CreateString("decline"), out var reason), Is.True, reason);
                Assert.That(f.Engine.RunUntilQuiescent().Faulted, Is.True);
                Assert.That(f.State.EffectRuntime.LastFaultMessage, Does.Contain("source_facts_unavailable"));
                Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == queued.ResponseRootEffectId).ChildEffectIds, Is.Empty);
                return;
            }
            f.Restore("005双来源续接前");
            var oldRequest = f.ResponseRequest(active.DeferredResponseId);
            f.AnswerResponse(active.DeferredResponseId, "decline");
            f.Restore("005两来源之间续接恢复");
            string digest = RuleStateDigestService.Compute(f.State);
            Assert.That(f.Engine.TrySubmitInteraction(oldRequest.GetStableInteractionId(), oldRequest.AnsweringPlayerId,
                oldRequest.StateRevision, NormalizedValue.CreateString("decline"), out _), Is.False);
            Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(digest));
            f.AnswerResponse(queued.DeferredResponseId, "store");
            f.Restore("005两来源均完成后恢复"); f.Pump();
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == f.DepartedItem).ZoneId, Is.EqualTo(f.Storage(queued.SourceCardInstanceId)));
            var completed = f.State.EffectRuntime.EffectNodes.Where(n => n.ContinuationContext != null &&
                Text(n.ContinuationContext, "stage") == "qy005.stored.probe").ToList();
            Assert.That(completed.Count, Is.EqualTo(2));
            foreach (var node in completed) Assert.That(f.State.EffectRuntime.DispatchReceipts.Count(r =>
                r.EventId == node.CompletedEventId && r.RouteTier == (int)EffectRouteTier.Targeted && r.Succeeded), Is.EqualTo(1));
        }

        [Test]
        public void PersistentPlaceRewriteRestoresAndCompletesOneRealPlacement()
        {
            // 正式多萝西Removed正文是存放Choice，不是放置根；仅在私有Lua加通用Place探针。
            // 保罗放置改写仍使用正式Lua/来源，所有持续凭据由真实激活/observer自动生成。
            using var f = new PaulContentTestFixture(sourceOverrides: root => new Dictionary<string, string> {
                [Script] = "local original=(function()\n" + File.ReadAllText(Path.Combine(root, Script)) + @"
end)()
return function(ctx)
    if ctx.eventType == 'InfluenceRemoved' then
        return {Effect.PlaceInfluence({executingPlayer=ctx.playerId,influenceSource='player_supply',ownerSubject=ctx.playerId,causeKind='other_intrinsic_flow'})}
    end
    return original(ctx)
end" });
            var card = f.State.ItemZones.Items.Single(i => i.DefinitionId == "operator.dorothy");
            foreach (var order in f.State.ItemZones.Orders) order.ItemIds.Remove(card.ItemId);
            card.ZoneId = FindZone(f.State, "hand", 1).ZoneId; card.SlotId = "";
            card.OwnerSubject = ItemSubjectRef.Player(1); card.ControllerSubject = ItemSubjectRef.Player(1);
            f.State.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == card.ZoneId)?.ItemIds.Add(card.ItemId);
            f.Artifacts.WriteLabeledText("005改写隔离输入说明", "把牌池中的真实多萝西实例设为隔离初始手牌，随后真实覆盖、使用与激活；私有Lua仅在Removed返回Place根以覆盖字段传播，不能代算正式卡面规则或正常局。");
            string coverRoot = f.Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); f.Answer(f.Request(coverRoot), card.ItemId);
            string useRoot = f.Run(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic"));
            var quantity = f.Request(useRoot);
            Assert.That(quantity.AnswerSchema, Is.EqualTo("bounded_amount"));
            Assert.That(f.Engine.TrySubmitInteraction(quantity.GetStableInteractionId(), quantity.AnsweringPlayerId,
                quantity.StateRevision, NormalizedValue.CreateString("1"), out string quantityReason), Is.True, quantityReason); f.Pump();
            string departed = f.Place(f.OtherLocation, 0, type: "core.player_marker");
            f.Run(InfluenceEffectSpecFactory.RemoveInfluence(2, departed));
            var mapping = f.State.EffectRuntime.InfluencePlaceMappings.Single(m => m.OriginalSpec.PersistentSourceFacts != null);
            var replacement = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == mapping.ActualRootEffectId);
            Assert.That(mapping.OriginalSpec.PersistentSourceFacts.SourceRef.ItemId, Is.EqualTo(card.ItemId));
            Assert.That(ReferenceEquals(mapping.OriginalSpec.PersistentSourceFacts, replacement.PersistentSourceFacts), Is.False);
            Assert.That(replacement.EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
            f.Restore("005持久放置改写Choice恢复");
            var choice = f.Request(mapping.ActualRootEffectId); f.Answer(choice, "gang");
            f.Restore("005改写类型已选地图待答");
            var placement = f.Request(mapping.ActualRootEffectId);
            int before = f.State.Map.Influences.Count;
            f.Answer(placement, placement.CandidateIds.First()); f.Restore("005改写实际放置后恢复"); f.Pump();
            Assert.That(f.State.Map.Influences.Count, Is.EqualTo(before + 1));
            var leaves = f.State.EffectRuntime.EffectNodes.Where(n => f.Within(n.EffectId, mapping.ActualRootEffectId) &&
                n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence && n.Status == EffectNodeStatus.Completed).ToList();
            Assert.That(leaves.Count, Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluencePlaced && e.SourceEffectId == leaves[0].EffectId), Is.EqualTo(1));
            Assert.That(f.Engine.TrySubmitInteraction(placement.GetStableInteractionId(), placement.AnsweringPlayerId, placement.StateRevision,
                NormalizedValue.CreateString(placement.CandidateIds.First()), out _), Is.False);
        }

        private sealed class DoublePersistentFixture : IDisposable
        {
            internal readonly IsolatedTestArtifacts Evidence = IsolatedTestArtifacts.BeginCase();
            internal readonly EffectRegistry Registry;
            internal GameState State;
            internal string DepartedItem;
            private readonly GameMapDefinition map;
            internal EffectTreeExecutor Engine => new EffectTreeExecutor(State, Registry);
            internal DoublePersistentFixture(bool continuationProbe)
            {
                var replacements = new Dictionary<string, string>();
                if (continuationProbe)
                {
                    string original = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), Installed, Script));
                    replacements[Script] = "local original=(function()\n" + original + @"
end)()
return function(ctx)
    local c=ctx.payload.continuationContext
    if ctx.eventType=='EffectCompleted' and c~=nil and c.stage=='qy005.stored.probe' then
        local facts=SourceData.GetCurrentSources({itemId=c.card,abilityId='operator.dorothy.tactic'})
        assert(facts.ruleOwnerSourceKey==c.scope and facts.totalCount==1,'qy005_cross_source')
        return {}
    end
    local effects=original(ctx)
    if ctx.eventType=='InfluenceRemoved' then
        local facts=SourceData.GetCurrentSources({itemId=ctx.payload.cardInstanceRef,abilityId='operator.dorothy.tactic'})
        for i,e in ipairs(effects) do effects[i]=Effect.WithContext(e,{protocol=1,stage='qy005.stored.probe',card=ctx.payload.cardInstanceRef,scope=facts.ruleOwnerSourceKey}) end
    end
    return effects
end";
                }
                string input = Evidence.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), Installed), "005双持续来源隔离内容", replacements);
                map = StaticMapDefinitions.CreateThreePlayerMap();
                var configuration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, EnabledExpansionIds = new List<string> { "core", "enterprise" } };
                var pack = ExternalContentPack.Load(input).CreateActiveSnapshot(configuration); configuration.ContentPackHash = pack.ContentHash;
                Evidence.RecordContent("005实际活动输入", input, pack.ContentHash);
                Evidence.WriteLabeledText("005隔离配方及探针说明", "同玩家两张多萝西由真实实例工厂按隔离配方创建，逐张覆盖/激活；不声称正式牌池可自然产生组合。P02保原Lua；P03私有包装仅为原存放Choice加SourceData续接探针，不改存放/放弃分支。所有事件、订阅、凭据和receipt走生产构树，无手工补造。probe=" + continuationProbe);
                Registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
                ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry); ItemTransferEffectExecutor.Register(Registry);
                CharacterCoverEffectExecutor.Register(Registry); CharacterLifecycleService.Register(Registry);
                var query = new MapQueryService(map); var influence = new InfluenceService(query);
                InfluenceEffectExecutor.Register(Registry, influence); ContentPrimitiveEffectExecutor.Register(Registry, query, influence);
                CharacterCardLuaCatalog.Register(Registry, pack); CityStyleLuaCatalog.Register(Registry, pack);
                ContentLifecycleLuaCatalog.Register(Registry, pack); AbilitySourceLuaCatalog.Register(Registry, pack);
                State = new ItemZoneStateFactory().CreateForSetup(new GameState {
                    GameId = "qy005.persistent.stored.isolated", MapId = map.MapId, ContentPackHash = pack.ContentHash, RuleConfiguration = configuration,
                    Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1,
                    LaunchPlayerOrder = new List<int> { 1, 2 }, Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离甲" }, new PlayerState { PlayerId = 2, Name = "隔离乙" } },
                    ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(),
                        AbilitySources = Registry.AbilityDeclarations.ToList(), UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(),
                        CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack) }
                }, map, new[] {
                    new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "supply" },
                    new ItemRecipeEntry { EntryId = "dorothy", DefinitionId = "operator.dorothy", TypeId = "core.operator_card", Quantity = 2, PerPlayer = true, InitialZoneRole = "hand" }
                }, new[] { new ItemCardZoneDeclaration { DefinitionId = "operator.dorothy", CardZones = pack.Definitions.Single(d => d.DefinitionId == "operator.dorothy").CardZones } });
                foreach (var location in map.Locations.Where(l => !l.IsRedZone))
                    if (!State.Map.OpenLocationIds.Contains(location.LocationId)) State.Map.OpenLocationIds.Add(location.LocationId);
                foreach (string card in State.ItemZones.Items.Where(i => i.DefinitionId == "operator.dorothy" && i.OwnerSubject.PlayerId == 1).Select(i => i.ItemId).ToList())
                {
                    Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(Open(), card);
                    Run(CharacterLifecycleService.CreateUseSpec(1, card, "tactic")); Answer(Open(), "1");
                }
            }
            internal string Storage(string card) => State.ItemZones.Zones.Single(z => z.ParentId == card && z.LocalZoneId == "markers").ZoneId;
            private InteractionRequest Open() => State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            private string Run(EffectSpec spec) { string id = Engine.CreateRoot(spec); Pump(); return id; }
            internal void Pump() => Assert.That(Engine.RunUntilQuiescent().Faulted, Is.False, State.EffectRuntime.LastFaultMessage);
            private void Answer(InteractionRequest q, string value)
            { Assert.That(Engine.TrySubmitInteraction(q.GetStableInteractionId(), q.AnsweringPlayerId, q.StateRevision, NormalizedValue.CreateString(value), out string reason), Is.True, reason); Pump(); }
            internal InteractionRequest ResponseRequest(string id) => State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open" &&
                InfluenceDepartureProtocol.IsWithin(State, q.OwnerEffectId, State.EffectRuntime.DeferredResponses.Single(r => r.DeferredResponseId == id).ResponseRootEffectId));
            internal void AnswerResponse(string id, string answer) => Answer(ResponseRequest(id), answer);
            internal List<DeferredResponseState> StartDeparture()
            {
                var slots = new InfluenceService(new MapQueryService(map)).GetLegalPlacementSlotIds(State, 1);
                Assert.That(slots, Is.Not.Empty, "隔离输入必须有真实合法放置目标");
                string slot = slots.OrderBy(s => s, StringComparer.Ordinal).First();
                string place = Run(InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
                Assert.That(State.EffectRuntime.EffectNodes.Single(n => n.EffectId == place).Status, Is.EqualTo(EffectNodeStatus.Completed));
                DepartedItem = Value(State.EffectRuntime.EffectNodes.Single(n => n.EffectId == place).NormalizedResult, "influenceId").ReferenceId;
                Run(InfluenceEffectSpecFactory.RemoveInfluence(2, DepartedItem));
                var responses = State.EffectRuntime.DeferredResponses.Where(r => r.ReservedItemId == DepartedItem).ToList();
                Assert.That(responses.Count, Is.EqualTo(2));
                Assert.That(responses.Select(r => r.SourceCardInstanceId).Distinct().Count(), Is.EqualTo(2));
                return responses;
            }
            internal void Restore(string label)
            { var restored = Evidence.Recover(State, label, Registry); Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); State = restored.State; }
            public void Dispose()
            { if (State != null) Evidence.WriteLabeledText("005真实最终状态", Newtonsoft.Json.JsonConvert.SerializeObject(State)); Evidence.Finish(); }
        }
    }
}
