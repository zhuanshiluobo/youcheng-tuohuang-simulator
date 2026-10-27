using System;
using System.IO;
using System.Linq;
using System.Reflection;
#if !YC_PERFORMANCE_SAVE_R20_OFFLINE
using NUnit.Framework;
#endif
using YC.Application.Sessions;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    // 只核启动路径解析和Store绑定，不创建/读取正式用户存档，不计正常游戏性能验收。
    public sealed class PerformanceSavePathR20Tests
    {
        private const string Option = "--yc-performance-save-root=";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        private static string Resolve(string normal, params string[] args)
        {
            var type = Find("YC.Presentation.PerformanceSavePath"); Require(type != null, "当前检查需要Editor或Development解析器");
            try { return (string)type.GetMethod("Select", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { normal, args }); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private static string NewRoot() => Path.Combine(Path.GetTempPath(), "YC-R20-parse-" + Guid.NewGuid().ToString("N"));
#if !YC_PERFORMANCE_SAVE_R20_OFFLINE
        private UnityEditor.SceneManagement.SceneSetup[] nativeScenes;
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

#if !YC_PERFORMANCE_SAVE_R20_OFFLINE
        [Test]
#endif
        public void NoOptionKeepsOriginalPathAndUnrelatedArgumentsDoNotCreateDirectories()
        {
            string root = NewRoot(), normal = Path.Combine(root, "normal", "..", "MatchSaves");
            Require(Resolve(normal, "program", "--unrelated-option") == normal && !Directory.Exists(root), "默认路径被规范化、切换或创建目录");
        }

#if !YC_PERFORMANCE_SAVE_R20_OFFLINE
        [Test]
#endif
        public void ExplicitAbsoluteDirectoryIsNormalizedWithoutFilesystemWrites()
        {
            string root = NewRoot(), normal = Path.Combine(root, "normal", "MatchSaves");
            string requested = Path.Combine(root, "samples", "unused", "..", "pid-101");
            Require(Resolve(normal, Option + requested) == Path.GetFullPath(requested) && !Directory.Exists(root), "显式绝对路径解析不正确或产生I/O写入");
            Require(Resolve(normal, Option + normal + "-sample") == Path.GetFullPath(normal + "-sample"), "同名前缀但非父子目录被误判");
        }

#if !YC_PERFORMANCE_SAVE_R20_OFFLINE
        [Test]
#endif
        public void InvalidMalformedAndOverlappingPathsFailInsteadOfUsingNormalSaves()
        {
            string root = NewRoot(), normal = Path.Combine(root, "normal", "MatchSaves");
            var invalid = new System.Collections.Generic.List<string> { Option, Option + " ", Option + "relative", "--yc-performance-save-root", "--yc-performance-save-rooted=" + root,
                "--YC-PERFORMANCE-SAVE-ROOT=" + root, Option + Path.GetPathRoot(root), Option + normal, Option + Path.Combine(normal, "child"), Option + Path.GetDirectoryName(normal),
                Option + root + Path.DirectorySeparatorChar + "invalid\0name" };
            if (Path.DirectorySeparatorChar == '\\') invalid.AddRange(new[] { Option + "C:relative", Option + @"\rooted-without-drive", Option + @"\\?\C:\sample", Option + @"\\.\C:\sample",
                Option + root + Path.DirectorySeparatorChar + "name*", Option + root + Path.DirectorySeparatorChar + "name:stream", Option + root + Path.DirectorySeparatorChar + "CON",
                Option + root + Path.DirectorySeparatorChar + "trailing.", Option + root + Path.DirectorySeparatorChar + "trailing " });
            foreach (string argument in invalid)
            {
                bool rejected = false; try { Resolve(normal, argument); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "非法参数静默回退或被接受:" + argument);
            }
            Require(!Directory.Exists(root), "非法参数处理创建了目录");
        }

#if !YC_PERFORMANCE_SAVE_R20_OFFLINE
        [Test]
#endif
        public void RepeatedOptionsAreRejectedEvenWhenTheyNameTheSameDirectory()
        {
            string root = NewRoot(), normal = Path.Combine(root, "normal", "MatchSaves");
            foreach (string second in new[] { root, root + "-other" })
            {
                bool rejected = false; try { Resolve(normal, Option + root, Option + second); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "重复参数被最后一次覆盖或回退默认目录");
            }
            Require(!Directory.Exists(root), "重复参数处理创建了目录");
        }

        private static void InitializeOnce(Type type, object component)
        {
            var instance = type.GetProperty("Instance").GetValue(null);
            if (instance == null) type.GetMethod("Awake", Private).Invoke(component, null);
            Require(ReferenceEquals(type.GetProperty("Instance").GetValue(null), component), "真实Awake未建立本用例的唯一实例");
        }

#if !YC_PERFORMANCE_SAVE_R20_OFFLINE
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator ControllerChoosesOneStoreAndRebindingKeepsAllSixSlotRoutes()
        {
            nativeScenes = UnityEditor.SceneManagement.EditorSceneManager.GetSceneManagerSetup();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                Require(!UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty, "不能替换未保存场景");
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            yield return new UnityEngine.TestTools.EnterPlayMode();
            CheckControllerStore();
        }
        private void CheckControllerStore()
#else
        public void ControllerChoosesOneStoreAndRebindingKeepsAllSixSlotRoutes()
#endif
        {
            var controllerType = Find("YC.Presentation.MatchSaveController"); var launchType = Find("YC.Presentation.GameLaunchContext");
            Require(controllerType.GetProperty("Instance").GetValue(null) == null && launchType.GetProperty("Instance").GetValue(null) == null,
                "本用例要求无活动保存会话，不能接管已有上下文");
            var gameObject = new UnityEngine.GameObject("R20隔离保存控制器"); var launchObject = new UnityEngine.GameObject("R20隔离启动上下文");
            try
            {
                var launch = launchObject.AddComponent(launchType); var controller = gameObject.AddComponent(controllerType);
                // EditMode不保证AddComponent派发Awake；PlayMode/离线端口已派发时不再调用。
                InitializeOnce(launchType, launch); InitializeOnce(controllerType, controller);
                var store = controllerType.GetField("store", Private).GetValue(controller);
                Require(store != null, "真实Awake未建立Store，不能注入替代实例冒通过");
                string normal = Path.Combine(UnityEngine.Application.persistentDataPath, "MatchSaves");
                string selected = Find("YC.Presentation.PerformanceSavePath") == null ? normal : Resolve(normal, Environment.GetCommandLineArgs());
                Require((string)store.GetType().GetField("directory", Private).GetValue(store) == selected, "初始化没有把已选路径传入唯一Store");
                launchType.GetField("Mode").SetValue(launch, LaunchMode.Local);
                controllerType.GetMethod("Bind").Invoke(controller, new object[] { new GameSession(new GameState { GameId = "r20-first" }), launch });
                controllerType.GetMethod("Unbind").Invoke(controller, null);
                controllerType.GetMethod("Bind").Invoke(controller, new object[] { new GameSession(new GameState { GameId = "r20-second" }), launch });
                Require(ReferenceEquals(store, controllerType.GetField("store", Private).GetValue(controller)), "重新绑定创建了第二套Store");
                for (int i = 0; i < 6; i++)
                {
                    string expected = Path.Combine(selected, (i < 3 ? "manual-" : "auto-") + (i % 3 + 1) + ".json");
                    Require((string)store.GetType().GetMethod("PathFor", Private).Invoke(store, new object[] { i }) == expected,
                        "自动/手动或读取槽位未使用相同选定目录");
                }
                Require(!((System.Threading.Tasks.Task)store.GetType().GetMethod("FlushAsync").Invoke(store, null)).IsFaulted, "仅绑定/解析路径产生了保存故障");
            }
            finally { UnityEngine.Object.DestroyImmediate(gameObject); UnityEngine.Object.DestroyImmediate(launchObject); }
        }
    }
}
