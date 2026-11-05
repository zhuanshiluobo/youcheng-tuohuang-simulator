using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离发布合同/原完整发布对照，不计正常UI覆盖。
    public sealed class BuiltinFlowPublicationTests
    {
        private static bool Flag(EffectRegistration registration,string name) => (bool)typeof(EffectRegistration).GetProperty(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(registration);
        [TestCase(false)][TestCase(true)]
        public void OnlyNativeFactoriesReceiveTheTwoIndependentContracts(bool full)
        {
            using var mode=full?EffectRegistry.UseFullBuiltinFlowPublicationForDiagnostics():null;
            var registry=new EffectRegistry();Assert.That(registry.TryGet(EffectTypeIds.Condition,out var condition),Is.True);
            Assert.That(Flag(condition,"ExecutionPreservesUniqueReferences"),Is.EqualTo(!full));
            Assert.That(Flag(condition,"ExecutionPreservesJournalPrefix"),Is.EqualTo(!full));
            AllocationEffects.Register(registry);Assert.That(registry.TryGet(AllocationEffects.TypeId,out var allocation),Is.True);
            Assert.That(Flag(allocation,"ExecutionPreservesUniqueReferences"),Is.EqualTo(!full));
            Assert.That(Flag(allocation,"ExecutionPreservesJournalPrefix"),Is.EqualTo(!full));
            var custom=new EffectRegistry();
            typeof(EffectRegistry).GetMethod("RegisterWithoutStateRetention",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(custom,
                new object[]{AllocationEffects.TypeId,new Func<EffectExecutionContext,EffectStepResult>(c=>EffectStepResult.Completed()),EffectExecutorKind.IntrinsicFlow,"v1"});
            AllocationEffects.Register(custom);Assert.That(custom.TryGet(AllocationEffects.TypeId,out var original),Is.True);
            Assert.That(Flag(original,"ExecutionPreservesUniqueReferences"),Is.False);Assert.That(Flag(original,"ExecutionPreservesJournalPrefix"),Is.False);
        }
        [TestCase(false)][TestCase(true)]
        public void ConditionKeepsFullResultsAndPreservesPublicationContractUnderVerification(bool full)
        {
            using var mode=full?EffectRegistry.UseFullBuiltinFlowPublicationForDiagnostics():null;
            using var fixture=new PaymentTestFixture();fixture.State=GameStateCloneService.DeepClone(fixture.State);
            var engine=fixture.Engine;string root=engine.CreateRoot(EffectSpec.Condition(new[]{ResourceEffectSpecFactory.GainScore(1,2)},new[]{ResourceEffectSpecFactory.GainScore(1,3)}));
            using var unique=EffectTreeExecutor.VerifyIndependentExecutionWritesForDiagnostics();
            using var copy=GameStateCloneService.VerifySchedulingPublicationCopies();
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(fixture.State.FindPlayer(1).Score,Is.EqualTo(5));Assert.That(engine.GetNode(root).NormalizedResult.Properties,Is.Not.Empty);
        }
    }
}
