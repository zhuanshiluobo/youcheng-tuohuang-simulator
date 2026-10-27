using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 正式卡牌/正式Lua的隔离入口；只核新原生请求链及本轮适配的共享case。
    public sealed class CharacterNativeSpecialSelectionR17Tests
    {
        private InfectedCharacterEffectsTests fixture;
        private SceneRuntimeCatalogScope nativeCatalogScope;
        public GameState ObservedState { get; private set; }
        private EffectRegistry Registry => (EffectRegistry)typeof(InfectedCharacterEffectsTests).GetField("registry", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture);
        [OneTimeSetUp]
        public void LoadFormalContentOnce()
        {
            // 原生串行集成可能已装配兼容目录；沿既有隔离域装载并在结束恢复，不跳生产防覆盖门禁。
            nativeCatalogScope = new SceneRuntimeCatalogScope();
            fixture = new InfectedCharacterEffectsTests(); fixture.LoadExplicitRealDefinitionsFromFormalPack();
            var core = (ExternalContentPack)typeof(InfectedCharacterEffectsTests).GetField("core", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture);
            if (!YC.Domain.Cards.CharacterCardDatabase.IsInitialized) YC.Domain.Cards.CharacterCardDatabase.Initialize(core.CreateCharacters());
            YC.Domain.Facilities.FacilityCardDatabase.InitializeExternal(core.CreateFacilities());
            YC.Domain.Cards.EventCardDatabase.InitializeExternal(core.CreateEvents());
            YC.Domain.CityStyles.CityStyleDatabase.InitializeExternal(core.CreateCityStyles());
            YC.Domain.SpecialActions.SpecialActionDatabase.InitializeExternal(core.CreateSpecialActions());
        }
        [OneTimeTearDown] public void RestoreNativeCatalogs()
        { nativeCatalogScope?.Dispose(); nativeCatalogScope = null; }
        [SetUp] public void BeginCase() { ObservedState = null; }
        private object Call(string name, params object[] values)
        {
            try { return typeof(InfectedCharacterEffectsTests).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic).Invoke(fixture, values); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private GameState State() => ObservedState = (GameState)Call("State", 3);
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.LastOrDefault(value => value.Status == "open");
        private void Answer(GameState state, string id) => Call("Answer", state, new[] { id });
        private GameState Restore(GameState state) => ObservedState = (GameState)Call("Restore", state);
        private void SetAllLevels(GameState state, int player, int level)
        {
            foreach (var marker in state.ItemZones.Items.Where(value => value.OwnerSubject.PlayerId == player &&
                state.ItemZones.Zones.Any(zone => zone.ZoneId == value.ZoneId && zone.LocalZoneId == "cooperation_" + player))) marker.SlotId = "level_" + level;
        }
        private EnterpriseSpecialCandidate[] Candidates(GameState state, int player, int delta) => new EnterpriseStateQuery(Registry.EnterpriseDefinitions).GetSpecialCandidates(state, 1, player, delta, null).ToArray();

        [Test]
        public void SwireTextRangeOverridesUnlocksAndTwoNativeSelectionsStayDifferentAcrossRecovery()
        {
            var state = State(); SetAllLevels(state, 1, 0);
            Assert.That(Candidates(state, 1, 0), Is.Empty);
            var expected = Candidates(state, 1, 5).Where(value => Registry.EnterpriseDefinitions.FindEnterprise(state.ItemZones.Items.Single(item => item.ItemId == value.EnterpriseItemId).DefinitionId)
                .Levels.Single(level => level.AbilityId == value.SpecialId).Level <= 2).ToArray();
            Assert.That(expected.Length, Is.GreaterThanOrEqualTo(2));
            int gold = state.FindPlayer(1).Resources.GoldVoucher, shards = state.FindPlayer(1).Resources.OriginiumShard;
            Call("StartCard", state, "swire", "strategy");
            EnterpriseSpecialCandidateScopeR17Tests.AssertNativeMetadata(Open(state));
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(expected.Select(value => value.CandidateId)));
            string firstRequest = Open(state).GetStableInteractionId(); state = Restore(state);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(firstRequest));
            var first = expected.Single(value => value.SpecialId == "enterprise.fussen_bank.level_1.special");
            Answer(state, first.CandidateId);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 8));
            EnterpriseSpecialCandidateScopeR17Tests.AssertNativeMetadata(Open(state));
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(expected.Where(value => value.SpecialId != first.SpecialId).Select(value => value.CandidateId)));
            string secondRequest = Open(state).GetStableInteractionId(); state = Restore(state);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(secondRequest));
            var second = expected.Single(value => value.SpecialId == "enterprise.raythean.level_1.special");
            Answer(state, second.CandidateId); Call("Drain", state);
            Assert.That(Open(state), Is.Null); Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(shards + 3));
            var events = state.EffectRuntime.RuleEvents.Where(value => value.EventType == "EnterpriseSpecialEffectActivated").ToArray();
            Assert.That(events.Length, Is.EqualTo(2)); Assert.That(events.Select(value => Text(value.Payload, "specialId")).Distinct().Count(), Is.EqualTo(2));
            Assert.That(events.All(value => Number(value.Payload, "executingPlayerId") == 1 && Number(value.Payload, "beneficiaryPlayerId") == 1), Is.True);
            Assert.That(Candidates(state, 1, 0), Is.Empty, "文字触发未解锁特效不能偷偷升级合作轨道。");
            state = Restore(state); Assert.That(new EffectTreeExecutor(state, Registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == "EnterpriseSpecialEffectActivated"), Is.EqualTo(2));
        }

        [Test]
        public void JessicaKeepsOpponentUnlocksAndAtLeastTenRealCandidatesUseNativeMetadata()
        {
            var state = State(); SetAllLevels(state, 1, 0); SetAllLevels(state, 2, 5);
            var expected = Candidates(state, 2, 0); Assert.That(expected.Length, Is.GreaterThanOrEqualTo(10));
            Assert.That(Candidates(state, 1, 0), Is.Empty);
            int gold = state.FindPlayer(1).Resources.GoldVoucher, otherGold = state.FindPlayer(2).Resources.GoldVoucher;
            Call("StartCard", state, "jessica_the_liberated", "strategy");
            Assert.That(Open(state).CandidateIds, Does.Contain("p2")); Assert.That(Open(state).AnsweringPlayerId, Is.EqualTo(1));
            state = Restore(state); Answer(state, "p2");
            EnterpriseSpecialCandidateScopeR17Tests.AssertNativeMetadata(Open(state));
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(expected.Select(value => value.CandidateId)));
            Assert.That(Value(Open(state).PromptParameters, "candidateCurrentLevels").Items.All(value => value.IntegerValue == 5), Is.True);
            state = Restore(state); Answer(state, expected.Single(value => value.SpecialId == "enterprise.fussen_bank.level_1.special").CandidateId);
            Call("Drain", state);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 8)); Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(otherGold));
            var activated = state.EffectRuntime.RuleEvents.Single(value => value.EventType == "EnterpriseSpecialEffectActivated");
            Assert.That(Number(activated.Payload, "unlockedByPlayerId"), Is.EqualTo(2));
            Assert.That(Number(activated.Payload, "executingPlayerId"), Is.EqualTo(1)); Assert.That(Number(activated.Payload, "beneficiaryPlayerId"), Is.EqualTo(1));
            state = Restore(state); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 8));
        }

        [TestCase("strategy")] [TestCase("jessica")] [TestCase("return")] [TestCase("cleanup")] [TestCase("empty")] [TestCase("detail")]
        public void AdaptedExistingContractsKeepTheirOriginalRuleAssertions(string name)
        {
            switch (name)
            {
                case "strategy": fixture.SwireSelectsTwoDifferentSpecialsAndNeverRepeatsFirst(); break;
                case "jessica": fixture.JessicaBorrowsOpponentUnlockedSpecialAndKeepsBenefit(); break;
                case "return": fixture.MarkersPreserveConfiguredSourceAndReturnToOriginalSupply("swire"); break;
                case "cleanup": fixture.MarkersRefreshCandidatesAndCleanupConsumesAtMostOnePerRound("swire"); break;
                case "empty": fixture.PlayerCandidatesIncludeEmptyOpponentSuppliesAndNeverRequestOpponentConsent(); break;
                case "detail": fixture.RealActiveEffectProjectsFullTextForOwnerOpponentAndRecovery("swire"); break;
            }
        }

        [TestCase("p2")] [TestCase("p3")]
        public void AdaptedReceiptContinuationKeepsTheOriginalOwner(string owner)
        {
            var receipt = new CharacterTokenContractConsumptionTests(); receipt.BeginEvidence();
            try { receipt.SwireReturnReceiptKeepsTheSelectedOriginalOwnerAsTheUpgradeBeneficiary(owner); }
            finally { receipt.EndEvidence(); }
        }
    }
}
