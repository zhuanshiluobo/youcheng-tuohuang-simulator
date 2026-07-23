using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class SpecialActionCoreIntegrationTests
    {
        [Test]
        public void GameSession_WithInvalidPendingSpecialAction_ClearsItOnConstructionAndReplacement()
        {
            var session = new GameSession(new GameState
            {
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "incomplete-session"
                }
            });

            Assert.That(session.State.PendingSpecialAction, Is.Null);
            Assert.That(session.State.HasPendingChoice(), Is.False);

            session.ReplaceState(new GameState
            {
                PendingSpecialAction = new PendingSpecialActionState
                {
                    PlayerId = 1,
                    SpecialActionId = SpecialActionDatabase.CompositePowerSystem
                }
            });

            Assert.That(session.State.PendingSpecialAction, Is.Null);
            Assert.That(session.State.HasPendingChoice(), Is.False);

            session.ReplaceState(new GameState
            {
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "incompatible-step",
                    PlayerId = 1,
                    SpecialActionId = SpecialActionDatabase.EfficientMobileManagementSystem,
                    DeclarationMarkerId = "marker",
                    Step = SpecialActionPendingSteps.AwaitMilitaryTargets
                }
            });

            Assert.That(session.State.PendingSpecialAction, Is.Null, "行动类型不兼容的步骤必须清理。");

            session.ReplaceState(new GameState
            {
                Players =
                {
                    CreatePlayerWithActivatedDeclaration(
                        1,
                        "legacy-payment-marker",
                        CityStyleDatabase.CompositePowerSystem,
                        SpecialActionDatabase.CompositePowerSystem,
                        CityStyleMarkerAreas.Used,
                        0)
                },
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "legacy-composite-payment",
                    PlayerId = 1,
                    SpecialActionId = SpecialActionDatabase.CompositePowerSystem,
                    DeclarationMarkerId = "legacy-payment-marker",
                    Step = "await_composite_payment"
                }
            });

            Assert.That(
                session.State.PendingSpecialAction,
                Is.Null,
                "旧版延迟支付步骤必须清理，复合动力费用只能随首条命令原子提交。");

            session.ReplaceState(new GameState
            {
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "orphan-move-event",
                    PlayerId = 1,
                    SpecialActionId = SpecialActionDatabase.EfficientMobileManagementSystem,
                    DeclarationMarkerId = "marker",
                    Step = SpecialActionPendingSteps.AwaitMoveEvent
                }
            });

            Assert.That(session.State.PendingSpecialAction, Is.Null, "没有对应事件会话的移动事件步骤必须清理。");

            session.ReplaceState(new GameState
            {
                Players =
                {
                    CreatePlayerWithActivatedDeclaration(
                        1,
                        "marker",
                        CityStyleDatabase.EfficientMobileManagementSystem,
                        SpecialActionDatabase.EfficientMobileManagementSystem,
                        SpecialActionMarkerAreas.UsedFromTwo,
                        2)
                },
                PendingCardSession = new PendingCardSessionState
                {
                    SessionId = "card-session",
                    ScenarioId = "event",
                    ChoiceType = MoveCityCommandHandler.MoveCityEventChoiceType,
                    CardId = "event_yellow_01",
                    PlayerId = 1,
                    OptionIds = { "0" }
                },
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "valid-move-event",
                    PlayerId = 1,
                    SpecialActionId = SpecialActionDatabase.EfficientMobileManagementSystem,
                    DeclarationMarkerId = "marker",
                    Step = SpecialActionPendingSteps.AwaitMoveEvent,
                    RemainingRepetitions = 1,
                    TraversedRouteId = "A1"
                }
            });

            Assert.That(session.State.PendingSpecialAction, Is.Not.Null, "有效事件会话必须保留特殊行动进度。");
        }

        [Test]
        public void GameSession_WithGhostSpecialActionOwnerDeclarationOrStep_ClearsSnapshotWithoutBlockingCommands()
        {
            var unknownOwnerState = new GameState
            {
                Players = { new PlayerState { PlayerId = 1 } },
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "ghost-owner",
                    PlayerId = 99,
                    SpecialActionId = SpecialActionDatabase.MilitaryIndustrialArea,
                    DeclarationMarkerId = "ghost-marker",
                    Step = SpecialActionPendingSteps.AwaitMilitaryTargets
                }
            };

            var session = new GameSession(unknownOwnerState);

            Assert.That(session.State.PendingSpecialAction, Is.Null);
            Assert.That(session.State.HasPendingChoice(), Is.False);

            session.ReplaceState(new GameState
            {
                Players =
                {
                    CreatePlayerWithActivatedDeclaration(
                        1,
                        "efficient-marker",
                        CityStyleDatabase.EfficientMobileManagementSystem,
                        SpecialActionDatabase.EfficientMobileManagementSystem,
                        SpecialActionMarkerAreas.UsedFromTwo,
                        2)
                },
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "exhausted-free-moves",
                    PlayerId = 1,
                    SpecialActionId = SpecialActionDatabase.EfficientMobileManagementSystem,
                    DeclarationMarkerId = "efficient-marker",
                    Step = SpecialActionPendingSteps.AwaitFreeMoveTarget,
                    RemainingRepetitions = 0
                }
            });

            Assert.That(session.State.PendingSpecialAction, Is.Null,
                "剩余次数为零的免费移动步骤不能从损坏快照中恢复。");
            Assert.That(session.State.HasPendingChoice(), Is.False);

            session.ReplaceState(new GameState
            {
                Players =
                {
                    CreatePlayerWithActivatedDeclaration(
                        1,
                        "owned-marker",
                        CityStyleDatabase.MilitaryIndustrialArea,
                        SpecialActionDatabase.MilitaryIndustrialArea,
                        CityStyleMarkerAreas.Used,
                        0)
                },
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "wrong-marker",
                    PlayerId = 1,
                    SpecialActionId = SpecialActionDatabase.MilitaryIndustrialArea,
                    DeclarationMarkerId = "other-marker",
                    Step = SpecialActionPendingSteps.AwaitMilitaryTargets
                }
            });

            Assert.That(session.State.PendingSpecialAction, Is.Null);
            Assert.That(session.State.HasPendingChoice(), Is.False);

            session.ReplaceState(new GameState
            {
                Players =
                {
                    CreatePlayerWithActivatedDeclaration(
                        1,
                        "owned-marker",
                        CityStyleDatabase.MilitaryIndustrialArea,
                        SpecialActionDatabase.MilitaryIndustrialArea,
                        CityStyleMarkerAreas.Used,
                        0)
                },
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "valid-used-marker",
                    PlayerId = 1,
                    SpecialActionId = SpecialActionDatabase.MilitaryIndustrialArea,
                    DeclarationMarkerId = "owned-marker",
                    Step = SpecialActionPendingSteps.AwaitMilitaryTargets
                }
            });

            Assert.That(session.State.PendingSpecialAction, Is.Not.Null,
                "运行中已移动到已使用区的合法标记不能被误清理。");
            Assert.That(session.State.HasPendingChoice(), Is.True);
        }

        [Test]
        public void StateDtos_JsonRoundTrip_PreserveSpecialActionSessionBudgetAndLevelTwoUsedArea()
        {
            var state = new GameState
            {
                PendingSpecialAction = new PendingSpecialActionState
                {
                    SessionId = "special-session",
                    PlayerId = 1,
                    SpecialActionId = SpecialActionDatabase.EfficientMobileManagementSystem,
                    DeclarationMarkerId = "style-marker",
                    SourceCommandId = "source-command",
                    Step = SpecialActionPendingSteps.AwaitMoveEvent,
                    RemainingRepetitions = 1,
                    TraversedRouteId = "A1",
                    PaidOriginiumShard = 3,
                    ResolvedTargetIds = { "A-02" }
                },
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        RemainingMainActionsThisTurn = 2,
                        CompletedMainActionsThisTurn = 1,
                        UsedCharacterThisTurn = false,
                        CharacterCardLockedThisTurn = true,
                        UsedSpecialActionIdsThisRound =
                        {
                            SpecialActionDatabase.EfficientMobileManagementSystem
                        },
                        DeclaredCityStyles =
                        {
                            new CityStyleDeclarationState
                            {
                                InfluenceMarkerId = "style-marker",
                                CityStyleId = CityStyleDatabase.EfficientMobileManagementSystem,
                                MarkerArea = SpecialActionMarkerAreas.UsedFromTwo,
                                UnlockedSpecialActionId = SpecialActionDatabase.EfficientMobileManagementSystem,
                                RemainingSpecialActionUses = 2
                            }
                        }
                    }
                }
            };

            var initial = CloneJson(new InitialGameStateDto
            {
                NextConfirmedSequence = 7,
                State = state
            });
            var confirmed = CloneJson(new ConfirmedGameCommandDto
            {
                Sequence = 6,
                State = state
            });

            AssertSpecialSnapshot(initial.State);
            Assert.That(initial.NextConfirmedSequence, Is.EqualTo(7));
            AssertSpecialSnapshot(confirmed.State);
            Assert.That(confirmed.Sequence, Is.EqualTo(6));
        }

        [Test]
        public void RoundAdvance_EndCleanup_AfterCharacterCleanupAdvancesSpecialActionMarkers()
        {
            var state = new GameState
            {
                Phase = GamePhase.Cleanup,
                Round = 1,
                MaxRounds = 8,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        UsedSpecialActionIdsThisRound =
                        {
                            SpecialActionDatabase.MilitaryIndustrialArea,
                            SpecialActionDatabase.SourceStoneIndustrialHub
                        },
                        DeclaredCityStyles =
                        {
                            new CityStyleDeclarationState
                            {
                                InfluenceMarkerId = "level-one-marker",
                                CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                                MarkerArea = CityStyleMarkerAreas.Used,
                                UnlockedSpecialActionId = SpecialActionDatabase.MilitaryIndustrialArea,
                                RemainingSpecialActionUses = 0
                            },
                            new CityStyleDeclarationState
                            {
                                InfluenceMarkerId = "level-two-marker",
                                CityStyleId = CityStyleDatabase.SourceStoneIndustrialHub,
                                MarkerArea = SpecialActionMarkerAreas.UsedFromTwo,
                                UnlockedSpecialActionId = SpecialActionDatabase.SourceStoneIndustrialHub,
                                RemainingSpecialActionUses = 2
                            }
                        }
                    }
                }
            };
            var turnOrder = new TurnOrderService();
            var lifecycle = new SpecialActionLifecycleService();
            var service = new RoundAdvanceService(
                turnOrder,
                new CharacterCardService(turnOrder),
                new MainActionBudgetService(),
                lifecycle);

            var result = service.EndCompletedAction(state, 1);

            Assert.That(result.IsValid, Is.True);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Players[0].DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Unused));
            Assert.That(state.Players[0].DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(1));
            Assert.That(state.Players[0].DeclaredCityStyles[1].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesOne));
            Assert.That(state.Players[0].DeclaredCityStyles[1].RemainingSpecialActionUses, Is.EqualTo(1));
            Assert.That(state.Players[0].UsedSpecialActionIdsThisRound, Is.Empty);
        }

        [Test]
        public void GameSession_RealSpecialAction_DelaysNamedPublicLogUntilSettlement()
        {
            var context = CreateCommandContext();
            var state = CreateMilitaryState();
            var session = new GameSession(state);
            session.RegisterHandler(context.SpecialActionHandler);
            session.RegisterHandler(context.MoveCityHandler);

            var begin = session.Submit(new GameCommand
            {
                CommandId = "special-begin",
                Kind = GameCommandKind.UseSpecialAction,
                PlayerId = 1,
                SourceId = "military-marker",
                TargetId = SpecialActionDatabase.MilitaryIndustrialArea
            });

            Assert.That(begin.Succeeded, Is.True);
            Assert.That(state.PendingSpecialAction, Is.Not.Null);
            Assert.That(state.Logs, Is.Empty, "多步特殊行动完整结算前不应发布公开日志。");

            var legalSlots = context.OptionQuery.GetLegalInfluencePlacementSlotIds(state, 1);
            Assert.That(legalSlots, Is.Not.Empty);
            var resolve = new GameCommand
            {
                CommandId = "special-resolve",
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1
            };
            resolve.Parameters[UseSpecialActionCommandHandler.SessionIdParameter] = state.PendingSpecialAction.SessionId;
            resolve.Parameters[UseSpecialActionCommandHandler.InfluenceSlotIdsParameter] = legalSlots[0];

            var settled = session.Submit(resolve);

            Assert.That(settled.Succeeded, Is.True);
            Assert.That(state.PendingSpecialAction, Is.Null);
            Assert.That(state.Logs, Has.Count.EqualTo(1));
            Assert.That(state.Logs[0].CommandId, Is.EqualTo("special-resolve"));
            Assert.That(state.Logs[0].Message, Does.Contain("军工化区域"));
            Assert.That(state.Logs[0].Message, Does.Contain("放置了 1 个影响力"));
        }

        private static T CloneJson<T>(T source)
        {
            return JsonUtility.FromJson<T>(JsonUtility.ToJson(source));
        }

        private static void AssertSpecialSnapshot(GameState state)
        {
            Assert.That(state.PendingSpecialAction, Is.Not.Null);
            Assert.That(state.PendingSpecialAction.SessionId, Is.EqualTo("special-session"));
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitMoveEvent));
            Assert.That(state.PendingSpecialAction.RemainingRepetitions, Is.EqualTo(1));
            Assert.That(state.PendingSpecialAction.TraversedRouteId, Is.EqualTo("A1"));
            Assert.That(state.PendingSpecialAction.PaidOriginiumShard, Is.EqualTo(3));
            Assert.That(state.PendingSpecialAction.ResolvedTargetIds, Is.EqualTo(new[] { "A-02" }));
            var player = state.FindPlayer(1);
            Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(2));
            Assert.That(player.CompletedMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(player.CharacterCardLockedThisTurn, Is.True);
            Assert.That(player.UsedSpecialActionIdsThisRound, Is.EqualTo(new[]
            {
                SpecialActionDatabase.EfficientMobileManagementSystem
            }));
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(SpecialActionMarkerAreas.UsedFromTwo));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(2));
        }

        private static CommandContext CreateCommandContext()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influenceService = new InfluenceService(mapQuery);
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                new TravelCostService(mapQuery));
            var lifecycle = new SpecialActionLifecycleService();
            var budget = new MainActionBudgetService();
            var optionQuery = new SpecialActionOptionQueryService(
                mapQuery,
                influenceService,
                movementService,
                lifecycle,
                budget);
            var moveHandler = new MoveCityCommandHandler(movementService);
            return new CommandContext
            {
                OptionQuery = optionQuery,
                MoveCityHandler = moveHandler,
                SpecialActionHandler = new UseSpecialActionCommandHandler(
                    new SpecialActionService(
                        optionQuery,
                        lifecycle,
                        influenceService,
                        new FacilityInfluenceEffectService(influenceService),
                        budget),
                    optionQuery,
                    moveHandler)
            };
        }

        private static GameState CreateMilitaryState()
        {
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Round = 4,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        CityLocationId = "A-01",
                        InfluenceSupply = 1,
                        DeclaredCityStyleIds = { CityStyleDatabase.MilitaryIndustrialArea },
                        DeclaredCityStyles =
                        {
                            new CityStyleDeclarationState
                            {
                                InfluenceMarkerId = "military-marker",
                                CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                                MarkerArea = CityStyleMarkerAreas.Unused,
                                UnlockedSpecialActionId = SpecialActionDatabase.MilitaryIndustrialArea,
                                RemainingSpecialActionUses = 1
                            }
                        }
                    }
                },
                Map =
                {
                    OpenLocationIds = { "A-01", "A-02" },
                    ResourceTokens =
                    {
                        new ResourceTokenState
                        {
                            LocationId = "A-01",
                            ResourceType = ResourceType.Iron,
                            Amount = 1
                        },
                        new ResourceTokenState
                        {
                            LocationId = "A-02",
                            ResourceType = ResourceType.Iron,
                            Amount = 1
                        }
                    }
                }
            };
            return state;
        }

        private static PlayerState CreatePlayerWithActivatedDeclaration(
            int playerId,
            string markerId,
            string cityStyleId,
            string specialActionId,
            string markerArea,
            int remainingUses)
        {
            return new PlayerState
            {
                PlayerId = playerId,
                DeclaredCityStyles =
                {
                    new CityStyleDeclarationState
                    {
                        InfluenceMarkerId = markerId,
                        CityStyleId = cityStyleId,
                        MarkerArea = markerArea,
                        UnlockedSpecialActionId = specialActionId,
                        RemainingSpecialActionUses = remainingUses
                    }
                }
            };
        }

        private sealed class CommandContext
        {
            public SpecialActionOptionQueryService OptionQuery;
            public UseSpecialActionCommandHandler SpecialActionHandler;
            public MoveCityCommandHandler MoveCityHandler;
        }
    }
}
