using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class CardMarkerContractTests
    {
        [TestCase("valid", true)] [TestCase("empty", false)] [TestCase("other_card", false)]
        [TestCase("other_item", false)] [TestCase("another_effect", false)] [TestCase("mandatory_single", true)] [TestCase("other_player", false)]
        public void StorageDestinationDetectsOnlyTheActualOptionalStorageSubtree(string variant, bool expected)
        {
            var store = EffectSpec.Create(ContentPrimitiveEffectExecutor.StoreRemoved, Object("apiVersion", NormalizedValue.CreateInteger(2),
                "player", NormalizedValue.CreateString("1"), "cardInstanceId", NormalizedValue.CreateString(variant == "other_card" ? "other" : "carrier"),
                "localZoneId", NormalizedValue.CreateString("markers"), "influenceItemId", NormalizedValue.CreateString(variant == "other_item" ? "other" : "cube")), variant == "other_player" ? 2 : 1);
            var choice = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString("1"),
                "minSelections", NormalizedValue.CreateInteger(variant == "mandatory_single" ? 1 : 0), "maxSelections", NormalizedValue.CreateInteger(1),
                "options", Strings(variant == "multi" ? new[] { "actual", "other" } : new[] { "actual" })), 1);
            choice.NestedEffectGroups.Add(variant == "empty" ? new List<EffectSpec>() : new List<EffectSpec> {
                variant == "another_effect" ? ResourceEffectSpecFactory.GainScore(1, 1) : store });
            if (variant == "multi") choice.NestedEffectGroups.Add(new List<EffectSpec>());
            Assert.That(InfluenceDestinationResolver.HasStorageChoice(choice, 1, "carrier", "cube"), Is.EqualTo(expected));
        }

        private EffectNodeRuntimeState StartActualInitialBranch(string identity, int supply = 6)
        {
            definition = identity; local = "markers";
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); CharacterCoverEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack); CityStyleLuaCatalog.Register(registry, pack);
            ContentLifecycleLuaCatalog.Register(registry, pack); AbilitySourceLuaCatalog.Register(registry, pack);
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            InfluenceEffectExecutor.Register(registry, new InfluenceService(new MapQueryService(map)));
            var real = pack.ActiveDefinitions.Single(d => d.DefinitionId == definition);
            var entries = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "card", DefinitionId = definition,
                TypeId = real.ItemTypeId, Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" } };
            if (supply > 0) entries.Add(new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker",
                Quantity = supply, PerPlayer = true, InitialZoneRole = "supply" });
            state = new ItemZoneStateFactory().CreateForSetup(new GameState { GameId = "api41.initial.isolated." + identity,
                MapId = map.MapId, ContentPackHash = pack.ContentHash, LaunchPlayerOrder = new List<int> { 1, 2 },
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离甲" }, new PlayerState { PlayerId = 2, Name = "隔离乙" } },
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(),
                    AbilitySources = registry.AbilityDeclarations.ToList(), UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(),
                    CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack) } },
                map, entries, new[] { new ItemCardZoneDeclaration { DefinitionId = definition, CardZones = real.CardZones } });
            state.Round = 1; state.ActionRound = 1; state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1; state.StartPlayerId = 1;
            card = state.ItemZones.Items.Single(i => i.DefinitionId == definition && i.OwnerSubject.PlayerId == 1).ItemId;
            Engine().CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Run(); Answer(card);
            Engine().CreateRoot(CharacterLifecycleService.CreateUseSpec(1, card, "tactic")); Run();
            var request = state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
            return request == null ? null : state.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
        }

        [TestCase("infected.character.lin", 2)]
        [TestCase("operator.passenger", 5)]
        [TestCase("operator.dorothy", 4)]
        public void InitialMarkerChoicesContainOnlyRealAtomicStorageBranchesAndRestore(string identity, int maximum)
        {
            var choice = StartActualInitialBranch(identity); var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(request.InteractionTypeId, Is.EqualTo("effect.choice.execute")); Assert.That(request.AllowDecline, Is.True);
            Assert.That(request.MinSelections, Is.Zero); Assert.That(request.MaxSelections, Is.EqualTo(1));
            Assert.That(request.CandidateIds, Is.EqualTo(Enumerable.Range(1, maximum).Select(n => n.ToString()).ToArray()));
            Assert.That(Stored, Is.Empty); Assert.That(choice.ChildEffectIds, Is.Empty);
            Assert.That(choice.NestedGroupSizes, Is.All.EqualTo(1));
            Assert.That(choice.NestedEffects.All(e => e.EffectTypeId == ItemTransferEffectExecutor.TypeId), Is.True);
            string choiceId = choice.EffectId; Restore("真实存放分支待答"); Answer(maximum.ToString());
            Assert.That(Stored.Count, Is.EqualTo(maximum)); Assert.That(Stored.All(i => i.OwnerSubject.PlayerId == 1), Is.True);
            choice = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == choiceId);
            Assert.That(choice.ChildEffectIds.Count, Is.EqualTo(1));
            var move = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == choice.ChildEffectIds.Single());
            Assert.That(move.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(move.PositionChanges.Count, Is.EqualTo(maximum));
            Restore("真实存放完整完成"); Run(); Assert.That(Stored.Count, Is.EqualTo(maximum));
        }

        [TestCase("infected.character.lin", false)] [TestCase("infected.character.lin", true)]
        [TestCase("operator.passenger", false)] [TestCase("operator.passenger", true)]
        [TestCase("operator.dorothy", false)] [TestCase("operator.dorothy", true)]
        public void InitialMarkerCancelUsesNoEmptyEffectAndDoesNotMoveOrEndAction(string identity, bool emptyArray)
        {
            var choice = StartActualInitialBranch(identity); string id = choice.EffectId;
            var before = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker").ToDictionary(i => i.ItemId, i => i.ZoneId);
            var phase = state.Phase; int actionRound = state.ActionRound;
            Restore("不存放前冷恢复"); var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                emptyArray ? NormalizedValue.CreateArray(Array.Empty<NormalizedValue>()) : NormalizedValue.CreateBoolean(false), out string reason), Is.True, reason);
            Run(); Assert.That(Stored, Is.Empty);
            choice = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(choice.ChildEffectIds, Is.Empty); Assert.That(choice.NormalizedResult.Kind, Is.EqualTo(NormalizedValueKind.Null));
            Assert.That(state.ItemZones.Items.Where(i => before.ContainsKey(i.ItemId)).All(i => i.ZoneId == before[i.ItemId]), Is.True);
            Assert.That(state.Phase, Is.EqualTo(phase)); Assert.That(state.ActionRound, Is.EqualTo(actionRound));
        }

        [TestCase("infected.character.lin")] [TestCase("operator.passenger")] [TestCase("operator.dorothy")]
        public void InitialMarkerBranchRejectsChangedSourceWithoutMovingRemainingSubset(string identity)
        {
            var choice = StartActualInitialBranch(identity); string owner = choice.EffectId;
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("2"), out string reason), Is.True, reason);
            // 隔离竞态定位：回答已接受、正文尚未运行时，模拟其中一枚已进入本牌合法载物区。
            var marker = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.ZoneId == FindZone(state, "supply", 1).ZoneId)
                .OrderBy(i => i.ItemId, StringComparer.Ordinal).First(); string first = marker.ItemId; marker.ZoneId = Storage.ZoneId;
            foreach (var order in state.ItemZones.Orders) order.ItemIds.Remove(first);
            // 失败后的正常角色离场会把预放的first送回原供应；其余未选标记不得被失败移动带走。
            var snapshot = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.ItemId != first).ToDictionary(i => i.ItemId, i => i.ZoneId);
            Run(); Assert.That(state.ItemZones.Items.Where(i => snapshot.ContainsKey(i.ItemId)).All(i => i.ZoneId == snapshot[i.ItemId]), Is.True);
            choice = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == owner);
            Assert.That(choice.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(Stored, Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == choice.ChildEffectIds.Single()).PositionChanges, Is.Empty);
            Assert.That(marker.ZoneId, Is.EqualTo(marker.OriginalSupplyZoneId));
        }

        [TestCase("infected.character.lin")] [TestCase("operator.passenger")] [TestCase("operator.dorothy")]
        public void InitialMarkerEmptySupplyCreatesNoChoiceOrInventedStorageEffect(string identity)
        {
            Assert.That(StartActualInitialBranch(identity, 0), Is.Null); Assert.That(Stored, Is.Empty);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
        }
    }
}
