using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
#if !YC_R18_CONTROLLER_OFFLINE
using NUnit.Framework;
#endif
using YC.Application.Sessions;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    // 反射连接实际控制器。仅写独立测试目录，不读取或修改用户存档，不切换真实场景。
    public sealed class ReturnToMenuSaveLifecycleR18Tests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null);
        private static object Read(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        private static async Task Wait(Task task)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted) { Require(watch.Elapsed.TotalSeconds < 15, "退出生命周期隔离用例超时"); await Task.Yield(); }
            await task;
        }
#if !YC_R18_CONTROLLER_OFFLINE
        private static System.Collections.IEnumerator AwaitNative(Task task)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                Require(watch.Elapsed.TotalSeconds < 60, "退出生命周期原生协程超时");
                yield return null;
            }
            task.GetAwaiter().GetResult();
        }
        private static readonly bool[] HostModes = { false, true };
        private UnityEditor.SceneManagement.SceneSetup[] nativeScenes;
        private System.Collections.IEnumerator RunNative(Func<Task> start)
        {
            nativeScenes = UnityEditor.SceneManagement.EditorSceneManager.GetSceneManagerSetup();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                Require(!UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty, "不能替换未保存场景");
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            yield return new UnityEngine.TestTools.EnterPlayMode();
            yield return AwaitNative(start());
        }
        [UnityEngine.TestTools.UnityTearDown]
        public System.Collections.IEnumerator RestoreNativeScenes()
        {
            if (UnityEngine.Application.isPlaying) yield return new UnityEngine.TestTools.ExitPlayMode();
            if (nativeScenes != null)
            {
                if (nativeScenes.Any(s => s.isLoaded && s.isActive)) UnityEditor.SceneManagement.EditorSceneManager.RestoreSceneManagerSetup(nativeScenes);
                else UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
                nativeScenes = null;
            }
        }
#endif
        private static void InitializeOnce(Type type, object component)
        {
            if (type.GetProperty("Instance").GetValue(null) == null) type.GetMethod("Awake", Flags).Invoke(component, null);
            Require(ReferenceEquals(type.GetProperty("Instance").GetValue(null), component), "真实Awake未建立本用例的唯一实例");
        }
        private sealed class Fixture : IDisposable
        {
            internal readonly UnityEngine.GameObject SaveObject, LaunchObject;
            internal readonly object Controller, Launch, Store;
            internal readonly GameSession Session;
            internal readonly string DirectoryPath;
            internal Fixture(bool host = false)
            {
                var controllerType = Find("YC.Presentation.MatchSaveController"); var launchType = Find("YC.Presentation.GameLaunchContext");
                Require(controllerType.GetProperty("Instance").GetValue(null) == null && launchType.GetProperty("Instance").GetValue(null) == null,
                    "本用例要求没有活动保存/启动上下文，不能接管已有会话");
                DirectoryPath = Path.Combine(Environment.GetEnvironmentVariable("QY008_R18_OUTPUT") ?? UnityEngine.Application.temporaryCachePath,
                    "R18退出生命周期", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(DirectoryPath);
                SaveObject = new UnityEngine.GameObject("R18隔离保存控制器"); LaunchObject = new UnityEngine.GameObject("R18隔离启动上下文");
                Launch = LaunchObject.AddComponent(launchType); Controller = SaveObject.AddComponent(controllerType);
                InitializeOnce(launchType, Launch); InitializeOnce(controllerType, Controller);
                Require(Read(Controller, "store") != null, "真实Awake未建立保存Store");
                var state = new GameState { GameId = "r18-leave-" + Guid.NewGuid().ToString("N") }; Session = new GameSession(state);
                Set(Launch, "Mode", host ? LaunchMode.Host : LaunchMode.Local); Set(Launch, "LocalPlayerId", 1); Set(Launch, "GameId", state.GameId);
                Store = Activator.CreateInstance(Find("YC.Infrastructure.Persistence.MatchSaveStore"), new object[] { DirectoryPath });
                // 本用例只验证离开生命周期；不创建网络运输或伪造联机确认。
                Set(Controller, "store", Store); Set(Controller, "session", Session); Set(Controller, "context", Launch);
                Set(Controller, "dirty", 1); Set(Controller, "lastFailure", "自动槽位已满");
            }
            internal bool IsOpen => (bool)Find("YC.Presentation.MatchSaveWindowView").GetProperty("IsAnyOpen").GetValue(null);
            internal Task Leave(Action onLeave = null)
            {
                var left = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                Call(Controller, "RequestLeave", (Action)(() => { onLeave?.Invoke(); left.TrySetResult(null); })); return left.Task;
            }
            public void Dispose() { UnityEngine.Object.DestroyImmediate(SaveObject); UnityEngine.Object.DestroyImmediate(LaunchObject); }
        }

#if !YC_R18_CONTROLLER_OFFLINE
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator FullSlotsAndPreviousFailureLeaveWithoutSavingOrOpeningManualPage([ValueSource(nameof(HostModes))] bool host)
            => RunNative(() => FullSlotsAndPreviousFailureLeaveWithoutSavingOrOpeningManualPageAsync(host));
        private async Task FullSlotsAndPreviousFailureLeaveWithoutSavingOrOpeningManualPageAsync(bool host)
#else
        public async Task FullSlotsAndPreviousFailureLeaveWithoutSavingOrOpeningManualPage(bool host)
#endif
        {
            using var f = new Fixture(host); Require(!f.IsOpen, "用例启动前已有存档页");
            // 即使所有槽文件均占用且不可读，返回主菜单也不得为退出读取、修复或覆盖它们。
            foreach (string kind in new[] { "auto", "manual" }) for (int i = 1; i <= 3; i++) File.WriteAllText(Path.Combine(f.DirectoryPath, kind + "-" + i + ".json"), "R18独立占用槽位");
            var before = Directory.GetFiles(f.DirectoryPath).ToDictionary(p => p, File.ReadAllText); int calls = 0;
            await Wait(f.Leave(() => calls++));
            Require(calls == 1 && !(bool)f.Controller.GetType().GetProperty("CanSave").GetValue(f.Controller), "退出未完成一次解绑及菜单回调");
            Require(!f.IsOpen && f.Controller.GetType().GetProperty("LastFailure").GetValue(f.Controller) == null, "退出重开手动保存页或保留错误循环");
            Require(Directory.GetFiles(f.DirectoryPath).Length == before.Count && before.All(p => File.ReadAllText(p.Key) == p.Value), "返回主菜单触发了保存或覆盖已有槽位");
        }

#if !YC_R18_CONTROLLER_OFFLINE
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator LeaveCancelsNotStartedRequestsAndWaitsForInFlightWorkWithoutFailurePage()
            => RunNative(LeaveCancelsNotStartedRequestsAndWaitsForInFlightWorkWithoutFailurePageAsync);
        private async Task LeaveCancelsNotStartedRequestsAndWaitsForInFlightWorkWithoutFailurePageAsync()
#else
        public async Task LeaveCancelsNotStartedRequestsAndWaitsForInFlightWorkWithoutFailurePage()
#endif
        {
            using var f = new Fixture(); var token = ((CancellationTokenSource)Read(f.Controller, "saveLifetime")).Token;
            var entered = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously); int queuedCalls = 0, menuCalls = 0;
            Func<Task<MatchSaveData>> first = async () => { entered.TrySetResult(null); await release.Task; throw new IOException("隔离在途准备失败"); };
            var writing = (Task)Call(f.Store, "SavePreparedAsync", first, "r18", "r18", -1, token);
            await Wait(entered.Task);
            Func<Task<MatchSaveData>> next = () => { queuedCalls++; throw new Exception("未启动请求不应执行"); };
            var queued = (Task)Call(f.Store, "SavePreparedAsync", next, "r18", "r18", -1, token);
            var leaving = f.Leave(() => menuCalls++); await Task.Yield();
            Require(token.IsCancellationRequested && !leaving.IsCompleted, "未取消队列或在途工作未结束就离开");
            Call(f.Controller, "RequestLeave", (Action)(() => menuCalls += 100));
            release.SetResult(null); await Wait(leaving);
            try { await writing; } catch (IOException) { }
            try { await queued; } catch (OperationCanceledException) { }
            Require(queuedCalls == 0 && menuCalls == 1 && !f.IsOpen && Directory.GetFiles(f.DirectoryPath).Length == 0,
                "取消后启动了旧请求、重复退出、重开保存页或写入文件");
        }

#if !YC_R18_CONTROLLER_OFFLINE
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator PendingSaveCallbackAndReboundSessionCannotReopenOrExitThroughOldLeave()
            => RunNative(PendingSaveCallbackAndReboundSessionCannotReopenOrExitThroughOldLeaveAsync);
        private async Task PendingSaveCallbackAndReboundSessionCannotReopenOrExitThroughOldLeaveAsync()
#else
        public async Task PendingSaveCallbackAndReboundSessionCannotReopenOrExitThroughOldLeave()
#endif
        {
            using var f = new Fixture(); Set(f.Session, "saveCriticalDepth", 1);
            var saving = (Task<bool>)Call(f.Controller, "Save", -1); var leaving = f.Leave(); await Task.Yield();
            Require(!leaving.IsCompleted, "当前事务执行中已执行退出回调");
            Call(f.Controller, "Unbind");
            Set(f.Controller, "session", new GameSession(new GameState { GameId = "r18-new-session" })); Set(f.Controller, "context", f.Launch);
            Set(f.Session, "saveCriticalDepth", 0); await Wait(saving);
            for (int n = 0; n < 8; n++) await Task.Yield();
            Require(!leaving.IsCompleted && (bool)f.Controller.GetType().GetProperty("CanSave").GetValue(f.Controller) && !f.IsOpen,
                "旧会话异步回调退出了新会话或重开存档页");
            Require(!saving.Result && Directory.GetFiles(f.DirectoryPath).Length == 0, "取消的旧保存仍成功或写入文件");
        }
    }
}
