using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class EnterpriseUpgradeSelectionTests
    {
        private static readonly string[] Boards = { "test.enterprise.first", "test.enterprise.second", "test.enterprise.third" };
        private static EnterpriseDefinitions Definitions(bool interactiveReward = false)
        {
            var definitions = new EnterpriseDefinitions();
            foreach (string id in Boards)
            {
                var board = new EnterpriseDefinition { DefinitionId = id, RuntimeId = id,
                    Levels = Enumerable.Range(0, 6).Select(level => new EnterpriseLevelDefinition { Level = level,
                        Kind = level == 1 ? "reward" : "empty", AbilityId = level == 1 ? id + ".reward" : string.Empty }).ToList() };
                definitions.Register(board);
                definitions.BindAbility(id + ".reward", context => interactiveReward
                    ? new[] { ChoiceReward(context.Event.PlayerId) }
                    : new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) });
            }
            definitions.Freeze(); return definitions;
        }
        private static EffectSpec ChoiceReward(int player)
        {
            var choice = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString(player.ToString()), "options", Strings(new[] { "accept" })), player);
            choice.NestedEffectGroups.Add(new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(player, 1) }); return choice;
        }
        private static EffectRegistry Registry(bool interactiveReward = false)
        {
            var registry = new EffectRegistry { ContentPackHash = "test.enterprise.upgrade.content" };
            EnterpriseEffectExecutor.Register(registry, Definitions(interactiveReward)); GenericLuaEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry); return registry;
        }
        private static GameState State(int level = 0)
        {
            var map = new GameMapDefinition { MapId = "test.enterprise.upgrade.map", MinPlayers = 2, MaxPlayers = 4 };
            var source = new GameState { GameId = "test.enterprise.upgrade.game", MapId = map.MapId, ContentPackHash = "test.enterprise.upgrade.content",
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "test.enterprise.upgrade.content", EnabledExpansionIds = new List<string> { "core", "enterprise" } },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "player_markers", DefinitionId = "component.marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "player_markers" } };
            recipe.AddRange(Boards.Select(id => new ItemRecipeEntry { EntryId = id, DefinitionId = id, TypeId = "core.board_card", Quantity = 1, InitialZoneRole = "enterprises" }));
            var declarations = Boards.Select(id => new ItemCardZoneDeclaration { DefinitionId = id, CardZones = Enumerable.Range(1, 2).Select(player => new CardZoneDefinition {
                LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track, EntryPolicyId = "core.owned_player_marker", ExitPolicyId = "core.explicit_transfer", VisibilityPolicyId = ItemZoneVisibility.Public,
                CarrierExitPolicyId = "core.preserve_contents", DisplayAnchorId = "cooperation" }).ToList() }).ToArray();
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, recipe, declarations);
            foreach (string id in Boards) foreach (int player in new[] { 1, 2 })
            {
                var track = state.ItemZones.Zones.Single(zone => zone.ParentId == Id(state, id) && zone.LocalZoneId == "cooperation_" + player);
                var marker = state.ItemZones.Items.First(item => item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == player && state.ItemZones.Zones.Single(zone => zone.ZoneId == item.ZoneId).LocalZoneId == "player_markers");
                marker.ZoneId = track.ZoneId; marker.SlotId = "level_" + level; state.ItemZones.Orders.Single(order => order.ZoneId == track.ZoneId).ItemIds.Add(marker.ItemId);
            }
            return state;
        }
        private static string Id(GameState state, string definition) => state.ItemZones.Items.Single(item => item.DefinitionId == definition).ItemId;
        private static int Level(GameState state, string id) => new EnterpriseStateQuery().GetLevel(state, 1, id);
        private static EffectSpec Range(IEnumerable<string> ids, int levels = 1) => EffectSpec.Create(LuaDomainEffectTypeIds.UpgradeEnterprise,
            Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "enterpriseItemIds", Strings(ids), "levels", NormalizedValue.CreateInteger(levels)), 1);
        private static EffectNodeRuntimeState Run(GameState state, EffectRegistry registry, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec); Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); return engine.GetNode(id);
        }
        private static void Answer(GameState state, EffectRegistry registry, string id)
        {
            var request = state.EffectRuntime.InteractionRequests.Last(value => value.Status == "open"); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, NormalizedValue.CreateString(id), out var reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private static GameState Restore(GameState state, EffectRegistry registry)
        {
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); return recovered.State;
        }

        [Test] public void MultipleCandidatesUseTypedEnterpriseRequestAndOnlyUpgradeChosenBoard()
        {
            var state = State(); var registry = Registry(); var first = Id(state, Boards[0]); var second = Id(state, Boards[1]);
            var node = Run(state, registry, Range(new[] { first, second })); var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.InteractionTypeId, Is.EqualTo("enterprise.upgrade")); Assert.That(request.CandidateIds, Is.EquivalentTo(new[] { first, second }));
            Assert.That(request.CandidateSetId, Is.Not.Empty); Assert.That(request.CandidateResolutionId, Is.EqualTo(request.CandidateSetId));
            state = Restore(state, registry); Answer(state, registry, second);
            Assert.That(Level(state, first), Is.Zero); Assert.That(Level(state, second), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Count(value => value.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise), Is.EqualTo(1));
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId).NormalizedResult, "selectedEnterpriseItemId"), Is.EqualTo(second));
        }
        [TestCase(0, 1, 1)][TestCase(5, 5, 0)] public void SingleCandidateAutomaticallyUpgradesOrCompletesAtLimit(int oldLevel, int finalLevel, int rewards)
        {
            var state = State(oldLevel); var registry = Registry(); string id = Id(state, Boards[0]); var node = Run(state, registry, Range(new[] { id }));
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty); Assert.That(Level(state, id), Is.EqualTo(finalLevel)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(rewards));
            Assert.That(node.EnterpriseArrivalReceipts.Count, Is.EqualTo(rewards)); Assert.That(Restore(state, registry).FindPlayer(1).Score, Is.EqualTo(rewards));
        }
        [Test] public void EmptyScopeCompletesWithoutChoiceOrIncompleteLog()
        {
            var state = State(); var registry = Registry(); var node = Run(state, registry, Range(Array.Empty<string>()));
            Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo("no_candidates")); Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(state.Logs.Any(value => value.Message.Contains("incomplete_effect_skipped")), Is.False); Restore(state, registry);
        }
        [Test] public void FixedTargetAndFourPlusTwoKeepExistingArrivalContract()
        {
            var state = State(4); var registry = Registry(); var id = Id(state, Boards[0]); var node = Run(state, registry, EnterpriseEffectExecutor.UpgradeSpec(1, id, 2));
            Assert.That(Level(state, id), Is.EqualTo(5)); Assert.That(node.EnterpriseArrivalReceipts.Count, Is.EqualTo(1)); Assert.That(Value(node.NormalizedResult, "selectedEnterpriseItemId"), Is.Null);
        }
        [Test] public void RangeFourPlusTwoAlsoHasOnlyOneTrueArrival()
        {
            var state = State(4); var registry = Registry(); var id = Id(state, Boards[0]); var node = Run(state, registry, Range(new[] { id }, 2));
            Assert.That(Level(state, id), Is.EqualTo(5)); Assert.That(node.EnterpriseArrivalReceipts.Count, Is.EqualTo(1)); Restore(state, registry);
        }
        [Test] public void RestoringWhileArrivalRewardWaitsDoesNotSelectAgainOrRepeatReward()
        {
            var state = State(); var registry = Registry(true); var id = Id(state, Boards[0]); var node = Run(state, registry, Range(new[] { id }));
            Assert.That(Level(state, id), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Score, Is.Zero); string arrival = node.EnterpriseArrivalReceipts.Single().EventId;
            state = Restore(state, registry); Answer(state, registry, "accept"); state = Restore(state, registry);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventId == arrival), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId).EnterpriseArrivalReceipts.Count, Is.EqualTo(1));
        }
        [TestCase("scope")][TestCase("selected")][TestCase("receipt")][TestCase("missing")]
        public void ForgedContinuationOrArrivalIsRejectedOnRecovery(string field)
        {
            var state = State(); var registry = Registry(true); var id = Id(state, Boards[0]); var node = Run(state, registry, Range(new[] { id }));
            var archive = HostRecoveryService.CreateArchive(state); node = archive.Snapshot.State.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId);
            if (field == "scope") Set(node, "upgradeCandidateItemIds", Strings(new[] { Id(state, Boards[2]) }));
            else if (field == "selected") Set(node, "selectedEnterpriseItemId", NormalizedValue.CreateString(Id(state, Boards[2])));
            else if (field == "receipt") node.EnterpriseArrivalReceipts[0].EnterpriseItemId = Id(state, Boards[2]);
            else node.ContinuationData.Properties.RemoveAll(value => value.Name == "selectedEnterpriseItemId");
            Assert.That(HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry).Succeeded, Is.False);
        }
        [Test] public void CandidateThatLeftOriginalRangeCannotExpandSelectionToOtherBoard()
        {
            var state = State(); var registry = Registry(); string first = Id(state, Boards[0]), second = Id(state, Boards[1]), excluded = Id(state, Boards[2]);
            var node = Run(state, registry, Range(new[] { first, second }));
            // 模拟候选回答前公开区事实变化；新合法集只能收窄原授权集合。
            var item = state.ItemZones.Items.Single(value => value.ItemId == first);
            state.ItemZones.Orders.FirstOrDefault(order => order.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
            item.ZoneId = state.ItemZones.Zones.Single(zone => zone.LocalZoneId == "retired").ZoneId;
            state.ItemZones.Orders.FirstOrDefault(order => order.ZoneId == item.ZoneId)?.ItemIds.Add(item.ItemId);
            Answer(state, registry, first);
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId).ContinuationData, "selectedEnterpriseItemId"), Is.EqualTo(second)); Assert.That(Level(state, excluded), Is.Zero);
        }
        [Test] public void DuplicateScopeAndBothTargetsAreRejected()
        {
            var state = State(); string id = Id(state, Boards[0]);
            var duplicate = Range(new[] { id, id }); Assert.That(EnterpriseEffectExecutor.ValidateArguments(duplicate.EffectTypeId, duplicate.NormalizedArguments), Is.Not.Empty);
            var ambiguous = Range(new[] { id }); ambiguous.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "enterpriseItemId", Value = NormalizedValue.CreateString(id) });
            Assert.That(EnterpriseEffectExecutor.ValidateArguments(ambiguous.EffectTypeId, ambiguous.NormalizedArguments), Is.EqualTo("enterprise_upgrade_target_ambiguous"));
        }
        [Test] public void DistinctUpgradeUsesEnterprisePageRequest()
        {
            var state = State(); var registry = Registry(); Run(state, registry, EffectSpec.Create(EnterpriseDistinctUpgradeFlow.TypeId,
                Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "count", NormalizedValue.CreateInteger(2), "levels", NormalizedValue.CreateInteger(1)), 1));
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open"); Assert.That(request.InteractionTypeId, Is.EqualTo("enterprise.upgrade"));
            Assert.That(request.PromptKey, Is.EqualTo("enterprise.choose_distinct")); Restore(state, registry);
        }
        private static EffectSpec AllPlayersRange(IEnumerable<string> ids, int player = 1) => EffectSpec.Create(EnterpriseEffectExecutor.UpgradeAllPlayersTypeId,
            Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString()), "enterpriseItemIds", Strings(ids), "levels", NormalizedValue.CreateInteger(1)), player);

        [Test] public void CollectiveRangeChoosesOnceAndRestoresBetweenOrderedArrivalRewards()
        {
            var state = State(); var registry = Registry(true); string first = Id(state, Boards[0]), second = Id(state, Boards[1]);
            var node = Run(state, registry, AllPlayersRange(new[] { first, second }, 2)); var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(request.InteractionTypeId, Is.EqualTo("enterprise.upgrade")); Assert.That(request.AnsweringPlayerId, Is.EqualTo(2));
            Assert.That(node.ChildEffectIds, Is.Empty); Assert.That(Value(node.ContinuationData, "authorizedPlayerIds"), Is.Null);
            state = Restore(state, registry); Answer(state, registry, second);
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 2, second), Is.EqualTo(1)); Assert.That(Level(state, second), Is.Zero);
            state = Restore(state, registry); Answer(state, registry, "accept");
            Assert.That(state.FindPlayer(2).Score, Is.EqualTo(1)); Assert.That(Level(state, second), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
            state = Restore(state, registry); Answer(state, registry, "accept"); state = Restore(state, registry);
            var parent = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == node.EffectId);
            Assert.That(parent.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Text(parent.NormalizedResult, "selectedEnterpriseItemId"), Is.EqualTo(second));
            Assert.That(state.EffectRuntime.RuleEvents.Where(e => e.EventType == EnterpriseEffectExecutor.LevelChangedEvent).Select(e => e.PlayerId), Is.EqualTo(new[] { 2, 1 }));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.InteractionTypeId == "enterprise.upgrade"), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(Level(state, first), Is.Zero);
            Assert.That(Value(parent.NormalizedArguments, "enterpriseItemId"), Is.Null);
        }
        [TestCase(0)][TestCase(1)][TestCase(5)] public void CollectiveEmptyOrSingleScopeRestoresWithoutSelector(int level)
        {
            var state = State(level); var registry = Registry(); string target = Id(state, Boards[0]);
            var node = Run(state, registry, AllPlayersRange(level == 0 ? Array.Empty<string>() : new[] { target }));
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty); state = Restore(state, registry);
            Assert.That(Level(state, target), Is.EqualTo(level == 1 ? 2 : level));
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 2, target), Is.EqualTo(level == 1 ? 2 : level));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == node.EffectId).ChildEffectIds.Count, Is.EqualTo(level == 0 ? 0 : 2));
            if (level == 0) Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo("no_candidates"));
        }
        [Test] public void CollectiveFixedTargetKeepsOriginalAuthorizationAndArrivalContract()
        {
            var state = State(); var registry = Registry(); string target = Id(state, Boards[0]);
            var node = Run(state, registry, EffectSpec.Create(EnterpriseEffectExecutor.UpgradeAllPlayersTypeId,
                Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "enterpriseItemId", NormalizedValue.CreateString(target), "levels", NormalizedValue.CreateInteger(1)), 1));
            Restore(state, registry); Assert.That(Level(state, target), Is.EqualTo(1)); Assert.That(new EnterpriseStateQuery().GetLevel(state, 2, target), Is.EqualTo(1));
            Assert.That(Value(node.NormalizedResult, "selectedEnterpriseItemId"), Is.Null);
        }
        [TestCase("scope")][TestCase("selected")][TestCase("definition")][TestCase("child_target")][TestCase("authorization")]
        public void ForgedCollectiveSelectionOrAuthorizationIsRejectedOnRecovery(string field)
        {
            var state = State(); var registry = Registry(true); string target = Id(state, Boards[0]), excluded = Id(state, Boards[2]);
            var node = Run(state, registry, AllPlayersRange(new[] { target })); var archive = HostRecoveryService.CreateArchive(state);
            var parent = archive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == node.EffectId);
            if (field == "scope") Set(parent, "upgradeCandidateItemIds", Strings(new[] { excluded }));
            else if (field == "selected") Set(parent, "selectedEnterpriseItemId", NormalizedValue.CreateString(excluded));
            else if (field == "definition") Set(parent, "upgradeCandidateDefinitionIds", Strings(new[] { Boards[2] }));
            else if (field == "authorization") Set(parent, "authorizedPlayerIds", NormalizedValue.CreateArray(new[] { NormalizedValue.CreateInteger(2), NormalizedValue.CreateInteger(1) }.ToList()));
            else
            {
                var child = archive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == parent.ChildEffectIds[0]);
                child.NormalizedArguments.Properties.Single(p => p.Name == "enterpriseItemId").Value = NormalizedValue.CreateString(excluded);
            }
            Assert.That(HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry).Succeeded, Is.False);
        }
        [Test] public void CollectiveTargetValidationRejectsAmbiguousDuplicateOrInactiveScope()
        {
            var state = State(); var registry = Registry(); string target = Id(state, Boards[0]);
            var duplicate = AllPlayersRange(new[] { target, target }); Assert.That(EnterpriseEffectExecutor.ValidateArguments(duplicate.EffectTypeId, duplicate.NormalizedArguments), Is.Not.Empty);
            var ambiguous = AllPlayersRange(new[] { target }); ambiguous.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "enterpriseItemId", Value = NormalizedValue.CreateString(target) });
            Assert.That(EnterpriseEffectExecutor.ValidateArguments(ambiguous.EffectTypeId, ambiguous.NormalizedArguments), Is.EqualTo("enterprise_upgrade_target_ambiguous"));
            var missing = AllPlayersRange(Array.Empty<string>()); missing.NormalizedArguments.Properties.RemoveAll(p => p.Name == "enterpriseItemIds");
            Assert.That(EnterpriseEffectExecutor.MissingFields(missing.EffectTypeId, missing.NormalizedArguments), Does.Contain("enterpriseItemId"));
            var rejected = Run(state, registry, AllPlayersRange(new[] { "unknown-board" }));
            Assert.That(rejected.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty); Assert.That(Level(state, target), Is.Zero);
        }
        [TestCase("enterprise.rhodes_island", true)][TestCase("infected.enterprise.rhodes_island", true)]
        [TestCase("enterprise.rhodes_island", false)][TestCase("infected.enterprise.rhodes_island", false)]
        public void RealRhodesLuaCompilesScopeExcludingOwnBoardInsteadOfChoice(string definition, bool hasOtherBoard)
        {
            string root = Environment.GetEnvironmentVariable("NOMADCITY_TEST_CONTENT_ROOT");
            if (string.IsNullOrEmpty(root)) root = Path.Combine(UnityEngine.Application.dataPath, "StreamingAssets", "Content", "core");
            string source = File.ReadAllText(Path.Combine(root, "lua", "enterprises", definition + ".level_1.special.lua"));
            LuaSnapshotValue Instance(string id, string type, string name) => LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("id", LuaSnapshotValue.String(id)), new LuaSnapshotEntry("contentType", LuaSnapshotValue.String(type)), new LuaSnapshotEntry("definitionId", LuaSnapshotValue.String(name)) });
            var instances = new List<LuaSnapshotValue> { Instance("source-board", "enterprise", definition), Instance("not-board", "character", "test.character") };
            if (hasOtherBoard) instances.Add(Instance("other-board", "enterprise", "test.enterprise.other"));
            var facade = new LuaFacadeSnapshot { GlobalContent = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("instances", LuaSnapshotValue.Array(instances)) }) };
            var script = new LuaScriptDefinition("test.rhodes.upgrade", "test.rhodes.upgrade", "test.rhodes.upgrade", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            // 内容/接口合同独立于首次Mono JIT耗时；生产宿主预算保持不变。
            var budget = LuaExecutionBudget.Default;
            var host = new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(budget.MaxInstructions, 1000, budget.MaxTableDepth, budget.MaxTableEntries, budget.MaxEffectSpecs, budget.MaxScriptLength));
            var result = host.Invoke(script, new LuaInvocationContext("test.event", "test.event", "p1", 0, "1.0.0", 2, 0, 0, facadeSnapshot: facade));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            Assert.That(specs.Single().EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.UpgradeEnterprise)); Assert.That(Value(specs[0].NormalizedArguments, "enterpriseItemIds").Items.Select(value => value.StringValue), Is.EqualTo(hasOtherBoard ? new[] { "other-board" } : Array.Empty<string>()));
        }
    }
}
