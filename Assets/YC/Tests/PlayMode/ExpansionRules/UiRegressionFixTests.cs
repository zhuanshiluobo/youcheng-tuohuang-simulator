using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 明确隔离：只读恢复R2封存副本，不接管原程序、不写真实存档，不计正常局覆盖。
    public sealed class UiRegressionFixTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target) ?? target.GetType().GetProperty(name, Flags)?.GetValue(target);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args);
        private GameObject hud;
        private object dialog;
        private sealed class Measurement { public string Phase; public int Frame; public UiRegressionMetrics.Sample Sample; }
        private readonly List<Measurement> samples = new List<Measurement>();
        private string phase;
        private static object restoredResult;
        private void Record(UiRegressionMetrics.Sample sample) => samples.Add(new Measurement { Phase = phase, Frame = Time.frameCount, Sample = sample });
        private static string Output => Environment.GetCommandLineArgs().SkipWhile(a => a != "-ycUiRegressionEvidence").Skip(1).First();
        private static GameState RestoreOriginal(out EffectRegistry registry)
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "prompt/企业家内容实施规划-20261019/实施记录/QY-019-HOTSEAT-G1-R2/a001-修复构建完整局/本局自动存档只读副本-auto-1.json");
            var wrapper = JObject.Parse(File.ReadAllText(path));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)wrapper["Payload"]);
            save.Validate();
            // 使用正式恢复装配，包含round.mainline.node等宿主原语，不另造不完整Registry。
            var result = T("GameSessionBootstrapper").GetMethod("Restore").Invoke(null, new object[] { null, save });
            restoredResult = result;
            registry = (EffectRegistry)Read(result, "EffectRegistry");
            var restored = ((GameSession)Read(result, "Session")).State;
            Assert.That(restored.GameId, Is.EqualTo("2bb30318366e4d2abd13f5984e3eb828"));
            Assert.That(restored.EffectRuntime.StateRevision, Is.EqualTo(443));
            return restored;
        }

        [UnityTest]
        public IEnumerator DeclarationProfileFromSealedR2Copy()
        {
            var state = RestoreOriginal(out _);
            var hostBefore = JsonConvert.SerializeObject(state);
            var theme = UnityEditor.AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog"));
            T("UiTheme").GetMethod("Initialize").Invoke(null, new[] { theme });
            hud = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            yield return null;
            var registry = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            var frame = Read(hud.GetComponent(T("GameplayInteractionHudView")), "Frame");
            dialog = Activator.CreateInstance(T("CityStyleDeclarationPreviewDialog"), Flags, null,
                new object[] { registry, new Func<RectTransform>(() => (RectTransform)Read(frame, "ContentRect")) }, null);
            var context = new Context { State = state };
            var view = new View();
            var interaction = new CityStyleInteraction(context, new NoCommand(), view, new InteractionFlowCoordinator(),
                new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)), () => true, () => true, () => string.Empty, _ => { }, () => { });
            UiRegressionMetrics.Recorded += Record; UiRegressionMetrics.Enabled = true;
            foreach (var run in new[] { "首次打开", "再次打开" })
            {
                phase = run;
                interaction.OpenPreview(""); Call(dialog, "Show", view.Model);
                using (UiRegressionMetrics.Measure("declare.layout")) Canvas.ForceUpdateCanvases();
                phase = run + "-稳定静置";
                for (int i = 0; i < 4; i++) { yield return new WaitForSecondsRealtime(.3f); Call(dialog, "RefreshProjection", false); }
                Call(dialog, "Hide"); yield return null;
            }
            phase = "恢复同身份的新状态对象";
            interaction.OpenPreview(""); Call(dialog, "Show", view.Model);
            context.State = GameStateCloneService.DeepClone(state);
            yield return new WaitForSecondsRealtime(.3f); Call(dialog, "RefreshProjection", false);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(hostBefore), "只读定位不得推进原档状态");
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, "宣告页隔离分段.json"), JsonConvert.SerializeObject(new { Isolated = true, NormalAcceptance = false,
                state.GameId, state.ContentPackHash, Revision = state.EffectRuntime.StateRevision, NoPreparedDraft = true,
                ScenePrefab = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab", Samples = samples }, Formatting.Indented));
            Assert.That(samples.Count(x => x.Phase == "首次打开-稳定静置" && x.Sample.Phase == "declare.options"), Is.Zero);
        }

        [UnityTest]
        public IEnumerator PublicFiveSlotsFollowViewedCityAtSameRevisionAndRetireOldCallbacks()
        {
            var state = RestoreOriginal(out _);
            var before = JsonConvert.SerializeObject(state);
            var session = (GameSession)Read(restoredResult, "Session");
            var visible = session.ProjectFor(GameStateViewer.Player(1));
            var theme = UnityEditor.AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog"));
            T("UiTheme").GetMethod("Initialize").Invoke(null, new[] { theme });
            hud = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            yield return null;
            var ui = hud.GetComponent(T("GameplayExpansionUi"));
            var hv = hud.GetComponent(T("GameplayInteractionHudView"));
            var frame = Read(hv, "Frame");
            Call(frame, "ConfigureGameplayPresentation", Read(restoredResult, "GameplayTextCatalog"), Read(restoredResult, "GameplayDefinitionNames"));
            Call(frame, "BindGameplaySnapshot", visible);
            Call(ui, "Configure", new Func<GameStateView>(() => visible), new Func<int>(() => 1),
                Read(restoredResult, "EnterpriseDefinitions"), Read(restoredResult, "CharacterModes"), new Action<GameCommand>(_ => Assert.Fail("只读查看不得提交")));
            Call(ui, "Refresh");
            var roots = (RectTransform[])Read(ui, "entrepreneurSlotRoots");
            var own = visible.Entrepreneurs.Players.Single(p => p.PlayerId == 1);
            var other = visible.Entrepreneurs.Players.Single(p => p.PlayerId == 2);
            var oldCallback = (Action)Read(roots[0].GetComponent(T("GameplayCardSlotView")), "clicked");
            Call(ui, "RefreshViewedEntrepreneurCards", visible, 2);
            Assert.That((int)Read(ui, "entrepreneurViewedPlayerId"), Is.EqualTo(2));
            Assert.That(((UnityEngine.UI.RawImage)Read(roots[0].GetComponent(T("GameplayCardSlotView")), "Artwork")).texture.name,
                Is.EqualTo(((Sprite)T("ExternalContentRuntime").GetMethod("GetDeclaredCardArtwork").Invoke(null, new object[] {other.DefinitionId, ""})).texture.name));
            oldCallback(); Assert.That(Read(ui, "entrepreneurViewer"), Is.Null, "旧城市的捕获回调必须失效");
            roots[0].GetComponent<UnityEngine.UI.Button>().onClick.Invoke(); yield return null;
            var viewer = Read(ui, "entrepreneurViewer");
            Assert.That((bool)Read(viewer, "IsShowing"), Is.True);
            Call(ui, "RefreshViewedEntrepreneurCards", visible, 1);
            Assert.That((bool)Read(viewer, "IsShowing"), Is.False, "切城关闭旧卡详情");
            roots[1].GetComponent<UnityEngine.UI.Button>().onClick.Invoke(); yield return null;
            Assert.That((bool)Read(viewer, "IsShowing"), Is.True);
            var chosen = own.RoundSlots.OrderBy(s => s.RoundNumber).First();
            var candidate = chosen.Candidates.Single(c => c.CandidateId == chosen.SelectedCandidateId);
            Assert.That(((Sprite)Read(viewer, "DisplayedSprite")).name,
                Is.EqualTo(((Sprite)T("ExternalContentRuntime").GetMethod("GetDeclaredCardArtwork").Invoke(null, new object[] {candidate.EntityDefinitionId, chosen.ActiveFaceId})).name));
            Call(viewer, "Close");
            roots[2].GetComponent<UnityEngine.UI.Button>().onClick.Invoke(); yield return null;
            Assert.That((bool)Read(Read(ui, "entrepreneurBrowser"), "IsShowing"), Is.True);
            Assert.That(hud.GetComponentsInChildren<Component>().Count(v => v != null && v.GetType().Name == "PlayerAutomationId" &&
                ((string)Read(v, "Id")).StartsWith("selection.card.")),
                Is.EqualTo(own.RoundSlots.OrderBy(s => s.RoundNumber).ElementAt(1).Candidates.Count));
            Call(ui, "RefreshViewedEntrepreneurCards", visible, 2);
            Assert.That((bool)Read(Read(ui, "entrepreneurBrowser"), "IsShowing"), Is.False);
            Call(ui, "RefreshViewedEntrepreneurCards", visible, 1);
            Assert.That(visible.ViewerPlayerId, Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }
        [Test]
        public void UnifiedSpecialActionRefreshesOnlyAfterAccurateLocalApplication()
        {
            var state = RestoreOriginal(out _);
            var context = new Context {State = state}; var view = new View();
            var port = new ResultPort();
            var action = new AbilityActionOption { ActionId = "regression.special", OptionId = "current-option", EntryKind = "special", Enabled = true,
                SourceRef = new AbilitySourceRef(), VersionRef = new AbilityVersionRef(), RequestRef = new AbilityActionRequestRef() };
            action.SourceRef.ItemId = "public-source";
            var visible = new GameStateView {HasAbilityActionCatalog = true}; visible.AbilityActions.Add(action);
            var interaction = new CityStyleInteraction(context, port, view, new InteractionFlowCoordinator(),
                new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)), () => true, () => true, () => "", _ => Assert.Fail("正文待答不能提前完成主要行动"), () => { });
            interaction.GetVisibleState = () => visible;
            foreach (var local in new[] {false, true})
            {
                port.Local = local; view.Refreshes = 0;
                Assert.That(interaction.SubmitSpecialAction(action.ActionId, action.SourceRef.ItemId), Is.True);
                Assert.That(view.Refreshes, Is.EqualTo(local ? 1 : 0));
            }
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            UiRegressionMetrics.Enabled = false; UiRegressionMetrics.Recorded -= Record;
            if (dialog != null) Call(dialog, "Hide");
            if (hud != null) UnityEngine.Object.Destroy(hud);
            yield return null;
        }
        private sealed class Context : IWritableGameplayContext
        { public GameState State; public GameState CurrentState => State; public int LocalPlayerId { get; private set; } = 1;
          public bool ControlsCurrentPlayerLocally => true; public void SetLocalPlayerId(int id) => LocalPlayerId = id; }
        private sealed class NoCommand : IGameCommandPort
        { public WorkflowSubmissionResult Submit(GameCommand command) => throw new InvalidOperationException("只读测量不得提交命令"); }
        private sealed class View : ITurnActionView
        {
            public CityStyleOptionsViewModel Model;
            public int Refreshes;
            public void ShowPrompt(string text) { }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> values) { }
            public void ClearHighlights() { }
            public void RefreshFromState() { Refreshes++; }
            public void RefreshInformation() { }
            public void RefreshActionPanel() { }
            public void ShowPendingChoice() { }
            public void CompleteMainActionPresentation(string name) { }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel model) { }
            public void HideBuildFacilityDraft() { }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel model) => Model = model;
        }
        private sealed class ResultPort : IGameCommandPort
        { public bool Local; public WorkflowSubmissionResult Submit(GameCommand command) => new WorkflowSubmissionResult(CommandResult.SuccessResult(new List<GameEvent>(), ""), Local); }
    }
}
