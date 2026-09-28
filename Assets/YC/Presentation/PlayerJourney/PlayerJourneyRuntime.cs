#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.PlayerJourney
{
    /// <summary>决策输入限于可见文字、可达控件和实际高亮；不访问游戏业务程序集。</summary>
    public sealed class PlayerJourneyRuntime : MonoBehaviour
    {
        [Serializable] public sealed class Result
        {
            public string scenario, seed, failureCode, lastVisible, screenshot, entranceLocation, characterScenario;
            public bool success, finalScoringVisible, returnedToStart;
            public int observedRounds, steps, errors;
            public int width, height, deployConfirmations, cancelledDeployments, characterActivations, mapSelections;
            public int cleanupRemovals, requisitionSelections, resourceSelections;
            public string finalScores;
            public List<string> settledAbilities = new List<string>();
            public List<string> completedMainActions = new List<string>();
        }
        [Serializable] private sealed class Trace
        {
            public int step; public float time; public string scene, action, target, visible;
        }
        private Result result;
        private string directory;
        private readonly HashSet<int> rounds = new HashSet<int>();
        private readonly PlayerPointerDriver pointer = new PlayerPointerDriver();
        private float started, lastAction;
        private string lastSignature = string.Empty;
        private string lastTarget = string.Empty;
        private int repeatedTarget;
        private bool usedCharacter;
        private bool testedCancel;
        private bool running;
        private bool enteredGame;
        private int currentRound;
        private string pendingAbility, pendingMainAction;
        private readonly HashSet<string> attemptedActions = new HashSet<string>();
        private readonly HashSet<string> triedBuildCandidates = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (string.IsNullOrEmpty(Argument("--yc-player-journey-output="))) return;
            var host = new GameObject("玩家黑盒测试");
            DontDestroyOnLoad(host); host.AddComponent<PlayerJourneyRuntime>();
        }
        private static string Argument(string prefix)
        {
            foreach (var arg in Environment.GetCommandLineArgs())
                if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg.Substring(prefix.Length);
            return string.Empty;
        }
        private void Start()
        {
            directory = Path.GetFullPath(Argument("--yc-player-journey-output="));
            Directory.CreateDirectory(directory);
            result = new Result { scenario = Argument("--yc-player-journey-scenario="), seed = Argument("--yc-player-journey-seed=") };
            result.characterScenario = Argument("--yc-player-journey-character=");
            UnityEngine.Application.logMessageReceived += OnLog;
            started = lastAction = Time.realtimeSinceStartup;
            Time.timeScale = 1;
            running = true;
            StartCoroutine(Run());
        }
        private void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                result.errors++;
                File.AppendAllText(Path.Combine(directory, "errors.log"), message + "\n" + stack + "\n");
            }
        }
        private static string VisibleText()
        {
            return string.Join(" | ", FindObjectsOfType<Text>().Where(t => t.isActiveAndEnabled &&
                t.canvasRenderer.GetAlpha() > 0.01f && !string.IsNullOrWhiteSpace(t.text) &&
                t.GetComponentsInParent<CanvasGroup>().All(g => g.alpha > 0.01f))
                .Select(t => t.text).Distinct().OrderBy(s => s).ToArray());
        }
        private static string Id(GameObject target)
        {
            var marker = target.GetComponent<PlayerAutomationId>();
            return marker == null ? target.name : marker.Id;
        }
        private IEnumerator Run()
        {
            yield return new WaitForSecondsRealtime(1);
            while (running)
            {
                if (result.errors > 0) { Finish("PROCESS_OR_LOG_FAILURE"); yield break; }
                if (Time.realtimeSinceStartup - started > (result.characterScenario.StartsWith("all-") ? 600 : 300)) { Finish("PROCESS_TIMEOUT"); yield break; }
                var scene = SceneManager.GetActiveScene().name;
                var text = VisibleText(); result.lastVisible = text;
                var roundLabel = FindObjectsOfType<Text>().FirstOrDefault(t => t.name == "Round Number" && t.isActiveAndEnabled);
                if (roundLabel != null && int.TryParse(roundLabel.text, out var displayedRound))
                {
                    rounds.Add(displayedRound);
                    if (currentRound != displayedRound)
                    {
                        currentRound = displayedRound;
                        if (result.characterScenario.StartsWith("all-")) usedCharacter = currentRound > 5;
                    }
                }
                var coveredCount = FindObjectsOfType<Text>().FirstOrDefault(t => t.name == "Covered Count" && t.isActiveAndEnabled);
                if (pendingAbility != null && (text.Contains("请选择一项主要行动") || coveredCount?.text == "0"))
                {
                    if (!result.settledAbilities.Contains(pendingAbility)) result.settledAbilities.Add(pendingAbility);
                    pendingAbility = null;
                }
                var markers = FindObjectsOfType<PlayerAutomationId>().Where(m => PlayerPointerDriver.Reachable(m.gameObject)).ToList();
                var buttons = FindObjectsOfType<Button>().Where(b => PlayerPointerDriver.Reachable(b.gameObject)).ToList();
                var signature = scene + text + string.Join(";", markers.Select(m => m.Id + m.Highlighted));
                if (scene != "StartScene" && scene != "LoadingScene") enteredGame = true;
                if (enteredGame && result.finalScoringVisible && scene == "StartScene")
                { result.returnedToStart = true; Finish(null); yield break; }
                if (text.Contains("胜者") && text.Contains("总分") && text.Contains("游戏结束"))
                { result.finalScoringVisible = true; result.finalScores = text; }

                GameObject target = buttons.FirstOrDefault(b => Label(b).Contains("确认盖放"))?.gameObject;
                var characterTemplate = result.characterScenario == "cannot-tactic" ? "cannot" :
                    result.characterScenario == "elysium-strategy" ? "elysium" : "liskarm";
                if (result.characterScenario.StartsWith("all-"))
                    characterTemplate = new[] { "elysium", "liskarm", "texas", "cannot", "tin-man" }[Mathf.Clamp(currentRound - 1, 0, 4)];
                var hand = markers.Where(m => m.Id.StartsWith("character.hand.")).OrderByDescending(m => m.Id.Contains(characterTemplate)).FirstOrDefault();
                var cover = markers.FirstOrDefault(m => m.Id == "character.cover_slot");
                bool drag = target == null && hand != null && cover != null && text.Contains("拖动手牌");
                if (drag) target = hand.gameObject;
                if (target == null && text.Contains("盖放角色牌"))
                    target = buttons.FirstOrDefault(b => b.name == "Confirm Selection")?.gameObject ??
                        markers.Where(m => m.Id.StartsWith("selection.card."))
                            .OrderByDescending(m => m.Id.Contains(characterTemplate)).FirstOrDefault()?.gameObject;
                if (target == null && result.finalScoringVisible)
                    target = buttons.FirstOrDefault(b => Label(b).Contains("返回开始") || Label(b).Contains("返回主菜单"))?.gameObject;
                if (target == null && text.Contains("可以再调度一个影响力"))
                    target = buttons.FirstOrDefault(b => Label(b).Contains("取消"))?.gameObject;
                if (target == null) target = markers.FirstOrDefault(m => m.Id == "start.local_game" || m.Id == "start.four_player_map")?.gameObject;

                // 模态弹窗的可达按钮先于地图和行动面板。
                if (target == null && text.Contains("使用角色牌"))
                {
                    var ability = result.characterScenario.EndsWith("tactic") ? "action.character.tactic" : "action.character.strategy";
                    target = markers.FirstOrDefault(m => m.Id == ability)?.gameObject;
                    if (target != null) { usedCharacter = true; result.characterActivations++; pendingAbility = characterTemplate + ":" + ability; }
                }
                if (target == null) target = markers.FirstOrDefault(m => m.Id.StartsWith("event.option."))?.gameObject;
                if (target == null && text.Contains("采集阶段：已选择"))
                    target = buttons.FirstOrDefault(b => b.name == "Pay Bank")?.gameObject ??
                        markers.FirstOrDefault(m => m.Id == "action.end")?.gameObject;
                if (target == null && !testedCancel && text.Contains("确认将影响力放置到"))
                {
                    target = buttons.FirstOrDefault(b => Label(b).Contains("取消") || Label(b).Contains("放弃选择"))?.gameObject;
                    if (target != null) { testedCancel = true; result.cancelledDeployments++; }
                }
                if (target == null)
                    target = buttons.FirstOrDefault(b => IsDialog(b) && !Excluded(Label(b)) &&
                        (Label(b).Contains("确认") || Label(b).Contains("确定")))?.gameObject;
                if (target == null)
                    target = buttons.FirstOrDefault(b => IsDialog(b) && !Excluded(Label(b)))?.gameObject;
                if (target == null) target = markers.FirstOrDefault(m => m.Id.StartsWith("selection.card."))?.gameObject;
                if (target == null) target = markers.FirstOrDefault(m => m.Id.StartsWith("build.slot."))?.gameObject;
                if (target == null)
                {
                    var candidate = markers.FirstOrDefault(m => m.Id.StartsWith("build.candidate.") && !triedBuildCandidates.Contains(m.Id));
                    if (candidate != null) { target = candidate.gameObject; triedBuildCandidates.Add(candidate.Id); }
                }
                if (target == null && text.Contains("请选择高亮的入场地点"))
                {
                    var entrance = Argument("--yc-player-journey-entrance=");
                    if (!string.IsNullOrEmpty(entrance))
                    {
                        target = markers.FirstOrDefault(m => m.Highlighted && m.Id == "map.location." + entrance)?.gameObject;
                        if (target == null)
                        {
                            if (Time.realtimeSinceStartup - lastAction > 15) { Finish("ENTRANCE_TARGET_UNAVAILABLE"); yield break; }
                            yield return new WaitForSecondsRealtime(0.2f);
                            continue;
                        }
                    }
                }
                if (target == null) target = markers.FirstOrDefault(m => m.Highlighted && m.Id.StartsWith("map."))?.gameObject;
                if (target == null && !usedCharacter)
                {
                    var abilityButton = result.characterScenario == "cannot-tactic" ? "action.character.tactic" : "action.character.strategy";
                    target = markers.FirstOrDefault(m => m.Id == abilityButton)?.gameObject;
                    if (target != null) { usedCharacter = true; result.characterActivations++; }
                    else target = markers.FirstOrDefault(m => m.Id == "action.character")?.gameObject;
                    if (target == null) target = markers.FirstOrDefault(m => m.Id == "action.tab.1")?.gameObject;
                }
                if (target == null)
                {
                    target = markers.FirstOrDefault(m => m.Id == "action.end")?.gameObject;
                    if (target != null && pendingMainAction != null)
                    {
                        if (!result.completedMainActions.Contains(pendingMainAction)) result.completedMainActions.Add(pendingMainAction);
                        pendingMainAction = null;
                        attemptedActions.Clear();
                    }
                }
                if (target == null && result.characterScenario.StartsWith("all-"))
                {
                    var next = new[] { "action.build", "action.move", "action.dispatch", "action.explore", "action.deploy" }
                        .FirstOrDefault(id => !result.completedMainActions.Contains(id) && !attemptedActions.Contains(id) && markers.Exists(m => m.Id == id));
                    if (next != null) target = markers.First(m => m.Id == next).gameObject;
                }
                if (target == null) target = markers.FirstOrDefault(m => m.Id == "action.deploy")?.gameObject;
                if (target == null && usedCharacter) target = markers.FirstOrDefault(m => m.Id == "action.tab.0")?.gameObject;
                if (target == null) target = buttons.FirstOrDefault(b => Label(b).Contains("采集") && !Excluded(Label(b)))?.gameObject;

                if (target == null || (signature == lastSignature && Id(target) == lastTarget))
                {
                    if (Time.realtimeSinceStartup - lastAction > 15)
                    {
                        File.WriteAllText(Path.Combine(directory, "visible-controls.log"), string.Join("\n", buttons.Select(b => b.name + ": " + Label(b))) +
                            "\n" + string.Join("\n", markers.Select(m => m.Id + " 高亮=" + m.Highlighted)));
                        Finish(target == null ? "PLAYER_DEADLOCK" : "NO_VISIBLE_PROGRESS"); yield break;
                    }
                    yield return new WaitForSecondsRealtime(0.2f); continue;
                }
                repeatedTarget = Id(target) == lastTarget ? repeatedTarget + 1 : 0;
                if (repeatedTarget > 3) { Finish("NO_VISIBLE_PROGRESS"); yield break; }
                lastSignature = signature; lastTarget = Id(target); lastAction = Time.realtimeSinceStartup;
                result.steps++;
                if (new[] { "action.build", "action.move", "action.dispatch", "action.explore", "action.deploy", "action.special" }.Contains(lastTarget))
                { pendingMainAction = lastTarget; attemptedActions.Add(lastTarget); if (lastTarget == "action.build") triedBuildCandidates.Clear(); }
                if (text.Contains("请选择高亮的入场地点") && lastTarget.StartsWith("map.location."))
                    result.entranceLocation = lastTarget.Substring("map.location.".Length);
                if (lastTarget.StartsWith("map.")) result.mapSelections++;
                if (lastTarget.StartsWith("map.") && text.Contains("请选择高亮的影响力移除")) result.cleanupRemovals++;
                var clickedButton = target.GetComponent<Button>();
                if (clickedButton != null && lastTarget.StartsWith("Character Effect Option") && text.Contains("征收"))
                    result.requisitionSelections++;
                if (clickedButton != null && lastTarget.StartsWith("Character Effect Option") && text.Contains("极境策略"))
                    result.resourceSelections++;
                if (clickedButton != null && Label(clickedButton).Contains("确认放置")) result.deployConfirmations++;
                File.AppendAllText(Path.Combine(directory, "actions.jsonl"), JsonUtility.ToJson(new Trace
                { step = result.steps, time = lastAction - started, scene = scene, action = drag ? "drag" : "click", target = lastTarget, visible = text }) + "\n");
                var action = drag ? pointer.Drag(target, cover.gameObject) : pointer.Click(target);
                while (true)
                {
                    bool more;
                    try { more = action.MoveNext(); }
                    catch (Exception ex) { result.lastVisible += "\n" + ex.Message; Finish("INPUT_BLOCKED"); yield break; }
                    if (!more) break;
                    yield return action.Current;
                }
                yield return new WaitForSecondsRealtime(0.5f);
                ScreenCapture.CaptureScreenshot(Path.Combine(directory, "latest.png"));
            }
        }
        private static string Label(Button button) => string.Join(" ", button.GetComponentsInChildren<Text>().Select(t => t.text));
        private static bool Excluded(string label) => string.IsNullOrEmpty(label) || label.Contains("取消") || label.Contains("返回") || label.Contains("收起") || label.Contains("展开") || label.Contains("关闭") || label.Contains("详情");
        private static bool IsDialog(Button button)
        {
            for (var parent = button.transform; parent != null; parent = parent.parent)
                if (parent.name.Contains("Dialog") || parent.name.Contains("Options") || parent.name.Contains("Confirmation") ||
                    parent.name.Contains("Character Effect") || parent.name.Contains("Character Card Effect") || parent.name == "Confirm Selection" ||
                    parent.name.Contains("Special Action Option")) return true;
            return false;
        }
        private void Finish(string failure)
        {
            running = false;
            result.observedRounds = rounds.Count;
            result.width = Screen.width; result.height = Screen.height;
            bool characterVerified = result.characterScenario == "cannot-tactic"
                ? result.requisitionSelections == 1 : result.characterScenario == "elysium-strategy"
                    ? result.resourceSelections == 1 : result.cleanupRemovals >= 1;
            bool requiredActions = result.characterScenario.StartsWith("all-")
                ? result.settledAbilities.Count >= 5 && result.completedMainActions.Count >= 5
                : characterVerified && result.cancelledDeployments >= 1 &&
                    result.characterActivations >= 1 && result.deployConfirmations >= 16;
            result.success = failure == null && result.finalScoringVisible && result.returnedToStart &&
                rounds.Count == 8 && result.errors == 0 && requiredActions;
            result.failureCode = failure ?? (result.success ? string.Empty :
                !requiredActions ? "REQUIRED_ACTION_MISSING" : "FINAL_SCORING_MISSING");
            result.screenshot = Path.Combine(directory, result.success ? "success.png" : "failure.png");
            ScreenCapture.CaptureScreenshot(result.screenshot);
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonUtility.ToJson(result, true));
            StartCoroutine(Exit());
        }
        private IEnumerator Exit()
        {
            yield return new WaitForSecondsRealtime(1);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(result.success ? 0 : 1);
#else
            UnityEngine.Application.Quit(result.success ? 0 : 1);
#endif
        }
        private void OnDestroy() { UnityEngine.Application.logMessageReceived -= OnLog; }
    }
}
#endif
