using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class BuildFacilityCommandHandlerTests
    {
        [Test]
        public void BuildFacility_WithResourceCost_SucceedsPaysPlacesScoresAndMarksMainActionComplete()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.BoroughAdministrativeDistrict);
            state.FindPlayer(1).Resources.Originium = 3;
            state.FindPlayer(1).Resources.Iron = 3;
            state.FindPlayer(1).Resources.OriginiumShard = 3;
            var handler = new BuildFacilityCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1,
                TargetId = FacilityCardDatabase.BoroughAdministrativeDistrict,
                Parameters =
                {
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "2" },
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeResources }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).BuiltFacilityIds, Does.Contain(FacilityCardDatabase.BoroughAdministrativeDistrict));
            Assert.That(state.Map.Facilities, Has.Count.EqualTo(1));
            Assert.That(state.Map.Facilities[0].CityBoardSlotIndex, Is.EqualTo(2));
            Assert.That(state.Decks.FacilitySupply, Is.Empty);
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void BuildFacility_WhenFacilityDeckHasCards_RefillsSupplyFromDeck()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.TradeDistrict);
            state.Decks.FacilityDeck.Add(FacilityCardDatabase.SourceStoneRefinery);
            state.FindPlayer(1).Resources.GoldVoucher = 6;
            var handler = new BuildFacilityCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1,
                TargetId = FacilityCardDatabase.TradeDistrict,
                Parameters =
                {
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeGold }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Decks.FacilitySupply, Is.EqualTo(new[] { FacilityCardDatabase.SourceStoneRefinery }));
            Assert.That(state.Decks.FacilityDeck, Is.Empty);
        }

        [Test]
        public void BuildFacility_WithGoldCost_SucceedsPaysGoldAndAppliesOnBuiltReward()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.SourceStoneRefinery);
            state.FindPlayer(1).Resources.GoldVoucher = 10;
            var handler = new BuildFacilityCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1,
                TargetId = FacilityCardDatabase.SourceStoneRefinery,
                Parameters =
                {
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeGold }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(6));
            Assert.That(state.Map.Facilities[0].CityBoardSlotIndex, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void BuildFacility_WhenInsufficientResources_FailsWithoutMutating()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.UrbanizedArea);
            state.FindPlayer(1).Resources.Originium = 2;
            state.FindPlayer(1).Resources.OriginiumShard = 1;
            var handler = new BuildFacilityCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1,
                TargetId = FacilityCardDatabase.UrbanizedArea,
                Parameters =
                {
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeResources }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(state.Map.Facilities, Is.Empty);
            Assert.That(state.FindPlayer(1).BuiltFacilityIds, Is.Empty);
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
            Assert.That(state.Decks.FacilitySupply, Does.Contain(FacilityCardDatabase.UrbanizedArea));
        }

        [Test]
        public void BuildFacility_WhenCityBoardSlotOccupied_FailsWithoutPaying()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.TradeDistrict);
            state.FindPlayer(1).Resources.GoldVoucher = 6;
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = FacilityCardDatabase.EquipmentWarehouse,
                CityBoardSlotIndex = 0
            });
            var handler = new BuildFacilityCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1,
                TargetId = FacilityCardDatabase.TradeDistrict,
                Parameters =
                {
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "0" },
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeGold }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(state.Map.Facilities, Has.Count.EqualTo(1));
        }

        [Test]
        public void BuildFacility_WhenUniqueAlreadyBuilt_FailsWithoutMutating()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.FederalOffice);
            state.FindPlayer(1).BuiltFacilityIds.Add(FacilityCardDatabase.FederalOffice);
            state.FindPlayer(1).Resources.GoldVoucher = 17;
            var handler = new BuildFacilityCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1,
                TargetId = FacilityCardDatabase.FederalOffice,
                Parameters =
                {
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeGold }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(17));
            Assert.That(state.Map.Facilities, Is.Empty);
        }

        [Test]
        public void BuildFacility_AllowsTwelfthCityBoardSlot()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.TradeDistrict);
            state.FindPlayer(1).Resources.GoldVoucher = 6;
            var handler = new BuildFacilityCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1,
                TargetId = FacilityCardDatabase.TradeDistrict,
                Parameters =
                {
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "11" },
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeGold }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Facilities[0].CityBoardSlotIndex, Is.EqualTo(11));
        }

        [Test]
        public void FindFirstEmptyCityBoardSlot_WhenTwelveSlotsOccupied_ReturnsMinusOne()
        {
            var state = CreateActionState();
            for (var i = 0; i < BuildFacilityService.CityBoardSlotCount; i++)
            {
                state.Map.Facilities.Add(new FacilityPlacement
                {
                    PlayerId = 1,
                    FacilityCardId = FacilityCardDatabase.TradeDistrict,
                    CityBoardSlotIndex = i
                });
            }

            Assert.That(BuildFacilityService.FindFirstEmptyCityBoardSlot(state, 1), Is.EqualTo(-1));
        }

        [Test]
        public void GameLaunchStateFactory_InitializesFacilitySupply()
        {
            var state = GameLaunchStateFactory.CreateInitialState(
                LaunchMode.Local,
                1,
                null,
                "map_four_players",
                123);

            Assert.That(state.Decks.FacilitySupply, Is.Not.Empty);
            Assert.That(state.Decks.FacilitySupply, Has.Count.EqualTo(6));
            Assert.That(
                state.Decks.FacilityDeck,
                Has.Count.EqualTo(FacilityCardDatabase.DefaultSupplyIds.Count - 6));
            Assert.That(state.Decks.FacilitySupply, Does.Contain(FacilityCardDatabase.BoroughAdministrativeDistrict));
        }

        private static GameState CreateActionState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Blue
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Red
                    }
                }
            };
        }
    }
}
