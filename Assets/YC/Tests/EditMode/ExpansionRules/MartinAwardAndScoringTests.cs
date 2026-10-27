using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class MartinAwardAndScoringTests : MartinContentTestFixture
    {
        [TestCase("M09")] [TestCase("M10")]
        public void SelectedActualImmediateGrantsEachOpponentOnceWithoutRandomOrSelfReward(string face)
        {
            var state=State("entrepreneur.round.M.5"); SelectFace(state,"entrepreneur.round.M.5",face);
            string random=JsonConvert.SerializeObject(state.RandomRuntime); int vouchers=state.FindPlayer(1).Resources.GoldVoucher;
            Immediate(state,face);
            // M09真实Place若有交互，沿真实候选逐步回答；无合法目标仍继续10券。
            for(int i=0;i<20 && state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open");i++)
                Answer(state,NormalizedValue.CreateString(Open(state).CandidateIds.First()));
            var awards=state.ItemZones.Items.Where(i=>i.DefinitionId=="entrepreneur.award.M01").ToList();
            Assert.That(awards.Count,Is.EqualTo(2)); Assert.That(awards.Select(i=>i.ControllerSubject.PlayerId).OrderBy(x=>x),Is.EqualTo(new[]{2,3}));
            Assert.That(awards.All(i=>ContentLifecycleEffectExecutor.IsHeld(state,i,i.ControllerSubject.PlayerId)),Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher-vouchers,Is.EqualTo(10));
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime),Is.EqualTo(random));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence),Is.EqualTo(face=="M09"?1:0));
        }
        [Test] public void M07EmitsOneScoreAndIronPerCoveredCellRatherThanPerLayer()
        {
            var state=State(); Position(state,Card(state,"reserve_001"),Cell(state,0).CellZoneId);
            Overlay(state,Card(state,"building_015"),0);
            Position(state,Card(state,"building_016"),Cell(state,1).CellZoneId);
            Overlay(state,Card(state,"building_017"),1); Overlay(state,Card(state,"entrepreneur.facility.M02"),1);
            var r=Invoke(state,"entrepreneur.face.M07",Rounds,Object("abilityId",NormalizedValue.CreateString("entrepreneur.face.M07")));
            Assert.That(r.IsSuccess,Is.True,r.Diagnostic); Assert.That(r.Effects.Count,Is.EqualTo(2));
            Assert.That(r.Effects[0].Amount,Is.EqualTo(2)); Assert.That(r.Effects[1].Amount,Is.EqualTo(2));
            Assert.That(r.Effects[1].ResourceTypeId,Is.EqualTo("iron"));
        }
        [Test] public void M08UsesActualMaximumLayerCountAndStableSingleCellOnTie()
        {
            var state=State();
            foreach(int slot in new[]{1,0}) { Position(state,Card(state,"building_015"),Cell(state,slot).CellZoneId); Overlay(state,Card(state,"building_016"),slot); }
            var r=Invoke(state,"entrepreneur.face.M08",Rounds,Object("abilityId",NormalizedValue.CreateString("entrepreneur.face.M08")));
            Assert.That(r.IsSuccess,Is.True,r.Diagnostic); var score=r.Effects.Single();
            Assert.That(score.Amount,Is.EqualTo(4)); Assert.That(Number(score.ContinuationContext,"selectedSlotIndex",-1),Is.EqualTo(0));
            Assert.That(Number(score.ContinuationContext,"facilityCount"),Is.EqualTo(2));
        }
        [TestCase("M07")] [TestCase("M08")]
        public void EmptyCityHasNoSpecialScoreOrInventedSelection(string face)
        {
            var state=State(); var r=Invoke(state,"entrepreneur.face."+face,Rounds,Object("abilityId",NormalizedValue.CreateString("entrepreneur.face."+face)));
            Assert.That(r.IsSuccess,Is.True,r.Diagnostic); Assert.That(r.Effects,Is.Empty);
        }
        [Test] public void M11TrueRetirementPromotesOnlyOneCoveredCardBeforeRewardPlacement()
        {
            var state=State("entrepreneur.round.M.7"); SelectFace(state,"entrepreneur.round.M.7","M11");
            InitializeTrack(state,"entrepreneur.round.M.7","usage.entrepreneur.M.7");
            Position(state,Card(state,"building_015"),Cell(state,0).CellZoneId); string lower=Cell(state,0).TopItemId;
            Overlay(state,Card(state,"building_016"),0); string upper=Cell(state,0).TopItemId;
            Action(state,"M11"); Assert.That(Open(state).InteractionTypeId,Is.EqualTo("action.decline_effect"));
            Answer(state,NormalizedValue.CreateString("continue"));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(q=>q.InteractionTypeId=="action.decline_effect"),Is.EqualTo(1));
            var request=Open(state); Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==request.OwnerEffectId).EffectTypeId,Is.EqualTo(FacilitySelectionEffectExecutor.PanelTypeId));
            var panel=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==request.OwnerEffectId);
            Assert.That(panel.AllowDecline,Is.False);Assert.That(request.AllowDecline,Is.True);
            var engine=new EffectTreeExecutor(state,Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString("0"),out string why),Is.True,why);
            for(int i=0;i<100&&!state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==ContentLifecycleEffectExecutor.RetireFacilityTypeId&&n.Status==EffectNodeStatus.Completed);i++) {
                Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence||n.EffectTypeId==ContentPrimitiveEffectExecutor.ChooseResources),Is.False,"退役及其响应未完成不得构造右式收益。");
                Assert.That(engine.Advance(),Is.True,engine.LastDiagnostic);
            }
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==ContentLifecycleEffectExecutor.RetireFacilityTypeId&&n.Status==EffectNodeStatus.Completed),Is.True);
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,engine.LastDiagnostic);
            Assert.That(Cell(state,0).TopItemId,Is.EqualTo(lower)); Assert.That(Cell(state,0).CoveredItemIds,Is.Empty);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==upper).ZoneId,Is.EqualTo(FindZone(state,"retired").ZoneId));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence),Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId=="effect.facility.build" || n.EffectTypeId=="effect.facility.entry"),Is.False);
        }
        [Test] public void M11EmptyPanelDoesNotGrantEitherReward()
        {
            var state=State("entrepreneur.round.M.7"); SelectFace(state,"entrepreneur.round.M.7","M11");
            InitializeTrack(state,"entrepreneur.round.M.7","usage.entrepreneur.M.7"); Action(state,"M11");
            Assert.That(Open(state).InteractionTypeId,Is.EqualTo("action.decline_effect"));Answer(state,NormalizedValue.CreateString("continue"));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(q=>q.InteractionTypeId=="action.decline_effect"),Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence || n.EffectTypeId==ContentPrimitiveEffectExecutor.ChooseResources),Is.False);
        }
        [Test] public void M11PanelCancelKeepsFacilityAndResourcesWithoutAnotherConfirmation()
        {
            var state=State("entrepreneur.round.M.7"); SelectFace(state,"entrepreneur.round.M.7","M11");
            InitializeTrack(state,"entrepreneur.round.M.7","usage.entrepreneur.M.7");Position(state,Card(state,"building_015"),Cell(state,0).CellZoneId);
            string top=Cell(state,0).TopItemId,resources=Newtonsoft.Json.JsonConvert.SerializeObject(state.FindPlayer(1).Resources);
            Action(state,"M11");Assert.That(Open(state).InteractionTypeId,Is.EqualTo("action.decline_effect"));Answer(state,NormalizedValue.CreateString("continue"));
            var panel=Open(state);Assert.That(panel.AllowDecline,Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==panel.OwnerEffectId).EffectTypeId,Is.EqualTo(FacilitySelectionEffectExecutor.PanelTypeId));
            state=Recover(state,"M11只有外层U10后的面板取消待答");Answer(state,NormalizedValue.CreateBoolean(false));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(q=>q.InteractionTypeId=="action.decline_effect"),Is.EqualTo(1));
            Assert.That(Cell(state,0).TopItemId,Is.EqualTo(top));Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(state.FindPlayer(1).Resources),Is.EqualTo(resources));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>new[]{ContentLifecycleEffectExecutor.RetireFacilityTypeId,InfluenceEffectTypeIds.PlaceInfluence,ContentPrimitiveEffectExecutor.ChooseResources}.Contains(n.EffectTypeId)),Is.False);
            Recover(state,"M11面板取消后不回盒不收益");
        }
    }
}
