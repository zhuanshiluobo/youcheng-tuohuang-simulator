using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfectedCharacterEffectsTests
    {
        [Test]
        public void FormalTexasUsesSharedSupplyCycleAndRefillsOnlyOriginalSlot()
        {
            var state=State();FillSupply(state);string slot=FindZone(state,"facility_supply_0").ZoneId;string selected=state.ItemZones.Items.Single(i=>i.ZoneId==slot).ItemId;
            string expected=state.ItemZones.Orders.Single(o=>o.ZoneId==FindZone(state,"facility_deck").ZoneId).ItemIds[0];int gold=state.FindPlayer(1).Resources.GoldVoucher;
            var texas=state.ItemZones.Items.First(i=>i.DefinitionId=="texas"&&i.ZoneId==FindZone(state,"hand",1).ZoneId);Run(state,EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId,playerId:1));Answer(state,texas.ItemId);Run(state,CharacterLifecycleService.CreateUseSpec(1,texas.ItemId,"strategy"));
            Assert.That(Open(state).CandidateIds,Is.EqualTo(new[]{selected}));Assert.That(Open(state).InteractionTypeId,Is.EqualTo("facility.supply.select"));Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==Open(state).OwnerEffectId).EffectTypeId,Is.EqualTo(LuaDomainEffectTypeIds.Choice));Assert.That(state.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==LuaDomainEffectTypeIds.MoveFacilityCard),Is.Empty);Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold+12));state=Restore(state);Answer(state,selected);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==expected).ZoneId,Is.EqualTo(slot));Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==LuaDomainEffectTypeIds.MoveFacilityCard&&Number(n.NormalizedArguments,"apiVersion")==4),Is.True);Assert.That(state.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==RefillTypeId).All(n=>n.PositionChanges.Count==0),Is.True);Restore(state);
        }
    }
}
