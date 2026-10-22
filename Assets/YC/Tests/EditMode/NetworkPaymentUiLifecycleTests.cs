using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 隔离展示生命周期窄验；不代替独立程序的正常流程验收。
    public sealed class NetworkPaymentUiLifecycleTests
    {
        [Test]
        public void OriginalBuildPanelAcceptsOnlyReadyMatchingModeDespiteLegacyPendingRejection()
        {
            var payment = Payment("build"); payment.TargetId = "facility-instance"; payment.PaymentMode = "resources";
            var model = BuildModel(false, payment);
            Assert.That(CanUseBuildPayment(model, "resources"), Is.True, "正式同笔可付状态优先于旧查询的待选拒绝");
            Assert.That(CanUseBuildPayment(model, "gold"), Is.False);
            payment.CanConfirm = false;
            Assert.That(CanUseBuildPayment(model, "resources"), Is.False);
            payment.CanConfirm = true; payment.IsReady = false;
            Assert.That(CanUseBuildPayment(model, "resources"), Is.False);
            payment.IsReady = true; payment.PaymentMode = "gold";
            Assert.That(CanUseBuildPayment(model, "gold"), Is.True);
            Assert.That(CanUseBuildPayment(model, "resources"), Is.False);
        }

        [Test]
        public void OriginalBuildPanelDoesNotBorrowForeignPreparationOrEnableUnavailableFallback()
        {
            var payment = Payment("build"); payment.TargetId = "other-instance"; payment.PaymentMode = "resources";
            var model = BuildModel(false, payment);
            Assert.That(CanUseBuildPayment(model, "resources"), Is.False);
            payment.TargetId = "facility-instance"; payment.ActionKind = "city_move";
            Assert.That(CanUseBuildPayment(model, "resources"), Is.False);
            payment.ActionKind = "build"; payment.PreparationOwnerEffectId = string.Empty;
            Assert.That(CanUseBuildPayment(model, "resources"), Is.False);
            Assert.That(CanUseBuildPayment(BuildModel(false, null), "resources"), Is.False);
            Assert.That(CanUseBuildPayment(BuildModel(true, null), "resources"), Is.True);
            Assert.That(CanUseBuildPayment(BuildModel(true, null), "gold"), Is.True);
        }

        private static bool CanUseBuildPayment(BuildFacilityDraftViewModel model, string mode) => (bool)
            Type.GetType("YC.Presentation.FacilityBuildDialogView, Assembly-CSharp", true)
                .GetMethod("CanUseBuildPayment", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { model, mode });

        private static BuildFacilityDraftViewModel BuildModel(bool available, PreparedActionPaymentView payment)
        {
            var validation = available ? ValidationResult.Success : ValidationResult.Failure(CommandErrorCode.InvalidTarget, "请先处理待选事项");
            var option = new BuildFacilityOptionQueryResult("facility-instance", validation, new List<BuildFacilitySlotOptionResult>(),
                new BuildFacilityPaymentOptionResult("resources", validation), new BuildFacilityPaymentOptionResult("gold", validation), new ResourceSet());
            var model = new BuildFacilityDraftViewModel(BuildFacilityDraftPhase.Focused, new[] { option }, option,
                new FacilityCardDefinition { FacilityId = "facility-definition" }, -1, string.Empty, string.Empty,
                Array.Empty<int>(), _ => { });
            typeof(BuildFacilityDraftViewModel).GetProperty("PaymentPreparation").SetValue(model, payment);
            return model;
        }

        [Test]
        public void RejectedCancellationCannotResubmitFromItsOwnRefresh()
        {
            var context = new Context(); var port = new Port { Reject = true };
            var visible = Projection(context, Payment("build"));
            ActionPaymentPreparation preparation = null;
            int refreshed = 0, continued = 0;
            preparation = new ActionPaymentPreparation(context, new CommandGateway(port), "build", () =>
            {
                // 修前最多模拟四层，保留故障而不让测试进程真正栈溢出。
                if (++refreshed < 4) preparation.Cancel(() => continued++);
            }, _ => { });
            preparation.GetVisibleState = () => visible;
            preparation.Cancel(() => continued++);
            Assert.That(port.Commands.Count, Is.EqualTo(1), "准确拒绝刷新不能递归重发取消");
            Assert.That(refreshed, Is.EqualTo(1));
            Assert.That(continued, Is.Zero, "拒绝不能执行成功取消的后续动作");
            Assert.That(preparation.Current, Is.SameAs(visible.PreparedActionPayments[0]));
            Assert.That(preparation.Current.CanConfirm, Is.True, "保留尚未取消的正式付款准备");
            Assert.That(preparation.LastResolvedApplied, Is.False);
            preparation.Cancel(null);
            Assert.That(port.Commands.Count, Is.EqualTo(2), "后续真实取消仍允许重试");
        }

        [Test]
        public void ExactCancellationAllowsContinuationButStaleReceiptsDoNotReleaseWait()
        {
            var context = new Context(); var port = new Port();
            var visible = Projection(context, Payment("build"));
            int refreshed = 0, continued = 0;
            var preparation = new ActionPaymentPreparation(context, new CommandGateway(port), "build", () => refreshed++, _ => { });
            preparation.GetVisibleState = () => visible;
            preparation.Cancel(() => { continued++; preparation.Prepare("next", "resources"); });
            var cancel = port.Commands.Single();
            Assert.That(preparation.NotifyResolved("old-game", cancel.CommandId, true), Is.False);
            Assert.That(preparation.NotifyResolved(context.State.GameId, "old-command", true), Is.False);
            Assert.That(preparation.IsInFlight, Is.True); Assert.That(refreshed, Is.Zero);
            visible.PreparedActionPayments.Clear();
            Assert.That(preparation.NotifyResolved(context.State.GameId, cancel.CommandId, true), Is.True);
            Assert.That(continued, Is.EqualTo(1)); Assert.That(refreshed, Is.EqualTo(1));
            Assert.That(port.Commands.Count, Is.EqualTo(2));
            Assert.That(preparation.NotifyResolved(context.State.GameId, cancel.CommandId, true), Is.False);
            Assert.That(preparation.IsInFlight, Is.True, "重复旧回执不能清除后续准备的等待");
        }

        [Test]
        public void ShutdownPreservesDomainPaymentAndSuppressesLateReceiptAndCancellation()
        {
            var context = new Context(); var port = new Port();
            var payment = Payment("build"); var visible = Projection(context, payment);
            int refreshed = 0, continued = 0;
            var preparation = new ActionPaymentPreparation(context, new CommandGateway(port), "build", () => refreshed++, _ => { });
            preparation.GetVisibleState = () => visible;
            preparation.Cancel(() => continued++); var command = port.Commands.Single();
            preparation.BeginShutdown();
            Assert.That(preparation.NotifyResolved(context.State.GameId, command.CommandId, false), Is.False);
            Assert.That(preparation.Cancel(null), Is.False); Assert.That(preparation.Prepare("new"), Is.False);
            Assert.That(port.Commands.Count, Is.EqualTo(1)); Assert.That(refreshed, Is.Zero); Assert.That(continued, Is.Zero);
            Assert.That(visible.PreparedActionPayments.Single(), Is.SameAs(payment));
        }

        [Test]
        public void ShutdownDuringAppliedRefreshDoesNotRunCapturedContinuation()
        {
            var context = new Context(); var port = new Port();
            var visible = Projection(context, Payment("build"));
            int continued = 0;
            ActionPaymentPreparation preparation = null;
            preparation = new ActionPaymentPreparation(context, new CommandGateway(port), "build", () => preparation.BeginShutdown(), _ => { });
            preparation.GetVisibleState = () => visible;
            preparation.Cancel(() => continued++);
            Assert.That(preparation.NotifyResolved(context.State.GameId, port.Commands.Single().CommandId, true), Is.True);
            Assert.That(continued, Is.Zero, "回执刷新中已停止的宿主不能继续旧取消动作");
            Assert.That(preparation.IsInFlight, Is.False);
        }

        [Test]
        public void MovePreparationKeepsFirstMarkerAndSecondClickSubmitsExactlyOnce()
        {
            var f = new MoveFixture(); f.Start();
            var target = f.Router.BuildActivePresentation().Highlights.First().TargetId;
            f.Router.OnLocationClicked(target, 10); f.Render();
            Assert.That(f.Marker(target), Is.EqualTo(WorkflowHighlightState.PendingConfirmation));
            var preparation = f.Port.Commands.Last();
            f.ResolvePreparation(preparation, target);
            Assert.That(f.Marker(target), Is.EqualTo(WorkflowHighlightState.PendingConfirmation), "准备回执后的最终画面仍保首击标记");
            Assert.That(f.Port.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.Zero);
            f.Router.OnLocationClicked(target, 11);
            Assert.That(f.Port.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1));
            f.Router.OnLocationClicked(target, 12);
            f.Router.NotifyInteractionCommandResolved(f.Context.State.GameId, preparation.CommandId, true, default);
            Assert.That(f.Port.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1));
        }

        [Test]
        public void SecondClickWhilePreparingWaitsForExactAppliedThenSubmitsOnce()
        {
            var f = new MoveFixture(); f.Start();
            var target = f.Router.BuildActivePresentation().Highlights.First().TargetId;
            f.Router.OnLocationClicked(target, 20); var preparation = f.Port.Commands.Last();
            f.Router.OnLocationClicked(target, 21);
            Assert.That(f.Port.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.Zero);
            f.Router.NotifyInteractionCommandResolved("old-game", preparation.CommandId, true, default);
            Assert.That(f.Move.IsSubmissionInFlight, Is.True);
            f.ResolvePreparation(preparation, target);
            Assert.That(f.Port.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1));
            f.Router.NotifyInteractionCommandResolved(f.Context.State.GameId, preparation.CommandId, true, default);
            Assert.That(f.Port.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1));
        }

        [Test]
        public void EscapeClearsMarkerAndNewGameCannotConfirmOldSelection()
        {
            var f = new MoveFixture(); f.Start();
            var target = f.Router.BuildActivePresentation().Highlights.First().TargetId;
            f.Router.OnLocationClicked(target, 30); f.ResolvePreparation(f.Port.Commands.Last(), target);
            f.Router.OnEscape(); f.Render();
            Assert.That(f.Router.HasPendingMapConfirmation, Is.False);
            Assert.That(f.Marker(target), Is.Not.EqualTo(WorkflowHighlightState.PendingConfirmation));
            f.Context.State.GameId = "new-game";
            f.Router.OnLocationClicked(target, 31);
            Assert.That(f.Port.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.Zero);
        }

        [Test]
        public void ChangingTargetWhilePreparingCancelsOldTargetThenKeepsNewFirstSelection()
        {
            var f = new MoveFixture(); f.Start();
            var targets = f.Router.BuildActivePresentation().Highlights.Take(2).Select(highlight => highlight.TargetId).ToArray();
            Assert.That(targets.Length, Is.EqualTo(2));
            f.Router.OnLocationClicked(targets[0], 40); var first = f.Port.Commands.Last();
            f.Router.OnLocationClicked(targets[1], 41);
            f.ResolvePreparation(first, targets[0]);
            var cancel = f.Port.Commands.Last();
            Assert.That(cancel.Parameters["operation"], Is.EqualTo("cancel_action"), "准备回执允许按最新真实首击换目标");
            f.ResolveCancellation(cancel);
            var next = f.Port.Commands.Last();
            Assert.That(next.TargetId, Is.EqualTo(targets[1]));
            f.ResolvePreparation(next, targets[1]);
            Assert.That(f.Marker(targets[1]), Is.EqualTo(WorkflowHighlightState.PendingConfirmation));
            Assert.That(f.Port.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.Zero);
            f.Router.OnLocationClicked(targets[1], 42);
            Assert.That(f.Port.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1));
        }

        private static PreparedActionPaymentView Payment(string kind) => new PreparedActionPaymentView
        {
            ActionKind = kind, PreparationOwnerEffectId = "preparation", PaymentOwnerEffectId = "payment-owner",
            PaymentId = "payment", PaymentGeneration = 1, OperationWindowId = "window", WindowGeneration = 1,
            IsReady = true, CanConfirm = true, CanCancel = true
        };
        private static GameStateView Projection(Context context, PreparedActionPaymentView payment) => new GameStateView
        { GameId = context.State.GameId, ViewerPlayerId = 1, PreparedActionPayments = new List<PreparedActionPaymentView> { payment } };

        private sealed class MoveFixture
        {
            public readonly Context Context = new Context();
            public readonly Port Port = new Port();
            public readonly View View = new View();
            public readonly InteractionRouter Router = new InteractionRouter(_ => { });
            public readonly MoveInteraction Move;
            private readonly GameStateView visible;
            public MoveFixture()
            {
                visible = Projection(Context, Payment("city_move"));
                Move = new MoveInteraction(Context, Port, View, new InteractionFlowCoordinator(),
                    new MapQueryService(StaticMapDefinitions.Resolve(Context.State.MapId)), () => { }, () => true, _ => { });
                Move.GetVisibleState = () => visible;
                Router.Register(Move);
                // 对应场景同步末尾：先恢复工作流高亮，再应用统一路由的确认状态。
                View.Refresh = () => { Move.RestorePresentation(); Render(); };
            }
            public void Start() { Move.Activate(); ResolvePreparation(Port.Commands.Single(), string.Empty); }
            public void ResolvePreparation(GameCommand command, string target)
            {
                if (visible.PreparedActionPayments.Count == 0) visible.PreparedActionPayments.Add(Payment("city_move"));
                visible.PreparedActionPayments[0].TargetId = target;
                visible.StateRevision = ++Context.State.EffectRuntime.StateRevision;
                Router.NotifyInteractionCommandResolved(Context.State.GameId, command.CommandId, true, default);
                Router.NotifyCommandSettled(command.CommandId);
            }
            public void ResolveCancellation(GameCommand command)
            {
                visible.PreparedActionPayments.Clear();
                visible.StateRevision = ++Context.State.EffectRuntime.StateRevision;
                Router.NotifyInteractionCommandResolved(Context.State.GameId, command.CommandId, true, default);
                Router.NotifyCommandSettled(command.CommandId);
            }
            public void Render() => View.SetHighlights(Router.BuildActivePresentation().Highlights);
            public WorkflowHighlightState Marker(string target) => View.Highlights.First(highlight => highlight.TargetId == target).State;
        }
        private sealed class Context : IWritableGameplayContext
        {
            public readonly GameState State = new GameState { GameId = "lifecycle-game", ContentPackHash = "lifecycle-content",
                MapId = StaticMapDefinitions.ThreePlayerMapId, Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1, Players = { new PlayerState { PlayerId = 1, CityLocationId = "A-03" } } };
            public GameState CurrentState => State;
            public int LocalPlayerId { get; private set; } = 1;
            public bool ControlsCurrentPlayerLocally => false;
            public void SetLocalPlayerId(int playerId) => LocalPlayerId = playerId;
        }
        private sealed class Port : IGameCommandPort
        {
            public bool Reject;
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                Commands.Add(command);
                return new WorkflowSubmissionResult(Reject ? CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.UnknownCommand, "模拟会话已停止")) :
                    CommandResult.SuccessResult(new List<GameEvent>(), "deferred"), false);
            }
        }
        private sealed class View : ITurnActionView
        {
            public Action Refresh;
            public IReadOnlyList<WorkflowHighlight> Highlights = Array.Empty<WorkflowHighlight>();
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights) => Highlights = highlights;
            public void ClearHighlights() => Highlights = Array.Empty<WorkflowHighlight>();
            public void RefreshFromState() => Refresh?.Invoke();
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
