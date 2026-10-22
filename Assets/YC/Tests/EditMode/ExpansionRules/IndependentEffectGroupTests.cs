using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class IndependentEffectGroupTests
    {
        private const string ParentType = "test.independent_group.parent";
        private const string WaitType = "test.independent_group.wait";
        private const string DoneType = "test.independent_group.done";

        private static GameState State()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            return new ItemZoneStateFactory().CreateForSetup(new GameState
            {
                GameId = "independent-group", MapId = map.MapId, ContentPackHash = "independent-group-content",
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "independent-group-content" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } }
            }, map, new List<ItemRecipeEntry>());
        }

        private static EffectSpec Spec(string type, string name) => new EffectSpec(type) { SourceId = name, PlayerId = 1 };
        private static EffectSpec Wait(string name) => Spec(WaitType, name);
        private static EffectSpec Done(string name) => Spec(DoneType, name);
        private static EffectNodeRuntimeState Node(GameState state, string name) => state.EffectRuntime.EffectNodes.Single(node => node.SourceId == name);
        private static string[] OpenNames(GameState state) => state.EffectRuntime.InteractionRequests.Where(request => request.Status == "open")
            .Select(request => state.EffectRuntime.EffectNodes.Single(node => node.EffectId == request.OwnerEffectId).SourceId).ToArray();

        private static EffectRegistry Registry(Func<EffectExecutionContext, EffectStepResult> parent = null)
        {
            var registry = new EffectRegistry();
            registry.Register(WaitType, context =>
            {
                var answer = context.GetLatestInteractionAnswer();
                if (answer != null) return answer.BooleanValue ? EffectStepResult.Completed() : EffectStepResult.Failed("declined");
                return EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec
                { InteractionTypeId = WaitType, AnsweringPlayerId = 1, Visibility = "owner", AnswerSchema = "boolean", AllowDecline = true });
            });
            registry.Register(DoneType, context => EffectStepResult.Completed());
            registry.Register(ParentType, parent ?? (context => EffectStepResult.Completed()
                .AddChild(Wait("A"))
                .AddIndependentChildren("members", new[] { Wait("B"), Wait("C") })
                .AddChild(Done("D"))));
            return registry;
        }

        private static EffectTreeExecutor Engine(GameState state, EffectRegistry registry) => new EffectTreeExecutor(state, registry);
        private static void Run(GameState state, EffectRegistry registry)
        {
            var report = Engine(state, registry).RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }

        private static void Start(GameState state, EffectRegistry registry)
        {
            Engine(state, registry).CreateRoot(Spec(ParentType, "parent"));
            Run(state, registry);
            PublishInteractionRevisions(state);
        }

        // 与 Round 的开放请求发布边界一致：通用内核不自动改写交互的乐观锁版本。
        // 不改候选、答案、节点状态或预算；正式采集还由 Round 同步 routeSelection 的版本字段。
        private static void PublishInteractionRevisions(GameState state)
        {
            foreach (var request in state.EffectRuntime.InteractionRequests.Where(value => value.Status == "open"))
                request.StateRevision = state.EffectRuntime.StateRevision;
        }

        private static void Answer(GameState state, EffectRegistry registry, string name, bool accepted = true)
        {
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open" && value.OwnerEffectId == Node(state, name).EffectId);
            Assert.That(Engine(state, registry).TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateBoolean(accepted), out var diagnostic), Is.True, diagnostic);
            Run(state, registry);
            PublishInteractionRevisions(state);
        }

        private static GameState Restore(GameState state, EffectRegistry registry)
        {
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            return restored.State;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SequentialBarriersAndIndependentAnswersSurviveRecoveryAtEveryBoundary(bool failB)
        {
            var state = State(); var registry = Registry(); Start(state, registry);
            Assert.That(OpenNames(state), Is.EqualTo(new[] { "A" }));
            Assert.That(Node(state, "B").Status, Is.EqualTo(EffectNodeStatus.Created));
            string group = JsonConvert.SerializeObject(Node(state, "parent").ChildSchedulingGroups);
            state = Restore(state, registry); Answer(state, registry, "A");
            Assert.That(OpenNames(state), Is.EquivalentTo(new[] { "B", "C" }));
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Created));
            state = Restore(state, registry); Answer(state, registry, "C");
            Assert.That(Node(state, "C").Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Created));
            Assert.That(Node(state, "parent").Status, Is.EqualTo(EffectNodeStatus.Blocked));
            state = Restore(state, registry); Answer(state, registry, "B", !failB);
            Assert.That(Node(state, "B").Status, Is.EqualTo(failB ? EffectNodeStatus.Failed : EffectNodeStatus.Completed));
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Node(state, "parent").Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(JsonConvert.SerializeObject(Node(state, "parent").ChildSchedulingGroups), Is.EqualTo(group));
            int eventCount = state.EffectRuntime.RuleEvents.Count, nodeCount = state.EffectRuntime.EffectNodes.Count;
            state = Restore(state, registry); string before = JsonConvert.SerializeObject(state); Run(state, registry);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(state.EffectRuntime.RuleEvents.Count, Is.EqualTo(eventCount));
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(nodeCount));
        }

        [Test]
        public void CreatedAndReadyGroupMembersRestoreWithoutReconstructingTheGroup()
        {
            var state = State(); var registry = Registry(context => EffectStepResult.Completed()
                .AddIndependentChildren("members", new[] { Wait("B"), Wait("C") }).AddChild(Done("D")));
            var engine = Engine(state, registry); engine.CreateRoot(Spec(ParentType, "parent"));
            while (state.EffectRuntime.EffectNodes.Count == 1) Assert.That(engine.Advance(), Is.True);
            Assert.That(Node(state, "B").Status, Is.EqualTo(EffectNodeStatus.Ready));
            Assert.That(Node(state, "C").Status, Is.EqualTo(EffectNodeStatus.Ready));
            Node(state, "C").Status = EffectNodeStatus.Created;
            state = Restore(state, registry); Run(state, registry);
            Assert.That(OpenNames(state), Is.EquivalentTo(new[] { "B", "C" }));
            string before = JsonConvert.SerializeObject(state);
            Run(state, registry); Run(state, registry);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [Test]
        public void CompletionResponsesRemainSequentialAndHoldTheFollowingBarrier()
        {
            var state = State(); var registry = Registry();
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("completion", "EffectCompleted", "*", "completion",
                context => context.OwnerNode.SourceId == "B" ? new[] { Wait("response-first"), Wait("response-second") } : new EffectSpec[0]));
            Start(state, registry); Answer(state, registry, "A"); Answer(state, registry, "B");
            Assert.That(OpenNames(state), Is.EquivalentTo(new[] { "C", "response-first" }));
            Assert.That(Node(state, "B").Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(Node(state, "B").ChildSchedulingGroups, Is.Empty);
            Answer(state, registry, "C"); state = Restore(state, registry);
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Created));
            Answer(state, registry, "response-first");
            Assert.That(OpenNames(state), Is.EqualTo(new[] { "response-second" }));
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Created));
            Answer(state, registry, "response-second");
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "EffectCompleted" && e.SourceEffectId == Node(state, "B").EffectId), Is.EqualTo(1));
        }

        [Test]
        public void SameResultEventResponsesFollowTheWholeGroupAndKeepTheirOwnOrder()
        {
            var state = State(); var registry = Registry(context => EffectStepResult.Completed()
                .AddIndependentChildren("members", new[] { Wait("B"), Wait("C") })
                .AddEvent(new EffectEventRequest { EventType = "group.signal" }));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("signal", "group.signal", "*", "signal",
                context => new[] { Wait("first"), Wait("second") }));
            Start(state, registry); Assert.That(OpenNames(state), Is.EquivalentTo(new[] { "B", "C" }));
            Answer(state, registry, "C"); Answer(state, registry, "B");
            Assert.That(OpenNames(state), Is.EqualTo(new[] { "first" }));
            state = Restore(state, registry); Answer(state, registry, "first");
            Assert.That(OpenNames(state), Is.EqualTo(new[] { "second" }));
            Assert.That(Node(state, "parent").Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Answer(state, registry, "second"); Assert.That(Node(state, "parent").Status, Is.EqualTo(EffectNodeStatus.Completed));
        }

        [Test]
        public void FaultedMemberNeverUnlocksFollowingSiblingOrParent()
        {
            var state = State(); var registry = Registry(context => EffectStepResult.Completed()
                .AddIndependentChildren("members", new[] { Wait("B"), Spec("test.independent_group.fault", "C") })
                .AddChild(Done("D")));
            registry.Register("test.independent_group.fault", context => throw new InvalidOperationException("expected_fault"));
            Engine(state, registry).CreateRoot(Spec(ParentType, "parent"));
            Assert.That(Engine(state, registry).RunUntilQuiescent().Faulted, Is.True);
            Assert.That(Node(state, "C").Status, Is.EqualTo(EffectNodeStatus.Faulted));
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Created));
            Assert.That(Node(state, "parent").Status, Is.EqualTo(EffectNodeStatus.Faulted));
            Assert.That(state.EffectRuntime.Blockers.Single(b => b.TargetEffectId == Node(state, "C").EffectId).IsResolved, Is.False);
            state = Restore(state, registry);
            Assert.That(Engine(state, registry).Advance(), Is.False);
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Created));
        }

        [Test]
        public void MissingLegacyGroupFieldsAndNestedSpecsRemainSequential()
        {
            var state = State(); var registry = Registry(context => EffectStepResult.Completed().AddChildren(new[] { Wait("B"), Wait("C") }));
            var parent = Spec(ParentType, "parent");
            parent.Children.Add(Wait("nested-one")); parent.Children.Add(Wait("nested-two"));
            Engine(state, registry).CreateRoot(parent); Run(state, registry);
            var serialized = Node(state, "parent").NestedEffects.Select(spec => spec.Clone()).ToList();
            Assert.That(serialized.Select(spec => spec.SourceId), Is.EqualTo(new[] { "nested-one", "nested-two" }));
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            foreach (var property in archive.Descendants().OfType<JProperty>().Where(property => property.Name == "ChildSchedulingGroups").ToList()) property.Remove();
            var restored = HostRecoveryService.RecoverJson(archive.ToString(), registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State;
            Assert.That(OpenNames(state), Is.EqualTo(new[] { "B" }));
            Assert.That(state.EffectRuntime.EffectNodes.All(node => node.ChildSchedulingGroups.Count == 0), Is.True);
            Answer(state, registry, "B"); Assert.That(OpenNames(state), Is.EqualTo(new[] { "C" }));
        }

        [TestCase("mode")]
        [TestCase("version")]
        [TestCase("owner")]
        [TestCase("id")]
        [TestCase("noncontiguous")]
        [TestCase("duplicate")]
        [TestCase("crossparent")]
        [TestCase("ready")]
        [TestCase("running")]
        [TestCase("resolved")]
        [TestCase("parent_terminal")]
        public void CorruptGroupStateIsRejectedByRuntimeAndHostRecovery(string corruption)
        {
            var state = State(); var registry = Registry(); Start(state, registry);
            var archive = HostRecoveryService.CreateArchive(state);
            var parent = Node(state, "parent"); var group = parent.ChildSchedulingGroups.Single();
            switch (corruption)
            {
                case "mode": group.Mode = (EffectChildSchedulingMode)99; break;
                case "version": group.ProtocolVersion = 99; break;
                case "owner": group.OwnerEffectId = Node(state, "A").EffectId; break;
                case "id": group.GroupId = "wrong"; break;
                case "noncontiguous": group.ChildEffectIds[1] = Node(state, "D").EffectId; break;
                case "duplicate": parent.ChildSchedulingGroups.Add(GameStateCloneService.DeepClone(group)); break;
                case "crossparent": Node(state, "C").ParentEffectId = Node(state, "A").EffectId; break;
                case "ready": Node(state, "B").Status = EffectNodeStatus.Ready; break;
                case "running": Node(state, "D").Status = EffectNodeStatus.Running; break;
                case "resolved": state.EffectRuntime.Blockers.Single(b => b.TargetEffectId == Node(state, "B").EffectId).IsResolved = true; break;
                case "parent_terminal": parent.Status = EffectNodeStatus.Completed; break;
            }
            Assert.That(state.EffectRuntime.TryValidate(out var reason), Is.False, corruption);
            Assert.That(reason, Is.Not.Empty);
            Assert.Throws<InvalidOperationException>(() => HostRecoveryService.CreateArchive(state));
            archive.Snapshot.State = state;
            var recovery = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry);
            Assert.That(recovery.Succeeded, Is.False, corruption);
        }

        [Test]
        public void ReusingGroupKeyInALaterResultFaultsWithoutAppendingMembers()
        {
            var state = State(); var registry = Registry(context => context.Node.FlowStage == "first"
                ? EffectStepResult.Completed().AddIndependentChildren("members", new[] { Done("second") })
                : EffectStepResult.Continue("first").AddIndependentChildren("members", new[] { Done("first") }));
            Engine(state, registry).CreateRoot(Spec(ParentType, "parent"));
            Assert.That(Engine(state, registry).RunUntilQuiescent().Faulted, Is.True);
            Assert.That(state.EffectRuntime.LastFaultCode, Is.EqualTo(EffectFaultCodes.InvalidHandlerResult));
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.SourceId == "second"), Is.False);
            Assert.That(Node(state, "parent").ChildSchedulingGroups.Count, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MutatingResultMembersIsRejectedBeforeAnyChildCommit(bool clear)
        {
            var state = State(); var registry = Registry(context =>
            {
                var result = EffectStepResult.Completed().AddIndependentChildren("members", new[] { Done("B"), Done("C") });
                if (clear) result.ChildEffects.Clear(); else result.ChildEffects.Reverse();
                return result;
            });
            Engine(state, registry).CreateRoot(Spec(ParentType, "parent"));
            Assert.That(Engine(state, registry).RunUntilQuiescent().Faulted, Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(1));
        }

        [Test]
        public void BuilderRejectsInvalidInputAndCopiesMembershipWithoutMutatingSpecs()
        {
            var result = EffectStepResult.Completed(); var specs = new List<EffectSpec> { Wait("B"), Wait("C") };
            string before = JsonConvert.SerializeObject(specs);
            Assert.That(result.AddIndependentChildren("members", specs), Is.SameAs(result));
            Assert.That(JsonConvert.SerializeObject(specs), Is.EqualTo(before));
            specs.Clear(); Assert.That(result.ChildEffects.Count, Is.EqualTo(2));
            Assert.Throws<ArgumentException>(() => result.AddIndependentChildren("members", new[] { Done("D") }));
            Assert.Throws<ArgumentException>(() => result.AddIndependentChildren(" ", new[] { Done("D") }));
            Assert.Throws<ArgumentException>(() => result.AddIndependentChildren("empty", new EffectSpec[0]));
            Assert.Throws<ArgumentException>(() => result.AddIndependentChildren("null", null));
            Assert.Throws<ArgumentException>(() => result.AddIndependentChildren("missing", new EffectSpec[] { null }));
        }

        [Test]
        public void AdjacentGroupsWaitForAllMembersOfThePreviousGroup()
        {
            var state = State(); var registry = Registry(context => EffectStepResult.Completed()
                .AddIndependentChildren("first", new[] { Wait("B"), Wait("C") })
                .AddIndependentChildren("second", new[] { Wait("E"), Wait("F") }).AddChild(Done("D")));
            Start(state, registry); Answer(state, registry, "C");
            Assert.That(OpenNames(state), Is.EqualTo(new[] { "B" }));
            state = Restore(state, registry); Answer(state, registry, "B");
            Assert.That(OpenNames(state), Is.EquivalentTo(new[] { "E", "F" }));
            Answer(state, registry, "F"); Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Created));
            Answer(state, registry, "E"); Assert.That(Node(state, "parent").Status, Is.EqualTo(EffectNodeStatus.Completed));
        }

        [Test]
        public void IndependentMembershipDoesNotPropagateIntoNestedConditionSpecs()
        {
            var state = State(); var condition = EffectSpec.Condition(new[] { Wait("nested-first"), Wait("nested-second") }, new[] { Done("nested-last") });
            condition.SourceId = "condition"; condition.PlayerId = 1;
            var registry = Registry(context => EffectStepResult.Completed()
                .AddIndependentChildren("members", new[] { condition, Wait("C") }).AddChild(Done("D")));
            var engine = Engine(state, registry); engine.CreateRoot(Spec(ParentType, "parent"));
            while (state.EffectRuntime.EffectNodes.Count == 1) Assert.That(engine.Advance(), Is.True);
            state = Restore(state, registry); Run(state, registry); PublishInteractionRevisions(state);
            Assert.That(OpenNames(state), Is.EquivalentTo(new[] { "nested-first", "C" }));
            Assert.That(Node(state, "condition").ChildSchedulingGroups, Is.Empty);
            Answer(state, registry, "C"); Answer(state, registry, "nested-first");
            Assert.That(OpenNames(state), Is.EqualTo(new[] { "nested-second" }));
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Created));
            state = Restore(state, registry); Answer(state, registry, "nested-second");
            Assert.That(Node(state, "nested-last").Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Node(state, "D").Status, Is.EqualTo(EffectNodeStatus.Completed));
        }

        [Test]
        public void GroupKeysAreScopedToTheirOwningParentAndClonedWithoutAliasing()
        {
            var state = State(); var registry = Registry(context => EffectStepResult.Completed()
                .AddIndependentChildren("same-key", new[] { Wait(context.Node.SourceId + ".child") }));
            Engine(state, registry).CreateRoot(Spec(ParentType, "first"));
            Engine(state, registry).CreateRoot(Spec(ParentType, "second")); Run(state, registry);
            var first = Node(state, "first").ChildSchedulingGroups.Single();
            var second = Node(state, "second").ChildSchedulingGroups.Single();
            Assert.That(first.GroupId, Is.Not.EqualTo(second.GroupId));
            var clone = GameStateCloneService.DeepClone(state);
            var cloned = Node(clone, "first").ChildSchedulingGroups.Single();
            Assert.That(JsonConvert.SerializeObject(cloned), Is.EqualTo(JsonConvert.SerializeObject(first)));
            Assert.That(cloned, Is.Not.SameAs(first)); Assert.That(cloned.ChildEffectIds, Is.Not.SameAs(first.ChildEffectIds));
            state = Restore(state, registry); Assert.That(OpenNames(state), Is.EquivalentTo(new[] { "first.child", "second.child" }));
        }

        [Test]
        public void IndependentSchedulingDoesNotBypassInteractionRevisionChecks()
        {
            var state = State(); var registry = Registry(context => EffectStepResult.Completed()
                .AddIndependentChildren("members", new[] { Wait("B"), Wait("C") }));
            Engine(state, registry).CreateRoot(Spec(ParentType, "parent")); Run(state, registry);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.OwnerEffectId == Node(state, "B").EffectId);
            Assert.That(request.StateRevision, Is.LessThan(state.EffectRuntime.StateRevision));
            string before = JsonConvert.SerializeObject(state);
            Assert.That(Engine(state, registry).TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateBoolean(true), out var diagnostic), Is.False);
            Assert.That(diagnostic, Does.Contain("过期")); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            PublishInteractionRevisions(state); Answer(state, registry, "B");
            Assert.That(OpenNames(state), Is.EqualTo(new[] { "C" }));
        }
    }
}
