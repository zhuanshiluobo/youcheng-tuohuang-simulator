"""在独立空工程中比较同步和延后一帧的 Unity 退出回调；不读取或改写游戏资产。"""
import argparse
import json
import os
import subprocess
import time
from pathlib import Path


project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
root = args.output.resolve()
if root == allowed or not root.is_relative_to(allowed) or root.exists():
    raise ValueError('测试工程必须是 TestResults 内尚不存在的明确子目录。')
unity = Path('D:/2022.3.62f1c1/Editor/Unity.exe')
if not unity.is_file():
    raise FileNotFoundError('没有找到项目使用的 Unity 编辑器。')
fixture = root / '空测试工程'
for path in (fixture / 'Assets/Editor', fixture / 'Packages', fixture / 'ProjectSettings', root / '隔离临时目录'):
    path.mkdir(parents=True)
(fixture / 'Packages/manifest.json').write_text('{"dependencies":{}}', encoding='utf-8')
(fixture / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.62f1\n', encoding='utf-8')
(fixture / 'Assets/Editor/ProbeBuilder.cs').write_text(r'''
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
public static class ProbeBuilder {
    public static void Build() {
        try {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(scene, "Assets/Probe.unity")) throw new Exception("fixture_scene_save_failed");
            PlayerSettings.companyName = "DIYFixture"; PlayerSettings.productName = "QuitProbe";
            string output = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "probe-build", "QuitProbe.exe"));
            var report = BuildPipeline.BuildPlayer(new[] {"Assets/Probe.unity"}, output,
                BuildTarget.StandaloneWindows64, BuildOptions.Development);
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 2);
        } catch (Exception e) { UnityEngine.Debug.LogException(e); EditorApplication.Exit(2); }
    }
}
''', encoding='utf-8')
(fixture / 'Assets/QuitProbe.cs').write_text(r'''
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
public sealed class QuitProbe : MonoBehaviour {
    private bool allowed, deferred;
    private string events;
    [RuntimeInitializeOnLoadMethod] static void Init() {
        DontDestroyOnLoad(new GameObject("QuitProbe").AddComponent<QuitProbe>());
    }
    void Start() {
        var args = Environment.GetCommandLineArgs();
        events = args.First(x => x.StartsWith("--probe-events=", StringComparison.Ordinal)).Substring("--probe-events=".Length);
        deferred = args.Contains("--probe-defer");
        Application.wantsToQuit += WantsToQuit;
        StartCoroutine(Run());
    }
    void Log(string value) { File.AppendAllText(events, value + "\n"); }
    IEnumerator Run() { yield return null; Log("outer-request"); Application.Quit(); Log("outer-returned"); }
    bool WantsToQuit() {
        Log("callback-allowed=" + allowed);
        if (allowed) return true;
        Leave();
        Log("callback-return-false");
        return false;
    }
    async void Leave() {
        if (deferred) await Task.Yield(); else await Task.CompletedTask;
        allowed = true;
        Log("nested-request");
        Application.Quit();
        Log("nested-returned");
    }
}
''', encoding='utf-8')
environment = os.environ.copy()
environment.update({'TEMP': str(root / '隔离临时目录'), 'TMP': str(root / '隔离临时目录')})
flags = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
build_args = [str(unity), '-batchmode', '-nographics', '-projectPath', str(fixture),
              '-executeMethod', 'ProbeBuilder.Build', '-logFile', str(root / 'unity-build.log')]
build = subprocess.Popen(build_args, cwd=fixture, env=environment, creationflags=flags)
(root / '构建启动.json').write_text(json.dumps({'pid': build.pid, 'arguments': build_args, 'isolatedProject': str(fixture)},
                                                ensure_ascii=False, indent=2), encoding='utf-8')
print('独立空工程构建进程：', build.pid, flush=True)
while build.poll() is None:
    time.sleep(2)
player = root / 'probe-build/QuitProbe.exe'
if build.returncode != 0 or not player.is_file():
    raise SystemExit('独立空工程构建失败，保留日志与原件。')
results = []
for case in ('同步完成', '延后完成'):
    events = root / (case + '-退出事件.txt')
    arguments = [str(player), '-batchmode', '-nographics', '-logFile', str(root / (case + '-Player.log')),
                 '--probe-events=' + str(events)]
    if case == '延后完成':
        arguments.append('--probe-defer')
    process = subprocess.Popen(arguments, cwd=player.parent, env=environment, creationflags=flags)
    try:
        process.wait(timeout=30)
        timed_out = False
    except subprocess.TimeoutExpired:
        timed_out = True
        process.terminate()  # 只结束本脚本创建的空工程探针，保留其事件和日志。
        process.wait(timeout=10)
    rows = events.read_text(encoding='utf-8').splitlines() if events.exists() else []
    results.append({'case': case, 'pid': process.pid, 'exitCode': process.returncode, 'timeout': timed_out,
                    'terminatedOwnedProbe': timed_out, 'events': rows})
    print(case, '超时=', timed_out, '事件=', rows, flush=True)
(root / '退出回调探针结果.json').write_text(json.dumps({'results': results, 'scope': '仅独立空 Unity 工程的回调行为，不代表游戏原因已确认。'},
                                                       ensure_ascii=False, indent=2), encoding='utf-8')
