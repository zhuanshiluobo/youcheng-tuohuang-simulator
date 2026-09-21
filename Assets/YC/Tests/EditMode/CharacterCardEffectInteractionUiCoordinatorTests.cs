using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class CharacterCardEffectInteractionUiCoordinatorTests
    {
        private GameObject canvasObject;

        [Test]
        public void ReplacementChoice_MapsStableInfluenceToSlotAndOnlySubmitsAnswer()
        {
            var state = CreateState();
            state.Map.Influences.Add(new InfluencePlacement { InfluenceId = "opponent", PlayerId = 2, SlotId = "location:A-02:0" });
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "replace-ui", InteractionTypeId = "lua.choice", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 1,
                CandidateIds = new List<string> { "opponent" }, MinSelections = 1, MaxSelections = 1,
                PromptKey = "effect.influence.replace.target"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var type = Type.GetType("YC.Presentation.MapEffectInteractionUiCoordinator, Assembly-CSharp", true);
            string prompt = "";
            var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type,
                new object[] { new Func<GameState>(() => state), new Func<int>(() => 1), fixture.Dialog,
                    new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                    new Action<YC.Domain.Commands.GameCommand>(command => commands.Add(command)), new Action<string>(value => prompt = value) });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            Assert.That(prompt, Does.Contain("替换"));
            type.GetMethod("OnInfluenceSlotClicked").Invoke(renderer, new object[] { "location:A-02:0" });
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "opponent" }));
            Assert.That(state.Map.Influences.Exists(i => i.InfluenceId == "opponent"), Is.True);
            renderer.Clear();
        }

        [Test]
        public void ZeroSale_ExistingQuantityDialogSubmitsWithoutChangingResources()
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "zero-sale", InteractionTypeId = "character.ability.choice.awaiting_trade", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 1,
                CandidateIds = new List<string> { "sale|originium|0", "sale|originium|1" },
                MinSelections = 1, MaxSelections = 4, PromptKey = "character.cannot.strategy.choose_sale"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var type = Type.GetType("YC.Presentation.CharacterAbilityInteractionUiCoordinator, Assembly-CSharp", true);
            var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type,
                new object[] { new Func<GameState>(() => state), new Func<int>(() => 1), fixture.Dialog,
                    new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                    new Action<YC.Domain.Commands.GameCommand>(command => commands.Add(command)), new Action<string>(_ => { }) });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            int before = state.FindPlayer(1).Resources.Originium;
            ClickButton(GetOverlay(fixture.Dialog), "Confirm Character Effect");
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "sale|originium|0" }));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(before));
            renderer.Clear();
        }

        [TestCase("continue", 0)]
        [TestCase("decline", 1)]
        public void OptionalEffect_UsesExistingDialogAndOnlySubmitsDecision(string choice, int button)
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "optional-test", InteractionTypeId = "action.decline_effect", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 3, AllowDecline = true,
                CandidateIds = new List<string> { "continue", "decline" }, MinSelections = 1, MaxSelections = 1,
                PromptKey = "character.tin_man.purchase.12"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            string prompt = "";
            var type = Type.GetType("YC.Presentation.MapEffectInteractionUiCoordinator, Assembly-CSharp", true);
            var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type,
                new object[] { new Func<GameState>(() => state), new Func<int>(() => 1), fixture.Dialog,
                    new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                    new Action<YC.Domain.Commands.GameCommand>(command => commands.Add(command)), new Action<string>(text => prompt = text) });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            Assert.That(prompt, Does.Contain("12 金券"));
            var before = state.FindPlayer(1).Resources.GoldVoucher;
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option " + button);
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { choice }));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before));
            renderer.Clear();
        }

        [TestCase(1)]
        [TestCase(2)]
        public void GenericChoice_RendersExistingDialogAndSubmitsOnlyConfirmedCandidates(int maxSelections)
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "lua.choice.test", InteractionTypeId = "lua.choice", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 8,
                CandidateIds = new List<string> { "originium", "iron" }, MinSelections = 1, MaxSelections = maxSelections,
                PromptKey = "character.elysium.strategy.choose_resource"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var type = Type.GetType("YC.Presentation.CharacterAbilityInteractionUiCoordinator, Assembly-CSharp", true);
            var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type,
                new object[] { new Func<GameState>(() => state), new Func<int>(() => 1), fixture.Dialog,
                    new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                    new Action<YC.Domain.Commands.GameCommand>(command => commands.Add(command)), new Action<string>(_ => { }) });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            var before = state.FindPlayer(1).Resources.Originium;
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 0");
            if (maxSelections == 2)
            {
                Assert.That(commands, Is.Empty, "多选草稿不能提前提交。");
                ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 1");
                Assert.That(commands, Is.Empty);
                ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 2");
            }
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(maxSelections == 1 ? new[] { "originium" } : new[] { "originium", "iron" }));
            Assert.That(commands[0].Parameters[YC.Application.Interactions.AnswerInteractionCommandHandler.ExpectedRevisionParameter], Is.EqualTo("8"));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(before), "UI 不能修改权威资源。");
            renderer.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            if (canvasObject != null)
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
                canvasObject = null;
            }
        }

        [TestCase(CharacterCardEffectKind.CannotRequisition, "tactic")]
        [TestCase(CharacterCardEffectKind.CannotTradeChannel, "strategy")]
        [TestCase(CharacterCardEffectKind.ElysiumLogistics, "strategy")]
        [TestCase(CharacterCardEffectKind.ElysiumNavigation, "tactic")]
        [TestCase(CharacterCardEffectKind.TexasSpecialDelivery, "strategy")]
        [TestCase(CharacterCardEffectKind.TexasRemoveAndDoubleMove, "tactic")]
        [TestCase(CharacterCardEffectKind.LiskarmSecurityProtocol, "strategy")]
        [TestCase(CharacterCardEffectKind.LiskarmControlPosition, "tactic")]
        [TestCase(CharacterCardEffectKind.TinManEstablishPrestige, "strategy")]
        [TestCase(CharacterCardEffectKind.TinManDeepPlanning, "tactic")]
        public void AllCharacterEntrances_SubmitOnceWithoutLegacyPreselection(CharacterCardEffectKind effect, string mode)
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state, mapEffectResult: true);
            Assert.That(BeginEffect(fixture.Coordinator, mode, effect), Is.True);
            Assert.That(fixture.SubmissionCount, Is.EqualTo(1));
            Assert.That(fixture.SubmittedMode, Is.EqualTo(mode));
            Assert.That(fixture.SubmittedEffect.Count, Is.EqualTo(1));
            Assert.That(fixture.SubmittedEffect[CharacterEffectParameterKeys.OfferSecondEffect], Is.EqualTo("true"));
            Assert.That(fixture.MapEffectBegun, Is.False);
            Assert.That(fixture.FacilitySelectionBegun, Is.False);
            Assert.That(GetOverlay(fixture.Dialog), Is.Null, "选项只能由内核 Interaction 创建。");
        }

        [Test]
        public void UnifiedInteraction_FacilitySelectionIsActiveAndEscapeCancelsIt()
        {
            var state = CreateState();
            var effectFixture = CreateCoordinator(state);
            var mapCoordinator = new YC.Presentation.CharacterMapInteractionCoordinator(
                () => state,
                () => 1,
                new CharacterCardPanelPresenter(),
                _ => { },
                () => { },
                (_, __) => { },
                _ => { },
                _ => { });
            var facilitySelectionActive = true;
            var cancellationCount = 0;
            var interactionType = Type.GetType(
                "YC.Presentation.CharacterCardInteraction, Assembly-CSharp",
                false);
            Assert.That(interactionType, Is.Not.Null);
            var constructors = interactionType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(constructors, Has.Length.EqualTo(1));
            var interaction = constructors[0].Invoke(new object[]
            {
                effectFixture.Coordinator,
                mapCoordinator,
                new Func<bool>(() => false),
                new Func<bool>(() => facilitySelectionActive),
                new Func<bool>(() =>
                {
                    cancellationCount += 1;
                    facilitySelectionActive = false;
                    return true;
                })
            });

            Assert.That(
                (bool)interactionType.GetProperty("IsActive").GetValue(interaction, null),
                Is.True);

            var result = (InteractionResult)interactionType
                .GetMethod("OnEscape")
                .Invoke(interaction, null);

            Assert.That(result, Is.SameAs(InteractionResult.Consumed));
            Assert.That(cancellationCount, Is.EqualTo(1));
            Assert.That(
                (bool)interactionType.GetProperty("IsActive").GetValue(interaction, null),
                Is.False);
        }

        [Test]
        public void ResourceSaleDialog_KeepsQuantityPricesAndTitleDragging()
        {
            var state = CreateState();
            state.FindPlayer(1).Resources.Originium = 2;
            state.FindPlayer(1).Resources.Iron = 1;
            var fixture = CreateCoordinator(state);

            IReadOnlyList<int> saleValues = null;
            fixture.Dialog.GetType().GetMethod("ShowResourceSale").Invoke(fixture.Dialog, new object[]
            {
                new[] { "源岩", "源石碎片", "异铁", "至纯源石" }, new[] { 2, 0, 1, 0 },
                new[] { 3, 3, 4, 15 }, new Action<IReadOnlyList<int>>(values => saleValues = values), null
            });
            var overlay = GetOverlay(fixture.Dialog);
            var panel = FindChild(overlay, "Character Card Effect Panel").GetComponent<RectTransform>();
            var title = FindChild(overlay, "Title");
            var dragHandleType = Type.GetType(
                "YC.Presentation.EffectDialogDragHandle, Assembly-CSharp",
                false);
            Assert.That(dragHandleType, Is.Not.Null);
            Assert.That(title.GetComponent(dragHandleType), Is.Not.Null);
            DragDialogTitle(title, panel, new Vector2(180f, 0f));
            Assert.That(panel.anchoredPosition.x, Is.GreaterThan(100f));
            ClickButton(overlay, "Character Sale Increase 0");
            ClickButton(overlay, "Character Sale Increase 2");
            Assert.That(GetText(overlay, "Character Sale Summary"), Is.EqualTo("预计获得 7 金券"));
            ClickButton(overlay, "Confirm Character Effect");

            Assert.That(saleValues, Is.EqualTo(new[] { 1, 0, 1, 0 }));
        }

        [Test]
        public void TinManStrategy_StartsAuthoritativeProgressiveSettlement()
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);

            Assert.That(BeginEffect(
                fixture.Coordinator,
                UseCharacterCardCommandHandler.Strategy,
                CharacterCardEffectKind.TinManEstablishPrestige), Is.True);

            Assert.That(fixture.SubmissionCount, Is.EqualTo(1));
            Assert.That(fixture.SubmittedMode, Is.EqualTo(UseCharacterCardCommandHandler.Strategy));
            Assert.That(
                fixture.SubmittedEffect.ContainsKey(
                    CharacterEffectParameterKeys.TinManPurchasePureOriginium12),
                Is.False);
            Assert.That(
                fixture.SubmittedEffect.ContainsKey(
                    CharacterEffectParameterKeys.TinManPurchasePureOriginium15),
                Is.False);
            Assert.That(fixture.SubmittedEffect[CharacterEffectParameterKeys.OfferSecondEffect],
                Is.EqualTo("true"));
            Assert.That(GetOverlay(fixture.Dialog), Is.Null);
        }

        [Test]
        public void TinManFirstPurchase_CancelSubmitsFinishChoice()
        {
            var state = CreateState();
            state.PendingCharacterEffect = TinManPurchasePending(
                CharacterPendingChoiceTypes.TinManFirstPurchase,
                CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);
            var fixture = CreateCoordinator(state);

            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(GetText(overlay, "Character Effect Title"), Does.Contain("第一步"));
            Assert.That(FindChild(overlay, "Character Effect Option 0"), Is.Not.Null);
            ClickButton(overlay, "Cancel Character Effect");

            Assert.That(
                fixture.SubmittedPending[CharacterEffectParameterKeys.Choice],
                Is.EqualTo(CharacterEffectChoiceIds.TinManFinishPurchasing));
            Assert.That(
                fixture.SubmittedPending[
                    CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                Is.EqualTo(state.PendingCharacterEffect.SourceCommandId));
        }

        [Test]
        public void TinManFirstPurchase_PaySubmitsOnlyCurrentStepChoice()
        {
            var state = CreateState();
            state.PendingCharacterEffect = TinManPurchasePending(
                CharacterPendingChoiceTypes.TinManFirstPurchase,
                CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);
            var fixture = CreateCoordinator(state);

            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 0");

            Assert.That(
                fixture.SubmittedPending[CharacterEffectParameterKeys.Choice],
                Is.EqualTo(CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium));
            Assert.That(
                fixture.SubmittedPending[
                    CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                Is.EqualTo(state.PendingCharacterEffect.SourceCommandId));
        }

        [Test]
        public void TinManSecondPurchase_UsesDistinctChoiceAndInsufficientStateOnlyAllowsCancel()
        {
            var state = CreateState();
            state.PendingCharacterEffect = TinManPurchasePending(
                CharacterPendingChoiceTypes.TinManSecondPurchase,
                CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium);
            state.PendingCharacterEffect.TinManPurchasePureOriginium12 = true;
            var fixture = CreateCoordinator(state);

            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(GetText(overlay, "Character Effect Title"), Does.Contain("第二步"));
            ClickButton(overlay, "Character Effect Option 0");
            Assert.That(
                fixture.SubmittedPending[CharacterEffectParameterKeys.Choice],
                Is.EqualTo(CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium));
            Assert.That(
                fixture.SubmittedPending[
                    CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                Is.EqualTo(state.PendingCharacterEffect.SourceCommandId));

            state.PendingCharacterEffect = TinManPurchasePending(
                CharacterPendingChoiceTypes.TinManSecondPurchase,
                null);
            state.PendingCharacterEffect.TinManPurchasePureOriginium12 = true;
            fixture.SubmittedPending = null;
            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            overlay = GetOverlay(fixture.Dialog);
            Assert.That(FindChild(overlay, "Character Effect Option 0"), Is.Null);
            ClickButton(overlay, "Cancel Character Effect");
            Assert.That(
                fixture.SubmittedPending[CharacterEffectParameterKeys.Choice],
                Is.EqualTo(CharacterEffectChoiceIds.TinManFinishPurchasing));
            Assert.That(
                fixture.SubmittedPending[
                    CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                Is.EqualTo(state.PendingCharacterEffect.SourceCommandId));
        }

        [Test]
        public void TinManPendingMove_UsesDialogForBranchThenTransfersToMap()
        {
            var state = CreateState();
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var sourceLocation = map.Locations[0];
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                LocationId = sourceLocation.LocationId,
                SlotId = InfluenceService.GetLocationSlotId(sourceLocation.LocationId, 0)
            });
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.TinManDiscard,
                PlayerId = 1,
                CardId = "character.red.p1.tin-man",
                SourceCommandId = "tin-man-test",
                RemainingCardIds = { "character.red.p1.liskarm" },
                OptionIds = { CharacterEffectChoiceIds.GainGold, CharacterEffectChoiceIds.MoveInfluence }
            };
            var fixture = CreateCoordinator(state, pendingMapResult: true);

            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(GetText(overlay, "Character Effect Description"), Does.Contain("雷蛇"));
            ClickButton(overlay, "Character Effect Option 1");

            Assert.That(fixture.PendingMapBegun, Is.True);
            Assert.That(fixture.SubmittedPending, Is.Null);
            Assert.That(GetOverlay(fixture.Dialog), Is.Null);
        }

        private CoordinatorFixture CreateCoordinator(
            GameState state,
            bool mapEffectResult = false,
            bool pendingMapResult = false)
        {
            var coordinatorType = Type.GetType(
                "YC.Presentation.CharacterCardEffectInteractionUiCoordinator, Assembly-CSharp",
                false);
            var dialogType = Type.GetType(
                "YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp",
                false);
            var registryType = Type.GetType(
                "YC.Presentation.GameplayDialogRegistry, Assembly-CSharp",
                false);
            var effectViewType = Type.GetType(
                "YC.Presentation.EffectDialogShellView, Assembly-CSharp",
                false);
            Assert.That(coordinatorType, Is.Not.Null);
            Assert.That(dialogType, Is.Not.Null);
            Assert.That(registryType, Is.Not.Null);
            Assert.That(effectViewType, Is.Not.Null);

            canvasObject = new GameObject(
                "Character Effect Test Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(1920f, 1080f);

            var hudPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            Assert.That(hudPrefab, Is.Not.Null);
            var registry = hudPrefab.GetComponentInChildren(registryType, true);
            Assert.That(registry, Is.Not.Null);
            var dialog = Activator.CreateInstance(
                dialogType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { registry, canvasObject.GetComponent<RectTransform>() },
                null);
            var presenter = new CharacterCardPanelPresenter();
            var fixture = new CoordinatorFixture(dialog);
            Func<GameState> getState = () => state;
            Func<int> getPlayerId = () => 1;
            Func<string, CharacterCardEffectKind, bool> beginMap = (mode, effect) =>
            {
                fixture.MapEffectBegun = mapEffectResult;
                fixture.MapEffect = effect;
                return mapEffectResult;
            };
            Func<bool> beginPendingMap = () =>
            {
                fixture.PendingMapBegun = pendingMapResult;
                return pendingMapResult;
            };
            Func<IReadOnlyList<string>, Action<string>, Action, bool> beginFacilitySelection =
                (facilityIds, select, cancel) =>
                {
                    fixture.FacilitySelectionBegun = true;
                    fixture.SelectableFacilityIds = facilityIds;
                    fixture.SelectFacility = select;
                    fixture.CancelFacilitySelection = cancel;
                    return true;
                };
            Action endFacilitySelection = () => fixture.FacilitySelectionEnded = true;
            Action<string, IReadOnlyDictionary<string, string>> submitEffect = (mode, parameters) =>
            {
                fixture.SubmissionCount += 1;
                fixture.SubmittedMode = mode;
                fixture.SubmittedEffect = parameters;
            };
            Action<IReadOnlyDictionary<string, string>> submitPending = parameters =>
                fixture.SubmittedPending = parameters;
            Action<string> setPrompt = prompt => fixture.LastPrompt = prompt;

            var constructors = coordinatorType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(constructors, Has.Length.EqualTo(1));
            fixture.Coordinator = constructors[0].Invoke(new object[]
            {
                getState,
                getPlayerId,
                presenter,
                dialog,
                beginPendingMap,
                endFacilitySelection,
                submitEffect,
                submitPending,
                setPrompt
            });
            return fixture;
        }

        private static void DragDialogTitle(
            GameObject title,
            RectTransform panel,
            Vector2 screenDelta)
        {
            var eventSystemObject = new GameObject("Character Effect Drag Test EventSystem", typeof(EventSystem));
            eventSystemObject.transform.SetParent(title.transform.root, false);
            var eventSystem = eventSystemObject.GetComponent<EventSystem>();
            var start = RectTransformUtility.WorldToScreenPoint(null, title.transform.position);
            var eventData = new PointerEventData(eventSystem)
            {
                button = PointerEventData.InputButton.Left,
                position = start
            };

            Assert.That(
                ExecuteEvents.Execute(title, eventData, ExecuteEvents.beginDragHandler),
                Is.True);
            eventData.position = start + screenDelta;
            Assert.That(
                ExecuteEvents.Execute(title, eventData, ExecuteEvents.dragHandler),
                Is.True);
            Assert.That(
                ExecuteEvents.Execute(title, eventData, ExecuteEvents.endDragHandler),
                Is.True);
            UnityEngine.Object.DestroyImmediate(eventSystemObject);
        }

        private static bool BeginEffect(object coordinator, string mode, CharacterCardEffectKind effect)
        {
            return (bool)coordinator.GetType()
                .GetMethod("TryBeginEffect", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(coordinator, new object[] { mode, effect });
        }

        private static bool SynchronizePending(object coordinator)
        {
            return (bool)coordinator.GetType()
                .GetMethod("SynchronizePending", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(coordinator, null);
        }

        private static GameObject GetOverlay(object dialog)
        {
            var shell = dialog.GetType()
                .GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(dialog);
            Assert.That(shell, Is.Not.Null);
            var view = shell.GetType()
                .GetField("view", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(shell) as Component;
            return view == null ? null : view.gameObject;
        }

        private static void ClickButton(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing button " + objectName + ".");
            child.GetComponent<Button>().onClick.Invoke();
        }

        private static string GetText(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing text " + objectName + ".");
            return child.GetComponent<Text>().text;
        }

        private static GameObject FindChild(GameObject root, string objectName)
        {
            if (root == null)
            {
                return null;
            }

            var children = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i] != null && children[i].name == objectName)
                {
                    return children[i].gameObject;
                }
            }

            return null;
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                StartPlayerId = 1,
                MapId = StaticMapDefinitions.FourPlayerMapId,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Resources = { GoldVoucher = 30 }
                    }
                }
            };
        }

        private static PendingCharacterEffectState TinManPurchasePending(
            string choiceType,
            string purchaseChoiceId)
        {
            var pending = new PendingCharacterEffectState
            {
                ChoiceType = choiceType,
                PlayerId = 1,
                CardId = "character.red.p1.tin-man",
                SourceCommandId = "tin-man-purchase-test"
            };
            pending.OptionIds.Add(CharacterEffectChoiceIds.TinManFinishPurchasing);
            if (!string.IsNullOrEmpty(purchaseChoiceId))
            {
                pending.OptionIds.Add(purchaseChoiceId);
            }

            return pending;
        }

        private sealed class CoordinatorFixture
        {
            public CoordinatorFixture(object dialog)
            {
                Dialog = dialog;
            }

            public object Coordinator { get; set; }
            public object Dialog { get; private set; }
            public bool MapEffectBegun { get; set; }
            public CharacterCardEffectKind MapEffect { get; set; }
            public bool PendingMapBegun { get; set; }
            public bool FacilitySelectionBegun { get; set; }
            public bool FacilitySelectionEnded { get; set; }
            public IReadOnlyList<string> SelectableFacilityIds { get; set; }
            public Action<string> SelectFacility { get; set; }
            public Action CancelFacilitySelection { get; set; }
            public string SubmittedMode { get; set; }
            public IReadOnlyDictionary<string, string> SubmittedEffect { get; set; }
            public int SubmissionCount { get; set; }
            public IReadOnlyDictionary<string, string> SubmittedPending { get; set; }
            public string LastPrompt { get; set; }
        }
    }
}
