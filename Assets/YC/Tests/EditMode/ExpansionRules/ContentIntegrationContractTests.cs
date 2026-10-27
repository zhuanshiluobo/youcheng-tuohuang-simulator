using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ContentIntegrationContractTests
    {
        private IsolatedTestArtifacts artifacts;
        private string baseline;
        [SetUp] public void Begin()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            baseline = artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "正式完整私有基线");
        }
        [TearDown] public void Finish() { artifacts?.Finish(); }

        [Test] public void FormalPackLoadsAndRegistersAuthorSharedInputsWithoutFixtureRepairs()
        {
            try
            {
                var pack = ExternalContentPack.Load(baseline);
                var details = PlayerDetailDisplayCatalog.Read(pack.Definitions, pack.Presentation);
                artifacts.WriteLabeledText("正式全定义详情声明", JsonConvert.SerializeObject(details));
                artifacts.RecordContent("生产加载器正式输入", baseline, pack.ContentHash);
                var active = ActiveContentCatalogBuilder.Build(pack, new GameRuleConfiguration { MapId = "map-three-players", PlayerCount = 3 });
                string[] availability = { "entrepreneur.T09.cleanup.availability", "entrepreneur.A05.cleanup.availability",
                    "entrepreneur.D09.investment.availability", "entrepreneur.D01.exchange.availability", "entrepreneur.D02.exchange.availability",
                    "entrepreneur.P03.cleanup.availability", "entrepreneur.P04.cleanup.availability" };
                foreach (string id in availability)
                    Assert.That(pack.SettlementHandlers.Count(h => h.HandlerId == id), Is.EqualTo(1), id);
                foreach (string path in new[] { "lua/entrepreneurs/agnes.invoke.lua", "lua/entrepreneurs/agnes.cost.lua", "lua/entrepreneurs/agnes.after.lua" })
                    Assert.That(pack.GetScript(path), Is.Not.Empty, path);
                artifacts.WriteLabeledText("正式共享装配实际身份", JsonConvert.SerializeObject(new { pack.ContentHash, active.ContentPackHash,
                    availability, source = "本case正式完整私有基线；没有夹具补登记或历史提案读取" }));
            }
            catch (Exception error)
            {
                artifacts.WriteLabeledText("正式装配首次诊断", JsonConvert.SerializeObject(new { exception = error.ToString(), baseline }));
                throw;
            }
        }
    }
}
