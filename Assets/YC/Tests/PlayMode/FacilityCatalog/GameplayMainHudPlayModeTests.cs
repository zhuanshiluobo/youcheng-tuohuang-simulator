using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class GameplayMainHudPlayModeTests
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
        public IEnumerator TabState_DisabledOverridesSelection_AndRetainsSelectionOnExit()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var tabs = Find("YC.Presentation.UiMainActionTabs");
            var buttons = (Button[])Field(tabs, "tabs");
            var state = buttons[0].GetComponent(Type.GetType(
                "YC.Presentation.UiMainButtonState, Assembly-CSharp", true));
            var image = buttons[0].GetComponent<Image>();
            var selected = (Sprite)Field(state, "selectedFace");
            var selectedHover = (Sprite)Field(state, "selectedHover");
            var disabled = (Sprite)Field(state, "disabled");
            Assert.That(image.sprite, Is.SameAs(selected));
            var pointer = new PointerEventData(EventSystem.current);
            state.GetType().GetMethod("OnPointerEnter").Invoke(state, new object[] { pointer });
            Assert.That(image.sprite, Is.SameAs(selectedHover));
            state.GetType().GetMethod("OnPointerExit").Invoke(state, new object[] { pointer });
            Assert.That(image.sprite, Is.SameAs(selected));
            state.GetType().GetMethod("SetAvailable").Invoke(state, new object[] { false });
            Assert.That(image.sprite, Is.SameAs(disabled));
            state.GetType().GetMethod("SetAvailable").Invoke(state, new object[] { true });
            Assert.That(image.sprite, Is.SameAs(selected));
        }

        [UnityTest]
        public IEnumerator OpponentProjection_ShowsPublicCountsWithoutCardIdentities()
        {
            var capture = Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-capture-opponents") >= 0;
            Type captureType = null;
            if (capture)
            {
                captureType = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
                captureType.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                captureType.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(1920, 1080) });
            }
            foreach (var sceneName in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
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
                    Score = 123,
                    Resources = new ResourceSet { Originium = 101, OriginiumShard = 202,
                        Iron = 303, PureOriginium = 404, GoldVoucher = 505 }
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
                var resources = (Text[])Field(modules, "opponentResourceValues");
                var data = (GameObject[])Field(modules, "opponentData");
                Assert.That(names[0].text, Is.EqualTo(opponent.Name));
                Assert.That(scores[0].text, Is.EqualTo("123"));
                Assert.That(hands[0].text, Is.EqualTo("2"));
                Assert.That(styles[0].text, Is.EqualTo("1"));
                Assert.That(data[0].activeSelf, Is.True);
                Assert.That(data[1].activeSelf, Is.False);
                for (var i = 0; i < 5; i++)
                    Assert.That(resources[i].text, Is.EqualTo(((i + 1) * 101).ToString()));
                foreach (var label in ((Component)modules).GetComponentsInChildren<Text>(true))
                    Assert.That(label.text, Does.Not.Contain("hidden-character-secret"));
                // 检查实际渲染的数值，不能仅以 Text.text 赋值成功代表画面可见。
                foreach (var size in capture ? new[] { new Vector2Int(1920, 1080), new Vector2Int(900, 600) } : new[] { Vector2Int.zero })
                {
                    if (capture)
                    {
                        captureType.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { size });
                        var deadline = Time.realtimeSinceStartup + 15f;
                        while (new Vector2Int(Screen.width, Screen.height) != size && Time.realtimeSinceStartup < deadline)
                            yield return null;
                        Assert.That(new Vector2Int(Screen.width, Screen.height), Is.EqualTo(size));
                    }
                    modules.GetType().GetMethod("Render").Invoke(modules, new object[] { sanitized, visible, 1, null });
                    Canvas.ForceUpdateCanvases();
                    yield return null;
                    Canvas.ForceUpdateCanvases();
                    for (var i = 0; i < 6; i++)
                    {
                        var label = i == 0 ? scores[0] : resources[i - 1];
                        Assert.That(label.gameObject.activeInHierarchy, Is.True);
                        Assert.That(label.canvasRenderer.cull, Is.False);
                        Assert.That(label.cachedTextGenerator.vertexCount, Is.GreaterThan(4));
                        Assert.That(label.rectTransform.rect.width + .5f, Is.GreaterThanOrEqualTo(label.preferredWidth),
                            label.name + " 的多位数必须获得足够显示空间");
                    }
                    if (capture)
                    {
                        var directory = CaptureDirectory("Logs/OpponentPanelFix-20260928");
                        Directory.CreateDirectory(directory);
                        ScreenCapture.CaptureScreenshot(Path.Combine(directory, sceneName + "-" + size.x + "x" + size.y + ".png"));
                        yield return null;
                        yield return null;
                    }
                }
                opponent.Resources.GoldVoucher = 9;
                opponent.Score = 456;
                visible = GameStateViewProjector.Project(state, GameStateViewer.Player(1));
                modules.GetType().GetMethod("Render").Invoke(modules,
                    new object[] { GameStateViewProjector.ToClientState(visible), visible, 1, null });
                Assert.That(scores[0].text, Is.EqualTo("456"));
                Assert.That(resources[4].text, Is.EqualTo("9"));
                state.Players.Remove(opponent);
                visible = GameStateViewProjector.Project(state, GameStateViewer.Player(1));
                modules.GetType().GetMethod("Render").Invoke(modules,
                    new object[] { GameStateViewProjector.ToClientState(visible), visible, 1, null });
                Assert.That(data[0].activeSelf, Is.False, "退出的玩家不能留下陈旧数值");
            }
        }

        [UnityTest]
        public IEnumerator ResourceProjection_UsesTopbarTextBindingsAndDiscardEntryOpensPreview()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            var modules = Property(hud, "MainModules");
            var state = new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        Resources = new ResourceSet
                        {
                            Originium = 3,
                            OriginiumShard = 5,
                            Iron = 7,
                            PureOriginium = 11,
                            GoldVoucher = 13
                        }
                    }
                }
            };
            var registry = Property(hud, "DialogRegistry");
            var catalog = Property(registry, "CardVisualCatalog");
            modules.GetType().GetMethod("Render").Invoke(modules,
                new[] { state, null, (object)1, catalog });
            var values = (Text[])Field(modules, "localResourceValues");
            CollectionAssert.AreEqual(new[] { "3", "5", "7", "11" },
                Array.ConvertAll(values, value => value.text));
            Assert.That(((Text)Field(modules, "localVoucherValue")).text, Is.EqualTo("13"));

            modules.GetType().GetMethod("RenderDiscardCount").Invoke(modules, new object[] { 4 });
            Assert.That(((Text)Property(modules, "DiscardCountText")).text, Is.EqualTo("4"));
            var button = (Button)Field(modules, "discardPreviewButton");
            Assert.That(button.interactable, Is.True);
            button.onClick.Invoke();
            var panel = Find("YC.Presentation.CharacterHandPanel");
            Assert.That((bool)Property(panel, "IsDiscardPreviewOpen"), Is.True);
            var discardPage = Field(panel, "discardPage");
            var discardView = (Component)Field(discardPage, "view");
            Child(discardView.transform, "Cancel Selection").GetComponent<Button>().onClick.Invoke();
            Assert.That((bool)Property(panel, "IsDiscardPreviewOpen"), Is.False);
        }

        [UnityTest]
        public IEnumerator OverflowHand_KeepsAllCardsWithoutScrollingAcrossRenders()
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
            yield return null;
            var pile = Property(Property(panel, "View"), "HandPile");
            var content = (RectTransform)Property(pile, "Content");
            Assert.That(content.GetComponentInParent<ScrollRect>(), Is.Null);
            Assert.That(content.childCount, Is.EqualTo(ids.Count));
            panel.GetType().GetMethod("Render").Invoke(panel, new object[] { 1, model });
            yield return null;
            Assert.That(content.childCount, Is.EqualTo(ids.Count));
            foreach (var id in ids)
                Assert.That(Child(content, "Card Slot: " + id).gameObject.activeSelf, Is.True);
        }

        [UnityTest]
        public IEnumerator BuildButton_OpensAndCancelsTheFormalBuildDialog()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--yc-dev-right-card-smoke-no-dialog") < 0)
                Assert.Ignore("此用例使用正式右卡行动阶段启动状态。");
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var controller = Find("YC.Presentation.MobileCityInteractionController");
            var presenter = Field(controller, "turnActionPresenter");
            var dialog = Field(controller, "characterCardEffectChoiceDialog");

            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            var action = Property(hud, "ActionPanelView");
            var build = (Button)Property(action, "BuildButton");
            var panel = Property(hud, "BuildInfoPanel");
            var panelView = Property(panel, "View");
            var externalArea = ((RectTransform)Property(panelView, "ExternalFacilityArea")).gameObject;
            Assert.That(build.interactable, Is.True, "建设入口应使用当前主要行动能力");
            build.onClick.Invoke();
            var interaction = Property(presenter, "BuildInteraction");
            Assert.That((bool)Property(interaction, "IsActive"), Is.True);
            Assert.That((bool)Property(dialog, "IsShowing"), Is.True,
                "正式建设入口应打开共享选择窗口。");
            Assert.That(externalArea.activeSelf, Is.False, "建设候选页不应同时显示旧供应板。");
            var shell = Field(dialog, "shell");
            var shellView = Field(shell, "view");
            var cancelObject = Child(((Component)shellView).transform, "Cancel Selection");
            Assert.That(cancelObject, Is.Not.Null);
            var cancel = cancelObject.GetComponent<Button>();
            Assert.That(cancel, Is.Not.Null);
            cancel.onClick.Invoke();
            Assert.That((bool)Property(dialog, "IsShowing"), Is.False);
            Assert.That((bool)Property(interaction, "IsActive"), Is.False);
            Assert.That(externalArea.activeSelf, Is.False);
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
                var currentHud = Find("YC.Presentation.GameplayInteractionHudView");
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

        [UnityTest]
        public IEnumerator CharacterCover_FormalEntranceSelectionDetailsAndConfirmation()
        {
            Assert.That(Array.IndexOf(Environment.GetCommandLineArgs(),
                "--yc-dev-right-card-smoke-no-dialog"), Is.LessThan(0), "盖牌验收必须使用正常开局。");
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-capture-card-picker") >= 0)
            {
                Assert.That(UnityEngine.Application.isBatchMode, Is.False,
                    "正式页面截图需要非 batchmode 的真实 Game View。");
                var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
                capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { new Vector2Int(1920, 1080) });
                var readyBy = Time.realtimeSinceStartup + 15f;
                while ((Screen.width != 1920 || Screen.height != 1080) && Time.realtimeSinceStartup < readyBy)
                    yield return null;
                Assert.That(new Vector2Int(Screen.width, Screen.height), Is.EqualTo(new Vector2Int(1920,1080)));
            }
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var controller = Find("YC.Presentation.MobileCityInteractionController");
                var session = Field(controller, "session");
                var state = (GameState)Property(session, "State");
                Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
                for (var step = 0; state.Phase == GamePhase.Entrance && step < 100; step++)
                {
                    GameCommand command;
                    var player = state.FindPlayer(state.CurrentPlayerId);
                    if (string.IsNullOrEmpty(player.CityLocationId))
                    {
                        var locations = StaticMapDefinitions.Resolve(state.MapId).Locations;
                        var location = locations.Find(item => item.CanDockCity &&
                            (state.MapId != StaticMapDefinitions.FourPlayerMapId ||
                             StaticMapDefinitions.FourPlayerInitialLocationIds.Contains(item.LocationId)) &&
                            !state.Players.Exists(other => other.CityLocationId == item.LocationId));
                        Assert.That(location, Is.Not.Null);
                        command = new GameCommand { Kind = GameCommandKind.ChooseInitialLocation,
                            PlayerId = player.PlayerId, TargetId = location.LocationId };
                    }
                    else
                    {
                        var request = state.EffectRuntime.InteractionRequests.Find(item =>
                            item.Status == "open" && item.AnsweringPlayerId == player.PlayerId &&
                            item.CandidateIds.Count > 0);
                        Assert.That(request, Is.Not.Null, "入场效果必须有正式可回答的请求。");
                        command = new GameCommand { Kind = GameCommandKind.AnswerInteraction,
                            PlayerId = player.PlayerId };
                        command.Parameters["interactionId"] = request.InteractionId;
                        command.Parameters["expectedRevision"] = request.StateRevision.ToString();
                        command.OptionIds.Add(request.CandidateIds[0]);
                    }
                    var result = (CommandResult)session.GetType().GetMethod("Submit").Invoke(session, new object[] { command });
                    Assert.That(result.Succeeded, Is.True, result.Validation == null ? scene : result.Validation.Reason);
                }
                Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
                InvokePrivate(controller, "SynchronizeInteractionFromState");
                yield return null;
                var coverPresenter = Field(controller, "turnActionPresenter");
                var configuredPrompt = (string)Property(Property(coverPresenter, "ActionPanelPresenter"), "CharacterCoverPrompt");
                var coverModel = coverPresenter.GetType().GetMethod("BuildActionPanelViewModel").Invoke(coverPresenter, null);
                Assert.That(configuredPrompt, Is.Not.Empty, "盖牌提示应来自正式文案配置。");
                Assert.That((string)Property(coverModel, "StatusText"), Is.EqualTo(configuredPrompt));
                var local = (int)Field(controller, "localPlayerId");
                Assert.That(state.CurrentPlayerId, Is.EqualTo(local));
                var hand = state.FindPlayer(local).HandCardIds;
                var count = hand.Count;
                var dialog = Field(controller, "characterCardEffectChoiceDialog");
                var shell = Field(dialog, "shell");
                var page = (Component)Field(shell, "view");
                Assert.That(page.gameObject.activeInHierarchy, Is.True);
                var pageSortingOrder = page.GetComponent<Canvas>().sortingOrder;
                Assert.That((bool)Property(page, "IsCardPicker"), Is.True,
                    "正式盖牌入口必须使用实际卡牌选择页。");
                var confirm = Child(page.transform, "Confirm Selection").GetComponent<Button>();
                Assert.That(confirm.interactable, Is.False, "未选牌不能确认。");
                RefreshPageLayout(page);
                var confirmLabel = confirm.GetComponentInChildren<Text>();
                Assert.That(confirmLabel, Is.Not.Null);
                Assert.That(confirmLabel.rectTransform.rect.height, Is.GreaterThanOrEqualTo(confirmLabel.preferredHeight),
                    scene + " 确认按钮的文字框不足以显示当前字号。");
                var coverFrame = Find("YC.Presentation.GameplayHudFrame");
                var coverFold = (Button)Property(coverFrame, "FoldButton");
                Assert.That(coverFold.interactable, Is.True, "盖牌页应可收起。");
                coverFold.onClick.Invoke();
                Assert.That(page.gameObject.activeInHierarchy, Is.False);
                yield return null;
                coverFold.onClick.Invoke();
                yield return null;
                Assert.That(page.gameObject.activeInHierarchy, Is.True, "再次点击应恢复同一盖牌页。");
                var candidate = Child(page.transform, "Character Cover Card 0");
                var card = candidate.GetComponent(Type.GetType("YC.Presentation.FacilityEffectCardView, Assembly-CSharp", true));
                var cardButton = (Button)Property(card, "Button");
                RefreshPageLayout(page);
                Assert.That(FirstHit((RectTransform)cardButton.transform), Is.SameAs(cardButton.gameObject),
                    scene + " 正式盖牌的卡面应为首个命中。");
                cardButton.onClick.Invoke();
                var selected = (string)Field(controller, "selectedCoverCardId");
                Assert.That(hand, Does.Contain(selected));
                Assert.That(hand.Count, Is.EqualTo(count), "预选不得移除手牌。");
                page = (Component)Field(shell, "view");
                InvokePrivate(controller, "SynchronizeInteractionFromState");
                Assert.That(Field(shell, "view"), Is.SameAs(page), "重复刷新不得重建页面。");
                candidate = Child(page.transform, "Character Cover Card 0");
                card = candidate.GetComponent(Type.GetType("YC.Presentation.FacilityEffectCardView, Assembly-CSharp", true));
                var previewCard = (Button)Property(card, "Button");
                yield return null;
                RefreshPageLayout(page);
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-capture-card-picker") >= 0)
                {
                    foreach (var actualCard in page.GetComponentsInChildren(
                        Type.GetType("YC.Presentation.FacilityEffectCardView, Assembly-CSharp", true), false))
                        Assert.That(((RawImage)Property(actualCard, "CardImage")).texture, Is.Not.Null,
                            "正式盖牌截图必须等待实际卡面就绪。");
                    yield return null;
                    RefreshPageLayout(page);
                    var directory = CaptureDirectory(Path.Combine(UnityEngine.Application.dataPath,
                        "..", "prompt", "UI换新", "执行记录", "卡牌选择"));
                    Directory.CreateDirectory(directory);
                    var screenshot = Path.Combine(directory, "FormalCover-" + scene + "-" +
                        Screen.width + "x" + Screen.height + ".png");
                    var requestedAt = DateTime.UtcNow;
                    ScreenCapture.CaptureScreenshot(screenshot);
                    var deadline = Time.realtimeSinceStartup + 15f;
                    while ((!File.Exists(screenshot) || File.GetLastWriteTimeUtc(screenshot) < requestedAt ||
                        new FileInfo(screenshot).Length == 0) && Time.realtimeSinceStartup < deadline)
                        yield return null;
                    Assert.That(File.Exists(screenshot) && File.GetLastWriteTimeUtc(screenshot) >= requestedAt &&
                        new FileInfo(screenshot).Length > 0, Is.True, "本轮正式盖牌截图未完成：" + screenshot);
                }
                Assert.That(FirstHit((RectTransform)previewCard.transform), Is.SameAs(previewCard.gameObject),
                    scene + " 卡面查看手势必须命中实际卡牌。");
                OpenCardPreview(previewCard);
                yield return null;
                Assert.That(page.gameObject.activeInHierarchy, Is.False);
                InvokePrivate(controller, "SynchronizeInteractionFromState");
                Assert.That(Field(shell, "view"), Is.SameAs(page));
                Assert.That(page.gameObject.activeInHierarchy, Is.False, "刷新不得抢走详情焦点。");
                var frame = Find("YC.Presentation.GameplayHudFrame");
                Assert.That(Field(frame, "suspendedEffectPage"), Is.Null, "盖牌不应注册为待结算效果。");
                var viewer = Find("YC.Presentation.CardViewer");
                Assert.That(viewer, Is.Not.Null);
                viewer.GetType().GetMethod("Close").Invoke(viewer, null);
                yield return null;
                Assert.That(page.gameObject.activeInHierarchy, Is.True, "详情返回必须恢复盖牌。");
                Assert.That(Field(controller, "selectedCoverCardId"), Is.EqualTo(selected));

                // 定向模拟网络控制层的 pending 与 settlement 通知，不注入候选或修改领域状态。
                SetField(controller, "coverSubmissionInFlight", true);
                SetField(controller, "submittedCoverCardId", selected);
                SetField(controller, "submittedCoverCommandId", "current-cover-command");
                InvokePrivate(controller, "ConfirmCoverSelection");
                Assert.That(hand.Count, Is.EqualTo(count), "pending 期间重复确认不得提交。");
                InvokePrivate(controller, "NotifyCoverCommandSettled", "older-cover-command");
                Assert.That(Field(controller, "coverSubmissionInFlight"), Is.EqualTo(true));
                InvokePrivate(controller, "NotifyCoverCommandSettled", "current-cover-command");
                InvokePrivate(controller, "SynchronizeInteractionFromState");
                yield return null;
                Assert.That(Field(controller, "coverSubmissionInFlight"), Is.EqualTo(false));
                Assert.That(Field(controller, "selectedCoverCardId"), Is.EqualTo(selected));
                page = (Component)Field(shell, "view");
                confirm = Child(page.transform, "Confirm Selection").GetComponent<Button>();
                Assert.That(confirm.interactable, Is.True, "拒绝后应允许重试。");
                RefreshPageLayout(page);
                Assert.That(FirstHit((RectTransform)confirm.transform), Is.SameAs(confirm.gameObject),
                    scene + " 正式盖牌的确认按钮应为首个命中。");
                confirm.onClick.Invoke();
                confirm.onClick.Invoke();
                yield return null;
                Assert.That(state.FindPlayer(local).CoveredCharacterCardId, Is.EqualTo(selected));
                Assert.That(state.FindPlayer(local).HandCardIds.Count, Is.EqualTo(count - 1));
                Assert.That((bool)Property(dialog, "IsShowing"), Is.False);
                Assert.That(Field(controller, "coverSubmissionInFlight"), Is.EqualTo(false));
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-capture-card-picker") >= 0)
                {
                    var captureType = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
                    foreach (var size in new[] { new Vector2Int(1920, 1080), new Vector2Int(900, 600), new Vector2Int(1920, 1080) })
                    {
                        captureType.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { size });
                        var deadline = Time.realtimeSinceStartup + 15f;
                        while (new Vector2Int(Screen.width, Screen.height) != size && Time.realtimeSinceStartup < deadline)
                            yield return null;
                        Assert.That(new Vector2Int(Screen.width, Screen.height), Is.EqualTo(size));
                        yield return null;
                        Canvas.ForceUpdateCanvases();
                        var hud = Find("YC.Presentation.GameplayInteractionHudView");
                        foreach (var pile in hud.GetComponentsInChildren(Type.GetType("YC.Presentation.CardPileView, Assembly-CSharp", true)))
                        {
                            var viewport = (RectTransform)Field(pile, "viewport");
                            var content = (RectTransform)Property(pile, "Content");
                            var header = (RectTransform)Field(pile, "protectedHeader");
                            var isHand = Field(pile, "kind").ToString() == "Hand";
                            if (isHand)
                            {
                                var handCanvas = content.GetComponent<Canvas>();
                                var bottomCanvas = ((RectTransform)Property(coverFrame, "BottomBar")).GetComponent<Canvas>();
                                Assert.That(handCanvas.sortingOrder, Is.GreaterThan(bottomCanvas.sortingOrder),
                                    "手牌应显示在底栏底图上方。");
                                Assert.That(handCanvas.sortingOrder, Is.LessThan(pageSortingOrder),
                                    "手牌不能遮住卡牌选择弹窗。");
                                Assert.That(header.GetComponent<Canvas>().sortingOrder, Is.GreaterThan(handCanvas.sortingOrder));
                            }
                            foreach (RectTransform slot in content)
                            {
                                Assert.That(slot.rect.size, Is.EqualTo(new Vector2(78, 112)));
                                Assert.That(slot.localScale, Is.EqualTo(Vector3.one * (float)Property(pile, "SlotScale")));
                                var center = viewport.InverseTransformPoint(slot.position);
                                Assert.That(center.x, Is.InRange(viewport.rect.xMin, viewport.rect.xMax));
                                Assert.That(center.y, Is.InRange(viewport.rect.yMin, viewport.rect.yMax));
                                if (isHand)
                                {
                                    var point = RectTransformUtility.WorldToScreenPoint(null,
                                        slot.TransformPoint(new Vector2(0, slot.rect.yMin + 2)));
                                    var hits = new List<RaycastResult>();
                                    EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
                                    Assert.That(hits.Count, Is.GreaterThan(0), "卡牌下沿应可接收输入。");
                                    var pileContent = hits[0].gameObject.transform;
                                    Assert.That(pileContent.IsChildOf(content), Is.True,
                                        "卡牌伸出区域的下沿被其他 UI 挡住：" + hits[0].gameObject.name);
                                }
                                if (header == null) continue;
                                var corners = new Vector3[4]; slot.GetWorldCorners(corners);
                                var labelRect = header.rect;
                                if ((bool)Field(pile, "protectHeaderTextOnly"))
                                {
                                    var label = header.GetComponent<Text>();
                                    var textWidth = Mathf.Min(labelRect.width, label.preferredWidth);
                                    labelRect.x += (labelRect.width - textWidth) * ((int)label.alignment % 3) * .5f;
                                    labelRect.width = textWidth;
                                }
                                var minX = float.PositiveInfinity;
                                var maxX = float.NegativeInfinity;
                                var top = float.NegativeInfinity;
                                foreach (var corner in corners)
                                {
                                    var localCorner = header.InverseTransformPoint(corner);
                                    minX = Mathf.Min(minX, localCorner.x);
                                    maxX = Mathf.Max(maxX, localCorner.x);
                                    top = Mathf.Max(top, localCorner.y);
                                }
                                if (maxX >= labelRect.xMin && minX <= labelRect.xMax)
                                    Assert.That(top, Is.LessThanOrEqualTo(labelRect.yMin + .1f),
                                        scene + " 卡牌不能遮住区域标题文字。");
                            }
                        }
                        var settings = (Button)Property(coverFrame, "SettingsButton");
                        Assert.That(FirstHit((RectTransform)settings.transform), Is.SameAs(settings.gameObject));
                        var screenshot = Path.Combine(CaptureDirectory("Logs/UIRegression-20260928"),
                            "MainAfterCover-" + scene + "-" + size.x + "x" + size.y + ".png");
                        var requestedAt = DateTime.UtcNow;
                        ScreenCapture.CaptureScreenshot(screenshot);
                        deadline = Time.realtimeSinceStartup + 15f;
                        while ((!File.Exists(screenshot) || File.GetLastWriteTimeUtc(screenshot) < requestedAt) && Time.realtimeSinceStartup < deadline)
                            yield return null;
                        Assert.That(File.Exists(screenshot) && File.GetLastWriteTimeUtc(screenshot) >= requestedAt, Is.True);
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator CardPicker_HorizontalWheelAndScrollbarReachLastCard()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            var frame = Property(hud, "Frame");
            var registry = Property(hud, "DialogRegistry");
            var controller = Find("YC.Presentation.MobileCityInteractionController");
            var state = (GameState)Property(Field(controller, "session"), "State");
            var initialPhase = state.Phase;
            var initialPlayer = state.CurrentPlayerId;
            var catalog = Property(registry, "CardVisualCatalog");
            var fronts = (Array)Field(catalog, "characterFrontTextures");
            Assert.That(fronts.Length, Is.GreaterThan(0));
            var page = registry.GetType().GetMethod("InstantiateEffectDialogShell")
                .Invoke(registry, new object[] { Property(frame, "ContentRect"), false, false, true }) as Component;
            Assert.That(page, Is.Not.Null);
            try
            {
                Assert.That((bool)Property(page, "IsCardPicker"), Is.True);
                page.GetType().GetMethod("PrepareForUse").Invoke(page, new object[]
                    { "卡牌滚动输入验证", "卡牌选择窗口", new Vector2(1774, 887), Vector2.zero, true });
                page.GetType().GetMethod("ConfigureHeading").Invoke(page, new object[]
                    { "卡牌滚动输入验证", "真实素材组件诊断，不提交游戏命令。", 34f,
                        "Diagnostic Title", "Diagnostic Description", 40 });
                page.GetType().GetMethod("ConfigureCardScroll").Invoke(page,
                    new object[] { new Vector2(180, 255), 180f });
                var cards = new List<Component>();
                // 仅增加运行中的显示项，用正式素材验证九张卡的溢出，不构造规则候选或结算命令。
                for (var i = 0; i < 9; i++)
                {
                    var card = (Component)page.GetType().GetMethod("CreateFacilityCard").Invoke(page, null);
                    card.name = "滚动诊断卡牌 " + i;
                    var image = (RawImage)Property(card, "CardImage");
                    image.texture = (Texture)Property(fronts.GetValue(i % fronts.Length), "Texture");
                    Assert.That(image.texture, Is.Not.Null);
                    image.color = Color.white;
                    ((Text)Property(card, "FallbackLabel")).gameObject.SetActive(false);
                    ((Button)Property(card, "Button")).onClick.RemoveAllListeners();
                    cards.Add(card);
                }
                yield return null;
                yield return null;
                RefreshPageLayout(page);
                var scroll = (ScrollRect)Property(page, "OptionScroll");
                Assert.That(scroll.horizontal, Is.True);
                Assert.That(scroll.vertical, Is.False);
                Assert.That(scroll.content.rect.width, Is.GreaterThan(scroll.viewport.rect.width));
                var firstCardButton = (Button)Property(cards[0], "Button");
                var wheelHit = FirstRaycast((RectTransform)firstCardButton.transform);
                Assert.That(wheelHit.gameObject, Is.SameAs(firstCardButton.gameObject));
                var beforeWheel = scroll.horizontalNormalizedPosition;
                var wheel = new PointerEventData(EventSystem.current)
                {
                    position = wheelHit.screenPosition,
                    pointerCurrentRaycast = wheelHit,
                    scrollDelta = Vector2.up
                };
                var wheelReceiver = ExecuteEvents.ExecuteHierarchy(wheelHit.gameObject, wheel, ExecuteEvents.scrollHandler);
                Assert.That(wheelReceiver, Is.SameAs(scroll.gameObject),
                    "卡面上的滚轮事件必须沿真实层级到达本页 ScrollRect。");
                yield return null;
                Assert.That(scroll.horizontalNormalizedPosition, Is.GreaterThan(beforeWheel),
                    "垂直滚轮输入必须推动横向列表。");

                var bar = scroll.horizontalScrollbar;
                Assert.That(bar, Is.Not.Null);
                Assert.That(bar.gameObject.activeInHierarchy, Is.True);
                var dragHit = FirstRaycast(bar.handleRect);
                Assert.That(dragHit.gameObject, Is.Not.Null);
                Assert.That(ExecuteEvents.GetEventHandler<IDragHandler>(dragHit.gameObject),
                    Is.SameAs(bar.gameObject), "滚动条滑块的实际首命中必须可以拖动本页滚动条。");
                var drag = new PointerEventData(EventSystem.current)
                {
                    button = PointerEventData.InputButton.Left,
                    position = dragHit.screenPosition,
                    pressPosition = dragHit.screenPosition,
                    pointerPressRaycast = dragHit,
                    pointerCurrentRaycast = dragHit,
                    pointerDrag = bar.gameObject,
                    useDragThreshold = false
                };
                ExecuteEvents.ExecuteHierarchy(dragHit.gameObject, drag, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.beginDragHandler);
                drag.dragging = true;
                var slidingArea = (RectTransform)bar.handleRect.parent;
                var right = RectTransformUtility.WorldToScreenPoint(drag.pressEventCamera,
                    slidingArea.TransformPoint(new Vector2(slidingArea.rect.xMax - .5f, slidingArea.rect.center.y)));
                var start = drag.position;
                for (var step = 1; step <= 3; step++)
                {
                    var next = Vector2.Lerp(start, right, step / 3f);
                    drag.delta = next - drag.position;
                    drag.position = next;
                    ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.dragHandler);
                    yield return null;
                }
                ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.pointerUpHandler);
                drag.dragging = false;
                RefreshPageLayout(page);
                Assert.That(scroll.horizontalNormalizedPosition, Is.GreaterThan(.99f),
                    "滚动条拖至右端应访问列表末尾，不能直接赋值代替输入。");
                var lastImage = ((RawImage)Property(cards[8], "CardImage")).rectTransform;
                AssertHorizontalInside(lastImage, scroll.viewport, "末张卡面");
                AssertVerticalInside(lastImage, scroll.viewport, "末张卡面");
                Assert.That(state.Phase, Is.EqualTo(initialPhase));
                Assert.That(state.CurrentPlayerId, Is.EqualTo(initialPlayer));
            }
            finally
            {
                frame.GetType().GetMethod("ReleasePage").Invoke(frame, new object[] { page.gameObject });
                UnityEngine.Object.Destroy(page.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator RightColumn_OverflowScrollsOnlyInsideTheActionBody()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var hud = Find("YC.Presentation.GameplayInteractionHudView");
                var column = Child(hud.transform, "Actions Column") as RectTransform;
                Assert.That(column.GetComponent<ScrollRect>(), Is.Null, "整列不得滚动。");
                var tabs = Find("YC.Presentation.UiMainActionTabs");
                var buttons = (Button[])Field(tabs, "tabs");
                buttons[1].onClick.Invoke();
                var group = Child(hud.transform, "Quick Actions") as RectTransform;
                var original = group.GetChild(0);
                Transform last = original;
                // 只扩充运行中的 UI 内容验证溢出，不伪造业务候选或写回资产。
                for (var i = 0; i < 12; i++)
                    last = UnityEngine.Object.Instantiate(original, group, false);
                yield return null;
                yield return null;
                Canvas.ForceUpdateCanvases();
                var face = (GameObject)Property(Property(hud, "ActionPanelView"), "MainFaceObject");
                var scroll = face.GetComponent<ScrollRect>();
                Assert.That(scroll, Is.Not.Null);
                Assert.That(scroll.content.rect.height, Is.GreaterThan(scroll.viewport.rect.height));
                foreach (var name in new[] { "Cooperation Region", "Deck Row", "Action Tabs", "Action Region" })
                    AssertVerticalInside(Child(column, name) as RectTransform, column, name);
                var outerBefore = column.anchoredPosition;
                scroll.StopMovement();
                scroll.verticalNormalizedPosition = 0;
                yield return null;
                Canvas.ForceUpdateCanvases();
                AssertVerticalInside(last as RectTransform, scroll.viewport, "快速行动末项必须可达");
                Assert.That(column.anchoredPosition, Is.EqualTo(outerBefore), "正文滚动不能移动右列。");
                foreach (var name in new[] { "Cooperation Region", "Deck Row", "Action Tabs", "Action Region" })
                    AssertVerticalInside(Child(column, name) as RectTransform, column, name);
                buttons[2].onClick.Invoke();
                yield return null;
                Assert.That(group.gameObject.activeSelf, Is.False, "隐藏长列表不应占据城市样式页。");
                AssertVerticalInside(Child(column, "City Style Actions") as RectTransform,
                    scroll.viewport, "城市样式页");
                buttons[0].onClick.Invoke();
                yield return null;
                AssertVerticalInside(Child(column, "Main Actions") as RectTransform,
                    scroll.viewport, "主要行动页");
            }
        }

        [UnityTest]
        public IEnumerator BuildFlow_CandidateSlotPaymentAndSubmitCommitsOnce()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-dev-right-card-smoke-no-dialog") < 0)
                Assert.Ignore("使用项目已有的建设行动测试起点；所有选择与提交走正式 UI 和规则。");
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var controller = Find("YC.Presentation.MobileCityInteractionController");
            var state = (GameState)Property(Field(controller, "session"), "State");
            var player = state.FindPlayer((int)Field(controller, "localPlayerId"));
            var before = player.BuiltFacilityIds.Count;
            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            ((Button)Property(Property(hud, "ActionPanelView"), "BuildButton")).onClick.Invoke();
            Component Page() => (Component)Field(controller, "facilityBuildPage");
            var candidates = (RectTransform)Field(Page(), "candidates");
            var cards = candidates.GetComponentsInChildren(Type.GetType("YC.Presentation.FacilityEffectCardView, Assembly-CSharp", true));
            Button slot = null;
            foreach (var card in cards)
            {
                ((Button)Property(card, "Button")).onClick.Invoke();
                slot = Array.Find((Button[])Field(Page(), "citySlots"), item => item.interactable);
                if (slot != null) break;
            }
            Assert.That(slot, Is.Not.Null, "正式报价必须至少存在一个合法建设候选。");
            Assert.That(player.BuiltFacilityIds.Count, Is.EqualTo(before), "预选不能提交建设。");
            slot.onClick.Invoke();
            var payments = (RectTransform)Field(Page(), "payments");
            var payment = Array.Find(payments.GetComponentsInChildren<Button>(), item => item.interactable);
            Assert.That(payment, Is.Not.Null);
            payment.onClick.Invoke();
            var confirm = (Button)Field(Page(), "confirm");
            Assert.That(confirm.interactable, Is.True);
            var submit = confirm.onClick;
            submit.Invoke(); submit.Invoke();
            yield return null;
            Assert.That(player.BuiltFacilityIds.Count, Is.EqualTo(before + 1));
            Assert.That((bool)Field(controller, "buildDialogActive"), Is.False);
        }

        [UnityTest]
        public IEnumerator SecondEffect_RealFirstSettlementThenContinueOrFinishThroughNewPage()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-dev-right-card-smoke-no-dialog") < 0)
                Assert.Ignore("角色效果兼容路径使用项目已有行动阶段测试起点。");
            foreach (var finish in new[] { true, false })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
                yield return null;
                var controller = Find("YC.Presentation.MobileCityInteractionController");
                var session = Field(controller, "session");
                var state = (GameState)Property(session, "State");
                var local = (int)Field(controller, "localPlayerId");
                var player = state.FindPlayer(local);
                state.StartPlayerId = local;
                var cardId = player.HandCardIds.Find(id => CharacterCardDatabase.Get(id).TemplateId == CharacterCardDatabase.Cannot);
                Assert.That(cardId, Is.Not.Null);
                player.HandCardIds.Remove(cardId);
                player.CoveredCharacterCardId = cardId;
                // 兼容旧存档的真实第一效果结算，由规则服务产生第二效果状态，禁止手填 Pending。
                var result = new CharacterCardService().Use(state, local, cardId, CharacterEffectModes.Strategy,
                    string.Empty, new Dictionary<string, string> { [CharacterEffectParameterKeys.OfferSecondEffect] = "true" });
                Assert.That(result.IsValid, Is.True, result.Reason);
                Assert.That(state.PendingCharacterEffect.ChoiceType, Is.EqualTo(CharacterPendingChoiceTypes.SecondEffectDecision));
                var pendingBeforeInvalid = state.PendingCharacterEffect;
                var invalidRepeat = new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = local, TargetId = cardId };
                invalidRepeat.Parameters["effectMode"] = CharacterEffectModes.Strategy;
                var rejected = (CommandResult)session.GetType().GetMethod("Submit").Invoke(session, new object[] { invalidRepeat });
                Assert.That(rejected.Succeeded, Is.False, "第二效果决定尚未确认时不得重复执行已完成的一侧。");
                Assert.That(state.PendingCharacterEffect, Is.SameAs(pendingBeforeInvalid));
                InvokePrivate(controller, "SynchronizeInteractionFromState");
                yield return null;
                var dialog = Field(controller, "characterUsePage");
                var page = (Component)Field(Field(dialog, "shell"), "view");
                Assert.That(page.gameObject.activeInHierarchy, Is.True);
                var hand = Find("YC.Presentation.CharacterHandPanel");
                hand.GetType().GetMethod("OpenCoveredCharacterCardViewer").Invoke(hand, null);
                yield return null;
                Assert.That(state.PendingCharacterEffect.ChoiceType, Is.EqualTo(CharacterPendingChoiceTypes.SecondEffectDecision),
                    "查看资料不能替代继续或结束决定。");
                hand.GetType().GetMethod("CloseCharacterCardViewer").Invoke(hand, null);
                yield return null;
                Assert.That(page.gameObject.activeInHierarchy, Is.True, "资料关闭应恢复真实待选页面。");
                var choice = Child(page.transform, "Character Effect Option " + (finish ? 2 : 1)).GetComponent<Button>();
                Assert.That(choice.interactable, Is.True);
                var callback = choice.onClick;
                callback.Invoke(); callback.Invoke();
                yield return null;
                if (finish)
                {
                    Assert.That(state.PendingCharacterEffect, Is.Null);
                    Assert.That(player.CoveredCharacterCardId, Is.Empty);
                    Assert.That(player.DiscardCardIds.FindAll(id => id == cardId).Count, Is.EqualTo(1));
                    hand.GetType().GetMethod("OpenDiscardPreview").Invoke(hand, null);
                    var discardShell = Field(hand, "discardPage");
                    var discardView = (Component)Field(discardShell, "view");
                    var discardCard = Child(discardView.transform, "Discard Card 0").GetComponent(
                        Type.GetType("YC.Presentation.FacilityEffectCardView, Assembly-CSharp", true));
                    var detail = (Button)Property(discardCard, "Button");
                    var scrollPosition = (float)Property(discardView, "ScrollPosition");
                    OpenCardPreview(detail);
                    yield return null;
                    Assert.That(discardView.gameObject.activeInHierarchy, Is.False);
                    var viewer = Find("YC.Presentation.CardViewer");
                    viewer.GetType().GetMethod("Close").Invoke(viewer, null);
                    yield return null;
                    Assert.That(discardView.gameObject.activeInHierarchy, Is.True);
                    Assert.That((float)Property(discardView, "ScrollPosition"), Is.EqualTo(scrollPosition).Within(.01f));
                    Assert.That(EventSystem.current.currentSelectedGameObject, Is.SameAs(detail.gameObject));
                    Child(discardView.transform, "Cancel Selection").GetComponent<Button>().onClick.Invoke();
                    Assert.That(player.DiscardCardIds.FindAll(id => id == cardId).Count, Is.EqualTo(1));
                }
                else
                {
                    Assert.That(state.PendingCharacterEffect, Is.Null, "继续后应接入统一效果参数页。");
                    var request = state.EffectRuntime.InteractionRequests.Find(item => item.Status == "open" &&
                        item.AnsweringPlayerId == local && item.CandidateIds.Count > 0 && item.PromptKey.Contains("cannot"));
                    Assert.That(request, Is.Not.Null, "剩余计谋必须产生正式请求。");
                    var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = local };
                    command.Parameters["interactionId"] = request.InteractionId;
                    command.Parameters["expectedRevision"] = request.StateRevision.ToString();
                    command.OptionIds.Add(request.CandidateIds[0]);
                    var settled = (CommandResult)session.GetType().GetMethod("Submit").Invoke(session, new object[] { command });
                    Assert.That(settled.Succeeded, Is.True, settled.Validation?.Reason);
                    Assert.That(player.CoveredCharacterCardId, Is.Empty);
                    Assert.That(player.UsedCharacterThisRound, Is.True);
                }
            }
        }

        [UnityTest]
        public IEnumerator CardViewer_FormalQuickActionCancelsWithoutCommandThenUsesPlotOnce()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-dev-right-card-smoke-no-dialog") < 0)
                Assert.Ignore("沿用项目行动阶段测试起点；不代表从大厅开始的联机验收。");
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var controller = Find("YC.Presentation.MobileCityInteractionController");
            var session = Field(controller, "session");
            var state = (GameState)Property(session, "State");
            var local = (int)Field(controller, "localPlayerId");
            var player = state.FindPlayer(local);
            var cardId = player.HandCardIds.Find(id => CharacterCardDatabase.Get(id).TemplateId == CharacterCardDatabase.Cannot);
            Assert.That(cardId, Is.Not.Null);
            player.HandCardIds.Remove(cardId);
            player.CoveredCharacterCardId = cardId;
            InvokePrivate(controller, "SynchronizeInteractionFromState");
            yield return null;
            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            var modules = Property(hud, "MainModules");
            var coveredPile = (Component)Field(modules, "coveredPile");
            var covered = ((RectTransform)Property(coveredPile, "Content")).GetComponentInChildren<Button>();
            Assert.That(covered, Is.Not.Null, "盖牌应通过动态卡槽打开查看页。");
            Assert.That(covered.interactable, Is.True);
            Assert.That(FirstHit(covered.transform as RectTransform), Is.SameAs(covered.gameObject));
            var revision = state.EffectRuntime.StateRevision;
            covered.onClick.Invoke();
            yield return null;
            var viewer = Find("YC.Presentation.CardViewer");
            Assert.That(viewer, Is.Not.Null);
            Assert.That(Property(viewer,"Mode").ToString(), Is.EqualTo("Inspect"));
            Assert.That((string)Property(viewer,"CardId"), Is.Empty);
            Assert.That(((Button)Property(viewer,"PlotButton")).gameObject.activeInHierarchy, Is.False);
            viewer.GetType().GetMethod("Close").Invoke(viewer,null);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));

            var tabs = Find("YC.Presentation.UiMainActionTabs");
            ((Button[])Field(tabs,"tabs"))[1].onClick.Invoke();
            var use = (Button)Property(Property(hud,"ActionPanelView"),"UseCharacterButton");
            Assert.That(use.interactable, Is.True);
            use.onClick.Invoke(); yield return null;
            viewer = Find("YC.Presentation.CardViewer");
            Assert.That(Property(viewer,"Mode").ToString(), Is.EqualTo("UseCharacter"));
            ((Button)Property(viewer,"CancelButton")).onClick.Invoke();
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(cardId));
            use.onClick.Invoke(); yield return null;
            viewer = Find("YC.Presentation.CardViewer");
            var click = ((Button)Property(viewer,"PlotButton")).onClick;
            click.Invoke(); click.Invoke();
            yield return null;
            state = (GameState)Property(session,"State");
            var request = state.EffectRuntime.InteractionRequests.Find(item => item.Status == "open" &&
                item.AnsweringPlayerId == local && item.CandidateIds.Count > 0 && item.PromptKey.Contains("cannot"));
            Assert.That(request, Is.Not.Null,"计谋按钮应进入正式坎诺特计谋的选择链。");
            Assert.That((bool)Property(viewer,"IsShowing"), Is.False);
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = local };
            command.Parameters["interactionId"] = request.InteractionId;
            command.Parameters["expectedRevision"] = request.StateRevision.ToString();
            command.OptionIds.Add(request.CandidateIds[0]);
            var result = (CommandResult)session.GetType().GetMethod("Submit").Invoke(session,new object[] { command });
            Assert.That(result.Succeeded, Is.True,result.Validation?.Reason);
            state = (GameState)Property(session,"State");
            Assert.That(state.FindPlayer(local).UsedCharacterThisRound, Is.True);
            Assert.That(state.FindPlayer(local).DiscardCardIds.FindAll(id => id == cardId).Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CardViewer_HandFacilityAndDiscardDetailsStayReadOnlyAndReturn()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-dev-right-card-smoke-no-dialog") < 0)
                Assert.Ignore("沿用项目行动阶段测试起点；不代表从大厅开始的联机验收。");
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var controller = Find("YC.Presentation.MobileCityInteractionController");
                var session = Field(controller, "session");
                var state = (GameState)Property(session, "State");
                var player = state.FindPlayer((int)Field(controller, "localPlayerId"));
                var hand = Find("YC.Presentation.CharacterHandPanel");
                var id = player.HandCardIds[0];
                var card = Child(hand.transform, "Card Slot: " + id);
                var pointerType = Type.GetType("YC.Presentation.CardPointerInteraction, Assembly-CSharp", true);
                var click = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, clickCount = 1 };
                var revision = state.EffectRuntime.StateRevision;
                Assert.That(card, Is.Not.Null, "正式手牌实例必须存在。");
                GameObject hit = null;
                for (var frame = 0; frame < 120; frame++)
                {
                    hit = FirstHit((RectTransform)card);
                    if (hit != null && hit.transform.IsChildOf(card)) break;
                    yield return null;
                }
                Assert.That(hit, Is.Not.Null, scene + " 手牌没有命中：" + card.position);
                Assert.That(hit.transform.IsChildOf(card), Is.True, scene + " 手牌首命中：" + hit.name);
                ExecuteEvents.Execute(card.gameObject, click, ExecuteEvents.pointerClickHandler);
                yield return null;
                AssertInspectAndClose(state, revision);
                Assert.That(player.HandCardIds, Does.Contain(id));

                var hud = Find("YC.Presentation.GameplayInteractionHudView");
                var build = Property(hud, "BuildInfoPanel");
                var slots = (Array)Property(Property(build, "View"), "CityBoardSlots");
                Component facilityPointer = null;
                foreach (var slot in slots)
                {
                    var root = (RectTransform)Property(slot, "Root");
                    var pointer = root.GetComponent(pointerType);
                    var image = root.GetComponentInChildren<RawImage>();
                    if (pointer != null && image != null && image.texture != null) { facilityPointer = pointer; break; }
                }
                Assert.That(facilityPointer, Is.Not.Null, "必须使用当前城市的真实设施卡入口。");
                Assert.That(FirstHit((RectTransform)facilityPointer.transform).transform.IsChildOf(facilityPointer.transform), Is.True);
                ExecuteEvents.Execute(facilityPointer.gameObject, click, ExecuteEvents.pointerClickHandler);
                yield return null;
                AssertInspectAndClose(state, revision);

                // 明确构造弃牌可见数据；入口、列表、详情及返回均使用正式运行实例。
                player.HandCardIds.Remove(id);
                player.DiscardCardIds.Add(id);
                InvokePrivate(controller, "SynchronizeInteractionFromState");
                yield return null;
                var modules = Property(hud, "MainModules");
                var discard = (Button)Field(modules, "discardPreviewButton");
                Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(FirstHit((RectTransform)discard.transform)),
                    Is.SameAs(discard.gameObject), "弃牌区的子图形命中必须归属正式入口按钮。");
                discard.onClick.Invoke();
                yield return null;
                var page = (Component)Field(Field(hand, "discardPage"), "view");
                var candidate = Child(page.transform, "Discard Card 0");
                var details = (Button)Property(candidate.GetComponent(Type.GetType("YC.Presentation.FacilityEffectCardView, Assembly-CSharp", true)), "Button");
                OpenCardPreview(details);
                yield return null;
                AssertInspectAndClose(state, revision);
                yield return null;
                Assert.That(page.gameObject.activeInHierarchy, Is.True, "关闭详情应返回有效弃牌列表。");
                Assert.That(player.DiscardCardIds.FindAll(value => value == id).Count, Is.EqualTo(1));
                Child(page.transform, "Cancel Selection").GetComponent<Button>().onClick.Invoke();
            }
        }

        [UnityTest]
        public IEnumerator InformationPages_FormalSettingsRulebookPagingImagePanAndLongLog()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var settings = Find("YC.Presentation.GameSettingsMenuController");
                var settingsView = Field(settings, "view");
                settings.GetType().GetMethod("Open").Invoke(settings, null);
                ((Button)Property(settingsView, "RulebookButton")).onClick.Invoke();
                yield return null;
                var rulebook = Field(settings, "rulebookViewer");
                var ruleView = Field(rulebook, "view");
                var imageController = Property(ruleView, "ImageViewer");
                var imageView = Field(imageController, "view");
                var next = (Button)Property(imageView, "NextButton");
                var previous = (Button)Property(imageView, "PreviousButton");
                var pageCount = (int)Property(ruleView, "PageCount");
                for (var page = 1; page < pageCount; page++)
                {
                    next.onClick.Invoke();
                    Assert.That((int)Property(imageController, "PageIndex"), Is.EqualTo(page));
                    Assert.That(((RawImage)Property(imageView, "Image")).texture, Is.Not.Null);
                }
                Assert.That(next.interactable, Is.False, "规则书末页不能越界。");
                previous.onClick.Invoke();
                Assert.That((int)Property(imageController, "PageIndex"), Is.EqualTo(pageCount - 2));
                imageController.GetType().GetMethod("SetZoom").Invoke(imageController, new object[] { 2f });
                yield return null;
                Canvas.ForceUpdateCanvases();
                var panel = (RectTransform)Property(imageView, "PanelTransform");
                var imageRect = (RectTransform)Property(imageView, "ImageTransform");
                var before = panel.anchoredPosition;
                var imageBefore = imageRect.anchoredPosition;
                var scroll = panel.GetComponentInChildren<ScrollRect>();
                var position = RectTransformUtility.WorldToScreenPoint(null, scroll.viewport.TransformPoint(scroll.viewport.rect.center));
                var drag = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = position };
                scroll.OnBeginDrag(drag);
                drag.position += new Vector2(25, 30);
                scroll.OnDrag(drag); scroll.OnEndDrag(drag); scroll.StopMovement();
                Assert.That(imageRect.anchoredPosition, Is.Not.EqualTo(imageBefore), "缩放后的图片应可内部平移。");
                Assert.That(panel.anchoredPosition, Is.EqualTo(before), "图片平移不能拖动整个窗口。");
                ((Button)Property(imageView, "CloseButton")).onClick.Invoke();

                var controller = Find("YC.Presentation.MobileCityInteractionController");
                var state = (GameState)Property(Field(controller, "session"), "State");
                var revision = state.EffectRuntime.StateRevision;
                for (var i = 0; i < 80; i++) state.Logs.Add(new GameLogEntry { Sequence = 10000 + i,
                    PlayerId = state.Players[0].PlayerId, Message = "查看器滚动诊断记录 " + i });
                settings.GetType().GetMethod("Open").Invoke(settings, null);
                ((Button)Property(settingsView, "ActionLogButton")).onClick.Invoke();
                yield return null;
                Canvas.ForceUpdateCanvases();
                var log = Field(settings, "actionLogViewer");
                var logView = Field(log, "view");
                var content = (RectTransform)Property(logView, "ContentTransform");
                var logScroll = content.GetComponentInParent<ScrollRect>();
                Assert.That(content.rect.height, Is.GreaterThan(logScroll.viewport.rect.height));
                logScroll.verticalNormalizedPosition = 0f;
                Canvas.ForceUpdateCanvases();
                var last = (RectTransform)content.GetChild(content.childCount - 1);
                AssertVerticalInside(last, logScroll.viewport, "日志末项");
                ((Button)Property(logView, "CloseButton")).onClick.Invoke();
                Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision), "资料操作不得发出游戏命令。");
            }
        }

        private static void OpenCardPreview(Button card)
        {
            ExecuteEvents.Execute(card.gameObject, new PointerEventData(EventSystem.current)
                { button = PointerEventData.InputButton.Right, clickCount = 1 }, ExecuteEvents.pointerClickHandler);
        }

        private static void AssertInspectAndClose(GameState state, int revision)
        {
            var viewer = Find("YC.Presentation.CardViewer");
            Assert.That(viewer, Is.Not.Null);
            Assert.That(Property(viewer, "Mode").ToString(), Is.EqualTo("Inspect"));
            Assert.That((string)Property(viewer, "CardId"), Is.Empty);
            Assert.That(((Button)Property(viewer, "PlotButton")).gameObject.activeInHierarchy, Is.False);
            viewer.GetType().GetMethod("Close").Invoke(viewer, null);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision), "资料查看不能提交命令。");
        }

        private static void AssertVerticalInside(RectTransform child, RectTransform parent, string context)
        {
            var a = new Vector3[4]; var b = new Vector3[4];
            child.GetWorldCorners(a); parent.GetWorldCorners(b);
            Assert.That(a[0].y, Is.GreaterThanOrEqualTo(b[0].y - .5f), context + " 底端越界");
            Assert.That(a[2].y, Is.LessThanOrEqualTo(b[2].y + .5f), context + " 顶端越界");
        }

        private static void AssertHorizontalInside(RectTransform child, RectTransform parent, string context)
        {
            var a = new Vector3[4]; var b = new Vector3[4];
            child.GetWorldCorners(a); parent.GetWorldCorners(b);
            Assert.That(a[0].x, Is.GreaterThanOrEqualTo(b[0].x - .5f), context + " 左端越界");
            Assert.That(a[2].x, Is.LessThanOrEqualTo(b[2].x + .5f), context + " 右端越界");
        }

        private static void RefreshPageLayout(Component page)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)page.transform);
            Canvas.ForceUpdateCanvases();
        }

        private static object InvokePrivate(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static string CaptureDirectory(string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--yc-ui-evidence-output");
            var directory = Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback);
            Directory.CreateDirectory(directory);
            return directory;
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
            return FirstRaycast(target).gameObject;
        }

        private static RaycastResult FirstRaycast(RectTransform target)
        {
            Canvas.ForceUpdateCanvases();
            var point = RectTransformUtility.WorldToScreenPoint(null,
                target.TransformPoint(target.rect.center));
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, results);
            return results.Count == 0 ? default(RaycastResult) : results[0];
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
