using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
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
    public sealed class EnterpriseEffectTests
    {
        private const string Business = "test.enterprise.business", Research = "test.enterprise.research";
        private static EnterpriseDefinition Board(string id, params string[] kinds) => new EnterpriseDefinition { DefinitionId = id, RuntimeId = id,
            Levels = kinds.Select((kind, level) => new EnterpriseLevelDefinition { Level = level, Kind = kind, AbilityId = kind == "reward" || kind == "special" ? id + ".level_" + level + "." + kind : "" }).ToList() };
        private static EnterpriseDefinitions Definitions(Action<EnterpriseDefinitions> configure = null)
        {
            var definitions = new EnterpriseDefinitions();
            var business = Board(Business, "empty", "reward", "special", "special", "reward", "reward"); definitions.Register(business);
            definitions.BindAbility(Business + ".level_1.reward", context => new[] { ResourceEffectSpecFactory.Gain(context.Event.PlayerId, ResourceType.GoldVoucher, 2) });
            definitions.BindAbility(Business + ".level_2.special", context => new[] { EffectSpec.Condition(new[] { LoseScore(context.Event.PlayerId, 1) }, new[] { ResourceEffectSpecFactory.Gain(context.Event.PlayerId, ResourceType.GoldVoucher, 20) }) });
            definitions.BindAbility(Business + ".level_3.special", context => new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 3) });
            definitions.BindAbility(Business + ".level_4.reward", context => new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) });
            definitions.BindAbility(Business + ".level_5.reward", context => new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 2) });
            var research = Board(Research, "empty", "special", "department_reward", "department_reward", "department_reward", "department_reward"); research.DepartmentRewardPolicy = "active_department"; definitions.Register(research);
            definitions.BindAbility(Research + ".level_1.special", context => new[] { Spec(LuaDomainEffectTypeIds.SwitchDepartment, context.Event.PlayerId, "enterpriseItemId", Value(context.Event.Payload, "enterpriseItemId").Clone()) });
            foreach (string department in new[] { "test.department.engineering", "test.department.structural" })
            {
                definitions.Register(new DepartmentDefinition { DefinitionId = department, EnterpriseRuntimeId = Research, Rewards = Enumerable.Range(2, 4).Select(level => new DepartmentRewardDefinition { Level = level, AbilityId = department + ".level_" + level + ".reward" }).ToList() });
                foreach (int level in Enumerable.Range(2, 4))
                {
                    int rewardLevel = level;
                    definitions.BindAbility(department + ".level_" + level + ".reward", context => department.EndsWith("structural") && rewardLevel == 5
                        ? new[] { Spec(EnterpriseEffectExecutor.ChangeLevelTypeId, context.Event.PlayerId, "enterpriseItemId", Value(context.Event.Payload, "enterpriseItemId").Clone(), "levelDelta", NormalizedValue.CreateInteger(-1), "resolveArrivalReward", NormalizedValue.CreateBoolean(true)) }
                        : department.EndsWith("structural") && rewardLevel == 4
                            ? new[] { EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(context.Event.PlayerId, ResourceType.OriginiumShard, 3) }, new[] { ResourceEffectSpecFactory.Gain(context.Event.PlayerId, ResourceType.PureOriginium, 1) }) }
                            : new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, rewardLevel) });
                }
            }
            configure?.Invoke(definitions); definitions.Freeze(); return definitions;
        }
        private static EffectRegistry Registry(EnterpriseDefinitions definitions = null)
        {
            var registry = new EffectRegistry { ContentPackHash = "test.enterprise.content" }; EnterpriseEffectExecutor.Register(registry, definitions ?? Definitions());
            GenericLuaEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry);
            return registry;
        }
        private static GameState State(int businessLevel = 0, int researchLevel = 0, int opponentBusinessLevel = 0, EnterpriseDefinitions definitions = null, int playerCount = 2)
        {
            var map = new GameMapDefinition { MapId = "test.enterprise.map", MinPlayers = 2, MaxPlayers = 4 };
            var source = new GameState { GameId = "test.enterprise.game", MapId = map.MapId, ContentPackHash = "test.enterprise.content",
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = playerCount, ContentPackHash = "test.enterprise.content", EnabledExpansionIds = new List<string> { "core", "enterprise" } },
                Players = Enumerable.Range(1, playerCount).Select(player => new PlayerState { PlayerId = player, Name = "玩家" + player, Resources = new ResourceSet { OriginiumShard = player == 1 ? 6 : 0 } }).ToList() };
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "supply", DefinitionId = "component.marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "supply" } };
            foreach (string id in new[] { Business, Research }) recipe.Add(new ItemRecipeEntry { EntryId = id, DefinitionId = id, TypeId = "core.board_card", Quantity = 1, InitialZoneRole = "enterprises" });
            foreach (string id in new[] { "test.department.engineering", "test.department.structural" }) recipe.Add(new ItemRecipeEntry { EntryId = id, DefinitionId = id, TypeId = "core.board_card", Quantity = 1, InitialZoneRole = "departments" });
            var declarations = new[] { Business, Research }.Select(id => new ItemCardZoneDeclaration { DefinitionId = id, CardZones = Enumerable.Range(1, playerCount).Select(playerId => new CardZoneDefinition {
                LocalZoneId = "cooperation_" + playerId, ZoneTemplateId = "core.zone.board_marker_track", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track, EntryPolicyId = "core.owned_player_marker", ExitPolicyId = "core.explicit_transfer", VisibilityPolicyId = ItemZoneVisibility.Public,
                CarrierExitPolicyId = "core.preserve_contents", DisplayAnchorId = "cooperation" }).ToList() }).ToArray();
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, recipe, declarations);
            foreach (string enterprise in new[] { Business, Research }) foreach (int player in Enumerable.Range(1, playerCount))
            {
                var track = state.ItemZones.Zones.Single(zone => zone.ParentId == ItemId(state, enterprise) && zone.LocalZoneId == "cooperation_" + player);
                var marker = state.ItemZones.Items.First(item => item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == player && state.ItemZones.Zones.Single(zone => zone.ZoneId == item.ZoneId).LocalZoneId == "supply");
                int level = enterprise == Business ? player == 1 ? businessLevel : opponentBusinessLevel : player == 1 ? researchLevel : 2;
                marker.ZoneId = track.ZoneId; marker.SlotId = "level_" + level; state.ItemZones.Orders.Single(order => order.ZoneId == track.ZoneId).ItemIds.Add(marker.ItemId);
            }
            state.ItemZones.Relations.Add(new ItemRelationState { RelationId = StableIdFactory.Create("relation", state.GameId, Research, "active_department"), RelationTypeId = EnterpriseStateQuery.ActiveDepartmentRelation,
                SourceItemId = ItemId(state, Research), TargetItemId = ItemId(state, "test.department.engineering") });
            Valid(state); return state;
        }
        private static string ItemId(GameState state, string definitionId) => state.ItemZones.Items.Single(item => item.DefinitionId == definitionId).ItemId;
        private static void Valid(GameState state) { Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason); Assert.That(state.EffectRuntime.TryValidate(out reason), Is.True, reason); }
        private static int Level(GameState state, string definitionId = Business, int playerId = 1) => new EnterpriseStateQuery().GetLevel(state, playerId, ItemId(state, definitionId));
        private static EffectSpec Spec(string typeId, int playerId = 1, params object[] values)
        {
            var fields = new List<object> { "apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(playerId.ToString(CultureInfo.InvariantCulture)) }; fields.AddRange(values);
            return EffectSpec.Create(typeId, Object(fields.ToArray()), playerId);
        }
        private static EffectSpec LoseScore(int playerId, int amount) => EffectSpec.Create(LuaDomainEffectTypeIds.LoseScore, Object("player", NormalizedValue.CreateString(playerId.ToString()), "amount", NormalizedValue.CreateInteger(amount)), playerId);
        private static EffectSpec Activate(GameState state, int unlockedByPlayerId = 1, string specialId = null) => specialId != null
            ? EnterpriseSpecialSelectionExecutor.ActivateSpec(1, unlockedByPlayerId, ItemId(state, Business), specialId)
            : Spec(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial, 1, "unlockedByPlayer", NormalizedValue.CreateString(unlockedByPlayerId.ToString()));
        private static EffectNodeRuntimeState Run(GameState state, EffectSpec spec, EffectRegistry registry = null)
        {
            registry = registry ?? Registry(); var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec); var result = engine.RunUntilQuiescent(); Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state); return engine.GetNode(id);
        }
        private static GameState Restore(GameState state, EffectRegistry registry)
        {
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry); Assert.That(result.Succeeded, Is.True, result.Diagnostic); Valid(result.State); return result.State;
        }
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Last(value => value.Status == "open");
        private static void AnswerRequest(GameState state, EffectRegistry registry, params string[] ids)
        {
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry); NormalizedValue answer = ids.Length == 1 ? NormalizedValue.CreateString(ids[0]) : Strings(ids);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, answer, out var reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state);
        }
        private static EffectSpec Sequence(params EffectSpec[] effects) { var spec = EffectSpec.Create(LuaDomainEffectTypeIds.Repeat, Object("apiVersion", NormalizedValue.CreateInteger(1), "count", NormalizedValue.CreateInteger(1)), 1); spec.NestedEffectGroups.Add(effects.ToList()); return spec; }

        [TestCase(0)][TestCase(1)][TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)]
        public void LevelReadsActualCooperationSlotWithoutWritableCache(int level)
        {
            var state = State(level); Assert.That(Level(state), Is.EqualTo(level)); Assert.That(typeof(PlayerState).GetField("EnterpriseLevels"), Is.Null);
        }
        [Test] public void MissingCooperationMarkerFailsInsteadOfReturningZero()
        {
            var state = State(); var track = state.ItemZones.Zones.Single(zone => zone.ParentId == ItemId(state, Business) && zone.OwnerPlayerId == 1);
            var marker = state.ItemZones.Items.Single(item => item.ZoneId == track.ZoneId); marker.ZoneId = FindZone(state, "supply", 1).ZoneId; marker.SlotId = "";
            Assert.Throws<InvalidOperationException>(() => Level(state));
        }
        [Test] public void RewardUnlockAndBlankArrivalsRemainDistinct()
        {
            var state = State(); var registry = Registry(); Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, Business), 3), registry);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Assert.That(new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetSpecialCandidates(state, 1, 1, 0, new[] { ItemId(state, Business) }).Count, Is.EqualTo(2));
        }
        [TestCase(4, 2, 1, 2)][TestCase(5, 1, 0, 0)]
        public void CappedUpgradeRewardsOnlyTrueArrivalsAndContinuesSibling(int oldLevel, int levels, int arrivals, int rewardScore)
        {
            var state = State(oldLevel); var registry = Registry(); Run(state, Sequence(EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, Business), levels), ResourceEffectSpecFactory.GainScore(1, 1)), registry);
            Assert.That(Level(state), Is.EqualTo(5)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(rewardScore + 1));
            var upgrade = state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise); Assert.That(upgrade.EnterpriseArrivalReceipts.Count, Is.EqualTo(arrivals));
            Assert.That(state.EffectRuntime.DispatchReceipts.Count(receipt => receipt.SubscriptionId == "enterprise.resolve:" + EnterpriseEffectExecutor.LevelChangedEvent), Is.EqualTo(arrivals));
        }
        [Test] public void DepartmentAtLevelOneSwitchesGloballyWithoutInventedReward()
        {
            var state = State(researchLevel: 1); var registry = Registry(); int opponentScore = state.FindPlayer(2).Score;
            Run(state, Spec(LuaDomainEffectTypeIds.SwitchDepartment, 1, "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, Research)), "departmentItemId", NormalizedValue.CreateString(ItemId(state, "test.department.structural"))), registry);
            Assert.That(new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetActiveDepartmentItemId(state, ItemId(state, Research)), Is.EqualTo(ItemId(state, "test.department.structural")));
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.FindPlayer(2).Score, Is.EqualTo(opponentScore)); Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == EnterpriseEffectExecutor.DepartmentChangedEvent), Is.EqualTo(1));
        }
        [Test] public void DepartmentSwitchRewardsOnlyExecutingPlayersCurrentLevel()
        {
            var state = State(researchLevel: 4); var registry = Registry(); Run(state, Spec(LuaDomainEffectTypeIds.SwitchDepartment, 1, "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, Research)), "departmentItemId", NormalizedValue.CreateString(ItemId(state, "test.department.structural"))), registry);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3)); Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1)); Assert.That(state.FindPlayer(2).Score, Is.Zero);
        }
        [TestCase(3, 1)][TestCase(0, 0)] public void StructuralArrivalLowersToFourAndResolvesRealConditionalPayment(int shards, int pure)
        {
            var state = State(researchLevel: 4); state.FindPlayer(1).Resources.OriginiumShard = shards; var registry = Registry();
            state.ItemZones.Relations.Single().TargetItemId = ItemId(state, "test.department.structural");
            Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, Research), 1), registry);
            Assert.That(Level(state, Research), Is.EqualTo(4)); Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure)); Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Sum(node => node.EnterpriseArrivalReceipts.Count), Is.EqualTo(2));
            int before = state.EffectRuntime.RuleEvents.Count; state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False); Assert.That(state.EffectRuntime.RuleEvents.Count, Is.EqualTo(before));
        }
        [Test] public void LaterTrueUpgradeHasNewArrivalReceiptsAndCanRewardAgain()
        {
            var state = State(researchLevel: 4); var registry = Registry(); state.ItemZones.Relations.Single().TargetItemId = ItemId(state, "test.department.structural");
            Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, Research), 1), registry); Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, Research), 1), registry);
            Assert.That(Level(state, Research), Is.EqualTo(4)); Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(2));
            var ids = state.EffectRuntime.EffectNodes.SelectMany(node => node.EnterpriseArrivalReceipts).Select(receipt => receipt.EventId).ToList(); Assert.That(ids.Distinct().Count(), Is.EqualTo(4));
        }
        [Test] public void UpgradeThenActivateOffersDirectSpecialAndSavesOneTarget()
        {
            var state = State(2); var registry = Registry(); string itemId = ItemId(state, Business);
            var parent = Run(state, Spec(EnterpriseSpecialSelectionExecutor.UpgradeThenActivateTypeId, 1, "levels", NormalizedValue.CreateInteger(1), "enterpriseItemIds", Strings(new[] { itemId })), registry);
            var request = Open(state); Assert.That(request.InteractionTypeId, Is.EqualTo("enterprise.special")); Assert.That(Level(state), Is.EqualTo(2));
            var selected = parent.FrozenEnterpriseSpecials.Single(value => value.Candidate.SpecialId == Business + ".level_3.special");
            state = Restore(state, registry); AnswerRequest(state, registry, selected.Candidate.CandidateId);
            Assert.That(Level(state), Is.EqualTo(3)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3)); Assert.That(state.EffectRuntime.InteractionRequests.Count, Is.EqualTo(1));
            var result = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == parent.EffectId).NormalizedResult; Assert.That(Text(result, "selectedEnterpriseItemId"), Is.EqualTo(itemId)); Assert.That(Text(result, "selectedSpecialId"), Is.EqualTo(selected.Candidate.SpecialId));
        }
        [Test] public void SeparateUpgradeAndActivationKeepIndependentEnterpriseTargets()
        {
            var state = State(0, 1); var registry = Registry(); Run(state, Sequence(EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, Business), 1),
                EnterpriseSpecialSelectionExecutor.ActivateSpec(1, 1, ItemId(state, Research), Research + ".level_1.special")), registry);
            Assert.That(Level(state), Is.EqualTo(1)); Assert.That(Open(state).InteractionTypeId, Is.EqualTo("enterprise.department"));
            AnswerRequest(state, registry, ItemId(state, "test.department.structural")); Assert.That(Level(state, Research), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
        }
        [Test] public void BorrowedUnlockRangeRewardsExecutingPlayer()
        {
            var state = State(opponentBusinessLevel: 3); var registry = Registry(); var candidates = new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetSpecialCandidates(state, 1, 2, 0, new[] { ItemId(state, Business) });
            Assert.That(candidates.Count, Is.EqualTo(2)); Run(state, Activate(state, 2, Business + ".level_3.special"), registry);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3)); Assert.That(state.FindPlayer(2).Score, Is.Zero); Assert.That(Level(state), Is.Zero);
        }
        [TestCase(0)][TestCase(-20)] public void ScorePaymentHasNoLowerBoundAndSurvivesPublicProjectionAndStorage(int score)
        {
            var state = State(2); state.FindPlayer(1).Score = score; var registry = Registry(); Run(state, Activate(state, specialId: Business + ".level_2.special"), registry);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score - 1)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20));
            state = Restore(state, registry); var view = GameStateViewProjector.ProjectForPlayer(state, 2); Assert.That(view.Players.Single(player => player.PlayerId == 1).Score, Is.EqualTo(score - 1));
            Assert.That(JObject.FromObject(view)["Players"].Any(player => (int)player["Score"] == score - 1), Is.True);
        }
        [Test] public void NoUnlockedSpecialCompletesOfficeEntryAndScoreWithoutEmptyDialog()
        {
            var state = State(); var registry = Registry(); Run(state, Sequence(ResourceEffectSpecFactory.GainScore(1, 1), Activate(state)), registry);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == LuaDomainEffectTypeIds.ActivateEnterpriseSpecial).NormalizedResult, "reasonCode"), Is.EqualTo("no_candidates"));
        }
        [Test] public void IncompleteLeftSkipsWholeConditionAndLogsOnceAcrossRecovery()
        {
            var state = State(); var registry = Registry(); var incomplete = Spec(LuaDomainEffectTypeIds.UpgradeEnterprise, 1, "levels", NormalizedValue.CreateInteger(1));
            Run(state, Sequence(EffectSpec.Condition(new[] { incomplete }, new[] { ResourceEffectSpecFactory.GainScore(1, 9) }), ResourceEffectSpecFactory.GainScore(1, 1)), registry);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(state.Logs.Count(log => log.Message.Contains("incomplete_effect_skipped")), Is.EqualTo(1));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.Logs.Count(log => log.Message.Contains("incomplete_effect_skipped")), Is.EqualTo(1));
        }
        [Test] public void MissingAbilitySkipsItsContentWithoutUndoingArrival()
        {
            var definitions = Definitions(value => value.Register(Board("test.enterprise.unbound", "empty", "reward", "empty", "empty", "empty", "empty")));
            var state = State(); state.ItemZones.Items.Single(item => item.DefinitionId == Business).DefinitionId = "test.enterprise.unbound"; var registry = Registry(definitions);
            Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, "test.enterprise.unbound"), 1), registry);
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, ItemId(state, "test.enterprise.unbound")), Is.EqualTo(1)); Assert.That(state.Logs.Any(log => log.Message.Contains("incomplete_effect_skipped")), Is.True);
        }
        [Test] public void PaymentFailureIsDistinctFromIncompleteSkipAndBlocksRightSide()
        {
            var state = State(); state.FindPlayer(1).Resources.GoldVoucher = 0; var registry = Registry();
            Run(state, EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, ResourceType.GoldVoucher, 1) }, new[] { ResourceEffectSpecFactory.GainScore(1, 10) }), registry);
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.Logs.Any(log => log.Message.Contains("incomplete_effect_skipped")), Is.False); Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.Status == EffectNodeStatus.Failed), Is.True);
        }
        [Test] public void UnlockedRewardsAndBlankSlotsNeverBecomeSpecialCandidates()
        {
            var definitions = Definitions(value => value.Register(Board("test.enterprise.blank", "empty", "empty", "empty", "empty", "empty", "empty")));
            var state = State(5); var query = new EnterpriseStateQuery(definitions); var ids = query.GetSpecialCandidates(state, 1, 1, int.MaxValue, new[] { ItemId(state, Business) });
            Assert.That(ids.Select(id => id.SpecialId), Is.EquivalentTo(new[] { Business + ".level_2.special", Business + ".level_3.special" })); Assert.That(ids.All(id => id.EffectiveLevelCeiling == 5), Is.True);
        }
        [Test] public void UnknownSchemaAndExecutionPermissionAreNotContentSkips()
        {
            var state = State(); var registry = Registry(); var spec = EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, Business), 1); Value(spec.NormalizedArguments, "apiVersion").IntegerValue = 2;
            Assert.That(Run(state, spec, registry).FailureReason, Is.EqualTo("enterprise_schema_unsupported")); Assert.That(state.Logs, Is.Empty);
            spec = EnterpriseEffectExecutor.UpgradeSpec(2, ItemId(state, Business), 1); spec.PlayerId = 1; var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(spec); Assert.That(engine.RunUntilQuiescent().Faulted, Is.True); Assert.That(Level(state), Is.Zero);
        }
        [Test] public void FrozenOrderExcludesNewUnlockAndResumesRemainingEffects()
        {
            var definitions = Definitions(value =>
            {
                value.Register(Board("test.enterprise.frozen", "empty", "special", "special", "special", "empty", "empty"));
                value.BindAbility("test.enterprise.frozen.level_1.special", context => new[] { EnterpriseEffectExecutor.UpgradeSpec(1, Text(context.Event.Payload, "enterpriseItemId"), 1),
                    EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString("1"), "options", Strings(new[] { "continue" })), 1) });
                value.BindAbility("test.enterprise.frozen.level_2.special", context => new[] { ResourceEffectSpecFactory.GainScore(1, 2) });
                value.BindAbility("test.enterprise.frozen.level_3.special", context => new[] { ResourceEffectSpecFactory.GainScore(1, 30) });
            });
            var state = State(2); state.ItemZones.Items.Single(item => item.DefinitionId == Business).DefinitionId = "test.enterprise.frozen"; var registry = Registry(definitions);
            var parent = Run(state, Spec(EnterpriseSpecialSelectionExecutor.ActivateAllTypeId, 1, "unlockedByPlayer", NormalizedValue.CreateString("1"), "enterpriseItemIds", Strings(new[] { ItemId(state, "test.enterprise.frozen") })), registry);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("enterprise.special.order")); var order = parent.FrozenEnterpriseSpecials.OrderBy(value => value.Candidate.SpecialId).Select(value => value.Candidate.CandidateId).ToArray();
            AnswerRequest(state, registry, order); Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, ItemId(state, "test.enterprise.frozen")), Is.EqualTo(3));
            state = Restore(state, registry); var recovered = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == parent.EffectId); Assert.That(recovered.FrozenEnterpriseSpecials.Count, Is.EqualTo(2)); Assert.That(Number(recovered.ContinuationData, "nextIndex"), Is.EqualTo(1));
            AnswerRequest(state, registry, "continue"); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2)); Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == "EnterpriseSpecialEffectActivated"), Is.EqualTo(2));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2));
        }
        [Test] public void UpgradeRewardDefinitionChangeInvalidatesPreviouslySelectedSpecial()
        {
            var definitions = Definitions(value =>
            {
                value.Register(Board("test.enterprise.replaced", "empty", "empty", "empty", "empty", "empty", "empty"));
                value.Register(Board("test.enterprise.replacing", "empty", "empty", "special", "reward", "empty", "empty"));
                value.BindAbility("test.enterprise.replacing.level_2.special", context => new[] { ResourceEffectSpecFactory.GainScore(1, 99) });
                value.BindAbility("test.enterprise.replacing.level_3.reward", context => new[] { EffectSpec.Create("test.enterprise.replace_definition", playerId: 1) });
            });
            var state = State(2); state.ItemZones.Items.Single(item => item.DefinitionId == Business).DefinitionId = "test.enterprise.replacing"; var registry = Registry(definitions);
            registry.Register("test.enterprise.replace_definition", context => { context.State.ItemZones.Items.Single(item => item.DefinitionId == "test.enterprise.replacing").DefinitionId = "test.enterprise.replaced"; return EffectStepResult.Completed(); });
            var parent = Run(state, Spec(EnterpriseSpecialSelectionExecutor.UpgradeThenActivateTypeId, 1, "levels", NormalizedValue.CreateInteger(1), "enterpriseItemIds", Strings(new[] { ItemId(state, "test.enterprise.replacing") })), registry);
            AnswerRequest(state, registry, Open(state).CandidateIds.Single()); Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.EffectRuntime.EffectNodes.Single(node => node.EffectId == parent.EffectId).FailureReason, Is.EqualTo("enterprise_special_no_longer_legal"));
        }
        [Test] public void OriginalHostFieldsAreRequiredForEnterpriseRecovery()
        {
            var state = State(2); var registry = Registry(); Run(state, Activate(state), registry); var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            foreach (var token in json.Descendants().OfType<JProperty>().Where(property => property.Name == "FrozenEnterpriseSpecials").ToList()) token.Remove();
            Assert.That(HostRecoveryService.RecoverJson(json.ToString(), registry).Succeeded, Is.False);
        }
        [Test] public void NewEnterpriseConstructorsCompileAndFlowTableIsReadOnly()
        {
            var state = State(0); var registry = Registry(); var definition = new ExternalContentDefinition { ContentType = "enterprise", DefinitionId = "test.enterprise.lua", RuntimeId = "test.enterprise.lua",
                Data = JObject.Parse("{levels:[{level:0,kind:'empty',abilityId:''},{level:1,kind:'reward',abilityId:'test.enterprise.lua.reward'},{level:2,kind:'empty',abilityId:''},{level:3,kind:'empty',abilityId:''},{level:4,kind:'empty',abilityId:''},{level:5,kind:'empty',abilityId:''}]}"),
                Abilities = new List<ExternalAbilityDefinition> { new ExternalAbilityDefinition { AbilityId = "test.enterprise.lua.reward", Version = "1.0.0", SubscriptionId = "content.test.enterprise.lua.reward", Script = "test.reward.lua" } } };
            var researchDefinition = new ExternalContentDefinition { ContentType = "enterprise", DefinitionId = Research, RuntimeId = Research, Data = new JObject {
                ["levels"] = JArray.FromObject(Board(Research, "empty", "special", "department_reward", "department_reward", "department_reward", "department_reward").Levels), ["departmentRewardPolicy"] = "active_department" } };
            registry.EnterpriseDefinitions = LuaContentCatalog.CreateEnterpriseDefinitions(registry, new[] { definition, researchDefinition }, path => "return function(ctx) return { Effect.GainScore({recipient=ctx.playerId,amount=4}) } end");
            state.ItemZones.Items.Single(item => item.DefinitionId == Business).DefinitionId = "test.enterprise.lua";
            string source = "return function(ctx) return { Effect.UpgradeEnterprise({apiVersion=1,player=ctx.playerId,enterpriseItemId='" + ItemId(state, "test.enterprise.lua") + "',levels=1}) } end";
            var script = new LuaScriptDefinition("test.enterprise.constructor", "test.enterprise.constructor", "test.enterprise.constructor", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("test.event", "test.event", "1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic); Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            Run(state, specs.Single(), registry); Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, ItemId(state, "test.enterprise.lua")), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(4));
            string readOnlySource = "return function(ctx) Flow.UpgradeThenActivateEnterpriseSpecial=nil return {} end";
            var readOnly = new LuaScriptDefinition("test.flow", "test.flow", "test.flow", "1.0.0", readOnlySource, LuaContentHasher.ComputeSha256(readOnlySource));
            Assert.That(new MoonSharpLuaRuntimeHost().Invoke(readOnly, new LuaInvocationContext("test.event", "test.event", "1", 0, "1.0.0", 2, 0, 0)).IsSuccess, Is.False);
        }

        [TestCase("empty", 0, 0)][TestCase("special", 0, 1)][TestCase("reward", 1, 0)]
        public void SingleKindBoardsKeepBlankRewardAndUnlockSemantics(string kind, int score, int candidates)
        {
            string definitionId = "test.enterprise.single_kind";
            var definitions = Definitions(value => { value.Register(Board(definitionId, "empty", kind, "empty", "empty", "empty", "empty")); if (kind != "empty") value.BindAbility(definitionId + ".level_1." + kind, context => new[] { ResourceEffectSpecFactory.GainScore(1, 1) }); });
            var state = State(); state.ItemZones.Items.Single(item => item.DefinitionId == Business).DefinitionId = definitionId; var registry = Registry(definitions);
            var node = Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, ItemId(state, definitionId), 1), registry);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score)); Assert.That(new EnterpriseStateQuery(definitions).GetSpecialCandidates(state, 1, 1, 0, new[] { ItemId(state, definitionId) }).Count, Is.EqualTo(candidates));
            Assert.That(node.EnterpriseArrivalReceipts.Count, Is.EqualTo(1)); Assert.That(state.EffectRuntime.DispatchReceipts.Any(receipt => receipt.EventId == node.EnterpriseArrivalReceipts[0].EventId), Is.True);
        }
        [Test] public void UpgradeWaitsForRewardChoiceBeforeNextLevelAndRestoresWithoutRepeatingArrival()
        {
            string definitionId = "test.enterprise.waiting";
            var definitions = Definitions(value =>
            {
                value.Register(Board(definitionId, "empty", "reward", "reward", "empty", "empty", "empty"));
                value.BindAbility(definitionId + ".level_1.reward", context => new[] { EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString("1"), "options", Strings(new[] { "accept" })), 1) });
                value.BindAbility(definitionId + ".level_2.reward", context => new[] { ResourceEffectSpecFactory.GainScore(1, 2) });
            });
            var state = State(); state.ItemZones.Items.Single(item => item.DefinitionId == Business).DefinitionId = definitionId; var registry = Registry(definitions); string enterpriseItemId = ItemId(state, definitionId);
            var node = Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, enterpriseItemId, 2), registry); Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, enterpriseItemId), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
            string arrivalEventId = node.EnterpriseArrivalReceipts.Single().EventId;
            state = Restore(state, registry); AnswerRequest(state, registry, "accept");
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, enterpriseItemId), Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2)); Assert.That(state.EffectRuntime.RuleEvents.Count(ruleEvent => ruleEvent.EventId == arrivalEventId), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId).EnterpriseArrivalReceipts.Count, Is.EqualTo(2));
        }
        [Test] public void DepartmentSelectionPersistsAndPublicStateShowsOneSharedBoard()
        {
            var state = State(researchLevel: 1); var registry = Registry(); Run(state, Spec(LuaDomainEffectTypeIds.SwitchDepartment, 1, "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, Research))), registry);
            var request = Open(state); Assert.That(request.CandidateSetId, Is.Not.Empty); Assert.That(request.CandidateResolutionId, Is.EqualTo(request.CandidateSetId));
            state = Restore(state, registry); AnswerRequest(state, registry, ItemId(state, "test.department.structural"));
            var publicView = GameStateViewProjector.ProjectForPlayer(state, 2); Assert.That(publicView.ItemZones.Relations.Single(relation => relation.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation).TargetItemId, Is.EqualTo(ItemId(state, "test.department.structural")));
        }
        [Test] public void ActiveCandidatesArePrivateWhileHistoricalHostSnapshotsStayOutOfPlayerDto()
        {
            var state = State(3); var registry = Registry(); Run(state, Activate(state), registry);
            var ownView = JObject.FromObject(GameStateViewProjector.ProjectForPlayer(state, 1)); var otherView = JObject.FromObject(GameStateViewProjector.ProjectForPlayer(state, 2));
            Assert.That(ownView.ToString(), Does.Contain("candidateEnterpriseItemIds")); Assert.That(otherView.ToString(), Does.Not.Contain("candidateEnterpriseItemIds"));
            Assert.That(ownView.ToString(), Does.Not.Contain("FrozenEnterpriseSpecials").And.Not.Contain("EnterpriseArrivalReceipts"));
        }
        [TestCase(1)][TestCase(2)] public void UpgradeThenActivateUsesEachEnterpriseCeilingIncludingCap(int levels)
        {
            var state = State(4, 0); var definitions = Definitions(); var candidates = new EnterpriseStateQuery(definitions).GetSpecialCandidates(state, 1, 1, levels, null);
            Assert.That(candidates.Where(candidate => candidate.EnterpriseItemId == ItemId(state, Business)).All(candidate => candidate.CurrentLevel == 4 && candidate.EffectiveLevelCeiling == 5), Is.True);
            Assert.That(candidates.Single(candidate => candidate.EnterpriseItemId == ItemId(state, Research)).EffectiveLevelCeiling, Is.EqualTo(levels)); Assert.That(Level(state, Research), Is.Zero);
        }
        [TestCase("Effect.ChangeEnterpriseLevel({apiVersion=1,player=ctx.playerId,enterpriseItemId='board',levelDelta=-1,resolveArrivalReward=true})")]
        [TestCase("Effect.ActivateEnterpriseSpecial({apiVersion=1,player=ctx.playerId,unlockedByPlayer='2',minLevel=2,maxLevel=4})")]
        [TestCase("Effect.SwitchDepartment({apiVersion=1,player=ctx.playerId,enterpriseItemId='board'})")]
        [TestCase("Flow.UpgradeThenActivateEnterpriseSpecial({apiVersion=1,player=ctx.playerId,levels=2})")]
        [TestCase("Flow.ActivateAllEnterpriseSpecials({apiVersion=1,player=ctx.playerId,unlockedByPlayer=ctx.playerId})")]
        [TestCase("Flow.UpgradeEnterpriseForAllPlayers({apiVersion=1,player=ctx.playerId,enterpriseItemId='board',levels=1})")]
        public void AllPublicEnterpriseConstructorsNormalizeAndCompile(string expression)
        {
            string source = "return function(ctx) return { " + expression + " } end";
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("test.constructor", "test.constructor", "test.constructor", "1.0.0", source, LuaContentHasher.ComputeSha256(source)), new LuaInvocationContext("test.event", "test.event", "1", 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic); Assert.That(specs.Count, Is.EqualTo(1));
        }
        [TestCase(0)][TestCase(-1)][TestCase(2147483648L)] public void InvalidUpgradeAmountCannotChangeState(long levels)
        {
            var state = State();
            if (levels > int.MaxValue) { Assert.Throws<ArgumentOutOfRangeException>(() => NormalizedValue.CreateInteger(levels)); Assert.That(Level(state), Is.Zero); return; }
            var node = Run(state, Spec(LuaDomainEffectTypeIds.UpgradeEnterprise, 1, "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, Business)), "levels", NormalizedValue.CreateInteger(levels)));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(Level(state), Is.Zero); Assert.That(node.EnterpriseArrivalReceipts, Is.Empty);
        }
        [Test] public void AllSpecialsChooseOneFrozenEnterpriseGroupAndHonorRequestedOrder()
        {
            var state = State(3, 1); var registry = Registry(); var node = Run(state, Spec(EnterpriseSpecialSelectionExecutor.ActivateAllTypeId, 1, "unlockedByPlayer", NormalizedValue.CreateString("1")), registry);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("enterprise.special.group")); AnswerRequest(state, registry, ItemId(state, Business));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("enterprise.special.order")); var order = state.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId).FrozenEnterpriseSpecials.OrderByDescending(value => value.Candidate.SpecialId).Select(value => value.Candidate.CandidateId).ToArray();
            AnswerRequest(state, registry, order); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20));
            Assert.That(state.EffectRuntime.RuleEvents.Where(value => value.EventType == "EnterpriseSpecialEffectActivated").Select(value => Text(value.Payload, "specialId")), Is.EqualTo(new[] { Business + ".level_3.special", Business + ".level_2.special" }));
        }
        [Test] public void FailedSpecialPaymentCannotSatisfyOuterCondition()
        {
            var definitions = Definitions(value => { value.Register(Board("test.enterprise.payment", "empty", "special", "empty", "empty", "empty", "empty")); value.BindAbility("test.enterprise.payment.level_1.special", context => new[] { ResourceEffectSpecFactory.Pay(1, ResourceType.GoldVoucher, 1) }); });
            var state = State(1); state.ItemZones.Items.Single(item => item.DefinitionId == Business).DefinitionId = "test.enterprise.payment"; var registry = Registry(definitions);
            Run(state, EffectSpec.Condition(new[] { EnterpriseSpecialSelectionExecutor.ActivateSpec(1, 1, ItemId(state, "test.enterprise.payment"), "test.enterprise.payment.level_1.special") }, new[] { ResourceEffectSpecFactory.GainScore(1, 9) }), registry);
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == LuaDomainEffectTypeIds.ActivateEnterpriseSpecial).Status, Is.EqualTo(EffectNodeStatus.Failed));
        }
        [Test] public void CappedUpgradeCanContinueIntoPreviouslyUnlockedSpecial()
        {
            var state = State(5); var registry = Registry(); var node = Run(state, Spec(EnterpriseSpecialSelectionExecutor.UpgradeThenActivateTypeId, 1, "levels", NormalizedValue.CreateInteger(1), "enterpriseItemIds", Strings(new[] { ItemId(state, Business) })), registry);
            string candidateId = node.FrozenEnterpriseSpecials.Single(value => value.Candidate.SpecialId == Business + ".level_3.special").Candidate.CandidateId;
            AnswerRequest(state, registry, candidateId); Assert.That(Level(state), Is.EqualTo(5)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3)); Assert.That(state.EffectRuntime.EffectNodes.Sum(value => value.EnterpriseArrivalReceipts.Count), Is.Zero);
        }
        [Test] public void MissingSnapshotMemberCannotBeRecreatedByFieldInitializers()
        {
            var state = State(3); var registry = Registry(); Run(state, Activate(state), registry); var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            foreach (var property in json.Descendants().OfType<JProperty>().Where(value => value.Name == "CurrentLevel").ToList()) property.Remove();
            Assert.That(HostRecoveryService.RecoverJson(json.ToString(), registry).Succeeded, Is.False);
        }
        [TestCase(true)][TestCase(false)] public void IncompleteLuaResourceSideSkipsCorrectSubtreeAndPreservesAlreadyPaidCost(bool missingLeft)
        {
            var state = State(); state.FindPlayer(1).Resources.GoldVoucher = 1; var registry = Registry();
            string left = missingLeft ? "Effect.PayResource({payer=ctx.playerId,resourceType='gold_voucher'})" : "Effect.PayResource({payer=ctx.playerId,resourceType='gold_voucher',amount=1})";
            string right = missingLeft ? "Effect.GainScore({recipient=ctx.playerId,amount=9})" : "Effect.GainScore({recipient=ctx.playerId})";
            string source = "return function(ctx) return { Effect.Condition({leftEffects={" + left + "},rightEffects={" + right + "}}), Effect.GainScore({recipient=ctx.playerId,amount=1}) } end";
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("test.incomplete", "test.incomplete", "test.incomplete", "1.0.0", source, LuaContentHasher.ComputeSha256(source)), new LuaInvocationContext("test.event", "test.event", "1", 0, "1.0.0", 2, 0, 1));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            Run(state, Sequence(specs.ToArray()), registry); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(missingLeft ? 1 : 0));
            Assert.That(state.Logs.Count(log => log.Message.Contains("missingFieldNames=amount")), Is.EqualTo(1)); state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.Logs.Count(log => log.Message.Contains("incomplete_effect_skipped")), Is.EqualTo(1));
        }

        private static EnterpriseDefinitions AllPlayerChoiceDefinitions()
        {
            return Definitions(definitions => {
                definitions.Register(Board("test.enterprise.shared_upgrade", "empty", "reward", "empty", "empty", "empty", "empty"));
                definitions.BindAbility("test.enterprise.shared_upgrade.level_1.reward", context => {
                    int player = context.Event.PlayerId;
                    var payChoice = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString(player.ToString()), "options", Strings(new[] { "pay" })), player);
                    payChoice.NestedEffectGroups.Add(new List<EffectSpec> { EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(player, ResourceType.OriginiumShard, 1) }, new[] { ResourceEffectSpecFactory.GainScore(player, 2) }) });
                    return new[] { payChoice, EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString(player.ToString()), "options", Strings(new[] { "accept" })), player) };
                });
            });
        }
        [Test] public void IncompleteAllPlayerFlowNeverCreatesAuthorityOrRepeatsItsSkipAfterRecovery()
        {
            var state = State(); var registry = Registry();
            Run(state, EffectSpec.Condition(new[] { Spec(EnterpriseEffectExecutor.UpgradeAllPlayersTypeId, 1, "levels", NormalizedValue.CreateInteger(1)) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 9) }), registry);
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(Level(state), Is.Zero);
            var parent = state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == EnterpriseEffectExecutor.UpgradeAllPlayersTypeId);
            Assert.That(parent.ChildEffectIds, Is.Empty); Assert.That(Value(parent.ContinuationData, "authorizedPlayerIds"), Is.Null);
            state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.Logs.Count(log => log.Message.Contains("incomplete_effect_skipped")), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
        }
        private static GameState SharedUpgradeState(int playerCount)
        {
            var state = State(playerCount: playerCount);
            state.ItemZones.Items.Single(item => item.DefinitionId == Business).DefinitionId = "test.enterprise.shared_upgrade";
            foreach (var player in state.Players) player.Resources.OriginiumShard = 3;
            state.EffectRuntime.PlayerOrderSnapshot = Enumerable.Range(1, playerCount).ToList();
            return state;
        }
        private static EffectSpec SharedUpgradeSpec(GameState state, int player) => Spec(EnterpriseEffectExecutor.UpgradeAllPlayersTypeId, player,
            "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, "test.enterprise.shared_upgrade")), "levels", NormalizedValue.CreateInteger(1));

        [TestCase(2, 2)][TestCase(3, 2)][TestCase(4, 3)]
        public void AuthorizedAllPlayerUpgradeWaitsForEachRecipientAndRestoresEveryRewardChoice(int playerCount, int initiatingPlayer)
        {
            var state = SharedUpgradeState(playerCount); var registry = Registry(AllPlayerChoiceDefinitions());
            var spec = SharedUpgradeSpec(state, initiatingPlayer); spec.SourceId = "content.test.shared_upgrade";
            string root = Run(state, spec, registry).EffectId;
            var order = Enumerable.Range(0, playerCount).Select(index => (initiatingPlayer - 1 + index) % playerCount + 1).ToArray();
            for (int index = 0; index < order.Length; index++)
            {
                int recipient = order[index];
                Assert.That(Open(state).AnsweringPlayerId, Is.EqualTo(recipient));
                Assert.That(state.EffectRuntime.RuleEvents.Where(ruleEvent => ruleEvent.EventType == EnterpriseEffectExecutor.LevelChangedEvent).Select(ruleEvent => ruleEvent.PlayerId), Is.EqualTo(order.Take(index + 1)));
                state = Restore(state, registry); var paymentRequest = Open(state);
                AnswerRequest(state, registry, "pay");
                Assert.That(state.FindPlayer(recipient).Resources.OriginiumShard, Is.EqualTo(2));
                Assert.That(state.FindPlayer(recipient).Score, Is.EqualTo(2));
                Assert.That(Open(state).AnsweringPlayerId, Is.EqualTo(recipient));
                Assert.That(state.EffectRuntime.RuleEvents.Count(ruleEvent => ruleEvent.EventType == EnterpriseEffectExecutor.LevelChangedEvent), Is.EqualTo(index + 1));
                state = Restore(state, registry);
                Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(paymentRequest.GetStableInteractionId(), recipient, NormalizedValue.CreateString("pay"), out _), Is.False);
                AnswerRequest(state, registry, "accept");
            }
            state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            var parent = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == root);
            Assert.That(parent.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(parent.PlayerId, Is.EqualTo(initiatingPlayer));
            Assert.That(Value(parent.NormalizedResult, "completedPlayerIds").Items.Select(value => (int)value.IntegerValue), Is.EqualTo(order));
            Assert.That(state.EffectRuntime.RuleEvents.Single(ruleEvent => ruleEvent.SourceEffectId == root && ruleEvent.EventType == "EffectCompleted").PlayerId, Is.EqualTo(initiatingPlayer));
            Assert.That(state.Players.All(player => player.Score == 2 && player.Resources.OriginiumShard == 2), Is.True);
            Assert.That(state.EffectRuntime.RuleEvents.Count(ruleEvent => ruleEvent.EventType == EnterpriseEffectExecutor.LevelChangedEvent), Is.EqualTo(playerCount));
        }

        [TestCase("Effect.UpgradeEnterprise({apiVersion=1,player='p2',enterpriseItemId='board',levels=1})")]
        [TestCase("Effect.ChangeEnterpriseLevel({apiVersion=1,player='p2',enterpriseItemId='board',levelDelta=-1,resolveArrivalReward=true})")]
        [TestCase("Effect.UpgradeEnterprise({apiVersion=1,player='p999',enterpriseItemId='board',levels=1})")]
        [TestCase("Flow.UpgradeEnterpriseForAllPlayers({apiVersion=1,player='p2',enterpriseItemId='board',levels=1})")]
        [TestCase("Flow.UpgradeEnterpriseForAllPlayers({apiVersion=1,player=ctx.playerId,enterpriseItemId='board',levels=2})")]
        [TestCase("Effect.UpgradeEnterprise({apiVersion=1,player=ctx.playerId,enterpriseItemId='board',levels=1,authorizationEffectId='forged'})")]
        [TestCase("Flow.UpgradeEnterpriseForAllPlayers({apiVersion=1,player=ctx.playerId,enterpriseItemId='board',levels=1,authorizedPlayerIds={'p1','p2'}})")]
        public void LuaRejectsUnapprovedCrossPlayerLevelChangesAndForgedAuthorization(string expression)
        {
            string source = "return function(ctx) return { " + expression + " } end";
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("test.permission", "test.permission", "test.permission", "1.0.0", source, LuaContentHasher.ComputeSha256(source)),
                new LuaInvocationContext("test.event", "test.event", "p1", 0, "1.0.0", 3, 0, 0));
            bool compiled = invocation.IsSuccess && new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out _, out _);
            Assert.That(compiled, Is.False);
        }
        [Test] public void AllPlayerUpgradeAtCapHasNoRewardAndDoesNotRepeatAfterRecovery()
        {
            var state = State(5, opponentBusinessLevel: 5); var registry = Registry();
            string root = Run(state, Spec(EnterpriseEffectExecutor.UpgradeAllPlayersTypeId, 2,
                "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, Business)), "levels", NormalizedValue.CreateInteger(1)), registry).EffectId;
            var parent = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == root);
            Assert.That(parent.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Flag(parent.NormalizedResult, "changed"), Is.False);
            Assert.That(Value(parent.NormalizedResult, "completedPlayerIds").Items.Select(value => (int)value.IntegerValue), Is.EqualTo(new[] { 2, 1 }));
            Assert.That(state.EffectRuntime.RuleEvents.Any(ruleEvent => ruleEvent.EventType == EnterpriseEffectExecutor.LevelChangedEvent), Is.False);
            int eventCount = state.EffectRuntime.RuleEvents.Count; state = Restore(state, registry);
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.EffectRuntime.RuleEvents.Count, Is.EqualTo(eventCount)); Assert.That(Level(state), Is.EqualTo(5)); Assert.That(Level(state, playerId: 2), Is.EqualTo(5));
        }
        [TestCase("completedPlayerIds")][TestCase("initiatingPlayerId")][TestCase("changed")]
        [TestCase("child_arrivalEventIds")][TestCase("child_changed")]
        public void RecoveryRejectsForgedCompletedAllPlayerUpgradeResults(string change)
        {
            var state = State(5, opponentBusinessLevel: 5); var registry = Registry();
            Run(state, Spec(EnterpriseEffectExecutor.UpgradeAllPlayersTypeId, 1,
                "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, Business)), "levels", NormalizedValue.CreateInteger(1)), registry);
            var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var nodes = (JArray)json["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"];
            var parent = nodes.Single(node => (string)node["EffectTypeId"] == EnterpriseEffectExecutor.UpgradeAllPlayersTypeId);
            var target = change.StartsWith("child_") ? nodes.First(node => (string)node["ParentEffectId"] == (string)parent["EffectId"]) : parent;
            string key = change.Replace("child_", "");
            ((JArray)target["NormalizedResult"]["Properties"]).Single(entry => (string)entry["Name"] == key).Remove();
            Assert.That(HostRecoveryService.RecoverJson(json.ToString(), registry).Succeeded, Is.False);
        }
        [TestCase(1)][TestCase(-1)] public void DomainStillRejectsUnauthorisedCrossPlayerUpgradeAndDowngrade(int delta)
        {
            var state = State(1, opponentBusinessLevel: 1); var registry = Registry();
            var spec = delta == 1 ? EnterpriseEffectExecutor.UpgradeSpec(2, ItemId(state, Business), 1) : Spec(EnterpriseEffectExecutor.ChangeLevelTypeId, 2,
                "enterpriseItemId", NormalizedValue.CreateString(ItemId(state, Business)), "levelDelta", NormalizedValue.CreateInteger(-1), "resolveArrivalReward", NormalizedValue.CreateBoolean(true));
            spec.PlayerId = 1; var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True); Assert.That(state.EffectRuntime.LastFaultMessage, Does.Contain("enterprise_execution_permission_denied"));
            Assert.That(Level(state, playerId: 2), Is.EqualTo(1)); Assert.That(state.EffectRuntime.RuleEvents.Any(ruleEvent => ruleEvent.EventType == EnterpriseEffectExecutor.LevelChangedEvent), Is.False);
        }
        [TestCase("initiatingPlayerId")][TestCase("authorizedPlayerIds")][TestCase("nextIndex")][TestCase("completedCount")]
        [TestCase("authorizedEnterpriseDefinitionId")]
        [TestCase("authorizationEffectId")][TestCase("parent")][TestCase("recipient")][TestCase("levels")][TestCase("source")]
        public void RecoveryRejectsMissingOrForgedAllPlayerUpgradeAuthority(string change)
        {
            var state = SharedUpgradeState(3); var registry = Registry(AllPlayerChoiceDefinitions()); Run(state, SharedUpgradeSpec(state, 2), registry);
            var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var nodes = (JArray)json["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"];
            var parent = nodes.Single(node => (string)node["EffectTypeId"] == EnterpriseEffectExecutor.UpgradeAllPlayersTypeId);
            var child = nodes.Single(node => (string)node["ParentEffectId"] == (string)parent["EffectId"]);
            if (new[] { "initiatingPlayerId", "authorizedPlayerIds", "nextIndex", "completedCount", "authorizedEnterpriseDefinitionId" }.Contains(change))
                ((JArray)parent["ContinuationData"]["Properties"]).Single(entry => (string)entry["Name"] == change).Remove();
            else if (change == "authorizationEffectId") ((JArray)child["NormalizedArguments"]["Properties"]).Single(entry => (string)entry["Name"] == change).Remove();
            else if (change == "parent") child["ParentEffectId"] = "forged";
            else if (change == "recipient") child["PlayerId"] = 1;
            else if (change == "source") child["SourceId"] = "forged";
            else ((JArray)child["NormalizedArguments"]["Properties"]).Single(entry => (string)entry["Name"] == "levels")["Value"]["IntegerValue"] = 2;
            Assert.That(HostRecoveryService.RecoverJson(json.ToString(), registry).Succeeded, Is.False);
        }
    }
}
