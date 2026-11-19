using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>隔离效果树夹具；不代表正式界面或联机验收。</summary>
    public sealed class PreparationWindowFlowTests
    {
        private const string Choice = "test.preparation.choice";
        private const string Handler = "test.preparation.handler";

        private static PreparationStepDeclaration Step(string id, string execution) => new PreparationStepDeclaration {
            StepId = id, SourcePackId = "test.pack", SourcePackVersion = "1.0.0", Kind = "builtin",
            HandlerId = Handler, HandlerVersion = "1.0.0", Execution = execution
        };

        private static GameState State(params PreparationStepDeclaration[] steps)
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var state = new ItemZoneStateFactory().CreateForSetup(new GameState {
                GameId = "preparation-window-fixture", MapId = map.MapId, ContentPackHash = "fixture-content",
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "fixture-content" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } },
                SetupRecipe = new GameSetupRecipe { Preparation = new PreparationFlowPlan {
                    ModeId = "test.mode", BeforeDistribution = steps.ToList()
                } }
            }, map, new List<ItemRecipeEntry>());
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            return state;
        }

        private static EffectRegistry Registry(PreparationStepDeclaration declaration, List<string> invocations)
        {
            var registry = new EffectRegistry();
            PreparationWindowFlow.Register(registry);
            registry.RegisterPreparationStep(new PreparationStepRegistration(declaration, (context, arguments) => {
                string stepId = Text(context.Node.NormalizedArguments, "stepId");
                invocations.Add(stepId + ":" + context.Node.PlayerId);
                return new List<EffectSpec> { EffectSpec.Create(Choice,
                    Object("stepId", NormalizedValue.CreateString(stepId)), context.Node.PlayerId) };
            }));
            registry.Register(Choice, context => context.GetLatestInteractionAnswer() != null
                ? EffectStepResult.Completed(context.GetLatestInteractionAnswer())
                : EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = Choice, AnsweringPlayerId = context.Node.PlayerId,
                    Visibility = "owner", AnswerSchema = "boolean"
                }));
            return registry;
        }

        private static void Run(EffectTreeExecutor engine)
        {
            var report = engine.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, engine.Runtime.LastFaultMessage);
        }

        private static void Answer(GameState state, EffectRegistry registry, int player)
        {
            var request = state.EffectRuntime.InteractionRequests.Single(item => item.Status == "open" && item.AnsweringPlayerId == player);
            // 独立效果树夹具没有正式主链；仅模拟发布新状态时更新待答请求的版本。
            request.StateRevision = state.EffectRuntime.StateRevision;
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), player, request.StateRevision,
                NormalizedValue.CreateBoolean(true), out var reason), Is.True, reason);
            Run(engine);
        }

        private static int[] OpenPlayers(GameState state) => state.EffectRuntime.InteractionRequests
            .Where(request => request.Status == "open").Select(request => request.AnsweringPlayerId).OrderBy(id => id).ToArray();

        [Test]
        public void OrderedAndIndependentStepsWaitForEveryNestedChoiceAndResumeAfterStateClone()
        {
            var once = Step("once", "once");
            var ordered = Step("ordered", "eachInOrder");
            var parallel = Step("parallel", "eachIndependent");
            var last = Step("last", "once");
            var state = State(once, ordered, parallel, last);
            var calls = new List<string>();
            var registry = Registry(once, calls);
            var engine = new EffectTreeExecutor(state, registry);
            string rootId = engine.CreateRoot(PreparationWindowFlow.WindowSpec(PreparationWindowFlow.Before, 1));
            Run(engine);
            Assert.That(OpenPlayers(state), Is.EqualTo(new[] { 1 }));
            Assert.That(calls, Is.EqualTo(new[] { "once:1" }));
            Answer(state, registry, 1);
            Assert.That(calls, Is.EqualTo(new[] { "once:1", "ordered:1" }));
            Answer(state, registry, 1);
            Assert.That(OpenPlayers(state), Is.EqualTo(new[] { 2 }));
            Assert.That(calls.Last(), Is.EqualTo("ordered:2"));
            Answer(state, registry, 2);
            Assert.That(OpenPlayers(state), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(calls.Skip(3), Is.EquivalentTo(new[] { "parallel:1", "parallel:2" }));

            var window = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId);
            Assert.That(PreparationWindowFlow.ValidateWindow(state, window, state.SetupRecipe.Preparation, registry, out var diagnostic), Is.True, diagnostic);
            var originalIds = window.ChildEffectIds.ToArray();
            var originalRequests = state.EffectRuntime.InteractionRequests.Where(x => x.Status == "open").Select(x => x.GetStableInteractionId()).ToArray();
            // 该孤立效果树没有正式主链/档案；用正式状态深拷贝验证续行，完整存档恢复另测。
            state = GameStateCloneService.DeepClone(state);
            window = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId);
            Assert.That(PreparationWindowFlow.ValidateWindow(state, window, state.SetupRecipe.Preparation, registry, out diagnostic), Is.True, diagnostic);
            Assert.That(window.ChildEffectIds, Is.EqualTo(originalIds));
            Assert.That(state.EffectRuntime.InteractionRequests.Where(x => x.Status == "open").Select(x => x.GetStableInteractionId()), Is.EqualTo(originalRequests));
            Run(new EffectTreeExecutor(state, registry));
            Assert.That(calls.Count, Is.EqualTo(5), "恢复待答阶段不能重新调用步骤。");

            Answer(state, registry, 2);
            Assert.That(OpenPlayers(state), Is.EqualTo(new[] { 1 }));
            Assert.That(calls.Count, Is.EqualTo(5), "另一名玩家未完成时不能进入下一步骤。");
            Answer(state, registry, 1);
            Assert.That(OpenPlayers(state), Is.EqualTo(new[] { 1 }));
            Assert.That(calls.Last(), Is.EqualTo("last:1"));
            Answer(state, registry, 1);
            window = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId);
            Assert.That(window.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(PreparationWindowFlow.ValidateWindow(state, window, state.SetupRecipe.Preparation, registry, out diagnostic), Is.True, diagnostic);
            Assert.That(calls.Count, Is.EqualTo(6));
        }

        [Test]
        public void SavedWindowRejectsAlteredPlayerAssignmentOrMissingIndependentGroup()
        {
            var step = Step("shared", "eachIndependent");
            var state = State(step);
            var registry = Registry(step, new List<string>());
            var engine = new EffectTreeExecutor(state, registry);
            string rootId = engine.CreateRoot(PreparationWindowFlow.WindowSpec(PreparationWindowFlow.Before, 1));
            Run(engine);
            var root = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId);
            Assert.That(PreparationWindowFlow.ValidateWindow(state, root, state.SetupRecipe.Preparation, registry, out _), Is.True);
            var changed = GameStateCloneService.DeepClone(state);
            var changedRoot = changed.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId);
            changed.EffectRuntime.EffectNodes.Single(node => node.EffectId == changedRoot.ChildEffectIds[0]).PlayerId = 2;
            Assert.That(PreparationWindowFlow.ValidateWindow(changed, changedRoot, changed.SetupRecipe.Preparation, registry, out _), Is.False);
            changed = GameStateCloneService.DeepClone(state);
            changedRoot = changed.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId);
            changedRoot.ChildSchedulingGroups.Clear();
            Assert.That(PreparationWindowFlow.ValidateWindow(changed, changedRoot, changed.SetupRecipe.Preparation, registry, out _), Is.False);
        }

        [Test]
        public void UnknownOrChangedHandlerIdentityCannotRun()
        {
            var step = Step("step", "once");
            var state = State(step);
            var calls = new List<string>();
            var registry = Registry(step, calls);
            state.SetupRecipe.Preparation.BeforeDistribution[0].SourcePackVersion = "1.0.1";
            var engine = new EffectTreeExecutor(state, registry);
            string rootId = engine.CreateRoot(PreparationWindowFlow.WindowSpec(PreparationWindowFlow.Before, 1));
            Run(engine);
            Assert.That(state.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(calls, Is.Empty);
            Assert.That(OpenPlayers(state), Is.Empty);
        }
    }
}
