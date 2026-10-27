using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离规则检查：复用既有真实内容装配，绝不计作正常页面、Steam或整局验收。
    public sealed class QY007SwireChoiceR16Tests
    {
        private InfectedCharacterEffectsTests fixture;
        public GameState ObservedState { get; private set; }
        private EffectRegistry Registry => (EffectRegistry)typeof(InfectedCharacterEffectsTests)
            .GetField("registry", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture);

        [OneTimeSetUp]
        public void LoadFormalContent()
        {
            fixture = new InfectedCharacterEffectsTests();
            fixture.LoadExplicitRealDefinitionsFromFormalPack();
            var core = (YC.Infrastructure.Lua.ExternalContentPack)typeof(InfectedCharacterEffectsTests)
                .GetField("core", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture);
            if (!YC.Domain.Cards.CharacterCardDatabase.IsInitialized) YC.Domain.Cards.CharacterCardDatabase.Initialize(core.CreateCharacters());
            YC.Domain.Facilities.FacilityCardDatabase.InitializeExternal(core.CreateFacilities());
            YC.Domain.Cards.EventCardDatabase.InitializeExternal(core.CreateEvents());
            YC.Domain.CityStyles.CityStyleDatabase.InitializeExternal(core.CreateCityStyles());
            YC.Domain.SpecialActions.SpecialActionDatabase.InitializeExternal(core.CreateSpecialActions());
            ObservedState = null;
        }

        [SetUp] public void BeginCase() { ObservedState = null; }

        private object Call(string name, params object[] args)
        {
            var method = typeof(InfectedCharacterEffectsTests).GetMethod(name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, name);
            try { return method.Invoke(method.IsStatic ? null : fixture, args); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        private GameState State(int players = 3) => ObservedState = (GameState)Call("State", players);
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
        private static ItemInstanceState Card(GameState state) => state.ItemZones.Items.Single(i => i.DefinitionId == "infected.character.swire");
        private List<ItemInstanceState> Markers(GameState state) => (List<ItemInstanceState>)Call("StoredMarkers", state, "swire");
        private EffectTreeExecutor Engine(GameState state) => new EffectTreeExecutor(state, Registry);
        private void Run(GameState state)
        {
            Assert.That(Engine(state).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
        }
        private void Submit(GameState state, NormalizedValue answer, bool run = true)
        {
            var request = Open(state); Assert.That(request, Is.Not.Null);
            Assert.That(Engine(state).TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId,
                request.StateRevision, answer, out string reason), Is.True, reason);
            if (run) Run(state);
        }
        private void Select(GameState state, params string[] ids) => Submit(state, Strings(ids));
        private GameState Restore(GameState state)
        {
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), Registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            return ObservedState = result.State;
        }
        private void Start(GameState state) => Call("StartCard", state, "swire", "tactic");
        private void Cleanup(GameState state) { Call("Cleanup", state); Call("InvokeCleanup", state); }
        private void Drain(GameState state) => Call("Drain", state);
        private void EmptySupply(GameState state, int player)
        {
            foreach (var item in state.ItemZones.Items.Where(i => i.ZoneId == FindZone(state, "supply", player).ZoneId).ToList())
                Call("SetFixturePosition", state, item, FindZone(state, "retired").ZoneId);
        }

        // 只在测试内逐步调用真实Host推进器，保存真实事务完成但续接尚未消费的中间档。
        private void AdvanceUntil(GameState state, Func<bool> reached)
        {
            var engine = Engine(state);
            for (int step = 0; step < 500 && !reached(); step++)
                Assert.That(engine.Advance(), Is.True, "未到达指定真实Host恢复检查点。");
            Assert.That(reached(), Is.True);
        }

        [TestCase(0, false)] [TestCase(1, false)] [TestCase(2, false)]
        [TestCase(1, true)] [TestCase(2, true)]
        public void TacticChoosesZeroOneOrTwoPlayersAndKeepsEmptyOpponents(int count, bool emptySecondPlayer)
        {
            var state = State();
            if (emptySecondPlayer) EmptySupply(state, 2);
            Start(state);
            var request = Open(state);
            Assert.That(request.CandidateIds, Is.EquivalentTo(new[] { "p2", "p3" }));
            Assert.That(request.MinSelections, Is.Zero); Assert.That(request.MaxSelections, Is.EqualTo(2));
            Assert.That(request.AnsweringPlayerId, Is.EqualTo(1));
            Select(state, request.CandidateIds.Take(count).ToArray());
            Assert.That(Open(state), Is.Null, "选对手后不得再弹标记或补选对手页。");
            int expected = count - (emptySecondPlayer && count > 0 ? 1 : 0);
            Assert.That(Markers(state).Count, Is.EqualTo(expected));
            Assert.That(Markers(state).Select(i => i.OwnerSubject.PlayerId).Distinct().Count(), Is.EqualTo(expected));
            if (expected == 0) Assert.That(Card(state).ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            Assert.That(state.EffectRuntime.InteractionRequests.All(r => r.AnsweringPlayerId == 1), Is.True);
        }

        [Test]
        public void BooleanCancellationStoresNothingAndUsesTheExistingEmptyCardExit()
        {
            var state = State(); Start(state); Submit(state, NormalizedValue.CreateBoolean(false));
            Assert.That(Markers(state), Is.Empty); Assert.That(Open(state), Is.Null);
            Assert.That(Card(state).ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
        }

        [TestCase("duplicate")] [TestCase("self")] [TestCase("foreign")] [TestCase("too_many")]
        public void HostRejectsInvalidOpponentAnswersWithoutMovingAnything(string invalid)
        {
            var state = State(); Start(state);
            var request = Open(state); int revision = state.EffectRuntime.StateRevision;
            var ids = invalid == "duplicate" ? new[] { "p2", "p2" } : invalid == "self" ? new[] { "p1" } :
                invalid == "foreign" ? new[] { "p4" } : new[] { "p1", "p2", "p3" };
            Assert.That(Engine(state).TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                Strings(ids), out _), Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(Markers(state), Is.Empty); Assert.That(Open(state), Is.SameAs(request));
        }

        [Test]
        public void OneOpponentStillAllowsZeroOrOneAndRetainsTypedPlayerIds()
        {
            var state = State(2); Start(state);
            Assert.That(Open(state).CandidateIds, Is.EqualTo(new[] { "p2" }));
            Assert.That(Open(state).MinSelections, Is.Zero); Assert.That(Open(state).MaxSelections, Is.EqualTo(1));
            Select(state, "p2"); Assert.That(Markers(state).Single().OwnerSubject.PlayerId, Is.EqualTo(2));
            Assert.That(Open(state), Is.Null);
        }

        [Test]
        public void SelectionAndFirstStoredMarkerRecoverWithoutRepeatedStorage()
        {
            var state = State(); Start(state); string requestId = Open(state).GetStableInteractionId();
            state = Restore(state); Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(requestId));
            Submit(state, Strings(new[] { "p2", "p3" }), false);
            AdvanceUntil(state, () => Markers(state).Count == 1);
            string first = Markers(state).Single().ItemId;
            state = Restore(state); Run(state);
            Assert.That(Markers(state).Count, Is.EqualTo(2));
            Assert.That(Markers(state).Count(i => i.ItemId == first), Is.EqualTo(1));
            Assert.That(Markers(state).Select(i => i.OwnerSubject.PlayerId), Is.EquivalentTo(new[] { 2, 3 }));
            state = Restore(state); Run(state); Assert.That(Markers(state).Count, Is.EqualTo(2));
        }

        [Test]
        public void SupplyIsRecheckedAfterSelectionWithoutReplacingTheChosenOpponent()
        {
            var state = State(); Start(state);
            Submit(state, Strings(new[] { "p2", "p3" }), false);
            EmptySupply(state, 2); Run(state);
            Assert.That(Markers(state).Select(i => i.OwnerSubject.PlayerId), Is.EqualTo(new[] { 3 }));
            Assert.That(Open(state), Is.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void DifferentOwnersRemainSelectableAndNativeUpgradeKeepsTheChosenBeneficiary(bool selfAtLimit)
        {
            var state = State(); Start(state); Select(state, "p2", "p3");
            var marker = Markers(state).Single(i => i.OwnerSubject.PlayerId == 3);
            string markerId = marker.ItemId;
            var board = state.ItemZones.Items.Single(i => i.DefinitionId == "enterprise.blacksteel");
            var levels = new EnterpriseStateQuery(Registry.EnterpriseDefinitions);
            if (selfAtLimit)
            {
                string track = state.ItemZones.Zones.Single(z => z.ParentId == board.ItemId && z.LocalZoneId == "cooperation_1").ZoneId;
                state.ItemZones.Items.Single(i => i.ZoneId == track).SlotId = "level_5";
            }
            int ownBefore = levels.GetLevel(state, 1, board.ItemId), otherBefore = levels.GetLevel(state, 3, board.ItemId);
            int untouchedBefore = levels.GetLevel(state, 2, board.ItemId), gold = state.FindPlayer(1).Resources.GoldVoucher;
            Cleanup(state);
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(Markers(state).Select(i => i.ItemId)));
            string markerRequest = Open(state).GetStableInteractionId();
            state = Restore(state); Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(markerRequest));
            Submit(state, Strings(new[] { markerId }), false);
            AdvanceUntil(state, () => state.ItemZones.Items.Single(i => i.ItemId == markerId).ZoneId == FindZone(state, "supply", 3).ZoneId);
            state = Restore(state); Run(state);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 6));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("enterprise.upgrade"));
            Assert.That(Open(state).CandidateIds, Does.Contain(board.ItemId), "本人满级不能剔除对原主仍有效的企业。");
            string enterpriseRequest = Open(state).GetStableInteractionId();
            state = Restore(state); Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(enterpriseRequest));
            Select(state, board.ItemId); Drain(state);
            Assert.That(levels.GetLevel(state, 1, board.ItemId), Is.EqualTo(Math.Min(5, ownBefore + 1)));
            Assert.That(levels.GetLevel(state, 3, board.ItemId), Is.EqualTo(otherBefore + 1));
            Assert.That(levels.GetLevel(state, 2, board.ItemId), Is.EqualTo(untouchedBefore));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 6));
            state = Restore(state); Run(state);
            Assert.That(Markers(state).Count, Is.EqualTo(1));
            Assert.That(levels.GetLevel(state, 3, board.ItemId), Is.EqualTo(otherBefore + 1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 6));
        }

        [Test]
        public void CancellingTheDifferentOwnerChoiceReturnsNoMarkerAndGrantsNothing()
        {
            var state = State(); Start(state); Select(state, "p2", "p3");
            int gold = state.FindPlayer(1).Resources.GoldVoucher; Cleanup(state);
            Submit(state, NormalizedValue.CreateBoolean(false));
            Assert.That(Markers(state).Count, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(Open(state), Is.Null);
        }

        [Test]
        public void AUniqueMarkerReturnsWithoutAnInstanceChoiceAndStillUpgradesItsOwnerAfterCardExit()
        {
            var state = State(); Start(state); Select(state, "p2");
            string marker = Markers(state).Single().ItemId;
            int gold = state.FindPlayer(1).Resources.GoldVoucher; Cleanup(state);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("enterprise.upgrade"));
            Assert.That(Card(state).ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).ZoneId, Is.EqualTo(FindZone(state, "supply", 2).ZoneId));
            var board = state.ItemZones.Items.Single(i => i.DefinitionId == "enterprise.blacksteel");
            state = Restore(state); Select(state, board.ItemId); Drain(state);
            Assert.That(new EnterpriseStateQuery(Registry.EnterpriseDefinitions).GetLevel(state, 2, board.ItemId), Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 6));
            Assert.That(Markers(state), Is.Empty);
        }

        [Test]
        public void StrategyKeepsTheFormalOneToTwoLevelDifferentSpecialRule()
        {
            fixture.SwireSelectsTwoDifferentSpecialsAndNeverRepeatsFirst();
        }
    }
}
