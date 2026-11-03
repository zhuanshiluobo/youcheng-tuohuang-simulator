using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class CharacterLifecycleTests
    {
        [TestCase(false)][TestCase(true)]
        public void V4CommonMovementReturnsRealCarrierContentsAndKeepsCompleteReceipt(bool facilities)
        {
            var state=State();var registry=Registry(tacticType:"persistent");string card=Ids(state,"hand",1)[0];var contents=facilities?Ids(state,"facility_deck").ToArray():new[]{Ids(state,"supply",1)[0]};
            var declaration=new CardZoneDefinition{LocalZoneId="api30_contents",ZoneTemplateId=facilities?"core.zone.card_facility_storage":"core.zone.card_cube_storage",AcceptedItemKinds=new List<ItemPhysicalKind>{facilities?ItemPhysicalKind.Card:ItemPhysicalKind.Cube},AcceptedTypeIds=new List<string>{facilities?"core.facility_card":"core.player_marker"},CapacityPolicy="unbounded",OrderingKind=ZoneOrderingKind.Unordered,EntryPolicyId="core.explicit_transfer",ExitPolicyId="core.explicit_transfer",CarrierExitPolicyId="core.return_contents_before_exit",VisibilityPolicyId=ItemZoneVisibility.Owner};
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state,card,declaration,out string storage,out string reason),Is.True,reason);Cover(state,registry,card);Use(state,registry,card,"tactic");
            Run(state,registry,MoveSpec(1,contents,FindZone(state,facilities?"facility_deck":"supply",facilities?-1:1).ZoneId,storage,"unordered",contents.Length,facilities?"core.facility_card_transfer":"core.cube_storage"));
            var item=state.ItemZones.Items.Single(i=>i.ItemId==card);var args=Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("transfer"),
                "selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{card}),"expectedSource",Object("zoneId",NormalizedValue.CreateString(item.ZoneId))),"destination",Object("zoneId",NormalizedValue.CreateString(FindZone(state,"hand",2).ZoneId)),"insertion",Object("kind",NormalizedValue.CreateString("unordered")),"operationProfileId",NormalizedValue.CreateString("core.character_card_transfer"),"requiredCount",NormalizedValue.CreateInteger(1));
            var node=Run(state,registry,EffectSpec.Create(TypeId,args,1));Assert.That(Ids(state,"hand",2),Does.Contain(card));Assert.That(new ItemZoneQuery().GetItemIds(state,storage),Is.Empty);Assert.That(node.CommonItemMovementV4.Receipt.LifecycleItems.Select(f=>f.ItemId),Is.EquivalentTo(contents));Assert.That(Number(node.NormalizedResult,"movedCount"),Is.EqualTo(1));Assert.That(node.PositionChanges.Count,Is.EqualTo(contents.Length+1));
            state=Restore(state,registry);Assert.That(state.CharacterUses.Single().Modes.All(m=>!m.SubscriptionActive),Is.True);Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="character.transferred"),Is.EqualTo(1));
        }
    }
}
