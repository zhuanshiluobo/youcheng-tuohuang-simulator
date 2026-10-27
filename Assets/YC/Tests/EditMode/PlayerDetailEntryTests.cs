using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class PlayerDetailEntryTests
    {
        private IReadOnlyList<PlayerDetailDisplayDefinition> definitions;
        private GameStateView view;
        [SetUp] public void SetUp()
        {
            var installed = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            // 未登记感染者也读取实际独立定义，不能用虚构卡文替代。
            var sources = installed.Definitions.ToList();
            foreach (var dir in new[] { "character", "award", "contract" })
            foreach (var path in Directory.GetFiles(Path.Combine(installed.RootPath, dir), "*.json"))
            {
                var source = JsonConvert.DeserializeObject<ExternalContentDefinition>(File.ReadAllText(path));
                if (!sources.Any(value => value.DefinitionId == source.DefinitionId)) sources.Add(source);
            }
            definitions = PlayerDetailDisplayCatalog.Read(sources);
            view = new GameStateView { ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1 };
            view.Players.Add(new PlayerStateView { PlayerId = 1 }); view.Players.Add(new PlayerStateView { PlayerId = 2 });
        }
        private ItemInstanceState Add(string definition, int player, string template = "core.zone.character_persistent")
        {
            var zone = new ZoneInstanceState { ZoneId = template + player, TemplateId = template, OwnerPlayerId = player };
            if (!view.ItemZones.Zones.Any(value => value.Zone.ZoneId == zone.ZoneId)) view.ItemZones.Zones.Add(new ZoneStateView { Zone = zone });
            var item = new ItemInstanceState { ItemId = definition + player, DefinitionId = definition, ZoneId = zone.ZoneId,
                ControllerSubject = ItemSubjectRef.Player(player), OwnerSubject = ItemSubjectRef.Player(player) };
            view.ItemZones.Items.Add(item); return item;
        }
        private void Activate(ItemInstanceState item, string mode = "tactic", PlayerDetailActivation activation = PlayerDetailActivation.Subscription)
        {
            view.PlayerEffects.Add(new PlayerEffectFactView { FactId = item.ItemId + mode, SourceItemId = item.ItemId,
                DefinitionId = item.DefinitionId, DetailPlayerId = item.ControllerSubject.PlayerId, AbilityId = item.DefinitionId + "." + mode, Activation = activation });
        }
        [Test] public void VisiblePublicItemsAndInactiveCardsDoNotBecomePlayerEffects()
        {
            Add("operator.mudrock", 1); Add("infected.character.amiya", 1, "core.zone.character_covered");
            Add("infected.award.permanent_support", 1, "core.zone.reward_pool");
            Add("infected.component.company_marker", 1, "core.zone.reward_pool");
            Assert.That(PlayerDetailEntryProjector.Project(view, definitions), Is.Empty);
        }
        [Test] public void HeldRewardsUseActualHolderAndLifecycleRegardlessOfDisplayName()
        {
            Add("infected.award.permanent_support", 2, "core.zone.player_rewards");
            Add("infected.award.enterprise_special", 1, "core.zone.player_rewards");
            Add("infected.award.roads", 1, "core.zone.player_rewards");
            Add("infected.award.score", 1, "core.zone.player_rewards");
            Add("infected.contract.lifetime_security", 1, "core.zone.player_rewards");
            var entries = PlayerDetailEntryProjector.Project(view, definitions);
            Assert.That(entries.Count, Is.EqualTo(2));
            Assert.That(entries.Single(entry => entry.SourceItemId.Contains("permanent_support")).DetailPlayerId, Is.EqualTo(2));
            Assert.That(entries.Single(entry => entry.SourceItemId.Contains("lifetime_security")).DetailPlayerId, Is.EqualTo(1));
        }
        [Test] public void OptionsDoNotDuplicateActiveModesAndTextComesVerbatimFromContent()
        {
            var item = Add("infected.character.amiya", 1); Activate(item);
            var before = PlayerDetailEntryProjector.Project(view, definitions).Single();
            view.CharacterSettlementOptions.Add(new CharacterSettlementOption { SourceItemId = item.ItemId, OptionId = "changing-option" });
            var after = PlayerDetailEntryProjector.Project(view, definitions).Single();
            Assert.That(after.EntryId, Is.EqualTo(before.EntryId));
            Assert.That(after.EffectText, Is.EqualTo(definitions.Single(value => value.DefinitionId == item.DefinitionId).EffectText));
            Assert.That(after.DisplayName, Is.EqualTo("阿米娅"));
        }
        [Test] public void TwoEffectiveAbilitiesOnOneCardRemainSeparateAndDelayedEffectCanOutliveCard()
        {
            var item = Add("infected.character.muelsyse", 1); Activate(item); Activate(item, "strategy", PlayerDetailActivation.Delayed);
            var entries = PlayerDetailEntryProjector.Project(view, definitions);
            Assert.That(entries.Count, Is.EqualTo(2)); Assert.That(entries.Select(value => value.EntryId), Is.Unique);
            view.PlayerEffects.RemoveAll(value => value.Activation == PlayerDetailActivation.Subscription);
            item.ZoneId = "discard";
            Assert.That(PlayerDetailEntryProjector.Project(view, definitions).Single().AbilityId, Does.EndWith("strategy"));
            view.PlayerEffects.Clear(); Assert.That(PlayerDetailEntryProjector.Project(view, definitions), Is.Empty);
        }
        [Test] public void StorageMergesIntoCarrierAndForeignMarkersDoNotChangeDetailOwner()
        {
            var item = Add("infected.character.swire", 1); Activate(item);
            var zone = new ZoneInstanceState { ZoneId = "storage", ParentKind = ZoneParentKind.Item, ParentId = item.ItemId,
                TemplateId = "core.zone.card_cube_storage", LocalZoneId = "markers" };
            view.ItemZones.Zones.Add(new ZoneStateView { Zone = zone });
            view.ItemZones.Items.Add(new ItemInstanceState { ItemId = "other-player-marker", ZoneId = zone.ZoneId, OwnerSubject = ItemSubjectRef.Player(2) });
            var entry = PlayerDetailEntryProjector.Project(view, definitions).Single();
            Assert.That(entry.DetailPlayerId, Is.EqualTo(1)); Assert.That(entry.StorageZoneIds, Is.EqualTo(new[] { "storage" }));
            Assert.That(view.ItemZones.Items.Last().OwnerSubject.PlayerId, Is.EqualTo(2));
            view.ItemZones.Items.RemoveAt(view.ItemZones.Items.Count - 1);
            Assert.That(PlayerDetailEntryProjector.Project(view, definitions).Single().StorageZoneIds, Has.Count.EqualTo(1), "空区是否失效由Host事实决定。");
            view.PlayerEffects.Clear(); Assert.That(PlayerDetailEntryProjector.Project(view, definitions), Is.Empty);
        }
        [Test] public void HiddenSourceCannotLeakThroughEffectFactsOrNetworkRoundtrip()
        {
            var item = Add("infected.character.amiya", 2); Activate(item);
            view.ItemZones.Items.Clear();
            var received = JsonConvert.DeserializeObject<GameStateView>(JsonConvert.SerializeObject(view));
            Assert.That(PlayerDetailEntryProjector.Project(received, definitions), Is.Empty);
        }
        [Test] public void EveryPersistentOrDeferredOfficialCharacterHasCompleteDisplayMetadata()
        {
            var folder = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core/character");
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                var source = JsonConvert.DeserializeObject<ExternalContentDefinition>(File.ReadAllText(file));
                foreach (var ability in source.Abilities.Where(value => value.SkillType == "persistent" || !string.IsNullOrEmpty(value.CleanupScript)))
                    Assert.That(definitions.Any(value => value.DefinitionId == source.DefinitionId && value.AbilityId == ability.AbilityId && !string.IsNullOrWhiteSpace(value.EffectText)), Is.True, ability.AbilityId);
            }
        }
    }
}
