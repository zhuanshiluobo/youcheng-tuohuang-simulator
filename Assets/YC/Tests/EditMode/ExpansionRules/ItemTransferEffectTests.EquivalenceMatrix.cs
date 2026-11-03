using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using static YC.Tests.EditMode.ExpansionRules.EnterpriseEffectTests;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        // 仅隔离等价性；精确比较业务字段和角色视图，根/收据/请求ID各自在真实节点上校验。
        public static string Api32Views(GameState s) => JsonConvert.SerializeObject(new {
            host = new ItemZoneQuery().Project(s, GameStateViewer.Host), p1 = new ItemZoneQuery().Project(s, GameStateViewer.Player(1)),
            p2 = new ItemZoneQuery().Project(s, GameStateViewer.Player(2)), spectator = new ItemZoneQuery().Project(s, GameStateViewer.Spectator)
        });
        private static NormalizedValue Api32EventPayload(GameState state, RuleEvent e)
        {
            if (e.EventType != "InteractionAnswered") return e.Payload;
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == Text(e.Payload, "requestId"));
            Assert.That(e.SourceEffectId, Is.EqualTo(request.OwnerEffectId)); Assert.That(e.OwnerNodeId, Is.EqualTo(request.OwnerEffectId));
            Assert.That(e.PlayerId, Is.EqualTo(request.AnsweringPlayerId));
            Assert.That(Value(e.Payload, "answer").ToDeterministicString(), Is.EqualTo(request.NormalizedAnswer.ToDeterministicString()));
            var copy = e.Payload.Clone(); Value(copy, "requestId").StringValue = "@this-answer-request"; return copy;
        }
        private static string Api32Observation(GameState s) => JsonConvert.SerializeObject(new {
            s.ItemZones, s.Players, s.CharacterCoverSlots, s.CharacterUses, s.FacilityCharacterBindings, s.InfluenceLocks, s.RandomRuntime,
            views = Api32Views(s),
            events = s.EffectRuntime.RuleEvents.Where(e => e.EventType != "EffectCompleted").Select(e => new { e.EventType, e.PlayerId, e.Visibility, e.ResponseKind, Payload = Api32EventPayload(s, e), e.HostOnlyPayload }),
            requests = s.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").Select(r => new { r.InteractionTypeId, r.AnsweringPlayerId, r.Visibility, r.MinSelections, r.MaxSelections, r.AllowDecline, r.AnswerSchema, r.CandidateIds })
        });
        private static List<string> Api32Trace(GameState state, EffectRegistry reg, EffectSpec spec, out string id)
        {
            var engine = new EffectTreeExecutor(state, reg); id = engine.CreateRoot(spec); return Api32DrainTrace(state, reg);
        }
        private static List<string> Api32DrainTrace(GameState state, EffectRegistry reg)
        {
            var trace = new List<string> { Api32Observation(state) }; var engine = new EffectTreeExecutor(state, reg);
            for (int step = 0; step < 2000; step++)
            {
                bool progress = engine.Advance(); Assert.That(state.EffectRuntime.LastFaultMessage, Is.Empty, state.EffectRuntime.LastFaultMessage);
                string current = Api32Observation(state); if (current != trace.Last()) trace.Add(current);
                if (!progress) { Valid(state); return trace; }
            }
            Assert.Fail("隔离步骤未在界限内静止。"); return trace;
        }
        private static void Api32Receipts(GameState state)
        {
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => ItemMovementV4.IsV4(n.EffectTypeId, n.NormalizedArguments)))
            {
                Assert.That(ItemMovementV4.TryValidateState(state, node, out var reason), Is.True, reason);
                var receipt = node.ItemMovementV4?.Receipt ?? node.CommonItemMovementV4?.Receipt;
                if (receipt == null) continue;
                Assert.That(receipt.OwnerEffectId, Is.EqualTo(node.EffectId));
                Assert.That(receipt.ArgumentsDigest, Is.EqualTo(ItemTransferService.MovementV4Digest(node.NormalizedArguments)));
                foreach (string eventId in receipt.EventIds)
                {
                    var e = state.EffectRuntime.RuleEvents.Single(x => x.EventId == eventId);
                    Assert.That(e.SourceEffectId, Is.EqualTo(node.EffectId)); Assert.That(e.OwnerNodeId, Is.EqualTo(node.EffectId));
                }
            }
        }
        private static void Api32Completion(GameState state, EffectNodeRuntimeState node)
        {
            var completion = state.EffectRuntime.RuleEvents.Single(e => e.EventType == "EffectCompleted" && e.SourceEffectId == node.EffectId);
            Assert.That(completion.OwnerNodeId, Is.EqualTo(node.EffectId));
            Assert.That(Text(completion.Payload, "effectTypeId"), Is.EqualTo(node.EffectTypeId));
            Assert.That(Text(completion.Payload, "outcome"), Is.EqualTo(node.Status == EffectNodeStatus.Completed ? "completed" : "failed"));
            Assert.That(Text(completion.Payload, "failureReason"), Is.EqualTo(node.FailureReason));
            Assert.That(Value(completion.Payload, "normalizedResult").ToDeterministicString(), Is.EqualTo(node.NormalizedResult.ToDeterministicString()));
        }
        private static GameState Api32Recover(GameState state, EffectRegistry reg, bool journal, HostSessionArchiveDto baseline = null)
        {
            var archive = journal ? baseline : HostRecoveryService.CreateArchive(state);
            Assert.That(archive, Is.Not.Null, "journal必须使用操作前的真实基线。");
            if (journal)
            {
                HostRecoveryService.AppendJournal(archive, state, state.ContentPackHash);
                Assert.That(archive.Journal.Entries.Any(e => e.StateAfter != null), Is.True, "必须存在实际新提交检查点，不能把空journal称为日志恢复。");
            }
            string raw = HostRecoveryService.SerializeArchive(archive); artifacts.WriteLabeledText("API32原始恢复输入_" + journal, raw);
            var recovered = HostRecoveryService.RecoverJson(raw, reg); Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); return recovered.State;
        }
        [TestCase("facility", "move")][TestCase("facility", "shortage")][TestCase("facility", "source")][TestCase("facility", "no_change")]
        [TestCase("character", "move")][TestCase("character", "shortage")][TestCase("character", "source")][TestCase("character", "no_change")]
        [TestCase("marker", "move")][TestCase("marker", "supply_owner")][TestCase("marker", "source")][TestCase("marker", "shortage")]
        public void Api32ExplicitDedicatedAndDirectCompareEveryObservableCommit(string kind, string variant)
        {
            var initial = State(); var reg = Registry(); PlayerMarkerOperation.Register(reg);
            initial.FindPlayer(1).UsedSpecialActionIdsThisRound.Add("must_preserve");
            string item, source, target, profile, insertion; int required = variant == "shortage" ? 2 : variant == "no_change" ? 0 : 1;
            EffectSpec dedicated;
            if (kind == "facility")
            {
                item = Ids(initial, "facility_deck")[0]; source = Zone(initial, "facility_deck").ZoneId; target = Zone(initial, "facility_supply_0").ZoneId;
                if (variant == "source") source = Zone(initial, "facility_supply_1").ZoneId;
                if (variant == "no_change") target = source;
                dedicated = ExplicitFacility(1, item, source, target, required); profile = "core.facility_card_transfer"; insertion = "top";
            }
            else if (kind == "character")
            {
                item = Ids(initial, "hand", 1)[0]; source = Zone(initial, "hand", 1).ZoneId; target = Zone(initial, "hand", 2).ZoneId;
                dedicated = CharacterMove(initial, item, target, false);
                if (variant == "source") source = Zone(initial, "discard", 1).ZoneId;
                if (variant == "no_change") target = source;
                Value(dedicated.NormalizedArguments, "expectedSource").Properties.Single(p => p.Name == "zoneId").Value.StringValue = source;
                Value(dedicated.NormalizedArguments, "destination").Properties.Single(p => p.Name == "zoneId").Value.StringValue = target;
                Value(dedicated.NormalizedArguments, "requiredCount").IntegerValue = required;
                profile = "core.character_card_transfer"; insertion = "unordered";
            }
            else
            {
                item = Ids(initial, "supply", 2)[0]; source = Zone(initial, "supply", 2).ZoneId; target = Zone(initial, "retired").ZoneId;
                if (variant == "supply_owner") target = Zone(initial, "supply", 1).ZoneId;
                if (variant == "source") source = Zone(initial, "supply", 1).ZoneId;
                var leg = MarkerLegV4(item, source, target); Value(leg, "requiredCount").IntegerValue = required;
                dedicated = DedicatedMarker(1, leg); profile = "core.player_marker_storage"; insertion = "unordered";
            }
            var direct = ItemMovementV4.TransferSpec(1, new[] { item }, source, target, insertion, required, profile);
            var left = GameStateCloneService.DeepClone(initial); var right = GameStateCloneService.DeepClone(initial);
            var beforeLeft = HostRecoveryService.CreateArchive(left); var beforeRight = HostRecoveryService.CreateArchive(right);
            var a = Api32Trace(left, reg, dedicated, out var parentId); var b = Api32Trace(right, reg, direct, out var rootId);
            observed = left;
            artifacts.WriteLabeledText("API32逐次真实可观察提交", JsonConvert.SerializeObject(new { kind, variant, dedicated = a, direct = b }));
            Assert.That(a, Is.EqualTo(b));
            var parent = left.EffectRuntime.EffectNodes.Single(n => n.EffectId == parentId); var root = right.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootId);
            Assert.That(parent.Status, Is.EqualTo(root.Status)); Assert.That(parent.FailureReason, Is.EqualTo(root.FailureReason));
            Api32Completion(left, parent); Api32Completion(right, root);
            if (root.Status == EffectNodeStatus.Completed)
                foreach (string field in new[] { "changed", "movedCount", "movedItemIds" }) Assert.That(Value(parent.NormalizedResult, field).ToDeterministicString(), Is.EqualTo(Value(root.NormalizedResult, field).ToDeterministicString()));
            Api32Receipts(left); Api32Receipts(right);
            foreach (bool journal in new[] { false, true })
            {
                var x = Api32Recover(left, reg, journal, beforeLeft); var y = Api32Recover(right, reg, journal, beforeRight); string expected = Api32Observation(x);
                new EffectTreeExecutor(x, reg).RunUntilQuiescent(); new EffectTreeExecutor(y, reg).RunUntilQuiescent();
                Assert.That(Api32Observation(x), Is.EqualTo(expected)); Assert.That(Api32Observation(y), Is.EqualTo(expected));
            }
        }
        private static string Api32LuaString(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        private static EffectSpec Api32CompilePublicMove(string args)
        {
            string code = "return function(ctx)return {Effect.MoveItem(" + args + ")} end";
            artifacts.WriteLabeledText("API32供应回底公开Lua步骤", code);
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(code), new LuaInvocationContext("api32.cycle", "isolated", "1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            return specs.Single();
        }
        [TestCase("empty")][TestCase("plain")][TestCase("stacked")]
        public void Api32ExplicitCycleMatchesCompiledPublicStepsAndActualOriginalSlot(string variant)
        {
            var initial = State(); if (variant != "empty") Fill(initial, variant == "plain" ? 2 : 1);
            string deck = Zone(initial, "facility_deck").ZoneId, firstSlot = Zone(initial, "facility_supply_0").ZoneId;
            if (variant == "stacked") Run(initial, MoveSpec(1, new[] { Ids(initial, "facility_deck")[0] }, deck, firstSlot, "top", 1));
            var slots = Enumerable.Range(0, 6).Select(i => Zone(initial, "facility_supply_" + i).ZoneId).ToList();
            Func<GameState, EffectNodeRuntimeState, bool, NormalizedValue> project = (state, move, refilled) => {
                var fact = move?.CommonItemMovementV4.Receipt.Items.Single();
                return Object("changed", NormalizedValue.CreateBoolean(move != null), "selectedItemId", NormalizedValue.CreateString(fact?.ItemId ?? ""),
                    "selectedDefinitionId", NormalizedValue.CreateString(fact == null ? "" : state.ItemZones.Items.Single(i => i.ItemId == fact.ItemId).DefinitionId),
                    "originalSupplySlotZoneId", NormalizedValue.CreateString(fact?.From.ZoneId ?? ""), "refilled", NormalizedValue.CreateBoolean(refilled),
                    "reasonCode", NormalizedValue.CreateString(move == null ? "no_target" : ""));
            };
            if(variant=="empty")
            {
                var empty=EffectSpec.Create(LuaDomainEffectTypeIds.Choice,Object("player",NormalizedValue.CreateString("1"),"options",Strings(Array.Empty<string>()),"interactionType",NormalizedValue.CreateString("facility.supply.select")),1);
                var emptyRegistry=Registry();GenericLuaEffectExecutor.Register(emptyRegistry);var node=Run(initial,empty,emptyRegistry);
                Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(initial.EffectRuntime.InteractionRequests,Is.Empty);Assert.That(initial.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==LuaDomainEffectTypeIds.MoveFacilityCard),Is.Empty);Restore(initial,emptyRegistry);return;
            }
            string selected=FacilityLayerQuery.Supply(initial).First(slot=>slot.ItemIds.Count>0).ItemIds[0];
            string selectedZone=initial.ItemZones.Items.Single(i=>i.ItemId==selected).ZoneId;
            var reg = Registry(); reg.Register("fixture.api32.cycle", c => {
                if(c.Node.FlowStage=="")
                    return EffectStepResult.Continue("move").AddChild(Api32CompilePublicMove("{apiVersion=4,player=ctx.playerId,operation='transfer',selector={mode='explicit',itemIds={"+Api32LuaString(selected)+"},expectedSource={zoneId="+Api32LuaString(selectedZone)+"}},destination={zoneId="+Api32LuaString(deck)+"},insertion={kind='bottom'},operationProfileId='core.facility_card_transfer',requiredCount=1}"));
                var children = c.Node.ChildEffectIds.Select(id => c.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)).ToList();
                var failed = children.FirstOrDefault(n => n.Status == EffectNodeStatus.Failed); if (failed != null) return EffectStepResult.Failed(failed.FailureReason);
                if (children.Any(n => n.Status != EffectNodeStatus.Completed)) return EffectStepResult.NoProgress("等待公开供应步骤。");
                string original = children[0].CommonItemMovementV4.Receipt.Items.Single().From.ZoneId;
                if (c.Node.FlowStage == "move" && !c.State.ItemZones.Items.Any(i => i.ZoneId == original))
                    return EffectStepResult.Continue("refill").AddChild(Api32CompilePublicMove("{apiVersion=4,player=ctx.playerId,operation='transfer',selector={mode='top',zoneId=" + Api32LuaString(deck) + ",itemTypeId='core.facility_card',count=1},destination={zoneId=" + Api32LuaString(original) + ",mustBeEmpty=true},insertion={kind='top'},operationProfileId='core.facility_card_transfer',requiredCount=0}"));
                return EffectStepResult.Completed(project(c.State, children[0], children.Count == 2 && Flag(children[1].NormalizedResult, "changed")));
            }, EffectExecutorKind.IntrinsicFlow);
            var left = GameStateCloneService.DeepClone(initial); var right = GameStateCloneService.DeepClone(initial);
            var baselines = new[] { HostRecoveryService.CreateArchive(left), HostRecoveryService.CreateArchive(right) };
            var a = Api32Trace(left, reg, ExplicitCycleV4(left,selected), out var leftRoot); var b = Api32Trace(right, reg, EffectSpec.Create("fixture.api32.cycle", playerId: 1), out var rightRoot); Assert.That(a, Is.EqualTo(b));
            Assert.That(left.EffectRuntime.InteractionRequests,Is.Empty);Assert.That(right.EffectRuntime.InteractionRequests,Is.Empty);
            var x = left.EffectRuntime.EffectNodes.Single(n => n.EffectId == leftRoot); var y = right.EffectRuntime.EffectNodes.Single(n => n.EffectId == rightRoot);
            Assert.That(x.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(y.Status, Is.EqualTo(x.Status)); Assert.That(x.NormalizedResult.ToDeterministicString(), Is.EqualTo(y.NormalizedResult.ToDeterministicString()));
            Assert.That(Flag(x.NormalizedResult, "refilled"), Is.EqualTo(variant == "plain")); Assert.That(Api32Observation(left), Is.EqualTo(Api32Observation(right)));
            Api32Completion(left, x); Api32Completion(right, y); int side = 0;
            foreach (var state in new[] { left, right })
            {
                Api32Receipts(state); string once = Api32Observation(state); var restored = Api32Recover(state, reg, true, baselines[side++]);
                new EffectTreeExecutor(restored, reg).RunUntilQuiescent(); Assert.That(Api32Observation(restored), Is.EqualTo(once)); Assert.That(JsonConvert.SerializeObject(restored.RandomRuntime), Is.EqualTo(JsonConvert.SerializeObject(initial.RandomRuntime)));
            }
            observed = left;
        }
        [TestCase(0)][TestCase(2)][TestCase(9)]
        public void Api32FullRefreshMatchesExecutablePublicLuaAtEveryMoveAndOneIndependentShuffle(int count)
        {
            var initial = State(count); Fill(initial, Math.Min(6, count)); string deck = Zone(initial, "facility_deck").ZoneId;
            var calls = new List<string>(); var slots = Enumerable.Range(0, 6).Select(i => Zone(initial, "facility_supply_" + i).ZoneId).ToList();
            foreach (string slot in slots) foreach (string item in new ItemZoneQuery().GetItemIds(initial, slot))
                calls.Add("Effect.MoveItem({apiVersion=4,player=ctx.playerId,operation='transfer',selector={mode='explicit',itemIds={" + Api32LuaString(item) + "},expectedSource={zoneId=" + Api32LuaString(slot) + "}},destination={zoneId=" + Api32LuaString(deck) + "},insertion={kind='bottom'},operationProfileId='core.facility_card_transfer',requiredCount=1})");
            calls.Add("Effect.ShuffleDeck({apiVersion=1,player=ctx.playerId,deckZone={zoneId=" + Api32LuaString(deck) + "}})");
            foreach (string slot in slots) calls.Add("Effect.MoveItem({apiVersion=4,player=ctx.playerId,operation='transfer',selector={mode='top',zoneId=" + Api32LuaString(deck) + ",itemTypeId='core.facility_card',count=1},destination={zoneId=" + Api32LuaString(slot) + ",mustBeEmpty=true},insertion={kind='top'},operationProfileId='core.facility_card_transfer',requiredCount=0})");
            string code = "return function(ctx)return {" + string.Join(",", calls) + "} end"; artifacts.WriteLabeledText("API32完整刷新可执行Lua", code);
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(code), new LuaInvocationContext("api32.refresh", "isolated", "1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var reg = Registry(); reg.Register("fixture.api32.refresh", c => {
                if (c.Node.FlowStage == "") return EffectStepResult.Continue("steps").AddChildren(specs);
                var children = c.Node.ChildEffectIds.Select(id => c.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)).ToList();
                var failed = children.FirstOrDefault(n => n.Status == EffectNodeStatus.Failed);
                if (failed != null) return EffectStepResult.Failed(failed.FailureReason);
                return children.Any(n => n.Status != EffectNodeStatus.Completed) ? EffectStepResult.NoProgress("等待公开步骤完成。") : EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true)));
            }, EffectExecutorKind.IntrinsicFlow);
            var left = GameStateCloneService.DeepClone(initial); var right = GameStateCloneService.DeepClone(initial);
            var baselines = new[] { HostRecoveryService.CreateArchive(left), HostRecoveryService.CreateArchive(right) };
            var a = Api32Trace(left, reg, RefreshSpec(1), out var rootLeft); var b = Api32Trace(right, reg, EffectSpec.Create("fixture.api32.refresh", playerId: 1), out var rootRight);
            observed = left;
            artifacts.WriteLabeledText("API32完整刷新逐次真实可观察提交", JsonConvert.SerializeObject(new { count, dedicated = a, direct = b })); Assert.That(a, Is.EqualTo(b));
            int side = 0; foreach (var s in new[] { left, right })
            {
                Assert.That(s.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == DeckShuffleEffectExecutor.TypeId), Is.EqualTo(1));
                Assert.That(s.ItemZones.Items.Count, Is.EqualTo(initial.ItemZones.Items.Count)); Api32Receipts(s);
                string expected = Api32Observation(s); var restored = Api32Recover(s, reg, true, baselines[side++]); new EffectTreeExecutor(restored, reg).RunUntilQuiescent(); Assert.That(Api32Observation(restored), Is.EqualTo(expected));
            }
            Assert.That(Value(left.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootLeft).NormalizedResult, "changed").BooleanValue, Is.True);
            Assert.That(Value(right.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootRight).NormalizedResult, "changed").BooleanValue, Is.True);
            Api32Completion(left, left.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootLeft)); Api32Completion(right, right.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootRight));
        }
        [TestCase(0)][TestCase(1)][TestCase(9)][TestCase(-1)]
        public void Api32RefillMatchesPublicTopIncludingEmptyAndOccupiedSlot(int count)
        {
            var initial = State(count < 0 ? 9 : count); if (count < 0) Fill(initial, 1);
            var reg = Registry(); string slot = Zone(initial, "facility_supply_0").ZoneId;
            var direct = CommonV4(TopV4(initial), slot, "top", 0);
            Value(direct.NormalizedArguments, "destination").Properties.Add(new NormalizedValueEntry { Name = "mustBeEmpty", Value = NormalizedValue.CreateBoolean(true) });
            Value(direct.NormalizedArguments, "destination").Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            var left = GameStateCloneService.DeepClone(initial); var right = GameStateCloneService.DeepClone(initial);
            var beforeLeft = HostRecoveryService.CreateArchive(left); var beforeRight = HostRecoveryService.CreateArchive(right);
            string random = JsonConvert.SerializeObject(initial.RandomRuntime);
            var a = Api32Trace(left, reg, RefillSpec(1, slot), out var parentId); var b = Api32Trace(right, reg, direct, out var rootId); observed = left;
            Assert.That(a, Is.EqualTo(b)); Assert.That(JsonConvert.SerializeObject(left.RandomRuntime), Is.EqualTo(random));
            var parent = left.EffectRuntime.EffectNodes.Single(n => n.EffectId == parentId); var root = right.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootId);
            Assert.That(parent.Status, Is.EqualTo(root.Status)); Assert.That(parent.FailureReason, Is.EqualTo(root.FailureReason));
            if (count < 0) Assert.That(parent.Status, Is.EqualTo(EffectNodeStatus.Failed));
            else foreach (string field in new[] { "changed", "movedCount", "movedItemIds" }) Assert.That(Value(parent.NormalizedResult, field).ToDeterministicString(), Is.EqualTo(Value(root.NormalizedResult, field).ToDeterministicString()));
            Api32Completion(left, parent); Api32Completion(right, root); Api32Receipts(left); Api32Receipts(right);
            Assert.That(Api32Observation(Api32Recover(left, reg, true, beforeLeft)), Is.EqualTo(Api32Observation(Api32Recover(right, reg, true, beforeRight))));
        }
        [TestCase(0)][TestCase(1)][TestCase(9)]
        public void Api32IndependentShuffleAliasKeepsSameRandomStateAndNeverRollsOnRecovery(int count)
        {
            var initial = State(count); var reg = Registry(); var left = GameStateCloneService.DeepClone(initial); var right = GameStateCloneService.DeepClone(initial);
            var beforeLeft = HostRecoveryService.CreateArchive(left); var beforeRight = HostRecoveryService.CreateArchive(right);
            var alias = EffectSpec.Create(LuaDomainEffectTypeIds.ShuffleFacilityDeck, Shuffle(initial).NormalizedArguments.Clone(), 1);
            Assert.That(Api32Trace(left, reg, Shuffle(initial), out var a), Is.EqualTo(Api32Trace(right, reg, alias, out var b)));
            observed = left;
            var first = left.EffectRuntime.EffectNodes.Single(n => n.EffectId == a); var second = right.EffectRuntime.EffectNodes.Single(n => n.EffectId == b);
            Assert.That(first.ShuffleInputItemIds, Is.EqualTo(second.ShuffleInputItemIds)); Assert.That(first.NormalizedResult.ToDeterministicString(), Is.EqualTo(second.NormalizedResult.ToDeterministicString()));
            var stream = left.RandomRuntime.Streams.SingleOrDefault(s => s.StreamId == GameplayRandomRuntime.DeckShuffle);
            Assert.That(stream?.RequestsConsumed ?? 0, Is.EqualTo((ulong)Math.Max(0, count - 1)));
            Api32Completion(left, first); Api32Completion(right, second);
            var x = Api32Recover(left, reg, true, beforeLeft); var y = Api32Recover(right, reg, true, beforeRight);
            string expected = Api32Observation(x); new EffectTreeExecutor(x, reg).RunUntilQuiescent(); new EffectTreeExecutor(y, reg).RunUntilQuiescent();
            Assert.That(Api32Observation(x), Is.EqualTo(expected)); Assert.That(Api32Observation(y), Is.EqualTo(expected));
        }
        [TestCase(false)][TestCase(true)]
        public void Api32CharacterTransferWaitsForSameRequiredResponseAndRepeatedAnswerDoesNotReplay(bool journal)
        {
            var initial = State(); var reg = Registry(); string item = Ids(initial, "hand", 1)[0];
            reg.Register("fixture.api32.ack", c => c.Node.FlowStage == "" ? EffectStepResult.Continue("acknowledged").AddInteraction(new EffectInteractionSpec {
                InteractionTypeId = "fixture.api32.ack", AnsweringPlayerId = 2, Visibility = "owner", MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id", CandidateIds = { "confirm" }
            }) : EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow);
            reg.RegisterEventHandler(new EffectEventHandlerRegistration("fixture.api32.ack", "character.transferred", "*", "fixture.api32.ack", c => new[] { EffectSpec.Create("fixture.api32.ack", playerId: 2) }, role: EffectHandlerRole.Observer));
            var left = GameStateCloneService.DeepClone(initial); var right = GameStateCloneService.DeepClone(initial);
            var beforeLeft = HostRecoveryService.CreateArchive(left); var beforeRight = HostRecoveryService.CreateArchive(right);
            var a = Api32Trace(left, reg, CharacterMove(initial, item, Zone(initial, "hand", 2).ZoneId, false), out var rootLeft);
            var b = Api32Trace(right, reg, ItemMovementV4.TransferSpec(1, new[] { item }, Zone(initial, "hand", 1).ZoneId, Zone(initial, "hand", 2).ZoneId, required: 1, profile: "core.character_card_transfer"), out var rootRight);
            Assert.That(a, Is.EqualTo(b)); Assert.That(left.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootLeft).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            left = Api32Recover(left, reg, journal, beforeLeft); right = Api32Recover(right, reg, journal, beforeRight);
            foreach (var state in new[] { left, right })
            {
                var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open"); Assert.That(request.AnsweringPlayerId, Is.EqualTo(2)); Assert.That(request.Visibility, Is.EqualTo("owner"));
                var engine = new EffectTreeExecutor(state, reg); Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision, NormalizedValue.CreateString("confirm"), out var reason), Is.True, reason);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False); string once = Api32Observation(state);
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision, NormalizedValue.CreateString("confirm"), out _), Is.False);
                engine.RunUntilQuiescent(); Assert.That(Api32Observation(state), Is.EqualTo(once)); Api32Receipts(state);
                Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.transferred"), Is.EqualTo(1));
            }
            observed = left; Assert.That(Api32Observation(left), Is.EqualTo(Api32Observation(right)));
            Api32Completion(left, left.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootLeft)); Api32Completion(right, right.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootRight));
        }
        [TestCase("refill", false)][TestCase("refill", true)][TestCase("facility", false)][TestCase("facility", true)]
        public void Api32PreviousSupplyFailureContractCannotResumeFromSnapshotOrRealJournal(string kind, bool journal)
        {
            var state = State(); var reg = Registry(); var baseline = HostRecoveryService.CreateArchive(state);
            EffectSpec spec = kind == "refill" ? RefillSpec(1, Zone(state, "facility_supply_0").ZoneId) : ExplicitFacility(1, Ids(state, "facility_deck")[0], Zone(state, "facility_deck").ZoneId, Zone(state, "facility_supply_0").ZoneId);
            var node = Run(state, spec, reg);
            var archive = journal ? baseline : HostRecoveryService.CreateArchive(state);
            if (journal) { HostRecoveryService.AppendJournal(archive, state, state.ContentPackHash); Assert.That(archive.Journal.Entries.Any(e => e.StateAfter != null), Is.True); }
            string clean = HostRecoveryService.SerializeArchive(archive); Assert.That(HostRecoveryService.RecoverJson(clean, reg).Succeeded, Is.True);
            var saved = journal ? archive.Journal.Entries.Last(e => e.StateAfter != null).StateAfter : archive.Snapshot.State;
            saved.EffectRuntime.EffectNodes.Single(n => n.EffectId == node.EffectId).DefinitionVersion = kind == "refill" ? "2.0.0" : "4.0.0";
            string raw = HostRecoveryService.SerializeArchive(archive); var rejected = HostRecoveryService.RecoverJson(raw, reg);
            Assert.That(rejected.Succeeded, Is.False); Assert.That(rejected.Diagnostic, Does.Contain("contract_version_unsupported"));
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(raw));
        }
    }
}
