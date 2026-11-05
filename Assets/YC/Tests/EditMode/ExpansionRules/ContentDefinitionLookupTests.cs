using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只检查元数据查询及其复制隔离；人工元数据不计作正常对局或UI覆盖。
    public sealed class ContentDefinitionLookupTests
    {
        private static ExternalContentPack Metadata()
        {
            var pack=new ExternalContentPack();
            var rows=(List<ExternalContentDefinition>)typeof(ExternalContentPack).GetField("definitions",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pack);
            ExternalContentDefinition Definition(string id,string kind,bool enabled)=>new ExternalContentDefinition
            {
                DefinitionId=id,ContentType=kind,Enabled=enabled,RuntimeId="runtime."+kind,Artwork="images/"+kind+".png",Tags=new[]{"tag"},
                Data=JObject.Parse("{\"nested\":{\"value\":3},\"faces\":[{\"faceId\":\"front\",\"artwork\":\"images/front.png\"},{\"faceId\":\"back\",\"artwork\":\"\"}]}"),
                AbilitySources=new List<AbilitySourceDeclaration>{new AbilitySourceDeclaration{AbilityId="isolated.ability"}},
                ArtworkSlice=new ExternalArtworkSlice{Id="slice."+kind,Artwork="sheet.png",SourceWidth=1000,SourceHeight=800,X=11,Y=13,Width=200,Height=300}
            };
            rows.Add(Definition("same","character",false));rows.Add(Definition("same","award",true));rows.Add(Definition("other","facility",true));rows.Add(Definition("disabled","character",false));
            return pack;
        }
        private static string Model(object value)=>JsonConvert.SerializeObject(value);
        [TestCase(false)][TestCase(true)]
        public void DefinitionQueriesMatchTheOriginalCatalogueSelection(bool enabledOnly)
        {
            var pack=Metadata();var catalogue=pack.Definitions;
            foreach(var id in new[]{null,"same","other","disabled","unknown"})
            foreach(var kind in new[]{null,"character","award","facility","unknown",""})
            {
                var original=catalogue.FirstOrDefault(d=>d.DefinitionId==id && (kind==null || d.ContentType==kind) && (!enabledOnly || d.Enabled));
                var actual=pack.FindDefinition(id,kind,enabledOnly);
                Assert.That(Model(actual),Is.EqualTo(Model(original)));
                Assert.That(actual?.RuntimeId,Is.EqualTo(original?.RuntimeId));
                Assert.That(Model(actual?.ArtworkSlice),Is.EqualTo(Model(original?.ArtworkSlice)));
            }
        }
        [Test]
        public void DefinitionQueriesReturnIndependentNestedDataAndIgnoredFields()
        {
            var pack=Metadata();var first=pack.FindDefinition("other");var expected=pack.FindDefinition("other");
            first.Data["nested"]["value"]=999;first.Tags[0]="changed";first.AbilitySources[0].AbilityId="changed";first.ArtworkSlice.X=999;first.RuntimeId="changed";
            var next=pack.FindDefinition("other");
            Assert.That(Model(next),Is.EqualTo(Model(expected)));Assert.That(next.RuntimeId,Is.EqualTo(expected.RuntimeId));Assert.That(Model(next.ArtworkSlice),Is.EqualTo(Model(expected.ArtworkSlice)));
            Assert.That(next.Data,Is.Not.SameAs(expected.Data));Assert.That(next.AbilitySources[0],Is.Not.SameAs(expected.AbilitySources[0]));Assert.That(next.ArtworkSlice,Is.Not.SameAs(expected.ArtworkSlice));
        }
        [TestCase(null)][TestCase("")][TestCase("front")][TestCase("back")][TestCase("unknown")]
        public void ArtworkSelectionMatchesEnabledFirstMatchAndExactFace(string face)
        {
            var pack=Metadata();var catalogue=pack.Definitions;
            foreach(string id in new[]{null,"same","other","disabled","unknown"})
            {
                var definition=catalogue.FirstOrDefault(d=>d.Enabled && d.DefinitionId==id);string expected=null;ExternalArtworkSlice expectedSlice=null;bool found=definition!=null;
                if(found)
                {
                    if(string.IsNullOrEmpty(face)){expected=definition.Artwork;expectedSlice=definition.ArtworkSlice;}
                    else {expected=(string)definition.Data["faces"]?.FirstOrDefault(f=>(string)f["faceId"]==face)?["artwork"];found=!string.IsNullOrEmpty(expected);}
                }
                Assert.That(pack.TryGetDeclaredArtwork(id,face,out var path,out var slice),Is.EqualTo(found));
                Assert.That(path,Is.EqualTo(expected));Assert.That(Model(slice),Is.EqualTo(Model(expectedSlice)));
            }
        }
        [Test]
        public void ArtworkSlicesAreDetachedOnEveryLookup()
        {
            var pack=Metadata();Assert.That(pack.TryGetDeclaredArtwork("other","",out var path,out var slice),Is.True);
            slice.X=999;slice.Artwork="changed";
            Assert.That(pack.TryGetDeclaredArtwork("other","",out var nextPath,out var next),Is.True);
            Assert.That(nextPath,Is.EqualTo(path));Assert.That(next.X,Is.EqualTo(11));Assert.That(next.Artwork,Is.EqualTo("sheet.png"));Assert.That(next,Is.Not.SameAs(slice));
        }
        [Test]
        public void RecordRealCatalogueLookupCostWithoutRenderingOrChangingTheGame()
        {
            using var fixture=new PaymentTestFixture();var pack=ExternalContentPack.Load(fixture.PrivateContent);
            string id=pack.Definitions.First(d=>d.Enabled && !string.IsNullOrEmpty(d.Artwork)).DefinitionId;const int count=12;
            var clock=Stopwatch.StartNew();for(int i=0;i<count;i++)Assert.That(pack.Definitions.FirstOrDefault(d=>d.Enabled && d.DefinitionId==id),Is.Not.Null);clock.Stop();double original=clock.Elapsed.TotalSeconds;
            clock.Restart();for(int i=0;i<count;i++)Assert.That(pack.TryGetDeclaredArtwork(id,"",out _,out _),Is.True);clock.Stop();double targeted=clock.Elapsed.TotalSeconds;
            fixture.Artifacts.WriteLabeledText("内容定点查询隔离计时",Model(new{Scope="仅目录元数据查询，非正常UI操作耗时",Iterations=count,OriginalCatalogueSeconds=original,TargetedArtworkSeconds=targeted,OriginalAverageSeconds=original/count,TargetedAverageSeconds=targeted/count}));
        }
    }
}
