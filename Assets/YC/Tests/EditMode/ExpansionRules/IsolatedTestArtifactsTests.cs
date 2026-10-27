using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>只验证测试输出的防覆写及路径边界，不是玩法验收。</summary>
    public sealed class IsolatedTestArtifactsTests
    {
        private IsolatedTestArtifacts artifacts;
        [SetUp] public void Begin() => artifacts = IsolatedTestArtifacts.BeginCase();
        [TearDown] public void Finish() => artifacts?.Finish();
        [Test] public void DuplicateLabelsKeepBothFilesAndAnExactCollisionFailsWithoutChangingTheFirst()
        {
            string first = artifacts.WriteLabeledText("重复标签", "第一次现场");
            string sha = IsolatedTestArtifacts.FileHash(first);
            string second = artifacts.WriteLabeledText("重复标签", "第二次现场");
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(IsolatedTestArtifacts.FileHash(first), Is.EqualTo(sha));
            Assert.That(File.ReadAllText(second), Is.EqualTo("第二次现场"));
            Assert.Throws<IOException>(() => artifacts.WriteText(Path.GetFileName(first), "禁止覆盖"));
            Assert.That(IsolatedTestArtifacts.FileHash(first), Is.EqualTo(sha));
        }
        [Test] public void EscapingRootedDeviceAndAlternateStreamPathsAreRejectedBeforeWriting()
        {
            int before = Directory.GetFiles(artifacts.ResultsDirectory, "*", SearchOption.AllDirectories).Length;
            foreach (string path in new[] { "../outside.json", "a/../../outside.json", "G:/NoMadCity/outside.json", "file.json:stream", "CON", "trailing. ", "a//b" })
                Assert.Throws<IOException>(() => artifacts.WriteText(path, "禁止写入"), path);
            Assert.That(Directory.GetFiles(artifacts.ResultsDirectory, "*", SearchOption.AllDirectories).Length, Is.EqualTo(before));
        }
        [Test] public void AContentVariantKeepsTheSourceSnapshotAndRejectsUnownedInputs()
        {
            string source = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            string originalHash = IsolatedTestArtifacts.FileHash(Path.Combine(source, "pack.json"));
            string first = artifacts.CopyContent(source, "独立输入");
            string second = artifacts.CopyContent(first, "独立输入", new Dictionary<string, string> { ["pack.json"] = "输出机制测试的独立变体，不送入内容装载器" });
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(IsolatedTestArtifacts.FileHash(Path.Combine(first, "pack.json")), Is.EqualTo(originalHash));
            Assert.That(IsolatedTestArtifacts.FileHash(Path.Combine(source, "pack.json")), Is.EqualTo(originalHash));
            Assert.That(File.ReadAllText(Path.Combine(second, "pack.json")), Does.StartWith("输出机制测试"));
            Assert.Throws<IOException>(() => artifacts.CopyContent(Directory.GetCurrentDirectory(), "禁止项目根输入"));
        }
    }
}
