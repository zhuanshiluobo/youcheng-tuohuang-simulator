#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Economy;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Persistence;
using YC.PlayerJourney;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    // 只有显式参数才挂载。从正常主菜单开始；全程不替换状态、身份或调用刷新。
    public sealed class HotseatUiValidationDriver : MonoBehaviour
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private string output, selectedCase, stage = "启动";
        private bool finished;
        private float started;
        private int captureNumber, eventNumber;
        private MobileCityInteractionController mobile;
        private GameSession session;
        private EffectRegistry registry;
        private GameplayInteractionHudView hud;
        private string amiyaId, nextCharacterId;
        private readonly List<object> commands = new List<object>();
        private readonly List<object> screenshots = new List<object>();
        private readonly List<object> checks = new List<object>();
        private readonly List<object> errors = new List<object>();
        private readonly Dictionary<string, StreamWriter> appendLogs = new Dictionary<string, StreamWriter>();
        private readonly List<int> harvestOrder = new List<int>();
        private readonly HashSet<CommandGateway> observedGateways = new HashSet<CommandGateway>();
        private string lastVisiblePaymentSummary;
        private readonly HashSet<string> paymentSourceAttempts = new HashSet<string>();
        private PaymentSourceContinuation paymentSourceContinuation;
        private PreparedMainActionContinuation preparedMainAction;
        private PreparedMainActionContinuation pendingMoveCompletion;
        private readonly HashSet<string> attemptedBuildPreparations = new HashSet<string>();
        private const string PlayPolicyVersion = "事件资源与样式布局-近点预算-林原控件加载-20261016";
        private sealed class PlayerPlayProgress
        {
            public int PlayerId, Moves, Explorations, Builds, Deployments, SpecialActions;
            public string Entrance = "";
            public bool RedZoneReached;
            public readonly HashSet<string> VisitedCities = new HashSet<string>();
        }
        private readonly Dictionary<int, PlayerPlayProgress> playerPlay = new Dictionary<int, PlayerPlayProgress>();
        private readonly HashSet<string> recordedPlayCommands = new HashSet<string>();
        private readonly Dictionary<int, string> playerStyleTargets = new Dictionary<int, string>();
        private readonly Dictionary<int, int[]> playerStyleLayouts = new Dictionary<int, int[]>();
        private ExplorationPlayPlan explorationPlayPlan;
        private sealed class ExplorationPlayPlan
        {
            public string Target;
            public int GoldCost, Hops;
            public List<string> Routes = new List<string>();
        }
        private sealed class StylePlacementPlan
        {
            public string StyleId;
            public int Slot, Score, Matched, Required, Rotation;
            public int[] PatternSlots;
        }
        private StylePlacementPlan lastStylePlacement;
        private readonly Dictionary<string, StylePlacementPlan> stylePlacementCache = new Dictionary<string, StylePlacementPlan>();
        private int stylePlacementRevision = -1;
        private sealed class PreparedMainActionContinuation
        {
            public string GameId, Kind, TargetId, StartCity;
            public int PlayerId, Round, ActionRound;
            public bool TargetPreselected, FinalControlClicked;
        }
        private sealed class DriverContinuationPendingException : InvalidOperationException
        { public DriverContinuationPendingException(string message) : base(message) { } }
        private sealed class PaymentCannotContinueException : InvalidOperationException
        { public PaymentCannotContinueException(string message) : base(message) { } }
        private sealed class PaymentSourceContinuation
        {
            public string PaymentId, OwnerEffectId, SourceKind, SourceItemId, OptionId, AttemptKey;
            public int Generation;
            public ResourceSet RemainingCost, ResourcesBefore;
        }
        private GameState State => session.State;
        private int LocalPlayer => (int)Read(mobile, "localPlayerId");
        private RouteResourceSelectionPresenter Route => (RouteResourceSelectionPresenter)Read(Read(mobile, "routeResourceSelection"), "Presenter");
        private bool SalesMode => selectedCase == "resource-sales";
        private bool CampaignMode => selectedCase == "full-game";
        private bool SaveResumeMode => selectedCase == "save-resume" || selectedCase == "save-resume-continue";
        private bool CoverageMode => SalesMode || CampaignMode;
        private string Argument(string prefix) => Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartRequested()
        {
            string Argument(string prefix) => Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);
            var path = Argument("--yc-hotseat-ui-validation-output=");
            if (string.IsNullOrEmpty(path)) return;
            if (!Path.IsPathRooted(path) || !Directory.Exists(path))
            { Debug.LogError("UI验证输出必须是已经建立的绝对目录。"); return; }
            if (FindObjectOfType<HotseatUiValidationDriver>() != null) return;
            var host = new GameObject("热座真实UI验证驱动");
            DontDestroyOnLoad(host);
            var driver = host.AddComponent<HotseatUiValidationDriver>();
            driver.output = path;
            driver.selectedCase = Argument("--yc-hotseat-ui-validation-case=") ?? "";
            driver.started = Time.realtimeSinceStartup;
            UnityEngine.Application.runInBackground = true;
            UnityEngine.Application.logMessageReceived += driver.OnLog;
            driver.StartCoroutine(driver.Guarded(driver.Run()));
        }

        private void OnLog(string condition, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                errors.Add(new { Stage = stage, Type = type.ToString(), Message = condition, Trace = trace });
        }
        private IEnumerator Guarded(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(routine);
            Exception failure = null;
            while (stack.Count > 0 && failure == null)
            {
                object current = null; bool moved = false;
                try
                {
                    moved = stack.Peek().MoveNext();
                    if (moved) current = stack.Peek().Current;
                }
                catch (Exception error) { failure = error; }
                if (failure != null) break;
                if (!moved) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                if (current is IEnumerator nested) stack.Push(nested);
                else yield return current;
            }
            if (failure != null)
            {
                Write("首失败.json", new { Stage = stage, Error = failure.ToString(), State = ObserveSafely() });
                yield return new WaitForEndOfFrame();
                try { SaveCapture("首失败"); } catch (Exception captureError) { Write("首失败截图错误.json", captureError.ToString()); }
            }
            Finish(failure);
        }

        private readonly HashSet<string> usedAmiya = new HashSet<string>();
        private readonly Dictionary<int, string> activatedAmiyaByPlayer = new Dictionary<int, string>();
        private bool amiyaCovered;
        private int actionChecks;
        private InteractionRequestProjection Request => (InteractionRequestProjection)Read(Read(mobile, "interactionRequestRouter"), "CurrentRequest");
        private IEnumerator Run()
        {
            Require(selectedCase == "normal-flow" || CoverageMode || SaveResumeMode, "验证入口必须是正常菜单流程");
            Write("运行身份.json", new { Case = selectedCase, ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                UnityVersion = UnityEngine.Application.unityVersion, Assembly = typeof(HotseatUiValidationDriver).Assembly.ManifestModule.ModuleVersionId,
                DataPath = UnityEngine.Application.dataPath, PersistentDataPath = UnityEngine.Application.persistentDataPath,
                TemporaryCachePath = UnityEngine.Application.temporaryCachePath,
                Arguments = Environment.GetCommandLineArgs(), ExternalMouseInput = false,
                NormalMenuFlow = true, StateReplacementCount = 0, RandomSeedOverride = false });
            stage = "正常启动菜单";
            yield return Settle();
            var menu = FindObjectOfType<StartMenuView>();
            Require(menu != null, "正常启动没有显示开始菜单");
            yield return Capture("正常开始菜单");
            yield return new WaitForSecondsRealtime(3);
            if (selectedCase == "save-resume-continue")
            {
                yield return SaveResumeLoadExisting(menu);
                yield return SaveResumeRun();
                yield break;
            }
            yield return Press(menu.StartGameButton, "主菜单创建本地房间");
            var room = menu.RoomPanel;
            for (int wait = 0; wait < 30 && !room.gameObject.activeInHierarchy; wait++) yield return Settle();
            Require(room.gameObject.activeInHierarchy, "创建房间后没有正常进入房间面板");
            stage = "房间正常配置企业感染者与四人热座";
            var expansions = (StartMenuRoomPanelView.ContentOption[])Read(room, "expansions");
            foreach (var id in new[] { "enterprise", "infected" })
            {
                if (SaveResumeMode) continue;
                if (SalesMode || CampaignMode && !(Argument("--yc-campaign-expansions=") ?? "enterprise,infected").Split(',').Contains(id)) continue;
                var option = expansions.Single(v => v.Id == id);
                if (!option.Selection.gameObject.activeSelf) yield return Press(option.Button, "房间启用扩展" + id);
            }
            var seats = (StartMenuRoomPanelView.SeatBinding[])Read(room, "seats");
            for (int i = 1; i < 4; i++) yield return Press(seats[i].HotSeatButton, "正常添加本地玩家" + (i + 1));
            yield return Capture("四人房间与正常扩展配置");
            yield return Press((Button)Read(room, "startButton"), "房间开始游戏");
            for (int wait = 0; wait < 90 && FindObjectOfType<MobileCityInteractionController>() == null; wait++) yield return Settle();
            mobile = FindObjectOfType<MobileCityInteractionController>();
            Require(mobile != null && mobile.enabled, "正常房间开局没有进入游戏场景");
            // 游戏控制器先于加载黑幕淡出就绪；正式切换完成前不得向地图输入。
            var transitionWaitStarted = Time.realtimeSinceStartup;
            while (SceneTransitionContext.IsTransitionInProgress &&
                   Time.realtimeSinceStartup - transitionWaitStarted < 60f) yield return null;
            Require(!SceneTransitionContext.IsTransitionInProgress, "正式场景切换尚未完成，不能在加载遮罩下输入");
            yield return Settle();
            Write("正式场景切换完成.json", new { Frame = Time.frameCount,
                Scene = SceneManager.GetActiveScene().name, SceneTransitionContext.IsTransitionInProgress,
                WaitedSeconds = Time.realtimeSinceStartup - transitionWaitStarted });
            session = (GameSession)Read(mobile, "session");
            registry = (EffectRegistry)Read(mobile, "sessionEffectRegistry");
            hud = (GameplayInteractionHudView)Read(mobile, "gameplayInteractionHud");
            Require(session != null && registry != null && hud != null, "正常开局场景装配引用缺失");
            Require(State.Players.Count == 4, "正常开局人数不是四人");
            ObserveGateway(mobile);
            ObserveGateway(Read(mobile, "turnActionPresenter"));
            ObserveGateway(Read(mobile, "influenceActionPresenter"));
            if (SalesMode) LoadCoverage();
            if (CampaignMode) LoadCampaign();
            yield return Capture("正常开局首个请求");
            if (SaveResumeMode) { yield return SaveResumeRun(); yield break; }
            for (int step = 0; CampaignMode || step < (SalesMode ? 900 : 150); step++)
            {
                if (CampaignMode)
                {
                    ObserveCampaignCompletion();
                    Require(State.EffectRuntime.Status != EffectRuntimeStatus.PausedFault,
                        "正常效果链进入故障：" + State.EffectRuntime.LastFaultCode + ":" + State.EffectRuntime.LastFaultMessage);
                    if (State.Phase == GamePhase.GameOver ||
                        State.Phase == GamePhase.FinalScoring && State.FinalScoring?.IsResolved == true) break;
                }
                else if (SalesMode)
                {
                    UpdateCoverage();
                    if (TargetPagesCovered || State.Phase == GamePhase.FinalScoring || State.Phase == GamePhase.GameOver) break;
                }
                else if (harvestOrder.Count == 4 && (amiyaCovered || State.Round >= 3)) break;
                stage = "正常流程步骤" + (step + 1) + "-回合" + State.Round + "-玩家" + LocalPlayer + "-" + (Request?.InteractionTypeId ?? State.Phase.ToString());
                Append("正常流程轨迹.jsonl", Observe());
                if (CampaignMode) ObservePlayerPlayPositions();
                RecordVisiblePaymentSummary();
                ObserveMoveCompletion();
                if (CampaignMode && Request != null) yield return CaptureSelectionPageFacts();
                if (paymentSourceContinuation != null)
                {
                    if (Request?.InteractionTypeId != ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId)
                        throw new DriverContinuationPendingException("异客Chip已受理，但实际分配页面尚未进入当前可见路由；保留驱动待接现场");
                    yield return PaymentSubstitutionAllocation();
                    continue;
                }
                var paymentSource = FindVisiblePaymentSource(out var paymentContinuation);
                if (paymentSource != null)
                {
                    paymentSourceAttempts.Add(paymentContinuation.AttemptKey);
                    paymentSourceContinuation = paymentContinuation;
                    int before = commands.Count;
                    if (campaignCaptureKeys.Add("payment-source:" + paymentContinuation.SourceKind))
                    {
                        yield return new WaitForEndOfFrame();
                        SaveCapture("正常付款宿主与合法异客来源");
                    }
                    yield return PointerSequence(paymentSource.gameObject, "当前同笔付款优先选择合法可见异客Chip");
                    Require(commands.Count == before + 1, "异客实际Chip未恰好提交一次来源命令");
                    var command = Read(commands[before], "Command") as GameCommand;
                    var result = (WorkflowSubmissionResult)Read(commands[before], "Result");
                    Require(command?.Kind == GameCommandKind.InvokeSettlementOption &&
                        command.Parameters.TryGetValue("optionId", out string optionId) && optionId == paymentContinuation.OptionId &&
                        result?.CommandResult?.Succeeded == true && result.AppliedLocally,
                        "异客实际Chip没有匹配本次来源及准确Applied回执");
                    continue;
                }
                var conditionPayment = FindConditionPaymentView();
                if (conditionPayment != null)
                {
                    yield return ConditionPaymentChoice(conditionPayment);
                    continue;
                }
                if (preparedMainAction != null && (Request == null || ActionPaymentPreparation.IsPreparedActionRequest(Request)))
                {
                    yield return ContinuePreparedMainAction();
                    continue;
                }
                if (Route?.IsActive == true)
                {
                    if (State.Phase == GamePhase.ResourceCollection) yield return HarvestOne(harvestOrder.Count < 4);
                    else yield return GrantedRouteSelection();
                    continue;
                }
                if (State.Phase == GamePhase.Cleanup && Request == null)
                {
                    var requiredChip = hud.GetComponentsInChildren<SettlementEffectChipView>().FirstOrDefault(c =>
                        (bool)Read(c, "executionAllowed") && !(bool)Read(c, "pending") &&
                        (Read(c, "option") as SettlementOptionView)?.EntryKind == "cleanup_order");
                    if (requiredChip != null)
                    {
                        yield return PointerSequence(requiredChip.gameObject, "正常收尾实际结算来源，不能直接跳过");
                        continue;
                    }
                    int revision = State.EffectRuntime.StateRevision;
                    yield return Press(hud.Frame.EndActionButton, "正常收尾首次结束");
                    Require(State.EffectRuntime.StateRevision > revision, "正常收尾首次结束未推进");
                    continue;
                }
                if (CoverageMode && Request?.PromptKey != null &&
                    (Request.PromptKey == "character.cannot.strategy.choose_sale" || Request.PromptKey == "effect.resource.sell" || Request.PromptKey == "facility.entry.sell_resources"))
                { if (CampaignMode) yield return CampaignSale(); else yield return SalePage(); continue; }
                if (CoverageMode && Request?.InteractionTypeId == FacilityBuildEffectExecutor.PlacementInteractionType)
                { yield return BuildPlacement(); continue; }
                if (CoverageMode && FindObjectsOfType<EffectDialogResourceRowView>().Length > 0)
                { yield return RequiredAllocation(); continue; }
                if (Request != null || State.Phase == GamePhase.CharacterCover || State.Phase == GamePhase.Entrance)
                {
                    yield return NormalChoice();
                    continue;
                }
                Require(State.Phase == GamePhase.ActionRound1 || State.Phase == GamePhase.ActionRound2,
                    "尚未适配正常阶段的真实控件：" + State.Phase);
                if (CampaignMode) yield return CampaignAction();
                else if (SalesMode) yield return SalesAction(); else yield return NormalAction();
            }
            if (CampaignMode)
            {
                Require(State.Round == State.MaxRounds && State.MaxRounds == 8 && State.FinalScoring?.IsResolved == true &&
                    State.FinalScoring.PlayerScores.Count == 4, "尚未形成八回合正式终局及四人计分，不能算完整局");
                stage = "八回合正式终局";
                yield return Capture("八回合正式终局");
                Write("正式终局.json", new { State.GameId, State.ContentPackHash, State.Round, State.MaxRounds, State.Phase,
                    State.FinalScoring, Revision = State.EffectRuntime.StateRevision, State.EffectRuntime.Status,
                    State.EffectRuntime.LastFaultCode, State.EffectRuntime.LastFaultMessage, Commands = commands });
                campaignGame.Terminal = true;
                PersistCampaign();
                yield break;
            }
            if (SalesMode)
            {
                UpdateCoverage();
                Require(TargetPagesCovered || State.Phase == GamePhase.FinalScoring || State.Phase == GamePhase.GameOver,
                    "正常对局尚未达到目标覆盖或终局，保留推进断点");
                yield return Capture("正常出售页面流程结束");
                yield break;
            }
            Require(harvestOrder.Count == 4 && actionChecks > 0, "正常流程未完成四位连续首次采集及部署结束");
            checks.Add(new { Item = "阿米娅正常获取及底栏", Covered = amiyaCovered,
                Reason = amiyaCovered ? "通过正常候选获得并使用" : "截至正常第三回合，未自然形成阿米娅来源与后继角色组合；未注入牌或状态" });
            stage = "正常流程本轮结束";
            yield return Capture("正常流程结束现场");
        }

        // 与整局共用原按钮、地图命中与准确回执；此模式只增加存读档路径，不写规则状态。
        private IEnumerator SaveResumeRun()
        {
            if (State.Phase != GamePhase.Entrance)
            {
                Require(State.Phase == GamePhase.ActionRound1 || State.Phase == GamePhase.ActionRound2,
                    "原存档不是入场或主要行动阶段，不猜测续接路径");
                yield return SaveResumeCompleteMove();
                yield break;
            }
            var pending = State.EffectRuntime.InteractionRequests.SingleOrDefault(value => value.Status == "open" && value.AnsweringPlayerId == LocalPlayer);
            Require(pending?.InteractionTypeId == "entrance.location", "本次验收预期的原入场待选已改变，不操作其他存档");
            var pendingId = pending.InteractionId;
            yield return SaveResumeRoundTrip("入场待选");
            int before = commands.Count;
            // 读取后路由器可延迟建立；以实际可见地图控件完成原选择，不用路由器快照代替操作结果。
            var target = PreferVisibleMapTarget(FindObjectsOfType<MapButtonView>().Where(button =>
                button.TargetKind == WorkflowHighlightTargetKind.Location && button.IsInteractive));
            Require(target != null, "读取后原入场没有实际可交互地图候选");
            string targetId = target.TargetId;
            yield return MapPress(WorkflowHighlightTargetKind.Location, targetId, "读取后原入场地图首击");
            Require(commands.Count == before && target.IsSelected, "入场首击应仅预选原可见候选");
            yield return MapPress(WorkflowHighlightTargetKind.Location, targetId, "读取后原入场地图同点二击确认");
            Require(commands.Skip(before).Count(record => (Read(record, "Command") as GameCommand)?.Kind == GameCommandKind.AnswerInteraction &&
                (Read(record, "Command") as GameCommand).Parameters.TryGetValue("interactionId", out var id) && id == pendingId) == 1,
                "读取后原待选没有恰好作答一次");
            checks.Add(new { Item = "读取后原待选仅作答一次", InteractionId = pendingId, Passed = true });
            for (int step = 0; step < 100 && !(State.Phase == GamePhase.ActionRound1 && Request == null && Route?.IsActive != true); step++)
                yield return SaveResumeAdvance();
            Require(State.Phase == GamePhase.ActionRound1 && Request == null && Route?.IsActive != true,
                "正常入场未结束，尚不能验收稳定行动点");
            yield return SaveResumeRoundTrip("稳定行动点");
            yield return SaveResumeCompleteMove();
        }

        private IEnumerator SaveResumeCompleteMove()
        {
            var savedPayments = new HashSet<string>();
            var triedMoves = new HashSet<string>();
            var triedExplorations = new HashSet<string>();
            for (int step = 0; step < 100; step++)
            {
                ObserveMoveCompletion();
                var moves = commands.Select(record => Read(record, "Command") as GameCommand)
                    .Where(command => command?.Kind == GameCommandKind.MoveCity).ToArray();
                Require(moves.Length <= 1, "续局发出多条最终MoveCity；保留现场，不过滤额外命令");
                if (moves.Length == 1 && Request == null && Route?.IsActive != true &&
                    preparedMainAction == null && pendingMoveCompletion == null && mobile.CanEndCurrentAction())
                {
                    int actor = LocalPlayer;
                    Require(AcceptedFinalCommands(0, GameCommandKind.MoveCity, actor).Length == 1,
                        "唯一最终MoveCity没有成功Applied回执");
                    yield return SaveResumeRoundTrip("移城完成后");
                    int before = commands.Count;
                    yield return Press(hud.Frame.EndActionButton, "续局移城后首次结束行动");
                    Require(commands.Count == before + 1 && LocalPlayer != actor,
                        "续局结束行动没有一次交接给下一玩家");
                    yield return Capture("存读档后合法继续与交接");
                    Write("存读档未覆盖.json", new { Items = new[] {
                        "物理Esc及同帧传播未覆盖，仅原关闭控件和设置处理器守卫",
                        "写盘失败未自然发生", "联机续局房间及席位需正常多实例另测" } });
                    yield break;
                }
                var visible = CurrentPaymentView();
                Require(visible != null, "续局本人可见投影尚未就绪，不猜余额或准备身份");
                var preparation = visible.PreparedActionPayments.SingleOrDefault(value => value.ActionKind == "city_move");
                string actionWindow = State.Round + ":" + State.ActionRound + ":" + LocalPlayer;
                if (preparation != null)
                {
                    triedMoves.Add(actionWindow);
                    if (preparedMainAction == null)
                    {
                        var mapTarget = FindObjectsOfType<MapButtonView>().SingleOrDefault(value =>
                            value.TargetKind == WorkflowHighlightTargetKind.Location && value.TargetId == preparation.TargetId && value.IsSelected);
                        Require(string.IsNullOrEmpty(preparation.TargetId) || mapTarget != null,
                            "恢复的付款目标未在原地图选中，不重做预选掩盖草稿丢失");
                        preparedMainAction = new PreparedMainActionContinuation { GameId = State.GameId, Kind = "city_move",
                            PlayerId = LocalPlayer, Round = State.Round, ActionRound = State.ActionRound,
                            StartCity = visible.Players.Single(player => player.PlayerId == LocalPlayer).CityLocationId,
                            TargetId = preparation.TargetId, TargetPreselected = mapTarget != null };
                        Append("决策变化.jsonl", new { State.GameId, Action = "原存档付款准备接续", preparation.PaymentId,
                            preparation.PaymentGeneration, preparation.TargetId, NewPrepareSubmitted = false });
                    }
                    if (savedPayments.Add(preparation.PaymentId))
                    {
                        yield return SaveResumeRoundTrip("玩家" + LocalPlayer + "-移城付款准备");
                        continue;
                    }
                    var balance = visible.Players.Single(player => player.PlayerId == LocalPlayer).Resources;
                    var cost = preparation.RemainingCost;
                    bool payable = balance.Originium >= cost.Originium && balance.OriginiumShard >= cost.OriginiumShard &&
                        balance.Iron >= cost.Iron && balance.PureOriginium >= cost.PureOriginium && balance.GoldVoucher >= cost.GoldVoucher;
                    if (!payable)
                    {
                        Append("决策变化.jsonl", new { State.GameId, PlayerId = LocalPlayer, Action = "原支付方案不足，正常推进同局",
                            preparation.PaymentId, preparation.PaymentGeneration, Balance = balance, Cost = cost,
                            Reason = "先原控件取消本笔准备，再优先最近且可付探索；不强行确认、不注入资源、不重开" });
                        Require(preparation.CanCancel, "不足额准备当前不可取消，保留必须处理的实际响应");
                        yield return CancelPreparedMainAction(hud.ActionPanelView.MoveCityButton, preparation, "续局原方案不足，原移动按钮正常取消");
                        continue;
                    }
                }
                if (Request != null || Route?.IsActive == true || preparedMainAction != null)
                { yield return SaveResumeAdvance(); continue; }
                Require(State.Phase == GamePhase.ActionRound1 || State.Phase == GamePhase.ActionRound2,
                    "付款短链路尚未适配新的阶段，不自动跳过");
                if (mobile.CanEndCurrentAction())
                { yield return Press(hud.Frame.EndActionButton, "不足额玩家正常主要行动完成后交接"); continue; }
                yield return Press(MarkedButton("action.tab.0"), "续局正常主要行动页签");
                if (hud.ActionPanelView.MoveCityButton.IsInteractable() && triedMoves.Add(actionWindow))
                {
                    yield return BeginCampaignMove();
                    Require(CurrentPaymentView()?.PreparedActionPayments.Any(value => value.ActionKind == "city_move") == true,
                        "原移城入口未保留付款准备，不重复点击或忽略自动取消");
                    continue;
                }
                if (hud.ActionPanelView.ExploreButton.IsInteractable() && triedExplorations.Add(actionWindow))
                {
                    yield return Press(hud.ActionPanelView.ExploreButton, "缺少移城资源，正常探索最近可支付资源点");
                    Require(Route?.IsActive == true, "原探索按钮没有进入实际路线选择");
                    continue;
                }
                yield return NormalAction();
            }
            throw new DriverContinuationPendingException("同局合法推进尚未完成一次移城验收，保留真实进度，不重开");
        }

        private IEnumerator SaveResumeAdvance()
        {
            stage = "存读档正常推进-" + State.Phase + "-" + (Request?.InteractionTypeId ?? "无请求");
            Append("正常流程轨迹.jsonl", Observe());
            ObserveMoveCompletion();
            var condition = FindConditionPaymentView();
            if (condition != null) { yield return ConditionPaymentChoice(condition); yield break; }
            if (preparedMainAction != null && (Request == null || ActionPaymentPreparation.IsPreparedActionRequest(Request)))
            { yield return ContinuePreparedMainAction(); yield break; }
            if (Route?.IsActive == true)
            {
                if (State.Phase == GamePhase.ResourceCollection) yield return HarvestOne(harvestOrder.Count < 4);
                else yield return GrantedRouteSelection();
                yield break;
            }
            Require(Request != null || State.Phase == GamePhase.CharacterCover || State.Phase == GamePhase.Entrance,
                "本轮短链路正常推进遇到尚未适配的阶段：" + State.Phase);
            yield return NormalChoice();
        }

        private IEnumerator SaveResumeAwait(Func<bool> ready, string description)
        {
            float start = Time.realtimeSinceStartup;
            while (!ready() && Time.realtimeSinceStartup - start < 75f) yield return null;
            Require(ready(), description + "超时；不直接调用保存/恢复API代替控件");
            yield return Settle();
        }

        private MatchSaveWindowView SaveResumeWindow => FindObjectOfType<MatchSaveWindowView>();
        private List<MatchSaveSlot> SaveResumeSlots => Read(SaveResumeWindow, "slots") as List<MatchSaveSlot>;
        private int saveResumeManualSlot = -1;
        private IEnumerator SaveResumeLoadExisting(StartMenuView menu)
        {
            stage = "从正常主页读取原失败测量的同一档";
            string expectedGameId = Argument("--yc-save-resume-game-id=");
            Require(!string.IsNullOrEmpty(expectedGameId), "必须声明本次本人已保存的GameId，防止操作其他存档");
            yield return Press(menu.LoadGameButton, "正常主页读取之前同一存档");
            yield return SaveResumeAwait(() => SaveResumeWindow != null && !(bool)Read(SaveResumeWindow, "busy") && SaveResumeSlots != null,
                "原存档读取列表");
            saveResumeManualSlot = SaveResumeSlots.FindIndex(slot => slot.Index < MatchSaveStore.ManualCount && slot.Data?.GameId == expectedGameId && slot.Error == null);
            Require(saveResumeManualSlot >= 0, "原本人手动档不在正式列表中，不改文件或选其他局");
            yield return Capture("主页原同局存档");
            yield return Press(((Button[])Read(SaveResumeWindow, "rows"))[saveResumeManualSlot], "原列表选择本人同局档");
            yield return Press(((GenericWindowView)Read(SaveResumeWindow, "window")).ConfirmButton, "原读取按钮进入同一对局");
            yield return SaveResumeAwait(() => FindObjectOfType<MobileCityInteractionController>() != null && !SceneTransitionContext.IsTransitionInProgress,
                "原同局正常场景读取");
            mobile = FindObjectOfType<MobileCityInteractionController>(); session = (GameSession)Read(mobile, "session");
            registry = (EffectRegistry)Read(mobile, "sessionEffectRegistry"); hud = (GameplayInteractionHudView)Read(mobile, "gameplayInteractionHud");
            Require(session != null && State.GameId == expectedGameId, "正常读取不是原本人对局");
            ObserveGateway(mobile); ObserveGateway(Read(mobile, "turnActionPresenter")); ObserveGateway(Read(mobile, "influenceActionPresenter"));
            yield return Capture("同局原档恢复后未操作");
        }
        private IEnumerator SaveResumeRoundTrip(string label)
        {
            stage = "存读档-" + label;
            var expected = JToken.FromObject(State);
            var requestBefore = Request == null ? null : JToken.FromObject(Request);
            string gameId = State.GameId;
            int countBefore = commands.Count;
            var paymentBefore = CurrentPaymentView()?.PreparedActionPayments.Select(value => new {
                value.PaymentId, value.PaymentGeneration, value.PreparationOwnerEffectId, value.OperationWindowId,
                value.WindowGeneration, value.ActionKind, value.TargetId, value.RemainingCost }).ToArray();
            var expectedPayments = paymentBefore == null ? null : JToken.FromObject(paymentBefore);
            var settings = FindObjectOfType<GameSettingsMenuView>();
            Require(settings != null, "实际游戏设置引用缺失");
            yield return Press(hud.Frame.SettingsButton, label + "点击当前HUD顶栏原设置按钮");
            yield return Press(settings.SaveGameButton, label + "打开原手动存档入口");
            yield return SaveResumeAwait(() => SaveResumeWindow != null && !(bool)Read(SaveResumeWindow, "busy") && SaveResumeSlots != null,
                "手动存档列表加载");
            var saveView = SaveResumeWindow;
            var rows = (Button[])Read(saveView, "rows");
            var window = (GenericWindowView)Read(saveView, "window");
            Require(rows.Length == MatchSaveStore.SlotCount && rows.Count(row => row.gameObject.activeInHierarchy) == MatchSaveStore.ManualCount,
                "手动存档模式未显示三个手动槽");
            if (saveResumeManualSlot < 0)
            {
                saveResumeManualSlot = SaveResumeSlots.FindIndex(slot => slot.Index < MatchSaveStore.ManualCount && !slot.Exists);
                Require(saveResumeManualSlot >= 0, "手动槽已满；不覆盖用户已有手动档");
                Require(!window.ConfirmButton.IsInteractable(), "未选择空槽前保存确认应禁用");
            }
            else Require(SaveResumeSlots[saveResumeManualSlot].Data?.GameId == gameId &&
                rows.Take(MatchSaveStore.ManualCount).Where((row, index) => index != saveResumeManualSlot).All(row => !row.IsInteractable()),
                "本局已有手动档时没有限定更新原槽");
            yield return Capture(label + "-手动存档三槽");
            // 只调用设置自身的Esc路由守卫，明确不计作物理键盘Esc测试。
            FindObjectOfType<GameSettingsMenuController>().HandleEscapePressed();
            Require(SaveResumeWindow == saveView && settings.OverlayObject.activeInHierarchy,
                "存档窗打开时设置的Esc处理器穿透了模态层");
            yield return Press(window.CancelButton != null ? window.CancelButton : (Button)Read(window, "closeButton"), label + "原关闭控件返回设置");
            Require(!MatchSaveWindowView.IsAnyOpen && settings.OverlayObject.activeInHierarchy && JToken.DeepEquals(expected, JToken.FromObject(State)),
                "原关闭控件误关设置或改变规则状态");
            yield return Press(settings.SaveGameButton, label + "再次经原按钮打开保存");
            yield return SaveResumeAwait(() => SaveResumeWindow != null && !(bool)Read(SaveResumeWindow, "busy") && SaveResumeSlots != null,
                "重新加载手动列表");
            rows = (Button[])Read(SaveResumeWindow, "rows");
            window = (GenericWindowView)Read(SaveResumeWindow, "window");
            yield return Press(rows[saveResumeManualSlot], label + "选择空槽或本局原槽");
            yield return Press(window.ConfirmButton, label + "原按钮确认手动保存");
            yield return SaveResumeAwait(() => !MatchSaveWindowView.IsAnyOpen, "保存成功并关闭");
            Require(string.IsNullOrEmpty(MatchSaveController.Instance.LastFailure), "手动保存失败：" + MatchSaveController.Instance.LastFailure);
            Require(JToken.DeepEquals(expected, JToken.FromObject(State)), "手动保存改变了规则状态");
            yield return Capture(label + "-保存成功回设置");
            yield return Press(settings.ReturnButton, label + "正常返回主页入口");
            yield return Press(settings.ConfirmReturnButton, label + "正常确认返回主页");
            mobile = null; session = null; registry = null; hud = null;
            yield return SaveResumeAwait(() => FindObjectOfType<StartMenuView>() != null && !SceneTransitionContext.IsTransitionInProgress,
                "返回正常主菜单");
            var menu = FindObjectOfType<StartMenuView>();
            yield return Press(menu.LoadGameButton, label + "主页读取存档原按钮");
            yield return SaveResumeAwait(() => SaveResumeWindow != null && !(bool)Read(SaveResumeWindow, "busy") && SaveResumeSlots != null,
                "读取列表加载");
            rows = (Button[])Read(SaveResumeWindow, "rows");
            window = (GenericWindowView)Read(SaveResumeWindow, "window");
            Require(rows.Length == MatchSaveStore.SlotCount && rows.All(row => row.gameObject.activeInHierarchy), "读取模式没有显示全部六槽");
            Require(!window.ConfirmButton.IsInteractable(), "读取未选槽时确认未禁用");
            var saved = SaveResumeSlots[saveResumeManualSlot].Data;
            Require(saved != null && saved.GameId == gameId && SaveResumeSlots[saveResumeManualSlot].Error == null,
                "本局手动槽摘要或校验失败");
            Write(label + "-读取原槽摘要.json", new { saved.GameId, saved.MapId, saved.Round, saved.ContentHash, saved.Mode,
                saved.LocalPlayerId, saved.SavedUtcTicks, saved.Seats, ManualSlot = saveResumeManualSlot,
                Slots = SaveResumeSlots.Select(slot => new { slot.Index, slot.Exists, slot.Error, GameId = slot.Data?.GameId }).ToArray() });
            File.WriteAllText(Path.Combine(output, label + "-保存点权威状态.json"), saved.ReadArchiveState().ToString());
            yield return Capture(label + "-主页读取六槽");
            yield return Press(rows[saveResumeManualSlot], label + "选择本次手动档");
            yield return Press(window.ConfirmButton, label + "原按钮确认读取");
            yield return SaveResumeAwait(() => FindObjectOfType<MobileCityInteractionController>() != null && !SceneTransitionContext.IsTransitionInProgress,
                "正常读取进入原游戏场景");
            mobile = FindObjectOfType<MobileCityInteractionController>();
            session = (GameSession)Read(mobile, "session"); registry = (EffectRegistry)Read(mobile, "sessionEffectRegistry");
            hud = (GameplayInteractionHudView)Read(mobile, "gameplayInteractionHud");
            Require(session != null && registry != null && hud != null, "读取后的原游戏引用缺失");
            ObserveGateway(mobile); ObserveGateway(Read(mobile, "turnActionPresenter")); ObserveGateway(Read(mobile, "influenceActionPresenter"));
            var actual = JToken.FromObject(State);
            var requestAfter = Request == null ? null : JToken.FromObject(Request);
            bool equal = JToken.DeepEquals(expected, actual);
            var paymentAfter = CurrentPaymentView()?.PreparedActionPayments.Select(value => new {
                value.PaymentId, value.PaymentGeneration, value.PreparationOwnerEffectId, value.OperationWindowId,
                value.WindowGeneration, value.ActionKind, value.TargetId, value.RemainingCost }).ToArray();
            var actualPayments = paymentAfter == null ? null : JToken.FromObject(paymentAfter);
            Write(label + "-恢复比较.json", new { State.GameId, State.ContentPackHash, FullStateEqual = equal,
                PendingRequestEqual = JToken.DeepEquals(requestBefore, requestAfter), CommandCountUnchanged = commands.Count == countBefore,
                PaymentIdentityEqual = JToken.DeepEquals(expectedPayments, actualPayments), ExpectedPayments = expectedPayments, ActualPayments = actualPayments,
                DifferentTopLevelFields = ((JObject)expected).Properties().Where(p => !JToken.DeepEquals(p.Value, actual[p.Name])).Select(p => p.Name).ToArray(),
                ExpectedRequest = requestBefore, ActualRequest = requestAfter, Actual = Observe() });
            yield return Capture(label + "-读取恢复现场");
            Require(equal && commands.Count == countBefore && JToken.DeepEquals(expectedPayments, actualPayments),
                "存读档往返后权威状态、命令数或本人付款身份不一致");
            checks.Add(new { Item = label + "原控件存读档闭环", Passed = true, State.GameId,
                State.ContentPackHash, Revision = State.EffectRuntime.StateRevision, ManualSlot = saveResumeManualSlot,
                FullStateEqual = true, AuthorityPendingPreserved = true,
                RouterSnapshotEqual = JToken.DeepEquals(requestBefore, requestAfter),
                PendingUiCompletionClaimed = false, CommandsAdded = commands.Count - countBefore });
        }

        private GameStateView CurrentPaymentView()
        {
            var visible = Read(hud?.Frame, "gameplaySnapshot") as GameStateView;
            return visible != null && visible.ViewerRole == GameStateViewerRole.Player && visible.ViewerPlayerId == LocalPlayer &&
                visible.GameId == State.GameId && visible.ContentPackHash == State.ContentPackHash &&
                visible.StateRevision == State.EffectRuntime.StateRevision ? visible : null;
        }

        private static ResourceSet VisiblePaymentCost(NormalizedValue cost)
        {
            if (cost?.Kind != NormalizedValueKind.Object) return null;
            var result = new ResourceSet();
            foreach (ResourceType type in new[] { ResourceType.Originium, ResourceType.OriginiumShard, ResourceType.Iron,
                ResourceType.PureOriginium, ResourceType.GoldVoucher })
            {
                string name = type.ToString(); name = char.ToLowerInvariant(name[0]) + name.Substring(1);
                var value = cost.Properties.Find(entry => entry.Name == name)?.Value;
                if (value?.Kind != NormalizedValueKind.Integer || value.IntegerValue < 0 || value.IntegerValue > int.MaxValue) return null;
                result.Set(type, (int)value.IntegerValue);
            }
            return result;
        }

        private SettlementEffectChipView FindVisiblePaymentSource(out PaymentSourceContinuation continuation)
        {
            continuation = null;
            var visible = CurrentPaymentView();
            if (visible == null || string.IsNullOrEmpty(visible.ActiveSettlementWindowId)) return null;
            var request = Request;
            string paymentId = PaymentField(request, "paymentId")?.StringValue;
            var sources = visible.CharacterSettlementOptions.Where(option => option.VisibleToViewer && option.IsEnabled &&
                option.AnsweringPlayerId == LocalPlayer && option.StateRevision == visible.StateRevision &&
                option.WindowId == visible.ActiveSettlementWindowId && option.SourceDefinitionId == "operator.passenger" &&
                option.LocalId == "operator.passenger.tactic.optional_insertion").ToArray();
            foreach (var source in sources)
            {
                var preparation = visible.PreparedActionPayments.FirstOrDefault(value => value.PaymentOwnerEffectId == source.OwnerEffectId);
                string currentPayment = string.IsNullOrEmpty(paymentId) ? preparation?.PaymentId : paymentId;
                if (string.IsNullOrEmpty(currentPayment) || currentPayment != source.OwnerEffectId) continue;
                var generation = PaymentField(request, "paymentGeneration");
                int currentGeneration = generation?.Kind == NormalizedValueKind.Integer ? (int)generation.IntegerValue : preparation?.PaymentGeneration ?? 0;
                var canPay = PaymentField(request, "canPay");
                if (CampaignMode && CampaignCount("character:operator.passenger:tactic") > 0 &&
                    (canPay?.BooleanValue == true || preparation?.CanConfirm == true)) continue;
                if (currentGeneration < 1)
                    throw new DriverContinuationPendingException("合法异客来源已可见，但同笔付款代次尚未进入本人投影；不能猜身份或提前放弃");
                string attemptKey = currentPayment + ":" + currentGeneration + ":" + source.SourceItemId;
                if (paymentSourceAttempts.Contains(attemptKey)) continue;
                var chip = hud.GetComponentsInChildren<SettlementEffectChipView>().FirstOrDefault(value =>
                    value.gameObject.activeInHierarchy && (Read(value, "option") as SettlementOptionView)?.OptionId == source.OptionId &&
                    (bool)Read(value, "executionAllowed") && !(bool)Read(value, "pending"));
                if (chip == null)
                    throw new DriverContinuationPendingException("本人同笔合法异客来源存在，但实际可执行Chip绑定尚未接齐；不把它误报为原价不足");
                Require(chip.ValidateReferences(out var reason), reason);
                var group = Read(chip, "visualGroup") as CanvasGroup;
                if (!chip.isActiveAndEnabled || group == null || group.alpha < .99f)
                    throw new DriverContinuationPendingException("当前同笔异客Chip尚未实际显示可交互；不绕过隐藏来源调用提交");
                var remaining = VisiblePaymentCost(PaymentField(request, "remainingCost")) ?? preparation?.RemainingCost?.Clone();
                var resources = visible.Players.FirstOrDefault(player => player.PlayerId == LocalPlayer)?.Resources;
                if (remaining == null || resources == null)
                    throw new DriverContinuationPendingException("异客分配所需本人余款或余额投影缺失；不读取全局付款树补造");
                continuation = new PaymentSourceContinuation { PaymentId = currentPayment, OwnerEffectId = source.OwnerEffectId,
                    Generation = currentGeneration, SourceKind = PaymentField(request, "sourceKind")?.StringValue ?? preparation?.ActionKind,
                    SourceItemId = source.SourceItemId, OptionId = source.OptionId, AttemptKey = attemptKey,
                    RemainingCost = remaining, ResourcesBefore = resources.Clone() };
                Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                    Action = "当前同笔付款优先异客来源", continuation.PaymentId, continuation.Generation,
                    source.OptionId, source.SourceItemId, source.WindowId, source.OwnerEffectId,
                    Reason = "本人合法可见来源匹配当前PaymentId；先经实际Chip及分配控件，再回原宿主" });
                return chip;
            }
            return null;
        }

        private IEnumerator PaymentSubstitutionAllocation()
        {
            var continuation = paymentSourceContinuation;
            var request = Request;
            if (PaymentField(request, "paymentId")?.StringValue != continuation.PaymentId)
                throw new DriverContinuationPendingException("异客分配页面未绑定刚受理的同笔PaymentId；不重试来源或代答");
            var ids = PaymentField(request, "rowIds"); var maximum = PaymentField(request, "maxAmount");
            var limits = PaymentField(request, "rowMaximums");
            if (ids?.Kind != NormalizedValueKind.Array || maximum?.Kind != NormalizedValueKind.Integer ||
                limits?.Kind != NormalizedValueKind.Object || maximum.IntegerValue < 0 || maximum.IntegerValue > 2)
                throw new DriverContinuationPendingException("实际异客分配页面的授权行/预算接线未齐；不复用普通数量零确认");
            var rowIds = ids.Items.Select(value => value.StringValue).ToArray();
            var rows = FindObjectsOfType<EffectDialogResourceRowView>().Where(row => row.name.StartsWith("Resource Allocation Row ", StringComparison.Ordinal))
                .OrderBy(row => int.Parse(row.name.Substring("Resource Allocation Row ".Length))).ToArray();
            if (rows.Length != rowIds.Length)
                throw new DriverContinuationPendingException("异客分配授权行与实际控件未对应；等待实际renderer接续");
            var types = rowIds.Select(id => Enum.TryParse(id, out ResourceType type) &&
                ResourcePaymentSubstitutionEffectExecutor.BasicTypes.Contains(type) ? type :
                throw new DriverContinuationPendingException("异客分配出现未适配的授权资源行：" + id)).ToArray();
            var amounts = new int[rowIds.Length]; int budget = (int)maximum.IntegerValue;
            foreach (int index in Enumerable.Range(0, rowIds.Length).OrderByDescending(index =>
                continuation.RemainingCost.Get(types[index]) - continuation.ResourcesBefore.Get(types[index])))
            {
                var limit = limits.Properties.Find(entry => entry.Name == rowIds[index])?.Value;
                if (limit?.Kind != NormalizedValueKind.Integer || limit.IntegerValue < 0 || limit.IntegerValue > 2)
                    throw new DriverContinuationPendingException("异客实际授权行缺少上限");
                amounts[index] = Math.Min(budget, Math.Min((int)limit.IntegerValue,
                    Math.Max(0, continuation.RemainingCost.Get(types[index]) - continuation.ResourcesBefore.Get(types[index]))));
                budget -= amounts[index];
            }
            if (amounts.Sum() == 0 && budget > 0)
                for (int index = 0; index < amounts.Length; index++)
                    if (limits.Properties.Find(entry => entry.Name == rowIds[index])?.Value.IntegerValue > 0 && rows[index].IncreaseButton.IsInteractable())
                    { amounts[index] = 1; break; }
            int before = commands.Count;
            if (campaignCaptureKeys.Add("payment-substitution-allocation"))
            {
                yield return new WaitForEndOfFrame();
                SaveCapture("正常异客授权分配页面");
            }
            foreach (int index in Enumerable.Range(0, amounts.Length))
                for (int amount = 0; amount < amounts[index]; amount++)
                    yield return Press(rows[index].IncreaseButton, "正常异客分配" + rowIds[index]);
            Require(commands.Count == before, "异客分配编辑不应提前提交或付款");
            var confirm = amounts.Sum() == 0 ? SaleFinish() : SaleConfirm();
            if (confirm == null || !confirm.IsInteractable())
                throw new DriverContinuationPendingException("异客实际分配确认/取消控件尚未可消费；不直接保存计划");
            yield return Press(confirm, amounts.Sum() == 0 ? "异客无正量授权，正常取消分配并保留缺口" : "正常异客分配保存计划，等待原宿主确认");
            Require(commands.Count == before + 1, "异客分配原按钮未恰好提交一次命令");
            var command = Read(commands[before], "Command") as GameCommand;
            var result = (WorkflowSubmissionResult)Read(commands[before], "Result");
            Require(command?.Kind == GameCommandKind.AnswerInteraction &&
                command.Parameters.TryGetValue("interactionId", out string interactionId) && interactionId == request.InteractionId &&
                command.Parameters.TryGetValue("expectedRevision", out string revision) &&
                revision == request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                result?.CommandResult?.Succeeded == true && result.AppliedLocally, "异客分配没有匹配原请求的准确Applied回执");
            var visible = CurrentPaymentView();
            var resources = visible?.Players.FirstOrDefault(player => player.PlayerId == LocalPlayer)?.Resources;
            if (resources == null)
                throw new DriverContinuationPendingException("异客分配准确受理后本人宿主投影尚未恢复；不把缺投影当作已扣款");
            Require(resources.Originium == continuation.ResourcesBefore.Originium &&
                resources.OriginiumShard == continuation.ResourcesBefore.OriginiumShard && resources.Iron == continuation.ResourcesBefore.Iron &&
                resources.PureOriginium == continuation.ResourcesBefore.PureOriginium && resources.GoldVoucher == continuation.ResourcesBefore.GoldVoucher,
                "异客分配保存后余额变化，尚未由原宿主确认付款");
            var preparation = visible.PreparedActionPayments.FirstOrDefault(value => value.PaymentId == continuation.PaymentId &&
                value.PaymentOwnerEffectId == continuation.OwnerEffectId && value.PaymentGeneration == continuation.Generation);
            var returnedGeneration = PaymentField(Request, "paymentGeneration");
            var commitRequested = PaymentField(Request, "isCommitRequested");
            bool samePayment = PaymentField(Request, "paymentId")?.StringValue == continuation.PaymentId;
            Require(!samePayment || commitRequested?.Kind != NormalizedValueKind.Boolean || !commitRequested.BooleanValue,
                "异客分配确认已经产生最终扣款意图，尚未从原宿主确认付款");
            bool returned = samePayment && returnedGeneration?.Kind == NormalizedValueKind.Integer &&
                returnedGeneration.IntegerValue == continuation.Generation && commitRequested?.Kind == NormalizedValueKind.Boolean &&
                !commitRequested.BooleanValue;
            if (!returned && preparation == null)
                throw new DriverContinuationPendingException("分配已保存，但同笔原宿主返回身份尚未接齐；不自动付款或宣称生产故障");
            Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                Action = "异客分配返回同笔宿主", continuation.PaymentId, continuation.Generation,
                Rows = rowIds, Amounts = amounts, SavedOnly = true, BalanceUnchanged = true, FinalPaymentSubmitted = false });
            paymentSourceContinuation = null;
            RecordVisiblePaymentSummary();
            if ((continuation.SourceKind == "build" || continuation.SourceKind == "city_move") && preparedMainAction == null)
                throw new DriverContinuationPendingException("异客分配已回同笔主要行动准备，但缺少本次真实控件启动记录；不重新准备或自动付款");
        }

        private IEnumerator ContinuePreparedMainAction()
        {
            var action = preparedMainAction;
            if (State.GameId != action.GameId || LocalPlayer != action.PlayerId || State.Round != action.Round || State.ActionRound != action.ActionRound)
                throw new DriverContinuationPendingException("准备接续的本人操作窗口已改变；不替换身份或重签旧准备");
            var visible = CurrentPaymentView();
            var preparation = visible?.PreparedActionPayments.SingleOrDefault(value => value.ActionKind == action.Kind);
            if (preparation == null)
                throw new DriverContinuationPendingException("原控件准备后本人付款投影尚未接续；不再点击入口重造准备");
            var presenter = Read(mobile, "turnActionPresenter") as TurnActionPresenter;
            if (action.Kind == "build")
            {
                if (preparation.TargetId != action.TargetId)
                    throw new DriverContinuationPendingException("建设分配/响应返回后设施草稿身份未保留；不重新选牌或更换PaymentId");
                string mode = preparation.PaymentMode;
                if (mode != BuildFacilityService.PaymentModeResources && mode != BuildFacilityService.PaymentModeGold)
                    throw new DriverContinuationPendingException("权威建设PaymentMode未落入当前准备；不从费用数字猜模式");
                var button = MarkedButton("build.payment." + mode);
                if (preparation.CanConfirm)
                {
                    if (button == null || !button.IsActive() || !button.IsInteractable())
                        throw new DriverContinuationPendingException("同笔建设余款可确认，但原模式支付控件尚未恢复；不改模式来掩盖返回缺口");
                    var model = presenter?.BuildBuildFacilityDraftViewModel();
                    if (model?.SelectedOption?.FacilityId != action.TargetId || model.PaymentPreparation?.PaymentId != preparation.PaymentId ||
                        model.PaymentPreparation.PaymentGeneration != preparation.PaymentGeneration)
                        throw new DriverContinuationPendingException("建设原面板未恢复同设施、同笔与代次；不借支付按钮重建草稿");
                    yield return ConfirmPreparedBuild(button, mode, preparation);
                    yield break;
                }
                string alternate = mode == BuildFacilityService.PaymentModeResources ? BuildFacilityService.PaymentModeGold : BuildFacilityService.PaymentModeResources;
                var alternateButton = MarkedButton("build.payment." + alternate);
                if (alternateButton != null && alternateButton.IsActive() && alternateButton.IsInteractable())
                {
                    yield return ConfirmPreparedBuild(alternateButton, alternate, preparation);
                    yield break;
                }
                if (!preparation.CanCancel)
                    throw new DriverContinuationPendingException("建设准备还在必结响应/不可撤销等待中，但当前没有对应可见请求；不重复点击付款");
                var page = FindObjectsOfType<FacilityBuildDialogView>().SingleOrDefault(value => value.gameObject.activeInHierarchy);
                var cancel = Read(page, "cancel") as Button;
                if (cancel == null || !cancel.IsActive() || !cancel.IsInteractable())
                    throw new DriverContinuationPendingException("建设当前无可用支付/替代，原取消控件尚未恢复；不直接取消领域准备");
                yield return CancelPreparedMainAction(cancel, preparation, "建设余款不可付，正常取消本次准备继续其他合法行动");
                yield break;
            }
            if (presenter?.MoveInteraction.IsSelectingMoveTarget != true)
                throw new DriverContinuationPendingException("移城付款响应后原地图预选工作流未恢复；不重开移动入口");
            if (!action.TargetPreselected)
            {
                var candidates = FindObjectsOfType<MapButtonView>().Where(value =>
                    value.TargetKind == WorkflowHighlightTargetKind.Location && value.IsInteractive);
                var target = CampaignMode || SaveResumeMode ? ChooseCampaignMoveTarget(candidates) : PreferVisibleMapTarget(candidates);
                if (target == null)
                    throw new DriverContinuationPendingException("正常移城入口准备后没有恢复实际合法地图高亮；不直接查询目标后提交");
                action.TargetId = target.TargetId;
                yield return MapPress(WorkflowHighlightTargetKind.Location, action.TargetId, "主要移城原地图首击预选并准备目标费用");
                action.TargetPreselected = true;
                Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = action.PlayerId,
                    Action = "主要移城首击预选", action.TargetId, FinalMoveSubmitted = false });
                yield break;
            }
            if (preparation.TargetId != action.TargetId)
                throw new DriverContinuationPendingException("移城目标准备回执尚未对应原首击目标；不改目标或补第三次确认");
            if (preparation.CanConfirm)
            {
                var target = FindObjectsOfType<MapButtonView>().SingleOrDefault(value =>
                    value.TargetKind == WorkflowHighlightTargetKind.Location && value.TargetId == action.TargetId);
                if (target == null || !target.IsInteractive || !target.IsSelected)
                    throw new DriverContinuationPendingException("移城付款/分配返回后首击草稿或原高亮丢失；不重做首击掩盖第三次确认");
                int before = commands.Count;
                action.FinalControlClicked = true;
                yield return MapPress(WorkflowHighlightTargetKind.Location, action.TargetId, "主要移城原同点二次确认，接续同笔准备");
                var accepted = AcceptedFinalCommands(before, GameCommandKind.MoveCity, action.PlayerId);
                Require(commands.Skip(before).Count(record => (Read(record, "Command") as GameCommand)?.Kind == GameCommandKind.MoveCity) <= 1,
                    "原移城二击重复发出最终移动命令");
                if (accepted.Length != 1)
                    throw new DriverContinuationPendingException("主要移城原二击尚未接续唯一最终命令；不补点或直接MoveCity");
                Require(accepted[0].TargetId == YC.Domain.Movement.CityMoveCandidateQueryService.CandidatePrefix + action.TargetId &&
                    accepted[0].Parameters.TryGetValue("paymentPreparationOwnerEffectId", out string owner) && owner == preparation.PreparationOwnerEffectId,
                    "原移城最终命令没有携带原目标与同笔准备身份");
                pendingMoveCompletion = action;
                preparedMainAction = null;
                Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = action.PlayerId,
                    Action = "主要移城原二击已受理", action.TargetId, preparation.PaymentId, preparation.PaymentGeneration,
                    CommandId = accepted[0].CommandId, ExtraConfirmation = false });
                ObserveMoveCompletion();
                yield break;
            }
            if (!preparation.CanCancel)
                throw new DriverContinuationPendingException("移城目标费用仍未就绪且无当前可见响应；不把等待当原价不足或重复二击");
            var moveButton = hud.ActionPanelView.MoveCityButton;
            if (!moveButton.IsActive() || !moveButton.IsInteractable())
                throw new DriverContinuationPendingException("当前移城不可付，原移动按钮的正常撤销路径尚未可交互");
            yield return CancelPreparedMainAction(moveButton, preparation, "移城余款不可付，正常原入口撤销并继续本局");
        }

        private GameCommand[] AcceptedFinalCommands(int start, GameCommandKind kind, int player) => commands.Skip(start)
            .Where(record => (Read(record, "Command") as GameCommand)?.Kind == kind)
            .Select(record => new { Command = Read(record, "Command") as GameCommand, Result = Read(record, "Result") as WorkflowSubmissionResult })
            .Where(value => value.Command.PlayerId == player && value.Result?.CommandResult?.Succeeded == true && value.Result.AppliedLocally)
            .Select(value => value.Command).ToArray();

        private IEnumerator ConfirmPreparedBuild(Button button, string mode, PreparedActionPaymentView beforePreparation)
        {
            var action = preparedMainAction;
            if (action.FinalControlClicked)
                throw new DriverContinuationPendingException("原建设支付按钮已经点击过，本次不补点推进工作流");
            action.FinalControlClicked = true;
            int before = commands.Count;
            yield return Press(button, "正常建设原支付按钮一次点击" + mode);
            var accepted = AcceptedFinalCommands(before, GameCommandKind.BuildFacility, action.PlayerId);
            Require(commands.Skip(before).Count(record => (Read(record, "Command") as GameCommand)?.Kind == GameCommandKind.BuildFacility) <= 1,
                "原建设支付按钮单次点击重复发出最终建设命令");
            if (accepted.Length != 1)
                throw new DriverContinuationPendingException("原建设支付按钮单次点击的准备/换模式接续尚未形成唯一最终建设命令；不额外点一次付款");
            accepted[0].Parameters.TryGetValue("paymentPreparationOwnerEffectId", out string owner);
            Require(accepted[0].TargetId == action.TargetId &&
                accepted[0].Parameters.TryGetValue("paymentMode", out string paidMode) && paidMode == mode &&
                !string.IsNullOrEmpty(owner) &&
                (mode != beforePreparation.PaymentMode || owner == beforePreparation.PreparationOwnerEffectId),
                "建设原按钮最终命令没有匹配实际模式/设施/同笔准备");
            preparedMainAction = null;
            Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = action.PlayerId,
                Action = "建设原支付按钮已受理", action.TargetId, PaymentMode = mode,
                BeforePaymentId = beforePreparation.PaymentId, BeforeGeneration = beforePreparation.PaymentGeneration,
                PreparationOwnerEffectId = owner, CommandId = accepted[0].CommandId, OriginalButtonClicks = 1, BuiltClaimed = false });
            RecordVisiblePaymentSummary();
            Require(Request != null || State.Map.Facilities.Any(value => value.ContentInstanceId == action.TargetId),
                "建设支付受理后没有原放置请求或已放置实例");
        }

        private IEnumerator CancelPreparedMainAction(Button button, PreparedActionPaymentView preparation, string reason)
        {
            int before = commands.Count;
            yield return Press(button, reason);
            var accepted = AcceptedFinalCommands(before, GameCommandKind.PreparePaymentDraft, preparedMainAction.PlayerId)
                .Where(command => command.Parameters.TryGetValue("operation", out string operation) && operation == "cancel_action" &&
                    command.Parameters.TryGetValue("preparationOwnerEffectId", out string owner) && owner == preparation.PreparationOwnerEffectId).ToArray();
            if (accepted.Length != 1 || CurrentPaymentView()?.PreparedActionPayments.Any(value => value.PreparationOwnerEffectId == preparation.PreparationOwnerEffectId) != false)
                throw new DriverContinuationPendingException("原取消入口尚未准确撤销同笔准备；不切换行动或直接取消来掩盖");
            Append("未覆盖原因.jsonl", new { State.GameId, State.Round, PlayerId = preparedMainAction.PlayerId,
                Source = preparedMainAction.Kind, Target = preparedMainAction.TargetId, preparation.PaymentId, preparation.PaymentGeneration,
                Cancelled = true, Covered = false, Reason = reason });
            preparedMainAction = null;
            RecordVisiblePaymentSummary();
        }

        private void ObserveMoveCompletion()
        {
            var action = pendingMoveCompletion;
            if (action == null) return;
            var player = CurrentPaymentView()?.Players.FirstOrDefault(value => value.PlayerId == action.PlayerId);
            if (player?.CityLocationId != action.TargetId || !player.HasMovedCityThisRound) return;
            Covered("action:city-move");
            Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = action.PlayerId,
                Action = "正常主要移城实际位置已完成", From = action.StartCity, To = action.TargetId,
                ExtraConfirmation = false, FullGameStillPending = true });
            pendingMoveCompletion = null;
        }

        private EventChoiceDialogView FindConditionPaymentView()
        {
            var request = Request;
            if (request == null || (request.InteractionTypeId != "action.decline_effect" &&
                request.InteractionTypeId != "effect.resource.payment_confirmation")) return null;
            int mode = (int)EventChoiceDialogMode.ConditionPayment;
            return FindObjectsOfType<EventChoiceDialogView>().SingleOrDefault(view =>
            {
                var blocks = view.ModeBlocks;
                return blocks != null && mode < blocks.Length && blocks[mode] != null && blocks[mode].activeInHierarchy;
            });
        }

        private IEnumerator ConditionPaymentChoice(EventChoiceDialogView view)
        {
            var request = Request;
            Require(request != null && request.VisibleToViewer && request.Status == "open" &&
                request.AnsweringPlayerId == LocalPlayer, "条件/付款小窗没有绑定本人当前开放请求");
            var sourceKind = PaymentField(request, "sourceKind")?.StringValue;
            var preparationOwner = PaymentField(request, "preparationOwnerEffectId")?.StringValue;
            Require(string.IsNullOrEmpty(preparationOwner) || sourceKind != "build" && sourceKind != "city_move",
                "主要行动付款准备应返回原建设按钮或地图确认；不能用额外小窗代替原最终入口");
            var execute = view.ConditionPaymentExecuteButton;
            var decline = view.ConditionPaymentDeclineButton;
            Require(execute != null, "条件/付款小窗缺少实际执行按钮引用");
            if (request.InteractionTypeId == "effect.resource.payment_confirmation")
                Require(decline == null || !decline.IsActive(), "普通必付确认不应增加放弃按钮");
            bool canExecute = execute.IsActive() && execute.IsInteractable();
            bool canDecline = decline != null && decline.IsActive() && decline.IsInteractable();
            if (sourceKind == "condition")
            {
                if (PaymentField(request, "canPay")?.BooleanValue == false)
                    Require(!canExecute, "条件正式不可付时原执行按钮仍可用");
                if (request.CandidateIds.Contains("decline"))
                    Require(canDecline, "原条件请求允许放弃，但原放弃按钮不可用");
            }
            string paymentId = PaymentField(request, "paymentId")?.StringValue;
            var generation = PaymentField(request, "paymentGeneration");
            string attemptPrefix = paymentId + ":" + generation?.IntegerValue + ":";
            bool sourceAttempted = !string.IsNullOrEmpty(paymentId) && generation?.Kind == NormalizedValueKind.Integer &&
                paymentSourceAttempts.Any(key => key.StartsWith(attemptPrefix, StringComparison.Ordinal));
            if (!canExecute && PaymentField(request, "canSubstitute")?.BooleanValue == true && !sourceAttempted)
                throw new DriverContinuationPendingException("当前付款仍可替代，但同笔异客Chip/分配入口尚未接齐；先保驱动待接，不能提前放弃或按原价不足报生产故障");
            if (!canExecute && !canDecline)
            {
                if (PaymentField(request, "canPay")?.BooleanValue == true)
                    throw new InvalidOperationException("本人正式付款投影canPay为true，但实际原确认按钮不可用");
                throw new PaymentCannotContinueException("当前同笔费用没有可用确认、未尝试的合法替代来源或合法拒绝；保留真实付款不可继续状态供定位");
            }
            string decision = canExecute ? "执行" : "放弃";
            int beforeCommands = commands.Count, beforeRevision = State.EffectRuntime.StateRevision;
            bool recordCondition = CampaignMode && sourceKind == "condition";
            var beforeInfluenceIds = State.Map.Influences.Select(value => value.InfluenceId).ToArray();
            if (recordCondition)
            {
                yield return new WaitForEndOfFrame();
                SaveCapture("原条件窗口首次" + decision + "前");
                Write("原条件窗口-" + request.InteractionId + ".json", new { State.GameId, State.Round,
                    Request = request, Decision = decision, State = Observe(),
                    Execute = ButtonInfo(execute), Decline = decline == null ? null : ButtonInfo(decline),
                    BeforeInfluenceIds = beforeInfluenceIds,
                    Text = view.GetComponentsInChildren<Text>().Where(value => value.gameObject.activeInHierarchy)
                        .Select(value => new { value.name, value.text, Font = value.font?.name, value.fontSize,
                            Rect = UiRectDetails(value.rectTransform) }).ToArray(),
                    Images = view.GetComponentsInChildren<Image>().Where(value => value.gameObject.activeInHierarchy)
                        .Select(value => new { value.name, Sprite = value.sprite?.name,
                            Rect = UiRectDetails(value.rectTransform) }).ToArray(),
                    ForcedShow = false, ForcedRefresh = false, EndActionUsed = false });
            }
            Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                Action = "实际条件/付款小窗选择", Decision = decision, request.InteractionId,
                request.InteractionTypeId, request.StateRevision, Mode = EventChoiceDialogMode.ConditionPayment.ToString(),
                Reason = canExecute ? "已优先检查当前同笔异客来源，再从实际原宿主执行" : "当前来源机会已处理或正式无替代，执行不可用时使用实际允许的放弃按钮" });
            if (campaignCaptureKeys.Add("condition-payment:" + request.InteractionTypeId))
            {
                yield return new WaitForEndOfFrame();
                SaveCapture("正常条件与付款小窗");
            }
            yield return Press(canExecute ? execute : decline, "正常条件与付款小窗首次" + decision);
            Require(commands.Count == beforeCommands + 1 && State.EffectRuntime.StateRevision > beforeRevision,
                "条件/付款小窗首次选择未取得一次正式提交与revision推进，不能补点或直接代答");
            var command = Read(commands[beforeCommands], "Command") as GameCommand;
            var result = (WorkflowSubmissionResult)Read(commands[beforeCommands], "Result");
            Require(command != null && command.Kind == GameCommandKind.AnswerInteraction &&
                command.PlayerId == request.AnsweringPlayerId &&
                command.Parameters.TryGetValue("interactionId", out string interactionId) && interactionId == request.InteractionId &&
                command.Parameters.TryGetValue("expectedRevision", out string revision) &&
                revision == request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                result?.CommandResult?.Succeeded == true && result.AppliedLocally,
                "条件/付款小窗没有匹配原请求及准确Applied回执");
            if (recordCondition)
            {
                var afterInfluenceIds = State.Map.Influences.Select(value => value.InfluenceId).ToArray();
                bool priorInfluencesRemain = beforeInfluenceIds.All(id => afterInfluenceIds.Contains(id));
                Require(command.OptionIds.SequenceEqual(new[] { canExecute ? "continue" : "decline" }),
                    "原条件按钮没有提交对应的原continue/decline答案");
                if (decision == "放弃" && request.Presentation?.Prompt?.Key == "core:condition.event.event_yellow_04.payment")
                    Require(priorInfluencesRemain, "锈锤领地条件放弃后已放影响力丢失");
                Write("原条件窗口回执-" + request.InteractionId + ".json", new { State.GameId, State.Round,
                    Request = request, Decision = decision, command.CommandId, Command = command, Result = result,
                    BeforeRevision = beforeRevision, AfterRevision = State.EffectRuntime.StateRevision,
                    CommandsAdded = commands.Count - beforeCommands, BeforeInfluenceIds = beforeInfluenceIds,
                    AfterInfluenceIds = afterInfluenceIds, PriorInfluencesRemain = priorInfluencesRemain,
                    NextRequest = Request, After = Observe(), EndActionUsed = false });
                yield return Capture("原条件窗口" + decision + "后正常后继");
            }
            RecordVisiblePaymentSummary();
            checks.Add(new { Item = "条件/付款小窗实际按钮首次提交", Decision = decision,
                request.InteractionId, BeforeRevision = beforeRevision, AfterRevision = State.EffectRuntime.StateRevision,
                command.CommandId, CommandsAdded = commands.Count - beforeCommands, MatchedAppliedReceipt = true,
                EffectCompletionClaimed = false });
        }

        private static NormalizedValue PaymentField(InteractionRequestProjection request, string name) =>
            request?.PromptParameters?.Properties.Find(entry => entry.Name == name)?.Value;

        private static object PaymentScalar(NormalizedValue value)
        {
            if (value == null) return null;
            if (value.Kind == NormalizedValueKind.String) return value.StringValue;
            if (value.Kind == NormalizedValueKind.Integer) return value.IntegerValue;
            if (value.Kind == NormalizedValueKind.Boolean) return value.BooleanValue;
            return null;
        }

        private static string PaymentCostSummary(NormalizedValue value) => value?.Kind == NormalizedValueKind.Object
            ? string.Join(",", value.Properties.OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => entry.Name + "=" + Convert.ToString(PaymentScalar(entry.Value), System.Globalization.CultureInfo.InvariantCulture))) : null;

        private static object PaymentResourceSummary(ResourceSet value) => value == null ? null :
            new { value.Originium, value.OriginiumShard, value.Iron, value.PureOriginium, value.GoldVoucher };

        private void RecordVisiblePaymentSummary()
        {
            var request = Request;
            // 只读HUD已经绑定的本人投影，不为日志调用ProjectFor或扫描全局付款树。
            var visible = CurrentPaymentView();
            bool currentView = visible != null;
            var preparations = currentView ? visible.PreparedActionPayments.OrderBy(value => value.PreparationOwnerEffectId, StringComparer.Ordinal)
                .Select(value => (object)new { value.PreparationOwnerEffectId, value.PaymentOwnerEffectId, value.PaymentId,
                    value.PaymentGeneration, value.OperationWindowId, value.WindowGeneration, value.ActionKind, value.TargetId, value.PaymentMode,
                    value.IsReady, value.CanConfirm, value.CanCancel, value.InteractionId,
                    OriginalCost = PaymentResourceSummary(value.OriginalCost), SubstitutionCost = PaymentResourceSummary(value.SubstitutionCost),
                    RemainingCost = PaymentResourceSummary(value.RemainingCost) }).ToArray() : null;
            bool relevant = preparations != null && preparations.Length > 0 || request != null && (PaymentField(request, "paymentId") != null ||
                request.InteractionTypeId == "action.decline_effect" || request.InteractionTypeId == "effect.resource.payment_confirmation");
            if (!relevant)
            {
                if (lastVisiblePaymentSummary == null) return;
                lastVisiblePaymentSummary = null;
                Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                    Action = "费用身份与状态变化", VisiblePaymentRequest = false,
                    Note = "当前没有可见付款上下文或本人缓存尚未就绪；不据此宣称已付款或已取消" });
                return;
            }
            var summary = new SortedDictionary<string, object>(StringComparer.Ordinal)
            { ["interactionId"] = request?.InteractionId, ["interactionTypeId"] = request?.InteractionTypeId,
                ["preparedViewCurrent"] = currentView, ["preparedActionPayments"] = preparations };
            foreach (string field in new[] { "ownerEffectId", "paymentId", "paymentGeneration", "sourceKind",
                "preparationOwnerEffectId", "canPay", "canSubstitute", "isCommitRequested", "isCommitted" })
                summary[field] = PaymentScalar(PaymentField(request, field));
            foreach (string field in new[] { "originalCost", "substitutionCost", "remainingCost" })
                summary[field] = PaymentCostSummary(PaymentField(request, field));
            string signature = State.GameId + ":" + LocalPlayer + ":" + JsonConvert.SerializeObject(summary);
            if (signature == lastVisiblePaymentSummary) return;
            lastVisiblePaymentSummary = signature;
            Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                Action = "费用身份与状态变化", StateRevision = State.EffectRuntime.StateRevision, Summary = summary });
        }

        private readonly HashSet<string> pageUiKeys = new HashSet<string>();
        private readonly Dictionary<int, string> rowUiContents = new Dictionary<int, string>();
        private IEnumerator CaptureSelectionPageFacts()
        {
            var request = Request;
            if (request == null || !request.VisibleToViewer) yield break;
            var rows = FindObjectsOfType<UiEffectRowView>().Cast<Component>()
                .Concat(FindObjectsOfType<EffectDialogOptionRowView>()).Concat(FindObjectsOfType<EffectDialogResourceRowView>()).ToArray();
            var map = FindObjectsOfType<MapButtonView>().Where(value => value.IsInteractive).ToArray();
            if (rows.Length == 0 && map.Length == 0) yield break;
            bool reused = false;
            foreach (var row in rows)
            {
                string text = string.Join("|", row.GetComponentsInChildren<Text>().Select(value => value.text));
                if (rowUiContents.TryGetValue(row.GetInstanceID(), out string prior) && prior != text) reused = true;
                rowUiContents[row.GetInstanceID()] = text;
            }
            string kind = string.Join(",", rows.Select(row => row.GetType().Name).Distinct().OrderBy(value => value));
            string saleSource = request.PromptKey == "effect.resource.sell" ? ResolveSaleSource(request) : "";
            string key = request.InteractionTypeId + ":" + request.PromptKey + ":" + saleSource + ":" + kind + ":" +
                (rows.Any(row => row.GetComponentsInChildren<Text>().Any(text => text.text.Length >= 100)) ? "长文" : "短文");
            if (reused) key += ":池复用";
            if (!pageUiKeys.Add(key)) yield break;
            yield return new WaitForEndOfFrame();
            SaveCapture("正式页面实显-" + pageUiKeys.Count);
            Write("正式页面实显-" + pageUiKeys.Count + ".json", new {
                State.GameId, State.Round, Frame = Time.frameCount, Request = request, SaleSource = saleSource, PoolContentChanged = reused,
                Rows = rows.Take(16).Select(row => new {
                    Type = row.GetType().FullName, InstanceId = row.GetInstanceID(), Path = ComponentPath(row),
                    ItemId = (row as UiEffectRowView)?.ItemId, RowMode = Read(row, "mode")?.ToString(),
                    ExecutionButton = Read(row, "statusButton") is Button executionButton ? ButtonInfo(executionButton) : null,
                    Rect = UiRectDetails(row.transform as RectTransform),
                    PreferredHeight = row.GetComponent<LayoutElement>()?.preferredHeight,
                    TitleBaseSize = Read(row, "titleBaseSize"), DescriptionBaseSize = Read(row, "descriptionBaseSize"),
                    Text = row.GetComponentsInChildren<Text>().Where(text => text.gameObject.activeInHierarchy).Select(text => new {
                        text.name, text.text, Font = text.font?.name, text.fontSize, text.resizeTextForBestFit,
                        Rect = UiRectDetails(text.rectTransform) }).ToArray(),
                    Images = row.GetComponentsInChildren<Image>().Where(value => value.gameObject.activeInHierarchy).Take(24).Select(value => new {
                        value.name, Sprite = value.sprite?.name, Rect = UiRectDetails(value.rectTransform) }).ToArray()
                }).ToArray(),
                Map = map.Take(32).Select(value => new { value.TargetKind, value.TargetId, value.IsInteractive }).ToArray(),
                Boundary = "实际帧和原组件只读事实；不同用途不强设相同字号，不写资产、不强刷、不代替人工视觉" });
        }

        private IEnumerator NormalChoice()
        {
            var request = Request;
            bool temporaryChoice = CampaignMode && request?.PromptKey == "facility.temporary_settlement.retire";
            bool optionalExecution = CampaignMode && request?.InteractionTypeId == "effect.choice.execute" && request.MinSelections == 0;
            int choiceCommandsBefore = commands.Count, choiceRevisionBefore = State.EffectRuntime.StateRevision;
            if (temporaryChoice || optionalExecution)
            {
                yield return new WaitForEndOfFrame();
                SaveCapture(temporaryChoice ? "临时聚居点宣告后原执行条目" : "min0原可选执行条目");
                Write("原执行条目-" + request.InteractionId + ".json", new { State.GameId, State.Round,
                    Request = request, Frame = Time.frameCount, State = Observe(), ForcedShow = false, ForcedRefresh = false });
            }
            if (request?.InteractionTypeId == "character.ability.choice.move" &&
                FindObjectsOfType<EffectDialogOptionRowView>().Any(row => row.Button.IsActive() &&
                    row.Label?.text?.Contains("item_") == true))
                throw new DriverContinuationPendingException("角色影响力移动错误落入raw ID文本列表；等待正式地图选择路由，不点击该页继续验收");
            if (request?.InteractionTypeId == "character.ability.choice.move" && request.CandidateIds.All(id => id.StartsWith("move|", StringComparison.Ordinal)))
            { yield return NormalInfluenceMove(request); yield break; }
            var identity = (request?.InteractionId ?? "") + ":" + LocalPlayer + ":" + State.EffectRuntime.StateRevision;
            var enterprise = FindObjectsOfType<EnterpriseSelectionDialogView>().FirstOrDefault(v => v.Draft.Projection != null && !v.Draft.Projection.ReadOnly);
            if (request?.InteractionTypeId == "character.cleanup.settlement")
            {
                var chip = CoverageMode ? hud.GetComponentsInChildren<SettlementEffectChipView>().FirstOrDefault(c =>
                    (bool)Read(c, "executionAllowed") && !(bool)Read(c, "pending") &&
                    (!CampaignMode || (Read(c, "option") as SettlementOptionView)?.SourceDefinitionId != "infected.character.amiya")) : null;
                if (chip != null && (!mobile.CanEndCurrentAction() || CampaignMode &&
                    CampaignDefinitionMissing((Read(chip, "option") as SettlementOptionView)?.SourceDefinitionId) > 0))
                    yield return PointerSequence(chip.gameObject, "正常收尾实际结算来源");
                else
                {
                    Require(mobile.CanEndCurrentAction(), "正常收尾仍有必须处理的结算，不能直接结束");
                    yield return Press(hud.Frame.EndActionButton, "正常收尾结算首次结束");
                }
            }
            else if (request?.InteractionTypeId == "event_card.option")
            {
                var card = EventCardDatabase.Get(State, PaymentField(request, "cardId")?.StringValue ?? "");
                bool needsFuel = CampaignMode && State.Round <= 3 && NeedsMoveFuel(PlayProgress(LocalPlayer));
                int preferred = PreferredEntranceEventOption(card, State.Phase == GamePhase.Entrance, needsFuel);
                var option = FindObjectsOfType<PlayerAutomationId>().Where(v => v.Id.StartsWith("event.option.") && v.GetComponent<Button>()?.IsInteractable() == true)
                    .OrderBy(v => v.Id == "event.option." + preferred ? 0 : 1).ThenBy(v => v.Id).FirstOrDefault();
                Require(option != null, "正常事件请求没有对应事件按钮");
                Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                    Policy = PlayPolicyVersion, Action = "事件牌选项", Card = card?.CardId, Name = card?.Name,
                    PreferredIndex = preferred, ActualControl = option.Id,
                    Reason = State.Phase == GamePhase.Entrance ? "入场除废弃矿坑外优先3碎；只选当前真实按钮" :
                        needsFuel ? "前三回合移城尚缺燃料，在本次正式事件选项中优先补碎片" : "后续事件沿当前正式候选处理" });
                yield return Press(option.GetComponent<Button>(), "正常随机事件首次选择");
            }
            else if (CampaignMode && new[] { CharacterLifecycleService.ModeInteractionTypeId,
                CharacterLifecycleService.OrderInteractionTypeId, CharacterLifecycleService.SecondModeInteractionTypeId }.Contains(Request?.InteractionTypeId))
            { yield return CampaignModeChoice(); }
            else if (CoverageMode && ChoiceExecuteButton() != null)
            {
                yield return Press(ChoiceExecuteButton(), "正常效果页面选择一个合法分支");
            }
            else if (enterprise != null)
            {
                var chosen = enterprise.Draft.Projection.Targets.Where(t => enterprise.Draft.IsAvailable(t))
                    .OrderBy(t => CampaignMode ? CampaignEnterprisePriority(t, enterprise.Draft.Projection) : 0)
                    .Take(Math.Max(1, enterprise.Draft.Projection.MinSelections)).Select(t => t.Id).ToArray();
                Require(chosen.Length == Math.Max(1, enterprise.Draft.Projection.MinSelections), "正常企业候选没有足够可用项");
                foreach (var id in chosen)
                {
                    var target = enterprise.Draft.Projection.Targets.Single(t => t.Id == id);
                    var boards = (IDictionary)Read(enterprise, "items");
                    var board = (EnterpriseBoardItemView)boards[target.BoardId];
                    var targets = (IDictionary)Read(board, "targets");
                    yield return Press((Button)Read(targets[target.Id], "button"), "正常企业/科室候选" + target.Id);
                }
                yield return Press((Button)Read(enterprise, "confirm"), "企业/科室首次确认");
            }
            else
            {
                var cards = FindObjectsOfType<PlayerAutomationId>().Where(v => v.Id.StartsWith("selection.card.") &&
                    v.GetComponent<Button>()?.IsInteractable() == true).ToArray();
                if (cards.Length > 0)
                {
                    var ids = cards.Select(v => v.Id.Substring("selection.card.".Length)).ToArray();
                    string Definition(string id) => State.ItemZones.Items.FirstOrDefault(i => i.ItemId == id)?.DefinitionId ??
                        (request == null ? id : GameplayExpansionUi.CandidateDefinition(request, id));
                    bool discard = request?.InteractionTypeId == "setup.basic_character.discard" || request?.InteractionTypeId == "operator.dispatch.return";
                    bool wantsOtherCard = State.Phase == GamePhase.CharacterCover &&
                        activatedAmiyaByPlayer.TryGetValue(LocalPlayer, out var activeSource) &&
                        CharacterLifecycleService.IsSubscriptionActive(State, activeSource, "infected.character.amiya.tactic");
                    ids = ids.OrderBy(id => Definition(id) == "infected.character.amiya" ? (discard || wantsOtherCard ? 1 : -1) : 0).ToArray();
                    if (SalesMode) ids = ids.OrderBy(id => Definition(id) == "cannot" ? (discard ? 1000 : -1000) :
                        discard ? -CoveragePriorityCount("character:" + Definition(id) + ":strategy") : CoveragePriorityCount("character:" + Definition(id) + ":strategy")).ToArray();
                    if (CampaignMode) ids = ids.OrderBy(id => discard ? -EnterpriseCharacterPriority(Definition(id)) : EnterpriseCharacterPriority(Definition(id)))
                        .ThenBy(id => discard ? CampaignDefinitionMissing(Definition(id)) : -CampaignDefinitionMissing(Definition(id))).ThenBy(id => Definition(id)).ToArray();
                    ids = ids.OrderBy(id => CampaignMode ? (discard ? -RequestedCharacterPriority(Definition(id)) : RequestedCharacterPriority(Definition(id))) : 0)
                        .ThenBy(id => IsUiTargetVisible(cards.Single(card => card.Id == "selection.card." + id).gameObject) ? 0 : 1).ToArray();
                    int count = request == null ? 1 : Math.Max(1, request.MinSelections);
                    Require(count <= ids.Length, "正常牌候选不足");
                    foreach (var id in ids.Take(count))
                        yield return Press(FindObjectsOfType<PlayerAutomationId>().Single(v => v.Id == "selection.card." + id).GetComponent<Button>(), "正常选择牌" + id);
                    yield return Press(FindObjectsOfType<Button>().Single(b => b.name == "Confirm Selection" && b.IsInteractable()), "正常牌选择首次确认");
                    if (CoverageMode && request?.PromptKey == "facility.entry.choose_additional_build" && count == 1)
                    {
                        lastBuildItem = ids[0]; lastBuildDefinition = Definition(ids[0]); lastBuildRecorded = false;
                        Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                            Action = "额外建设使用实际所选设施来源", ItemId = lastBuildItem, Definition = lastBuildDefinition,
                            OriginalRequest = request.InteractionId, Reason = "原控件选卡提交后才记录来源；后续仍等待正式付款及强制放置" });
                    }
                }
                else
                {
                    var rows = FindObjectsOfType<EffectDialogOptionRowView>().Where(v => v.Button.IsInteractable())
                        .OrderBy(v => IsUiTargetVisible(v.Button.gameObject) ? 0 : 1)
                        .ThenBy(v => CampaignSpecialOptionPriority(v.name)).ToArray();
                    if (rows.Length > 0)
                    {
                        int count = request == null ? 1 : Math.Max(1, request.MinSelections);
                        Require(count <= rows.Length, "正常文本候选不足");
                        // 每次选择会正常重绘列表，按当前控件名重新定位，不保存失效引用。
                        var names = rows.Take(count).Select(r => r.name).ToArray();
                        foreach (var name in names)
                            yield return Press(FindObjectsOfType<EffectDialogOptionRowView>().Single(r => r.name == name).Button, "正常文本选择" + name);
                        var confirm = FindObjectsOfType<Button>().FirstOrDefault(b => b.name == "Confirm Selection" && b.IsInteractable());
                        if (confirm != null) yield return Press(confirm, "正常文本选择首次确认");
                    }
                    else
                    {
                        var candidates = FindObjectsOfType<MapButtonView>().Where(b =>
                            (b.TargetKind == WorkflowHighlightTargetKind.Location || CoverageMode &&
                                (b.TargetKind == WorkflowHighlightTargetKind.InfluenceSlot ||
                                 request?.InteractionTypeId == "effect.route.choose" && b.TargetKind == WorkflowHighlightTargetKind.Route && request.CandidateIds.Contains(b.TargetId))) &&
                            b.IsInteractive).ToArray();
                        var target = PreferVisibleMapTarget(candidates.Where(IsMapTargetVisible).Concat(candidates.OrderBy(b => b.TargetId)));
                        Require(target != null, "当前正常请求未找到对应真实候选控件：" + request?.InteractionTypeId);
                        string location = target.TargetId;
                        var kind = target.TargetKind;
                        yield return MapPress(kind, location, "正常入场/地图选择");
                        if (identity == (Request?.InteractionId ?? "") + ":" + LocalPlayer + ":" + State.EffectRuntime.StateRevision)
                            yield return MapPress(kind, location, "正常地图二次确认");
                    }
                }
            }
            yield return Capture("正常选择后");
            Require(identity != (Request?.InteractionId ?? "") + ":" + LocalPlayer + ":" + State.EffectRuntime.StateRevision,
                "真实候选首次确认未推进正常请求，保留现场，不重试");
            if (temporaryChoice || optionalExecution)
            {
                Require(commands.Count == choiceCommandsBefore + 1, "原执行条目未恰好提交一次命令");
                var command = Read(commands[choiceCommandsBefore], "Command") as GameCommand;
                var receipt = Read(commands[choiceCommandsBefore], "Result") as WorkflowSubmissionResult;
                Require(command != null && command.PlayerId == request.AnsweringPlayerId &&
                    command.Parameters.TryGetValue("interactionId", out string answeredId) && answeredId == request.InteractionId &&
                    command.Parameters.TryGetValue("expectedRevision", out string revision) && revision == choiceRevisionBefore.ToString() &&
                    command.OptionIds.All(id => request.CandidateIds.Contains(id)) &&
                    receipt?.CommandResult?.Succeeded == true && receipt.AppliedLocally,
                    "原执行条目的请求身份或准确Applied回执不匹配");
                Write("原执行条目回执-" + request.InteractionId + ".json", new { State.GameId, State.Round,
                    OriginalRequest = request, CommandsAdded = commands.Count - choiceCommandsBefore,
                    LastCommand = commands[choiceCommandsBefore], BeforeRevision = choiceRevisionBefore,
                    AfterRevision = State.EffectRuntime.StateRevision, NextRequest = Request,
                    FullEffectCompletionClaimed = false, State = Observe() });
                Covered(temporaryChoice ? "page:temporary-settlement-declared" :
                    command.OptionIds.Count > 0 ? "page:min0-original-row-choice" : "page:min0-empty-answer");
                yield return new WaitForEndOfFrame();
                SaveCapture(temporaryChoice ? "临时聚居点原答案后正常后继" : "min0原执行后正常后继");
            }
        }

        private IEnumerator NormalInfluenceMove(InteractionRequestProjection request)
        {
            var pairs = request.CandidateIds.Select(id => new { Id = id, Parts = id.Split('|') }).ToArray();
            Require(pairs.Length > 0 && pairs.All(pair => pair.Parts.Length == 3), "原影响力移动候选结构不完整");
            var sources = pairs.Select(pair => pair.Parts[1]).Distinct().ToDictionary(id => id,
                id => new ItemZoneQuery().FindItem(State, id)?.SlotId);
            var source = FindObjectsOfType<MapButtonView>().Where(button => button.TargetKind == WorkflowHighlightTargetKind.InfluenceSlot &&
                button.IsInteractive && sources.Values.Contains(button.TargetId)).OrderBy(button => IsMapTargetVisible(button) ? 0 : 1)
                .ThenBy(button => button.TargetId).FirstOrDefault();
            if (source == null) throw new DriverContinuationPendingException("角色移动当前来源阶段没有原候选对应地图高亮");
            string sourceSlot = source.TargetId, sourceId = sources.Single(pair => pair.Value == sourceSlot).Key;
            int before = commands.Count, revision = State.EffectRuntime.StateRevision;
            yield return Capture("角色影响力移动原地图来源");
            yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, sourceSlot, "角色移动来源原首击");
            if (FindObjectsOfType<MapButtonView>().Any(button => button.TargetKind == WorkflowHighlightTargetKind.InfluenceSlot &&
                button.TargetId == sourceSlot && button.IsInteractive && button.IsSelected))
                yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, sourceSlot, "角色移动来源原二击");
            Require(commands.Count == before && State.EffectRuntime.StateRevision == revision && Request?.InteractionId == request.InteractionId,
                "角色移动来源只能修改本地草稿，不能提前提交或结束正式请求");
            var allowed = pairs.Where(pair => pair.Parts[1] == sourceId).ToArray();
            var target = FindObjectsOfType<MapButtonView>().Where(button => button.TargetKind == WorkflowHighlightTargetKind.InfluenceSlot &&
                button.IsInteractive && allowed.Any(pair => pair.Parts[2] == button.TargetId)).OrderBy(button => IsMapTargetVisible(button) ? 0 : 1)
                .ThenBy(button => button.TargetId).FirstOrDefault();
            if (target == null) throw new DriverContinuationPendingException("来源已选，目标阶段未形成正式候选对应地图高亮");
            string targetSlot = target.TargetId, answer = allowed.Single(pair => pair.Parts[2] == targetSlot).Id;
            yield return Capture("角色影响力移动原地图目标");
            yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, targetSlot, "角色移动目标原首击");
            Require(commands.Count == before && State.EffectRuntime.StateRevision == revision, "角色移动目标首击不能提前提交");
            yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, targetSlot, "角色移动目标原二击确认");
            Require(commands.Count == before + 1, "角色移动原目标确认未准确提交一次");
            var command = Read(commands[before], "Command") as GameCommand;
            var result = Read(commands[before], "Result") as WorkflowSubmissionResult;
            Require(command?.Kind == GameCommandKind.AnswerInteraction && command.OptionIds.SequenceEqual(new[] { answer }) &&
                command.Parameters.TryGetValue("interactionId", out string id) && id == request.InteractionId &&
                command.Parameters.TryGetValue("expectedRevision", out string expected) && expected == request.StateRevision.ToString() &&
                result?.CommandResult?.Succeeded == true && result.AppliedLocally && State.EffectRuntime.StateRevision > revision,
                "角色移动确认未得到原完整候选、strict revision与准确Applied回执");
            Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer, Action = "角色移动原来源与目标两阶段完成",
                SourceId = sourceId, SourceSlot = sourceSlot, TargetSlot = targetSlot, Candidate = answer, command.CommandId });
        }
        [Serializable] private sealed class CampaignTarget
        {
            public string Key, DefinitionId, Category, AbilityId, Expansion, SettlementKind;
            public int Level;
        }
        [Serializable] private sealed class CampaignGame
        {
            public string GameId, ContentHash, AssemblyVersion, Mode, Output, Failure;
            public int Round, Revision, CommandCount;
            public bool Terminal, Passed;
            public List<string> Completed = new List<string>();
        }
        [Serializable] private sealed class CampaignVersion
        {
            public string Key;
            public List<CampaignTarget> Targets = new List<CampaignTarget>();
            public Dictionary<string, int> Completed = new Dictionary<string, int>();
            public List<CampaignGame> Games = new List<CampaignGame>();
        }
        [Serializable] private sealed class CampaignProgress
        {
            public List<CampaignVersion> Versions = new List<CampaignVersion>();
            public string Boundary = "仅正常可见Player、合法行动与实际完成证据；不同模式/内容hash/运行程序集分区，企业家排除；未覆盖不是通过";
        }
        private CampaignProgress campaign;
        private CampaignVersion campaignVersion;
        private CampaignGame campaignGame;
        private string campaignPath;
        private string campaignPriorityBaselinePath, campaignPriorityBaselineVersion;
        private readonly Dictionary<string, int> campaignPriorityBaseline = new Dictionary<string, int>();
        private readonly HashSet<string> requestedCampaignPriorities = new HashSet<string>();
        private readonly HashSet<string> campaignCaptureKeys = new HashSet<string>();
        private readonly HashSet<string> campaignEvidenceIds = new HashSet<string>();
        private readonly HashSet<string> campaignActionAttempts = new HashSet<string>();
        private int CampaignCount(string key) => campaignVersion?.Completed != null && campaignVersion.Completed.TryGetValue(key, out int count) ? count : 0;
        // 旧证据只改变合法候选排序；当前版本的完成账目始终由实际回执更新。
        private int CampaignPriorityCount(string key) => Math.Max(Math.Max(CampaignCount(key),
            campaignPriorityBaseline.TryGetValue(key, out int count) ? count : 0),
            requestedCampaignPriorities.Count > 0 && !requestedCampaignPriorities.Contains(key) ? 1 : 0);
        private int CampaignDefinitionMissing(string definition) => campaignVersion == null ? 2 :
            campaignVersion.Targets.Count(t => t.DefinitionId == definition && CampaignPriorityCount(t.Key) == 0);
        private int RequestedCharacterPriority(string definition) => !string.IsNullOrEmpty(definition) &&
            requestedCampaignPriorities.Any(key => key.StartsWith("character:" + definition + ":", StringComparison.Ordinal) && CampaignCount(key) == 0) ? -1 : 0;
        private bool TemporaryChoiceNeeded => CampaignMode && requestedCampaignPriorities.Contains("page:temporary-settlement-declared") &&
            CampaignCount("page:temporary-settlement-declared") == 0;
        private int RequestedBuildPriority(string definition) => CampaignMode &&
            (TemporaryChoiceNeeded && definition == "infected.facility.temporary_settlement" ||
             requestedCampaignPriorities.Contains("build:" + definition) && CampaignCount("build:" + definition) == 0) ? -1 : 0;
        private void LoadCampaign()
        {
            campaignPath = Argument("--yc-ui-campaign-path=");
            Require(!string.IsNullOrEmpty(campaignPath) && Path.IsPathRooted(campaignPath), "整局累计路径必须明确");
            campaign = File.Exists(campaignPath) ? JsonConvert.DeserializeObject<CampaignProgress>(File.ReadAllText(campaignPath)) : new CampaignProgress();
            campaign = campaign ?? new CampaignProgress();
            string mode = "standard:" + (Argument("--yc-campaign-expansions=") ?? "enterprise,infected");
            string assembly = typeof(HotseatUiValidationDriver).Assembly.ManifestModule.ModuleVersionId.ToString();
            string versionKey = mode + ":" + State.ContentPackHash + ":" + assembly;
            campaignVersion = campaign.Versions.FirstOrDefault(v => v.Key == versionKey);
            if (campaignVersion == null)
            {
                campaignVersion = new CampaignVersion { Key = versionKey };
                var root = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
                var pack = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
                foreach (var relative in pack["definitions"].Values<string>())
                {
                    var content = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(root, relative)));
                    string definition = (string)content["definitionId"], kind = (string)content["contentType"], expansion = (string)content["expansionId"];
                    if (expansion == "entrepreneur") continue;
                    void Add(string key, string category, string ability = "", int level = 0, string settlementKind = "") => campaignVersion.Targets.Add(new CampaignTarget
                        { Key = key, DefinitionId = definition, Category = category, AbilityId = ability, Level = level, Expansion = expansion, SettlementKind = settlementKind });
                    if (kind == "character" && State.ContentDefinitions.Characters.Any(c => c.TemplateId == definition))
                        foreach (var ability in content["abilities"])
                            if (new[] { "strategy", "tactic" }.Contains((string)ability["mode"]))
                                Add("character:" + definition + ":" + (string)ability["mode"], "角色模式", (string)ability["abilityId"],
                                    settlementKind: (string)ability["settlement"]?["entryKind"] ?? "");
                    if (kind == "city_style" && State.ContentDefinitions.CityStyles.Any(s => s.CityStyleId == definition))
                    {
                        Add("style:" + definition + ":declare", "城市样式宣告");
                        if (content["data"]?["specialAction"] is Newtonsoft.Json.Linq.JObject)
                            Add("style:" + definition + ":effect", "城市样式效果", (string)content["data"]["luaAbilityId"] ?? "city_styles." + definition);
                    }
                    if (kind == "enterprise" && State.ContentDefinitions.EnterpriseDefinitionIds.Contains(definition))
                        foreach (var level in content["data"]["levels"])
                            if ((string)level["kind"] == "special") Add("enterprise:" + definition + ":level:" + (int)level["level"],
                                "企业等级特效", (string)level["abilityId"], (int)level["level"]);
                }
                campaign.Versions.Add(campaignVersion);
            }
            campaignGame = new CampaignGame { GameId = State.GameId, ContentHash = State.ContentPackHash, AssemblyVersion = assembly, Mode = mode, Output = output };
            campaignVersion.Games.Add(campaignGame);
            foreach (string key in (Argument("--yc-campaign-requested-priorities=") ?? "").Split(';'))
                if (!string.IsNullOrEmpty(key)) requestedCampaignPriorities.Add(key);
            Append("决策变化.jsonl", new { State.GameId, Action = "本批正常局优先级", Keys = requestedCampaignPriorities,
                Reason = "旧局缺口只作为当前本人合法候选排序请求，不迁入本版完成账目" });
            campaignPriorityBaselinePath = Argument("--yc-ui-campaign-priority-baseline=");
            if (!string.IsNullOrEmpty(campaignPriorityBaselinePath))
            {
                var proofPath = Argument("--yc-ui-campaign-priority-proof=");
                Require(Path.IsPathRooted(campaignPriorityBaselinePath) && File.Exists(campaignPriorityBaselinePath) &&
                    !string.IsNullOrEmpty(proofPath) && Path.IsPathRooted(proofPath) && File.Exists(proofPath), "缺口排序基线或生产输入证明缺失");
                var prior = JObject.Parse(File.ReadAllText(campaignPriorityBaselinePath));
                var proof = JObject.Parse(File.ReadAllText(proofPath));
                Require((bool?)proof["Enabled"] == true && ((int?)proof["ProductionInputDriftCount"] == 0 ||
                    (int?)proof["UnexplainedProductionInputDriftCount"] == 0 && (bool?)proof["RulesAndCandidateInputsMatch"] == true &&
                    proof["AuthorizedUiAssetChanges"] is JArray changes && changes.Count <= 2 &&
                    (proof["AuthorizedUiSourceChanges"] as JArray)?.Count <= 1 &&
                    ((proof["AuthorizedContentDisplayChanges"] as JArray)?.Count ?? 0) <= 1 &&
                    changes.Count + ((proof["AuthorizedUiSourceChanges"] as JArray)?.Count ?? 0) +
                        ((proof["AuthorizedContentDisplayChanges"] as JArray)?.Count ?? 0) > 0) &&
                    (string)proof["BaselinePath"] == campaignPriorityBaselinePath &&
                    (string)prior["Game"]?["Mode"] == mode && ((string)prior["Game"]?["ContentHash"] == State.ContentPackHash ||
                        (bool?)proof["ContentDisplayBusinessUnchanged"] == true &&
                        (string)proof["BaselineContentHash"] == (string)prior["Game"]?["ContentHash"] &&
                        proof["AuthorizedContentDisplayChanges"] is JArray contentChanges && contentChanges.Count == 1 &&
                        ((string)contentChanges[0]["Path"]).Replace('\\', '/') == "Assets/StreamingAssets/Content/core/lua/enterprises/infected.enterprise.stonehoove_security.level_2.special.lua"),
                    "缺口排序基线模式/内容hash/生产输入不匹配");
                campaignPriorityBaselineVersion = (string)prior["Key"];
                foreach (var entry in ((JObject)prior["Cumulative"]).Properties())
                    campaignPriorityBaseline[entry.Name] = (int)entry.Value;
            }
            Write("本局正式目标全集.json", campaignVersion.Targets);
            Write("开局缺口与决策.json", new { campaignVersion.Key, State.GameId, Missing = campaignVersion.Targets.Where(t => CampaignCount(t.Key) == 0),
                PriorityBaselinePath = campaignPriorityBaselinePath, PriorityBaselineVersion = campaignPriorityBaselineVersion,
                NextPriorities = campaignVersion.Targets.Where(t => CampaignPriorityCount(t.Key) == 0), BaselineChangesCurrentCompleted = false,
                Decision = "按真实缺口排序本人正式可见合法候选；未出现继续本局至八回合，不改随机、不注入；旧快照的阿米娅可选插入不使用" });
            PersistCampaign();
        }
        private void CampaignCompleted(string key, object evidence)
        {
            if (campaignGame == null || !recordedThisGame.Add(key)) return;
            campaignGame.Completed.Add(key);
            campaignVersion.Completed[key] = CampaignCount(key) + 1;
            Append("实际完成覆盖.jsonl", new { State.GameId, State.ContentPackHash, Key = key, State.Round,
                Revision = State.EffectRuntime.StateRevision, Evidence = evidence });
        }
        private void ObserveCampaignCompletion()
        {
            if (campaignGame == null) return;
            var completedNodes = new HashSet<string>(State.EffectRuntime.EffectNodes
                .Where(n => n.Status == EffectNodeStatus.Completed).Select(n => n.EffectId), StringComparer.Ordinal);
            foreach (var use in State.CharacterUses)
            {
                string definition = State.ItemZones.Items.FirstOrDefault(i => i.ItemId == use.CardInstanceId)?.DefinitionId;
                foreach (var mode in use.CompletedModes.Where(m => !use.SkippedModes.Contains(m)))
                {
                    var target = campaignVersion.Targets.FirstOrDefault(t => t.Key == "character:" + definition + ":" + mode);
                    if (target == null || recordedThisGame.Contains(target.Key)) continue;
                    if (target.SettlementKind == "cleanup_order" && !CleanupModeActuallyCompleted(use, target)) continue;
                    var receipts = State.EffectRuntime.DispatchReceipts.Where(r => r.AbilityId == target.AbilityId && r.Succeeded && ReceiptBelongsToUse(r, use, mode) && r.AttachedEffectIds.Count > 0 &&
                        r.AttachedEffectIds.All(completedNodes.Contains)).ToArray();
                    if (receipts.Length > 0) CampaignCompleted(target.Key, new { CharacterUse = use, Receipts = receipts,
                        Nodes = State.EffectRuntime.EffectNodes.Where(n => receipts.SelectMany(r => r.AttachedEffectIds).Contains(n.EffectId)).ToArray(), LastCommand = commands.LastOrDefault() });
                }
            }
            foreach (var receipt in State.EffectRuntime.DispatchReceipts.Where(r => r.Succeeded && r.AttachedEffectIds.Count > 0))
            {
                if (campaignEvidenceIds.Contains(receipt.ReceiptId)) continue;
                var target = campaignVersion.Targets.FirstOrDefault(t => t.Category != "角色模式" &&
                    !string.IsNullOrEmpty(t.AbilityId) && t.AbilityId == receipt.AbilityId);
                if (target == null) { campaignEvidenceIds.Add(receipt.ReceiptId); continue; }
                if (!receipt.AttachedEffectIds.All(completedNodes.Contains)) continue;
                campaignEvidenceIds.Add(receipt.ReceiptId);
                var ruleEvent = State.EffectRuntime.RuleEvents.FirstOrDefault(e => e.EventId == receipt.EventId);
                CampaignCompleted(target.Key, new { Receipt = receipt, Event = ruleEvent,
                    Nodes = State.EffectRuntime.EffectNodes.Where(n => receipt.AttachedEffectIds.Contains(n.EffectId)).ToArray(), LastCommand = commands.LastOrDefault() });
            }
            foreach (var node in State.EffectRuntime.EffectNodes.Where(n => n.Status == EffectNodeStatus.Completed && n.EffectTypeId == CityStyleDeclarationEffectExecutor.TypeId))
            foreach (var player in State.Players)
            foreach (var declaration in player.DeclaredCityStyles)
                if (!string.IsNullOrEmpty(declaration.DeclarationId) && player.PlayerId == node.PlayerId && declaration.DeclarationId ==
                    node.NormalizedResult?.Properties?.FirstOrDefault(p => p.Name == "declarationId")?.Value?.StringValue)
                    CampaignCompleted("style:" + declaration.CityStyleId + ":declare", new { Node = node, Declaration = declaration, player.PlayerId, LastCommand = commands.LastOrDefault() });
            PersistCampaign();
        }
        private bool ReceiptBelongsToUse(DispatchReceipt receipt, CharacterUseRuntimeState use, string mode)
        {
            var ruleEvent = State.EffectRuntime.RuleEvents.FirstOrDefault(e => e.EventId == receipt.EventId);
            var source = State.EffectRuntime.EffectNodes.FirstOrDefault(n => n.EffectId == ruleEvent?.SourceEffectId);
            var contract = use.Modes.FirstOrDefault(m => m.Mode == mode);
            if (contract?.SkillType == "persistent")
                return ruleEvent?.EventType != "CharacterAbilityActivated" && receipt.AttachedEffectIds.Any(id =>
                    State.EffectRuntime.EffectNodes.Any(n => n.EffectId == id && n.SourceId == "character-card:" + use.CardInstanceId && n.PlayerId == use.ExecutingPlayerId));
            return source?.NormalizedArguments?.Properties?.FirstOrDefault(p => p.Name == "useId")?.Value?.StringValue == use.UseId;
        }
        private bool CleanupModeActuallyCompleted(CharacterUseRuntimeState use, CampaignTarget target)
        {
            foreach (var owner in State.EffectRuntime.EffectNodes.Where(n => n.SettlementWindow != null))
            foreach (var entry in owner.SettlementWindow.Entries.Where(e => e.SourceItemId == use.CardInstanceId && e.AbilityId == target.AbilityId &&
                e.AcceptedUseCount > 0 && !string.IsNullOrEmpty(e.TargetEffectId)))
            {
                var effect = State.EffectRuntime.EffectNodes.FirstOrDefault(n => n.EffectId == entry.TargetEffectId);
                if (effect?.Status != EffectNodeStatus.Completed) continue;
                var source = State.EffectRuntime.EffectNodes.FirstOrDefault(n => n.EffectId == entry.SourceEffectId);
                var seen = new HashSet<string>();
                while (source != null && seen.Add(source.EffectId))
                {
                    if (source.EffectId == use.OwnerEffectId || source.NormalizedArguments?.Properties?.FirstOrDefault(p => p.Name == "useId")?.Value?.StringValue == use.UseId)
                        return true;
                    source = State.EffectRuntime.EffectNodes.FirstOrDefault(n => n.EffectId == source.ParentEffectId);
                }
            }
            return false;
        }
        private void PersistCampaign()
        {
            if (campaignGame == null) return;
            campaignGame.Round = State.Round; campaignGame.Revision = State.EffectRuntime.StateRevision; campaignGame.CommandCount = commands.Count;
            File.WriteAllText(campaignPath, JsonConvert.SerializeObject(campaign, Formatting.Indented));
            var missing = campaignVersion.Targets.Where(t => CampaignCount(t.Key) == 0).ToArray();
            Write("本局覆盖与下一局决策.json", new { campaignVersion.Key, Game = campaignGame, Cumulative = campaignVersion.Completed,
                PlayPolicy = PlayPolicyVersion, PlayerPlans = playerPlay.Values,
                BaseActionBudget = new { EventActions = 3, Buildings = 11, RedZoneOrSpecialActions = 2, BonusActionsAssumed = 0 },
                TargetPagesCovered, MissingTargetPages = new[] { "page:cannot-sale", "page:trade-sale" }.Where(key => CoverageCount(key) == 0).ToArray(),
                MissingTargetPageResources = new[] { "cannot-sale", "trade-sale" }.SelectMany(page => new[] { "originium", "originium-shard", "iron", "pure-originium" }
                    .Select(resource => "page:" + page + ":resource:" + resource)).Where(key => CoverageCount(key) == 0).ToArray(),
                Missing = missing, NextPriorities = missing.Where(t => CampaignPriorityCount(t.Key) == 0)
                    .OrderBy(t => t.Category == "角色模式" ? 0 : t.Category == "城市样式宣告" ? 1 : 2),
                PriorityBaselinePath = campaignPriorityBaselinePath, PriorityBaselineVersion = campaignPriorityBaselineVersion,
                BaselineChangesCurrentCompleted = false,
                NotEncounteredIsFailure = false, KnownUiDefect = "旧163339快照阿米娅顺序页不计合规；新合并版沿用原CardViewer按钮，不以请求类型仍为Order判失败" });
            Write("实时进度.json", new { State.GameId, State.Round, State.Phase, Stage = stage, Revision = State.EffectRuntime.StateRevision,
                Commands = commands.Count, Completed = campaignGame.Completed, Terminal = campaignGame.Terminal, RuntimeStatus = State.EffectRuntime.Status });
        }
        private int CampaignEnterprisePriority(EnterpriseSelectionTarget target, EnterpriseSelectionProjection projection)
        {
            var board = projection.Boards.FirstOrDefault(b => b.Id == target.BoardId) ?? projection.FixedCompany;
            string definition = State.ItemZones.Items.FirstOrDefault(i => i.ItemId == target.CompanyId || i.ItemId == target.BoardId)?.DefinitionId ?? board?.VisualKey ?? target.CompanyId;
            var matching = campaignVersion.Targets.Where(t => t.DefinitionId == definition && t.Category == "企业等级特效").ToArray();
            int missing = matching.Count(t => CampaignPriorityCount(t.Key) == 0);
            if (!string.IsNullOrEmpty(target.EffectId))
                return matching.Any(t => t.AbilityId == target.EffectId && CampaignPriorityCount(t.Key) == 0) ? -100 : 0;
            return -missing;
        }
        private IEnumerator CampaignModeChoice()
        {
            var viewer = FindObjectsOfType<CardViewer>().FirstOrDefault(v => v.gameObject.activeInHierarchy);
            Require(viewer != null, "角色正式模式请求未显示原CardViewer，保留页面与请求");
            string definition = State.ItemZones.Items.FirstOrDefault(i => i.ItemId == viewer.CardId)?.DefinitionId ?? viewer.CardId;
            var choices = new[] { new { Mode = "strategy", Button = viewer.StrategyButton }, new { Mode = "tactic", Button = viewer.PlotButton } }
                .Where(c => c.Button.IsActive() && c.Button.IsInteractable())
                .OrderBy(c => EnterpriseCharacterPriority(definition) < 0 && definition == "mountain" && c.Mode == "tactic" ? -1 : 0)
                .ThenBy(c => CampaignPriorityCount("character:" + definition + ":" + c.Mode)).ToArray();
            Require(choices.Length > 0, "角色原卡面没有正式可用策略/计谋按钮");
            Append("决策变化.jsonl", new { State.GameId, State.Round, Action = "原角色卡模式选择", Definition = definition,
                Request = Request, Mode = choices[0].Mode, PriorCompleted = CampaignCount("character:" + definition + ":" + choices[0].Mode),
                PriorityCount = CampaignPriorityCount("character:" + definition + ":" + choices[0].Mode), PriorityBaselineVersion = campaignPriorityBaselineVersion });
            yield return Press(choices[0].Button, "正常原角色卡选择" + choices[0].Mode);
        }
        private IEnumerator CampaignSale()
        {
            string page = ResolveSaleSource(Request);
            var owner = State.EffectRuntime.EffectNodes.FirstOrDefault(node => node.EffectId == Request?.OwnerEffectId);
            Append("出售来源识别.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                Request = Request, Page = page, Owner = owner == null ? null : new {
                    owner.EffectId, owner.ParentEffectId, owner.SourceId, owner.ContinuationContentId,
                    owner.ContinuationContentInstanceId, owner.ContinuationAbilityId } });
            if (page != "other-sale" && SalePageNeedsCoverage(page))
            { yield return SalePage(); yield break; }
            var before = State.FindPlayer(LocalPlayer).Resources;
            var amounts = PlannedSaleAmounts(before);
            var rows = FindObjectsOfType<EffectDialogResourceRowView>().OrderBy(row => row.name).ToArray();
            var row = rows.Where((value, index) => index < amounts.Length && amounts[index] > 0 && value.IncreaseButton.IsInteractable()).FirstOrDefault();
            if (row == null) { yield return Press(SaleFinish(), "无多余可售库存，保留移城燃料并正常结束"); yield break; }
            Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer, Action = "出售保留前期燃料",
                ReserveShards = SaleFuelReserve(), Before = before, PlannedAmounts = amounts,
                Reason = "只卖正式库存中的多余资源；前三回合尚欠移城时保留下一次正式移城所需碎片" });
            yield return Press(row.IncreaseButton, "正常出售一份实际库存");
            yield return Press(SaleConfirm(), "正常出售首次确认");
            if (SaleFinish() != null) yield return Press(SaleFinish(), "正常完成本次出售");
        }
        private int SaleFuelReserve()
        {
            if (!CampaignMode || State.Round > 3) return 0;
            var progress = PlayProgress(LocalPlayer);
            return progress.Moves >= (progress.Entrance == "B-01" ? 3 : 2) ? 0 :
                new TravelCostService(new MapQueryService(StaticMapDefinitions.Resolve(State.MapId))).GetCityMoveBaseCost().OriginiumShard;
        }
        private int[] PlannedSaleAmounts(ResourceSet resources) => new[] { Math.Min(1, resources.Originium),
            Math.Min(1, Math.Max(0, resources.OriginiumShard - SaleFuelReserve())), Math.Min(1, resources.Iron), Math.Min(1, resources.PureOriginium) };
        private bool SalePageNeedsCoverage(string page)
        {
            if (CoverageCount("page:" + page) == 0 || CoverageCount("page:" + page + ":multi-batch") == 0) return true;
            var amounts = PlannedSaleAmounts(State.FindPlayer(LocalPlayer).Resources);
            return new[] { "originium", "originium-shard", "iron", "pure-originium" }
                .Where((resource, index) => amounts[index] > 0 && CoverageCount("page:" + page + ":resource:" + resource) == 0).Any();
        }
        private string ResolveSaleSource(InteractionRequestProjection request)
        {
            if (request?.PromptKey == "character.cannot.strategy.choose_sale") return "cannot-sale";
            string owner = request?.OwnerEffectId;
            var seen = new HashSet<string>();
            while (!string.IsNullOrEmpty(owner) && seen.Add(owner))
            {
                var node = State.EffectRuntime.EffectNodes.FirstOrDefault(value => value.EffectId == owner);
                if (node == null) break;
                if (node.ContinuationContentId == "cannot" || node.ContinuationAbilityId == "character.cannot.strategy") return "cannot-sale";
                if (node.ContinuationAbilityId == "facility.entry.sell_resources" ||
                    node.ContinuationContentId == "content.facility.sell_resources" ||
                    node.SourceId == "content.facility.sell_resources") return "trade-sale";
                foreach (string id in new[] { node.ContinuationContentId, node.SourceId, node.ContinuationContentInstanceId })
                {
                    var item = State.ItemZones.Items.FirstOrDefault(value => value.ItemId == id);
                    string definition = item?.DefinitionId ?? id;
                    if (definition == "cannot") return "cannot-sale";
                    if (FacilityCardDatabase.Get(State, definition)?.EffectId == "sell_resources") return "trade-sale";
                }
                owner = node.ParentEffectId;
            }
            return request?.PromptKey == "facility.entry.sell_resources" ? "trade-sale" : "other-sale";
        }
        private static int PreferredEntranceEventOption(EventCardDefinition card, bool entrance, bool needsFuel)
        {
            if (card == null) return 0;
            if (entrance) return card.Name.StartsWith("废弃矿坑", StringComparison.Ordinal) ? 0 :
                card.ChoiceRewards.FindIndex(reward => reward != null && reward.OriginiumShard == 3);
            if (!needsFuel) return 0;
            return Enumerable.Range(0, card.ChoiceRewards.Count).OrderByDescending(index => card.ChoiceRewards[index]?.OriginiumShard ?? 0).FirstOrDefault();
        }
        private bool NeedsMoveFuel(PlayerPlayProgress progress) => progress.Moves < (progress.Entrance == "B-01" ? 3 : 2) &&
            State.FindPlayer(progress.PlayerId).Resources.OriginiumShard < new TravelCostService(new MapQueryService(StaticMapDefinitions.Resolve(State.MapId))).GetCityMoveBaseCost().OriginiumShard;
        private int EnterpriseCharacterPriority(string definition) =>
            RequestedCharacterPriority(definition) < 0 ? -200 :
                campaignGame?.Mode?.Contains("enterprise") == true && (definition == "mountain" || definition == "mlynar") ? -100 : 0;
        private PlayerPlayProgress PlayProgress(int actor)
        {
            if (!playerPlay.TryGetValue(actor, out var progress))
                playerPlay[actor] = progress = new PlayerPlayProgress { PlayerId = actor };
            string city = State.FindPlayer(actor)?.CityLocationId;
            if (!string.IsNullOrEmpty(city))
            {
                if (progress.Entrance.Length == 0) progress.Entrance = city;
                progress.VisitedCities.Add(city);
                if (StaticMapDefinitions.Resolve(State.MapId).Locations.Any(location => location.LocationId == city && location.IsRedZone))
                    progress.RedZoneReached = true;
            }
            return progress;
        }
        private void ObservePlayerPlayPositions()
        {
            foreach (var player in State.Players) PlayProgress(player.PlayerId);
        }
        private string PlannedMainAction(PlayerPlayProgress progress)
        {
            int goal = progress.Entrance == "B-01" ? 3 : 2;
            if (State.Round <= 3)
            {
                if (progress.Moves < goal && !State.FindPlayer(progress.PlayerId).HasMovedCityThisRound)
                    return NeedsMoveFuel(progress) ? "explore" : "move";
                if (progress.Entrance != "B-01" && progress.Explorations == 0 &&
                    (progress.Moves >= goal || State.Round == 3)) return "explore";
            }
            else if (!progress.RedZoneReached)
            {
                var view = CurrentPaymentView();
                if (view != null && new RouteResourceSelectionService().TryCreateExplorationPreview(
                    GameStateViewProjector.ToClientState(view), view, progress.PlayerId,
                    out var selection, out _, out var projection).IsValid && PlanExploration(selection, projection, true) != null)
                    return "explore";
                if (!State.FindPlayer(progress.PlayerId).HasMovedCityThisRound) return "move";
            }
            return "build";
        }
        private void RecordPlayCommand(GameCommand command, WorkflowSubmissionResult result)
        {
            if (!CampaignMode || result?.CommandResult?.Succeeded != true || !result.AppliedLocally ||
                !new[] { GameCommandKind.MoveCity, GameCommandKind.ExploreLocation,
                    GameCommandKind.DeployInfluence, GameCommandKind.UseSpecialAction }.Contains(command.Kind) ||
                !recordedPlayCommands.Add(command.CommandId)) return;
            var progress = PlayProgress(command.PlayerId);
            switch (command.Kind)
            {
                case GameCommandKind.MoveCity: progress.Moves++; break;
                case GameCommandKind.ExploreLocation:
                    progress.Explorations++;
                    if (StaticMapDefinitions.Resolve(State.MapId).Locations.Any(location => location.IsRedZone && location.LocationId == command.TargetId))
                        progress.RedZoneReached = true;
                    explorationPlayPlan = null;
                    break;
                case GameCommandKind.DeployInfluence: progress.Deployments++; break;
                case GameCommandKind.UseSpecialAction: progress.SpecialActions++; break;
            }
            Append("逐玩家行动计划.jsonl", new { State.GameId, State.Round, State.ActionRound, Policy = PlayPolicyVersion,
                command.CommandId, command.Kind, command.TargetId, Progress = progress,
                Evidence = "仅计正式命令准确成功并Applied的次数；不按按钮点击或旧局覆盖推断" });
        }
        private int DistanceToRed(string source)
        {
            var map = new MapQueryService(StaticMapDefinitions.Resolve(State.MapId));
            var distances = new Dictionary<string, int> { [source] = 0 };
            var queue = new Queue<string>(); queue.Enqueue(source);
            while (queue.Count > 0)
            {
                string id = queue.Dequeue();
                if (map.GetLocation(id).IsRedZone) return distances[id];
                foreach (var target in map.GetAdjacentLocations(id))
                    if (!distances.ContainsKey(target.LocationId))
                    { distances[target.LocationId] = distances[id] + 1; queue.Enqueue(target.LocationId); }
            }
            return 1000;
        }
        private MapButtonView ChooseCampaignMoveTarget(IEnumerable<MapButtonView> candidates)
        {
            var progress = PlayProgress(LocalPlayer);
            var map = StaticMapDefinitions.Resolve(State.MapId);
            var legal = candidates.Where(target => State.Round >= 4 || !map.Locations.Any(location => location.LocationId == target.TargetId && location.IsRedZone));
            var target = legal.OrderBy(button => DistanceToRed(button.TargetId))
                .ThenBy(button => progress.VisitedCities.Contains(button.TargetId) ? 1 : 0)
                .ThenBy(button => State.Map.ResourceTokens.Any(token => token.LocationId == button.TargetId) ? 1 : 0)
                .ThenBy(button => IsMapTargetVisible(button) ? 0 : 1).ThenBy(button => button.TargetId).FirstOrDefault();
            if (target != null) Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                Policy = PlayPolicyVersion, Action = "移城目标", From = State.FindPlayer(LocalPlayer).CityLocationId,
                To = target.TargetId, RedDistance = DistanceToRed(target.TargetId),
                Reason = "只排序当前真实交互高亮，沿正式地图最短跳数接近红区，优先未到访且未开矿的地点；屏外走原地图导航" });
            return target;
        }
        private ExplorationPlayPlan PlanExploration(RouteSelectionSession selection, RouteSelectionProjection projection, bool requireRed)
        {
            if (selection == null || projection?.Validation?.IsValid != true) return null;
            var map = StaticMapDefinitions.Resolve(State.MapId);
            return NearestAffordableExploration(selection.StartLocationId, projection.RouteOptions,
                selection.SelectableLocationUpperBound.Where(id => !requireRed || map.Locations.Any(location => location.LocationId == id &&
                    location.IsRedZone && !RedZoneAccessRule.IsClosed(State, map, location))).ToArray(),
                Math.Min(selection.StartGoldVoucherAmount, projection.Budget.AvailableSelectionGoldVoucherAmount));
        }
        private static ExplorationPlayPlan NearestAffordableExploration(string start,
            IReadOnlyList<RouteSelectionRouteOption> routes, IReadOnlyList<string> targetIds, int budget)
        {
            // 探索须从城市沿至少一条航道到达资源点；起点不能作为零跳目标。
            var targets = new HashSet<string>(targetIds.Where(id => id != start));
            var queue = new List<ExplorationPlayPlan> { new ExplorationPlayPlan { Target = start } };
            var bestHops = new Dictionary<string, int> { [start + ":0"] = 0 };
            while (queue.Count > 0)
            {
                var current = queue.OrderBy(plan => plan.Hops).ThenBy(plan => plan.GoldCost).ThenBy(plan => plan.Target).First();
                queue.Remove(current);
                if (targets.Contains(current.Target)) return current;
                foreach (var route in routes.Where(option => option.CoveredLocationIds.Contains(current.Target) && !current.Routes.Contains(option.RouteId)))
                {
                    int cost = current.GoldCost + route.TollGoldVoucherAmount;
                    if (cost > budget) continue;
                    foreach (string next in route.CoveredLocationIds)
                    {
                        string key = next + ":" + cost;
                        int hops = current.Hops + 1;
                        if (bestHops.TryGetValue(key, out int previous) && previous <= hops) continue;
                        bestHops[key] = hops;
                        queue.Add(new ExplorationPlayPlan { Target = next, GoldCost = cost, Hops = hops,
                            Routes = current.Routes.Concat(new[] { route.RouteId }).ToList() });
                    }
                }
            }
            return null;
        }
        private IEnumerator CampaignAction()
        {
            int actor = LocalPlayer;
            string actionWindow = State.Round + ":" + State.ActionRound + ":" + actor;
            yield return Press(MarkedButton("action.tab.1"), "整局正常快捷行动");
            var rows = FindObjectsOfType<PlayerAutomationId>().Where(v => v.Id.StartsWith("action.character.") &&
                v.GetComponent<Button>()?.IsInteractable() == true && !campaignActionAttempts.Contains(actionWindow + ":" + v.Id))
                .OrderBy(v => EnterpriseCharacterPriority(State.ItemZones.Items.FirstOrDefault(i => "action.character." + i.ItemId == v.Id)?.DefinitionId))
                .ThenBy(v => -CampaignDefinitionMissing(State.ItemZones.Items.FirstOrDefault(i => "action.character." + i.ItemId == v.Id)?.DefinitionId)).ToArray();
            if (rows.Length > 0)
            {
                var row = rows[0]; campaignActionAttempts.Add(actionWindow + ":" + row.Id);
                yield return Press(row.GetComponent<Button>(), "整局本人正式角色候选");
                var optionalAmiya = hud.GetComponentsInChildren<SettlementEffectChipView>().FirstOrDefault(c =>
                    (Read(c, "option") as SettlementOptionView)?.SourceDefinitionId == "infected.character.amiya" &&
                    (bool)Read(c, "executionAllowed") && !(bool)Read(c, "pending"));
                // 仅合并修补明确标记后走真实来源；旧版不回答未经批准的额外顺序页。
                if (optionalAmiya != null && Argument("--yc-amiya-original-viewer=") == "true" &&
                    campaignActionAttempts.Add(actionWindow + ":amiya-insertion"))
                    yield return PointerSequence(optionalAmiya.gameObject, "正常阿米娅可选插入来源");
                yield return CampaignModeChoice();
                yield break;
            }
            var declarationButton = hud.ActionPanelView.DeclareCityStyleButton;
            yield return Press(MarkedButton("action.tab.2"), "整局正常城市样式页签");
            if (declarationButton.IsActive() && declarationButton.IsInteractable() && campaignActionAttempts.Add(actionWindow + ":declare:" +
                State.FindPlayer(actor).DeclaredCityStyles.Count + ":" + State.Map.Facilities.Count(f => f.PlayerId == actor)))
            {
                yield return Press(declarationButton, "整局正常城市样式宣告入口");
                yield return CampaignDeclareStyle(); yield break;
            }
            if (mobile.CanEndCurrentAction())
            { yield return Press(hud.Frame.EndActionButton, "整局正常主要行动完成后结束"); yield break; }
            yield return Press(MarkedButton("action.tab.0"), "整局正常主要行动");
            var progress = PlayProgress(actor);
            string planned = PlannedMainAction(progress);
            Append("决策变化.jsonl", new { State.GameId, State.Round, State.ActionRound, PlayerId = actor,
                Policy = PlayPolicyVersion, Progress = progress, Planned = planned,
                Reason = "前三回合按玩家完成两次移城加一次探索；B-01改三次移城；第四回合优先进红区，其余建设，不能建再部署" });
            if (planned == "move" && hud.ActionPanelView.MoveCityButton.IsInteractable() &&
                campaignActionAttempts.Add(actionWindow + ":policy-move"))
            { yield return BeginCampaignMove(); yield break; }
            if (planned == "explore" && hud.ActionPanelView.ExploreButton.IsInteractable() &&
                campaignActionAttempts.Add(actionWindow + ":policy-explore"))
            {
                yield return Press(hud.ActionPanelView.ExploreButton, "按资源计划正常探索");
                Require(Route?.IsActive == true, "正常探索入口未形成实际地图草稿");
                explorationPlayPlan = PlanExploration(Route.Session, Route.Projection, State.Round >= 4 && !progress.RedZoneReached);
                yield break;
            }
            if (State.Round >= 4 && !progress.RedZoneReached)
                Append("未覆盖原因.jsonl", new { State.GameId, State.Round, PlayerId = actor, Action = "第四回合红区准备",
                    Planned = planned, Reason = "本次正式资格或资源不允许计划行动；保留缺口，正常建设/部署，不强行进入" });
            int remainingBaseActions = (State.MaxRounds - State.Round) * 2 + (State.Phase == GamePhase.ActionRound1 ? 2 : 1);
            bool spareForSpecial = State.Round >= 4 && progress.RedZoneReached &&
                progress.SpecialActions < 2 && remainingBaseActions > Math.Max(0, 11 - progress.Builds);
            if (spareForSpecial && hud.ActionPanelView.SpecialButton.IsInteractable() && campaignActionAttempts.Add(actionWindow + ":special"))
            {
                yield return Press(hud.ActionPanelView.SpecialButton, "整局正常特殊行动入口");
                int before = commands.Count, revision = State.EffectRuntime.StateRevision;
                yield return NormalChoice();
                Require(commands.Count == before + 1 && State.EffectRuntime.StateRevision > revision,
                    "特殊行动当前选项与首次确认未获得准确完成回执");
                yield break;
            }
            // 复用正常建设/付款/放置与部署控件流程，供应没出现目标仍完成本局。
            yield return SalesAction();
        }
        private IEnumerator BeginCampaignMove()
        {
            preparedMainAction = new PreparedMainActionContinuation { GameId = State.GameId, Kind = "city_move",
                PlayerId = LocalPlayer, Round = State.Round, ActionRound = State.ActionRound,
                StartCity = CurrentPaymentView()?.Players.FirstOrDefault(player => player.PlayerId == LocalPlayer)?.CityLocationId };
            int before = commands.Count;
            yield return Press(hud.ActionPanelView.MoveCityButton, "按事件资源与红区路线计划进入正常移城");
            Require(!commands.Skip(before).Any(record => (Read(record, "Command") as GameCommand)?.Kind == GameCommandKind.MoveCity),
                "主要移城入口尚未地图两击，却已提交最终移动");
            RecordVisiblePaymentSummary();
        }
        private IEnumerator CampaignDeclareStyle()
        {
            var dialog = Read(Read(mobile, "workflowView"), "cityStyleDeclarationDialog");
            var model = Read(dialog, "model") as CityStyleOptionsViewModel;
            var view = Read(dialog, "view") as CityStyleDeclarationPreviewView;
            Require(model != null && view != null, "正常宣告入口没有显示当前样式页面");
            var available = model.CityBoardSlots.Where(s => !s.Used && !string.IsNullOrEmpty(s.FacilityId)).Select(s => s.SlotIndex).ToArray();
            var temporarySlots = model.CityBoardSlots.Where(s => !s.Used && !string.IsNullOrEmpty(s.FacilityId) &&
                FacilityCardDatabase.Get(State, s.FacilityId)?.FacilityId == "infected.facility.temporary_settlement")
                .Select(s => s.SlotIndex).ToArray();
            string chosen = null; int[] selected = null;
            playerStyleTargets.TryGetValue(LocalPlayer, out string plannedStyle);
            playerStyleLayouts.TryGetValue(LocalPlayer, out var reservedSlots);
            foreach (bool includeTemporary in TemporaryChoiceNeeded && temporarySlots.Length > 0 ? new[] { true, false } : new[] { false })
            {
                foreach (var option in model.Options.Where(o => o.CanDeclare)
                    .OrderBy(o => o.CityStyleId == plannedStyle ? 0 : 1).ThenBy(o => CampaignPriorityCount("style:" + o.CityStyleId + ":declare")))
                {
                    var definition = State.ContentDefinitions.CityStyles.FirstOrDefault(s => s.CityStyleId == option.CityStyleId);
                    int count = definition?.DeclarationRequirement?.RequiredFacilityCount ?? 0;
                    foreach (int actualCount in count > 0 ? new[] { count } : Enumerable.Range(1, available.Length))
                    {
                        foreach (var combination in Combinations(available, actualCount))
                        {
                            if (includeTemporary && !combination.Intersect(temporarySlots).Any()) continue;
                            if ((includeTemporary || option.CityStyleId == plannedStyle || reservedSlots == null || !combination.Intersect(reservedSlots).Any()) &&
                                model.ValidateSelection?.Invoke(option.CityStyleId, combination)?.CanConfirm == true)
                            { chosen = option.CityStyleId; selected = combination; break; }
                        }
                        if (chosen != null) break;
                    }
                    if (chosen != null) break;
                }
                if (chosen != null) break;
            }
            if (chosen == null)
            { Append("未覆盖原因.jsonl", new { State.GameId, State.Round, Action = "城市样式宣告", Reason = "本人正式样式与设施条件尚不匹配，继续合法行动", model.Options });
                yield return Press(view.CloseButton, "当前宣告不可达，正常关闭并继续"); yield break; }
            Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                Action = "按本局明确缺口选择正式宣告组合", TemporaryChoiceNeeded, TemporarySlots = temporarySlots,
                ChosenStyle = chosen, SelectedSlots = selected, IncludesTemporary = selected.Intersect(temporarySlots).Any(),
                Reason = "优先使用含未用临时聚居点且正式验证可宣告的组合；不存在时沿原布局继续，不改条件或槽位" });
            var card = view.GetComponentsInChildren<BuildInfoItemView>().First(v => v.name == chosen);
            yield return PointerSequence(card.gameObject, "正常选择待覆盖城市样式" + chosen);
            foreach (var index in selected)
            {
                var handler = view.GetComponentsInChildren<CityStyleDeclarationSlotPointerHandler>().Single(h => (int)Read(h, "slotIndex") == index);
                yield return PointerSequence(handler.gameObject, "正常宣告选择本人设施槽" + index);
            }
            Require((bool)Read(dialog, "CanConfirm"), "正式组合有效但真实宣告控件不能确认");
            yield return Press(view.ConfirmDeclarationButton, "正常城市样式首次宣告确认");
            if (chosen == plannedStyle || reservedSlots != null && selected.Intersect(reservedSlots).Any())
            { playerStyleTargets.Remove(LocalPlayer); playerStyleLayouts.Remove(LocalPlayer); }
        }
        private static IEnumerable<int[]> Combinations(int[] values, int count, int start = 0)
        {
            if (count == 0) { yield return Array.Empty<int>(); yield break; }
            if (count < 0) yield break;
            for (int i = start; i <= values.Length - count; i++)
                foreach (var rest in Combinations(values, count - 1, i + 1)) yield return new[] { values[i] }.Concat(rest).ToArray();
        }

        [Serializable] private sealed class CoverageState
        {
            public string ContentHash;
            public Dictionary<string, int> Completed = new Dictionary<string, int>();
            public List<string> GameIds = new List<string>();
        }
        private CoverageState coverage = new CoverageState();
        private string coveragePath;
        private readonly HashSet<string> recordedThisGame = new HashSet<string>();
        private readonly HashSet<string> attemptedCharacterUses = new HashSet<string>();
        private string lastBuildItem, lastBuildDefinition;
        private bool lastBuildRecorded;
        private readonly Dictionary<string, float> stripArrivals = new Dictionary<string, float>();
        private bool stripObserved;
        private void LateUpdate()
        {
            if (!SalesMode || hud == null || finished) return;
            foreach (var strip in hud.GetComponentsInChildren<SettlementEffectStripView>())
            foreach (var chip in strip.GetComponentsInChildren<SettlementEffectChipView>())
            {
                var option = Read(chip, "option") as SettlementOptionView;
                if (option == null) continue;
                string key = strip.WindowId + ":" + option.OptionId;
                if (!stripArrivals.TryGetValue(key, out float began))
                {
                    began = Time.realtimeSinceStartup; stripArrivals[key] = began;
                    StartCoroutine(CaptureNaturalStrip(key));
                }
                if (Time.realtimeSinceStartup - began > .8f) continue;
                var visual = ((CanvasGroup)Read(chip, "visualGroup"))?.transform as RectTransform;
                var viewport = Read(strip, "viewport") as RectTransform;
                var group = Read(chip, "visualGroup") as CanvasGroup;
                Append("结算条自然入场几何.jsonl", new { State.GameId, Frame = Time.frameCount, Elapsed = Time.realtimeSinceStartup - began,
                    strip.WindowId, option.OptionId, Strip = ComponentPath(strip), StripRect = UiRectDetails(strip.transform as RectTransform),
                    ViewportRect = UiRectDetails(viewport), ViewportPosition = UiAnchoredPosition(viewport), ChipRect = UiRectDetails(chip.transform as RectTransform),
                    ChipPosition = UiAnchoredPosition(chip.transform as RectTransform), VisualPosition = UiAnchoredPosition(visual), Alpha = group?.alpha,
                    HeaderReservedWidth = Read(strip, "headerReservedWidth"), Inset = Read(strip, "horizontalInset"),
                    HeaderActive = (Read(strip, "header") as GameObject)?.activeInHierarchy, Screen.width, Screen.height });
                stripObserved = true;
            }
        }
        private IEnumerator CaptureNaturalStrip(string key)
        {
            yield return new WaitForEndOfFrame();
            if (!finished) SaveCapture("结算条自然入场-初帧");
            yield return new WaitForSecondsRealtime(.1f); yield return new WaitForEndOfFrame();
            if (!finished) SaveCapture("结算条自然入场-约100毫秒");
            yield return new WaitForSecondsRealtime(.3f); yield return new WaitForEndOfFrame();
            if (!finished) SaveCapture("结算条自然入场-约400毫秒");
        }
        private bool TargetPagesCovered => CoverageCount("page:cannot-sale") > 0 && CoverageCount("page:trade-sale") > 0;
        private int CoverageCount(string key) => CampaignMode ? CampaignCount(key) : coverage.Completed.TryGetValue(key, out int count) ? count : 0;
        private int CoveragePriorityCount(string key) => CampaignMode ? CampaignPriorityCount(key) : CoverageCount(key);
        private Button MarkedButton(string id) => FindObjectsOfType<PlayerAutomationId>().FirstOrDefault(v => v.Id == id)?.GetComponent<Button>();
        private Button ChoiceExecuteButton() => FindObjectsOfType<UiEffectRowView>()
            .Where(v => v.GetComponentInParent<EffectDialogShellView>() != null && Read(v, "mode") is UiEffectRowView.Mode mode && mode == UiEffectRowView.Mode.ChoiceExecute)
            .OrderBy(v => v.ItemId == "continue" ? -1 : v.ItemId == "decline" ? 1 : 0)
            .ThenBy(v => CampaignMode ? CampaignChoicePriority(v.ItemId) : 0)
            .Select(v => Read(v, "statusButton") as Button).FirstOrDefault(b => b != null && b.IsActive() && b.IsInteractable());
        private int CampaignChoicePriority(string id)
        {
            var style = State.ContentDefinitions.CityStyles.FirstOrDefault(s => !string.IsNullOrEmpty(s.SpecialActionId) &&
                id != null && id.EndsWith(":" + s.SpecialActionId, StringComparison.Ordinal));
            return style == null ? 0 : CampaignPriorityCount("style:" + style.CityStyleId + ":effect");
        }
        private int CampaignSpecialOptionPriority(string rowName)
        {
            const string prefix = "Special Action Option ";
            if (!CampaignMode || rowName == null || !rowName.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(rowName.Substring(prefix.Length), out int index)) return 0;
            var presenter = Read(mobile, "turnActionPresenter") as TurnActionPresenter;
            var options = presenter?.CityStyleInteraction.QuerySpecialActions().Options.ToArray();
            Require(options != null && index >= 0 && index < options.Length,
                "特殊行动实际选项行与本人可见查询不一致");
            var option = options[index];
            return CampaignChoicePriority(option.DeclarationMarkerId + ":" + option.SpecialActionId);
        }
        private void LoadCoverage()
        {
            coveragePath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--yc-ui-coverage-path="))?.Substring("--yc-ui-coverage-path=".Length);
            Require(!string.IsNullOrEmpty(coveragePath) && Path.IsPathRooted(coveragePath), "多局覆盖记录路径必须明确");
            if (File.Exists(coveragePath)) coverage = JsonConvert.DeserializeObject<CoverageState>(File.ReadAllText(coveragePath)) ?? new CoverageState();
            Require(string.IsNullOrEmpty(coverage.ContentHash) || coverage.ContentHash == State.ContentPackHash, "不同内容版本不得合并覆盖记录");
            coverage.ContentHash = State.ContentPackHash;
            Write("开局覆盖与缺口.json", new { State.GameId, State.ContentPackHash, State.EventDeckSeed, coverage,
                Required = new[] { "page:cannot-sale", "page:trade-sale" }, Decision = "只在当前本人可见合法候选中优先补未覆盖项；目标未出现继续本局" });
        }
        private void Covered(string key)
        {
            if (CampaignMode) { CampaignCompleted(key, new { Boundary = "实际控件确认的附加行动", LastCommand = commands.LastOrDefault() }); return; }
            if (!recordedThisGame.Add(key)) return;
            coverage.Completed[key] = CoverageCount(key) + 1;
            Append("实际完成覆盖.jsonl", new { State.GameId, State.Round, LocalPlayer, Key = key, Revision = State.EffectRuntime.StateRevision });
        }
        private void UpdateCoverage()
        {
            foreach (var use in State.CharacterUses)
            {
                string definition = State.ItemZones.Items.FirstOrDefault(i => i.ItemId == use.CardInstanceId)?.DefinitionId;
                foreach (var mode in use.CompletedModes)
                    if (!(definition == "cannot" && mode == "strategy" && CoverageCount("page:cannot-sale") == 0))
                        Covered("character:" + definition + ":" + mode);
            }
            // 建设覆盖只在实际放置控件确认后登记；开局已有设施不是建设操作证据。
            if (!coverage.GameIds.Contains(State.GameId)) coverage.GameIds.Add(State.GameId);
            File.WriteAllText(coveragePath, JsonConvert.SerializeObject(coverage, Formatting.Indented));
            var universe = State.ContentDefinitions.Characters.SelectMany(c => new[] { "character:" + c.TemplateId + ":strategy", "character:" + c.TemplateId + ":tactic" })
                .Concat(State.ContentDefinitions.Facilities.Select(f => "build:" + f.FacilityId))
                .Concat(new[] { "cannot-sale", "trade-sale" }.SelectMany(s => new[] { "originium", "originium-shard", "iron", "pure-originium" }
                    .Select(r => "page:" + s + ":resource:" + r)))
                .Concat(new[] { "page:cannot-sale", "page:trade-sale", "action:deploy", "action:move", "action:explore", "action:harvest" }).Distinct().ToArray();
            var missing = universe.Where(k => CoverageCount(k) == 0).ToArray();
            Write("本局覆盖与后续决策.json", new { State.GameId, State.ContentPackHash, State.EventDeckSeed, State.Round, State.Phase,
                ActuallyCompleted = recordedThisGame.OrderBy(k => k).ToArray(), Cumulative = coverage.Completed,
                Missing = missing, TargetPagesCovered,
                NextPriorities = missing.OrderBy(k => k.StartsWith("page:") ? 0 : k == "build:sell_resources" ? 1 : 2).ToArray(),
                DecisionBoundary = "未覆盖不是通过；不能注入候选或改随机。未出贸易街区时优先建设实际可达未覆盖设施，正常补牌后重新观察。" });
        }
        private static bool MatchesStyleColors(FacilityCardDefinition card, IList<string> colors) =>
            colors == null || colors.Count == 0 || card != null && card.Color.Split(new[] { ',', '/', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(color => string.Equals(color, "rainbow", StringComparison.OrdinalIgnoreCase) || colors.Any(allowed =>
                    string.Equals(color.Trim(), allowed.Trim(), StringComparison.OrdinalIgnoreCase)));
        private StylePlacementPlan PlanStylePlacement(string facilityId, int slot)
        {
            int revision = State.EffectRuntime.StateRevision;
            if (stylePlacementRevision != revision) { stylePlacementCache.Clear(); stylePlacementRevision = revision; }
            string key = LocalPlayer + ":" + facilityId + ":" + slot;
            if (!stylePlacementCache.TryGetValue(key, out var plan)) stylePlacementCache[key] = plan = CalculateStylePlacement(facilityId, slot);
            return plan;
        }
        private StylePlacementPlan CalculateStylePlacement(string facilityId, int slot)
        {
            var added = FacilityCardDatabase.Get(State, facilityId);
            var owner = State.FindPlayer(LocalPlayer);
            var best = new StylePlacementPlan { Slot = slot };
            if (added == null || owner == null) return best;
            var occupied = State.Map.Facilities.Where(facility => facility.PlayerId == LocalPlayer)
                .GroupBy(facility => facility.CityBoardSlotIndex).ToDictionary(group => group.Key, group => group.First());
            var used = new HashSet<string>(owner.DeclaredCityStyles.SelectMany(declaration => declaration.UsedFacilityIds));
            foreach (string styleId in State.Decks.CityStyleSupply)
            {
                var style = CityStyleDatabase.Get(State, styleId);
                if (style == null || owner.DeclaredCityStyles.Count(declaration => declaration.CityStyleId == style.CityStyleId && declaration.MarkerArea.Length > 0) >= style.MaxDeclarationsPerPlayer)
                    continue;
                var requirement = style.DeclarationRequirement;
                if (requirement == null) continue;
                bool Eligible(FacilityCardDefinition card, int index) => card != null &&
                    (requirement.RequiredCityBoardSlotIndexes.Count == 0 || requirement.RequiredCityBoardSlotIndexes.Contains(index)) &&
                    (requirement.RequiredResourceTypes.Count == 0 || requirement.RequiredResourceTypes.Any(resource => card.ResourceCost.Get(resource) > 0));
                if (!Eligible(added, slot)) continue;
                var cells = requirement.RequiredPatternCells;
                if (cells == null || cells.Count == 0) continue;
                int columns = BuildFacilityService.CityBoardSlotCountPerRow, rows = BuildFacilityService.CityBoardSlotCount / columns;
                for (int rotation = 0; rotation < 4; rotation++)
                for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                {
                    bool valid = true, includesAdded = false;
                    int matched = 0;
                    var patternSlots = new List<int>();
                    var matchingCards = new List<FacilityCardDefinition>();
                    foreach (var cell in cells)
                    {
                        int dr = cell.RowOffset, dc = cell.ColumnOffset;
                        for (int turn = 0; turn < rotation; turn++) { int previous = dr; dr = dc; dc = -previous; }
                        int targetRow = row + dr, targetColumn = column + dc;
                        if (targetRow < 0 || targetRow >= rows || targetColumn < 0 || targetColumn >= columns)
                        { valid = false; break; }
                        int index = targetRow * columns + targetColumn;
                        patternSlots.Add(index);
                        FacilityCardDefinition card = null;
                        if (index == slot) { card = added; includesAdded = true; }
                        else if (occupied.TryGetValue(index, out var placed))
                        {
                            if (used.Contains(placed.ContentInstanceId)) { valid = false; break; }
                            card = FacilityCardDatabase.Get(State, placed.FacilityCardId);
                        }
                        if (card == null) continue;
                        if (!Eligible(card, index) || !MatchesStyleColors(card, cell.AllowedFacilityColors)) { valid = false; break; }
                        matched++; matchingCards.Add(card);
                    }
                    if (!valid || !includesAdded || requirement.RequireSameCityBoardRow && patternSlots.Select(index => index / columns).Distinct().Count() != 1) continue;
                    int missing = cells.Count - matched;
                    int effectFit = requirement.RequiredEffectTypes.Count(effect => matchingCards.Any(card => card.EffectType == effect));
                    int score = (CampaignPriorityCount("style:" + style.CityStyleId + ":declare") == 0 ? 10000 : 0) +
                        cells.Count * 500 + matched * 300 - missing * 50 + effectFit * 20 + (missing == 0 ? 1000 : 0);
                    if (playerStyleTargets.TryGetValue(LocalPlayer, out string targetStyle) && targetStyle == style.CityStyleId &&
                        !owner.DeclaredCityStyles.Any(declaration => declaration.CityStyleId == targetStyle)) score += 4000;
                    if (score > best.Score) best = new StylePlacementPlan { StyleId = style.CityStyleId, Slot = slot,
                        Score = score, Matched = matched, Required = cells.Count, Rotation = rotation * 90, PatternSlots = patternSlots.ToArray() };
                }
            }
            return best;
        }
        private StylePlacementPlan BestStylePlacement(BuildFacilityOptionQueryResult option) => option.SlotOptions.Where(slot => slot.IsLegal)
            .Select(slot => PlanStylePlacement(option.FacilityId, slot.CityBoardSlotIndex)).OrderByDescending(plan => plan.Score).ThenBy(plan => plan.Slot).FirstOrDefault();
        private IEnumerator SalesAction()
        {
            int actor = LocalPlayer;
            if (mobile.CanEndCurrentAction())
            {
                yield return Press(hud.Frame.EndActionButton, "正常完成主要行动后结束"); yield break;
            }
            yield return Press(MarkedButton("action.tab.1"), "正常快捷行动页签");
            var rows = FindObjectsOfType<PlayerAutomationId>().Where(v => v.Id.StartsWith("action.character.") && v.GetComponent<Button>()?.IsInteractable() == true)
                .OrderBy(v => State.ItemZones.Items.FirstOrDefault(i => "action.character." + i.ItemId == v.Id)?.DefinitionId == "cannot" ? -1000 :
                    CoverageCount("character:" + State.ItemZones.Items.FirstOrDefault(i => "action.character." + i.ItemId == v.Id)?.DefinitionId + ":strategy")).ToArray();
            foreach (var row in rows)
            {
                string item = row.Id.Substring("action.character.".Length);
                string key = State.Round + ":" + State.ActionRound + ":" + actor + ":" + item;
                if (!attemptedCharacterUses.Add(key)) continue;
                var definition = State.ItemZones.Items.First(i => i.ItemId == item).DefinitionId;
                Append("决策变化.jsonl", new { State.Round, PlayerId = actor, Action = "角色策略", Definition = definition,
                    PreviousCoverage = CoverageCount("character:" + definition + ":strategy"), Reason = definition == "cannot" ? "正常候选含待测坎诺特" : "目标未覆盖时同时补其他可达角色策略" });
                yield return Press(row.GetComponent<Button>(), "正常使用角色" + definition);
                var viewer = FindObjectsOfType<CardViewer>().FirstOrDefault(v => v.StrategyButton.IsActive() && v.StrategyButton.IsInteractable());
                Require(viewer != null, "正常角色使用没有可选策略卡面");
                yield return Press(viewer.StrategyButton, "正常选择角色策略" + definition);
                yield break;
            }
            yield return Press(MarkedButton("action.tab.0"), "正常主要行动页签");
            var options = new BuildFacilityOptionQueryService().Query(State, actor);
            string buildWindow = State.Round + ":" + State.ActionRound + ":" + actor + ":";
            var legal = options.Where(o => BuildFacilitySelectionController.CanPrepareOption(o) && o.PaymentOptions.Any(payment => payment.IsAvailable) && o.SlotOptions.Any(slot => slot.IsLegal) &&
                !attemptedBuildPreparations.Contains(buildWindow + o.FacilityId))
                .OrderBy(o => RequestedBuildPriority(o.DefinitionId))
                .ThenBy(o => o.PaymentOptions.Any(payment => payment.IsAvailable) ? 0 : 1)
                .ThenBy(o => FacilityCardDatabase.Get(State, o.FacilityId)?.EffectId == "sell_resources" && CoveragePriorityCount("page:trade-sale") == 0 ? -1 : 0)
                .ThenByDescending(o => CampaignMode ? BestStylePlacement(o)?.Score ?? 0 : 0)
                .ThenBy(o => CoveragePriorityCount("build:" + o.DefinitionId))
                .ThenBy(o => FacilityCardDatabase.Get(State, o.FacilityId)?.HasEntryEffect == true ? 1 : 0).ToArray();
            Append("供应与合法决策.jsonl", new { State.Round, PlayerId = actor,
                Supply = options.Select(o => new { o.FacilityId, o.DefinitionId, o.CanBuild,
                    CanPrepare = BuildFacilitySelectionController.CanPrepareOption(o), DirectPaymentAvailable = o.PaymentOptions.Any(payment => payment.IsAvailable), o.Reason }).ToArray(),
                MissingTrade = !options.Any(o => FacilityCardDatabase.Get(State, o.FacilityId)?.EffectId == "sell_resources"),
                Decision = legal.Length > 0 ? "建设未覆盖的合法设施，正常补充供应并推进" : "当前建设不可达，执行正常部署与采集继续积累" });
            if (legal.Length > 0 && hud.ActionPanelView.BuildButton.IsInteractable())
            {
                var chosen = legal[0];
                lastStylePlacement = CampaignMode ? BestStylePlacement(chosen) : null;
                if (CampaignMode) Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = actor,
                    Policy = PlayPolicyVersion, Action = "按正式样式条件选择设施与槽位", chosen.FacilityId, chosen.DefinitionId,
                    Plan = lastStylePlacement, Reason = "优先未覆盖复杂样式的颜色、旋转和空间组合；已使用设施与错误颜色占位会使该布局不可达" });
                attemptedBuildPreparations.Add(buildWindow + chosen.FacilityId);
                lastBuildItem = chosen.FacilityId; lastBuildDefinition = chosen.DefinitionId; lastBuildRecorded = false;
                yield return Press(hud.ActionPanelView.BuildButton, "正常建设入口");
                preparedMainAction = new PreparedMainActionContinuation { GameId = State.GameId, Kind = "build", TargetId = chosen.FacilityId,
                    PlayerId = actor, Round = State.Round, ActionRound = State.ActionRound };
                int before = commands.Count;
                yield return Press(MarkedButton("build.candidate." + chosen.FacilityId), "实际供应区选择" + chosen.DefinitionId);
                Require(!commands.Skip(before).Any(record => (Read(record, "Command") as GameCommand)?.Kind == GameCommandKind.BuildFacility),
                    "仅选择设施准备费用，不应已经提交最终建设");
                RecordVisiblePaymentSummary();
                yield break;
            }
            var influence = (YC.Domain.Influence.InfluenceService)Read(mobile, "influenceService");
            var deploySlots = influence.GetLegalPlacementSlotIds(State, actor);
            Append("决策变化.jsonl", new { State.Round, PlayerId = actor, Action = "主要行动合法性复核",
                LegalDeploySlots = deploySlots, Reason = deploySlots.Count == 0 ? "正式规则无合法部署位置，改选其他合法行动继续本局" : "按正式合法部署候选推进" });
            if (deploySlots.Count > 0 && hud.ActionPanelView.DeployButton.IsInteractable())
            {
                yield return Press(hud.ActionPanelView.DeployButton, "正常部署推进");
                string slot = ChooseDeploySlot(influence.GetLegalPlacementSlotIds(State, actor));
                yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, slot, "正常部署选择");
                yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, slot, "正常部署确认");
                Covered("action:deploy"); yield break;
            }
            string fallbackWindow = State.Round + ":" + State.ActionRound + ":" + actor;
            if (hud.ActionPanelView.ExploreButton.IsInteractable() && attemptedCharacterUses.Add(fallbackWindow + ":fallback-explore"))
            {
                yield return Press(hud.ActionPanelView.ExploreButton, "建设部署不可达后正常探索");
                Require(Route?.IsActive == true, "正常探索入口未形成正式地图草稿");
                yield break;
            }
            if (hud.ActionPanelView.DispatchButton.IsInteractable() && TryFindDispatch(out var source, out var target))
            { yield return DispatchAction(source, target); yield break; }
            if (hud.ActionPanelView.MoveCityButton.IsInteractable() && attemptedCharacterUses.Add(fallbackWindow + ":fallback-move"))
            {
                preparedMainAction = new PreparedMainActionContinuation { GameId = State.GameId, Kind = "city_move",
                    PlayerId = actor, Round = State.Round, ActionRound = State.ActionRound,
                    StartCity = CurrentPaymentView()?.Players.FirstOrDefault(player => player.PlayerId == actor)?.CityLocationId };
                yield return Press(hud.ActionPanelView.MoveCityButton, "其他主行动不可达后正常移城准备");
                RecordVisiblePaymentSummary(); yield break;
            }
            Require(false, "当前没有已适配的合法主行动，保留正常对局断点");
        }
        private bool TryFindDispatch(out string source, out string target)
        {
            var service = (YC.Domain.Influence.InfluenceService)Read(mobile, "influenceService");
            foreach (var item in State.Map.Influences.Where(value => value.PlayerId == LocalPlayer))
                foreach (var slot in service.GetLegalMovementSlotIds(State, item.InfluenceId))
                    if (service.CanMove(State, LocalPlayer, item.InfluenceId, slot).IsValid)
                    { source = item.SlotId; target = slot; return true; }
            source = target = null; return false;
        }
        private IEnumerator DispatchAction(string source, string target)
        {
            int before = commands.Count;
            yield return Press(hud.ActionPanelView.DispatchButton, "正常调度合法影响力");
            yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, source, "正常调度选择正式来源");
            yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, target, "正常调度选择正式目标并确认");
            Require(commands.Count == before + 1 && (Read(commands[before], "Command") as GameCommand)?.Kind == GameCommandKind.DispatchInfluence,
                "正常调度未准确提交一次正式调度命令");
            Covered("action:dispatch");
        }
        private IEnumerator BuildPlacement()
        {
            var request = Request;
            if (CampaignMode)
            {
                var facility = FacilityCardDatabase.Get(State, lastBuildItem);
                var visibleCopy = FindObjectsOfType<FacilityBuildDialogView>().SelectMany(v => v.GetComponentsInChildren<Text>())
                    .Where(t => t.gameObject.activeInHierarchy).Select(t => t.text).ToArray();
                Write("放置名称-" + State.Round + "-" + LocalPlayer + "-" + lastBuildDefinition + ".json", new {
                    State.GameId, Request = request, ItemId = lastBuildItem, Definition = lastBuildDefinition,
                    FormalName = facility?.Name, VisibleCopy = visibleCopy, ActualNameVisible = facility != null && visibleCopy.Any(t => t != null && t.Contains(facility.Name)),
                    RawDefinitionVisible = visibleCopy.Any(t => t != null && !string.IsNullOrEmpty(lastBuildDefinition) && t.Contains(lastBuildDefinition)) });
                if (lastBuildDefinition == "infected.facility.temporary_settlement") yield return Capture("临时聚居点实际放置名称");
            }
            var marker = FindObjectsOfType<PlayerAutomationId>().Where(v => v.Id.StartsWith("build.slot.") && v.GetComponent<Button>()?.IsInteractable() == true)
                .OrderByDescending(v => CampaignMode && int.TryParse(v.Id.Substring("build.slot.".Length), out int slot) ? PlanStylePlacement(lastBuildItem, slot).Score : 0)
                .ThenBy(v => v.Id).FirstOrDefault();
            Require(marker != null, "正式建设放置请求没有对应合法城市槽");
            if (CampaignMode && int.TryParse(marker.Id.Substring("build.slot.".Length), out int actualSlot))
            {
                lastStylePlacement = PlanStylePlacement(lastBuildItem, actualSlot);
                if (!string.IsNullOrEmpty(lastStylePlacement.StyleId))
                { playerStyleTargets[LocalPlayer] = lastStylePlacement.StyleId; playerStyleLayouts[LocalPlayer] = lastStylePlacement.PatternSlots; }
                Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                    Action = "实际放置页复核样式布局", Plan = lastStylePlacement, ActualControl = marker.Id });
            }
            yield return Press(marker.GetComponent<Button>(), "正常建设选择城市槽");
            yield return Press(MarkedButton("build.confirm"), "正常建设首次放置确认");
            yield return Capture("正常建设放置后");
            var placed = State.Map.Facilities.FirstOrDefault(f => f.ContentInstanceId == lastBuildItem);
            Require(placed != null, "建设首次放置后没有实际设施实例");
            if (CampaignMode && recordedPlayCommands.Add("built:" + lastBuildItem))
                PlayProgress(placed.PlayerId).Builds++;
            lastBuildRecorded = true; Covered("build:" + lastBuildDefinition);
            Write("建设来源-" + State.Round + "-" + LocalPlayer + "-" + lastBuildDefinition + ".json", new { ItemId = lastBuildItem, Definition = lastBuildDefinition, Placement = placed, SubmittedRequest = request });
        }
        private IEnumerator RequiredAllocation()
        {
            var request = Request;
            var confirm = FindObjectsOfType<Button>().FirstOrDefault(b => (b.name == "Confirm" || b.name == "Confirm Character Effect") && b.IsActive());
            Require(confirm != null, "数量请求没有实际确认按钮");
            var sourceItem = PaymentField(request, "sourceItemId")?.StringValue;
            bool markerQuantity = PaymentField(request, "amountKind")?.StringValue == "player_marker";
            var source = markerQuantity && !string.IsNullOrEmpty(sourceItem)
                ? CurrentPaymentView()?.ItemZones?.Items.FirstOrDefault(item => item.ItemId == sourceItem) : null;
            var maximum = PaymentField(request, "maxAmount");
            bool linLoad = CampaignMode && source?.DefinitionId == "infected.character.lin";
            if (markerQuantity && (source?.DefinitionId == "operator.passenger" || linLoad) &&
                maximum?.Kind == NormalizedValueKind.Integer && maximum.IntegerValue > 0)
            {
                var row = FindObjectsOfType<EffectDialogResourceRowView>().Single();
                int desired = (int)Math.Min(2, maximum.IntegerValue);
                var rowMaximums = PaymentField(request, "rowMaximums");
                if (rowMaximums?.Kind == NormalizedValueKind.Array && rowMaximums.Items.Count == 1 &&
                    rowMaximums.Items[0].Kind == NormalizedValueKind.Integer)
                    desired = Math.Min(desired, (int)rowMaximums.Items[0].IntegerValue);
                Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = LocalPlayer,
                    Action = linLoad ? "正常林计谋加载标记" : "正常异客加载授权标记", SourceItemId = sourceItem, Desired = desired,
                    Reason = linLoad ? "上一局两次合法选0未覆盖收尾付款；本次只用本人原数量加号加载实际可用的1至2枚标记，再正常推进收尾" :
                        "从本人物资池经实际数量控件取得合法标记，给后续支付保留替代机会；不注入标记或强造223/222" });
                Require(int.TryParse(row.ValueInput.text, out int amount), "原标记数量控件没有可读取的整数草稿");
                while (amount < desired)
                {
                    Require(row.IncreaseButton.IsInteractable(), "原标记数量加号在正式上限内不可用");
                    yield return Press(row.IncreaseButton, linLoad ? "正常林计谋加载一枚标记" : "正常异客加载一枚授权标记");
                    row = FindObjectsOfType<EffectDialogResourceRowView>().Single();
                    Require(int.TryParse(row.ValueInput.text, out int nextAmount) && nextAmount > amount,
                        "原标记数量加号没有推进实际草稿");
                    amount = nextAmount;
                }
                if (linLoad)
                {
                    yield return new WaitForEndOfFrame();
                    SaveCapture("林计谋原数量加载确认前");
                    Write("林原标记加载-" + request.InteractionId + ".json", new { State.GameId, State.Round,
                        Request = request, SourceItemId = sourceItem, Desired = desired, ActualDraft = amount,
                        OriginalRow = ComponentPath(row), State = Observe(), InjectedMarkers = false });
                }
            }
            for (int i = 0; i < 100 && !confirm.IsInteractable(); i++)
            {
                var row = FindObjectsOfType<EffectDialogResourceRowView>().FirstOrDefault(v => v.IncreaseButton.IsInteractable());
                Require(row != null, "正式数量请求无法满足最小合法数值");
                yield return Press(row.IncreaseButton, "正常数量选择增加");
            }
            yield return Press(confirm, "正常数量请求首次确认");
            Require(Request?.InteractionId != request?.InteractionId || State.EffectRuntime.StateRevision > request.StateRevision, "正式数量请求首次确认未推进");
        }
        private Button SaleConfirm() => FindObjectsOfType<Button>().FirstOrDefault(b => (b.name == "Confirm Character Effect" || b.name == "Confirm") && b.IsActive());
        private Button SaleFinish() => FindObjectsOfType<Button>().FirstOrDefault(b => (b.name == "Cancel Character Effect" || b.name == "Skip") && b.IsActive() && b.IsInteractable());
        private IEnumerator SaleInput(InputField input, string value, bool finishEdit)
        {
            Require(input != null && input.IsActive() && input.IsInteractable(), "出售数量输入框不可用");
            Append("控件事件.jsonl", new { Index = ++eventNumber, Stage = stage, Path = ComponentPath(input),
                Input = "实际InputField.onValueChanged/onEndEdit", Value = value });
            input.text = value;
            if (finishEdit) input.onEndEdit.Invoke(value);
            yield return Settle();
        }
        private IEnumerator SalePage()
        {
            var request = Request;
            int actor = LocalPlayer;
            string source = ResolveSaleSource(request);
            bool cannot = source == "cannot-sale";
            Require(source != "other-sale", "出售来源尚未由当前正式Effect身份识别，保留现场");
            string evidenceName = source + "-R" + State.Round + "-P" + actor + "-" + eventNumber;
            if (!cannot)
                Require(FacilityCardDatabase.Get(State, lastBuildItem)?.EffectId == "sell_resources" && lastBuildRecorded && State.Map.Facilities.Any(f => f.ContentInstanceId == lastBuildItem),
                    "设施出售请求没有正常建设贸易街区的来源证明");
            stage = (cannot ? "坎诺特策略" : "贸易街区") + "实际出售资源页面";
            var before = State.FindPlayer(actor).Resources.Clone();
            var rows = FindObjectsOfType<EffectDialogResourceRowView>().OrderBy(v => v.name).ToArray();
            Require(rows.Length == 4 && rows.All(v => v.TryValidateConfiguration(out _)), "出售页四种资源行引用不完整");
            var confirm = SaleConfirm();
            Require(confirm != null && !confirm.IsInteractable(), "出售页零数量仍允许确认");
            yield return Capture(source + "-初始数量与库存");
            int[] inventory = { before.Originium, before.OriginiumShard, before.Iron, before.PureOriginium };
            int[] amounts = PlannedSaleAmounts(before);
            int index = Array.FindIndex(amounts, amount => amount > 0);
            if (index < 0)
            {
                Append("未覆盖原因.jsonl", new { Source = source, State.Round, PlayerId = actor, ReserveShards = SaleFuelReserve(),
                    Reason = inventory.Any(amount => amount > 0) ? "现有碎片需保留为近期移城燃料，没有多余可售资源；正常结束并稍后补验" : "正常库存为0，未执行出售，继续本局积累资源" });
                yield return Press(SaleFinish(), "正常结束零库存出售"); yield break;
            }
            int beforeCommands = commands.Count;
            yield return Press(rows[index].IncreaseButton, "出售页增加一个数量");
            yield return Press(rows[index].DecreaseButton, "出售页减少到零");
            Require(!confirm.IsInteractable(), "出售数量回到零后确认仍可用");
            yield return SaleInput(rows[index].ValueInput, (inventory[index] + 1).ToString(), false);
            Require(!confirm.IsInteractable(), "超过本人库存的输入仍允许确认");
            yield return Capture(source + "-超库存输入不可确认");
            yield return SaleInput(rows[index].ValueInput, "-1", false);
            Require(!confirm.IsInteractable(), "负数出售输入仍允许确认");
            yield return SaleInput(rows[index].ValueInput, "0", true);
            for (int i = 0; i < rows.Length; i++)
                if (amounts[i] > 0) yield return SaleInput(rows[i].ValueInput, amounts[i].ToString(), true);
            Require(confirm.IsInteractable() && commands.Count == beforeCommands && before.Enumerate().All(p => State.FindPlayer(actor).Resources.Get(p.Key) == p.Value),
                "出售合法草稿未可确认或草稿编辑提前提交/扣款");
            int revenue = amounts[0] * ResourceSaleService.OriginiumUnitPrice + amounts[1] * ResourceSaleService.OriginiumShardUnitPrice +
                amounts[2] * ResourceSaleService.IronUnitPrice + amounts[3] * ResourceSaleService.PureOriginiumUnitPrice;
            yield return Capture(source + "-合法草稿首次确认前");
            Write(evidenceName + "-首次出售输入.json", new { State.GameId, State.Round, PlayerId = actor, Request = request, SourceFacilityItem = cannot ? null : lastBuildItem,
                SourceDefinition = cannot ? "cannot" : lastBuildDefinition, Before = before, Amounts = amounts, Revenue = revenue, ReserveShards = SaleFuelReserve(),
                Component = ComponentPath(rows[0].GetComponentInParent<EffectDialogShellView>()), Rows = rows.Select(v => new { Path = ComponentPath(v), Input = v.ValueInput.text, Label = v.Label.text, Decrease = v.DecreaseButton.IsInteractable(), Increase = v.IncreaseButton.IsInteractable() }).ToArray() });
            if (cannot && CoverageCount("page:cannot-sale:cancel-draft") == 0)
            {
                yield return Press(SaleFinish(), "坎诺特实际丢弃本批草稿并结束");
                Require(before.Enumerate().All(p => State.FindPlayer(actor).Resources.Get(p.Key) == p.Value), "取消出售草稿仍扣资源或增加金券");
                Covered("page:cannot-sale:cancel-draft");
                yield return Capture(source + "-取消草稿后"); yield break;
            }
            yield return Press(confirm, "出售首次确认，不补点");
            var after = State.FindPlayer(actor).Resources.Clone();
            Require(after.Originium == before.Originium - amounts[0] && after.OriginiumShard == before.OriginiumShard - amounts[1] &&
                after.Iron == before.Iron - amounts[2] && after.PureOriginium == before.PureOriginium - amounts[3] && after.GoldVoucher == before.GoldVoucher + revenue,
                "首次出售资源扣除或金券收入与草稿不符");
            Require(commands.Count == beforeCommands + 1, "首次出售没有恰好一次真实提交");
            yield return Capture(source + "-首次出售到账后");
            int completedBatches = 1;
            if (Request?.PromptKey == request.PromptKey)
            {
                var next = Request;
                Require(next.InteractionId != request.InteractionId, "出售成交后未形成新批次请求");
                var freshRows = FindObjectsOfType<EffectDialogResourceRowView>().OrderBy(value => value.name).ToArray();
                Require(freshRows.Length == 4 && freshRows.All(value => int.TryParse(value.ValueInput.text, out int amount) && amount == 0),
                    "出售新请求仍残留上一批数量草稿");
                var secondBefore = State.FindPlayer(actor).Resources.Clone();
                int[] second = PlannedSaleAmounts(secondBefore);
                if (second.Any(amount => amount > 0))
                {
                    int commandStart = commands.Count;
                    for (int i = 0; i < second.Length; i++)
                        if (second[i] > 0) yield return SaleInput(freshRows[i].ValueInput, second[i].ToString(), true);
                    yield return new WaitForEndOfFrame(); SaveCapture(source + "-第二批正式累计提示与草稿");
                    Write(evidenceName + "-第二批输入.json", new { State.GameId, Request = next, Before = secondBefore, Amounts = second,
                        VisibleText = FindObjectsOfType<Text>().Where(value => value.gameObject.activeInHierarchy).Select(value => value.text).ToArray() });
                    Require(commands.Count == commandStart, "第二批草稿编辑提前提交");
                    yield return Press(SaleConfirm(), "同一出售来源第二批首次确认");
                    Require(commands.Count == commandStart + 1, "第二批未恰好提交一次");
                    var command = Read(commands.Last(), "Command") as GameCommand;
                    var receipt = Read(commands.Last(), "Result") as WorkflowSubmissionResult;
                    Require(command?.Kind == GameCommandKind.AnswerInteraction && command.Parameters["interactionId"] == next.InteractionId &&
                        command.Parameters["expectedRevision"] == next.StateRevision.ToString() && receipt?.CommandResult?.Succeeded == true && receipt.AppliedLocally,
                        "第二批未取得当前请求的准确成功回执");
                    completedBatches++;
                    yield return new WaitForEndOfFrame(); SaveCapture(source + "-第二批成交后的正式累计");
                    Write(evidenceName + "-第二批结果.json", new { State.GameId, Before = secondBefore, After = State.FindPlayer(actor).Resources,
                        CurrentRequest = Request, LastCommand = commands.Last() });
                    Covered("page:" + source + ":multi-batch");
                }
                else Append("未覆盖原因.jsonl", new { State.GameId, Source = source, Reason = "首批后无多余可售库存，第二批留缺口，正常0结束" });
            }
            if (Request?.PromptKey == request.PromptKey)
            {
                int ending = commands.Count; var endingRequest = Request;
                yield return Press(SaleFinish(), "出售到账后正常结束该来源");
                Require(commands.Count == ending + 1, "0结束未恰好提交一次");
                Write(evidenceName + "-0结束回执.json", new { State.GameId, Request = endingRequest, LastCommand = commands.Last(), CompletedBatches = completedBatches });
                Covered("page:" + source + ":zero-finish");
            }
            Require(Request?.PromptKey != request.PromptKey && string.IsNullOrEmpty((string)Read(mobile, "pendingEffectCommandId")),
                "出售来源结束后旧请求或busy未释放");
            Covered("page:" + source);
            var resourceIds = new[] { "originium", "originium-shard", "iron", "pure-originium" };
            for (int i = 0; i < inventory.Length; i++)
                if (amounts[i] > 0) Covered("page:" + source + ":resource:" + resourceIds[i]);
            checks.Add(new { Item = source, Passed = true, PlayerId = actor, InventoryValidation = true, DraftNoSubmission = true, Revenue = revenue, Before = before, After = after });
            Write(evidenceName + "-首次出售结果.json", new { State.GameId, State.Round, PlayerId = actor, Before = before, After = after, Revenue = revenue,
                CurrentRequest = Request, Pending = Read(mobile, "pendingEffectCommandId"), State = Observe() });
            yield return Capture(source + "-结束出售后正常页面");
        }
        private IEnumerator NormalAction()
        {
            int actor = LocalPlayer;
            yield return Press(FindObjectsOfType<PlayerAutomationId>().Single(x => x.Id == "action.tab.1").GetComponent<Button>(), "正常快捷页签");
            var rows = FindObjectsOfType<PlayerAutomationId>().Where(x => x.Id.StartsWith("action.character.") && x.GetComponent<Button>()?.IsInteractable() == true).ToArray();
            var amiyaRow = rows.FirstOrDefault(x => State.ItemZones.Items.Any(i => i.ItemId == x.Id.Substring("action.character.".Length) && i.DefinitionId == "infected.character.amiya"));
            if (amiyaRow != null && !usedAmiya.Contains(amiyaRow.Id))
            {
                amiyaId = amiyaRow.Id.Substring("action.character.".Length);
                usedAmiya.Add(amiyaRow.Id);
                yield return Press(amiyaRow.GetComponent<Button>(), "正常使用已获得的阿米娅");
                var viewer = FindObjectsOfType<CardViewer>().FirstOrDefault(v => v.PlotButton.IsActive() && v.PlotButton.IsInteractable());
                Require(viewer != null, "正常阿米娅使用未显示模式卡面");
                yield return Press(viewer.PlotButton, "正常选择阿米娅计谋");
                activatedAmiyaByPlayer[actor] = amiyaId;
                yield return Capture("正常阿米娅计谋激活");
                yield break;
            }
            if (!amiyaCovered && activatedAmiyaByPlayer.TryGetValue(actor, out amiyaId) &&
                State.ItemZones.Items.Any(i => i.ItemId == amiyaId && i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId == actor) &&
                CharacterLifecycleService.IsSubscriptionActive(State, amiyaId, "infected.character.amiya.tactic"))
            {
                var next = rows.FirstOrDefault(x => x.Id != "action.character." + amiyaId);
                if (next != null)
                {
                    nextCharacterId = next.Id.Substring("action.character.".Length);
                    yield return Amiya(); amiyaCovered = true; yield break;
                }
            }
            yield return Press(FindObjectsOfType<PlayerAutomationId>().Single(x => x.Id == "action.tab.0").GetComponent<Button>(), "正常主行动页签");
            Require(!mobile.CanEndCurrentAction(), "新的行动窗口未经主要行动就已经允许结束");
            int before = commands.Count;
            yield return Press(hud.ActionPanelView.DeployButton, "正常主要行动部署");
            var influence = (YC.Domain.Influence.InfluenceService)Read(mobile, "influenceService");
            string slot = ChooseDeploySlot(influence.GetLegalPlacementSlotIds(State, actor));
            yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, slot, "正常部署选择");
            yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, slot, "正常部署确认");
            yield return Capture("正常部署后结束按钮");
            Require(commands.Count == before + 1, "正常部署未恰好提交一次命令");
            Require(mobile.CanEndCurrentAction() && hud.Frame.EndActionButton.IsInteractable(), "正常部署后结束按钮仍不可用");
            before = commands.Count;
            yield return Press(hud.Frame.EndActionButton, "正常部署后首次结束");
            yield return Capture("正常部署首次结束后");
            Require(commands.Count == before + 1 && (LocalPlayer != actor || State.Phase == GamePhase.ResourceCollection), "正常部署首次结束没有交接");
            actionChecks++;
            checks.Add(new { Item = "正常部署直接结束", PlayerId = actor, NextPlayerId = LocalPlayer, Passed = true, ForcedRefresh = false });
        }

        private IEnumerator GrantedRouteSelection()
        {
            var presenter = Route;
            var request = Request;
            bool localPreview = presenter.RequestBinding == null;
            bool exploration = presenter.Session.Purpose == RouteSelectionPurpose.Exploration;
            bool planSelection = (CampaignMode || SaveResumeMode) && exploration;
            int actor = LocalPlayer;
            if (planSelection)
                explorationPlayPlan = PlanExploration(presenter.Session, presenter.Projection, State.Round >= 4 && !PlayProgress(actor).RedZoneReached);
            if (planSelection && explorationPlayPlan == null && State.Round >= 4)
                explorationPlayPlan = PlanExploration(presenter.Session, presenter.Projection, false);
            if (planSelection)
                Append("决策变化.jsonl", new { State.GameId, State.Round, PlayerId = actor, Action = "正常探索路线计划",
                    Policy = PlayPolicyVersion, Plan = explorationPlayPlan,
                    Origin = localPreview ? "普通主要行动" : "正式效果赠送探索",
                    Reason = "排除城市起点及累计路费超过开始/当前预算的完整路线，再按跳数近到远、同距离低路费排序；逐段沿原控件选择" });
            var visitedDrafts = new HashSet<string>();
            while (!presenter.CanConfirm)
            {
                string draftKey = string.Join(",", presenter.Draft.ConfirmedRouteIds.OrderBy(id => id)) + "|" +
                    string.Join(",", presenter.Draft.SelectedLocationIds.OrderBy(id => id)) + "|" + presenter.Draft.EditingRouteId + "|" +
                    string.Join(",", presenter.Draft.RecipientChoices.Select(choice => choice.RouteId + ":" + choice.ReceiverSubjectKey).OrderBy(id => id)) + "|" +
                    State.EffectRuntime.StateRevision + "|" + presenter.Projection.Budget.AvailableSelectionGoldVoucherAmount;
                if (!visitedDrafts.Add(draftKey))
                {
                    Write("地图选择无进展-" + State.EffectRuntime.StateRevision + ".json", new { State.GameId, State.Round,
                        PlayerId = actor, Request = request, Session = presenter.Session, Draft = presenter.Draft, Projection = presenter.Projection,
                        Plan = explorationPlayPlan, Reason = "同一权威revision重复到达相同选择与收款草稿，停止驱动，不能反复点击" });
                    throw new DriverContinuationPendingException("地图选点返回重复草稿，无法形成可确认的合法预览；保留首次循环，不补点");
                }
                var locations = FindObjectsOfType<MapButtonView>().Where(b => b.TargetKind == WorkflowHighlightTargetKind.Location && b.IsInteractive &&
                    !presenter.Draft.SelectedLocationIds.Contains(b.TargetId));
                var location = planSelection ?
                    locations.FirstOrDefault(button => explorationPlayPlan != null && button.TargetId == explorationPlayPlan.Target) : PreferVisibleMapTarget(locations);
                if (location != null)
                {
                    yield return MapPress(WorkflowHighlightTargetKind.Location, location.TargetId, "正常额外采集选择正式可达资源点");
                    continue;
                }
                var option = presenter.Projection.RouteOptions.Where(o => o.CanAffordIfCharged && o.IsFrontier && !o.IsOriginallyConfirmed &&
                    (!planSelection || explorationPlayPlan != null && explorationPlayPlan.Routes.Contains(o.RouteId)) &&
                    FindObjectsOfType<MapButtonView>().Any(b => b.TargetKind == WorkflowHighlightTargetKind.Route && b.TargetId == o.RouteId && b.IsInteractive))
                    .OrderBy(o => FindObjectsOfType<MapButtonView>().Any(b => b.TargetKind == WorkflowHighlightTargetKind.Route &&
                        b.TargetId == o.RouteId && b.IsInteractive && IsMapTargetVisible(b)) ? 0 : 1)
                    .ThenBy(o => o.TollGoldVoucherAmount).FirstOrDefault();
                if (option == null && presenter.Session.AllowCancelBeforeCommit)
                {
                    int cancelRevision = State.EffectRuntime.StateRevision, cancelCommands = commands.Count;
                    Write("额外采集不可达-" + cancelRevision + ".json", new { State.GameId, State.Round,
                        PlayerId = actor, Request = request, Session = presenter.Session, Draft = presenter.Draft,
                        Projection = presenter.Projection, Reason = "正式可见草稿没有可达资源点或可负担航道，允许取消；不计采集完成" });
                    Append("控件事件.jsonl", new { Index = ++eventNumber, Stage = stage, Frame = Time.frameCount,
                        Input = "内部正常Escape路由入口，非外部按键", Description = "不可达的额外采集正常取消" });
                    Require(mobile.TryHandleInteractionEscape(), "允许取消的额外采集未被正常Escape路由受理");
                    yield return Settle();
                    Require(localPreview ? State.EffectRuntime.StateRevision == cancelRevision && commands.Count == cancelCommands && Route?.IsActive != true :
                        State.EffectRuntime.StateRevision > cancelRevision && commands.Count == cancelCommands + 1 &&
                        Request?.InteractionId != request?.InteractionId, "地图草稿正常取消未获得对应结果");
                    explorationPlayPlan = null;
                    Append("未覆盖原因.jsonl", new { State.GameId, State.Round, PlayerId = actor,
                        Source = "额外采集", Cancelled = true, Covered = false, Reason = "本人正式预算/可达性不足，正常取消并继续本局" });
                    yield break;
                }
                Require(option != null, "额外采集正式草稿不可达且不允许取消，保留实际界面");
                yield return MapPress(WorkflowHighlightTargetKind.Route, option.RouteId, "正常额外采集选择可负担航道");
                var recipient = option.EligibleRecipients.OrderBy(r => r.Kind.ToString() == "Bank" ? 0 : 1).FirstOrDefault();
                Require(recipient != null, "正式航道没有合法路费收款对象");
                var paymentView = FindObjectsOfType<EventChoiceDialogView>().FirstOrDefault(v => v.gameObject.activeInHierarchy);
                var recipientButton = recipient.Kind.ToString() == "Bank" ? paymentView?.ResourcePaymentBankButton :
                    FindObjectsOfType<Button>().FirstOrDefault(b => b.IsActive() && b.IsInteractable() &&
                        ComponentPath(b).Contains("Route Recipient " + recipient.SubjectKey));
                yield return Press(recipientButton, "正常航道路费选择正式收款对象");
            }
            int beforeRevision = State.EffectRuntime.StateRevision, beforeCommands = commands.Count;
            var selectedLocationsBefore = presenter.Draft.SelectedLocationIds.ToArray();
            var confirmedPreview = presenter.Projection.Preview;
            Require(confirmedPreview?.FinalizationValidation?.IsValid == true && confirmedPreview.PaymentPlan.TotalGoldVoucherAmount <=
                Math.Min(presenter.Session.StartGoldVoucherAmount, State.FindPlayer(actor).Resources.GoldVoucher),
                "探索/采集完整路线仍不可付，不能进入二次确认");
            var beforeResources = State.FindPlayer(actor).Resources.Clone();
            string name = "回合" + State.Round + "-玩家" + actor + (exploration ? "-探索-" : "-额外采集-") + beforeRevision;
            Write(name + "输入.json", new { State.GameId, Request = request, Session = presenter.Session,
                Draft = presenter.Draft, Preview = presenter.Projection.Preview, BeforeResources = beforeResources });
            if (localPreview)
            {
                Require(presenter.Draft.SelectedLocationIds.Count == 1, "正常探索草稿必须有唯一正式目标");
                Require(!presenter.Draft.SelectedLocationIds.Contains(presenter.Session.StartLocationId) &&
                    confirmedPreview.EffectiveRouteIds.Count > 0, "正常探索必须沿非空航道到达城市起点以外的资源点");
                yield return MapPress(WorkflowHighlightTargetKind.Location, presenter.Draft.SelectedLocationIds[0], "正常探索原地图二击确认");
            }
            else
            {
                Require(mobile.CanEndCurrentAction(), "额外采集草稿可确认但真实结束入口不可用");
                yield return Press(hud.Frame.EndActionButton, "正常额外采集首次确认");
            }
            Require(State.EffectRuntime.StateRevision > beforeRevision && commands.Count == beforeCommands + 1 &&
                (localPreview ? (Read(commands[beforeCommands], "Command") as GameCommand)?.Kind == GameCommandKind.ExploreLocation :
                Request?.InteractionId != request?.InteractionId), "地图草稿首次确认未受理完成，不能补点或强刷");
            var completionResult = Read(commands[beforeCommands], "Result") as WorkflowSubmissionResult;
            Require(completionResult?.CommandResult?.Succeeded == true && completionResult.AppliedLocally,
                "地图首次确认没有准确成功并Applied的回执");
            Write(name + "结果.json", new { State.GameId, BeforeResources = beforeResources, AfterResources = State.FindPlayer(actor).Resources,
                BeforeRevision = beforeRevision, AfterRevision = State.EffectRuntime.StateRevision, CommandsAdded = commands.Count - beforeCommands });
            Covered(exploration ? "action:explore" : "action:granted-harvest");
            if (CampaignMode && exploration && !localPreview && recordedPlayCommands.Add("explore:" + request.InteractionId))
                PlayProgress(actor).Explorations++;
            if (CampaignMode && exploration && StaticMapDefinitions.Resolve(State.MapId).Locations.Any(location =>
                location.IsRedZone && selectedLocationsBefore.Contains(location.LocationId)))
                PlayProgress(actor).RedZoneReached = true;
            explorationPlayPlan = null;
        }

        private IEnumerator HarvestOne(bool record)
        {
            int actor = LocalPlayer;
            var presenter = Route;
            string harvestName = (CoverageMode ? "回合" + State.Round + "-" : "") + "正常采集玩家" + actor;
            var initial = presenter.Draft.SelectedLocationIds.ToArray();
            var target = PreferVisibleMapTarget(FindObjectsOfType<MapButtonView>().Where(b =>
                b.TargetKind == WorkflowHighlightTargetKind.Location && b.IsInteractive && initial.Contains(b.TargetId)));
            bool edited = target != null;
            if (target != null)
            {
                string id = target.TargetId;
                yield return MapPress(WorkflowHighlightTargetKind.Location, id, "正常采集取消默认点");
                Require(!presenter.Draft.SelectedLocationIds.Contains(id), "正常采集取消输入未更新草稿");
                // 正常开局可能只有一个资源点，恢复它仍是实际编辑，不造点、不造资源。
                if (initial.Length == 1)
                    yield return MapPress(WorkflowHighlightTargetKind.Location, id, "正常采集重新选中唯一资源点");
            }
            var selected = presenter.Draft.SelectedLocationIds.ToArray();
            var beforeResources = JsonConvert.SerializeObject(State.FindPlayer(actor).Resources);
            var expectedResources = State.FindPlayer(actor).Resources.Clone();
            var tokens = State.Map.ResourceTokens.Where(t => selected.Contains(t.LocationId)).ToArray();
            foreach (var token in tokens)
                expectedResources.Set(token.ResourceType, expectedResources.Get(token.ResourceType) + token.Amount);
            int cost = presenter.Projection.Preview?.PaymentPlan?.TotalGoldVoucherAmount ?? 0;
            expectedResources.GoldVoucher -= cost;
            var beforeRound = State.Round;
            var revision = State.EffectRuntime.StateRevision;
            string requestId = Request?.InteractionId;
            int before = commands.Count;
            Require(presenter.CanConfirm && mobile.CanEndCurrentAction(), "正常采集本人草稿不能首次确认");
            yield return Capture(harvestName + "首次结束前");
            Write(harvestName + "正式状态前.json", State);
            Write(harvestName + "首次输入.json", new { PlayerId = actor, InitialSelection = initial, Edited = edited,
                Selected = selected, BeforeResources = JsonConvert.DeserializeObject(beforeResources), Preview = presenter.Projection.Preview,
                ResourceTokens = tokens, ExpectedResources = expectedResources, Request = Request });
            yield return Press(hud.Frame.EndActionButton, "正常采集首次结束，不补点");
            yield return Capture(harvestName + "首次结束后");
            Write(harvestName + "正式状态后.json", State);
            bool resourceMatch = expectedResources.Enumerate().All(pair => State.FindPlayer(actor).Resources.Get(pair.Key) == pair.Value);
            var reopened = State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection" && r.AnsweringPlayerId == actor).ToArray();
            Write(harvestName + "首次结果.json", new { PlayerId = actor, RoundBefore = beforeRound, RoundAfter = State.Round,
                BeforeResources = JsonConvert.DeserializeObject(beforeResources), AfterResources = State.FindPlayer(actor).Resources,
                ExpectedResources = expectedResources, ResourcesMatch = resourceMatch,
                CommandsAdded = commands.Count - before, Reopened = reopened, State = Observe() });
            Require(commands.Count == before + 1 && State.EffectRuntime.StateRevision > revision && reopened.Length == 0 &&
                (LocalPlayer != actor || State.Phase != GamePhase.ResourceCollection), "正常采集首次确认没有受理并交接");
            if (!CoverageMode) Require(resourceMatch, "正常采集实际资源与已选资源点及正式路费不符，保留完整状态核对");
            bool finalScoringResolved = State.Round == State.MaxRounds && State.Round == beforeRound &&
                (State.Phase == GamePhase.FinalScoring || State.Phase == GamePhase.GameOver) &&
                State.FinalScoring?.IsResolved == true && State.FinalScoring.PlayerScores.Count == State.Players.Count;
            Require(State.Round > beforeRound || State.FindPlayer(actor).HasCollectedResourcesThisRound || finalScoringResolved,
                "正常采集首次结束未标记本人完成");
            if (finalScoringResolved) Append("决策变化.jsonl", new { State.GameId, PlayerId = actor, State.Round,
                Action = "最后采集已进入正式终局", State.Phase, State.FinalScoring.IsResolved,
                Note = "正式收尾已复位回合标记；沿已解析四人计分进入正常终局出口，不要求回合标记继续为真" });
            if (CoverageMode) Covered("action:harvest");
            if (record)
            {
                Require(!harvestOrder.Contains(actor), "正常采集首轮重复玩家"); harvestOrder.Add(actor);
                checks.Add(new { Item = "正常连续四人首次采集", ActualSequence = harvestOrder.Count, PlayerId = actor, Passed = true, Edited = edited,
                    Selected = selected, RequestId = requestId, ResourcesMatch = true, SecondAttempt = false });
            }
        }
        private IEnumerator Amiya()
        {
            stage = "阿米娅：切换真实快捷行动页签";
            yield return Press(FindObjectsOfType<PlayerAutomationId>().Single(x => x.Id == "action.tab.1").GetComponent<Button>(), "快捷行动页签");
            stage = "阿米娅：实际角色快捷行开始使用";
            var row = FindObjectsOfType<PlayerAutomationId>().Single(x => x.Id == "action.character." + nextCharacterId);
            int before = commands.Count;
            yield return Press(row.GetComponent<Button>(), "角色快捷行开始使用");
            yield return Capture("01-阿米娅使用前底栏");
            Require(commands.Count == before + 1, "角色快捷行未恰好提交一次命令");
            var window = SettlementHostProtocol.FindActiveWindow(State);
            Require(window != null && window.Entries.Any(e => e.AbilityId == "infected.character.amiya.tactic"), "正式角色使用前窗口没有阿米娅来源");
            var projected = session.ProjectFor(GameStateViewer.Player(LocalPlayer));
            var source = projected.CharacterSettlementOptions.Single(o => o.SourceItemId == amiyaId && o.IsEnabled);
            var strip = hud.GetComponentsInChildren<SettlementEffectStripView>(true).Single(v => v.gameObject.activeInHierarchy);
            var chips = strip.GetComponentsInChildren<SettlementEffectChipView>(true).Where(c => c.gameObject.activeInHierarchy).ToArray();
            var chip = chips.SingleOrDefault(c => ((SettlementOptionView)Read(c, "option"))?.OptionId == source.OptionId);
            Require(chip != null, "正式本人option存在，但实际底栏没有匹配Chip");
            Require(chip.ValidateReferences(out var reason), reason);
            Require(strip.WindowId == source.WindowId && source.WindowId == projected.ActiveSettlementWindowId, "底栏绑定窗口与本人正式投影不一致");
            Require(hud.Frame.GameplayPresentationReady, "真实HUD展示尚未就绪");
            Require(!((GameObject)Read(hud.Frame, "longSummary")).activeInHierarchy && !((GameObject)Read(hud.Frame, "shortSummary")).activeInHierarchy,
                "结算按钮与动态提示同时显示");
            Require((bool)Read(chip, "executionAllowed") && !(bool)Read(chip, "pending"), "实际Chip不可执行或仍待回执");
            var visualGroup = (CanvasGroup)Read(chip, "visualGroup");
            Require(visualGroup.alpha > .99f && chip.isActiveAndEnabled, "Chip尚未真实显示");
            Write("阿米娅底栏核验.json", new { PlayerId = LocalPlayer, Option = source, strip.WindowId, strip.VisibleOptionCount,
                Chip = ComponentPath(chip), ParentChain = Parents(chip.transform), Label = ComponentPath((Component)Read(chip, "labelView")),
                Icon = ComponentPath((Component)Read(chip, "icon")), IconSprite = ((Image)Read(chip, "icon")).sprite?.name,
                hud.Frame.GameplayPresentationReady, SummaryMutuallyExclusive = true });
            string useId = window.Context.TargetUseId;
            stage = "阿米娅：真实Chip完整指针事件";
            before = commands.Count;
            yield return PointerSequence(chip.gameObject, "阿米娅结算Chip");
            yield return Capture("02-阿米娅插入后继页面");
            Require(commands.Count == before + 1, "真实Chip未恰好提交一次命令");
            var use = State.CharacterUses.Single(u => u.UseId == useId);
            Require(use.SourceConsumed && use.DoubleUseGranted, "阿米娅准确受理后未挂接双模式正文");
            Require(!CharacterLifecycleService.IsSubscriptionActive(State, amiyaId, "infected.character.amiya.tactic"), "阿米娅一次来源未消费");
            var request = (InteractionRequestProjection)Read(Read(mobile, "interactionRequestRouter"), "CurrentRequest");
            Require(request != null && request.InteractionTypeId == CharacterLifecycleService.OrderInteractionTypeId, "后继正式顺序请求没有接续");
            Require(string.IsNullOrEmpty((string)Read(mobile, "pendingEffectCommandId")) && string.IsNullOrEmpty((string)Read(mobile, "characterUseCommandId")), "准确回执后busy未释放");
            Require((bool)Read(Read(mobile, "characterCardEffectChoiceDialog"), "IsShowing"),
                "顺序请求存在，但实际选择页面未显示");
            checks.Add(new { Item = "阿米娅实际底栏到后继页面", Passed = true, ActualPointerHandler = true, OptionId = source.OptionId, TargetUseId = useId });
        }

        private IEnumerator Press(Button button, string description)
        {
            Require(button != null && button.IsActive() && button.IsInteractable(), description + "：实际按钮不处于可用状态");
            yield return PointerSequence(button.gameObject, description);
        }
        private IEnumerator PointerSequence(GameObject target, string description)
        {
            if (!IsUiTargetVisible(target)) yield return RevealOriginalScrollTarget(target, description);
            var point = UiPointerPoint(target);
            var first = FirstUiClickTarget(point);
            if (!target.activeInHierarchy || first != target)
            {
                Write("不可操作控件-" + (eventNumber + 1) + ".json", new { Stage = stage, Frame = Time.frameCount,
                    AtUtc = DateTime.UtcNow.ToString("o"), Description = description,
                    Target = ComponentPath(target.transform), ScreenX = point.x, ScreenY = point.y,
                    Screen.width, Screen.height, FirstClickTarget = first == null ? null : ComponentPath(first.transform),
                    RaycastDetails = UiRaycastDetails(point), ActivePages = UiPageDetails(), PageRouting = UiPageRouting(),
                    CallbackInvoked = false, Reason = "目标必须在实际视口内且为当前EventSystem首个真实点击处理器；不绕过裁切或遮挡" });
                throw new DriverContinuationPendingException(description + "：控件位于屏外、被裁切或被遮挡，未调用指针回调");
            }
            var data = Pointer(point);
            var targetPath = ComponentPath(target.transform);
            Append("控件事件.jsonl", new { Index = ++eventNumber, Frame = Time.frameCount, Stage = stage, Description = description,
                Path = targetPath, Input = "内部真实控件PointerDown/PointerUp/PointerClick，非外部鼠标", ScreenX = point.x, ScreenY = point.y });
            void Boundary(string callback, bool returned) => Append("控件回调边界.jsonl", new { Index = eventNumber,
                AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount, Stage = stage, Description = description,
                Path = targetPath, Callback = callback, Returned = returned });
            Boundary("PointerDown", false);
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerDownHandler);
            Boundary("PointerDown", true);
            Boundary("PointerUp", false);
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerUpHandler);
            Boundary("PointerUp", true);
            Boundary("PointerClick", false);
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
            Boundary("PointerClick", true);
            Boundary("ObserveAndSave", false);
            Write("最近控件同步返回.json", ObserveSafely());
            Boundary("ObserveAndSave", true);
            yield return Settle();
        }
        private IEnumerator RevealOriginalScrollTarget(GameObject target, string description)
        {
            var scroll = target?.GetComponentInParent<ScrollRect>();
            if (scroll == null || !scroll.isActiveAndEnabled || scroll.viewport == null || scroll.scrollSensitivity <= 0f)
                yield break;
            for (int attempt = 0; attempt < 32 && !IsUiTargetVisible(target); attempt++)
            {
                var targetPoint = UiPointerPoint(target);
                var camera = scroll.GetComponentInParent<Canvas>()?.worldCamera;
                if (RectTransformUtility.RectangleContainsScreenPoint(scroll.viewport, targetPoint, camera)) yield break;
                var point = UiPointerPoint(scroll.viewport.gameObject);
                var data = Pointer(point);
                var hits = new List<RaycastResult>();
                EventSystem.current?.RaycastAll(data, hits);
                var first = hits.Select(hit => ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject))
                    .FirstOrDefault(handler => handler != null);
                if (first != scroll.gameObject) yield break;
                float horizontal = scroll.horizontal ? Mathf.Sign(point.x - targetPoint.x) *
                    scroll.viewport.rect.width * .5f / scroll.scrollSensitivity : 0f;
                float vertical = scroll.vertical ? Mathf.Sign(targetPoint.y - point.y) *
                    scroll.viewport.rect.height * .5f / scroll.scrollSensitivity : 0f;
                data.scrollDelta = new Vector2(horizontal, vertical);
                var before = scroll.normalizedPosition;
                Append("控件事件.jsonl", new { Index = ++eventNumber, Stage = stage, Frame = Time.frameCount,
                    Description = description + "先经原列表滚动显示候选", Path = ComponentPath(scroll),
                    Input = "原ScrollRect的内部真实IScrollHandler回调，非外部鼠标；不设置位置或修改布局",
                    ScreenX = point.x, ScreenY = point.y, DeltaX = horizontal, DeltaY = vertical });
                ExecuteEvents.Execute(scroll.gameObject, data, ExecuteEvents.scrollHandler);
                yield return Settle();
                if (scroll.normalizedPosition == before) yield break;
            }
        }
        private static Vector2 UiPointerPoint(GameObject target)
        {
            var rect = target.GetComponent<RectTransform>();
            return RectTransformUtility.WorldToScreenPoint(target.GetComponentInParent<Canvas>()?.worldCamera,
                rect == null ? target.transform.position : rect.TransformPoint(rect.rect.center));
        }
        private static GameObject FirstUiClickTarget(Vector2 point)
        {
            if (EventSystem.current == null || point.x < 0 || point.y < 0 || point.x >= Screen.width || point.y >= Screen.height) return null;
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(Pointer(point), hits);
            foreach (var hit in hits)
            {
                var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
                if (handler != null) return handler;
            }
            return null;
        }
        private static bool IsUiTargetVisible(GameObject target) => target != null && target.activeInHierarchy &&
            FirstUiClickTarget(UiPointerPoint(target)) == target;
        private static object[] UiRaycastDetails(Vector2 point)
        {
            var hits = new List<RaycastResult>();
            EventSystem.current?.RaycastAll(Pointer(point), hits);
            return hits.Take(10).Select(hit =>
            {
                var graphic = hit.gameObject.GetComponent<Graphic>();
                var canvas = graphic?.canvas;
                var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
                return (object)new { Graphic = ComponentPath(hit.gameObject.transform),
                    ClickHandler = handler == null ? null : ComponentPath(handler.transform),
                    GraphicAlpha = graphic?.color.a, Culled = graphic?.canvasRenderer.cull,
                    CanvasEnabled = canvas?.enabled, SortingOrder = canvas?.sortingOrder,
                    RenderMode = canvas?.renderMode.ToString(), RootCanvas = canvas?.rootCanvas?.name,
                    Raycaster = hit.module == null ? null : ComponentPath(hit.module.transform),
                    RaycasterCamera = hit.module?.eventCamera?.name, RaycasterCameraEnabled = hit.module?.eventCamera?.enabled,
                    Camera = canvas?.worldCamera?.name, CameraEnabled = canvas?.worldCamera?.enabled,
                    Groups = UiCanvasGroups(hit.gameObject.transform) };
            }).ToArray();
        }
        private static object[] UiCanvasGroups(Transform target) => target.GetComponentsInParent<CanvasGroup>(true)
            .Select(group => (object)new { Path = ComponentPath(group.transform), group.alpha,
                group.blocksRaycasts, group.interactable, group.ignoreParentGroups }).ToArray();
        private static object[] UiPageDetails() => FindObjectsOfType<EffectDialogShellView>().Select(page =>
            (object)new { Path = ComponentPath(page.transform), page.gameObject.activeSelf,
                Groups = UiCanvasGroups(page.transform), Rect = UiRectDetails(page.transform as RectTransform),
                Panel = UiRectDetails(Read(page, "panel") as RectTransform),
                ScaleX = page.transform.lossyScale.x, ScaleY = page.transform.lossyScale.y,
                Canvases = page.GetComponentsInChildren<Canvas>(true).Select(canvas => new { canvas.name,
                    canvas.enabled, canvas.overrideSorting, canvas.sortingOrder, RenderMode = canvas.renderMode.ToString(),
                    RootCanvas = canvas.rootCanvas?.name, canvas.targetDisplay,
                    Camera = canvas.worldCamera?.name, CameraEnabled = canvas.worldCamera?.enabled }).ToArray() }).ToArray();
        private static object UiRectDetails(RectTransform rect)
        {
            if (rect == null) return null;
            var point = RectTransformUtility.WorldToScreenPoint(rect.GetComponentInParent<Canvas>()?.worldCamera,
                rect.TransformPoint(rect.rect.center));
            return new { Path = ComponentPath(rect), rect.rect.x, rect.rect.y, rect.rect.width, rect.rect.height,
                ScreenX = point.x, ScreenY = point.y, ScaleX = rect.lossyScale.x, ScaleY = rect.lossyScale.y, ScaleZ = rect.lossyScale.z };
        }
        private static object UiAnchoredPosition(RectTransform rect) => rect == null ? null :
            (object)new { rect.anchoredPosition.x, rect.anchoredPosition.y };
        private static object UiPageRouting()
        {
            var frame = GameplayHudFrame.Active;
            string PathOf(string field) => (Read(frame, field) as GameObject) is GameObject page ? ComponentPath(page.transform) : null;
            return new { VisiblePage = PathOf("visiblePage"), StagePage = PathOf("stagePage"),
                SuspendedEffectPage = PathOf("suspendedEffectPage"), InformationPage = PathOf("informationPage"),
                InformationNavigation = Read(frame, "informationNavigation"), CurrentRequestId = Read(frame, "currentRequestId"),
                GameplayHudFrame.EffectInputSuspended };
        }
        private IEnumerator MapPress(WorkflowHighlightTargetKind kind, string id, string description)
        {
            var target = FindObjectsOfType<MapButtonView>().Single(b => b.TargetKind == kind && b.TargetId == id);
            Require(target.IsInteractive, description + "：地图控件不接收输入 " + id);
            var point = (Vector2)Camera.main.WorldToScreenPoint(target.HitArea.bounds.center);
            if (!TabletopPointerClassifier.CanRouteMapPointer(point))
            {
                var gameId = State.GameId;
                int revision = State.EffectRuntime.StateRevision, actor = LocalPlayer, commandCount = commands.Count;
                string requestId = Request?.InteractionId;
                bool selected = target.IsSelected;
                Append("地图候选导航.jsonl", new { Stage = stage, Description = description,
                    Action = "原地图控件点击前正常显露", Before = MapInputDiagnostics(target) });
                yield return RevealMapTarget(target, details => Append("地图候选导航.jsonl", details));
                Require(State.GameId == gameId && State.EffectRuntime.StateRevision == revision && LocalPlayer == actor &&
                    commands.Count == commandCount && Request?.InteractionId == requestId && target.IsSelected == selected,
                    description + "：正常地图导航意外改变了对局、请求、命令或原预选");
                point = Camera.main.WorldToScreenPoint(target.HitArea.bounds.center);
            }
            Require(TabletopPointerClassifier.CanRouteMapPointer(point), description + "：地图控件中心被HUD遮挡或不在视口 " + id);
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(Pointer(point), hits);
            var hitHandler = hits.Count == 0 ? null : ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject);
            Require(hitHandler == target.gameObject, description + "：真实首命中不是该地图候选 " + id);
            Append("控件事件.jsonl", new { Index = ++eventNumber, Frame = Time.frameCount, Stage = stage, Description = description,
                Input = "真实MapButtonView.OnPointerClick", Kind = kind.ToString(), Id = id, ScreenX = point.x, ScreenY = point.y });
            ExecuteEvents.Execute(target.gameObject, Pointer(point), ExecuteEvents.pointerClickHandler);
            Write("最近控件同步返回.json", ObserveSafely());
            yield return Settle();
        }
        private static object MapInputDiagnostics(MapButtonView button)
        {
            var camera = Camera.main;
            var point = camera == null || button?.HitArea == null ? Vector3.zero : camera.WorldToScreenPoint(button.HitArea.bounds.center);
            var hits = new List<RaycastResult>();
            if (EventSystem.current != null) EventSystem.current.RaycastAll(Pointer(point), hits);
            var world = button.HitArea.bounds.center;
            var viewport = camera == null ? default(Rect) : camera.pixelRect;
            return new { button.TargetId, Kind = button.TargetKind.ToString(), button.IsInteractive,
                WorldCenter = new { world.x, world.y, world.z }, ScreenPoint = new { point.x, point.y, point.z },
                Viewport = new { viewport.x, viewport.y, viewport.width, viewport.height },
                InViewport = camera != null && MapCameraGeometry.IsScreenPointInCameraViewport(camera, point),
                BlockedByHud = TabletopPointerClassifier.IsBlockedByFlatHud(point),
                Suppressed = MapDisplayController.SuppressMapClick,
                RouteAllowed = TabletopPointerClassifier.CanRouteMapPointer(point),
                RaycastObjects = hits.Take(6).Select(hit => hit.gameObject == null ? "" : ComponentPath(hit.gameObject.transform)).ToArray() };
        }
        private static IEnumerator RevealMapTarget(MapButtonView target, Action<object> record)
        {
            var navigation = MapDisplayController.Active;
            Require(navigation != null && Camera.main != null && target?.HitArea != null, "地图候选导航缺少现有正式入口");
            var methods = BindingFlags.Instance | BindingFlags.NonPublic;
            for (var step = 0; step < 8; step++)
            {
                for (var frame = 0; frame < 20 && MapDisplayController.SuppressMapClick; frame++) yield return null;
                var point = (Vector2)Camera.main.WorldToScreenPoint(target.HitArea.bounds.center);
                if (TabletopPointerClassifier.CanRouteMapPointer(point)) yield break;
                var viewport = Camera.main.pixelRect;
                var starts = new[] { viewport.center, new Vector2(viewport.center.x, viewport.yMin + viewport.height * .3f),
                    new Vector2(viewport.xMin + viewport.width * .3f, viewport.center.y),
                    new Vector2(viewport.xMin + viewport.width * .7f, viewport.center.y) };
                var start = starts.FirstOrDefault(value => MapCameraGeometry.IsScreenPointInCameraViewport(Camera.main, value) &&
                    !TabletopPointerClassifier.IsBlockedByFlatHud(value));
                Require(start != Vector2.zero, "现有地图视口没有可开始正常手势的位置");
                var shift = viewport.center - point;
                shift = new Vector2(Mathf.Clamp(shift.x, -viewport.width * .2f, viewport.width * .2f),
                    Mathf.Clamp(shift.y, -viewport.height * .2f, viewport.height * .2f));
                var end = start + shift;
                end = new Vector2(Mathf.Clamp(end.x, viewport.xMin + 12f, viewport.xMax - 12f),
                    Mathf.Clamp(end.y, viewport.yMin + 12f, viewport.yMax - 12f));
                record?.Invoke(new { Action = "内部正式地图拖动入口", Step = step, target.TargetId,
                    Start = new { start.x, start.y }, End = new { end.x, end.y },
                    Before = MapInputDiagnostics(target), StateWrites = false, ExternalMouse = false });
                navigation.GetType().GetMethod("BeginPointer", methods).Invoke(navigation, new object[] { start });
                navigation.GetType().GetMethod("MovePointer", methods).Invoke(navigation, new object[] { end });
                navigation.GetType().GetMethod("EndDrag", methods).Invoke(navigation, null);
                yield return Settle();
                var after = (Vector2)Camera.main.WorldToScreenPoint(target.HitArea.bounds.center);
                if ((after - point).sqrMagnitude < 1f && !TabletopPointerClassifier.CanRouteMapPointer(after))
                {
                    // 只消费滚轮已有输入处理函数；不写缩放/焦点/相机，也不使用开发参数。
                    var zoom = (float)Read(navigation, "targetZoom");
                    var wheelStep = (float)Read(navigation, "wheelZoomStep");
                    navigation.GetType().GetMethod("SetZoomTarget", methods).Invoke(navigation, new object[] { start, zoom - wheelStep });
                    record?.Invoke(new { Action = "内部正式地图滚轮入口", Step = step, Wheel = -1, target.TargetId });
                    yield return Settle();
                }
                record?.Invoke(new { Action = "正式地图手势后重核", Step = step, After = MapInputDiagnostics(target) });
            }
        }
        private static string ChooseDeploySlot(IReadOnlyList<string> legal)
        {
            var buttons = FindObjectsOfType<MapButtonView>();
            var candidate = PreferVisibleMapTarget(legal.Select(id => buttons.FirstOrDefault(b =>
                b.TargetKind == WorkflowHighlightTargetKind.InfluenceSlot && b.TargetId == id && b.IsInteractive)));
            Require(candidate != null, "正常部署没有对应的真实合法交互高亮");
            return candidate.TargetId;
        }
        private static bool IsMapTargetVisible(MapButtonView target) => target != null && target.IsInteractive &&
            TabletopPointerClassifier.CanRouteMapPointer(Camera.main.WorldToScreenPoint(target.HitArea.bounds.center));
        private static MapButtonView PreferVisibleMapTarget(IEnumerable<MapButtonView> candidates)
        {
            var legal = candidates.Where(target => target != null && target.IsInteractive).ToArray();
            return legal.FirstOrDefault(IsMapTargetVisible) ?? legal.FirstOrDefault();
        }
        private static PointerEventData Pointer(Vector2 point) => new PointerEventData(EventSystem.current) {
            pointerId = -1, button = PointerEventData.InputButton.Left, position = point, pressPosition = point,
            clickCount = 1, clickTime = Time.unscaledTime, eligibleForClick = true, dragging = false };
        private static IEnumerator Settle()
        { yield return null; yield return null; yield return new WaitForSecondsRealtime(.65f); yield return new WaitForEndOfFrame(); }
        private IEnumerator Capture(string name)
        {
            yield return Settle();
            if (CampaignMode && session != null && name != "八回合正式终局" &&
                !campaignCaptureKeys.Add(State.Round + ":" + State.Phase + ":" + (Request?.InteractionTypeId ?? name))) yield break;
            SaveCapture(name); Write((CoverageMode ? captureNumber.ToString("00") + "-" : "") + name + ".json", ObserveSafely());
        }
        private void SaveCapture(string name)
        {
            var file = (++captureNumber).ToString("00") + "-" + name + ".png";
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            if (texture == null) throw new InvalidOperationException("真实渲染截图为空");
            var pixels = texture.GetPixels32();
            bool hasVariation = false;
            for (int i = 0; i < pixels.Length; i += 257) if (!pixels[i].Equals(pixels[0])) { hasVariation = true; break; }
            File.WriteAllBytes(Path.Combine(output, file), texture.EncodeToPNG()); Destroy(texture);
            screenshots.Add(new { File = file, Label = name, Stage = stage, Frame = Time.frameCount, Width = Screen.width, Height = Screen.height, HasPixelVariation = hasVariation });
            Write("截图索引.json", screenshots);
            Require(hasVariation, "截图没有像素变化，不能作为真实UI画面证据");
        }
        private object ObserveSafely()
        { try { return mobile != null && session != null ? Observe() : new { Stage = stage, Scene = SceneManager.GetActiveScene().name, Buttons = FindObjectsOfType<Button>().Select(ButtonInfo).ToArray(), Text = FindObjectsOfType<Text>().Select(t => t.text).ToArray() }; } catch (Exception error) { return new { ObservationError = error.ToString() }; } }
        private object Observe()
        {
            var route = Read(mobile, "routeResourceSelection"); var presenter = Route;
            return new { Stage = stage, Frame = Time.frameCount, Scene = SceneManager.GetActiveScene().name, State.GameId, State.Round, State.Phase,
                State.CurrentPlayerId, LocalPlayerId = LocalPlayer, Revision = State.EffectRuntime.StateRevision,
                CurrentRequest = Read(Read(mobile, "interactionRequestRouter"), "CurrentRequest"),
                RouteSession = presenter.Session, RouteBinding = presenter.RequestBinding, Draft = presenter.Draft,
                presenter.CanConfirm, CanEnd = mobile.CanEndCurrentAction(), Preview = presenter.Projection?.Preview,
                ActualHighlights = FindObjectsOfType<MapButtonView>().Where(b => b.Image.enabled || b.IsInteractive).Select(b => new {
                    Kind = b.TargetKind.ToString(), b.TargetId, b.IsSelected, b.IsInteractive, Visible = b.Image.enabled, Active = b.gameObject.activeInHierarchy }).ToArray(),
                RoutePending = Read(route, "inFlight"), MobilePending = Read(mobile, "pendingEffectCommandId"), CharacterPending = Read(mobile, "characterUseCommandId"),
                Players = State.Players.Select(p => new { p.PlayerId, p.CityLocationId, p.Resources, p.HasCollectedResourcesThisRound }).ToArray(),
                OpenRequests = State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").ToArray(),
                RouteRequests = State.EffectRuntime.InteractionRequests.Where(r => r.InteractionTypeId == "route_resource.selection").ToArray(),
                CharacterUses = State.CharacterUses, SettlementWindow = SettlementHostProtocol.FindActiveWindow(State),
                CommandCount = commands.Count, Commands = CampaignMode ? null : commands.ToArray(),
                EndButtons = new[] { ButtonInfo(hud.ActionPanelView.EndRoundButton), ButtonInfo(hud.Frame.EndActionButton) },
                Strips = hud.GetComponentsInChildren<SettlementEffectStripView>(true).Select(s => new { Path = ComponentPath(s), Active = s.gameObject.activeInHierarchy,
                    s.WindowId, s.VisibleOptionCount, Chips = s.GetComponentsInChildren<SettlementEffectChipView>(true).Where(c => c.gameObject.activeInHierarchy)
                        .Select(c => new { Path = ComponentPath(c), Option = Read(c, "option"), Pending = Read(c, "pending"), Allowed = Read(c, "executionAllowed") }).ToArray() }).ToArray(),
                ActiveButtons = FindObjectsOfType<Button>().Select(ButtonInfo).ToArray(),
                VisibleText = FindObjectsOfType<Text>().Where(t => t.isActiveAndEnabled).Select(t => t.text).ToArray() };
        }
        private static object ButtonInfo(Button b) => new { Path = ComponentPath(b), Active = b != null && b.IsActive(),
            Interactable = b != null && b.IsInteractable(), Text = b?.GetComponentInChildren<Text>()?.text };
        private void ObserveGateway(object owner)
        {
            var gateway = Read(owner, "commandGateway") as CommandGateway;
            if (gateway == null || !observedGateways.Add(gateway)) return;
            var inner = (IGameCommandPort)Read(gateway, "commandPort");
            typeof(CommandGateway).GetField("commandPort", Members).SetValue(gateway, new ObservationPort(inner, this));
        }
        private sealed class ObservationPort : IGameCommandPort
        {
            private readonly IGameCommandPort inner; private readonly HotseatUiValidationDriver driver;
            public ObservationPort(IGameCommandPort inner, HotseatUiValidationDriver driver) { this.inner = inner; this.driver = driver; }
            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                var before = driver.State.EffectRuntime.StateRevision;
                var result = inner.Submit(command); // 原适配器、真实提交、准确回执和正常同步保持原顺序。
                var record = new { Stage = driver.stage, Frame = Time.frameCount, Command = command, Result = result,
                    BeforeRevision = before, AfterRevision = driver.State.EffectRuntime.StateRevision, LocalPlayerAfter = driver.LocalPlayer };
                driver.commands.Add(record); driver.Append("命令与准确回执.jsonl", record);
                driver.RecordPlayCommand(command, result);
                return result;
            }
        }
        private static object Read(object target, string name)
        {
            if (target == null) return null;
            return target.GetType().GetField(name, Members)?.GetValue(target) ?? target.GetType().GetProperty(name, Members)?.GetValue(target);
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static string ComponentPath(Component component)
        { if (component == null) return null; var path = component.name; for (var p = component.transform.parent; p != null; p = p.parent) path = p.name + "/" + path; return path; }
        private static object[] Parents(Transform target)
        { var values = new List<object>(); for (var p = target; p != null; p = p.parent) values.Add(new { p.name, p.gameObject.activeSelf, p.gameObject.activeInHierarchy }); return values.ToArray(); }
        private void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonConvert.SerializeObject(value, Formatting.Indented));
        private void Append(string name, object value)
        {
            if (!appendLogs.TryGetValue(name, out var writer))
            {
                // 始终持有共享追加句柄；只读采证必须共享写入，不能在真实命令回执中抢占日志。
                writer = new StreamWriter(new FileStream(Path.Combine(output, name), FileMode.Append,
                    FileAccess.Write, FileShare.ReadWrite), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                appendLogs.Add(name, writer);
            }
            writer.WriteLine(JsonConvert.SerializeObject(value));
        }
        private void Finish(Exception failure)
        {
            if (finished) return; finished = true;
            if (failure == null && errors.Count > 0) failure = new InvalidOperationException("运行出现Unity错误，详见RuntimeErrors及Player.log");
            if (SalesMode && session != null && !string.IsNullOrEmpty(coveragePath))
                try { UpdateCoverage(); }
                catch (Exception coverageError) { Write("覆盖导出错误.json", coverageError.ToString()); if (failure == null) failure = coverageError; }
            if (CampaignMode && campaignGame != null)
            {
                campaignGame.Passed = failure == null && campaignGame.Terminal;
                campaignGame.Failure = failure?.ToString();
                try { ObserveCampaignCompletion(); PersistCampaign(); }
                catch (Exception coverageError)
                { Write("覆盖导出错误.json", coverageError.ToString()); if (failure == null) failure = coverageError; }
            }
            foreach (var writer in appendLogs.Values) writer.Dispose();
            appendLogs.Clear();
            Write("验证结果.json", new { Case = selectedCase, Passed = failure == null, Stage = stage, Error = failure?.ToString(),
                DriverContinuationPending = failure is DriverContinuationPendingException,
                FailureCategory = failure is DriverContinuationPendingException ? "驱动待接" :
                    failure is PaymentCannotContinueException ? "付款不可继续" : null,
                ExitCode = failure == null ? 0 : 2, NaturalExitRequested = true, CommandCount = commands.Count, Checks = checks,
                FullGameCompleted = CampaignMode && campaignGame?.Terminal == true, Campaign = CampaignMode ? campaignGame : null,
                SaveResumeCompleted = SaveResumeMode && failure == null,
                TargetPagesCovered = SalesMode && TargetPagesCovered, SettlementStripObserved = stripObserved, Coverage = SalesMode ? coverage : null,
                AmiyaCovered = amiyaCovered, AmiyaSourcesByPlayer = activatedAmiyaByPlayer, NormalActionEndChecks = actionChecks,
                ActualHarvestOrder = harvestOrder, Screenshots = screenshots, RuntimeErrors = errors,
                Boundary = SaveResumeMode ? "可见独立Player正常菜单建房、入场、原设置保存、原主页读取、合法移城继续；原控件内部指针事件，非外部鼠标，非物理Esc。无直接保存或恢复API、无状态注入、无随机覆盖。" :
                    "可见独立Player从正常主菜单创建四人房间，经完整开局、行动进入采集；正式控件内部指针事件，无状态注入、无固定随机种子。非外部鼠标命中。",
                SourceWrites = false, AssetsRebuilt = false, ForcedRefresh = false, StateReplacementCount = 0 });
            UnityEngine.Application.logMessageReceived -= OnLog;
            UnityEngine.Application.Quit(failure == null ? 0 : 2);
        }
    }
}
#endif
