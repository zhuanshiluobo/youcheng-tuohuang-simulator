#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using YC.Infrastructure.Persistence;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        // 显式批处理有限入口；不打开、重建或保存场景/预制体，不代正常游戏验收。
        public static void VerifyR16Integration()
        {
            string Arg(string key) => Environment.GetCommandLineArgs().FirstOrDefault(v => v.StartsWith(key, StringComparison.Ordinal))?.Substring(key.Length);
            string destination = Arg("--qy004-r16-check-output="), source = Arg("--qy004-r16-source-save-root=");
            if (!Directory.Exists(destination) || !Directory.Exists(source)) throw new ArgumentException("缺少明确原生输出/只读输入目录");
            var checks = new List<string>();
            Require(ParsePerformanceGameCount(null, false) == 1 && ParsePerformanceGameCount("2", false) == 2 &&
                ParsePerformanceGameCount("1", true) == 1, "局数未与弱观察解耦");
            bool rejected = false;
            try { ParsePerformanceGameCount("3", false); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "局数上限缺失"); checks.Add("普通默认一局、显式两局不启弱实验、非法局数拒绝");
            var page = new EnterpriseSelectionProjection { GameId = "native-game", RequestId = "native-request", Revision = 16,
                ViewerPlayerId = 2, RequestContract = "native-contract", Mode = EnterpriseSelectionMode.Company };
            bool Match(bool current = true, string game = "native-game", string request = "native-request", int revision = 16,
                int viewer = 2, string contract = "native-contract") => EnterpriseChoicePageMatches(current, page, game, request, revision, viewer, contract);
            Require(Match() && !Match(false) && !Match(game: "other") && !Match(request: "other") && !Match(revision: 15) &&
                !Match(viewer: 3) && !Match(contract: "map-contract"), "当前企业页完整合同门禁错误");
            checks.Add("R15实际程序集企业页当前身份/版本/完整合同门禁");
            page.ReadOnly = true; Require(!Match(), "只读企业页不能选择"); page.ReadOnly = false;
            page.Mode = EnterpriseSelectionMode.BrowseDepartment; Require(!Match(), "浏览企业页不能选择");
            Require(!EnterprisePageBelongsToRequest(null, "native-request") && !EnterprisePageBelongsToRequest(page, ""), "空请求门禁错误");
            checks.Add("只读、浏览及空请求拒绝；不是实际UI点击验收");
            var args = new[] { "--yc-ui-performance=true", "--yc-perf-save-weak=false", "--yc-perf-save-trace=false", "--yc-performance-save-root=" + destination };
            var options = MatchSavePerformanceOptions.Parse(args, destination, UnityEngine.Application.dataPath);
            Require(options.Performance && !options.DisableAutomatic && !options.Weak && !options.Trace, "正式保存开启且诊断关闭的参数合同错误");
            checks.Add("正式自动保存开启、双弱和详细保存诊断关闭");
            // 使用已封存真实小存档作为隔离输入；不恢复对局，不向正式存档写入。
            var input = new MatchSaveStore(source).Read(4);
            string target = Path.Combine(destination, "隔离重复保存");
            Require(!Directory.Exists(target), "隔离目标必须新建");
            var store = new MatchSaveStore(target, true);
            store.SaveAsync(input).GetAwaiter().GetResult();
            store.SaveAsync(input).GetAwaiter().GetResult();
            var restored = new MatchSaveStore(target).Read(3);
            var stats = store.ReadQueueStatistics();
            Require(restored.GameId == input.GameId && restored.ContentHash == input.ContentHash && restored.ArchiveJson == input.ArchiveJson &&
                restored.ContinuationJson == input.ContinuationJson && Directory.GetFiles(target, "*.json").Length == 1,
                "同局重复保存或完整Read校验失败");
            Require(stats.Completed == 2 && stats.Enqueued == 2 && stats.Failed == 0 && stats.Cancelled == 0 && stats.Outstanding == 0,
                "隔离保存队列未完整结束");
            var cache = Read(store, "ownershipCache") as Array;
            Require(cache != null && cache.Length == 6 && cache.GetValue(3) != null, "实际Store短缓存没有发布");
            checks.Add("实际Unity中完整Read、两次真实SaveAsync与完整回读；同局唯一且两次队列结清");
            checks.Add("Stream/SHA256/严格UTF8当前保存链原生执行；缓存非空不冒逐笔命中证明");
            var result = new { Passed = true, Checks = checks, MainMvid = typeof(HotseatUiValidationDriver).Assembly.ManifestModule.ModuleVersionId,
                Unity = UnityEngine.Application.unityVersion, SourceGameId = input.GameId, SourceContentHash = input.ContentHash,
                ArchiveChars = input.ArchiveJson.Length, ContinuationChars = input.ContinuationJson.Length,
                Queue = stats, ColdReadAfterTwoWrites = true, ActualHitPerSaveClaimed = false,
                NormalGameplayClaimed = false, SceneOrPrefabWritten = false };
            using (var writer = new StreamWriter(new FileStream(Path.Combine(destination, "原生有限核验.json"), FileMode.CreateNew)))
                writer.Write(JsonConvert.SerializeObject(result, Formatting.Indented));
        }
    }
}
#endif
