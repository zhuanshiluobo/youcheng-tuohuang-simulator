using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
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
    /// <summary>R3隔离源码；本轮未编译或执行，不计正式卡面、正常开局或界面验收。</summary>
    public sealed class StrictInfluenceRemovalShortfallTests
    {
        private const string Hash = "qy006.r3.strict-shortfall.isolated";
        private IsolatedTestArtifacts artifacts;
        private GameState state;
        private GameMapDefinition map;
        private EffectRegistry registry;

        [SetUp] public void Begin()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            map = StaticMapDefinitions.CreateThreePlayerMap();
            registry = new EffectRegistry { ContentPackHash = Hash };
            ResourceEffectExecutor.Register(registry);
            GenericLuaEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, new InfluenceService(new MapQueryService(map)));
            var initial = new GameState {
                GameId = "qy006.r3.strict-shortfall.isolated", ContentPackHash = Hash, MapId = map.MapId,
                Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1, StartPlayerId = 1, CurrentPlayerId = 1,
                LaunchPlayerOrder = new List<int> { 1, 2 },
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, ContentPackHash = Hash, PlayerCount = 2 },
                Players = new List<PlayerState> {
                    new PlayerState { PlayerId = 1, Resources = new ResourceSet { GoldVoucher = 20 } },
                    new PlayerState { PlayerId = 2, Resources = new ResourceSet { GoldVoucher = 20 } } }
            };
            var recipe = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", PerPlayer = true, Quantity = 6, InitialZoneRole = "supply" }
            };
            foreach (var location in map.Locations.Where(l => !l.IsRedZone))
                recipe.Add(new ItemRecipeEntry { EntryId = "resource_" + location.LocationId, DefinitionId = "core.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            state = new ItemZoneStateFactory().CreateForSetup(initial, map, recipe);
            state.Map.OpenLocationIds.AddRange(map.Locations.Where(l => !l.IsRedZone).Select(l => l.LocationId));
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            artifacts.WriteLabeledText("隔离边界", "人工初始化物品，仅经实际Lua、编译器、正式Condition请求和真实影响力操作定位通用合同；不是正式P05/P06或正常游戏验收。");
        }
        [TearDown] public void End()
        {
            if (state != null) artifacts.WriteLabeledText("最终真实状态", JsonConvert.SerializeObject(state));
            artifacts.Finish();
        }
        private EffectTreeExecutor Engine() => new EffectTreeExecutor(state, registry);
        private void Pump()
        {
            var result = Engine().RunUntilQuiescent();
            if (result.Faulted) artifacts.WriteLabeledText("首次故障状态", JsonConvert.SerializeObject(state));
            Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private EffectNodeRuntimeState Run(EffectSpec spec)
        {
            string id = Engine().CreateRoot(spec); Pump();
            return state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
        }
        private string Place(int ordinal)
        {
            var slots = map.Locations.Where(l => !l.IsRedZone).SelectMany(l => Enumerable.Range(0, l.InfluenceSlotCount)
                .Select(i => InfluenceService.GetLocationSlotId(l.LocationId, i))).ToList();
            return Value(Run(InfluenceEffectSpecFactory.PlaceInfluence(1, slots[ordinal])).NormalizedResult, "influenceId").ReferenceId;
        }
        private void Restore(string label)
        {
            artifacts.WriteLabeledText(label + "恢复前原始状态", JsonConvert.SerializeObject(state));
            var result = artifacts.Recover(state, label, registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            state = result.State;
        }
        private void Answer(string value)
        {
            var request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId,
                request.StateRevision, NormalizedValue.CreateString(value), out string reason), Is.True, reason);
            Pump();
        }
        private string DomainSignature() => JsonConvert.SerializeObject(new {
            items = state.ItemZones.Items.OrderBy(i => i.ItemId).Select(i => new { i.ItemId, i.TypeId, i.ZoneId, i.SlotId, i.OriginalSupplyZoneId }),
            zones = state.ItemZones.Zones, orders = state.ItemZones.Orders,
            influences = state.Map.Influences.Select(i => new { i.InfluenceId, i.PlayerId, i.SlotId, i.LocationId, i.RouteId }),
            players = state.Players.Select(p => new { p.PlayerId, p.Score, p.Resources })
        });
        private int[] DepartureCounts() => new[] {
            state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved),
            state.EffectRuntime.InfluenceDepartures.Count, state.EffectRuntime.InfluenceDepartureBatches.Count,
            state.EffectRuntime.DeferredResponses.Count
        };
        private LuaInvocationResult Invoke(IEnumerable<string> targets, string expression = "ctx.payload.targets", int required = 2,
            string executing = "ctx.playerId", string extra = "", int api = 2)
        {
            string code = "return function(ctx) return {Effect.Condition({leftEffects={Effect.RemoveInfluence({apiVersion=" + api +
                ",executingPlayer=" + executing + ",targets=" + expression + ",requiredCount=" + required +
                ",reasonId='qy006.r3.strict'" + extra + "})},rightEffects={Effect.GainScore({recipient=ctx.playerId,amount=1})}})} end";
            var payload = LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("targets", LuaSnapshotValue.Array(targets.Select(LuaSnapshotValue.String).ToList()))
            });
            artifacts.WriteLabeledText("实际Lua输入", code);
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Hash, "qy006.r3.strict", "qy006.r3.strict", "1.0.0", code,
                LuaContentHasher.ComputeSha256(code)), new LuaInvocationContext("qy006.r3.invoke", "QY006StrictInput", "p1", 0, "1.0.0", 2, 0, 0,
                new LuaFacadeSnapshot { EventPayload = payload }));
            artifacts.WriteLabeledText("Lua规范化结果", JsonConvert.SerializeObject(new { result.IsSuccess, result.FailureCode, result.Diagnostic }));
            return result;
        }
        private EffectSpec Compile(LuaInvocationResult invocation)
        {
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 },
                out var specs, out var failure), Is.True, failure?.Diagnostic);
            Assert.That(specs.Count, Is.EqualTo(1));
            return specs.Single();
        }
        private string BeginCondition(IEnumerable<string> targets, string expression = "ctx.payload.targets")
        {
            var ids = targets.ToList(); var spec = Compile(Invoke(ids, expression));
            var rawTargets = Value(spec.NestedEffectGroups[0][0].NormalizedArguments, "targets");
            Assert.That(rawTargets.Kind, Is.EqualTo(NormalizedValueKind.Array));
            Assert.That(rawTargets.Items.Count, Is.EqualTo(ids.Count));
            string id = Engine().CreateRoot(spec); Pump();
            Assert.That(state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open").CandidateIds, Does.Contain("continue"));
            return id;
        }
        private EffectNodeRuntimeState StrictNode() => state.EffectRuntime.EffectNodes.Single(n =>
            n.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence && Value(n.NormalizedArguments, "targets") != null);
        private void AssertZeroFailure(string domain, int[] counts, int requested = 2)
        {
            var node = StrictNode();
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(node.FailureReason, Is.EqualTo("incomplete_influence_payment"));
            Assert.That(Number(node.NormalizedResult, "requestedCount"), Is.EqualTo(requested));
            Assert.That(Number(node.NormalizedResult, "actualRemovedCount"), Is.Zero);
            Assert.That(Value(node.NormalizedResult, "removedItemIds").Items, Is.Empty);
            Assert.That(Value(node.NormalizedResult, "departureIds").Items, Is.Empty);
            Assert.That(node.PositionChanges, Is.Empty);
            Assert.That(DomainSignature(), Is.EqualTo(domain));
            Assert.That(DepartureCounts(), Is.EqualTo(counts));
            Assert.That(state.EffectRuntime.InteractionRequests.Where(q => q.Status == "open"), Is.Empty);
        }

        [TestCase(0, false)][TestCase(0, true)][TestCase(1, false)][TestCase(1, true)]
        public void RealShortListReachesConditionAndFailsWithoutRemovingAnything(int count, bool restoreBeforeContinue)
        {
            var all = new[] { Place(0), Place(1) };
            string domain = DomainSignature(); int[] counts = DepartureCounts();
            string root = BeginCondition(all.Take(count));
            if (restoreBeforeContinue) Restore("短目标执行确认待答");
            Answer("continue"); AssertZeroFailure(domain, counts);
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).NormalizedResult, "outcome"), Is.EqualTo("condition_not_met"));
            Restore("短目标零变更失败终态"); Pump(); AssertZeroFailure(domain, counts);
        }
        [TestCase(1)][TestCase(16)] public void InclusiveRequiredCountBoundsAcceptARealShortfall(int required)
        {
            Place(0); string domain = DomainSignature(); int[] counts = DepartureCounts();
            var spec = Compile(Invoke(Array.Empty<string>(), required: required));
            Engine().CreateRoot(spec); Pump(); Answer("continue"); AssertZeroFailure(domain, counts, required);
        }
        [Test] public void LuaLiteralEmptyTableStaysAnExplicitEmptyTargetArray()
        {
            Place(0); string domain = DomainSignature(); int[] counts = DepartureCounts();
            BeginCondition(Array.Empty<string>(), "{}"); Restore("Lua空表左式尚未执行");
            Answer("continue"); AssertZeroFailure(domain, counts);
        }
        [Test] public void DuplicateRealTargetFailsWithoutSelectingAnotherCube()
        {
            string one = Place(0); Place(1); string domain = DomainSignature(); int[] counts = DepartureCounts();
            BeginCondition(new[] { one, one }); Answer("continue"); AssertZeroFailure(domain, counts);
        }
        [Test] public void TargetThatActuallyLeftAfterConfirmationIsNotReplacedByAnotherCube()
        {
            string one = Place(0), two = Place(1); Place(2);
            BeginCondition(new[] { one, two });
            var request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            var engine = Engine();
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("continue"), out string reason), Is.True, reason);
            // 合法确认已受理，随后由独立真实移除抢先改变目标；不改修订/Map/实例或补选ID。
            string competing = engine.CreateRoot(InfluenceEffectSpecFactory.RemoveInfluence(1, two));
            for (int guard = 0; guard < 200 && engine.GetNode(competing).Status != EffectNodeStatus.Completed; guard++)
                Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
            Assert.That(engine.GetNode(competing).Status, Is.EqualTo(EffectNodeStatus.Completed));
            string domain = DomainSignature(); int[] counts = DepartureCounts();
            Restore("合法确认后目标真实离场且严格左式尚未执行"); Pump(); AssertZeroFailure(domain, counts);
        }
        [TestCase("supply")][TestCase("resource")]
        public void RealItemFromTheWrongSourceCannotPayForMapInfluenceRemoval(string source)
        {
            string one = Place(0); Place(1);
            string other = source == "supply"
                ? state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId).ItemId
                : state.ItemZones.Items.First(i => i.TypeId == "core.resource_point_token").ItemId;
            string domain = DomainSignature(); int[] counts = DepartureCounts();
            BeginCondition(new[] { one, other }); Answer("continue"); AssertZeroFailure(domain, counts);
        }
        [Test] public void WrongExecutingPlayerCannotUseTheShortfallExtensionToRemoveAnything()
        {
            var targets = new[] { Place(0), Place(1) }; string domain = DomainSignature(); int[] counts = DepartureCounts();
            var invocation = Invoke(targets, executing: "'p2'");
            if (invocation.IsSuccess && new LuaEffectSpecCompiler().TryCompile(invocation,
                new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out _))
            {
                Engine().CreateRoot(specs.Single()); Pump(); Answer("continue");
                Assert.That(StrictNode().Status, Is.EqualTo(EffectNodeStatus.Failed));
                Assert.That(StrictNode().FailureReason, Is.EqualTo("invalid_player"));
            }
            Assert.That(DomainSignature(), Is.EqualTo(domain)); Assert.That(DepartureCounts(), Is.EqualTo(counts));
        }
        [Test] public void FullCountCommitsBothBeforeRequiredLuaResponsesAndOnlyThenRunsTheRightSide()
        {
            var targets = new[] { Place(0), Place(1) };
            const string body = "return function(ctx) return {Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',options={'ack'}})} end";
            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition("qy006.r3.required", InfluenceEventTypeIds.InfluenceRemoved, "",
                new LuaScriptDefinition(Hash, "qy006.r3.required", "qy006.r3.required", "1.0.0", body, LuaContentHasher.ComputeSha256(body)), EffectHandlerRole.Observer));
            BeginCondition(targets); Answer("continue");
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(StrictNode().Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(StrictNode().PositionChanges.Count, Is.EqualTo(2));
            Assert.That(DepartureCounts()[0], Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Restore("两枚提交完成但必要响应待答"); Answer("ack");
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Restore("第一必要响应结束"); Answer("ack");
            Assert.That(StrictNode().Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Number(StrictNode().NormalizedResult, "actualRemovedCount"), Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(DepartureCounts()[0], Is.EqualTo(2));
            Restore("足额与必要响应完成"); Pump();
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(DepartureCounts()[0], Is.EqualTo(2));
        }
        [Test] public void OverfullArrayIsStillRejectedBeforeAnyRuntimeNode()
        {
            var targets = new[] { Place(0), Place(1), Place(2) }; string domain = DomainSignature(); int nodes = state.EffectRuntime.EffectNodes.Count;
            Assert.That(Invoke(targets).IsSuccess, Is.False);
            Assert.That(DomainSignature(), Is.EqualTo(domain)); Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(nodes));
            Assert.That(InfluenceEffectExecutor.ValidateRemoveSpec(InfluenceEffectExecutor.StrictRemove(1, targets, 2)), Is.Not.Empty);
        }
        [TestCase("42")][TestCase("{42}")][TestCase("{{}}")][TestCase("false")][TestCase("nil")]
        [TestCase("{bad=ctx.payload.targets[1]}")]
        public void MalformedLuaTargetsAreStillRejected(string expression)
        {
            string one = Place(0); string domain = DomainSignature();
            var invocation = Invoke(new[] { one }, expression);
            Assert.That(invocation.IsSuccess, Is.False);
            if (expression == "nil") Assert.That(invocation.Diagnostic, Does.Contain("strict_remove_arguments_invalid"));
            Assert.That(DomainSignature(), Is.EqualTo(domain));
        }
        [TestCase(false)][TestCase(true)]
        public void LegacySingleTargetLuaRemovalStillAcceptsDefaultAndExplicitOne(bool explicitOne)
        {
            string one = Place(0), other = Place(1);
            string code = "return function(ctx) return {Effect.RemoveInfluence({" + (explicitOne ? "apiVersion=1," : "") +
                "executingPlayer=ctx.playerId,targetInfluence='" + one + "'})} end";
            artifacts.WriteLabeledText("旧单目标Lua合法对照原文", code);
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Hash, "qy007.legacy.remove", "qy007.legacy.remove", "1.0.0", code,
                LuaContentHasher.ComputeSha256(code)), new LuaInvocationContext("qy007.legacy.remove", "QY007LegacyRemove", "p1", 0, "1.0.0", 2, 0, 0));
            var node = Run(Compile(invocation));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Map.Influences.Any(i => i.InfluenceId == one), Is.False);
            Assert.That(state.Map.Influences.Any(i => i.InfluenceId == other), Is.True);
            Assert.That(DepartureCounts()[0], Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20));
            Restore("旧单目标Lua合法移除完成-" + explicitOne);
        }
        [TestCase(0)][TestCase(17)] public void RequiredCountBoundsAreNotRelaxed(int required)
        {
            var targets = new[] { Place(0), Place(1) };
            Assert.That(Invoke(targets, required: required).IsSuccess, Is.False);
            Assert.That(InfluenceEffectExecutor.ValidateRemoveSpec(InfluenceEffectExecutor.StrictRemove(1, targets, required)), Is.Not.Empty);
        }
        [TestCase(",targetInfluence=ctx.payload.targets[1]")][TestCase(",candidateScope=ctx.payload.targets")]
        public void LegacyTargetFieldsCannotBeCombinedWithStrictTargets(string extra)
        {
            var targets = new[] { Place(0), Place(1) };
            Assert.That(Invoke(targets, extra: extra).IsSuccess, Is.False);
        }
        [Test] public void StrictInputStillRequiresApiVersionTwo()
        {
            var targets = new[] { Place(0), Place(1) };
            Assert.That(Invoke(targets, api: 1).IsSuccess, Is.False);
        }
    }
}
