#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Infrastructure.Persistence;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private const string R22NativePending = "QY004.R22.native.pending";
        private static string R22NativeArg(string key) => Environment.GetCommandLineArgs().FirstOrDefault(v => v.StartsWith(key, StringComparison.Ordinal))?.Substring(key.Length);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void PrepareR22NativeRestore()
        {
            if (!UnityEditor.SessionState.GetBool(R22NativePending, false)) return;
            var data = new MatchSaveStore(R22NativeArg("--qy004-r22-source-save=")).Read(3);
            GameLaunchContext.Ensure().ConfigureSavedLocal(data);
            File.WriteAllText(Path.Combine(R22NativeArg("--qy004-r22-output="), "隔离公开恢复入口.json"), JsonConvert.SerializeObject(new {
                data.GameId, data.ContentHash, OriginalConfigureSavedLocal = true, RegistryOrStateFieldsInjected = false,
                IsolatedEditorPlayMode = true, NormalGameplayClaimed = false
            }, Formatting.Indented));
        }
        [UnityEditor.InitializeOnLoadMethod]
        private static void ResumeR22Native()
        {
            if (!UnityEditor.SessionState.GetBool(R22NativePending, false)) return;
            void Finish()
            {
                if (!UnityEditor.SessionState.GetBool(R22NativePending, false)) return;
                UnityEditor.SessionState.SetBool(R22NativePending, false);
                try { VerifyR22RegisteredHost(); UnityEditor.EditorApplication.Exit(0); }
                catch (Exception error) { Debug.LogException(error); UnityEditor.EditorApplication.Exit(1); }
            }
            if (UnityEngine.Application.isPlaying) UnityEditor.EditorApplication.delayCall += Finish;
            else UnityEditor.EditorApplication.playModeStateChanged += state => {
                if (state == UnityEditor.PlayModeStateChange.EnteredPlayMode) UnityEditor.EditorApplication.delayCall += Finish;
            };
        }
        private static int R22RegisteredGetterCalls(string iterator)
        {
            var owner = typeof(HotseatUiValidationDriver).GetMethod(iterator, Members)
                .GetCustomAttribute<IteratorStateMachineAttribute>().StateMachineType.GetMethod("MoveNext", Members);
            var codes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(c => unchecked((ushort)c.Value));
            var il = owner.GetMethodBody().GetILAsByteArray(); int calls = 0;
            for (int i = 0; i < il.Length;)
            {
                ushort code = il[i++]; if (code == 0xfe) code = (ushort)(0xfe00 | il[i++]);
                var op = codes[code];
                if (op.OperandType == OperandType.InlineMethod)
                {
                    var method = owner.Module.ResolveMethod(BitConverter.ToInt32(il, i));
                    if (method.DeclaringType == typeof(HotseatUiValidationDriver) && method.Name == nameof(CurrentRegisteredBuildOptions)) calls++;
                    Require(!(op == OpCodes.Newobj && method.DeclaringType == typeof(BuildFacilityOptionQueryService)), "迭代器仍新建默认建设服务");
                }
                int count = op.OperandType == OperandType.InlineSwitch ? 4 + 4 * BitConverter.ToInt32(il, i) :
                    op.OperandType == OperandType.InlineNone ? 0 :
                    op.OperandType == OperandType.ShortInlineBrTarget || op.OperandType == OperandType.ShortInlineI || op.OperandType == OperandType.ShortInlineVar ? 1 :
                    op.OperandType == OperandType.InlineVar ? 2 :
                    op.OperandType == OperandType.InlineI8 || op.OperandType == OperandType.InlineR ? 8 : 4;
                i += count;
            }
            return calls;
        }
        // 只用于本轮两处接线的Editor隔离核查；不重跑资源/Store旧矩阵。
        public static void VerifyR22Integration()
        {
            string Arg(string key) => Environment.GetCommandLineArgs().FirstOrDefault(v => v.StartsWith(key, StringComparison.Ordinal))?.Substring(key.Length);
            string destination = Arg("--qy004-r22-output="), sourceSave = Arg("--qy004-r22-source-save=");
            Require(Directory.Exists(destination) && Directory.Exists(sourceSave), "R22明确隔离输入输出缺失");
            var checks = new List<string>();
            void Check(string name, Action action)
            {
                File.AppendAllText(Path.Combine(destination, "原生检查进度.jsonl"), JsonConvert.SerializeObject(new { Name = name, Begin = true }) + "\n");
                action(); checks.Add(name);
                File.AppendAllText(Path.Combine(destination, "原生检查进度.jsonl"), JsonConvert.SerializeObject(new { Name = name, Passed = true }) + "\n");
            }
            var probeObject = new GameObject("R22隔离脉冲出口检查");
            var driver = probeObject.AddComponent<HotseatUiValidationDriver>();
            driver.performanceProcess = System.Diagnostics.Process.GetCurrentProcess(); driver.performanceRowLimit = 10;
            string pulsePath = Path.Combine(destination, "实际Pulse出口原JSON.jsonl");
            driver.performanceLog = new StreamWriter(new FileStream(pulsePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite));
            Check("实际PerformanceMemoryPulse序列化出口开关", () => {
                driver.performanceMemoryDetail = false; driver.PerformanceMemoryPulse("memory.pulse.off");
                driver.performanceMemoryDetail = true; driver.PerformanceMemoryPulse("memory.pulse.on");
                driver.performanceMemoryDetail = false; driver.PerformanceMemoryPulse("memory.pulse.off_again");
                driver.performanceLog.Dispose(); driver.performanceLog = null;
                var written = File.ReadAllLines(pulsePath).Select(JObject.Parse).ToArray();
                Require(written.Length == 3 && written[0]["Detail"]["Attribution"].Type == JTokenType.Null &&
                    written[2]["Detail"]["Attribution"].Type == JTokenType.Null, "关闭时出口仍记录归属");
                var attribution = written[1]["Detail"]["Attribution"] as JObject;
                Require(attribution != null && new[] { "UnityUnused", "GraphicsDriver", "Artwork", "Icons", "SaveSize" }.All(k => attribution.Property(k) != null), "真实Pulse JSON缺字段");
                Require(!driver.performanceSnapshotRequested && !driver.performanceLifetimeMode && driver.performanceRows == 3, "脉冲检查意外依赖快照/弱观察");
                // 不断言能力正值；没有Store时SaveSize允许null，缓存默认关闭快照也必须真实序列化。
            });
            driver.performanceLog?.Dispose(); driver.performanceLog = null;
            Check("三个实际编译迭代器均调用现役服务getter", () => {
                foreach (string name in new[] { "PerformanceStep", "EffectBuildChoice", "SalesAction" })
                    Require(R22RegisteredGetterCalls(name) == 1, "编译接线遗漏或重复：" + name);
            });
            Check("宿主尚未装配时拒绝默认或旧服务", () => {
                bool rejected = false;
                try { driver.CurrentRegisteredBuildOptions(); } catch (InvalidOperationException error) { rejected = error.Message.StartsWith("驱动建设查询缺口："); }
                Require(rejected, "没有宿主仍返回默认服务");
            });
            File.WriteAllText(Path.Combine(destination, "前三项针对性核验.json"), JsonConvert.SerializeObject(checks, Formatting.Indented));
            // 原Mobile的Awake含播放生命周期；仅此显式隔离入口切换Editor Play Mode。
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            UnityEditor.SessionState.SetBool(R22NativePending, true);
            ResumeR22Native();
            UnityEditor.EditorApplication.EnterPlaymode();
        }
        private static void VerifyR22RegisteredHost()
        {
            string destination = R22NativeArg("--qy004-r22-output=");
            var checks = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(Path.Combine(destination, "前三项针对性核验.json")));
            void Check(string name, Action action)
            {
                File.AppendAllText(Path.Combine(destination, "原生检查进度.jsonl"), JsonConvert.SerializeObject(new { Name = name, Begin = true }) + "\n");
                action(); checks.Add(name);
                File.AppendAllText(Path.Combine(destination, "原生检查进度.jsonl"), JsonConvert.SerializeObject(new { Name = name, Passed = true }) + "\n");
            }
            var data = GameLaunchContext.Instance.RestoreData;
            var active = UnityEngine.Object.FindObjectOfType<MobileCityInteractionController>();
            Require(active != null, "实际场景正式Mobile缺失");
            // 正式Mobile已自然Awake/BuildSession；只建立验收器自己的观察引用。
            var probeObject = new GameObject("R22隔离正式建设服务观察器"); var driver = probeObject.AddComponent<HotseatUiValidationDriver>();
            driver.mobile = active; driver.session = (GameSession)Read(active, "session"); driver.registry = (EffectRegistry)Read(active, "sessionEffectRegistry");
            var expected = (BuildFacilityOptionQueryService)Read(active, "buildFacilityOptions");
            Check("当前正式Mobile服务和registry严格同一身份", () => {
                Require(ReferenceEquals(driver.CurrentRegisteredBuildOptions(), expected) &&
                    ReferenceEquals(Read(expected, "candidates"), driver.registry.FacilityBuildCandidates), "读取了另一个候选服务");
                bool enabled = active.enabled; active.enabled = false;
                bool rejected = false;
                try { driver.CurrentRegisteredBuildOptions(); } catch (InvalidOperationException error) { rejected = error.Message.StartsWith("驱动建设查询缺口："); }
                finally { active.enabled = enabled; }
                Require(rejected && ReferenceEquals(driver.CurrentRegisteredBuildOptions(), expected), "失活宿主仍返回服务或重新装配了替代服务");
            });
            Check("冻结真实状态有无覆盖层的合法候选与正式服务一致", () => {
                var state = driver.session.State; string before = RuleStateDigestService.Compute(state); var observations = new List<object>();
                bool hasLayer = false, noLayer = false;
                foreach (var player in state.Players)
                {
                    bool layered = FacilityLayerQuery.ResolveCity(state, player.PlayerId).Any(c => c.CoveredItemIds.Count > 0);
                    hasLayer |= layered; noLayer |= !layered;
                    var actual = driver.CurrentRegisteredBuildOptions().Query(state, player.PlayerId);
                    var formal = expected.Query(state, player.PlayerId);
                    Require(JToken.DeepEquals(JToken.FromObject(actual), JToken.FromObject(formal)), "与正式候选/付款/槽位不一致");
                    observations.Add(new { player.PlayerId, HasCoveredLayer = layered, Options = actual,
                        Legal = actual.Where(o => BuildFacilitySelectionController.CanPrepareOption(o) && o.PaymentOptions.Any(p => p.IsAvailable) && o.SlotOptions.Any(s => s.IsLegal)).Select(o => o.FacilityId).ToArray() });
                }
                Require(hasLayer && noLayer, "冻结输入未包含有/无覆盖层两种本人查询");
                Require(before == RuleStateDigestService.Compute(state), "只读候选查询改变了原状态");
                File.WriteAllText(Path.Combine(destination, "实际正式候选两类输入.json"), JsonConvert.SerializeObject(new { data.GameId, data.ContentHash,
                    StateDigestUnchanged = true, Observations = observations, IsolatedRestoration = true, NormalUiAcceptanceClaimed = false }, Formatting.Indented));
            });
            File.WriteAllText(Path.Combine(destination, "R22原生有限核验.json"), JsonConvert.SerializeObject(new {
                Passed = true, Checks = checks, MainMvid = typeof(HotseatUiValidationDriver).Assembly.ManifestModule.ModuleVersionId,
                Unity = UnityEngine.Application.unityVersion, ActualSerializedPulseRead = true, NormalGameplayClaimed = false,
                OldEightGroupsRerun = false, SceneOrPrefabSaved = false, RegistryInjected = false
            }, Formatting.Indented));
        }
    }
}
#endif
