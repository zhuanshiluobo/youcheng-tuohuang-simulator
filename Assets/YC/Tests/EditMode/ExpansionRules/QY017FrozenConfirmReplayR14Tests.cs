using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只用于获授权的R12真实前Host/原命令同输入对照；不是原按钮、正常菜单或多局验收。
    public sealed class QY017FrozenConfirmReplayR14Tests
    {
        [TestCase("a08")] [TestCase("face")]
        public void R12OriginalHostAndCommandKeepCompleteResultAcrossThreeReplays(string scenario)
        {
            var index=JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),
                "prompt/企业家内容实施规划-20261019/实施记录/QY-017-VALIDATION-PERFORMANCE-R14/受控复放现场.json")))[scenario];
            var record=JObject.Parse(File.ReadAllText((string)index["recordPath"]));
            string hostJson=File.ReadAllText((string)index["beforeHostPath"]);
            var raw=JObject.Parse(hostJson)["Snapshot"]["State"];
            var command=record["commands"][0].ToObject<GameCommand>();
            var setup=Stopwatch.StartNew();
            using var fixture=new AgnesContentTestFixture();
            var rows=new List<object>();
            try
            {
                EffectRegistry registry;
                if(scenario=="face")
                {
                    var config=raw["RuleConfiguration"].ToObject<GameRuleConfiguration>();
                    var catalog=ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(AgnesContentTestFixture.Installed),config);
                    Assert.That(catalog.ContentPackHash,Is.EqualTo((string)raw["ContentPackHash"]));
                    registry=new EffectRegistry();catalog.Register(registry);
                }
                else {fixture.Formal();registry=fixture.Registry;}
                // 与008已验证的R12正式恢复适配相同：不用fixture.State充当前命令前态。
                var query=new MapQueryService(StaticMapDefinitions.Resolve((string)raw["MapId"]));
                ResourceEffectExecutor.Register(registry);
                InfluenceEffectExecutor.Register(registry,new InfluenceService(query));
                PlayerEntranceEffectExecutor.Register(registry,query);
                EventCardEffectExecutor.Register(registry,query,new InfluenceService(query),
                    new YC.Domain.Cards.EventDeckService((int)raw["EventDeckSeed"]),new YC.Domain.Cards.ResourceTokenService());
                var round=new RoundExecutionService(registry);
                var recovered=HostRecoveryService.RecoverJson(hostJson,registry);
                Assert.That(recovered.Succeeded,Is.True,recovered.Diagnostic);
                var before=recovered.State;setup.Stop();
                fixture.Artifacts.WriteLabeledText("R14同输入原前Host",hostJson);
                fixture.Artifacts.WriteLabeledText("R14同输入原命令",JsonConvert.SerializeObject(command));
                for(int sample=0;sample<3;sample++)
                {
                    var state=GameStateCloneService.DeepClone(before);
                    var session=new GameSession(state);session.ExecutionTimings.Enabled=true;
                    session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry,round));
                    session.RegisterHandler(new AnswerInteractionCommandHandler(registry,round));
                    var countersBefore=session.ExecutionTimings.Snapshot();
                    var oldLua=LuaEffectEventDispatcher.CpuTimingSample;var oldValidation=ItemZoneQuery.ValidationTimingSample;
                    CommandResult result;var watch=new Stopwatch();
                    try
                    {
                        LuaEffectEventDispatcher.CpuTimingSample=null;ItemZoneQuery.ValidationTimingSample=null;
                        watch.Start();result=session.Submit(command);watch.Stop();
                    }
                    finally {LuaEffectEventDispatcher.CpuTimingSample=oldLua;ItemZoneQuery.ValidationTimingSample=oldValidation;}
                    var countersAfter=session.ExecutionTimings.Snapshot();
                    bool stateMatches=JToken.DeepEquals(record["state"],JToken.FromObject(session.State));
                    bool resultMatches=JToken.DeepEquals(record["results"][0],JToken.FromObject(result));
                    rows.Add(new {sample,elapsedMs=watch.Elapsed.TotalMilliseconds,result.Succeeded,stateMatches,resultMatches,
                        targetCounters=countersAfter.ToDictionary(p=>p.Key,p=>p.Key=="clock.frequency"?p.Value:p.Value-countersBefore[p.Key])});
                    if(sample==0)
                    {
                        fixture.Artifacts.WriteLabeledText("R14同输入首次实际后Host",HostRecoveryService.SerializeArchive(session.CreateHostArchive(session.State.ContentPackHash)));
                        fixture.Artifacts.WriteLabeledText("R14同输入首次完整结果",JsonConvert.SerializeObject(result));
                    }
                    Assert.That(result.Succeeded,Is.True,result.LogMessage);
                    Assert.That(stateMatches,Is.True,"完整最终状态必须匹配R12原件");
                    Assert.That(resultMatches,Is.True,"完整CommandResult必须匹配R12原件");
                }
                fixture.Artifacts.WriteLabeledText("R14同输入准备与版本",JsonConvert.SerializeObject(new {scenario,before.GameId,before.ContentPackHash,
                    preparationTotalMsInclusiveFixtureIo=setup.Elapsed.TotalMilliseconds,
                    assemblies=new[]{typeof(GameState).Assembly,typeof(GameSession).Assembly,typeof(ExternalContentPack).Assembly}
                        .Select(a=>new {a.FullName,a.Location,mvid=a.ManifestModule.ModuleVersionId}),
                    scope="准备含私有内容复制/注册/正式Host恢复，首次内建定义成本留在实际准备段；不作为正常启动A/B。"},Formatting.Indented));
            }
            finally
            {
                fixture.Artifacts.WriteLabeledText("R14同输入三次无监听复放",JsonConvert.SerializeObject(new {scenario,rows,
                    scope="两版同R12原Host/原命令，首个Submit单列，其后2次为本进程暖重复；不代表原按钮/资产冷开。全部序列化/Host/I/O在Submit计时外。"},Formatting.Indented));
            }
        }
    }
}
