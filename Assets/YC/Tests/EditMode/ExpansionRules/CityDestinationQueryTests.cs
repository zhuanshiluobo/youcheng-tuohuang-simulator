using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Movement;
using YC.Domain.State;
using YC.Domain.Rules;
using YC.Domain.Travel;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CityDestinationQueryTests
    {
        [TestCase(1,"ordinary")]
        [TestCase(1,"free")]
        [TestCase(1,"specified")]
        [TestCase(2,"ordinary")]
        [TestCase(2,"free")]
        [TestCase(2,"specified")]
        public void RealLuaHardBanUsesTheRuleSourcePlayerForEveryMovingPlayer(int moving,string purpose)
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string target=f.RemotePoint(2);f.Put(2,target,true);f.Put(2,target,false);
                string before=f.Snapshot();
                var query=new CityDestinationQuery(moving,"fixture.move",purpose,"standard",new[]{target});
                var result=new CityDestinationQueryService(f.Map,f.Registry).Query(f.State,query);
                Assert.That(result.AllowedLocationIds,Does.Not.Contain(target));
                Assert.That(result.HardExclusions.Single().SourceRef.AuthorizedPlayerId,Is.EqualTo(2));
                Assert.That(result.HardExclusions.Single().CandidateId,Is.EqualTo(CityMoveCandidateQueryService.CandidatePrefix+target));
                Assert.That(f.Snapshot(),Is.EqualTo(before));
            }
        }

        [Test]
        public void CompanyAtTheSamePointIsNotTheSourcesNonGangInfluence()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string target=f.RemotePoint(2);f.Put(2,target,true);f.PutCompany(target);
                var result=new CityDestinationQueryService(f.Map,f.Registry).Query(f.State,
                    new CityDestinationQuery(1,"fixture","free","standard",new[]{target}));
                Assert.That(result.AllowedLocationIds,Does.Contain(target));
                Assert.That(result.HardExclusions,Is.Empty);
            }
        }

        [Test]
        public void OrdinaryAddCannotReintroduceHardExcludedDestinationAndSourceExitWithdrawsItsBan()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string target=f.RemotePoint(2);f.Put(2,target,true);f.Put(2,target,false);
                string candidate=CityDestinationQueryService.CandidateId(target);
                f.Registry.CityDestinationCandidatePolicies.Register(new CandidatePolicyRegistration("qy009.add",
                    CityDestinationQueryService.CandidateKind,_=>new List<CandidatePatch> {
                        new CandidatePatch {Operation=CandidatePatchOperation.Add,CandidateIds=new List<string>{candidate}} }));
                var query=new CityDestinationQuery(1,"fixture","specified","standard",new[]{target});
                Assert.That(new CityDestinationQueryService(f.Map,f.Registry).Query(f.State,query).AllowedLocationIds,Does.Not.Contain(target));
                f.RetireSource(2);
                Assert.That(new CityDestinationQueryService(f.Map,f.Registry).Query(f.State,query).AllowedLocationIds,Does.Contain(target));
            }
        }

        [TestCase("return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=9})} end")]
        [TestCase("return function(ctx) return {Candidate.Remove({ids={'city.move.target:unknown'},reasonCode='invalid'})} end")]
        public void PureCandidateContractRejectsAnEffectOrUnknownCandidateWithoutExecutingIt(string script)
        {
            using(var f=new CollectionPlanTestFixture(cityScript:script))
            {
                string target=f.RemotePoint();string before=f.Snapshot();
                Assert.Throws<InvalidOperationException>(()=>new CityDestinationQueryService(f.Map,f.Registry).Query(f.State,
                    new CityDestinationQuery(1,"fixture","ordinary","standard",new[]{target})));
                Assert.That(f.Snapshot(),Is.EqualTo(before));
            }
        }

        [Test]
        public void QueryNeverMovesACityAlreadyStandingAtALaterMixedPoint()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string occupied=f.State.FindPlayer(1).CityLocationId;f.Put(2,occupied,true);f.Put(2,occupied,false);
                string before=f.Snapshot();
                new CityDestinationQueryService(f.Map,f.Registry).Query(f.State,
                    new CityDestinationQuery(2,"fixture","specified","standard",new[]{occupied}));
                Assert.That(f.State.FindPlayer(1).CityLocationId,Is.EqualTo(occupied));
                Assert.That(f.Snapshot(),Is.EqualTo(before));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SpecifiedNormalOrFreeMoveCannotBypassTheBan(bool free)
        {
            using(var f=new CollectionPlanTestFixture())
            {
                f.State.Phase=GamePhase.ActionRound1;
                string target=f.AdjacentPoint(2),start=f.State.FindPlayer(1).CityLocationId;
                f.Put(2,target,true);f.Put(2,target,false);
                int amount=f.State.FindPlayer(1).Resources.OriginiumShard;
                var engine=new EffectTreeExecutor(f.State,f.Registry);
                engine.CreateRoot(CityMoveEffectSpecFactory.Move(1,target,free,false,"qy009.fixture"));
                var result=engine.RunUntilQuiescent();Assert.That(result.Faulted,Is.False);
                Assert.That(f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==CityMoveEffectTypeIds.Move&&n.Status==EffectNodeStatus.Failed),Is.True);
                Assert.That(f.State.FindPlayer(1).CityLocationId,Is.EqualTo(start));
                Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard,Is.EqualTo(amount));
            }
        }

        [Test]
        public void RealPaymentThenNewBanFaultsBeforePositionCommitAndPreservesThePayment()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                f.State.Phase=GamePhase.ActionRound1;
                string target=f.AdjacentPoint(2),start=f.State.FindPlayer(1).CityLocationId;
                int before=f.State.FindPlayer(1).Resources.OriginiumShard;
                var engine=new EffectTreeExecutor(f.State,f.Registry);
                engine.CreateRoot(CityMoveEffectSpecFactory.Move(1,target,false,false,"qy009.fixture"));
                for(int i=0;i<128&&!f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==ResourceEffectTypeIds.Pay&&n.Status==EffectNodeStatus.Completed);i++)
                    Assert.That(engine.Advance(),Is.True,engine.LastDiagnostic);
                Assert.That(f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==ResourceEffectTypeIds.Pay&&n.Status==EffectNodeStatus.Completed),Is.True);
                int paid=before-f.State.FindPlayer(1).Resources.OriginiumShard;Assert.That(paid,Is.GreaterThan(0));
                // 隔离权威事实变化；不是正常游戏或页面验收，不伪造已付收据。
                f.Put(2,target,true);f.Put(2,target,false);
                f.Artifacts.WriteLabeledText("真实付款后新增禁令",f.Snapshot());
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.True);
                Assert.That(f.State.FindPlayer(1).CityLocationId,Is.EqualTo(start));
                Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard,Is.EqualTo(before-paid));
                f.Artifacts.WriteLabeledText("位置提交前故障现场",f.Snapshot());
            }
        }
    }
}
