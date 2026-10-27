using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 59限定的资格事实投影单元输入；不计真实激活、完整Host或正常页面验收。
    public sealed class QY018TokenRegistrationProjectionTests
    {
        private IsolatedTestArtifacts artifacts;
        private GameState state;
        [SetUp] public void Begin() { artifacts = IsolatedTestArtifacts.BeginCase(); state = null; }
        [TearDown] public void End()
        {
            if (state != null) artifacts.WriteLabeledText("018失效付款来源投影输入", JsonConvert.SerializeObject(state));
            artifacts.Finish();
        }
        [Test] public void UnavailablePaymentSourceCannotCountLoadOnlyCarrierCubesOrChangeNonPaymentFacts()
        {
            const string definition = "qy018.projection.carrier", ability = "qy018.projection.load_only";
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var source = new GameState { GameId = "qy018.projection.unit", MapId = map.MapId,
                ContentPackHash = "qy018.projection.unit", LaunchPlayerOrder = new List<int> { 1, 2 },
                ContentDefinitions = new GameContentDefinitions(),
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "qy018.projection.unit" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            source.ContentDefinitions.AbilitySources.Add(new AbilitySourceDeclaration { DefinitionId = definition, AbilityId = ability,
                HandlerRef = ability, DeclarationVersion = "1.0.0", Kind = "event", AuthorizationPolicy = "controller", PresentationRef = definition,
                EffectiveZones = new List<string> { "core.zone.character_hand" },
                AllowedOperations = new List<string> { "carrier.load:" + definition + ":markers:core.player_marker" } });
            state = new ItemZoneStateFactory().CreateForSetup(source, map, new[] {
                new ItemRecipeEntry { EntryId = "carrier", DefinitionId = definition, TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 2, PerPlayer = true, InitialZoneRole = "supply" }
            }, new[] { new ItemCardZoneDeclaration { DefinitionId = definition, CardZones = new List<CardZoneDefinition> {
                new CardZoneDefinition { LocalZoneId = "markers", ZoneTemplateId = "core.zone.card_cube_storage",
                    AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                    CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.card_payment",
                    CarrierExitPolicyId = "core.preserve_contents", VisibilityPolicyId = ItemZoneVisibility.Owner,
                    PaymentPolicyId = "core.basic_resource_substitution", MaxMarkersPerPayment = 2 }
            } } });
            var card = state.ItemZones.Items.Single(i => i.DefinitionId == definition && i.ControllerSubject.PlayerId == 1);
            var zone = state.ItemZones.Zones.Single(z => z.ParentId == card.ItemId && z.LocalZoneId == "markers");
            // 只安排本例初始库存；没有伪造SourceRef/付款收据，也不执行独立程序。
            foreach (var item in state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1)) item.ZoneId = zone.ZoneId;
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, 1, zone.ZoneId), Is.Null);
            Assert.That(state.ContentDefinitions.AbilitySources.Single().AllowedOperations.Any(p => p.StartsWith("carrier.transfer.", StringComparison.Ordinal)), Is.False);
            var method = typeof(SettlementHostCatalog).GetMethod("Source", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            string before = RuleStateDigestService.Compute(state);
            NormalizedValue Project(string kind) => (NormalizedValue)method.Invoke(null, new object[] {
                state, new SettlementWindowContext { Kind = kind, PlayerId = 1 }, card.ItemId, ability, false });
            var payment = Project(SettlementEntryService.ResourcePayment);
            Assert.That(Value(payment, "markerCount").IntegerValue, Is.Zero);
            Assert.That(Value(payment, "storageZoneId").StringValue, Is.Empty);
            Assert.That(Value(payment, "transferableTokenCount").IntegerValue, Is.Zero);
            var cleanup = Project(SettlementEntryService.PlayerCleanup);
            Assert.That(Value(cleanup, "markerCount").IntegerValue, Is.EqualTo(2), "原非付款兼容事实保持；本例不授予其付款资格。");
            Assert.That(Value(cleanup, "storageZoneId").StringValue, Is.Empty);
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before), "投影读取不能改变库存或其他权威字段。");
        }
    }
}
