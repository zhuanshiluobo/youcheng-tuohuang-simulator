#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode
{
    public static class GameplayInteractionHudPlayModeProbe
    {
        public static void Run()
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "SampleScene")
            {
                throw new InvalidOperationException("交互 HUD Probe 必须在 SampleScene 运行。");
            }

            if (UnityEngine.Object.FindObjectsOfType<EventSystem>().Length != 1)
            {
                throw new InvalidOperationException("SampleScene 必须且只能有一个 EventSystem。");
            }

            var rootsBefore = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects().Length;
            if (rootsBefore != 6)
            {
                throw new InvalidOperationException("SampleScene 运行初始化后必须保持 6 个场景根。");
            }

            var hud = UnityEngine.Object.FindObjectOfType<GameplayInteractionHudView>();
            var city = UnityEngine.Object.FindObjectOfType<MobileCityInteractionController>();
            var resourceBoards = UnityEngine.Object.FindObjectsOfType<ResourceCounterBoard>();
            var buildInfoPanels = UnityEngine.Object.FindObjectsOfType<BuildInfoPanel>();
            var characterHandPanels = UnityEngine.Object.FindObjectsOfType<CharacterHandPanel>();
            if (resourceBoards.Length != 1 || buildInfoPanels.Length != 1 || characterHandPanels.Length != 1)
            {
                throw new InvalidOperationException("HUD 内必须且只能有一个资源卡板、建设面板和手牌面板。");
            }

            var reason = string.Empty;
            if (hud == null || city == null ||
                hud.ResourceCounterBoard != resourceBoards[0] ||
                hud.BuildInfoPanel != buildInfoPanels[0] ||
                hud.CharacterHandPanel != characterHandPanels[0] ||
                !characterHandPanels[0].TryValidateConfiguration(out reason) ||
                !GameplayInteractionHudView.TryValidateSceneBinding(
                    hud,
                    city,
                    resourceBoards[0],
                    buildInfoPanels[0],
                    out reason))
            {
                throw new InvalidOperationException("交互 HUD 与 MobileCity 三方接线无效：" + reason);
            }

            var cardVisualCatalog = hud.DialogRegistry.CardVisualCatalog;
            RequireCardVisualCatalogCoverage(cardVisualCatalog);
            RequirePendingBuildGhost(buildInfoPanels[0], cardVisualCatalog);
            RequireCharacterHandModal(characterHandPanels[0]);
            RequireResourceCounterBoard(resourceBoards[0]);
            RequireLocalPlayerResourceRefresh(city, resourceBoards[0]);
            RequirePersistentHudCoexistence(hud);
            RunDialogMigrationChecks(hud, rootsBefore);

            var promptField = typeof(MobileCityInteractionController).GetField(
                "promptPresenter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var actionField = typeof(MobileCityInteractionController).GetField(
                "actionPanel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var prompt = promptField.GetValue(city);
            var action = actionField.GetValue(city);
            if (prompt == null || action == null)
            {
                throw new InvalidOperationException("MobileCity 未绑定 Prompt/Action 行为对象。");
            }

            prompt.GetType().GetMethod("SetPrompt", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(prompt, new object[] { "SampleScene 交互 HUD 冒烟" });
            prompt.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(prompt, new object[] { true });
            if (hud.PromptView.PromptText.text != "SampleScene 交互 HUD 冒烟")
            {
                throw new InvalidOperationException("Prompt 文本更新失败。");
            }

            hud.ActionPanelView.FlipButton.onClick.Invoke();
            if (hud.ActionPanelView.MainFaceObject.activeSelf ||
                !hud.ActionPanelView.CardFaceObject.activeSelf ||
                hud.ActionPanelView.CardImage.texture != hud.ActionPanelView.HintCardTexture)
            {
                throw new InvalidOperationException("ActionPanel 主面/提示卡面翻转失败。");
            }

            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects().Length != rootsBefore)
            {
                throw new InvalidOperationException("Prompt/ActionPanel 绑定不应在运行时新增场景根。");
            }

            var verifyDetachedViewerEntry = false;
            if (verifyDetachedViewerEntry)
            {
            hud.ActionPanelView.CardImageButton.onClick.Invoke();
            var viewer = GameObject.Find("Hint Card Image Viewer");
            if (viewer == null)
            {
                throw new InvalidOperationException("提示卡 Zoomable viewer 入口未打开。");
            }

            var viewerCanvas = GameObject.Find("Hint Card Viewer Canvas");
            if (viewerCanvas != null)
            {
                UnityEngine.Object.DestroyImmediate(viewerCanvas);
            }
            }

            RequirePersistentHudCoexistence(hud);
            RequireRootCount(rootsBefore, "Prompt/ActionPanel 验证完成后");
            if (UnityEngine.Object.FindObjectsOfType<EventSystem>().Length != 1)
            {
                throw new InvalidOperationException("Play 探针完成后必须仍只有一个 EventSystem。");
            }
        }

        private static void RunDialogMigrationChecks(GameplayInteractionHudView hud, int rootsBefore)
        {
            var canvas = hud.Canvas.transform as RectTransform;
            if (canvas == null || hud.DialogRegistry == null)
            {
                throw new InvalidOperationException("HUD 缺少 Canvas 或 GameplayDialogRegistry。");
            }

            var eventChoiceCount = 0;
            var eventDialog = new EventChoiceDialog(hud.DialogRegistry, () => canvas);
            eventDialog.ShowEventCardOptions(
                new EventCardDefinition
                {
                    CardId = "play-probe-event",
                    Name = "Event Play Probe",
                    Description = "EventChoiceDialog runtime prefab probe",
                    ChoiceDescriptions = new List<string> { "Choose" },
                    ChoiceRewards = new List<ResourceSet> { new ResourceSet() }
                },
                "Play Probe",
                Array.Empty<ExplorePaymentChoice>(),
                new Dictionary<string, int>(),
                playerId => playerId.ToString(),
                _ => eventChoiceCount++,
                null);
            var eventView = RequireActiveDialog<EventChoiceDialogView>(
                canvas,
                "Event Choice Overlay");
            RequireDirectCanvasParent(eventView.transform, canvas, "EventChoiceDialog");
            if (eventView.OverlayImage.raycastTarget)
            {
                throw new InvalidOperationException("EventChoiceDialog event-card overlay must pass pointer input through.");
            }

            var eventClick = RequireButton(eventView.transform, "Choice 1").onClick;
            eventClick.Invoke();
            eventClick.Invoke();
            if (eventChoiceCount != 1 || !eventDialog.IsShowing)
            {
                throw new InvalidOperationException("EventChoiceDialog step callback must dispatch once without hiding.");
            }

            eventDialog.Hide();
            if (eventDialog.IsShowing || eventView.gameObject.activeSelf)
            {
                throw new InvalidOperationException("EventChoiceDialog Hide must deactivate its prefab instance immediately.");
            }
            RequireRootCount(rootsBefore, "EventChoiceDialog");

            var cityStyleConfirmCount = 0;
            var cityStyleCallbackObservedHiddenDialog = false;
            var cityStyle = new CityStyleDeclarationPreviewDialog(hud.DialogRegistry, () => canvas);
            var previewStyleId = CityStyleDatabase.MilitaryIndustrialArea;
            var previewFacilityId = FacilityCardDatabase.SourceStoneRefinery;
            cityStyle.Show(new CityStyleOptionsViewModel(
                new List<CityStyleOptionViewModel>
                {
                    new CityStyleOptionViewModel
                    {
                        CityStyleId = previewStyleId,
                        Name = "Play 探针样式",
                        CanDeclare = true
                    }
                }.AsReadOnly(),
                new List<CityBoardSlotViewModel>
                {
                    new CityBoardSlotViewModel(0, previewFacilityId, false)
                }.AsReadOnly(),
                new List<CityStyleMarkerViewModel>().AsReadOnly(),
                previewStyleId,
                (styleId, slots) => new CityStyleSelectionValidationViewModel(
                    true,
                    string.Empty,
                    0,
                    0,
                    slots.Count),
                (styleId, slots) =>
                {
                    cityStyleConfirmCount++;
                    cityStyleCallbackObservedHiddenDialog = !cityStyle.IsShowing;
                    return true;
                },
                null,
                null));
            var cityStyleView = RequireActiveDialog<CityStyleDeclarationPreviewView>(
                canvas,
                "City Style Declaration Preview Canvas");
            RequireDirectCanvasParent(cityStyleView.transform, canvas, "城市样式声明预览");
            if (cityStyleView.OverlayCanvas == null || !cityStyleView.OverlayCanvas.overrideSorting ||
                cityStyleView.OverlayCanvas.sortingOrder != 130 ||
                cityStyleView.CityBoardSlotCount != CityStyleDeclarationPreviewView.RequiredCityBoardSlotCount)
            {
                throw new InvalidOperationException("城市样式声明预览的 Canvas 排序或固定槽位配置无效。");
            }
            var catalog = hud.DialogRegistry.CardVisualCatalog;
            var firstSlot = cityStyleView.GetCityBoardSlot(0);
            if (cityStyleView.CityStyleCardImage.texture != catalog.GetCityStyle(previewStyleId) ||
                cityStyleView.CityBoardImage.texture != catalog.GetCityBoard() ||
                firstSlot.FacilityImage.texture != catalog.GetFacility(previewFacilityId))
            {
                throw new InvalidOperationException("城市样式预览未使用 CardVisualCatalog 的样式卡、城市板或设施卡贴图。");
            }

            var cityStyleConfirm = cityStyleView.ConfirmDeclarationButton.onClick;
            cityStyleConfirm.Invoke();
            cityStyleConfirm.Invoke();
            if (cityStyleConfirmCount != 1 || !cityStyleCallbackObservedHiddenDialog ||
                cityStyle.IsShowing || cityStyleView.gameObject.activeSelf)
            {
                throw new InvalidOperationException("城市样式声明必须先隐藏预览，再且仅分发一次确认回调。");
            }
            RequireRootCount(rootsBefore, "城市样式声明预览");

            var characterCount = 0;
            var character = new CharacterCardEffectChoiceDialog(hud.DialogRegistry, canvas);
            character.ShowOptions(
                "角色效果",
                "Play 探针",
                new[] { new EffectDialogOption("确认角色效果", () => characterCount++) });
            var characterShell = RequireActiveDialog<EffectDialogShellView>(
                canvas,
                "Character Card Effect Overlay");
            RequireDirectCanvasParent(characterShell.transform, canvas, "角色效果");
            var characterClick = RequireButton(characterShell.transform, "Character Effect Option 0").onClick;
            characterClick.Invoke();
            characterClick.Invoke();
            RequireClosedOnce(characterCount, character.IsShowing, characterShell.gameObject, "角色效果");
            RequireRootCount(rootsBefore, "角色效果");

            var facilityCount = 0;
            var facility = new FacilityEffectChoiceDialog(hud.DialogRegistry, canvas);
            facility.ShowOptions(
                "设施效果",
                "Play 探针",
                new[] { new EffectDialogOption("确认设施效果", () => facilityCount++) });
            var facilityShell = RequireActiveDialog<EffectDialogShellView>(
                canvas,
                "Facility Effect Choice Overlay");
            RequireDirectCanvasParent(facilityShell.transform, canvas, "设施效果");
            var facilityClick = RequireButton(facilityShell.transform, "Option 0").onClick;
            facilityClick.Invoke();
            facilityClick.Invoke();
            RequireClosedOnce(facilityCount, facility.IsShowing, facilityShell.gameObject, "设施效果");
            RequireRootCount(rootsBefore, "设施效果");

            var specialCancelCount = 0;
            var special = new SpecialActionChoiceDialog(hud.DialogRegistry, () => canvas);
            special.ShowCompositePayment(3, 3, _ => { }, () => specialCancelCount++);
            var specialShell = RequireActiveDialog<EffectDialogShellView>(
                canvas,
                "Special Action Choice Overlay");
            RequireDirectCanvasParent(specialShell.transform, canvas, "特殊行动");
            var specialCancel = RequireButton(specialShell.transform, "Cancel Special Action Payment").onClick;
            specialCancel.Invoke();
            specialCancel.Invoke();
            RequireClosedOnce(specialCancelCount, special.IsShowing, specialShell.gameObject, "特殊行动");
            RequireRootCount(rootsBefore, "特殊行动");

            var dispatchContinueCount = 0;
            var dispatchFinishCount = 0;
            var dispatch = new DispatchDecisionView(hud.DialogRegistry, () => canvas);
            dispatch.Show(new DispatchDecisionViewModel(
                "调度决策",
                "Play 探针",
                "继续调度",
                "完成调度",
                () => dispatchContinueCount++,
                () => dispatchFinishCount++));
            var dispatchView = RequireActiveDialog<DispatchDecisionDialogView>(
                canvas,
                "Dispatch Decision Overlay");
            RequireDirectCanvasParent(dispatchView.transform, canvas, "调度决策");
            if (dispatchView.ContinueLabel.text != "继续调度" || dispatchView.FinishLabel.text != "完成调度")
            {
                throw new InvalidOperationException("调度决策按钮文案绑定失败。");
            }

            var dispatchContinue = dispatchView.ContinueButton.onClick;
            dispatchContinue.Invoke();
            dispatchContinue.Invoke();
            if (dispatchContinueCount != 1 || dispatchFinishCount != 0 || dispatchView.gameObject.activeSelf)
            {
                throw new InvalidOperationException("调度决策必须先 Hide，再且仅分发一次 Continue 回调。");
            }

            dispatch.Hide();
            RequireRootCount(rootsBefore, "调度决策");
        }

        private static void RequirePersistentHudCoexistence(GameplayInteractionHudView hud)
        {
            if (hud == null || hud.Canvas == null || hud.PromptView == null || hud.ActionPanelView == null ||
                hud.ResourceCounterBoard == null || hud.BuildInfoPanel == null || hud.CharacterHandPanel == null ||
                hud.DialogRegistry == null ||
                UnityEngine.Object.FindObjectOfType<RoundTrackerController>() == null)
            {
                throw new InvalidOperationException("Prompt/Action/Round/Info/Build/DialogRegistry 必须在 Play 中共存。");
            }
        }

        private static void RequireCharacterHandModal(CharacterHandPanel handPanel)
        {
            if (handPanel.View.DiscardCountText.text.Length == 0 ||
                !handPanel.View.DiscardOverlayObject.GetComponent<Image>().raycastTarget)
            {
                throw new InvalidOperationException("手牌弃牌角标或全屏输入遮罩配置无效。");
            }

            handPanel.OpenDiscardPreview();
            if (!handPanel.IsDiscardPreviewOpen)
            {
                throw new InvalidOperationException("弃牌预览窗口无法打开。");
            }
            handPanel.CloseDiscardPreview();
        }

        private static void RequireResourceCounterBoard(ResourceCounterBoard board)
        {
            var resources = new ResourceSet
            {
                Originium = 9,
                OriginiumShard = 99,
                Iron = 12,
                PureOriginium = 3,
                GoldVoucher = 7
            };
            board.Render(resources, false);
            if (board.View.GearCounters[0].TensDigitText.text != "0" ||
                board.View.GearCounters[0].OnesDigitText.text != "9" ||
                board.View.GearCounters[1].TensDigitText.text != "9" ||
                board.View.GearCounters[1].OnesDigitText.text != "9" ||
                board.View.GearCounters[2].TensDigitText.text != "1" ||
                board.View.GearCounters[2].OnesDigitText.text != "2" ||
                board.View.GearCounters[0].TensGearCover == null ||
                board.View.GearCounters[0].OnesGearCover == null ||
                board.View.AuxiliaryCounters[0].AmountText.text != "3" ||
                board.View.AuxiliaryCounters[1].AmountText.text != "7" ||
                board.GetComponentInChildren<Button>(true) != null)
            {
                throw new InvalidOperationException("资源卡板五种资源或只读约束无效。");
            }

            resources.Originium = 10;
            resources.OriginiumShard = 100;
            resources.Iron = 11;
            resources.PureOriginium = 4;
            resources.GoldVoucher = 8;
            board.Render(resources, true);
            var advance = typeof(ResourceCounterBoard).GetMethod(
                "AdvanceAnimations",
                BindingFlags.Instance | BindingFlags.NonPublic);
            advance.Invoke(board, new object[] { 1f });
            if (board.View.GearCounters[0].TensDigitText.text != "1" ||
                board.View.GearCounters[0].OnesDigitText.text != "0" ||
                board.View.GearCounters[1].TensDigitText.text != "0" ||
                board.View.GearCounters[1].OnesDigitText.text != "0" ||
                board.View.GearCounters[2].TensDigitText.text != "1" ||
                board.View.GearCounters[2].OnesDigitText.text != "1" ||
                board.View.AuxiliaryCounters[0].AmountText.text != "4" ||
                board.View.AuxiliaryCounters[1].AmountText.text != "8")
            {
                throw new InvalidOperationException("资源卡板动画结束后数值不精确。");
            }
        }

        private static void RequireLocalPlayerResourceRefresh(
            MobileCityInteractionController city,
            ResourceCounterBoard board)
        {
            var sessionField = typeof(MobileCityInteractionController).GetField(
                "session",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var localPlayerField = typeof(MobileCityInteractionController).GetField(
                "localPlayerId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var refresh = typeof(MobileCityInteractionController).GetMethod(
                "RefreshResourceDisplay",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var session = (YC.Application.Sessions.GameSession)sessionField.GetValue(city);
            var localPlayerId = (int)localPlayerField.GetValue(city);
            var player = session.State.FindPlayer(localPlayerId);
            var beforeOriginium = player.Resources.Originium;
            var beforeShard = player.Resources.OriginiumShard;
            var beforeIron = player.Resources.Iron;
            try
            {
                player.Resources.Originium = 8;
                player.Resources.OriginiumShard = 27;
                player.Resources.Iron = 34;
                refresh.Invoke(city, null);
                var advance = typeof(ResourceCounterBoard).GetMethod(
                    "AdvanceAnimations",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                advance.Invoke(board, new object[] { 1f });
                if (board.View.GearCounters[0].TensDigitText.text != "0" ||
                    board.View.GearCounters[0].OnesDigitText.text != "8" ||
                    board.View.GearCounters[1].TensDigitText.text != "2" ||
                    board.View.GearCounters[1].OnesDigitText.text != "7" ||
                    board.View.GearCounters[2].TensDigitText.text != "3" ||
                    board.View.GearCounters[2].OnesDigitText.text != "4")
                {
                    throw new InvalidOperationException("本地玩家获得资源后，十位／个位读数未刷新。");
                }
            }
            finally
            {
                player.Resources.Originium = beforeOriginium;
                player.Resources.OriginiumShard = beforeShard;
                player.Resources.Iron = beforeIron;
                refresh.Invoke(city, null);
            }
        }

        private static void RequireCardVisualCatalogCoverage(CardVisualCatalog catalog)
        {
            var reason = string.Empty;
            if (catalog == null || !catalog.TryValidateConfiguration(out reason))
            {
                throw new InvalidOperationException("SampleScene CardVisualCatalog 无效：" + reason);
            }

            var facilityIds = new List<string>(FacilityCardDatabase.DefaultSupplyIds);
            facilityIds.AddRange(FacilityCardDatabase.ReserveIds);
            facilityIds.Add(FacilityCardDatabase.EnterpriseOffice);
            if (facilityIds.Count != CardVisualCatalog.ExpectedFacilityCount)
            {
                throw new InvalidOperationException("设施卡查询集合数量异常：" + facilityIds.Count);
            }

            for (var i = 0; i < facilityIds.Count; i++)
            {
                RequireTexture(catalog.GetFacility(facilityIds[i]), "设施卡 " + facilityIds[i]);
            }

            for (var i = 0; i < CityStyleDatabase.DefaultSupplyIds.Count; i++)
            {
                var id = CityStyleDatabase.DefaultSupplyIds[i];
                RequireTexture(catalog.GetCityStyle(id), "城市样式卡 " + id);
            }

            var characterTemplateIds = new[]
            {
                CharacterCardDatabase.Liskarm,
                CharacterCardDatabase.Elysium,
                CharacterCardDatabase.Texas,
                CharacterCardDatabase.Cannot,
                CharacterCardDatabase.TinMan
            };
            for (var i = 0; i < characterTemplateIds.Length; i++)
            {
                RequireTexture(
                    catalog.GetCharacterFront(characterTemplateIds[i]),
                    "角色卡正面 " + characterTemplateIds[i]);
            }

            var playerColors = new[]
            {
                PlayerColor.Red,
                PlayerColor.Blue,
                PlayerColor.Green,
                PlayerColor.Yellow
            };
            for (var i = 0; i < playerColors.Length; i++)
            {
                RequireTexture(catalog.GetCharacterBack(playerColors[i]), "角色卡背面 " + playerColors[i]);
            }

            RequireTexture(catalog.GetCityBoard(), "城市板");
        }

        private static void RequirePendingBuildGhost(BuildInfoPanel panel, CardVisualCatalog catalog)
        {
            var facilityId = FacilityCardDatabase.TradeDistrict;
            panel.SetPendingBuildGhost(true, facilityId, 0, null, null);
            var ghost = GameObject.Find("本地建设虚影");
            var ghostImage = ghost == null ? null : ghost.GetComponentInChildren<RawImage>(true);
            if (ghostImage == null || ghostImage.texture != catalog.GetFacility(facilityId))
            {
                throw new InvalidOperationException("SampleScene 建设虚影未使用 CardVisualCatalog 的设施贴图。");
            }

            panel.SetPendingBuildGhost(false, string.Empty, -1, null, null);
        }

        private static void RequireTexture(Texture2D texture, string label)
        {
            if (texture == null)
            {
                throw new InvalidOperationException(label + "贴图查询为空。");
            }
        }

        private static T RequireActiveDialog<T>(RectTransform canvas, string objectName) where T : Component
        {
            var candidates = canvas.GetComponentsInChildren<T>(true);
            for (var i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] != null && candidates[i].gameObject.name == objectName &&
                    candidates[i].gameObject.activeInHierarchy)
                {
                    return candidates[i];
                }
            }

            throw new InvalidOperationException("缺少活动对话框实例：" + objectName);
        }

        private static Button RequireButton(Transform root, string objectName)
        {
            var button = RequireChild(root, objectName).GetComponent<Button>();
            if (button == null)
            {
                throw new InvalidOperationException("缺少对话框按钮组件：" + objectName);
            }

            return button;
        }

        private static Transform RequireChild(Transform root, string objectName)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == objectName)
                {
                    return transforms[i];
                }
            }

            throw new InvalidOperationException("缺少对话框对象：" + objectName);
        }

        private static void RequireDirectCanvasParent(Transform instance, RectTransform canvas, string label)
        {
            if (instance == null || instance.parent != canvas)
            {
                throw new InvalidOperationException(label + "实例必须直接挂在 HUD Canvas 下。");
            }
        }

        private static void RequireClosedOnce(int callbackCount, bool isShowing, GameObject instance, string label)
        {
            if (callbackCount != 1 || isShowing || (instance != null && instance.activeSelf))
            {
                throw new InvalidOperationException(label + "必须先 Hide，再且仅分发一次回调。");
            }
        }

        private static void RequireRootCount(int expected, string stage)
        {
            var actual = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects().Length;
            if (actual != expected)
            {
                throw new InvalidOperationException(stage + "后场景根数量变化：" + actual + "，期望 " + expected + "。");
            }
        }
    }
}
#endif
