using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Items;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class GameBoxStateProjectionTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void LegacyCompletedEnterpriseSelectionOnlyRevealsOwnDiscardedBoards(bool concurrent)
        {
            var state = State();
            var candidates = new[] { "p1-keep-a", "p1-drop-a", "p1-keep-b", "p1-drop-b", "p2-keep-a", "p2-drop-a", "p2-keep-b", "p2-drop-b" };
            foreach (string id in candidates) Item(state, id, "diy.no_prefix", "core.board_card", id.Contains("keep") ? "outside" : "reserve");
            Item(state, "never-dealt", "diy.no_prefix", "core.board_card", "reserve");
            var setup = new EffectNodeRuntimeState { EffectId = "old-setup", EffectTypeId = ExpansionSetupFlow.PreparationTypeId,
                Status = EffectNodeStatus.Completed, ContinuationData = ItemTransferEffectExecutor.Object(
                    "candidateEnterpriseItemIds", ItemTransferEffectExecutor.Strings(candidates),
                    "selectedEnterpriseItemIds", ItemTransferEffectExecutor.Strings(candidates.Where(id => id.Contains("keep"))),
                    "initialEnterprisePlayerIds", NormalizedValue.CreateArray(new[] { 1, 1, 2, 2 }.Select(player =>
                        ItemTransferEffectExecutor.Object("playerId", NormalizedValue.CreateInteger(player))).ToList())) };
            state.EffectRuntime.EffectNodes.Add(setup);
            if (concurrent)
                foreach (int player in new[] { 1, 2 }) state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState {
                    EffectId = "selection-" + player, ParentEffectId = setup.EffectId, PlayerId = player,
                    EffectTypeId = ExpansionSetupFlow.EnterpriseSelectionTypeId, Status = EffectNodeStatus.Completed,
                    NormalizedArguments = ItemTransferEffectExecutor.Object("candidateEnterpriseItemIds",
                        ItemTransferEffectExecutor.Strings(candidates.Skip((player - 1) * 4).Take(4))) });
            string before = JsonConvert.SerializeObject(state);
            foreach (var viewer in new[] { GameStateViewer.Player(1), GameStateViewer.Player(2), GameStateViewer.Spectator, GameStateViewer.Unknown })
            {
                var visible = new ItemZoneQuery().Project(state, viewer);
                var box = GameBoxStateProjector.Project(state, visible, viewer);
                var expected = viewer.Role == GameStateViewerRole.Player
                    ? candidates.Where(id => id.StartsWith("p" + viewer.PlayerId + "-drop")).ToArray() : new string[0];
                Assert.That(box.Cards.Select(card => card.InstanceId), Is.EquivalentTo(expected));
                Assert.That(visible.Items.Any(item => item.ItemId.Contains("drop") || item.ItemId == "never-dealt"), Is.False);
            }
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            setup.Status = EffectNodeStatus.Blocked;
            Assert.That(GameBoxStateProjector.Project(state, new ItemZoneQuery().Project(state, GameStateViewer.Player(1)), GameStateViewer.Player(1)).Cards, Is.Empty);
        }

        private static GameState State()
        {
            var state = new GameState { GameId = "game.box_projection",
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Color = YC.Domain.Rules.PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Color = YC.Domain.Rules.PlayerColor.Blue } },
                ContentDefinitions = new GameContentDefinitions { EnterpriseDefinitionIds =
                    new List<string> { "diy.no_prefix", "board.not_created" } },
                SetupRecipe = new GameSetupRecipe
            {
                EnterpriseEntrance = true,
                Entries = new List<SetupRecipeEntry>
                {
                    // 同角色的非企业板不能被配方猜成企业；DIY 企业只依赖明确元数据。
                    new SetupRecipeEntry { DefinitionId = "board.unknown", TypeId = "core.board_card", InitialZoneRole = "reserve" }
                }
            } };
            foreach (string root in new[] { "map", "table", "players", "game_box" })
                Zone(state, root, "core.zone." + root, ZoneParentKind.Root, "");
            Zone(state, "retired", "core.zone.box_retired", ZoneParentKind.Zone, "game_box");
            Zone(state, "reserve", "core.zone.box_reserve", ZoneParentKind.Zone, "game_box", ItemZoneVisibility.Hidden);
            Zone(state, "secret", "core.zone.box_secret_retired", ZoneParentKind.Zone, "game_box", ItemZoneVisibility.SecretRetired);
            Zone(state, "outside", "core.zone.character_discard", ZoneParentKind.Zone, "players");
            ItemZoneLegacyProjection.Bind(state, true);
            return state;
        }

        private static void Zone(GameState state, string id, string template, ZoneParentKind parentKind, string parent,
            string visibility = ItemZoneVisibility.Public)
        {
            state.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = id, TemplateId = template,
                LocalZoneId = id, ParentKind = parentKind, ParentId = parent, VisibilityPolicyId = visibility });
        }

        private static ItemInstanceState Item(GameState state, string id, string definition, string type, string zone,
            int owner = -1, int controller = -1)
        {
            var item = new ItemInstanceState { ItemId = id, DefinitionId = definition,
                TypeId = type, PhysicalKind = ItemPhysicalKind.Card, ZoneId = zone,
                OwnerSubject = owner > 0 ? ItemSubjectRef.Player(owner) : new ItemSubjectRef(),
                ControllerSubject = controller > 0 ? ItemSubjectRef.Player(controller) : new ItemSubjectRef() };
            state.ItemZones.Items.Add(item);
            return item;
        }

        [Test]
        public void VisibleGameBoxUsesRealInstancesExplicitMetadataAndCarrierAncestry()
        {
            var state = State();
            Item(state, "character-z", "character.z", "core.operator_card", "retired");
            var configuredBack = Item(state, "character-a", "character.a", "core.basic_character_card", "retired", 1, 2);
            configuredBack.LocalState.Properties.Add(new NormalizedValueEntry { Name = "backColor", Value = NormalizedValue.CreateInteger(2) });
            configuredBack.LocalState.Properties.Add(new NormalizedValueEntry { Name = "faceUp", Value = NormalizedValue.CreateBoolean(false) });
            Item(state, "event", "event.revealed", "core.event_card", "retired");
            Item(state, "board", "diy.no_prefix", "core.board_card", "retired");
            Item(state, "style", "enterprise.lookalike", "core.board_card", "retired");
            Item(state, "department", "board.department", "core.board_card", "retired");
            Item(state, "facility", "facility.retired", "core.facility_card", "retired");
            Item(state, "award", "award.retired", "core.award_card", "retired");
            Item(state, "unknown-board", "board.unknown", "core.board_card", "retired");
            Item(state, "discard", "character.outside", "core.character_card", "outside");
            // 直接构造投影父链，验证区域父级与载体父级；转移许可由其它领域用例验证。
            Zone(state, "nested", "test.box_child", ZoneParentKind.Zone, "retired");
            Zone(state, "carried", "test.card_child", ZoneParentKind.Item, "board");
            Item(state, "nested-event", "event.nested", "core.event_card", "nested");
            var explicitBack = Item(state, "carried-character", "character.carried", "core.generated_character_card", "carried");
            explicitBack.LocalState.Properties.Add(new NormalizedValueEntry { Name = "backVisualKey", Value = NormalizedValue.CreateString("character_back_3") });

            var view = GameStateViewProjector.Project(state, GameStateViewer.Spectator);
            var expected = new[] { "carried-character", "character-a", "character-z", "event", "nested-event", "board" };
            Assert.That(view.GameBox.Cards.Select(card => card.InstanceId), Is.EqualTo(expected));
            Assert.That(view.GameBox.Cards.Select(card => card.Kind), Is.EqualTo(new[] { GameBoxCardKind.Character,
                GameBoxCardKind.Character, GameBoxCardKind.Character, GameBoxCardKind.Event,
                GameBoxCardKind.Event, GameBoxCardKind.EnterpriseCooperation }));
            Assert.That(view.GameBox.Cards.Single(card => card.InstanceId == "character-a").FaceUp, Is.False);
            Assert.That(view.GameBox.Cards.Single(card => card.InstanceId == "character-a").BackColor, Is.EqualTo(PlayerColor.Red),
                "基础角色牌背随原归属玩家，旧的 backColor 配置不能覆盖归属语义。");
            Assert.That(view.GameBox.Cards.Single(card => card.InstanceId == "character-z").BackVisualKey, Is.EqualTo("operator_back"));
            Assert.That(view.GameBox.Cards.Single(card => card.InstanceId == "carried-character").BackVisualKey, Is.EqualTo("character_back_3"));
            Assert.That(view.GameBox.Cards.Single(card => card.InstanceId == "board").BackVisualKey, Is.EqualTo("enterprise_back"));
            Assert.That(view.GameBox.Cards.Single(card => card.InstanceId == "event").BackVisualKey, Is.Empty);
            Assert.That(GameBoxState.ForViewer(view.GameBox, GameStateViewer.Player(2)).Cards.Single(card =>
                card.InstanceId == "character-a").CardId, Is.EqualTo("character.a"), "朝向不能去掉公开牌的查看身份。");
            Assert.That(view.GameBox.Enterprises, Is.Empty);
            var metadata = JsonConvert.DeserializeObject<GameContentDefinitions>(JsonConvert.SerializeObject(state.ContentDefinitions));
            Assert.That(GameStateCloneService.DeepClone(metadata).EnterpriseDefinitionIds,
                Is.EqualTo(state.ContentDefinitions.EnterpriseDefinitionIds), "分类元数据应随内容快照保存和克隆。");
            state.ItemZones.Items.Reverse();
            Assert.That(GameStateViewProjector.Project(state, GameStateViewer.Spectator).GameBox.Cards.Select(card => card.InstanceId), Is.EqualTo(expected));
            state.ItemZones.Items.Single(item => item.ItemId == "board").ZoneId = "outside";
            Assert.That(GameStateViewProjector.Project(state, GameStateViewer.Spectator).GameBox.Cards.Any(card =>
                card.InstanceId == "board" || card.InstanceId == "carried-character"), Is.False);
        }

        [Test]
        public void HiddenReserveAndSecretRetirementStayAnonymousAndProjectionDoesNotWriteState()
        {
            var state = State();
            Item(state, "public", "character.public", "core.character_card", "retired", 1, 1);
            Item(state, "hidden-board", "diy.no_prefix", "core.board_card", "reserve", 2, 2);
            Item(state, "hidden-owned-operator", "operator.reserve", "core.operator_card", "reserve", 2, 2);
            var own = Item(state, "own-private", "character.secret", "core.basic_character_card", "secret", 1, 2);
            Item(state, "peer-private", "character.peer", "core.basic_character_card", "secret", 2, 1);
            Item(state, "unassigned-private", "character.unassigned", "core.basic_character_card", "secret");
            Item(state, "private-operator", "operator.secret", "core.operator_card", "secret", -1, 2);
            Zone(state, "own-child", "test.card_child", ZoneParentKind.Item, "own-private");
            Item(state, "own-child-event", "event.own", "core.event_card", "own-child");
            Zone(state, "hidden-child", "test.card_child", ZoneParentKind.Item, "hidden-board");
            Item(state, "hidden-child-event", "event.secret", "core.event_card", "hidden-child");
            Zone(state, "cycle", "test.card_child", ZoneParentKind.Item, "cyclic-event");
            var cyclic = Item(state, "cyclic-event", "event.cyclic", "core.event_card", "cycle", 1, 2);
            state.ItemZones.Orders.Add(new ZoneOrderState { ZoneId = "secret", ItemIds = new List<string>
                { "own-private", "peer-private", "unassigned-private", "private-operator" } });
            state.ItemZones.Orders.Add(new ZoneOrderState { ZoneId = "own-child", ItemIds = new List<string> { "own-child-event" } });
            state.ItemZones.Relations.Add(new ItemRelationState { RelationId = "own-public", SourceItemId = "own-private", TargetItemId = "public" });
            state.ItemZones.Relations.Add(new ItemRelationState { RelationId = "cross-private", SourceItemId = "own-private", TargetItemId = "peer-private" });
            var before = JsonConvert.SerializeObject(state);
            foreach (var viewer in new[] { GameStateViewer.Player(1), GameStateViewer.Player(2), GameStateViewer.Spectator, GameStateViewer.Unknown })
            {
                var items = new ItemZoneQuery().Project(state, viewer);
                var box = GameBoxStateProjector.Project(state, items);
                bool holder = viewer.Role == GameStateViewerRole.Player && viewer.PlayerId == 2;
                var expected = holder ? new[] { "public", "own-private", "private-operator", "own-child-event" } :
                    viewer.Role == GameStateViewerRole.Player ? new[] { "public", "peer-private" } : new[] { "public" };
                Assert.That(box.Cards.Select(card => card.InstanceId), Is.EquivalentTo(expected));
                Assert.That(items.Zones.Single(zone => zone.Zone.ZoneId == "reserve").ItemCount, Is.EqualTo(2));
                var secretZone = items.Zones.Single(zone => zone.Zone.ZoneId == "secret");
                Assert.That(secretZone.ItemCount, Is.EqualTo(4)); Assert.That(secretZone.IdentitiesVisible, Is.False);
                Assert.That(items.Zones.Any(zone => zone.Zone.ZoneId == "own-child"), Is.EqualTo(holder));
                Assert.That(items.Zones.Any(zone => zone.Zone.ZoneId == "hidden-child"), Is.False);
                Assert.That(items.Items.Any(item => item.ItemId == "cyclic-event"), Is.False);
                Assert.That(items.Orders.Any(order => order.ZoneId == "secret"), Is.False);
                Assert.That(items.Orders.Any(order => order.ZoneId == "own-child"), Is.EqualTo(holder));
                Assert.That(items.Relations.Any(relation => relation.RelationId == "cross-private"), Is.False);
                Assert.That(items.Relations.Any(relation => relation.RelationId == "own-public"), Is.EqualTo(holder));
                Assert.That(box.Enterprises, Is.Empty);
                if (holder)
                {
                    var card = box.Cards.Single(value => value.InstanceId == "own-private");
                    Assert.That(card.CardId, Is.EqualTo("character.secret")); Assert.That(card.PrivateForPlayerId, Is.EqualTo(2));
                    Assert.That(card.FaceUp, Is.False); Assert.That(card.BackVisualKey, Is.EqualTo("character_back_0"));
                    Assert.That(card.BackColor, Is.EqualTo(PlayerColor.Red), "私密权限归 B，原实体牌背仍属 A。");
                    Assert.That(box.Cards.Single(value => value.InstanceId == "private-operator").BackVisualKey, Is.EqualTo("operator_back"));
                    Assert.That(box.Cards.Single(value => value.InstanceId == "own-child-event").PrivateForPlayerId, Is.EqualTo(2));
                    Assert.That(GameBoxState.ForViewer(box, viewer).Cards.Select(value => value.InstanceId), Is.EquivalentTo(expected));
                    Assert.That(GameBoxState.ForViewer(box, GameStateViewer.Player(1)).Cards.Select(value => value.InstanceId), Is.EqualTo(new[] { "public" }));
                    var view = GameStateViewProjector.Project(state, viewer);
                    Assert.That(view.GameBox.Cards.Single(value => value.InstanceId == card.InstanceId).CardId, Is.EqualTo(card.CardId));
                    Assert.That(GameStateViewProjector.ToClientState(view).ItemZones.Items.Any(value => value.ItemId == card.InstanceId), Is.True);
                }
            }
            var host = GameBoxStateProjector.Project(state, new ItemZoneQuery().Project(state, GameStateViewer.Host));
            Assert.That(host.Cards.Any(card => card.InstanceId == "unassigned-private"), Is.True);
            Assert.That(GameBoxState.ForViewer(host, GameStateViewer.Spectator).Cards.Any(card => card.InstanceId == "unassigned-private"), Is.False);
            Assert.That(GameBoxStateProjector.Project(state, new ItemZoneStateView { Items = new List<ItemInstanceState> { cyclic } }).Cards, Is.Empty);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(state.GameBox.Cards, Is.Empty, "投影不能反写权威态旧盒列表。");
            own.ZoneId = "retired";
            foreach (var viewer in new[] { GameStateViewer.Player(1), GameStateViewer.Player(2), GameStateViewer.Spectator })
            {
                var publicCard = GameBoxStateProjector.Project(state, new ItemZoneQuery().Project(state, viewer)).Cards.Single(card => card.InstanceId == "own-private");
                Assert.That(publicCard.PrivateForPlayerId, Is.EqualTo(-1)); Assert.That(publicCard.FaceUp, Is.True);
            }
        }
    }
}
