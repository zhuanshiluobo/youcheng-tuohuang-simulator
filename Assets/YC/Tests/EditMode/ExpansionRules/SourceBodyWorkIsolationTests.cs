using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离回调生命周期验证，不计作正常界面验收。
    public sealed class SourceBodyWorkIsolationTests
    {
        [TestCase(false)][TestCase(true)]
        public void DecliningTheOptionalGateNeverCallsTheBody(bool detached)
        {
            using var fixture = new PaymentTestFixture();
            int calls=0;
            Func<EffectExecutionContext,EffectStepResult> body=context=>{calls++;context.State.FindPlayer(1).Score+=3;return EffectStepResult.Completed();};
            if(detached)typeof(EffectRegistry).GetMethod("RegisterWithoutStateRetention",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(fixture.Registry,new object[]{"isolated.optional.decline",body,EffectExecutorKind.IntrinsicFlow,"1.0.0"});
            else fixture.Registry.Register("isolated.optional.decline",body,EffectExecutorKind.IntrinsicFlow,"1.0.0");
            fixture.State=GameStateCloneService.DeepClone(fixture.State);
            var engine=fixture.Engine;var root=EffectSpec.Create("isolated.optional.decline",playerId:1);root.AllowDecline=true;root.DecisionPlayerId=1;
            string id=engine.CreateRoot(root);
            using var verification=GameStateCloneService.VerifySchedulingPublicationCopies();
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            var request=fixture.State.EffectRuntime.InteractionRequests.Find(r=>r.InteractionTypeId=="action.decline_effect" && r.Status=="open");
            Assert.That(request,Is.Not.Null);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString("decline"),out var reason),Is.True,reason);
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(calls,Is.Zero);Assert.That(fixture.State.FindPlayer(1).Score,Is.Zero);
            Assert.That(engine.GetNode(id).Status,Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(engine.GetNode(id).FailureReason,Is.EqualTo("player_declined"));
        }
        [TestCase("default", false)][TestCase("detached", false)][TestCase("action", false)]
        [TestCase("default", true)][TestCase("detached", true)][TestCase("action", true)]
        public void SourceContractRecyclesOnlyItsDeclaredScopeAndKeepsOtherCallbacksIsolated(string mode, bool optional)
        {
            using var fixture = new PaymentTestFixture();
            const string handler = "isolated.source.body", hash = "isolated.source.hash", ability = "isolated.source.ability";
            bool detached = mode == "detached";
            var held = new List<GameState>(); int calls = 0, executions = 0, recycledAfterBody = 0;
            Func<AbilityActionCallContext, IList<EffectSpec>> body = call =>
            {
                calls++;
                if (!detached) held.Add(call.State);
                return Array.Empty<EffectSpec>();
            };
            if (mode == "default") fixture.Registry.RegisterAbilityHandler(handler, hash, body);
            else fixture.Registry.RegisterNonRetainedSourceHandler(handler, hash, body);
            var construct = typeof(EffectRegistry).GetMethod("ConstructAbilityBody", BindingFlags.Instance | BindingFlags.NonPublic);
            Func<EffectExecutionContext, EffectStepResult> execute = context =>
            {
                executions++;
                if (executions == 1)
                {
                    construct.Invoke(context.Registry, new object[] { handler, new AbilityActionCallContext
                    {
                        State = context.State, OwnerNode = context.Node,
                        SourceRef = new AbilitySourceRef { AbilityId = ability },
                        Action = mode == "action" ? new AbilityActionDeclaration { ActionId = "isolated.action" } : null
                    } });
                    return EffectStepResult.Continue("body_constructed");
                }
                context.State.FindPlayer(1).Score += 3;
                return EffectStepResult.Completed();
            };
            typeof(EffectRegistry).GetMethod("RegisterWithoutStateRetention", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(fixture.Registry, new object[] { "isolated.source.executor", execute, EffectExecutorKind.IntrinsicFlow, "1.0.0" });
            fixture.State = GameStateCloneService.DeepClone(fixture.State);
            var engine = fixture.Engine;
            var root = EffectSpec.Create("isolated.source.executor", playerId: 1);
            root.AllowDecline = optional; root.DecisionPlayerId = 1;
            engine.CreateRoot(root);
            using (GameStateCloneService.VerifySchedulingPublicationCopies())
            using (GameStateCloneService.BeginTimingScope((phase, ticks) =>
                { if (phase == "kernel.recycled_execution_work" && executions == 1) recycledAfterBody++; }))
                {
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, fixture.State.EffectRuntime.LastFaultMessage);
                if(optional)
                {
                    Assert.That(calls,Is.Zero,"未接受可选门前不能执行正文");
                    var request=fixture.State.EffectRuntime.InteractionRequests.Find(r=>r.InteractionTypeId=="action.decline_effect" && r.Status=="open");
                    Assert.That(request,Is.Not.Null);
                    Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString("continue"),out var reason),Is.True,reason);
                    Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
                }
            }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(executions, Is.EqualTo(2));
            Assert.That(recycledAfterBody > 0, Is.EqualTo(detached));
            Assert.That(held.Count, Is.EqualTo(detached ? 0 : 1));
            foreach (var old in held)
            {
                Assert.That(old.FindPlayer(1).Score, Is.EqualTo(0), "后续执行不能改写普通回调保留的旧工作图");
                old.FindPlayer(1).Score = 999;
            }
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(3));
        }
    }
}
