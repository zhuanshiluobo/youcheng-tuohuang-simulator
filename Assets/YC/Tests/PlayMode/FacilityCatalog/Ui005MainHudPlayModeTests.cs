using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class Ui005MainHudPlayModeTests
    {
        [UnityTest]
        public IEnumerator BothScenes_UseOneBottomActionAndTabbedFormalEntries()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var hud = Find("YC.Presentation.GameplayInteractionHudView");
                Assert.That(hud, Is.Not.Null, scene);
                var args = new object[] { null };
                Assert.That((bool)hud.GetType().GetMethod("TryValidateConfiguration")
                    .Invoke(hud, args), Is.True, args[0] as string);
                var action = Property(hud, "ActionPanelView");
                var frame = Property(hud, "Frame");
                var compactNavigation = Child(hud.transform, "Compact Region Navigation");
                if (compactNavigation != null && compactNavigation.gameObject.activeInHierarchy)
                {
                    Child(compactNavigation, "Region Button 2").GetComponent<Button>().onClick.Invoke();
                    yield return null;
                }
                var end = (Button)Property(action, "EndRoundButton");
                Assert.That(end, Is.SameAs(Property(frame, "EndActionButton")), scene);
                Assert.That(FirstHit(end.transform as RectTransform), Is.SameAs(end.gameObject), scene);
                var build = (Button)Property(action, "BuildButton");
                var special = (Button)Property(action, "SpecialButton");
                Assert.That(build, Is.Not.Null, scene);
                Assert.That(special, Is.Not.Null, scene);
                Assert.That(special.interactable, Is.False, "尚无独立的特殊行动入口能力时应禁用");

                var tabs = Find("YC.Presentation.UiMainActionTabs");
                Assert.That(tabs, Is.Not.Null, scene);
                var buttons = (Button[])Field(tabs, "tabs");
                var sections = (GameObject[])Field(tabs, "sections");
                Assert.That(buttons, Has.Length.EqualTo(3));
                for (var i = 0; i < 3; i++)
                {
                    Assert.That(FirstHit(buttons[i].transform as RectTransform),
                        Is.SameAs(buttons[i].gameObject), scene + " 页签被装饰层遮挡");
                    buttons[i].onClick.Invoke();
                    Assert.That((int)Property(tabs, "SelectedIndex"), Is.EqualTo(i));
                    for (var j = 0; j < 3; j++)
                        Assert.That(sections[j].activeSelf, Is.EqualTo(j == i),
                            scene + " 页签显示了错误行动组");
                }
                buttons[0].onClick.Invoke();
                Assert.That(build.gameObject.activeInHierarchy, Is.True);
                Assert.That(special.gameObject.activeInHierarchy, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator TabState_PreservesAuthoredVisuals_AndUpdatesAvailability()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var tabs = Find("YC.Presentation.UiMainActionTabs");
            var buttons = (Button[])Field(tabs, "tabs");
            var state = buttons[0].GetComponent(Type.GetType(
                "YC.Presentation.UiMainButtonState, Assembly-CSharp", true));
            var image = buttons[0].GetComponent<Image>();
            var authoredSprite = image.sprite;
            var authoredColor = image.color;
            var pointer = new PointerEventData(EventSystem.current);
            state.GetType().GetMethod("OnPointerEnter").Invoke(state, new object[] { pointer });
            state.GetType().GetMethod("OnPointerExit").Invoke(state, new object[] { pointer });
            Assert.That(image.sprite, Is.SameAs(authoredSprite));
            Assert.That(image.color, Is.EqualTo(authoredColor));
            state.GetType().GetMethod("SetAvailable").Invoke(state, new object[] { false });
            Assert.That(buttons[0].interactable, Is.False);
            Assert.That((bool)Property(state, "IsSelected"), Is.True);
            Assert.That(image.sprite, Is.SameAs(authoredSprite));
            state.GetType().GetMethod("SetAvailable").Invoke(state, new object[] { true });
            Assert.That(buttons[0].interactable, Is.True);
            Assert.That((bool)Property(state, "IsSelected"), Is.True);
            Assert.That(image.sprite, Is.SameAs(authoredSprite));
        }

        [UnityTest]
        public IEnumerator OpponentProjection_ShowsPublicCountsWithoutCardIdentities()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            var modules = Property(hud, "MainModules");
            var state = new GameState();
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "本机玩家", Color = PlayerColor.Red });
            var opponent = new PlayerState
            {
                PlayerId = 2,
                Name = "一个很长的玩家名字用于检查适配",
                Color = PlayerColor.Blue,
                Score = 123
            };
            opponent.HandCardIds.Add("hidden-character-secret-1");
            opponent.HandCardIds.Add("hidden-character-secret-2");
            opponent.DeclaredCityStyles.Add(new CityStyleDeclarationState
                { CityStyleId = "public-style" });
            state.Players.Add(opponent);
            state.Decks.CharacterDeck.Add("deck-card-1");
            var visible = GameStateViewProjector.Project(state, GameStateViewer.Player(1));
            var sanitized = GameStateViewProjector.ToClientState(visible);
            Assert.That(visible.Players[1].HandCardCount, Is.EqualTo(2));
            Assert.That(visible.Players[1].HandCardIds, Is.Empty);
            Assert.That(sanitized.Players[1].HandCardIds, Is.Empty);
            Assert.That(sanitized.Decks.CharacterDeck, Is.Empty);
            modules.GetType().GetMethod("Render").Invoke(modules,
                new object[] { sanitized, visible, 1, null });
            var names = (Text[])Field(modules, "opponentName");
            var scores = (Text[])Field(modules, "opponentScore");
            var hands = (Text[])Field(modules, "opponentHandCount");
            var styles = (Text[])Field(modules, "opponentStyleCount");
            var deck = (Text)Field(modules, "characterDeckCount");
            Assert.That(names[0].text, Is.EqualTo(opponent.Name));
            Assert.That(scores[0].text, Is.EqualTo("123"));
            Assert.That(hands[0].text, Is.EqualTo("2"));
            Assert.That(styles[0].text, Is.EqualTo("1"));
            Assert.That(deck.text, Is.EqualTo("1"));
            foreach (var label in ((Component)modules).GetComponentsInChildren<Text>(true))
                Assert.That(label.text, Does.Not.Contain("hidden-character-secret"));
        }

        [UnityTest]
        public IEnumerator FullHand_RemainsVisibleAcrossRenders_AndDiscardPreviewCloses()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var panel = Find("YC.Presentation.CharacterHandPanel");
            var ids = new List<string>(CharacterCardDatabase.GetInitialCardIds(
                new PlayerState { PlayerId = 1, Color = PlayerColor.Red }));
            var secondPlayer = CharacterCardDatabase.GetInitialCardIds(
                new PlayerState { PlayerId = 2, Color = PlayerColor.Blue });
            for (var i = 0; i < 2; i++)
                ids.Add(secondPlayer[i]);
            var itemType = Type.GetType(
                "YC.Presentation.Workflows.CharacterCardHandItemViewModel, YC.Presentation.Workflows", true);
            var cards = Array.CreateInstance(itemType, ids.Count);
            for (var i = 0; i < ids.Count; i++)
                cards.SetValue(Activator.CreateInstance(itemType, ids[i], ids[i], false), i);
            var modelType = Type.GetType(
                "YC.Presentation.Workflows.CharacterCardPanelViewModel, YC.Presentation.Workflows", true);
            var model = Activator.CreateInstance(modelType, cards,
                Array.CreateInstance(itemType, 0), string.Empty, string.Empty,
                false, false, false, false, false, false, string.Empty,
                0, 0, 0, 0, CharacterCardEffectKind.Unsupported,
                CharacterCardEffectKind.Unsupported, string.Empty, string.Empty,
                PlayerColor.Red, string.Empty);
            panel.GetType().GetMethod("Render").Invoke(panel, new object[] { 1, model });
            // 等待上一批动态卡牌完成延迟销毁，避免把旧实例计入完整手牌。
            yield return null;
            AssertCompleteHand(panel, ids);
            panel.GetType().GetMethod("Render").Invoke(panel, new object[] { 1, model });
            // 等待上一批动态卡牌完成延迟销毁，避免把旧实例计入完整手牌。
            yield return null;
            AssertCompleteHand(panel, ids);

            var view = Property(panel, "View");
            var discard = (Button)Property(view, "DiscardButton");
            Assert.That(discard.IsActive() && discard.IsInteractable(), Is.True,
                "弃牌预览入口应可交互");
            discard.onClick.Invoke();
            Assert.That((bool)Property(panel, "IsDiscardPreviewOpen"), Is.True);
            var close = (Button)Property(view, "DiscardCloseButton");
            Assert.That(close.IsActive() && close.IsInteractable(), Is.True,
                "弃牌预览关闭入口应可交互");
            close.onClick.Invoke();
            Assert.That((bool)Property(panel, "IsDiscardPreviewOpen"), Is.False);
            AssertCompleteHand(panel, ids);
        }

        [UnityTest]
        public IEnumerator MainActionButtons_CreateAuthoritativeDraftAndCancelThroughHudOrEscape()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var controller = Find("YC.Presentation.MobileCityInteractionController");
            var state = (GameState)Property(controller, "CurrentState");
            state.Phase = GamePhase.ActionRound1;
            state.Round = state.ActionRound = 1;
            state.CurrentPlayerId = state.StartPlayerId = 1;
            state.PendingChoice = null;
            state.PendingCardSession = null;
            state.PendingCharacterEffect = null;
            state.PendingSpecialAction = null;
            state.EffectRuntime = new YC.Domain.State.EffectRuntimeState();
            var player = state.FindPlayer(1);
            player.CityLocationId = "A-01";
            player.InfluenceSupply = 30;
            player.RemainingMainActionsThisTurn = 1;
            player.CompletedMainActionsThisTurn = 0;
            player.Resources = new ResourceSet { Iron = 30, Originium = 30, OriginiumShard = 30 };
            state.Decks.FacilitySupply.Clear();
            state.Decks.FacilitySupply.Add(YC.Domain.Facilities.FacilityCardDatabase.BoroughAdministrativeDistrict);
            state.Map.Influences.Clear();
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, LocationId = "A-01",
                SlotId = YC.Domain.Influence.InfluenceService.GetLocationSlotId("A-01", 0) });
            foreach (var location in new[] { "A-01", "A-02", "B-01" })
                state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = location, ResourceType = ResourceType.Iron });
            player.DeclaredCityStyles.Clear();
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState {
                InfluenceMarkerId = "draft-smoke-marker",
                CityStyleId = YC.Domain.CityStyles.CityStyleDatabase.EfficientMobileManagementSystem,
                UnlockedSpecialActionId = YC.Domain.SpecialActions.SpecialActionDatabase.EfficientMobileManagementSystem,
                MarkerArea = YC.Domain.CityStyles.CityStyleMarkerAreas.UsesTwo, RemainingSpecialActionUses = 2 });
            controller.GetType().GetMethod("SynchronizeInteractionFromState", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(controller, null);
            yield return null;
            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            var action = Property(hud, "ActionPanelView");
            var undo = (Button)Field(Property(hud, "Frame"), "undoButton");
            foreach (var name in new[] { "DeployButton", "DispatchButton", "ExploreButton", "MoveCityButton", "BuildButton", "SpecialButton" })
            {
                foreach (var escape in new[] { false, true })
                {
                    var button = (Button)Property(action, name);
                    Assert.That(button.interactable, Is.True, name);
                    button.onClick.Invoke();
                    yield return null;
                    Assert.That(state.HasPendingChoice(), Is.True, name);
                    Assert.That((bool)Property(controller, "CanCancelMainAction"), Is.True, name);
                    var requestCount = state.EffectRuntime.InteractionRequests.Count;
                    button.onClick.Invoke();
                    Assert.That(state.EffectRuntime.InteractionRequests.Count, Is.EqualTo(requestCount), "重复点击不得取消或重提：" + name);
                    Assert.That(undo.interactable, Is.True, name);
                    if (escape)
                        Assert.That((bool)controller.GetType().GetMethod("TryHandleInteractionEscape").Invoke(controller, null), Is.True, name);
                    else
                        undo.onClick.Invoke();
                    yield return null;
                    Assert.That(state.HasPendingChoice(), Is.False, name);
                    Assert.That(undo.interactable, Is.False, name);
                    Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(1), name);
                    Assert.That(player.CompletedMainActionsThisTurn, Is.Zero, name);
                    Assert.That(player.Resources.Iron, Is.EqualTo(30), name);
                    Assert.That(player.Resources.Originium, Is.EqualTo(30), name);
                    Assert.That(player.Resources.OriginiumShard, Is.EqualTo(30), name);
                    Assert.That(player.CityLocationId, Is.EqualTo("A-01"), name);
                    Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(2), name);
                    Assert.That(state.Map.Influences.Count, Is.EqualTo(1), name);
                }
            }
        }

        [UnityTest]
        public IEnumerator CityStyleTab_OpensAndClosesTheFormalQuickActionPreview()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--yc-dev-right-card-smoke-no-dialog") < 0)
                Assert.Ignore("此用例使用正式右卡行动阶段启动状态。");
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var tabs = Find("YC.Presentation.UiMainActionTabs");
                ((Button[])Field(tabs, "tabs"))[2].onClick.Invoke();
                var hud = Find("YC.Presentation.GameplayInteractionHudView");
                var action = Property(hud, "ActionPanelView");
                var declare = (Button)Property(action, "DeclareCityStyleButton");
                Assert.That(declare.gameObject.activeInHierarchy, Is.True, scene);
                Assert.That(declare.interactable, Is.True, scene);
                declare.onClick.Invoke();
                var controller = Find("YC.Presentation.MobileCityInteractionController");
                var workflow = Field(controller, "workflowView");
                var preview = Field(workflow, "cityStyleDeclarationDialog");
                Assert.That((bool)Property(preview, "IsShowing"), Is.True, scene);
                var previewView = Field(preview, "view");
                ((Button)Property(previewView, "CloseButton")).onClick.Invoke();
                Assert.That((bool)Property(preview, "IsShowing"), Is.False, scene);
                Assert.That((int)Property(tabs, "SelectedIndex"), Is.EqualTo(2), scene);
            }
        }

        private static void AssertCompleteHand(Component panel, ICollection<string> expectedIds)
        {
            Assert.That(panel, Is.Not.Null, "实际场景缺少手牌面板");
            var args = new object[] { null };
            Assert.That((bool)panel.GetType().GetMethod("TryValidateConfiguration")
                .Invoke(panel, args), Is.True, args[0] as string);
            var view = Property(panel, "View");
            var root = (RectTransform)Property(view, "HandCardsRoot");
            var cardType = Type.GetType("YC.Presentation.CharacterHandCardView, Assembly-CSharp", true);
            var cards = root.GetComponentsInChildren(cardType, false);
            Assert.That(cards.Length, Is.EqualTo(expectedIds.Count), "必须显示完整手牌，不能分页隐藏");
            var actualNames = new List<string>();
            foreach (var card in cards)
            {
                actualNames.Add(card.gameObject.name);
                args = new object[] { null };
                Assert.That((bool)cardType.GetMethod("TryValidateConfiguration")
                    .Invoke(card, args), Is.True, args[0] as string);
                Assert.That(((RawImage)Property(card, "Image")).texture, Is.Not.Null);
                Assert.That(((Button)Property(card, "Button")).IsInteractable(), Is.True,
                    card.name + " 应允许查看卡牌");
                var group = (CanvasGroup)Property(card, "CanvasGroup");
                Assert.That(group.alpha, Is.GreaterThan(0f), card.name + " 不应被分页隐藏");
                Assert.That(group.blocksRaycasts, Is.True, card.name + " 应接收手牌指针交互");
                Assert.That(((Behaviour)Property(card, "PointerInteraction")).isActiveAndEnabled,
                    Is.True, card.name + " 缺少活动的指针交互");
            }
            var expectedNames = new List<string>();
            foreach (var id in expectedIds) expectedNames.Add("Hand Card: " + id);
            Assert.That(actualNames, Is.EquivalentTo(expectedNames), "手牌显示不能遗漏或重复卡牌");
        }

        private static Component Find(string typeName)
        {
            var type = Type.GetType(typeName + ", Assembly-CSharp", true);
            return UnityEngine.Object.FindObjectOfType(type) as Component;
        }

        private static IEnumerator ClearLaunchContext()
        {
            var context = Find("YC.Presentation.GameLaunchContext");
            if (context == null) yield break;
            UnityEngine.Object.Destroy(context.gameObject);
            yield return null;
        }

        private static object Property(object target, string name)
        {
            return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                .GetValue(target);
        }

        private static object Field(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(target);
        }

        private static GameObject FirstHit(RectTransform target)
        {
            Canvas.ForceUpdateCanvases();
            var point = RectTransformUtility.WorldToScreenPoint(null,
                target.TransformPoint(target.rect.center));
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, results);
            return results.Count == 0 ? null : results[0].gameObject;
        }

        private static Transform Child(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = Child(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
