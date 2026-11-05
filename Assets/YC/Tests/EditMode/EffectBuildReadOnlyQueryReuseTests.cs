using System;
using System.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Tests.EditMode.ExpansionRules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // 真实ItemZones/Lua的隔离合同，不计正常页面、付款或对局验收。
    public sealed class EffectBuildReadOnlyQueryReuseTests : FacilityLayerTestFixture
    {
        protected override bool UseInMemoryContent => true;
        protected override bool UseCardQueryCarrier => true;

        private static void Same(ValidationResult actual, ValidationResult expected)
        {
            Assert.That(actual.IsValid, Is.EqualTo(expected.IsValid));
            Assert.That(actual.ErrorCode, Is.EqualTo(expected.ErrorCode));
            Assert.That(actual.Reason, Is.EqualTo(expected.Reason));
        }

        [Test]
        public void LuaFacilityProjectionMatchesFreshCityEvidenceAndSeesNextMutation()
        {
            var state=State();
            string definition=Definition(state,d=>!d.Unique && !d.HasEntryEffect);
            Put(state,Card(state,definition).ItemId,0);
            string before=RuleStateDigestService.Compute(state);
            var first=FacilityLayerLuaCatalog.CreateProjection(state);
            foreach(var player in state.Players)
            {
                var cells=FacilityLayerQuery.ResolveCity(state,player.PlayerId);
                Assert.That(FacilityLayerLuaCatalog.GetFactsDigest(first,"p"+player.PlayerId).StringValue,
                    Is.EqualTo(FacilityLayerEvidence.Digest(state,player.PlayerId)));
                Assert.That(FacilityLayerEvidence.Digest(player.PlayerId,cells),Is.EqualTo(FacilityLayerEvidence.Digest(state,player.PlayerId)));
                Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(FacilityLayerQuery.ResolveTowerAdjacency(state,cells)),
                    Is.EqualTo(Newtonsoft.Json.JsonConvert.SerializeObject(FacilityLayerQuery.ResolveTowerAdjacency(state,player.PlayerId))));
            }
            Assert.That(RuleStateDigestService.Compute(state),Is.EqualTo(before));
            Put(state,Card(state,definition).ItemId,1);
            var second=FacilityLayerLuaCatalog.CreateProjection(state);
            Assert.That(FacilityLayerLuaCatalog.GetFactsDigest(second,"p1").StringValue,
                Is.Not.EqualTo(FacilityLayerLuaCatalog.GetFactsDigest(first,"p1").StringValue));
        }

        [Test]
        public void PaidScopePreservesInvalidUniqueAndOccupiedSlotErrors()
        {
            var state = State();
            string normal = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string unique = Definition(state, d => d.Unique);
            Put(state, Card(state, normal).ItemId, 0);
            Put(state, Card(state, unique).ItemId, 1);
            string occupied = Card(state, normal).ItemId;
            string duplicateUnique = Card(state, unique).ItemId;
            var service = new BuildFacilityService().UseQueries(Registry.FacilityBuildQueries);
            string before = RuleStateDigestService.Compute(state);

            foreach (int player in new[] { 1, 99 })
                service.WithReadOnlyQuery(state, player, null, query =>
                {
                    foreach (string item in new[] { occupied, duplicateUnique, "missing.instance" })
                        foreach (int slot in new[] { -1, 0, 1, 2, 12 })
                            Same(query.ValidatePaidPlacement(item, slot),
                                service.ValidatePaidPlacement(state, player, item, slot));
                    return 0;
                });

            Assert.That(service.ValidatePaidPlacement(state, 99, occupied, 0).ErrorCode,
                Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(service.ValidatePaidPlacement(state, 1, "missing.instance", 0).ErrorCode,
                Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(service.ValidatePaidPlacement(state, 1, duplicateUnique, 2).ErrorCode,
                Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(service.ValidatePaidPlacement(state, 1, occupied, 0).ErrorCode,
                Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
        }

        [Test]
        public void FreshQueriesObserveRealInstanceSourceAndLayerChanges()
        {
            var state = State();
            OwnInfluence(state);
            string low = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string high = Definition(state, d => !d.Unique && d.Score > FacilityCardDatabase.Get(state, low).Score);
            Put(state, Card(state, low).ItemId, 0);
            string first = Card(state, high).ItemId, second = Card(state, high, 1).ItemId;
            Assert.That(first, Is.Not.EqualTo(second));
            var service = new BuildFacilityService().UseQueries(Registry.FacilityBuildQueries);
            var options = new BuildFacilityOptionQueryService(service, Registry.FacilityBuildCandidates);
            Assert.That(options.QueryCandidates(state, 1).Candidates.Any(c => c.ItemId == first), Is.False);
            string supply = FindZone(state, "facility_supply_0").ZoneId;
            Transfer(state, Leg(state, first, supply, "top"));
            Assert.That(options.QueryCandidates(state, 1).Candidates.Single(c => c.ItemId == first).SourceZoneId,
                Is.EqualTo(supply));
            var observed = Registry.FacilityBuildQueries.ResolveSlots(state, 1, first).Single(c => c.SlotIndex == 0);
            string before = RuleStateDigestService.Compute(state);
            service.WithReadOnlyQuery(state, 1, null, query =>
            {
                foreach (string item in new[] { first, second })
                    for (int slot = 0; slot < BuildFacilityService.CityBoardSlotCount; slot++)
                        Same(query.ValidatePaidPlacement(item, slot), service.ValidatePaidPlacement(state, 1, item, slot));
                Assert.That(query.ValidatePaidPlacement(first, 0).IsValid, Is.True);
                return 0;
            });
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));

            // 正式批次移动改变顶牌与层序，新Query不能借旧观察授权。
            Overlay(state, second, 0);
            Assert.That(Cell(state, 0).OrderDigest, Is.Not.EqualTo(observed.OrderDigest));
            Assert.That(service.WithReadOnlyQuery(state, 1, null,
                query => query.ValidatePaidPlacement(first, 0).IsValid), Is.False);
            Assert.Throws<InvalidOperationException>(() => Registry.FacilityBuildQueries.RevalidateSlot(state, observed, ""));

            Transfer(state, Leg(state, first, FindZone(state, "facility_deck").ZoneId, "top"));
            Assert.That(options.QueryCandidates(state, 1).Candidates.Any(c => c.ItemId == first), Is.False);
            // 已付款落位验证不要求牌仍在供应区，原公开语义必须保留。
            Same(service.WithReadOnlyQuery(state, 1, null, query => query.ValidatePaidPlacement(first, 2)),
                service.ValidatePaidPlacement(state, 1, first, 2));
        }

        [Test]
        public void DynamicLuaFactsStayReadOnlyAndAreScopedToThePlayerAndLifetime()
        {
            var state = State();
            OwnInfluence(state);
            string low = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string high = Definition(state, d => !d.Unique && d.Score > FacilityCardDatabase.Get(state, low).Score);
            Put(state, Card(state, low).ItemId, 0);
            Put(state, Card(state, low).ItemId, 1, player: 2);
            string item = Card(state, high).ItemId;
            var service = new BuildFacilityService().UseQueries(Registry.FacilityBuildQueries);
            string before = RuleStateDigestService.Compute(state);
            BuildFacilityService.ReadOnlyQuery escaped = null;
            int luaCalls = 0;
            var previous = YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample;
            YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample = (phase, handler, ticks) =>
            { if (phase == "lua.execute" && handler == QueryAbility) luaCalls++; };
            try
            {
                var one = service.WithReadOnlyQuery(state, 1, null, query =>
                {
                    escaped = query;
                    return Enumerable.Range(0, 12).Select(slot => query.ValidatePaidPlacement(item, slot).IsValid).ToArray();
                });
                Assert.That(luaCalls, Is.GreaterThan(0), "实际执行了动态Lua provider，不能用静态默认结果代替。");
                int previousCalls = luaCalls;
                var two = service.WithReadOnlyQuery(state, 2, null, query =>
                    Enumerable.Range(0, 12).Select(slot => query.ValidatePaidPlacement(item, slot).IsValid).ToArray());
                Assert.That(luaCalls, Is.GreaterThan(previousCalls), "新玩家/新Query必须重新读取真实provider。");
                Assert.That(one[0], Is.True);
                Assert.That(two[0], Is.True);
                Assert.That(two[1], Is.False, "不能借用另一玩家的移除前提或叠建授权。");
                Assert.Throws<InvalidOperationException>(() => escaped.ValidatePaidPlacement(item, 0));
                Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before), "只读回调不能推进状态或收据。");
            }
            finally { YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample = previous; }
        }

        [Test]
        public void SharedSnapshotKeepsFreshLuaQueryBodiesAndExactResultsForEachCandidateAndPlayer()
        {
            var state=State();OwnInfluence(state);
            string low=Definition(state,d=>!d.Unique && !d.HasEntryEffect);
            string high=Definition(state,d=>!d.Unique && d.Score>FacilityCardDatabase.Get(state,low).Score);
            Put(state,Card(state,low).ItemId,0);
            var first=Card(state,high).ItemId;var second=Card(state,high,1).ItemId;
            string baseline=RuleStateDigestService.Compute(state);
            string Read(int player,string item)=>Newtonsoft.Json.JsonConvert.SerializeObject(Registry.FacilityBuildQueries.ResolveSlots(state,player,item));
            var expected=new[]{Read(1,first),Read(1,second),Read(2,first)};
            int stamps=0,executions=0;
            var previous=YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample;
            YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample=(phase,handler,ticks)=> {
                if(phase=="lua.snapshot.stamp")stamps++;
                if(phase=="lua.execute")executions++;
            };
            try
            {
                using(ReadOnlyStateQueryScope.Begin(state))
                {
                    var actual=new[]{Read(1,first),Read(1,second),Read(2,first)};
                    Assert.That(actual,Is.EqualTo(expected));
                    Assert.That(executions,Is.GreaterThan(stamps),"Lua正文仍逐候选执行，只复用共同快照");
                }
                int old=stamps;Read(1,first);
                Assert.That(stamps,Is.GreaterThan(old),"同步作用域结束后必须重新物化");
            }
            finally {YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample=previous;}
            Assert.That(RuleStateDigestService.Compute(state),Is.EqualTo(baseline));
        }
    }
}
