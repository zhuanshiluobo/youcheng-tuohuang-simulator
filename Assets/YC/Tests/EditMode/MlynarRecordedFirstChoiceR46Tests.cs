using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    // 018正式玛恩纳父链的录制帧进入原消费者；不重放规则，不包含提交控制器、Steam或正常对局验收。
    [Category("录制父链输入的原生UI合同")]
    public sealed class MlynarRecordedFirstChoiceR46Tests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string Game = "9abb1d9ab9c54baeb855a7a291c348aa";
        private const string Content = "a31523994961101d2ac79a36eb10d467a168fbd77800ea0af5a84043f8abd036";
        private const string First = "interaction_3e1ed82939b5abcfe08b9a480ee48b944c4acd9b252e4defddd55989d1d52022";
        private const string Main = "interaction_a309f4eb07f1fdc5a4ee2193dcf9932da4fa1822bcffa0a1a0cfea5b48c9e142";
        private const string Rhodes = "item_2e4bd5b30eca6aa3c019f48bc7071ada47254f3cb96017f358b05258a571385e";
        private static string Project => Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        private static string Frames => Path.Combine(Project, "prompt/企业家内容实施规划-20261019/实施记录/QY-018-MLYNAR-SELF-RHODES-LEVEL4-REQUEST-LOSS-R17/R43-main-window-v2");
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target)
            ?? target.GetType().GetProperty(name, Flags)?.GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Flags).SetValue(target, value);
        private static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static TData Load<TData>(string name, string sha)
        {
            var bytes = File.ReadAllBytes(Path.Combine(Frames, name));
            using var algorithm = SHA256.Create();
            Assert.That(string.Concat(algorithm.ComputeHash(bytes).Select(b => b.ToString("x2"))), Is.EqualTo(sha), "录制输入身份改变，须重新核来源，不能调整revision凑帧");
            return JsonConvert.DeserializeObject<TData>(File.ReadAllText(Path.Combine(Frames, name)),
                new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
        }
        private static GameState Before() => RecordedState("正式玛恩纳策略企业选择-完整隔离状态.json", "b1ca2c003e25dcea50a8db4d8653a03c1ddcd91597a009765c79d6e67e254c42");
        private static GameState After() => RecordedState("群体升级企业回答后-完整隔离状态.json", "36c673b9b56e3faf1c248269918f7bd701ac691daf47bb8a9c4359607ed7490e");
        private static GameState RecordedState(string name, string sha)
        {
            var state = Load<GameState>(name, sha);
            var authority = JsonConvert.SerializeObject(new { state.ItemZones, state.EffectRuntime, state.RuleConfiguration, state.CharacterUses, state.ContentDefinitions });
            var hands = state.Players.ToDictionary(p => p.PlayerId, p => p.HandCardIds.ToArray());
            // 诊断JSON含派生旧位置列表；沿正式实体投影重绑，权威ItemZones/效果/候选不改。
            ItemZoneLegacyProjection.Bind(state, clearLegacy: true);
            Assert.That(JsonConvert.SerializeObject(new { state.ItemZones, state.EffectRuntime, state.RuleConfiguration, state.CharacterUses, state.ContentDefinitions }), Is.EqualTo(authority));
            foreach (var player in state.Players) Assert.That(player.HandCardIds, Is.EqualTo(hands[player.PlayerId]));
            return state;
        }
        private static GameStateView Guest() => Load<GameStateView>("后台企业回答后-Guest发表视图.json", "d1462e95c772f0e28c1f6ecda3c40cbe5b9b026f32a28409558eb077fffad47f");
        private static JArray Permit() => Load<JArray>("后台企业回答后-Guest许可.json", "e506a9fef1cb9e599d79a1339d7f17aefd56873c4dc80ea0c9afbf4bde26700a");
        private static JObject Facts() => Load<JObject>("正式父链结果.json", "a4a1c423f651dd59d6a07d56a2c4042329f9dcbb5521d4da02214d234f4f0166");
        private static GameState UiState(GameState simulation, GameStateView view) => (GameState)T("PlayerUiReadStateFactory")
            .GetMethod("Create", Flags).Invoke(null, new object[] { simulation, view, 2 });

        [Test]
        public void PublishedGuestProjectionKeepsBothRequestsAndRejectsUnpairedSimulation()
        {
            var simulation = After(); var paired = GameStateViewProjector.ProjectForPlayer(simulation, 2); var published = Guest();
            Assert.That(simulation.GameId, Is.EqualTo(Game)); Assert.That(simulation.ContentPackHash, Is.EqualTo(Content));
            Assert.That(simulation.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(paired.StateRevision, Is.EqualTo(31)); Assert.That(published.StateRevision, Is.EqualTo(32));
            foreach (var view in new[] { paired, published })
            {
                var requests = VisibleInteractionRequestSource.Read(null, view, 2);
                Assert.That(requests.Select(r => r.InteractionId), Is.EqualTo(new[] { Main, First }));
                var choice = requests.Single(r => r.InteractionId == First);
                Assert.That(choice.InteractionTypeId, Is.EqualTo("operator.replace.choose"));
                Assert.That(choice.AnsweringPlayerId, Is.EqualTo(2));
                Assert.That(choice.CandidateIds.Count, Is.EqualTo(16));
                Assert.That(choice.MinSelections, Is.EqualTo(1)); Assert.That(choice.MaxSelections, Is.EqualTo(1));
                Assert.That(choice.AllowDecline, Is.False);
                Assert.That(VisibleInteractionRequestSource.Read(null, view, 1), Is.Empty);
            }
            var read = UiState(simulation, paired);
            Assert.That(read, Is.Not.Null);
            Assert.That(read.EffectRuntime.InteractionRequests.Select(r => r.InteractionId), Is.EqualTo(new[] { Main, First }));
            Assert.That(read.FindPlayer(1).HandCardIds, Is.Empty);
            Assert.That(read.FindPlayer(2).HandCardIds, Has.Count.EqualTo(7));
            Assert.That(UiState(simulation, published), Is.Null, "后台32未导出完整模拟状态，禁止与纯规则31拼成同帧");
            Assert.That((bool)Permit().Single()["CanSubmitForLocalPlayer"], Is.True);
        }

        [TestCase(true), TestCase(false)]
        public void RecordedEnterpriseAnswerRoutesBothOpenRequestsToFirstOperatorPage(bool resultBeforeNextFrame)
        {
            using var fixture = new ConsumerFrames();
            Assert.That(fixture.Router.RouteOpen(fixture.Requests(), 2), Is.True);
            Assert.That(fixture.Router.CurrentRequest.InteractionTypeId, Is.EqualTo("enterprise.upgrade"));
            var enterprisePage = (Component)Read(fixture.Enterprise, "enterprise");
            var draft = (EnterpriseSelectionDraft)Read(enterprisePage, "Draft");
            Assert.That(draft.Select(Rhodes), Is.True);
            // 调用原页面确认入口，沿其BindSubmission回调生成命令；不直接Begin/手填在途字段。
            Call(enterprisePage, "Confirm");
            Assert.That(fixture.Commands, Has.Count.EqualTo(1));
            var command = fixture.Commands.Single();
            Assert.That(command.PlayerId, Is.EqualTo(2)); Assert.That(command.OptionIds, Is.EqualTo(new[] { Rhodes }));
            var intent = (InteractionCommandIntent)Read(fixture.Enterprise, "confirmedIntent");
            Assert.That(intent.CommandId, Is.EqualTo(command.CommandId));
            fixture.Results.NotifyInteractionCommandResolved("旧局", command.CommandId, true, CommandErrorCode.None);
            fixture.Results.NotifyInteractionCommandResolved(Game, "其它命令", true, CommandErrorCode.None);
            Assert.That(intent.HasPending, Is.True);
            if (resultBeforeNextFrame) fixture.Resolve(command);
            fixture.UseNextFrame();
            Assert.That(fixture.Requests().Select(r => r.InteractionId), Is.EqualTo(new[] { Main, First }));
            Assert.That(fixture.Router.RouteOpen(fixture.Requests(), 2), Is.True);
            Assert.That(fixture.Router.CurrentRequest.InteractionId, Is.EqualTo(First), "不能被列表首个mainline.complete截断");
            Assert.That(((IInteractionRequestRenderer)fixture.Character).CanRender(fixture.Router.CurrentRequest), Is.True);
            Assert.That((bool)Read(fixture.Dialog, "IsShowing"), Is.True);
            Assert.That(enterprisePage.gameObject.activeSelf, Is.False, "企业旧页先清理，新首步页仍可显示");
            var shell = (Component)Read(Read(fixture.Dialog, "shell"), "view");
            Assert.That(shell.gameObject.activeInHierarchy, Is.True);
            Assert.That((bool)Read(shell, "IsCardPicker"), Is.True, "必须使用原卡牌页而非玩家或执行条目页");
            Assert.That(shell.GetComponentsInChildren(T("FacilityEffectCardView"), false), Has.Length.EqualTo(16));
            var background = ((Image)Read(shell, "OverlayImage")).gameObject;
            var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left,
                pointerPressRaycast = new RaycastResult { gameObject = background }, pointerCurrentRaycast = new RaycastResult { gameObject = background } };
            Call(shell, "OnPointerClick", pointer);
            Assert.That((bool)Read(fixture.Dialog, "IsShowing"), Is.True, "必答选择不继承R45只读空白退出");
            Assert.That(shell.GetComponentsInChildren<Button>(false).Any(b => b.name == "Cancel Selection"), Is.False);
            if (!resultBeforeNextFrame)
            {
                Assert.That(intent.HasPending, Is.True, "切页不是正式结果，不能替代回执释放意图");
                Assert.That(fixture.SubmissionAvailable, Is.False, "此处刻意保留本地待回执，展示不能依赖提交许可");
                fixture.Resolve(command); // 原InteractionRouter必须通知已经不活动的企业消费者。
            }
            Assert.That(intent.IsBusy, Is.False);
            Assert.That((string)Read(fixture.Enterprise, "inFlight"), Is.Empty);
            Assert.That(fixture.SubmissionAvailable, Is.True);
            Assert.That(fixture.Router.RouteOpen(fixture.Requests(), 2), Is.True);
            Assert.That(fixture.Router.CurrentRequest.InteractionId, Is.EqualTo(First));
            Assert.That((bool)Read(fixture.Dialog, "IsShowing"), Is.True);
            Assert.That(fixture.Commands, Has.Count.EqualTo(1), "换页或准确通知不得重发企业选择");
        }

        private sealed class ConsumerFrames : IDisposable
        {
            internal readonly InteractionRequestRouter Router = new InteractionRequestRouter();
            internal readonly InteractionRouter Results = new InteractionRouter(_ => { });
            internal readonly List<GameCommand> Commands = new List<GameCommand>();
            internal object Enterprise, Character, Dialog;
            private GameObject root, eventRoot;
            private object frame, expansion;
            private GameState state;
            private GameStateView view;
            private string pending;
            private readonly bool recordedPermission, recordedEnterpriseAdmission;
            private readonly Type runtime = T("ExternalContentRuntime");
            private readonly Dictionary<string, object> previous = new Dictionary<string, object>();
            // 这是录制许可+夹具待回执信号，仅测UI合同；不替代008提交控制器或协议验收。
            internal bool SubmissionAvailable => (view?.StateRevision == 24 ? recordedEnterpriseAdmission : recordedPermission) && string.IsNullOrEmpty(pending);
            internal IReadOnlyList<InteractionRequest> Requests() => VisibleInteractionRequestSource.Read(null, view, 2);
            internal ConsumerFrames()
            {
                Assert.That((Object)T("GameplayHudFrame").GetProperty("Active", Flags).GetValue(null) == null, Is.True, "不接管既有HUD");
                Assert.That((Object)T("GameLaunchContext").GetProperty("Instance").GetValue(null) == null, Is.True, "不接管用户会话");
                foreach (var name in new[] { "pack", "coreRoot", "registryRoot" }) previous.Add(name, runtime.GetField(name, Flags).GetValue(null));
                try
                {
                    // 只切换本用例的只读内容资源，绝不覆写录制局、revision、候选或操作窗口。
                    runtime.GetMethod("Reset", Flags).Invoke(null, null);
                    var pack = ExternalContentPack.Load(Path.Combine(Project, "Builds/QY017-R43-20261024-current/tuohuang_Data/StreamingAssets/Content/core"));
                    runtime.GetField("pack", Flags).SetValue(null, pack);
                    state = Before(); view = GameStateViewProjector.ProjectForPlayer(state, 2);
                    var catalogue = ActiveContentCatalogBuilder.Build(pack, state.RuleConfiguration.Clone());
                    Assert.That(catalogue.ContentPackHash, Is.EqualTo(Content));
                    var registry = new EffectRegistry(); catalogue.Register(registry);
                    recordedEnterpriseAdmission = (string)Facts()["network"]["admission"] == "Accepted";
                    var permit = Permit().Single();
                    Assert.That((string)permit["Window"]["GameId"], Is.EqualTo(Game));
                    Assert.That(permit["AvailablePlayerIds"].Values<int>(), Is.EqualTo(new[] { 2 }));
                    recordedPermission = (bool)permit["CanSubmitForLocalPlayer"];
                    T("UiTheme").GetMethod("Initialize", Flags).Invoke(null, new object[] {
                        AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog")) });
                    root = new GameObject("R46录制帧原生消费者", typeof(RectTransform)); root.SetActive(false);
                    if (EventSystem.current == null) eventRoot = new GameObject("R46隔离指针", typeof(EventSystem));
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
                    Assert.That(prefab, Is.Not.Null);
                    var instance = Object.Instantiate(prefab, root.transform, false);
                    foreach (var controller in instance.GetComponentsInChildren(T("MobileCityInteractionController"), true)) Object.DestroyImmediate(controller);
                    var hud = instance.GetComponent(T("GameplayInteractionHudView"));
                    frame = Read(hud, "Frame"); expansion = Read(hud, "ExpansionUi");
                    root.SetActive(true);
                    if (!ReferenceEquals(T("GameplayHudFrame").GetProperty("Active", Flags).GetValue(null), frame)) Call(frame, "Awake");
                    Call(hud, "ConfigureGameplayPresentation", catalogue.TextCatalog, catalogue.Definitions.ToDictionary(d => d.DefinitionId, d => d.DisplayName));
                    Call(expansion, "Configure", (Func<GameStateView>)(() => view), (Func<int>)(() => 2), registry.EnterpriseDefinitions,
                        registry.CharacterAbilityContracts, (Action<GameCommand>)Capture);
                    var session = new GameSession(state); // 仅原展示绑定的会话身份锚点，不提交或执行规则。
                    var binding = T("SessionPresentationBinding").GetMethod("Create", Flags).Invoke(null,
                        new object[] { catalogue.Presentation, session, Game, state.RuleConfiguration, Content, pack.ContentHash });
                    Assert.That(binding, Is.Not.Null);
                    Call(expansion, "BindSessionPresentation", binding, (Func<GameSession>)(() => session));
                    Call(frame, "BindGameplaySnapshot", view);
                    Dialog = Activator.CreateInstance(T("CharacterCardEffectChoiceDialog"), Flags, null, new[] { Read(hud, "DialogRegistry"), Read(frame, "ContentRect") }, null);
                    Character = Activator.CreateInstance(T("CharacterAbilityInteractionUiCoordinator"), Flags, null,
                        new object[] { (Func<GameState>)(() => UiState(state, view)), (Func<int>)(() => 2), Dialog,
                            (Action<IReadOnlyList<WorkflowHighlight>>)(_ => { }), (Action)(() => { }), (Action<GameCommand>)Capture, (Action<string>)(_ => { }) }, null);
                    Set(Character, "ExpansionUi", expansion); Set(Character, "GetVisibleState", (Func<GameStateView>)(() => view));
                    Set(Character, "GetVisibleRequests", (Func<IReadOnlyList<InteractionRequest>>)Requests);
                    Set(Character, "CanSubmit", (Func<bool>)(() => SubmissionAvailable));
                    Enterprise = Activator.CreateInstance(T("EnterpriseInteractionUiCoordinator"), Flags, null,
                        new object[] { expansion, hud, (Func<IReadOnlyList<InteractionRequest>>)Requests, (Func<int>)(() => 2), (Action<GameCommand>)Capture }, null);
                    Set(Enterprise, "CanSubmit", (Func<bool>)(() => SubmissionAvailable)); Set(Enterprise, "IsNetworkSession", (Func<bool>)(() => true));
                    foreach (var consumer in new[] { Enterprise, Character })
                    { Router.Register((IInteractionRequestRenderer)consumer); Results.Register((IInteraction)consumer); }
                }
                catch { Dispose(); throw; }
            }
            private void Capture(GameCommand command) { Assert.That(pending, Is.Null.Or.Empty); Commands.Add(command); pending = command.CommandId; }
            internal void UseNextFrame()
            {
                state = After(); view = GameStateViewProjector.ProjectForPlayer(state, 2);
                Assert.That(UiState(state, view), Is.Not.Null); Call(frame, "BindGameplaySnapshot", view);
            }
            internal void Resolve(GameCommand command)
            {
                Results.NotifyInteractionCommandResolved(Game, command.CommandId, true, CommandErrorCode.None);
                Assert.That(pending, Is.EqualTo(command.CommandId)); pending = null;
            }
            public void Dispose()
            {
                Router.Clear();
                if (Character != null) Call(Character, "Clear");
                if (Enterprise != null) Call(Enterprise, "Clear");
                if (Dialog != null) Call(Dialog, "Hide");
                if (frame != null) Call(frame, "OnDestroy"); // EditMode手动Awake的订阅同样沿原生命周期成对释放。
                if (expansion != null) Call(expansion, "OnDestroy");
                if (root != null) Object.DestroyImmediate(root);
                if (eventRoot != null) Object.DestroyImmediate(eventRoot);
                runtime.GetMethod("Reset", Flags).Invoke(null, null);
                foreach (var pair in previous) runtime.GetField(pair.Key, Flags).SetValue(null, pair.Value);
            }
        }
    }
}
