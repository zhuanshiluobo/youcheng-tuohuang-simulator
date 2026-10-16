using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 隐藏 Player 的后台截屏可能没有可呈现的交换链；用真实网络终局归档在原场景重现，明确作为视觉证据。
    public sealed class NetworkFinalScoringUiTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type Presentation(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YC.Presentation." + name)).First(t => t != null);
        private static object Field(object owner, string name) => owner.GetType().GetField(name, Members).GetValue(owner);

        [UnityTest, Timeout(600000)]
        public IEnumerator SixRealNetworkFinalArchivesRestoreAndRenderInTheirActualScenes()
        {
            string evidence = Environment.GetEnvironmentVariable("YC_ENTERPRISE_NETWORK_EVIDENCE");
            if (string.IsNullOrEmpty(evidence))
                Assert.Ignore("真实网络终局视觉验证需要先完成六组矩阵，并通过 YC_ENTERPRISE_NETWORK_EVIDENCE 指定本次证据目录。");
            string root = Path.GetFullPath(evidence);
            string screenshots = Path.Combine(root, "screenshots"); Directory.CreateDirectory(screenshots);
            foreach (string expansion in new[] { "core", "enterprise" })
            foreach (int players in new[] { 2, 3, 4 })
            {
                string directory = Path.Combine(root, expansion + "-" + players);
                var results = JArray.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")));
                Assert.That((bool)JObject.Parse(File.ReadAllText(Path.Combine(directory, "consistency.json")))["Success"], Is.True);
                Assert.That(results.Count, Is.EqualTo(players));
                Assert.That(results.All(r => (bool)r["Result"]["success"] && (int)r["ExitCode"] == 0), Is.True);
                string json = File.ReadAllText(Path.Combine(directory, "player-1/final-host-archive.json"));
                var config = JObject.Parse(json)["Snapshot"]["State"]["RuleConfiguration"].ToObject<GameRuleConfiguration>();
                var context = Presentation("GameLaunchContext").GetMethod("Ensure").Invoke(null, null);
                var seats = Enumerable.Range(1, players).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "验收玩家" + id }).ToList();
                Presentation("GameLaunchContext").GetMethod("Configure", new[] { typeof(LaunchMode), typeof(int), typeof(string), typeof(IList<PlayerSeat>), typeof(GameRuleConfiguration) })
                    .Invoke(context, new object[] { LaunchMode.Local, 1, "", seats, config });
                yield return SceneManager.LoadSceneAsync(config.MapId == StaticMapDefinitions.ThreePlayerMapId ? "ThreePlayerScene" : "SampleScene");
                yield return null;
                var controller = UnityEngine.Object.FindObjectOfType(Presentation("MobileCityInteractionController"));
                var session = (GameSession)Field(controller, "session");
                var handler = ((List<IGameCommandHandler>)Field(session, "commandHandlers")).OfType<AnswerInteractionCommandHandler>().Single();
                var registry = (EffectRegistry)Field(handler, "registry");
                var recovered = HostRecoveryService.RecoverJson(json, registry);
                Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
                Assert.That(recovered.State.Round, Is.EqualTo(8));
                Assert.That(recovered.State.FinalScoring.IsResolved, Is.True);
                GameStateCloneService.CopyTo(session.State, recovered.State);
                controller.GetType().GetMethod("SynchronizeInteractionFromState", Members).Invoke(controller, null);
                var finalScore = UnityEngine.Object.FindObjectOfType(Presentation("FinalScoreController"));
                finalScore.GetType().GetMethod("RefreshFromState").Invoke(finalScore, new object[] { session.State });
                var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
                capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(1920, 1080) });
                for (int frame = 0; frame < 8; frame++) yield return null;
                Canvas.ForceUpdateCanvases();
                string path = Path.Combine(screenshots, expansion + "-" + players + "-真实网络归档终局.png");
                ScreenCapture.CaptureScreenshot(path);
                for (int frame = 0; frame < 12; frame++) yield return null;
                Assert.That(File.Exists(path), Is.True);
            }
        }
    }
}
