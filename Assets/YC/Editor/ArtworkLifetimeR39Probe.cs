#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YC.Presentation;

/// <summary>有限Editor PlayMode隔离：空源场景中的真实卡图，经正式退出/加载入口检查；不计正常对局。</summary>
public static class ArtworkLifetimeR39Probe
{
    private const string Pending = "QY018.R39.native.pending";
    private const string MissingScene = "QY018_R39_EXPECTED_MISSING_SCENE";
    private static readonly BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static string output;
    private static IEnumerator routine;
    private static int nextFrame;
    private static double started;
    private static bool expectedLoadFailure;
    private static bool sawNullLoadFailure;
    private static readonly List<string> checks = new List<string>();
    private static readonly List<string> unexpectedErrors = new List<string>();

    public static void Run()
    {
        try
        {
            output = Output();
            Require(Directory.Exists(output), "独立输出目录不存在");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Pending, true);
            Resume(); // 同时兼容项目关闭Enter Play Mode域重载；不改项目设置。
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Fail(error); }
    }
    private static string Output()
    {
        var arg = Environment.GetCommandLineArgs().FirstOrDefault(v => v.StartsWith("--qy018-r39-output=", StringComparison.Ordinal));
        Require(arg != null, "缺少明确输出参数");
        var path = Path.GetFullPath(arg.Substring("--qy018-r39-output=".Length));
        var root = Path.GetFullPath("prompt/企业家内容实施规划-20261019/实施记录/QY-018-ARTWORK-LIFETIME-RELEASE-R39") + Path.DirectorySeparatorChar;
        Require(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "输出目录越界");
        return path;
    }
    [InitializeOnLoadMethod]
    private static void Resume()
    {
        if (!SessionState.GetBool(Pending, false)) return;
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        started = EditorApplication.timeSinceStartup;
    }
    private static void Tick()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 240) throw new TimeoutException("R39有限检查超时");
            if (!UnityEngine.Application.isPlaying || Time.frameCount < 2) return;
            if (routine == null)
            {
                output = Output();
                UnityEngine.Application.logMessageReceived += OnLog;
                routine = Check();
            }
            if (Time.frameCount < nextFrame) return;
            Require(unexpectedErrors.Count == 0, string.Join("\n", unexpectedErrors));
            if (routine.MoveNext()) { nextFrame = Time.frameCount + 1; return; }
            Write("原生有限结果.json", new { Passed = true, IsolatedEditorPlayMode = true, NormalGameplayClaimed = false,
                MainMvid = typeof(ExternalContentRuntime).Assembly.ManifestModule.ModuleVersionId, Checks = checks, unexpectedErrors });
            Stop(); EditorApplication.Exit(0);
        }
        catch (Exception error) { Fail(error); }
    }
    private static void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        if (expectedLoadFailure && (message.Contains(MissingScene) || message == "Unity 未能创建场景加载任务。"))
        {
            if (message == "Unity 未能创建场景加载任务。") sawNullLoadFailure = true;
            Append(new { ExpectedFailure = true, message, stack }); return;
        }
        unexpectedErrors.Add(message + "\n" + stack);
    }
    private static void Stop()
    {
        SessionState.SetBool(Pending, false);
        EditorApplication.update -= Tick;
        UnityEngine.Application.logMessageReceived -= OnLog;
    }
    private static void Fail(Exception error)
    {
        if (string.IsNullOrEmpty(output)) { try { output = Output(); } catch { } }
        if (!string.IsNullOrEmpty(output) && Directory.Exists(output))
            Write("首次原生失败.json", new { Passed = false, Error = error.ToString(), Checks = checks, unexpectedErrors });
        Stop(); Debug.LogException(error); EditorApplication.Exit(1);
    }
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonConvert.SerializeObject(value, Formatting.Indented));
    private static void Append(object value) => File.AppendAllText(Path.Combine(output, "实际路径证据.jsonl"), JsonConvert.SerializeObject(value) + "\n");
    private static void Passed(string name, object evidence)
    { checks.Add(name); Append(new { Name = name, Passed = true, Frame = Time.frameCount, Evidence = evidence }); }
    private static float[] RectValues(Rect value) => new[] { value.x, value.y, value.width, value.height };
    private static bool Release() => (bool)typeof(ExternalContentRuntime).GetMethod("ReleaseUnloadedGameplayArtwork", Members).Invoke(null, null);
    private static Scene EmptyGameplay(Sprite image)
    {
        var scene = SceneManager.CreateScene("SampleScene");
        SceneManager.SetActiveScene(scene);
        var owner = new GameObject("R39隔离旧页图像持有者");
        owner.AddComponent<SpriteRenderer>().sprite = image;
        SceneManager.MoveGameObjectToScene(owner, scene);
        return scene;
    }
    private static IEnumerator Check()
    {
        ExternalContentRuntime.SetMemoryDiagnosticsEnabled(true);
        var pack = ExternalContentRuntime.Pack;
        string hash = pack.ContentHash;
        var definition = pack.Definitions.First(d => d.Enabled && d.ContentType == "facility" && d.ArtworkSlice != null);
        var sliceId = definition.ArtworkSlice.Id;
        var cardCatalog = AssetDatabase.LoadAssetAtPath<CardVisualCatalog>("Assets/YC/Presentation/Content/CardVisualCatalog.asset");
        var boardCatalog = AssetDatabase.LoadAssetAtPath<EnterpriseBoardCatalog>("Assets/YC/Presentation/Enterprise/EnterpriseBoardCatalog.asset");
        Require(cardCatalog != null && boardCatalog != null, "实际Catalog资产缺失");
        var persistent = (Texture2D)typeof(CardVisualCatalog).GetField("cityBoardTexture", Members).GetValue(cardCatalog);
        Require(persistent != null, "原持久贴图缺失");
        int persistentId = persistent.GetInstanceID();
        var crops = (EnterpriseBoardCatalog.ExternalLogoCrop[])typeof(EnterpriseBoardCatalog).GetField("externalLogoCrops", Members).GetValue(boardCatalog);
        var logoKey = crops.First(c => pack.Definitions.Any(d => d.Enabled && d.DefinitionId == c.visualKey)).visualKey;
        var first = ExternalContentRuntime.GetRegisteredArtworkSprite(sliceId);
        var oldTexture = first.texture;
        int width = oldTexture.width, height = oldTexture.height, format = (int)oldTexture.format;
        Rect rect = first.rect;
        var oldLogo = boardCatalog.Logo(logoKey);
        Require(oldLogo != null && oldLogo.texture != null, "实际企业Logo未取得");
        var logoRect = oldLogo.rect;
        int logoWidth = oldLogo.texture.width, logoHeight = oldLogo.texture.height;
        var logoVertices = oldLogo.vertices;
        var before = ExternalContentRuntime.ReadMemoryDiagnostics();
        Passed("正式内容与原Catalog载入", new { hash, definition.DefinitionId, sliceId, logoKey, width, height, format, Rect = RectValues(rect), before });

        // 明确隔离源场景，不注入规则状态；仍经过原RequestLeave/加载器及实际场景卸载。
        var source = EmptyGameplay(first);
        GameLaunchContext.Ensure();
        var inactive = new GameObject("R39隔离门禁检查"); inactive.SetActive(false);
        var controller = inactive.AddComponent<LoadingSceneController>();
        var gate = typeof(LoadingSceneController).GetMethod("TryReleaseGameplayArtworkBeforeActivation", Members);
        Require(!(bool)gate.Invoke(controller, new object[] { "StartScene", true, null }), "无加载任务/活源场景门禁未拒绝");
        Require(!(bool)gate.Invoke(controller, new object[] { "CollectionRoom", true, null }), "非菜单目标门禁未拒绝");
        Require(!(bool)gate.Invoke(controller, new object[] { "StartScene", false, null }), "取消资格门禁未拒绝");
        Require(ExternalContentRuntime.ReadMemoryDiagnostics().ClearGeneration == before.ClearGeneration && oldTexture != null, "活源/拒绝门禁改变资源");
        UnityEngine.Object.Destroy(inactive);
        Passed("活源空任务非菜单及取消资格不清", ExternalContentRuntime.ReadMemoryDiagnostics());

        bool sawSourceUnload = false, sawTargetLoad = false;
        ArtworkMemorySnapshot atUnload = default, atTarget = default;
        bool imageAliveAtUnload = false, destroyedAtTarget = false;
        void OnUnloaded(Scene scene)
        {
            if (scene.handle != source.handle) return;
            sawSourceUnload = true; atUnload = ExternalContentRuntime.ReadMemoryDiagnostics();
            imageAliveAtUnload = oldTexture != null;
        }
        void OnLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "StartScene") return;
            sawTargetLoad = true; atTarget = ExternalContentRuntime.ReadMemoryDiagnostics();
            destroyedAtTarget = oldTexture == null && first == null;
        }
        SceneManager.sceneUnloaded += OnUnloaded; SceneManager.sceneLoaded += OnLoaded;
        GameLaunchContext.ReturnToStartScene("StartScene");
        while (SceneTransitionContext.IsTransitionInProgress) yield return null;
        yield return null;
        SceneManager.sceneUnloaded -= OnUnloaded; SceneManager.sceneLoaded -= OnLoaded;
        Require(sawSourceUnload && imageAliveAtUnload && atUnload.ClearGeneration == before.ClearGeneration, "旧源卸载完成前发生提前释放");
        Require(sawTargetLoad && destroyedAtTarget && atTarget.CachedTextureEntries == 0 && atTarget.CachedSpriteEntries == 0, "目标激活前未释放动态对象");
        Require(atTarget.ClearGeneration == before.ClearGeneration + 1 && atTarget.TextureReleaseRequests > before.TextureReleaseRequests, "释放计数/代际未一致推进");
        Require(ReferenceEquals(pack, ExternalContentRuntime.Pack) && pack.ContentHash == hash, "释放修改冻结内容身份");
        Require(persistent != null && persistent.GetInstanceID() == persistentId, "误销毁序列化持久贴图");
        Passed("原正式退出加载顺序及延迟销毁", new { sawSourceUnload, imageAliveAtUnload, atUnload, sawTargetLoad, destroyedAtTarget, atTarget });
        Require(!Release() && ExternalContentRuntime.ReadMemoryDiagnostics().ClearGeneration == atTarget.ClearGeneration, "空缓存重复释放不幂等");
        Passed("空缓存重复释放及冻结内容保留", new { hash, persistentId, Snapshot = ExternalContentRuntime.ReadMemoryDiagnostics() });

        var reloaded = ExternalContentRuntime.GetRegisteredArtworkSprite(sliceId);
        Require(reloaded != null && reloaded.texture != null && reloaded.rect == rect && reloaded.texture.width == width &&
            reloaded.texture.height == height && (int)reloaded.texture.format == format, "同路径同slice重载改变原参数");
        var viaCatalog = cardCatalog.GetFacility(definition.DefinitionId);
        Require(viaCatalog != null && viaCatalog.texture == reloaded.texture && viaCatalog.rect == rect, "CardVisualCatalog未重新取得原切片");
        var logo = boardCatalog.Logo(logoKey);
        Require(logo != null && logo.texture != null && !ReferenceEquals(logo, oldLogo) && logo.rect == logoRect &&
            logo.texture.width == logoWidth && logo.texture.height == logoHeight && logo.vertices.SequenceEqual(logoVertices), "Logo失效缓存未按原裁切重建");
        yield return null;
        Require(oldLogo == null, "失效二级Sprite未按原Destroy语义释放");
        Passed("同slice尺寸UV及Catalog二级失效重取", new { width, height, Rect = RectValues(rect), logoKey, LogoRect = RectValues(logoRect), Snapshot = ExternalContentRuntime.ReadMemoryDiagnostics() });

        var current = ExternalContentRuntime.ReadMemoryDiagnostics();
        var nextSource = EmptyGameplay(reloaded);
        var menu = SceneManager.GetSceneByName("StartScene"); var removeMenu = SceneManager.UnloadSceneAsync(menu);
        while (!removeMenu.isDone) yield return null;
        GameLaunchContext.ReturnToStartScene("StartScene");
        while (SceneTransitionContext.IsTransitionInProgress) yield return null;
        yield return null;
        var last = ExternalContentRuntime.ReadMemoryDiagnostics();
        Require(!nextSource.isLoaded && reloaded == null && last.CachedTextureEntries == 0 && last.ClearGeneration == current.ClearGeneration + 1,
            "同PID第二次隔离离局未按同一边界清理");
        Require(ReferenceEquals(pack, ExternalContentRuntime.Pack) && pack.ContentHash == hash, "第二次离局改变pack");
        Passed("同PID再次按需重载后离局", last);

        // 将故意失败放在末尾：当前Unity返回null任务，原转换会保持未完成；不注入恢复来继续验收。
        var failureSprite = ExternalContentRuntime.GetRegisteredArtworkSprite(sliceId);
        var failureTexture = failureSprite.texture;
        var failureBefore = ExternalContentRuntime.ReadMemoryDiagnostics();
        var failureSource = EmptyGameplay(failureSprite);
        menu = SceneManager.GetSceneByName("StartScene"); removeMenu = SceneManager.UnloadSceneAsync(menu);
        while (!removeMenu.isDone) yield return null;
        expectedLoadFailure = true;
        GameLaunchContext.ReturnToStartScene(MissingScene);
        while (!sawNullLoadFailure) yield return null;
        yield return null;
        expectedLoadFailure = false;
        var failed = ExternalContentRuntime.ReadMemoryDiagnostics();
        Require(!failureSource.isLoaded && SceneTransitionContext.IsTransitionInProgress, "故意缺失场景未走实测null任务分支");
        Require(failureTexture != null && failureSprite != null && failed.ClearGeneration == failureBefore.ClearGeneration &&
            failed.TextureReleaseRequests == failureBefore.TextureReleaseRequests, "空加载任务误清缓存");
        Passed("原目标加载返回空任务不清缓存且不伪造转换完成", new { failureBefore, failed, sawNullLoadFailure });
    }
}
#endif
