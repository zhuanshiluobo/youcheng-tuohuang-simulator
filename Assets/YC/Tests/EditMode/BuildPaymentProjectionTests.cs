using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 隔离构造局面，经正式准备、正式投影和实际公开状态工厂；不计正常页面/视觉验收。
    public sealed class BuildPaymentProjectionTests
    {
        [Test]
        public void RealProjectionKeepsIndependentGoldQuoteAndSelectionWithoutHiddenRuntime()
        {
            var f = new Fixture();
            var selection = new BuildFacilitySelectionController();
            Bind(selection, () => f.Visible);
            selection.Begin(1);
            Assert.That(selection.TrySelectFacility(f.Public, f.Item.ItemId, out var reason), Is.True, reason);
            f.Prepare("resources");
            var payment = f.Visible.PreparedActionPayments.Single();
            Assert.That(payment.IsReady && payment.CanCancel, Is.True);
            Assert.That(payment.CanConfirm, Is.False);
            Assert.That(payment.OriginalCost.Enumerate().Any(entry => entry.Value > 0), Is.True);
            Assert.That(f.Public.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(18));
            Assert.That(FacilityCardDatabase.Get(f.Public, f.Item.DefinitionId).GoldVoucherCost, Is.EqualTo(10));
            Assert.That(f.Public.EffectRuntime.EffectNodes, Is.Empty);
            Assert.That(f.Public.EffectRuntime.MainNodes, Is.Empty);
            Assert.That(f.Public.EffectRuntime.OperationWindows, Is.Empty);
            Assert.That(f.Public.Decks.EventDeckGreen, Is.Empty);
            var before = GameStateCloneService.DeepClone(f.State);
            var publicBefore = GameStateCloneService.DeepClone(f.Public);
            var query = new BuildFacilityOptionQueryService();
            var option = Quote(query, f.Public, f.Item.ItemId, f.Visible);
            Assert.That(option.ResourcesPayment.IsAvailable, Is.False);
            Assert.That(option.GoldPayment.IsAvailable && option.CanBuild, Is.True, option.Reason);
            Assert.That(selection.QueryOptions(f.Public).Single(value => value.FacilityId == f.Item.ItemId).GoldPayment.IsAvailable, Is.True);
            Assert.That(selection.QuerySelectedOption(f.Public).GoldPayment.IsAvailable, Is.True);
            Assert.That(selection.TrySelectPayment(f.Public, "gold", out reason), Is.True, reason);
            Assert.That(GameStateCloneService.AreEquivalent(before, f.State), Is.True);
            Assert.That(GameStateCloneService.AreEquivalent(publicBefore, f.Public), Is.True);
            Assert.That(query.Query(f.State, 1, f.Item.ItemId).GoldPayment.IsAvailable, Is.True, "完整状态旧入口继续可用");
            Assert.That(query.Query(f.Public, 1, f.Item.ItemId).GoldPayment.IsAvailable, Is.False, "无正式投影不得猜测隐藏准备");
        }

        [Test]
        public void OriginalWorkflowSwitchesToGoldAndCommitsOnceThroughOriginalSessionGuard()
        {
            var f = new Fixture(); var port = new Port(f); var view = new View();
            var workflow = new BuildInteraction(f, port, view, new InteractionFlowCoordinator(), () => true, _ => { });
            workflow.GetVisibleState = () => f.Visible;
            workflow.Begin(); workflow.SelectFacility(f.Item.ItemId);
            Assert.That(f.Visible.PreparedActionPayments.Single().PaymentMode, Is.EqualTo("resources"));
            var originalPreparation = f.Visible.PreparedActionPayments.Single().PreparationOwnerEffectId;
            var model = workflow.BuildDraftViewModel();
            Assert.That(model.SelectedOption.GoldPayment.IsAvailable, Is.True, model.SelectedOption.Reason);
            Assert.That(model.SelectedOption.ResourcesPayment.IsAvailable, Is.False);
            Assert.That(CanUsePayment(model, "resources"), Is.False);
            Assert.That(CanUsePayment(model, "gold"), Is.True);
            if (workflow.TryPreparePayment("gold")) workflow.Confirm();
            Assert.That(port.Commands.Count(command => command.Kind == GameCommandKind.BuildFacility), Is.EqualTo(1));
            Assert.That(port.Commands.Where(command => command.Kind == GameCommandKind.PreparePaymentDraft)
                .Select(command => command.Parameters["operation"]), Is.EqualTo(new[] { "prepare_action", "cancel_action", "prepare_action" }));
            var final = port.Commands.Single(command => command.Kind == GameCommandKind.BuildFacility);
            Assert.That(final.Parameters["paymentMode"], Is.EqualTo("gold"));
            Assert.That(final.Parameters["paymentPreparationOwnerEffectId"], Is.Not.EqualTo(originalPreparation));
            Assert.That(port.Results.All(result => result.Succeeded), Is.True, string.Join(";", port.Results.Select(result => result.LogMessage)));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == final.Parameters["paymentPreparationOwnerEffectId"]).Payment.IsCommitted, Is.True);
            Assert.That(f.Session.Submit(final).Succeeded, Is.False);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8), "重复最终命令不能再扣款");
        }

        [Test]
        public void GoldQuoteCannotTurnResourcePreparationIntoFinalGoldCommand()
        {
            var f = new Fixture(); f.Prepare("resources");
            var command = new GameCommand { Kind = GameCommandKind.BuildFacility, PlayerId = 1, TargetId = f.Item.ItemId };
            command.Parameters["paymentMode"] = "gold";
            command.Parameters["expectedSourceZoneId"] = f.Item.ZoneId;
            command.Parameters["paymentPreparationOwnerEffectId"] = f.Visible.PreparedActionPayments.Single().PreparationOwnerEffectId;
            Assert.That(f.Session.Submit(command).Succeeded, Is.False);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(18));
        }

        [Test]
        public void CachedSourcesStillRequoteCurrentRevisionAndBudget()
        {
            var f = new Fixture(); var selection = new BuildFacilitySelectionController();
            Bind(selection, () => f.Visible); selection.Begin(1);
            selection.QueryOptions(f.Public); f.Prepare("resources");
            Assert.That(selection.QueryOptions(f.Public).Single(option => option.FacilityId == f.Item.ItemId).GoldPayment.IsAvailable, Is.True);
            f.State.FindPlayer(1).Resources.GoldVoucher = 9; f.NextSnapshot();
            var option = selection.QueryOptions(f.Public).Single(value => value.FacilityId == f.Item.ItemId);
            Assert.That(option.GoldPayment.IsAvailable, Is.False);
            Assert.That(option.GoldPayment.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            f.State.FindPlayer(1).Resources.GoldVoucher = 18; f.State.FindPlayer(1).RemainingMainActionsThisTurn = 0;
            f.NextSnapshot();
            Assert.That(selection.QueryOptions(f.Public).Single(option => option.FacilityId == f.Item.ItemId).GoldPayment.IsAvailable, Is.False);
        }

        [Test]
        public void DeferredSwitchRequiresExactReceiptsAndRejectedGoldIntentDoesNotSignAgain()
        {
            var f = new Fixture(); var port = new Port(f) { Deferred = true };
            var workflow = new BuildInteraction(f, port, new View(), new InteractionFlowCoordinator(), () => true, _ => { });
            workflow.GetVisibleState = () => f.Visible;
            workflow.Begin(); workflow.SelectFacility(f.Item.ItemId);
            var resources = port.Commands.Last();
            workflow.NotifyInteractionCommandResolved(f.State.GameId, resources.CommandId + ".other", true, default);
            Assert.That(workflow.IsSubmissionInFlight, Is.True);
            workflow.NotifyInteractionCommandResolved(f.State.GameId, resources.CommandId, true, default);
            workflow.TryPreparePayment("gold"); var cancel = port.Commands.Last();
            workflow.NotifyInteractionCommandResolved(f.State.GameId + ".other", cancel.CommandId, true, default);
            Assert.That(port.Commands.Count, Is.EqualTo(2));
            port.RejectNext = true;
            workflow.NotifyInteractionCommandResolved(f.State.GameId, cancel.CommandId, true, default);
            var gold = port.Commands.Last();
            Assert.That(gold.Parameters["operation"], Is.EqualTo("prepare_action"));
            Assert.That(gold.Parameters["paymentMode"], Is.EqualTo("gold"));
            workflow.NotifyInteractionCommandResolved(f.State.GameId, gold.CommandId, false, CommandErrorCode.InvalidTarget);
            workflow.SynchronizeFromState();
            workflow.NotifyInteractionCommandResolved(f.State.GameId, gold.CommandId, true, default);
            Assert.That(port.Commands.Count, Is.EqualTo(3));
            Assert.That(port.Commands.Any(command => command.Kind == GameCommandKind.BuildFacility), Is.False);
            var other = workflow.BuildDraftViewModel().Options.First(option => option.FacilityId != f.Item.ItemId &&
                BuildFacilitySelectionController.CanPrepareOption(option));
            workflow.SelectFacility(other.FacilityId);
            Assert.That(port.Commands.Last().TargetId, Is.EqualTo(other.FacilityId));
            Assert.That(port.Commands.Last().Parameters["paymentMode"], Is.EqualTo("resources"));
            workflow.NotifyInteractionCommandResolved(f.State.GameId, gold.CommandId, true, default);
            Assert.That(port.Commands.Count, Is.EqualTo(4));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(18));
        }

        [TestCase("game")]
        [TestCase("content")]
        [TestCase("player")]
        [TestCase("role")]
        [TestCase("revision")]
        [TestCase("target")]
        [TestCase("owner")]
        [TestCase("payment_generation")]
        [TestCase("window")]
        [TestCase("window_generation")]
        [TestCase("not_ready")]
        [TestCase("committing")]
        [TestCase("request")]
        [TestCase("duplicate")]
        public void MismatchedVisibleQuoteContextCannotExemptPendingPayment(string mismatch)
        {
            var f = new Fixture(); f.Prepare("resources");
            var view = GameStateCloneService.DeepClone(f.Visible); var payment = view.PreparedActionPayments.Single();
            if (mismatch == "game") view.GameId += ".other";
            if (mismatch == "content") view.ContentPackHash += ".other";
            if (mismatch == "player") view.ViewerPlayerId = 2;
            if (mismatch == "role") view.ViewerRole = GameStateViewerRole.Host;
            if (mismatch == "revision") view.StateRevision--;
            if (mismatch == "target") payment.TargetId += ".other";
            if (mismatch == "owner") payment.PaymentOwnerEffectId += ".other";
            if (mismatch == "payment_generation") payment.PaymentGeneration++;
            if (mismatch == "window") payment.OperationWindowId += ".other";
            if (mismatch == "window_generation") payment.WindowGeneration++;
            if (mismatch == "not_ready") payment.IsReady = false;
            if (mismatch == "committing") payment.CanCancel = false;
            if (mismatch == "request") payment.InteractionId += ".other";
            if (mismatch == "duplicate") view.PreparedActionPayments.Add(payment);
            var option = Quote(new BuildFacilityOptionQueryService(), f.Public, f.Item.ItemId, view);
            Assert.That(option.GoldPayment.IsAvailable || option.ResourcesPayment.IsAvailable || option.CanBuild, Is.False, mismatch);
        }

        [TestCase("required")]
        [TestCase("not_ready")]
        [TestCase("window_generation")]
        public void FormalProjectionKeepsRequiredResponsesAndInvalidWindowsBlocked(string obstruction)
        {
            var f = new Fixture(); f.Prepare("resources");
            var owner = f.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == f.Visible.PreparedActionPayments.Single().PreparationOwnerEffectId);
            if (obstruction == "not_ready") owner.Payment.BeforePaymentReady = false;
            else if (obstruction == "window_generation") owner.Payment.Preparation.WindowGeneration++;
            else
            {
                f.Registry.Register(new EffectRegistration("fixture.build.required", context => EffectStepResult.Continue("waiting")
                    .AddInteraction(new EffectInteractionSpec { InteractionTypeId = "fixture.build.required", AnsweringPlayerId = 1,
                        AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "finish" } }), EffectExecutorKind.IntrinsicFlow));
                var engine = new EffectTreeExecutor(f.State, f.Registry);
                engine.CreateRoot(EffectSpec.Create("fixture.build.required", playerId: 1));
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            }
            f.Refresh();
            var option = Quote(new BuildFacilityOptionQueryService(), f.Public, f.Item.ItemId, f.Visible);
            Assert.That(option.GoldPayment.IsAvailable || option.CanBuild, Is.False, obstruction);
        }

        // 修前同一完整测试文件也可编译执行；缺少新入口时精确落到原查询，保存行为首败。
        private static BuildFacilityOptionQueryResult Quote(BuildFacilityOptionQueryService query, GameState state, string id, GameStateView view)
        {
            var method = query.GetType().GetMethod("Query", new[] { typeof(GameState), typeof(int), typeof(string), typeof(GameStateView) });
            return method == null ? query.Query(state, 1, id) : (BuildFacilityOptionQueryResult)method.Invoke(query, new object[] { state, 1, id, view });
        }
        private static void Bind(BuildFacilitySelectionController selection, Func<GameStateView> getView) =>
            selection.GetType().GetProperty("GetVisibleState")?.SetValue(selection, getView);
        private static bool CanUsePayment(BuildFacilityDraftViewModel model, string mode) => (bool)
            Type.GetType("YC.Presentation.FacilityBuildDialogView, Assembly-CSharp", true)
                .GetMethod("CanUseBuildPayment", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { model, mode });

        private sealed class Fixture : IWritableGameplayContext
        {
            public readonly GameState State;
            public readonly EffectRegistry Registry = new EffectRegistry();
            public readonly GameSession Session;
            public readonly ItemInstanceState Item;
            public GameStateView Visible;
            public GameState Public;
            public GameState CurrentState => Public;
            public int LocalPlayerId => 1;
            public bool ControlsCurrentPlayerLocally => true;
            public void SetLocalPlayerId(int playerId) { }
            public Fixture()
            {
                var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
                var configuration = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.FourPlayerMapId,
                    EnabledExpansionIds = new List<string> { "core", "enterprise" } };
                var catalog = ActiveContentCatalogBuilder.Build(pack, configuration); configuration.ContentPackHash = catalog.ContentPackHash;
                State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                    Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id }).ToList(), configuration, 321,
                    catalog.SetupRecipe, catalog.ContentDefinitions);
                catalog.Register(Registry); GenericLuaEffectExecutor.Register(Registry);
                ResourceEffectExecutor.Register(Registry); ResourcePaymentChoiceEffectExecutor.Register(Registry);
                ItemTransferEffectExecutor.Register(Registry); FacilityBuildEffectExecutor.Register(Registry);
                FacilityEntryEffectExecutor.Register(Registry); RepeatEffectExecutor.Register(Registry);
                foreach (var player in State.Players) player.RemainingMainActionsThisTurn = 1;
                State.FindPlayer(1).Resources = new ResourceSet { GoldVoucher = 18 };
                // 固定种子下的正式供应候选，未注入目标牌或改变候选定义。
                Item = State.ItemZones.Items.First(item => State.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId &&
                    zone.TemplateId == "core.zone.facility_supply_slot") && FacilityCardDatabase.Get(State, item.DefinitionId)?.GoldVoucherCost == 10 &&
                    FacilityCardDatabase.Get(State, item.DefinitionId).ResourceCost.Enumerate().Any(entry => entry.Value > 0));
                var builder = new RoundMainChainBuilder(); var order = new List<int> { 1, 2, 3 };
                var chain = builder.Build(State.GameId, builder.CreateRoundExecutionId(State.GameId, 2, order), 2, order, 1);
                var window = chain.Nodes.Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && node.ActionRound == 2 && node.PlayerId == 1);
                foreach (var node in chain.Nodes.Where(node => node.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
                var runtime = State.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2;
                runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
                runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
                var validation = new RoundExecutionService(Registry).Advance(State);
                Assert.That(validation.IsValid, Is.True, validation.Reason);
                Session = new GameSession(State);
                Session.RegisterHandler(new PreparePaymentDraftCommandHandler(Registry));
                Session.RegisterHandler(new BuildFacilityCommandHandler(new BuildFacilityService(), new RoundAdvanceService(), Registry));
                Refresh();
            }
            public void Prepare(string mode)
            {
                var result = Session.Submit(EffectInteractionCommands.PrepareActionPayment(1, "build", Item.ItemId,
                    State.EffectRuntime.StateRevision, mode, Item.ZoneId));
                Assert.That(result.Succeeded, Is.True, result.LogMessage); Refresh();
            }
            public void Refresh()
            {
                Visible = Session.ProjectFor(GameStateViewer.Player(1));
                Public = (GameState)Type.GetType("YC.Presentation.PlayerUiReadStateFactory, Assembly-CSharp", true)
                    .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { State, Visible, 1 });
                Assert.That(Public, Is.Not.Null);
            }
            public void NextSnapshot()
            {
                State.EffectRuntime.StateRevision++;
                foreach (var request in State.EffectRuntime.InteractionRequests.Where(request => request.Status == "open"))
                    request.StateRevision = State.EffectRuntime.StateRevision;
                Refresh();
            }
        }
        private sealed class Port : IGameCommandPort
        {
            private readonly Fixture fixture;
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public readonly List<CommandResult> Results = new List<CommandResult>();
            public bool Deferred, RejectNext;
            public Port(Fixture fixture) { this.fixture = fixture; }
            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                Commands.Add(command);
                var result = RejectNext ? CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "隔离模拟的准确拒绝")) : fixture.Session.Submit(command);
                RejectNext = false; Results.Add(result); fixture.Refresh();
                // 远端拒绝在准确回执到达前保持等待，避免用本地返回值代替联机回执。
                return new WorkflowSubmissionResult(Deferred ? CommandResult.SuccessResult(new List<GameEvent>(), "等待隔离回执") : result, !Deferred);
            }
        }
        private sealed class View : ITurnActionView
        {
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights) { }
            public void ClearHighlights() { }
            public void RefreshFromState() { }
            public void ShowPrompt(string message) { }
            public void RefreshInformation() { }
            public void RefreshActionPanel() { }
            public void ShowPendingChoice() { }
            public void CompleteMainActionPresentation(string name) { }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel model) { }
            public void HideBuildFacilityDraft() { }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel model) { }
        }
    }
}
