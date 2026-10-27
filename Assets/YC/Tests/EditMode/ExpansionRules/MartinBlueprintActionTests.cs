using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class MartinBlueprintActionTests : MartinContentTestFixture
    {
        [Test] public void M05PlacesAreTwoAlreadyAttachedOrderedSiblings()
        {
            var state=State(); var r=Invoke(state,"entrepreneur.face.M05",Rounds,Object("abilityId",NormalizedValue.CreateString("entrepreneur.face.M05")));
            Assert.That(r.IsSuccess,Is.True,r.Diagnostic);var choice=r.Effects.Single();
            Assert.That(choice.NestedEffectGroups.Length,Is.EqualTo(2));Assert.That(choice.NestedEffectGroups[1].Count,Is.EqualTo(2));
            Assert.That(choice.NestedEffectGroups[1].All(e=>e.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence),Is.True);
        }
        [Test] public void M06MoveIsInsideItsPaymentConditionAndInsertionIsOutsideIt()
        {
            var state=State();var r=Invoke(state,"entrepreneur.face.M06",Rounds,Object("abilityId",NormalizedValue.CreateString("entrepreneur.face.M06")));
            Assert.That(r.IsSuccess,Is.True,r.Diagnostic);var condition=r.Effects.Single();
            Assert.That(condition.NestedEffectGroups[0].Single().EffectTypeId,Is.EqualTo(ResourceEffectTypeIds.Pay));
            Assert.That(condition.NestedEffectGroups[1].Count,Is.EqualTo(1));Assert.That(Text(condition.ContinuationContext,"stage"),Is.EqualTo("M06.after_move"));
        }
        [Test] public void M05FiltersMissingColorsThenExchangesTwoRealInstancesAtomically()
        {
            var state=State("entrepreneur.round.M.1",true);ActivateFace(state,"entrepreneur.round.M.1","M05");
            var blueprint=Blueprint(state,"entrepreneur.facility.M03");var below=Supply(state,"entrepreneur.facility.M04",0);
            var red=Supply(state,"entrepreneur.facility.M03",0);var blue=Supply(state,"entrepreneur.facility.M02",1);
            Action(state,"M05");Answer(state,NormalizedValue.CreateString("place_twice"));UntilPrompt(state,"core:entrepreneur.M.blueprint.choose_supply");
            Assert.That(Open(state).CandidateIds,Is.EqualTo(new[]{red.ItemId}));Assert.That(Open(state).CandidateIds,Does.Not.Contain(blue.ItemId));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence),Is.EqualTo(2));
            Answer(state,NormalizedValue.CreateString(red.ItemId));Assert.That(Open(state).PromptKey,Is.EqualTo("core:entrepreneur.M.blueprint.choose_card"));
            Answer(state,NormalizedValue.CreateString(blueprint.ItemId));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==red.ItemId).ZoneId,Is.EqualTo(BlueprintZone(state)));
            var order=FacilityLayerQuery.Supply(state).Single(s=>s.SlotIndex==0).ItemIds;
            Assert.That(order,Is.EqualTo(new[]{blueprint.ItemId,below.ItemId}));
            var batch=state.EffectRuntime.EffectNodes.Single(n=>n.TransferBatchRequest?.OperationId.StartsWith("martin:exchange:")==true);
            Assert.That(batch.TransferBatchRequest.Transfers.Count,Is.EqualTo(2));Assert.That(batch.Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(ItemTransferService.TryValidateBatchReceipt(batch,out string reason),Is.True,reason);
        }
        [TestCase(false)][TestCase(true)] public void M06DeclineOrInsufficientPaymentStillInsertsUnderTheChosenTop(bool insufficient)
        {
            var state=State("entrepreneur.round.M.1",true);ActivateFace(state,"entrepreneur.round.M.1","M06");
            var a=Blueprint(state,"entrepreneur.facility.M02");Blueprint(state,"entrepreneur.facility.M02");
            var lower=Supply(state,"entrepreneur.facility.M04",0);var top=Supply(state,"building_015",0);
            if(insufficient)state.FindPlayer(1).Resources.OriginiumShard=0;int shards=state.FindPlayer(1).Resources.OriginiumShard;
            Action(state,"M06");
            if(!insufficient)
            {
                var decline=Open(state).CandidateIds.FirstOrDefault(id=>id=="decline"||id=="cancel"||id=="choice.skip");
                Assert.That(decline,Is.Not.Null,"正式付款必须允许放弃左式");Answer(state,NormalizedValue.CreateString(decline));
            }
            UntilPrompt(state,"core:entrepreneur.M.blueprint.choose_supply");Answer(state,NormalizedValue.CreateString(top.ItemId));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open"&&r.PromptKey=="core:entrepreneur.M.blueprint.choose_card"),Is.False);
            var order=FacilityLayerQuery.Supply(state).Single(s=>s.SlotIndex==0).ItemIds;
            Assert.That(order.Count,Is.EqualTo(3));Assert.That(order[0],Is.EqualTo(top.ItemId));Assert.That(order[2],Is.EqualTo(lower.ItemId));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==order[1]).DefinitionId,Is.EqualTo(a.DefinitionId));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard,Is.EqualTo(shards));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==CityMoveEffectTypeIds.Move),Is.False);
        }
        [Test] public void M09NonEquivalentBlueprintsRequireChoiceThenMayMoveTheJustInsertedCardToBottom()
        {
            var state=State("entrepreneur.round.M.5",true);ActivateFace(state,"entrepreneur.round.M.5","M09");
            var urban=Blueprint(state,"entrepreneur.facility.M02");var other=Blueprint(state,"building_016");var top=Supply(state,"building_015",0);
            Action(state,"M09");Answer(state,NormalizedValue.CreateString(top.ItemId));
            Assert.That(Open(state).PromptKey,Is.EqualTo("core:entrepreneur.M.blueprint.choose_card"));
            Assert.That(Open(state).CandidateIds,Is.EquivalentTo(new[]{urban.ItemId,other.ItemId}));Answer(state,NormalizedValue.CreateString(urban.ItemId));
            Assert.That(Open(state).PromptKey,Is.EqualTo("core:entrepreneur.M.blueprint.to_bottom"));Assert.That(Open(state).CandidateIds,Does.Contain(urban.ItemId));
            Answer(state,NormalizedValue.CreateString(urban.ItemId));
            Assert.That(FacilityLayerQuery.Supply(state).Single(s=>s.SlotIndex==0).ItemIds,Is.EqualTo(new[]{top.ItemId}));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==urban.ItemId).ZoneId,Is.EqualTo(FindZone(state,"facility_deck").ZoneId));
        }
        [Test] public void M09NoColorPairStillOpensIndependentUnfilteredBottomSelection()
        {
            var state=State("entrepreneur.round.M.5",true);ActivateFace(state,"entrepreneur.round.M.5","M09");
            Blueprint(state,"entrepreneur.facility.M03");var blue=Supply(state,"building_015",0);
            Action(state,"M09");Assert.That(Open(state).PromptKey,Is.EqualTo("core:entrepreneur.M.blueprint.to_bottom"));
            Assert.That(Open(state).CandidateIds,Does.Contain(blue.ItemId));
        }
        [TestCase("M07",false)][TestCase("M08",false)][TestCase("M08",true)]
        public void ImmediateBuildUsesOnlyOwnedAdditionalTowerSourceAndOptionalRemainingHub(string face,bool withHub)
        {
            var state=State("entrepreneur.round.M.3",true,new[]{new ItemRecipeEntry {EntryId="actual-owned-additional-tower",DefinitionId="entrepreneur.facility.additional_core_tower",TypeId="core.facility_card",Quantity=1,PerPlayer=true,InitialZoneRole="supply"}});
            SelectFace(state,"entrepreneur.round.M.3",face);string hub="";
            if(withHub){var c=Card(state,"reserve_002");hub=c.ItemId;Position(state,c,state.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.box_reserve").ZoneId);}
            var expected=Registry.QuerySources(state,1).Single(s=>s.AbilityId=="entrepreneur.M13.tower_source");
            int budget=state.FindPlayer(1).RemainingMainActionsThisTurn;Immediate(state,face);
            var build=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Build);
            Assert.That(Text(build.NormalizedArguments,"sourceKey"),Is.EqualTo(expected.SourceKey));Assert.That(Text(build.NormalizedArguments,"sourceKind"),Is.EqualTo("authorized"));
            Assert.That(Text(build.NormalizedArguments,"paymentMode"),Is.EqualTo("free"));Assert.That(Flag(build.NormalizedArguments,"consumeMainAction"),Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==FacilitySelectionEffectExecutor.TypeId),Is.False,"指定塔不先选卡");
            Answer(state,NormalizedValue.CreateString("0"));
            if(withHub){Assert.That(Open(state).CandidateIds,Does.Contain(hub));Answer(state,NormalizedValue.CreateString(hub));Answer(state,NormalizedValue.CreateString("1"));}
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Build&&n.Status==EffectNodeStatus.Completed),Is.EqualTo(withHub?2:1));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn,Is.EqualTo(budget));
        }
        [Test] public void M10PaysItsOuterCostThenDirectNormalBuildUsesBlueprintSourceAndNormalFee()
        {
            var state=State("entrepreneur.round.M.5",true);ActivateFace(state,"entrepreneur.round.M.5","M10");var card=Blueprint(state,"entrepreneur.facility.M02");
            int gold=state.FindPlayer(1).Resources.GoldVoucher;var source=Registry.QuerySources(state,1).Single(s=>s.AbilityId=="entrepreneur.M13.blueprint_source");
            Action(state,"M10");Answer(state,NormalizedValue.CreateString("continue"));
            Assert.That(Open(state).InteractionTypeId,Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));Assert.That(Open(state).CandidateIds,Is.EqualTo(new[]{card.ItemId}));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold-2));PrepareGoldBuild(state,card.ItemId,BlueprintZone(state));
            Answer(state,NormalizedValue.CreateString("pay"));Answer(state,NormalizedValue.CreateString("0"));Answer(state,NormalizedValue.CreateString("p2"));
            var build=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Build);
            Assert.That(Text(build.NormalizedArguments,"sourceKey"),Is.EqualTo(source.SourceKey));
            Assert.That(Text(build.NormalizedArguments,"sourceKey"),Is.Not.EqualTo(state.EffectRuntime.AbilityActionInvocations.Single(i=>i.SourceRef.AbilityId=="entrepreneur.face.M10").SourceRef.SourceKey));
            Assert.That(Flag(build.NormalizedArguments,"consumeMainAction"),Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold-2-FacilityCardDatabase.Get(state,card.DefinitionId).GoldVoucherCost));
        }
        [Test] public void M10DeclineDoesNotOpenBuildOrChargeTheOuterCost()
        {
            var state=State("entrepreneur.round.M.5",true);ActivateFace(state,"entrepreneur.round.M.5","M10");var card=Blueprint(state,"entrepreneur.facility.M02");
            int gold=state.FindPlayer(1).Resources.GoldVoucher;Action(state,"M10");Answer(state,NormalizedValue.CreateString("decline"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==card.ItemId).ZoneId,Is.EqualTo(BlueprintZone(state)));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==FacilitySelectionEffectExecutor.TypeId||n.EffectTypeId==FacilityEntryEffectTypeIds.Build),Is.False);
        }
        [Test] public void M12EmptyNormalSupplyStillPlacesAndDoesNotTreatBlueprintsAsNormalSupply()
        {
            var state=State("entrepreneur.round.M.7",true);ActivateFace(state,"entrepreneur.round.M.7","M12");var card=Blueprint(state,"entrepreneur.facility.M02");
            int pure=state.FindPlayer(1).Resources.PureOriginium;Action(state,"M12");Answer(state,NormalizedValue.CreateString("continue"));
            var selection=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==FacilitySelectionEffectExecutor.TypeId);
            Assert.That(selection.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(state.FindPlayer(1).Resources.PureOriginium,Is.EqualTo(pure-1));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence),Is.True);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==card.ItemId).ZoneId,Is.EqualTo(BlueprintZone(state)));
        }
        [Test] public void M13SlotQueryReturnsStrictHigherScoreOverlayWithMandatoryRemoval()
        {
            var state=State(carrier:true);Position(state,Card(state,"entrepreneur.facility.additional_core_tower"),Cell(state,0).CellZoneId);OwnInfluence(state);
            var higher=Registry.FacilityBuildQueries.ResolveSlots(state,1,Card(state,"entrepreneur.facility.M02").ItemId).Single(c=>c.SlotIndex==0);
            Assert.That(higher.IsOverlay,Is.True);Assert.That(higher.RequiredResponseAbilityId,Is.EqualTo("entrepreneur.M13.overlay_response"));
            var equal=Registry.FacilityBuildQueries.ResolveSlots(state,1,Card(state,"entrepreneur.facility.additional_core_tower").ItemId);
            Assert.That(equal.Any(c=>c.SlotIndex==0),Is.False);
        }
        [Test] public void M13MandatoryRemovalSpecCannotBeDeclinedAndRequiresOneActualRemoval()
        {
            var state=State();var r=Invoke(state,"entrepreneur.M13.overlay_response",Ability,Object("abilityId",NormalizedValue.CreateString("entrepreneur.M13.overlay_response"),"buildFacts",Object("overlay",NormalizedValue.CreateBoolean(true))));
            Assert.That(r.IsSuccess,Is.True,r.Diagnostic);var e=r.Effects.Single();Assert.That(Flag(e.RawArguments,"allowDecline"),Is.False);Assert.That(Number(e.RawArguments,"requiredCount"),Is.EqualTo(1));Assert.That(e.RequiredOutcome.ExpectedValue,Is.EqualTo(1));
        }
        [Test] public void M12BuildAndPlaceRemainTwoIndependentRightSideEffectsOfOneCondition()
        {
            var state=State();var r=Invoke(state,"entrepreneur.face.M12",Rounds,Object("abilityId",NormalizedValue.CreateString("entrepreneur.face.M12")));
            Assert.That(r.IsSuccess,Is.True,r.Diagnostic);var e=r.Effects.Single();Assert.That(e.NestedEffectGroups[0].Count,Is.EqualTo(1));Assert.That(e.NestedEffectGroups[1].Count,Is.EqualTo(2));
            Assert.That(e.NestedEffectGroups[1][0].EffectTypeId,Is.EqualTo(FacilitySelectionEffectExecutor.TypeId));Assert.That(e.NestedEffectGroups[1][1].EffectTypeId,Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence));
        }
    }
}
