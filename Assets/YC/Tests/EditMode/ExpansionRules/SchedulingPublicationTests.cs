using System;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class SchedulingPublicationTests
    {
        [Test]
        public void UnchangedHistoryKeepsReferencesButChangedEqualStringAndDormantFieldsStillPublish()
        {
            var state = new GameState { GameId = "isolated.history.copy" };
            state.Players.Add(new PlayerState { PlayerId = 1 });
            var registry = new EffectRegistry();
            registry.Register("isolated.history.first", context => EffectStepResult.Completed());
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(EffectSpec.Create("isolated.history.first", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            var history = state.EffectRuntime.Journal[0];
            var unchanged = state.EffectRuntime.EffectNodes[0]; var arguments = unchanged.NormalizedArguments;
            string replacement = null; GameState escaped = null;
            registry.Register("isolated.history.second", context =>
            {
                escaped = context.State;
                replacement = new string(context.State.EffectRuntime.Journal[0].Detail.ToCharArray());
                context.State.EffectRuntime.Journal[0].Detail = replacement;
                context.State.EffectRuntime.Journal[0].Payload = NormalizedValue.CreateInteger(7);
                context.State.EffectRuntime.Journal[0].Payload.StringValue = "闲置字段";
                return EffectStepResult.Completed();
            });
            engine.CreateRoot(EffectSpec.Create("isolated.history.second", playerId: 1));
            using (GameStateCloneService.VerifySchedulingPublicationCopies())
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.EffectRuntime.Journal[0], Is.SameAs(history));
            Assert.That(history.Detail, Is.SameAs(replacement));
            Assert.That(history.Payload.IntegerValue, Is.EqualTo(7));
            Assert.That(history.Payload.StringValue, Is.EqualTo("闲置字段"));
            Assert.That(state.EffectRuntime.EffectNodes[0], Is.SameAs(unchanged));
            Assert.That(unchanged.NormalizedArguments, Is.SameAs(arguments));
            escaped.EffectRuntime.Journal[0].Payload.StringValue = "处理器持有旧工作图";
            Assert.That(history.Payload.StringValue, Is.EqualTo("闲置字段"));
        }

        [TestCase(false)] [TestCase(true)]
        public void AliasedNormalizedLeavesAndEmptyListsKeepTheFullPublicationFallback(bool shareLeaf)
        {
            var state=new GameState{GameId="isolated.scheduling.alias"};state.Players.Add(new PlayerState{PlayerId=1});
            var first=NormalizedValue.CreateString("payload");var second=shareLeaf?first:NormalizedValue.CreateString("payload");
            second.Items=first.Items;
            state.EffectRuntime.RuleEvents.Add(new RuleEvent{EventId="event.a",Payload=first});
            state.EffectRuntime.RuleEvents.Add(new RuleEvent{EventId="event.b",Payload=second});
            var registry=new EffectRegistry();registry.Register("isolated.alias.work",context=>{
                context.State.Players[0].Score=5;return EffectStepResult.Completed();});
            var engine=new EffectTreeExecutor(state,registry);engine.CreateRoot(EffectSpec.Create("isolated.alias.work",playerId:1));
            int statusCopies=0,reused=0,uniqueCopies=0;
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>{
                if(phase=="handler.copy.status")statusCopies++;
                if(phase=="kernel.reused_execution_work")reused++;
                if(phase=="handler.copy.unique")uniqueCopies++;
            }))Assert.That(engine.RunUntilQuiescent().Faulted,Is.False);
            Assert.That(statusCopies,Is.Zero);Assert.That(reused,Is.Zero);Assert.That(uniqueCopies,Is.Zero);
            Assert.That(state.Players[0].Score,Is.EqualTo(5));
        }

        [TestCase(false)] [TestCase(true)]
        public void StatusBufferHandoffKeepsPrivateExecutionRollbackAndPublishedReferences(bool fail)
        {
            var state=new GameState{GameId="isolated.scheduling.publication"};
            state.Players.Add(new PlayerState{PlayerId=1});var player=state.Players[0];
            state.Players.Add(new PlayerState{PlayerId=2});var firstResources=player.Resources;var secondResources=state.Players[1].Resources;
            var registry=new EffectRegistry();
            registry.Register("isolated.scheduling.work",context=> {
                Assert.That(context.State,Is.Not.SameAs(state));
                Assert.That(context.State.Players[0],Is.Not.SameAs(player));
                context.State.Players[0].Score=fail?999:5;
                var shared=new ResourceSet{Originium=5};
                context.State.Players[0].Resources=shared;context.State.Players[1].Resources=shared;
                if(fail)throw new InvalidOperationException("隔离处理器失败");
                return EffectStepResult.Completed();
            });
            var engine=new EffectTreeExecutor(state,registry);var id=engine.CreateRoot(EffectSpec.Create("isolated.scheduling.work",playerId:1));
            var node=engine.GetNode(id);var nodes=state.EffectRuntime.EffectNodes;var journal=state.EffectRuntime.Journal;
            int statusCopies=0,reused=0,uniqueCopies=0;
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>{
                if(phase=="handler.copy.status")statusCopies++;
                if(phase=="kernel.reused_execution_work")reused++;
                if(phase=="handler.copy.unique")uniqueCopies++;
            }))
            using(GameStateCloneService.VerifySchedulingPublicationCopies())
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.EqualTo(fail));
            Assert.That(statusCopies,Is.GreaterThan(0));Assert.That(reused,Is.EqualTo(1));
            Assert.That(state.Players[0],Is.SameAs(player));Assert.That(player.Score,Is.EqualTo(fail?0:5));
            Assert.That(player.Resources,Is.SameAs(firstResources));Assert.That(state.Players[1].Resources,Is.SameAs(secondResources));
            Assert.That(firstResources,Is.Not.SameAs(secondResources));
            Assert.That(firstResources.Originium,Is.EqualTo(fail?0:5));Assert.That(secondResources.Originium,Is.EqualTo(fail?0:5));
            Assert.That(state.EffectRuntime.EffectNodes,Is.SameAs(nodes));Assert.That(engine.GetNode(id),Is.SameAs(node));
            Assert.That(state.EffectRuntime.Journal,Is.SameAs(journal));
            Assert.That(uniqueCopies,Is.GreaterThan(0),"成功和故障发布都须经过完整复制的参考对照");
            uniqueCopies=0;
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>{
                if(phase=="handler.copy.unique")uniqueCopies++;
            }))GameStateCloneService.CopyTo(state,GameStateCloneService.DeepClone(state));
            Assert.That(uniqueCopies,Is.Zero,"Run返回或失败后，目标证明不得泄漏到后续复制");
        }
    }
}
