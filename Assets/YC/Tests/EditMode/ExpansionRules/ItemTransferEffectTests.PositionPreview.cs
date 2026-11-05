using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        // 隔离写集核验，不是正常页面或合法行动覆盖。
        [Test]
        public void PositionPreviewMatchesFullCloneApplyAndDoesNotRebindOrMutateItsSource()
        {
            var source=State(3);string id=Ids(source,"hand",1)[0];
            var item=source.ItemZones.Items.Single(i=>i.ItemId==id);
            var persistent=source.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.character_persistent" && z.OwnerPlayerId==1);
            item.ZoneId=persistent.ZoneId;
            source.CharacterUses.Add(new CharacterUseRuntimeState{CardInstanceId=id,Modes={new CharacterModeContract{SubscriptionActive=true}}});
            source.CharacterCoverSlots.Add(new CoverSlotState{CardInstanceId=id,SlotId="test.slot"});
            source.FacilityCharacterBindings.Add(new FacilityCharacterBindingState{CharacterItemId=id,Active=true});
            source.FacilityCharacterBindings.Add(new FacilityCharacterBindingState{FacilityItemId=id,ControllerPlayerId=1,Active=true});
            var original=GameStateCloneService.DeepClone(source);var expected=GameStateCloneService.DeepClone(source);
            var method=typeof(GameState).GetMethod("CreateItemTransferPreview",BindingFlags.Instance|BindingFlags.NonPublic);
            var preview=(GameState)method.Invoke(source,null);
            var request=new ItemTransferRequest{ExecutingPlayerId=1,ItemIds={id},RequiredCount=1,
                ExpectedSource=new ItemPositionRef{ZoneId=item.ZoneId},Destination=new ZoneRef{ZoneId=Zone(source,"hand",2).ZoneId},InsertionKind="unordered"};
            var apply=typeof(ItemTransferService).GetMethod("Apply",BindingFlags.Static|BindingFlags.NonPublic);
            apply.Invoke(null,new object[]{expected,request,true});apply.Invoke(null,new object[]{preview,request,true});
            Assert.That(GameStateCloneService.AreEquivalent(preview,expected),Is.True);
            Assert.That(GameStateCloneService.AreEquivalent(source,original),Is.True);
            Assert.That(source.Players[0].HandCardIds.ToArray(),Is.EqualTo(original.Players[0].HandCardIds.ToArray()));
            Assert.That(preview.Players[1].HandCardIds.ToArray(),Is.EqualTo(expected.Players[1].HandCardIds.ToArray()));
            Assert.That(source.CharacterUses[0].Modes[0].SubscriptionActive,Is.True);
            Assert.That(preview.CharacterUses[0].Modes[0].SubscriptionActive,Is.False);
            Assert.That(preview.CharacterCoverSlots[0].CardInstanceId,Is.Empty);
            Assert.That(preview.FacilityCharacterBindings.All(b=>!b.Active),Is.True);
        }

        [Test]
        public void PublicPositionValidationRemainsPureForLegalCharacterTransferAndFacilityBatch()
        {
            var source=State(3);var original=GameStateCloneService.DeepClone(source);var service=new ItemTransferService();
            var character=new ItemTransferRequest{ExecutingPlayerId=1,ItemIds={Ids(source,"hand",1)[0]},RequiredCount=1,
                ExpectedSource=new ItemPositionRef{ZoneId=Zone(source,"hand",1).ZoneId},Destination=new ZoneRef{ZoneId=Zone(source,"hand",2).ZoneId},
                OperationProfileId="core.character_card_transfer",InsertionKind="unordered"};
            Assert.That(service.Validate(source,character).IsValid,Is.True);
            var facility=new ItemTransferRequest{ExecutingPlayerId=1,ItemIds={Ids(source,"facility_deck")[0]},RequiredCount=1,
                ExpectedSource=new ItemPositionRef{ZoneId=Zone(source,"facility_deck").ZoneId},Destination=new ZoneRef{ZoneId=Zone(source,"facility_supply_0").ZoneId},
                OperationProfileId="core.facility_card_transfer",InsertionKind="top"};
            var batch=ItemTransferService.CreateBatch(source,1,"isolated.preview.batch",facility);
            var validation=service.ValidateBatch(source,batch);Assert.That(validation.IsValid,Is.True,validation.ReasonCode);
            Assert.That(GameStateCloneService.AreEquivalent(source,original),Is.True);
        }
    }
}
