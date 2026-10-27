using System;
using System.Collections.Generic;
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
    // 隔离通用候选集合合同，实际使用正式Host/事务/恢复器；不计正常页面或Steam验收。
    public sealed class EnterpriseSpecialCandidateScopeR17Tests
    {
        private static readonly string[] Boards = { "test.r17.alpha", "test.r17.beta" };
        private EffectRegistry registry;
        public GameState ObservedState { get; private set; }
        private static string Special(int board, int level) => Boards[board] + ".special_" + level;
        private static string Item(GameState state, int board) => state.ItemZones.Items.Single(item => item.DefinitionId == Boards[board]).ItemId;
        private static string Candidate(GameState state, int board, int level) => StableIdFactory.Create("candidate", Item(state, board), Special(board, level));
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.LastOrDefault(request => request.Status == "open");

        [SetUp] public void BeginCase() { ObservedState = null; }
        private GameState State(int level = 0, bool interactive = false)
        {
            registry = new EffectRegistry { ContentPackHash = "qy007.r17.isolated.candidate.scope" };
            var definitions = new EnterpriseDefinitions();
            foreach (int board in Enumerable.Range(0, 2))
            {
                int number = board;
                definitions.Register(new EnterpriseDefinition { DefinitionId = Boards[board], RuntimeId = Boards[board],
                    Levels = Enumerable.Range(0, 6).Select(value => new EnterpriseLevelDefinition { Level = value,
                        Kind = new[] { 1, 2, 4 }.Contains(value) ? "special" : "empty", AbilityId = new[] { 1, 2, 4 }.Contains(value) ? Special(number, value) : "" }).ToList() });
                foreach (int value in new[] { 1, 2, 4 })
                {
                    definitions.BindAbility(Special(board, value), context => interactive
                        ? new[] { EffectSpec.Condition(new[] { EffectSpec.Create(LuaDomainEffectTypeIds.Choice,
                            Object("player", NormalizedValue.CreateString("1"), "options", Strings(new[] { "continue" })), 1) },
                            new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) }) }
                        : new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) });
                }
            }
            definitions.Freeze(); EnterpriseEffectExecutor.Register(registry, definitions);
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var source = new GameState { GameId = "qy007.r17.scope", MapId = map.MapId, ContentPackHash = registry.ContentPackHash,
                Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1,
                RuleConfiguration = new YC.Domain.Rules.GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = registry.ContentPackHash },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "supply" } };
            recipe.AddRange(Boards.Select(id => new ItemRecipeEntry { EntryId = id, DefinitionId = id, TypeId = "core.board_card", Quantity = 1, InitialZoneRole = "enterprises" }));
            var declarations = Boards.Select(id => new ItemCardZoneDeclaration { DefinitionId = id, CardZones = Enumerable.Range(1, 2).Select(player => new CardZoneDefinition {
                LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track,
                EntryPolicyId = "core.owned_player_marker", ExitPolicyId = "core.explicit_transfer", VisibilityPolicyId = ItemZoneVisibility.Public,
                CarrierExitPolicyId = "core.preserve_contents", DisplayAnchorId = "cooperation" }).ToList() }).ToArray();
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, recipe, declarations);
            foreach (int board in Enumerable.Range(0, 2)) foreach (int player in new[] { 1, 2 })
            {
                var zone = state.ItemZones.Zones.Single(value => value.ParentId == Item(state, board) && value.LocalZoneId == "cooperation_" + player);
                var marker = state.ItemZones.Items.First(value => value.TypeId == "core.player_marker" && value.OwnerSubject.PlayerId == player && value.ZoneId == FindZone(state, "supply", player).ZoneId);
                marker.ZoneId = zone.ZoneId; marker.SlotId = "level_" + level;
                state.ItemZones.Orders.Single(value => value.ZoneId == zone.ZoneId).ItemIds.Add(marker.ItemId);
            }
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
            return ObservedState = state;
        }
        private static EffectSpec Spec(NormalizedValue candidates = null, string enterprise = null, string special = null)
        {
            var args = Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "unlockedByPlayer", NormalizedValue.CreateString("1"));
            if (candidates != null) args.Properties.Add(new NormalizedValueEntry { Name = "candidateIds", Value = candidates });
            if (enterprise != null) args.Properties.Add(new NormalizedValueEntry { Name = "enterpriseItemIds", Value = Strings(new[] { enterprise }) });
            if (special != null) args.Properties.Add(new NormalizedValueEntry { Name = "specialId", Value = NormalizedValue.CreateString(special) });
            args.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            return EffectSpec.Create(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial, args, 1);
        }
        private EffectNodeRuntimeState Run(GameState state, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); return engine.GetNode(id);
        }
        private void Answer(GameState state, string id)
        {
            var request = Open(state); Assert.That(request, Is.Not.Null);
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, NormalizedValue.CreateString(id), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private GameState Restore(GameState state)
        {
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); return ObservedState = result.State;
        }
        internal static void AssertNativeMetadata(InteractionRequest request)
        {
            Assert.That(request.InteractionTypeId, Is.EqualTo("enterprise.special"));
            foreach (string field in new[] { "candidateEnterpriseItemIds", "candidateSpecialIds", "candidateLevelCeilings", "candidateCurrentLevels", "candidateDisplayNameKeys" })
            { Assert.That(Value(request.PromptParameters, field).Kind, Is.EqualTo(NormalizedValueKind.Array)); Assert.That(Value(request.PromptParameters, field).Items.Count, Is.EqualTo(request.CandidateIds.Count)); }
            for (int index = 0; index < request.CandidateIds.Count; index++)
                Assert.That(request.CandidateIds[index], Is.EqualTo(StableIdFactory.Create("candidate", Value(request.PromptParameters, "candidateEnterpriseItemIds").Items[index].StringValue,
                    Value(request.PromptParameters, "candidateSpecialIds").Items[index].StringValue)));
        }

        [Test]
        public void ExplicitCollectionContainsLockedSpecialsAndKeepsOtherLevelsAndBoards()
        {
            var state = State(); var ids = new[] { Candidate(state, 0, 2), Candidate(state, 1, 1), Candidate(state, 1, 2) };
            Run(state, Spec(Strings(ids))); AssertNativeMetadata(Open(state));
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(ids));
            Assert.That(Open(state).CandidateIds, Does.Not.Contain(Candidate(state, 0, 1)));
            state = Restore(state); Answer(state, Candidate(state, 0, 2)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.FindPlayer(2).Score, Is.Zero); Assert.That(Open(state), Is.Null);
        }

        [TestCase("absent")] [TestCase("empty")] [TestCase("unknown")]
        public void AbsentUsesUnlockedDefaultsAndExplicitEmptyOrUnknownSetStaysEmpty(string mode)
        {
            var state = State(1); var node = Run(state, Spec(mode == "absent" ? null : Strings(mode == "empty" ? new string[0] : new[] { "unknown.candidate" })));
            if (mode == "absent")
            { AssertNativeMetadata(Open(state)); Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { Candidate(state, 0, 1), Candidate(state, 1, 1) })); }
            else { Assert.That(Open(state), Is.Null); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo("no_candidates")); }
            Assert.That(state.FindPlayer(1).Score, Is.Zero); state = Restore(state);
        }

        [TestCase("scalar")] [TestCase("object")] [TestCase("null")] [TestCase("blank")] [TestCase("number")] [TestCase("duplicate")]
        public void InvalidCandidateCollectionFailsBeforeSelectionOrExecution(string damage)
        {
            var state = State(); string id = Candidate(state, 0, 1);
            var bad = damage == "scalar" ? NormalizedValue.CreateString(id) : damage == "object" ? Object() : damage == "null" ? NormalizedValue.CreateNull() :
                damage == "blank" ? Strings(new[] { " " }) : damage == "number" ? NormalizedValue.CreateArray(new[] { NormalizedValue.CreateInteger(1) }) : Strings(new[] { id, id });
            var node = Run(state, Spec(bad)); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(node.FailureReason, Is.EqualTo("invalid_enterprise_target")); Assert.That(Open(state), Is.Null); Assert.That(state.FindPlayer(1).Score, Is.Zero);
        }

        [Test]
        public void ExplicitTargetMustBelongToItsSuppliedCollectionEvenWhenNothingElseRemains()
        {
            var state = State(); var node = Run(state, Spec(Strings(new[] { Candidate(state, 1, 1) }), Item(state, 0), Special(0, 1)));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.FailureReason, Is.EqualTo("enterprise_special_not_in_scope"));
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(Open(state), Is.Null);
        }

        [Test]
        public void OrdinaryIconStyleCallStillRequiresUnlockedLevel()
        {
            var state = State(); var node = Run(state, Spec()); Assert.That(Open(state), Is.Null);
            Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo("no_candidates")); Assert.That(state.FindPlayer(1).Score, Is.Zero);
        }

        [Test]
        public void ExplicitSingleCandidateUsesTheNativeRequestAndSurvivesNestedResponseRecovery()
        {
            var state = State(interactive: true); var node = Run(state, Spec(Strings(new[] { Candidate(state, 0, 1) })));
            AssertNativeMetadata(Open(state)); Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(1));
            string requestId = Open(state).GetStableInteractionId(); state = Restore(state);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(requestId)); Answer(state, Candidate(state, 0, 1));
            Assert.That(Open(state).CandidateIds, Is.EqualTo(new[] { "continue" })); Assert.That(state.FindPlayer(1).Score, Is.Zero);
            state = Restore(state); Answer(state, "continue"); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            state = Restore(state); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == "EnterpriseSpecialEffectActivated"), Is.EqualTo(1));
        }

        [TestCase("frozen")] [TestCase("request")]
        public void RecoveryRejectsCandidateIdentityOutsideTheSuppliedCollection(string damage)
        {
            var state = State(); var node = Run(state, Spec(Strings(new[] { Candidate(state, 0, 1) })));
            var archive = HostRecoveryService.CreateArchive(state);
            state = ObservedState = archive.Snapshot.State;
            node = state.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId);
            if (damage == "frozen") node.FrozenEnterpriseSpecials[0].Candidate.CandidateId = Candidate(state, 1, 1);
            else Open(state).CandidateIds[0] = Candidate(state, 1, 1);
            Assert.That(EnterpriseSpecialSelectionExecutor.TryValidateCandidateScopes(state, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("enterprise_special_candidate_scope_restore_invalid"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => HostRecoveryService.CreateArchive(state)).Message, Does.Contain(reason));
            var result = HostRecoveryService.Recover(archive, registry);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Does.Contain(reason));
            Assert.That(state.FindPlayer(1).Score, Is.Zero);
        }

        [Test]
        public void LuaFacadePassesTheSameRealCandidateIdsWithoutAnExclusionInterface()
        {
            var state = State(); string id = Candidate(state, 0, 2);
            string code = "return function(ctx) return {Effect.ActivateEnterpriseSpecial({apiVersion=1,player=ctx.playerId,unlockedByPlayer=ctx.playerId,candidateIds={'" + id + "'}})} end";
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 2, "1.0.0");
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("r17.scope", "r17.scope", "r17.scope", "1.0.0", code, LuaContentHasher.ComputeSha256(code)),
                new LuaInvocationContext("r17.scope.event", "test", "p1", 0, "1.0.0", 2, 0, 0, snapshot));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects.Count, Is.EqualTo(1));
            var raw = result.Effects.Single(); Assert.That(Value(raw.RawArguments, "candidateIds").Items.Single().StringValue, Is.EqualTo(id));
            Run(state, EffectSpec.Create(raw.EffectTypeId, raw.RawArguments, 1)); AssertNativeMetadata(Open(state)); Answer(state, id);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }
    }
}
