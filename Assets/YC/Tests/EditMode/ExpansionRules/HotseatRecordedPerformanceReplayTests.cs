using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 同档/原命令隔离定位，绝不计作正常页面验收；只读原件，报告写到新路径。
    public sealed class HotseatRecordedPerformanceReplayTests
    {
        private Action<string,string,long> timing;
        private Dictionary<string,string> replayArguments;
        private YC.Domain.State.GameState capturedReplayState;
        [TearDown] public void RemoveTiming() { if(timing!=null) YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample-=timing; timing=null; }
        [TestCase(5736)][TestCase(6109)][TestCase(7461)][TestCase(8485)][TestCase(9300)][TestCase(9916)][TestCase(10036)]
        public void OptionalCapturedCommandProfiles(int revision)
        {
            string Arg(string key) => Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string checkpoints=Arg("--yc-replay-checkpoint-directory"), output=Arg("--yc-replay-checkpoint-output");
            if(checkpoints==null || output==null)Assert.Ignore("仅显式提供原命令检查点及新报告路径时执行隔离定位，不计正常UI验收。");
            Directory.CreateDirectory(output);
            replayArguments=new Dictionary<string,string> {
                {"--yc-replay-source",Path.Combine(checkpoints,"replay.json.before-"+revision+".json")},
                {"--yc-replay-report",Path.Combine(output,"replay-"+revision+".json")},
                {"--yc-replay-last-before",revision.ToString(System.Globalization.CultureInfo.InvariantCulture)} };
            try { OptionalOriginalCommandsKeepReceiptsAndRevisionsWhileMeasuringEachStep(); }
            finally { replayArguments=null; }
        }
        [TestCase(5736)][TestCase(6109)][TestCase(7461)][TestCase(8485)][TestCase(9300)][TestCase(9916)][TestCase(10036)]
        public void OptionalJournalPublicationComparisonProfiles(int revision)
        {
            string Arg(string key)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string checkpoints=Arg("--yc-replay-checkpoint-directory"),output=Arg("--yc-replay-journal-comparison-output");
            if(checkpoints==null||output==null)Assert.Ignore("仅显式提供原检查点和新报告目录时比较日志发布，不计正常UI验收。");
            Directory.CreateDirectory(output);var measurements=new List<object>();YC.Domain.State.GameState previous=null;
            try
            {
                foreach(bool full in revision%2==0?new[]{true,false}:new[]{false,true})
                {
                    string report=Path.Combine(output,"journal-"+revision+(full?"-full.json":"-prefix.json"));
                    replayArguments=new Dictionary<string,string>{
                        {"--yc-replay-source",Path.Combine(checkpoints,"replay.json.before-"+revision+".json")},
                        {"--yc-replay-report",report},{"--yc-replay-last-before",revision.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                        {"--yc-replay-full-journal-publication",full?"true":"false"},{"--yc-replay-capture-final-state","true"}};
                    capturedReplayState=null;
                    try { OptionalOriginalCommandsKeepReceiptsAndRevisionsWhileMeasuringEachStep(); }
                    finally { RemoveTiming(); }
                    Assert.That(capturedReplayState,Is.Not.Null);
                    if(previous==null)previous=capturedReplayState;
                    else Assert.That(YC.Domain.State.GameStateCloneService.AreEquivalent(previous,capturedReplayState),Is.True,"同原命令两种发布必须得到相同的全部持久字段。");
                    var row=JObject.Parse(File.ReadAllText(report))["Rows"].Single();long hits=(long?)row["Calls"]["handler.copy.journal_prefix.calls"]??0;
                    if(full)Assert.That(hits,Is.Zero);else if(revision==6109)Assert.That(hits,Is.GreaterThan(0));
                    measurements.Add(new{FullJournalPublication=full,Seconds=(double)row["Seconds"],PrefixPublicationCalls=hits,Report=Path.GetFileName(report)});
                }
                File.WriteAllText(Path.Combine(output,"journal-comparison-"+revision+".json"),JsonConvert.SerializeObject(new{Scope="同进程交替原命令发布计时，非正常UI验收",BeforeRevision=revision,AllPersistentFieldsEquivalent=true,ForcedGc=false,Measurements=measurements},Formatting.Indented));
            }
            finally { RemoveTiming();replayArguments=null;capturedReplayState=null; }
        }
        [TestCase(5736)][TestCase(6109)][TestCase(7461)][TestCase(8485)][TestCase(9300)][TestCase(9916)][TestCase(10036)]
        public void OptionalDeferredValidationComparisonProfiles(int revision)
        {
            string Arg(string key)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string checkpoints=Arg("--yc-replay-checkpoint-directory"),output=Arg("--yc-replay-deferred-comparison-output");
            if(checkpoints==null||output==null)Assert.Ignore("仅显式提供原检查点和新报告目录时比较离场响应校验，不计正常UI验收。");
            Directory.CreateDirectory(output);var measurements=new List<object>();YC.Domain.State.GameState previous=null;
            try
            {
                foreach(bool full in revision%2==0?new[]{true,false}:new[]{false,true})
                {
                    string report=Path.Combine(output,"deferred-"+revision+(full?"-full.json":"-indexed.json"));
                    replayArguments=new Dictionary<string,string>{
                        {"--yc-replay-source",Path.Combine(checkpoints,"replay.json.before-"+revision+".json")},
                        {"--yc-replay-report",report},{"--yc-replay-last-before",revision.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                        {"--yc-replay-full-deferred-validation",full?"true":"false"},{"--yc-replay-capture-final-state","true"}};
                    capturedReplayState=null;
                    try { OptionalOriginalCommandsKeepReceiptsAndRevisionsWhileMeasuringEachStep(); }
                    finally { RemoveTiming(); }
                    Assert.That(capturedReplayState,Is.Not.Null);
                    if(previous==null)previous=capturedReplayState;
                    else Assert.That(YC.Domain.State.GameStateCloneService.AreEquivalent(previous,capturedReplayState),Is.True,"同原命令两种校验必须得到相同的全部持久字段。");
                    var row=JObject.Parse(File.ReadAllText(report))["Rows"].Single();long hits=(long?)row["Calls"]["deferred.validation.index.calls"]??0;
                    if(full)Assert.That(hits,Is.Zero);else if(revision==6109)Assert.That(hits,Is.GreaterThan(0));
                    measurements.Add(new{FullDeferredValidation=full,Seconds=(double)row["Seconds"],IndexedValidationCalls=hits,Report=Path.GetFileName(report)});
                }
                File.WriteAllText(Path.Combine(output,"deferred-comparison-"+revision+".json"),JsonConvert.SerializeObject(new{Scope="同进程交替原命令离场响应校验计时，非正常UI验收",BeforeRevision=revision,AllPersistentFieldsEquivalent=true,ForcedGc=false,Measurements=measurements},Formatting.Indented));
            }
            finally { RemoveTiming();replayArguments=null;capturedReplayState=null; }
        }
        [TestCase(5736)][TestCase(6109)][TestCase(7461)][TestCase(8485)][TestCase(9300)][TestCase(9916)][TestCase(10036)]
        public void OptionalBuiltinFlowComparisonProfiles(int revision)
        {
            string Arg(string key)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string checkpoints=Arg("--yc-replay-checkpoint-directory"),output=Arg("--yc-replay-flow-comparison-output");
            if(checkpoints==null||output==null)Assert.Ignore("仅显式提供原检查点和新报告目录时比较内置流程发布，不计正常UI验收。");
            Directory.CreateDirectory(output);var measurements=new List<object>();YC.Domain.State.GameState previous=null;
            try
            {
                foreach(bool full in revision%2==0?new[]{true,false}:new[]{false,true})
                {
                    string report=Path.Combine(output,"flow-"+revision+(full?"-full.json":"-proven.json"));
                    replayArguments=new Dictionary<string,string>{
                        {"--yc-replay-source",Path.Combine(checkpoints,"replay.json.before-"+revision+".json")},
                        {"--yc-replay-report",report},{"--yc-replay-last-before",revision.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                        {"--yc-replay-full-builtin-flow-publication",full?"true":"false"},{"--yc-replay-capture-final-state","true"}};
                    capturedReplayState=null;
                    try { OptionalOriginalCommandsKeepReceiptsAndRevisionsWhileMeasuringEachStep(); }
                    finally { RemoveTiming(); }
                    Assert.That(capturedReplayState,Is.Not.Null);
                    if(previous==null)previous=capturedReplayState;
                    else Assert.That(YC.Domain.State.GameStateCloneService.AreEquivalent(previous,capturedReplayState),Is.True,"同原命令两种流程发布必须得到相同的全部持久字段。");
                    var row=JObject.Parse(File.ReadAllText(report))["Rows"].Single();long hits=(long?)row["Calls"]["kernel.independent_execution_writes.calls"]??0;
                    if(revision==9300&&!full)Assert.That(hits,Is.GreaterThan(9),"原付款慢命令应实际覆盖新增流程证明。");
                    measurements.Add(new{FullBuiltinFlowPublication=full,Seconds=(double)row["Seconds"],IndependentPublicationCalls=hits,Report=Path.GetFileName(report)});
                }
                File.WriteAllText(Path.Combine(output,"flow-comparison-"+revision+".json"),JsonConvert.SerializeObject(new{Scope="同进程交替原命令内置流程发布计时，非正常UI验收",BeforeRevision=revision,AllPersistentFieldsEquivalent=true,ForcedGc=false,Measurements=measurements},Formatting.Indented));
            }
            finally { RemoveTiming();replayArguments=null;capturedReplayState=null; }
        }
        [TestCase(5736)][TestCase(6109)][TestCase(7461)][TestCase(8485)][TestCase(9300)][TestCase(9916)][TestCase(10036)]
        [TestCase(8231)][TestCase(8709)][TestCase(8903)][TestCase(9589)][TestCase(9671)][TestCase(9740)]
        public void OptionalFactoryPublicationComparisonProfiles(int revision)
        {
            string Arg(string key)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string checkpoints=Arg("--yc-replay-checkpoint-directory"),output=Arg("--yc-replay-factory-comparison-output");
            if(checkpoints==null||output==null)Assert.Ignore("仅显式提供原检查点和新报告目录时比较实体工厂写集发布，不计正常UI验收。");
            if(new[]{8231,8709,8903,9589,9671,9740}.Contains(revision))checkpoints=Arg("--yc-replay-extended-checkpoint-directory");
            Assert.That(checkpoints,Is.Not.Null,"扩展慢步骤需要R106连续原命令生成的检查点。");
            int expectedEndRevision=(int)File.ReadLines(Arg("--yc-replay-commands")).Where(line=>!string.IsNullOrWhiteSpace(line)).Select(JObject.Parse).First(row=>(int)row["BeforeRevision"]==revision)["AfterRevision"];
            int lastIncludedBeforeRevision=expectedEndRevision-1;
            Directory.CreateDirectory(output);var measurements=new List<object>();YC.Domain.State.GameState previous=null;
            try
            {
                foreach(bool full in revision%2==0?new[]{true,false}:new[]{false,true})
                {
                    string report=Path.Combine(output,"factory-"+revision+(full?"-full.json":"-item-world.json"));
                    replayArguments=new Dictionary<string,string>{
                        {"--yc-replay-source",Path.Combine(checkpoints,"replay.json.before-"+revision+".json")},
                        {"--yc-replay-report",report},{"--yc-replay-last-before",lastIncludedBeforeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                        {"--yc-replay-full-structural-publication",full?"true":"false"},{"--yc-replay-capture-final-state","true"}};
                    capturedReplayState=null;
                    try { OptionalOriginalCommandsKeepReceiptsAndRevisionsWhileMeasuringEachStep(); }
                    finally { RemoveTiming(); }
                    Assert.That(capturedReplayState,Is.Not.Null);
                    Assert.That(capturedReplayState.EffectRuntime.StateRevision,Is.EqualTo(expectedEndRevision),"必须完整覆盖原同一点击内的所有命令。");
                    if(previous==null)previous=capturedReplayState;
                    else Assert.That(YC.Domain.State.GameStateCloneService.AreEquivalent(previous,capturedReplayState),Is.True,"同原命令两种工厂发布必须得到相同的全部持久字段。");
                    var measuredRows=JObject.Parse(File.ReadAllText(report))["Rows"].ToArray();long hits=measuredRows.Sum(row=>(long?)row["Calls"]["handler.copy.item_world.calls"]??0);
                    if(full)Assert.That(hits,Is.Zero);else if(revision==7461||revision==8709)Assert.That(hits,Is.GreaterThan(0),"必须真实覆盖新工厂发布路径。");
                    measurements.Add(new{FullStructuralPublication=full,Seconds=measuredRows.Sum(row=>(double)row["Seconds"]),CommandCount=measuredRows.Length,ExpectedAfterRevision=expectedEndRevision,ItemWorldPublicationCalls=hits,Report=Path.GetFileName(report)});
                }
                File.WriteAllText(Path.Combine(output,"factory-comparison-"+revision+".json"),JsonConvert.SerializeObject(new{Scope="同进程交替原命令实体工厂发布计时，非正常UI验收",BeforeRevision=revision,AllPersistentFieldsEquivalent=true,ForcedGc=false,Measurements=measurements},Formatting.Indented));
            }
            finally { RemoveTiming();replayArguments=null;capturedReplayState=null; }
        }
        [TestCase(5736)][TestCase(6109)][TestCase(7461)][TestCase(8485)][TestCase(9300)][TestCase(9916)][TestCase(10036)]
        [TestCase(8231)][TestCase(8709)][TestCase(8903)][TestCase(9589)][TestCase(9671)][TestCase(9740)]
        public void OptionalNodeItemWorldPreviewComparisonProfiles(int revision)
        {
            string Arg(string key)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string checkpoints=Arg("--yc-replay-checkpoint-directory"),output=Arg("--yc-replay-node-preview-comparison-output");
            if(checkpoints==null||output==null)Assert.Ignore("仅显式提供原检查点和新报告目录时比较离场节点物品预演，不计正常UI验收。");
            if(new[]{8231,8709,8903,9589,9671,9740}.Contains(revision))checkpoints=Arg("--yc-replay-extended-checkpoint-directory");
            Assert.That(checkpoints,Is.Not.Null,"扩展慢步骤需要R106连续原命令生成的检查点。");
            int expectedEndRevision=(int)File.ReadLines(Arg("--yc-replay-commands")).Where(line=>!string.IsNullOrWhiteSpace(line)).Select(JObject.Parse).First(row=>(int)row["BeforeRevision"]==revision)["AfterRevision"];
            int lastIncludedBeforeRevision=expectedEndRevision-1;
            Directory.CreateDirectory(output);var measurements=new List<object>();YC.Domain.State.GameState previous=null;
            try
            {
                foreach(bool full in revision%2==0?new[]{true,false}:new[]{false,true})
                {
                    string report=Path.Combine(output,"node-preview-"+revision+(full?"-full.json":"-narrow.json"));
                    replayArguments=new Dictionary<string,string>{
                        {"--yc-replay-source",Path.Combine(checkpoints,"replay.json.before-"+revision+".json")},
                        {"--yc-replay-report",report},{"--yc-replay-last-before",lastIncludedBeforeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                        {"--yc-replay-full-node-item-world-previews",full?"true":"false"},{"--yc-replay-capture-final-state","true"}};
                    capturedReplayState=null;
                    try { OptionalOriginalCommandsKeepReceiptsAndRevisionsWhileMeasuringEachStep(); }
                    finally { RemoveTiming(); }
                    Assert.That(capturedReplayState,Is.Not.Null);
                    Assert.That(capturedReplayState.EffectRuntime.StateRevision,Is.EqualTo(expectedEndRevision),"必须完整覆盖原同一点击内的所有命令。");
                    if(previous==null)previous=capturedReplayState;
                    else Assert.That(YC.Domain.State.GameStateCloneService.AreEquivalent(previous,capturedReplayState),Is.True,"同原命令两种节点物品预演必须得到相同的全部持久字段。");
                    var measuredRows=JObject.Parse(File.ReadAllText(report))["Rows"].ToArray();long hits=measuredRows.Sum(row=>(long?)row["Calls"]["transfer.node_preview.calls"]??0);
                    if(full)Assert.That(hits,Is.Zero);else if(revision==7461||revision==8709)Assert.That(hits,Is.GreaterThan(0),"必须真实覆盖离场节点物品预演。");
                    measurements.Add(new{FullNodePreview=full,Seconds=measuredRows.Sum(row=>(double)row["Seconds"]),CommandCount=measuredRows.Length,ExpectedAfterRevision=expectedEndRevision,NodePreviewCalls=hits,Report=Path.GetFileName(report)});
                }
                File.WriteAllText(Path.Combine(output,"node-preview-comparison-"+revision+".json"),JsonConvert.SerializeObject(new{Scope="同进程交替原命令节点物品预演计时，非正常UI验收",BeforeRevision=revision,AllPersistentFieldsEquivalent=true,ForcedGc=false,Measurements=measurements},Formatting.Indented));
            }
            finally { RemoveTiming();replayArguments=null;capturedReplayState=null; }
        }
        [TestCase(5736)][TestCase(6109)][TestCase(7461)][TestCase(8485)][TestCase(9300)][TestCase(9916)][TestCase(10036)]
        [TestCase(8231)][TestCase(8709)][TestCase(8903)][TestCase(9589)][TestCase(9671)][TestCase(9740)]
        public void OptionalCharacterItemWorldPreviewComparisonProfiles(int revision)
        {
            string Arg(string key)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string checkpoints=Arg("--yc-replay-checkpoint-directory"),output=Arg("--yc-replay-character-preview-comparison-output");
            if(checkpoints==null||output==null)Assert.Ignore("仅显式提供原检查点和新报告目录时比较角色移动与回收预演，不计正常UI验收。");
            if(new[]{8231,8709,8903,9589,9671,9740}.Contains(revision))checkpoints=Arg("--yc-replay-extended-checkpoint-directory");
            Assert.That(checkpoints,Is.Not.Null,"扩展慢步骤需要R106连续原命令生成的检查点。");
            int expectedEndRevision=(int)File.ReadLines(Arg("--yc-replay-commands")).Where(line=>!string.IsNullOrWhiteSpace(line)).Select(JObject.Parse).First(row=>(int)row["BeforeRevision"]==revision)["AfterRevision"];
            int lastIncludedBeforeRevision=expectedEndRevision-1;
            Directory.CreateDirectory(output);var measurements=new List<object>();YC.Domain.State.GameState previous=null;
            try
            {
                foreach(bool full in revision%2==0?new[]{true,false}:new[]{false,true})
                {
                    string report=Path.Combine(output,"character-preview-"+revision+(full?"-full.json":"-narrow.json"));
                    replayArguments=new Dictionary<string,string>{
                        {"--yc-replay-source",Path.Combine(checkpoints,"replay.json.before-"+revision+".json")},
                        {"--yc-replay-report",report},{"--yc-replay-last-before",lastIncludedBeforeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                        {"--yc-replay-full-character-item-world-previews",full?"true":"false"},{"--yc-replay-capture-final-state","true"}};
                    capturedReplayState=null;
                    try { OptionalOriginalCommandsKeepReceiptsAndRevisionsWhileMeasuringEachStep(); }
                    finally { RemoveTiming(); }
                    Assert.That(capturedReplayState,Is.Not.Null);
                    Assert.That(capturedReplayState.EffectRuntime.StateRevision,Is.EqualTo(expectedEndRevision),"必须完整覆盖原同一点击内的所有命令。");
                    if(previous==null)previous=capturedReplayState;
                    else Assert.That(YC.Domain.State.GameStateCloneService.AreEquivalent(previous,capturedReplayState),Is.True,"同原命令两种角色预演必须得到相同的全部持久字段。");
                    var measuredRows=JObject.Parse(File.ReadAllText(report))["Rows"].ToArray();long hits=measuredRows.Sum(row=>(long?)row["Calls"]["character.item_world.publish.calls"]??0);
                    if(full)Assert.That(hits,Is.Zero);
                    measurements.Add(new{FullCharacterPreview=full,Seconds=measuredRows.Sum(row=>(double)row["Seconds"]),CommandCount=measuredRows.Length,ExpectedAfterRevision=expectedEndRevision,CharacterPublicationCalls=hits,Report=Path.GetFileName(report)});
                }
                File.WriteAllText(Path.Combine(output,"character-preview-comparison-"+revision+".json"),JsonConvert.SerializeObject(new{Scope="同进程交替原命令角色物品预演计时，非正常UI验收",BeforeRevision=revision,AllPersistentFieldsEquivalent=true,ForcedGc=false,Measurements=measurements},Formatting.Indented));
            }
            finally { RemoveTiming();replayArguments=null;capturedReplayState=null; }
        }
        [Test]
        public void OptionalOriginalCommandsKeepReceiptsAndRevisionsWhileMeasuringEachStep()
        {
            string Arg(string key) => replayArguments!=null && replayArguments.TryGetValue(key,out var value) ? value :
                Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string source=Arg("--yc-replay-source"),commands=Arg("--yc-replay-commands"),report=Arg("--yc-replay-report");
            int lastBefore=int.TryParse(Arg("--yc-replay-last-before"),out int requestedLast)?requestedLast:2139;
            var captureRevisions=new HashSet<int>((Arg("--yc-replay-capture-before")??"").Split(',').Where(s=>s.Length>0).Select(int.Parse));
            if(source==null||commands==null||report==null)Assert.Ignore("仅显式提供同档/原命令/新报告路径才执行隔离定位");
            using var recoveryVerification=Arg("--yc-replay-verify-recovery-copy")=="true" ? YC.Domain.Items.ItemTransferService.VerifyRecoveryReplayCopies() : null;
            using var catalogCopies=Arg("--yc-replay-full-ability-catalog")=="true" ? YC.Domain.Effects.EffectRegistry.UseFullAbilityCatalogCopiesForDiagnostics() : null;
            using var journalCopies=Arg("--yc-replay-full-journal-publication")=="true" ? YC.Domain.State.GameStateCloneService.UseFullJournalPublicationForDiagnostics() : null;
            using var fullNodePreview=Arg("--yc-replay-full-node-item-world-previews")=="true" ? YC.Domain.State.GameStateCloneService.UseFullItemWorldNodePreviewsForDiagnostics() : null;
            using var verifyNodePreview=Arg("--yc-replay-verify-node-item-world-previews")=="true" ? YC.Domain.State.GameStateCloneService.VerifyItemWorldNodePreviewsForDiagnostics() : null;
            using var factoryPublication=Arg("--yc-replay-full-structural-publication")=="true" ? YC.Domain.Items.ItemTransferService.UseFullStructuralPublicationForDiagnostics() : null;
            using var flowPublication=Arg("--yc-replay-full-builtin-flow-publication")=="true" ? YC.Domain.Effects.EffectRegistry.UseFullBuiltinFlowPublicationForDiagnostics() : null;
            using var deferredValidation=Arg("--yc-replay-full-deferred-validation")=="true" ? YC.Domain.Effects.EffectTreeExecutor.UseFullDeferredResponseValidationForDiagnostics() : null;
            using var recycleCopies=Arg("--yc-replay-full-recycle-copies")=="true" ? YC.Domain.Cards.CharacterLifecycleService.UseFullRecycleCopiesForDiagnostics() : null;
            using var characterPreviews=Arg("--yc-replay-full-character-item-world-previews")=="true" ? YC.Domain.Cards.CharacterLifecycleService.UseFullCharacterItemWorldPreviewsForDiagnostics() : null;
            using var previewDigests=Arg("--yc-replay-full-response-preview-digests")=="true" ? YC.Domain.Effects.EffectRegistry.UseFullResponsePreviewDigestsForDiagnostics() : null;
            using var publicationShape=Arg("--yc-replay-full-publication-shape")=="true" ? YC.Domain.State.GameStateCloneService.UseFullPublicationShapeTraversalForDiagnostics() : null;
            using var fullCleanup=Arg("--yc-replay-full-cleanup-obligation-traversal")=="true" ? YC.Domain.Effects.UsageTracks.UseFullCleanupObligationTraversalForDiagnostics() : null;
            using var verifyCleanup=Arg("--yc-replay-verify-cleanup-obligation-traversal")=="true" ? YC.Domain.Effects.UsageTracks.VerifyCleanupObligationTraversalForDiagnostics() : null;
            using var fullDescendants=Arg("--yc-replay-full-descendant-traversal")=="true" ? YC.Domain.Scoring.FinalScoringSessionService.UseFullDescendantTraversalForDiagnostics() : null;
            using var verifyDescendants=Arg("--yc-replay-verify-descendant-traversal")=="true" ? YC.Domain.Scoring.FinalScoringSessionService.VerifyDescendantTraversalForDiagnostics() : null;
            using var fullStructural=Arg("--yc-replay-full-structural-previews")=="true" ? YC.Domain.Items.ItemTransferService.UseFullStructuralPreviewsForDiagnostics() : null;
            using var verifyStructural=Arg("--yc-replay-verify-structural-previews")=="true" ? YC.Domain.Items.ItemTransferService.VerifyStructuralPreviewsForDiagnostics() : null;
            using var entityFull=Arg("--yc-replay-full-entity-validation-snapshots")=="true" ? YC.Domain.Items.ItemZoneQuery.UseFullValidationSnapshotsForDiagnostics() : null;
            using var entityVerify=Arg("--yc-replay-verify-entity-validation-snapshots")=="true" ? YC.Domain.Items.ItemZoneQuery.VerifyValidationSnapshotsForDiagnostics() : null;
            using var independentWrites=Arg("--yc-replay-verify-independent-execution-writes")=="true" ? YC.Domain.Effects.EffectTreeExecutor.VerifyIndependentExecutionWritesForDiagnostics() : null;
            using var canonicalFull=Arg("--yc-replay-full-canonical-equality")=="true" ? YC.Domain.State.NormalizedValue.UseFullCanonicalEqualityForDiagnostics() : null;
            using var canonicalVerify=Arg("--yc-replay-verify-canonical-equality")=="true" ? YC.Domain.State.NormalizedValue.VerifyCanonicalEqualityForDiagnostics() : null;
            using var fullViewRuntime=Arg("--yc-replay-full-view-state-copies")=="true" ? YC.Domain.State.GameStateViewProjector.UseFullProjectionStateCopiesForDiagnostics() : null;
            using var verifyViewRuntime=Arg("--yc-replay-verify-view-state-copies")=="true" ? YC.Domain.State.GameStateViewProjector.VerifyProjectionStateCopiesForDiagnostics() : null;
            using var schedulingValidation=Arg("--yc-replay-full-status-validation")=="true" ? YC.Domain.State.GameStateCloneService.UseFullSchedulingValidationForDiagnostics() : null;
            using var eagerQueries=Arg("--yc-replay-eager-query-snapshots")=="true" ? YC.Infrastructure.Lua.LuaEffectEventDispatcher.UseEagerQuerySnapshotsForDiagnostics() : null;
            using var verifyQueries=Arg("--yc-replay-verify-query-snapshots")=="true" ? YC.Infrastructure.Lua.LuaEffectEventDispatcher.VerifyDeferredQuerySnapshotsForDiagnostics() : null;
            using var executionCopies=Arg("--yc-replay-disable-work-recycling")=="true" ? YC.Domain.Effects.EffectTreeExecutor.UseFullExecutionCopiesForDiagnostics() : null;
            var envelope=JObject.Parse(File.ReadAllText(source));
            var data=JsonConvert.DeserializeObject<MatchSaveData>((string)envelope["Payload"]);
            var bootstrapper=Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp",true);
            var restored=bootstrapper.GetMethod("Restore",BindingFlags.Static|BindingFlags.Public).Invoke(null,new object[]{null,data});
            var session=(GameSession)restored.GetType().GetField("Session").GetValue(restored);
            session.EnableConfirmedRecoveryCapture();session.ExecutionTimings.Enabled=true;
            Assert.That(YC.Domain.State.RuleStateDigestService.Compute(session.State),Is.EqualTo(LegacyRuleStateDigestReference.Compute(session.State)),"原档完整规则摘要必须兼容");
            Assert.That(LegacyRuleStateDigestReference.Compute(YC.Domain.State.GameStateCloneService.DeepClone(session.State)),
                Is.EqualTo(LegacyRuleStateDigestReference.Compute(session.State)),"空壳优化仍须深复制原档的所有持久字段");
            timing=(phase,definition,ticks)=>session.ExecutionTimings.AddSample(phase,ticks);
            YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample+=timing;
            var rows=new List<object>();
            var copyProfileRevisions=new HashSet<int>((Arg("--yc-replay-profile-state-copies-before")??"").Split(new[]{','},StringSplitOptions.RemoveEmptyEntries).Select(int.Parse));
            var copyCallers=new Dictionary<string,(int Calls,long Ticks)>(StringComparer.Ordinal);
            var copyStack=new Stack<(string Caller,long Started)>();bool captureCopies=false;int copyTraceErrors=0;
            Action<string,bool> copyTrace=(phase,begin)=>
            {
                if(!captureCopies || phase!="handler.clone")return;
                try
                {
                    if(begin)
                    {
                        string caller="unknown";
                        foreach(var frame in new StackTrace(false).GetFrames()??Array.Empty<StackFrame>())
                        {
                            var method=frame.GetMethod();string type=method?.DeclaringType?.FullName??"";
                            if(!type.StartsWith("YC.",StringComparison.Ordinal) || type.StartsWith("YC.Domain.State.GameStateCloneService",StringComparison.Ordinal) || type.Contains(nameof(HotseatRecordedPerformanceReplayTests)))continue;
                            caller=type+"."+method.Name;break;
                        }
                        copyStack.Push((caller,Stopwatch.GetTimestamp()));
                    }
                    else
                    {
                        var active=copyStack.Pop();copyCallers.TryGetValue(active.Caller,out var prior);
                        copyCallers[active.Caller]=(prior.Calls+1,prior.Ticks+Stopwatch.GetTimestamp()-active.Started);
                    }
                }
                catch{copyTraceErrors++;}
            };
            using var copyTracing=copyProfileRevisions.Count>0?YC.Domain.State.GameStateCloneService.BeginTimingScope(session.ExecutionTimings.AddSample,copyTrace):null;
            bool verifyScheduling=Arg("--yc-replay-verify-status-copy")=="true";
            bool compareSupplementalPayments=Arg("--yc-replay-supplemental-payment-comparison")!="false";
            using var schedulingVerification=verifyScheduling ? YC.Domain.State.GameStateCloneService.VerifySchedulingPublicationCopies() : null;
            long probeBefore=GC.GetAllocatedBytesForCurrentThread();var allocationProbe=new byte[8192];
            bool allocationAvailable=GC.GetAllocatedBytesForCurrentThread()-probeBefore>=allocationProbe.Length;
            GC.KeepAlive(allocationProbe);
            using var commandStream=new FileStream(commands,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var commandReader=new StreamReader(commandStream);
            string line;
            var originals=new List<JObject>();
            while((line=commandReader.ReadLine())!=null) originals.Add(JObject.Parse(line));
            int initialRevision=session.State.EffectRuntime.StateRevision, lastExpected=initialRevision;
            // 原回执文件按返回顺序落盘；同步UI回调可嵌套提交。按实际BeforeRevision还原权威提交顺序，
            // 外层回执的AfterRevision可能已包括子回调，不能把它当外层单条命令自己的结束revision。
            foreach(var original in originals.OrderBy(value=>(int)value["BeforeRevision"]))
            {
                if((int)original["BeforeRevision"]<initialRevision)continue;
                if((int)original["BeforeRevision"]>lastBefore)break;
                Assert.That(session.State.EffectRuntime.StateRevision,Is.EqualTo((int)original["BeforeRevision"]));
                if(captureRevisions.Contains(session.State.EffectRuntime.StateRevision))
                {
                    var captured=MatchSaveData.CreateFromLocalCaptureAsync(data,session.CaptureStableSavePoint()).GetAwaiter().GetResult();
                    File.WriteAllText(report+".before-"+session.State.EffectRuntime.StateRevision+".json",JsonConvert.SerializeObject(new {
                        Scope="原命令隔离重放检查点，非正常UI验收存档",Payload=JsonConvert.SerializeObject(captured)}));
                }
                var command=original["Command"].ToObject<GameCommand>();
                var before=session.ExecutionTimings.Snapshot();long memory=GC.GetTotalMemory(false);
                using var process=Process.GetCurrentProcess(); process.Refresh();
                long workingSet=process.WorkingSet64,privateBytes=process.PrivateMemorySize64;
                long allocated=GC.GetAllocatedBytesForCurrentThread();
                captureCopies=copyProfileRevisions.Contains((int)original["BeforeRevision"]);copyCallers.Clear();copyStack.Clear();copyTraceErrors=0;
                var clock=Stopwatch.StartNew();var result=session.Submit(command);clock.Stop();
                captureCopies=false;
                Assert.That(copyTraceErrors,Is.Zero,"整图复制来源诊断不能悄悄丢失样本");Assert.That(copyStack.Count,Is.Zero);
                var after=session.ExecutionTimings.Snapshot();process.Refresh();
                rows.Add(new {command.CommandId,command.Kind,command.PlayerId,Stage=(string)original["Stage"],BeforeRevision=(int)original["BeforeRevision"],Seconds=clock.Elapsed.TotalSeconds,
                    ManagedDelta=GC.GetTotalMemory(false)-memory,Revision=session.State.EffectRuntime.StateRevision,
                    WorkingSet=process.WorkingSet64,PrivateBytes=process.PrivateMemorySize64,
                    DeltaWorkingSet=process.WorkingSet64-workingSet,DeltaPrivateBytes=process.PrivateMemorySize64-privateBytes,
                    AllocatedBytes=allocationAvailable ? GC.GetAllocatedBytesForCurrentThread()-allocated : (long?)null,
                    StateCopyCallerTracing=copyProfileRevisions.Contains((int)original["BeforeRevision"]),
                    StateCopyCallers=copyCallers.ToDictionary(p=>p.Key,p=>new {p.Value.Calls,Seconds=p.Value.Ticks/(double)Stopwatch.Frequency}),
                    SchedulingPublicationVerification=verifyScheduling,
                    SupplementalPaymentComparisonRequested=compareSupplementalPayments,
                    Calls=after.Where(p=>p.Key.EndsWith(".calls")).ToDictionary(p=>p.Key,p=>p.Value-(before.TryGetValue(p.Key,out long priorCalls)?priorCalls:0)),
                    Timings=after.Where(p=>p.Key.EndsWith(".ticks")).ToDictionary(p=>p.Key,p=>(p.Value-(before.TryGetValue(p.Key,out long prior)?prior:0))/(double)Stopwatch.Frequency),
                    result.Succeeded,Reason=result.Validation?.Reason});
                File.WriteAllText(report,JsonConvert.SerializeObject(new {Scope="同档原命令隔离定位，非正常页面验收",data.GameId,Rows=rows},Formatting.Indented));
                if(session.State.EffectRuntime.Status!=YC.Domain.State.EffectRuntimeStatus.Active)
                {
                    var runtime=session.State.EffectRuntime;
                    var faultNodes=runtime.EffectNodes.Where(n=>n.FailureReason.Length>0 || n.Status==YC.Domain.State.EffectNodeStatus.Faulted).ToArray();
                    var ids=new HashSet<string>(faultNodes.Select(n=>n.EffectId));
                    foreach(var failed in faultNodes)
                        for(var ancestor=failed;ancestor!=null;ancestor=runtime.EffectNodes.Find(n=>n.EffectId==ancestor.ParentEffectId))ids.Add(ancestor.EffectId);
                    File.WriteAllText(report+".fault.json",JsonConvert.SerializeObject(new {runtime.LastFaultCode,runtime.LastFaultMessage,
                        Chain=runtime.EffectNodes.Where(n=>ids.Contains(n.EffectId)||ids.Contains(n.ParentEffectId))},Formatting.Indented));
                }
                Assert.That(result.Succeeded,Is.EqualTo((bool)original["Result"]["CommandResult"]["Succeeded"]),result.Validation?.Reason);
                Assert.That(session.State.EffectRuntime.StateRevision,Is.InRange((int)original["BeforeRevision"],(int)original["AfterRevision"]));
                lastExpected=(int)original["AfterRevision"];
                if(Arg("--yc-replay-verify-view-state-copies")=="true")
                    using(YC.Domain.State.GameStateCloneService.BeginTimingScope(session.ExecutionTimings.AddSample))
                        session.ProjectFor(YC.Domain.State.GameStateViewer.Player(command.PlayerId));
            }
            Assert.That(rows.Count,Is.GreaterThan(0));
            Assert.That(session.State.EffectRuntime.StateRevision,Is.EqualTo(lastExpected));
            Assert.That(session.State.EffectRuntime.Status,Is.EqualTo(YC.Domain.State.EffectRuntimeStatus.Active),session.State.EffectRuntime.LastFaultMessage);
            Assert.That(YC.Domain.State.RuleStateDigestService.Compute(session.State),Is.EqualTo(LegacyRuleStateDigestReference.Compute(session.State)),"重放后完整规则摘要必须兼容");
            if(Arg("--yc-replay-capture-final-state")=="true")capturedReplayState=session.State;
            if (Arg("--yc-replay-require-work-recycling") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.recycled_execution_work.calls", out var recycled) && recycled > 0,
                    Is.True, "本次实际原命令必须覆盖无外部引用的工作图复用，不能仅凭未命中的代码分支宣称验证通过");
            if (Arg("--yc-replay-require-journal-prefix-publication") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("handler.copy.journal_prefix.calls", out var journalPrefixCopies) && journalPrefixCopies > 0,
                    Is.True, "本次实际原命令必须覆盖封闭日志前缀发布，不能由未命中宣称收益");
            if (Arg("--yc-replay-require-resume-recycling") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.recycled_resume_work.calls", out var resumed) && resumed > 0,
                    Is.True, "本次实际原命令必须覆盖父任务恢复工作图复用");
            if (Arg("--yc-replay-require-exact-preview-guard") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("response.preview.identical_fields.calls", out var matched) && matched > 0,
                    Is.True, "本次实际原命令必须命中独立基线的完整字段预检，不能由未命中掩盖额外副本成本");
            if (Arg("--yc-replay-require-status-validation") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.validation.status_delta.calls", out var checkedStatus) && checkedStatus > 0,
                    Is.True, "本次实际原命令必须命中纯状态封闭写集校验");
            if (Arg("--yc-replay-require-round-recycling") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.recycled_executor.round.mainline.node.calls", out var checkedRound) && checkedRound > 0,
                    Is.True, "本次实际原命令必须命中回合主链独占工作图复用");
            if (Arg("--yc-replay-require-unused-query-snapshots") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().Any(p=>p.Key.StartsWith("lua.snapshot.deferred.unused_") && p.Key.EndsWith(".calls") && p.Value>0),
                    Is.True, "实际原命令必须覆盖未读取的Lua全局快照，不能只由未命中推定收益");
            if (Arg("--yc-replay-require-cleanup-obligation-index") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("usage.reset.obligation_index.calls",out var indexed) && indexed>0, Is.True, "实际原命令必须命中收尾义务父链索引");
            if (Arg("--yc-replay-require-nonretained-query") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("ability.query.nonretained.calls",out var pureCalls) && pureCalls>0, Is.True, "实际原命令必须覆盖脱离状态的Lua纯查询合同");
            if (Arg("--yc-replay-require-payment-recycling") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.recycled_executor.effect.resource.pay.calls",out var payCalls) && payCalls>0, Is.True, "实际原命令必须覆盖付款工作图复用");
            if (Arg("--yc-replay-require-influence-recycling") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().Any(p=>p.Key.StartsWith("kernel.recycled_executor.effect.influence.") && p.Key.EndsWith(".calls") && p.Value>0), Is.True, "实际原命令必须覆盖影响力工作图复用");
            if (Arg("--yc-replay-require-fresh-validation-baseline") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.status.baseline_from_fresh_copy.calls",out var freshBaseline) && freshBaseline>0, Is.True, "实际原命令必须覆盖完整复制后的紧邻校验凭据");
            if (Arg("--yc-replay-require-core-flow-recycling") == "true")
                Assert.That(new[]{"round.prepare_cover","character.cover.task","effect.facility.build","effect.facility.pay_build_cost","effect.facility.refill_slot","effect.city.position.commit"}.Any(id=>session.ExecutionTimings.Snapshot().TryGetValue("kernel.recycled_executor."+id+".calls",out var hits) && hits>0), Is.True, "实际原命令必须命中本批内置流程工作图复用，逐类覆盖另按计数记录");
            if (Arg("--yc-replay-require-entity-validation-snapshots") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("entity.validation.reused.calls",out var reusedEntity) && reusedEntity>0,Is.True,"实际原命令必须覆盖独立实体校验快照复用");
            if (Arg("--yc-replay-require-independent-execution-writes") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.independent_execution_writes.calls",out var independent) && independent>0,Is.True,"实际原命令必须覆盖内置独立引用写入合同");
            if (Arg("--yc-replay-require-continuation-recycling") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.recycled_continuation_work.calls",out var continuationWork) && continuationWork>0,Is.True,"实际原命令必须覆盖带续接元数据的内置阶段复用");
            if (Arg("--yc-replay-require-character-recycling") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.recycled_executor.character.use.calls",out var characterRecycling) && characterRecycling>0,Is.True,"实际原命令必须覆盖角色使用工作图复用");
            if (Arg("--yc-replay-require-city-recycling") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.recycled_executor.effect.city.move.calls",out var cityRecycling) && cityRecycling>0,Is.True,"实际原命令必须覆盖移城非回调阶段工作图复用");
            if (Arg("--yc-replay-require-attachment-index") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("attachment.descendant_index.calls",out var attachmentIndex) && attachmentIndex>0,Is.True,"实际原命令必须覆盖映射放置祖先索引");
            if (Arg("--yc-replay-require-canonical-equality") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("normalized.canonical_equal.calls",out var comparedCanonical) && comparedCanonical>0,Is.True,"实际原命令必须覆盖规范值直接比较");
            if (Arg("--yc-replay-require-optional-recycling") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().Any(p=>p.Key.StartsWith("kernel.recycled_optional_executor.") && p.Key.EndsWith(".calls") && p.Value>0),Is.True,"实际原命令必须覆盖可选效果接受后的工作图复用");
            if (Arg("--yc-replay-require-source-body-recycling") == "true")
            {
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("ability.body.nonretained_source.calls",out var bodies) && bodies>0, Is.True, "实际原命令必须覆盖无行动授权策略的来源正文合同");
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.recycled_executor.effect.ability.source.resolve.calls",out var sourceWork) && sourceWork>0, Is.True, "实际原命令必须覆盖来源执行工作图复用");
            }
            if (Arg("--yc-replay-require-status-dependency-delta") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("kernel.validation.status_dependencies_delta.calls",out var dependencyDelta) && dependencyDelta>0, Is.True, "实际原命令必须覆盖闭合纯状态依赖校验");
            if (Arg("--yc-replay-require-readonly-projection") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("projection.borrowed_runtime.calls",out var borrowedViews) && borrowedViews>0, Is.True, "实际原命令之后必须命中只读历史投影并完成完整字段对照");
            // 额外穷举对照独立于原命令重放；热点定位可显式跳过，默认仍执行且报告中记录范围。
            if (Arg("--yc-replay-require-factory-previews") == "true")
                foreach(string detail in new[]{"factory_effect_staging","factory_granted_item","departure_custody"})
                    Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("transfer.structural_preview."+detail+".calls",out var factoryHits) && factoryHits>0,Is.True,"实际原命令必须覆盖新增结构预检："+detail);
            if (Arg("--yc-replay-require-structural-previews") == "true")
                Assert.That(session.ExecutionTimings.Snapshot().TryGetValue("transfer.structural_preview.calls",out var previews) && previews>0, Is.True, "实际原命令必须覆盖完整独立写集的结构预检");
            if (Arg("--yc-replay-require-descendant-index") == "true")
                foreach (string phase in new[]{"cleanup.obligation.descendants","scoring.archive.descendants"})
                    Assert.That(session.ExecutionTimings.Snapshot().TryGetValue(phase+".calls",out var hits) && hits>0, Is.True, "实际原命令必须命中后代索引："+phase);
            if(!compareSupplementalPayments)return;
            var costRegistryMethod=typeof(YC.Domain.Effects.ResourcePaymentSourceService).GetMethod("CostQueryRegistry",BindingFlags.NonPublic|BindingFlags.Static);
            var costRegistry=(YC.Domain.Effects.EffectRegistry)costRegistryMethod.Invoke(null,new object[]{session.State});
            if(costRegistry!=null)
                foreach(var player in session.State.Players)
                foreach(int amount in new[]{1,3,8})
                {
                    var cost=new YC.Domain.State.ResourceSet{Originium=amount,Iron=amount};
                    var owner=new YC.Domain.State.EffectNodeRuntimeState{EffectId="isolated-preview-comparison",EffectTypeId=YC.Domain.Effects.ResourceEffectTypeIds.Pay,PlayerId=player.PlayerId};
                    Assert.That(YC.Domain.Effects.ResourcePaymentCostPreview.CanPay(session.State,costRegistry,owner,cost),
                        Is.EqualTo(LegacyResourcePaymentCostPreview.CanPay(session.State,costRegistry,owner,cost)),"付款搜索剪枝不得改变可支付结果");
                }
        }
    }
}
