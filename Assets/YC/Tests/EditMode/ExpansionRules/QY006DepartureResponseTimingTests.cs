using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 仅规则隔离装配。真实选人/选面、角色使用、Remove/Replace、Lua及Host均用生产合同；不计正常局。
    public sealed class QY006DepartureResponseTimingTests
    {
        [TestCase("decline-zero")]
        [TestCase("decline-two")]
        [TestCase("store-zero")]
        [TestCase("batch-decline")]
        [TestCase("batch-mixed")]
        [TestCase("a05")]
        [TestCase("t09-decline")]
        [TestCase("t09-store")]
        [TestCase("without-destination")]
        [TestCase("nonbatch")]
        public void RelatedDestinationsSettleBeforeTheRealLuaBuildsItsReward(string scenario) => RunCase(scenario);

        public static GameState LastState;
        public static readonly List<object> Observations = new List<object>();
        private static AgnesContentTestFixture current;
        private static int actor;
        private static EffectTreeExecutor Engine => new RoundExecutionService(current.Registry).CreateCommandExecutor(current.State);
        private static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        private static void Capture(string label)
        {
            LastState = current.State;
            current.Artifacts.WriteLabeledText(label, JsonConvert.SerializeObject(new { current.State.GameId, current.State.ContentPackHash,
                departures = current.State.EffectRuntime.InfluenceDepartures, responses = current.State.EffectRuntime.DeferredResponses,
                opens = current.State.EffectRuntime.InteractionRequests.Where(q => q.Status == "open"), observations = Observations }));
        }
        public static void RunCase(string scenario)
        {
            Observations.Clear(); LastState = null; actor = scenario.StartsWith("t09", StringComparison.Ordinal) ? 2 : 1;
            bool t09 = actor == 2, noDestination = scenario == "without-destination";
            using var f = new AgnesContentTestFixture(); current = f;
            try
            {
                if (scenario == "nonbatch")
                {
                    const string path = "entrepreneur/entrepreneur.round.A.3.json";
                    var json = JObject.Parse(File.ReadAllText(Path.Combine(f.Pack.RootPath, path)));
                    var source = json["abilitySources"].Single(v => (string)v["abilityId"] == "entrepreneur.A06.departure");
                    ((JArray)source["allowedOperations"]).Single(v => (string)v == "response.batch").Remove();
                    f.Pack = f.Pack.CreateWithOverrides(new Dictionary<string, string> { [path] = json.ToString() });
                    f.Artifacts.WriteLabeledText("非批次通用协议隔离", "只在内存内容副本移除response.batch，实际A06 Lua不变；不声称正式A06的默认声明为非批次。");
                }
                f.Formal(); CharacterCoverEffectExecutor.Register(f.Registry); f.Choose(scenario == "a05" ? 5 : 3, scenario == "a05" ? "A05" : "A06", declineGain: true);
                // 隔离初始手牌：把真实多萝西实例放入本人手牌，不造订阅/SourceRef/收据，之后正常Cover/Use。
                f.State.CurrentPlayerId = actor;
                var dorothy = f.State.ItemZones.Items.Single(i => i.DefinitionId == "operator.dorothy");
                var hand = f.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.character_hand" && z.OwnerPlayerId == actor);
                dorothy.OwnerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = actor };
                dorothy.ControllerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = actor };
                InitialPosition(dorothy, hand.ZoneId);
                if (!noDestination)
                {
                    f.Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: actor));
                    f.Answer(dorothy.ItemId); f.Run(CharacterLifecycleService.CreateUseSpec(actor, dorothy.ItemId, "tactic")); f.Answer("1");
                }
                var map = StaticMapDefinitions.CreateThreePlayerMap();
                int count = scenario.StartsWith("batch", StringComparison.Ordinal) ? 2 : 1;
                var targets = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    string slot = f.Registry.InfluenceService.GetLegalPlacementSlotIds(f.State, actor).First();
                    string id = f.Engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(actor, slot)); f.Pump();
                    targets.Add(Value(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).NormalizedResult, "influenceId").ReferenceId);
                }
                string supply = f.State.ItemZones.Items.Single(i => i.ItemId == targets[0]).OriginalSupplyZoneId;
                int stock = t09 ? f.State.ItemZones.Items.Count(i => i.ZoneId == supply && i.TypeId == "core.player_marker") : scenario == "decline-two" ? 2 : 0;
                var parked = f.State.ItemZones.Items.Where(i => i.ZoneId == supply && i.TypeId == "core.player_marker").Skip(stock).ToList();
                foreach (var item in parked) InitialPosition(item, f.Storage.ZoneId);
                Need(new ItemZoneQuery().TryValidate(f.State, out string inputReason), "隔离初始位置无效：" + inputReason);
                f.Artifacts.WriteLabeledText("隔离初始条件", "真实已选来源和激活收据保持；仅为库存边界人工配置手牌/卡上存量，不计正常局或页面验收。" + stock);
                f.Run(ResourceEffectSpecFactory.Gain(1, ResourceType.GoldVoucher, 20)); // 隔离准备真实资源，不伪造付款收据。
                int storedBefore = f.Stored;
                var calls = new List<Tuple<int, int, int>>();
                var bodies = (Dictionary<string, Func<AbilityActionCallContext, IList<EffectSpec>>>)typeof(EffectRegistry)
                    .GetField("abilityBodies", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Registry);
                var original = bodies["entrepreneur.A06.departure"];
                // 只观察原委托，不改参数、返回式、权限或内容；保真实Lua调用时点的证据。
                bodies["entrepreneur.A06.departure"] = call => {
                    int open = call.State.EffectRuntime.DeferredResponses.Count(r => r.Kind == "destination" && (r.Status == DeferredResponseStatus.Pending || r.Status == DeferredResponseStatus.Queued));
                    int realStock = call.State.ItemZones.Items.Count(i => i.ZoneId == supply && i.TypeId == "core.player_marker");
                    int n = Number(Value(call.OwnerNode?.NormalizedArguments, "triggerPayload"), "departureCount");
                    calls.Add(Tuple.Create(open, realStock, n));
                    Observations.Add(new { stage = "actual_ability_body_delegate", pendingDestinations = open, stock = realStock, departureCount = n,
                        ownerEffectId = call.OwnerNode?.EffectId, ownerType = call.OwnerNode?.EffectTypeId });
                    return original(call);
                };
                if (scenario == "a05")
                {
                    var registration = (EffectEventHandlerRegistration)typeof(EffectRegistry).GetMethod("FindEventHandler", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(f.Registry, new object[] { "entrepreneur.A05.tactic.completed" });
                    var callback = registration.Handler;
                    var field = typeof(EffectEventHandlerRegistration).GetField("<Handler>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                    Need(field != null, "续接观测字段不存在");
                    field.SetValue(registration, new Func<EffectEventHandlerContext, IList<EffectSpec>>(context => {
                        if (Text(context.OwnerNode.ContinuationContext, "stage") == "after_map_gain")
                        {
                            int pending = context.State.EffectRuntime.DeferredResponses.Count(v => v.Kind == "destination" && (v.Status == DeferredResponseStatus.Pending || v.Status == DeferredResponseStatus.Queued));
                            int actualStock = context.State.ItemZones.Items.Count(i => i.ZoneId == supply && i.TypeId == "core.player_marker");
                            calls.Add(Tuple.Create(pending, actualStock, 0));
                            Observations.Add(new { stage = "actual_A05_after_map_gain_continuation", pendingDestinations = pending, stock = actualStock, effectId = context.OwnerNode.EffectId });
                        }
                        return callback(context);
                    }));
                }
                f.State.RuleConfiguration.PlayerStageSchedulingMode = PlayerStageSchedulingMode.Concurrent;
                var rounds = new RoundExecutionService(f.Registry);
                PrepareWindow(rounds);
                string root;
                if (scenario == "a05")
                {
                    root = WithCommand(() => {
                        var option = f.Action("entrepreneur.A05.tactic.special"); Need(option.Enabled, option.DisabledReason);
                        return Engine.CreateRoot(AbilityActionExecutor.Create(option, "qy006.r10.a05", Object()));
                    });
                    Answer(f.Open, NormalizedValue.CreateString("remove"));
                    var request = f.Open;
                    Need(request.CandidateIds.Contains(targets[0]), "A05没有真实移除候选");
                    Answer(request, request.AnswerSchema == "candidate_ids" || request.AnswerSchema == "candidate_id_array"
                        ? Strings(new[] { targets[0] }) : NormalizedValue.CreateString(targets[0]));
                }
                else
                    root = WithCommand(() => { Need(Engine.TryCreatePlayerActionEffect(actor, count == 1
                        ? InfluenceEffectSpecFactory.RemoveInfluence(actor, targets[0])
                        : InfluenceDepartureBatchExecutor.Create(actor, targets.Select(t => InfluenceEffectSpecFactory.RemoveInfluence(actor, t))), "1", "qy006.r10.remove", out string id), "移除入口拒绝"); return id; });
                Capture("去向待答首次现场");
                if (!noDestination) Need(f.State.EffectRuntime.DeferredResponses.Any(r => r.Kind == "destination" && r.Status == DeferredResponseStatus.Pending), "未进入真实去向响应");
                if (t09) Need(f.State.EffectRuntime.DeferredResponses.Where(r => r.Kind == "destination").Select(r => r.AbilityId).OrderBy(x => x)
                    .SequenceEqual(new[] { "entrepreneur.T09.departure", "operator.dorothy.tactic" }.OrderBy(x => x)), "未形成实际T09/多萝西两个独立来源");
                if (!noDestination) Need(calls.Count == 0, "修前故障：去向仍待答时已调用收益Lua正文/实际完成续接，预演也算提前调用");
                if (scenario == "a05") Need(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status != EffectNodeStatus.Completed, "带实际完成续接的操作在相关去向结清前完成");
                int independentPlayer = actor == 1 ? 2 : 1;
                int vouchers = f.State.FindPlayer(independentPlayer).Resources.GoldVoucher;
                Engine.CreateRoot(ResourceEffectSpecFactory.Gain(independentPlayer, ResourceType.GoldVoucher, 1)); Pump();
                Need(f.State.FindPlayer(independentPlayer).Resources.GoldVoucher == vouchers + 1, "无关独立效果被全面冻结");
                Restore("去向待答Host");
                int answered = 0;
                while (f.State.EffectRuntime.DeferredResponses.Any(r => r.Kind == "destination" && (r.Status == DeferredResponseStatus.Pending || r.Status == DeferredResponseStatus.Queued)))
                {
                    var response = f.State.EffectRuntime.DeferredResponses.First(r => r.Kind == "destination" && r.Status == DeferredResponseStatus.Pending);
                    var request = f.State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open" && InfluenceDepartureProtocol.IsWithin(f.State, q.OwnerEffectId, response.ResponseRootEffectId));
                    string answer = scenario == "store-zero" || scenario == "t09-store" || scenario == "batch-mixed" && answered == 0 ? "store" : "decline";
                    Answer(request, NormalizedValue.CreateString(answer)); answered++;
                    Capture("去向回答后" + answered);
                    if (f.State.EffectRuntime.DeferredResponses.Any(r => r.Kind == "destination" && (r.Status == DeferredResponseStatus.Pending || r.Status == DeferredResponseStatus.Queued)))
                    { Need(calls.Count == 0, "批次尚有相关去向待答时收益Lua已读取库存"); Restore("批次中途Host"); }
                    Need(answered < 10, "去向队列未有界收敛");
                }
                Need(calls.All(x => x.Item1 == 0), "收益Lua在去向结清前查询库存");
                if (t09)
                {
                    var d = f.State.EffectRuntime.InfluenceDepartures.Single();
                    Need(d.Status == (scenario == "t09-store" ? "stored" : "returned"), "T09独立去向最终状态错误");
                    Need(f.State.ItemZones.Items.Count(i => i.ItemId == d.ItemId) == 1 && f.State.ItemZones.Items.Single(i => i.ItemId == d.ItemId).ZoneId == d.DestinationZoneId, "同物被复制或丢失唯一保管");
                    Need(!f.State.EffectRuntime.DeferredResponses.Any(r => r.Kind == "optional_effect"), "非本人离场混入A06批次收益");
                    Need(!f.State.EffectRuntime.AbilitySourceUses.Any(u => u.AbilityId == "entrepreneur.A06.departure"), "非本人离场消费A06来源");
                    Need(answered == (scenario == "t09-store" ? 1 : 2), "各来源的放弃/收容机会不独立");
                }
                else if (scenario == "store-zero")
                {
                    Need(f.State.ItemZones.Items.Single(i => i.ItemId == targets[0]).ZoneId != supply, "已收容物被错误返供");
                    Need(f.Stored == storedBefore, "空供应收益错误移动标记");
                    Need(calls.Count > 0, "结清后未执行资格判断");
                }
                else
                {
                    int returned = count - (scenario == "batch-mixed" ? 1 : 0);
                    Need(f.State.ItemZones.Items.Count(i => i.ZoneId == supply && i.TypeId == "core.player_marker") == stock + returned, "返供后真实供应不符");
                    if (scenario != "a05")
                    {
                        Need(calls.Count > 0 && calls.All(x => x.Item2 == stock + returned), "Lua没有读取结清后的实时库存");
                        var response = f.State.EffectRuntime.DeferredResponses.Single(r => r.Kind == "optional_effect");
                        if (scenario == "nonbatch") Need(response.BatchId.Length == 0, "通用非批次响应被强制聚合");
                        else
                        {
                            var offer = f.State.EffectRuntime.InfluenceDepartureBatches.SelectMany(b => b.Offers).Single(o => o.SourceKey == f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.A06.departure").SourceKey);
                            Need(offer.DepartureIds.Count == count, "批次实际n被库存裁小或重复");
                            Need(Number(Value(response.PendingBody[0].NormalizedArguments, "triggerPayload"), "departureCount") == count, "正式批次正文n错误");
                        }
                    }
                    Need(f.State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open" && !q.IsInternalMainlineInteraction()), "相关结清后未生成真实收益请求");
                    Restore("收益真实待答Host");
                    int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
                    for (int guard = 0; guard < 12; guard++)
                    {
                        var q = f.State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open" && !r.IsInternalMainlineInteraction());
                        if (q == null) break;
                        string value = q.CandidateIds.Contains("1") ? "1" : q.CandidateIds.Contains("continue") ? "continue" : q.CandidateIds.First();
                        Answer(q, NormalizedValue.CreateString(value));
                    }
                    Need(f.Stored == storedBefore + 1, "后继没有恰好转移一枚真实收益标记");
                    if (scenario != "a05") Need(f.State.FindPlayer(1).Resources.GoldVoucher == gold - 2, "A06已选一枚费用错误或重复");
                }
                Pump(); Restore("完成Host");
                int beforeStored = f.Stored; int beforeGold = f.State.FindPlayer(1).Resources.GoldVoucher;
                Pump(); Need(f.Stored == beforeStored && f.State.FindPlayer(1).Resources.GoldVoucher == beforeGold, "完成恢复后重复收益或扣费");
                Need(f.State.EffectRuntime.InfluenceDepartures.Count == count, "同一真实离场重复产生身份");
                Capture("最终有界结果");
            }
            finally { LastState = f.State; current = null; }
        }
        private static void InitialPosition(ItemInstanceState item, string zone)
        {
            foreach (var order in current.State.ItemZones.Orders) order.ItemIds.Remove(item.ItemId);
            item.ZoneId = zone; item.SlotId = "";
            current.State.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zone)?.ItemIds.Add(item.ItemId);
        }
        private static void PrepareWindow(RoundExecutionService rounds)
        {
            // 用真实builder建立当前选面轮的隔离主链；Smoke入口固定第1轮，不能覆盖第3/5轮轨道身份。
            var state = current.State; var runtime = state.EffectRuntime; var builder = new RoundMainChainBuilder();
            var order = new TurnOrderService().GetTurnOrder(state, actor).ToList();
            var chain = builder.Build(state.GameId, builder.CreateRoundExecutionId(state.GameId, state.Round, order), state.Round, order, actor);
            runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = chain.RoundNumber;
            runtime.CurrentRoundStartPlayerId = chain.StartPlayerId; runtime.PlayerOrderSnapshot = chain.PlayerOrderSnapshot;
            runtime.FirstMainNodeId = chain.FirstNodeId; runtime.MainNodes.AddRange(chain.Nodes);
            var action = chain.Nodes.First(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && n.PlayerId == actor && n.ActionRound == 1);
            foreach (var node in chain.Nodes.Where(n => n.MainlineIndex < action.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            runtime.ActiveMainNodeId = action.NodeId; action.Status = EffectNodeStatus.Ready;
            new RoundExecutionProjector().Project(state);
            Need(rounds.Advance(state).IsValid, "当前轮并发窗口未就绪");
        }
        private static string WithCommand(Func<string> run)
        {
            var state = current.State;
            Need(OperationWindowService.TryResolveCommandContext(state, new GameCommand { CommandId = "qy006.r10.cause", PlayerId = actor, Kind = GameCommandKind.EndAction }, out var cause, out var invalid), invalid?.Reason ?? "命令上下文不存在");
            state.EffectRuntime.CurrentOperationContext = cause;
            try { string id = run(); Pump(); return id; }
            finally { state.EffectRuntime.CurrentOperationContext = null; }
        }
        private static void Pump()
        {
            var result = Engine.RunUntilQuiescent(); LastState = current.State;
            Need(!result.Faulted, "实际内核故障：" + current.State.EffectRuntime.LastFaultMessage);
            RoundExecutionService.RefreshConcurrentInteractionRevisions(current.State);
        }
        private static void Answer(InteractionRequest q, NormalizedValue answer)
        {
            RoundExecutionService.RefreshConcurrentInteractionRevisions(current.State);
            Need(Engine.TrySubmitInteraction(q.GetStableInteractionId(), q.AnsweringPlayerId, q.StateRevision, answer, out string reason), reason); Pump();
        }
        private static void Restore(string label)
        {
            var before = RuleStateDigestService.Compute(current.State);
            var result = current.Artifacts.Recover(current.State, label, current.Registry);
            Need(result.Succeeded, result.Diagnostic); current.State = result.State;
            Need(RuleStateDigestService.Compute(current.State) == before, "Host恢复改变规则字段");
        }
    }
}
