using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class IndependentCallbackAliasTests
    {
        // 不保留工作图的回调仍可在图内引入别名；这不是“保持引用独立”的合同。
        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)][TestCase(true,true)]
        public void NonRetainingObserverMayAliasItsWorkWithoutAliasingTheNextOperation(bool fullCopies, bool builtin)
        {
            using var fixture=new PaymentTestFixture();const string after="isolated.independent.callback.after";
            string rootType=builtin ? ResourceEffectTypeIds.GainScore : "isolated.independent.callback";
            Func<EffectExecutionContext,EffectStepResult> body=c=>EffectStepResult.Completed();
            if (!builtin) typeof(EffectRegistry).GetMethod("RegisterWithIndependentStateWrites",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(fixture.Registry,new object[]{rootType,body,EffectExecutorKind.IntrinsicFlow,"v1"});
            fixture.Registry.Register(after,c=>{c.State.EffectRuntime.Diagnostics[0].Message="after";return EffectStepResult.Completed();});
            fixture.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("isolated.alias.observer","EffectCompleted","*","isolated.alias.handler",c=>
            {
                var diagnostic=new EffectDiagnosticRuntimeState{Code="isolated.callback",Message="before",NodeId=c.OwnerNode.EffectId};
                c.State.EffectRuntime.Diagnostics.Add(diagnostic);c.State.EffectRuntime.Diagnostics.Add(diagnostic);
                return new[]{EffectSpec.Create(after,playerId:1)};
            }){EffectTypeFilter=rootType}.WithNonRetainedStateContract(()=>true));
            fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;
            engine.CreateRoot(builtin ? ResourceEffectSpecFactory.GainScore(1,1) : EffectSpec.Create(rootType,playerId:1));
            using var original=fullCopies?EffectTreeExecutor.UseFullExecutionCopiesForDiagnostics():null;
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            var rows=fixture.State.EffectRuntime.Diagnostics;
            Assert.That(rows.Count,Is.EqualTo(2));Assert.That(rows[0],Is.Not.SameAs(rows[1]));
            Assert.That(rows[0].Message,Is.EqualTo("after"));
            Assert.That(rows[1].Message,Is.EqualTo("before"),"后续操作必须从已发布的独立引用图继续，不能复用回调引入别名的私有图");
        }
    }
}
