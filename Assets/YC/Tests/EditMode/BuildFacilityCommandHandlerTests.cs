using NUnit.Framework;
using System.Collections.Generic;
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
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "0" },
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
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "0" },
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeGold }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(6));
            Assert.That(state.Map.Facilities[0].CityBoardSlotIndex, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void BuildFacility_ResolvesEntryEffectAfterPlacementAndScoreBeforeSupplyRefill()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.SourceStoneRefinery);
            state.Decks.FacilityDeck.Add(FacilityCardDatabase.TradeDistrict);
            state.FindPlayer(1).Resources.GoldVoucher = 10;
            var resolver = new OrderAssertingEntryEffectResolver(state);
            var service = new BuildFacilityService(resolver);

            var result = service.Build(
                state,
                1,
                FacilityCardDatabase.SourceStoneRefinery,
                2,
                BuildFacilityService.PaymentModeGold);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(resolver.WasCalled, Is.True);
            Assert.That(state.Decks.FacilitySupply, Is.EqualTo(new[] { FacilityCardDatabase.TradeDistrict }));
            Assert.That(state.Decks.FacilityDeck, Is.Empty);
        }

        [Test]
        public void BuildFacility_ReplacesBuiltCardAtItsOriginalSupplySlotWithoutShiftingOtherCards()
        {
            var state = CreateActionState();
            var originalSupply = new[]
            {
                FacilityCardDatabase.TradeDistrict,
                FacilityCardDatabase.SourceStoneRefinery,
                FacilityCardDatabase.UrbanizedArea,
                FacilityCardDatabase.SimpleEngineeringCamp,
                FacilityCardDatabase.FederalOffice,
                "building_008"
            };
            state.Decks.FacilitySupply.AddRange(originalSupply);
            state.Decks.FacilityDeck.Add(FacilityCardDatabase.EquipmentWarehouse);
            state.FindPlayer(1).Resources.GoldVoucher = 100;

            var result = new BuildFacilityService().Build(
                state,
                1,
                FacilityCardDatabase.UrbanizedArea,
                4,
                BuildFacilityService.PaymentModeGold);

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(state.Decks.FacilitySupply, Is.EqualTo(new[]
            {
                originalSupply[0],
                originalSupply[1],
                FacilityCardDatabase.EquipmentWarehouse,
                originalSupply[3],
                originalSupply[4],
                originalSupply[5]
            }));
            Assert.That(state.Decks.FacilityDeck, Is.Empty);
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
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "0" },
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
            state.Decks.FacilitySupply.Add("building_008");
            state.FindPlayer(1).BuiltFacilityIds.Add(FacilityCardDatabase.FederalOffice);
            state.FindPlayer(1).Resources.GoldVoucher = 17;
            var handler = new BuildFacilityCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1,
                TargetId = "building_008",
                Parameters =
                {
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "0" },
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeGold }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(17));
            Assert.That(state.Map.Facilities, Is.Empty);
        }

        [Test]
        public void BuildFacility_WhenSlotIsMissing_FailsWithoutMutating()
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
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeGold }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(state.Map.Facilities, Is.Empty);
            Assert.That(state.Decks.FacilitySupply, Does.Contain(FacilityCardDatabase.TradeDistrict));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
        }

        [Test]
        public void BuildFacility_WhenPaymentModeIsMissing_FailsWithoutMutating()
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
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "4" }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(state.Map.Facilities, Is.Empty);
            Assert.That(state.Decks.FacilitySupply, Does.Contain(FacilityCardDatabase.TradeDistrict));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
        }

        [TestCase(BuildFacilityService.PaymentModeAuto)]
        [TestCase("unknown")]
        public void BuildFacility_WhenPaymentModeIsNotExplicit_FailsWithoutMutating(string paymentMode)
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
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "4" },
                    { BuildFacilityCommandHandler.PaymentModeParameter, paymentMode }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(state.Map.Facilities, Is.Empty);
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
        }

        [Test]
        public void QueryBuildFacilityOptions_ReturnsAllSupplyOptionsAndDoesNotMutateState()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.TradeDistrict);
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.SourceStoneRefinery);
            state.Decks.FacilityDeck.Add(FacilityCardDatabase.UrbanizedArea);
            state.FindPlayer(1).Resources.Originium = 1;
            state.FindPlayer(1).Resources.Iron = 1;
            state.FindPlayer(1).Resources.GoldVoucher = 0;
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = FacilityCardDatabase.CoreCommandTower,
                CityBoardSlotIndex = 0
            });
            var service = new BuildFacilityOptionQueryService();

            var options = service.Query(state, 1);

            Assert.That(options, Has.Count.EqualTo(2));
            Assert.That(options[0].FacilityId, Is.EqualTo(FacilityCardDatabase.TradeDistrict));
            Assert.That(options[1].FacilityId, Is.EqualTo(FacilityCardDatabase.SourceStoneRefinery));
            Assert.That(options[0].CanBuild, Is.True, options[0].Reason);
            Assert.That(options[0].SlotOptions, Has.Count.EqualTo(BuildFacilityService.CityBoardSlotCount));
            Assert.That(options[0].SlotOptions[0].IsLegal, Is.False);
            Assert.That(options[0].SlotOptions[0].Validation.ErrorCode, Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(options[0].SlotOptions[1].IsLegal, Is.True, options[0].SlotOptions[1].Reason);
            Assert.That(options[0].PaymentOptions, Has.Count.EqualTo(2));
            Assert.That(options[0].ResourcesPayment.IsAvailable, Is.True, options[0].ResourcesPayment.Reason);
            Assert.That(options[0].GoldPayment.IsAvailable, Is.False);
            Assert.That(options[0].GoldPayment.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));

            Assert.That(state.Decks.FacilitySupply, Is.EqualTo(new[]
            {
                FacilityCardDatabase.TradeDistrict,
                FacilityCardDatabase.SourceStoneRefinery
            }));
            Assert.That(state.Decks.FacilityDeck, Is.EqualTo(new[] { FacilityCardDatabase.UrbanizedArea }));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).BuiltFacilityIds, Is.Empty);
            Assert.That(state.Map.Facilities, Has.Count.EqualTo(1));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
        }

        [Test]
        public void QueryBuildFacilityOption_WhenUniqueAlreadyBuilt_ReturnsReasonsForFacilitySlotsAndPayments()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add("building_008");
            state.FindPlayer(1).BuiltFacilityIds.Add(FacilityCardDatabase.FederalOffice);
            state.FindPlayer(1).Resources.Originium = 100;
            state.FindPlayer(1).Resources.OriginiumShard = 100;
            state.FindPlayer(1).Resources.GoldVoucher = 100;
            var service = new BuildFacilityOptionQueryService();

            var option = service.Query(state, 1, "building_008");

            Assert.That(option.CanBuild, Is.False);
            Assert.That(option.Reason, Does.Contain("唯一设施"));
            Assert.That(option.SlotOptions, Has.Count.EqualTo(BuildFacilityService.CityBoardSlotCount));
            for (var i = 0; i < option.SlotOptions.Count; i++)
            {
                Assert.That(option.SlotOptions[i].IsLegal, Is.False);
            }
            Assert.That(option.SlotOptions[0].Reason, Does.Contain("唯一设施"));
            Assert.That(option.ResourcesPayment.IsAvailable, Is.False);
            Assert.That(option.ResourcesPayment.Reason, Does.Contain("唯一设施"));
            Assert.That(option.GoldPayment.IsAvailable, Is.False);
            Assert.That(option.GoldPayment.Reason, Does.Contain("唯一设施"));
        }

        [Test]
        public void QueryBuildFacilityOption_WhenBothPaymentModesAreInsufficient_ReturnsOverallAndPerModeReasons()
        {
            var state = CreateActionState();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.TradeDistrict);
            var service = new BuildFacilityOptionQueryService();

            var option = service.Query(state, 1, FacilityCardDatabase.TradeDistrict);

            Assert.That(option.CanBuild, Is.False);
            Assert.That(option.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(option.ResourcesPayment.IsAvailable, Is.False);
            Assert.That(option.ResourcesPayment.Reason, Does.Contain("资源不足"));
            Assert.That(option.GoldPayment.IsAvailable, Is.False);
            Assert.That(option.GoldPayment.Reason, Does.Contain("金券不足"));
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
        public void FacilityCardDatabase_LoadsFormalBuildingCardsManifest()
        {
            Assert.That(FacilityCardDatabase.DefaultSupplyIds, Has.Count.EqualTo(41));
            Assert.That(FacilityCardDatabase.DefaultSupplyIds, Does.Not.Contain(FacilityCardDatabase.CoreCommandTower));
            Assert.That(FacilityCardDatabase.DefaultSupplyIds, Does.Not.Contain(FacilityCardDatabase.ExtensionHubBlue));

            for (var i = 0; i < FacilityCardDatabase.DefaultSupplyIds.Count; i++)
            {
                var facilityId = FacilityCardDatabase.DefaultSupplyIds[i];
                var facility = FacilityCardDatabase.Get(facilityId);
                Assert.That(facility, Is.Not.Null);
            }

            var logisticsHub = FacilityCardDatabase.Get(FacilityCardDatabase.LogisticsHub);
            Assert.That(logisticsHub.Name, Is.EqualTo("物流枢纽"));
            Assert.That(logisticsHub.ResourceCost.PureOriginium, Is.EqualTo(1));
            Assert.That(logisticsHub.ResourceCost.GoldVoucher, Is.EqualTo(2));
            Assert.That(
                System.IO.File.Exists(
                    System.IO.Path.Combine(
                        System.IO.Directory.GetCurrentDirectory(),
                        "Assets",
                        "StreamingAssets",
                        "YC",
                        "Data",
                        "building_cards_manifest.json")),
                Is.True);

            var extensionHub = FacilityCardDatabase.Get(FacilityCardDatabase.ExtensionHubBlue);
            Assert.That(extensionHub.ReserveOnly, Is.True);

            for (var i = 0; i < FacilityCardDatabase.ReserveIds.Count; i++)
            {
                var reserveId = FacilityCardDatabase.ReserveIds[i];
                Assert.That(FacilityCardDatabase.Get(reserveId), Is.Not.Null);
            }

            Assert.That(typeof(FacilityCardDefinition).GetField("ImageRelativePath"), Is.Null);
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
            Assert.That(
                state.Decks.FacilitySupply,
                Is.EqualTo(new[]
                {
                    FacilityCardDatabase.CityIndustrialDistrict,
                    FacilityCardDatabase.OriginiumPurificationPlant,
                    FacilityCardDatabase.LogisticsHub,
                    "building_038",
                    FacilityCardDatabase.SimpleEngineeringCamp,
                    "building_008"
                }));
        }

        [Test]
        public void GameLaunchStateFactory_PlacesCoreCommandTowerOnSlotEightForEveryPlayer()
        {
            var state = GameLaunchStateFactory.CreateInitialState(
                LaunchMode.Local,
                1,
                new List<PlayerSeat>
                {
                    new PlayerSeat { PlayerId = 1, PlayerName = "Player 1", Color = PlayerColor.Blue },
                    new PlayerSeat { PlayerId = 2, PlayerName = "Player 2", Color = PlayerColor.Red },
                    new PlayerSeat { PlayerId = 3, PlayerName = "Player 3", Color = PlayerColor.Green },
                    new PlayerSeat { PlayerId = 4, PlayerName = "Player 4", Color = PlayerColor.Yellow }
                },
                "map_four_players",
                123);

            Assert.That(state.Map.Facilities, Has.Count.EqualTo(4));
            for (var playerId = 1; playerId <= 4; playerId++)
            {
                Assert.That(
                    CountCoreCommandTowerPlacements(state, playerId),
                    Is.EqualTo(1),
                    "Player " + playerId + " should start with one core command tower.");
                Assert.That(
                    state.FindPlayer(playerId).BuiltFacilityIds,
                    Does.Contain(FacilityCardDatabase.CoreCommandTower));
            }
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

        private static int CountCoreCommandTowerPlacements(GameState state, int playerId)
        {
            var count = 0;
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var placement = state.Map.Facilities[i];
                if (placement.PlayerId == playerId &&
                    placement.FacilityCardId == FacilityCardDatabase.CoreCommandTower &&
                    placement.CityBoardSlotIndex == BuildFacilityService.CoreCommandTowerCityBoardSlotIndex)
                {
                    count++;
                }
            }

            return count;
        }

        private sealed class OrderAssertingEntryEffectResolver : IFacilityEntryEffectResolver
        {
            private readonly GameState expectedState;

            public OrderAssertingEntryEffectResolver(GameState expectedState)
            {
                this.expectedState = expectedState;
            }

            public bool WasCalled { get; private set; }

            public void Resolve(
                GameState state,
                PlayerState player,
                FacilityCardDefinition facility,
                int cityBoardSlotIndex)
            {
                Assert.That(state, Is.SameAs(expectedState));
                Assert.That(player.Resources.GoldVoucher, Is.EqualTo(0), "入场效果前应已支付费用。");
                Assert.That(player.BuiltFacilityIds, Does.Contain(facility.FacilityId), "入场效果前应已登记设施。");
                Assert.That(player.Score, Is.EqualTo(facility.Score), "入场效果前应已获得牌面分数。");
                Assert.That(
                    state.Map.Facilities.Exists(placement =>
                        placement.PlayerId == player.PlayerId &&
                        placement.FacilityCardId == facility.FacilityId &&
                        placement.CityBoardSlotIndex == cityBoardSlotIndex),
                    Is.True,
                    "入场效果前应已将设施放入指定槽位。");
                Assert.That(state.Decks.FacilitySupply, Does.Contain(facility.FacilityId), "入场效果结算时供应区尚未补牌。");
                Assert.That(state.Decks.FacilityDeck, Is.EqualTo(new[] { FacilityCardDatabase.TradeDistrict }));
                WasCalled = true;
            }
        }
    }
}
