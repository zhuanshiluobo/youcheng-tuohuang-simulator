using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Enterprises;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    /// <summary>正式资产的隔离交互夹具，不替代从菜单进入的可见程序验收。</summary>
    public sealed class PreparationPresentationTests
    {
        private const string HudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject root;
        private Component expansion, hud, registry, frame;
        private object previousFrame;
        private Texture2D texture;
        private Sprite sprite;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("准备阶段界面隔离夹具", typeof(RectTransform), typeof(Canvas));
            root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)root.transform).sizeDelta = new Vector2(1920, 1080);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HudPath);
            Assert.That(prefab, Is.Not.Null);
            var instance = UnityEngine.Object.Instantiate(prefab, root.transform, false);
            expansion = instance.GetComponent(RuntimeType("GameplayExpansionUi"));
            hud = instance.GetComponent(RuntimeType("GameplayInteractionHudView"));
            registry = instance.GetComponentInChildren(RuntimeType("GameplayDialogRegistry"), true);
            frame = (Component)Property(hud, "Frame");
            Assert.That(expansion, Is.Not.Null);
            Assert.That(registry, Is.Not.Null);
            Assert.That(frame, Is.Not.Null);
            var active = RuntimeType("GameplayHudFrame").GetField("active", BindingFlags.Static | BindingFlags.NonPublic);
            previousFrame = active.GetValue(null);
            active.SetValue(null, frame);
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeType("GameplayHudFrame").GetField("active", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, previousFrame);
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (sprite != null) UnityEngine.Object.DestroyImmediate(sprite);
            if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OwnEnterprisePreviewReturnsToSameDraft(bool effectPage)
        {
            var view = PreparationView();
            var request = InteractionRequestProjector.ProjectForPlayer(VisibleInteractionRequestSource.Read(null, view, 1).Single(), 1);
            Set(expansion, "hud", hud);
            Set(expansion, "getView", new Func<GameStateView>(() => view));
            Set(expansion, "getPlayer", new Func<int>(() => 1));
            Set(expansion, "definitions", Definitions());
            if (effectPage)
            {
                Set(frame, "currentRequestId", request.InteractionId);
                Set(frame, "currentRevision", request.StateRevision);
                Set(frame, "currentProjection", request);
            }

            texture = new Texture2D(8, 8);
            sprite = Sprite.Create(texture, new Rect(0, 0, 8, 8), new Vector2(.5f, .5f));
            var dialog = Activator.CreateInstance(RuntimeType("CharacterCardEffectChoiceDialog"), Flags, null,
                new object[] { registry, Property(frame, "ContentRect") }, null);
            var spec = Activator.CreateInstance(RuntimeType("EffectDialogSelectionSpec"), true);
            var selections = new HashSet<string> { "fixture.card.1" };
            int submitted = 0;
            Set(spec, "Request", request);
            Set(spec, "SelectedIds", selections);
            Set(spec, "IsEffectPage", effectPage);
            Set(spec, "IsCardSelection", true);
            Set(spec, "IsCurrent", new Func<bool>(() => true));
            Set(spec, "Label", new Func<string, string>(id => id));
            Set(spec, "CardSprite", new Func<string, Sprite>(_ => sprite));
            Set(spec, "Confirm", new Action(() => submitted++));
            Call(dialog, "ShowSelection", "夹具选择", "夹具", spec, false);
            var page = (Component)Field(Field(dialog, "shell"), "view");
            var slot = (Component)Field(page, "preparationPreviewButton");
            var open = (Button)Property(slot, "Button");
            Assert.That(open.gameObject.activeInHierarchy, Is.True, "中间卡牌请求应提供企业查看入口。");
            var confirm = page.GetComponentsInChildren<Button>(true).Single(button => button.name == "Confirm Selection");
            Assert.That(confirm.interactable, Is.True);
            var scroll = (ScrollRect)Property(page, "OptionScroll");
            scroll.horizontalNormalizedPosition = .4f;
            var beforeScroll = scroll.horizontalNormalizedPosition;
            open.onClick.Invoke();
            var preview = (Component)Field(expansion, "preparationPreview");
            Assert.That(preview, Is.Not.Null);
            Assert.That(preview.gameObject.activeInHierarchy, Is.True);
            Assert.That(page.gameObject.activeInHierarchy, Is.False);
            Assert.That(submitted, Is.Zero);
            var resume = (Button)Property(preview, "ReadOnlyContinueButton");
            resume.onClick.Invoke();
            Assert.That(page.gameObject.activeInHierarchy, Is.True, "查看后应恢复原选择页。");
            Assert.That(preview.gameObject.activeInHierarchy, Is.False);
            Assert.That(selections, Is.EquivalentTo(new[] { "fixture.card.1" }));
            Assert.That(scroll.horizontalNormalizedPosition, Is.EqualTo(beforeScroll).Within(.01f));
            Assert.That(confirm.interactable, Is.True);
            Assert.That(submitted, Is.Zero);
            Call(dialog, "Hide");
        }

        [Test]
        public void PreviewAuthorizationRejectsStaleOrForeignRequestsAndAcceptsDisplayOrder()
        {
            var view = PreparationView();
            var request = InteractionRequestProjector.ProjectForPlayer(VisibleInteractionRequestSource.Read(null, view, 1).Single(), 1);
            Set(expansion, "getView", new Func<GameStateView>(() => view));
            Set(expansion, "getPlayer", new Func<int>(() => 1));
            Set(expansion, "definitions", Definitions());
            Assert.That((bool)Call(expansion, "CanPreviewPreparationEnterprises", request), Is.True);
            var displayed = GameStateCloneService.DeepClone(request);
            displayed.CandidateIds.Reverse();
            Assert.That((bool)Call(expansion, "CanPreviewPreparationEnterprises", displayed), Is.True);
            displayed.MinSelections = 0;
            Assert.That((bool)Call(expansion, "CanPreviewPreparationEnterprises", displayed), Is.False);
            displayed.MinSelections = request.MinSelections;
            displayed.StateRevision++;
            Assert.That((bool)Call(expansion, "CanPreviewPreparationEnterprises", displayed), Is.False);
            view.Preparation.OwnEnterpriseCandidates[0].DefinitionId = "fixture.unknown.company";
            Assert.That((bool)Call(expansion, "CanPreviewPreparationEnterprises", request), Is.False);
            view.ViewerPlayerId = 2;
            Assert.That((bool)Call(expansion, "CanPreviewPreparationEnterprises", request), Is.False);
        }

        [Test]
        public void ChangedRequestClosesTheOldPreviewWithoutSubmitting()
        {
            var view = PreparationView();
            var request = InteractionRequestProjector.ProjectForPlayer(VisibleInteractionRequestSource.Read(null, view, 1).Single(), 1);
            Set(expansion, "hud", hud);
            Set(expansion, "getView", new Func<GameStateView>(() => view));
            Set(expansion, "getPlayer", new Func<int>(() => 1));
            Set(expansion, "definitions", Definitions());
            int returned = 0;
            Assert.That((bool)Call(expansion, "OpenPreparationEnterprisePreview", request, Property(frame, "ContentRect"),
                new Func<bool>(() => true), new Action(() => returned++)), Is.True);
            var preview = (Component)Field(expansion, "preparationPreview");
            Assert.That(preview.gameObject.activeInHierarchy, Is.True);
            view.Interactions[0].StateRevision++;
            view.StateRevision++;
            Call(expansion, "RefreshPreparationEnterprisePreview");
            Assert.That(preview.gameObject.activeInHierarchy, Is.False);
            Assert.That(returned, Is.Zero);
        }

        [Test]
        public void CooperationShowsEveryBoardAndHidesRowsNoLongerPresent()
        {
            var view = new GameStateView();
            // 源 Prefab 的外层尺寸由场景决定；夹具为滚动区域提供独立的可见画布。
            var area = new GameObject("合作区夹具画布", typeof(RectTransform));
            area.transform.SetParent(root.transform, false);
            ((RectTransform)area.transform).sizeDelta = new Vector2(600, 260);
            var scroll = (ScrollRect)Field(expansion, "cooperationScroll");
            var scrollRect = (RectTransform)scroll.transform;
            scrollRect.SetParent(area.transform, false);
            scrollRect.anchorMin = Vector2.zero; scrollRect.anchorMax = Vector2.one;
            scrollRect.offsetMin = Vector2.zero; scrollRect.offsetMax = Vector2.zero;
            view.Players.Add(new PlayerStateView { PlayerId = 1, Color = PlayerColor.Red });
            view.Players.Add(new PlayerStateView { PlayerId = 2, Color = PlayerColor.Blue });
            view.ItemZones.Zones.Add(new ZoneStateView { Zone = new ZoneInstanceState { ZoneId = "boards", TemplateId = "core.zone.enterprise_display" } });
            for (int i = 0; i < 7; i++) view.ItemZones.Items.Add(new ItemInstanceState {
                ItemId = "fixture.board." + i, DefinitionId = "fixture.company." + i, ZoneId = "boards" });
            for (int player = 1; player <= 2; player++)
            {
                var track = "fixture.track." + player;
                view.ItemZones.Zones.Add(new ZoneStateView { Zone = new ZoneInstanceState { ZoneId = track,
                    ParentKind = ZoneParentKind.Item, ParentId = "fixture.board.6", LocalZoneId = "cooperation_" + player } });
                view.ItemZones.Items.Add(new ItemInstanceState { ItemId = "fixture.marker." + player, ZoneId = track,
                    SlotId = "level_4", OwnerSubject = ItemSubjectRef.Player(player) });
            }
            var layout = (LayoutElement)Field(expansion, "cooperationContentLayout");
            var baseline = layout.preferredHeight;
            Call(expansion, "RefreshCooperation", view);
            var rows = ((IEnumerable)Field(expansion, "cooperationRows")).Cast<object>().ToArray();
            Assert.That(rows.Length, Is.EqualTo(7));
            Assert.That(rows.Select(row => ((GameObject)Field(row, "Root")).activeSelf), Is.All.True);
            Assert.That(rows.Select(row => ((Text)Field(row, "Name")).text),
                Is.EqualTo(Enumerable.Range(0, 7).Select(i => "fixture.company." + i)));
            var finalGrades = (Image[])Field(rows[6], "Grades");
            var masks = (Sprite[])Field(expansion, "cooperationMasks");
            Assert.That(finalGrades[4].enabled, Is.True);
            Assert.That(finalGrades[4].sprite, Is.SameAs(masks[12]), "第七家应显示红蓝玩家的实际合作等级。");
            Assert.That(layout.preferredHeight, Is.GreaterThan(baseline));
            var header = (GameObject)Field(expansion, "cooperationLevelHeaders");
            Canvas.ForceUpdateCanvases();
            Assert.That(scroll.vertical, Is.True);
            Assert.That(scroll.viewport.rect.height, Is.GreaterThan(0));
            var originalHeaderPosition = header.transform.position;
            scroll.verticalNormalizedPosition = 0;
            Canvas.ForceUpdateCanvases();
            var corners = new Vector3[4];
            ((RectTransform)((GameObject)Field(rows[6], "Root")).transform).GetWorldCorners(corners);
            var minY = corners.Min(corner => scroll.viewport.InverseTransformPoint(corner).y);
            var maxY = corners.Max(corner => scroll.viewport.InverseTransformPoint(corner).y);
            var diagnostic = " viewport=" + scroll.viewport.rect + " content=" + scroll.content.rect +
                " anchor=" + scroll.content.anchoredPosition + " scale=" + scroll.content.localScale +
                " last=" + minY + ".." + maxY + " position=" + scroll.verticalNormalizedPosition;
            Assert.That(minY, Is.GreaterThanOrEqualTo(scroll.viewport.rect.yMin - 1f), "滚到底部时最后一家应能完全显示。" + diagnostic);
            Assert.That(maxY, Is.LessThanOrEqualTo(scroll.viewport.rect.yMax + 1f), "滚到底部时最后一家应在视口内。" + diagnostic);
            Assert.That(Vector3.Distance(header.transform.position, originalHeaderPosition), Is.LessThan(.01f), "滚动企业行时列头应保持原位。");
            view.ItemZones.Items.RemoveAll(item => item.ZoneId == "boards" && item.ItemId != "fixture.board.0" && item.ItemId != "fixture.board.1");
            Call(expansion, "RefreshCooperation", view);
            Assert.That(rows.Take(2).Select(row => ((GameObject)Field(row, "Root")).activeSelf), Is.All.True);
            Assert.That(rows.Skip(2).Select(row => ((GameObject)Field(row, "Root")).activeSelf), Is.All.False);
            Assert.That(layout.preferredHeight, Is.EqualTo(baseline));
        }

        [TestCase("Assets/Scenes/SampleScene.unity")]
        [TestCase("Assets/Scenes/ThreePlayerScene.unity")]
        public void ActualSceneKeepsCooperationReferences(string path)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                var instances = scene.GetRootGameObjects().SelectMany(item =>
                    item.GetComponentsInChildren(RuntimeType("GameplayExpansionUi"), true)).ToArray();
                Assert.That(instances.Length, Is.EqualTo(1));
                var actual = instances[0];
                var scroll = (ScrollRect)Field(actual, "cooperationScroll");
                var content = (RectTransform)Field(actual, "cooperationContent");
                var names = (Text[])Field(actual, "cooperationNames");
                var header = (GameObject)Field(actual, "cooperationLevelHeaders");
                Assert.That(scroll, Is.Not.Null);
                Assert.That(scroll.content, Is.SameAs(content));
                Assert.That(scroll.viewport, Is.Not.Null);
                Assert.That(scroll.viewport.GetComponent<RectMask2D>(), Is.Not.Null);
                Assert.That(names.All(name => name != null && name.transform.parent.parent == content), Is.True);
                Assert.That(header.transform.IsChildOf(content), Is.False);
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        private static EnterpriseDefinitions Definitions()
        {
            var result = new EnterpriseDefinitions();
            result.Register(new EnterpriseDefinition { DefinitionId = "fixture.company", RuntimeId = "fixture.company",
                Levels = Enumerable.Range(0, 6).Select(level => new EnterpriseLevelDefinition { Level = level }).ToList() });
            return result;
        }

        private static GameStateView PreparationView()
        {
            var view = new GameStateView { GameId = "preparation-ui-fixture", ViewerRole = GameStateViewerRole.Player,
                ViewerPlayerId = 1, StateRevision = 5, Preparation = new PreparationStateView() };
            view.Preparation.OwnEnterpriseCandidates.Add(new EnterpriseCandidateCardView { ItemId = "fixture.board", DefinitionId = "fixture.company" });
            view.Preparation.Requests.Add(new PreparationRequestContextView { InteractionId = "fixture.request", OwnerEffectId = "fixture.effect",
                CandidateSetId = "fixture.candidates", CandidateSetVersion = 1, StateRevision = 5 });
            view.Interactions.Add(new InteractionView { InteractionId = "fixture.request", OwnerEffectId = "fixture.effect",
                SourceNodeId = "fixture.node", Kind = "fixture.card.selection", AnsweringPlayerId = 1, Visibility = "owner",
                PromptKey = "fixture.prompt", CandidateSetId = "fixture.candidates", CandidateSetVersion = 1,
                CandidateIds = new List<string> { "fixture.card.1", "fixture.card.2" }, MinSelections = 1, MaxSelections = 1,
                AnswerSchema = "candidate_ids", Status = "open", StateRevision = 5 });
            return view;
        }

        private static Type RuntimeType(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Field(object source, string name) => source.GetType().GetField(name, Flags).GetValue(source);
        private static void Set(object source, string name, object value) => source.GetType().GetField(name, Flags).SetValue(source, value);
        private static object Property(object source, string name) => source.GetType().GetProperty(name, Flags).GetValue(source);
        private static object Call(object source, string name, params object[] args) => source.GetType().GetMethod(name, Flags).Invoke(source, args);
    }
}
