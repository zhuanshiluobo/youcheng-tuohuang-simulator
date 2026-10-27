using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
#if !QY007_CLR_DIAGNOSTIC
    public sealed class CompositeContinuationDirectTests
    {
        [NUnit.Framework.Test] public void RegisteredBodyBindsTheRealMoveAndKeepsDeclaredPayment() => Run("binding");
        [NUnit.Framework.Test] public void ActualActionPaysMovesPlacesOnceAndSurvivesThreeRecoveries() => Run("full");
        [NUnit.Framework.TestCase("decline")] [NUnit.Framework.TestCase("insufficient")]
        public void UnpaidActionCannotMoveOrPlace(string mode) => Run(mode);
        [NUnit.Framework.TestCase("condition")] [NUnit.Framework.TestCase("failed")]
        [NUnit.Framework.TestCase("missing_result")] [NUnit.Framework.TestCase("missing_slots")]
        [NUnit.Framework.TestCase("empty_slots")]
        public void CompletionGuardDoesNotInventTraversedSlots(string mode) => Run(mode);
        private static void Run(string mode)
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try
            {
                string root = artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "复合动力续接正式内容");
                artifacts.WriteLabeledText("复合动力隔离直接结果", JsonConvert.SerializeObject(CompositeContinuationChecks.Run(root, mode)));
            }
            finally { artifacts.Finish(); }
        }
    }
#endif

    // 与独立CLR探针共用的直接检查。初始化只在隔离状态中布置正式配方已有实例，非正常局/页面验收。
    internal static class CompositeContinuationChecks
    {
        private const string Style = "city_style_composite_power_system";
        private const string Action = "special_action.city_style.composite_power_system";
        private const string Handler = "city-styles.composite.after_move";
        internal static object Run(string root, string mode)
        {
            var f = new Fixture(root);
            try
            {
                if (mode == "binding") f.CheckBinding();
                else if (mode == "full") f.FullChain();
                else if (mode == "decline" || mode == "insufficient") f.Unpaid(mode);
                else f.Guard(mode);
                return new { mode, success = true, scope = "正式内容和生产机制的隔离直接检查，非Unity/正常局/UI", f.Pack.ContentHash,
                    active = f.State.ContentPackHash, f.State.GameId, f.Trace, state = f.State };
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(JsonConvert.SerializeObject(new { mode, success = false,
                    error = error.ToString(), f.Trace, state = f.State }), error);
            }
        }

        private sealed class Fixture
        {
            internal readonly ExternalContentPack Pack;
            internal readonly EffectRegistry Registry;
            internal GameState State;
            internal readonly List<object> Trace = new List<object>();
            private readonly MapQueryService map;
            private readonly InfluenceService influence;
            private readonly ExternalAbilityHandler scriptHandler;
            private readonly string script;
            private EffectTreeExecutor Engine => new EffectTreeExecutor(State, Registry);
            private static void Need(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
            internal Fixture(string root)
            {
                Pack = ExternalContentPack.Load(root);
                var configuration = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = 4 };
                var catalog = ActiveContentCatalogBuilder.Build(Pack, configuration); configuration.ContentPackHash = catalog.ContentPackHash;
                State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                    Enumerable.Range(1, 4).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "隔离玩家" + p, Color = (PlayerColor)(p - 1) }).ToList(),
                    configuration, 73, catalog.SetupRecipe, catalog.ContentDefinitions, new[] { 1, 2, 3, 4 }, "qy007.composite.r5", null);
                map = new MapQueryService(StaticMapDefinitions.Resolve(State.MapId)); influence = new InfluenceService(map);
                var tokens = new ResourceTokenService(); var decks = new EventDeckService(73); var travel = new TravelCostService(map);
                var movement = new CityMovementService(map, influence, travel, decks, tokens);
                Registry = new EffectRegistry(); catalog.Register(Registry);
                ResourceEffectExecutor.Register(Registry); InfluenceEffectExecutor.Register(Registry, influence);
                CityMoveEffectExecutor.Register(Registry, map, influence, movement, travel, tokens);
                EventCardEffectExecutor.Register(Registry, map, influence, decks, tokens);
                CityStyleDeclarationEffectExecutor.Register(Registry);
                new RoundExecutionService(Registry);
                var definition = Pack.ActiveDefinitions.Single(d => d.DefinitionId == Style);
                scriptHandler = definition.AbilityHandlers.Single(h => h.HandlerId == "city-styles.composite");
                script = Pack.GetScript(scriptHandler.Script);
                State.Phase = GamePhase.ActionRound1; State.Round = State.ActionRound = 1;
                State.CurrentPlayerId = State.StartPlayerId = 1; State.PendingChoice = null;
                State.EffectRuntime = new EffectRuntimeState { PlayerOrderSnapshot = new List<int> { 1, 2, 3, 4 } };
                State.FindPlayer(1).RemainingMainActionsThisTurn = 1;
                State.FindPlayer(1).Resources = new ResourceSet { Originium = 10, Iron = 10, OriginiumShard = 10, GoldVoucher = 10 };
                Place(State.ItemZones.Items.Single(i => i.DefinitionId == Style), State.ItemZones.Zones.Single(z => z.LocalZoneId == "city_styles"));
                var chosen = new HashSet<string>();
                foreach (var cell in new[] { (0, "yellow"), (3, "red"), (4, "yellow") })
                {
                    var item = State.ItemZones.Items.First(i => !chosen.Contains(i.ItemId) &&
                        State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "facility_deck") &&
                        YC.Domain.Facilities.FacilityCardDatabase.Get(State, i.DefinitionId)?.Color == cell.Item2);
                    chosen.Add(item.ItemId);
                    Place(item, State.ItemZones.Zones.Single(z => z.OwnerPlayerId == 1 && z.LocalZoneId == "city_cell_" + cell.Item1));
                }
                var parent = State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == "A-01");
                Place(State.ItemZones.Items.Single(i => i.TypeId == "core.mobile_city" && i.OwnerSubject.PlayerId == 1),
                    State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.LocalZoneId == "cities"));
                if (!State.Map.OpenLocationIds.Contains("A-01")) State.Map.OpenLocationIds.Add("A-01");
                Need(new ItemZoneQuery().TryValidate(State, out string reason), "隔离初态非法：" + reason);
                Root(CityStyleDeclarationEffectExecutor.Create(1, Style, new[] { 0, 3, 4 }));
                Need(State.FindPlayer(1).DeclaredCityStyles.Count == 1, "真实声明失败");
                Trace.Add(new { stage = "隔离正式配方布置并完成真实声明", State.GameId, State.ContentPackHash });
            }
            private void Place(ItemInstanceState item, ZoneInstanceState zone)
            {
                foreach (var order in State.ItemZones.Orders) if (order.ItemIds.Remove(item.ItemId)) order.OrderRevision++;
                item.ZoneId = zone.ZoneId; item.SlotId = "";
                var next = State.ItemZones.Orders.Find(o => o.ZoneId == zone.ZoneId);
                if (next != null) { next.ItemIds.Add(item.ItemId); next.OrderRevision++; }
                if (zone.TemplateId == "core.zone.city_facility_cell") item.ControllerSubject = ItemSubjectRef.Player(1);
            }
            private void Drain() { var report = Engine.RunUntilQuiescent(); Need(!report.Faulted, State.EffectRuntime.LastFaultCode + ":" + State.EffectRuntime.LastFaultMessage); }
            private string Root(EffectSpec spec) { string id = Engine.CreateRoot(spec); Drain(); return id; }
            private InteractionRequest Open(string type) => State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == type);
            private void Answer(InteractionRequest request, NormalizedValue answer)
            {
                Need(Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, answer, out string reason), reason); Drain();
            }
            private void Start()
            {
                var option = Registry.QueryActions(State, 1).Single(a => a.ActionId == Action);
                Need(option.Enabled, option.DisabledReason);
                Root(AbilityActionExecutor.Create(option, "qy007.composite.r5.action", Object()));
            }
            private LuaInvocationResult Invoke(string eventType, LuaSnapshotValue payload, string responseKind = "effects")
            {
                var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("content.city-styles.composite", "city_styles.composite",
                    eventType == "EffectCompleted" ? Handler : scriptHandler.HandlerId, scriptHandler.Version, script, LuaContentHasher.ComputeSha256(script)),
                    new LuaInvocationContext("qy007.composite.direct", eventType, "p1", 0, scriptHandler.Version, 4, 0, 0,
                        new LuaFacadeSnapshot { EventPayload = payload }) { ResponseKind = responseKind });
                Need(result.IsSuccess, result.Diagnostic); return result;
            }
            internal void CheckBinding()
            {
                var invocation = Invoke("AbilityAction", LuaSnapshotValue.Object(Array.Empty<LuaSnapshotEntry>()));
                var raw = invocation.Effects.Single(); var rawMove = raw.NestedEffectGroups[1].Single();
                Need(rawMove.CompletionHandlerId == Handler, "真实Move缺少显式完成绑定");
                Need(Value(rawMove.RawArguments, "completionHandlerId") == null, "续接字段错误地进入Move业务参数");
                Need(new LuaEffectSpecCompiler(presentationPack: Pack).TryCompile(invocation, new LuaEffectCompilationContext {
                    PlayerId = 1, AbilityId = "city_styles.composite", CompletionHandlerId = Handler,
                    ContentInstanceId = CityStyleLuaCatalog.ContentInstanceId, StableKeyPrefix = "qy007.composite.binding"
                }, out var specs, out var fault), fault?.Diagnostic ?? "构树失败");
                var condition = specs.Single(); var move = condition.NestedEffectGroups[1].Single();
                Need(move.EffectTypeId == CityMoveEffectTypeIds.Move && move.CompletionHandlerId == Handler, "编译后绑定错层");
                var pay = condition.NestedEffectGroups[0].Single();
                Need(ResourcePaymentDemandParser.TryParse(pay.NormalizedArguments, out var demand, out int player).Length == 0 && player == 1, "付款合同无效");
                Need(demand.TotalCount == 3 && demand.AllowedResourceTypes.SequenceEqual(new[] { ResourceType.Originium, ResourceType.Iron }.OrderBy(t => (int)t)) &&
                    PaymentResources.Same(demand.FixedCost, new ResourceSet { OriginiumShard = 1 }), "付款费用改变");
                var source = Registry.QuerySources(State, 1, "ruleQuery", queryKind: ResourcePaymentCostPreview.DemandQueryKind).Single();
                Need(ResourcePaymentCostPreview.TryDeclaredDemand(State, Registry, source, Action, out var queryDemand, out string reason), reason);
                Need(JsonConvert.SerializeObject(queryDemand) == JsonConvert.SerializeObject(demand), "正式费用查询与正文不一致");
                Trace.Add(new { stage = "正式查询及构树", condition.CompletionHandlerId, moveBinding = move.CompletionHandlerId, demand });
            }
            internal void Guard(string mode)
            {
                string type = mode == "condition" ? EffectTypeIds.Condition : CityMoveEffectTypeIds.Move;
                var entries = new List<LuaSnapshotEntry> { new LuaSnapshotEntry("effectTypeId", LuaSnapshotValue.String(type)),
                    new LuaSnapshotEntry("outcome", LuaSnapshotValue.String(mode == "failed" ? "failed" : "completed")) };
                if (mode != "missing_result") entries.Add(new LuaSnapshotEntry("normalizedResult", LuaSnapshotValue.Object(
                    mode == "missing_slots" ? Array.Empty<LuaSnapshotEntry>() : new[] { new LuaSnapshotEntry("traversedRouteSlots",
                        LuaSnapshotValue.Array(mode == "empty_slots" ? Array.Empty<LuaSnapshotValue>() : new[] { LuaSnapshotValue.String("route:A1:0") })) })));
                Need(Invoke("EffectCompleted", LuaSnapshotValue.Object(entries)).Effects.Count == 0, "无真实成功路线却生成放置");
                Trace.Add(new { stage = "显式Lua守卫探针，非真实完成事件", mode });
            }
            internal void Unpaid(string mode)
            {
                var before = State.FindPlayer(1).Resources.Clone();
                if (mode == "insufficient")
                {
                    State.FindPlayer(1).Resources.OriginiumShard = 0; before.OriginiumShard = 0;
                    Start();
                    var invocation = State.EffectRuntime.AbilityActionInvocations.Single();
                    Need(invocation.Outcome == "insufficient_resource" && !invocation.ActivationCommitted && !invocation.MainActionCommitted,
                        "不足费用未在受理阶段拒绝");
                }
                else { Start(); Answer(Open(ResourcePaymentChoiceEffectExecutor.InteractionTypeId), NormalizedValue.CreateBoolean(false)); }
                Need(PaymentResources.Same(State.FindPlayer(1).Resources, before), "未付却部分扣费");
                Need(!State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == CityMoveEffectTypeIds.Move || n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence), "未付款已创建移动/放置");
                Need(State.FindPlayer(1).CityLocationId == "A-01", "未付款已移动");
                Need(State.FindPlayer(1).RemainingMainActionsThisTurn == (mode == "decline" ? 0 : 1), "行动消费偏离既有正文条件/报价合同");
                Trace.Add(new { stage = "未付停止", mode });
            }
            private string Business() => JsonConvert.SerializeObject(new { resources = State.Players.Select(p => p.Resources).ToArray(),
                position = State.FindPlayer(1).CityLocationId, budget = State.FindPlayer(1).RemainingMainActionsThisTurn,
                State.ItemZones, State.EffectRuntime.AbilityActionInvocations,
                payments = State.EffectRuntime.EffectNodes.Where(n => n.Payment != null).Select(n => n.Payment).ToArray() });
            private void Recover(string stage)
            {
                string before = Business(); var archive = HostRecoveryService.CreateArchive(State);
                var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), Registry);
                Need(result.Succeeded, "正式Host恢复失败：" + result.Diagnostic); State = result.State; Drain();
                Need(Business() == before, "恢复改变费用、物品、位置或行动"); Trace.Add(new { stage, recovered = true });
            }
            internal void FullChain()
            {
                var initial = State.FindPlayer(1).Resources.Clone(); Start();
                var request = Open(ResourcePaymentChoiceEffectExecutor.InteractionTypeId); string payRequest = request.GetStableInteractionId();
                Need(request.Presentation?.Prompt?.Key == "core:condition.city_style.composite_power_system", "R14正式复合动力缺条件正文；须先汇合017纯展示Lua");
                Need(request.Presentation?.Progress?.Key == "core:resource.payment.choice.prompt", "R14合计配比说明丢失");
                Need(request.Presentation.Prompt.Arguments.Single(a => a.Name == "shards").Value.IntegerValue == 1 &&
                    request.Presentation.Prompt.Arguments.Single(a => a.Name == "flexibleCost").Value.IntegerValue == 3, "R14复合动力条件参数不符真实需求");
                string display = JsonConvert.SerializeObject(request.Presentation);
                var pay = Engine.GetNode(request.OwnerEffectId); string payOwner = pay.EffectId;
                Need(Engine.TryPrepareTotalPaymentDraft(1, payOwner, payRequest, pay.Payment.PaymentGeneration,
                    new ResourceSet { Originium = 1, Iron = 2 }, State.EffectRuntime.StateRevision, "007复合动力合计草稿", out string reason), reason);
                request = Open(ResourcePaymentChoiceEffectExecutor.InteractionTypeId);
                Need(JsonConvert.SerializeObject(request.Presentation) == display, "R14配比刷新改掉源正文或进度");
                string payAnswer = PaymentResources.Candidate(pay.Payment.PaymentCost); Answer(request, NormalizedValue.CreateString(payAnswer));
                var paid = initial.Clone(); paid.Originium--; paid.Iron -= 2; paid.OriginiumShard--;
                Need(PaymentResources.Same(State.FindPlayer(1).Resources, paid), "费用未按3合计+1碎晶提交");
                Need(Engine.GetNode(payOwner).Payment.IsCommitted, "费用未提交");
                string paidReceipt = JsonConvert.SerializeObject(Engine.GetNode(payOwner).Payment);
                var moveRequest = Open(CityMoveEffectExecutor.TargetInteractionTypeId); string moveId = moveRequest.OwnerEffectId;
                string moveRequestId = moveRequest.GetStableInteractionId();
                Need(Engine.GetNode(moveId).ContinuationHandlerId == Handler, "真实运行Move未绑定");
                Recover("已付待移动第一次恢复");
                Need(Open(CityMoveEffectExecutor.TargetInteractionTypeId).GetStableInteractionId() == moveRequestId, "恢复更换移动请求");
                Reject(payRequest, NormalizedValue.CreateString(payAnswer));
                moveRequest = Open(CityMoveEffectExecutor.TargetInteractionTypeId);
                Need(moveRequest.CandidateIds.Contains("A-02"), "隔离输入无合法A1路线，不计成功");
                Answer(moveRequest, NormalizedValue.CreateString("A-02"));
                for (int i = 0; i < 8 && !State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.InteractionTypeId == "effect.influence.place.target"); i++)
                {
                    var next = State.EffectRuntime.InteractionRequests.SingleOrDefault(r => r.Status == "open");
                    Need(next != null && next.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId && next.CandidateIds.Count > 0,
                        "移动完成后没有路线放置，或出现未覆盖的事件后继");
                    Trace.Add(new { stage = "回答实际抽出事件的合法候选", next.InteractionTypeId, answer = next.CandidateIds[0] });
                    Answer(next, NormalizedValue.CreateString(next.CandidateIds[0]));
                }
                var move = Engine.GetNode(moveId);
                // 完成事件已发布后，节点会等待自己的续接子树；不能在路线待答时要求整棵子树终结。
                Need(Text(move.NormalizedResult, "outcome") == "completed", "真实移动未成功");
                var slots = Value(move.NormalizedResult, "traversedRouteSlots").Items.Select(v => v.StringValue).ToArray(); Need(slots.Length > 0, "实际路线为空");
                var completed = State.EffectRuntime.RuleEvents.Single(e => e.SourceEffectId == moveId && e.EventType == "EffectCompleted");
                Need(State.EffectRuntime.ContinuationBindings.Any(b => b.SourceEffectId == moveId && b.CompletionEventId == completed.EventId), "续接不是来自真实移动事件");
                var route = Open("effect.influence.place.target"); string routeId = route.GetStableInteractionId(); string routeOwner = route.OwnerEffectId;
                Need(route.CandidateIds.Count > 0 && route.CandidateIds.All(s => slots.Contains(s) && influence.CanPlace(State, 1, s).IsValid), "放置不在真实合法路线上");
                Recover("路线待答第二次恢复"); route = Open("effect.influence.place.target"); Need(route.GetStableInteractionId() == routeId, "恢复更换路线请求");
                var afterEventResources = State.FindPlayer(1).Resources.Clone();
                string target = route.CandidateIds[0]; Answer(route, NormalizedValue.CreateString(target));
                Need(Engine.GetNode(moveId).Status == EffectNodeStatus.Completed, "路线续接结清后移动节点未终结");
                Need(Engine.GetNode(routeOwner).Status == EffectNodeStatus.Completed, "路线放置未完成");
                Need(influence.FindInfluence(State, target) != null, "地图无真实路线标记");
                Need(State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence && n.Status == EffectNodeStatus.Completed) == 1, "路线重复放置");
                Need(PaymentResources.Same(State.FindPlayer(1).Resources, afterEventResources), "路线放置改变资源");
                Need(JsonConvert.SerializeObject(Engine.GetNode(payOwner).Payment) == paidReceipt, "付款收据被移动/放置改写");
                Need(State.EffectRuntime.EffectNodes.Count(n => n.Payment != null) == 1, "重复建立付款");
                Need(State.FindPlayer(1).RemainingMainActionsThisTurn == 0, "正式action未消费恰好一次主行动");
                Recover("全链完成第三次恢复");
                Reject(payRequest, NormalizedValue.CreateString(payAnswer)); Reject(moveRequestId, NormalizedValue.CreateString("A-02")); Reject(routeId, NormalizedValue.CreateString(target));
                Trace.Add(new { stage = "全链完成", moveId, routeOwner, slots, target, payOwner, recoveries = 3 });
            }
            private void Reject(string request, NormalizedValue answer)
            {
                string before = Business(); Need(!Engine.TrySubmitInteraction(request, 1, State.EffectRuntime.StateRevision, answer, out _), "旧答案被再次接受");
                Drain(); Need(Business() == before, "旧答案改变业务状态");
            }
        }
    }
}
