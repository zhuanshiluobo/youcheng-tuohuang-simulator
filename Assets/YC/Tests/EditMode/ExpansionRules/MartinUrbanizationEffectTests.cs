using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class MartinUrbanizationEffectTests : MartinContentTestFixture
    {
        [TestCase(2,"entrepreneur.facility.M02")][TestCase(1,"unrelated.facility")]
        public void CompletionFromOtherBuilderOrNonUrbanDefinitionDoesNotOfferOpponent(int builder,string definition)
        {
            var state=State();var r=Invoke(state,"entrepreneur.M13.urbanization_after",Ability,
                Object("abilityId",NormalizedValue.CreateString("entrepreneur.M13.urbanization_after"),"buildFacts",Object("builderId",NormalizedValue.CreateInteger(builder),"definitionId",NormalizedValue.CreateString(definition))),"EffectCompleted");
            Assert.That(r.IsSuccess,Is.True,r.Diagnostic);Assert.That(r.Effects,Is.Empty);
        }
        [TestCase("entrepreneur.facility.M02")][TestCase("entrepreneur.facility.M04")]
        [TestCase("building_015")][TestCase("building_016")][TestCase("building_017")]
        public void RealBuildCompletesOwnEntryThenChoiceBindsOrdinarySharedEffectsToOpponent(string definition)
        {
            var state=State(carrier:true);
            Position(state,Card(state,"reserve_001"),Cell(state,0).CellZoneId);
            Position(state,Card(state,"reserve_001"),Cell(state,0,2).CellZoneId,player:2);
            Position(state,Card(state,"entrepreneur.facility.additional_core_tower"),Cell(state,1,2).CellZoneId,player:2);
            int own=state.FindPlayer(1).Resources.GoldVoucher,other=state.FindPlayer(2).Resources.GoldVoucher;
            int pure=state.FindPlayer(2).Resources.PureOriginium;
            var card=Card(state,definition);var build=Run(state,BuildScenario(state,card,1));
            var facts=FacilityBuildJournal.Read(build);Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.EntryEffectId).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Open(state).PromptKey,Is.EqualTo("core:entrepreneur.M.M13.opponent.choose"));Assert.That(Open(state).AnsweringPlayerId,Is.EqualTo(1));
            Assert.That(Open(state).CandidateIds,Is.EquivalentTo(new[]{"p2","p3"}));Assert.That(state.FindPlayer(2).Resources.GoldVoucher,Is.EqualTo(other));
            string choice=Open(state).GetStableInteractionId();Answer(state,NormalizedValue.CreateString("p2"));
            var bound=state.EffectRuntime.EffectNodes.Where(n=>n.ExecutionBinding!=null).ToList();
            Assert.That(bound.Count,Is.GreaterThan(0));Assert.That(bound.All(n=>n.PlayerId==2&&n.ExecutionBinding.ParentBuildId==build.EffectId&&n.ExecutionBinding.ChoiceInteractionId==choice),Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Build),Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==build.EffectId).PlayerId,Is.EqualTo(1));
            if(definition=="entrepreneur.facility.M02"){Assert.That(state.FindPlayer(1).Resources.GoldVoucher-own,Is.EqualTo(12));Assert.That(state.FindPlayer(2).Resources.GoldVoucher-other,Is.EqualTo(24));}
            else if(definition=="entrepreneur.facility.M04")Assert.That(state.FindPlayer(2).Resources.PureOriginium-pure,Is.EqualTo(2));
            else {int adjacent=FacilityLayerQuery.ResolveTowerAdjacency(state,2).Sum(t=>t.AdjacentTopItemIds.Count);Assert.That(state.FindPlayer(2).Resources.GoldVoucher-other,Is.EqualTo(4*adjacent));}
        }
        [Test] public void RedOpponentReceivesTwoIndependentPlacementsAndAnswersItsOwnRequests()
        {
            var state=State(carrier:true);Position(state,Card(state,"reserve_001"),Cell(state,0,2).CellZoneId,player:2);
            Position(state,Card(state,"entrepreneur.facility.additional_core_tower"),Cell(state,1,2).CellZoneId,player:2);
            var build=Run(state,BuildScenario(state,Card(state,"entrepreneur.facility.M03"),0));Answer(state,NormalizedValue.CreateString("p2"));
            var nodes=state.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence&&n.ExecutionBinding!=null).ToList();
            Assert.That(nodes.Count,Is.EqualTo(2));Assert.That(nodes.All(n=>n.PlayerId==2),Is.True);
            for(int i=0;i<20&&state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open");i++)
            {Assert.That(Open(state).AnsweringPlayerId,Is.EqualTo(2));Answer(state,NormalizedValue.CreateString(Open(state).CandidateIds.First()));}
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==build.EffectId).PlayerId,Is.EqualTo(1));
        }
        [Test] public void OverlayScoresBeforeWrappedRemovalAndWaitsForResponsesBeforeOriginalEntry()
        {
            var state=State(carrier:true);OwnInfluence(state);Position(state,Card(state,"entrepreneur.facility.additional_core_tower"),Cell(state,0).CellZoneId);
            const string hold="qy016.removal.response";
            Registry.Register(hold,c=>c.GetLatestInteractionAnswer()!=null?EffectStepResult.Completed():EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec {
                InteractionTypeId=hold,AnsweringPlayerId=1,Visibility="owner",AnswerSchema="candidate_id",MinSelections=1,MaxSelections=1,CandidateIds={"continue"}}),EffectExecutorKind.IntrinsicFlow);
            Registry.RegisterEventHandler(new EffectEventHandlerRegistration(hold,"InfluenceRemoved","*",hold,c=>new System.Collections.Generic.List<EffectSpec>{EffectSpec.Create(hold,playerId:1)},EffectHandlerRole.Observer));
            int before=state.FindPlayer(1).Resources.GoldVoucher;var build=Run(state,BuildScenario(state,Card(state,"entrepreneur.facility.M02"),0));
            var facts=FacilityBuildJournal.Read(build);var required=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.ObligationEffectId);
            Assert.That(facts.ScoreCommitted,Is.True);Assert.That(required.EffectTypeId,Is.EqualTo(AbilitySourceEffectExecutor.TypeId));Assert.That(required.RequiredOutcome,Is.Not.Null);
            Assert.That(required.AllowDecline,Is.False);Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(before));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.EntryEffectId).FlowStage,Is.Empty);
            for(int i=0;i<20&&Open(state).InteractionTypeId!=hold;i++)Answer(state,NormalizedValue.CreateString(Open(state).CandidateIds.First()));
            Assert.That(Open(state).InteractionTypeId,Is.EqualTo(hold));Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(before));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.EntryEffectId).FlowStage,Is.Empty);
            state=Recover(state,"移除真实外部响应未结清");Answer(state,NormalizedValue.CreateString("continue"));
            Assert.That(Open(state).PromptKey,Is.EqualTo("core:entrepreneur.M.M13.opponent.choose"));
            Assert.That(state.Map.Influences.Count(i=>i.PlayerId==1),Is.EqualTo(0),"移除最后1枚后仍可继续");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher-before,Is.EqualTo(12));Assert.That(required.RequiredOutcome.ExpectedValue,Is.EqualTo(1));
            Answer(state,NormalizedValue.CreateString("p2"));
        }
        [Test] public void OverlayWithoutEntryStillRemovesOneAndCannotInventAnEntryOrOpponentChoice()
        {
            var state=State(carrier:true);OwnInfluence(state);Position(state,Card(state,"reserve_002"),Cell(state,0).CellZoneId);
            var build=Run(state,BuildScenario(state,Card(state,"entrepreneur.facility.additional_core_tower"),0));
            var facts=FacilityBuildJournal.Read(build);Assert.That(facts.EntryRequired,Is.False);Assert.That(facts.ObligationEffectId,Is.Not.Empty);
            for(int i=0;i<20&&state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open");i++)Answer(state,NormalizedValue.CreateString(Open(state).CandidateIds.First()));
            Assert.That(state.Map.Influences.Count(i=>i.PlayerId==1),Is.EqualTo(0));Assert.That(state.EffectRuntime.InteractionRequests.Any(r=>r.PromptKey=="core:entrepreneur.M.M13.opponent.choose"),Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==build.EffectId).Status,Is.EqualTo(EffectNodeStatus.Completed));
        }
        [Test] public void MissingRealM13TowerSourceCannotFallBackToPublicSameNameCard()
        {
            var state=State("entrepreneur.round.M.3");SelectFace(state,"entrepreneur.round.M.3","M07");
            var engine=BeginImmediate(state,"M07");Assert.That(engine.RunUntilQuiescent().Faulted,Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Build||n.EffectTypeId==ResourceEffectTypeIds.Pay),Is.False);
        }
        [Test] public void RealFacilityEntryCatalogExecutesTheAuthoredBlueUrbanizationBody()
        {
            var state=State(); Position(state,Card(state,"reserve_001"),Cell(state,0).CellZoneId);
            Position(state,Card(state,"entrepreneur.facility.additional_core_tower"),Cell(state,1).CellZoneId);
            var card=Card(state,"entrepreneur.facility.M02"); Position(state,card,Cell(state,2).CellZoneId);
            int before=state.FindPlayer(1).Resources.GoldVoucher;
            Run(state,YC.Domain.Effects.FacilityEntryEffectSpecFactory.Activate(1,card.DefinitionId,card.ItemId,2,"qy016.isolated"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher-before,Is.EqualTo(24));
        }
        [TestCase("M02", 12, "gold_voucher")]
        [TestCase("M04", 1, "pure_originium")]
        public void EachRealTowerIncludingCoveredTowerContributesItsPrintedAmount(string face, int unit, string resource)
        {
            var state = State(); Position(state, Card(state,"reserve_001"), Cell(state,0).CellZoneId);
            Position(state, Card(state,"entrepreneur.facility.additional_core_tower"), Cell(state,1).CellZoneId);
            Overlay(state, Card(state,"building_015"), 1);
            var r = Invoke(state, "entrepreneur." + face + ".entry", Urban,
                Object("facilityId", NormalizedValue.CreateString("entrepreneur.facility." + face)));
            Assert.That(r.IsSuccess, Is.True, r.Diagnostic); Assert.That(r.Effects.Single().Amount, Is.EqualTo(2*unit));
            Assert.That(r.Effects.Single().ResourceTypeId, Is.EqualTo(resource));
        }
        [Test] public void RedUrbanizationCreatesOneOrdinaryPlaceForEachTower()
        {
            var state = State(); Position(state, Card(state,"reserve_001"), Cell(state,0).CellZoneId);
            Position(state, Card(state,"entrepreneur.facility.additional_core_tower"), Cell(state,1).CellZoneId);
            var r = Invoke(state, "entrepreneur.M03.entry", Ability, Object("facilityId", NormalizedValue.CreateString("entrepreneur.facility.M03")));
            Assert.That(r.IsSuccess, Is.True, r.Diagnostic); Assert.That(r.Effects.Count, Is.EqualTo(2));
            Assert.That(r.Effects.All(e => e.EffectTypeId == YC.Domain.Effects.InfluenceEffectTypeIds.PlaceInfluence), Is.True);
        }
        [TestCase("M02")] [TestCase("M03")] [TestCase("M04")]
        public void NoTowerProducesNoResourceOrPlacement(string face)
        {
            var state = State(); var r = Invoke(state, "entrepreneur." + face + ".entry", Urban,
                Object("facilityId", NormalizedValue.CreateString("entrepreneur.facility." + face)));
            Assert.That(r.IsSuccess, Is.True, r.Diagnostic); Assert.That(r.Effects, Is.Empty);
        }
        [Test] public void OtherDefinitionsAreRejectedInsteadOfBorrowingAnUrbanizationEffect()
        {
            var state = State(); var r = Invoke(state, "bad", Urban, Object("facilityId", NormalizedValue.CreateString("unrelated.facility")));
            Assert.That(r.IsSuccess, Is.False); Assert.That(r.Diagnostic, Does.Contain("martin_urbanization_unknown_definition"));
        }
    }
}
