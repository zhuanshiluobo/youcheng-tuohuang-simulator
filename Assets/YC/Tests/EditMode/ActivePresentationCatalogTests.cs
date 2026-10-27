using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    // 可独立CLR执行的只读合同；不是Unity页面/正常局或时延验收。
    public sealed class ActivePresentationCatalogTests
    {
        public object Evidence;
        private static (ExternalContentPack installed, ActiveContentCatalog catalog, GameRuleConfiguration config) Open()
        {
            var root=Environment.GetEnvironmentVariable("YC_ACTIVE_PRESENTATION_TEST_ROOT") ??
                Path.Combine(Directory.GetCurrentDirectory(),"Assets/StreamingAssets/Content/core");
            var installed=ExternalContentPack.Load(root);
            var config=new GameRuleConfiguration {PlayerCount=2,MapId="map-three-players",RuleModeId="standard",
                EnabledExpansionIds=new List<string>{"core","enterprise"}};
            var catalog=ActiveContentCatalogBuilder.Build(installed,config);config.ContentPackHash=catalog.ContentPackHash;
            return(installed,catalog,config);
        }
        [Test]public void FrozenDetailsMatchLegacyAndReturnedValuesCannotMutateTheCatalog()
        {
            var f=Open();var legacy=f.installed.CreateActiveSnapshot(f.config);var rows=new List<object>();
            foreach(var definition in f.catalog.Definitions.Where(d=>(d.ContentType=="enterprise"||d.ContentType=="department") &&
                !string.IsNullOrEmpty(d.Presentation?.DetailScript)))
            {
                var context=new GameplayPresentationContext {Purpose="enterprise_detail",SourceDefinitionId=definition.DefinitionId,
                    ViewerPlayerId=1,StateRevision=7,Facts=NormalizedValue.CreateObject(new List<NormalizedValueEntry>())};
                string before=JsonConvert.SerializeObject(context);
                Assert.That(legacy.TryPresent(definition.Presentation.DetailScript,context,out var expected,out var oldCode,out var oldReason),Is.True,oldReason);
                Assert.That(f.catalog.Presentation.TryPresentEnterprise(definition.Presentation.DetailScript,context,out var actual,out var code,out var reason),Is.True,reason);
                string json=JsonConvert.SerializeObject(expected);
                Assert.That(JsonConvert.SerializeObject(actual),Is.EqualTo(json));Assert.That(code,Is.EqualTo(oldCode));
                Assert.That(JsonConvert.SerializeObject(context),Is.EqualTo(before),"调用方事实不能被等级注入改写");
                actual.Details.Clear();definition.Data["levels"]=new JArray();
                Assert.That(f.catalog.Presentation.TryPresentEnterprise(definition.Presentation.DetailScript,context,out var again,out _,out reason),Is.True,reason);
                Assert.That(JsonConvert.SerializeObject(again),Is.EqualTo(json),"展示结果/定义副本不能污染正式活动目录");
                rows.Add(new {definition.DefinitionId,definition.Presentation.DetailScript,json});
            }
            Assert.That(rows,Is.Not.Empty);
            Assert.That(typeof(IActiveContentPresentation).GetProperties().Any(p=>p.PropertyType==typeof(ExternalContentPack)),Is.False);
            Evidence=new {f.installed.ContentHash,active=f.catalog.ContentPackHash,rows};
        }
        [Test]public void InstalledActiveAndEveryConfigurationIdentityMustMatch()
        {
            var f=Open();var p=f.catalog.Presentation;
            Assert.That(p.MatchesConfiguration(f.config,f.catalog.ContentPackHash,f.installed.ContentHash),Is.True);
            var reordered=f.config.Clone();reordered.EnabledExpansionIds.Reverse();
            Assert.That(p.MatchesConfiguration(reordered,f.catalog.ContentPackHash,f.installed.ContentHash),Is.True);
            Assert.That(p.MatchesConfiguration(f.config,"other_active",f.installed.ContentHash),Is.False);
            Assert.That(p.MatchesConfiguration(f.config,f.catalog.ContentPackHash,"other_installed"),Is.False);
            var mutations=new Action<GameRuleConfiguration>[] {
                c=>c.ContentPackHash="other",c=>c.SchemaVersion++,c=>c.MapId="map-four-players",c=>c.RuleModeId="entrepreneur",
                c=>c.PlayerCount=3,c=>c.ConfigurationRevision++,c=>c.RuleContractVersion="other",
                c=>c.PlayerStageSchedulingMode=PlayerStageSchedulingMode.Sequential,
                c=>c.EnabledExpansionIds.Remove("enterprise"),c=>c.EnabledExpansionIds=new List<string>{"core","core"} };
            foreach(var mutate in mutations){var changed=f.config.Clone();mutate(changed);
                Assert.That(p.MatchesConfiguration(changed,f.catalog.ContentPackHash,f.installed.ContentHash),Is.False,JsonConvert.SerializeObject(changed));}
            Evidence=new {active=f.catalog.ContentPackHash,negativeConfigurations=mutations.Length,installed=p.InstalledContentHash};
        }
        [Test]public void ReadOnlyPresentationRejectsOtherPurposesAndUnregisteredSources()
        {
            var f=Open();var d=f.catalog.Definitions.First(x=>x.ContentType=="enterprise"&&!string.IsNullOrEmpty(x.Presentation?.DetailScript));
            var c=new GameplayPresentationContext {Purpose="ability_action",SourceDefinitionId=d.DefinitionId,ViewerPlayerId=1};
            Assert.That(f.catalog.Presentation.TryPresentEnterprise(d.Presentation.DetailScript,c,out _,out _,out var reason),Is.False);
            Assert.That(reason,Is.EqualTo("active_presentation_scope_invalid"));
            c.Purpose="enterprise_detail";c.SourceDefinitionId="not_registered";
            Assert.That(f.catalog.Presentation.TryPresentEnterprise(d.Presentation.DetailScript,c,out _,out _,out reason),Is.False);
            Assert.That(reason,Does.Contain("presentation_enterprise_source_invalid"));
            Evidence=new {active=f.catalog.ContentPackHash,reason};
        }
        [Test]public void SessionAndGameChangesRequireExplicitMatchingRebinding()
        {
            var f=Open();var session=new GameSession(new GameState {GameId="readonly-binding-fixture",ContentPackHash=f.catalog.ContentPackHash,RuleConfiguration=f.config.Clone()});
            var view=new GameStateView {GameId=session.State.GameId,ContentPackHash=f.catalog.ContentPackHash,RuleConfiguration=f.config.Clone()};
            var type=Type.GetType("YC.Presentation.SessionPresentationBinding, Assembly-CSharp",false) ??
                typeof(ActivePresentationCatalogTests).Assembly.GetType("YC.Presentation.SessionPresentationBinding",true);
            const BindingFlags flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
            object Create(GameSession target,GameStateView v)=>(object)type.GetMethod("Create",flags).Invoke(null,
                new object[]{f.catalog.Presentation,target,v.GameId,v.RuleConfiguration,v.ContentPackHash,f.installed.ContentHash});
            bool Matches(object binding,GameSession target,GameStateView v)=>(bool)type.GetMethod("Matches",flags).Invoke(binding,new object[]{target,v,f.installed.ContentHash});
            var bound=Create(session,view);Assert.That(bound,Is.Not.Null);Assert.That(Matches(bound,session,view),Is.True);
            var replacement=new GameSession(GameStateCloneService.DeepClone(session.State));
            Assert.That(Matches(bound,replacement,view),Is.False,"相同GameId的新会话不能自动继承旧绑定");
            Assert.That(Matches(Create(replacement,view),replacement,view),Is.True);
            view.GameId="another-game";Assert.That(Matches(bound,session,view),Is.False);
            view.RuleConfiguration.PlayerCount++;Assert.That(Create(replacement,view),Is.Null);
            Evidence=new {scope="隔离会话身份合同，未冒正式Host恢复",active=f.catalog.ContentPackHash};
        }
    }
}
