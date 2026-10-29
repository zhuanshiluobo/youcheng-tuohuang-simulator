using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation.Workflows;
using YC.Application.Sessions;

namespace YC.Tests.EditMode
{
    // 隔离加载门控与实际场景引用回归，不代替独立程序或 Steam 联机验收。
    public sealed class GameplayLoadingProgressTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void PublishedProtocolStateDistinguishesWaitingFromReadyForPassivePlayers(bool confirmed)
        {
            // 复用规则隔离夹具创建真实 coordinator，验证发布状态而不是伪造 UI 权限。
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var fixture = typeof(DeterministicSimulationCoordinatorTests);
            var group = fixture.GetMethod("ActionGroup", flags).Invoke(null, new object[] { null, 2, 4096 });
            if (confirmed) group.GetType().GetMethod("Seal", flags).Invoke(group, new object[] { 0L, true });
            var peers = (DeterministicSimulationCoordinator[])group.GetType().GetField("Coordinators", flags).GetValue(group);
            try
            {
                foreach (var peer in peers)
                {
                    peer.StartBackgroundExecution();
                    SimulationPublishedSnapshot snapshot = null;
                    Assert.That(SpinWait.SpinUntil(() =>
                    {
                        peer.DrainMainThreadNotifications(32);
                        return peer.TryReadPublishedSnapshot(out snapshot);
                    }, TimeSpan.FromSeconds(10)), Is.True);
                    Assert.That(GameplayLoadingProgress.HasConfirmedSimulationState(snapshot, snapshot.GameId), Is.EqualTo(confirmed));
                    Assert.That(GameplayLoadingProgress.HasConfirmedSimulationState(snapshot, "previous-game"), Is.False);
                    if (confirmed && snapshot.LocalPlayerId != 1)
                        Assert.That(snapshot.ReadWindowPermissions().Any(window => window.CanSubmitForLocalPlayer), Is.False,
                            "非行动玩家没有操作权限，但确认完成后仍应进入游戏。");
                }
            }
            finally
            {
                foreach (var peer in peers.Where(value => value.IsBackgroundExecutionEnabled))
                {
                    peer.RequestStop();
                    Assert.That(SpinWait.SpinUntil(() => peer.ExecutionStopped.IsCompleted, TimeSpan.FromSeconds(10)), Is.True);
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LocalNewAndSavedGamesWaitForStartupAfterSceneResources(bool restoring)
        {
            var progress = new GameplayLoadingProgress(false, false, restoring);
            progress.SetSceneProgress(1);
            Assert.That(progress.IsFinished, Is.False);
            Assert.That(progress.Fill, Is.LessThan(1));
            progress.BeginInitialization();
            Assert.That(progress.Stage, Is.EqualTo(restoring ? GameplayLoadingStage.Restoring : GameplayLoadingStage.Initializing));
            progress.CompleteLocal(true);
            Assert.That(progress.IsFinished, Is.False, "场景已显示不代表存档及界面准备完成。");
            progress.MarkSessionPrepared();
            progress.CompleteLocal(false);
            Assert.That(progress.IsFinished, Is.False);
            progress.CompleteLocal(true);
            Assert.That(progress.Stage, Is.EqualTo(GameplayLoadingStage.Ready));
            Assert.That(progress.Fill, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HostWaitsForEveryPlayerAndConfirmedState(bool restoring)
        {
            var progress = Prepared(true, restoring);
            progress.ObserveNetwork(0, 0, 1, 4, true, true, false, false);
            Assert.That(progress.Stage, Is.EqualTo(GameplayLoadingStage.WaitingForPlayers));
            Assert.That(progress.ReadyPlayers, Is.EqualTo(1));
            progress.ObserveNetwork(0, 0, 3, 4, true, true, true, false);
            Assert.That(progress.IsFinished, Is.False, "有玩家退出或尚未装载完，不能提前完成。");
            progress.ObserveNetwork(0, 0, 4, 4, true, true, false, false);
            Assert.That(progress.IsFinished, Is.False, "收齐安装确认后仍需确认规则状态。");
            progress.ObserveNetwork(0, 0, 4, 4, true, true, true, false);
            Assert.That(progress.Stage, Is.EqualTo(GameplayLoadingStage.Ready));
        }

        [Test]
        public void ClientWaitsForInstallProjectionAndConfirmationAfterReceivingAllBytes()
        {
            var progress = Prepared(false, true);
            progress.ObserveNetwork(25, 100, 0, 4, false, false, false, false);
            Assert.That(progress.ReceivedBytes, Is.EqualTo(25));
            Assert.That(progress.TotalBytes, Is.EqualTo(100));
            Assert.That(progress.Stage, Is.EqualTo(GameplayLoadingStage.Synchronizing));
            progress.ObserveNetwork(100, 100, 0, 4, false, true, true, false);
            Assert.That(progress.IsFinished, Is.False);
            progress.ObserveNetwork(100, 100, 0, 4, true, false, true, false);
            Assert.That(progress.IsFinished, Is.False);
            progress.ObserveNetwork(100, 100, 0, 4, true, true, false, false);
            Assert.That(progress.IsFinished, Is.False);
            progress.ObserveNetwork(100, 100, 0, 4, true, true, true, false);
            Assert.That(progress.Stage, Is.EqualTo(GameplayLoadingStage.Ready), "非行动玩家也应进入页面；客户端不等待只有房主掌握的安装人数。");
        }

        [Test]
        public void FailureDoesNotBecomeSuccessAndNextLoadStartsFresh()
        {
            var progress = Prepared(false, true);
            progress.ObserveNetwork(50, 100, 0, 4, false, false, false, true);
            Assert.That(progress.Stage, Is.EqualTo(GameplayLoadingStage.Failed));
            progress.ObserveNetwork(100, 100, 4, 4, true, true, true, false);
            Assert.That(progress.Stage, Is.EqualTo(GameplayLoadingStage.Failed));
            Assert.That(progress.Fill, Is.LessThan(1));
            var next = new GameplayLoadingProgress(true, false, false);
            Assert.That(next.Stage, Is.EqualTo(GameplayLoadingStage.Scene));
            Assert.That(next.ReceivedBytes, Is.Zero);
            Assert.That(next.SessionPrepared, Is.False);
        }

        [Test]
        public void ActualLoadingSceneHasStageAndReturnBindings()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/LoadingScene.unity", OpenSceneMode.Additive);
            try
            {
                var type = Type.GetType("YC.Presentation.LoadingSceneController, Assembly-CSharp", true);
                var controller = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(type, true)).Single();
                object Read(string field) => type.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
                Assert.That(Read("tipsText"), Is.Not.Null);
                Assert.That(Read("progressFill"), Is.Not.Null);
                var button = (Button)Read("returnToMenuButton");
                Assert.That(button, Is.Not.Null);
                Assert.That(button.targetGraphic.raycastTarget, Is.True);
                Assert.That(button.GetComponentsInParent<CanvasGroup>(true).All(group => group.interactable && group.blocksRaycasts), Is.True,
                    "返回入口不能被加载层自身禁止点击。");
                foreach (var field in new[] { "sceneStageText", "hostInitializationText", "restoreStageText", "preparingSynchronizationText",
                    "awaitingHostText", "receivingFormat", "hostWaitingText", "confirmingText", "readyText", "failedText" })
                    Assert.That(Read(field), Is.Not.Null.And.Not.Empty, field);
                var model = Prepared(false, true);
                model.ObserveNetwork(1048576, 2097152, 0, 3, false, false, false, false);
                type.GetField("gameplayProgress", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, model);
                type.GetMethod("RenderGameplayProgress", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, null);
                Assert.That(((Text)Read("tipsText")).text, Is.EqualTo(string.Format((string)Read("receivingFormat"), 1f, 2f)));
                Assert.That(((Text)Read("progressText")).text, Is.EqualTo(string.Format((string)Read("percentageFormat"), 50)));
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        private static GameplayLoadingProgress Prepared(bool host, bool restoring)
        {
            var progress = new GameplayLoadingProgress(true, host, restoring);
            progress.BeginInitialization();
            progress.MarkSessionPrepared();
            return progress;
        }
    }
}
