using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class SchedulingValidationTests
    {
        private static (GameState,EffectTreeExecutor) Prepared(bool alias=false,Action<GameState> capture=null)
        {
            var state=new GameState { GameId="isolated.scheduling.validation" };
            state.Players.Add(new PlayerState { PlayerId=1 });state.Players.Add(new PlayerState { PlayerId=2 });
            if(alias)state.Players[1].Resources=state.Players[0].Resources;
            var registry=new EffectRegistry();
            registry.Register("isolated.status.parent",c=>{capture?.Invoke(c.State);return EffectStepResult.Completed().AddChild(EffectSpec.Create("isolated.status.child",playerId:1));});
            registry.Register("isolated.status.child",c=>{c.State.Players[0].Score+=3;return EffectStepResult.Completed();});
            var engine=new EffectTreeExecutor(state,registry);engine.CreateRoot(EffectSpec.Create("isolated.status.parent",playerId:1));
            return(state,engine);
        }
        private static (GameState,int,string) Run(bool full,bool alias=false,Action<GameState> change=null,bool reference=false)
        {
            var pair=Prepared(alias);change?.Invoke(pair.Item1);int count=0;string result;
            using var original=full ? GameStateCloneService.UseFullSchedulingValidationForDiagnostics() : null;
            using var comparison=reference ? GameStateCloneService.VerifySchedulingPublicationCopies() : null;
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.validation.status_delta")count++;});
            try {var report=pair.Item2.RunUntilQuiescent();result=report.FaultCode+"|"+pair.Item1.EffectRuntime.LastFaultMessage;}
            catch(Exception error){result=error.GetType().FullName+"|"+error.Message;}
            return(pair.Item1,count,result);
        }
        [Test]
        public void PureTransitionsActuallyUseDeltaValidationAndMatchEveryFieldOfFullValidation()
        {
            var original=Run(true);var current=Run(false,reference:true);
            Assert.That(current.Item3,Is.EqualTo(original.Item3));
            Assert.That(GameStateCloneService.AreEquivalent(current.Item1,original.Item1),Is.True);
            Assert.That(current.Item1.Players[0].Score,Is.EqualTo(3));Assert.That(original.Item2,Is.Zero);
            Assert.That(current.Item2,Is.GreaterThan(0),"不能用未命中的纯状态分支宣称通过");
        }
        [Test]
        public void AValidatedDetachedPublicationAllowsNextStatusProofWithoutReusingUnknownCallbackWork()
        {
            var expected=Run(true);GameState held=null;var pair=Prepared(capture:value=>held=value);
            bool parentRan=false;int checkedAfterCallback=0;
            using(var verify=GameStateCloneService.VerifySchedulingPublicationCopies())
            using(var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=> {
                if(phase=="executor.isolated.status.parent")parentRan=true;
                if(parentRan && phase=="kernel.validation.status_delta" && pair.Item1.EffectRuntime.EffectNodes.Exists(n=>n.EffectTypeId=="isolated.status.child" && n.Status==EffectNodeStatus.Ready))checkedAfterCallback++;
            })) Assert.That(pair.Item2.RunUntilQuiescent().Faulted,Is.False);
            Assert.That(parentRan,Is.True,"必须实际经过未知父回调");
            Assert.That(checkedAfterCallback,Is.GreaterThan(0));
            Assert.That(GameStateCloneService.AreEquivalent(expected.Item1,pair.Item1),Is.True);
            Assert.That(held,Is.Not.Null);held.Players[0].Score=999;
            Assert.That(pair.Item1.Players[0].Score,Is.EqualTo(3),"未知回调仍持有其独立旧图，不能因输入校验凭据影响后续已发布图");
        }
        [Test]
        public void AliasedPublishedStateKeepsTheOriginalFullValidationPath()
        {
            var original=Run(true,true);var current=Run(false,true);
            Assert.That(current.Item3,Is.EqualTo(original.Item3));Assert.That(current.Item2,Is.Zero);
            Assert.That(GameStateCloneService.AreEquivalent(current.Item1,original.Item1),Is.True);
        }
        [Test]
        public void FirstTransitionCannotReuseValidationAfterMalformedInput()
        {
            var changes=new Action<GameState>[] {
                s=>s.EffectRuntime.EffectNodes[0].NormalizedArguments=new NormalizedValue { Kind=NormalizedValueKind.String,StringValue=null },
                s=>s.EffectRuntime.Journal[0].Payload=new NormalizedValue { Kind=NormalizedValueKind.Integer,IntegerValue=long.MaxValue },
                s=>s.EffectRuntime.OperationWindows=null
            };
            foreach(var change in changes)
            {
                var original=Run(true,change:change);var current=Run(false,change:change);
                Assert.That(current.Item3,Is.EqualTo(original.Item3));Assert.That(current.Item2,Is.Zero);
            }
        }
        [Test]
        public void StepBudgetCannotCarryValidationProofIntoTheNextRun()
        {
            var pair=Prepared();int count=0;
            using(var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.validation.status_delta")count++;}))
                pair.Item2.RunUntilQuiescent(1);
            Assert.That(count,Is.Zero,"首次纯状态提交仍须完整校验");
            // 步数上限可能按原合同形成故障；下一次入口也不能继承上次的私有校验凭据。
            using(var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.validation.status_delta")count++;}))
                pair.Item2.RunUntilQuiescent(1);
            Assert.That(count,Is.Zero);
        }
    }
}
