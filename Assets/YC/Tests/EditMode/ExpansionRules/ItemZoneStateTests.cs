using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using System.Reflection;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ItemZoneStateTests
    {
        private static GameMapDefinition Map() => new GameMapDefinition
        {
            MapId = "test.entity_map", MinPlayers = 2, MaxPlayers = 4,
            Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "north" } },
            Locations = new List<MapLocationDefinition>
            {
                new MapLocationDefinition { LocationId = "harbor", RegionId = "north", CanDockCity = true, InfluenceSlotCount = 2 },
                new MapLocationDefinition { LocationId = "mine", RegionId = "north", CanDockCity = true, InfluenceSlotCount = 1 }
            },
            Routes = new List<MapRouteDefinition> { new MapRouteDefinition { RouteId = "canal", RegionId = "north", FromLocationId = "harbor", ToLocationId = "mine", InfluenceSlotCount = 1 } }
        };
        private static List<ItemRecipeEntry> Recipe() => new List<ItemRecipeEntry>
        {
            new ItemRecipeEntry { EntryId = "player_markers", DefinitionId = "component.player_marker", TypeId = "core.player_marker", Quantity = 3, PerPlayer = true, InitialZoneRole = "player_markers" },
            new ItemRecipeEntry { EntryId = "initial_character", DefinitionId = "character.example", TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" },
            new ItemRecipeEntry { EntryId = "mobile_city", DefinitionId = "component.mobile_city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" },
            new ItemRecipeEntry { EntryId = "facility_cards", DefinitionId = "facility.example", TypeId = "core.facility_card", Quantity = 3, InitialZoneRole = "facility_deck" },
            new ItemRecipeEntry { EntryId = "start_player", DefinitionId = "component.start_player", TypeId = "core.start_player_token", Quantity = 1, InitialZoneRole = "table" }
        };
        private static GameState Source() => new GameState
        {
            GameId = "game.entity_contract", MapId = "test.entity_map", ContentPackHash = "content.entity_contract",
            RuleConfiguration = new YC.Domain.Rules.GameRuleConfiguration { MapId = "test.entity_map", PlayerCount = 2, ContentPackHash = "content.entity_contract" },
            Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "甲", InfluenceSupply = 3 }, new PlayerState { PlayerId = 2, Name = "乙", InfluenceSupply = 3 } }
        };
        private static GameState State() => new ItemZoneStateFactory().CreateForSetup(Source(), Map(), Recipe());
        private static ZoneInstanceState Zone(GameState state, string local, int player = -1) => state.ItemZones.Zones.Single(zone => zone.LocalZoneId == local && (player < 0 || zone.OwnerPlayerId == player));
        private static ItemInstanceState Character(GameState state, int player = 1) => state.ItemZones.Items.Single(item => item.TypeId == "core.basic_character_card" && item.OwnerSubject.PlayerId == player);
        private static ItemInstanceState Cube(GameState state, int player = 1) => state.ItemZones.Items.First(item => item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == player);
        private static void Position(GameState state, ItemInstanceState item, string zoneId, string slotId = "")
        {
            state.ItemZones.Orders.Find(order => order.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
            item.ZoneId = zoneId;
            item.SlotId = slotId;
            state.ItemZones.Orders.Find(order => order.ZoneId == zoneId)?.ItemIds.Add(item.ItemId);
        }
        private static CardZoneDefinition Storage() => new CardZoneDefinition
        {
            LocalZoneId = "stored_cubes", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
            CapacityPolicy = "unbounded", EntryPolicyId = "core.removed_owned_influence", ExitPolicyId = "core.card_payment", CarrierExitPolicyId = "core.preserve_contents", VisibilityPolicyId = ItemZoneVisibility.Public
        };
        private static string Attach(GameState state, CardZoneDefinition declaration = null)
        {
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, Character(state).ItemId, declaration ?? Storage(), out var zone, out var reason), Is.True, reason);
            return zone;
        }
        private static void Valid(GameState state) => Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason);

        [Test] public void SetupCreatesStableRecipeInstancesAndFourRoots()
        {
            var state = State(); var other = State();
            Assert.That(state.ItemZones.Zones.Count(zone => zone.ParentKind == ZoneParentKind.Root), Is.EqualTo(4));
            Assert.That(state.ItemZones.Items.Count, Is.EqualTo(14));
            Assert.That(state.ItemZones.Items.Select(item => item.ItemId), Is.EqualTo(other.ItemZones.Items.Select(item => item.ItemId)));
            Assert.That(Zone(state, "facility_supply_5").ZoneId, Is.Not.EqualTo(Zone(state, "facility_supply_0").ZoneId));
            Assert.Throws<InvalidOperationException>(() => new ItemZoneStateFactory().CreateForSetup(state, Map(), Recipe()));
            Valid(state);
        }
        [Test] public void ParentZoneCanContainItemsAndStructuralChildren()
        {
            var state = State(); var table = Zone(state, "table");
            Assert.That(state.ItemZones.Items.Any(item => item.ZoneId == table.ZoneId), Is.True);
            Assert.That(state.ItemZones.Zones.Any(zone => zone.ParentId == table.ZoneId), Is.True);
            Valid(state);
        }
        [Test] public void UnknownParentIsRejected()
        {
            var state = State(); Zone(state, "hand", 1).ParentId = "unknown";
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.False); Assert.That(reason, Is.EqualTo("ParentId_rejected"));
        }
        [Test] public void CarrierCycleIsRejectedEvenWhenTemplateBindingsAreLegal()
        {
            var state = State(); var facility = state.ItemZones.Items.First(item => item.TypeId == "core.facility_card");
            var declaration = Storage(); declaration.LocalZoneId = "stored_facilities"; declaration.ZoneTemplateId = "core.zone.card_facility_storage";
            declaration.AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Card }; declaration.AcceptedTypeIds = new List<string> { "core.facility_card" };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, facility.ItemId, declaration, out var zone, out var reason), Is.True, reason);
            Position(state, facility, zone);
            Assert.That(new ItemZoneQuery().TryValidate(state, out reason), Is.False); Assert.That(reason, Is.EqualTo("zone_cycle"));
        }
        [Test] public void DuplicateItemAndDuplicateOrderMembershipAreRejected()
        {
            var state = State(); state.ItemZones.Items.Add(GameStateCloneService.DeepClone(Cube(state)));
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.False); Assert.That(reason, Is.EqualTo("duplicate_item_id"));
            state = State(); var deck = state.ItemZones.Orders.Single(order => order.ZoneId == Zone(state, "facility_deck").ZoneId); deck.ItemIds.Add(deck.ItemIds[0]);
            Assert.That(new ItemZoneQuery().TryValidate(state, out reason), Is.False); Assert.That(reason, Is.EqualTo("Orders_membership_mismatch"));
        }
        [Test] public void CardCannotEnterCubeStorageAndSupplySlotCapacityIsEnforced()
        {
            var state = State(); var target = Attach(state); Position(state, Character(state), target);
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.False); Assert.That(reason, Is.EqualTo("acceptedItemKinds_acceptedTypeIds_rejected"));
            state = State(); var cards = state.ItemZones.Items.Where(item => item.TypeId == "core.facility_card").Take(2).ToList();
            foreach (var card in cards) Position(state, card, Zone(state, "facility_supply_0").ZoneId);
            Assert.That(new ItemZoneQuery().TryValidate(state, out reason), Is.False); Assert.That(reason, Is.EqualTo("capacity_exceeded"));
        }
        [Test] public void LegalInfluenceSlotsHaveUniqueOccupancy()
        {
            var state = State(); var zone = state.ItemZones.Zones.First(value => value.TemplateId == "core.zone.influence_slots");
            Position(state, Cube(state), zone.ZoneId, zone.LegalSlotIds[0]);
            Position(state, Cube(state, 2), zone.ZoneId, zone.LegalSlotIds[0]);
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.False); Assert.That(reason, Is.EqualTo("SlotId_rejected_or_occupied"));
        }
        [Test] public void SameCubeLosesInfluenceWhenStoredWithoutLosingItsOwner()
        {
            var state = State(); string storage = Attach(state); var cube = Cube(state); string id = cube.ItemId;
            var influence = state.ItemZones.Zones.First(zone => zone.TemplateId == "core.zone.influence_slots");
            Position(state, cube, influence.ZoneId, influence.LegalSlotIds[0]);
            Assert.That(new ItemZoneQuery().IsInfluence(state, id), Is.True);
            Position(state, cube, storage);
            Assert.That(new ItemZoneQuery().IsInfluence(state, id), Is.False); Assert.That(cube.ItemId, Is.EqualTo(id));
            Assert.That(cube.OwnerSubject.PlayerId, Is.EqualTo(1)); Assert.That(state.ItemZones.Items.Count(item => item.ItemId == id), Is.EqualTo(1)); Valid(state);
        }
        [Test] public void GenericCubeStorageAcceptsRegisteredCubeWithoutPlayerMarkerQualification()
        {
            var types = ItemTypeRegistry.CreateBuiltIn();
            Assert.That(types.TryRegister(new ItemTypeDefinition { TypeId = "test.gang_cube", BaseTypeId = "core.cube", PhysicalKind = ItemPhysicalKind.Cube, OperationProfileIds = new List<string> { "core.influence_cube" } }, out var reason), Is.True, reason);
            var state = State(); string storage = Attach(state); var cube = Cube(state); cube.TypeId = "test.gang_cube";
            // 此夹具合成的是通用 Cube，其组件来源为盒内储备，不继承玩家标记供应资格。
            cube.OriginalSupplyZoneId = Zone(state, "reserve").ZoneId;
            var query = new ItemZoneQuery(types); var influence = state.ItemZones.Zones.First(zone => zone.TemplateId == "core.zone.influence_slots");
            Position(state, cube, influence.ZoneId, influence.LegalSlotIds[0]); Assert.That(query.IsInfluence(state, cube.ItemId), Is.True); Assert.That(query.IsPlayerMarker(state, cube.ItemId), Is.False);
            Position(state, cube, storage); Assert.That(query.IsInfluence(state, cube.ItemId), Is.False); Assert.That(query.TryValidate(state, out reason), Is.True, reason);
        }
        [Test] public void CubeRejectsOriginalSupplyWithoutMatchingItsTypeAndOwner()
        {
            var state = State(); string storage = Attach(state); var cube = Cube(state);
            Position(state, cube, storage); cube.OriginalSupplyZoneId = Zone(state, "player_markers", 2).ZoneId;
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.False); Assert.That(reason, Is.EqualTo("OriginalSupplyZoneId_invalid"));
        }

        [Test] public void CarrierTransferKeepsAttachedZoneIdAndOriginalOwner()
        {
            var state = State(); string storage = Attach(state); var character = Character(state); var cube = Cube(state); Position(state, cube, storage);
            Position(state, character, Zone(state, "hand", 2).ZoneId); character.ControllerSubject = ItemSubjectRef.Player(2);
            Assert.That(Zone(state, "stored_cubes").ZoneId, Is.EqualTo(storage)); Assert.That(character.OwnerSubject.PlayerId, Is.EqualTo(1));
            Assert.That(character.ControllerSubject.PlayerId, Is.EqualTo(2)); Assert.That(state.ItemZones.Items.Count(item => item.ItemId == character.ItemId), Is.EqualTo(1));
            Assert.That(state.Players[0].HandCardIds.Count, Is.EqualTo(0)); Assert.That(state.Players[1].HandCardIds.Count, Is.EqualTo(2)); Valid(state);
        }
        [Test] public void HiddenCardZonesDoNotExposeItemIdsOrCarrierIdsToOtherPlayers()
        {
            var state = State(); Attach(state); var character = Character(state);
            var own = GameStateViewProjector.ProjectForPlayer(state, 1); var other = GameStateViewProjector.ProjectForPlayer(state, 2);
            Assert.That(own.ItemZones.Items.Any(item => item.ItemId == character.ItemId), Is.True);
            Assert.That(other.ItemZones.Items.Any(item => item.ItemId == character.ItemId), Is.False);
            Assert.That(other.ItemZones.Zones.Any(zone => zone.Zone.ParentId == character.ItemId), Is.False);
            Assert.That(other.ItemZones.Orders.Any(order => order.ZoneId == Zone(state, "facility_deck").ZoneId), Is.False);
            Assert.That(other.Players[0].HandCardCount, Is.EqualTo(1));
        }
        [Test] public void SecretRetirementShowsCurrentHolderIdentityWhileHistoryRemainsRedacted()
        {
            var state = State(); var character = Character(state); Position(state, character, Zone(state, "secret_retired").ZoneId);
            state.Logs.Add(new GameLogEntry { Message = "秘密裁牌 " + character.DefinitionId });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "retirement", PlayerId = 1, NormalizedResult = NormalizedValue.CreateString(character.ItemId) });
            foreach (var viewer in new[] { GameStateViewer.Player(1), GameStateViewer.Player(2), GameStateViewer.Spectator, GameStateViewer.Unknown })
            {
                var view = GameStateViewProjector.Project(state, viewer);
                bool holder = viewer.Role == GameStateViewerRole.Player && viewer.PlayerId == character.ControllerSubject.PlayerId;
                Assert.That(view.ItemZones.Items.Any(item => item.ItemId == character.ItemId), Is.EqualTo(holder));
                Assert.That(view.ItemZones.Zones.Single(zone => zone.Zone.LocalZoneId == "secret_retired").ItemCount, Is.EqualTo(1));
                Assert.That(view.ItemZones.Zones.Single(zone => zone.Zone.LocalZoneId == "secret_retired").IdentitiesVisible, Is.False);
                Assert.That(view.GameBox.Cards.Count, Is.EqualTo(holder ? 1 : 0));
                if (holder)
                {
                    Assert.That(view.GameBox.Cards[0].InstanceId, Is.EqualTo(character.ItemId));
                    Assert.That(view.GameBox.Cards[0].CardId, Is.EqualTo(character.DefinitionId));
                    Assert.That(view.GameBox.Cards[0].FaceUp, Is.False);
                }
                Assert.That(view.Logs.Count, Is.EqualTo(0)); Assert.That(view.Effects.Count, Is.EqualTo(0));
            }
            Assert.That(new ItemZoneQuery().Project(state, GameStateViewer.Host).Items.Any(item => item.ItemId == character.ItemId), Is.True);
        }
        [Test] public void PublicRetirementRemainsVisible()
        {
            var state = State(); var character = Character(state); Position(state, character, Zone(state, "retired").ZoneId);
            var view = GameStateViewProjector.ProjectForPlayer(state, 2);
            Assert.That(view.GameBox.Cards.Single().InstanceId, Is.EqualTo(character.ItemId)); Assert.That(view.GameBox.Cards.Single().FaceUp, Is.True);
        }
        [Test] public void QueriesAndProjectionsDoNotMutateState()
        {
            var state = State(); var before = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state, state.ContentPackHash));
            var query = new ItemZoneQuery(); query.GetTopItemIds(state, Zone(state, "facility_deck").ZoneId, 99);
            query.FindItem(state, Cube(state).ItemId).OwnerSubject.PlayerId = 2;
            query.Project(state, GameStateViewer.Player(1)); GameStateViewProjector.ProjectForPlayer(state, 1);
            Assert.That(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state, state.ContentPackHash)), Is.EqualTo(before));
            Assert.Throws<InvalidOperationException>(() => query.GetItemIds(state, "unknown"));
        }
        [Test] public void CloneAndCopyIsolateItemsOrdersContinuationAndLegacyProjections()
        {
            var state = State(); state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "node", ContinuationData = NormalizedValue.CreateObject(new List<NormalizedValueEntry> { new NormalizedValueEntry { Name = "nextIndex", Value = NormalizedValue.CreateInteger(1) } }) });
            var clone = GameStateCloneService.DeepClone(state); Character(clone).ControllerSubject.PlayerId = 2; clone.EffectRuntime.EffectNodes[0].ContinuationData.Properties[0].Value.IntegerValue = 3;
            Position(clone, Character(clone), Zone(clone, "hand", 2).ZoneId);
            Assert.That(state.Players[0].HandCardIds.Count, Is.EqualTo(1)); Assert.That(clone.Players[0].HandCardIds.Count, Is.EqualTo(0));
            Assert.That(state.EffectRuntime.EffectNodes[0].ContinuationData.Properties[0].Value.IntegerValue, Is.EqualTo(1));
            GameStateCloneService.CopyTo(state, clone); Position(clone, Character(clone), Zone(clone, "discard", 2).ZoneId);
            Assert.That(state.Players[1].HandCardIds.Count, Is.EqualTo(2)); Assert.That(clone.Players[1].HandCardIds.Count, Is.EqualTo(1));
        }
        [Test] public void LegacyWritesAreRejectedInEntityMode()
        {
            var state = State(); Assert.That(state.Players[0].HandCardIds.IsReadOnly, Is.True);
            Assert.Throws<InvalidOperationException>(() => state.Players[0].HandCardIds.Add("duplicate"));
            Assert.Throws<InvalidOperationException>(() => state.Decks.FacilityDeck.RemoveAt(0));
            Assert.Throws<InvalidOperationException>(() => state.Players[0].InfluenceSupply++);
            Assert.Throws<InvalidOperationException>(() => state.Players[0].CoveredCharacterCardId = "duplicate");
        }
        [Test] public void SnapshotJsonRoundTripRestoresConservedInstancesAndDoesNotRunRecipe()
        {
            var state = State(); Attach(state); Position(state, Character(state), Zone(state, "discard", 1).ZoneId);
            var beforeIds = state.ItemZones.Items.Select(item => item.ItemId).ToArray();
            var archive = HostRecoveryService.CreateArchive(state, state.ContentPackHash); string json = HostRecoveryService.SerializeArchive(archive);
            var recovered = HostRecoveryService.RecoverJson(json); Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.ItemZones.Items.Select(item => item.ItemId), Is.EqualTo(beforeIds));
            Assert.That(recovered.State.Players[0].DiscardCardIds.Count, Is.EqualTo(1)); Valid(recovered.State);
            Position(recovered.State, Character(recovered.State), Zone(recovered.State, "hand", 1).ZoneId);
            Assert.That(state.Players[0].DiscardCardIds.Count, Is.EqualTo(1));
        }
        [Test] public void JournalCheckpointKeepsNewPositionAndExactDeckOrder()
        {
            var state = State(); var archive = HostRecoveryService.CreateArchive(state, state.ContentPackHash);
            Position(state, Character(state), Zone(state, "discard", 1).ZoneId); RuleCommit.Apply(state, "move_character");
            HostRecoveryService.AppendJournal(archive, state, state.ContentPackHash);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive)); Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.Players[0].DiscardCardIds.Count, Is.EqualTo(1));
            Assert.That(new ItemZoneQuery().GetTopItemIds(recovered.State, Zone(state, "facility_deck").ZoneId, 3), Is.EqualTo(new ItemZoneQuery().GetTopItemIds(state, Zone(state, "facility_deck").ZoneId, 3)));
        }
        [Test] public void MissingEntityVersionCannotBeMaskedByFieldInitializer()
        {
            var state = State(); var raw = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state, state.ContentPackHash)));
            ((JObject)raw["Snapshot"]["State"]).Remove("EntitySchemaVersion");
            var recovered = HostRecoveryService.RecoverJson(raw.ToString()); Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.FaultCode, Is.EqualTo(ItemZoneStateMigrator.Unsupported));
        }
        [Test] public void FutureVersionAndUnknownContinuationShapeAreRejected()
        {
            var state = State(); var raw = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state, state.ContentPackHash)));
            raw["Snapshot"]["State"]["EntitySchemaVersion"] = 99; Assert.That(HostRecoveryService.RecoverJson(raw.ToString()).Succeeded, Is.False);
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "invalid", ContinuationData = NormalizedValue.CreateString("stage|1") });
            Assert.That(state.EffectRuntime.TryValidate(out _), Is.False);
        }
        [TestCase("parentId")][TestCase("parentKind")][TestCase("acceptsZones")][TestCase("rootId")][TestCase("AllowedChildTemplateIds")]
        public void ExternalStructuralFieldsAreRejected(string field)
        {
            var registry = ZoneTemplateRegistry.CreateBuiltIn(ItemTypeRegistry.CreateBuiltIn()); Assert.That(registry.ValidateExternalFields(new[] { "localZoneId", field }, out var reason), Is.False); Assert.That(reason, Is.EqualTo(field + "_unsupported"));
        }
        [Test] public void ExternalZonePhysicalKindAndCapacityCannotExpandTemplate()
        {
            var registry = ZoneTemplateRegistry.CreateBuiltIn(ItemTypeRegistry.CreateBuiltIn()); var declaration = Storage(); declaration.AcceptedItemKinds[0] = (ItemPhysicalKind)99;
            Assert.That(registry.ValidateCardZoneDeclaration("core.basic_character_card", declaration, out var reason), Is.False); Assert.That(reason, Is.EqualTo("acceptedItemKinds_rejected"));
            declaration = Storage(); declaration.Capacity = 4; Assert.That(registry.ValidateCardZoneDeclaration("core.basic_character_card", declaration, out reason), Is.False); Assert.That(reason, Is.EqualTo("capacity_rejected"));
            declaration = Storage(); declaration.EntryPolicyId = "any"; Assert.That(registry.ValidateCardZoneDeclaration("core.basic_character_card", declaration, out reason), Is.False);
        }
        [Test] public void TypeRegistryRejectsDuplicateUnknownParentCyclesAndUnlicensedProfiles()
        {
            var types = ItemTypeRegistry.CreateBuiltIn();
            Assert.That(types.TryRegister(new ItemTypeDefinition { TypeId = "core.cube" }, out var reason), Is.False); Assert.That(reason, Is.EqualTo("duplicate_type_id"));
            Assert.That(types.TryRegister(new ItemTypeDefinition { TypeId = "custom", BaseTypeId = "missing" }, out reason), Is.False); Assert.That(reason, Is.EqualTo("unknown_base_type"));
            Assert.That(types.TryRegister(new ItemTypeDefinition { TypeId = "custom", BaseTypeId = "custom" }, out reason), Is.False); Assert.That(reason, Is.EqualTo("type_cycle"));
            Assert.That(types.TryRegister(new ItemTypeDefinition { TypeId = "custom", BaseTypeId = "core.cube", PhysicalKind = ItemPhysicalKind.Card }, out reason), Is.False); Assert.That(reason, Is.EqualTo("physical_kind_mismatch"));
            Assert.That(types.TryRegister(new ItemTypeDefinition { TypeId = "custom", BaseTypeId = "core.cube", OperationProfileIds = new List<string> { "core.player_marker_storage" } }, out reason), Is.False); Assert.That(reason, Is.EqualTo("operation_profile_rejected"));
            types.Find("core.cube").OperationProfileIds.Add("invented"); Assert.That(types.HasOperationProfile("core.cube", "invented"), Is.False);
            types.Freeze(); Assert.That(types.TryRegister(new ItemTypeDefinition { TypeId = "custom" }, out reason), Is.False); Assert.That(reason, Is.EqualTo("registry_frozen"));
        }
        [Test] public void TemplateQueriesReturnIsolatedCopiesAndStorageHasNoInventedLimit()
        {
            var types = ItemTypeRegistry.CreateBuiltIn(); var templates = ZoneTemplateRegistry.CreateBuiltIn(types); var template = templates.Find("core.zone.card_cube_storage");
            Assert.That(template.Capacity, Is.EqualTo(-1)); template.Capacity = 4;
            Assert.That(templates.Find("core.zone.card_cube_storage").Capacity, Is.EqualTo(-1));
        }
        [Test] public void NumericScoresProduceUnclampedMarkerPositionsAtCommit()
        {
            var state = State(); var cube = Cube(state); Position(state, cube, Zone(state, "score_track").ZoneId, "score_0");
            state.Players[0].Score = -7; RuleCommit.Apply(state, "lose_score"); Assert.That(cube.SlotId, Is.EqualTo("score_-7"));
            state.Players[0].Score = 101; RuleCommit.Apply(state, "gain_score"); Assert.That(cube.SlotId, Is.EqualTo("score_101")); Valid(state);
        }
        [Test] public void MigrationRequiresExactRecipeAndDeckSequenceEvidence()
        {
            var legacy = Source(); legacy.ItemZones = new ItemZoneRuntimeState(); legacy.Players.ForEach(player => player.HandCardIds.Add("character.example"));
            Assert.That(ItemZoneStateMigrator.TryMigrate(legacy, Map(), null, out _, out var reason), Is.False); Assert.That(reason, Is.EqualTo(ItemZoneStateMigrator.Unsupported));
        }
        [Test] public void LegacySetupMigratesOnceAndPreservesTheNextDrawSequence()
        {
            var legacy = Source(); legacy.EntitySchemaVersion = 0;
            legacy.Players.ForEach(player => player.HandCardIds.Add("character.example"));
            legacy.Decks.FacilityDeck.AddRange(new[] { "facility.example", "facility.workshop", "facility.warehouse" });
            var recipe = Recipe(); recipe.Single(entry => entry.EntryId == "facility_cards").Quantity = 1;
            recipe.Add(new ItemRecipeEntry { EntryId = "workshop_card", DefinitionId = "facility.workshop", TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" });
            recipe.Add(new ItemRecipeEntry { EntryId = "warehouse_card", DefinitionId = "facility.warehouse", TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" });
            var evidence = new ItemZoneMigrationEvidence { GameId = legacy.GameId, MapId = legacy.MapId, ContentPackHash = legacy.ContentPackHash,
                RuleContractVersion = legacy.RuleConfiguration.RuleContractVersion, RuleConfiguration = legacy.RuleConfiguration.Clone(), Recipe = recipe, PlayerIds = new List<int> { 1, 2 } };
            foreach (string deck in new[] { "facility_deck", "character_deck", "event_green", "event_yellow", "event_red" })
                evidence.Decks.Add(new LegacyDeckEvidence { LocalZoneId = deck, BottomToTopDefinitionIds = deck == "facility_deck" ? legacy.Decks.FacilityDeck.ToList() : new List<string>() });
            Assert.That(ItemZoneStateMigrator.TryMigrate(legacy, Map(), evidence, out var migrated, out var reason), Is.True, reason);
            var query = new ItemZoneQuery(); string deckZone = Zone(migrated, "facility_deck").ZoneId;
            var order = query.GetTopItemIds(migrated, deckZone, 3).Select(id => query.FindItem(migrated, id).DefinitionId).ToList();
            Assert.That(order, Is.EqualTo(new[] { "facility.warehouse", "facility.workshop", "facility.example" }));
            Assert.That(migrated.Decks.FacilityDeck, Is.EqualTo(legacy.Decks.FacilityDeck));
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(migrated, migrated.ContentPackHash)));
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); Assert.That(query.GetTopItemIds(recovered.State, deckZone, 3), Is.EqualTo(query.GetTopItemIds(migrated, deckZone, 3)));
            Assert.That(ItemZoneStateMigrator.TryMigrate(migrated, Map(), evidence, out _, out reason), Is.False);
            legacy.Decks.FacilityDeck.RemoveAt(0); evidence.Decks[0].BottomToTopDefinitionIds.RemoveAt(0);
            legacy.Map.Facilities.Add(new FacilityPlacement { ContentInstanceId = "legacy.facility", FacilityCardId = "facility.example", PlayerId = 1, CityBoardSlotIndex = 0 });
            legacy.Map.FacilityInstances.Add(new FacilityInstanceRuntimeState { ContentInstanceId = "legacy.facility", FacilityCardId = "facility.example", OwnerPlayerId = 1, CityBoardSlotIndex = 0, ActivationCount = 1 });
            Assert.That(ItemZoneStateMigrator.TryMigrate(legacy, Map(), evidence, out migrated, out reason), Is.True, reason);
            Assert.That(migrated.Map.FacilityInstances.Single().ContentInstanceId, Is.EqualTo(migrated.Map.Facilities.Single().ContentInstanceId));
            Assert.That(migrated.Map.FacilityInstances.Single().ActivationCount, Is.EqualTo(1));
            Assert.That(migrated.Map.FacilityInstances.Single().CityBoardSlotIndex, Is.EqualTo(0));
            evidence.Decks[0].BottomToTopDefinitionIds.Reverse(); Assert.That(ItemZoneStateMigrator.TryMigrate(legacy, Map(), evidence, out _, out reason), Is.False);
            legacy.Round = 1; Assert.That(ItemZoneStateMigrator.TryMigrate(legacy, Map(), evidence, out _, out reason), Is.False);
        }
        [Test] public void MissingConfigurationAndJournalEntityVersionAreRejectedFromRawInput()
        {
            var state = State(); var archive = HostRecoveryService.CreateArchive(state, state.ContentPackHash);
            RuleCommit.Apply(state, "checkpoint"); HostRecoveryService.AppendJournal(archive, state, state.ContentPackHash);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(archive)); ((JObject)raw["Journal"]["Entries"][0]["StateAfter"]).Remove("EntitySchemaVersion");
            Assert.That(HostRecoveryService.RecoverJson(raw.ToString()).FaultCode, Is.EqualTo(ItemZoneStateMigrator.Unsupported));
            raw = JObject.Parse(HostRecoveryService.SerializeArchive(archive)); ((JObject)raw["Snapshot"]["State"]).Remove("RuleConfiguration");
            Assert.That(HostRecoveryService.RecoverJson(raw.ToString()).FaultCode, Is.EqualTo(ItemZoneStateMigrator.Unsupported));
        }
        [Test] public void ProductionContentLoaderRejectsExternalStructuralFields()
        {
            var validate = typeof(ExternalContentPack).GetMethod("ValidateItemZoneDeclaration", BindingFlags.NonPublic | BindingFlags.Static);
            var definition = new ExternalContentDefinition { DefinitionId = "character.storage", ContentType = "character", CardZones = new List<CardZoneDefinition> { Storage() } };
            var raw = JObject.Parse("{\"cardZones\":[{\"localZoneId\":\"stored_cubes\",\"acceptsZones\":true}]}");
            var error = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, new object[] { raw, definition }));
            Assert.That(error.InnerException.Message, Does.Contain("acceptsZones_unsupported"));
            raw = JObject.Parse("{\"cardZones\":[{\"localZoneId\":\"stored_cubes\",\"capacityPolicy\":\"unbounded\",\"capacity\":null}]}");
            error = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, new object[] { raw, definition }));
            Assert.That(error.InnerException.Message, Does.Contain("capacity_rejected"));
            raw = JObject.Parse("{\"cardZones\":[{\"localZoneId\":\"stored_cubes\"}]}");
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { raw, definition }));
        }
        [Test] public void ContentLoaderKeepsInstalledBaseDefinitionsCompatible()
        {
            var pack = ExternalContentPack.Load(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Assets", "StreamingAssets", "Content", "core"));
            Assert.That(pack.Definitions.Count, Is.GreaterThan(0)); Assert.That(pack.Definitions.All(definition => definition.CardZones != null), Is.True);
        }
        [Test] public void SetupCreatesDeclaredCardZonesForEveryPhysicalCopyAtomically()
        {
            var source = Source(); var declarations = new[] { new ItemCardZoneDeclaration { DefinitionId = "character.example", CardZones = new List<CardZoneDefinition> { Storage() } } };
            var state = new ItemZoneStateFactory().CreateForSetup(source, Map(), Recipe(), declarations);
            Assert.That(state.ItemZones.Zones.Count(zone => zone.TemplateId == "core.zone.card_cube_storage"), Is.EqualTo(2));
            Assert.That(state.ItemZones.Zones.Where(zone => zone.TemplateId == "core.zone.card_cube_storage").Select(zone => zone.ParentId).Distinct().Count(), Is.EqualTo(2));
            declarations[0].CardZones.Add(Storage());
            Assert.Throws<InvalidOperationException>(() => new ItemZoneStateFactory().CreateForSetup(source, Map(), Recipe(), declarations));
            Assert.That(source.ItemZones.Items.Count, Is.EqualTo(0)); Assert.That(source.ItemZones.Zones.Count, Is.EqualTo(0));
        }
        [Test] public void BoundedCardZoneConfigurationSurvivesHostSerialization()
        {
            var state = State(); var storage = Storage(); storage.CapacityPolicy = "bounded"; storage.Capacity = 1;
            var zoneId = Attach(state, storage); Position(state, Cube(state), zoneId);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.ItemZones.Zones.Single(zone => zone.ZoneId == zoneId).Configuration.Capacity, Is.EqualTo(1));
            Position(recovered.State, Cube(recovered.State, 2), zoneId);
            Assert.That(new ItemZoneQuery().TryValidate(recovered.State, out var reason), Is.False); Assert.That(reason, Is.EqualTo("capacity_exceeded"));
        }
        [Test] public void EffectStagingHasARealOwnerAndMustBeEmptyAtCompletion()
        {
            var state = State(); state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "removal_response", PlayerId = 1 });
            var declaration = Storage(); declaration.ZoneTemplateId = "core.zone.effect_staging"; declaration.LocalZoneId = "removed_influence"; declaration.VisibilityPolicyId = ItemZoneVisibility.Owner;
            var factory = new ItemZoneStateFactory(); Assert.That(factory.TryCreateEffectStaging(state, "unknown", declaration, out _, out var reason), Is.False);
            Assert.That(factory.TryCreateEffectStaging(state, "removal_response", declaration, out var zoneId, out reason), Is.True, reason);
            Position(state, Cube(state), zoneId); Valid(state);
            Assert.That(new ItemZoneQuery().Project(state, GameStateViewer.Player(2)).Items.Any(item => item.ZoneId == zoneId), Is.False);
            state.EffectRuntime.EffectNodes[0].Status = EffectNodeStatus.Completed;
            Assert.That(new ItemZoneQuery().TryValidate(state, out reason), Is.False); Assert.That(reason, Is.EqualTo("staging_not_cleared"));
        }
        [Test] public void GrantedTokenCreationUsesEffectRecipeAndRejectsRepeatedOrOverCapacityCreation()
        {
            var state = State(); state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "road_creation", PlayerId = 1 });
            var factory = new ItemZoneStateFactory(); var road = Zone(state, "road");
            Assert.That(factory.TryCreateGrantedItem(state, "road_creation", "placed_road", "enterprise.component.road", "core.road_token", road.ZoneId, ItemSubjectRef.Player(1), out var itemId, out var reason), Is.True, reason);
            Assert.That(factory.TryCreateGrantedItem(state, "road_creation", "placed_road", "enterprise.component.road", "core.road_token", road.ZoneId, ItemSubjectRef.Player(1), out _, out reason), Is.False); Assert.That(reason, Is.EqualTo("duplicate_item_id"));
            int count = state.ItemZones.Items.Count;
            Assert.That(factory.TryCreateGrantedItem(state, "road_creation", "another_road", "enterprise.component.road", "core.road_token", road.ZoneId, ItemSubjectRef.Player(1), out _, out reason), Is.False); Assert.That(reason, Is.EqualTo("capacity_exceeded"));
            Assert.That(state.ItemZones.Items.Count, Is.EqualTo(count)); Assert.That(state.ItemZones.Items.Count(item => item.ItemId == itemId), Is.EqualTo(1)); Valid(state);
        }
        [Test] public void LegacyRawWritesCannotBeSilentlyDiscardedDuringCloneOrSave()
        {
            var state = State(); state.Players[0].HandCardIds.Values.Add("shadow_card");
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(state));
            Assert.Throws<InvalidOperationException>(() => HostRecoveryService.CreateArchive(state));
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.False); Assert.That(reason, Is.EqualTo("legacy_position_write_rejected"));
            state = State(); state.Players[0].LegacyInfluenceSupply = 10;
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(state));
        }
        [Test] public void FacilityBehaviorUsesTheSameItemAndDerivedPositionAfterTransferAndRecovery()
        {
            var state = State(); var item = state.ItemZones.Items.First(value => value.TypeId == "core.facility_card");
            Position(state, item, Zone(state, "city_cell_0", 1).ZoneId); item.ControllerSubject = ItemSubjectRef.Player(1);
            YC.Domain.Facilities.FacilityInstanceStateService.EnsureAll(state);
            var behavior = state.Map.FacilityInstances.Single(); behavior.ActivationCount = 2;
            Assert.That(behavior.ContentInstanceId, Is.EqualTo(item.ItemId)); Assert.That(behavior.CityBoardSlotIndex, Is.EqualTo(0));
            Assert.Throws<InvalidOperationException>(() => behavior.OwnerPlayerId = 2);
            Position(state, item, Zone(state, "city_cell_3", 2).ZoneId); item.ControllerSubject = ItemSubjectRef.Player(2);
            Assert.That(behavior.OwnerPlayerId, Is.EqualTo(2)); Assert.That(behavior.CityBoardSlotIndex, Is.EqualTo(3)); Valid(state);
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), new YC.Domain.Effects.EffectRegistry());
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            var restoredBehavior = restored.State.Map.FacilityInstances.Single();
            Assert.That(restoredBehavior.OwnerPlayerId, Is.EqualTo(2)); Assert.That(restoredBehavior.CityBoardSlotIndex, Is.EqualTo(3)); Assert.That(restoredBehavior.ActivationCount, Is.EqualTo(2));
            Position(state, item, Zone(state, "secret_retired").ZoneId);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).Map.FacilityInstances, Is.Empty);
            behavior.LegacyCityBoardSlotIndex = 7;
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(state));
        }
    }
}
