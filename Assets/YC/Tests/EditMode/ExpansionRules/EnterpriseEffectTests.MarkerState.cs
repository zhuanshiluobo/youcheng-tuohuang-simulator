using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class EnterpriseEffectTests
    {
        // 隔离规则装配；不计为正常对局、界面或联机验收。
        private static void PlaceMarkerForIsolation(GameState state, string markerId, string zoneId, string slotId = "")
        {
            var marker = state.ItemZones.Items.Single(i => i.ItemId == markerId);
            state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == marker.ZoneId)?.ItemIds.Remove(markerId);
            marker.ZoneId = zoneId; marker.SlotId = slotId;
            state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zoneId)?.ItemIds.Add(markerId);
        }
        private static string RemoveCooperationMarkerForIsolation(GameState state, string enterprise, int owner)
        {
            var zone = state.ItemZones.Zones.Single(z => z.ParentId == ItemId(state, enterprise) && z.LocalZoneId == "cooperation_" + owner);
            var marker = state.ItemZones.Items.Single(i => i.ZoneId == zone.ZoneId);
            string id = marker.ItemId;
            PlaceMarkerForIsolation(state, id, marker.OriginalSupplyZoneId);
            return id;
        }
        [TestCase(0)][TestCase(2)][TestCase(5)]
        public void MissingEnterpriseMarkerHasNoCandidatesOrArrivalRewardsAndSurvivesRecovery(int beforeLevel)
        {
            var state = State(beforeLevel); var registry = Registry();
            string markerId = RemoveCooperationMarkerForIsolation(state, Business, 1);
            Assert.That(new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetSpecialCandidates(state, 1, 1, 5, new[] { ItemId(state, Business) }), Is.Empty);
            var node = Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, Business), 3), registry);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Flag(node.NormalizedResult, "changed"), Is.False);
            Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo("enterprise_cooperation_marker_missing"));
            Assert.That(Number(node.NormalizedResult, "newLevel"), Is.EqualTo(-1));
            Assert.That(node.EnterpriseArrivalReceipts, Is.Empty);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == EnterpriseEffectExecutor.LevelChangedEvent), Is.False);
            state = Restore(state, registry);
            Assert.That(Level(state), Is.EqualTo(-1));
            var marker = state.ItemZones.Items.Single(i => i.ItemId == markerId);
            Assert.That(marker.ZoneId, Is.EqualTo(marker.OriginalSupplyZoneId));
        }
        [Test]
        public void MissingMarkerDoesNotSwitchDepartmentOrPreventOtherPlayersCollectiveUpgrade()
        {
            var state = State(); var registry = Registry();
            RemoveCooperationMarkerForIsolation(state, Research, 1);
            string enterprise = ItemId(state, Research);
            string previous = new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetActiveDepartmentItemId(state, enterprise);
            var department = Run(state, Spec(LuaDomainEffectTypeIds.SwitchDepartment, 1, "enterpriseItemId", NormalizedValue.CreateString(enterprise)), registry);
            Assert.That(Text(department.NormalizedResult, "reasonCode"), Is.EqualTo("enterprise_cooperation_marker_missing"));
            Assert.That(new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetActiveDepartmentItemId(state, enterprise), Is.EqualTo(previous));
            RemoveCooperationMarkerForIsolation(state, Business, 1);
            var all = Run(state, Spec(EnterpriseEffectExecutor.UpgradeAllPlayersTypeId, 1,
                "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, Business)), "levels", NormalizedValue.CreateInteger(1)), registry);
            Assert.That(all.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Level(state, Business, 1), Is.EqualTo(-1));
            Assert.That(Level(state, Business, 2), Is.EqualTo(1));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Restore(state, registry);
        }
        [Test]
        public void EnterpriseRangeDropsMissingMarkerWithoutExpandingFrozenScope()
        {
            var state = State(); var registry = Registry();
            RemoveCooperationMarkerForIsolation(state, Business, 1);
            var node = Run(state, Spec(LuaDomainEffectTypeIds.UpgradeEnterprise, 1,
                "enterpriseItemIds", Strings(new[] { ItemId(state, Business) }), "levels", NormalizedValue.CreateInteger(1)), registry);
            Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo("no_candidates"));
            Assert.That(Level(state, Research), Is.Zero);
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            Restore(state, registry);
        }
        [TestCase(false)][TestCase(true)]
        public void ArrivalResponseCanRemoveMarkerWithoutUndoingRewardOrContinuingUpgrade(bool range)
        {
            var state = State(); var registry = Registry(); string enterprise = ItemId(state, Business);
            const string remove = "test.enterprise.return_cooperation";
            registry.Register(remove, context =>
            {
                var marker = context.State.ItemZones.Items.Single(i => i.ZoneId == context.State.ItemZones.Zones.Single(z => z.ParentId == enterprise && z.OwnerPlayerId == 1).ZoneId);
                new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = new List<string> { marker.ItemId },
                    ExpectedSource = new ItemPositionRef { ZoneId = marker.ZoneId, SlotId = marker.SlotId }, Destination = new ZoneRef { ZoneId = marker.OriginalSupplyZoneId },
                    InsertionKind = "unordered", OperationProfileId = "core.player_marker_storage", RequiredCount = 1 });
                return EffectStepResult.Completed();
            });
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.return", EnterpriseEffectExecutor.LevelChangedEvent, "", "test.return",
                context => new[] { EffectSpec.Create(remove, playerId: 1) }, EffectHandlerRole.Observer));
            var spec = range ? Spec(LuaDomainEffectTypeIds.UpgradeEnterprise, 1, "enterpriseItemIds", Strings(new[] { enterprise }), "levels", NormalizedValue.CreateInteger(3)) :
                EnterpriseEffectExecutor.UpgradeSpec(1, enterprise, 3);
            var node = Run(state, spec, registry);
            Assert.That(Level(state), Is.EqualTo(-1));
            Assert.That(Flag(node.NormalizedResult, "changed"), Is.True);
            Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo("enterprise_cooperation_marker_missing"));
            Assert.That(node.EnterpriseArrivalReceipts.Count, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == EnterpriseEffectExecutor.LevelChangedEvent), Is.EqualTo(1));
            Restore(state, registry);
        }
        [TestCase(false)][TestCase(true)]
        public void MissingLevelSentinelWithoutMatchingReasonIsRejectedOnRecovery(bool collective)
        {
            var state = State(); var registry = Registry(); RemoveCooperationMarkerForIsolation(state, Business, 1);
            var spec = collective ? Spec(EnterpriseEffectExecutor.UpgradeAllPlayersTypeId, 1, "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, Business)), "levels", NormalizedValue.CreateInteger(1)) :
                Spec(LuaDomainEffectTypeIds.UpgradeEnterprise, 1, "enterpriseItemIds", Strings(new[] { ItemId(state, Business) }), "levels", NormalizedValue.CreateInteger(1));
            // 单人先在有标记状态完成范围升级，再损坏结果，不能借缺失值放宽所有负等级。
            if (!collective)
            {
                var marker = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId);
                PlaceMarkerForIsolation(state, marker.ItemId, state.ItemZones.Zones.Single(z => z.ParentId == ItemId(state, Business) && z.OwnerPlayerId == 1).ZoneId, "level_5");
            }
            var node = Run(state, spec, registry); var archive = HostRecoveryService.CreateArchive(state);
            var resultNode = archive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == (collective ? node.ChildEffectIds[0] : node.EffectId));
            resultNode.NormalizedResult.Properties.Single(p => p.Name == "reasonCode").Value = NormalizedValue.CreateString("limit_reached");
            resultNode.NormalizedResult.Properties.Single(p => p.Name == "newLevel").Value = NormalizedValue.CreateInteger(-1);
            Assert.That(HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry).Succeeded, Is.False);
        }
        [TestCase(false)][TestCase(true)]
        public void EnterpriseLogicalTrackRejectsSecondMarkerAcrossSlotsAndSubzones(bool anotherZone)
        {
            var state = State(); string enterprise = ItemId(state, Business);
            var track = state.ItemZones.Zones.Single(z => z.ParentId == enterprise && z.OwnerPlayerId == 1);
            // 撤去结构容量后，逻辑域仍须独立阻止第二枚；不以容量冒充业务互斥。
            track.Configuration.CapacityPolicy = "unbounded"; track.Configuration.Capacity = null;
            string target = track.ZoneId;
            if (anotherZone)
            {
                var definition = GameStateCloneService.DeepClone(track.Configuration); definition.LocalZoneId = "additional_levels";
                definition.Slots = new List<string>(track.LegalSlotIds);
                Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, enterprise, definition, out target, out var reason), Is.True, reason);
                state.ItemZones.Zones.Single(z => z.ZoneId == target).OwnerPlayerId = 1;
            }
            var marker = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId);
            var request = new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = new List<string> { marker.ItemId },
                ExpectedSource = new ItemPositionRef { ZoneId = marker.ZoneId }, Destination = new ZoneRef { ZoneId = target },
                InsertionKind = "slot", DestinationSlotId = "level_4", OperationProfileId = "core.player_marker_storage", RequiredCount = 1 };
            string before = JsonConvert.SerializeObject(state);
            Assert.That(new ItemTransferService().Validate(state, request).IsValid, Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), "失败预检不能移动现存标记");
            var archive = HostRecoveryService.CreateArchive(state);
            PlaceMarkerForIsolation(state, marker.ItemId, target, "level_4");
            Assert.That(new ItemZoneQuery().TryValidate(state, out var error), Is.False);
            Assert.That(error, Is.EqualTo("player_marker_track_duplicate"));
            archive.Snapshot.State = GameStateCloneService.DeepClone(state);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), Registry());
            Assert.That(recovered.Succeeded, Is.False, "恢复不能静默丢弃、归还或补发标记");
        }
        [TestCase(true)][TestCase(false)]
        public void MarkerOwnerStillMustMatchSupplyOrPlayerTrack(bool toSupply)
        {
            var state = State();
            var marker = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 2 && i.ZoneId == i.OriginalSupplyZoneId);
            var target = toSupply ? FindZone(state, "supply", 1) : state.ItemZones.Zones.Single(z => z.ParentId == ItemId(state, Business) && z.OwnerPlayerId == 1);
            PlaceMarkerForIsolation(state, marker.ItemId, target.ZoneId, toSupply ? "" : "level_5");
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo(toSupply ? "OwnerSubject_supply_mismatch" : "OwnerSubject_track_mismatch"));
        }
        [Test]
        public void MissingMarkerCanBeReplacedWithoutFabricatingHistoryOrRewards()
        {
            var state = State(3); var registry = Registry();
            string removed = RemoveCooperationMarkerForIsolation(state, Business, 1);
            var track = state.ItemZones.Zones.Single(z => z.ParentId == ItemId(state, Business) && z.OwnerPlayerId == 1);
            var replacement = state.ItemZones.Items.First(i => i.ItemId != removed && i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId);
            PlaceMarkerForIsolation(state, replacement.ItemId, track.ZoneId, "level_2");
            Valid(state); state = Restore(state, registry);
            Assert.That(Level(state), Is.EqualTo(2));
            Assert.That(Level(state, Business, 2), Is.Zero);
            Assert.That(Level(state, Research, 1), Is.Zero);
            Assert.That(state.EffectRuntime.RuleEvents, Is.Empty);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetSpecialCandidates(state, 1, 1, 0, new[] { ItemId(state, Business) }).Select(c => c.CurrentLevel), Is.EqualTo(new[] { 2 }));
        }
        [Test]
        public void DeclarationUsageAndFirstRecordShareDomainButRepeatedScoringRecordIsIndependent()
        {
            var state = State(); string carrier = ItemId(state, Research);
            // 仅装配共同位置校验的逻辑域，不声称执行了合法宣告或获取了宣告权限。
            var item = state.ItemZones.Items.Single(i => i.ItemId == carrier);
            state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == item.ZoneId)?.ItemIds.Remove(carrier);
            item.ZoneId = FindZone(state, "retired").ZoneId;
            state.ContentDefinitions = new GameContentDefinitions { UsageTracks = new List<UsageTrackDeclaration> {
                new UsageTrackDeclaration { DefinitionId = Research, TrackId = "usage.test", CardZoneLocalId = "usage", InitializationPolicy = "public_declaration" } } };
            var zoneIds = new Dictionary<string, string>();
            foreach (string local in new[] { "usage", "declaration_1_0", "declaration_1_1", "declaration_2_0" })
            {
                var definition = new CardZoneDefinition { LocalZoneId = local, ZoneTemplateId = "core.zone.board_marker_track",
                    AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                    OrderingKind = ZoneOrderingKind.Track, Slots = new List<string> { "declared", "unused", "used" }, CapacityPolicy = "bounded", Capacity = 1,
                    VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, carrier, definition, out string id, out var reason), Is.True, reason);
                state.ItemZones.Zones.Single(z => z.ZoneId == id).OwnerPlayerId = local == "declaration_2_0" ? 2 : 1;
                zoneIds.Add(local, id);
            }
            var own = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId).Take(3).Select(i => i.ItemId).ToArray();
            var other = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 2 && i.ZoneId == i.OriginalSupplyZoneId);
            PlaceMarkerForIsolation(state, own[0], zoneIds["usage"], "used");
            PlaceMarkerForIsolation(state, own[1], zoneIds["declaration_1_1"], "declared");
            PlaceMarkerForIsolation(state, other.ItemId, zoneIds["declaration_2_0"], "unused");
            Assert.That(new ItemZoneQuery().TryValidate(state, out var validReason), Is.True, validReason);
            PlaceMarkerForIsolation(state, own[2], zoneIds["declaration_1_0"], "unused");
            Assert.That(new ItemZoneQuery().TryValidate(state, out var duplicateReason), Is.False);
            Assert.That(duplicateReason, Is.EqualTo("player_marker_track_duplicate"));
        }
    }
}
