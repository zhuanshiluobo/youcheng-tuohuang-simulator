using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class FinalScoringSessionTests
    {
        [Test] public void MissingUnsealedProducerIsAnExplicitFaultAndCannotStartOrInventAReadyDependency()
        {
            fixture=new FinalScoringTestFixture();fixture.Start();fixture.Until(()=>fixture.Owner.FlowStage==FinalScoringSessionService.Prepare);
            const string missing="qy007.corrupt.missing-producer";
            fixture.State.EffectRuntime.InfluenceDepartureBatches.Add(new InfluenceDepartureBatch {BatchId=StableIdFactory.Create("departure_batch",missing),OwnerEffectId=missing});
            var report=fixture.Engine().RunUntilQuiescent();Assert.That(report.Faulted,Is.True);Assert.That(fixture.State.EffectRuntime.LastFaultMessage,Does.Contain("final_scoring_unsealed_producer_invalid"));
            Assert.That(fixture.State.FinalScoring?.Session,Is.Null);Assert.That(fixture.State.EffectRuntime.RuleEvents.Any(e=>e.EventType=="FinalScoringStarted"),Is.False);
            fixture.Artifacts.WriteLabeledText("损坏未封口生产者明确故障非合法等待",JsonConvert.SerializeObject(fixture.State));
        }
        [Test] public void OriginalLastRoundAndEndActionWaitForARealDepartureBatchBeforeResourceSnapshot()
        {
            var map=YC.Domain.Maps.StaticMapDefinitions.Resolve(YC.Domain.Maps.StaticMapDefinitions.FourPlayerMapId);
            string slot=YC.Domain.Influence.InfluenceService.GetRouteSlotId(map.Routes.First(r=>r.InfluenceSlotCount>0).RouteId,0);
            string script="return function(ctx) if ctx.playerId~='p1' then return {} end; return {Effect.DepartureBatch({effects={Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,influenceSource='qy007.final.batch',targetSlotId='"+slot+"'}),Effect.SelectInfluence({player=ctx.playerId,operation='remove',ownerFilter='self',count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false})}})} end";
            fixture=new FinalScoringTestFixture(eventScript:script,departureResponse:true);var rounds=fixture.StartOriginalLastRound();fixture.Pump();
            string session=fixture.State.FinalScoring.Session.SessionId;string control=fixture.State.FinalScoring.Session.Control.BasisDigest;
            fixture.Artifacts.WriteLabeledText("原末轮特殊效果首次实际现场",JsonConvert.SerializeObject(fixture.State));
            Assert.That(fixture.State.EffectRuntime.InteractionRequests.Count(r=>r.Status=="open"),Is.EqualTo(1),"真实特殊效果必须停在移除选择");
            var remove=fixture.State.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            Assert.That(fixture.Engine().TrySubmitInteraction(remove.GetStableInteractionId(),1,remove.StateRevision,NormalizedValue.CreateArray(remove.CandidateIds.Take(1).Select(NormalizedValue.CreateString).ToList()),out var reason),Is.True,reason);fixture.Pump();
            Assert.That(fixture.State.EffectRuntime.InfluenceDepartureBatches.Single().Sealed,Is.True);
            Assert.That(fixture.State.EffectRuntime.DeferredResponses.Single(r=>r.Kind=="optional_effect").Status,Is.EqualTo(DeferredResponseStatus.Pending));
            Assert.That(fixture.State.FinalScoring.IsResolved,Is.False);Assert.That(fixture.State.FinalScoring.Session.Players.All(p=>p.Resources==null),Is.True);
            var handler=new EndActionCommandHandler(new RoundAdvanceService(),new FinalScoringService(fixture.Map),fixture.Registry,rounds,s=>rounds.Advance(s));
            for(int i=0;i<2;i++) Assert.That(handler.Handle(fixture.State,new GameCommand {Kind=GameCommandKind.EndAction,PlayerId=1}).Succeeded,Is.True);
            Assert.That(fixture.State.FinalScoring.Session.SessionId,Is.EqualTo(session));Assert.That(fixture.State.FinalScoring.Session.Control.BasisDigest,Is.EqualTo(control));
            fixture.Restore("原末轮真实外部离场义务等待同Session");var response=fixture.State.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            Assert.That(fixture.Engine().TrySubmitInteraction(response.GetStableInteractionId(),response.AnsweringPlayerId,response.StateRevision,NormalizedValue.CreateString("iron"),out reason),Is.True,reason);fixture.Pump();
            Assert.That(fixture.State.FinalScoring.IsResolved,Is.True);Assert.That(fixture.State.FinalScoring.Session.SessionId,Is.EqualTo(session));
            Assert.That(fixture.State.FinalScoring.Session.Players.Single(p=>p.PlayerId==1).Resources.Iron,Is.EqualTo(4));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e=>e.EventType=="FinalScoringStarted"),Is.EqualTo(1));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e=>e.EventType=="RoundEnded"),Is.EqualTo(1));fixture.Restore("铁真实到账后才冻结资源并完成终局");
        }
        [Test] public void QY011B2OriginalRoundAndEndActionKeepControlFrozenWhileIronCrossesResourceThreshold()
        {
            // 隔离规则定位：真实末轮及正式Choice链；不手写已付节点或Ready义务。
            const string script = @"return function(ctx) if ctx.playerId~='p1' then return {} end
              return {Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',branches={
                {id='iron',effects={Effect.GainResource({recipient=ctx.playerId,resourceType='Iron',amount=1}),Effect.GainScore({recipient=ctx.playerId,amount=2})}},
                {id='leave',effects={}}
              }})} end";
            fixture = new FinalScoringTestFixture(eventScript: script); fixture.State.FindPlayer(1).Resources.Iron = 5;
            var rounds = fixture.StartOriginalLastRound(); fixture.Pump();
            var session = fixture.State.FinalScoring.Session;
            string sessionId = session.SessionId, control = JsonConvert.SerializeObject(session.Control), startedId = session.StartedEventId;
            Assert.That(startedId, Is.Not.Empty);
            var main = fixture.State.EffectRuntime.MainNodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded);
            Assert.That(session.OwnerEffectId, Is.EqualTo(main.ExecutionEffectId)); Assert.That(session.OwnerMainNodeId, Is.EqualTo(main.NodeId));
            Assert.That(fixture.State.EffectRuntime.InteractionRequests.Count(r => r.Status == "open"), Is.EqualTo(1));
            Assert.That(fixture.State.FindPlayer(1).Resources.Iron, Is.EqualTo(5)); Assert.That(fixture.State.FindPlayer(1).Score, Is.Zero);
            Assert.That(session.Players.All(p => p.Resources == null), Is.True); Assert.That(session.RegionCommitReceipt, Is.Empty); Assert.That(session.ResourceCommitReceipt, Is.Empty);
            fixture.Artifacts.WriteLabeledText("B2原末轮真实Choice等待且铁尚未到账", JsonConvert.SerializeObject(fixture.State));
            var handler = new EndActionCommandHandler(new RoundAdvanceService(), new FinalScoringService(fixture.Map), fixture.Registry, rounds, s => rounds.Advance(s));
            for (int i = 0; i < 2; i++) Assert.That(handler.Handle(fixture.State, new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 }).Succeeded, Is.True);
            Assert.That(fixture.State.FinalScoring.Session.SessionId, Is.EqualTo(sessionId)); Assert.That(JsonConvert.SerializeObject(fixture.State.FinalScoring.Session.Control), Is.EqualTo(control));
            fixture.Restore("B2原末轮Choice等待恢复");
            Assert.That(fixture.State.FinalScoring.Session.StartedEventId, Is.EqualTo(startedId));
            Assert.That(JsonConvert.SerializeObject(fixture.State.FinalScoring.Session.Control), Is.EqualTo(control));
            var choice = fixture.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open"); Assert.That(choice.AnsweringPlayerId, Is.EqualTo(1));
            Assert.That(fixture.Engine().TrySubmitInteraction(choice.GetStableInteractionId(), choice.AnsweringPlayerId, choice.StateRevision, NormalizedValue.CreateString("iron"), out string reason), Is.True, reason);
            fixture.Pump();
            var ledger = fixture.State.FinalScoring.Session.Players.Single(p => p.PlayerId == 1);
            Assert.That(fixture.State.FinalScoring.IsResolved, Is.True); Assert.That(ledger.Resources.Iron, Is.EqualTo(6));
            Assert.That(ledger.SpecialScore, Is.EqualTo(2)); Assert.That(ledger.ResourceScore, Is.EqualTo(1), "真实新增的铁必须在剩余资源计分前到账");
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(3)); Assert.That(fixture.State.FindPlayer(1).Resources.Iron, Is.EqualTo(6));
            Assert.That(fixture.State.FinalScoring.Session.SessionId, Is.EqualTo(sessionId)); Assert.That(JsonConvert.SerializeObject(fixture.State.FinalScoring.Session.Control), Is.EqualTo(control));
            fixture.Restore("B2原末轮跨资源分阈值完成恢复"); fixture.Pump(); fixture.Pump();
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(3)); Assert.That(fixture.State.FindPlayer(1).Resources.Iron, Is.EqualTo(6));
            Assert.That(fixture.State.FinalScoring.Session.StartedEventId, Is.EqualTo(startedId));
            Assert.That(JsonConvert.SerializeObject(fixture.State.FinalScoring.Session.Control), Is.EqualTo(control));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(1));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "RoundEnded"), Is.EqualTo(1));
        }
        private FinalScoringTestFixture fixture;
        [TearDown] public void FinishArtifacts() => fixture?.Finish();
        private const string ScoreAndIron = "return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=1}),Effect.GainResource({recipient=ctx.playerId,resourceType='Iron',amount=1})} end";
        [Test] public void ActualSpecialEffectsAreArchivedOnceAndIronPrecedesResourceSnapshot()
        {
            fixture = new FinalScoringTestFixture(eventScript: ScoreAndIron);
            fixture.State.FindPlayer(1).Score = 7; fixture.State.FindPlayer(1).Resources.Iron = 5;
            fixture.Start(); fixture.Until(() => fixture.Owner.FlowStage == FinalScoringSessionService.ResourceSnapshot);
            Assert.That(fixture.State.FindPlayer(1).Resources.Iron, Is.EqualTo(6));
            Assert.That(fixture.State.FinalScoring.Session.Players.All(p => p.Resources == null), Is.True);
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(8));
            fixture.Pump(); var s = fixture.State.FinalScoring.Session;
            Assert.That(fixture.State.FinalScoring.IsResolved, Is.True); Assert.That(s.Effects.Count, Is.EqualTo(4));
            Assert.That(fixture.State.FinalScoring.PlayerScores.Single(p => p.PlayerId == 1).TotalScore, Is.EqualTo(9));
            Assert.That(s.Players.Single(p => p.PlayerId == 1).ResourceScore, Is.EqualTo(1));
            fixture.Pump(); Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(9));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "RoundEnded"), Is.EqualTo(1));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(1));
        }
        [Test] public void PersonalProfileReplacesWholeDefaultInsteadOfAddingBoth()
        {
            string query = "return function(ctx) local items={}; if ctx.payload.authorizedPlayerId==1 then items={{kind='resource_profile',playerId=1,profileId='fixture.personal',entries={{resourceType='Iron',units=4,points=1}}}} end; return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items=items} end";
            fixture = new FinalScoringTestFixture(new[] { (query, new[] { "resource_profile" }) });
            fixture.State.FindPlayer(1).Resources.Iron = 11; fixture.State.FindPlayer(1).Resources.PureOriginium = 3;
            fixture.State.FindPlayer(2).Resources.Iron = 11; fixture.State.FindPlayer(2).Resources.PureOriginium = 3;
            fixture.Start(); fixture.Pump();
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(2)); Assert.That(fixture.State.FindPlayer(2).Score, Is.EqualTo(4));
            Assert.That(fixture.State.FindPlayer(1).Resources.Iron, Is.EqualTo(11)); Assert.That(fixture.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(3));
        }
        [TestCase(0, 0)] [TestCase(3, 3)] [TestCase(4, 5)] [TestCase(7, 8)] [TestCase(8, 10)]
        public void ExtraSpecialRewardRetainsDefaultResourceScore(int pure, int total)
        {
            const string script = "return function(ctx) local n=PlayerData.GetResources(ctx.playerId).pureOriginium; local k=0; while n>=4 do n=n-4; k=k+1 end; if k==0 then return {} end; return {Effect.GainScore({recipient=ctx.playerId,amount=k})} end";
            fixture = new FinalScoringTestFixture(eventScript: script);
            fixture.State.FindPlayer(1).Resources.PureOriginium = pure;
            fixture.Start(); fixture.Pump();
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(total)); Assert.That(fixture.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure));
        }
        [Test] public void EndActionAndSynchronousResolveCannotSkipARealSpecialChoice()
        {
            const string script = "return function(ctx) return {Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',branches={{id='take',effects={Effect.GainScore({recipient=ctx.playerId,amount=2})}},{id='leave',effects={}}}})} end";
            fixture = new FinalScoringTestFixture(eventScript: script); fixture.Start(); fixture.Pump();
            Assert.That(fixture.State.FinalScoring.IsResolved, Is.False); Assert.That(fixture.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.True);
            var sync = new FinalScoringService(fixture.Map).Resolve(fixture.State);
            Assert.That(sync.Succeeded, Is.False); Assert.That(sync.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.PendingChoiceRequired));
            var handler = new EndActionCommandHandler(new RoundAdvanceService(), new FinalScoringService(fixture.Map), fixture.Registry, null, state => {
                var report = new EffectTreeExecutor(state, fixture.Registry).RunUntilQuiescent();
                return report.Faulted ? ValidationResult.Failure(CommandErrorCode.UnknownCommand, state.EffectRuntime.LastFaultCode) : ValidationResult.Success; });
            for (int n = 0; n < 2; n++) Assert.That(handler.Handle(fixture.State, new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 }).Succeeded, Is.True);
            Assert.That(fixture.State.FinalScoring.IsResolved, Is.False); Assert.That(fixture.State.FindPlayer(1).Score, Is.Zero);
            var request = fixture.State.EffectRuntime.InteractionRequests.First(r => r.Status == "open");
            Assert.That(fixture.Engine().TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId == 1 ? 2 : 1, request.StateRevision, NormalizedValue.CreateString("take"), out _), Is.False);
            while (fixture.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"))
            {
                request = fixture.State.EffectRuntime.InteractionRequests.First(r => r.Status == "open");
                Assert.That(fixture.Engine().TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, NormalizedValue.CreateString("take"), out string reason), Is.True, reason); fixture.Pump();
            }
            Assert.That(fixture.State.FinalScoring.IsResolved, Is.True); Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(2));
        }
        [Test] public void RelatedMapChangeAfterSpecialStopsRatherThanRecomputingAndKeepsFirstIncome()
        {
            fixture = new FinalScoringTestFixture(eventScript: ScoreAndIron, includeCities: true);
            var start = fixture.Definition.Locations.First(l => l.CanDockCity && fixture.Map.GetAdjacentLocations(l.LocationId).Any(t => t.CanDockCity));
            var destination = fixture.Map.GetAdjacentLocations(start.LocationId).First(l => l.CanDockCity);
            var parent = fixture.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == start.LocationId);
            var dock = fixture.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.city_dock" && z.ParentId == parent.ZoneId);
            var city = fixture.State.ItemZones.Items.Single(i => i.TypeId == "core.mobile_city" && i.OwnerSubject.PlayerId == 1);
            // 仅隔离初始位置；冻结后的位置变化必须通过真实Commit及统一转移。
            typeof(YC.Domain.Items.ItemZoneStateMigrator).GetMethod("SetPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { fixture.State, city, dock.ZoneId });
            CityPositionCommitEffectExecutor.Register(fixture.Registry, fixture.Map);
            var original = fixture.Registry.Get(FinalScoringTestFixture.RootType); bool requested = false;
            const string probeType = "qy007.final.map_change_probe";
            fixture.Registry.Register(new EffectRegistration(probeType, context => {
                if (context.Node.FlowStage == FinalScoringSessionService.RegionCommit && !requested)
                {
                    requested = true;
                    return EffectStepResult.Continue(context.Node.FlowStage).AddChild(CityPositionCommitEffectSpecFactory.Commit(1,
                        start.LocationId, destination.LocationId, fixture.Map.FindRoute(start.LocationId, destination.LocationId).RouteId));
                }
                return original.Executor(context);
            }, original.ExecutorKind, original.DefinitionVersion));
            fixture.Artifacts.WriteLabeledText("冻结事实故障定位范围", "隔离终局依赖探针：在特殊收益后插入真实位置Commit子树；不是正常终局允许玩家移动或正常页面验收。");
            fixture.OwnerId = fixture.Engine().CreateRoot(EffectSpec.Create(probeType, playerId: 1));
            fixture.Until(() => fixture.Owner.FlowStage == FinalScoringSessionService.RegionCommit);
            var report = fixture.Engine().RunUntilQuiescent();
            var committed = fixture.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == CityMoveEffectTypeIds.CommitPosition);
            Assert.That(committed.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(committed.PositionChanges.Count, Is.EqualTo(1));
            Assert.That(fixture.State.FindPlayer(1).CityLocationId, Is.EqualTo(destination.LocationId));
            Assert.That(fixture.State.EffectRuntime.LastFaultMessage, Does.Contain("final_scoring_frozen_control_changed"));
            Assert.That(report.Faulted, Is.True); Assert.That(fixture.State.FinalScoring.IsResolved, Is.False); Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(fixture.State.FindPlayer(1).Resources.Iron, Is.EqualTo(1)); Assert.That(fixture.State.FinalScoring.Session.RegionCommitReceipt, Is.Empty);
            fixture.Artifacts.WriteLabeledText("首次冻结依赖变化故障", JsonConvert.SerializeObject(fixture.State));
        }
        [Test] public void ScoreOnlyRevisionChangesDoNotInvalidateFrozenControl()
        {
            fixture = new FinalScoringTestFixture(eventScript: ScoreAndIron); fixture.Start(); fixture.Until(() => fixture.Owner.FlowStage == FinalScoringSessionService.Special && fixture.State.FinalScoring?.Session?.Control != null);
            string digest = fixture.State.FinalScoring.Session.Control.BasisDigest; fixture.Pump();
            Assert.That(fixture.State.FinalScoring.Session.Control.BasisDigest, Is.EqualTo(digest)); Assert.That(fixture.State.FinalScoring.IsResolved, Is.True);
            Assert.That(fixture.State.FinalScoring.Session.ProcessedSourceReceiptIds.Count, Is.EqualTo(2));
        }
        [Test] public void RegionValueBelongsToActualOpponentAndIndependentSourceRewardRemainsSeparate()
        {
            string query = "return function(ctx) local items={}; if ctx.payload.authorizedPlayerId==1 then items={{kind='region_value_delta',regionId=ctx.payload.facts.regionId,delta=1,reasonRef='region'}} end; return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items=items} end";
            string special = "return function(ctx) if ctx.payload.authorizedPlayerId~=1 then return {} end; return {Effect.GainScore({recipient=ctx.playerId,amount=1})} end";
            fixture = new FinalScoringTestFixture(new[] { (query, new[] { "region_value_delta" }) }, special);
            fixture.PlacePlayer(2, 1); fixture.Start(); fixture.Pump();
            var region = fixture.State.FinalScoring.Session.Control.Regions.Single(r => r.RegionId == fixture.RegionId);
            Assert.That(region.ControllerPlayerId, Is.EqualTo(2)); Assert.That(fixture.State.FindPlayer(2).Score, Is.EqualTo(region.BaseScoreValue + 1));
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(fixture.State.FinalScoring.Session.Players.Single(p => p.PlayerId == 1).RegionScore, Is.Zero);
        }
        [Test] public void EmptySourceGetsProcessedReceiptWithoutInventedZeroScoreEffect()
        {
            fixture = new FinalScoringTestFixture(eventScript: "return function(ctx) return {} end"); fixture.Start(); fixture.Pump();
            Assert.That(fixture.State.FinalScoring.Session.ProcessedSourceReceiptIds.Count, Is.EqualTo(2)); Assert.That(fixture.State.FinalScoring.Session.Effects, Is.Empty);
            Assert.That(fixture.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ResourceEffectTypeIds.GainScore), Is.False);
        }
        [Test] public void ActualLoseScoreIsArchivedAsNetChangeAndNeverAppliedTwice()
        {
            fixture = new FinalScoringTestFixture(eventScript: "return function(ctx) return {Effect.LoseScore({player=ctx.playerId,amount=1})} end");
            fixture.State.FindPlayer(1).Score = 3; fixture.Start(); fixture.Pump(); fixture.Pump();
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(2)); Assert.That(fixture.State.FinalScoring.Session.Players.Single(p => p.PlayerId == 1).SpecialScore, Is.EqualTo(-1));
            Assert.That(fixture.State.FinalScoring.Session.Effects.Single(e => e.PlayerId == 1).ScoreDelta, Is.EqualTo(-1));
        }
        [Test] public void ZeroIncomeFactsUseRealContinuationWithoutFakeGainScore()
        {
            // 依赖整合接入Global.Scoring读取；未接入应失败，不在夹具注入替身查询。
            const string script = @"return function(ctx) if ctx.eventType=='EffectCompleted' then return {} end
              local f=Global.Scoring.GetFinalControl()
              return {Effect.WithContext(Effect.Repeat({apiVersion=1,count=0,effects={}}),
                {scoringEvidence={sessionId=f.sessionId,snapshotId=f.snapshotId,count=0}})} end";
            fixture = new FinalScoringTestFixture(eventScript: script, completionHandler: true); fixture.Start(); fixture.Pump();
            Assert.That(fixture.State.FinalScoring.Session.Facts.Count, Is.EqualTo(2)); Assert.That(fixture.State.FinalScoring.Session.Effects, Is.Empty);
            Assert.That(fixture.State.FinalScoring.Session.Facts.All(f => f.Evidence.Properties.Single(p => p.Name == "count").Value.IntegerValue == 0), Is.True);
            Assert.That(fixture.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ResourceEffectTypeIds.GainScore), Is.False);
        }
    }
}
