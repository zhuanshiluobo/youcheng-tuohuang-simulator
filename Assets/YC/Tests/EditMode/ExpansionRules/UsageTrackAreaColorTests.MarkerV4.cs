using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using static YC.Tests.EditMode.ExpansionRules.EnterpriseEffectTests;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class UsageTrackAreaColorTests
    {
        private string EnsureActualUsageArea(GameState state, string itemId)
        {
            var item = state.ItemZones.Items.Single(i => i.ItemId == itemId);
            var declaration = state.ContentDefinitions.UsageTracks.Single(d => d.DefinitionId == item.DefinitionId);
            var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ParentId == itemId && z.LocalZoneId == declaration.CardZoneLocalId);
            if (zone != null) return zone.ZoneId;
            var definition = pack.ActiveDefinitions.Single(d => d.DefinitionId == item.DefinitionId).CardZones.Single(z => z.LocalZoneId == declaration.CardZoneLocalId);
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, itemId, definition, out string created, out var reason), Is.True, reason);
            return created;
        }
        private void MoveMarkerV4(GameState state, int actor, string id, string destination, string slot = null)
        {
            ItemTransferEffectExecutor.Register(registry);
            var item = state.ItemZones.Items.Single(i => i.ItemId == id);
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(MarkerSpecV4(actor, MarkerLegV4(id, item.ZoneId, destination, slot, item.SlotId.Length == 0 ? null : item.SlotId)));
            Run(engine); Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed), engine.GetNode(root).FailureReason);
        }
        [Test]
        public void V4ReplacementUsesActualTrackMemberAndKeepsInitialProvenanceAndViews()
        {
            var state = State(); Initialize(state); var binding = state.EffectRuntime.UsageTrackBindings.Single(); string original = binding.MarkerItemId;
            MoveMarkerV4(state, 2, original, null);
            Assert.That(UsageTracks.CurrentMarker(state, binding), Is.Null); Assert.That(registry.QueryActions(state, 1).All(o => !o.Enabled), Is.True);
            string next = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ItemId != original && i.ZoneId == i.OriginalSupplyZoneId).ItemId;
            MoveMarkerV4(state, 2, next, binding.ZoneId, "unused");
            Assert.That(UsageTracks.CurrentMarker(state, binding).ItemId, Is.EqualTo(next)); Assert.That(binding.MarkerItemId, Is.EqualTo(original));
            Assert.That(UsageTracks.Project(state).Single().MarkerItemId, Is.EqualTo(next));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).UsageTracks.Single().MarkerItemId, Is.EqualTo(next));
            Assert.That(registry.QueryActions(state, 1).All(o => o.Enabled), Is.True);
            state = Restore(state, "v4替换成员保留初始凭证"); Assert.That(UsageTracks.CurrentMarker(state, state.EffectRuntime.UsageTrackBindings.Single()).ItemId, Is.EqualTo(next));
        }
        [Test]
        public void V4ReplacementCannotEraseConsumedUsageGroupOrResetOriginalMarker()
        {
            var state = State(); Initialize(state); var binding = state.EffectRuntime.UsageTrackBindings.Single(); string original = binding.MarkerItemId;
            var quick = registry.QueryActions(state, 1).Single(o => o.ActionId == Quick);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(AbilityActionExecutor.Create(quick, "api25.used", Object())); Run(engine);
            MoveMarkerV4(state, 2, original, null);
            string replacement = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ItemId != original && i.ZoneId == i.OriginalSupplyZoneId).ItemId;
            MoveMarkerV4(state, 2, replacement, binding.ZoneId, "unused");
            Assert.That(registry.QueryActions(state, 1).All(o => !o.Enabled && o.DisabledReason == "style_action_used_this_round"), Is.True);
            Assert.That(state.EffectRuntime.UsageGroupReceipts.Count, Is.EqualTo(1));
            MoveMarkerV4(state, 2, replacement, binding.ZoneId, "used");
            engine = BeginRealCleanup(state, out var chain, out _);
            foreach (var cleanup in chain.Nodes.Where(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow)) { StartMain(engine, cleanup); Run(engine); }
            StartMain(engine, chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded)); Run(engine);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == replacement).SlotId, Is.EqualTo("unused"));
            var old = state.ItemZones.Items.Single(i => i.ItemId == original); Assert.That(old.ZoneId, Is.EqualTo(old.OriginalSupplyZoneId));
            Restore(state, "v4实际替代成员复位不拉回旧实例");
        }
        [Test]
        public void V4DeclarationSharedAreaReplacementPreservesHistoryAndUsesActualNewMember()
        {
            var state = State(finite: true); Initialize(state); string original = DeclareFinite(state, 1);
            var record = state.FindPlayer(1).DeclaredCityStyles.Single(); var used = record.UsedFacilityIds.ToArray(); int score = state.FindPlayer(1).Score;
            var binding = state.EffectRuntime.UsageTrackBindings.Single(b => b.MarkerItemId == original);
            MoveMarkerV4(state, 2, original, null);
            Assert.That(record.MarkerArea, Is.Empty);
            string replacement = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ItemId != original && i.ZoneId == i.OriginalSupplyZoneId).ItemId;
            var declaration = UsageTracks.Declaration(state, binding.TrackId);
            var other = state.ItemZones.Zones.SingleOrDefault(z => z.ParentId == binding.SourceItemId && z.LocalZoneId == declaration.CardZoneLocalId);
            if (other == null)
            {
                // 隔离规则以实际配置创建合法承载区，不代替正常游戏验收。
                var definition = pack.ActiveDefinitions.Single(d => d.DefinitionId == declaration.DefinitionId).CardZones.Single(z => z.LocalZoneId == declaration.CardZoneLocalId);
                Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, binding.SourceItemId, definition, out string created, out var reason), Is.True, reason);
                other = state.ItemZones.Zones.Single(z => z.ZoneId == created);
                record = state.FindPlayer(1).DeclaredCityStyles.Single();
            }
            string otherArea = other.ZoneId;
            MoveMarkerV4(state, 2, replacement, otherArea, "1");
            Assert.That(record.InfluenceMarkerId, Is.EqualTo(original)); Assert.That(record.MarkerArea, Is.EqualTo("1"));
            Assert.That(record.GetCurrentMarkerItemId(), Is.EqualTo(replacement)); Assert.That(record.RemainingSpecialActionUses, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score)); Assert.That(record.UsedFacilityIds, Is.EqualTo(used));
            state = Restore(state, "v4宣告跨同域替换不重发历史收益");
            var action = registry.QueryActions(state, 1).Single(o => o.ActionId == YC.Domain.SpecialActions.SpecialActionDatabase.SourceStoneIndustrialHub);
            Assert.That(action.Enabled, Is.True, action.DisabledReason);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(AbilityActionExecutor.Create(action, "api25.replacement", Object())); Run(engine);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == replacement).SlotId, Is.EqualTo("used_from_1"));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == original).ZoneId, Is.EqualTo(FindZone(state, "supply", 1).ZoneId));
            Restore(state, "v4替换宣告成员真实发动后");
        }
        [TestCase(false)][TestCase(true)]
        public void V4FirstPlacementRegistersRealOwnersPermissionWithoutDeclarationHistory(bool publicStyle)
        {
            var state = State(finite: publicStyle);
            string source = publicStyle ? state.ItemZones.Items.Single(i => i.DefinitionId == "city_style_source_stone_industrial_hub").ItemId : CardId(state);
            string zone = EnsureActualUsageArea(state, source);
            string marker = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId).ItemId;
            int score = state.FindPlayer(1).Score;
            MoveMarkerV4(state, 2, marker, zone, publicStyle ? "2" : "unused");
            var binding = state.EffectRuntime.UsageTrackBindings.Single(b => b.SourceItemId == source);
            Assert.That(binding.PlayerId, Is.EqualTo(1)); Assert.That(binding.ProtocolVersion, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == binding.InitializationEffectId).PlayerId, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).DeclaredCityStyles, Is.Empty); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
            if (publicStyle)
            {
                var displayed = YC.Presentation.Workflows.CityStyleInteraction.CurrentMarkersWithoutDeclaration(state).Single();
                Assert.That(displayed.MarkerId, Is.EqualTo(marker)); Assert.That(displayed.MarkerArea, Is.EqualTo("2"));
                Assert.That(displayed.PlayerId, Is.EqualTo(1));
            }
            state = Restore(state, "v4直接入轨仅建当前资格不伪造历史");
            var action = registry.QueryActions(state, 1).First(o => o.SourceRef.ItemId == source);
            Assert.That(action.Enabled, Is.True, action.DisabledReason);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(AbilityActionExecutor.Create(action, "api25.first-placement", Object())); Run(engine);
            Assert.That(state.EffectRuntime.UsageGroupReceipts.Count, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).DeclaredCityStyles, Is.Empty);
            Restore(state, "v4首次当前资格正常发动后");
        }
        [Test]
        public void V4DirectPermissionCanLaterDeclareNormallyUsingSameRealMarker()
        {
            var state = State(finite: true); string source = state.ItemZones.Items.Single(i => i.DefinitionId == "city_style_source_stone_industrial_hub").ItemId;
            string zone = EnsureActualUsageArea(state, source);
            string marker = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId).ItemId;
            MoveMarkerV4(state, 2, marker, zone, "1");
            int supply = state.ItemZones.Items.Count(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId);
            Assert.That(DeclareFinite(state, 1), Is.EqualTo(marker));
            Assert.That(state.EffectRuntime.UsageTrackBindings.Count, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).DeclaredCityStyles.Count, Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Count(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId), Is.EqualTo(supply));
            Assert.That(state.FindPlayer(1).DeclaredCityStyles[0].MarkerArea, Is.EqualTo("2"));
            Restore(state, "v4先入轨后正常宣告不重复标记或绑定");
        }
        [TestCase("owner")][TestCase("effect")][TestCase("authorization")]
        [TestCase("marker")][TestCase("receipt")][TestCase("header")]
        public void V4FirstPlacementRecoveryRejectsChangedQualificationProofWithoutRepair(string fault)
        {
            var state = State(); string source = CardId(state), zone = EnsureActualUsageArea(state, source);
            string marker = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId == i.OriginalSupplyZoneId).ItemId;
            MoveMarkerV4(state, 2, marker, zone, "unused");
            string clean = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var archive = JObject.Parse(clean); var runtime = (JObject)archive["Snapshot"]["State"]["EffectRuntime"];
            var binding = (JObject)runtime["UsageTrackBindings"][0];
            if (fault == "owner") binding["PlayerId"] = 2;
            if (fault == "effect") binding["InitializationEffectId"] = "not-the-initializer";
            if (fault == "authorization") binding["AuthorizationRef"] = "marker-placement:other";
            if (fault == "marker") binding["MarkerItemId"] = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ItemId != marker).ItemId;
            if (fault == "receipt") binding["InitializationReceiptId"] = "not-the-receipt";
            if (fault == "header") runtime["UsageTrackProtocolVersion"] = 1;
            string bad = archive.ToString();
            Assert.That(HostRecoveryService.RecoverJson(bad, registry).Succeeded, Is.False);
            Assert.That(archive.ToString(), Is.EqualTo(bad));
            Assert.That(HostRecoveryService.RecoverJson(clean, registry).Succeeded, Is.True);
            artifacts.WriteLabeledText("v4首次资格损坏原始归档_" + fault, bad);
        }
    }
}
