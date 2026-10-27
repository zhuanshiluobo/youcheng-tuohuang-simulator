using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>隔离只读查询合同及小样本计时，不是正常页面或旧R28大状态性能验收。</summary>
    public sealed class BuildSlotQueryReuseContractTests : FacilityLayerTestFixture
    {
        private const string SlotAbility = "qy010.slot.reuse";
        protected override bool UseInMemoryContent => true;
        protected override bool UseCardQueryCarrier => true;

        private void SeparateSlotHandlerForMeasurement()
        {
            var definition = Pack.ActiveDefinitions.Single(d => d.DefinitionId == Token);
            var source = GameStateCloneService.DeepClone(definition.AbilitySources.Single());
            source.QueryKinds.Remove(FacilityBuildQueryService.SlotKind);
            var slots = GameStateCloneService.DeepClone(source);
            slots.AbilityId = SlotAbility;
            slots.HandlerRef = SlotAbility;
            slots.QueryKinds = new List<string> { FacilityBuildQueryService.SlotKind };
            var handler = GameStateCloneService.DeepClone(definition.AbilityHandlers.Single());
            string lua = Pack.GetScript(handler.Script).Replace("'qy010.fixture.query'", "'" + SlotAbility + "'");
            handler.HandlerId = SlotAbility;
            handler.Script = "lua/qy010_slot_reuse.lua";
            var token = JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),
                "Assets/StreamingAssets/Content/core/component/" + Token + ".json")));
            token["itemTypeId"] = "core.board_card";
            token.Remove("cubeAppearance");
            token["abilitySources"] = JArray.FromObject(new[] { source, slots });
            token["abilityHandlers"] = JArray.FromObject(new[] { definition.AbilityHandlers.Single(), handler });
            var overrides = new Dictionary<string, string> {
                ["component/" + Token + ".json"] = token.ToString(), [handler.Script] = lua };
            Pack = Pack.CreateWithOverrides(overrides);
            artifacts.WriteLabeledText("槽位独立计数输入", JsonConvert.SerializeObject(new { overrides, Pack.ContentHash }));
        }

        [Test]
        public void TypedSlotQuery_ReusesOnlyWithinOneReadScopeAndKeepsLiveRulesAndInstances()
        {
            SeparateSlotHandlerForMeasurement();
            var state = State();
            OwnInfluence(state);
            string low = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string high = Definition(state, d => !d.Unique && d.Score > FacilityCardDatabase.Get(state, low).Score);
            Put(state, Card(state, low).ItemId, 0);
            Put(state, Card(state, low).ItemId, 1, player: 2);
            string first = Card(state, high).ItemId, second = Card(state, high, 1).ItemId;
            Transfer(state, Leg(state, first, FindZone(state, "facility_supply_0").ZoneId, "top"),
                Leg(state, second, FindZone(state, "facility_supply_1").ZoneId, "top"));
            var service = new BuildFacilityService().UseQueries(Registry.FacilityBuildQueries);
            var expected = Registry.FacilityBuildQueries.ResolveSlots(state, 1, first);
            var observed = expected.Single(c => c.SlotIndex == 0);
            string before = RuleStateDigestService.Compute(state);
            int revision = state.EffectRuntime.StateRevision;
            int calls = 0;
            var samples = new List<object>();
            var previous = LuaEffectEventDispatcher.CpuTimingSample;
            LuaEffectEventDispatcher.CpuTimingSample = (phase, handler, ticks) => {
                if (phase == "lua.execute" && handler == SlotAbility) calls++;
            };
            try
            {
                for (int sample = 0; sample < 3; sample++)
                {
                    calls = 0;
                    var timer = Stopwatch.StartNew();
                    var repeated = Enumerable.Range(0, BuildFacilityService.CityBoardSlotCount)
                        .Select(slot => Registry.FacilityBuildQueries.ResolveSlots(state, 1, first).Any(c => c.SlotIndex == slot)).ToArray();
                    timer.Stop();
                    double repeatedMs = timer.Elapsed.TotalMilliseconds;
                    int repeatedCalls = calls;
                    calls = 0;
                    timer.Restart();
                    var scoped = service.WithReadOnlyQuery(state, 1, null, query =>
                        Enumerable.Range(0, BuildFacilityService.CityBoardSlotCount)
                            .Select(slot => query.ValidateCityBoardSlot(first, slot).IsValid).ToArray());
                    timer.Stop();
                    samples.Add(new { sample, repeatedCalls, scopedCalls = calls, repeatedMs, scopedMs = timer.Elapsed.TotalMilliseconds });
                    Assert.That(scoped, Is.EqualTo(repeated));
                    Assert.That(repeatedCalls, Is.EqualTo(12));
                    Assert.That(calls, Is.EqualTo(1), "同一次只读查询只求一次该实例的完整槽位集合。");
                }

                calls = 0;
                var option = new BuildFacilityOptionQueryService(service, Registry.FacilityBuildCandidates)
                    .QueryForAdditionalBuild(state, 1, first);
                Assert.That(option.SlotOptions.Where(s => s.IsLegal).Select(s => s.CityBoardSlotIndex),
                    Is.EqualTo(expected.Select(c => c.SlotIndex)));
                Assert.That(calls, Is.EqualTo(1), "Application实际12槽预览同样复用。");

                calls = 0;
                BuildFacilityService.ReadOnlyQuery escaped = null;
                service.WithReadOnlyQuery(state, 1, null, query => {
                    escaped = query;
                    foreach (string item in new[] { first, second })
                        for (int slot = 0; slot < BuildFacilityService.CityBoardSlotCount; slot++)
                            Assert.That(query.ValidateCityBoardSlot(item, slot).IsValid,
                                Is.EqualTo(expected.Any(c => c.SlotIndex == slot)));
                    return 0;
                });
                Assert.That(calls, Is.EqualTo(2), "同定义的不同真实实例必须分别求解。");
                Assert.Throws<InvalidOperationException>(() => escaped.ValidateCityBoardSlot(first, 0));

                calls = 0;
                var other = service.WithReadOnlyQuery(state, 2, null, query =>
                    Enumerable.Range(0, BuildFacilityService.CityBoardSlotCount)
                        .Select(slot => query.ValidateCityBoardSlot(first, slot).IsValid).ToArray());
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(other[0], Is.True);
                Assert.That(other[1], Is.False, "另一玩家无本人可移除目标，不能借用本人叠建授权。");
                Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
                Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));

                // 真实批次改变顶牌/层序后，新查询与执行前复验必须看到变化，不能借预览授权。
                Overlay(state, second, 0);
                calls = 0;
                Assert.That(service.ValidateCityBoardSlot(state, 1, first, 0).IsValid, Is.False);
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(Cell(state, 0).OrderDigest, Is.Not.EqualTo(observed.OrderDigest));
                Assert.Throws<InvalidOperationException>(() => Registry.FacilityBuildQueries.RevalidateSlot(state, observed, ""));
                artifacts.WriteLabeledText("槽位复用次数与小样本时间", JsonConvert.SerializeObject(new {
                    note = "计时仅纯查询，摘要/断言/日志均在计时外；参考是当前源重复ResolveSlots，不是旧R28程序集或3031/6851基准",
                    samples, instances = new[] { first, second }, stateBefore = before, revision,
                    scopedInstanceCalls = 2, otherPlayerCalls = 1, changedOrderFreshCalls = 1,
                    oldOrderDigest = observed.OrderDigest, newOrderDigest = Cell(state, 0).OrderDigest
                }, Formatting.Indented));
            }
            finally { LuaEffectEventDispatcher.CpuTimingSample = previous; }
        }
    }
}
