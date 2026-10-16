using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>优化后仍须成立的复制、发布与查询生命周期合同；不依赖机器耗时阈值。</summary>
    public sealed class StatePerformanceContractTests
    {
        [Serializable]
        public class CopyBase
        {
            private List<string> inherited = new List<string>();
            public List<string> Inherited => inherited;
        }

        [Serializable]
        public struct CopyValue
        {
            public int? Count;
            public List<string> Values;
        }

        [Serializable]
        public sealed class CopyFixture : CopyBase
        {
            public readonly List<string> ReadonlyValues = new List<string>();
            public CopyValue Value;
            public CopyValue[] Array;
            public object Boxed;
            public List<CopyValue> List = new List<CopyValue>();
            [NonSerialized] public string Transient = "constructor";
        }

        [Serializable] public sealed class InvalidContainer { public Dictionary<string, int> Values = new Dictionary<string, int>(); }
        [Serializable] public sealed class CyclicDto { public CyclicDto Next; }
        [Serializable] public sealed class ObjectContainer { public object Value; }

        [Test]
        public void DeepClonePreservesPrivateBaseReadonlyNullableAndBoxedValueFieldsWithoutSharing()
        {
            var source = new CopyFixture
            {
                Value = new CopyValue { Count = 7, Values = new List<string> { "value" } },
                Array = new[] { new CopyValue { Count = null, Values = new List<string> { "array" } } },
                Boxed = new List<string> { "boxed" },
                Transient = "source"
            };
            source.Inherited.Add("base"); source.ReadonlyValues.Add("readonly");
            source.List.Add(new CopyValue { Count = 9, Values = new List<string> { "list" } });
            var clone = GameStateCloneService.DeepClone(source);

            Assert.That(clone.Value.Count, Is.EqualTo(7)); Assert.That(clone.List[0].Count, Is.EqualTo(9));
            Assert.That(clone.Array[0].Count, Is.Null); Assert.That(clone.Transient, Is.EqualTo("constructor"));
            Assert.That(clone.Inherited, Is.EqualTo(new[] { "base" }));
            Assert.That(clone.ReadonlyValues, Is.EqualTo(new[] { "readonly" }));
            clone.Inherited[0] = "changed"; clone.ReadonlyValues.Clear(); clone.Value.Values.Clear();
            clone.Array[0].Values.Clear(); clone.List[0].Values.Clear();
            ((List<string>)clone.Boxed).Clear();
            Assert.That(source.Inherited[0], Is.EqualTo("base")); Assert.That(source.ReadonlyValues.Count, Is.EqualTo(1));
            Assert.That(source.Value.Values.Count, Is.EqualTo(1)); Assert.That(source.Array[0].Values.Count, Is.EqualTo(1));
            Assert.That(source.List[0].Values.Count, Is.EqualTo(1));
            Assert.That((List<string>)source.Boxed, Is.EqualTo(new[] { "boxed" }));
        }

        [Test]
        public void DeepCloneStillRejectsPersistentDictionaryAndCycles()
        {
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(new InvalidContainer()));
            var cycle = new CyclicDto(); cycle.Next = cycle;
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(cycle));
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(new ObjectContainer { Value = new Dictionary<string, int>() }));
            var objectCycle = new ObjectContainer(); objectCycle.Value = objectCycle;
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(objectCycle));
        }

        [Test]
        public void CopyToKeepsPublishedRootAndExistingReferencesWhileIsolatingMutableContentAndNewElements()
        {
            var published = new GameState { Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } } };
            var originalPlayers = published.Players; var originalPlayer = published.Players[0]; var originalMap = published.Map;
            var work = GameStateCloneService.DeepClone(published);
            work.Players[0].Score = 4; work.Players.Add(new PlayerState { PlayerId = 2 });
            work.ContentDefinitions = new GameContentDefinitions { Components = new List<ComponentContentDefinition> { new ComponentContentDefinition { DefinitionId = "component.example" } } };
            work.SetupRecipe = new GameSetupRecipe { Entries = new List<SetupRecipeEntry> { new SetupRecipeEntry { EntryId = "recipe.example" } } };
            GameStateCloneService.CopyTo(published, work);

            Assert.That(published.Players, Is.SameAs(originalPlayers)); Assert.That(published.Players[0], Is.SameAs(originalPlayer));
            Assert.That(published.Map, Is.SameAs(originalMap)); Assert.That(GameStateCloneService.AreEquivalent(published, work), Is.True);
            work.Players[1].Name = "changed"; work.ContentDefinitions.Components[0].DefinitionId = "changed";
            work.SetupRecipe.Entries[0].EntryId = "changed";
            Assert.That(published.Players[1].Name, Is.Empty);
            Assert.That(published.ContentDefinitions.Components[0].DefinitionId, Is.EqualTo("component.example"));
            Assert.That(published.SetupRecipe.Entries[0].EntryId, Is.EqualTo("recipe.example"));
            Assert.That(GameStateCloneService.AreEquivalent(published, work), Is.False);
        }

        [Test]
        public void ReusedQuerySeesWorkCopyMovesAndCopyToAtTheSameRevision()
        {
            var state = EntityState(); var query = new ItemZoneQuery();
            var hand = Zone(state, "hand", 1); var discard = Zone(state, "discard", 1);
            string cardId = Card(state).ItemId; int revision = state.EffectRuntime.StateRevision;
            var oldIds = query.GetItemIds(state, hand.ZoneId);
            var work = GameStateCloneService.DeepClone(state);
            Move(work, Card(work), discard.ZoneId);
            Assert.That(query.GetItemIds(work, hand.ZoneId), Is.Empty);
            Assert.That(query.GetItemIds(state, hand.ZoneId), Is.EqualTo(new[] { cardId }));
            Assert.That(query.TryValidate(work, out string reason), Is.True, reason);
            GameStateCloneService.CopyTo(state, work);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(query.GetItemIds(state, hand.ZoneId), Is.Empty);
            Assert.That(query.GetItemIds(state, discard.ZoneId), Is.EqualTo(new[] { cardId }));
            Assert.That(oldIds, Is.EqualTo(new[] { cardId }));
            Assert.That(query.TryValidate(state, out reason), Is.True, reason);
            Move(state, Card(state), hand.ZoneId);
            Assert.That(query.GetItemIds(state, discard.ZoneId), Is.Empty);
            Assert.That(query.Project(state, GameStateViewer.Player(1)).Items.Single(item => item.ItemId == cardId).ZoneId, Is.EqualTo(hand.ZoneId));
        }

        [Test]
        public void ReusedQueryNeverSharesVisibilityAcrossPlayersOrAfterSecretRetirement()
        {
            var state = EntityState(); var query = new ItemZoneQuery(); var card = Card(state);
            var declaration = new CardZoneDefinition
            {
                LocalZoneId = "stored_cubes", ZoneTemplateId = "core.zone.card_cube_storage",
                AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, CapacityPolicy = "unbounded",
                EntryPolicyId = "core.removed_owned_influence", ExitPolicyId = "core.card_payment",
                CarrierExitPolicyId = "core.preserve_contents", VisibilityPolicyId = ItemZoneVisibility.Public
            };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card.ItemId, declaration, out string storage, out string reason), Is.True, reason);
            var cube = state.ItemZones.Items.First(item => item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == 1);
            Move(state, cube, storage);
            foreach (var viewer in new[] { GameStateViewer.Player(1), GameStateViewer.Player(2), GameStateViewer.Spectator, GameStateViewer.Player(1) })
            {
                var view = query.Project(state, viewer); bool owner = viewer.Role == GameStateViewerRole.Player && viewer.PlayerId == 1;
                Assert.That(view.Items.Any(item => item.ItemId == card.ItemId), Is.EqualTo(owner));
                Assert.That(view.Items.Any(item => item.ItemId == cube.ItemId), Is.EqualTo(owner));
                Assert.That(view.Zones.Any(zone => zone.Zone.ParentId == card.ItemId), Is.EqualTo(owner));
                if (owner) view.Items.Single(item => item.ItemId == card.ItemId).DefinitionId = "changed";
            }
            Assert.That(card.DefinitionId, Is.EqualTo("character.example"));
            Move(state, card, Zone(state, "secret_retired").ZoneId);
            foreach (var viewer in new[] { GameStateViewer.Player(1), GameStateViewer.Player(2), GameStateViewer.Spectator, GameStateViewer.Unknown })
            {
                var view = query.Project(state, viewer);
                Assert.That(view.Items.Any(item => item.ItemId == card.ItemId || item.ItemId == cube.ItemId), Is.False);
                Assert.That(view.Zones.Any(zone => zone.Zone.ParentId == card.ItemId), Is.False);
                Assert.That(view.Orders.Any(order => order.ItemIds.Contains(card.ItemId) || order.ItemIds.Contains(cube.ItemId)), Is.False);
                Assert.That(view.Zones.Single(zone => zone.Zone.LocalZoneId == "secret_retired").ItemCount, Is.EqualTo(1));
            }
            Assert.That(query.Project(state, GameStateViewer.Host).Items.Any(item => item.ItemId == card.ItemId), Is.True);
        }

        [Test]
        public void CustomRegistryChangesAreVisibleWithoutPollutingDefaultQueries()
        {
            var state = EntityState(); var cube = state.ItemZones.Items.First(item => item.TypeId == "core.player_marker");
            var registry = ItemTypeRegistry.CreateBuiltIn(); var custom = new ItemZoneQuery(registry); var standard = new ItemZoneQuery();
            cube.TypeId = "test.marker";
            Assert.That(custom.IsPlayerMarker(state, cube.ItemId), Is.False);
            Assert.That(registry.TryRegister(new ItemTypeDefinition { TypeId = "test.marker", BaseTypeId = "core.player_marker", PhysicalKind = ItemPhysicalKind.Cube }, out string reason), Is.True, reason);
            Assert.That(custom.IsPlayerMarker(state, cube.ItemId), Is.True);
            Assert.That(standard.IsPlayerMarker(state, cube.ItemId), Is.False);
            registry.Find("core.player_marker").BaseTypeId = "changed";
            Assert.That(custom.IsPlayerMarker(state, cube.ItemId), Is.True);
            cube.TypeId = "core.player_marker";
            standard.FindItem(state, cube.ItemId).TypeId = "changed";
            Assert.That(new ItemZoneQuery().IsPlayerMarker(state, cube.ItemId), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void FailedCommandCannotPublishWorkingCopyStateOrJournal(bool throws)
        {
            var state = new GameState { GameId = "copy.rollback", Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } } };
            var before = GameStateCloneService.DeepClone(state); var session = new GameSession(state);
            session.RegisterHandler(new FailingHandler(throws));
            var result = session.Submit(new GameCommand { CommandId = "rollback.command", PlayerId = 1 });
            Assert.That(result.Succeeded, Is.False); Assert.That(session.State, Is.SameAs(state));
            Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True);
            Assert.That(state.EffectRuntime.Journal, Is.Empty); Assert.That(state.Players[0].Score, Is.EqualTo(0));
        }

        private sealed class FailingHandler : IGameCommandHandler
        {
            private readonly bool throws;
            internal FailingHandler(bool throws) { this.throws = throws; }
            public bool CanHandle(GameCommand command) => true;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.Players[0].Score = 99;
                RuleCommit.Apply(state, command.CommandId, new RuleJournalEntry { Kind = RuleJournalEntryKind.Command, EntityId = command.CommandId });
                if (throws) throw new InvalidOperationException("transaction_failure");
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "transaction_failure"));
            }
        }

        private static GameState EntityState()
        {
            var map = new GameMapDefinition
            {
                MapId = "test.copy_map", MinPlayers = 2, MaxPlayers = 4,
                Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "north" } },
                Locations = new List<MapLocationDefinition>
                {
                    new MapLocationDefinition { LocationId = "harbor", RegionId = "north", CanDockCity = true, InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "mine", RegionId = "north", CanDockCity = true, InfluenceSlotCount = 1 }
                },
                Routes = new List<MapRouteDefinition> { new MapRouteDefinition { RouteId = "canal", RegionId = "north", FromLocationId = "harbor", ToLocationId = "mine", InfluenceSlotCount = 1 } }
            };
            var source = new GameState
            {
                GameId = "game.copy_contract", MapId = map.MapId, ContentPackHash = "content.copy_contract",
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "content.copy_contract" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, InfluenceSupply = 1 }, new PlayerState { PlayerId = 2, InfluenceSupply = 1 } }
            };
            return new ItemZoneStateFactory().CreateForSetup(source, map, new List<ItemRecipeEntry>
            {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "component.player_marker", TypeId = "core.player_marker", Quantity = 1, PerPlayer = true, InitialZoneRole = "player_markers" },
                new ItemRecipeEntry { EntryId = "characters", DefinitionId = "character.example", TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" }
            });
        }
        private static ZoneInstanceState Zone(GameState state, string local, int player = -1) => state.ItemZones.Zones.Single(zone => zone.LocalZoneId == local && (player < 0 || zone.OwnerPlayerId == player));
        private static ItemInstanceState Card(GameState state) => state.ItemZones.Items.Single(item => item.TypeId == "core.basic_character_card" && item.OwnerSubject.PlayerId == 1);
        private static void Move(GameState state, ItemInstanceState item, string zoneId)
        {
            state.ItemZones.Orders.Find(order => order.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
            item.ZoneId = zoneId; item.SlotId = string.Empty;
            state.ItemZones.Orders.Find(order => order.ZoneId == zoneId)?.ItemIds.Add(item.ItemId);
        }
    }
}
