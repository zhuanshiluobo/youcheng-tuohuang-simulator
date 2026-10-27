using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ContentIntegrationContractTests
    {
        // 记录生产加载器实际装配身份，不读取历史登记提案或在夹具中修复manifest。
        [Test] public void R2FormalLoaderRecordsActualRawAndAssembledScriptIdentities()
        {
            var pack = ExternalContentPack.Load(baseline);
            artifacts.RecordContent("R2正式生产加载器原样输入", baseline, pack.ContentHash);
            var scripts = pack.Scripts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new {
                path = p.Key, rawSha256 = IsolatedTestArtifacts.FileHash(Path.Combine(baseline, p.Key)),
                rawBytes = new FileInfo(Path.Combine(baseline, p.Key)).Length,
                assembledSha256 = LuaContentHasher.ComputeSha256(p.Value), assembledBytes = Encoding.UTF8.GetByteCount(p.Value)
            }).ToArray();
            Assert.That(scripts.Length, Is.GreaterThan(0));
            artifacts.WriteLabeledText("R2正式原始及生产装配脚本身份", JsonConvert.SerializeObject(new {
                pack.ContentHash, pack.PresentationContentHash, manifest = pack.Manifest,
                settlementHandlers = pack.SettlementHandlers, scripts,
                definitions = pack.Definitions.Select(d => new { d.DefinitionId, d.Version, d.AbilityHandlers, d.AbilitySources }).ToArray(),
                scope = "安装包完整私有基线，生产ExternalContentPack.Load；不启用正常企业家模式或修改G0"
            }, Formatting.Indented));
        }
    }
}
