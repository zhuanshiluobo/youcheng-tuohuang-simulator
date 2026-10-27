using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 技术隔离：当前正式Lua/目录 + 既有马丁夹具 + 原HUD/供应Prefab。
    // 隔离摆牌明确记录，不能计正常菜单/四人整局或旧现场恢复。
    public sealed class SupplyPromptHostR22Tests : MartinContentTestFixture
    {
        const string PagePath = "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/FacilityBuildDialog.prefab";
        const string HudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        static string Output => Environment.GetEnvironmentVariable("QY017_R22_OUTPUT") ?? throw new InvalidOperationException("缺少R22独立证据目录");
        GameObject hudObject, cameraObject;
        RenderTexture target;
        object hud, frame, coordinator, dialogs;
        IInteractionRequestRenderer renderer;
        GameState state;
        GameStateView current;
        readonly List<GameCommand> commands = new List<GameCommand>();
        readonly List<object> receipts = new List<object>();
        bool canSubmit = true;
        static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        static object Read(object owner, string name) => owner.GetType().GetField(name, Flags)?.GetValue(owner) ?? owner.GetType().GetProperty(name, Flags)?.GetValue(owner);
        static object Call(object owner, string name, params object[] args)
        {
            try { return owner.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw(); throw; }
        }
        static string Hash(string path) { using var sha = SHA256.Create(); using var stream = File.OpenRead(path); return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2"))); }
        static void Save(string name, object value) { using var stream = new FileStream(Path.Combine(Output, name + ".json"), FileMode.CreateNew); using var writer = new StreamWriter(stream); writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented)); }
        Component Page => (Component)Read(coordinator, "page");
        Button Confirm => (Button)Read(Page, "confirm");
        Button Cancel => (Button)Read(Page, "cancel");
        Component Prompt => (Component)Read(Page, "quoteDocumentView");
        InteractionRequestProjection Request => InteractionRequestProjector.ProjectForPlayer(Open(state), 1);
        Component[] Cards => Page.GetComponentsInChildren(T("FacilityEffectCardView"), false).Cast<Component>().ToArray();
        Button Card(string id) => Cards.Select(c => (Button)Read(c, "Button")).Single(b =>
            b.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "PlayerAutomationId" && (string)Read(c, "Id") == "supply.select." + id));

        public override void StartMartin() { }
        void PrepareInput()
        {
            Artifacts = IsolatedTestArtifacts.BeginCase();
            var installed = ExternalContentPack.Load(Installed);
            var method = typeof(ExternalContentPack).GetMethod("CreateActiveSnapshot", Flags, null,
                new[] { typeof(IEnumerable<string>), typeof(string), typeof(string) }, null);
            Pack = (ExternalContentPack)method.Invoke(installed, new object[] { new[] { "core", "enterprise", "infected" },
                LuaContentHasher.ComputeSha256(installed.ContentHash + "|R22.UI.isolated"), "entrepreneur" });
            Artifacts.WriteLabeledText("R22隔离输入", "只读当前正式内容，在既有马丁夹具内合法转移摆牌；不是正常局。活动目录=" + Pack.ContentHash);
            Save(TestContext.CurrentContext.Test.Name + "-身份", new {
                installed = installed.ContentHash, active = Pack.ContentHash,
                files = new[] { PagePath, HudPath, "Assets/YC/Presentation/FacilitySupplySelectionUiCoordinator.cs", "Assets/YC/Presentation/FacilityBuildDialogSupplySelection.cs", "Assets/YC/Presentation/FacilityBuildDialogView.cs", "Assets/YC/Tests/EditMode/SupplyPromptHostR22Tests.cs", "Assets/StreamingAssets/Content/core/presentation/texts.zh-CN.json" }.Select(p => new { path = p, sha256 = Hash(p) }),
                assemblies = new[] { GetType().Assembly, T("GameplayTextView").Assembly, typeof(GameState).Assembly, typeof(GameplayPresentationCatalog).Assembly, typeof(ExternalContentPack).Assembly }
                    .Distinct().Select(a => new { a.Location, mvid = a.ManifestModule.ModuleVersionId, sha256 = Hash(a.Location) }) });
        }
        void CreateHud()
        {
            current = GameStateViewProjector.ProjectForPlayer(state, 1);
            Assert.That(Pack.Presentation.ContentHash, Is.EqualTo(current.ContentPackHash));
            T("UiTheme").GetMethod("Initialize", Flags).Invoke(null, new object[] { AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog")) });
            hudObject = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(HudPath));
            hud = hudObject.GetComponent(T("GameplayInteractionHudView"));
            Call(hud, "ConfigureGameplayPresentation", Pack.Presentation, new Dictionary<string, string>());
            frame = Read(hud, "Frame"); dialogs = Read(hud, "DialogRegistry");
            Call(frame, "BindGameplaySnapshot", current);
            Call(Read(hud, "MainModules"), "Render", null, current, 1, Read(dialogs, "CardVisualCatalog"));
            Call(Read(hud, "ExpansionUi"), "Configure", new Func<GameStateView>(() => current), new Func<int>(() => 1), null,
                new Dictionary<string, YC.Domain.Cards.CharacterModeContract>(), new Action<GameCommand>(Submit));
            coordinator = Activator.CreateInstance(T("FacilitySupplySelectionUiCoordinator"), new object[] {
                new Func<GameStateView>(() => current), new Func<int>(() => 1), dialogs, Read(frame, "ContentRect"),
                new Action<GameCommand>(Submit), new Func<bool>(() => canSubmit) });
            coordinator.GetType().GetProperty("ExpansionUi").SetValue(coordinator, Read(hud, "ExpansionUi"));
            renderer = (IInteractionRequestRenderer)coordinator;
            renderer.Render(Request);
        }
        void Submit(GameCommand command)
        {
            commands.Add(command);
            var result = new AnswerInteractionCommandHandler(Registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, JsonConvert.SerializeObject(result));
            var run = new EffectTreeExecutor(state, Registry).RunUntilQuiescent();
            Assert.That(run.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            receipts.Add(new { command, result, afterRevision = state.EffectRuntime.StateRevision,
                open = state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").ToArray() });
        }
        string[] SupplyFour()
        {
            return Enumerable.Range(0, 4).Select(slot => Supply(state, "entrepreneur.facility.M03", slot).ItemId).ToArray();
        }
        void AssertPrompt()
        {
            Assert.That(Prompt, Is.Not.Null);
            Assert.That(Prompt.gameObject, Is.SameAs(((Text)Read(Page, "quote")).gameObject));
            Assert.That(Read(Prompt, "HasDocument"), Is.EqualTo(true));
            var expected = (GameplayTextDocument)Call(frame, "ResolveGameplayText", Request.Presentation.Prompt);
            var actual = (GameplayTextDocument)Read(Prompt, "document");
            Assert.That(actual.ToPlainText(), Is.EqualTo(expected.ToPlainText()));
            Assert.That(actual.ToPlainText(), Is.Not.Empty);
            Assert.That(actual.ToPlainText(), Does.Not.Contain("core:"));
            Save(TestContext.CurrentContext.Test.Name + "-真实提示与候选", new { request = Request, plain = actual.ToPlainText(),
                candidateIds = Request.CandidateIds.Where(id => Card(id).interactable).ToArray(),
                source = PagePath, promptSource = "Build Quote原节点，新增序列化GameplayTextView组件", isolated = true });
        }

        [Test]
        public void ActualSceneRegistryUsesWiredSourceWithoutSceneOverride()
        {
            PrepareInput();
            var scenes = new List<object>();
            foreach (string path in new[] { "Assets/Scenes/SampleScene.unity", "Assets/Scenes/ThreePlayerScene.unity" })
            {
                string before = Hash(path); var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try {
                    var registries = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren(T("GameplayDialogRegistry"), true)).ToArray();
                    Assert.That(registries, Is.Not.Empty);
                    foreach (var registry in registries) {
                        var source = (Component)Read(registry, "facilityBuildPrefab");
                        Assert.That(AssetDatabase.GetAssetPath(source), Is.EqualTo(PagePath));
                        var serialized = new SerializedObject(registry).FindProperty("facilityBuildPrefab");
                        Assert.That(serialized.prefabOverride, Is.False);
                        var host = (Component)Read(source, "quoteDocumentView");
                        Assert.That(host, Is.Not.Null); Assert.That(Read(host, "source"), Is.SameAs(Read(source, "quote")));
                        Assert.That(Read(host, "style"), Is.Not.Null); Assert.That(Read(host, "icons"), Is.Not.Null);
                        scenes.Add(new { path, source = AssetDatabase.GetAssetPath(source), quoteHost = host.GetType().FullName, prefabOverride = serialized.prefabOverride });
                    }
                } finally { EditorSceneManager.CloseScene(scene, true); }
                Assert.That(Hash(path), Is.EqualTo(before));
            }
            Save("实际两场景供应资产来源", scenes);
        }

        [UnityTest]
        public IEnumerator FormalM05PromptFourTopCandidatesConfirmAndExchangeSettle()
        {
            yield return new EnterPlayMode(); PrepareInput();
            state = State("entrepreneur.round.M.1", true); ActivateFace(state, "entrepreneur.round.M.1", "M05");
            var blueprint = Blueprint(state, "entrepreneur.facility.M03");
            var lower = Supply(state, "building_015", 0); var tops = SupplyFour();
            Action(state, "M05"); Answer(state, NormalizedValue.CreateString("place_twice")); UntilPrompt(state, "core:entrepreneur.M.blueprint.choose_supply");
            Assert.That(Request.CandidateIds, Is.EquivalentTo(tops)); Assert.That(Request.AllowDecline, Is.False);
            Save("M05原Lua隔离请求首次现场", new { state, fixture = "既有MartinContentTestFixture，3人隔离，合法摆牌；不计正常四人局" });
            CreateHud(); yield return null; Canvas.ForceUpdateCanvases(); yield return null;
            AssertPrompt(); Assert.That(Cards.Count(c => ((Button)Read(c, "Button")).interactable), Is.EqualTo(4));
            Assert.That(Card(lower.ItemId).interactable, Is.False); Assert.That(Confirm.interactable, Is.False);
            Assert.That(Cancel.gameObject.activeSelf, Is.False); Assert.That(Cancel.interactable, Is.False);
            Cancel.onClick.Invoke(); Call(coordinator, "OnEscape"); Assert.That(commands, Is.Empty);
            Capture("01-M05正式提示及四候选-隔离组件.png");
            Card(tops[0]).onClick.Invoke(); yield return null; Assert.That(commands, Is.Empty);
            Assert.That(Confirm.interactable, Is.True); var originalConfirm = Confirm;
            originalConfirm.onClick.Invoke(); originalConfirm.onClick.Invoke();
            Assert.That(commands.Count, Is.EqualTo(1)); Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { tops[0] }));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("lua.choice"));
            Answer(state, NormalizedValue.CreateString(blueprint.ItemId));
            var batch = state.EffectRuntime.EffectNodes.Single(n => n.TransferBatchRequest?.OperationId.StartsWith("martin:exchange:") == true);
            Assert.That(batch.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(YC.Domain.Items.ItemTransferService.TryValidateBatchReceipt(batch, out var why), Is.True, why);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Save("M05供应原确认回执及交换后继结清", new { receipts, batch, state, scope = "供应由原UI提交；后继图纸回答沿既有规则夹具，不计正常局" });
            Call(coordinator, "NotifyCommandSettled", commands[0].CommandId);
            yield return VerifyNormalBuildReuse();
        }

        void GenericOptionalSupply()
        {
            var ids = FacilityLayerQuery.Supply(state).Where(s => s.ItemIds.Count > 0).Select(s => s.ItemIds[0]).ToArray();
            // 只验可选通用请求的UI/命令边界；不冒充M05有可弃分支。
            // Lua Choice的min=0不属于供应页min=1合同，故用原EffectInteractionSpec构造独立Host夹具。
            string type = "r22.optional.supply." + Guid.NewGuid().ToString("N");
            Registry.Register(new EffectRegistration(type, context => {
                if (context.Node.FlowStage.Length != 0) return EffectStepResult.Completed();
                var request = new EffectInteractionSpec { InteractionTypeId = "facility.supply.select", Visibility = "owner",
                    AnsweringPlayerId = 1, AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, AllowDecline = true,
                    PromptKey = "core:entrepreneur.M.blueprint.choose_supply",
                    Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:entrepreneur.M.blueprint.choose_supply") } };
                request.CandidateIds.AddRange(ids);
                return EffectStepResult.Continue("waiting").AddInteraction(request);
            }, EffectExecutorKind.IntrinsicFlow));
            Run(state, EffectSpec.Create(type, playerId: 1));
        }

        [UnityTest]
        public IEnumerator OptionalOriginalCancelAndEscapeUseRealDeclineBranch()
        {
            yield return new EnterPlayMode(); PrepareInput();
            state = State(); SupplyFour(); GenericOptionalSupply(); CreateHud(); yield return null;
            AssertPrompt(); Assert.That(Cancel.gameObject.activeSelf && Cancel.interactable, Is.True);
            Cancel.onClick.Invoke(); Cancel.onClick.Invoke(); Assert.That(commands.Count, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Call(coordinator, "NotifyCommandSettled", commands[0].CommandId);
            GenericOptionalSupply(); current = GameStateViewProjector.ProjectForPlayer(state, 1); Call(frame, "BindGameplaySnapshot", current);
            renderer.Render(Request); yield return null; Call(coordinator, "OnEscape"); Call(coordinator, "OnEscape");
            Assert.That(commands.Count, Is.EqualTo(2)); Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(state.Map.Facilities, Is.Empty);
            Save("可选供应原取消及Escape原答复", new { receipts, state, isolatedGenericHostRequest = true });
        }

        [UnityTest]
        public IEnumerator StaleRevisionViewerAndSubmissionGateCannotSubmitOldPrompt()
        {
            yield return new EnterPlayMode(); PrepareInput();
            state = State(); var tops = SupplyFour(); GenericOptionalSupply(); CreateHud(); yield return null;
            Assert.That(renderer.CanRender(InteractionRequestProjector.ProjectForPlayer(Open(state), 2)), Is.False);
            foreach (string kind in new[] { "revision", "viewer", "submission_gate" }) {
                current = GameStateViewProjector.ProjectForPlayer(state, 1); canSubmit = true;
                renderer.Clear(); renderer.Render(Request); yield return null; Card(tops[0]).onClick.Invoke(); yield return null;
                var confirm = Confirm; var cancel = Cancel;
                if (kind == "revision") current.StateRevision++;
                else if (kind == "viewer") current.ViewerPlayerId = 2;
                else canSubmit = false;
                confirm.onClick.Invoke(); cancel.onClick.Invoke(); Call(coordinator, "OnEscape");
                Assert.That(commands, Is.Empty, kind);
            }
            Save("陈旧请求与回答者及提交门控", new { tested = new[] { "revision", "viewer", "submission_gate" }, commands, original = Open(state) });
        }

        IEnumerator VerifyNormalBuildReuse()
        {
            Assert.That(Read(Prompt, "HasDocument"), Is.EqualTo(false));
            state = State(); Supply(state, "building_015", 0);
            current = GameStateViewProjector.ProjectForPlayer(state, 1); Call(frame, "BindGameplaySnapshot", current);
            Call(Page, "RefreshSupplyStatus", state, 1, null);
            var id = FacilityLayerQuery.Supply(state).First(s => s.ItemIds.Count > 0).ItemIds[0];
            var option = new BuildFacilityOptionQueryService().Query(state, 1, id);
            Assert.That(option.CanBuild, Is.True, option.Reason);
            var model = new BuildFacilityDraftViewModel(BuildFacilityDraftPhase.Selecting, new[] { option }, option, null, -1, "", "", Array.Empty<int>(), _ => { });
            string paid = null;
            Call(Page, "Show", model, state.Map.Facilities, 1, Read(dialogs, "CardVisualCatalog"),
                AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/EffectDialogLayoutProfile.asset", T("EffectDialogLayoutProfile")), id, "", new Func<bool>(() => true),
                new Action<string>(_ => { }), new Action<int>(_ => { }), new Action<string>(mode => paid = mode), new Action(() => { }), new Action(() => { }), false);
            yield return null;
            Assert.That(Read(Prompt, "HasDocument"), Is.EqualTo(false));
            var rows = ((IEnumerable)Read(Page, "paymentRows")).Cast<Component>().ToArray();
            Assert.That(rows.All(r => r.gameObject.activeSelf), Is.True);
            Assert.That(Cancel.gameObject.activeSelf && Cancel.interactable, Is.True);
            Assert.That(Confirm.gameObject.activeSelf, Is.False);
            var payable = rows.Select(r => (Button)Read(r, "Button")).First(b => b.interactable);
            payable.onClick.Invoke(); Assert.That(paid, Is.Not.Null.And.Not.Empty);
            Assert.That(commands.Count, Is.EqualTo(1), "普通付款回调不生成供应答复");
            Save("原建设入口复用与付款回调", new { selected = id, paid, paymentRows = rows.Length, noSupplyDocument = !(bool)Read(Prompt, "HasDocument"), supplyAnswerCount = commands.Count,
                scope = "原Show/原付款回调，不重复验证已交付的付款内核" });
        }

        void Capture(string filename)
        {
            cameraObject = new GameObject("R22隔离组件相机", typeof(Camera)); var camera = cameraObject.GetComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, -10); camera.orthographic = true; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.gray;
            target = new RenderTexture(1280, 720, 24); target.Create(); camera.targetTexture = target;
            foreach (var canvas in hudObject.GetComponentsInChildren<Canvas>(true)) { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 1; }
            Canvas.ForceUpdateCanvases(); camera.Render(); var old = RenderTexture.active; RenderTexture.active = target;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try { texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply(); File.WriteAllBytes(Path.Combine(Output, filename), texture.EncodeToPNG()); }
            finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(texture); }
        }
        [UnityTearDown]
        public IEnumerator ReleaseComponents()
        {
            if (coordinator is IDisposable disposable) disposable.Dispose();
            if (hudObject != null) UnityEngine.Object.DestroyImmediate(hudObject);
            if (cameraObject != null) { cameraObject.GetComponent<Camera>().targetTexture = null; UnityEngine.Object.DestroyImmediate(cameraObject); }
            if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            hudObject = cameraObject = null; target = null; coordinator = hud = frame = dialogs = null; renderer = null; state = null; current = null; commands.Clear(); receipts.Clear(); canSubmit = true;
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
        }
    }
}
