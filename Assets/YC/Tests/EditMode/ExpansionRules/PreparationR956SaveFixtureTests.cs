using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>仅对用户授权复制到 TestResults 的 R956 存档副本运行，不写入原槽位。</summary>
    public sealed class PreparationR956SaveFixtureTests
    {
        private static string Hash(string path)
        {
            using var input = File.OpenRead(path);
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(input).Select(value => value.ToString("x2")));
        }

        [TestCase(3, "auto-1.json", "13345d2ba92696db4212adcfa0ac64bafd6af06b9db9e9ae6c97c67517b0e3de")]
        [TestCase(4, "auto-2.json", "51ba515fbf2584330b51b6a24a15a7bd2747ab9e98e6f7d3426db0bd13af9d1d")]
        public void ExistingMultiplayerCheckpointKeepsItsR956Digest(int slot, string file, string expectedHash)
        {
            string fixture = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114", "R956-save-fixtures"));
            string source = Path.Combine(fixture, file);
            if (!File.Exists(source)) Assert.Ignore("此环境没有用户授权的隔离存档副本。");
            Assert.That(Hash(source), Is.EqualTo(expectedHash));
            var presentation = AppDomain.CurrentDomain.GetAssemblies().First(assembly => assembly.GetName().Name == "Assembly-CSharp");
            string core = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
            presentation.GetType("YC.Presentation.ExternalContentRuntime", true).GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { core, Path.Combine(fixture, "隔离表现目录") });
            var storeType = presentation.GetType("YC.Infrastructure.Persistence.MatchSaveStore", true);
            var store = Activator.CreateInstance(storeType, new object[] { fixture });
            using var validation = new MatchSaveData.ValidationScope(reuseUnchangedValidation: true);
            var save = (MatchSaveData)storeType.GetMethod("ReadWithValidation").Invoke(store, new object[] { slot, validation });
            var assembled = presentation.GetType("YC.Presentation.GameSessionBootstrapper", true)
                .GetMethod("RestoreWithValidation", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { null, save, validation });
            var session = (GameSession)assembled.GetType().GetField("Session").GetValue(assembled);
            var baseline = validation.ReadRecoveryBaseline(save);
            Assert.That(session.State.SetupRecipe.Preparation, Is.Null);
            Assert.That(RuleStateDigestService.Compute(session.State), Is.EqualTo(baseline.RuleStateDigest));
            var withContract = GameStateCloneService.DeepClone(session.State);
            withContract.RuleConfiguration.OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion;
            Assert.That(RuleStateDigestService.Compute(withContract), Is.Not.EqualTo(baseline.RuleStateDigest));
            var withPlan = GameStateCloneService.DeepClone(session.State);
            withPlan.SetupRecipe.Preparation = new PreparationFlowPlan { ModeId = withPlan.RuleConfiguration.RuleModeId };
            Assert.That(RuleStateDigestService.Compute(withPlan), Is.Not.EqualTo(baseline.RuleStateDigest));
            var withLedger = GameStateCloneService.DeepClone(withPlan);
            withLedger.PrivateCommandLedger = new PrivateCommandLedger { GameId = withLedger.GameId, ContentPackHash = withLedger.ContentPackHash };
            Assert.That(RuleStateDigestService.Compute(withLedger), Is.Not.EqualTo(RuleStateDigestService.Compute(withPlan)));
            Assert.That(Hash(source), Is.EqualTo(expectedHash));
        }
    }
}
