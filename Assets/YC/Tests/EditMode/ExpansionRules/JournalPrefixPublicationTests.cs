using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 封闭发布的隔离测试，不计正常对局、页面或P95覆盖。
    public sealed class JournalPrefixPublicationTests
    {
        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)][TestCase(true,true)]
        public void NativeAppendPreservesOriginalEntriesAndStandaloneCopyStillCopiesChangedHistory(bool full,bool verify)
        {
            using var fixture=new PaymentTestFixture();fixture.State=GameStateCloneService.DeepClone(fixture.State);
            var engine=fixture.Engine;engine.CreateRoot(ResourceEffectSpecFactory.GainScore(1,3));
            var original=fixture.State.EffectRuntime.Journal[0];string detail=original.Detail;
            int count=fixture.State.EffectRuntime.Journal.Count,hits=0;
            using var mode=full?GameStateCloneService.UseFullJournalPublicationForDiagnostics():null;
            using var comparison=verify?GameStateCloneService.VerifySchedulingPublicationCopies():null;
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="handler.copy.journal_prefix")hits++;});
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(fixture.State.Players[0].Score,Is.EqualTo(3));Assert.That(fixture.State.EffectRuntime.Journal.Count,Is.GreaterThan(count));
            Assert.That(fixture.State.EffectRuntime.Journal[0],Is.SameAs(original));Assert.That(original.Detail,Is.EqualTo(detail));
            Assert.That(hits>0,Is.EqualTo(!full),"必须实际命中仅追加尾部的发布，完整诊断路径不得命中。");
            var changed=GameStateCloneService.DeepClone(fixture.State);changed.EffectRuntime.Journal[0].Detail="outside_copy";
            GameStateCloneService.CopyTo(fixture.State,changed);
            Assert.That(fixture.State.EffectRuntime.Journal[0].Detail,Is.EqualTo("outside_copy"),"退出封闭发布后，公开CopyTo仍须复制历史的真实修改。");
        }
        [TestCase(false)][TestCase(true)]
        public void OrdinaryStateCallbackCanChangeHistoryAndAlwaysRevokesThePrefixProof(bool retains)
        {
            using var fixture=new PaymentTestFixture();GameState held=null;
            var observer=new EffectEventHandlerRegistration("isolated.journal.callback","EffectCompleted","*","isolated.journal.callback",context=>
            {
                context.State.EffectRuntime.Journal[0].Detail="callback_changed";if(retains)held=context.State;return Array.Empty<EffectSpec>();
            }){EffectTypeFilter=ResourceEffectTypeIds.GainScore};
            if(!retains)observer.WithNonRetainedStateContract(()=>true);fixture.Registry.RegisterEventHandler(observer);
            fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(ResourceEffectSpecFactory.GainScore(1,1));
            int hits=0;using var comparison=GameStateCloneService.VerifySchedulingPublicationCopies();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="handler.copy.journal_prefix")hits++;});
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(hits,Is.Zero);Assert.That(fixture.State.EffectRuntime.Journal[0].Detail,Is.EqualTo("callback_changed"));
            if(held!=null){held.EffectRuntime.Journal[0].Detail="old_work_changed";Assert.That(fixture.State.EffectRuntime.Journal[0].Detail,Is.EqualTo("callback_changed"));}
        }
        [TestCase(false)][TestCase(true)]
        public void CallbackOnAnIndependentPreviewAlsoRevokesProofsForCapturedPublicationTargets(bool nonRetained)
        {
            using var fixture=new PaymentTestFixture();const string body="isolated.journal.preview.body",type="isolated.journal.preview.executor";
            Func<AbilityActionCallContext,IList<EffectSpec>> callback=call=>{fixture.State.EffectRuntime.Journal[0].Detail="captured_target_changed";return Array.Empty<EffectSpec>();};
            if(nonRetained)fixture.Registry.RegisterNonRetainedSourceHandler(body,"isolated.hash",callback);else fixture.Registry.RegisterAbilityHandler(body,"isolated.hash",callback);
            var construct=typeof(EffectRegistry).GetMethod("ConstructAbilityBody",BindingFlags.Instance|BindingFlags.NonPublic);
            var registration=new EffectRegistration(type,context=>
            {
                var preview=GameStateCloneService.DeepClone(context.State);
                construct.Invoke(fixture.Registry,new object[]{body,new AbilityActionCallContext{State=preview,SourceRef=new AbilitySourceRef{AbilityId="isolated.ability"}}});
                return EffectStepResult.Completed();
            },EffectExecutorKind.IntrinsicFlow);
            foreach(string property in new[]{"ExecutionDoesNotRetainState","ExecutionPreservesJournalPrefix"})
                typeof(EffectRegistration).GetProperty(property,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(registration,true);
            fixture.Registry.Register(registration);fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(EffectSpec.Create(type,playerId:1));
            string before=fixture.State.EffectRuntime.Journal[0].Detail;int hits=0;
            using var comparison=GameStateCloneService.VerifySchedulingPublicationCopies();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="handler.copy.journal_prefix")hits++;});
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(hits,Is.Zero);Assert.That(fixture.State.EffectRuntime.Journal[0].Detail,Is.EqualTo(before),"完整发布原本会覆盖回调对另一个目标的未提交修改，不能因回调收到预览而跳过该覆盖。");
        }
        [Test]
        public void SpecValidatorCannotLeaveAChangeInACapturedPublicationTarget()
        {
            using var fixture=new PaymentTestFixture();const string parent="isolated.journal.parent",child="isolated.journal.child";
            fixture.Registry.Register(new EffectRegistration(child,c=>EffectStepResult.Completed(),EffectExecutorKind.IntrinsicFlow)
                {Validator=spec=>{fixture.State.EffectRuntime.Journal[0].Detail="validator_target_changed";return "";}});
            var registration=new EffectRegistration(parent,c=>c.Node.FlowStage.Length==0?EffectStepResult.Continue("children").AddChild(EffectSpec.Create(child,playerId:1)):EffectStepResult.Completed(),EffectExecutorKind.IntrinsicFlow);
            foreach(string property in new[]{"ExecutionDoesNotRetainState","ExecutionPreservesJournalPrefix"})
                typeof(EffectRegistration).GetProperty(property,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(registration,true);
            fixture.Registry.Register(registration);fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(EffectSpec.Create(parent,playerId:1));
            string before=fixture.State.EffectRuntime.Journal[0].Detail;using var comparison=GameStateCloneService.VerifySchedulingPublicationCopies();
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(fixture.State.EffectRuntime.Journal[0].Detail,Is.EqualTo(before));
        }
        [Test]
        public void AnAliasPreservingCallbackStillCannotStandInForAHistoricalWriteProof()
        {
            using var fixture=new PaymentTestFixture();
            var observer=new EffectEventHandlerRegistration("isolated.journal.alias_only","EffectCompleted","*","isolated.journal.alias_only",context=>
            {context.State.EffectRuntime.Journal[0].Detail="alias_safe_history_write";return Array.Empty<EffectSpec>();}){EffectTypeFilter=ResourceEffectTypeIds.GainScore}
                .WithNonRetainedStateContract(()=>true);
            typeof(EffectEventHandlerRegistration).GetProperty("HandlerPreservesUniqueReferences",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(observer,true);
            fixture.Registry.RegisterEventHandler(observer);fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(ResourceEffectSpecFactory.GainScore(1,1));
            int hits=0,independent=0;using var comparison=GameStateCloneService.VerifySchedulingPublicationCopies();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="handler.copy.journal_prefix")hits++;if(phase=="kernel.independent_execution_writes")independent++;});
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(independent,Is.GreaterThan(0));Assert.That(hits,Is.Zero);
            Assert.That(fixture.State.EffectRuntime.Journal[0].Detail,Is.EqualTo("alias_safe_history_write"));
        }
        [Test]
        public void AliasedInputNeverObtainsAClosedPublicationProof()
        {
            using var fixture=new PaymentTestFixture();fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;
            string id=engine.CreateRoot(ResourceEffectSpecFactory.GainScore(1,1));
            var row=new EffectDiagnosticRuntimeState{Code="isolated",Message="alias",NodeId=id};
            fixture.State.EffectRuntime.Diagnostics.Add(row);fixture.State.EffectRuntime.Diagnostics.Add(row);
            int hits=0;using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="handler.copy.journal_prefix")hits++;});
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);Assert.That(hits,Is.Zero);
        }
        [Test]
        public void FullPublicationOracleRejectsAnIncorrectNativePrefixDeclaration()
        {
            using var fixture=new PaymentTestFixture();const string type="isolated.journal.incorrect_contract";
            var registration=new EffectRegistration(type,context=>{context.State.EffectRuntime.Journal[0].Detail="incorrect_old_write";return EffectStepResult.Completed();},EffectExecutorKind.IntrinsicFlow);
            foreach(string property in new[]{"ExecutionDoesNotRetainState","ExecutionPreservesJournalPrefix"})
                typeof(EffectRegistration).GetProperty(property,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(registration,true);
            fixture.Registry.Register(registration);fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(EffectSpec.Create(type,playerId:1));
            using var comparison=GameStateCloneService.VerifySchedulingPublicationCopies();var report=engine.RunUntilQuiescent();
            Assert.That(report.Faulted,Is.True,"诊断完整复制必须发现错误合同留下了旧日志内容。");
            Assert.That(fixture.State.EffectRuntime.LastFaultMessage,Does.Contain("复制"));
        }
    }
}
