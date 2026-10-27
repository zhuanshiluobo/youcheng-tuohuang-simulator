using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // 规则隔离：真实选面链产生正例，副本篡改用于只读投影拒绝检查，不计正常UI或对局。
    public sealed class PlayerDetailRuleFactsR9Tests
    {
        private static PlayerDetailDisplayDefinition Row(string definition, string ability, string rule, string binding = "owner") =>
            new PlayerDetailDisplayDefinition { DefinitionId = definition, AbilityId = ability, RequiredRuleRef = rule, RequiredRuleBinding = binding,
                DisplayName = "规则段", EffectTextRef = new GameplayTextRef { Key = "core:entrepreneur.A.detail.A01.cover" },
                DisplayCategory = PlayerDetailDisplayCategory.PlayerEffect, Activation = PlayerDetailActivation.Held };
        private static GameStateView View(GameState state) => GameStateViewProjector.ProjectForPlayer(state, 1);
        private static RoundChoiceState Group(GameState state, int round) => state.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == round);
        private static ItemInstanceState Selected(GameState state, int round) => state.ItemZones.Items.Single(i => i.ItemId == Group(state, round).Candidates.Single(c => c.CandidateId == Group(state, round).SelectedCandidateId).EntityItemId);
        private static void ReadOnly(GameState state, Action action)
        {
            string before = JsonConvert.SerializeObject(state); action();
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), "只读显示不得改变效果节点、动作、库存或其它Host状态");
        }

        [Test]
        public void OwnerRuleWaitsForActualSelectionAndLegacyIdRemainsUnchanged()
        {
            using var f = new AgnesContentTestFixture(); f.Formal();
            var gated = Row("entrepreneur.ability.A01", "entrepreneur.A01.cover", "entrepreneur.rule.A.cover_command");
            var legacy = Row(gated.DefinitionId, gated.AbilityId, "");
            var view = View(f.State);
            Assert.That(PlayerDetailEntryProjector.Project(view, new[] { gated }), Is.Empty);
            var oldFact = view.PlayerEffects.Single(e => e.AbilityId == legacy.AbilityId && e.DetailPlayerId == 1);
            Assert.That(PlayerDetailEntryProjector.Project(view, new[] { legacy }).Single().EntryId,
                Is.EqualTo(StableIdFactory.Create("player_detail", "1", oldFact.FactId, oldFact.AbilityId)));
            f.Choose(1, "A09");
            ReadOnly(f.State, () => {
                view = View(f.State);
                var entry = PlayerDetailEntryProjector.Project(view, new[] { gated }).Single();
                Assert.That(entry.SourceItemId, Is.EqualTo(f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).AbilityItemId));
                Assert.That(entry.EffectTextRef.Key, Is.EqualTo(gated.EffectTextRef.Key));
                Assert.That(ReferenceEquals(entry.EffectTextRef, gated.EffectTextRef), Is.False);
                var received = JsonConvert.DeserializeObject<GameStateView>(JsonConvert.SerializeObject(view));
                Assert.That(PlayerDetailEntryProjector.Project(received, new[] { gated }).Single().EntryId, Is.EqualTo(entry.EntryId));
                Assert.That(PlayerDetailEntryProjector.Project(GameStateViewProjector.ProjectForPlayer(f.State, 2), new[] { gated }).Single().DetailPlayerId, Is.EqualTo(1), "公开规则按观察者合法可见集合显示");
            });
        }

        [Test]
        public void InvalidReceiptWrongFaceDiyRuleAndInvisibleOrDepartedItemsDoNotRevealRules()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A09");
            var row = Row("entrepreneur.ability.A01", "entrepreneur.A01.cover", "entrepreneur.rule.A.cover_command");
            foreach (string mutation in new[] { "unselected", "face", "receipt", "request", "selecting", "source_left", "owner_left", "diy", "version", "hidden_source", "hidden_owner", "unknown" })
            {
                var state = GameStateCloneService.DeepClone(f.State); var group = Group(state, 1); var item = Selected(state, 1);
                string ownerId = state.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).AbilityItemId;
                if (mutation == "unselected") group.SelectedCandidateId = "";
                else if (mutation == "face") group.ActiveFaceId = "fixture.other_face";
                else if (mutation == "receipt") Value(item.LocalState, "sourceSelectionReceipt").Properties.Single(p => p.Name == "candidateId").Value = NormalizedValue.CreateString("forged");
                else if (mutation == "request") state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == group.RequestId).Status = "open";
                else if (mutation == "selecting") state.EffectRuntime.EffectNodes.Single(n => n.EffectId == group.OwnerEffectId).EffectTypeId = "fixture.forged_selection";
                else if (mutation == "source_left") item.ZoneId = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.box_reserve").ZoneId;
                else if (mutation == "owner_left") state.ItemZones.Items.Single(i => i.ItemId == ownerId).ZoneId = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.box_reserve").ZoneId;
                else if (mutation == "diy") state.ContentDefinitions.Entrepreneurs.Single(d => d.DefinitionId == row.DefinitionId).RoundGroups.Single(g => g.GroupId == group.GroupId)
                    .Candidates.Single(c => c.CandidateId == group.SelectedCandidateId).RuleRefs = new List<string> { "fixture.diy.explicit_other_rule" };
                else if (mutation == "version") state.EntrepreneurSetup.ContentVersion = "different-content";
                var view = View(state);
                if (mutation == "hidden_source") view.ItemZones.Items.RemoveAll(i => i.ItemId == item.ItemId);
                if (mutation == "hidden_owner") view.ItemZones.Items.RemoveAll(i => i.ItemId == ownerId);
                if (mutation == "unknown") view.ViewerRole = GameStateViewerRole.Unknown;
                ReadOnly(state, () => {
                    view.PlayerRules = PlayerRuleFactProjector.Project(state, view);
                    view.PlayerEffects = PlayerEffectFactProjector.Project(state, view);
                    Assert.That(PlayerDetailEntryProjector.Project(view, new[] { row }), Is.Empty, mutation);
                });
            }
        }

        [Test]
        public void PureHeldRulesUseSelectedEntityAndMultipleSegmentsHaveDistinctStableIds()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(3, "A06");
            var item = Selected(f.State, 3);
            var rows = new[] { Row(item.DefinitionId, "", "entrepreneur.rule.A.command_price"), Row(item.DefinitionId, "", "entrepreneur.face.A06") };
            ReadOnly(f.State, () => {
                var view = View(f.State); var entries = PlayerDetailEntryProjector.Project(view, rows);
                Assert.That(entries.Count, Is.EqualTo(2)); Assert.That(entries.Select(e => e.EntryId).Distinct().Count(), Is.EqualTo(2));
                Assert.That(entries.All(e => e.SourceItemId == item.ItemId && e.AbilityId == ""), Is.True);
                Assert.That(PlayerDetailEntryProjector.Project(view, rows.Reverse()).Select(e => e.EntryId).OrderBy(x => x), Is.EqualTo(entries.Select(e => e.EntryId).OrderBy(x => x)));
                var copy = GameStateCloneService.DeepClone(item); copy.ItemId += ".same_definition_other_entity"; view.ItemZones.Items.Add(copy);
                Assert.That(PlayerDetailEntryProjector.Project(view, rows).All(e => e.SourceItemId == item.ItemId), Is.True);
            });
            var state = GameStateCloneService.DeepClone(f.State); var original = Selected(state, 3);
            var duplicate = GameStateCloneService.DeepClone(original); duplicate.ItemId += ".same_definition_other_entity";
            state.ItemZones.Items.Add(duplicate); original.ZoneId = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.box_reserve").ZoneId;
            ReadOnly(state, () => Assert.That(PlayerDetailEntryProjector.Project(View(state), rows), Is.Empty,
                "原选中实体离场后，同定义实体不能复用指向原ItemId的收据"));
        }

        [Test]
        public void SharedReceiverRequiresExactCurrentVisibleGrantAndRetainsReceiverIdentity()
        {
            using var f = new PaulContentTestFixture(); f.Choose(1, "P01");
            var row = Row("entrepreneur.ability.P09", "entrepreneur.P09.departure", "entrepreneur.face.P01", "grant");
            ReadOnly(f.State, () => {
                var view = View(f.State); var source = view.PlayerEffects.Single(e => e.AbilityId == row.AbilityId && e.DetailPlayerId == 1);
                Assert.That(source.GrantingRuleFactIds.Count > 0, Is.True);
                Assert.That(PlayerDetailEntryProjector.Project(view, new[] { row }).Single().AbilityId, Is.EqualTo(row.AbilityId));
            });
            var state = GameStateCloneService.DeepClone(f.State); var ability = state.ItemZones.Items.Single(i => i.ItemId == f.AbilityItem);
            Value(ability.LocalState, "grantingSourceRefs").Items.Clear();
            Assert.That(PlayerDetailEntryProjector.Project(View(state), new[] { row }), Is.Empty, "规则仍在，但共享接收器的grant已经消失");
            state = GameStateCloneService.DeepClone(f.State); ability = state.ItemZones.Items.Single(i => i.ItemId == f.AbilityItem);
            Value(ability.LocalState, "grantingSourceRefs").Items.Clear(); Value(ability.LocalState, "grantingSourceRefs").Items.Add(NormalizedValue.CreateString("not-a-current-source-key"));
            Assert.That(PlayerDetailEntryProjector.Project(View(state), new[] { row }), Is.Empty);
            state = GameStateCloneService.DeepClone(f.State); ability = state.ItemZones.Items.Single(i => i.ItemId == f.AbilityItem);
            var foreign = AbilitySourceService.QuerySources(state, state.ContentDefinitions.AbilitySources, 2).First();
            Value(ability.LocalState, "grantingSourceRefs").Items.Clear(); Value(ability.LocalState, "grantingSourceRefs").Items.Add(NormalizedValue.CreateString(foreign.SourceKey));
            Assert.That(PlayerDetailEntryProjector.Project(View(state), new[] { row }), Is.Empty, "异玩家合法来源也不能成为本接收器的grant");
            var hidden = View(f.State); hidden.ItemZones.Items.RemoveAll(i => i.ItemId == Selected(f.State, 1).ItemId);
            hidden.PlayerRules = PlayerRuleFactProjector.Project(f.State, hidden); hidden.PlayerEffects = PlayerEffectFactProjector.Project(f.State, hidden);
            Assert.That(PlayerDetailEntryProjector.Project(hidden, new[] { row }), Is.Empty);
        }

        [Test]
        public void CatalogConsumesTypedRuleGateAndRejectsInvalidOrDuplicateBindings()
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.ability.A01.json");
            var source = JsonConvert.DeserializeObject<ExternalContentDefinition>(File.ReadAllText(path));
            var row = Row(source.DefinitionId, "entrepreneur.A01.cover", "entrepreneur.rule.A.cover_command");
            var other = Row(source.DefinitionId, row.AbilityId, "entrepreneur.rule.A.physical_final");
            source.Data["playerDetails"] = JArray.FromObject(new[] { row, other });
            Assert.That(PlayerDetailDisplayCatalog.Read(new[] { source }).Count, Is.EqualTo(2));
            source.Data["playerDetails"] = JArray.FromObject(new[] { row, row });
            Assert.Throws<InvalidOperationException>(() => PlayerDetailDisplayCatalog.Read(new[] { source }));
            row.RequiredRuleBinding = "unknown"; source.Data["playerDetails"] = JArray.FromObject(new[] { row });
            Assert.Throws<InvalidOperationException>(() => PlayerDetailDisplayCatalog.Read(new[] { source }));
            row.RequiredRuleBinding = "owner"; row.EffectTextRef = null; row.EffectText = "不能用摘要绕过typed规则段";
            source.Data["playerDetails"] = JArray.FromObject(new[] { row });
            Assert.Throws<InvalidOperationException>(() => PlayerDetailDisplayCatalog.Read(new[] { source }));
        }
    }
}
