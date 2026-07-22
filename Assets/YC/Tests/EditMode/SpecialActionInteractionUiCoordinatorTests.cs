using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class SpecialActionInteractionUiCoordinatorTests
    {
        private GameObject canvasObject;
        private object coordinator;

        [TearDown]
        public void TearDown()
        {
            if (coordinator != null)
            {
                Invoke(coordinator, "Dispose");
                coordinator = null;
            }

            if (canvasObject != null)
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
                canvasObject = null;
            }
        }

        [Test]
        public void CompositePayment_UsesExactAllocationAndHasNoAcceptedSessionCancel()
        {
            var state = CreateState(
                SpecialActionDatabase.CompositePowerSystem,
                SpecialActionPendingSteps.AwaitCompositePayment);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(), Is.True);
            var overlay = FindChild(canvasObject, "Special Action Choice Overlay");
            Assert.That(overlay, Is.Not.Null);
            Assert.That(GetText(overlay, "Title"), Does.Contain("复合动力系统"));
            Assert.That(GetText(overlay, "Description"), Does.Contain("固定支付 1 份源石碎片"));
            Assert.That(FindChild(overlay, "Confirm Special Action Payment"), Is.Not.Null);
            Assert.That(FindChild(overlay, "Skip"), Is.Null);
            Assert.That(FindChild(overlay, "Cancel"), Is.Null);
            Assert.That(FindChild(overlay, "Back"), Is.Null);

            Assert.That(GetText(overlay, "Value 0"), Is.EqualTo("2"));
            Assert.That(GetText(overlay, "Value 1"), Is.EqualTo("0"));
            ClickButton(overlay, "Increase 1");
            ClickButton(overlay, "Confirm Special Action Payment");

            Assert.That(fixture.SubmittedCommand, Is.Not.Null);
            Assert.That(
                fixture.SubmittedCommand.Parameters[UseSpecialActionCommandHandler.SessionIdParameter],
                Is.EqualTo("special-session"));
            Assert.That(
                fixture.SubmittedCommand.Parameters[UseSpecialActionCommandHandler.OriginiumAmountParameter],
                Is.EqualTo("2"));
            Assert.That(
                fixture.SubmittedCommand.Parameters[UseSpecialActionCommandHandler.IronAmountParameter],
                Is.EqualTo("1"));
            Assert.That(state.PendingSpecialAction, Is.Not.Null, "UI 不得清空 Host 已接受的会话。");
        }

        [Test]
        public void FreeMove_RestoresCollapsedPromptAndRerendersSecondSegment()
        {
            var state = CreateState(
                SpecialActionDatabase.EfficientMobileManagementSystem,
                SpecialActionPendingSteps.AwaitFreeMoveTarget);
            state.PendingSpecialAction.RemainingRepetitions = 2;
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(), Is.True);
            var firstOverlay = FindChild(canvasObject, "Special Action Choice Overlay");
            var firstPanel = FindChild(firstOverlay, "Special Action Choice Panel").GetComponent<RectTransform>();
            Assert.That(firstPanel.sizeDelta.y, Is.EqualTo(58f));
            Assert.That(FindChild(firstOverlay, "Special Action Expanded Content").activeSelf, Is.False);
            Assert.That(FindChild(firstOverlay, "Special Action Collapsed Summary").activeSelf, Is.True);
            Assert.That(FindChild(firstOverlay, "Back"), Is.Null);
            Assert.That(FindChild(firstOverlay, "Cancel"), Is.Null);
            Assert.That(fixture.Highlights.ConvertAll(item => item.TargetId), Does.Contain("B"));

            ClickButton(firstOverlay, "Special Action Collapse Toggle");
            Assert.That(firstPanel.sizeDelta.y, Is.EqualTo(260f));
            Assert.That(GetText(firstOverlay, "Description"), Does.Contain("第一段"));

            Assert.That(TryHandleLocationClicked("B"), Is.True);
            Assert.That(fixture.SubmittedCommand.TargetId, Is.EqualTo("B"));
            Assert.That(
                fixture.SubmittedCommand.Parameters[UseSpecialActionCommandHandler.TargetLocationIdParameter],
                Is.EqualTo("B"));
            Assert.That(state.PendingSpecialAction, Is.Not.Null);

            state.PendingSpecialAction.RemainingRepetitions = 1;
            Assert.That(Synchronize(), Is.True);
            var secondOverlay = FindChild(canvasObject, "Special Action Choice Overlay");
            Assert.That(secondOverlay, Is.Not.SameAs(firstOverlay));
            ClickButton(secondOverlay, "Special Action Collapse Toggle");
            Assert.That(GetText(secondOverlay, "Description"), Does.Contain("第二段"));
        }

        [Test]
        public void PendingMapSubmission_BlocksDuplicateClicksUntilMatchingCommandSettles()
        {
            var state = CreateState(
                SpecialActionDatabase.EfficientMobileManagementSystem,
                SpecialActionPendingSteps.AwaitFreeMoveTarget);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(), Is.True);
            Assert.That(TryHandleLocationClicked("B"), Is.True);
            var commandId = fixture.SubmittedCommand.CommandId;
            Assert.That(fixture.SubmissionCount, Is.EqualTo(1));

            Assert.That(TryHandleLocationClicked("B"), Is.True);
            Assert.That(fixture.SubmissionCount, Is.EqualTo(1));
            Assert.That(fixture.LastPrompt, Does.Contain("等待确认"));

            Invoke(coordinator, "NotifyCommandSettled", "another-command");
            Assert.That(TryHandleLocationClicked("B"), Is.True);
            Assert.That(fixture.SubmissionCount, Is.EqualTo(1));

            Invoke(coordinator, "NotifyCommandSettled", commandId);
            Assert.That(TryHandleLocationClicked("B"), Is.True);
            Assert.That(fixture.SubmissionCount, Is.EqualTo(2));
        }

        [Test]
        public void MoveEvent_YieldsToExistingEventFlowAndHidesSpecialPrompt()
        {
            var state = CreateState(
                SpecialActionDatabase.CompositePowerSystem,
                SpecialActionPendingSteps.AwaitFreeMoveTarget);
            CreateCoordinator(state);
            Assert.That(Synchronize(), Is.True);
            var overlay = FindChild(canvasObject, "Special Action Choice Overlay");
            Assert.That(overlay, Is.Not.Null);
            ClickButton(overlay, "Special Action Collapse Toggle");
            Assert.That(GetText(overlay, "Description"), Does.Contain("本次"));

            state.PendingSpecialAction.Step = SpecialActionPendingSteps.AwaitMoveEvent;
            state.PendingSpecialAction.RemainingRepetitions = 0;
            state.PendingSpecialAction.TraversedRouteId = "R";
            Assert.That(Synchronize(), Is.False);
            Assert.That(FindChild(canvasObject, "Special Action Choice Overlay"), Is.Null);
            Assert.That(TryHandleLocationClicked("B"), Is.False);
        }

        [Test]
        public void DomainInvalidPending_DoesNotRenderOrBlockMapInteraction()
        {
            var state = CreateState(
                SpecialActionDatabase.EfficientMobileManagementSystem,
                SpecialActionPendingSteps.AwaitFreeMoveTarget);
            state.FindPlayer(1).DeclaredCityStyles.Clear();
            CreateCoordinator(state);

            Assert.That(state.PendingSpecialAction.IsValid(), Is.True, "测试需要一个结构仍有效的旧会话。");
            Assert.That(state.PendingSpecialAction.IsValid(state), Is.False);
            Assert.That(Synchronize(), Is.False);
            Assert.That(FindChild(canvasObject, "Special Action Choice Overlay"), Is.Null);
            Assert.That(TryHandleLocationClicked("B"), Is.False);
        }

        [Test]
        public void StaleSpecialAction_WithMercenaryFacilityPending_YieldsToFacilitySession()
        {
            var state = CreateState(
                SpecialActionDatabase.MobilizationSupportSystem,
                SpecialActionPendingSteps.AwaitMobilizationTarget);
            state.FindPlayer(1).DeclaredCityStyles.Clear();
            var targetSlot = InfluenceService.GetLocationSlotId("B", 0);
            state.PendingCardSession = new PendingCardSessionState
            {
                SessionId = "mercenary-facility-session",
                ScenarioId = FacilityPendingChoiceTypes.ScenarioId,
                ChoiceType = FacilityPendingChoiceTypes.ReplaceOneInfluence,
                CardId = FacilityCardDatabase.MercenaryCommand,
                PlayerId = 1,
                OptionIds = { FacilityPendingChoiceTypes.ReplaceInfluenceOption }
            };
            CreateCoordinator(state);

            Assert.That(state.PendingSpecialAction.IsValid(), Is.True);
            Assert.That(state.PendingSpecialAction.IsValid(state), Is.False);
            Assert.That(state.PendingCardSession.IsValid(), Is.True);
            Assert.That(Synchronize(), Is.False);
            Assert.That(FindChild(canvasObject, "Special Action Choice Overlay"), Is.Null);
            Assert.That(TryHandleInfluenceSlotClicked(targetSlot), Is.False,
                "地图点击应继续交给设施效果协调器。");
        }

        [Test]
        public void MilitaryControl_HighlightsLegalSlotsAndSubmitsRequiredCount()
        {
            var state = CreateState(
                SpecialActionDatabase.MilitaryIndustrialArea,
                SpecialActionPendingSteps.AwaitMilitaryTargets);
            var player = state.FindPlayer(1);
            player.DeclaredCityStyles.Add(Declaration(CityStyleDatabase.MilitaryIndustrialArea, "military-2"));
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(), Is.True);
            var overlay = FindChild(canvasObject, "Special Action Choice Overlay");
            Assert.That(FindChild(overlay, "Special Action Expanded Content").activeSelf, Is.False);
            Assert.That(fixture.Highlights.Count, Is.GreaterThanOrEqualTo(2));
            var first = fixture.Highlights[0].TargetId;
            var second = fixture.Highlights[1].TargetId;

            Assert.That(TryHandleInfluenceSlotClicked(first), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Null);
            Assert.That(TryHandleInfluenceSlotClicked(second), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Not.Null);
            Assert.That(fixture.SubmittedCommand.OptionIds, Is.EquivalentTo(new[] { first, second }));
            Assert.That(
                fixture.SubmittedCommand.Parameters[UseSpecialActionCommandHandler.InfluenceSlotIdsParameter],
                Is.EqualTo(first + "," + second));
            Assert.That(state.PendingSpecialAction, Is.Not.Null);
        }

        [Test]
        public void MobilizationAndRoutePlacement_SubmitOnlyHighlightedInfluenceTargets()
        {
            var state = CreateState(
                SpecialActionDatabase.MobilizationSupportSystem,
                SpecialActionPendingSteps.AwaitMobilizationTarget);
            var opponentSlot = InfluenceService.GetLocationSlotId("B", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = opponentSlot,
                LocationId = "B"
            });
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(), Is.True);
            Assert.That(fixture.Highlights.ConvertAll(item => item.TargetId), Is.EqualTo(new[] { opponentSlot }));
            Assert.That(TryHandleInfluenceSlotClicked(opponentSlot), Is.True);
            Assert.That(
                fixture.SubmittedCommand.Parameters[UseSpecialActionCommandHandler.TargetInfluenceSlotIdParameter],
                Is.EqualTo(opponentSlot));

            state.PendingSpecialAction = Pending(
                SpecialActionDatabase.CompositePowerSystem,
                SpecialActionPendingSteps.AwaitRouteInfluence,
                "route-session");
            var player = state.FindPlayer(1);
            player.DeclaredCityStyles.Clear();
            player.DeclaredCityStyles.Add(ActivatedDeclaration(
                SpecialActionDatabase.Get(SpecialActionDatabase.CompositePowerSystem),
                state.PendingSpecialAction.DeclarationMarkerId));
            fixture.ClearSubmittedCommand();
            Assert.That(Synchronize(), Is.True);
            var routeSlot = InfluenceService.GetRouteSlotId("R", 0);
            Assert.That(fixture.Highlights.ConvertAll(item => item.TargetId), Does.Contain(routeSlot));
            Assert.That(TryHandleInfluenceSlotClicked(routeSlot), Is.True);
            Assert.That(
                fixture.SubmittedCommand.Parameters[UseSpecialActionCommandHandler.RouteInfluenceSlotIdParameter],
                Is.EqualTo(routeSlot));
        }

        [Test]
        public void CompletedSourceIndustrialAction_HasNoPendingPopup()
        {
            var state = CreateState(
                SpecialActionDatabase.EfficientMobileManagementSystem,
                SpecialActionPendingSteps.AwaitFreeMoveTarget);
            CreateCoordinator(state);
            Assert.That(Synchronize(), Is.True);
            Assert.That(FindChild(canvasObject, "Special Action Choice Overlay"), Is.Not.Null);

            state.PendingSpecialAction = null;
            Assert.That(Synchronize(), Is.False);
            Assert.That(FindChild(canvasObject, "Special Action Choice Overlay"), Is.Null);
        }

        private Fixture CreateCoordinator(GameState state)
        {
            canvasObject = new GameObject("Special Action Test Canvas", typeof(RectTransform), typeof(Canvas));
            var mapQuery = new MapQueryService(CreateMap());
            var influenceService = new InfluenceService(mapQuery);
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                new TravelCostService(mapQuery));
            var optionQuery = new SpecialActionOptionQueryService(
                mapQuery,
                influenceService,
                movementService,
                new SpecialActionLifecycleService());
            var fixture = new Fixture();
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights = values =>
            {
                fixture.Highlights.Clear();
                if (values != null) fixture.Highlights.AddRange(values);
            };
            Action clearHighlights = () => fixture.Highlights.Clear();
            Action<GameCommand> submit = command =>
            {
                fixture.SubmittedCommand = command;
                fixture.SubmissionCount += 1;
            };
            Action<string> setPrompt = prompt => fixture.LastPrompt = prompt;
            var type = Type.GetType(
                "YC.Presentation.SpecialActionInteractionUiCoordinator, Assembly-CSharp",
                false);
            Assert.That(type, Is.Not.Null);
            coordinator = Activator.CreateInstance(
                type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[]
                {
                    new Func<GameState>(() => state),
                    new Func<int>(() => 1),
                    new Func<RectTransform>(() => canvasObject.GetComponent<RectTransform>()),
                    optionQuery,
                    setHighlights,
                    clearHighlights,
                    submit,
                    setPrompt
                },
                null);
            Assert.That(coordinator, Is.Not.Null);
            fixture.Coordinator = coordinator;
            return fixture;
        }

        private bool Synchronize()
        {
            return (bool)Invoke(coordinator, "Synchronize");
        }

        private bool TryHandleLocationClicked(string locationId)
        {
            return (bool)Invoke(coordinator, "TryHandleLocationClicked", locationId);
        }

        private bool TryHandleInfluenceSlotClicked(string slotId)
        {
            return (bool)Invoke(coordinator, "TryHandleInfluenceSlotClicked", slotId);
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(target, arguments);
        }

        private static GameObject FindChild(GameObject root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name == name) return transforms[i].gameObject;
            }

            return null;
        }

        private static string GetText(GameObject root, string name)
        {
            var child = FindChild(root, name);
            Assert.That(child, Is.Not.Null, "Missing text: " + name);
            return child.GetComponent<Text>().text;
        }

        private static void ClickButton(GameObject root, string name)
        {
            var child = FindChild(root, name);
            Assert.That(child, Is.Not.Null, "Missing button: " + name);
            child.GetComponent<Button>().onClick.Invoke();
        }

        private static GameState CreateState(string specialActionId, string step)
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
                        CityLocationId = "A",
                        InfluenceSupply = 8,
                        Resources = new ResourceSet
                        {
                            Originium = 2,
                            OriginiumShard = 4,
                            Iron = 2,
                            GoldVoucher = 10
                        }
                    },
                    new PlayerState { PlayerId = 2, InfluenceSupply = 8 }
                },
                PendingSpecialAction = Pending(specialActionId, step, "special-session")
            };
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "B",
                ResourceType = ResourceType.Originium,
                Amount = 1
            });
            state.Map.OpenLocationIds.Add("A");
            state.Map.OpenLocationIds.Add("B");
            var definition = SpecialActionDatabase.Get(specialActionId);
            Assert.That(definition, Is.Not.Null);
            state.FindPlayer(1).DeclaredCityStyles.Add(ActivatedDeclaration(
                definition,
                state.PendingSpecialAction.DeclarationMarkerId));
            return state;
        }

        private static PendingSpecialActionState Pending(string specialActionId, string step, string sessionId)
        {
            var definition = SpecialActionDatabase.Get(specialActionId);
            Assert.That(definition, Is.Not.Null);
            var pending = new PendingSpecialActionState
            {
                SessionId = sessionId,
                PlayerId = 1,
                SpecialActionId = specialActionId,
                DeclarationMarkerId = "marker-1",
                Step = step
            };

            if (step == SpecialActionPendingSteps.AwaitFreeMoveTarget)
            {
                pending.RemainingRepetitions =
                    definition.EffectKind == SpecialActionEffectKind.CompositePowerMove
                        ? 1
                        : definition.FreeMoveCount;
            }
            else if (step == SpecialActionPendingSteps.AwaitMoveEvent)
            {
                pending.RemainingRepetitions =
                    definition.EffectKind == SpecialActionEffectKind.CompositePowerMove
                        ? 0
                        : Math.Max(0, definition.FreeMoveCount - 1);
                pending.TraversedRouteId = "R";
            }
            else if (step == SpecialActionPendingSteps.AwaitRouteInfluence)
            {
                pending.TraversedRouteId = "R";
            }

            if (definition.EffectKind == SpecialActionEffectKind.CompositePowerMove &&
                step != SpecialActionPendingSteps.AwaitCompositePayment)
            {
                pending.PaidOriginium = 2;
                pending.PaidOriginiumShard = 1;
                pending.PaidIron = 1;
            }

            return pending;
        }

        private static CityStyleDeclarationState ActivatedDeclaration(
            SpecialActionDefinition definition,
            string markerId)
        {
            Assert.That(definition, Is.Not.Null);
            return new CityStyleDeclarationState
            {
                CityStyleId = definition.CityStyleId,
                InfluenceMarkerId = markerId,
                UnlockedSpecialActionId = definition.SpecialActionId,
                MarkerArea = definition.Level >= 2
                    ? SpecialActionMarkerAreas.UsedFromTwo
                    : CityStyleMarkerAreas.Used,
                RemainingSpecialActionUses = definition.Level >= 2 ? 2 : 0
            };
        }

        private static CityStyleDeclarationState Declaration(string cityStyleId, string markerId)
        {
            return new CityStyleDeclarationState
            {
                CityStyleId = cityStyleId,
                InfluenceMarkerId = markerId
            };
        }

        private static GameMapDefinition CreateMap()
        {
            var map = new GameMapDefinition { MapId = "special-action-ui-test", MinPlayers = 2, MaxPlayers = 4 };
            map.Locations.Add(new MapLocationDefinition
            {
                LocationId = "A",
                CanDockCity = true,
                InfluenceSlotCount = 3
            });
            map.Locations.Add(new MapLocationDefinition
            {
                LocationId = "B",
                CanDockCity = true,
                InfluenceSlotCount = 2
            });
            map.Routes.Add(new MapRouteDefinition
            {
                RouteId = "R",
                FromLocationId = "A",
                ToLocationId = "B",
                InfluenceSlotCount = 2
            });
            return map;
        }

        private sealed class Fixture
        {
            public object Coordinator;
            public GameCommand SubmittedCommand;
            public int SubmissionCount;
            public string LastPrompt = string.Empty;
            public readonly List<WorkflowHighlight> Highlights = new List<WorkflowHighlight>();

            public void ClearSubmittedCommand()
            {
                SubmittedCommand = null;
            }
        }
    }
}
