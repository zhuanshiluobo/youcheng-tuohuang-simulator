#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Infrastructure.Persistence;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        // 显式Editor隔离入口；不恢复对局、不输入UI、不打开或保存场景资产。
        public static void VerifyR18Integration()
        {
            string Arg(string key) => Environment.GetCommandLineArgs().FirstOrDefault(v => v.StartsWith(key, StringComparison.Ordinal))?.Substring(key.Length);
            string destination = Arg("--qy004-r16-check-output="), fixtures = Arg("--qy004-r18-fixtures=");
            Require(Directory.Exists(destination) && Directory.Exists(fixtures), "R18隔离输入/输出缺失");
            var checks = new List<object>();
            void Check(string name, Action check)
            {
                File.AppendAllText(Path.Combine(destination, "R18原生检查进度.jsonl"), JsonConvert.SerializeObject(new { Name = name, Begin = true }) + "\n");
                check(); checks.Add(new { Name = name, Passed = true });
                File.AppendAllText(Path.Combine(destination, "R18原生检查进度.jsonl"), JsonConvert.SerializeObject(new { Name = name, Passed = true }) + "\n");
            }
            void Reject(Action action)
            {
                bool rejected = false;
                try { action(); } catch (Exception error)
                {
                    var actual = error is TargetInvocationException invocation ? invocation.InnerException : error;
                    rejected = actual is JsonException || actual is InvalidOperationException;
                    if (!rejected) throw;
                }
                Require(rejected, "错误JSON/Host差异必须拒绝");
            }
            Check("当前完整Read、两次实际保存及R16小合同", VerifyR16Integration);
            Check("R17空目标准备与原Ready/请求/授权边界", () =>
            {
                var action = new PreparedMainActionContinuation { Kind = "city_move" };
                var preparation = new PreparedActionPaymentView { ActionKind = "city_move", CanCancel = true };
                Require(PerformanceNeedsMoveTarget(action, preparation, null), "合法空目标未续接");
                preparation.CanCancel = false;
                Require(PerformanceNeedsMoveTarget(action, preparation, null), "目标选择不能被取消权限替代");
                preparation.IsReady = true; Require(!PerformanceNeedsMoveTarget(action, preparation, null), "已Ready仍走原链");
                preparation.IsReady = false; action.TargetPreselected = true;
                Require(!PerformanceNeedsMoveTarget(action, preparation, null), "不可重做首击");
                action.TargetPreselected = false; preparation.AuthorizationKind = "effect";
                Require(!PerformanceNeedsMoveTarget(action, preparation, null), "不可放松授权");
                preparation.AuthorizationKind = "main_action";
                Require(!PerformanceNeedsMoveTarget(action, preparation, new InteractionRequestProjection()), "必答不走新目标分流");
                action.Kind = preparation.ActionKind = "build";
                Require(!PerformanceNeedsMoveTarget(action, preparation, null), "非移城未Ready仍等待");
                Require(typeof(HotseatUiValidationDriver).GetMethod("CancelPreparedMainAction", Members) != null,
                    "原取消助手未在当前程序集接通");
            });
            Check("当前有界UTF8分块、非BMP和不成对代理的字节及checksum", () =>
            {
                var type = typeof(MatchSaveStore).GetNestedType("BoundedUtf8Writer", BindingFlags.NonPublic);
                var complete = type.GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic);
                var checksum = typeof(MatchSaveStore).GetMethod("HashUtf8Bounded", BindingFlags.Static | BindingFlags.NonPublic);
                var texts = new[] { "汉字\"\\\n", new string('a', 4095) + "\ud83d\ude80尾", "头\ud800尾\udc00", new string('中', 8193) };
                foreach (string text in texts)
                {
                    using var stream = new MemoryStream();
                    using var writer = (TextWriter)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new object[] { stream, true, true }, null);
                    for (int index = 0; index < text.Length; index++) writer.Write(text[index]);
                    var identity = complete.Invoke(writer, null);
                    byte[] expected = new UTF8Encoding(false).GetBytes(text);
                    using var hash = SHA256.Create();
                    string digest = Convert.ToBase64String(hash.ComputeHash(expected));
                    Require(stream.ToArray().SequenceEqual(expected) && (long)Read(identity, "Bytes") == expected.Length &&
                        (string)Read(identity, "Digest") == digest && (string)checksum.Invoke(null, new object[] { text }) == digest,
                        "Unity实际UTF8聚合输出或checksum不等价");
                }
            });
            var local = JsonConvert.DeserializeObject<MatchSaveData>(File.ReadAllText(Path.Combine(fixtures, "隔离小夹具.json")));
            var host = JsonConvert.DeserializeObject<MatchSaveData>(File.ReadAllText(Path.Combine(fixtures, "隔离Host小夹具.json")));
            Check("当前Local投影校验、公开完整读取及Host完整校验", () =>
            {
                local.Validate(); host.Validate();
                var expected = JObject.Parse(local.ArchiveJson);
                Require(JToken.DeepEquals(local.ReadArchiveState(), expected["Snapshot"]["State"]), "公开读被投影替代");
                var parse = typeof(MatchSaveData).GetMethod("ReadValidationArchive", BindingFlags.Static | BindingFlags.NonPublic);
                var full = (JObject)parse.Invoke(null, new object[] { host.ArchiveJson, false });
                Require(JToken.DeepEquals(full, JObject.Parse(host.ArchiveJson)), "Host没有保留完整树");
            });
            Check("被丢弃子树的重复键/错误JSON与Host全文差异仍拒绝", () =>
            {
                string original = local.ArchiveJson;
                local.ArchiveJson = original.TrimEnd().Substring(0, original.TrimEnd().Length - 1) + ",\"IgnoredR18\":{\"x\":1,\"x\":2}}";
                Reject(local.Validate);
                local.ArchiveJson = original + "非法尾部"; Reject(local.Validate); local.ArchiveJson = original;
                string hostOriginal = host.ArchiveJson;
                var changed = JObject.Parse(hostOriginal); changed["R18UnknownWitness"] = true;
                host.ArchiveJson = changed.ToString(Formatting.None); Reject(host.Validate); host.ArchiveJson = hostOriginal;
            });
            Check("同文本Local/Host模式切换与Dispose清引用", () =>
            {
                using var scope = new MatchSaveData.ValidationScope();
                var read = typeof(MatchSaveData.ValidationScope).GetMethod("ReadArchive", BindingFlags.Instance | BindingFlags.NonPublic);
                string text = local.ArchiveJson;
                var projected = (JObject)read.Invoke(scope, new object[] { text, true });
                var full = (JObject)read.Invoke(scope, new object[] { text, false });
                Require(!ReferenceEquals(projected, full) && JToken.DeepEquals(full, JObject.Parse(text)) &&
                    !(bool)Read(scope, "archiveIsLocalProjection"), "同文本切Host复用错误投影");
                var projectedAgain = (JObject)read.Invoke(scope, new object[] { text, true });
                Require(!ReferenceEquals(projectedAgain, full) && JToken.DeepEquals(projected, projectedAgain) &&
                    ReferenceEquals(Read(scope, "archive"), projectedAgain), "切回Local未替换全文引用");
                scope.Dispose();
                Require(Read(scope, "archive") == null && Read(scope, "archiveJson") == null && Read(scope, "trace") == null &&
                    !(bool)Read(scope, "archiveIsLocalProjection"), "Dispose没有清空保存解析引用");
            });
            File.WriteAllText(Path.Combine(destination, "R18原生有限核验.json"), JsonConvert.SerializeObject(new {
                Passed = true, Checks = checks, MainMvid = typeof(HotseatUiValidationDriver).Assembly.ManifestModule.ModuleVersionId,
                Unity = UnityEngine.Application.unityVersion, NormalGameplayClaimed = false,
                R17ActualPointerOrCancelReceiptClaimed = false, Scope = "当前程序集有限合同；真实地图首击/确认/取消须正常Player覆盖"
            }, Formatting.Indented));
        }
    }
}
#endif
