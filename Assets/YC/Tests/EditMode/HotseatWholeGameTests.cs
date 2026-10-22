using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace YC.Tests.EditMode
{
    [Explicit("须从专用入口显式选择三模式完整热座，普通测试不自动跑局。")]
    public sealed class HotseatWholeGameTests
    {
        [Test, Order(1)] public void Core_FormalLocalLaunch_CompletesEightRounds() => Complete("core", 3);
        [Test, Order(2)] public void Enterprise_FormalLocalLaunch_CompletesEightRounds() => Complete("enterprise", 3);
        [Test, Order(3)] public void Infected_FormalLocalLaunch_CompletesEightRounds() => Complete("infected", 4);

        private static void Complete(string mode, int players)
        {
            string root = Environment.GetEnvironmentVariable("YC_HOTSEAT_OUTPUT_ROOT");
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
                Assert.Fail("须经Run-HotseatWholeGameValidation显式提供独立输出目录，防止普通测试自动跑局。");
            string prioritiesRoot = Environment.GetEnvironmentVariable("YC_HOTSEAT_PRIORITY_ROOT");
            string priority = string.IsNullOrWhiteSpace(prioritiesRoot) ? null : Path.Combine(prioritiesRoot, mode + ".json");
            string seedToken = Environment.GetEnvironmentVariable("YC_HOTSEAT_EXPLICIT_SEED");
            int? seed = string.IsNullOrWhiteSpace(seedToken) ? (int?)null : int.Parse(seedToken, CultureInfo.InvariantCulture);
            var type = Assembly.Load("Assembly-CSharp").GetType("YC.Presentation.Validation.HotseatGameValidationRunner", true);
            string json;
            try { json = (string)type.GetMethod("Run").Invoke(null, new object[] { mode, Path.Combine(root, mode), priority, seed }); }
            catch (TargetInvocationException error)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
            var result = JObject.Parse(json);
            Assert.That(result.Value<bool>("Succeeded"), Is.True, json);
            Assert.That(result.Value<bool?>("EffectChainHealthy"), Is.True, json);
            Assert.That(result.Value<int>("FinalRound"), Is.EqualTo(8));
            Assert.That(result.Value<string>("FinalPhase"), Is.EqualTo("FinalScoring"));
            Assert.That(result["CompletedRounds"].ToObject<int[]>(), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.That(result.Value<int>("PlayerCount"), Is.EqualTo(players));
            Assert.That(result.Value<int>("AcceptedCommands"), Is.EqualTo(result.Value<int>("SubmittedCommands")));
            Assert.That(result.Value<bool>("FormalLaunchCompleted"), Is.True);
            Assert.That(result.Value<bool>("OwnPlayerProjectionOnly"), Is.True);
            Assert.That(result.Value<bool>("SmokeStateUsed"), Is.False);
            Assert.That(result.Value<bool>("ExtraResourcesCardsOrFacilitiesInjected"), Is.False);
            Assert.That(result.Value<bool>("UiInputUsed"), Is.False);
            Assert.That(result.Value<string>("GameId"), Is.Not.Empty);
            Assert.That(result.Value<int?>("Seed"), Is.Not.Null);
            if (seed.HasValue) Assert.That(result.Value<int?>("Seed"), Is.EqualTo(seed));
        }
    }
}
