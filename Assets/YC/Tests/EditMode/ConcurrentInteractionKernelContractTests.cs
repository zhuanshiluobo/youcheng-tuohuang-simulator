using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ConcurrentInteractionKernelContractTests
    {
        private const string ChoiceType = "test.concurrent_interaction.choice";

        private static GameState CreateState()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            return new ItemZoneStateFactory().CreateForSetup(new GameState
            {
                GameId = "concurrent-interaction-contract", MapId = map.MapId,
                ContentPackHash = "concurrent-interaction-content",
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1,
                CurrentPlayerId = 1, StartPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration
                {
                    MapId = map.MapId, PlayerCount = 2,
                    ContentPackHash = "concurrent-interaction-content"
                },
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 }
                }
            }, map, new List<ItemRecipeEntry>());
        }

        private static EffectRegistry CreateRegistry(bool grouped = false)
        {
            var registry = new EffectRegistry();
            registry.Register(ChoiceType, context => context.GetLatestInteractionAnswer() != null
                ? EffectStepResult.Completed(context.GetLatestInteractionAnswer())
                : EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec
                {
                    InteractionTypeId = ChoiceType, AnsweringPlayerId = context.Node.PlayerId,
                    Visibility = "owner", AnswerSchema = "boolean"
                }));
            if (grouped)
            {
                // 只测试共享调度和请求门禁；不替代真实企业业务测试。
                registry.Register(ExpansionSetupFlow.PreparationTypeId, context =>
                    EffectStepResult.Completed().WithFlowStage("awaiting_initial_enterprise_tasks")
                        .AddIndependentChildren("initial_enterprise_selections", new[]
                        {
                            EffectSpec.Create(ChoiceType, playerId: 1),
                            EffectSpec.Create(ChoiceType, playerId: 2)
                        }), EffectExecutorKind.IntrinsicFlow);
            }
            return registry;
        }

        private static void Run(EffectTreeExecutor executor)
        {
            var report = executor.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, executor.Runtime.LastFaultMessage);
        }

        private static EffectTreeExecutor OpenSingle(out GameState state)
        {
            state = CreateState();
            var executor = new EffectTreeExecutor(state, CreateRegistry());
            executor.CreateRoot(EffectSpec.Create(ChoiceType, playerId: 1));
            Run(executor);
            return executor;
        }

        private static EffectTreeExecutor OpenGroup(out GameState state, out string rootId)
        {
            state = CreateState();
            var executor = new EffectTreeExecutor(state, CreateRegistry(true));
            rootId = executor.CreateRoot(EffectSpec.Create(ExpansionSetupFlow.PreparationTypeId,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry
                    {
                        Name = "setupProtocolVersion", Value = NormalizedValue.CreateInteger(2)
                    }
                })));
            Run(executor);
            PublishKernelFixtureInteractionRevisions(state);
            return executor;
        }

        private static void PublishKernelFixtureInteractionRevisions(GameState state)
        {
            // 此夹具是孤立的通用内核组，没有真实 active mainline。
            // 仅模拟发布时绑定当前 token；真实业务祖先的窄刷新由规则专用测试覆盖。
            foreach (var request in state.EffectRuntime.InteractionRequests.Where(value => value.Status == "open"))
                request.StateRevision = state.EffectRuntime.StateRevision;
        }

        [Test]
        public void StrictRevisionRejectionIsTypedAndDoesNotPublishAnyMutation()
        {
            var executor = OpenSingle(out var state);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            int staleRevision = state.EffectRuntime.StateRevision - 1;
            string before = JsonConvert.SerializeObject(state);

            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1,
                staleRevision, NormalizedValue.CreateBoolean(true), out var diagnostic,
                out var errorCode), Is.False);
            Assert.That(errorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(diagnostic, Is.EqualTo("交互已过期，请刷新当前交互。"));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1,
                staleRevision, NormalizedValue.CreateBoolean(true), out var oldDiagnostic), Is.False);
            Assert.That(oldDiagnostic, Is.EqualTo(diagnostic));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [Test]
        public void OtherRejectionsKeepTheirOriginalClassificationAndSuccessReturnsNone()
        {
            var executor = OpenSingle(out var state);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            int revision = state.EffectRuntime.StateRevision;
            string before = JsonConvert.SerializeObject(state);

            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 2, revision,
                NormalizedValue.CreateBoolean(true), out _, out var unauthorized), Is.False);
            Assert.That(unauthorized, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1, revision,
                NormalizedValue.CreateString("invalid"), out _, out var malformed), Is.False);
            Assert.That(malformed, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));

            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1, revision,
                NormalizedValue.CreateBoolean(true), out var diagnostic, out var accepted), Is.True, diagnostic);
            Assert.That(accepted, Is.EqualTo(CommandErrorCode.None));
            Run(executor);
            string completed = JsonConvert.SerializeObject(state);
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1, revision,
                NormalizedValue.CreateBoolean(true), out _, out var duplicate), Is.False);
            Assert.That(duplicate, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(completed));
        }

        [Test]
        public void IndependentPlayerAnswersPreserveTheOtherRequestAndCompletionBarrier()
        {
            var executor = OpenGroup(out var state, out var rootId);
            var requests = state.EffectRuntime.InteractionRequests.Where(value => value.Status == "open").ToArray();
            Assert.That(requests.Select(value => value.AnsweringPlayerId), Is.EquivalentTo(new[] { 1, 2 }));
            var first = requests.Single(value => value.AnsweringPlayerId == 1);
            var second = requests.Single(value => value.AnsweringPlayerId == 2);
            int originalRevision = first.StateRevision;
            Assert.That(InteractionRequestProjector.ProjectForPlayer(second, 1).VisibleToViewer, Is.False);

            Assert.That(executor.TrySubmitInteraction(second.GetStableInteractionId(), 2,
                second.StateRevision, NormalizedValue.CreateBoolean(true), out var diagnostic,
                out var accepted), Is.True, diagnostic);
            Assert.That(accepted, Is.EqualTo(CommandErrorCode.None));
            Run(executor);
            PublishKernelFixtureInteractionRevisions(state);
            Assert.That(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == rootId).Status,
                Is.EqualTo(EffectNodeStatus.Blocked));
            var remaining = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(remaining.GetStableInteractionId(), Is.EqualTo(first.GetStableInteractionId()));
            Assert.That(remaining.AnsweringPlayerId, Is.EqualTo(1));
            Assert.That(remaining.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
            string before = JsonConvert.SerializeObject(state);
            Assert.That(executor.TrySubmitInteraction(remaining.GetStableInteractionId(), 1,
                originalRevision, NormalizedValue.CreateBoolean(true), out _, out var stale), Is.False);
            Assert.That(stale, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(executor.TrySubmitInteraction(remaining.GetStableInteractionId(), 1,
                remaining.StateRevision, NormalizedValue.CreateBoolean(true), out diagnostic,
                out _), Is.True, diagnostic);
            Run(executor);
            Assert.That(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == rootId).Status,
                Is.EqualTo(EffectNodeStatus.Completed));
        }

        [Test]
        public void FormalSubmissionRefreshesRemainingRequestAndStaleRejectionIsAtomic()
        {
            var executor = OpenGroup(out var state, out _);
            var first = state.EffectRuntime.InteractionRequests.Single(value => value.AnsweringPlayerId == 1);
            var second = state.EffectRuntime.InteractionRequests.Single(value => value.AnsweringPlayerId == 2);
            var firstCommand = BooleanAnswer(first, 1);
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(executor.Registry));

            Assert.That(session.Submit(BooleanAnswer(second, 2)).Succeeded, Is.True);
            var remaining = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(remaining.GetStableInteractionId(), Is.EqualTo(first.GetStableInteractionId()));
            Assert.That(remaining.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
            string before = JsonConvert.SerializeObject(state);
            var rejected = session.Submit(firstCommand);
            Assert.That(rejected.Succeeded, Is.False);
            Assert.That(rejected.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(session.Submit(BooleanAnswer(remaining, 1)).Succeeded, Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(value => value.Status == "open"), Is.False);
        }

        private static GameCommand BooleanAnswer(InteractionRequest request, int playerId)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = playerId };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "true";
            return command;
        }

        [Test]
        public void RequiredParallelGroupCannotDisappearThroughTheNoGroupsValidationShortcut()
        {
            OpenGroup(out var state, out var rootId);
            Assert.That(state.EffectRuntime.TryValidate(out var diagnostic), Is.True, diagnostic);
            var tampered = GameStateCloneService.DeepClone(state);
            tampered.EffectRuntime.EffectNodes.Single(value => value.EffectId == rootId).ChildSchedulingGroups.Clear();
            Assert.That(tampered.EffectRuntime.EffectNodes.All(value => value.ChildSchedulingGroups.Count == 0), Is.True);
            Assert.That(tampered.EffectRuntime.TryValidate(out diagnostic), Is.False);
            Assert.That(diagnostic, Does.Contain("必需子任务组"));
            Assert.That(state.EffectRuntime.TryValidate(out diagnostic), Is.True, diagnostic);
        }
    }
}
