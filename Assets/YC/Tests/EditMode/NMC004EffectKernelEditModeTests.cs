using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class NMC004EffectKernelEditModeTests
    {
        [TestCase(EffectNodeStatus.Completed)]
        [TestCase(EffectNodeStatus.Failed)]
        public void ParentChildOutcomeMatrix_DoesNotPropagateBusinessFailure(
            EffectNodeStatus childStatus)
        {
            var registry = new EffectRegistry();
            registry.Register("test.parent", context =>
            {
                var result = EffectStepResult.Completed();
                result.AddChild(EffectSpec.Create(childStatus == EffectNodeStatus.Failed
                    ? "test.failed"
                    : "test.completed"));
                return result;
            });
            registry.Register("test.completed", context => EffectStepResult.Completed());
            registry.Register("test.failed", context => EffectStepResult.Failed("business_rule_failed"));

            GameState state = NewState("matrix-" + childStatus);
            var executor = new EffectTreeExecutor(state, registry);
            string parentId = executor.CreateRoot(EffectSpec.Create("test.parent"));

            EffectRunReport report = executor.RunUntilQuiescent();

            Assert.That(report.Faulted, Is.False);
            Assert.That(executor.GetNode(parentId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
        }

        [Test]
        public void Condition_LeftFailureStopsLeftAndSkipsRight()
        {
            var registry = new EffectRegistry();
            var executed = new List<string>();
            registry.Register("test.left.failed", context =>
            {
                executed.Add("left-1");
                return EffectStepResult.Failed("not_enough_resource");
            });
            registry.Register("test.left.uncreated", context =>
            {
                executed.Add("left-2");
                return EffectStepResult.Completed();
            });
            registry.Register("test.right", context =>
            {
                executed.Add("right");
                return EffectStepResult.Completed();
            });

            GameState state = NewState("condition-left-failure");
            var executor = new EffectTreeExecutor(state, registry);
            string conditionId = executor.CreateRoot(EffectSpec.Condition(
                new List<EffectSpec>
                {
                    EffectSpec.Create("test.left.failed"),
                    EffectSpec.Create("test.left.uncreated")
                },
                new List<EffectSpec> { EffectSpec.Create("test.right") }));

            executor.RunUntilQuiescent();

            EffectNodeRuntimeState condition = executor.GetNode(conditionId);
            Assert.That(condition.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(executed, Is.EqualTo(new[] { "left-1" }));
            Assert.That(condition.NormalizedResult.ToDeterministicString(), Does.Contain("condition_not_met"));
        }

        [Test]
        public void Condition_RightFailureStillCompletesAsConditionMet()
        {
            var registry = new EffectRegistry();
            registry.Register("test.left", context => EffectStepResult.Completed());
            registry.Register("test.right.failed", context => EffectStepResult.Failed("optional_effect_unavailable"));

            GameState state = NewState("condition-right-failure");
            var executor = new EffectTreeExecutor(state, registry);
            string conditionId = executor.CreateRoot(EffectSpec.Condition(
                new List<EffectSpec> { EffectSpec.Create("test.left") },
                new List<EffectSpec> { EffectSpec.Create("test.right.failed") }));

            executor.RunUntilQuiescent();

            Assert.That(executor.GetNode(conditionId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(executor.GetNode(conditionId).NormalizedResult.ToDeterministicString(), Does.Contain("condition_met"));
        }

        [Test]
        public void EventHandlers_AreSortedAndReceiptIsIdempotentAcrossRecovery()
        {
            var registry = new EffectRegistry();
            var order = new List<string>();
            registry.Register("test.root", context => EffectStepResult.Completed().AddEvent(new EffectEventRequest
            {
                EventType = "TestEvent",
                RouteKey = "target.route",
                SemanticKey = "one"
            }));
            registry.Register("test.response.wait", context =>
            {
                if (context.GetLatestInteractionAnswer() != null)
                {
                    return EffectStepResult.Completed();
                }

                var result = new EffectStepResult();
                result.AddInteraction(new EffectInteractionSpec
                {
                    InteractionTypeId = "test.wait",
                    AnsweringPlayerId = 1,
                    AnswerSchema = "boolean"
                });
                return result;
            });

            registry.RegisterEventHandler(new EffectEventHandlerRegistration(
                "observer.z",
                "TestEvent",
                "*",
                "handler.z",
                context =>
                {
                    order.Add("observer-z");
                    return new List<EffectSpec>();
                },
                EffectHandlerRole.Observer,
                0,
                "content.z",
                "ability.z"));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration(
                "primary",
                "TestEvent",
                "target.route",
                "handler.primary",
                context =>
                {
                    order.Add("primary");
                    return new List<EffectSpec>();
                },
                EffectHandlerRole.Primary,
                9,
                "content.primary",
                "ability.primary"));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration(
                "observer.a",
                "EffectCompleted",
                "*",
                "handler.response",
                context =>
                {
                    if (context.OwnerNode.EffectTypeId != "test.root") return new List<EffectSpec>();
                    return new List<EffectSpec> { EffectSpec.Create("test.response.wait") };
                },
                EffectHandlerRole.Observer,
                0,
                "content.a",
                "ability.a"));

            GameState state = NewState("event-idempotence");
            var executor = new EffectTreeExecutor(state, registry);
            string rootId = executor.CreateRoot(EffectSpec.Create("test.root"));
            EffectRunReport first = executor.RunUntilQuiescent();

            Assert.That(first.WaitingForInput, Is.True);
            Assert.That(order, Is.EqualTo(new[] { "primary", "observer-z" }));
            Assert.That(state.EffectRuntime.DispatchReceipts, Has.Count.EqualTo(3));

            GameState recoveredState = GameStateCloneService.DeepClone(state);
            var recovered = new EffectTreeExecutor(recoveredState, registry);
            recovered.RunUntilQuiescent();

            Assert.That(order, Has.Count.EqualTo(2));
            InteractionRequest request = recoveredState.EffectRuntime.InteractionRequests.Find(candidate => candidate.Status == "open");
            Assert.That(request, Is.Not.Null);
            string diagnostic;
            Assert.That(recovered.TrySubmitInteraction(request.RequestId, 1, NormalizedValue.CreateBoolean(true), out diagnostic), Is.True, diagnostic);
            recovered.RunUntilQuiescent();
            Assert.That(recovered.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(recoveredState.EffectRuntime.DispatchReceipts, Has.Count.EqualTo(4));
        }

        [Test]
        public void DeclineBeforeConditionCreatesNoChildrenAndFailsCondition()
        {
            var registry = new EffectRegistry();
            registry.Register("test.left", context => EffectStepResult.Completed());
            GameState state = NewState("condition-decline");
            var executor = new EffectTreeExecutor(state, registry);
            string conditionId = executor.CreateRoot(EffectSpec.Condition(
                new List<EffectSpec> { EffectSpec.Create("test.left") },
                new List<EffectSpec> { EffectSpec.Create("test.left") },
                true,
                1));

            string diagnostic;
            Assert.That(executor.TryDeclineEffect(1, conditionId, out diagnostic), Is.True, diagnostic);

            Assert.That(executor.GetNode(conditionId).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(executor.GetNode(conditionId).ChildEffectIds, Is.Empty);
        }

        [Test]
        public void BlockerCycle_IsRejectedAndPausesRuntime()
        {
            var registry = new EffectRegistry();
            registry.Register("test.wait", context =>
            {
                var result = new EffectStepResult();
                result.AddInteraction(new EffectInteractionSpec { InteractionTypeId = "wait" });
                return result;
            });

            GameState state = NewState("blocker-cycle");
            var executor = new EffectTreeExecutor(state, registry);
            string first = executor.CreateRoot(EffectSpec.Create("test.wait"));
            string second = executor.CreateRoot(EffectSpec.Create("test.wait"));
            string blockerId;
            Assert.That(executor.TryAddBlocker(first, second, "dependency", "test", out blockerId), Is.True);
            Assert.That(executor.TryAddBlocker(second, first, "dependency", "test", out blockerId), Is.False);

            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(state.EffectRuntime.LastFaultCode, Is.EqualTo(EffectFaultCodes.BlockerCycle));
        }

        [Test]
        public void HandlerNoProgress_EntersPausedFault()
        {
            var registry = new EffectRegistry();
            registry.Register("test.stuck", context => EffectStepResult.Continue());
            GameState state = NewState("no-progress");
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(EffectSpec.Create("test.stuck"));

            executor.RunUntilQuiescent();

            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(state.EffectRuntime.LastFaultCode, Is.EqualTo(EffectFaultCodes.NoProgress));
        }

        [Test]
        public void Limits_EnterPausedFaultInsteadOfSkippingNode()
        {
            var registry = new EffectRegistry();
            registry.Register("test.parent", context => EffectStepResult.Completed().AddChild(EffectSpec.Create("test.child")));
            registry.Register("test.child", context => EffectStepResult.Completed());
            GameState state = NewState("node-limit");
            var executor = new EffectTreeExecutor(
                state,
                registry,
                new EffectRuntimeLimits(100, 8, 1, 16, 16));
            executor.CreateRoot(EffectSpec.Create("test.parent"));

            executor.RunUntilQuiescent();

            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(state.EffectRuntime.LastFaultCode, Is.EqualTo(EffectFaultCodes.NodeLimitExceeded));
        }

        [TestCase(EffectFaultCodes.StepLimitExceeded)]
        [TestCase(EffectFaultCodes.TreeDepthExceeded)]
        [TestCase(EffectFaultCodes.EventLimitExceeded)]
        [TestCase(EffectFaultCodes.DispatchLimitExceeded)]
        public void RuntimeLimits_AreFailStopAndDiagnosable(string expectedFaultCode)
        {
            var registry = new EffectRegistry();
            registry.Register("test.leaf", context => EffectStepResult.Completed());
            registry.Register("test.child-producing", context =>
                EffectStepResult.Completed().AddChild(EffectSpec.Create("test.leaf")));
            registry.Register("test.event-producing", context => EffectStepResult.Completed().AddEvent(new EffectEventRequest
            {
                EventType = "LimitEvent",
                SemanticKey = "limit"
            }));

            if (expectedFaultCode == EffectFaultCodes.DispatchLimitExceeded)
            {
                registry.RegisterEventHandler(new EffectEventHandlerRegistration(
                    "limit.handler.a", "LimitEvent", "", "limit.a",
                    context => new List<EffectSpec>()));
                registry.RegisterEventHandler(new EffectEventHandlerRegistration(
                    "limit.handler.b", "LimitEvent", "", "limit.b",
                    context => new List<EffectSpec>()));
            }

            int maxSteps = expectedFaultCode == EffectFaultCodes.StepLimitExceeded ? 1 : 100;
            int maxDepth = expectedFaultCode == EffectFaultCodes.TreeDepthExceeded ? 1 : 8;
            int maxEvents = expectedFaultCode == EffectFaultCodes.EventLimitExceeded ? 1 : 16;
            int maxDispatches = expectedFaultCode == EffectFaultCodes.DispatchLimitExceeded ? 1 : 16;
            EffectSpec rootSpec;
            if (expectedFaultCode == EffectFaultCodes.TreeDepthExceeded)
            {
                rootSpec = EffectSpec.Create("test.child-producing");
            }
            else if (expectedFaultCode == EffectFaultCodes.EventLimitExceeded ||
                     expectedFaultCode == EffectFaultCodes.DispatchLimitExceeded)
            {
                rootSpec = EffectSpec.Create("test.event-producing");
            }
            else
            {
                rootSpec = EffectSpec.Create("test.leaf");
            }

            GameState state = NewState("limit-" + expectedFaultCode);
            var executor = new EffectTreeExecutor(
                state,
                registry,
                new EffectRuntimeLimits(maxSteps, maxDepth, 16, maxEvents, maxDispatches));
            executor.CreateRoot(rootSpec);
            executor.RunUntilQuiescent();

            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(state.EffectRuntime.LastFaultCode, Is.EqualTo(expectedFaultCode));
        }

        private static GameState NewState(string gameId)
        {
            return new GameState
            {
                GameId = gameId,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } }
            };
        }
    }
}
