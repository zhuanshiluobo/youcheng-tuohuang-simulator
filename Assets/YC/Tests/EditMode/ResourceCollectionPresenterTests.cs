using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class ResourceCollectionPresenterTests
    {
        [Test]
        public void Begin_BuildsQueryAndHighlightsReachableSelectionAndTollRoute()
        {
            var fixture = CreateFixture();

            fixture.Presenter.Begin();

            Assert.That(fixture.Presenter.CurrentQuery, Is.Not.Null);
            Assert.That(fixture.Presenter.CurrentQuery.IsValid, Is.True);
            Assert.That(fixture.Presenter.SelectedLocationIds, Is.EquivalentTo(new[] { "B" }));
            Assert.That(fixture.View.HasHighlight("B", WorkflowHighlightSemantic.CollectionSelected), Is.True);
            Assert.That(fixture.View.HasHighlight("R2", WorkflowHighlightSemantic.CollectionPaymentRequired), Is.True);
        }

        [Test]
        public void SelectRoutePayment_WhenUnaffordable_DoesNotOpenPaymentOptions()
        {
            var fixture = CreateFixture();
            fixture.Context.State.FindPlayer(1).Resources.GoldVoucher = 0;
            fixture.Presenter.Begin();

            fixture.Presenter.SelectRoutePayment("R2");

            Assert.That(fixture.View.PaymentRouteId, Is.Empty);
            Assert.That(fixture.View.Prompt, Does.Contain("\u91d1\u5238\u4e0d\u8db3"));
        }

        [Test]
        public void ConfirmRoutePayment_RecordsRecipientAndMakesTargetReachable()
        {
            var fixture = CreateFixture();
            fixture.Presenter.Begin();

            fixture.Presenter.SelectRoutePayment("R2");
            fixture.Presenter.ConfirmRoutePayment("R2", 2);

            Assert.That(fixture.View.PaymentRouteId, Is.EqualTo("R2"));
            Assert.That(fixture.View.PaymentRecipients, Is.EqualTo(new[] { 2 }));
            Assert.That(fixture.Presenter.PaidRouteIds, Is.EquivalentTo(new[] { "R2" }));
            Assert.That(fixture.Presenter.SelectedLocationIds, Is.EquivalentTo(new[] { "B", "C" }));
            Assert.That(fixture.View.Prompt, Does.StartWith("\u91c7\u96c6\u9636\u6bb5\uff1a"));
        }

        [Test]
        public void ConfirmRoutePayment_ToBank_AddsVisualGhostWithoutMutatingInfluenceState()
        {
            var fixture = CreateFixture();
            fixture.Presenter.Begin();
            var influenceCount = fixture.Context.State.Map.Influences.Count;
            var influenceSupply = fixture.Context.State.FindPlayer(1).InfluenceSupply;

            fixture.Presenter.ConfirmRoutePayment("R2", -1);

            Assert.That(
                fixture.View.HasHighlight("R2", WorkflowHighlightSemantic.CollectionBankPaymentGhost),
                Is.True);
            Assert.That(fixture.Context.State.Map.Influences, Has.Count.EqualTo(influenceCount));
            Assert.That(fixture.Context.State.FindPlayer(1).InfluenceSupply, Is.EqualTo(influenceSupply));
        }

        [Test]
        public void SelectLocation_TogglesReachableResourcePoint()
        {
            var fixture = CreateFixture();
            fixture.Presenter.Begin();

            fixture.Presenter.SelectLocation("B");
            Assert.That(fixture.Presenter.SelectedLocationIds, Is.Empty);
            Assert.That(fixture.View.HasHighlight("B", WorkflowHighlightSemantic.CollectionCandidate), Is.False);
            Assert.That(fixture.View.HasHighlight("B", WorkflowHighlightSemantic.CollectionSelected), Is.False);
            Assert.That(fixture.View.Prompt, Does.StartWith("\u91c7\u96c6\u9636\u6bb5\uff1a"));

            fixture.Presenter.SelectLocation("B");
            Assert.That(fixture.Presenter.SelectedLocationIds, Is.EquivalentTo(new[] { "B" }));
        }

        [Test]
        public void Begin_DoesNotHighlightMobileCityLocationDuringCollection()
        {
            var fixture = CreateFixture();
            fixture.Context.State.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });

            fixture.Presenter.Begin();

            Assert.That(fixture.Presenter.SelectedLocationIds, Does.Contain("A"));
            Assert.That(fixture.View.HasHighlight("A", WorkflowHighlightSemantic.CollectionSelected), Is.False);
            Assert.That(fixture.View.HasHighlight("A", WorkflowHighlightSemantic.CollectionCandidate), Is.False);
        }

        [Test]
        public void Submit_BuildsCollectResourceCommandWithLocationsRoutesAndRecipient()
        {
            var fixture = CreateFixture();
            fixture.CommandPort.NextResult = Success(false);
            fixture.Presenter.Begin();
            fixture.Presenter.ConfirmRoutePayment("R2", 2);

            fixture.Presenter.Submit();

            var command = fixture.CommandPort.LastCommand;
            Assert.That(command, Is.Not.Null);
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.CollectResource));
            Assert.That(command.PlayerId, Is.EqualTo(1));
            Assert.That(command.Parameters[CollectResourceCommandHandler.LocationIdsParameter], Is.EqualTo("B,C"));
            Assert.That(Split(command.Parameters[CollectResourceCommandHandler.RouteIdsParameter]),
                Is.EquivalentTo(new[] { "R1", "R2" }));
            Assert.That(command.Parameters[CollectResourceCommandHandler.PaymentRecipientsParameter],
                Is.EqualTo("R2=2"));
        }

        [Test]
        public void Submit_WhenAppliedLocally_RefreshesStateAndShowsResult()
        {
            var fixture = CreateFixture();
            fixture.CommandPort.NextResult = new WorkflowSubmissionResult(
                CommandResult.SuccessResult(new List<GameEvent>
                {
                    new GameEvent
                    {
                        Kind = GameEventKind.ResourceChanged,
                        Data =
                        {
                            { "locationIds", "B" },
                            { "rewardIron", "1" }
                        }
                    }
                }, "collected"),
                true);
            fixture.Presenter.Begin();

            fixture.Presenter.Submit();

            Assert.That(fixture.View.RefreshFromStateCount, Is.EqualTo(1));
            Assert.That(fixture.View.Prompt, Does.Contain("B"));
            Assert.That(fixture.View.Prompt, Does.Contain("\u5f02\u94c1 +1"));
        }

        [Test]
        public void Submit_WhenWaitingForHost_KeepsSelectionAndDoesNotRefreshState()
        {
            var fixture = CreateFixture();
            fixture.CommandPort.NextResult = Success(false);
            fixture.Presenter.Begin();

            fixture.Presenter.Submit();

            Assert.That(fixture.View.RefreshFromStateCount, Is.Zero);
            Assert.That(fixture.Presenter.SelectedLocationIds, Is.EquivalentTo(new[] { "B" }));
            Assert.That(fixture.View.Prompt, Does.Contain("\u7b49\u5f85\u786e\u8ba4"));
        }

        [Test]
        public void Submit_WhenRejected_ShowsValidationAndDoesNotRefreshState()
        {
            var fixture = CreateFixture();
            fixture.CommandPort.NextResult = new WorkflowSubmissionResult(
                CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "rejected")),
                false);
            fixture.Presenter.Begin();

            fixture.Presenter.Submit();

            Assert.That(fixture.View.RefreshFromStateCount, Is.Zero);
            Assert.That(fixture.View.Prompt, Is.EqualTo("rejected"));
        }

        [Test]
        public void Cancel_ClearsQuerySelectionAndHighlights()
        {
            var fixture = CreateFixture();
            fixture.Presenter.Begin();

            fixture.Presenter.Cancel();

            Assert.That(fixture.Presenter.CurrentQuery, Is.Null);
            Assert.That(fixture.Presenter.SelectedLocationIds, Is.Empty);
            Assert.That(fixture.Presenter.PaidRouteIds, Is.Empty);
            Assert.That(fixture.View.Highlights, Is.Empty);
            Assert.That(fixture.View.ClearHighlightsCount, Is.GreaterThan(0));
        }

        private static Fixture CreateFixture()
        {
            var map = CreateMap();
            var context = new FakeContext { State = CreateState() };
            var view = new FakeView();
            var commandPort = new FakeCommandPort { NextResult = Success(false) };
            var mapQuery = new MapQueryService(map);
            return new Fixture
            {
                Context = context,
                View = view,
                CommandPort = commandPort,
                Presenter = new ResourceCollectionPresenter(
                    context,
                    commandPort,
                    view,
                    mapQuery,
                    new ResourceCollectionService(mapQuery))
            };
        }

        private static WorkflowSubmissionResult Success(bool appliedLocally)
        {
            return new WorkflowSubmissionResult(
                CommandResult.SuccessResult(new List<GameEvent>(), "ok"),
                appliedLocally);
        }

        private static GameMapDefinition CreateMap()
        {
            return new GameMapDefinition
            {
                MapId = "resource-collection-presenter",
                Locations =
                {
                    new MapLocationDefinition { LocationId = "A", CanDockCity = true, InfluenceSlotCount = 1 },
                    new MapLocationDefinition { LocationId = "B", InfluenceSlotCount = 1 },
                    new MapLocationDefinition { LocationId = "C", InfluenceSlotCount = 1 }
                },
                Routes =
                {
                    new MapRouteDefinition
                    {
                        RouteId = "R1",
                        FromLocationId = "A",
                        ToLocationId = "B",
                        InfluenceSlotCount = 1
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "R2",
                        FromLocationId = "B",
                        ToLocationId = "C",
                        InfluenceSlotCount = 1
                    }
                }
            };
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Phase = GamePhase.ResourceCollection,
                Round = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CityLocationId = "A",
                        Resources = { GoldVoucher = 2 }
                    },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue }
                },
                Map =
                {
                    ResourceTokens =
                    {
                        new ResourceTokenState { LocationId = "B", ResourceType = ResourceType.Iron, Amount = 1 },
                        new ResourceTokenState { LocationId = "C", ResourceType = ResourceType.Originium, Amount = 1 }
                    },
                    Influences =
                    {
                        new InfluencePlacement
                        {
                            PlayerId = 1,
                            SlotId = InfluenceService.GetLocationSlotId("B", 0),
                            LocationId = "B"
                        },
                        new InfluencePlacement
                        {
                            PlayerId = 1,
                            SlotId = InfluenceService.GetLocationSlotId("C", 0),
                            LocationId = "C"
                        },
                        new InfluencePlacement
                        {
                            PlayerId = 1,
                            SlotId = InfluenceService.GetRouteSlotId("R1", 0),
                            RouteId = "R1"
                        },
                        new InfluencePlacement
                        {
                            PlayerId = 2,
                            SlotId = InfluenceService.GetRouteSlotId("R2", 0),
                            RouteId = "R2"
                        }
                    }
                }
            };
        }

        private static string[] Split(string value)
        {
            return value.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
        }

        private sealed class Fixture
        {
            public FakeContext Context;
            public FakeView View;
            public FakeCommandPort CommandPort;
            public ResourceCollectionPresenter Presenter;
        }

        private sealed class FakeContext : IGameplayContext
        {
            public GameState State;

            public GameState CurrentState
            {
                get { return State; }
            }

            public int LocalPlayerId
            {
                get { return 1; }
            }
        }

        private sealed class FakeCommandPort : IGameCommandPort
        {
            public WorkflowSubmissionResult NextResult;
            public GameCommand LastCommand;

            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                LastCommand = command;
                return NextResult;
            }
        }

        private sealed class FakeView : IResourceCollectionView
        {
            public readonly List<WorkflowHighlight> Highlights = new List<WorkflowHighlight>();
            public string Prompt = string.Empty;
            public string PaymentRouteId = string.Empty;
            public IReadOnlyList<int> PaymentRecipients = new List<int>();
            public int ClearHighlightsCount;
            public int RefreshFromStateCount;

            public void ShowPrompt(string message)
            {
                Prompt = message;
            }

            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights)
            {
                Highlights.Clear();
                if (highlights != null)
                {
                    Highlights.AddRange(highlights);
                }
            }

            public void ClearHighlights()
            {
                ClearHighlightsCount += 1;
                Highlights.Clear();
            }

            public void ShowRoutePaymentOptions(
                string routeId,
                int cost,
                IReadOnlyList<int> recipientPlayerIds)
            {
                PaymentRouteId = routeId;
                PaymentRecipients = recipientPlayerIds;
            }

            public string GetPlayerDisplayName(int playerId)
            {
                return "\u73a9\u5bb6" + playerId;
            }

            public void RefreshSelectionView()
            {
            }

            public void RefreshFromState()
            {
                RefreshFromStateCount += 1;
            }

            public bool HasHighlight(string targetId, WorkflowHighlightSemantic semantic)
            {
                for (var i = 0; i < Highlights.Count; i++)
                {
                    if (Highlights[i].TargetId == targetId && Highlights[i].Semantic == semantic)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
