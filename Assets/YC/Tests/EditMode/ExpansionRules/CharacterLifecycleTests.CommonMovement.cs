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
        public void CommonCharacterMovementDuringModeKeepsOldExecutorAndNewHoldersHand(bool direct)
        {
            var registry = Registry(tactic: c => {
                string card = Value(c.OwnerNode.NormalizedArguments, "cardInstanceId").ReferenceId;
                var spec = CharacterCardMovement.Create(c.State, 1, card, FindZone(c.State, "hand", 2).ZoneId);
                if (direct) { spec.EffectTypeId = TypeId; spec.DefinitionVersion = TransferDefinitionVersion; spec.NormalizedArguments.Properties.Single(p => p.Name == "apiVersion").Value = NormalizedValue.CreateInteger(1); }
                return new List<EffectSpec> { spec };
            });
            var state = State(); string item = Ids(state, "hand", 1)[0]; Cover(state, registry, item); var use = Use(state, registry, item, "tactic");
            Assert.That(use.Finished, Is.True); Assert.That(use.ExecutingPlayerId, Is.EqualTo(1)); Assert.That(use.FinishDestinationZoneId, Is.EqualTo(FindZone(state, "hand", 2).ZoneId));
            Assert.That(Ids(state, "hand", 2), Does.Contain(item)); Assert.That(Ids(state, "discard", 1), Is.Empty);
            Assert.That(state.CharacterCoverSlots.Single(s => s.PlayerId == 1).CardInstanceId, Is.Empty);
            state = Restore(state, registry); Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.transferred"), Is.EqualTo(1));
        }
        [TestCase(false)][TestCase(true)]
        public void CommonCharacterMovementReturnsActualStoredCubesBeforeMovingCarrier(bool direct)
        {
            var state = State(); string card = Ids(state, "hand", 1)[0], marker = Ids(state, "supply", 1)[0];
            var declaration = new CardZoneDefinition { LocalZoneId = "api19_contents", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer",
                CarrierExitPolicyId = "core.return_contents_before_exit", VisibilityPolicyId = ItemZoneVisibility.Owner };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card, declaration, out string storage, out string reason), Is.True, reason);
            var registry = Registry(tacticType: "persistent"); Cover(state, registry, card); Use(state, registry, card, "tactic");
            Run(state, registry, MoveSpec(1, new[] { marker }, FindZone(state, "supply", 1).ZoneId, storage, "unordered", 1, "core.cube_storage"));
            var spec = CharacterCardMovement.Create(state, 1, card, FindZone(state, "hand", 2).ZoneId);
            if (direct) { spec.EffectTypeId = TypeId; spec.DefinitionVersion = TransferDefinitionVersion; spec.NormalizedArguments.Properties.Single(p => p.Name == "apiVersion").Value = NormalizedValue.CreateInteger(1); }
            var moved = Run(state, registry, spec);
            Assert.That(Number(moved.NormalizedResult, "movedCount"), Is.EqualTo(1)); Assert.That(Ids(state, "hand", 2), Does.Contain(card));
            Assert.That(Ids(state, "supply", 1), Does.Contain(marker)); Assert.That(new ItemZoneQuery().GetItemIds(state, storage), Is.Empty);
            Assert.That(state.CharacterUses.Single().Modes.All(m => !m.SubscriptionActive), Is.True);
            state = Restore(state, registry); Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.transferred"), Is.EqualTo(1));
        }
        [TestCase(false)][TestCase(true)]
        public void CommonCharacterMovementReturnsExactStoredFacilitiesBeforeTransfer(bool direct)
        {
            var state = State(); var registry = Registry(tacticType: "persistent"); string card = Ids(state, "hand", 1)[0]; var facilities = Ids(state, "facility_deck").ToArray();
            var declaration = new CardZoneDefinition { LocalZoneId = "api19_facilities", ZoneTemplateId = "core.zone.card_facility_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Card },
                AcceptedTypeIds = new List<string> { "core.facility_card" }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered,
                EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.return_contents_before_exit", VisibilityPolicyId = ItemZoneVisibility.Owner };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card, declaration, out string storage, out string reason), Is.True, reason);
            Cover(state, registry, card); Use(state, registry, card, "tactic");
            Run(state, registry, MoveSpec(1, facilities, FindZone(state, "facility_deck").ZoneId, storage, "unordered", 2, "core.facility_card_transfer"));
            state = Restore(state, registry); var spec = CharacterCardMovement.Create(state, 1, card, FindZone(state, "hand", 2).ZoneId);
            if (direct) { spec.EffectTypeId = TypeId; spec.DefinitionVersion = TransferDefinitionVersion; spec.NormalizedArguments.Properties.Single(p => p.Name == "apiVersion").Value = NormalizedValue.CreateInteger(1); }
            Run(state, registry, spec);
            Assert.That(Ids(state, "facility_deck"), Is.EquivalentTo(facilities)); Assert.That(new ItemZoneQuery().GetItemIds(state, storage), Is.Empty);
            Assert.That(Ids(state, "hand", 2), Does.Contain(card)); Restore(state, registry);
        }
    }
}
