using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // 隔离规则→正式投影→实际公开状态工厂→完整工作流；不是正常可见Player验收。
    public sealed class EffectDeckBuildWorkflowTests
    {
        private SceneSetup[] nativeSceneSetup;
        private GameObject nativeRoot;
        private IDisposable nativeCoordinator;
        private SceneRuntimeCatalogScope nativeCatalogs;

        [UnityTearDown]
        public IEnumerator LeaveNativeLifecycleFixture()
        {
            nativeCoordinator?.Dispose(); nativeCoordinator = null;
            if (nativeRoot != null) UnityEngine.Object.Destroy(nativeRoot);
            if (UnityEngine.Application.isPlaying)
            {
                yield return null;
                yield return new ExitPlayMode();
            }
            nativeCatalogs?.Dispose(); nativeCatalogs = null;
            if (nativeSceneSetup != null)
            {
                if (nativeSceneSetup.Any(scene => scene.isLoaded && scene.isActive))
                    EditorSceneManager.RestoreSceneManagerSetup(nativeSceneSetup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            nativeSceneSetup = null;
        }

        // 仅资源生命周期定位：隔离授予不计正常页面/合法游戏流程验收，不保存或生成任何资产。
        [UnityTest]
        [Category("需要Unity")]
        public IEnumerator NativeSharedBuildPageReusesOriginalPrefabAcrossFlowsAndReleasesOnDispose()
        {
            nativeSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                Assert.That(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty, Is.False, "不能替换用户未保存的场景");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            nativeCatalogs = new SceneRuntimeCatalogScope();
            yield return new EnterPlayMode();

            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var coordinatorType = Type.GetType("YC.Presentation.FacilityBuildRequestUiCoordinator, Assembly-CSharp", true);
            var pageType = Type.GetType("YC.Presentation.FacilityBuildDialogView, Assembly-CSharp", true);
            var registryType = Type.GetType("YC.Presentation.GameplayDialogRegistry, Assembly-CSharp", true);
            const string hudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
            const string pagePath = "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/FacilityBuildDialog.prefab";
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>(hudPath);
            Assert.That(hud, Is.Not.Null);
            var registry = hud.GetComponentInChildren(registryType, true);
            var configuredPage = (Component)registryType.GetField("facilityBuildPrefab", fields).GetValue(registry);
            Assert.That(AssetDatabase.GetAssetPath(configuredPage), Is.EqualTo(pagePath), "必须用实际HUD绑定的同一建设资产");
            nativeRoot = new GameObject("共享建设页生命周期隔离", typeof(RectTransform), typeof(Canvas));
            var rootRect = (RectTransform)nativeRoot.transform;
            rootRect.sizeDelta = new Vector2(1920, 1080);
            nativeRoot.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var fixture = new Fixture(0, withMainWindow: false);
            GameState readState = fixture.Public;
            GameStateView readView = fixture.Visible;
            var firstState = readState; var firstView = readView;
            var records = new List<object>();
            Func<Component[]> pages = () => Resources.FindObjectsOfTypeAll(pageType).OfType<Component>()
                .Where(component => component.gameObject.scene.IsValid() && component.transform.IsChildOf(rootRect)).ToArray();
            object coordinator = Activator.CreateInstance(coordinatorType, new object[] {
                (Func<GameState>)(() => readState), (Func<GameStateView>)(() => readView), (Func<int>)(() => 1),
                registry, rootRect, fixture.Port, (Func<bool>)(() => false), (Action)(() => { }), (Action<string>)(_ => { }) });
            nativeCoordinator = (IDisposable)coordinator;
            var render = coordinatorType.GetMethod("Render");
            var clear = coordinatorType.GetMethod("Clear");
            var retire = coordinatorType.GetMethod("Retire", fields);
            var active = coordinatorType.GetField("active", fields);
            var pageField = coordinatorType.GetField("page", fields);
            var flowsField = coordinatorType.GetField("flows", fields);
            void Record(string step)
            {
                records.Add(new { Step = step, PageCount = pages().Length,
                    ActiveCount = pages().Count(page => page.gameObject.activeInHierarchy),
                    InstanceIds = pages().Select(page => page.GetInstanceID()).ToArray(),
                    FlowCount = ((IDictionary)flowsField.GetValue(coordinator)).Count });
                TestContext.WriteLine("共享页生命周期 " + Newtonsoft.Json.JsonConvert.SerializeObject(records.Last()));
            }
            EffectBuildScope Scope(string owner)
            {
                Assert.That(EffectBuildScope.TryReadDeckBuildScope(readState, readView, 1, owner, out var scope), Is.True);
                return scope;
            }
            string AddFlow()
            {
                var executor = new EffectTreeExecutor(fixture.State, fixture.Registry);
                var owner = executor.CreateRoot(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId,
                    Object("sourceZone", NormalizedValue.CreateString("deck"), "paymentMode", NormalizedValue.CreateString("auto"),
                        "allowDecline", NormalizedValue.CreateBoolean(true)), 1));
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
                fixture.Refresh(1); readState = fixture.Public; readView = fixture.Visible; return owner;
            }
            try
            {
                Record("首次渲染前"); Assert.That(pages(), Is.Empty);
                render.Invoke(coordinator, new object[] { Scope(fixture.Owner).ProjectInput() });
                yield return null;
                var page = (Component)pageField.GetValue(coordinator);
                Assert.That(page, Is.Not.Null); int pageId = page.GetInstanceID();
                Assert.That(pages().Length, Is.EqualTo(1)); Assert.That(page.gameObject.activeInHierarchy, Is.True);
                Record("首Flow原页创建");
                var firstFlow = active.GetValue(coordinator);
                string secondOwner = AddFlow();
                var secondState = readState; var secondView = readView;
                render.Invoke(coordinator, new object[] { Scope(secondOwner).ProjectInput() });
                yield return null;
                var secondFlow = active.GetValue(coordinator);
                Assert.That(pageField.GetValue(coordinator), Is.SameAs(page)); Assert.That(pages().Length, Is.EqualTo(1));
                Record("第二Flow复用");
                // 只读回放各Flow曾正式产生的成对投影；不修改旧请求版本、不提交任何命令。
                // 第二根创建使旧B请求过期，不能伪造为同一当前版本来测试页面切换。
                readState = firstState; readView = firstView;
                render.Invoke(coordinator, new object[] { Scope(fixture.Owner).ProjectInput() });
                yield return null;
                Assert.That(page.GetInstanceID(), Is.EqualTo(pageId)); Assert.That(page.gameObject.activeInHierarchy, Is.True);
                readState = secondState; readView = secondView;
                render.Invoke(coordinator, new object[] { Scope(secondOwner).ProjectInput() });
                yield return null;
                Record("跨Flow往返仍一页");
                // 模拟旧Flow晚到的隐藏回调，再退役非活动Flow，均不得隐藏当前共享页。
                var workflow = (BuildInteraction)firstFlow.GetType().GetField("Workflow", fields).GetValue(firstFlow);
                var workflowView = workflow.GetType().GetField("view", fields).GetValue(workflow);
                workflowView.GetType().GetMethod("HideBuildFacilityDraft").Invoke(workflowView, null);
                retire.Invoke(coordinator, new[] { firstFlow });
                yield return null;
                Assert.That(active.GetValue(coordinator), Is.SameAs(secondFlow));
                Assert.That(page.gameObject.activeInHierarchy, Is.True); Assert.That(pages().Length, Is.EqualTo(1));
                Record("旧回调及非活动退役不误隐藏");
                clear.Invoke(coordinator, null); yield return null;
                Assert.That(page.gameObject.activeSelf, Is.False); Assert.That(pages().Length, Is.EqualTo(1));
                render.Invoke(coordinator, new object[] { Scope(secondOwner).ProjectInput() }); yield return null;
                Assert.That(page.gameObject.activeInHierarchy, Is.True); Assert.That(page.GetInstanceID(), Is.EqualTo(pageId));
                Record("Clear后相同版本恢复");
                retire.Invoke(coordinator, new[] { secondFlow }); yield return null;
                Assert.That(page.gameObject.activeSelf, Is.False); Assert.That(pages().Length, Is.EqualTo(1));
                string thirdOwner = AddFlow();
                render.Invoke(coordinator, new object[] { Scope(thirdOwner).ProjectInput() }); yield return null;
                Assert.That(page.GetInstanceID(), Is.EqualTo(pageId)); Assert.That(pages().Length, Is.EqualTo(1));
                Record("活动退役后新Flow仍复用");
                nativeCoordinator.Dispose(); nativeCoordinator.Dispose();
                yield return null; yield return null;
                Assert.That(pages(), Is.Empty); Assert.That(page == null, Is.True);
                Assert.That(((IDictionary)flowsField.GetValue(coordinator)).Count, Is.Zero);
                render.Invoke(coordinator, new object[] { Scope(thirdOwner).ProjectInput() }); yield return null;
                Assert.That(pages(), Is.Empty); Record("Dispose释放且不重建");
                Assert.That(fixture.Commands, Is.Empty, "页面生命周期定位禁止把历史投影变为游戏提交");
            }
            finally
            {
                var output = Environment.GetEnvironmentVariable("YC_BUILD_PAGE_LIFECYCLE_EVIDENCE");
                if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    Kind = "真实Prefab资源生命周期隔离，非正常页面/整局验收", HudPath = hudPath, PagePath = pagePath,
                    HudGuid = AssetDatabase.AssetPathToGUID(hudPath), PageGuid = AssetDatabase.AssetPathToGUID(pagePath), Records = records }));
            }
        }

        [Test]
        public void ZeroWalletKeepsWholeDeckAndSelectsIntoRealPreparationOutsideMainAction()
        {
            var f = new Fixture(0);
            var original = f.Scope();
            var options = f.Options();
            Assert.That(options.Select(option => option.FacilityId), Is.EqualTo(original.Selection.CandidateIds));
            Assert.That(options.Count, Is.GreaterThan(6));
            Assert.That(options.Select(option => option.FacilityId).Distinct().Count(), Is.EqualTo(options.Count));
            Assert.That(options.Any(option => !option.CanBuild && option.PreparationValidation.IsValid), Is.True);
            Assert.That(CanRender(f, original.ProjectInput()), Is.True);
            f.Start(); f.SelectTarget();
            var payment = f.Visible.PreparedActionPayments.Single(value => value.AuthorizationKind == "effect_build");
            Assert.That(payment.IsReady && payment.CanCancel, Is.True);
            Assert.That(payment.CanConfirm, Is.False);
            Assert.That(payment.SelectionOwnerEffectId, Is.EqualTo(f.Owner));
            Assert.That(payment.SelectionInteractionId, Is.EqualTo(original.Selection.InteractionId));
            var model = f.Workflow.BuildDraftViewModel();
            Assert.That(model.IsEffectBuild, Is.True); Assert.That(model.IsSupplySource, Is.False);
            Assert.That(model.Options.Count, Is.EqualTo(options.Count));
            Assert.That(CanUsePayment(model, "resources") || CanUsePayment(model, "gold"), Is.False);
            Assert.That(f.Public.EffectRuntime.EffectNodes, Is.Empty);
            Assert.That(f.Public.EffectRuntime.OperationWindows, Is.Empty);
            Assert.That(f.Public.Decks.FacilityDeck, Is.Empty);
            Assert.That(f.Scope().Selection.Status, Is.EqualTo("invalidated"));
            Assert.That(CanRender(f, f.Scope().ProjectInput()), Is.True, "P的正式付款输入仍进原建设宿主");
            Assert.That(f.Commands.Single().Parameters["operation"], Is.EqualTo("prepare_effect_build"));
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void EffectOnlyWindowPreparationResolvesItsGrantedRequestBeforeHandler()
        {
            var f = new Fixture(0, GamePhase.ResourceCollection, false); f.Start(); f.SelectTarget();
            Assert.That(f.Scope().Payment.IsReady, Is.True);
        }

        // 另标处理器隔离定位：不替代上方真实Session失败，也不计正常联机验收。
        [Test]
        public void IsolatedHandlerWorkflowSelectsWholeDeckAndPaysGoldOnceThroughP()
        {
            var f = new Fixture(18, isolatedHandlers: true); var count = f.Options().Count;
            var supply = f.State.Decks.FacilitySupply.ToArray(); f.Start(); f.SelectTarget();
            var scope = f.Scope();
            Assert.That(scope.Payment.IsReady, Is.True); Assert.That(scope.Payment.CanConfirm, Is.False);
            Assert.That(CanRender(f, scope.ProjectInput()), Is.True);
            Assert.That(f.Workflow.BuildDraftViewModel().Options.Count, Is.EqualTo(count));
            Assert.That(CanUsePayment(f.Workflow.BuildDraftViewModel(), "gold"), Is.True);
            if (f.Workflow.TryPreparePayment("gold")) f.Workflow.Confirm();
            Assert.That(f.Results.All(result => result.Succeeded), Is.True, string.Join(";", f.Results.Select(result => result.LogMessage)));
            Assert.That(f.Commands.Count, Is.EqualTo(4));
            Assert.That(f.Commands.Last().Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            Assert.That(f.State.Decks.FacilitySupply, Is.EqualTo(supply));
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void IsolatedHandlerMainPendingGateDoesNotIgnoreEffectPreparation()
        {
            var f = new Fixture(18, withMainWindow: false, isolatedHandlers: true); f.Start(); f.SelectTarget();
            var ordinary = new ActionPaymentPreparation(f, new CommandGateway(f.Port), "build", () => { }, _ => { });
            ordinary.GetVisibleState = () => f.Visible;
            Assert.That(ordinary.Current, Is.Null);
            var requests = f.Public.EffectRuntime.InteractionRequests.Where(request => request.Status == "open").ToArray();
            Assert.That(requests.Length, Is.EqualTo(1), "只保留P当前付款输入，排除清理窗口等其他请求掩盖错误豁免");
            Assert.That(requests[0].OwnerEffectId, Is.EqualTo(f.Scope().Payment.PaymentOwnerEffectId));
            Assert.That(ActionPaymentPreparation.IsPreparedActionRequest(requests[0]), Is.True);
            Assert.That(ActionPaymentPreparation.HasBlockingChoice(requests, 1), Is.True, "正式效果付款必须保留为主要行动待办");
        }

        [Test]
        public void IsolatedHandlerDeferredGoldWaitsForCorrectReceiptAndViewer()
        {
            var f = new Fixture(18, isolatedHandlers: true) { Deferred = true };
            f.Start(); f.SelectTarget(); f.Ack(f.Commands.Last(), true);
            f.Workflow.TryPreparePayment("gold"); var cancel = f.Commands.Last();
            f.Ack(new GameCommand { CommandId = cancel.CommandId + ".wrong" }, true);
            Assert.That(f.Commands.Count, Is.EqualTo(2));
            f.Ack(cancel, true); var gold = f.Commands.Last();
            Assert.That(f.Commands.Count, Is.EqualTo(3));
            f.Refresh(2); f.Ack(gold, true);
            Assert.That(f.Commands.Count, Is.EqualTo(3));
            f.Refresh(1); f.Workflow.RefreshEffectBuild();
            Assert.That(f.Commands.Count, Is.EqualTo(4));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            f.Workflow.RefreshEffectBuild(); f.Ack(gold, true);
            Assert.That(f.Commands.Count, Is.EqualTo(4));
        }

        [Test]
        public void OneGoldClickCancelsPreparesAndAnswersPaymentOwnerOnceWithoutRefillingSupply()
        {
            var f = new Fixture(18); var supply = f.State.Decks.FacilitySupply.ToArray(); var deckCount = f.State.Decks.FacilityDeck.Count;
            f.Start(); var item = f.Target(); f.SelectTarget();
            var resources = f.Scope().Payment;
            Assert.That(resources.CanConfirm, Is.False);
            Assert.That(CanUsePayment(f.Workflow.BuildDraftViewModel(), "gold"), Is.True);
            if (f.Workflow.TryPreparePayment("gold")) f.Workflow.Confirm();
            Assert.That(f.Results.All(result => result.Succeeded), Is.True, string.Join(";", f.Results.Select(result => result.LogMessage)));
            Assert.That(f.Commands.Select(command => command.Kind), Is.EqualTo(new[] {
                GameCommandKind.PreparePaymentDraft, GameCommandKind.PreparePaymentDraft,
                GameCommandKind.PreparePaymentDraft, GameCommandKind.AnswerInteraction }));
            Assert.That(f.Commands.Take(3).Select(command => command.Parameters["operation"]),
                Is.EqualTo(new[] { "prepare_effect_build", "cancel_effect_build", "prepare_effect_build" }));
            var pay = f.Commands.Last();
            var request = f.State.EffectRuntime.InteractionRequests.Single(value => value.GetStableInteractionId() == pay.Parameters["interactionId"]);
            var payer = f.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == request.OwnerEffectId);
            Assert.That(payer.Payment.Preparation.SelectionOwnerEffectId, Is.EqualTo(f.Owner));
            Assert.That(request.OwnerEffectId, Is.Not.EqualTo(f.Owner), "最终回答P，不回答B历史输入");
            Assert.That(pay.OptionIds, Is.EqualTo(new[] { "pay" }));
            Assert.That(payer.Payment.IsCommitted, Is.True);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(f.State.Decks.FacilitySupply, Is.EqualTo(supply));
            Assert.That(f.State.Decks.FacilityDeck.Count, Is.EqualTo(deckCount - 1));
            Assert.That(f.Visible.Interactions.Any(value => value.Status == "open" && value.Kind == FacilityBuildEffectExecutor.PlacementInteractionType), Is.True);
            Assert.That(f.Session.Submit(pay).Succeeded, Is.False);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            Assert.That(f.Commands.Any(command => command.Kind == GameCommandKind.BuildFacility), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OriginalCancelAnswersCurrentBOrPAndDoesNotPay(bool withPreparation)
        {
            var f = new Fixture(0); f.Start();
            if (withPreparation) f.SelectTarget();
            var scope = f.Scope(); var expectedOwner = withPreparation ? scope.Payment.PaymentOwnerEffectId : f.Owner;
            f.Workflow.CancelExplicitly();
            var answer = f.Commands.Last();
            Assert.That(answer.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(answer.Parameters["answerValue"], Is.EqualTo("false"));
            var request = f.State.EffectRuntime.InteractionRequests.Single(value => value.GetStableInteractionId() == answer.Parameters["interactionId"]);
            Assert.That(request.OwnerEffectId, Is.EqualTo(expectedOwner));
            Assert.That(f.Results.Last().Succeeded, Is.True, f.Results.Last().LogMessage);
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == f.Owner).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
        }

        [Test]
        public void MainCurrentAndDraftClearingCannotBorrowEffectPreparation()
        {
            var f = new Fixture(18, withMainWindow: false); f.Start(); f.SelectTarget();
            var ordinary = new ActionPaymentPreparation(f, new CommandGateway(f.Port), "build", () => { }, _ => { });
            ordinary.GetVisibleState = () => f.Visible;
            Assert.That(ordinary.Current, Is.Null); Assert.That(ordinary.HasPreparation, Is.False);
            var open = f.Public.EffectRuntime.InteractionRequests.Where(value => value.Status == "open").ToArray();
            Assert.That(open.Length, Is.EqualTo(1), "正式Session中也只留P，不能借其他待办使HasBlockingChoice假通过");
            Assert.That(open[0].OwnerEffectId, Is.EqualTo(f.Scope().Payment.PaymentOwnerEffectId));
            Assert.That(ActionPaymentPreparation.HasBlockingChoice(f.Public, 1), Is.True, "效果待办不能被当作主行动准备忽略");
            Assert.That(ActionPaymentPreparation.IsPreparedActionRequest(f.Public.EffectRuntime.InteractionRequests.Single(value =>
                value.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId)), Is.True, "通用渲染仍识别已有准备，避免另弹确认页");
            Assert.That(new BuildFacilityOptionQueryService().Query(f.Public, 1, f.Target(), f.Visible).CanBuild, Is.False);
        }

        [Test]
        public void DeferredSwitchResumesOnlyAfterExactReceiptAndOriginalViewerReturns()
        {
            var f = new Fixture(18) { Deferred = true }; f.Start(); f.SelectTarget();
            f.Ack(f.Commands.Last(), true);
            f.Workflow.TryPreparePayment("gold"); var cancel = f.Commands.Last();
            f.Workflow.NotifyInteractionCommandResolved(f.State.GameId, cancel.CommandId + ".wrong", true, default);
            Assert.That(f.Commands.Count, Is.EqualTo(2));
            f.Ack(cancel, true); var gold = f.Commands.Last();
            Assert.That(gold.Parameters["paymentMode"], Is.EqualTo("gold"));
            f.Refresh(2); f.Ack(gold, true);
            Assert.That(f.Commands.Count, Is.EqualTo(3), "别的查看者不能代原玩家续付");
            f.Refresh(1); f.Workflow.RefreshEffectBuild();
            Assert.That(f.Commands.Count, Is.EqualTo(4));
            Assert.That(f.Commands.Last().Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            f.Workflow.RefreshEffectBuild(); f.Ack(gold, true);
            Assert.That(f.Commands.Count, Is.EqualTo(4));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
        }

        [Test]
        public void RejectedGoldPreparationDoesNotResignOnRefreshOrLateSuccess()
        {
            var f = new Fixture(18) { Deferred = true }; f.Start(); f.SelectTarget(); f.Ack(f.Commands.Last(), true);
            f.Workflow.TryPreparePayment("gold"); var cancel = f.Commands.Last(); f.RejectNext = true; f.Ack(cancel, true);
            var rejected = f.Commands.Last(); f.Ack(rejected, false);
            f.Workflow.RefreshEffectBuild(); f.Ack(rejected, true);
            Assert.That(f.Commands.Count, Is.EqualTo(3));
            Assert.That(f.Commands.Any(command => command.Kind == GameCommandKind.AnswerInteraction), Is.False);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(18));
        }

        [TestCase("game")]
        [TestCase("player")]
        [TestCase("revision")]
        [TestCase("selection")]
        [TestCase("candidate_version")]
        public void ForeignOrStalePreparedProjectionCannotBeUsedAsEffectScope(string mismatch)
        {
            var f = new Fixture(18); f.Start(); f.SelectTarget();
            var view = GameStateCloneService.DeepClone(f.Visible);
            var payment = view.PreparedActionPayments.Single(value => value.AuthorizationKind == "effect_build");
            if (mismatch == "game") view.GameId += ".other";
            if (mismatch == "player") view.ViewerPlayerId = 2;
            if (mismatch == "revision") view.StateRevision--;
            if (mismatch == "selection") payment.SelectionInteractionId += ".other";
            if (mismatch == "candidate_version") payment.SelectionCandidateSetVersion++;
            Assert.That(EffectBuildScope.TryReadDeckBuildScope(f.Public, view, 1, f.Owner, out _), Is.False);
        }

        // 本批供应正常费用接缝：隔离授予/余额不计正常可见游戏验收，命令始终走真实Session。
        [Test]
        public void SupplyGrantKeepsUnpayableCandidatesAndBindsEachRealSlotBeforePayment()
        {
            var f = new Fixture(0, withMainWindow: false, sourceZone: "supply");
            var options = f.Options(); var scope = f.Scope();
            Assert.That(options.Select(value => value.FacilityId), Is.EqualTo(scope.Selection.CandidateIds));
            Assert.That(options.Count, Is.GreaterThan(1));
            Assert.That(options.Select(value => value.SourceZoneId).Distinct().Count(), Is.EqualTo(options.Count));
            Assert.That(options.Any(value => !value.CanBuild && value.PreparationValidation.IsValid), Is.True);
            var slotIds = options.Where(value => value.PreparationValidation.IsValid).Select(value => value.FacilityId).ToArray();
            Assert.That(slotIds.Length, Is.GreaterThan(1));
            f.Start();
            foreach (var itemId in new[] { slotIds.First(), slotIds.Last() })
            {
                f.Workflow.SelectFacility(itemId);
                Assert.That(f.Results.Last().Succeeded, Is.True, f.Results.Last().LogMessage);
                var prepare = f.Commands.Last();
                var actual = f.Visible.ItemZones.Items.Single(value => value.ItemId == itemId);
                Assert.That(prepare.Parameters["expectedSourceZoneId"], Is.EqualTo(actual.ZoneId));
                Assert.That(f.Scope().Payment.TargetId, Is.EqualTo(itemId));
                Assert.That(f.Scope().Payment.IsReady, Is.True); Assert.That(f.Scope().Payment.CanConfirm, Is.False);
            }
            var active = f.Scope(); var command = EffectInteractionCommands.CancelEffectBuildPayment(1,
                active.Payment.PreparationOwnerEffectId, f.Visible.StateRevision);
            Assert.That(f.Session.Submit(command).Succeeded, Is.True); f.Refresh(1);
            var current = f.Scope();
            var wrong = EffectInteractionCommands.PrepareEffectBuildPayment(current, slotIds.First(), "resources");
            wrong.Parameters["expectedSourceZoneId"] = options.Last(value => value.FacilityId != slotIds.First()).SourceZoneId;
            Assert.That(f.Session.Submit(wrong).Succeeded, Is.False, "真实卡不能借用别的供应槽");
            var stale = EffectInteractionCommands.PrepareEffectBuildPayment(current, slotIds.First(), "resources");
            stale.Parameters["expectedRevision"] = (f.Visible.StateRevision - 1).ToString();
            Assert.That(f.Session.Submit(stale).Succeeded, Is.False);
            f.Workflow.RefreshEffectBuild(); f.Workflow.CancelExplicitly();
            Assert.That(f.Results.Last().Succeeded, Is.True, f.Results.Last().LogMessage);
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void RaytheanThirdLevelPaysConditionOnceThenBuildsThroughOriginalPaymentOwner()
        {
            var f = new Fixture(30, withMainWindow: false, sourceZone: "supply",
                grantScript: "lua/enterprises/enterprise.raythean.level_3.special.lua");
            Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard, Is.Zero, "原Condition已扣2碎，后续设施费是另一笔义务");
            Assert.That(f.Scope().Input.Kind, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            f.Start(); var selected = f.Options().First(value => value.PreparationValidation.IsValid &&
                FacilityCardDatabase.Get(f.Public, value.DefinitionId).ResourceCost.Enumerate().Any(cost => cost.Value > 0));
            var definition = FacilityCardDatabase.Get(f.Public, selected.DefinitionId);
            var beforeSupply = f.State.Decks.FacilitySupply.ToArray();
            f.Workflow.SelectFacility(selected.FacilityId);
            Assert.That(f.Results.Last().Succeeded, Is.True, f.Results.Last().LogMessage);
            if (f.Workflow.TryPreparePayment("gold")) f.Workflow.Confirm();
            Assert.That(f.Results.All(value => value.Succeeded), Is.True, string.Join(";", f.Results.Select(value => value.LogMessage)));
            var pay = f.Commands.Last(); Assert.That(pay.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(pay.OptionIds, Is.EqualTo(new[] { "pay" }));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(30 - definition.GoldVoucherCost));
            Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(f.State.Decks.FacilitySupply.Count, Is.EqualTo(beforeSupply.Length));
            Assert.That(f.State.Decks.FacilitySupply, Does.Not.Contain(selected.FacilityId));
            Assert.That(f.Visible.Interactions.Any(value => value.Status == "open" && value.Kind == FacilityBuildEffectExecutor.PlacementInteractionType), Is.True);
            Assert.That(f.Commands.Any(value => value.Kind == GameCommandKind.BuildFacility), Is.False);
            Assert.That(f.Session.Submit(pay).Succeeded, Is.False);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(30 - definition.GoldVoucherCost));
        }

        [Test]
        public void StoredSupplyGrantUsesOnlyGrantedVisibleCarrierAndDoesNotRefillSupply()
        {
            var f = new Fixture(30, GamePhase.ActionRound1, withMainWindow: false, sourceZone: "supply", includeInfected: true,
                playerCount: 4, seed: 713);
            void AnswerLatest(string candidate = null, bool decline = false)
            {
                var request = f.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open" && value.AnsweringPlayerId == 1);
                var answer = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, 1), 1,
                    candidate == null ? null : new[] { candidate }, decline);
                var result = f.Session.Submit(answer); Assert.That(result.Succeeded, Is.True, result.LogMessage); f.Refresh(1);
            }
            void Run(EffectSpec spec)
            {
                var executor = new EffectTreeExecutor(f.State, f.Registry); executor.CreateRoot(spec);
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            }
            AnswerLatest(decline: true);
            // 仅隔离准备可露希尔正式常驻技能；这段不计正常游戏行动覆盖。
            f.State.CurrentPlayerId = 1;
            CharacterCoverEffectExecutor.Register(f.Registry); CharacterLifecycleService.Register(f.Registry);
            var carriers = f.State.ItemZones.Items.Where(value => value.DefinitionId == "operator.closure").ToArray();
            Assert.That(carriers.Length, Is.EqualTo(1), "本储备隔离夹具必须含正式可露希尔实体");
            var carrier = carriers[0];
            Run(MoveSpec(1, new[] { carrier.ItemId }, carrier.ZoneId, FindZone(f.State, "hand", 1).ZoneId,
                "unordered", 1, "core.character_card_transfer"));
            Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); AnswerLatest(carrier.ItemId);
            Run(CharacterLifecycleService.CreateUseSpec(1, carrier.ItemId, "tactic"));
            var choice = f.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open" && value.AnsweringPlayerId == 1);
            string held = choice.CandidateIds.First(id => f.State.ItemZones.Items.Any(value => value.ItemId == id &&
                FacilityCardDatabase.Get(f.State, value.DefinitionId)?.ResourceCost.Enumerate().Any(cost => cost.Value > 0) == true));
            AnswerLatest(held); AnswerLatest(decline: true);
            var storage = f.State.ItemZones.Items.Single(value => value.ItemId == held).ZoneId;
            var executor2 = new EffectTreeExecutor(f.State, f.Registry);
            f.Owner = executor2.CreateRoot(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, playerId: 1));
            Assert.That(executor2.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage); f.Refresh(1);
            var scope = f.Scope(); Assert.That(scope.Selection.CandidateIds, Does.Contain(held));
            var option = f.Options().Single(value => value.FacilityId == held);
            Assert.That(option.SourceZoneId, Is.EqualTo(storage));
            var missingView = GameStateCloneService.DeepClone(f.Visible);
            missingView.ItemZones.Items.RemoveAll(value => value.ItemId == held);
            Assert.That(EffectBuildScope.TryReadDeckBuildScope(f.State, missingView, 1, f.Owner, out _), Is.False,
                "即使调用方持有全态，也不能为本人View缺失的储备实例回读Host补源");
            var foreignView = GameStateCloneService.DeepClone(f.Visible);
            foreignView.ItemZones.Items.Single(value => value.ItemId == carrier.ItemId).ControllerSubject = YC.Domain.Items.ItemSubjectRef.Player(2);
            Assert.That(EffectBuildScope.TryReadDeckBuildScope(f.Public, foreignView, 1, f.Owner, out _), Is.False,
                "获准候选也不能借用他人载体来源");
            var supplies = f.State.ItemZones.Items.Where(value => f.State.ItemZones.Zones.Any(zone => zone.ZoneId == value.ZoneId &&
                zone.TemplateId == "core.zone.facility_supply_slot")).Select(value => value.ItemId).ToArray();
            f.Start(); f.Workflow.SelectFacility(held);
            Assert.That(f.Results.Last().Succeeded, Is.True, f.Results.Last().LogMessage);
            Assert.That(f.Commands.Last().Parameters["expectedSourceZoneId"], Is.EqualTo(storage));
            if (f.Workflow.TryPreparePayment("gold")) f.Workflow.Confirm();
            Assert.That(f.Results.All(value => value.Succeeded), Is.True, string.Join(";", f.Results.Select(value => value.LogMessage)));
            Assert.That(f.State.ItemZones.Items.Single(value => value.ItemId == held).ZoneId, Is.Not.EqualTo(storage));
            Assert.That(f.State.ItemZones.Items.Where(value => f.State.ItemZones.Zones.Any(zone => zone.ZoneId == value.ZoneId &&
                zone.TemplateId == "core.zone.facility_supply_slot")).Select(value => value.ItemId), Is.EqualTo(supplies));
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
        }

        [TestCase("lua/facilities/build_additional_facility.lua", false)]
        [TestCase("lua/city_styles/infected.city_style.emerging_buildings.declared.lua", true)]
        public void EntrySupplyGrantOpensOriginalBuildDirectlyAndCanDeclineWithoutWallet(string script, bool prepare)
        {
            var f = new Fixture(0, withMainWindow: false, sourceZone: "supply", grantScript: script, includeInfected: true);
            Assert.That(f.Scope().Input.Kind, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Count(value => value.Status == "open"), Is.EqualTo(1));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            f.Start();
            if (prepare)
            {
                var target = f.Options().First(value => value.PreparationValidation.IsValid).FacilityId;
                f.Workflow.SelectFacility(target);
                Assert.That(f.Results.Last().Succeeded, Is.True, f.Results.Last().LogMessage);
            }
            f.Workflow.CancelExplicitly();
            Assert.That(f.Results.Last().Succeeded, Is.True, f.Results.Last().LogMessage);
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(value => value.EffectId == f.Owner).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void FixedGoldSupplyGrantDoesNotPrepareOrQuoteUnauthorizedResourceMode()
        {
            var f = new Fixture(30, withMainWindow: false, sourceZone: "supply", paymentMode: "gold");
            var option = f.Options().First(value => value.PreparationValidation.IsValid);
            Assert.That(option.ResourcesPayment.IsAvailable, Is.False); Assert.That(option.GoldPayment.IsAvailable, Is.True);
            f.Start(); f.Workflow.SelectFacility(option.FacilityId);
            Assert.That(f.Commands, Is.Empty, "旧工作流默认资源准备不能越过本次固定gold许可");
            if (f.Workflow.TryPreparePayment("gold")) f.Workflow.Confirm();
            Assert.That(f.Results.All(value => value.Succeeded), Is.True);
            Assert.That(f.Commands.Count, Is.EqualTo(2));
            Assert.That(f.Commands[0].Parameters["paymentMode"], Is.EqualTo("gold"));
            Assert.That(f.Commands[1].OptionIds, Is.EqualTo(new[] { "pay" }));
        }

        [TestCase("supply", "free")]
        [TestCase("reserve", "auto")]
        public void FreeOrReserveBusinessIsNotTakenOverByNormalPaidBuildHost(string source, string mode)
        {
            var f = new Fixture(0, withMainWindow: false, sourceZone: source, paymentMode: mode);
            Assert.That(EffectBuildScope.TryReadDeckBuildScope(f.Public, f.Visible, 1, f.Owner, out _), Is.False);
            var requests = f.State.EffectRuntime.InteractionRequests.Where(value => value.Status == "open").ToArray();
            Assert.That(requests.Length, Is.GreaterThan(0), "只核实际仍有选择的免费/保留授予边界");
            foreach (var request in requests)
                Assert.That(CanRender(f, InteractionRequestProjector.ProjectForPlayer(request, 1)), Is.False);
            Assert.That(f.Commands, Is.Empty);
        }

        private static bool CanUsePayment(BuildFacilityDraftViewModel model, string mode) => (bool)
            Type.GetType("YC.Presentation.FacilityBuildDialogView, Assembly-CSharp", true)
                .GetMethod("CanUseBuildPayment", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { model, mode });
        private static bool CanRender(Fixture f, InteractionRequestProjection input)
        {
            var type = Type.GetType("YC.Presentation.FacilityBuildRequestUiCoordinator, Assembly-CSharp", true);
            var renderer = Activator.CreateInstance(type, new object[] {
                (Func<GameState>)(() => f.Public), (Func<GameStateView>)(() => f.Visible), (Func<int>)(() => 1),
                null, null, f.Port, (Func<bool>)(() => true), (Action)(() => { }), (Action<string>)(_ => { }) });
            return (bool)type.GetMethod("CanRender").Invoke(renderer, new object[] { input });
        }

        private sealed class Fixture : IWritableGameplayContext
        {
            public readonly GameState State;
            public readonly EffectRegistry Registry = new EffectRegistry();
            public readonly GameSession Session;
            public readonly Port Port;
            public string Owner;
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public readonly List<CommandResult> Results = new List<CommandResult>();
            public GameStateView Visible;
            public GameState Public;
            public BuildInteraction Workflow;
            public bool Deferred, RejectNext;
            public readonly bool IsolatedHandlers;
            public GameState CurrentState => Public;
            public int LocalPlayerId => 1;
            public bool ControlsCurrentPlayerLocally => false;
            public void SetLocalPlayerId(int id) { }
            public Fixture(int gold, GamePhase phase = GamePhase.Cleanup, bool withMainWindow = true, bool isolatedHandlers = false,
                string sourceZone = "deck", string grantScript = null, bool includeInfected = false, string paymentMode = "auto",
                int playerCount = 3, int seed = 321)
            {
                IsolatedHandlers = isolatedHandlers;
                var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
                var config = new GameRuleConfiguration { PlayerCount = playerCount, MapId = YC.Domain.Maps.StaticMapDefinitions.FourPlayerMapId,
                    EnabledExpansionIds = new List<string> { "core", "enterprise" } };
                if (includeInfected) config.EnabledExpansionIds.Add("infected");
                var catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
                State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, Enumerable.Range(1, playerCount)
                    .Select(id => new PlayerSeat { PlayerId = id }).ToList(), config, seed, catalog.SetupRecipe, catalog.ContentDefinitions);
                State.Phase = phase; State.CurrentPlayerId = 2;
                if (grantScript != null || phase == GamePhase.ActionRound1)
                { State.Round = 1; State.ActionRound = 1; State.Phase = GamePhase.ActionRound1; }
                State.FindPlayer(1).Resources = new ResourceSet { GoldVoucher = gold,
                    OriginiumShard = grantScript?.EndsWith("enterprise.raythean.level_3.special.lua", StringComparison.Ordinal) == true ? 2 : 0 };
                State.FindPlayer(1).RemainingMainActionsThisTurn = 0;
                catalog.Register(Registry); GenericLuaEffectExecutor.Register(Registry); ResourceEffectExecutor.Register(Registry);
                ResourcePaymentChoiceEffectExecutor.Register(Registry); ItemTransferEffectExecutor.Register(Registry);
                FacilityBuildEffectExecutor.Register(Registry); FacilitySelectionEffectExecutor.Register(Registry);
                FacilityEntryEffectExecutor.Register(Registry); RepeatEffectExecutor.Register(Registry);
                if (withMainWindow)
                {
                    var builder = new RoundMainChainBuilder(); var order = new List<int> { 1, 2, 3 };
                    var chain = builder.Build(State.GameId, builder.CreateRoundExecutionId(State.GameId, 2, order), 2, order, 1);
                    var window = chain.Nodes.Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && node.PlayerId == 1);
                    foreach (var node in chain.Nodes.Where(node => node.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
                    var runtime = State.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2;
                    runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
                    runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
                    var validation = new RoundExecutionService(Registry).Advance(State);
                    Assert.That(validation.IsValid, Is.True, validation.Reason);
                    State.FindPlayer(1).RemainingMainActionsThisTurn = 0;
                }
                var executor = new EffectTreeExecutor(State, Registry);
                if (grantScript == null)
                    Owner = executor.CreateRoot(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId,
                        Object("sourceZone", NormalizedValue.CreateString(sourceZone), "paymentMode", NormalizedValue.CreateString(paymentMode),
                            "allowDecline", NormalizedValue.CreateBoolean(true)), 1));
                else
                {
                    string script = pack.GetScript(grantScript);
                    var dispatcher = new LuaEffectEventDispatcher(Registry);
                    var scriptDefinition = new LuaScriptDefinition("test.supply.formal", "test.supply.grant", "test.supply.primary",
                        "1.0.0", script, LuaContentHasher.ComputeSha256(script));
                    dispatcher.Register(new LuaHandlerDefinition("test.supply.primary", "TestSupplyGranted", "", scriptDefinition,
                        EffectHandlerRole.Primary, abilityId: "test.supply.grant"));
                    Registry.Register("test.supply.activate", c => EffectStepResult.Completed().AddEvent(
                        new EffectEventRequest { EventType = "TestSupplyGranted", PlayerId = 1 }));
                    executor.CreateRoot(EffectSpec.Create("test.supply.activate", playerId: 1));
                }
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, State.EffectRuntime.LastFaultMessage);
                Session = new GameSession(State); Session.RegisterHandler(new PreparePaymentDraftCommandHandler(Registry));
                Session.RegisterHandler(new AnswerInteractionCommandHandler(Registry));
                if (grantScript != null)
                {
                    int count = 0;
                    while (!State.EffectRuntime.InteractionRequests.Any(value => value.Status == "open" &&
                        value.InteractionTypeId == FacilitySelectionEffectExecutor.NormalBuildInteractionType) && count++ < 4)
                    {
                        var request = State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open" && value.AnsweringPlayerId == 1);
                        var projection = InteractionRequestProjector.ProjectForPlayer(request, 1);
                        var answer = EffectInteractionCommands.Answer(projection, 1, new[] { request.CandidateIds.First() });
                        var result = Session.Submit(answer);
                        Assert.That(result.Succeeded, Is.True, result.LogMessage);
                    }
                    Owner = State.EffectRuntime.EffectNodes.Single(value => value.EffectTypeId == FacilitySelectionEffectExecutor.TypeId &&
                        value.PlayerId == 1).EffectId;
                }
                Port = new Port(this); Refresh(1);
            }
            public EffectBuildScope Scope()
            {
                Assert.That(EffectBuildScope.TryReadDeckBuildScope(Public, Visible, 1, Owner, out var scope), Is.True, "正式牌堆建设作用域应可读");
                return scope;
            }
            public IReadOnlyList<BuildFacilityOptionQueryResult> Options() => new BuildFacilityOptionQueryService().QueryForEffectBuild(Public, 1, Visible, Owner);
            public string Target() => Options().First(option => option.PreparationValidation.IsValid &&
                FacilityCardDatabase.Get(Public, option.DefinitionId).GoldVoucherCost == 10 && option.EffectiveResourceCost.Enumerate().Any(cost => cost.Value > 0)).FacilityId;
            public void Start()
            {
                Workflow = new BuildInteraction(this, Port, new View(), new InteractionFlowCoordinator(), () => false, _ => { });
                Workflow.GetVisibleState = () => Visible; Workflow.BeginEffectBuild(Scope());
            }
            public void SelectTarget()
            {
                Workflow.SelectFacility(Target());
                Assert.That(Results.Count, Is.GreaterThan(0), "选卡必须发正式准备命令");
                Assert.That(Results.Last().Succeeded, Is.True, Results.Last().LogMessage);
            }
            public void Ack(GameCommand command, bool applied) => Workflow.NotifyInteractionCommandResolved(State.GameId, command.CommandId, applied,
                applied ? default : CommandErrorCode.InvalidTarget);
            public void Refresh(int viewer)
            {
                Visible = Session.ProjectFor(GameStateViewer.Player(viewer));
                Public = (GameState)Type.GetType("YC.Presentation.PlayerUiReadStateFactory, Assembly-CSharp", true)
                    .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { State, Visible, viewer });
                Assert.That(Public, Is.Not.Null);
            }
        }
        private sealed class Port : IGameCommandPort
        {
            private readonly Fixture fixture;
            public Port(Fixture fixture) { this.fixture = fixture; }
            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                fixture.Commands.Add(command);
                var result = fixture.RejectNext ? CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "隔离准确拒绝")) :
                    fixture.IsolatedHandlers ? command.Kind == GameCommandKind.PreparePaymentDraft ?
                        new PreparePaymentDraftCommandHandler(fixture.Registry).Handle(fixture.State, command) :
                        new AnswerInteractionCommandHandler(fixture.Registry).Handle(fixture.State, command) : fixture.Session.Submit(command);
                fixture.RejectNext = false; fixture.Results.Add(result); fixture.Refresh(1);
                return new WorkflowSubmissionResult(fixture.Deferred ? CommandResult.SuccessResult(new List<GameEvent>(), "等待隔离回执") : result, !fixture.Deferred);
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
