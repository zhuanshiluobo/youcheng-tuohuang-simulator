using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class CharacterHandPanelUiTests
    {
        private const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/CharacterHandPanel.prefab";
        private const string CatalogPath =
            "Assets/YC/Presentation/Content/CardVisualCatalog.asset";

        private static readonly string[] CardIds =
        {
            "character.red.p1.liskarm",
            "character.red.p1.texas",
            "character.red.p1.cannot"
        };

        private GameObject owner;
        private PanelFacade panel;

        [SetUp]
        public void SetUp()
        {
            ViewerPrefabTestUtility.RegisterZoomablePrefab();
        }

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                Object.DestroyImmediate(owner);
                owner = null;
            }

            var viewers = Resources.FindObjectsOfTypeAll(GetRuntimeType(
                "YC.Presentation.ZoomableImageViewerController"));
            for (var i = 0; i < viewers.Length; i++)
            {
                var component = viewers[i] as Component;
                if (component != null && component.gameObject.scene.IsValid())
                {
                    Object.DestroyImmediate(component.gameObject);
                }
            }
        }

        [Test]
        public void Prefab_HasLockedProfileTemplatesGrayscaleMaterialAndModalBlocker()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null);
            var prefabPanel = new PanelFacade(prefab.GetComponent(GetRuntimeType(
                "YC.Presentation.CharacterHandPanel")));
            Assert.That(prefabPanel, Is.Not.Null);
            Assert.That(prefabPanel.TryValidateConfiguration(out var reason), Is.True, reason);
            Assert.That(prefabPanel.LayoutProfile.PassiveVisibleFraction, Is.EqualTo(0.3f).Within(0.001f));
            Assert.That(prefabPanel.LayoutProfile.PassiveAlpha, Is.EqualTo(0.38f).Within(0.001f));
            Assert.That(prefabPanel.LayoutProfile.HoverScale, Is.EqualTo(1.18f).Within(0.001f));
            Assert.That(prefabPanel.LayoutProfile.DiscardButtonPosition,
                Is.EqualTo(new Vector2(-372f, 12f)));
            Assert.That(prefabPanel.View.DiscardGrayscaleMaterial.shader.name, Is.EqualTo("YC/UI Grayscale"));
            Assert.That(prefabPanel.View.DiscardOverlayObject.GetComponent<Image>().raycastTarget, Is.True);
            Assert.That(prefabPanel.View.DiscardOverlayObject.activeSelf, Is.False);
            Assert.That(prefabPanel.View.HandCardTemplate.gameObject.activeSelf, Is.False);
            Assert.That(prefabPanel.View.OverlayCardTemplate.gameObject.activeSelf, Is.False);
            Assert.That(prefabPanel.View.DragGhostTemplate.gameObject.activeSelf, Is.False);
            var hoverHitArea = prefabPanel.View.HandCardTemplate.transform.Find("Hover Hit Area");
            Assert.That(hoverHitArea, Is.Not.Null);
            Assert.That(hoverHitArea.GetComponent<Image>().raycastTarget, Is.True);
            Assert.That(((RectTransform)hoverHitArea).offsetMax.y,
                Is.EqualTo(97.25f).Within(0.01f));
            Assert.That(prefabPanel.View.HandCardsAnimation, Is.Not.Null);
            Assert.That(prefabPanel.View.HandCardsAnimation.clip.name,
                Is.EqualTo("CharacterHandReveal"));
            Assert.That(prefabPanel.View.HandCardsAnimation.GetClip("CharacterHandReturn"),
                Is.Not.Null);
            Assert.That(prefabPanel.View.HandCardsAnimation.playAutomatically, Is.False);
        }

        [Test]
        public void PassiveAnimatorAndCoverLayouts_ExposeRaiseAndExpandUsingProfile()
        {
            CreatePanel();
            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), false));
            var middle = FindHandCard(CardIds[1]);
            var profile = panel.LayoutProfile;

            Assert.That(middle.Root.anchorMin, Is.EqualTo(Vector2.right * 0.5f));
            Assert.That(
                middle.Root.anchoredPosition.y + profile.CardSize.y * 0.5f,
                Is.EqualTo(profile.CardSize.y * profile.PassiveVisibleFraction).Within(0.01f));
            Assert.That(middle.CanvasGroup.alpha, Is.EqualTo(profile.PassiveAlpha).Within(0.001f));

            var button = middle.Root.GetComponent<Button>();
            var animator = middle.Root.GetComponent<Animator>();
            Assert.That(button.transition, Is.EqualTo(Selectable.Transition.Animation));
            Assert.That(animator, Is.Not.Null);
            Assert.That(animator.updateMode, Is.EqualTo(AnimatorUpdateMode.UnscaledTime));
            Assert.That(middle.Image.rectTransform.parent, Is.EqualTo(middle.Root));

            var controller = animator.runtimeAnimatorController as AnimatorController;
            Assert.That(controller, Is.Not.Null);
            var highlightedState = Array.Find(
                controller.layers[0].stateMachine.states,
                state => state.state.name == "Highlighted").state;
            Assert.That(highlightedState, Is.Not.Null);
            var highlightedClip = highlightedState.motion as AnimationClip;
            Assert.That(highlightedClip, Is.Not.Null);
            var yBinding = Array.Find(
                AnimationUtility.GetCurveBindings(highlightedClip),
                binding => binding.path == "Preview Visual" &&
                           binding.propertyName == "m_AnchoredPosition.y");
            var yCurve = AnimationUtility.GetEditorCurve(highlightedClip, yBinding);
            var expectedRaise = (profile.CardSize.y * (1f - profile.PassiveVisibleFraction) +
                                 profile.HoverBottom) * 0.5f;
            Assert.That(yCurve.keys[0].value, Is.EqualTo(0f).Within(0.01f));
            Assert.That(yCurve.keys[yCurve.length - 1].value,
                Is.EqualTo(expectedRaise).Within(0.01f));
            Assert.That(yCurve.keys[0].outTangent,
                Is.EqualTo(expectedRaise * 3f / 0.2f).Within(0.01f));
            Assert.That(yCurve.keys[yCurve.length - 1].inTangent,
                Is.EqualTo(0f).Within(0.01f));
            Assert.That(yCurve.Evaluate(0.1f),
                Is.GreaterThan(expectedRaise * 0.85f),
                "卡片移动到半程时应已完成大部分距离，留下缓慢落点段。");

            var stateMachine = controller.layers[0].stateMachine;
            var reextractTransition = Array.Find(
                stateMachine.anyStateTransitions,
                transition => transition.destinationState == highlightedState);
            Assert.That(reextractTransition, Is.Not.Null);
            Assert.That(reextractTransition.offset, Is.EqualTo(1f).Within(0.001f));
            Assert.That(reextractTransition.hasFixedDuration, Is.True);
            Assert.That(reextractTransition.duration, Is.EqualTo(0.1f).Within(0.001f));
            Assert.That(reextractTransition.canTransitionToSelf, Is.True);
            Assert.That(reextractTransition.interruptionSource,
                Is.EqualTo(TransitionInterruptionSource.SourceThenDestination));
            Assert.That(reextractTransition.orderedInterruption, Is.False);

            var previewVisual = middle.Root.Find("Preview Visual") as RectTransform;
            var pointer = CreatePointer(ScreenPoint(middle.Root));
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.Play("Normal", 0, 0f);
            animator.Update(0f);
            button.OnPointerEnter(pointer);
            animator.Update(0f);
            animator.Update(0.2f);
            var fullyRaisedY = previewVisual.anchoredPosition.y;
            button.OnPointerExit(pointer);
            animator.Update(0f);
            animator.Update(0.05f);
            var retractingY = previewVisual.anchoredPosition.y;
            button.OnPointerEnter(pointer);
            animator.Update(0f);
            animator.Update(0.05f);
            var reextractingY = previewVisual.anchoredPosition.y;
            Assert.That(fullyRaisedY, Is.EqualTo(expectedRaise).Within(0.5f));
            Assert.That(retractingY, Is.LessThan(fullyRaisedY));
            Assert.That(reextractingY, Is.GreaterThan(retractingY),
                "收回尚未结束时再次悬停，手卡必须立即反向重新抽出。");

            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), true));
            var handCardsAnimation = panel.View.HandCardsAnimation;
            var entranceStartY = panel.View.HandCardsRoot.anchoredPosition.y;
            var expectedEntranceDistance =
                profile.CardSize.y * 0.5f + profile.ExpandedBottom -
                profile.CardSize.y * (profile.PassiveVisibleFraction - 0.5f);
            Assert.That(entranceStartY,
                Is.EqualTo(-expectedEntranceDistance).Within(0.5f));
            handCardsAnimation[handCardsAnimation.clip.name].time = 0.14f;
            handCardsAnimation.Sample();
            Assert.That(panel.View.HandCardsRoot.anchoredPosition.y,
                Is.GreaterThan(entranceStartY + expectedEntranceDistance * 0.85f));
            Assert.That(panel.View.HandCardsRoot.anchoredPosition.y, Is.LessThan(0f));
            handCardsAnimation[handCardsAnimation.clip.name].time = 0.28f;
            handCardsAnimation.Sample();
            Assert.That(panel.View.HandCardsRoot.anchoredPosition.y,
                Is.EqualTo(0f).Within(0.5f));
            middle = FindHandCard(CardIds[1]);
            Assert.That(middle.CanvasGroup.alpha, Is.EqualTo(1f).Within(0.001f));
            Assert.That(
                middle.Root.anchoredPosition.y,
                Is.EqualTo(profile.CardSize.y * 0.5f + profile.ExpandedBottom).Within(0.01f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(
                    0f,
                    FindHandCard(CardIds[0]).Root.localEulerAngles.z)),
                Is.LessThanOrEqualTo(profile.ExpandedMaxAngle + 0.01f));

            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), false));
            var returnStartY = panel.View.HandCardsRoot.anchoredPosition.y;
            Assert.That(returnStartY,
                Is.EqualTo(expectedEntranceDistance).Within(0.5f));
            var returnAnimation = handCardsAnimation["CharacterHandReturn"];
            returnAnimation.time = 0.11f;
            handCardsAnimation.Sample();
            Assert.That(panel.View.HandCardsRoot.anchoredPosition.y,
                Is.LessThan(returnStartY * 0.15f));
            Assert.That(panel.View.HandCardsRoot.anchoredPosition.y, Is.GreaterThan(0f));
            returnAnimation.time = 0.22f;
            handCardsAnimation.Sample();
            Assert.That(panel.View.HandCardsRoot.anchoredPosition.y,
                Is.EqualTo(0f).Within(0.5f));
            Assert.That(FindHandCard(CardIds[1]).CanvasGroup.alpha,
                Is.EqualTo(profile.PassiveAlpha).Within(0.001f));
        }

        [Test]
        public void Reorder_IsLocalPerPlayerRestoresReturningCardsAndDoesNotMutateAuthorityOrder()
        {
            CreatePanel();
            var authorityOrder = new List<string>(CardIds);
            panel.Render(1, BuildModel(authorityOrder, Array.Empty<string>(), false));

            DragCardToHandIndex(CardIds[0], CardIds[2]);
            CollectionAssert.AreEqual(new[] { CardIds[1], CardIds[2], CardIds[0] }, OrderedIds());
            CollectionAssert.AreEqual(CardIds, authorityOrder, "UI 排序不得改写权威 HandCardIds 顺序。");
            var ghostReturn = Array.Find(
                owner.GetComponentsInChildren<Animation>(true),
                animation => animation.gameObject.name == "Character Hand Drag Ghost");
            Assert.That(ghostReturn, Is.Not.Null);
            Assert.That(ghostReturn.clip.name, Is.EqualTo("CharacterHandDragGhostReturn"));
            Assert.That(FindHandCard(CardIds[0]).CanvasGroup.alpha, Is.EqualTo(0f));
            var ghostState = ghostReturn[ghostReturn.clip.name];
            var ghostRect = ghostReturn.GetComponent<RectTransform>();
            var ghostStart = ghostRect.anchoredPosition;
            var expectedLandingPoint = (Vector2)panel.View.HandCardsRoot.parent
                .InverseTransformPoint(FindHandCard(CardIds[0]).Root.position);
            ghostState.time = ghostReturn.clip.length * 0.5f;
            ghostReturn.Sample();
            Assert.That(
                Vector2.Distance(ghostStart, ghostRect.anchoredPosition) /
                Vector2.Distance(ghostStart, expectedLandingPoint),
                Is.GreaterThan(0.85f));
            ghostState.time = ghostReturn.clip.length;
            ghostReturn.Sample();
            Assert.That(
                Vector2.Distance(
                    ghostRect.anchoredPosition,
                    expectedLandingPoint),
                Is.LessThan(0.5f));

            panel.Render(2, BuildModel(CardIds, Array.Empty<string>(), false));
            CollectionAssert.AreEqual(CardIds, OrderedIds(), "热座玩家必须使用彼此隔离的本地顺序。");
            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), false));
            CollectionAssert.AreEqual(new[] { CardIds[1], CardIds[2], CardIds[0] }, OrderedIds());

            panel.Render(1, BuildModel(
                new[] { CardIds[1], CardIds[2] },
                new[] { CardIds[0] },
                false));
            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), false));
            CollectionAssert.AreEqual(
                new[] { CardIds[1], CardIds[2], CardIds[0] },
                OrderedIds(),
                "弃置后返回手牌的卡应恢复原有相对顺序。");
        }

        [Test]
        public void InvalidOrCoverDrop_RestoresOrderAndDragSuppressesFollowingClick()
        {
            var began = string.Empty;
            var ended = string.Empty;
            CreatePanel(
                (id, _) => began = id,
                _ => { },
                (id, _) =>
                {
                    ended = id;
                    return null;
                });
            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), true));
            var card = FindHandCard(CardIds[0]);
            var pointer = CreatePointer(ScreenPoint(card.Root));
            ((IPointerDownHandler)card.PointerInteraction).OnPointerDown(pointer);
            ((IBeginDragHandler)card.PointerInteraction).OnBeginDrag(pointer);
            pointer.position = ScreenPoint(panel.View.HandDropArea) + Vector2.up * 500f;
            ((IDragHandler)card.PointerInteraction).OnDrag(pointer);
            ((IEndDragHandler)card.PointerInteraction).OnEndDrag(pointer);
            ((IPointerClickHandler)card.PointerInteraction).OnPointerClick(pointer);

            Assert.That(began, Is.EqualTo(CardIds[0]));
            Assert.That(ended, Is.EqualTo(CardIds[0]));
            CollectionAssert.AreEqual(CardIds, OrderedIds());
            Assert.That(HasOpenViewer(), Is.False,
                "拖动结束产生的点击必须被 CardPointerInteraction 抑制。");
        }

        [Test]
        public void CoverDrop_GhostLandsOnActionPanelPreviewInsteadOfReturningToHand()
        {
            RectTransform panelTarget = null;
            CreatePanel(
                null,
                null,
                (_, __) => panelTarget);
            panelTarget = new GameObject("Action Panel Card Target", typeof(RectTransform))
                .GetComponent<RectTransform>();
            panelTarget.SetParent(panel.View.Root, false);
            panelTarget.anchorMin = Vector2.one * 0.5f;
            panelTarget.anchorMax = Vector2.one * 0.5f;
            panelTarget.pivot = Vector2.one * 0.5f;
            panelTarget.anchoredPosition = new Vector2(650f, 230f);
            panelTarget.sizeDelta = new Vector2(118f, 188f);

            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), true));
            var card = FindHandCard(CardIds[0]);
            var pointer = CreatePointer(ScreenPoint(card.Root));
            ((IPointerDownHandler)card.PointerInteraction).OnPointerDown(pointer);
            ((IBeginDragHandler)card.PointerInteraction).OnBeginDrag(pointer);
            pointer.position = ScreenPoint(panelTarget) + Vector2.left * 180f;
            ((IDragHandler)card.PointerInteraction).OnDrag(pointer);
            ((IEndDragHandler)card.PointerInteraction).OnEndDrag(pointer);

            var ghostAnimation = Array.Find(
                owner.GetComponentsInChildren<Animation>(true),
                animation => animation.gameObject.name == "Character Hand Drag Ghost");
            Assert.That(ghostAnimation, Is.Not.Null);
            Assert.That(ghostAnimation.clip.name, Is.EqualTo("CharacterHandDragGhostPanel"));
            Assert.That(FindHandCard(CardIds[0]).CanvasGroup.alpha, Is.EqualTo(0f),
                "等待确认时，同一张角色牌不能同时显示在手牌区和行动面板。");
            Assert.That(FindHandCard(CardIds[0]).CanvasGroup.blocksRaycasts, Is.False);

            var ghostState = ghostAnimation[ghostAnimation.clip.name];
            var ghostRect = ghostAnimation.GetComponent<RectTransform>();
            var ghostStart = ghostRect.anchoredPosition;
            var expectedLandingPoint = (Vector2)panel.View.Root
                .InverseTransformPoint(panelTarget.position);
            ghostState.time = ghostAnimation.clip.length * 0.5f;
            ghostAnimation.Sample();
            Assert.That(
                Vector2.Distance(ghostStart, ghostRect.anchoredPosition) /
                Vector2.Distance(ghostStart, expectedLandingPoint),
                Is.GreaterThan(0.85f));
            ghostState.time = ghostAnimation.clip.length;
            ghostAnimation.Sample();
            Assert.That(Vector2.Distance(ghostRect.anchoredPosition, expectedLandingPoint),
                Is.LessThan(0.5f));
            Assert.That(ghostRect.sizeDelta.x, Is.EqualTo(panelTarget.rect.width).Within(0.5f));
            Assert.That(ghostRect.sizeDelta.y, Is.EqualTo(panelTarget.rect.height).Within(0.5f));

            panel.ResolvePendingCover(false);
            var cancelReturn = Array.Find(
                owner.GetComponentsInChildren<Animation>(true),
                animation => animation.gameObject.name == "Character Hand Drag Ghost");
            Assert.That(cancelReturn, Is.Not.Null);
            Assert.That(cancelReturn.clip.name, Is.EqualTo("CharacterHandDragGhostReturn"));
            Assert.That(FindHandCard(CardIds[0]).CanvasGroup.alpha, Is.EqualTo(0f),
                "取消返回动画结束前，手牌落点应继续隐藏。");
            var cancelState = cancelReturn[cancelReturn.clip.name];
            cancelState.time = cancelReturn.clip.length;
            cancelReturn.Sample();
            var cancelLandingPoint = (Vector2)panel.View.Root
                .InverseTransformPoint(FindHandCard(CardIds[0]).Root.position);
            Assert.That(Vector2.Distance(
                    cancelReturn.GetComponent<RectTransform>().anchoredPosition,
                    cancelLandingPoint),
                Is.LessThan(0.5f));
            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), true));
            Assert.That(FindHandCard(CardIds[0]).CanvasGroup.alpha, Is.EqualTo(1f));
        }

        [Test]
        public void BeginningSecondCoverDrag_RestoresFirstPendingCardBeforeCreatingNewGhost()
        {
            RectTransform panelTarget = null;
            var waitingForConfirmation = false;
            CreatePanel(
                (_, __) =>
                {
                    if (!waitingForConfirmation)
                    {
                        return;
                    }

                    waitingForConfirmation = false;
                    panel.ResolvePendingCover(false);
                },
                null,
                (_, __) =>
                {
                    waitingForConfirmation = true;
                    return panelTarget;
                });
            panelTarget = new GameObject("Action Panel Card Target", typeof(RectTransform))
                .GetComponent<RectTransform>();
            panelTarget.SetParent(panel.View.Root, false);
            panelTarget.anchorMin = Vector2.one * 0.5f;
            panelTarget.anchorMax = Vector2.one * 0.5f;
            panelTarget.pivot = Vector2.one * 0.5f;
            panelTarget.anchoredPosition = new Vector2(650f, 230f);
            panelTarget.sizeDelta = new Vector2(118f, 188f);

            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), true));
            var firstCard = FindHandCard(CardIds[0]);
            var firstPointer = CreatePointer(ScreenPoint(firstCard.Root));
            ((IPointerDownHandler)firstCard.PointerInteraction).OnPointerDown(firstPointer);
            ((IBeginDragHandler)firstCard.PointerInteraction).OnBeginDrag(firstPointer);
            firstPointer.position = ScreenPoint(panelTarget) + Vector2.left * 180f;
            ((IDragHandler)firstCard.PointerInteraction).OnDrag(firstPointer);
            ((IEndDragHandler)firstCard.PointerInteraction).OnEndDrag(firstPointer);
            Assert.That(FindHandCard(CardIds[0]).CanvasGroup.alpha, Is.EqualTo(0f));

            var secondCard = FindHandCard(CardIds[1]);
            var secondTexture = secondCard.Image.texture;
            var secondPointer = CreatePointer(ScreenPoint(secondCard.Root));
            ((IPointerDownHandler)secondCard.PointerInteraction).OnPointerDown(secondPointer);
            ((IBeginDragHandler)secondCard.PointerInteraction).OnBeginDrag(secondPointer);

            Assert.That(FindHandCard(CardIds[0]).CanvasGroup.alpha, Is.EqualTo(1f),
                "开始拖动第二张牌时，第一张待确认牌必须恢复到手牌区。");
            Assert.That(FindHandCard(CardIds[0]).CanvasGroup.blocksRaycasts, Is.True);
            var activeGhost = Array.Find(
                owner.GetComponentsInChildren<Animation>(true),
                animation => animation.gameObject.name == "Character Hand Drag Ghost");
            Assert.That(activeGhost, Is.Null,
                "新的鼠标跟随虚影尚未开始回位，不应残留上一张牌的动画组件。");
            var ghostImage = Array.Find(
                owner.GetComponentsInChildren<RawImage>(true),
                image => image.gameObject.name == "Character Hand Drag Ghost");
            Assert.That(ghostImage, Is.Not.Null);
            Assert.That(ghostImage.texture, Is.SameAs(secondTexture));
            Assert.That(FindHandCard(CardIds[1]).CanvasGroup.alpha, Is.EqualTo(0.12f));
        }

        [Test]
        public void PendingCoverLayout_UsesRemainingCardCountAndMatchesConfirmedHand()
        {
            RectTransform panelTarget = null;
            CreatePanel(null, null, (_, __) => panelTarget);
            panelTarget = new GameObject("Action Panel Card Target", typeof(RectTransform))
                .GetComponent<RectTransform>();
            panelTarget.SetParent(panel.View.Root, false);
            panelTarget.anchorMin = Vector2.one * 0.5f;
            panelTarget.anchorMax = Vector2.one * 0.5f;
            panelTarget.pivot = Vector2.one * 0.5f;
            panelTarget.anchoredPosition = new Vector2(650f, 230f);
            panelTarget.sizeDelta = new Vector2(118f, 188f);

            panel.Render(1, BuildModel(CardIds, Array.Empty<string>(), true));
            var coveredCard = FindHandCard(CardIds[0]);
            var pointer = CreatePointer(ScreenPoint(coveredCard.Root));
            ((IPointerDownHandler)coveredCard.PointerInteraction).OnPointerDown(pointer);
            ((IBeginDragHandler)coveredCard.PointerInteraction).OnBeginDrag(pointer);
            pointer.position = ScreenPoint(panelTarget) + Vector2.left * 180f;
            ((IDragHandler)coveredCard.PointerInteraction).OnDrag(pointer);
            ((IEndDragHandler)coveredCard.PointerInteraction).OnEndDrag(pointer);

            var countLayoutAnimation = panel.View.HandCardsAnimation;
            var countLayoutClip = countLayoutAnimation.GetClip("CharacterHandCountLayout");
            Assert.That(countLayoutClip, Is.Not.Null);
            var countLayoutState = countLayoutAnimation["CharacterHandCountLayout"];
            countLayoutState.time = countLayoutClip.length * 0.5f;
            countLayoutAnimation.Sample();
            countLayoutState.time = countLayoutClip.length;
            countLayoutAnimation.Sample();

            var remainingCount = CardIds.Length - 1;
            var pendingPositions = new Vector2[remainingCount];
            var pendingAngles = new float[remainingCount];
            for (var i = 0; i < remainingCount; i++)
            {
                var card = FindHandCard(CardIds[i + 1]);
                var normalized = remainingCount <= 1
                    ? 0f
                    : i / (float)(remainingCount - 1) * 2f - 1f;
                var expectedX = panel.LayoutProfile.FanCenterOffsetX +
                                (i - (remainingCount - 1) * 0.5f) *
                                panel.LayoutProfile.ExpandedSpacing;
                Assert.That(card.Root.anchoredPosition.x,
                    Is.EqualTo(expectedX).Within(0.01f));
                Assert.That(Mathf.DeltaAngle(
                        card.Root.localEulerAngles.z,
                        -normalized * panel.LayoutProfile.ExpandedMaxAngle),
                    Is.EqualTo(0f).Within(0.01f));
                pendingPositions[i] = card.Root.anchoredPosition;
                pendingAngles[i] = card.Root.localEulerAngles.z;
            }

            panel.Render(1, BuildModel(
                new[] { CardIds[1], CardIds[2] },
                Array.Empty<string>(),
                true));
            for (var i = 0; i < remainingCount; i++)
            {
                var card = FindHandCard(CardIds[i + 1]);
                Assert.That(Vector2.Distance(card.Root.anchoredPosition, pendingPositions[i]),
                    Is.LessThan(0.01f));
                Assert.That(Mathf.DeltaAngle(card.Root.localEulerAngles.z, pendingAngles[i]),
                    Is.EqualTo(0f).Within(0.01f));
            }
        }

        [Test]
        public void DiscardModal_IsReadOnlyGrayscaleClickableAndUsesViewerFirstClosePriority()
        {
            CreatePanel();
            panel.Render(1, BuildModel(
                new[] { CardIds[0], CardIds[1] },
                new[] { CardIds[2] },
                false));
            Assert.That(panel.View.DiscardCountText.text, Is.EqualTo("1"));
            panel.OpenDiscardPreview();
            Assert.That(panel.IsDiscardPreviewOpen, Is.True);
            Assert.That(panel.View.OverlayHandTitle.text, Is.EqualTo("手牌（2）"));
            Assert.That(panel.View.OverlayDiscardTitle.text, Is.EqualTo("弃牌（1）"));

            var handCards = GetCardComponents(panel.View.OverlayHandContent);
            var discards = GetCardComponents(panel.View.OverlayDiscardContent);
            Assert.That(handCards.Length, Is.EqualTo(2));
            Assert.That(discards.Length, Is.EqualTo(1));
            Assert.That(discards[0].Image.material, Is.SameAs(panel.View.DiscardGrayscaleMaterial));
            Assert.That(discards[0].CanvasGroup.alpha,
                Is.EqualTo(panel.LayoutProfile.DiscardAlpha).Within(0.001f));

            Click(discards[0]);
            Assert.That(HasOpenViewer(), Is.True);
            Assert.That(panel.TryHandleEscape(), Is.True);
            Assert.That(panel.IsDiscardPreviewOpen, Is.True, "第一次 Esc 必须只留给卡牌大图。");
            panel.CloseCharacterCardViewer();
            Assert.That(panel.TryHandleEscape(), Is.True);
            Assert.That(panel.IsDiscardPreviewOpen, Is.False, "第二次 Esc 才关闭弃牌窗口。");

            panel.Render(1, BuildModel(new[] { CardIds[0] }, Array.Empty<string>(), false));
            Assert.That(panel.View.DiscardCountText.text, Is.EqualTo("0"));
            panel.OpenDiscardPreview();
            Assert.That(panel.View.OverlayDiscardEmptyObject.activeSelf, Is.True);
            Assert.That(panel.View.OverlayDiscardEmptyObject.GetComponent<Text>().text, Is.EqualTo("暂无弃牌"));
            panel.View.RequestClose();
            Assert.That(panel.IsDiscardPreviewOpen, Is.False);
        }

        [Test]
        public void EmptyHandCover_ProjectsDiscardWithoutChangingGameStateAndBadgeShowsZero()
        {
            var state = new GameState
            {
                Phase = GamePhase.CharacterCover,
                CurrentPlayerId = 1,
                StartPlayerId = 1,
                Players = { new PlayerState { PlayerId = 1, Color = PlayerColor.Red } }
            };
            state.FindPlayer(1).DiscardCardIds.Add(CardIds[0]);
            state.FindPlayer(1).DiscardCardIds.Add(CardIds[1]);
            var model = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(model.CanCover, Is.True);
            Assert.That(model.HandCards.Count, Is.EqualTo(2));
            Assert.That(model.DiscardCards.Count, Is.Zero);
            Assert.That(state.FindPlayer(1).HandCardIds, Is.Empty);
            CollectionAssert.AreEqual(
                new[] { CardIds[0], CardIds[1] },
                state.FindPlayer(1).DiscardCardIds);

            CreatePanel();
            panel.Render(1, model);
            Assert.That(panel.View.DiscardCountText.text, Is.EqualTo("0"));
            Assert.That(panel.OrderedHandCount, Is.EqualTo(2));
        }

        private void CreatePanel(
            Action<string, Vector2> begin = null,
            Action<Vector2> update = null,
            Func<string, Vector2, RectTransform> end = null)
        {
            owner = new GameObject(
                "Character Hand Panel Test Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            owner.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = owner.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, owner.transform);
            panel = new PanelFacade(instance.GetComponent(GetRuntimeType(
                "YC.Presentation.CharacterHandPanel")));
            var catalog = AssetDatabase.LoadAssetAtPath(
                CatalogPath,
                GetRuntimeType("YC.Presentation.CardVisualCatalog"));
            Assert.That(catalog, Is.Not.Null);
            Assert.That(panel.Configure(catalog, begin, update, end), Is.True);
            Canvas.ForceUpdateCanvases();
        }

        private void DragCardToHandIndex(string cardId, string targetCardId)
        {
            var source = FindHandCard(cardId);
            var target = FindHandCard(targetCardId);
            var pointer = CreatePointer(ScreenPoint(source.Root));
            ((IPointerDownHandler)source.PointerInteraction).OnPointerDown(pointer);
            ((IBeginDragHandler)source.PointerInteraction).OnBeginDrag(pointer);
            pointer.position = new Vector2(
                ScreenPoint(target.Root).x,
                ScreenPoint(panel.View.HandDropArea.TransformPoint(Vector3.up * 100f)).y);
            ((IDragHandler)source.PointerInteraction).OnDrag(pointer);
            ((IEndDragHandler)source.PointerInteraction).OnEndDrag(pointer);
        }

        private CardFacade FindHandCard(string cardId)
        {
            var cards = GetCardComponents(panel.View.HandCardsRoot);
            for (var i = 0; i < cards.Length; i++)
            {
                if (cards[i].name == "Hand Card: " + cardId)
                {
                    return cards[i];
                }
            }

            Assert.Fail("找不到手牌：" + cardId);
            return null;
        }

        private string[] OrderedIds()
        {
            var result = new string[panel.OrderedHand.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = panel.OrderedHand[i].CardId;
            }
            return result;
        }

        private static CharacterCardPanelViewModel BuildModel(
            IEnumerable<string> handIds,
            IEnumerable<string> discardIds,
            bool canCover)
        {
            var hand = new List<CharacterCardHandItemViewModel>();
            foreach (var id in handIds)
                hand.Add(new CharacterCardHandItemViewModel(id, id, canCover));
            var discard = new List<CharacterCardHandItemViewModel>();
            foreach (var id in discardIds)
                discard.Add(new CharacterCardHandItemViewModel(id, id, false));
            return new CharacterCardPanelViewModel(
                hand.AsReadOnly(), discard.AsReadOnly(), string.Empty, string.Empty,
                canCover, false, false, false, false, false, string.Empty,
                0, 0, 0, 0,
                CharacterCardEffectKind.Unsupported,
                CharacterCardEffectKind.Unsupported,
                string.Empty, string.Empty, default(PlayerColor), string.Empty);
        }

        private static PointerEventData CreatePointer(Vector2 position)
        {
            return new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                position = position
            };
        }

        private static void Click(CardFacade card)
        {
            var pointer = CreatePointer(ScreenPoint(card.Root));
            ((IPointerDownHandler)card.PointerInteraction).OnPointerDown(pointer);
            ((IPointerClickHandler)card.PointerInteraction).OnPointerClick(pointer);
        }

        private static Vector2 ScreenPoint(RectTransform rect)
        {
            return RectTransformUtility.WorldToScreenPoint(null, rect.position);
        }

        private static Vector2 ScreenPoint(Vector3 worldPosition)
        {
            return RectTransformUtility.WorldToScreenPoint(null, worldPosition);
        }

        private static CardFacade[] GetCardComponents(RectTransform parent)
        {
            var components = parent.GetComponentsInChildren(
                GetRuntimeType("YC.Presentation.CharacterHandCardView"),
                true);
            var result = new CardFacade[components.Length];
            for (var i = 0; i < components.Length; i++)
            {
                result[i] = new CardFacade(components[i]);
            }
            return result;
        }

        private static bool HasOpenViewer()
        {
            return (bool)GetRuntimeType("YC.Presentation.ZoomableImageViewerController")
                .GetMethod("HasOpenViewer", System.Reflection.BindingFlags.Public |
                                            System.Reflection.BindingFlags.Static)
                .Invoke(null, null);
        }

        private static Type GetRuntimeType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, fullName);
            return type;
        }

        private static T GetProperty<T>(object target, string name)
        {
            var property = target.GetType().GetProperty(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public);
            Assert.That(property, Is.Not.Null, target.GetType().Name + "." + name);
            return (T)property.GetValue(target, null);
        }

        private sealed class ProfileFacade
        {
            private readonly Object target;

            public ProfileFacade(Object target)
            {
                this.target = target;
                Assert.That(target, Is.Not.Null);
            }

            public Vector2 CardSize => GetProperty<Vector2>(target, "CardSize");
            public float PassiveVisibleFraction => GetProperty<float>(target, "PassiveVisibleFraction");
            public float PassiveAlpha => GetProperty<float>(target, "PassiveAlpha");
            public float HoverScale => GetProperty<float>(target, "HoverScale");
            public float HoverAlpha => GetProperty<float>(target, "HoverAlpha");
            public float HoverBottom => GetProperty<float>(target, "HoverBottom");
            public float ExpandedBottom => GetProperty<float>(target, "ExpandedBottom");
            public float ExpandedSpacing => GetProperty<float>(target, "ExpandedSpacing");
            public float ExpandedMaxAngle => GetProperty<float>(target, "ExpandedMaxAngle");
            public float FanCenterOffsetX => GetProperty<float>(target, "FanCenterOffsetX");
            public float DiscardAlpha => GetProperty<float>(target, "DiscardAlpha");
            public Vector2 DiscardButtonPosition => GetProperty<Vector2>(target, "DiscardButtonPosition");
        }

        private sealed class CardFacade
        {
            private readonly Component target;

            public CardFacade(Component target)
            {
                this.target = target;
                Assert.That(target, Is.Not.Null);
            }

            public string name => target.name;
            public RectTransform Root => GetProperty<RectTransform>(target, "Root");
            public RawImage Image => GetProperty<RawImage>(target, "Image");
            public CanvasGroup CanvasGroup => GetProperty<CanvasGroup>(target, "CanvasGroup");
            public Component PointerInteraction => GetProperty<Component>(target, "PointerInteraction");
        }

        private sealed class ViewFacade
        {
            private readonly Component target;

            public ViewFacade(Component target)
            {
                this.target = target;
                Assert.That(target, Is.Not.Null);
            }

            public RectTransform HandCardsRoot => GetProperty<RectTransform>(target, "HandCardsRoot");
            public RectTransform Root => GetProperty<RectTransform>(target, "Root");
            public Animation HandCardsAnimation => GetProperty<Animation>(target, "HandCardsAnimation");
            public RectTransform HandDropArea => GetProperty<RectTransform>(target, "HandDropArea");
            public Text DiscardCountText => GetProperty<Text>(target, "DiscardCountText");
            public GameObject DiscardOverlayObject => GetProperty<GameObject>(target, "DiscardOverlayObject");
            public Text OverlayHandTitle => GetProperty<Text>(target, "OverlayHandTitle");
            public Text OverlayDiscardTitle => GetProperty<Text>(target, "OverlayDiscardTitle");
            public RectTransform OverlayHandContent => GetProperty<RectTransform>(target, "OverlayHandContent");
            public RectTransform OverlayDiscardContent => GetProperty<RectTransform>(target, "OverlayDiscardContent");
            public GameObject OverlayDiscardEmptyObject =>
                GetProperty<GameObject>(target, "OverlayDiscardEmptyObject");
            public Component HandCardTemplate => GetProperty<Component>(target, "HandCardTemplate");
            public Component OverlayCardTemplate => GetProperty<Component>(target, "OverlayCardTemplate");
            public RectTransform DragGhostTemplate => GetProperty<RectTransform>(target, "DragGhostTemplate");
            public Material DiscardGrayscaleMaterial => GetProperty<Material>(target, "DiscardGrayscaleMaterial");

            public void RequestClose()
            {
                var handler = GetProperty<Component>(target, "DiscardCloseInputHandler");
                handler.GetType().GetMethod("RequestClose").Invoke(handler, null);
            }
        }

        private sealed class PanelFacade
        {
            private readonly Component target;

            public PanelFacade(Component target)
            {
                this.target = target;
                Assert.That(target, Is.Not.Null);
                View = new ViewFacade(GetProperty<Component>(target, "View"));
                LayoutProfile = new ProfileFacade(GetProperty<Object>(target, "LayoutProfile"));
            }

            public ViewFacade View { get; }
            public ProfileFacade LayoutProfile { get; }
            public bool IsDiscardPreviewOpen => GetProperty<bool>(target, "IsDiscardPreviewOpen");
            public IReadOnlyList<CharacterCardHandItemViewModel> OrderedHand =>
                GetProperty<IReadOnlyList<CharacterCardHandItemViewModel>>(target, "OrderedHand");
            public int OrderedHandCount => OrderedHand.Count;

            public bool TryValidateConfiguration(out string reason)
            {
                var args = new object[] { null };
                var result = (bool)target.GetType().GetMethod("TryValidateConfiguration")
                    .Invoke(target, args);
                reason = args[0] as string ?? string.Empty;
                return result;
            }

            public bool Configure(
                Object catalog,
                Action<string, Vector2> begin,
                Action<Vector2> update,
                Func<string, Vector2, RectTransform> end)
            {
                return (bool)target.GetType().GetMethod("Configure")
                    .Invoke(target, new object[] { catalog, begin, update, end });
            }

            public void Render(int playerId, CharacterCardPanelViewModel model)
            {
                target.GetType().GetMethod("Render").Invoke(target, new object[] { playerId, model });
            }

            public void ResolvePendingCover(bool confirmed)
            {
                target.GetType().GetMethod("ResolvePendingCover")
                    .Invoke(target, new object[] { confirmed });
            }

            public void OpenDiscardPreview()
            {
                target.GetType().GetMethod("OpenDiscardPreview").Invoke(target, null);
            }

            public void CloseCharacterCardViewer()
            {
                target.GetType().GetMethod("CloseCharacterCardViewer").Invoke(target, null);
            }

            public bool TryHandleEscape()
            {
                return (bool)target.GetType().GetMethod("TryHandleEscape").Invoke(target, null);
            }
        }
    }
}
