using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;

namespace YC.Tests.EditMode
{
    public sealed class RightCardSmokeStateFactoryTests
    {
        [Test]
        public void CreateInitialState_PreparesActionRoundPlayerAndRightCardSupplies()
        {
            var state = CreateSmokeState(CreateSeats());
            var player = state.FindPlayer(2);

            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(state.Round, Is.EqualTo(1));
            Assert.That(state.ActionRound, Is.EqualTo(1));
            Assert.That(state.StartPlayerId, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(player, Is.Not.Null);
            Assert.That(player.CityLocationId, Is.EqualTo(RightCardSmokeStateFactory.DefaultCityLocationId));
            Assert.That(state.Map.OpenLocationIds, Does.Contain(player.CityLocationId));
            Assert.That(player.Resources.Originium, Is.GreaterThanOrEqualTo(6));
            Assert.That(player.Resources.OriginiumShard, Is.GreaterThanOrEqualTo(6));
            Assert.That(player.Resources.Iron, Is.GreaterThanOrEqualTo(6));
            Assert.That(player.Resources.PureOriginium, Is.GreaterThanOrEqualTo(2));
            Assert.That(player.Resources.GoldVoucher, Is.GreaterThanOrEqualTo(30));
            Assert.That(state.Decks.FacilitySupply, Does.Contain(FacilityCardDatabase.SimpleEngineeringCamp));
            Assert.That(state.Decks.CityStyleSupply, Is.EquivalentTo(CityStyleDatabase.DefaultSupplyIds));
        }

        [Test]
        public void CreateInitialState_RepeatedCallsDoNotShareMutableCollections()
        {
            var seats = CreateSeats();
            var first = CreateSmokeState(seats);
            var second = CreateSmokeState(seats);

            first.Players[0].Name = "mutated";
            first.Players[0].HandCardIds.Add("test-card");
            first.Map.OpenLocationIds.Add("test-location");
            first.Decks.CityStyleSupply.Clear();

            Assert.That(first.Players, Is.Not.SameAs(second.Players));
            Assert.That(first.Players[0].HandCardIds, Is.Not.SameAs(second.Players[0].HandCardIds));
            Assert.That(first.Map.OpenLocationIds, Is.Not.SameAs(second.Map.OpenLocationIds));
            Assert.That(first.Decks.CityStyleSupply, Is.Not.SameAs(second.Decks.CityStyleSupply));
            Assert.That(second.Players[0].Name, Is.EqualTo("Player 1"));
            Assert.That(second.Players[0].HandCardIds, Has.Count.EqualTo(5));
            Assert.That(second.Players[0].HandCardIds, Does.Not.Contain("test-card"));
            Assert.That(second.Map.OpenLocationIds, Does.Not.Contain("test-location"));
            Assert.That(second.Decks.CityStyleSupply, Is.Not.Empty);
            Assert.That(seats[0].PlayerName, Is.EqualTo("Player 1"));
        }

        [Test]
        public void CreateInitialState_DoesNotChangeOrdinaryLaunchFactoryBehavior()
        {
            var seats = CreateSeats();
            CreateSmokeState(seats);

            var ordinary = GameLaunchStateFactory.CreateInitialState(
                LaunchMode.Local,
                2,
                seats,
                StaticMapDefinitions.FourPlayerMapId,
                EventDeckService.DefaultSeed);
            var player = ordinary.FindPlayer(2);

            Assert.That(ordinary.Phase, Is.EqualTo(GamePhase.Entrance));
            Assert.That(ordinary.Round, Is.Zero);
            Assert.That(ordinary.ActionRound, Is.Zero);
            Assert.That(player.CityLocationId, Is.Empty);
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
            Assert.That(ordinary.Map.OpenLocationIds, Does.Not.Contain(RightCardSmokeStateFactory.DefaultCityLocationId));
        }

        [Test]
        public void CreateInitialState_SharedStyleSmokePreparesFourDistinctPlayerMarkers()
        {
            var seats = new List<PlayerSeat>
            {
                new PlayerSeat { PlayerId = 1, PlayerName = "Player 1", Color = PlayerColor.Blue },
                new PlayerSeat { PlayerId = 2, PlayerName = "Player 2", Color = PlayerColor.Red },
                new PlayerSeat { PlayerId = 3, PlayerName = "Player 3", Color = PlayerColor.Green },
                new PlayerSeat { PlayerId = 4, PlayerName = "Player 4", Color = PlayerColor.Yellow }
            };

            var state = RightCardSmokeStateFactory.CreateInitialState(
                LaunchMode.Host,
                1,
                seats,
                StaticMapDefinitions.FourPlayerMapId,
                EventDeckService.DefaultSeed,
                true);
            var markerIds = new HashSet<string>();

            Assert.That(state.Players, Has.Count.EqualTo(4));
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                Assert.That(player.DeclaredCityStyles, Has.Count.EqualTo(1));
                Assert.That(
                    player.DeclaredCityStyles[0].CityStyleId,
                    Is.EqualTo(RightCardSmokeStateFactory.SharedCityStyleId));
                Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Unused));
                Assert.That(markerIds.Add(player.DeclaredCityStyles[0].InfluenceMarkerId), Is.True);
            }

            Assert.That(markerIds, Has.Count.EqualTo(4));
        }

        private static YC.Domain.State.GameState CreateSmokeState(IList<PlayerSeat> seats)
        {
            return RightCardSmokeStateFactory.CreateInitialState(
                LaunchMode.Local,
                2,
                seats,
                StaticMapDefinitions.FourPlayerMapId,
                EventDeckService.DefaultSeed);
        }

        private static List<PlayerSeat> CreateSeats()
        {
            return new List<PlayerSeat>
            {
                new PlayerSeat
                {
                    PlayerId = 1,
                    PlayerName = "Player 1",
                    Color = PlayerColor.Blue,
                    IsReady = true
                },
                new PlayerSeat
                {
                    PlayerId = 2,
                    PlayerName = "Player 2",
                    Color = PlayerColor.Red,
                    IsReady = true
                }
            };
        }
    }
}
