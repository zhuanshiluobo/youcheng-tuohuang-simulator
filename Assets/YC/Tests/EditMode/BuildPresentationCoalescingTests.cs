using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // 正式Workflow/命令/投影的隔离对照；测试视图模拟原Flow的同步刷新，不计Unity画面验收。
    public sealed class BuildPresentationCoalescingTests
    {
        [Test]
        public void SynchronousPreparePresentsOnlyTheFinalDraft()
        {
            var f = new Fixture(); f.ResetCounts();
            f.Workflow.SelectFacility(f.Target);
            Assert.That(f.Port.Commands.Count, Is.EqualTo(1));
            Assert.That(f.Port.Commands[0].Parameters["operation"], Is.EqualTo("prepare_effect_build"));
            Assert.That(f.Payment.TargetId, Is.EqualTo(f.Target));
            Assert.That(f.View.Last.PaymentPreparation.PaymentId, Is.EqualTo(f.Payment.PaymentId));
            Assert.That(f.View.Shows, Is.EqualTo(1), "同一同步准备只呈现最终模型，不能为每次刷新重新查询/绘制。");
        }

        [Test]
        public void SynchronousReselectionKeepsCancelAndPrepareButCoalescesTheirPresentation()
        {
            var f = new Fixture(); f.Workflow.SelectFacility(f.Target);
            string firstPayment = f.Payment.PaymentId;
            string other = f.Workflow.BuildDraftViewModel().Options.First(o => o.FacilityId != f.Target && BuildFacilitySelectionController.CanPrepareOption(o)).FacilityId;
            f.ResetCounts(); f.Workflow.SelectFacility(other);
            Assert.That(f.Port.Commands.Select(c => c.Parameters["operation"]),
                Is.EqualTo(new[] { "prepare_effect_build", "cancel_effect_build", "prepare_effect_build" }));
            Assert.That(f.Payment.PaymentId, Is.Not.EqualTo(firstPayment));
            Assert.That(f.Payment.TargetId, Is.EqualTo(other));
            Assert.That(f.Payment.PaymentGeneration, Is.EqualTo(1));
            Assert.That(f.View.Last.SelectedOption.FacilityId, Is.EqualTo(other));
            Assert.That(f.View.Shows, Is.EqualTo(1));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(40));
        }

        [Test]
        public void DeferredReceiptsAreConsumedOnceAndNeverByWrongIdentity()
        {
            var f = new Fixture(); f.Port.Deferred = true; f.Workflow.SelectFacility(f.Target);
            var command = f.Port.Commands.Single();
            Assert.That(f.Workflow.IsSubmissionInFlight, Is.True);
            f.ResetCounts(); f.Ack(command, game: f.State.GameId + ".other");
            f.Ack(command, id: command.CommandId + ".other");
            Assert.That(f.Workflow.IsSubmissionInFlight, Is.True);
            Assert.That(f.View.Shows, Is.Zero);
            f.Ack(command);
            Assert.That(f.Workflow.IsSubmissionInFlight, Is.False);
            Assert.That(f.View.Shows, Is.EqualTo(1));
            f.Ack(command);
            Assert.That(f.View.Shows, Is.EqualTo(1));
            Assert.That(f.Port.Commands.Count, Is.EqualTo(1));
        }

        [Test]
        public void RejectedModeSwitchKeepsOriginalPaymentAndDoesNotCommit()
        {
            var f = new Fixture(); f.Workflow.SelectFacility(f.Target);
            var original = f.Payment; f.Port.Deferred = true; f.Port.RejectNext = true;
            f.Workflow.TryPreparePayment("gold"); var command = f.Port.Commands.Last();
            Assert.That(command.Parameters["operation"], Is.EqualTo("change_payment_mode"));
            Assert.That(command.Parameters["paymentGeneration"], Is.EqualTo(original.PaymentGeneration.ToString()));
            f.ResetCounts(); f.Ack(command, false);
            Assert.That(f.Payment.PaymentId, Is.EqualTo(original.PaymentId));
            Assert.That(f.Payment.PaymentGeneration, Is.EqualTo(original.PaymentGeneration));
            Assert.That(f.Payment.PaymentMode, Is.EqualTo("resources"));
            Assert.That(f.Port.Commands.Count, Is.EqualTo(2));
            Assert.That(f.View.Shows, Is.EqualTo(1));
            f.Ack(command);
            Assert.That(f.Port.Commands.Count, Is.EqualTo(2));
        }

        [Test]
        public void DeferredCancelContinuesReselectionOnlyAfterItsExactReceipt()
        {
            var f = new Fixture(); f.Workflow.SelectFacility(f.Target);
            string other = f.Workflow.BuildDraftViewModel().Options.First(o => o.FacilityId != f.Target && BuildFacilitySelectionController.CanPrepareOption(o)).FacilityId;
            f.Port.Deferred = true; f.Workflow.SelectFacility(other);
            var cancel = f.Port.Commands.Last();
            Assert.That(f.Port.Commands.Count, Is.EqualTo(2));
            f.Ack(cancel, id: cancel.CommandId + ".old");
            Assert.That(f.Port.Commands.Count, Is.EqualTo(2));
            f.ResetCounts(); f.Ack(cancel);
            Assert.That(f.Port.Commands.Count, Is.EqualTo(3));
            Assert.That(f.Port.Commands.Last().TargetId, Is.EqualTo(other));
            Assert.That(f.Workflow.IsSubmissionInFlight, Is.True);
            f.Ack(f.Port.Commands.Last());
            Assert.That(f.Workflow.IsSubmissionInFlight, Is.False);
            Assert.That(f.View.Last.PaymentPreparation.TargetId, Is.EqualTo(other));
        }

        [Test]
        public void IndependentDraftChangesAtTheSameRevisionArePresentedAgain()
        {
            var f = new Fixture(); f.Workflow.SelectFacility(f.Target);
            int revision = f.Visible.StateRevision, commands = f.Port.Commands.Count;
            f.ResetCounts(); f.Workflow.BeginDrag(f.Target);
            var dragging = f.View.Last.Phase;
            f.Workflow.RejectDrop();
            Assert.That(f.Visible.StateRevision, Is.EqualTo(revision));
            Assert.That(f.Port.Commands.Count, Is.EqualTo(commands));
            Assert.That(f.View.Shows, Is.EqualTo(2));
            Assert.That(f.View.Last.Phase, Is.Not.EqualTo(dragging));
        }

        [Test]
        public void OwnerRebindUsesTheNewRequestAndCandidates()
        {
            var f = new Fixture();
            string oldRequest = f.Workflow.EffectBuildScope.Input.InteractionId;
            string next = f.OpenOwner(); f.ResetCounts();
            f.Workflow.BeginEffectBuild(f.Scope(next));
            Assert.That(f.Workflow.EffectBuildScope.OwnerEffectId, Is.EqualTo(next));
            Assert.That(f.Workflow.EffectBuildScope.Input.InteractionId, Is.Not.EqualTo(oldRequest));
            Assert.That(f.View.Shows, Is.EqualTo(1));
            Assert.That(f.View.Last.SelectedOption, Is.Null);
        }

        [Test]
        public void ViewerOrGameChangesCannotConsumeTheOldPaymentReceipt()
        {
            var f = new Fixture(); f.Port.Deferred = true; f.Workflow.SelectFacility(f.Target);
            var command = f.Port.Commands.Single(); f.Player = 2; f.Refresh(); f.Ack(command);
            Assert.That(f.Workflow.IsSubmissionInFlight, Is.True);
            f.Player = 1; f.Refresh();
            var original = f.Public; f.Public = GameStateCloneService.DeepClone(original); f.Public.GameId += ".other";
            f.Ack(command); Assert.That(f.Workflow.IsSubmissionInFlight, Is.True);
            f.Refresh(); f.ResetCounts(); f.Ack(command);
            Assert.That(f.Workflow.IsSubmissionInFlight, Is.False);
            Assert.That(f.View.Shows, Is.EqualTo(1));
        }

        [Test]
        public void CancellationFinishesWithHiddenDraftAndNoPreparedPayment()
        {
            var f = new Fixture(); f.Workflow.SelectFacility(f.Target);
            var original = f.Payment; f.ResetCounts();
            Assert.That(f.Workflow.TryCancelPreparedPayment(null), Is.True);
            Assert.That(f.Workflow.IsActive, Is.False);
            Assert.That(f.Visible.PreparedActionPayments, Is.Empty);
            Assert.That(f.View.Hidden, Is.True);
            Assert.That(f.Port.Commands.Last().Parameters["preparationOwnerEffectId"], Is.EqualTo(original.PreparationOwnerEffectId));
        }

        [Test]
        public void EffectGoldSwitchCommitsOnceWithTheSamePaymentAndNextGeneration()
        {
            var f = new Fixture(); f.Workflow.SelectFacility(f.Target); var original = f.Payment;
            int cost = FacilityCardDatabase.Get(f.Public, f.Target).GoldVoucherCost;
            if (f.Workflow.TryPreparePayment("gold")) f.Workflow.Confirm();
            Assert.That(f.Port.Commands.Count(c => c.Kind == GameCommandKind.AnswerInteraction), Is.EqualTo(1));
            var payment = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == original.PaymentOwnerEffectId).Payment;
            Assert.That(payment.PaymentId, Is.EqualTo(original.PaymentId));
            Assert.That(payment.PaymentGeneration, Is.EqualTo(original.PaymentGeneration + 1));
            Assert.That(payment.IsCommitted, Is.True);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(40 - cost));
            int count = f.Port.Commands.Count; f.Ack(f.Port.Commands.Last());
            Assert.That(f.Port.Commands.Count, Is.EqualTo(count));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(40 - cost));
        }

        [Test]
        public void ShutdownDuringSynchronousRefreshCannotResurrectThePendingDraft()
        {
            var f = new Fixture(); f.ResetCounts(); f.View.Refresh = f.Workflow.BeginShutdown;
            f.Workflow.SelectFacility(f.Target);
            Assert.That(f.View.Shows, Is.Zero);
            Assert.That(f.View.Hidden, Is.True);
            Assert.That(f.Workflow.IsActive, Is.False);
            Assert.That(f.Port.Commands.Count, Is.EqualTo(1));
        }

        // 无详细诊断计时与分配在动作外取首尾；返回原始命令和完整规则摘要供私有改前/后配对。
        public static object MeasureReselection(bool traceCounts)
        {
            var f = new Fixture(); f.Workflow.SelectFacility(f.Target);
            string other = f.Workflow.BuildDraftViewModel().Options.First(o => o.FacilityId != f.Target && BuildFacilitySelectionController.CanPrepareOption(o)).FacilityId;
            int queries = 0, presents = 0; f.ResetCounts();
            if (traceCounts) Assert.That(InteractionLatencyTrace.TryStart(s =>
            { if (s.Edge == "end" && s.Name == "build.query_options") queries++; if (s.Edge == "end" && s.Name == "build.present_draft") presents++; }), Is.True);
            long bytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            try { f.Workflow.SelectFacility(other); }
            finally { if (traceCounts) InteractionLatencyTrace.Stop(); }
            long ticks = Stopwatch.GetTimestamp() - start, allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            return new { ticks, frequency = Stopwatch.Frequency, allocated, queries, presents, shows = f.View.Shows,
                commands = JsonConvert.SerializeObject(f.Port.Commands), stateDigest = RuleStateDigestService.Compute(f.State),
                state = JsonConvert.SerializeObject(f.State), model = JsonConvert.SerializeObject(new {
                    f.View.Last.Phase, f.View.Last.PaymentMode, f.View.Last.CanCancel, f.View.Last.Options,
                    Selected = f.View.Last.SelectedOption?.FacilityId, Payment = f.View.Last.PaymentPreparation }),
                receipts = f.Port.Results.Select(r => new { r.Succeeded, r.Validation.ErrorCode, r.LogMessage }).ToArray() };
        }

        private sealed class Fixture : IWritableGameplayContext
        {
            public readonly GameState State;
            public readonly EffectRegistry Registry = new EffectRegistry();
            public readonly GameSession Session;
            public GameStateView Visible;
            public GameState Public;
            public int Player = 1;
            public readonly Port Port;
            public readonly View View = new View();
            public readonly BuildInteraction Workflow;
            public readonly string Owner, Target;
            public GameState CurrentState => Public;
            public int LocalPlayerId => Player;
            public bool ControlsCurrentPlayerLocally => false;
            public void SetLocalPlayerId(int id) { Player = id; Refresh(); }
            public PreparedActionPaymentView Payment => Visible.PreparedActionPayments.Single();
            public Fixture()
            {
                var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
                var config = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.FourPlayerMapId,
                    EnabledExpansionIds = new List<string> { "core", "enterprise" } };
                var catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
                State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                    Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id }).ToList(), config, 321,
                    catalog.SetupRecipe, catalog.ContentDefinitions, new[] { 1, 2, 3 }, "build.coalesce.r3", null);
                catalog.Register(Registry); GenericLuaEffectExecutor.Register(Registry); ResourceEffectExecutor.Register(Registry);
                ResourcePaymentChoiceEffectExecutor.Register(Registry); ItemTransferEffectExecutor.Register(Registry);
                FacilitySelectionEffectExecutor.Register(Registry); FacilityBuildEffectExecutor.Register(Registry); FacilityEntryEffectExecutor.Register(Registry);
                State.Phase = GamePhase.ActionRound1; State.Round = 1; State.ActionRound = 1;
                State.FindPlayer(1).Resources = new ResourceSet { GoldVoucher = 40 };
                Session = new GameSession(State); Session.RegisterHandler(new PreparePaymentDraftCommandHandler(Registry));
                Session.RegisterHandler(new AnswerInteractionCommandHandler(Registry));
                // 与正式会话一致提供付款投影Registry；仅有Prepare/Answer不能证明api3计划可付。
                Session.RegisterHandler(new BuildFacilityCommandHandler(new BuildFacilityService(), new RoundAdvanceService(), Registry));
                Owner = OpenOwner(); Port = new Port(this);
                Workflow = new BuildInteraction(this, Port, View, new InteractionFlowCoordinator(), () => false, _ => { });
                Workflow.GetVisibleState = () => Visible;
                View.Refresh = () => Workflow.RefreshEffectBuild();
                Workflow.BeginEffectBuild(Scope(Owner));
                Target = Workflow.BuildDraftViewModel().Options.First(o => BuildFacilitySelectionController.CanPrepareOption(o)).FacilityId;
            }
            public string OpenOwner()
            {
                var engine = new EffectTreeExecutor(State, Registry);
                var node = engine.CreateRoot(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId,
                    Object("sourceZone", NormalizedValue.CreateString("supply"), "paymentMode", NormalizedValue.CreateString("auto"),
                        "allowDecline", NormalizedValue.CreateBoolean(true)), 1));
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
                Refresh(); return node;
            }
            public EffectBuildScope Scope(string owner)
            {
                Assert.That(EffectBuildScope.TryReadEffectBuildScope(Public, Visible, Player, owner, out var scope), Is.True);
                return scope;
            }
            public void Refresh()
            {
                Visible = Session.ProjectFor(GameStateViewer.Player(Player));
                Public = (GameState)Type.GetType("YC.Presentation.PlayerUiReadStateFactory, Assembly-CSharp", true)
                    .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { State, Visible, Player });
            }
            public void ResetCounts() { View.Shows = 0; }
            public void Ack(GameCommand c, bool accepted = true, string game = null, string id = null) =>
                Workflow.NotifyInteractionCommandResolved(game ?? State.GameId, id ?? c.CommandId, accepted, accepted ? default : CommandErrorCode.InvalidTarget);
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
                var result = RejectNext ? CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "隔离准确拒绝")) : fixture.Session.Submit(command);
                RejectNext = false; Results.Add(result); fixture.Refresh();
                Assert.That(result.Succeeded || Deferred, Is.True, result.LogMessage);
                return new WorkflowSubmissionResult(Deferred ? CommandResult.SuccessResult(new List<YC.Domain.Events.GameEvent>(), "等待隔离回执") : result, !Deferred);
            }
        }
        private sealed class View : ITurnActionView
        {
            public Action Refresh;
            public int Shows;
            public bool Hidden;
            public BuildFacilityDraftViewModel Last;
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel m) { Last = m; Shows++; Hidden = false; }
            public void HideBuildFacilityDraft() { Hidden = true; }
            public void RefreshFromState() => Refresh?.Invoke();
            public void RefreshInformation() => Refresh?.Invoke();
            public void RefreshActionPanel() => Refresh?.Invoke();
            public void ShowPendingChoice() => Refresh?.Invoke();
            public void ShowPrompt(string message) { }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> h) { }
            public void ClearHighlights() { }
            public void CompleteMainActionPresentation(string name) { }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel model) { }
        }
    }
}
