using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    // 原录制请求/控件的UI隔离装配，不是原完整规则状态或正常对局重放。
    public sealed class MapInputOwnerAndReadinessR56Tests
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, Flags);
            if (field != null) field.SetValue(target, value); else target.GetType().GetProperty(name, Flags).SetValue(target, value);
        }
        private static object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Flags).Invoke(target, args);

        [Test]
        public void RecordedRequiredMapFirstClickKeepsHighlightsDespiteUnrenderedOptionalRequest()
        {
            using var f = new Fixture(true);
            Assert.That(f.Buttons.Count(b => (bool)b.GetType().GetProperty("IsInteractive").GetValue(b)), Is.EqualTo(23));
            f.Router.OnInfluenceSlotClicked(f.Target, 22901); f.Apply(f.Router.BuildActivePresentation());
            Assert.That(f.Commands, Is.Empty, "首击只预选");
            Assert.That(f.Highlights.Count, Is.EqualTo(23), "未获展示权的可选宿主不能接管同一必答地图输入并清空高亮");
            Assert.That(f.Highlights.Single(h => h.TargetId == f.Target).State, Is.EqualTo(WorkflowHighlightState.PendingConfirmation));
            f.Router.OnInfluenceSlotClicked(f.Target, 22942);
            Assert.That(f.Commands.Count, Is.EqualTo(1));
            Assert.That(f.Commands[0].Parameters["interactionId"], Is.EqualTo(f.Request.InteractionId));
            Assert.That(f.Commands[0].Parameters["expectedRevision"], Is.EqualTo("3720"));
            Assert.That(f.Commands[0].PlayerId, Is.EqualTo(3));
            f.Router.OnInfluenceSlotClicked(f.Target, 22943);
            Assert.That(f.Commands.Count, Is.EqualTo(1), "已发出的原命令不重发");
        }

        [Test]
        public void RequiredMapWithoutOptionalSiblingUsesNormalTwoClickConfirmation()
        {
            using var f = new Fixture(false);
            f.Router.OnInfluenceSlotClicked(f.Target, 1); f.Apply(f.Router.BuildActivePresentation());
            Assert.That(f.Commands, Is.Empty); Assert.That(f.Router.HasPendingMapConfirmation, Is.True);
            f.Router.OnInfluenceSlotClicked(f.Target, 2); Assert.That(f.Commands.Count, Is.EqualTo(1));
        }

        [TestCase("revision")] [TestCase("candidateVersion")] [TestCase("viewer")]
        public void ChangedMapContextCannotConfirmOldSelection(string change)
        {
            using var f = new Fixture(false);
            f.Router.OnInfluenceSlotClicked(f.Target, 1);
            if (change == "revision") { f.Request.StateRevision++; f.View.StateRevision++; }
            else if (change == "candidateVersion") f.Request.CandidateSetVersion++;
            else f.Player = 2;
            f.Router.OnInfluenceSlotClicked(f.Target, 2);
            Assert.That(f.Commands, Is.Empty);
        }

        [Test]
        public void SelectedMapRendererOwnsItsRouteAndRevisionRefreshRequiresNewPreselection()
        {
            using var f = new Fixture(false);
            var route = f.OwnRoute();
            f.Router.OnRouteClicked("r56-route", 1);
            Assert.That(f.Router.HasPendingMapConfirmation, Is.True);
            route.StateRevision++; f.View.StateRevision++;
            f.Apply(f.Router.BuildActivePresentation());
            Assert.That(f.Router.HasPendingMapConfirmation, Is.False);
            f.RenderRoute(route);
            f.Router.OnRouteClicked("r56-route", 2); Assert.That(f.Commands, Is.Empty);
            f.Router.OnRouteClicked("r56-route", 3); Assert.That(f.Commands.Count, Is.EqualTo(1));
            Assert.That(f.Commands[0].Parameters["interactionId"], Is.EqualTo(route.InteractionId));
        }

        [TestCase("ready")] [TestCase("pending")] [TestCase("submitted")]
        [TestCase("revision")] [TestCase("candidateVersion")] [TestCase("player")]
        [TestCase("request")] [TestCase("game")]
        public void DriverRereadsExactContextAndNeverRepeatsAnAlreadySubmittedControl(string change)
        {
            var expected = new InteractionRequestProjection { VisibleToViewer = true, Status = "open", InteractionId = "request-r56",
                AnsweringPlayerId = 3, StateRevision = 3720, CandidateSetId = "candidates", CandidateSetVersion = 1,
                InteractionTypeId = "effect.map.location.target", CandidateIds = new List<string> { "a", "b" },
                AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
            var current = GameStateCloneService.DeepClone(expected);
            int player = 3, revision = 3720; string game = "r56";
            bool submitted = change == "submitted", ready = change != "pending";
            if (change == "revision") { current.StateRevision++; revision++; }
            if (change == "candidateVersion") current.CandidateSetVersion++;
            if (change == "player") player = 2;
            if (change == "request" || submitted) current.InteractionId = "other-request";
            if (change == "game") game = "other-game";
            var type = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
            var decision = (string)type.GetMethod("MapFollowupDecision", Flags).Invoke(null,
                new object[] { "r56", 3, 3720, expected, game, player, revision, current, submitted, ready });
            Assert.That(decision, Is.EqualTo(submitted ? "submitted" : change == "ready" ? "ready" : change == "pending" ? "pending" : "changed"));
        }

        [Test]
        public void ActualContinuationPolicyBoundsUnchangedRequiredInputAndStopsOnUnhealthyIdentity()
        {
            var type = Type.GetType("YC.Presentation.Validation.HotseatUiValidationContinuationPolicy, Assembly-CSharp", true);
            var policy = Activator.CreateInstance(type, true);
            bool Reread(string key, bool exact = true, bool protocol = true, bool runtime = true) =>
                (bool)Call(policy, "TryReread", key, exact, protocol, runtime);
            Assert.That(Reread("same-request"), Is.True); Assert.That(Reread("same-request"), Is.True); Assert.That(Reread("same-request"), Is.True);
            Assert.That(Reread("same-request"), Is.False, "同版本/请求无进展不能无限等待或算通过");
            Assert.That(Reread("real-next-revision"), Is.True);
            Assert.That(Reread("wrong-viewer", exact: false), Is.False);
            Assert.That(Reread("protocol-fault", protocol: false), Is.False);
            Assert.That(Reread("runtime-fault", runtime: false), Is.False);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly List<GameObject> owned = new List<GameObject>();
            private readonly IInteractionRequestRenderer map, character;
            private readonly GameState state;
            private readonly List<InteractionRequest> requests;
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public readonly List<Component> Buttons = new List<Component>();
            public List<WorkflowHighlight> Highlights = new List<WorkflowHighlight>();
            public readonly InteractionRouter Router = new InteractionRouter(_ => { });
            public readonly InteractionRequest Request;
            public readonly GameStateView View;
            public int Player = 3;
            public string Target;

            public Fixture(bool optionalSibling)
            {
                var path = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath,
                    "../prompt/企业家内容实施规划-20261019/实施记录/QY-017-MAP-FIRST-CLICK-HIGHLIGHT-AND-DRIVER-READINESS-R56/录制请求与控件.json"));
                var facts = JObject.Parse(File.ReadAllText(path));
                requests = facts["OpenRequests"].ToObject<List<InteractionRequest>>();
                if (!optionalSibling) requests.RemoveAll(r => r.InteractionTypeId == "action.decline_effect");
                Request = requests.Single(r => r.InteractionTypeId == "lua.choice");
                var slots = facts["Highlights"].Select(h => (string)h["TargetId"]).ToArray(); Target = slots[0];
                state = new GameState { GameId = (string)facts["GameId"], ContentPackHash = "r56-isolated-view",
                    EffectRuntime = new EffectRuntimeState { StateRevision = 3720 } };
                state.EffectRuntime.InteractionRequests.AddRange(requests);
                View = new GameStateView { GameId = state.GameId, ContentPackHash = state.ContentPackHash,
                    ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 3, StateRevision = 3720 };
                View.Players.Add(new PlayerStateView { PlayerId = 3 });
                for (var i = 0; i < slots.Length; i++)
                {
                    // 原件未含完整地图；仅装配真实候选ID和真实控件ID的一对一UI映射，不执行移除规则。
                    View.Map.Influences.Add(new InfluencePlacementView { InfluenceId = Request.CandidateIds[i], SlotId = slots[i], PlayerId = 3 });
                    var buttonRoot = new GameObject("R56真实地图按钮"); owned.Add(buttonRoot);
                    var face = buttonRoot.AddComponent<SpriteRenderer>(); var hit = buttonRoot.AddComponent<PolygonCollider2D>();
                    var button = buttonRoot.AddComponent(T("MapButtonView"));
                    Set(button, "image", face); Set(button, "hitArea", hit);
                    Set(button, "targetKind", WorkflowHighlightTargetKind.InfluenceSlot); Set(button, "targetId", slots[i]); Buttons.Add(button);
                }
                var canvas = new GameObject("R56隔离原地图宿主", typeof(RectTransform), typeof(Canvas)); owned.Add(canvas); canvas.SetActive(false);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
                var registry = prefab.GetComponentInChildren(T("GameplayDialogRegistry"), true);
                var dialog = Activator.CreateInstance(T("CharacterCardEffectChoiceDialog"), Flags, null,
                    new object[] { registry, canvas.GetComponent<RectTransform>() }, null);
                var args = new object[] { (Func<GameState>)(() => state), (Func<int>)(() => Player), dialog,
                    (Action<IReadOnlyList<WorkflowHighlight>>)Paint, (Action)(() => Paint(Array.Empty<WorkflowHighlight>())),
                    (Action<GameCommand>)(c => Commands.Add(c)), (Action<string>)(_ => { }) };
                map = (IInteractionRequestRenderer)Activator.CreateInstance(T("MapEffectInteractionUiCoordinator"), args);
                character = (IInteractionRequestRenderer)Activator.CreateInstance(T("CharacterAbilityInteractionUiCoordinator"), args);
                foreach (var renderer in new[] { map, character })
                {
                    Set(renderer, "GetVisibleRequests", (Func<IReadOnlyList<InteractionRequest>>)(() => requests));
                    Set(renderer, "GetVisibleState", (Func<GameStateView>)(() => View));
                    Router.Register((IInteraction)renderer);
                }
                Set(character, "CanSubmit", (Func<bool>)(() => true));
                character.Render(InteractionRequestProjector.ProjectForPlayer(Request, 3));
            }
            private void Paint(IReadOnlyList<WorkflowHighlight> values)
            {
                Highlights = values.ToList();
                foreach (var button in Buttons)
                {
                    var id = (string)button.GetType().GetProperty("TargetId").GetValue(button);
                    var value = values.FirstOrDefault(h => h.TargetId == id);
                    Call(button, "SetState", value != null, value != null &&
                        (value.State == WorkflowHighlightState.PendingConfirmation || value.State == WorkflowHighlightState.Selected), value?.IsInteractive == true);
                }
            }
            public void Apply(InteractionPresentation value) { if (value.ReplacesHighlights) Paint(value.Highlights); }
            public InteractionRequest OwnRoute()
            {
                character.Clear(); requests.Clear();
                var route = new InteractionRequest { InteractionId = "r56-owned-route", InteractionTypeId = "effect.route.choose",
                    Visibility = "owner", AnsweringPlayerId = 3, Status = "open", StateRevision = 3720,
                    CandidateSetId = "r56-route-set", CandidateSetVersion = 1, AnswerSchema = "candidate_id",
                    MinSelections = 1, MaxSelections = 1, CandidateIds = new List<string> { "r56-route" } };
                requests.Add(route); RenderRoute(route); return route;
            }
            public void RenderRoute(InteractionRequest route)
            { state.EffectRuntime.StateRevision = route.StateRevision; map.Render(InteractionRequestProjector.ProjectForPlayer(route, 3)); }
            public void Dispose()
            {
                map?.Clear(); character?.Clear(); foreach (var item in owned) Object.DestroyImmediate(item);
            }
        }
    }
}
