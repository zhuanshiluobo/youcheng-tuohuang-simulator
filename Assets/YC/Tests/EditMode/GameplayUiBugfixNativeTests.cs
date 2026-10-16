using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Maps;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 复用既有正式内容与真实 SampleScene 装配；只经生产 UI 回调提交受测命令。
    // 准备阶段可以摆放实际物件，不复制规则或修改生产资产。
    public sealed class GameplayUiBugfixNativeTests
    {
        private SceneRuntimeCatalogScope runtimeCatalogs;
        private void PrepareRuntime()
        {
            runtimeCatalogs = new SceneRuntimeCatalogScope();
        }
        [UnityTearDown] public IEnumerator LeaveRuntime()
        {
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
            runtimeCatalogs?.Dispose(); runtimeCatalogs = null;
        }
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type Existing = typeof(MainActionPreselectionUiTests);
        private static object Read(object target, string name) => target.GetType().GetField(name, Members)?.GetValue(target)
            ?? target.GetType().GetProperty(name, Members)?.GetValue(target);
        private static void Write(object target, string name, object value) => target.GetType().GetField(name, Members).SetValue(target, value);
        private static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethods(Members).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static object Prepare(string method, params object[] args)
        {
            try { return Existing.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static object Production(bool infected = true) => Prepare("Production", infected);
        private static GameState State(object fixture) => (GameState)Read(fixture, "State");
        private static void Run(object fixture, EffectSpec spec) => Prepare("Run", fixture, spec);
        private static void Cover(object fixture, string id) => Prepare("Cover", fixture, id);
        private static void Answer(object fixture, string id) => Prepare("Answer", fixture, id);
        private static void Position(GameState state, ItemInstanceState item, string zone) => Prepare("SetPosition", state, item, zone);
        private static void PrepareEnterpriseBoards(GameState state)
        {
            // 与现有企业夹具一致：只准备实际物件和正式合作轨，受测效果仍使用生产执行器。
            var publicZone = state.ItemZones.Zones.Single(z => z.LocalZoneId == "enterprises" && z.OwnerPlayerId == -1);
            foreach (var id in new[] { "infected.enterprise.rhodes_island", "infected.enterprise.stonehoove_security", "infected.enterprise.maylander_foundation", "enterprise.fussen_bank" })
            {
                var board = state.ItemZones.Items.Single(i => i.DefinitionId == id); Position(state, board, publicZone.ZoneId);
                foreach (var playerId in state.Players.Select(p => p.PlayerId).ToArray())
                {
                    var definition = new CardZoneDefinition { LocalZoneId = "cooperation_" + playerId,
                        ZoneTemplateId = "core.zone.board_marker_track", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                        AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Track,
                        VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                    Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, board.ItemId, definition, out var zone, out var reason), Is.True, reason);
                    var supply = state.ItemZones.Zones.Single(z => z.LocalZoneId == "player_markers" && z.OwnerPlayerId == playerId);
                    var marker = state.ItemZones.Items.First(i => i.ZoneId == supply.ZoneId && i.TypeId == "core.player_marker");
                    Position(state, marker, zone); marker.SlotId = "level_0";
                }
            }
        }

        private sealed class Native : IDisposable
        {
            private readonly object inner;
            private readonly GameObject contextRoot;
            public readonly object Mobile, Observed;
            public readonly GameSession Session;
            public GameState State => Session.State;
            public List<GameCommand> Commands => (List<GameCommand>)Read(Observed, "Commands");
            public WorkflowSubmissionResult Last => (WorkflowSubmissionResult)Read(Observed, "Last");
            public object Hud => Read(Mobile, "gameplayInteractionHud");
            public object Frame => Read(Hud, "Frame");
            public Native(object prepared)
            {
                inner = Activator.CreateInstance(Existing.GetNestedType("NativeExploreScene", BindingFlags.NonPublic),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { prepared }, null);
                Mobile = Read(inner, "Mobile"); Session = (GameSession)Read(inner, "Session"); Observed = Read(inner, "Observed");
            }
            private Native(object mobile, GameObject context)
            {
                Mobile = mobile; contextRoot = context; Session = (GameSession)Read(mobile, "session");
                Observed = new ObservationPort((IGameCommandPort)Read(mobile, "gameplayAdapter"));
                Write(mobile, "commandGateway", new CommandGateway((IGameCommandPort)Observed));
            }
            public static IEnumerator CreatePlayScene(object prepared, Action<Native> ready, int? savedViewer = null)
            {
                // 原生隔离测试的自动存档只写本次夹具目录，避免占用玩家的六个正式槽位。
                {
                    var saveType = Type.GetType("YC.Presentation.MatchSaveController, Assembly-CSharp", true);
                    var saves = saveType.GetMethod("Ensure", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                    Call(saves, "Unbind");
                    var storeType = Type.GetType("YC.Infrastructure.Persistence.MatchSaveStore, Assembly-CSharp", true);
                    Write(saves, "store", Activator.CreateInstance(storeType, Path.Combine(Environment.CurrentDirectory,
                        "Logs/GitMergeValidation/SaveResume-20261016/EventAndMoveFix/native-save-tests", Guid.NewGuid().ToString("N"))));
                }
                var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
                var previous = contextType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null) as Component;
                if (previous != null) UnityEngine.Object.Destroy(previous.gameObject);
                yield return null;
                var root = new GameObject("七项原生启动上下文"); root.SetActive(false);
                var context = root.AddComponent(contextType);
                var state = GameplayUiBugfixNativeTests.State(prepared);
                var seats = state.Players.Select(p => new PlayerSeat { PlayerId = p.PlayerId, Color = p.Color }).ToList();
                var order = state.LaunchPlayerOrder.ToList();
                Assert.That(TurnOrderService.IsValidPlayerOrder(order, seats.Select(p => p.PlayerId).ToList()), Is.True);
                Call(context, "Configure", LaunchMode.Local, 1, string.Empty, seats,
                    state.RuleConfiguration.Clone(), order, state.GameId, state.EventDeckSeed, GameplayRandomRuntime.Create(state.GameId));
                root.SetActive(true);
                var loading = SceneManager.LoadSceneAsync("SampleScene");
                while (!loading.isDone) yield return null;
                yield return null;
                if (!UnityEngine.Application.isBatchMode)
                {
                    var gameViewType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(assembly => assembly.GetType("UnityEditor.GameView", false)).First(type => type != null);
                    var gameView = UnityEditor.EditorWindow.GetWindow(gameViewType);
                    gameView.Show(); gameView.Focus(); gameView.Repaint();
                    yield return null; yield return null;
                }
                var mobile = UnityEngine.Object.FindObjectOfType(Type.GetType("YC.Presentation.MobileCityInteractionController, Assembly-CSharp", true));
                var native = new Native(mobile, root);
                native.Session.ReplaceState(state); Call(mobile, "SynchronizeInteractionFromState");
                if (savedViewer.HasValue)
                {
                    // 隔离存读档入口回归：通过正式恢复重新加载场景，不补调刷新来掩盖首次点击问题。
                    yield return native.ReloadSavedScene(savedViewer.Value, value => native = value);
                }
                ready(native);
            }
            public IEnumerator ReloadSavedScene(int savedViewer, Action<Native> ready)
            {
                var save = MatchSaveResumeTests.FromSession(Session, LaunchMode.Local, savedViewer);
                var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
                Call(contextRoot.GetComponent(contextType), "ConfigureSavedLocal", save);
                var loading = SceneManager.LoadSceneAsync("SampleScene");
                while (!loading.isDone) yield return null;
                yield return null;
                var mobile = UnityEngine.Object.FindObjectOfType(Type.GetType("YC.Presentation.MobileCityInteractionController, Assembly-CSharp", true));
                ready(new Native(mobile, contextRoot));
            }
            public void ObserveGateway(object target) => Write(target, "commandGateway", new CommandGateway((IGameCommandPort)Observed));
            public void ObserveActionPayment(object interaction)
            {
                ObserveGateway(interaction);
                Write(Read(interaction, "paymentPreparation"), "gateway", new CommandGateway((IGameCommandPort)Observed));
            }
            public void Applied() => Assert.That(Last?.CommandResult?.Succeeded == true && Last.AppliedLocally, Is.True, JsonConvert.SerializeObject(Last));
            public void Dispose()
            {
                if (inner != null) ((IDisposable)inner).Dispose();
                else if (contextRoot != null) UnityEngine.Object.Destroy(contextRoot);
            }
        }

        private sealed class ObservationPort : IGameCommandPort
        {
            private readonly IGameCommandPort inner;
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public WorkflowSubmissionResult Last;
            public ObservationPort(IGameCommandPort inner) { this.inner = inner; }
            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                Commands.Add(command);
                if (command.Parameters.TryGetValue("operation", out var operation) && operation == "cancel_action")
                {
                    var folder = Environment.GetEnvironmentVariable("NMC_GAMEPLAY_UI_EVIDENCE") ??
                        Path.Combine(Environment.CurrentDirectory, "Logs/GameplayUiBugfixes/方法原件");
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "统一支付-准备取消调用栈-" + command.CommandId + ".json"),
                        JsonConvert.SerializeObject(new { IsolatedSeam = true, Command = command,
                            StackBeforeSubmission = Environment.StackTrace }, Formatting.Indented));
                }
                return Last = inner.Submit(command);
            }
        }

        private static void Evidence(string name, Native native, object details)
        {
            var folder = Environment.GetEnvironmentVariable("NMC_GAMEPLAY_UI_EVIDENCE")
                ?? Path.Combine(Environment.CurrentDirectory, "Logs/GameplayUiBugfixes/方法原件");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".json"), JsonConvert.SerializeObject(new {
                Environment = "Unity 实际 SampleScene/Mobile/正式内容；生产回调调用，非外部自动点击", IsPlaying = UnityEngine.Application.isPlaying, Details = details,
                Commands = native.Commands, LastResult = native.Last,
                native.State.Phase, Revision = native.State.EffectRuntime.StateRevision,
                OpenRequests = native.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").ToArray()
            }, Formatting.Indented));
        }

        // 本轮反馈的独立复现：不改变上轮单点/末位玩家测试及其断言。
        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileMultiplayerHarvestKeepsEditedLocationsOnFirstEnd()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false); var state = State(prepared);
            var registry = (EffectRegistry)Read(prepared, "Registry");
            // 受控制备第2回合行动前地图，随后由Mobile正式行动/结束进入采集，不直接设采集Phase或完成标记。
            var locations = new[] { "A-03", "A-02", "B-03", "D-02", "B-01", "C-01" };
            foreach (var id in locations)
                if (!state.Map.OpenLocationIds.Contains(id)) state.Map.OpenLocationIds.Add(id);
            foreach (var seat in new[] { (1, "A-03"), (2, "B-01"), (3, "C-01") })
            {
                var parent = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == seat.Item2);
                var dock = state.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.city_dock");
                var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == seat.Item1);
                Position(state, city, dock.ZoneId);
            }
            foreach (var target in new[] { ("core.zone.map_route", "A2"), ("core.zone.map_location", "A-02"), ("core.zone.map_location", "B-03"), ("core.zone.map_location", "D-02") })
            {
                var parent = state.ItemZones.Zones.Single(z => z.TemplateId == target.Item1 && z.LocalZoneId == target.Item2);
                var zone = state.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.influence_slots");
                var supply = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_marker_supply" && z.OwnerPlayerId == 1);
                var marker = state.ItemZones.Items.First(i => i.ZoneId == supply.ZoneId && i.OwnerSubject.PlayerId == 1 && i.PhysicalKind == ItemPhysicalKind.Cube);
                var slot = zone.LegalSlotIds.First(id => !state.ItemZones.Items.Any(i => i.ZoneId == zone.ZoneId && i.SlotId == id));
                Position(state, marker, zone.ZoneId); marker.SlotId = slot;
            }
            registry.Register(new EffectRegistration("fixture.gameplay_ui.multiplayer_points", context =>
            {
                foreach (var location in locations)
                {
                    var parent = context.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location);
                    var zone = context.State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.resource_point_markers");
                    Assert.That(context.State.ItemZones.Items.Any(i => i.ZoneId == zone.ZoneId), Is.False, "不能覆盖已有资源点");
                    Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, location,
                        "core.resource_point", "core.resource_point_token", zone.ZoneId, new ItemSubjectRef(), out var id, out var reason), Is.True, reason);
                    context.State.ItemZones.Items.Single(i => i.ItemId == id).LocalState = ItemTransferEffectExecutor.Object(
                        "resourceType", NormalizedValue.CreateInteger((int)ResourceType.Iron), "amount", NormalizedValue.CreateInteger(1));
                }
                return EffectStepResult.Completed();
            }));
            Run(prepared, EffectSpec.Create("fixture.gameplay_ui.multiplayer_points", playerId: 1));
            Assert.That(new ItemZoneQuery().TryValidate(state, out var entityReason), Is.True, entityReason);
            var order = state.LaunchPlayerOrder.ToList();
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                // 前序也使用Mobile真实入口；观察所有正式命令，不用直接Session.Submit加速。
                var turns = 0;
                native.ObserveGateway(Read(native.Mobile, "turnActionPresenter"));
                var map = new YC.Domain.Maps.MapQueryService(YC.Domain.Maps.StaticMapDefinitions.Resolve(native.State.MapId));
                var influence = new YC.Domain.Influence.InfluenceService(map);
                while (native.State.Phase == GamePhase.ActionRound1 || native.State.Phase == GamePhase.ActionRound2)
                {
                    Assert.That(++turns, Is.LessThanOrEqualTo(6));
                    var preparatoryActor = (int)Read(native.Mobile, "localPlayerId");
                    var presenterForAction = Read(Read(native.Mobile, "turnActionPresenter"), "influenceActionPresenter");
                    native.ObserveGateway(presenterForAction);
                    Call(native.Mobile, "BeginDeployAction"); yield return null;
                    var legalSlots = influence.GetLegalPlacementSlotIds(native.State, preparatoryActor).ToArray();
                    var cityLocation = native.State.FindPlayer(preparatoryActor).CityLocationId;
                    Evidence("采集多点-前序部署候选-" + turns, native, new { preparatoryActor, cityLocation, LegalSlots = legalSlots });
                    // 本城已具采集资格，填本城空位不扩大资源候选，也不改变收费D1。
                    var slot = legalSlots.First(id => id.StartsWith("location:" + cityLocation + ":", StringComparison.Ordinal));
                    Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.InfluenceSlot, slot); yield return null;
                    Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.InfluenceSlot, slot); yield return null;
                    Evidence("采集多点-前序部署-" + turns, native, MultiHarvestObservation(native));
                    native.Applied();
                    Assert.That(Call(native.Mobile, "CanEndCurrentAction"), Is.EqualTo(true));
                    Call(native.Mobile, "EndCurrentAction"); yield return null;
                    Evidence("采集多点-前序结束-" + turns, native, MultiHarvestObservation(native));
                }
                Assert.That(turns, Is.EqualTo(6), "由三名玩家两轮行动正常进入采集");
                Assert.That(native.State.Phase, Is.EqualTo(GamePhase.ResourceCollection));
                var preparatoryCommandCount = native.Commands.Count;
                var route = Read(native.Mobile, "routeResourceSelection");
                var presenter = (RouteResourceSelectionPresenter)Read(route, "Presenter");
                Evidence("采集多点-00初始", native, MultiHarvestObservation(native));
                Assert.That(native.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection")
                    .Select(r => r.AnsweringPlayerId), Is.EquivalentTo(order), "所有玩家必须保留真实未完成采集请求");
                Assert.That(native.State.Players.All(p => !p.HasCollectedResourcesThisRound), Is.True);
                Assert.That((int)Read(native.Mobile, "localPlayerId"), Is.EqualTo(order[0]));
                Assert.That(presenter.Session.Purpose.ToString(), Is.EqualTo("NormalHarvest"));
                var initial = presenter.Draft.SelectedLocationIds.ToArray();
                Assert.That(initial, Is.EquivalentTo(new[] { "A-02", "A-03", "B-03" }));
                Assert.That(presenter.Projection.LocationOptions.Count(o => o.IsReachableInOriginalDraft), Is.GreaterThanOrEqualTo(3));
                // 实际Mobile地图入口：撤销默认A-03/B-03，重新选择A-02，再通过收费D1选择D-02。
                var edits = new[] { "A-03", "A-02", "A-02", "B-03" };
                for (var i = 0; i < edits.Length; i++)
                {
                    yield return null;
                    var selectedBefore = presenter.Draft.SelectedLocationIds.Contains(edits[i]);
                    Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, edits[i]);
                    Evidence("采集多点-编辑-" + i, native, MultiHarvestObservation(native));
                    Assert.That(presenter.Draft.SelectedLocationIds.Contains(edits[i]), Is.EqualTo(!selectedBefore), "地图输入必须真正改变草稿");
                }
                yield return null;
                Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Route, "D1"); yield return null;
                var dialog = Read(native.Mobile, "eventChoiceDialog");
                var dialogView = Read(dialog, "view");
                var bankButton = (Button)Read(dialogView, "ResourcePaymentBankButton");
                Assert.That(bankButton.gameObject.activeInHierarchy && bankButton.interactable, Is.True);
                bankButton.onClick.Invoke(); yield return null;
                Assert.That(presenter.Draft.ConfirmedRouteIds, Does.Contain("D1"));
                Assert.That(presenter.Projection.LocationOptions.Single(o => o.LocationId == "D-02").IsReachableInOriginalDraft, Is.True);
                if (presenter.Draft.SelectedLocationIds.Contains("D-02"))
                {
                    Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, "D-02"); yield return null;
                }
                Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, "D-02"); yield return null;
                var chosen = presenter.Draft.SelectedLocationIds.ToArray();
                Assert.That(chosen, Is.EquivalentTo(new[] { "A-02", "D-02" }));
                Assert.That(chosen, Is.Not.EquivalentTo(initial));
                Assert.That(native.Commands.Count, Is.EqualTo(preparatoryCommandCount), "本地选点和选择收款人不能提前提交");
                var request = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                var actor = request.AnsweringPlayerId; var beforeIron = native.State.FindPlayer(actor).Resources.Iron;
                var beforeGold = native.State.FindPlayer(actor).Resources.GoldVoucher;
                var expectedCost = presenter.Projection.Preview.PaymentPlan.TotalGoldVoucherAmount;
                var beforeDraft = presenter.Draft; var beforeCount = native.Commands.Count;
                Evidence("采集多点-01首次结束前", native, MultiHarvestObservation(native));
                Assert.That(presenter.CanConfirm, Is.True);
                Assert.That(Call(native.Mobile, "CanEndCurrentAction"), Is.EqualTo(true));
                try { Call(native.Mobile, "EndCurrentAction"); }
                finally { Evidence("采集多点-02首次结束同步返回", native, MultiHarvestObservation(native)); }
                yield return null;
                var reopened = native.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" &&
                    r.InteractionTypeId == "route_resource.selection" && r.AnsweringPlayerId == actor).ToArray();
                var classification = native.Commands.Count == beforeCount ? "未发命令" :
                    native.Last?.CommandResult?.Succeeded != true ? "命令被准确拒绝" :
                    reopened.Length > 0 ? "命令受理后同玩家仍有开放采集请求，核selectionDiagnostic与请求身份" : "本人采集请求结束";
                Evidence("采集多点-03首次结束后一帧", native, new {
                    Classification = classification, BeforeRequest = request, InitialSelection = initial, ExpectedSelection = chosen,
                    BeforeDraftVersion = beforeDraft.LocalDraftVersion, BeforeIron = beforeIron, BeforeGold = beforeGold, ExpectedGoldCost = expectedCost,
                    AfterIron = native.State.FindPlayer(actor).Resources.Iron, DraftObjectChanged = !ReferenceEquals(beforeDraft, presenter.Draft),
                    Observation = MultiHarvestObservation(native)
                });
                Assert.That(native.Commands.Count - beforeCount, Is.EqualTo(1), classification);
                native.Applied();
                Assert.That(native.State.FindPlayer(actor).Resources.Iron, Is.EqualTo(beforeIron + 2), classification);
                Assert.That(native.State.FindPlayer(actor).Resources.GoldVoucher, Is.EqualTo(beforeGold - expectedCost), classification);
                Assert.That(native.State.FindPlayer(actor).HasCollectedResourcesThisRound, Is.True, classification);
                Assert.That(reopened, Is.Empty, classification);
                Assert.That(native.State.Phase, Is.EqualTo(GamePhase.ResourceCollection), "其余玩家仍有义务，不能提前推进回合");
                Assert.That((int)Read(native.Mobile, "localPlayerId"), Is.EqualTo(order[1]), "首次结束应按正常热座顺序接续");
                Assert.That(native.State.Players.Where(p => p.PlayerId != actor).All(p => !p.HasCollectedResourcesThisRound), Is.True);
                Assert.That(native.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection")
                    .Select(r => r.AnsweringPlayerId), Is.EquivalentTo(order.Skip(1)));
                // 第二位也只能使用既有Mobile入口；若首位失败，前面断言立即保留失败，不进行重选/重试。
                yield return null;
                var second = order[1]; var secondIron = native.State.FindPlayer(second).Resources.Iron;
                Evidence("采集多点-04第二玩家首次结束前", native, MultiHarvestObservation(native));
                Call(native.Mobile, "EndCurrentAction"); yield return null;
                Evidence("采集多点-05第二玩家首次结束后", native, MultiHarvestObservation(native));
                native.Applied();
                Assert.That(native.Commands.Count - preparatoryCommandCount, Is.EqualTo(2));
                Assert.That(native.State.FindPlayer(second).Resources.Iron, Is.EqualTo(secondIron + 1));
                Assert.That(native.State.FindPlayer(second).HasCollectedResourcesThisRound, Is.True);
                Assert.That((int)Read(native.Mobile, "localPlayerId"), Is.EqualTo(order[2]));
                Assert.That(native.State.FindPlayer(order[2]).HasCollectedResourcesThisRound, Is.False);
            }
        }

        private static object MultiHarvestObservation(Native native)
        {
            var route = Read(native.Mobile, "routeResourceSelection");
            var presenter = (RouteResourceSelectionPresenter)Read(route, "Presenter");
            var request = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
            var buttonType = Type.GetType("YC.Presentation.MapButtonView, Assembly-CSharp", true);
            var buttons = ((Component)native.Mobile).gameObject.scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren(buttonType, true)).Cast<Component>();
            return new {
                native.State.Round, native.State.Phase, native.State.CurrentPlayerId, LocalPlayerId = Read(native.Mobile, "localPlayerId"),
                Revision = native.State.EffectRuntime.StateRevision, CurrentRequest = request,
                Session = presenter.Session, Binding = presenter.RequestBinding, Draft = presenter.Draft,
                CanConfirm = presenter.CanConfirm, CanEnd = Call(native.Mobile, "CanEndCurrentAction"),
                ProjectionValidation = presenter.Projection?.Validation, FinalizationValidation = presenter.Projection?.Preview?.FinalizationValidation,
                Preview = presenter.Projection?.Preview, HighlightPlan = presenter.BuildHighlights(),
                ActualHighlights = buttons.Select(button => new {
                    Kind = Read(button, "TargetKind").ToString(), Id = Read(button, "TargetId"),
                    Selected = Read(button, "IsSelected"), Interactive = Read(button, "IsInteractive"),
                    Visible = ((SpriteRenderer)Read(button, "Image")).enabled, Active = button.gameObject.activeInHierarchy
                }).ToArray(),
                CommandCount = native.Commands.Count, Commands = native.Commands.ToArray(), ExactResult = native.Last,
                Pending = Read(route, "inFlight"), MobilePending = Read(native.Mobile, "pendingEffectCommandId"),
                Players = native.State.Players.Select(p => new { p.PlayerId, p.CityLocationId, p.Resources, p.HasCollectedResourcesThisRound }).ToArray(),
                Requests = native.State.EffectRuntime.InteractionRequests.Where(r => r.InteractionTypeId == "route_resource.selection").ToArray(),
                OwnerNodes = native.State.EffectRuntime.EffectNodes.Where(n =>
                    native.State.EffectRuntime.InteractionRequests.Any(r => r.InteractionTypeId == "route_resource.selection" && r.OwnerEffectId == n.EffectId))
                    .Select(n => new { n.EffectId, n.PlayerId, n.Status, n.FlowStage, n.FailureReason, n.ContinuationData }).ToArray()
            };
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileCollectionFirstEndSubmitsOnceAndReleasesBusy()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false); var state = State(prepared);
            // 准备正式主链采集窗口，不能用只有 Phase 而缺少主链义务的混合旧状态代替。
            state.EffectRuntime = new EffectRuntimeState(); state.Phase = GamePhase.ResourceCollection;
            foreach (var player in state.Players) { player.HasCollectedResourcesThisRound = player.PlayerId != 1; player.ResourceCollectionStartGoldVoucher = player.Resources.GoldVoucher; }
            // 明确准备本城上的实际资源点；不以空地图候选伪装完成采集。
            var registry = (EffectRegistry)Read(prepared, "Registry");
            registry.Register(new EffectRegistration("fixture.gameplay_ui.resource_point", context =>
            {
                if (!context.State.Map.OpenLocationIds.Contains("A-03")) context.State.Map.OpenLocationIds.Add("A-03");
                var parent = context.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == "A-03");
                var zone = context.State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.resource_point_markers");
                Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, "A-03", "core.resource_point", "core.resource_point_token", zone.ZoneId,
                    new ItemSubjectRef(), out var id, out var reason), Is.True, reason);
                context.State.ItemZones.Items.Single(i => i.ItemId == id).LocalState = ItemTransferEffectExecutor.Object(
                    "resourceType", NormalizedValue.CreateInteger((int)ResourceType.Iron), "amount", NormalizedValue.CreateInteger(1));
                return EffectStepResult.Completed();
            }));
            Run(prepared, EffectSpec.Create("fixture.gameplay_ui.resource_point", playerId: 1));
            var builder = new RoundMainChainBuilder(); var order = state.LaunchPlayerOrder.ToList();
            var chain = builder.Build(state.GameId, builder.CreateRoundExecutionId(state.GameId, 2, order), 2, order, 1);
            var collectionNode = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.Collection);
            foreach (var node in chain.Nodes.Where(n => n.MainlineIndex < collectionNode.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = state.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = collectionNode.NodeId; collectionNode.Status = EffectNodeStatus.Ready;
            Assert.That(new RoundExecutionService(registry).Advance(state).IsValid, Is.True);
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var route = Read(native.Mobile, "routeResourceSelection");
                var presenter = (RouteResourceSelectionPresenter)Read(route, "Presenter");
                Assert.That(Read(route, "IsActive"), Is.EqualTo(true));
                Assert.That(presenter.Session.Purpose.ToString(), Is.EqualTo("NormalHarvest"));
                var request = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                Assert.That(request.InteractionTypeId, Is.EqualTo("route_resource.selection"));
                if (!presenter.Draft.SelectedLocationIds.Contains("A-03")) Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, "A-03");
                Assert.That(presenter.Draft.SelectedLocationIds, Does.Contain("A-03"));
                Evidence("采集前置状态", native, new { Request = request, Purpose = presenter.Session.Purpose.ToString(), Selected = presenter.Draft.SelectedLocationIds,
                    OpenLocations = native.State.Map.OpenLocationIds.ToArray(), ResourcePoints = native.State.Map.ResourceTokens.ToArray(), City = native.State.FindPlayer(1).CityLocationId });
                Assert.That(Call(native.Mobile, "CanEndCurrentAction"), Is.EqualTo(true));
                var ironBefore = native.State.FindPlayer(1).Resources.Iron;
                Call(native.Mobile, "EndCurrentAction");
                yield return null;
                Evidence("采集首次结束", native, new { Pipeline = "routeResourceSelection", RequestId = request.InteractionId,
                    Pending = Read(route, "inFlight"), MobilePending = Read(native.Mobile, "pendingEffectCommandId"), IronBefore = ironBefore, IronAfter = native.State.FindPlayer(1).Resources.Iron });
                native.Applied();
                Assert.That(native.Commands.Count(c => c.Kind == GameCommandKind.AnswerInteraction), Is.EqualTo(1));
                Assert.That(native.Commands.Any(c => c.Kind == GameCommandKind.CollectResource), Is.False);
                Assert.That((string)Read(route, "inFlight"), Is.Null.Or.Empty);
                Assert.That((string)Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                Assert.That(native.State.FindPlayer(1).Resources.Iron, Is.EqualTo(ironBefore + 1));
                // 其余玩家预先已采集，正式推进已进入下一回合；每回合采集标记此时按规则重置。
                Assert.That(native.State.Round, Is.GreaterThan(2));
                Assert.That(native.State.Phase, Is.Not.EqualTo(GamePhase.ResourceCollection));
                Call(native.Mobile, "SynchronizeInteractionFromState");
                Assert.That(native.Commands.Count, Is.EqualTo(1), "同步后继不得要求重选或再次提交采集");
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileCharacterBeginsBeforeModeAndPreservesSettlementAcrossInspection()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(); var state = State(prepared);
            if (!CharacterCardDatabase.IsInitialized) CharacterCardDatabase.Initialize(ExternalContentPack.Load(
                Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core")).CreateCharacters());
            var amiya = state.ItemZones.Items.Single(i => i.DefinitionId == "infected.character.amiya" && i.OwnerSubject.PlayerId == 1);
            var hand = state.ItemZones.Zones.Single(z => z.LocalZoneId == "hand" && z.OwnerPlayerId == 1);
            Run(prepared, ItemTransferEffectExecutor.MoveSpec(1, new[] { amiya.ItemId }, amiya.ZoneId, hand.ZoneId, "unordered", 1, "core.character_card_transfer"));
            Cover(prepared, amiya.ItemId); Run(prepared, CharacterLifecycleService.CreateUseSpec(1, amiya.ItemId, "tactic"));
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, amiya.ItemId, "infected.character.amiya.tactic"), Is.True);
            var card = state.ItemZones.Items.First(i => i.TypeId == "core.basic_character_card" && i.ZoneId == hand.ZoneId && i.ItemId != amiya.ItemId);
            Cover(prepared, card.ItemId);
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                Call(native.Mobile, "OnUseCharacterActionClicked", card.ItemId);
                Evidence("角色开始", native, new { Card = card.ItemId });
                native.Applied();
                var command = native.Commands.Single(c => c.Kind == GameCommandKind.UseCharacterCard);
                Assert.That(command.Parameters.Values, Does.Contain(card.ItemId));
                Assert.That(command.Parameters.TryGetValue("effectMode", out var mode) ? mode : "", Is.Empty);
                Assert.That(command.Parameters.TryGetValue("effectOrder", out var order) ? order : "", Is.Empty);
                var window = SettlementHostProtocol.FindActiveWindow(native.State);
                Assert.That(window, Is.Not.Null);
                Assert.That(window.Entries.Any(e => e.AbilityId == "infected.character.amiya.tactic"), Is.True);
                var useId = window.Context.TargetUseId;
                var before = JsonConvert.SerializeObject(native.State);
                var count = native.Commands.Count;
                foreach (var action in new[] { "BeginDeployAction", "BeginDispatchAction", "BeginExploreAction", "BeginMoveAction", "OnSpecialActionClicked" }) Call(native.Mobile, action);
                Call(native.Mobile, "OnUseCharacterActionClicked", card.ItemId);
                Assert.That(native.Commands.Count, Is.EqualTo(count));
                Assert.That(Read(native.Mobile, "CanBeginGameplayAction"), Is.EqualTo(false));
                Call(native.Mobile, "OnBuildActionClicked");
                Assert.That(Read(native.Mobile, "supplyInspectionActive"), Is.EqualTo(true));
                Assert.That(((Component)Read(native.Mobile, "facilityBuildPage")).gameObject.activeInHierarchy, Is.True);
                Call(native.Mobile, "PayBuildFacilityInPanel", BuildFacilityService.PaymentModeGold);
                Call(native.Mobile, "ConfirmBuildFacilityQuote");
                Call(native.Mobile, "CloseFacilitySupplyInspection");
                yield return null;
                Call(native.Mobile, "OnDeclareCityStyleClicked");
                var styles = Read(Read(native.Mobile, "turnActionPresenter"), "CityStyleInteraction");
                var styleDialog = Read(Read(native.Mobile, "workflowView"), "cityStyleDeclarationDialog");
                Assert.That(Read(styleDialog, "IsShowing"), Is.EqualTo(true));
                Call(styles, "SubmitDeclare", (string)Read(styleDialog, "CurrentCityStyleId"), new int[0]);
                Call(styleDialog, "Hide");
                yield return null;
                Assert.That(native.Commands.Count, Is.EqualTo(count));
                Assert.That(JsonConvert.SerializeObject(native.State), Is.EqualTo(before), "查看/关闭不得改变请求、使用实例或权威状态");
                Call(native.Frame, "ToggleEffectPage");
                Call(native.Mobile, "OnUseCharacterActionClicked", card.ItemId);
                Call(native.Mobile, "BeginMoveAction");
                Assert.That(native.Commands.Count, Is.EqualTo(count));
                Call(native.Frame, "ResumeEffectPage");
                yield return null;
                var expansion = Read(native.Hud, "ExpansionUi");
                var visible = Call(native.Mobile, "VisibleSaleState");
                var options = (IEnumerable)Read(visible, "CharacterSettlementOptions");
                var option = options.Cast<object>().Single(o => (string)Read(o, "SourceItemId") == amiya.ItemId && (bool)Read(o, "IsEnabled"));
                var invoke = expansion.GetType().GetMethod("InvokeSettlement", Members);
                var optionType = invoke.GetParameters()[0].ParameterType;
                var view = optionType.IsInstanceOfType(option) ? option : optionType.GetMethod("FromOption", BindingFlags.Static | BindingFlags.Public).Invoke(null, new[] { option });
                Call(expansion, "InvokeSettlement", view);
                Evidence("阿米娅插入与门禁", native, new { TargetUseId = useId, InspectionRetained = true });
                native.Applied();
                var use = native.State.CharacterUses.Single(u => u.UseId == useId);
                Assert.That(use.SourceConsumed && use.DoubleUseGranted, Is.True);
                Assert.That(CharacterLifecycleService.IsSubscriptionActive(native.State, amiya.ItemId, "infected.character.amiya.tactic"), Is.False);
                Assert.That(native.Commands.Count(c => c.Kind == GameCommandKind.UseCharacterCard), Is.EqualTo(1));
                var modeRequest = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                Assert.That(modeRequest, Is.Not.Null);
                Assert.That(modeRequest.InteractionTypeId, Is.EqualTo(CharacterLifecycleService.OrderInteractionTypeId));
                Assert.That(modeRequest.CandidateIds, Does.Contain("tactic-first"));
                var countBeforeFold = native.Commands.Count;
                ((Button)Read(native.Frame, "FoldButton")).onClick.Invoke(); yield return null;
                Assert.That(Read(native.Frame, "effectSuspended"), Is.EqualTo(true), "必须确实收起当前结算页");
                Call(native.Mobile, "OnUseCharacterActionClicked", card.ItemId); Call(native.Mobile, "BeginMoveAction");
                Assert.That(native.Commands.Count, Is.EqualTo(countBeforeFold));
                Assert.That(Read(native.Mobile, "CanBeginGameplayAction"), Is.EqualTo(false));
                Call(native.Frame, "ResumeEffectPage"); yield return null;
                Assert.That(((InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest")).InteractionId, Is.EqualTo(modeRequest.InteractionId));
                var revision = native.State.EffectRuntime.StateRevision;
                Assert.That(Call(Read(native.Mobile, "characterAbilityInteraction"), "TrySubmitUseSelection", modeRequest, "tactic-first"), Is.EqualTo(true));
                yield return null;
                Evidence("结算中的合法顺序选择", native, new { UseId = useId, PreviousRequest = modeRequest.InteractionId, FoldedWithoutNewCommands = true });
                native.Applied();
                Assert.That(native.State.EffectRuntime.StateRevision, Is.GreaterThan(revision));
                Assert.That(native.Commands.Count(c => c.Kind == GameCommandKind.UseCharacterCard), Is.EqualTo(1), "当前模式选择沿既有实例，不再次开始角色使用");
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileMaylanderQuantityPageSubmitsOneFormalAllocation()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(); var state = State(prepared);
            PrepareEnterpriseBoards(state);
            var board = state.ItemZones.Items.Single(i => i.DefinitionId == "infected.enterprise.maylander_foundation");
            var cooperation = state.ItemZones.Zones.Single(z => z.ParentId == board.ItemId && z.LocalZoneId == "cooperation_1");
            state.ItemZones.Items.Single(i => i.ZoneId == cooperation.ZoneId).SlotId = "level_1";
            Run(prepared, EffectSpec.Create(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial,
                ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                    "unlockedByPlayer", NormalizedValue.CreateString("1"), "enterpriseItemIds", NormalizedValue.CreateArray(new[] { NormalizedValue.CreateString(board.ItemId) }),
                    "specialId", NormalizedValue.CreateString("infected.enterprise.maylander_foundation.level_1.special")), 1));
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var request = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                Assert.That(request.AnswerSchema, Is.EqualTo("resource_allocation"));
                Assert.That(request.Presentation.QuantityRows.Select(r => r.RowId), Is.EqualTo(new[] { "Originium", "OriginiumShard", "Iron" }));
                foreach (var row in request.Presentation.QuantityRows)
                {
                    var document = (GameplayTextDocument)Call(Read(native.Hud, "ExpansionUi"), "ResolveGameplayText", row.Label);
                    Assert.That(document.Runs.OfType<IconRun>().Any(), Is.True, row.RowId + "必须使用正式资源图标元数据");
                    Assert.That(System.Text.RegularExpressions.Regex.IsMatch(document.ToPlainText(), "[\u4e00-\u9fff]"), Is.True);
                }
                var before = native.State.FindPlayer(1).Resources.Clone();
                var buttons = ((Component)native.Mobile).gameObject.scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Button>(true)).ToArray();
                Button Current(string name) => buttons.Single(b => b.gameObject.activeInHierarchy && b.name == name);
                Assert.That(Current("Confirm").interactable, Is.True, "现有次数页默认将总量分配给第一行");
                Current("Decrease 0").onClick.Invoke();
                Assert.That(Current("Confirm").interactable, Is.False, "总量未满足时不能提交");
                Current("Increase 2").onClick.Invoke();
                Assert.That(native.Commands, Is.Empty);
                Assert.That(Current("Confirm").interactable, Is.True);
                Current("Confirm").onClick.Invoke();
                yield return null;
                Evidence("梅兰德次数分配", native, new { Before = before, After = native.State.FindPlayer(1).Resources, Request = request });
                native.Applied();
                Assert.That(native.Commands.Count(c => c.Kind == GameCommandKind.AnswerInteraction), Is.EqualTo(1));
                Assert.That(native.State.FindPlayer(1).Resources.Originium, Is.EqualTo(before.Originium + 1));
                Assert.That(native.State.FindPlayer(1).Resources.Iron, Is.EqualTo(before.Iron + 1));
                Assert.That(native.State.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(before.OriginiumShard));
                Assert.That(native.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.AnswerSchema == "resource_allocation"), Is.False);
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileInfectedFacilityShowsCostsAndBuildsThroughProductionCallback()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            foreach (var test in new[] { ("rhodes_office_blue", "resources"), ("rhodes_office_red", "gold"), ("rhodes_office_yellow", "resources"), ("temporary_settlement", "gold") })
            {
            var suffix = test.Item1; var payment = test.Item2;
            var prepared = Production(); var state = State(prepared);
            PrepareEnterpriseBoards(state);
            var item = state.ItemZones.Items.First(i => i.DefinitionId == "infected.facility." + suffix);
            var supply = state.ItemZones.Zones.Single(z => z.LocalZoneId == "facility_supply_0");
            var occupant = state.ItemZones.Items.FirstOrDefault(i => i.ZoneId == supply.ZoneId);
            var previousZone = item.ZoneId;
            if (occupant != null && occupant.ItemId != item.ItemId) Position(state, occupant, previousZone);
            Position(state, item, supply.ZoneId);
            var resources = state.FindPlayer(1).Resources;
            resources.Originium = 50; resources.OriginiumShard = 50; resources.Iron = 50; resources.GoldVoucher = 100;
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var build = Read(Read(native.Mobile, "turnActionPresenter"), "BuildInteraction"); native.ObserveGateway(build);
                Call(native.Mobile, "OnBuildActionClicked"); yield return null;
                Call(native.Mobile, "SelectBuildCardInPanel", item.ItemId); yield return null;
                var model = (BuildFacilityDraftViewModel)Call(Read(native.Mobile, "turnActionPresenter"), "BuildBuildFacilityDraftViewModel");
                Assert.That(model.SelectedOption.DefinitionId, Is.EqualTo(item.DefinitionId));
                Assert.That(model.SelectedOption.CanBuild, Is.True, model.SelectedOption.Reason);
                var definition = FacilityCardDatabase.Get(native.State, item.DefinitionId);
                Assert.That(model.SelectedOption.EffectiveResourceCost, Is.Not.Null);
                Assert.That(((Component)Read(native.Mobile, "facilityBuildPage")).gameObject.activeInHierarchy, Is.True);
                var before = native.State.FindPlayer(1).Resources.Clone();
                var cost = model.SelectedOption.EffectiveResourceCost.Clone();
                var mode = payment == "gold" ? BuildFacilityService.PaymentModeGold : BuildFacilityService.PaymentModeResources;
                Call(native.Mobile, "PayBuildFacilityInPanel", mode);
                yield return null;
                Evidence("设施付款-" + suffix, native, new { Before = before, Cost = cost, GoldCost = definition.GoldVoucherCost });
                native.Applied();
                var after = native.State.FindPlayer(1).Resources;
                Assert.That(after.GoldVoucher, Is.EqualTo(before.GoldVoucher - (payment == "gold" ? definition.GoldVoucherCost : cost.GoldVoucher)));
                Assert.That(after.Originium, Is.EqualTo(before.Originium - (payment == "resources" ? cost.Originium : 0)));
                Assert.That(after.OriginiumShard, Is.EqualTo(before.OriginiumShard - (payment == "resources" ? cost.OriginiumShard : 0)));
                Assert.That(after.Iron, Is.EqualTo(before.Iron - (payment == "resources" ? cost.Iron : 0)));
                var placementRequest = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                Assert.That(placementRequest.InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
                var placement = Read(native.Mobile, "facilityBuildPlacement");
                var placementPage = Read(placement, "page");
                ((Button[])Read(placementPage, "citySlots"))[int.Parse(placementRequest.CandidateIds.First())].onClick.Invoke();
                yield return null;
                ((Button)Read(placementPage, "confirm")).onClick.Invoke();
                yield return null;
                native.Applied();
                Evidence("设施建设后继-" + suffix, native, new { Definition = item.DefinitionId });
                Assert.That(native.State.Map.Facilities.Any(f => f.PlayerId == 1 && f.ContentInstanceId == item.ItemId), Is.True);
                var request = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                if (suffix.StartsWith("rhodes"))
                {
                    Assert.That(request, Is.Not.Null);
                    Assert.That(request.PromptKey, Is.EqualTo("facility.rhodes_office.entry"));
                    Assert.That(request.Presentation.Options.Select(o => o.CandidateId), Does.Contain("dispatch"));
                    Assert.That(request.Presentation.Prompt, Is.Not.Null);
                    foreach (var option in request.Presentation.Options)
                    {
                        var document = (GameplayTextDocument)Call(Read(native.Hud, "ExpansionUi"), "ResolveGameplayText", option.Label);
                        Assert.That(document.Runs.OfType<IconRun>().Any(), Is.True, option.CandidateId + "必须含正式效果图标");
                        Assert.That(System.Text.RegularExpressions.Regex.IsMatch(document.ToPlainText(), "[\u4e00-\u9fff]"), Is.True);
                    }
                    var branch = suffix == "rhodes_office_red" ? "upgrade_two" : "dispatch";
                    Assert.That(request.CandidateIds, Does.Contain(branch));
                    Call(Read(native.Mobile, "facilityInteraction"), "TryHandleCandidateClicked", branch);
                    yield return null;
                    ActiveButton(native, "Confirm Selection").onClick.Invoke();
                    yield return null;
                    native.Applied();
                    var successor = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                    Evidence("办事处后继-" + suffix, native, new { Branch = branch, Successor = successor });
                    Assert.That(successor, Is.Not.Null);
                    Assert.That(successor.InteractionId, Is.Not.EqualTo(request.InteractionId));
                    Assert.That(successor.CandidateIds, Is.Not.Empty);
                }
            }
            yield return null;
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileLiskarmMapsVisibleInfluenceItemsToSlotsAndAnswersOriginalId()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false); var state = State(prepared);
            var registry = (EffectRegistry)Read(prepared, "Registry");
            var influence = (InfluenceService)Read(prepared, "Influence");
            // 隔离接缝准备：用正式放置Effect制备影响力，再沿正式雷蛇Lua请求测试地图入口，不计正常获得/完整局。
            foreach (int player in new[] { 2, 3, 1 })
            {
                var slot = influence.GetLegalPlacementSlotIds(state, player).First();
                Run(prepared, InfluenceEffectSpecFactory.PlaceInfluence(player, slot));
            }
            const string trigger = "fixture.ui.liskarm.target";
            registry.Register(new EffectRegistration(trigger, context => EffectStepResult.Completed().AddEvent(
                new EffectEventRequest { EventType = CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType,
                    RouteKey = "character.liskarm.tactic", PlayerId = 1,
                    Payload = NormalizedValue.CreateObject(Array.Empty<NormalizedValueEntry>()) })));
            Run(prepared, EffectSpec.Create(trigger, playerId: 1));
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var coordinator = Read(native.Mobile, "characterAbilityInteraction");
                var request = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                if (request.InteractionTypeId == "action.decline_effect")
                {
                    // 条件默认语义变化后的正式开始确认；仍经实际新执行按钮，不跳过规则左式。
                    ((Button)Read(Read(Read(native.Mobile, "eventChoiceDialog"), "view"), "ConditionPaymentExecuteButton")).onClick.Invoke();
                    yield return null;
                    request = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                }
                Assert.That(request.PromptKey, Is.EqualTo("character.liskarm.tactic.choose_target"));
                var targetCommandStart = native.Commands.Count;
                Assert.That(request.CandidateIds, Is.Not.Empty);
                Assert.That(request.CandidateIds.All(id => native.State.ItemZones.Items.Any(item => item.ItemId == id)), Is.True);
                var expected = request.CandidateIds.Select(id => native.State.Map.Influences.Single(item => item.InfluenceId == id).SlotId).ToArray();
                var buttonType = Type.GetType("YC.Presentation.MapButtonView, Assembly-CSharp", true);
                var buttons = ((Component)native.Mobile).gameObject.scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren(buttonType, true))
                    .Where(button => (WorkflowHighlightTargetKind)Read(button, "TargetKind") == WorkflowHighlightTargetKind.InfluenceSlot).ToArray();
                Assert.That(buttons.Where(button => (bool)Read(button, "IsInteractive")).Select(button => (string)Read(button, "TargetId")),
                    Is.EquivalentTo(expected), "仅本人可见的正式候选实例所处槽位可交互，不能把物品ID当槽位");
                var ownSlot = native.State.Map.Influences.Single(item => item.PlayerId == 1).SlotId;
                Assert.That(expected, Does.Not.Contain(ownSlot));
                Call(coordinator, "OnInfluenceSlotClicked", ownSlot);
                Assert.That(native.Commands.Count, Is.EqualTo(targetCommandStart), "非候选槽位不能扩大成新的合法答案");
                var candidate = request.CandidateIds[0]; var targetSlot = expected[0];
                Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.InfluenceSlot, targetSlot);
                yield return null;
                if (native.Commands.Count == targetCommandStart)
                {
                    // 复用当前地图确认行为，不新增确认步骤。
                    Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.InfluenceSlot, targetSlot);
                    yield return null;
                }
                native.Applied();
                Assert.That(native.Commands.Count - targetCommandStart, Is.EqualTo(1));
                var answer = native.Commands.Last();
                Assert.That(answer.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
                Assert.That(answer.OptionIds, Is.EqualTo(new[] { candidate }));
                Assert.That(answer.Parameters["interactionId"], Is.EqualTo(request.InteractionId));
                Assert.That(answer.Parameters["expectedRevision"], Is.EqualTo(request.StateRevision.ToString()));
                Assert.That(Read(coordinator, "inFlightCommandId"), Is.EqualTo(string.Empty), "准确本地回执后必须释放busy");
                Assert.That(native.State.Map.Influences.Single(item => item.SlotId == targetSlot).PlayerId, Is.EqualTo(1));
                Evidence("雷蛇计谋-影响力实例到地图槽位接缝", native,
                    new { IsolatedSeam = true, CandidateIds = request.CandidateIds, Slots = expected, AnswerId = candidate, ClickedSlot = targetSlot });
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileRetireKeepSelectionRetainsInactiveShellOwnershipAndResumes()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(); var state = State(prepared);
            var registry = (EffectRegistry)Read(prepared, "Registry");
            var facility = state.ItemZones.Items.First(item => item.DefinitionId == "infected.facility.temporary_settlement");
            const string trigger = "fixture.ui.temporary_settlement.declared";
            // 隔离声明入口仅供页面接缝；正式观察者和正式Lua产生retire/keep，不代表正常建设/宣告覆盖。
            registry.Register(new EffectRegistration(trigger, context => EffectStepResult.Completed().AddEvent(
                new EffectEventRequest { EventType = "CityStyleDeclared", PlayerId = 1,
                    Payload = NormalizedValue.CreateObject(new[] { new NormalizedValueEntry { Name = "usedFacilityItemIds",
                        Value = NormalizedValue.CreateArray(new[] { NormalizedValue.CreateString(facility.ItemId) }) } }) })));
            Run(prepared, EffectSpec.Create(trigger, playerId: 1));
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var coordinator = Read(native.Mobile, "characterAbilityInteraction");
                var dialog = Read(native.Mobile, "characterCardEffectChoiceDialog");
                var request = (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                Assert.That(request.PromptKey, Is.EqualTo("facility.temporary_settlement.retire"));
                Assert.That(request.CandidateIds, Is.EquivalentTo(new[] { "retire", "keep" }));
                var host = (RectTransform)Read(native.Frame, "ContentRect");
                Call(dialog, "Hide");
                var wasActive = host.gameObject.activeSelf;
                host.gameObject.SetActive(false);
                try
                {
                    // 控制非激活祖先这一组件前提，不强刷布局；不能把它算作正常完整局画面。
                    Call(coordinator, "Render", request);
                    var pageType = Type.GetType("YC.Presentation.EffectDialogShellView, Assembly-CSharp", true);
                    var page = host.GetComponentsInChildren(pageType, true).Single(value => value.gameObject.activeSelf);
                    Assert.That(page.gameObject.activeInHierarchy, Is.False);
                    var content = (RectTransform)Read(page, "ExpandedContent");
                    var shellType = Type.GetType("YC.Presentation.EffectDialogShell, Assembly-CSharp", true);
                    var resolve = shellType.GetMethod("ResolveView", BindingFlags.Static | BindingFlags.NonPublic);
                    Assert.That(resolve.Invoke(null, new object[] { content }), Is.SameAs(page));
                    var foreign = new GameObject("无弹窗归属的组件探针", typeof(RectTransform));
                    try
                    {
                        var error = Assert.Throws<TargetInvocationException>(() => resolve.Invoke(null, new object[] { foreign.transform }));
                        Assert.That(error.InnerException, Is.InstanceOf<InvalidOperationException>(), "非所属内容仍必须抛错，不能吞异常或借另一活动页");
                    }
                    finally { UnityEngine.Object.Destroy(foreign); }
                }
                finally { host.gameObject.SetActive(wasActive); }
                yield return null;
                var rowType = Type.GetType("YC.Presentation.EffectDialogOptionRowView, Assembly-CSharp", true);
                var keepRow = host.GetComponentsInChildren(rowType, false)
                    .Single(row => row.gameObject.name == "Character Effect Option " + request.CandidateIds.IndexOf("keep"));
                ((Button)Read(keepRow, "Button")).onClick.Invoke();
                yield return null;
                ActiveButton(native, "Confirm Selection").onClick.Invoke();
                yield return null;
                native.Applied();
                Assert.That(native.Commands, Has.Count.EqualTo(1));
                Assert.That(native.Commands.Single().OptionIds, Is.EqualTo(new[] { "keep" }));
                Assert.That(native.Commands.Single().Parameters["interactionId"], Is.EqualTo(request.InteractionId));
                Assert.That(Read(coordinator, "inFlightCommandId"), Is.EqualTo(string.Empty));
                Assert.That(native.State.EffectRuntime.InteractionRequests.Any(item => item.Status == "open" && item.GetStableInteractionId() == request.InteractionId), Is.False);
                Evidence("临时聚居点-非激活页面归属与原确认接缝", native,
                    new { IsolatedSeam = true, request.InteractionId, request.CandidateIds, Selected = "keep", ExtraPage = false });
            }
        }

        private static InteractionRequestProjection UiRequest(Native native) =>
            (InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
        private static object PaymentWindow(Native native) => Read(Read(native.Mobile, "eventChoiceDialog"), "view");
        private static GameStateView Visible(Native native) => (GameStateView)Call(native.Mobile, "VisibleSaleState");
        private static string Balances(Native native) => JsonConvert.SerializeObject(native.State.FindPlayer(1).Resources);
        private static int Markers(GameState state) => state.ItemZones.Zones.Where(zone =>
            ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, 1, zone.ZoneId) != null)
            .Sum(zone => ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, zone, 1).Count);
        private static void LoadPassenger(object prepared)
        {
            var state = State(prepared);
            var card = state.ItemZones.Items.First(item => item.DefinitionId == "operator.passenger");
            card.ControllerSubject = ItemSubjectRef.Player(1); card.OwnerSubject = ItemSubjectRef.Player(1);
            Position(state, card, state.ItemZones.Zones.Single(zone => zone.LocalZoneId == "hand" && zone.OwnerPlayerId == 1).ZoneId);
            Cover(prepared, card.ItemId);
            Run(prepared, CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic")); Answer(prepared, "3");
            Assert.That(Markers(state), Is.EqualTo(3));
        }
        private static IEnumerable<Component> ActiveComponents(Native native, string type) =>
            ((Component)native.Mobile).gameObject.scene.GetRootGameObjects().SelectMany(root =>
                root.GetComponentsInChildren(Type.GetType("YC.Presentation." + type + ", Assembly-CSharp", true), false));
        private static IEnumerator CaptureUi(string name, Native native)
        {
            // 等待实际帧，不强制刷新布局；PNG来自当前SampleScene，不能计作正常局或人工验收。
            yield return null; yield return null;
            var folder = Environment.GetEnvironmentVariable("NMC_GAMEPLAY_UI_EVIDENCE") ??
                Path.Combine(Environment.CurrentDirectory, "Logs/GameplayUiBugfixes/方法原件");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name + "-" + DateTime.UtcNow.Ticks + ".png");
            ScreenCapture.CaptureScreenshot(path);
            for (var frame = 0; frame < 120 && !File.Exists(path); frame++) yield return null;
            if (!File.Exists(path))
            {
                Evidence(name + "-截图未验证", native, new { IsolatedSeam = true, NormalGameAcceptance = false,
                    Screenshot = path, ScreenshotGenerated = false, ScreenshotVerified = false,
                    ActualScene = SceneManager.GetActiveScene().name, CurrentRequest = UiRequest(native),
                    Prepared = Visible(native).PreparedActionPayments });
                yield break;
            }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length <= 32 || !bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                Evidence(name + "-截图未验证", native, new { IsolatedSeam = true, NormalGameAcceptance = false,
                    Screenshot = path, ScreenshotGenerated = true, ScreenshotVerified = false });
                yield break;
            }
            Evidence(name, native, new { IsolatedSeam = true, NormalGameAcceptance = false, Screenshot = path,
                ScreenshotGenerated = true, ScreenshotVerified = true, ActualScene = SceneManager.GetActiveScene().name,
                CurrentRequest = UiRequest(native), Prepared = Visible(native).PreparedActionPayments });
        }
        private static IEnumerator InvokePassenger(Native native)
        {
            Component chip = null;
            for (var frame = 0; frame < 120; frame++)
            {
                chip = ActiveComponents(native, "SettlementEffectChipView").FirstOrDefault(component =>
                    Read(component, "option") != null && (Read(Read(component, "option"), "SourceDefinitionId") as string) == "operator.passenger" &&
                    (bool)Read(component, "executionAllowed"));
                if (chip != null && !(bool)Read(chip, "animating")) break;
                yield return null;
            }
            Assert.That(chip, Is.Not.Null, "合法异客来源必须在原价不足时保留底栏入口");
            var rect = (RectTransform)chip.transform;
            var canvas = chip.GetComponentInParent<Canvas>();
            var point = RectTransformUtility.WorldToScreenPoint(canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera,
                rect.TransformPoint(rect.rect.center));
            var pointer = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left, position = point };
            var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(pointer, hits);
            Assert.That(hits, Is.Not.Empty);
            Assert.That(hits[0].gameObject.transform == chip.transform || hits[0].gameObject.transform.IsChildOf(chip.transform), Is.True,
                "原宿主不能以全屏命中层遮挡底栏：" + hits[0].gameObject.name);
            Assert.That(((GameObject)Read(native.Frame, "longSummary")).activeInHierarchy, Is.False);
            Assert.That(((GameObject)Read(native.Frame, "shortSummary")).activeInHierarchy, Is.False);
            try
            {
                Call(chip, "OnPointerDown", pointer); Call(chip, "OnPointerUp", pointer); Call(chip, "OnPointerClick", pointer);
            }
            catch (Exception error)
            {
                CaptureSettlementCallbackFailure("统一支付-异客原指针初始异常", native, error);
                throw;
            }
            yield return null; native.Applied();
            Assert.That(UiRequest(native).InteractionTypeId, Is.EqualTo(ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId));
        }
        private static IEnumerator SaveOneSubstitution(Native native, ResourceType type)
        {
            var request = UiRequest(native);
            var ids = request.PromptParameters.Properties.Single(entry => entry.Name == "rowIds").Value.Items.Select(value => value.StringValue).ToArray();
            var index = Array.IndexOf(ids, type.ToString()); Assert.That(index, Is.GreaterThanOrEqualTo(0));
            var row = ActiveComponents(native, "EffectDialogResourceRowView").Single(component => component.name == "Resource Allocation Row " + index);
            ((Button)Read(row, "IncreaseButton")).onClick.Invoke(); yield return null;
            var confirm = ActiveButton(native, "Confirm"); Assert.That(confirm.IsInteractable(), Is.True);
            try { confirm.onClick.Invoke(); }
            catch (Exception error)
            {
                CaptureSettlementCallbackFailure("统一支付-替代分配原确认初始异常", native, error);
                throw;
            }
            yield return null; native.Applied();
        }
        private static void CaptureSettlementCallbackFailure(string name, Native native, Exception error)
        {
            try
            {
                var expansion = Read(native.Hud, "ExpansionUi");
                var view = (GameStateView)Call(expansion, "VisibleView");
                // 与RefreshSettlementBar同一生产投影及同一FromOption映射；不再调用任何控件或BindWindow。
                var inputs = view.CharacterSettlementOptions.Where(option => option.WindowId == view.ActiveSettlementWindowId &&
                    option.VisibleToViewer && option.AnsweringPlayerId == view.ViewerPlayerId &&
                    (option.EntryKind == "optional_insertion" || option.EntryKind == "cleanup_order"))
                    .Select(SettlementOptionView.FromOption).ToArray();
                Evidence(name, native, new { IsolatedSeam = true, InitialException = error.ToString(),
                    ExceptionCapturedBeforeDispose = true, view.StateRevision,
                    CurrentProductionVisibleViewWindowId = view.ActiveSettlementWindowId,
                    CurrentProductionMappedBindOptions = inputs,
                    Boundary = "异常后的同一生产VisibleView绑定参数；没有再次调用BindWindow，非拦截器记录", CurrentRequest = UiRequest(native) });
            }
            catch (Exception diagnosticError)
            {
                Evidence(name, native, new { IsolatedSeam = true, InitialException = error.ToString(),
                    ExceptionCapturedBeforeDispose = true, DiagnosticFailure = diagnosticError.ToString() });
            }
        }
        private static Button BuildPaymentButton(Native native, int index) =>
            (Button)Read(((Array)Read(Read(native.Mobile, "facilityBuildPage"), "paymentRows")).GetValue(index), "Button");
        private static string BuildCandidate(GameState state) => new BuildFacilityOptionQueryService().Query(state, 1)
            .First(option => BuildFacilitySelectionController.CanPrepareOption(option) &&
                FacilityCardDatabase.Get(state, option.DefinitionId).GoldVoucherCost > 0 &&
                ResourcePaymentSubstitutionEffectExecutor.BasicTypes.Any(type => option.EffectiveResourceCost.Get(type) > 0)).FacilityId;

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileConditionPaymentSavesPlanAndWaitsForOriginalExecute()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(true); LoadPassenger(prepared);
            State(prepared).FindPlayer(1).Resources = new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = 2 };
            Run(prepared, EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1,
                new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = 3 }) },
                new[] { ResourceEffectSpecFactory.Gain(1, ResourceType.PureOriginium, 1) }, ruleCondition: true));
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var before = Balances(native); var request = UiRequest(native); var owner = request.OwnerEffectId;
                var payment = native.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == owner).Payment;
                var paymentId = payment.PaymentId; var window = PaymentWindow(native);
                Assert.That(request.InteractionTypeId, Is.EqualTo("action.decline_effect"));
                Assert.That(((Button)Read(window, "ConditionPaymentExecuteButton")).interactable, Is.False);
                Assert.That(((Button)Read(window, "ConditionPaymentDeclineButton")).gameObject.activeInHierarchy, Is.True);
                Assert.That(((Button)Read(window, "CloseButton")).gameObject.activeInHierarchy, Is.False);
                Assert.That(((Behaviour)Read(window, "CloseInputHandler")).enabled, Is.False);
                yield return CaptureUi("统一支付-条件小窗与底栏", native);
                try
                {
                    yield return InvokePassenger(native); yield return SaveOneSubstitution(native, ResourceType.Iron);
                }
                finally
                {
                    // 即使生产回执抛异常，也先保提交后事实；不吞原异常或以销毁栈代替原操作。
                    var visible = Visible(native);
                    Evidence("统一支付-条件替代提交后本人投影", native, new { IsolatedSeam = true,
                        visible.ActiveSettlementWindowId, visible.CharacterSettlementOptions,
                        visible.PreparedActionPayments, CurrentRequest = UiRequest(native),
                        Balances = Balances(native), Markers = Markers(native.State) });
                }
                payment = native.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == owner).Payment;
                Assert.That(payment.PaymentId, Is.EqualTo(paymentId)); Assert.That(payment.IsCommitRequested || payment.IsCommitted, Is.False);
                Assert.That(Balances(native), Is.EqualTo(before)); Assert.That(Markers(native.State), Is.EqualTo(3));
                Assert.That(UiRequest(native).OwnerEffectId, Is.EqualTo(owner));
                window = PaymentWindow(native); var execute = (Button)Read(window, "ConditionPaymentExecuteButton");
                Assert.That(execute.IsInteractable(), Is.True);
                yield return CaptureUi("统一支付-分配返回条件原宿主", native);
                execute.onClick.Invoke(); execute.onClick.Invoke(); yield return null; native.Applied();
                Assert.That(native.Commands.Count(command => command.Kind == GameCommandKind.AnswerInteraction && command.OptionIds.Contains("continue")), Is.EqualTo(1));
                Assert.That(native.State.FindPlayer(1).Resources.Iron, Is.Zero);
                Assert.That(native.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1)); Assert.That(Markers(native.State), Is.EqualTo(2));
                Assert.That(native.State.EffectRuntime.RuleEvents.Count(value => value.SourceEffectId == owner &&
                    value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(1));
                Evidence("统一支付-条件原确认只扣一次", native, new { IsolatedSeam = true, paymentId, before, After = Balances(native) });
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileConditionDeclinesBothSidesAndOrdinaryPayKeepsRequiredPermission()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            foreach (var condition in new[] { true, false })
            {
                var prepared = Production(true); LoadPassenger(prepared); State(prepared).FindPlayer(1).Resources.Iron = 3;
                var pay = ResourceEffectSpecFactory.Pay(1, new ResourceSet { Iron = 1 });
                Run(prepared, condition ? EffectSpec.Condition(new[] { pay },
                    new[] { ResourceEffectSpecFactory.Gain(1, ResourceType.PureOriginium, 1) }, ruleCondition: true) : pay);
                Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
                using (native)
                {
                    var before = Balances(native); var view = PaymentWindow(native);
                    Assert.That(view, Is.Not.Null); var decline = (Button)Read(view, "ConditionPaymentDeclineButton");
                    Assert.That(decline.gameObject.activeInHierarchy, Is.EqualTo(condition));
                    yield return CaptureUi(condition ? "统一支付-条件放弃权限" : "统一支付-普通必付共用小窗", native);
                    var button = condition ? decline : (Button)Read(view, "ConditionPaymentExecuteButton");
                    button.onClick.Invoke(); yield return null; native.Applied();
                    if (condition) Assert.That(Balances(native), Is.EqualTo(before), "放弃不执行左式和右式");
                    else Assert.That(native.State.FindPlayer(1).Resources.Iron, Is.EqualTo(2));
                    Assert.That(native.Commands, Has.Count.EqualTo(1));
                }
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileDeploymentNavigatesOffscreenLegalSlotAndConfirmsOnce()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false); var state = State(prepared);
            // 仅隔离接缝准备，不计正常获得或完整对局；实际场景/输入/提交使用生产入口。
            var city = state.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == 1);
            var location = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == "G-03");
            Position(state, city, state.ItemZones.Zones.Single(zone => zone.ParentId == location.ZoneId && zone.TemplateId == "core.zone.city_dock").ZoneId);
            var preparationInfluence = (InfluenceService)Read(prepared, "Influence");
            foreach (var occupied in preparationInfluence.GetLegalPlacementSlotIds(state, 1).Where(slot => slot != "route:G1:0").ToArray())
            {
                var owner = new[] { 2, 3 }.First(player => preparationInfluence.GetLegalPlacementSlotIds(state, player).Contains(occupied));
                Run(prepared, InfluenceEffectSpecFactory.PlaceInfluence(owner, occupied));
            }
            Assert.That(preparationInfluence.GetLegalPlacementSlotIds(state, 1), Is.EquivalentTo(new[] { "route:G1:0" }),
                "仅隔离准备：用正式放置Effect占据其他槽位，保留原失败唯一合法G1");
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var driverType = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
                var host = new GameObject("隔离部署原驱动接线"); host.SetActive(false);
                var driver = host.AddComponent(driverType);
                var folder = Environment.GetEnvironmentVariable("NMC_GAMEPLAY_UI_EVIDENCE"); Directory.CreateDirectory(folder);
                Write(driver, "mobile", native.Mobile); Write(driver, "session", native.Session); Write(driver, "hud", native.Hud);
                Write(driver, "output", folder); Write(driver, "selectedCase", "isolated-deploy"); Write(driver, "stage", "隔离部署视口外目标");
                var influencePresenter = Read(Read(native.Mobile, "turnActionPresenter"), "influenceActionPresenter");
                native.ObserveGateway(influencePresenter); Call(driver, "ObserveGateway", influencePresenter);
                IEnumerator RunDriver(IEnumerator routine, Action done) { yield return routine; done(); }
                bool done = false;
                var deploy = (Button)Read(Read(native.Hud, "ActionPanelView"), "DeployButton");
                ((MonoBehaviour)native.Mobile).StartCoroutine(RunDriver((IEnumerator)Call(driver, "Press", deploy, "隔离原部署按钮"), () => done = true));
                for (var frame = 0; frame < 2000 && !done; frame++) yield return null;
                Assert.That(done, Is.True);
                var influence = (InfluenceService)Read(native.Mobile, "influenceService");
                var actor = (int)Read(native.Mobile, "localPlayerId");
                var legal = influence.GetLegalPlacementSlotIds(native.State, actor);
                var buttonType = Type.GetType("YC.Presentation.MapButtonView, Assembly-CSharp", true);
                var buttons = UnityEngine.Object.FindObjectsOfType(buttonType).Cast<Component>().Where(button =>
                    (WorkflowHighlightTargetKind)Read(button, "TargetKind") == WorkflowHighlightTargetKind.InfluenceSlot &&
                    (bool)Read(button, "IsInteractive") && legal.Contains((string)Read(button, "TargetId"))).ToArray();
                Assert.That(buttons, Is.Not.Empty);
                var navigation = Type.GetType("YC.Presentation.MapDisplayController, Assembly-CSharp", true)
                    .GetProperty("Active", BindingFlags.Static | BindingFlags.Public).GetValue(null);
                var classifier = Type.GetType("YC.Presentation.TabletopPointerClassifier, Assembly-CSharp", true)
                    .GetMethod("CanRouteMapPointer", BindingFlags.Static | BindingFlags.Public);
                bool RouteAllowed(Component button) => (bool)classifier.Invoke(null, new object[] {
                    (Vector2)Camera.main.WorldToScreenPoint(((Collider2D)Read(button, "HitArea")).bounds.center) });
                var unchanged = JsonConvert.SerializeObject(native.State); int before = native.Commands.Count;
                for (var step = 0; step < 16 && buttons.Any(RouteAllowed); step++)
                {
                    Call(navigation, "SetZoomTarget", Camera.main.pixelRect.center,
                        (float)Read(navigation, "targetZoom") + (float)Read(navigation, "wheelZoomStep"));
                    yield return null; yield return new WaitForSecondsRealtime(.4f);
                }
                Evidence("部署入口-正式合法高亮均在视口外", native, new { IsolatedSeam = true, Legal = legal,
                    Targets = buttons.Select(button => driverType.GetMethod("MapInputDiagnostics", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { button })).ToArray() });
                Assert.That(buttons.Any(RouteAllowed), Is.False, "只通过原滚轮入口制备视口外场景");
                Assert.That(JsonConvert.SerializeObject(native.State), Is.EqualTo(unchanged)); Assert.That(native.Commands.Count, Is.EqualTo(before));
                var slot = (string)driverType.GetMethod("ChooseDeploySlot", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { legal });
                Assert.That(legal, Does.Contain(slot));
                yield return CaptureUi("部署入口-正常导航前", native);
                done = false;
                ((MonoBehaviour)native.Mobile).StartCoroutine(RunDriver((IEnumerator)Call(driver, "MapPress",
                    WorkflowHighlightTargetKind.InfluenceSlot, slot, "隔离原地图首击"), () => done = true));
                for (var frame = 0; frame < 2000 && !done; frame++) yield return null;
                Assert.That(done, Is.True); Assert.That(native.Commands.Count, Is.EqualTo(before), "首击只预选，不提交部署");
                Assert.That(JsonConvert.SerializeObject(native.State), Is.EqualTo(unchanged));
                var target = buttons.Single(button => (string)Read(button, "TargetId") == slot);
                Assert.That((bool)Read(target, "IsSelected"), Is.True); Assert.That(RouteAllowed(target), Is.True);
                yield return CaptureUi("部署入口-原首击预选", native);
                done = false;
                ((MonoBehaviour)native.Mobile).StartCoroutine(RunDriver((IEnumerator)Call(driver, "MapPress",
                    WorkflowHighlightTargetKind.InfluenceSlot, slot, "隔离原地图二击确认"), () => done = true));
                for (var frame = 0; frame < 2000 && !done; frame++) yield return null;
                Assert.That(done, Is.True); native.Applied();
                Assert.That(native.Commands.Count, Is.EqualTo(before + 1));
                Assert.That(native.Commands.Last().Kind, Is.EqualTo(GameCommandKind.DeployInfluence));
                Assert.That(native.Commands.Last().TargetId, Is.EqualTo(slot));
                Assert.That(native.State.Map.Influences.Any(value => value.PlayerId == actor && value.SlotId == slot), Is.True);
                Evidence("部署入口-原二击唯一成功回执", native, new { IsolatedSeam = true, Slot = slot,
                    Command = native.Commands.Last(), ExactResult = native.Last, NormalGameAcceptance = false });
                yield return CaptureUi("部署入口-原二击确认完成", native);
                UnityEngine.Object.Destroy(host);
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileMapNavigationRevealsG1WithoutChangingGameState()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            Native native = null; yield return Native.CreatePlayScene(Production(false), value => native = value);
            using (native)
            {
                var buttonType = Type.GetType("YC.Presentation.MapButtonView, Assembly-CSharp", true);
                var target = UnityEngine.Object.FindObjectsOfType(buttonType).Cast<Component>().Single(button =>
                    (WorkflowHighlightTargetKind)Read(button, "TargetKind") == WorkflowHighlightTargetKind.InfluenceSlot &&
                    (string)Read(button, "TargetId") == "route:G1:0");
                var driverType = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
                var diagnostics = driverType.GetMethod("MapInputDiagnostics", BindingFlags.Static | BindingFlags.NonPublic);
                var navigationType = Type.GetType("YC.Presentation.MapDisplayController, Assembly-CSharp", true);
                var navigation = navigationType.GetProperty("Active", BindingFlags.Static | BindingFlags.Public).GetValue(null);
                var classifier = Type.GetType("YC.Presentation.TabletopPointerClassifier, Assembly-CSharp", true)
                    .GetMethod("CanRouteMapPointer", BindingFlags.Static | BindingFlags.Public);
                bool RouteAllowed() => (bool)classifier.Invoke(null, new object[] {
                    (Vector2)Camera.main.WorldToScreenPoint(((Collider2D)Read(target, "HitArea")).bounds.center) });
                var before = diagnostics.Invoke(null, new object[] { target });
                Evidence("正常地图导航-G1初始几何与HUD命中", native, before);
                var baseline = JsonConvert.SerializeObject(native.Session.State);
                // 若初始镜头已显露目标，仅用玩家已有滚轮入口制备导航定位；不写相机或地图字段。
                for (var step = 0; step < 4 && RouteAllowed(); step++)
                {
                    var point = Camera.main.pixelRect.center;
                    var zoom = (float)Read(navigation, "targetZoom");
                    var wheel = (float)Read(navigation, "wheelZoomStep");
                    Call(navigation, "SetZoomTarget", point, zoom + wheel);
                    yield return null; yield return new WaitForSecondsRealtime(.7f);
                }
                Evidence("正常地图导航-G1导航前几何", native, diagnostics.Invoke(null, new object[] { target }));
                var records = new List<object>();
                var routine = (IEnumerator)driverType.GetMethod("RevealMapTarget", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { target, (Action<object>)(record => records.Add(record)) });
                bool navigationCompleted = false;
                IEnumerator RunNavigation() { yield return routine; navigationCompleted = true; }
                ((MonoBehaviour)native.Mobile).StartCoroutine(RunNavigation());
                for (var frame = 0; frame < 2000 && !navigationCompleted; frame++) yield return null;
                Assert.That(navigationCompleted, Is.True, "正常地图运行时手势协程应完成");
                Evidence("正常地图导航-G1正式手势后几何", native, new { IsolatedSeam = true, Records = records,
                    After = diagnostics.Invoke(null, new object[] { target }), StateUnchanged = baseline == JsonConvert.SerializeObject(native.Session.State) });
                Assert.That(RouteAllowed(), Is.True);
                Assert.That(JsonConvert.SerializeObject(native.Session.State), Is.EqualTo(baseline));
                Assert.That(native.Commands, Is.Empty, "地图导航不提交游戏命令");
                yield return CaptureUi("正常地图导航-G1已显露", native);
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileCannotTacticShowsThreeResourceIconsAndSubmitsOriginalId()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false); var state = State(prepared);
            var card = state.ItemZones.Items.First(item => item.DefinitionId == "cannot");
            card.OwnerSubject = card.ControllerSubject = ItemSubjectRef.Player(1);
            Position(state, card, state.ItemZones.Zones.Single(zone => zone.LocalZoneId == "hand" && zone.OwnerPlayerId == 1).ZoneId);
            Cover(prepared, card.ItemId);
            foreach (var player in state.Players) { player.Resources.Originium = 1; player.Resources.GoldVoucher = 0; }
            Run(prepared, CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic"));
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                yield return null; yield return new WaitForSecondsRealtime(.7f);
                var request = UiRequest(native);
                Assert.That(request.PromptKey, Is.EqualTo("character.cannot.tactic.choose_resource"));
                Assert.That(request.CandidateIds, Is.EqualTo(new[] { "originium", "originium-shard", "iron" }));
                Assert.That(request.Presentation?.Prompt?.Key, Is.EqualTo("core:interaction.character.cannot.tactic.resource.prompt"));
                var expected = new[] { "core:resource.orirock", "core:resource.originium_shard", "core:resource.iron" };
                var rowType = Type.GetType("YC.Presentation.EffectDialogOptionRowView, Assembly-CSharp", true);
                var rows = UnityEngine.Object.FindObjectsOfType(rowType).Cast<Component>()
                    .Where(row => row.gameObject.activeInHierarchy && row.name.StartsWith("Character Effect Option ", StringComparison.Ordinal))
                    .OrderBy(row => int.Parse(row.name.Substring("Character Effect Option ".Length))).ToArray();
                Assert.That(rows, Has.Length.EqualTo(3));
                var rendered = new List<object>();
                for (var index = 0; index < rows.Length; index++)
                {
                    var textView = Read(rows[index], "labelDocument"); Assert.That(textView, Is.Not.Null);
                    var document = (GameplayTextDocument)Read(textView, "document"); Assert.That(document, Is.Not.Null);
                    Assert.That(document.Runs.OfType<IconRun>().Select(run => run.IconKey), Is.EqualTo(new[] { expected[index] }));
                    var images = ((IEnumerable)Read(textView, "iconPool")).Cast<object>().Select(slot => (Image)Read(slot, "Image"))
                        .Where(image => image != null && image.gameObject.activeInHierarchy && image.sprite != null).ToArray();
                    Assert.That(images, Has.Length.EqualTo(1), "原选项正文必须实际生成图标，不能只有AltText");
                    rendered.Add(new { CandidateId = request.CandidateIds[index], IconKey = expected[index],
                        Text = document.ToPlainText(), Sprite = UnityEditor.AssetDatabase.GetAssetPath(images[0].sprite),
                        RenderCount = Read(textView, "RenderCount") });
                }
                yield return CaptureUi("坎诺特计谋-原资源选择页三图标", native);
                Evidence("坎诺特计谋-三个原选项图标宿主", native, new { IsolatedSeam = true, NormalGameAcceptance = false,
                    request.InteractionId, request.CandidateIds, Rendered = rendered });
                var driverType = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
                var root = new GameObject("坎诺特原选择页实际指针接缝"); root.SetActive(false);
                var driver = root.AddComponent(driverType);
                Write(driver, "session", native.Session); Write(driver, "mobile", native.Mobile); Write(driver, "hud", native.Hud);
                Write(driver, "output", Environment.GetEnvironmentVariable("NMC_GAMEPLAY_UI_EVIDENCE"));
                Write(driver, "selectedCase", "isolated-cannot-icons"); Write(driver, "stage", "坎诺特原资源页图标接缝");
                IEnumerator Drive(Button button, string description)
                {
                    bool done = false;
                    IEnumerator Click() { yield return (IEnumerator)Call(driver, "Press", button, description); done = true; }
                    ((MonoBehaviour)native.Mobile).StartCoroutine(Click());
                    for (var frame = 0; frame < 1000 && !done; frame++) yield return null;
                    Assert.That(done, Is.True);
                }
                yield return Drive((Button)Read(rows[0], "Button"), "坎诺特原源岩选项");
                Assert.That(native.Commands, Is.Empty, "选择仅编辑原草稿");
                yield return Drive(ActiveButton(native, "Confirm Selection"), "坎诺特原选择页首次确认");
                native.Applied(); Assert.That(native.Commands, Has.Count.EqualTo(1));
                Assert.That(native.Commands.Single().OptionIds, Is.EqualTo(new[] { "originium" }));
                Assert.That(native.Commands.Single().Parameters["interactionId"], Is.EqualTo(request.InteractionId));
                Assert.That(native.Commands.Single().Parameters["expectedRevision"], Is.EqualTo(request.StateRevision.ToString()));
                Assert.That(UiRequest(native)?.InteractionId, Is.Not.EqualTo(request.InteractionId));
                Assert.That(native.State.Players.All(player => player.Resources.Originium == 0 && player.Resources.GoldVoucher == 2), Is.True);
                Evidence("坎诺特计谋-原ID准确提交与后继", native, new { IsolatedSeam = true, NormalGameAcceptance = false,
                    OriginalId = "originium", Successor = UiRequest(native), CardUse = native.State.CharacterUses.Last(use => use.CardInstanceId == card.ItemId) });
                yield return CaptureUi("坎诺特计谋-原ID确认后继", native);
                UnityEngine.Object.Destroy(root);
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileTexasTacticMovesTwiceThroughMapWithOnePayment() => TexasMapWorkflow(false);

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualDriverNormalChoiceCompletesTexasMapSourceAndTarget() => TexasMapWorkflow(true);

        private IEnumerator TexasMapWorkflow(bool driverEntry)
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false); var state = State(prepared);
            var influence = (InfluenceService)Read(prepared, "Influence");
            // 仅隔离准备正式物件/Effect，不冒充正常获得角色或正常整局。
            for (var index = 0; index < 3; index++)
                Run(prepared, InfluenceEffectSpecFactory.PlaceInfluence(1,
                    influence.GetLegalPlacementSlotIds(state, 1).First(slot => slot.StartsWith("route:", StringComparison.Ordinal))));
            var removalSlot = influence.GetLegalPlacementSlotIds(state, 2).First(slot => slot.StartsWith("route:", StringComparison.Ordinal));
            Run(prepared, InfluenceEffectSpecFactory.PlaceInfluence(2, removalSlot));
            var card = state.ItemZones.Items.First(item => item.DefinitionId == "texas");
            card.OwnerSubject = card.ControllerSubject = ItemSubjectRef.Player(1);
            Position(state, card, state.ItemZones.Zones.Single(zone => zone.LocalZoneId == "hand" && zone.OwnerPlayerId == 1).ZoneId);
            Cover(prepared, card.ItemId); state.FindPlayer(1).Resources.GoldVoucher = 8;
            Run(prepared, CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic"));
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var driverType = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
                var host = new GameObject("德克萨斯实际地图原驱动接缝"); host.SetActive(false);
                var driver = host.AddComponent(driverType);
                Write(driver, "mobile", native.Mobile); Write(driver, "session", native.Session); Write(driver, "hud", native.Hud);
                var output = Environment.GetEnvironmentVariable("NMC_GAMEPLAY_UI_EVIDENCE"); Directory.CreateDirectory(output);
                Write(driver, "output", output);
                Write(driver, "selectedCase", "isolated-texas-map"); Write(driver, "stage", "德克萨斯条件付款与两次地图移动隔离接缝");
                var buttonType = Type.GetType("YC.Presentation.MapButtonView, Assembly-CSharp", true);
                var rowType = Type.GetType("YC.Presentation.EffectDialogOptionRowView, Assembly-CSharp", true);
                Component[] Interactive() => UnityEngine.Object.FindObjectsOfType(buttonType).Cast<Component>()
                    .Where(button => (WorkflowHighlightTargetKind)Read(button, "TargetKind") == WorkflowHighlightTargetKind.InfluenceSlot &&
                        (bool)Read(button, "IsInteractive")).ToArray();
                void NoRawMoveList() => Assert.That(UnityEngine.Object.FindObjectsOfType(rowType).Cast<Component>().Any(row =>
                    row.gameObject.activeInHierarchy && ((Text)Read(row, "Label")).text.Contains("item_") &&
                    ((Text)Read(row, "Label")).text.Contains("移动到")), Is.False);
                IEnumerator Drive(string method, params object[] args)
                {
                    bool done = false;
                    IEnumerator RunDriver() { yield return (IEnumerator)Call(driver, method, args); done = true; }
                    ((MonoBehaviour)native.Mobile).StartCoroutine(RunDriver());
                    for (var frame = 0; frame < 2000 && !done; frame++) yield return null;
                    Assert.That(done, Is.True, method + "应经实际运行时协程完成");
                }
                var payment = PaymentWindow(native);
                Assert.That(payment, Is.Not.Null); Assert.That(UiRequest(native).InteractionTypeId, Is.EqualTo("action.decline_effect"));
                yield return Drive("Press", (Button)Read(payment, "ConditionPaymentExecuteButton"), "德克萨斯条件原执行按钮仅付款一次");
                native.Applied(); Assert.That(native.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
                Assert.That(UiRequest(native).PromptKey, Is.EqualTo("character.texas.tactic.choose_removal"));
                yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, removalSlot, "德克萨斯原地图移除首击");
                yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, removalSlot, "德克萨斯原地图移除确认");
                native.Applied(); Assert.That(native.State.Map.Influences.Any(item => item.SlotId == removalSlot), Is.False);
                string previousSource = null;
                for (var move = 0; move < 2; move++)
                {
                    var request = UiRequest(native);
                    Assert.That(request.InteractionTypeId, Is.EqualTo("character.ability.choice.move"));
                    Assert.That(request.PromptKey, Is.EqualTo(move == 0 ? "character.texas.tactic.choose_first_move" : "character.texas.tactic.choose_second_move"));
                    NoRawMoveList();
                    var pairs = request.CandidateIds.Select(id => new { Id = id, Parts = id.Split('|') }).ToArray();
                    Assert.That(pairs.All(pair => pair.Parts.Length == 3 && pair.Parts[0] == "move"), Is.True);
                    if (previousSource != null) Assert.That(pairs.Any(pair => pair.Parts[1] == previousSource), Is.False, "第二次正式候选必须按稳定实例排重");
                    if (driverEntry)
                    {
                        Call(driver, "ObserveGateway", native.Mobile);
                        int entryCommandCount = native.Commands.Count;
                        yield return Drive("NormalChoice");
                        native.Applied(); Assert.That(native.Commands.Count, Is.EqualTo(entryCommandCount + 1));
                        var entryCommand = native.Commands.Last(); var driverAnswer = entryCommand.OptionIds.Single();
                        Assert.That(request.CandidateIds, Does.Contain(driverAnswer));
                        Assert.That(entryCommand.Parameters["interactionId"], Is.EqualTo(request.InteractionId));
                        Assert.That(entryCommand.Parameters["expectedRevision"], Is.EqualTo(request.StateRevision.ToString()));
                        Assert.That(native.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
                        previousSource = driverAnswer.Split('|')[1];
                        Evidence("德克萨斯-当前正常驱动入口-第" + (move + 1) + "次原地图移动", native,
                            new { IsolatedSeam = true, NormalGameAcceptance = false, request.InteractionId, Answer = driverAnswer });
                        continue;
                    }
                    var sources = pairs.Select(pair => pair.Parts[1]).Distinct().ToDictionary(id => id,
                        id => native.State.Map.Influences.Single(item => InfluenceIdentity.GetStableId(native.State, item) == id).SlotId);
                    var sourceButton = Interactive().First(button => sources.Values.Contains((string)Read(button, "TargetId")));
                    var sourceSlot = (string)Read(sourceButton, "TargetId"); var sourceId = sources.Single(pair => pair.Value == sourceSlot).Key;
                    var before = JsonConvert.SerializeObject(native.State); int commandCount = native.Commands.Count;
                    yield return CaptureUi("德克萨斯-第" + (move + 1) + "次移动-实际地图来源", native);
                    yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, sourceSlot, "德克萨斯地图选择来源");
                    if (Interactive().Any(button => (string)Read(button, "TargetId") == sourceSlot && (bool)Read(button, "IsSelected")))
                        yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, sourceSlot, "德克萨斯地图来源确认");
                    Assert.That(native.Commands.Count, Is.EqualTo(commandCount)); Assert.That(JsonConvert.SerializeObject(native.State), Is.EqualTo(before));
                    var allowed = pairs.Where(pair => pair.Parts[1] == sourceId).ToArray();
                    var targetButton = Interactive().First(button => allowed.Any(pair => pair.Parts[2] == (string)Read(button, "TargetId")));
                    var target = (string)Read(targetButton, "TargetId"); var answer = allowed.Single(pair => pair.Parts[2] == target).Id;
                    yield return CaptureUi("德克萨斯-第" + (move + 1) + "次移动-实际地图目标", native);
                    yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, target, "德克萨斯地图目标首击");
                    Assert.That(native.Commands.Count, Is.EqualTo(commandCount));
                    yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, target, "德克萨斯地图目标原二击确认");
                    native.Applied(); Assert.That(native.Commands.Count, Is.EqualTo(commandCount + 1));
                    Assert.That(native.Commands.Last().OptionIds, Is.EqualTo(new[] { answer }));
                    Assert.That(native.Commands.Last().Parameters["interactionId"], Is.EqualTo(request.InteractionId));
                    Assert.That(native.Commands.Last().Parameters["expectedRevision"], Is.EqualTo(request.StateRevision.ToString()));
                    Assert.That(native.State.Map.Influences.Single(item => InfluenceIdentity.GetStableId(native.State, item) == sourceId).SlotId, Is.EqualTo(target));
                    Assert.That(native.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5), "移动不得再次支付条件费用");
                    Assert.That((string)Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                    Evidence("德克萨斯-第" + (move + 1) + "次地图准确回执", native, new { IsolatedSeam = true, NormalGameAcceptance = false,
                        request.InteractionId, request.StateRevision, SourceId = sourceId, SourceSlot = sourceSlot, Target = target, Answer = answer });
                    previousSource = sourceId;
                }
                Assert.That(native.Commands, Has.Count.EqualTo(4), "条件执行、移除与两次移动各只提交一次");
                Assert.That(UiRequest(native)?.InteractionTypeId, Is.Not.EqualTo("character.ability.choice.move")); NoRawMoveList();
                yield return CaptureUi("德克萨斯-两次实际地图移动完成", native);
                UnityEngine.Object.Destroy(host);
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualDriverUiPointerRequiresVisibleFirstHit()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false); State(prepared).FindPlayer(1).Resources.Iron = 2;
            Run(prepared, EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, new ResourceSet { Iron = 1 }) },
                new[] { ResourceEffectSpecFactory.Gain(1, ResourceType.PureOriginium, 1) }, ruleCondition: true));
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                yield return null; yield return new WaitForSecondsRealtime(.7f);
                var view = PaymentWindow(native);
                var execute = (Button)Read(view, "ConditionPaymentExecuteButton");
                var hidden = (Button)Read(view, "CloseButton");
                var driverType = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
                var statics = BindingFlags.Static | BindingFlags.NonPublic;
                var visible = driverType.GetMethod("IsUiTargetVisible", statics);
                var firstHit = driverType.GetMethod("FirstUiClickTarget", statics);
                Assert.That((bool)visible.Invoke(null, new object[] { execute.gameObject }), Is.True);
                Assert.That((bool)visible.Invoke(null, new object[] { hidden.gameObject }), Is.False);
                Assert.That(firstHit.Invoke(null, new object[] { new Vector2(631.3333f, -10568.667f) }), Is.Null,
                    "原失败屏外坐标不得成为可点击目标");
                var root = new GameObject("实际控件首命中驱动隔离检查"); root.SetActive(false);
                var driver = root.AddComponent(driverType);
                var folder = Environment.GetEnvironmentVariable("NMC_GAMEPLAY_UI_EVIDENCE");
                Directory.CreateDirectory(folder);
                Write(driver, "output", folder); Write(driver, "stage", "实际SampleScene指针边界隔离检查");
                Write(driver, "session", native.Session); Write(driver, "mobile", native.Mobile); Write(driver, "hud", native.Hud);
                Write(driver, "selectedCase", "isolated-ui-pointer");
                var before = JsonConvert.SerializeObject(native.State);
                var rejected = (IEnumerator)Call(driver, "PointerSequence", hidden.gameObject, "隐藏的条件确认关闭按钮不得触发");
                var error = Assert.Catch<InvalidOperationException>(() => rejected.MoveNext());
                Assert.That(error.Message, Does.Contain("控件位于屏外、被裁切或被遮挡"));
                Assert.That(native.Commands, Is.Empty); Assert.That(JsonConvert.SerializeObject(native.State), Is.EqualTo(before));
                var routine = (IEnumerator)Call(driver, "PointerSequence", execute.gameObject, "实际可见首命中执行按钮");
                bool completed = false;
                IEnumerator RunPointer() { yield return routine; completed = true; }
                ((MonoBehaviour)native.Mobile).StartCoroutine(RunPointer());
                for (var frame = 0; frame < 1000 && !completed; frame++) yield return null;
                Assert.That(completed, Is.True); native.Applied(); Assert.That(native.Commands, Has.Count.EqualTo(1));
                Evidence("驱动指针边界-实际宿主首命中与屏外拒绝", native, new { IsolatedSeam = true,
                    NormalGameAcceptance = false, OriginalOffscreenPointRejected = true, HiddenButtonCallbackInvoked = false,
                    HiddenAttemptStateUnchanged = true, VisibleOriginalButtonSubmittedOnce = true });
                UnityEngine.Object.Destroy(root);
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator DiagnoseRecordedNormalGameDeploymentEligibility()
        {
            // 只读原件并在独立内存中重放准确命令，用于定位；绝不作为正常UI验收或续局。
            var folder = Path.Combine(Environment.CurrentDirectory,
                "Logs/GameplayCoverageCampaign/组合-20261015-091839/full-game-局1");
            if (!File.Exists(Path.Combine(folder, "首失败.json"))) Assert.Ignore("本机没有对应首失败原件");
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false);
            var savedJson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(folder,
                "回合5-正常采集玩家4正式状态后.json")));
            // EffectRuntimeState是EffectRuntime的兼容别名，诊断导出同时记录两者，不能重复填充同一列表。
            savedJson.Remove("EffectRuntimeState");
            var saved = savedJson.ToObject<GameState>();
            // 原诊断JSON同时含旧只读投影；使用正式实体绑定恢复派生列表，权威ItemZones保持原样。
            ItemZoneLegacyProjection.Bind(saved, true);
            Write(prepared, "State", saved);
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var startRevision = native.State.EffectRuntime.StateRevision;
                var records = File.ReadLines(Path.Combine(folder, "命令与准确回执.jsonl"))
                    .Select(Newtonsoft.Json.Linq.JObject.Parse)
                    .Where(record => (int)record["BeforeRevision"] >= startRevision).ToArray();
                foreach (var record in records)
                {
                    Assert.That(native.State.EffectRuntime.StateRevision, Is.EqualTo((int)record["BeforeRevision"]));
                    var command = record["Command"].ToObject<GameCommand>();
                    var result = native.Session.Submit(command);
                    Assert.That(result.Succeeded, Is.EqualTo((bool)record["Result"]["CommandResult"]["Succeeded"]), command.CommandId);
                    Assert.That(native.State.EffectRuntime.StateRevision, Is.EqualTo((int)record["AfterRevision"]), command.CommandId);
                }
                Assert.That(native.State.GameId, Is.EqualTo("00a6f555ecb84b429a9b1d8c16d94ceb"));
                Assert.That(native.State.EffectRuntime.StateRevision, Is.EqualTo(4220));
                var influence = new InfluenceService(new YC.Domain.Maps.MapQueryService(
                    YC.Domain.Maps.StaticMapDefinitions.Resolve(native.State.MapId)));
                Evidence("正常局091839-原命令隔离复算部署资格", native, new { IsolatedDiagnosis = true,
                    NormalGameAcceptance = false, OriginalFilesUnmodified = true, StartRevision = startRevision,
                    ReplayedCommandCount = records.Length, native.State.GameId, native.State.Round,
                    Revision = native.State.EffectRuntime.StateRevision, PlayerId = 2,
                    LegalPlacementSlotIds = influence.GetLegalPlacementSlotIds(native.State, 2),
                    HasPendingChoice = native.State.HasPendingChoice(),
                    HasMainAction = new MainActionBudgetService().HasAvailableMainAction(native.State, 2),
                    Supply = native.State.FindPlayer(2).InfluenceSupply,
                    Movement = native.State.Map.Influences.Where(item => item.PlayerId == 2).Select(item => new {
                        InfluenceId = InfluenceIdentity.GetStableId(native.State, item), item.SlotId,
                        LegalTargets = influence.GetLegalMovementSlotIds(native.State, InfluenceIdentity.GetStableId(native.State, item)) }).ToArray() });
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileConditionPaymentUses32ButtonStatesFromCurrentHost()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false);
            State(prepared).FindPlayer(1).Resources.Iron = 2;
            Run(prepared, EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, new ResourceSet { Iron = 1 }) },
                new[] { ResourceEffectSpecFactory.Gain(1, ResourceType.PureOriginium, 1) }, ruleCondition: true));
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var view = PaymentWindow(native);
                var buttons = new[] { (Button)Read(view, "ConditionPaymentExecuteButton"),
                    (Button)Read(view, "ConditionPaymentDeclineButton") };
                var roles = new[] { "primary", "action" };
                var stateType = Type.GetType("YC.Presentation.UiMainButtonState, Assembly-CSharp", true);
                var states = buttons.Select(button => button.GetComponent(stateType)).ToArray();
                var before = JsonConvert.SerializeObject(native.State);
                var bindings = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory,
                    "Logs/GameplayUiBugfixes/条件确认按钮3.2-修补交付.json")))["Bindings"];
                EventSystem.current.SetSelectedGameObject(null);
                for (var index = 0; index < buttons.Length; index++)
                {
                    Assert.That(buttons[index].IsActive() && buttons[index].IsInteractable(), Is.True);
                    Assert.That(states[index], Is.Not.Null);
                    Assert.That(buttons[index].GetComponents<MonoBehaviour>().Any(component =>
                        component != null && component.GetType().Name.StartsWith("UiShared", StringComparison.Ordinal)), Is.False);
                    foreach (var pair in new[] { new[] { "normal", "default" }, new[] { "hover", "hover" },
                        new[] { "pressed", "pressed" }, new[] { "disabled", "disabled" } })
                    {
                        var sprite = (Sprite)Read(states[index], pair[0]);
                        var path = UnityEditor.AssetDatabase.GetAssetPath(sprite);
                        Assert.That(path, Is.EqualTo((string)bindings[roles[index]][pair[1]]["Asset"]));
                        Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(path),
                            Is.EqualTo((string)bindings[roles[index]][pair[1]]["GUID"]));
                    }
                }
                var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
                foreach (var phase in new[] { "default", "hover", "pressed", "disabled" })
                {
                    for (var index = 0; index < buttons.Length; index++)
                    {
                        if (phase == "default")
                        {
                            ExecuteEvents.Execute(buttons[index].gameObject, pointer, ExecuteEvents.pointerExitHandler);
                            ExecuteEvents.Execute(buttons[index].gameObject, pointer, ExecuteEvents.deselectHandler);
                        }
                        if (phase == "hover") ExecuteEvents.Execute(buttons[index].gameObject, pointer, ExecuteEvents.pointerEnterHandler);
                        if (phase == "pressed") ExecuteEvents.Execute(buttons[index].gameObject, pointer, ExecuteEvents.pointerDownHandler);
                        if (phase == "disabled") Call(states[index], "SetAvailable", false);
                    }
                    yield return CaptureUi("条件确认3.2-两按钮-" + phase, native);
                    for (var index = 0; index < buttons.Length; index++)
                    {
                        var face = (Image)Read(states[index], "face");
                        Assert.That(UnityEditor.AssetDatabase.GetAssetPath(face.sprite),
                            Is.EqualTo((string)bindings[roles[index]][phase]["Asset"]));
                    }
                    Evidence("条件确认3.2-四态-" + phase, native, new { IsolatedSeam = true,
                        NormalGameAcceptance = false, ActualHost = ((Component)view).gameObject.scene.name,
                        Buttons = buttons.Select((button, index) => new { Role = roles[index], Phase = phase,
                            Interactable = button.IsInteractable(), Controller = states[index].GetType().FullName,
                            Sprite = UnityEditor.AssetDatabase.GetAssetPath(((Image)Read(states[index], "face")).sprite) }).ToArray() });
                }
                foreach (var button in buttons)
                {
                    ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                }
                yield return null;
                Assert.That(native.Commands, Is.Empty, "禁用按钮的原指针回调不得提交");
                Assert.That(JsonConvert.SerializeObject(native.State), Is.EqualTo(before));
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileOrdinaryPaymentCapturesCurrentHostAfterTextBindingRepair()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(true); LoadPassenger(prepared);
            State(prepared).FindPlayer(1).Resources.Iron = 3;
            Run(prepared, ResourceEffectSpecFactory.Pay(1, new ResourceSet { Iron = 1 }));
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var view = PaymentWindow(native);
                Assert.That(((Button)Read(view, "ConditionPaymentDeclineButton")).gameObject.activeInHierarchy, Is.False);
                yield return CaptureUi("统一支付-修补后普通必付共用小窗", native);
                ((Button)Read(view, "ConditionPaymentExecuteButton")).onClick.Invoke();
                yield return null; native.Applied();
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileBuildButtonsCheckEachModeAndCommitFromOneClick()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            foreach (var gold in new[] { true, false })
            {
                var prepared = Production(false); State(prepared).FindPlayer(1).Resources = gold ?
                    new ResourceSet { GoldVoucher = 100 } : new ResourceSet { Originium = 100, OriginiumShard = 100, Iron = 100, PureOriginium = 100 };
                var facility = BuildCandidate(State(prepared));
                Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
                using (native)
                {
                    var build = Read(Read(native.Mobile, "turnActionPresenter"), "BuildInteraction"); native.ObserveActionPayment(build);
                    Call(native.Mobile, "OnBuildActionClicked"); Call(native.Mobile, "SelectBuildCardInPanel", facility); yield return null;
                    var old = Visible(native).PreparedActionPayments.Single();
                    Evidence("统一支付-建设模式按钮首态-" + gold, native, new { IsolatedSeam = true,
                        native.State.GameId, native.State.Round, Player = native.State.FindPlayer(1),
                        Prepared = Visible(native).PreparedActionPayments,
                        ViewModel = Call(Read(native.Mobile, "turnActionPresenter"), "BuildBuildFacilityDraftViewModel"),
                        ResourceButton = BuildPaymentButton(native, 0).IsInteractable(),
                        GoldButton = BuildPaymentButton(native, 1).IsInteractable() });
                    Assert.That(old.PaymentMode, Is.EqualTo("resources"));
                    Assert.That(BuildPaymentButton(native, 0).IsInteractable(), Is.EqualTo(!gold));
                    Assert.That(BuildPaymentButton(native, 1).IsInteractable(), Is.EqualTo(gold));
                    yield return CaptureUi(gold ? "统一支付-资源不足金券按钮可付" : "统一支付-金券不足资源按钮可付", native);
                    var before = native.State.FindPlayer(1).Resources.Clone();
                    BuildPaymentButton(native, gold ? 1 : 0).onClick.Invoke(); yield return null; native.Applied();
                    Assert.That(native.Commands.Count(command => command.Kind == GameCommandKind.BuildFacility), Is.EqualTo(1));
                    Assert.That(Read(build, "IsSubmissionInFlight"), Is.EqualTo(false));
                    if (gold)
                    {
                        var final = native.Commands.Single(command => command.Kind == GameCommandKind.BuildFacility);
                        var operations = native.Commands.Where(command => command.Kind == GameCommandKind.PreparePaymentDraft).ToArray();
                        Assert.That(operations.Select(command => command.Parameters["operation"]), Is.EqualTo(new[] { "prepare_action", "cancel_action", "prepare_action" }));
                        Assert.That(operations.Last().Parameters["paymentMode"], Is.EqualTo("gold"));
                        Assert.That(final.Parameters["paymentPreparationOwnerEffectId"], Is.Not.EqualTo(old.PreparationOwnerEffectId));
                        Assert.That(native.State.FindPlayer(1).Resources.GoldVoucher, Is.LessThan(before.GoldVoucher));
                    }
                    Assert.That(Visible(native).PreparedActionPayments, Is.Empty);
                    Assert.That(UiRequest(native).InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
                    Assert.That(Read(native.Mobile, "CanBeginGameplayAction"), Is.EqualTo(false), "已付后放置义务仍锁行动");
                }
            }
            var substituted = Production(true); LoadPassenger(substituted);
            var exactFacility = BuildCandidate(State(substituted));
            var quoted = new BuildFacilityOptionQueryService().Query(State(substituted), 1).Single(option => option.FacilityId == exactFacility);
            var selectedType = ResourcePaymentSubstitutionEffectExecutor.BasicTypes.First(type => quoted.EffectiveResourceCost.Get(type) > 0);
            var balance = quoted.EffectiveResourceCost.Clone(); balance.Set(selectedType, balance.Get(selectedType) - 1);
            State(substituted).FindPlayer(1).Resources = balance;
            Native substitutedNative = null; yield return Native.CreatePlayScene(substituted, value => substitutedNative = value);
            using (substitutedNative)
            {
                var build = Read(Read(substitutedNative.Mobile, "turnActionPresenter"), "BuildInteraction"); substitutedNative.ObserveActionPayment(build);
                Call(substitutedNative.Mobile, "OnBuildActionClicked");
                Call(substitutedNative.Mobile, "SelectBuildCardInPanel", exactFacility); yield return null;
                var original = Visible(substitutedNative).PreparedActionPayments.Single(); var before = Balances(substitutedNative);
                Assert.That(BuildPaymentButton(substitutedNative, 0).IsInteractable(), Is.False);
                yield return InvokePassenger(substitutedNative); yield return SaveOneSubstitution(substitutedNative, selectedType);
                var returned = Visible(substitutedNative).PreparedActionPayments.Single();
                Assert.That(ActionPaymentPreparation.SameIdentity(original, returned), Is.True);
                Assert.That(Balances(substitutedNative), Is.EqualTo(before)); Assert.That(Markers(substitutedNative.State), Is.EqualTo(3));
                Assert.That(substitutedNative.Commands.Any(command => command.Kind == GameCommandKind.BuildFacility), Is.False);
                Assert.That(BuildPaymentButton(substitutedNative, 0).IsInteractable(), Is.True);
                yield return CaptureUi("统一支付-分配返回建设原资源按钮", substitutedNative);
                BuildPaymentButton(substitutedNative, 0).onClick.Invoke(); yield return null; substitutedNative.Applied();
                Assert.That(substitutedNative.Commands.Count(command => command.Kind == GameCommandKind.BuildFacility), Is.EqualTo(1));
                Assert.That(Markers(substitutedNative.State), Is.EqualTo(2));
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator RestoredHotseatFirstMoveClickKeepsPreparedPayment()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false);
            State(prepared).RuleConfiguration.PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential;
            foreach (var player in State(prepared).Players)
                player.Color = RoomConfigurationService.GetSeatColor(player.PlayerId - 1);
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value, savedViewer: 2);
            using (native)
            {
                Assert.That(Read(native.Mobile, "localPlayerId"), Is.EqualTo(native.State.CurrentPlayerId));
                Assert.That(native.State.CurrentPlayerId, Is.Not.EqualTo(2), "覆盖读档时需要切换热座视角的场景");
                var move = Read(Read(native.Mobile, "turnActionPresenter"), "MoveInteraction");
                native.ObserveActionPayment(move);
                var button = (Button)Read(Read(native.Hud, "ActionPanelView"), "MoveCityButton");
                Assert.That(button.IsInteractable(), Is.True);
                button.onClick.Invoke(); yield return null; native.Applied();
                Assert.That(native.Commands.Select(command => command.Parameters["operation"]), Is.EqualTo(new[] { "prepare_action" }),
                    "读档后第一次移城点击不得自行提交取消");
                Assert.That(Visible(native).PreparedActionPayments.Single().ActionKind, Is.EqualTo("city_move"));
                Assert.That(Read(move, "CanResumePreparedDraft"), Is.EqualTo(true));
                var payment = Visible(native).PreparedActionPayments.Single();
                var digest = RuleStateDigestService.Compute(native.State);
                Native resumed = null; yield return native.ReloadSavedScene(2, value => resumed = value);
                using (resumed)
                {
                    Assert.That(RuleStateDigestService.Compute(resumed.State), Is.EqualTo(digest), "恢复付款准备不得重建或取消权威状态");
                    Assert.That(ActionPaymentPreparation.SameIdentity(payment, Visible(resumed).PreparedActionPayments.Single()), Is.True);
                    var restoredMove = Read(Read(resumed.Mobile, "turnActionPresenter"), "MoveInteraction");
                    Assert.That(Read(restoredMove, "CanResumePreparedDraft"), Is.EqualTo(true), "同笔付款准备恢复后必须接回地图工作流");
                    Assert.That(resumed.Commands, Is.Empty);
                    resumed.ObserveActionPayment(restoredMove);
                    var target = ((InteractionPresentation)Call(restoredMove, "BuildPresentation")).Highlights.First().TargetId;
                    Call(resumed.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, target); yield return null;
                    Assert.That(resumed.Commands.Any(command => command.Kind == GameCommandKind.MoveCity), Is.False);
                    Call(resumed.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, target); yield return null; resumed.Applied();
                    Assert.That(resumed.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1));
                    Assert.That(resumed.State.FindPlayer(resumed.State.StartPlayerId).CityLocationId, Is.EqualTo(target));
                }
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileMoveChangesTargetAndReturnsToSameTwoClickDraft()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(true); LoadPassenger(prepared);
            var map = new YC.Domain.Maps.MapQueryService(YC.Domain.Maps.StaticMapDefinitions.Resolve(State(prepared).MapId));
            var cost = new TravelCostService(map).GetCityMoveBaseCost();
            Assert.That(cost.OriginiumShard, Is.GreaterThan(0));
            State(prepared).FindPlayer(1).Resources = new ResourceSet { OriginiumShard = cost.OriginiumShard - 1 };
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var turn = Read(native.Mobile, "turnActionPresenter"); var move = Read(turn, "MoveInteraction"); native.ObserveActionPayment(move);
                Call(native.Mobile, "BeginMoveAction"); yield return null;
                var targets = ((InteractionPresentation)Call(move, "BuildPresentation")).Highlights.Select(value => value.TargetId).ToArray();
                Evidence("统一支付-移城准备首态", native, new { IsolatedSeam = true,
                    native.State.GameId, native.State.Round, Player = native.State.FindPlayer(1),
                    Prepared = Visible(native).PreparedActionPayments, Targets = targets,
                    CanBegin = Read(native.Mobile, "CanBeginGameplayAction"), MoveActive = Read(move, "IsActive"),
                    DraftRevision = Read(move, "draftRevision"), DraftCity = Read(move, "draftCityLocation") });
                Assert.That(targets.Length, Is.GreaterThan(1));
                Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, targets[0]); yield return null;
                var first = Visible(native).PreparedActionPayments.Single();
                Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, targets[1]); yield return null;
                var current = Visible(native).PreparedActionPayments.Single();
                Assert.That(current.TargetId, Is.EqualTo(targets[1])); Assert.That(current.PaymentId, Is.Not.EqualTo(first.PaymentId));
                Assert.That(native.Commands.Any(command => command.Kind == GameCommandKind.MoveCity), Is.False);
                var before = Balances(native); yield return InvokePassenger(native); yield return SaveOneSubstitution(native, ResourceType.OriginiumShard);
                var restored = Visible(native).PreparedActionPayments.Single();
                Assert.That(ActionPaymentPreparation.SameIdentity(current, restored), Is.True); Assert.That(restored.CanConfirm, Is.True);
                Assert.That(Balances(native), Is.EqualTo(before)); Assert.That(Markers(native.State), Is.EqualTo(3));
                Assert.That(Read(Read(native.Mobile, "interactionRouter"), "HasPendingMapConfirmation"), Is.EqualTo(true));
                yield return CaptureUi("统一支付-移城分配返回原目标草稿", native);
                Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, targets[1]); yield return null; native.Applied();
                Assert.That(native.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1), "原同点二击，不增加第三次点击");
                Assert.That(native.State.FindPlayer(1).CityLocationId, Is.EqualTo(targets[1])); Assert.That(Markers(native.State), Is.EqualTo(2));
                Assert.That(Read(move, "IsSubmissionInFlight"), Is.EqualTo(false));
                Evidence("统一支付-移城改点撤销与二击接续", native, new { IsolatedSeam = true, first.PaymentId, NewPayment = current.PaymentId, Target = targets[1] });
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileUncommittedPreparationCancelsBeforeSwitchAndStaleReceiptDoesNothing()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            var prepared = Production(false); State(prepared).FindPlayer(1).Resources = new ResourceSet { GoldVoucher = 100, Originium = 100, OriginiumShard = 100, Iron = 100 };
            Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
            using (native)
            {
                var turn = Read(native.Mobile, "turnActionPresenter"); var move = Read(turn, "MoveInteraction"); var build = Read(turn, "BuildInteraction");
                native.ObserveActionPayment(move); native.ObserveActionPayment(build);
                var before = Balances(native); Call(native.Mobile, "BeginMoveAction"); yield return null;
                Evidence("统一支付-切换前移城准备首态", native, new { IsolatedSeam = true,
                    native.State.GameId, native.State.Round, Player = native.State.FindPlayer(1),
                    Prepared = Visible(native).PreparedActionPayments,
                    CanBegin = Read(native.Mobile, "CanBeginGameplayAction"), MoveActive = Read(move, "IsActive"),
                    DraftRevision = Read(move, "draftRevision"), DraftCity = Read(move, "draftCityLocation") });
                var old = Visible(native).PreparedActionPayments.Single(); Assert.That(Read(native.Mobile, "CanBeginGameplayAction"), Is.EqualTo(true));
                Call(native.Mobile, "OnBuildActionClicked"); yield return null;
                Assert.That(Visible(native).PreparedActionPayments, Is.Empty); Assert.That(Read(move, "IsActive"), Is.EqualTo(false));
                Call(native.Mobile, "SelectBuildCardInPanel", BuildCandidate(native.State)); yield return null;
                var buildPayment = Visible(native).PreparedActionPayments.Single(); Assert.That(buildPayment.ActionKind, Is.EqualTo("build"));
                Call(native.Mobile, "BeginDeployAction"); yield return null;
                Assert.That(Visible(native).PreparedActionPayments, Is.Empty); Assert.That(Read(build, "IsActive"), Is.EqualTo(false));
                Assert.That(Balances(native), Is.EqualTo(before));
                var count = native.Commands.Count;
                var oldCommand = native.Commands.First(command => command.Kind == GameCommandKind.PreparePaymentDraft);
                Call(move, "NotifyInteractionCommandResolved", native.State.GameId, oldCommand.CommandId, true, CommandErrorCode.None);
                Assert.That(native.Commands.Count, Is.EqualTo(count)); Assert.That(Visible(native).PreparedActionPayments, Is.Empty);
                Assert.That(native.Commands.Any(command => command.Kind == GameCommandKind.BuildFacility || command.Kind == GameCommandKind.MoveCity), Is.False);
                Evidence("统一支付-未提交准备切换与旧回执", native, new { IsolatedSeam = true, old.PaymentId, BuildPayment = buildPayment.PaymentId });
            }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileStonehooveLevelTwoRemovesOwnMarkerOnMapBeforeUpgrade()
        {
            PrepareRuntime(); yield return new EnterPlayMode();
            foreach (bool execute in new[] { true, false })
            {
                var prepared = Production(); var state = State(prepared); PrepareEnterpriseBoards(state);
                var influence = (InfluenceService)Read(prepared, "Influence");
                var companyPool = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.reward_pool" && zone.LocalZoneId == "company_markers");
                foreach (var marker in state.ItemZones.Items.Where(item => item.DefinitionId == "infected.component.company_marker").ToArray())
                    Position(state, marker, companyPool.ZoneId);
                var registry = (EffectRegistry)Read(prepared, "Registry");
                // 隔离前置：公司只能进入已开矿、无城市且无影响力的资源点；路线上的己方标记不能代替这个条件。
                registry.Register(new EffectRegistration("fixture.stonehoove.native.resource_points", context =>
                {
                    foreach (string location in new[] { "A-02", "B-03" })
                    {
                        if (!context.State.Map.OpenLocationIds.Contains(location)) context.State.Map.OpenLocationIds.Add(location);
                        var parent = context.State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == location);
                        var zone = context.State.ItemZones.Zones.Single(value => value.ParentId == parent.ZoneId && value.TemplateId == "core.zone.resource_point_markers");
                        Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, location,
                            "core.resource_point", "core.resource_point_token", zone.ZoneId, new ItemSubjectRef(), out string id, out string reason), Is.True, reason);
                        context.State.ItemZones.Items.Single(item => item.ItemId == id).LocalState = ItemTransferEffectExecutor.Object(
                            "resourceType", NormalizedValue.CreateInteger((int)ResourceType.Iron), "amount", NormalizedValue.CreateInteger(1));
                    }
                    return EffectStepResult.Completed();
                }));
                Run(prepared, EffectSpec.Create("fixture.stonehoove.native.resource_points", playerId: 1));
                Assert.That(new CompanyInfluenceRules(new MapQueryService(StaticMapDefinitions.Resolve(state.MapId))).GetPlacementSlotIds(state).Count, Is.GreaterThanOrEqualTo(2));
                for (int count = 0; count < 2; count++)
                    Run(prepared, InfluenceEffectSpecFactory.PlaceInfluence(1, influence.GetLegalPlacementSlotIds(state, 1).First(id => id.StartsWith("route:"))));
                Run(prepared, InfluenceEffectSpecFactory.PlaceInfluence(2, influence.GetLegalPlacementSlotIds(state, 2).First(id => id.StartsWith("route:"))));
                var board = state.ItemZones.Items.Single(item => item.DefinitionId == "infected.enterprise.stonehoove_security");
                var cooperation = state.ItemZones.Zones.Single(zone => zone.ParentId == board.ItemId && zone.LocalZoneId == "cooperation_1");
                state.ItemZones.Items.Single(item => item.ZoneId == cooperation.ZoneId).SlotId = "level_2";
                Run(prepared, EffectSpec.Create(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial,
                    ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                        "unlockedByPlayer", NormalizedValue.CreateString("1"), "enterpriseItemIds", NormalizedValue.CreateArray(new[] { NormalizedValue.CreateString(board.ItemId) }),
                        "specialId", NormalizedValue.CreateString("infected.enterprise.stonehoove_security.level_2.special")), 1));
                Native native = null; yield return Native.CreatePlayScene(prepared, value => native = value);
                using (native)
                {
                    var driverType = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
                    var host = new GameObject("岩蹄二级实际地图隔离接缝"); host.SetActive(false);
                    var driver = host.AddComponent(driverType);
                    Write(driver, "mobile", native.Mobile); Write(driver, "session", native.Session); Write(driver, "hud", native.Hud);
                    var output = Environment.GetEnvironmentVariable("NMC_GAMEPLAY_UI_EVIDENCE"); Directory.CreateDirectory(output);
                    Write(driver, "output", output); Write(driver, "selectedCase", "isolated-stonehoove-map");
                    Write(driver, "stage", execute ? "岩蹄二级执行移除后升级" : "岩蹄二级放弃移除不升级");
                    IEnumerator Drive(string method, params object[] args)
                    {
                        bool done = false;
                        IEnumerator Routine() { yield return (IEnumerator)Call(driver, method, args); done = true; }
                        ((MonoBehaviour)native.Mobile).StartCoroutine(Routine());
                        for (int frame = 0; frame < 2000 && !done; frame++) yield return null;
                        Assert.That(done, Is.True, method);
                    }
                    var buttonType = Type.GetType("YC.Presentation.MapButtonView, Assembly-CSharp", true);
                    Component[] Interactive() => UnityEngine.Object.FindObjectsOfType(buttonType).Cast<Component>().Where(button =>
                        (WorkflowHighlightTargetKind)Read(button, "TargetKind") == WorkflowHighlightTargetKind.InfluenceSlot && (bool)Read(button, "IsInteractive")).ToArray();
                    yield return new WaitForSecondsRealtime(0.7f);
                    Evidence("岩蹄二级-公司放置初始实际请求", native, new { IsolatedSeam = true, NormalGameAcceptance = false,
                        Request = UiRequest(native), InteractiveSlots = Interactive().Select(button => (string)Read(button, "TargetId")).ToArray(), State = native.State });
                    yield return CaptureUi("岩蹄二级-公司放置初始页面", native);
                    for (int count = 0; count < 2; count++)
                    {
                        if (UiRequest(native)?.InteractionTypeId == "action.decline_effect" &&
                            UiRequest(native).Presentation?.Prompt?.Key == "core:interaction.effect.optional")
                            yield return Drive("Press", (Button)Call(driver, "ChoiceExecuteButton"), "岩蹄公司放置前原可选执行按钮");
                        Assert.That(Interactive(), Is.Not.Empty, "岩蹄正式公司放置请求未形成实际地图高亮；保留初始请求，不直接提交答案");
                        var target = Interactive().OrderBy(button => (string)Read(button, "TargetId")).First();
                        string slot = (string)Read(target, "TargetId"); int before = native.Commands.Count;
                        string originalRequest = UiRequest(native).InteractionId;
                        yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, slot, "岩蹄公司影响力原地图首击");
                        if (native.Commands.Count == before && UiRequest(native)?.InteractionId == originalRequest)
                            yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, slot, "岩蹄公司影响力原地图确认");
                        native.Applied(); Assert.That(native.Commands.Count, Is.EqualTo(before + 1));
                    }
                    ItemInstanceState[] Markers() => native.State.ItemZones.Items.Where(item => new ItemZoneQuery().IsInfluence(native.State, item.ItemId)).ToArray();
                    var companyIds = Markers().Where(item => item.OwnerSubject.Kind == ItemSubjectKind.Company).Select(item => item.ItemId).OrderBy(id => id).ToArray();
                    var ownIds = Markers().Where(item => item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == 1).Select(item => item.ItemId).OrderBy(id => id).ToArray();
                    var otherIds = Markers().Where(item => item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == 2).Select(item => item.ItemId).OrderBy(id => id).ToArray();
                    Evidence("岩蹄二级-两次放置准确回执与权威位置", native, new { IsolatedSeam = true, NormalGameAcceptance = false,
                        CompanyIds = companyIds, OwnIds = ownIds, OtherIds = otherIds, State = native.State });
                    Assert.That(companyIds, Has.Length.EqualTo(2));
                    Assert.That(UiRequest(native).InteractionTypeId, Is.EqualTo("action.decline_effect"));
                    var paymentHost = Call(driver, "FindConditionPaymentView");
                    var conditionButton = paymentHost != null ? (Button)Read(paymentHost, execute ? "ConditionPaymentExecuteButton" : "ConditionPaymentDeclineButton") :
                        (Button)Read(ActiveComponents(native, "UiEffectRowView").Single(row => (string)Read(row, "ItemId") == (execute ? "continue" : "decline")), "StatusButton");
                    yield return Drive("Press", conditionButton, "岩蹄原条件按钮");
                    native.Applied();
                    if (execute)
                    {
                        var request = UiRequest(native);
                        Assert.That(request.PromptKey, Is.EqualTo("effect.influence.remove.choose_target"));
                        Assert.That(request.AnswerSchema, Is.EqualTo("candidate_id_array"));
                        Assert.That(request.CandidateIds.All(ownIds.Contains), Is.True);
                        Assert.That(request.CandidateIds.Intersect(companyIds.Concat(otherIds)), Is.Empty);
                        Assert.That(ActiveComponents(native, "EffectDialogOptionRowView"), Is.Empty, "移除必须沿地图，不生成通用候选列表");
                        string id = request.CandidateIds.First();
                        string slot = Markers().Single(item => item.ItemId == id).SlotId;
                        int before = native.Commands.Count; string beforeState = JsonConvert.SerializeObject(native.State);
                        yield return CaptureUi("岩蹄二级-实际己方地图移除", native);
                        yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, slot, "岩蹄己方移除原首击");
                        Assert.That(native.Commands.Count, Is.EqualTo(before)); Assert.That(JsonConvert.SerializeObject(native.State), Is.EqualTo(beforeState));
                        yield return Drive("MapPress", WorkflowHighlightTargetKind.InfluenceSlot, slot, "岩蹄己方移除原二击确认");
                        native.Applied(); Assert.That(native.Commands.Count, Is.EqualTo(before + 1));
                        Assert.That(native.Commands.Last().OptionIds, Is.EqualTo(new[] { id }));
                        Assert.That(native.Commands.Last().Parameters["interactionId"], Is.EqualTo(request.InteractionId));
                        Assert.That(native.Commands.Last().Parameters["expectedRevision"], Is.EqualTo(request.StateRevision.ToString()));
                        Assert.That(Markers().Any(item => item.ItemId == id), Is.False);
                        Assert.That(Markers().Count(item => item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == 1), Is.EqualTo(ownIds.Length - 1));
                    }
                    Assert.That(native.State.ItemZones.Items.Single(item => item.ZoneId == cooperation.ZoneId).SlotId, Is.EqualTo(execute ? "level_3" : "level_2"));
                    Assert.That(Markers().Where(item => item.OwnerSubject.Kind == ItemSubjectKind.Company).Select(item => item.ItemId).OrderBy(id => id), Is.EqualTo(companyIds));
                    Assert.That(Markers().Where(item => item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == 2).Select(item => item.ItemId).OrderBy(id => id), Is.EqualTo(otherIds));
                    if (!execute) Assert.That(Markers().Count(item => item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == 1), Is.EqualTo(ownIds.Length));
                    Assert.That((string)Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                    Evidence("岩蹄二级-" + (execute ? "执行原ItemId移除后升级" : "放弃保留己方影响力与公司放置"), native,
                        new { IsolatedSeam = true, NormalGameAcceptance = false, Execute = execute, CompanyIds = companyIds, OwnIds = ownIds, OtherIds = otherIds });
                    yield return CaptureUi("岩蹄二级-" + (execute ? "移除后升级" : "放弃不升级"), native);
                    UnityEngine.Object.Destroy(host);
                }
            }
        }

        [Test]
        public void ExplorationPlanPrefersNearbyPointWithinWholeRouteBudget()
        {
            // 仅隔离检验测试驱动的路线决策；不计正常页面或正常对局验收。
            var driver = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
            var method = driver.GetMethod("NearestAffordableExploration", BindingFlags.NonPublic | BindingFlags.Static);
            var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var state = new GameState { MapId = map.Map.MapId };
            var facts = new RouteTollService(map).ReadRouteFacts(state, 3);
            var original = method.Invoke(null, new object[] { "B-03", facts,
                new[] { "C-03", "D-02", "D-03", "E-01", "G-02", "G-03" }, 4 });
            Assert.That(Read(original, "Target"), Is.EqualTo("D-03"));
            Assert.That(Read(original, "GoldCost"), Is.EqualTo(2));
            Assert.That(Read(original, "Hops"), Is.EqualTo(1));
            Assert.That((IEnumerable<string>)Read(original, "Routes"), Is.EqualTo(new[] { "R7" }));
            Assert.That(method.Invoke(null, new object[] { "B-03", facts, new[] { "C-03" }, 2 }), Is.Null,
                "两段收费航道需要4金券，不能只按每段2金券判断付得起");

            state.Map.RoadRouteIds.AddRange(new[] { "A2", "A1", "R1", "G1" });
            facts = new RouteTollService(map).ReadRouteFacts(state, 3);
            var nearby = method.Invoke(null, new object[] { "B-03", facts, new[] { "D-03", "G-03" }, 2 });
            Assert.That(Read(nearby, "Target"), Is.EqualTo("D-03"), "有预算时先选一跳近点，而非四跳免费远点");
            var noGold = method.Invoke(null, new object[] { "B-03", facts, new[] { "D-03", "G-03" }, 0 });
            Assert.That(Read(noGold, "Target"), Is.EqualTo("G-03"), "近点不可付时仍保留真正免费且可达的远点");
            Assert.That(Read(noGold, "GoldCost"), Is.EqualTo(0));
        }
        private static Button ActiveButton(Native native, string name) => ((Component)native.Mobile).gameObject.scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<Button>(true)).Single(b => b.gameObject.activeInHierarchy && b.name == name);
    }
}
