using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.State;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    // 仅装配实际展示守卫与发布端口；不运行Awake、网络、规则或正常菜单验收。
    public sealed class VisibleSnapshotSourceTransitionR55Tests
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private readonly List<GameObject> owned = new List<GameObject>();
        private object mobile, launch, previousLaunch;
        private GameSession session;
        private GameState state;
        private GameStateView view;
        private static Type Presentation(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target)
            ?? target.GetType().GetProperty(name, Flags)?.GetValue(target);
        private static void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, Flags);
            if (field != null) field.SetValue(target, value);
            else target.GetType().GetProperty(name, Flags).SetValue(target, value);
        }
        private static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private object InactiveComponent(string name)
        {
            var root = new GameObject("R55隔离守卫-" + name); root.SetActive(false); owned.Add(root);
            return root.AddComponent(Presentation(name));
        }
        private static GameState State() => new GameState { GameId = "r55-visible-source", ContentPackHash = "r55-content",
            EffectRuntime = new EffectRuntimeState { StateRevision = 41 } };
        private static GameStateView View() => new GameStateView { GameId = "r55-visible-source", ContentPackHash = "r55-content",
            StateRevision = 41, ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1 };

        [SetUp]
        public void PrepareOnlyInactivePresentationObjects()
        {
            var instance = Presentation("GameLaunchContext").GetProperty("Instance", Flags);
            previousLaunch = instance.GetValue(null);
            launch = InactiveComponent("GameLaunchContext"); Set(launch, "Mode", LaunchMode.Host);
            instance.SetValue(null, launch);
            mobile = InactiveComponent("MobileCityInteractionController");
            state = State(); view = View(); session = new GameSession(state); Set(session, "View", view);
            Set(mobile, "session", session); Set(mobile, "localPlayerId", 1);
        }
        [TearDown]
        public void ReleaseOnlyOwnedObjects()
        {
            Presentation("GameLaunchContext").GetProperty("Instance", Flags).SetValue(null, previousLaunch);
            foreach (var root in owned) Object.DestroyImmediate(root);
            owned.Clear();
        }
        private object Submission(bool simulated)
        {
            var value = Activator.CreateInstance(Presentation("CommandSubmissionController"), Flags, null,
                new object[] { session, launch, 1, null, null, null, null, null, null }, null);
            Set(value, "usesDeterministicSimulation", simulated); return value;
        }
        private void BindWithoutPublication()
        {
            Call(session, "BindBackgroundExecution", 55L, 1);
            Assert.That(() => { var unreadable = session.State; }, Throws.InvalidOperationException.With.Message.EqualTo("simulation_snapshot_not_ready"));
        }
        private Func<bool> Guard(GameStateView snapshot = null, bool shared = false)
        {
            if (!shared) return (Func<bool>)Call(mobile, "CreateVisibleSnapshotGuard", snapshot ?? view);
            var expansion = InactiveComponent("GameplayExpansionUi");
            Set(expansion, "presentationBinding", new object());
            Set(expansion, "getPlayer", (Func<int>)(() => (int)Read(mobile, "localPlayerId")));
            Set(expansion, "CreateSnapshotGuard", (Func<GameStateView, Func<bool>>)(s => (Func<bool>)Call(mobile, "CreateVisibleSnapshotGuard", s)));
            return (Func<bool>)Call(expansion, "GuardSnapshot", snapshot ?? view);
        }
        private void Publish(object submission, GameState publishedState, GameStateView publishedView)
        {
            Call(session, "PublishBackgroundReadState", 55L, 0L, publishedState, publishedView, (Func<bool>)(() => true));
            Set(submission, "simulationProjectionState", publishedState); Set(submission, "simulationProjectionView", publishedView);
        }

        [TestCase(false)] [TestCase(true)]
        public void OldGuardExpiresBeforeUnpublishedSourceIsRead(bool shared)
        {
            var guard = Guard(shared: shared);
            Assert.That(guard(), Is.True, "绑定前实际守卫须有效");
            Set(mobile, "commandSubmission", Submission(true)); BindWithoutPublication();
            Assert.That(guard(), Is.False, "旧守卫必须先识别提交源更换，不能读取未发布State");
        }

        [TestCase(false)] [TestCase(true)]
        public void SameSubmissionModeFlipExpiresBeforeUnpublishedState(bool shared)
        {
            var submission = Submission(false); Set(mobile, "commandSubmission", submission);
            var guard = Guard(shared: shared); Assert.That(guard(), Is.True);
            Set(submission, "usesDeterministicSimulation", true); BindWithoutPublication();
            Assert.That(guard(), Is.False, "即使同一提交对象，也须检测读取模式变化");
        }

        [Test]
        public void CreationWithoutSnapshotOrPublicationIsExpiredWithoutReadingState()
        {
            var submission = Submission(true); Set(mobile, "commandSubmission", submission); BindWithoutPublication();
            Assert.That(Guard()(), Is.False);
            Assert.That(((Func<bool>)Call(mobile, "CreateVisibleSnapshotGuard", new object[] { null }))(), Is.False);
        }

        [Test]
        public void LegalPublicationRebuildsAndSameRevisionNewSourceInvalidates()
        {
            var submission = Submission(true); Set(mobile, "commandSubmission", submission); BindWithoutPublication();
            Assert.That(Guard()(), Is.False);
            Publish(submission, state, view);
            var current = Guard(shared: true); Assert.That(current(), Is.True);
            var replacementState = State(); var replacementView = View();
            Publish(submission, replacementState, replacementView);
            Assert.That(current(), Is.False, "相同GameId/修订/观看者不能替代发布来源身份");
            Assert.That(Guard(replacementView, true)(), Is.True, "原调用路径可用合法发表图重建守卫");
        }

        [TestCase("session")] [TestCase("submission")] [TestCase("launch")]
        [TestCase("readMode")] [TestCase("viewer")] [TestCase("game")]
        [TestCase("content")] [TestCase("revision")] [TestCase("snapshotRole")]
        public void ExistingSourceAndPermissionIdentityChangesStillExpire(string changed)
        {
            var guard = Guard(shared: true); Assert.That(guard(), Is.True);
            switch (changed)
            {
                case "session": Set(mobile, "session", new GameSession(State())); break;
                case "submission": Set(mobile, "commandSubmission", Submission(false)); break;
                case "launch": Presentation("GameLaunchContext").GetProperty("Instance", Flags).SetValue(null, InactiveComponent("GameLaunchContext")); break;
                case "readMode": Set(launch, "Mode", LaunchMode.Client); break;
                case "viewer": Set(mobile, "localPlayerId", 2); break;
                case "game": view.GameId = "other-game"; break;
                case "content": view.ContentPackHash = "other-content"; break;
                case "revision": view.StateRevision++; break;
                case "snapshotRole": view.ViewerRole = GameStateViewerRole.Spectator; break;
            }
            Assert.That(guard(), Is.False, changed);
        }

        [Test]
        public void LeavingGameplayInvalidatesBeforeUnpublishedStateAccess()
        {
            var guard = Guard(); Set(launch, "returningToStart", true); BindWithoutPublication();
            Assert.That(guard(), Is.False);
        }

        [Test]
        public void ExistingLocalHotseatReadPathRemainsValidAndChecksViewerRevision()
        {
            Set(launch, "Mode", LaunchMode.Local); Set(session, "View", null);
            var guard = Guard(); Assert.That(guard(), Is.True);
            Set(mobile, "localPlayerId", 2); Assert.That(guard(), Is.False);
            Set(mobile, "localPlayerId", 1); state.EffectRuntime.StateRevision++; Assert.That(guard(), Is.False);
        }

        [Test]
        public void ExistingInspectionInstanceAndFaceCheckRemainsStrict()
        {
            var expected = new PlayerDetailEntry { EntryId = "detail", SourceItemId = "ability",
                InspectionSourceItemId = "instance-a", InspectionDefinitionId = "definition", InspectionFaceId = "A" };
            var current = new PlayerDetailEntry { EntryId = "detail", SourceItemId = "ability",
                InspectionSourceItemId = "instance-a", InspectionDefinitionId = "definition", InspectionFaceId = "A" };
            Assert.That(Guard()() && PlayerDetailEntryProjector.SameInspectionSource(expected, current), Is.True);
            current.InspectionSourceItemId = "instance-b";
            Assert.That(PlayerDetailEntryProjector.SameInspectionSource(expected, current), Is.False);
            current.InspectionSourceItemId = "instance-a"; current.InspectionFaceId = "B";
            Assert.That(PlayerDetailEntryProjector.SameInspectionSource(expected, current), Is.False);
        }
    }
}
