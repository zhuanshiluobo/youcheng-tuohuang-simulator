using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 隔离组件回归：正式双人开局推进至真实后手请求，不计作可见独立程序正常验收。
    public sealed class SetupInfluenceUiR16Tests
    {
        private SceneRuntimeCatalogScope runtimeCatalogs;
        [UnityTearDown]
        public IEnumerator LeaveRuntime()
        {
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
            runtimeCatalogs?.Dispose(); runtimeCatalogs = null;
        }
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type Production(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target)
            ?? target.GetType().GetProperty(name, Flags)?.GetValue(target);
        private static void Write(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
        private static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Single(r =>
            r.Status == "open" && r.InteractionTypeId == "setup.influence.optional");
        private static GameSession Prepare()
        {
            var helper = typeof(HotseatSequentialStageTests);
            var fixture = helper.GetMethod("Build", Flags).Invoke(null, new object[] { LaunchMode.Local, true, 2, 42, false });
            var session = (GameSession)Read(fixture, "Session");
            helper.GetMethod("DriveUntil", Flags).Invoke(null, new object[] { fixture, new Func<bool>(() =>
                session.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.InteractionTypeId == "setup.influence.optional")) });
            var request = Open(session.State);
            Assert.That(session.State.Players.Count, Is.EqualTo(2));
            Assert.That(request.AnsweringPlayerId, Is.Not.EqualTo(session.State.StartPlayerId));
            Assert.That(request.MinSelections, Is.Zero); Assert.That(request.MaxSelections, Is.EqualTo(1));
            Assert.That(request.AllowDecline, Is.True); Assert.That(request.CandidateIds, Is.Not.Empty);
            return session;
        }

        [SetUp]
        public void RecordLoadedIdentity()
        {
            object Identity(Assembly assembly)
            {
                string hash;
                using (var algorithm = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(assembly.Location))
                    hash = string.Concat(algorithm.ComputeHash(stream).Select(b => b.ToString("x2")));
                return new { assembly.FullName, assembly.Location, Mvid = assembly.ManifestModule.ModuleVersionId, Sha256 = hash };
            }
            SaveEvidence("identity-" + Guid.NewGuid().ToString("N"), new { Test = TestContext.CurrentContext.Test.FullName,
                Production = Identity(Production("MobileCityInteractionController").Assembly),
                TestAssembly = Identity(GetType().Assembly), Domain = Identity(typeof(GameState).Assembly) });
        }

        [Test]
        public void OptionalInfluenceRendersWithoutSettlementHost()
        {
            using (new SceneRuntimeCatalogScope())
            {
                var session = Prepare(); var request = Open(session.State); int player = request.AnsweringPlayerId;
                var projection = InteractionRequestProjector.ProjectForPlayer(request, player);
                IReadOnlyList<WorkflowHighlight> targets = null; var commands = new List<GameCommand>();
                var coordinator = Activator.CreateInstance(Production("MapEffectInteractionUiCoordinator"),
                    new object[] { new Func<GameState>(() => session.State), new Func<int>(() => player), null,
                        new Action<IReadOnlyList<WorkflowHighlight>>(value => targets = value), new Action(() => { }),
                        new Action<GameCommand>(commands.Add), new Action<string>(_ => { }) });
                Assert.DoesNotThrow(() => Call(coordinator, "Render", projection), "地图请求不应依赖结算宿主或条目模板。");
                Assert.That(targets.Select(h => h.TargetId), Is.EquivalentTo(request.CandidateIds));
                player = session.State.StartPlayerId;
                Call(coordinator, "Render", projection); Call(coordinator, "OnEscape");
                Call(coordinator, "OnInfluenceSlotClicked", request.CandidateIds[0]);
                Assert.That(commands, Is.Empty, "观察者切换后不能回答后手专属请求。");
                player = request.AnsweringPlayerId; Call(coordinator, "Render", projection);
                Call(coordinator, "OnEscape"); Call(coordinator, "OnEscape");
                Assert.That(commands.Count, Is.EqualTo(1), "等待提交期间不能再次跳过。");
                Assert.That(commands[0].Parameters["answerValue"], Is.EqualTo("false"));
                Assert.That(session.Submit(commands[0]).Succeeded, Is.True);
                Call(coordinator, "NotifyCommandSettled", commands[0].CommandId); Call(coordinator, "Clear");
            }
        }

        [UnityTest] public IEnumerator ActualScenePlacesInfluenceWithoutSettlementRow() => SceneInput("place");
        [UnityTest] public IEnumerator ActualSceneOriginalEndButtonSkipsWithoutSettlementRow() => SceneInput("end");
        [UnityTest] public IEnumerator ActualSceneEscapeSkipsWithoutSettlementRow() => SceneInput("escape");
        private IEnumerator SceneInput(string input)
        {
            runtimeCatalogs = new SceneRuntimeCatalogScope(); yield return new EnterPlayMode();
            {
                var prepared = Prepare(); var original = GameStateCloneService.DeepClone(Open(prepared.State));
                NativeScene native = null; yield return NativeScene.Create(prepared.State, value => native = value);
                using (native)
                {
                    var mobile = native.Mobile; var state = native.Session.State;
                    var hud = Read(mobile, "gameplayInteractionHud"); var expansion = Read(hud, "ExpansionUi");
                    var host = (RectTransform)Read(expansion, "settlementHost");
                    var initialChildren = host.childCount;
                    Assert.That(host.gameObject.activeSelf, Is.False, "额外影响力不能打开真实结算宿主。");
                    Assert.That(host.Cast<Transform>().Any(t => t.name == "OptionalInitialInfluenceSkip"), Is.False);
                    Assert.That((bool)Read(Read(expansion, "settlementStrip"), "HasVisibleOptions"), Is.False);
                    Assert.That(Read(mobile, "localPlayerId"), Is.EqualTo(original.AnsweringPlayerId));
                    Assert.That(Call(mobile, "CanEndCurrentAction"), Is.EqualTo(true));
                    var button = (Button)Read(Read(hud, "Frame"), "EndActionButton");
                    Assert.That(button.IsInteractable(), Is.True);
                    int beforeCount = state.Map.Influences.Count;
                    var current = (InteractionRequestProjection)Read(Read(mobile, "interactionRequestRouter"), "CurrentRequest");
                    Assert.That(current.InteractionId, Is.EqualTo(original.InteractionId));
                    var map = Read(mobile, "influenceEffectInteraction");
                    Assert.That(map, Is.Not.Null);
                    if (input == "place")
                    {
                        Call(map, "OnInfluenceSlotClicked", "not-a-legal-candidate");
                        Assert.That(native.Port.Commands, Is.Empty);
                        Call(map, "OnInfluenceSlotClicked", original.CandidateIds[0]);
                    }
                    else if (input == "end") button.onClick.Invoke();
                    else Call(map, "OnEscape");
                    var command = native.Port.Commands.Single();
                    Assert.That(command.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
                    Assert.That(command.PlayerId, Is.EqualTo(original.AnsweringPlayerId));
                    Assert.That(command.Parameters["interactionId"], Is.EqualTo(original.InteractionId));
                    Assert.That(native.Port.Last.CommandResult.Succeeded && native.Port.Last.AppliedLocally, Is.True,
                        JsonConvert.SerializeObject(native.Port.Last));
                    Assert.That(native.Session.State.Map.Influences.Count, Is.EqualTo(beforeCount + (input == "place" ? 1 : 0)));
                    if (input == "place")
                    {
                        Assert.That(command.OptionIds, Is.EqualTo(new[] { original.CandidateIds[0] }));
                        Assert.That(native.Session.State.Map.Influences.Last().SlotId, Is.EqualTo(original.CandidateIds[0]));
                    }
                    else Assert.That(command.Parameters["answerValue"], Is.EqualTo("false"));
                    Assert.That(native.Session.State.EffectRuntime.InteractionRequests.Single(r => r.InteractionId == original.InteractionId).Status,
                        Is.Not.EqualTo("open"));
                    Assert.That(native.Session.State.EffectRuntime.LastFaultCode, Is.Null.Or.Empty);
                    Assert.That(Read(mobile, "localPlayerId"), Is.EqualTo(new LocalPlayerResolver().Resolve(native.Session.State, 1, true)),
                        "正式后继按当前热座请求切换本人视角。");
                    var bridgeTypes = new List<string>();
                    // 可选影响力之后仍有本人的必做开局步骤，沿真实候选结清后才核热座切人。
                    for (int guard = 0; guard < 12 && new LocalPlayerResolver().Resolve(native.Session.State, 1, true) == original.AnsweringPlayerId; guard++)
                    {
                        var next = (InteractionRequestProjection)Read(Read(mobile, "interactionRequestRouter"), "CurrentRequest");
                        Assert.That(next, Is.Not.Null); Assert.That(next.InteractionId, Is.Not.EqualTo(original.InteractionId));
                        bridgeTypes.Add(next.InteractionTypeId);
                        var answer = EffectInteractionCommands.Answer(next, next.AnsweringPlayerId,
                            next.CandidateIds.Take(Math.Max(1, next.MinSelections)), false);
                        if (next.AnswerSchema == "boolean_accept") { answer.OptionIds.Clear(); answer.Parameters["answerValue"] = "true"; }
                        Call(mobile, "SubmitPendingEffectCommand", answer); yield return null;
                        Assert.That(native.Port.Last.CommandResult.Succeeded, Is.True, JsonConvert.SerializeObject(native.Port.Last));
                    }
                    Assert.That(Read(mobile, "localPlayerId"), Is.Not.EqualTo(original.AnsweringPlayerId));
                    int answered = native.Port.Commands.Count;
                    Call(map, "OnInfluenceSlotClicked", original.CandidateIds[0]); Call(map, "OnEscape");
                    Assert.That(native.Port.Commands.Count, Is.EqualTo(answered), "旧地图输入不能回答切人后的请求。");
                    Assert.That(host.childCount, Is.EqualTo(initialChildren));
                    Assert.That(host.gameObject.activeSelf, Is.False);
                    var beforeRejected = GameStateCloneService.DeepClone(native.Session.State);
                    var stale = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(original, original.AnsweringPlayerId),
                        original.AnsweringPlayerId, new[] { original.CandidateIds[0] }, false);
                    Assert.That(native.Session.Submit(stale).Succeeded, Is.False, "旧请求不得以新命令ID重答。");
                    Assert.That(GameStateCloneService.AreEquivalent(beforeRejected, native.Session.State), Is.True);
                    SaveEvidence(input, new { Request = original, Command = command, BeforeInfluences = beforeCount,
                        AfterInfluences = native.Session.State.Map.Influences.Count, SettlementChildren = initialChildren,
                        NextPlayer = Read(mobile, "localPlayerId"), BridgeRequests = bridgeTypes, OldRequestRejected = true });
                }
            }
        }

        [UnityTest]
        public IEnumerator ActualSettlementStillUsesItsOriginalHostAndCommand()
        {
            runtimeCatalogs = new SceneRuntimeCatalogScope(); yield return new EnterPlayMode();
            {
                var helper = typeof(MainActionPreselectionUiTests);
                var prepared = helper.GetMethod("Production", Flags).Invoke(null, new object[] { true });
                var state = (GameState)Read(prepared, "State");
                var amiya = state.ItemZones.Items.Single(i => i.DefinitionId == "infected.character.amiya" && i.OwnerSubject.PlayerId == 1);
                var hand = state.ItemZones.Zones.Single(z => z.LocalZoneId == "hand" && z.OwnerPlayerId == 1);
                helper.GetMethod("Run", Flags).Invoke(null, new object[] { prepared,
                    ItemTransferEffectExecutor.MoveSpec(1, new[] { amiya.ItemId }, amiya.ZoneId, hand.ZoneId, "unordered", 1, "core.character_card_transfer") });
                helper.GetMethod("Cover", Flags).Invoke(null, new object[] { prepared, amiya.ItemId });
                helper.GetMethod("Run", Flags).Invoke(null, new object[] { prepared, CharacterLifecycleService.CreateUseSpec(1, amiya.ItemId, "tactic") });
                var target = state.ItemZones.Items.First(i => i.TypeId == "core.basic_character_card" && i.ZoneId == hand.ZoneId && i.ItemId != amiya.ItemId);
                helper.GetMethod("Cover", Flags).Invoke(null, new object[] { prepared, target.ItemId });
                // 仅准备正式角色生命周期宿主；受测结算经实际条目绑定和当前生产InvokeSettlement提交。
                helper.GetMethod("Run", Flags).Invoke(null, new object[] { prepared, CharacterLifecycleService.CreateUseSpec(1, target.ItemId) });
                NativeScene native = null; yield return NativeScene.Create(state, value => native = value);
                using (native)
                {
                    var expansion = Read(Read(native.Mobile, "gameplayInteractionHud"), "ExpansionUi");
                    var host = (RectTransform)Read(expansion, "settlementHost");
                    var strip = Read(expansion, "settlementStrip");
                    var view = (GameStateView)Call(expansion, "VisibleView");
                    SaveEvidence("settlement-before", new { view.ActiveSettlementWindowId, view.CharacterSettlementOptions,
                        Uses = native.Session.State.CharacterUses, view.StateRevision });
                    var option = view.CharacterSettlementOptions.Single(o => o.SourceItemId == amiya.ItemId && o.IsEnabled);
                    Assert.That(host.gameObject.activeSelf, Is.True);
                    Assert.That(Read(strip, "HasVisibleOptions"), Is.EqualTo(true));
                    Assert.That(host.Cast<Transform>().Any(t => t.gameObject.activeSelf), Is.True);
                    Assert.That(host.Cast<Transform>().Any(t => t.name == "OptionalInitialInfluenceSkip"), Is.False);
                    Call(expansion, "InvokeSettlement", SettlementOptionView.FromOption(option));
                    var command = native.Port.Commands.Single();
                    Assert.That(native.Port.Last.CommandResult.Succeeded && native.Port.Last.AppliedLocally, Is.True,
                        JsonConvert.SerializeObject(native.Port.Last));
                    Assert.That(native.Session.State.CharacterUses.Single(u => u.UseId == option.TargetUseId).DoubleUseGranted, Is.True);
                    SaveEvidence("settlement", new { View = option, Command = command, HostActive = true, RealOptionInvoked = true });
                }
            }
        }

        [Test]
        public void WrongPlayerStaleRevisionAndDuplicateCannotPlaceExtraInfluence()
        {
            using (new SceneRuntimeCatalogScope())
            {
                var session = Prepare(); var request = Open(session.State);
                var projection = InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId);
                var before = GameStateCloneService.DeepClone(session.State);
                var wrong = EffectInteractionCommands.Answer(projection, request.AnsweringPlayerId, request.CandidateIds.Take(1), false);
                wrong.PlayerId = session.State.StartPlayerId;
                Assert.That(session.Submit(wrong).Succeeded, Is.False);
                Assert.That(GameStateCloneService.AreEquivalent(before, session.State), Is.True);
                var stale = EffectInteractionCommands.Answer(projection, request.AnsweringPlayerId, request.CandidateIds.Take(1), false);
                stale.Parameters["expectedRevision"] = (request.StateRevision - 1).ToString();
                Assert.That(session.Submit(stale).Succeeded, Is.False);
                Assert.That(GameStateCloneService.AreEquivalent(before, session.State), Is.True);
                var accepted = EffectInteractionCommands.Answer(projection, request.AnsweringPlayerId, request.CandidateIds.Take(1), false);
                Assert.That(session.Submit(accepted).Succeeded, Is.True);
                var once = GameStateCloneService.DeepClone(session.State);
                var duplicate = session.Submit(accepted);
                Assert.That(GameStateCloneService.AreEquivalent(once, session.State), Is.True);
                var expired = EffectInteractionCommands.Answer(projection, request.AnsweringPlayerId, request.CandidateIds.Take(1), false);
                Assert.That(session.Submit(expired).Succeeded, Is.False);
                Assert.That(GameStateCloneService.AreEquivalent(once, session.State), Is.True);
                SaveEvidence("guards", new { WrongPlayerRejected = true, StaleRevisionRejected = true,
                    DuplicateSucceeded = duplicate.Succeeded, DuplicateStateUnchanged = true, ExpiredRequestRejected = true });
            }
        }

        private static void SaveEvidence(string name, object value)
        {
            string path = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-ycR16Evidence="))?.Substring("-ycR16Evidence=".Length);
            if (path == null) return;
            using (var stream = new FileStream(Path.Combine(path, name + ".json"), FileMode.CreateNew))
            using (var writer = new StreamWriter(stream)) writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
        }
        private sealed class Port : IGameCommandPort
        {
            private readonly IGameCommandPort inner;
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public WorkflowSubmissionResult Last;
            public Port(IGameCommandPort inner) { this.inner = inner; }
            public WorkflowSubmissionResult Submit(GameCommand command) { Commands.Add(command); return Last = inner.Submit(command); }
        }
        private sealed class NativeScene : IDisposable
        {
            public object Mobile { get; private set; }
            public GameSession Session { get; private set; }
            public Port Port { get; private set; }
            private readonly GameObject contextRoot;
            private Scene scene;
            private readonly object previousContext;
            private readonly FieldInfo instance;
            private readonly object saves, previousStore;
            private readonly bool ownsSaveController;
            private NativeScene(GameState state)
            {
                Assert.That(UnityEngine.Application.isPlaying, Is.True, "真实组件通过播放生命周期装配。");
                var type = Production("GameLaunchContext");
                instance = type.GetField("<Instance>k__BackingField", Flags); previousContext = instance.GetValue(null);
                var saveType = Production("MatchSaveController");
                ownsSaveController = saveType.GetProperty("Instance", Flags).GetValue(null) == null;
                saves = saveType.GetMethod("Ensure", Flags).Invoke(null, null);
                Call(saves, "Unbind"); previousStore = Read(saves, "store");
                var storeType = Type.GetType("YC.Infrastructure.Persistence.MatchSaveStore, Assembly-CSharp", true);
                Write(saves, "store", Activator.CreateInstance(storeType, Path.Combine(Directory.GetCurrentDirectory(),
                    "Logs/QY-017-SETUP-INFLUENCE-UI-R16/isolated-saves", Guid.NewGuid().ToString("N"))));
                contextRoot = new GameObject("R16隔离双人开局上下文"); contextRoot.SetActive(false);
                var context = contextRoot.AddComponent(type); instance.SetValue(null, context);
                Write(context, "Mode", LaunchMode.Local); Write(context, "LocalPlayerId", 1);
                Write(context, "GameId", state.GameId); Write(context, "MapId", state.MapId);
                Write(context, "EventDeckSeed", state.EventDeckSeed); Write(context, "InitialRandomState", state.RandomRuntime.Clone());
                Write(context, "RuleConfiguration", state.RuleConfiguration.Clone());
                Write(context, "LaunchPlayerOrder", state.LaunchPlayerOrder.ToList());
                Write(context, "Players", state.Players.Select(p => new PlayerSeat { PlayerId = p.PlayerId, Color = p.Color }).ToList());
                contextRoot.SetActive(true);
            }
            public static IEnumerator Create(GameState state, Action<NativeScene> ready)
            {
                var native = new NativeScene(state);
                var scenePath = state.MapId == YC.Domain.Maps.StaticMapDefinitions.ThreePlayerMapId
                    ? "Assets/Scenes/ThreePlayerScene.unity" : "Assets/Scenes/SampleScene.unity";
                var loading = SceneManager.LoadSceneAsync(scenePath);
                while (!loading.isDone) yield return null;
                yield return null;
                native.scene = SceneManager.GetActiveScene();
                native.Mobile = native.scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(Production("MobileCityInteractionController"), true)).Single();
                native.Session = (GameSession)Read(native.Mobile, "session");
                native.Session.ReplaceState(state); Call(native.Mobile, "SynchronizeInteractionFromState");
                native.Port = new Port((IGameCommandPort)Read(native.Mobile, "gameplayAdapter"));
                Write(native.Mobile, "commandGateway", new CommandGateway(native.Port));
                ready(native);
            }
            public void Dispose()
            {
                if (Mobile != null) (Read(Mobile, "commandSubmission") as IDisposable)?.Dispose();
                if (saves != null)
                {
                    Call(saves, "Unbind"); Write(saves, "store", previousStore);
                    if (ownsSaveController) UnityEngine.Object.DestroyImmediate(((Component)saves).gameObject);
                }
                instance.SetValue(null, previousContext); UnityEngine.Object.DestroyImmediate(contextRoot);
            }
        }
    }
}
