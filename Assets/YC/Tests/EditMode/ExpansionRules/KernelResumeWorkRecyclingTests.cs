using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class KernelResumeWorkRecyclingTests
    {
        private const string Parent = "isolated.resume.parent", Child = "isolated.resume.child";
        private static GameState RunBuiltIns(bool full,out Dictionary<string,int> recycled)
        {
            var state=new GameState{GameId="isolated.audited.builtins"};state.Players.Add(new PlayerState{PlayerId=1});
            var registry=new EffectRegistry();ResourceEffectExecutor.Register(registry);GenericLuaEffectExecutor.Register(registry);
            var choice=EffectSpec.Create(LuaDomainEffectTypeIds.Choice,ItemTransferEffectExecutor.Object(
                "player",NormalizedValue.CreateString("1"),"options",ItemTransferEffectExecutor.Strings(new[]{"x"})),1);
            choice.NestedEffectGroups.Add(new List<EffectSpec>{ResourceEffectSpecFactory.GainScore(1,5)});
            var root=EffectSpec.Condition(new[]{ResourceEffectSpecFactory.Gain(1,ResourceType.Iron,3)},new[]{choice});
            var counts=new Dictionary<string,int>();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{
                if(phase.StartsWith("kernel.recycled_executor.",StringComparison.Ordinal)) counts[phase]=counts.TryGetValue(phase,out int n)?n+1:1;
            });
            using var verify=GameStateCloneService.VerifySchedulingPublicationCopies();
            using var copies=full?EffectTreeExecutor.UseFullExecutionCopiesForDiagnostics():null;
            var engine=new EffectTreeExecutor(state,registry);engine.CreateRoot(root);
            var first=engine.RunUntilQuiescent();Assert.That(first.Faulted,Is.False,engine.LastDiagnostic);
            Assert.That(first.WaitingForInput,Is.True);
            var request=state.EffectRuntime.InteractionRequests.Find(r=>r.Status=="open");Assert.That(request,Is.Not.Null);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString("x"),out var reason),Is.True,reason);
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,engine.LastDiagnostic);
            recycled=counts;return state;
        }
        [Test]
        public void ActualConditionResourceAndChoiceStepsKeepFullCopiesAndLegalInteractionResults()
        {
            var reference=RunBuiltIns(true,out var oldCounts);var actual=RunBuiltIns(false,out var newCounts);
            Assert.That(oldCounts.Count,Is.Zero);Assert.That(GameStateCloneService.AreEquivalent(actual,reference),Is.True);
            Assert.That(actual.Players[0].Resources.Iron,Is.EqualTo(3));Assert.That(actual.Players[0].Score,Is.EqualTo(5));
            foreach(string type in new[]{EffectTypeIds.Condition,ResourceEffectTypeIds.Gain,LuaDomainEffectTypeIds.Choice})
                Assert.That(newCounts.TryGetValue("kernel.recycled_executor."+type,out int count)&&count>0,Is.True,type);
        }
        private static GameState Run(bool full, bool retain, bool fail, out int recycled, out List<(GameState state, int score)> captured)
        {
            var state = new GameState { GameId = "isolated.resume.work" };
            state.Players.Add(new PlayerState { PlayerId = 1 });
            var registry = new EffectRegistry(); var held = new List<(GameState state, int score)>();
            registry.Register(Parent, c => {
                held.Add((c.State, c.State.Players[0].Score));
                return EffectStepResult.Completed().AddChild(EffectSpec.Create(Child, playerId: 1));
            });
            registry.Register(Child, c => {
                c.State.Players[0].Score++;
                held.Add((c.State, c.State.Players[0].Score));
                return EffectStepResult.Completed();
            });
            if(retain || fail) registry.RegisterEventHandler(new EffectEventHandlerRegistration("resume.retaining", "EffectCompleted", "*", "resume.retaining", c => {
                if(fail) { c.State.Players[0].Score=999; throw new InvalidOperationException("恢复父任务失败"); }
                held.Add((c.State,c.State.Players[0].Score)); return Array.Empty<EffectSpec>();
            }) { EffectTypeFilter = Parent });
            var engine = new EffectTreeExecutor(state,registry);
            for(int i=0;i<(fail?1:3);i++) engine.CreateRoot(EffectSpec.Create(Parent,playerId:1));
            int count=0;
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.recycled_resume_work")count++;}))
            using(GameStateCloneService.VerifySchedulingPublicationCopies())
            using(full?EffectTreeExecutor.UseFullExecutionCopiesForDiagnostics():null)
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.EqualTo(fail));
            recycled=count; captured=held; return state;
        }
        [TestCase(false)] [TestCase(true)]
        public void ClosedKernelResumeKeepsAllPublishedFieldsAndDefaultExecutorSnapshots(bool fail)
        {
            var reference=Run(true,false,fail,out int oldCount,out _);
            var actual=Run(false,false,fail,out int newCount,out var held);
            Assert.That(oldCount,Is.Zero);
            Assert.That(GameStateCloneService.AreEquivalent(actual,reference),Is.True);
            Assert.That(actual.Players[0].Score,Is.EqualTo(fail?1:3));
            if(!fail)Assert.That(newCount,Is.GreaterThan(0));
            foreach(var value in held)Assert.That(value.state.Players[0].Score,Is.EqualTo(value.score));
        }
        [Test]
        public void DefaultCompletionObserversKeepResumeStatesDetached()
        {
            var reference=Run(true,true,false,out _,out _);
            var actual=Run(false,true,false,out int count,out var held);
            Assert.That(count,Is.Zero);
            Assert.That(GameStateCloneService.AreEquivalent(actual,reference),Is.True);
            foreach(var value in held)Assert.That(value.state.Players[0].Score,Is.EqualTo(value.score));
        }
        [Test]
        public void MulticastRoundObserverCannotHideAnEarlierRetainingCallback()
        {
            var state=new GameState{GameId="isolated.resume.multicast"};state.Players.Add(new PlayerState{PlayerId=1});
            var registry=new EffectRegistry();var round=new RoundExecutionService(registry);
            registry.Register(Parent,c=>EffectStepResult.Completed().AddChild(EffectSpec.Create(Child,playerId:1)));
            registry.Register(Child,c=>{c.State.Players[0].Score++;return EffectStepResult.Completed();});
            var held=new List<(GameState state,int score)>();
            Action<GameState,EffectNodeRuntimeState> observer=(work,node)=>held.Add((work,work.Players[0].Score));
            observer+=(Action<GameState,EffectNodeRuntimeState>)Delegate.CreateDelegate(typeof(Action<GameState,EffectNodeRuntimeState>),round,
                typeof(RoundExecutionService).GetMethod("OnEffectTerminal",BindingFlags.Instance|BindingFlags.NonPublic));
            var engine=new EffectTreeExecutor(state,registry,null,observer);
            for(int i=0;i<3;i++)engine.CreateRoot(EffectSpec.Create(Parent,playerId:1));
            int count=0;
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.recycled_resume_work")count++;}))
            using(GameStateCloneService.VerifySchedulingPublicationCopies())
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,engine.LastDiagnostic);
            Assert.That(count,Is.Zero);Assert.That(held.Count,Is.EqualTo(6));
            foreach(var value in held)Assert.That(value.state.Players[0].Score,Is.EqualTo(value.score));
        }
    }
}
