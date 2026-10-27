using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>隔离测试专用：每次进程运行、每个用例、每次同名断点均留独立且不覆盖的证据。</summary>
    internal sealed class IsolatedTestArtifacts
    {
        private sealed class Run
        {
            public readonly string Project, Id, Results, Inputs, CodeHash, ContentHash;
            public Run()
            {
                Project = Path.GetFullPath(Directory.GetCurrentDirectory());
                if (!File.Exists(Path.Combine(Project, "Assets/StreamingAssets/Content/core/pack.json"))) throw new IOException("isolated_test_project_required");
                Id = Environment.GetEnvironmentVariable("YC_ISOLATED_TEST_RUN_ID");
                if (string.IsNullOrEmpty(Id)) Id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                if (!Regex.IsMatch(Id, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,95}\z") || Reserved(Id)) throw new IOException("isolated_test_run_id_invalid");
                string resultsParent = Path.GetFullPath(Path.Combine(Project, "TestResults/IsolatedRuns")), inputsParent = Path.GetFullPath(Path.Combine(Project, "Logs/IsolatedRuns"));
                EnsureParent(resultsParent, Project); EnsureParent(inputsParent, Project);
                Results = Path.Combine(resultsParent, Id); Inputs = Path.Combine(inputsParent, Id);
                if (Directory.Exists(Results) || File.Exists(Results) || Directory.Exists(Inputs) || File.Exists(Inputs)) throw new IOException("isolated_test_run_collision");
                // CreateNew原子占用运行身份；用户指定已用身份时失败，不追加、不复用目录。
                WriteNew(Path.Combine(resultsParent, Id + ".claim"), Id, resultsParent, Project);
                CreateDirectory(Results, Project); CreateDirectory(Inputs, Project);
                var code = Fingerprint(Project, Path.Combine(Project, "Assets/YC"), p => new[] { ".cs", ".asmdef", ".asmref" }.Contains(Path.GetExtension(p)));
                var content = Fingerprint(Project, Path.Combine(Project, "Assets/StreamingAssets/Content/core"), p => !p.EndsWith(".meta", StringComparison.Ordinal));
                CodeHash = TreeHash(code); ContentHash = TreeHash(content);
                WriteNew(Path.Combine(Results, "run.json"), JsonConvert.SerializeObject(new { runId = Id, startedUtc = DateTime.UtcNow, scope = "规则隔离测试，不计正常游戏或页面验收", project = Project,
                    codeFingerprint = CodeHash, installedContentTreeFingerprint = ContentHash, codeFiles = code, contentFiles = content,
                    loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => new[] { "YC.Domain", "YC.Application", "YC.Infrastructure.Lua",
                        "YC.Presentation.Workflows", "Assembly-CSharp", "YC.Tests.EditMode", "YC.Tests.PlayMode" }.Contains(a.GetName().Name))
                        .OrderBy(a => a.GetName().Name, StringComparer.Ordinal).Select(a => new { name = a.GetName().Name, path = a.Location,
                            sha256 = FileHash(a.Location), mvid = a.ManifestModule.ModuleVersionId }).ToArray(),
                    abilityExecutorVersion = AbilityActionExecutor.Version, usageTrackVersion = UsageTracks.Version, globalHostVersion = HostSnapshotDto.CurrentSchemaVersion,
                    executionBoundarySeen = boundaryStarted, resultsRoot = Results, inputsRoot = Inputs }, Formatting.Indented), Results, Project);
            }
        }
        private static Lazy<Run> shared = new Lazy<Run>(() => new Run());
        private static bool boundaryStarted;
        private static UnityEditor.TestTools.TestRunner.Api.TestRunnerApi callbackApi;
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterRunBoundary()
        {
            callbackApi = UnityEngine.ScriptableObject.CreateInstance<UnityEditor.TestTools.TestRunner.Api.TestRunnerApi>();
            callbackApi.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            callbackApi.RegisterCallbacks(new RunBoundary());
        }
        private sealed class RunBoundary : UnityEditor.TestTools.TestRunner.Api.ICallbacks
        {
            public void RunStarted(UnityEditor.TestTools.TestRunner.Api.ITestAdaptor _) { shared = new Lazy<Run>(() => new Run()); boundaryStarted = true; }
            public void RunFinished(UnityEditor.TestTools.TestRunner.Api.ITestResultAdaptor result)
            {
                if (!shared.IsValueCreated) return;
                var run = shared.Value;
                WriteNew(Path.Combine(run.Results, "run-result.json"), JsonConvert.SerializeObject(new { runId = run.Id, result = result.ResultState,
                    passed = result.PassCount, failed = result.FailCount, skipped = result.SkipCount, inconclusive = result.InconclusiveCount,
                    codeFingerprint = run.CodeHash, installedContentTreeFingerprint = run.ContentHash }, Formatting.Indented), run.Results, run.Project);
            }
            public void TestStarted(UnityEditor.TestTools.TestRunner.Api.ITestAdaptor _) { }
            public void TestFinished(UnityEditor.TestTools.TestRunner.Api.ITestResultAdaptor _) { }
        }
        private readonly Run run;
        private readonly Dictionary<string, int> labels = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> workspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int sequence;
        private bool finished;
        public string ResultsDirectory { get; }
        public string InputsDirectory { get; }
        public string RunId => run.Id;
        public string CodeFingerprint => run.CodeHash;
        public string InstalledContentTreeFingerprint => run.ContentHash;
        public string CaseId { get; }
        public string TestId { get; }
        public string TestName { get; }

        private IsolatedTestArtifacts()
        {
            run = shared.Value;
            var test = TestContext.CurrentContext.Test;
            TestId = test.ID; TestName = test.FullName;
            CaseId = Hash(Encoding.UTF8.GetBytes(TestId + "|" + TestName)).Substring(0, 16) + "-" + Guid.NewGuid().ToString("N");
            ResultsDirectory = Path.Combine(run.Results, CaseId); InputsDirectory = Path.Combine(run.Inputs, CaseId);
            CreateDirectory(ResultsDirectory, run.Project); CreateDirectory(InputsDirectory, run.Project);
            WriteText("case-start.json", JsonConvert.SerializeObject(Identity("开始"), Formatting.Indented));
        }
        public static IsolatedTestArtifacts BeginCase() => new IsolatedTestArtifacts();
        private object Identity(string status) => new { runId = run.Id, caseId = CaseId, testId = TestId, testFullName = TestName, codeFingerprint = run.CodeHash,
            installedContentTreeFingerprint = run.ContentHash, status, utc = DateTime.UtcNow };

        public void Finish()
        {
            if (finished) throw new IOException("isolated_test_result_already_written");
            var result = TestContext.CurrentContext.Result;
            WriteText("case-result.json", JsonConvert.SerializeObject(new { identity = Identity(result.Outcome.Status.ToString()), result = result.Outcome.ToString(),
                message = result.Message, stackTrace = result.StackTrace }, Formatting.Indented));
            finished = true;
        }
        public string WriteText(string relative, string text)
        {
            string path = Destination(ResultsDirectory, relative);
            WriteNew(path, text, ResultsDirectory, run.Project); return path;
        }
        public string WriteLabeledText(string label, string text)
        {
            string stem = Allocate(label, out int occurrence);
            string path = WriteText(stem + ".json", text);
            WriteText(stem + ".artifact.json", JsonConvert.SerializeObject(new { identity = Identity("已写入"), label, labelOccurrence = occurrence,
                file = path, sha256 = FileHash(path) }, Formatting.Indented));
            return path;
        }
        private string Allocate(string label, out int occurrence)
        {
            if (label == null) throw new ArgumentNullException(nameof(label));
            occurrence = labels.TryGetValue(label, out int previous) ? previous + 1 : 1; labels[label] = occurrence;
            string safe = Regex.Replace(label, @"[^\p{L}\p{N}_.-]", "_"); if (safe.Length > 60) safe = safe.Substring(0, 60);
            return (++sequence).ToString("D4", CultureInfo.InvariantCulture) + "-" + safe + "-" + Hash(Encoding.UTF8.GetBytes(label)).Substring(0, 10);
        }
        public string CopyContent(string source, string label, IDictionary<string, string> replacements = null, IDictionary<string, byte[]> binaryReplacements = null)
        {
            source = Path.GetFullPath(source);
            string installed = Path.GetFullPath(Path.Combine(run.Project, "Assets/StreamingAssets/Content/core"));
            if (!string.Equals(source, installed, StringComparison.OrdinalIgnoreCase) && !workspaces.Contains(source)) throw new IOException("isolated_test_input_source_not_owned");
            VerifyAncestors(source, run.Project);
            string stem = Allocate(label, out int occurrence), root = Destination(InputsDirectory, stem);
            CreateDirectory(root, run.Project);
            var normalized = (replacements ?? new Dictionary<string, string>()).Select(p => new KeyValuePair<string, string>(p.Key.Replace('\\', '/'), p.Value)).ToList();
            if (normalized.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Count) throw new IOException("isolated_test_input_patch_collision");
            var patches = normalized.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var binaries = (binaryReplacements ?? new Dictionary<string, byte[]>()).ToDictionary(p => p.Key.Replace('\\', '/'), p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (binaries.Keys.Any(patches.ContainsKey)) throw new IOException("isolated_test_input_patch_collision");
            foreach (string relative in patches.Keys) Destination(root, relative);
            foreach (string relative in binaries.Keys) Destination(root, relative);
            foreach (string file in SafeFiles(source).Where(p => !p.EndsWith(".meta", StringComparison.Ordinal)))
            {
                string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                string target = Destination(root, relative); EnsureParent(Path.GetDirectoryName(target), run.Project);
                if (binaries.TryGetValue(relative, out byte[] bytes)) WriteNewBytes(target, bytes, root, run.Project);
                else if (patches.TryGetValue(relative, out string text)) WriteNew(target, text, root, run.Project);
                else { VerifyAncestors(target, run.Project); File.Copy(file, target, false); }
            }
            foreach (var patch in patches.Where(p => !File.Exists(Destination(root, p.Key)))) WriteNew(Destination(root, patch.Key), patch.Value, root, run.Project);
            foreach (var patch in binaries.Where(p => !File.Exists(Destination(root, p.Key)))) WriteNewBytes(Destination(root, patch.Key), patch.Value, root, run.Project);
            var files = Fingerprint(root, root, _ => true);
            WriteText(stem + ".input.json", JsonConvert.SerializeObject(new { identity = Identity("输入已封存"), label, labelOccurrence = occurrence, inputRoot = root,
                sourceRoot = source, inputTreeFingerprint = TreeHash(files), files }, Formatting.Indented));
            workspaces.Add(root); return root;
        }
        public void RecordContent(string label, string root, string contentHash)
        {
            if (!workspaces.Contains(Path.GetFullPath(root))) throw new IOException("isolated_test_input_source_not_owned");
            WriteLabeledText(label, JsonConvert.SerializeObject(new { identity = Identity("生产装载器已读取"), inputRoot = root, contentPackHash = contentHash }, Formatting.Indented));
        }
        public HostRecoveryResult Recover(GameState state, string label, EffectRegistry registry)
        {
            string json = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            string stem = Allocate(label, out int occurrence), path = WriteText(stem + ".archive.json", json);
            var inputDigest = RuleStateDigestService.Compute(state);
            HostRecoveryResult result = null; string error = "";
            try { result = HostRecoveryService.RecoverJson(json, registry); return result; }
            catch (Exception exception) { error = exception.ToString(); throw; }
            finally
            {
                string restoredDigest = result?.Succeeded == true ? RuleStateDigestService.Compute(result.State) : "";
                string inputStatePath = "", restoredStatePath = "";
                if (result?.Succeeded == true && inputDigest != restoredDigest)
                {
                    // 新摘要差异保留两份实际状态供逐字段核定，不借历史差异替当前输入作解释。
                    inputStatePath = WriteText(stem + ".input-state.json", JsonConvert.SerializeObject(state));
                    restoredStatePath = WriteText(stem + ".restored-state.json", JsonConvert.SerializeObject(result.State));
                }
                WriteText(stem + ".recovery.json", JsonConvert.SerializeObject(new { identity = Identity("恢复检查"), label, labelOccurrence = occurrence,
                    archivePath = path, archiveSha256 = FileHash(path), contentPackHash = state.ContentPackHash, gameId = state.GameId,
                    recoverySucceeded = result?.Succeeded == true, faultCode = result?.FaultCode, diagnostic = result?.Diagnostic, exception = error,
                    inputStateDigest = inputDigest, restoredStateDigest = restoredDigest, stateDigestEqual = inputDigest == restoredDigest,
                    inputStatePath, restoredStatePath }, Formatting.Indented));
            }
        }
        internal static string Destination(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p == ".." || p == "." || p.Length == 0 || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal) || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Reserved(p))) throw new IOException("isolated_test_path_invalid");
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("isolated_test_path_escape");
            return path;
        }
        private static bool Reserved(string name) => Regex.IsMatch(name, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static void CreateDirectory(string path, string project)
        {
            VerifyAncestors(path, project);
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("isolated_test_directory_collision:" + path);
            EnsureParent(Path.GetDirectoryName(path), project); Directory.CreateDirectory(path);
        }
        private static void EnsureParent(string path, string project) { VerifyAncestors(path, project); Directory.CreateDirectory(path); VerifyAncestors(path, project); }
        private static void VerifyAncestors(string path, string project)
        {
            path = Path.GetFullPath(path); project = Path.GetFullPath(project);
            if (!string.Equals(path, project, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("isolated_test_path_outside_project");
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("isolated_test_reparse_point");
                if (string.Equals(current, project, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        private static void WriteNew(string path, string text, string root, string project)
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("isolated_test_path_escape");
            EnsureParent(Path.GetDirectoryName(path), project); VerifyAncestors(path, project);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
        }
        private static void WriteNewBytes(string path, byte[] bytes, string root, string project)
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("isolated_test_path_escape");
            EnsureParent(Path.GetDirectoryName(path), project); VerifyAncestors(path, project);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length);
        }
        private static IEnumerable<string> SafeFiles(string root)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(root).OrderBy(p => p, StringComparer.Ordinal))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("isolated_test_reparse_point");
                if (Directory.Exists(path)) { foreach (string file in SafeFiles(path)) yield return file; }
                else yield return path;
            }
        }
        private static SortedDictionary<string, string> Fingerprint(string relativeTo, string root, Func<string, bool> include) => new SortedDictionary<string, string>(
            SafeFiles(root).Where(include).ToDictionary(p => Path.GetRelativePath(relativeTo, p).Replace('\\', '/'), FileHash), StringComparer.Ordinal);
        private static string TreeHash(SortedDictionary<string, string> files) => Hash(Encoding.UTF8.GetBytes(string.Concat(files.Select(p => p.Key + ":" + p.Value + "\n"))));
        public static string FileHash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return Hex(sha.ComputeHash(stream)); }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
