using NUnit.Framework;
using YC.Domain.CityStyles;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class SpecialActionDomainTests
    {
        [Test]
        public void MilitaryGroup_AllMarkersMoveAndResetTogether()
        {
            var state = CreateStateWithUnlockedMarker(SpecialActionDatabase.MilitaryIndustrialArea,
                CityStyleDatabase.MilitaryIndustrialArea, CityStyleMarkerAreas.Unused, 1);
            var player = state.Players[0];
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState {
                InfluenceMarkerId = "marker-2", CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                MarkerArea = CityStyleMarkerAreas.Declared,
                UnlockedSpecialActionId = string.Empty,
                RemainingSpecialActionUses = 0 });
            var lifecycle = new SpecialActionLifecycleService();
            lifecycle.MarkActivated(player, SpecialActionDatabase.Get(SpecialActionDatabase.MilitaryIndustrialArea), "marker");
            foreach (var marker in player.DeclaredCityStyles)
            {
                Assert.That(marker.MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Used));
                Assert.That(marker.RemainingSpecialActionUses, Is.Zero);
            }
            lifecycle.CleanupRound(state);
            foreach (var marker in player.DeclaredCityStyles)
            {
                var isActionMarker = !string.IsNullOrEmpty(marker.UnlockedSpecialActionId);
                Assert.That(marker.MarkerArea, Is.EqualTo(isActionMarker ? CityStyleMarkerAreas.Unused : CityStyleMarkerAreas.Declared));
                Assert.That(marker.RemainingSpecialActionUses, Is.EqualTo(isActionMarker ? 1 : 0));
            }
        }

        [Test]
        public void Database_DefinesFiveCardActionsAndFrozenCosts()
        {
            Assert.That(SpecialActionDatabase.All.Count, Is.EqualTo(5));

            var composite = SpecialActionDatabase.Get(SpecialActionDatabase.CompositePowerSystem);
            Assert.That(composite.CityStyleId, Is.EqualTo(CityStyleDatabase.CompositePowerSystem));
            Assert.That(composite.Level, Is.EqualTo(1));
            Assert.That(composite.FixedCost.OriginiumShard, Is.EqualTo(1));
            Assert.That(composite.FlexibleOriginiumAndIronCost, Is.EqualTo(3));
            Assert.That(composite.FreeMoveCount, Is.EqualTo(1));

            var industrialHub = SpecialActionDatabase.Get(SpecialActionDatabase.SourceStoneIndustrialHub);
            Assert.That(industrialHub.Level, Is.EqualTo(2));
            Assert.That(industrialHub.FixedCost.GoldVoucher, Is.EqualTo(6));
            Assert.That(industrialHub.ExtraMainActionCount, Is.EqualTo(2));
            Assert.That(industrialHub.LocksCharacterCard, Is.True);

            var efficientMove = SpecialActionDatabase.Get(SpecialActionDatabase.EfficientMobileManagementSystem);
            Assert.That(efficientMove.FixedCost.OriginiumShard, Is.EqualTo(3));
            Assert.That(efficientMove.FreeMoveCount, Is.EqualTo(2));
            Assert.That(efficientMove.LocksCharacterCard, Is.True);

            Assert.That(CityStyleDatabase.Get(CityStyleDatabase.MaterialRelayStation).SpecialActionId, Is.Empty);
        }

        [Test]
        public void LevelOneMarker_MovesToUsedAndResetsAtCleanup()
        {
            var state = CreateStateWithUnlockedMarker(
                SpecialActionDatabase.MilitaryIndustrialArea,
                CityStyleDatabase.MilitaryIndustrialArea,
                CityStyleMarkerAreas.Unused,
                1);
            var player = state.Players[0];
            var definition = SpecialActionDatabase.Get(SpecialActionDatabase.MilitaryIndustrialArea);
            var lifecycle = new SpecialActionLifecycleService();

            Assert.That(lifecycle.ValidateAvailable(player, definition, "marker").IsValid, Is.True);
            lifecycle.MarkActivated(player, definition, "marker");

            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Used));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(0));
            player.UsedSpecialActionIdsThisRound.Add(definition.SpecialActionId);

            lifecycle.CleanupRound(state);

            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Unused));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(1));
            Assert.That(player.UsedSpecialActionIdsThisRound, Is.Empty);
        }

        [Test]
        public void LevelTwoMarker_DecrementsOnlyDuringCleanup_AndEventuallyExhausts()
        {
            var state = CreateStateWithUnlockedMarker(
                SpecialActionDatabase.SourceStoneIndustrialHub,
                CityStyleDatabase.SourceStoneIndustrialHub,
                CityStyleMarkerAreas.UsesTwo,
                2);
            var player = state.Players[0];
            var definition = SpecialActionDatabase.Get(SpecialActionDatabase.SourceStoneIndustrialHub);
            var lifecycle = new SpecialActionLifecycleService();

            lifecycle.MarkActivated(player, definition, "marker");
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(SpecialActionMarkerAreas.UsedFromTwo));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(2));

            lifecycle.CleanupRound(state);
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesOne));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(1));

            lifecycle.MarkActivated(player, definition, "marker");
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(SpecialActionMarkerAreas.UsedFromOne));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(1));

            lifecycle.CleanupRound(state);
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesZero));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(0));
            Assert.That(lifecycle.ValidateAvailable(player, definition, "marker").IsValid, Is.False);
        }

        [Test]
        public void Cleanup_DoesNotChangeOrdinaryLaterDeclarationMarker()
        {
            var state = CreateStateWithUnlockedMarker(
                SpecialActionDatabase.MilitaryIndustrialArea,
                CityStyleDatabase.MilitaryIndustrialArea,
                CityStyleMarkerAreas.Unused,
                1);
            state.Players[0].DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "later-marker",
                CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                MarkerArea = CityStyleMarkerAreas.Declared,
                UnlockedSpecialActionId = string.Empty,
                RemainingSpecialActionUses = 0
            });

            new SpecialActionLifecycleService().CleanupRound(state);

            Assert.That(state.Players[0].DeclaredCityStyles[1].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Declared));
            Assert.That(state.Players[0].DeclaredCityStyles[1].RemainingSpecialActionUses, Is.EqualTo(0));
        }

        private static GameState CreateStateWithUnlockedMarker(
            string specialActionId,
            string cityStyleId,
            string markerArea,
            int remainingUses)
        {
            var player = new PlayerState { PlayerId = 1 };
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "marker",
                CityStyleId = cityStyleId,
                MarkerArea = markerArea,
                UnlockedSpecialActionId = specialActionId,
                RemainingSpecialActionUses = remainingUses
            });

            var state = new GameState();
            state.Players.Add(player);
            return state;
        }
    }
}
