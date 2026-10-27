using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 原6917项目备份正规恢复及实际HUD资产的隔离组件回归，不计正常Player/完整局验收。
    public sealed class SettlementColdLayoutR34Tests
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const string HudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private static Type Production(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value) ?? value.GetType().GetProperty(name, Flags)?.GetValue(value);
        private static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw(); throw; }
        }
        private static void Record(string name, object value)
        {
            var folder = Environment.GetEnvironmentVariable("QY017_R34_OUTPUT") ?? throw new InvalidOperationException("R34输出未指定");
            using (var file = new FileStream(Path.Combine(folder, name + ".json"), FileMode.CreateNew))
            using (var writer = new StreamWriter(file)) writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
        }
        private SceneRuntimeCatalogScope catalogs;
        private GameObject root, canvasHost;
        [UnityTearDown] public IEnumerator Release()
        {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (canvasHost != null) UnityEngine.Object.DestroyImmediate(canvasHost);
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
            catalogs?.Dispose(); catalogs = null;
        }

        [Test] public void SampleSceneReferencesTheTestedHudAndOriginalStripTemplate()
        {
            var path = "Assets/Scenes/SampleScene.unity";
            var scene = SceneManager.GetSceneByPath(path); bool owned = !scene.IsValid() || !scene.isLoaded;
            if (owned) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                var controller = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren(Production("MobileCityInteractionController"), true)).Single();
                var hud = (Component)Read(controller, "gameplayInteractionHud");
                Assert.That(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(hud), Is.EqualTo(HudPath));
                var strip = hud.GetComponentsInChildren(Production("SettlementEffectStripView"), true).Single();
                var template = (Component)Read(strip, "chipTemplate");
                var sourceTemplate = PrefabUtility.GetCorrespondingObjectFromSource(template.gameObject) ?? template.gameObject;
                var prefabStrip = AssetDatabase.LoadAssetAtPath<GameObject>(HudPath).GetComponentsInChildren(Production("SettlementEffectStripView"), true).Single();
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(strip), Is.SameAs(prefabStrip));
                var modifications = (PrefabUtility.GetPropertyModifications(hud) ?? Array.Empty<PropertyModification>())
                    .Where(m => m.target == prefabStrip || m.target == prefabStrip.transform)
                    .Select(m => new { m.propertyPath, m.value }).ToArray();
                Record("实际场景与受测资产来源", new { scene = path, hud = HudPath, chip = AssetDatabase.GetAssetPath(sourceTemplate),
                    minimum = Read(template, "minimumTextWidth"), maximum = Read(template, "maximumTextWidth"),
                    stripSceneOverrides = modifications, assetWrites = 0, normalUiAcceptance = false });
            }
            finally { if (owned) EditorSceneManager.CloseScene(scene, true); }
        }

        private sealed class Context
        {
            internal GameSession Session; internal GameStateView View;
            internal object Hud, Expansion, Strip;
            internal readonly List<GameCommand> Commands = new List<GameCommand>();
            internal readonly List<bool> Accepted = new List<bool>();
            internal IDictionary Chips => (IDictionary)Read(Strip, "chips");
        }
        private Context BindRealSavedHud()
        {
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            CharacterCardDatabase.Initialize(pack.CreateCharacters()); EventCardDatabase.InitializeExternal(pack.CreateEvents());
            FacilityCardDatabase.InitializeExternal(pack.CreateFacilities()); CityStyleDatabase.InitializeExternal(pack.CreateCityStyles());
            SpecialActionDatabase.InitializeExternal(pack.CreateSpecialActions());
            Production("UiTheme").GetMethod("Initialize", Flags).Invoke(null, new object[] {
                AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", Production("UiThemeCatalog")) });
            var backup = Environment.GetEnvironmentVariable("QY017_R34_BACKUP") ?? throw new InvalidOperationException("6917项目备份未指定");
            var payload = JObject.Parse((string)JObject.Parse(File.ReadAllText(backup))["Payload"]);
            var archive = (string)payload["ArchiveJson"]; var raw = JObject.Parse(archive)["Snapshot"]["State"];
            var config = raw["RuleConfiguration"].ToObject<GameRuleConfiguration>();
            // 复用018已验的正式服务装配函数，不调用其测试或改写其证据/状态。
            var configured = typeof(QY018D09SnapshotRecoveryR13Tests).GetMethod("Configure", Flags).Invoke(null,
                new object[] { pack, config, (string)raw["MapId"], (int)raw["EventDeckSeed"] });
            var registry = (EffectRegistry)configured.GetType().GetField("Item1").GetValue(configured);
            var handlers = (List<IGameCommandHandler>)configured.GetType().GetField("Item2").GetValue(configured);
            var recovered = HostRecoveryService.RecoverJson(archive, registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            var session = new GameSession(recovered.State); session.ConfigurePaymentSourceQueries(registry);
            foreach (var handler in handlers) session.RegisterHandler(handler);
            var continuation = SimulationConfirmedRecoveryInstaller.ReadContinuation((string)payload["ContinuationJson"]);
            Assert.That(session.TryPrepareLocalRecoveryState(recovered.State, continuation, out var prepared, out var reason), Is.True, reason);
            Assert.That(session.TryCommitPreparedRecovery(prepared, out reason), Is.True, reason);
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(6917));
            var c = new Context { Session = session, View = session.ProjectFor(GameStateViewer.Player(3)) };
            root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(HudPath));
            if (root.GetComponentInParent<Canvas>() == null && root.GetComponentInChildren<Canvas>() == null)
            {
                canvasHost = new GameObject("R34隔离Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvasHost.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                root.transform.SetParent(canvasHost.transform, false);
            }
            c.Hud = root.GetComponent(Production("GameplayInteractionHudView")); c.Expansion = Read(c.Hud, "ExpansionUi");
            var catalog = ActiveContentCatalogBuilder.Build(pack, config);
            Call(c.Hud, "ConfigureGameplayPresentation", catalog.TextCatalog, new Dictionary<string, string>());
            Call(c.Expansion, "Configure", new Func<GameStateView>(() => c.View), new Func<int>(() => 3), null,
                new Dictionary<string, CharacterModeContract>(), new Action<GameCommand>(command => {
                    c.Commands.Add(command); var accepted = session.Submit(command); c.Accepted.Add(accepted.Succeeded);
                    Assert.That(accepted.Succeeded, Is.True, accepted.Validation?.Reason);
                }));
            // 原消费者在本帧自然布局完成前绑定真实存档投影，沿同一RefreshSettlementBar/BindWindow链。
            Call(c.Expansion, "Refresh"); c.Strip = Read(c.Expansion, "settlementStrip");
            c.Strip.GetType().GetField("reducedMotion", Flags).SetValue(c.Strip, true);
            return c;
        }

        [UnityTest] public IEnumerator ColdBindingAndNarrowViewportScrollToARealEnabledOptionAndSubmitOnce()
        {
            catalogs = new SceneRuntimeCatalogScope(); yield return new EnterPlayMode();
            var c = BindRealSavedHud();
            var viewport = (RectTransform)Read(c.Strip, "viewport"); var content = (RectTransform)Read(c.Strip, "content");
            var initialWidth = viewport.rect.width; var count = c.Chips.Count;
            Assert.That(count, Is.GreaterThan(0));
            yield return null; yield return null;
            var stripRect = (RectTransform)((Component)c.Strip).transform;
            var template = (Component)Read(c.Strip, "chipTemplate"); var minimum = (float)Read(template, "minimumTextWidth");
            var inset = (float)Read(c.Strip, "horizontalInset");
            stripRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, inset * 2 + minimum / 2);
            Call(c.Expansion, "Refresh"); yield return null; yield return null;
            Assert.That(viewport.rect.width, Is.GreaterThan(0).And.LessThan(minimum));
            Assert.That(content.rect.width, Is.GreaterThan(viewport.rect.width));
            Assert.That(c.Chips.Count, Is.EqualTo(count));
            var scroll = (ScrollRect)Read(c.Strip, "scrollRect");
            var events = EventSystem.current ?? new GameObject("R34隔离指针系统", typeof(EventSystem)).GetComponent<EventSystem>();
            var before = scroll.horizontalNormalizedPosition;
            var wheel = new PointerEventData(events) { scrollDelta = new Vector2(0, before > .5f ? -20 : 20) };
            ExecuteEvents.Execute(((Component)c.Strip).gameObject, wheel, ExecuteEvents.scrollHandler);
            yield return null;
            Assert.That(scroll.horizontalNormalizedPosition, Is.Not.EqualTo(before), "原ScrollRect必须实际移动内容");
            var option = c.View.CharacterSettlementOptions.Single(o => o.IsEnabled && o.VisibleToViewer &&
                o.Presentation.IconKey == "core:settlement.fund_invest");
            var chip = (Component)c.Chips[option.OptionId]; Assert.That(chip, Is.Not.Null);
            var pointer = new PointerEventData(events) { pointerId = 34, button = PointerEventData.InputButton.Left };
            for (int i = 0; i < 2; i++)
            {
                ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            }
            Assert.That(c.Commands.Count, Is.EqualTo(1)); Assert.That(c.Accepted.Single(), Is.True);
            Assert.That(c.Session.State.EffectRuntime.StateRevision, Is.EqualTo(6927));
            Record("冷绑定与窄视口原控件", new { initialWidth, laidOutNarrowWidth = viewport.rect.width, minimum,
                contentWidth = content.rect.width, optionCount = count, beforeScroll = before, afterScroll = scroll.horizontalNormalizedPosition,
                command = c.Commands.Single(), finalRevision = c.Session.State.EffectRuntime.StateRevision,
                inputBoundary = "组件隔离的原ExecuteEvents链，不冒物理命中/正常菜单验收；未强刷布局、未改游戏状态" });
            if (events.name == "R34隔离指针系统") UnityEngine.Object.DestroyImmediate(events.gameObject);
        }

        [UnityTest] public IEnumerator UnreadyResizeAndReactivationKeepFreshBindingsWithoutDispatch()
        {
            catalogs = new SceneRuntimeCatalogScope(); yield return new EnterPlayMode();
            var c = BindRealSavedHud(); var rect = (RectTransform)((Component)c.Strip).transform;
            var viewport = (RectTransform)Read(c.Strip, "viewport"); var count = c.Chips.Count;
            var ids = c.Chips.Keys.Cast<string>().OrderBy(s => s).ToArray(); var normalWidth = rect.rect.width;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 0);
            Call(c.Expansion, "Refresh"); Assert.That(c.Chips.Count, Is.EqualTo(count));
            var unreadyWidth = viewport.rect.width;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, normalWidth);
            yield return null; yield return null;
            Call(c.Expansion, "Refresh");
            ((Component)c.Strip).gameObject.SetActive(false); yield return null;
            ((Component)c.Strip).gameObject.SetActive(true); Call(c.Expansion, "Refresh");
            yield return null; yield return null;
            Assert.That(c.Chips.Keys.Cast<string>().OrderBy(s => s).ToArray(), Is.EqualTo(ids));
            Assert.That(c.Commands, Is.Empty); Assert.That(c.Session.State.EffectRuntime.StateRevision, Is.EqualTo(6917));
            var restoredCount = c.Chips.Count;
            Call(c.Strip, "SetScope", "R34-另一会话负例", 4);
            Assert.That(c.Chips.Count, Is.Zero, "显式身份切换仍须清理旧绑定");
            Assert.That(c.Commands, Is.Empty);
            Record("未就绪尺寸与重新激活", new { originalWidth = normalWidth, isolatedUnreadyWidth = unreadyWidth,
                restoredWidth = viewport.rect.width, optionCount = restoredCount, afterScopeChange = c.Chips.Count, commandCount = c.Commands.Count,
                originalFailureWidthWasNotRecorded = true, gameStateChanged = false, forcedCanvasOrLayoutUpdate = false });
        }
    }
}
