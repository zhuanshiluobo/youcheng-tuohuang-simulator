using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using YC.Application.Presentation;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
#if !QY007_CLR_DIAGNOSTIC
    public sealed class CharacterMiscR8Tests
    {
        [NUnit.Framework.TestCase("gavial_target")]
        [NUnit.Framework.TestCase("gavial_empty")]
        [NUnit.Framework.TestCase("muelsyse_recycle")]
        [NUnit.Framework.TestCase("muelsyse_decline")]
        [NUnit.Framework.TestCase("jessica_stored")]
        [NUnit.Framework.TestCase("tin_strategy")]
        [NUnit.Framework.TestCase("tin_no_move")]
        public void ActualCharacterRulesKeepTheirTargetsAndContinuation(string mode)
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try
            {
                var result = CharacterMiscR8Checks.Run(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), mode);
                artifacts.WriteLabeledText("角色四项隔离定向结果", JsonConvert.SerializeObject(result));
            }
            finally { artifacts.Finish(); }
        }
    }
#endif

    // 真实注册角色、实际实例及生产效果树的隔离规则检查。直接布置隔离初态，不代表正常局或画面验收。
    internal static class CharacterMiscR8Checks
    {
        private static ExternalContentPack installed;
        private static string installedRoot;
        internal static ExternalContentPack Installed(string root)
        {
            if (installed == null || installedRoot != root) { installed = ExternalContentPack.Load(root); installedRoot = root; }
            return installed;
        }
        internal static object Run(string root, string mode)
        {
            Fixture f = null;
            try
            {
                f = new Fixture(Installed(root));
                switch (mode)
                {
                    case "gavial_target": f.Gavial(true); break;
                    case "gavial_empty": f.Gavial(false); break;
                    case "muelsyse_recycle": f.Muelsyse(true); break;
                    case "muelsyse_decline": f.Muelsyse(false); break;
                    case "jessica_stored": f.Jessica(); break;
                    case "tin_strategy": f.TinStrategy(); break;
                    case "tin_no_move": f.TinNoMove(); break;
                    default: throw new ArgumentException(mode);
                }
                return new { success = true, mode, scope = "真实Lua/注册入口/效果树与恢复的隔离直接检查，非Unity/Player/正常局/UI", installedHash = installed.ContentHash,
                    activeHash = f.State.ContentPackHash, f.Trace };
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(JsonConvert.SerializeObject(new { success = false, mode, error = error.ToString(), trace = f?.Trace, state = f?.State }), error);
            }
        }
        private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        private sealed class Fixture
        {
            internal GameState State;
            internal readonly List<object> Trace = new List<object>();
            private readonly ExternalContentPack pack;
            private readonly EffectRegistry registry;
            private readonly MapQueryService map;
            private EffectTreeExecutor Engine => new EffectTreeExecutor(State, registry);
            internal Fixture(ExternalContentPack source)
            {
                var configuration = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3,
                    EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
                var catalog = ActiveContentCatalogBuilder.Build(source, configuration);
                configuration.ContentPackHash = catalog.ContentPackHash;
                pack = source.CreateActiveSnapshot(configuration);
                var recipe = catalog.SetupRecipe;
                // 隔离定位初态显式布置资源点；正式开局的探索过程不在本组规则检查范围。
                foreach (var location in StaticMapDefinitions.Resolve(configuration.MapId).Locations.Where(l => !l.IsRedZone))
                    recipe.Entries.Add(new SetupRecipeEntry { EntryId = "qy007.point." + location.LocationId, DefinitionId = "core.resource_point",
                        TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
                State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                    Enumerable.Range(1, 3).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "隔离玩家" + p, Color = (PlayerColor)(p - 1) }).ToList(),
                    configuration, 73, recipe, catalog.ContentDefinitions, new[] { 1, 2, 3 }, "qy007.characters.r8", null);
                map = new MapQueryService(StaticMapDefinitions.Resolve(State.MapId)); var influence = new InfluenceService(map);
                registry = new EffectRegistry(); catalog.Register(registry);
                registry.SetupRecipe = recipe;
                ResourceEffectExecutor.Register(registry); InfluenceEffectExecutor.Register(registry, influence);
                ItemTransferEffectExecutor.Register(registry); RepeatEffectExecutor.Register(registry);
                ContentPrimitiveEffectExecutor.Register(registry, map, influence); FacilityEntryEffectExecutor.Register(registry);
                CharacterLifecycleService.Register(registry); ResourceCollectionEffectExecutor.Register(registry);
                var tokens = new ResourceTokenService(); var travel = new TravelCostService(map); var decks = new EventDeckService(73);
                CityMoveEffectExecutor.Register(registry, map, influence, new CityMovementService(map, influence, travel, decks, tokens), travel, tokens);
                EventCardEffectExecutor.Register(registry, map, influence, decks, tokens);
                new RoundExecutionService(registry);
                State.Phase = GamePhase.ActionRound1; State.Round = State.ActionRound = 1;
                State.CurrentPlayerId = State.StartPlayerId = 1; State.PendingChoice = null;
                State.EffectRuntime = new EffectRuntimeState { PlayerOrderSnapshot = new List<int> { 1, 2, 3 } };
                foreach (var player in State.Players)
                {
                    player.RemainingMainActionsThisTurn = 1;
                    player.Resources = new ResourceSet { Originium = 20, OriginiumShard = 20, Iron = 20, GoldVoucher = 80, PureOriginium = 3 };
                }
                State.Map.OpenLocationIds.Clear(); State.Map.OpenLocationIds.AddRange(map.Map.Locations.Where(l => !l.IsRedZone).Select(l => l.LocationId));
                foreach (var point in State.ItemZones.Items.Where(i => i.TypeId == "core.resource_point_token"))
                    point.LocalState = Object("resourceType", NormalizedValue.CreateInteger((int)ResourceType.Originium), "amount", NormalizedValue.CreateInteger(2));
                Valid();
            }
            private ZoneInstanceState Zone(string local, int player = 0) => State.ItemZones.Zones.Single(z => z.LocalZoneId == local && (player == 0 || z.OwnerPlayerId == player) &&
                (local != "covered" || z.TemplateId == "core.zone.character_covered"));
            private ItemInstanceState Item(string id) => State.ItemZones.Items.Single(i => i.ItemId == id);
            private ItemInstanceState Card(string definition) => State.ItemZones.Items.First(i => i.DefinitionId == definition &&
                (i.OwnerSubject.Kind != ItemSubjectKind.Player || i.OwnerSubject.PlayerId == 1));
            private void Valid() => Need(new ItemZoneQuery().TryValidate(State, out string why), "隔离状态非法：" + why);
            private void Place(ItemInstanceState item, ZoneInstanceState zone, string slot = "")
            {
                foreach (var order in State.ItemZones.Orders) if (order.ItemIds.Remove(item.ItemId)) order.OrderRevision++;
                item.ZoneId = zone.ZoneId; item.SlotId = slot;
                var destination = State.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zone.ZoneId);
                if (destination != null) { destination.ItemIds.Add(item.ItemId); destination.OrderRevision++; }
            }
            private InteractionRequest Open(string type = null) => State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open" && (type == null || r.InteractionTypeId == type));
            private void Pump()
            {
                Need(!Engine.RunUntilQuiescent().Faulted, State.EffectRuntime.LastFaultMessage); Valid();
                var failed = State.EffectRuntime.EffectNodes.Where(n => n.Status == EffectNodeStatus.Failed).Select(n => n.EffectTypeId + ":" + n.FailureReason).ToArray();
                Trace.Add(new { stage = "停稳", request = Open()?.InteractionTypeId, failed });
            }
            private string Run(EffectSpec spec) { string id = Engine.CreateRoot(spec); Pump(); return id; }
            private void Answer(NormalizedValue answer, InteractionRequest request = null)
            {
                request = request ?? Open(); Need(request != null, "没有预期请求");
                Trace.Add(new { stage = "真实请求提交", request.InteractionTypeId, request.MinSelections, request.MaxSelections, candidates = request.CandidateIds.ToArray(), answer });
                // 多人共同采集请求同时打开，提交使用本人当前可见状态的revision。
                Need(Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, State.EffectRuntime.StateRevision, answer, out string why), why); Pump();
            }
            private void Answer(string value) => Answer(NormalizedValue.CreateString(value));
            private void Recover(string stage)
            {
                var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(State)), registry);
                Need(recovered.Succeeded, recovered.Diagnostic); State = recovered.State; Valid();
                Trace.Add(new { stage, revision = State.EffectRuntime.StateRevision, request = Open()?.GetStableInteractionId() });
            }
            private string Start(string definition, string mode)
            {
                var card = Card(definition);
                if (card.ZoneId != Zone("hand", 1).ZoneId)
                    Run(MoveSpec(1, new[] { card.ItemId }, card.ZoneId, Zone("hand", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
                Valid();
                Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(card.ItemId);
                return Run(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, mode));
            }
            private void Mainline(string nodeType, GamePhase phase)
            {
                State.Phase = phase;
                var main = new MainlineNodeRuntimeState { NodeId = "qy007.r8." + nodeType, NodeTypeId = nodeType, PlayerId = 1,
                    RoundNumber = State.Round, ActionRound = State.ActionRound, Status = EffectNodeStatus.Running };
                State.EffectRuntime.MainNodes.Add(main); State.EffectRuntime.ActiveMainNodeId = main.NodeId;
                string id = Engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
                State.EffectRuntime.MainNodes.Single(n => n.NodeId == main.NodeId).ExecutionEffectId = id; Pump();
            }
            private void CleanupOption(string source)
            {
                Mainline(RoundMainlineNodeTypeIds.PlayerCleanupWindow, GamePhase.Cleanup);
                var option = registry.GetSettlementOptions(State, 1).Single(o => o.SourceItemId == source && o.IsEnabled);
                Need(Engine.TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision, out _, out string why), why); Pump();
            }
            private ItemInstanceState Influence(int player, string location)
            {
                var marker = State.ItemZones.Items.First(i => i.ZoneId == Zone("supply", player).ZoneId && i.TypeId == "core.player_marker");
                string slot = InfluenceService.GetLocationSlotId(location, 0);
                Place(marker, State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.influence_slots" && z.LegalSlotIds.Contains(slot)), slot); Valid(); return marker;
            }
            internal void Gavial(bool hasInfluence)
            {
                const string definition = "infected.character.gavial_the_invincible";
                Start(definition, "tactic"); Need(Open() != null, "嘉维尔初始放置未开启"); Answer(Open().CandidateIds.First());
                string token = State.ItemZones.Items.Single(i => i.TypeId == "infected.gavial_token").ItemId;
                string target = new TokenMovementProfileRegistry().GetDestinationLocationIds(State, token, "core.adjacent_location_token_move").First();
                string marker = hasInfluence ? Influence(2, target).ItemId : "";
                var resources = JsonConvert.SerializeObject(State.Players.Select(p => p.Resources));
                CleanupOption(Card(definition).ItemId); Recover("嘉维尔待相邻资源点恢复"); Answer(target);
                Need(TokenMovementProfileRegistry.GetLocationId(State, token) == target, "未移动真实指示物");
                if (hasInfluence)
                {
                    var request = Open("effect.influence.move.source"); Need(request != null && request.CandidateIds.SequenceEqual(new[] { marker }), "没有只取到达资源点真实影响力");
                    var prompt = request.Presentation.Prompt;
                    var argument = prompt.Arguments.Single(a => a.Name == "resourcePoint");
                    Need(argument.Kind == "text" && argument.Value.StringValue == target, "资源点编号参数未绑定真实位置");
                    var rendered = new GameplayTextResolver(pack.Presentation).Resolve(prompt, new GameplayTextContext { Catalog = pack.Presentation }).ToPlainText();
                    Need(rendered == "移动" + target + "上的1个影响力。", "提示渲染错误：" + rendered);
                    Trace.Add(new { stage = "真实提示渲染", target, rendered, marker });
                    Recover("嘉维尔待影响力选择恢复"); Answer(marker);
                    var destination = Open(); Need(destination != null, "没有移动目的地续接");
                    string chosen = destination.CandidateIds.First(); Recover("嘉维尔待移动目的地恢复"); Answer(chosen);
                    Need(Item(marker).SlotId == chosen && Item(marker).OwnerSubject.PlayerId == 2, "未移动原主真实实例");
                }
                else Need(Open("effect.influence.move.source") == null && !State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == InfluenceEffectTypeIds.MoveInfluence), "空目标仍生成移动");
                Need(JsonConvert.SerializeObject(State.Players.Select(p => p.Resources)) == resources, "指示物/影响力移动额外扣资源");
                Need(!State.EffectRuntime.RuleEvents.Any(e => e.EventType == CityMoveEventTypeIds.CityMoveCompleted), "误走城市移动事件");
            }
            internal void Muelsyse(bool recycle)
            {
                const string definition = "infected.character.muelsyse";
                Start(definition, "tactic"); Answer("hand"); Answer("hand"); Answer("discard");
                string selected = Open().CandidateIds.First(); Answer(selected);
                var cover = State.CharacterCoverSlots.Single(s => s.Reusable && s.SourceEffectId == "character-card:" + Card(definition).ItemId);
                Need(cover.CardInstanceId == selected && Item(selected).ZoneId == Zone("covered", 1).ZoneId, "未实际盖放流形");
                string unrelated = State.ItemZones.Items.Single(i => i.TypeId == "core.generated_character_card" && i.ZoneId == Zone("discard", 1).ZoneId).ItemId;
                var dockLocations = map.Map.Locations.Where(l => l.CanDockCity && !l.IsRedZone).Take(3).ToArray();
                for (int p = 1; p <= 3; p++)
                {
                    var parent = State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == dockLocations[p - 1].LocationId);
                    Place(State.ItemZones.Items.Single(i => i.TypeId == "core.mobile_city" && i.OwnerSubject.PlayerId == p),
                        State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.city_dock"));
                }
                foreach (var player in State.Players) player.ResourceCollectionStartGoldVoucher = player.Resources.GoldVoucher;
                Mainline(RoundMainlineNodeTypeIds.Collection, GamePhase.ResourceCollection);
                var request = Open(); Need(request != null && request.CandidateIds.SequenceEqual(new[] { "recycle" }) && request.MinSelections == 0, "回手仍有保留/空分支或不可放弃");
                Recover("缪尔赛思采集前回手待答恢复");
                Answer(recycle ? NormalizedValue.CreateString("recycle") : NormalizedValue.CreateArray(new List<NormalizedValue>()));
                Need(Item(selected).ZoneId == Zone(recycle ? "hand" : "covered", 1).ZoneId, "回手/放弃结果错误");
                Need(Item(unrelated).ZoneId == Zone("discard", 1).ZoneId, "错误回手其他流形");
                Need(Card(definition).ZoneId == Zone("persistent", 1).ZoneId, "回手误消耗持续角色");
                Need(Open()?.CandidateIds.Contains("recycle") != true, "采集被重复回手选择阻塞");
                Trace.Add(new { stage = "回手处理后采集续接", recycle, next = Open()?.InteractionTypeId, State.Phase });
                Recover("缪尔赛思回手或放弃之后恢复");
                // 本组验回手后的续接边界：处理当前revision对应的一个实际采集请求。
                // 其余并行玩家的有序请求合同属于Session流程，不用直接executor越过其currentness。
                var selection = State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection" &&
                    r.StateRevision == State.EffectRuntime.StateRevision);
                string commit = selection.CandidateIds.Single(id => id.EndsWith(".commit", StringComparison.Ordinal));
                Answer(NormalizedValue.CreateArray(new[] { NormalizedValue.CreateStableReference("candidate", commit) }.ToList()), selection);
                Need(State.FindPlayer(selection.AnsweringPlayerId).HasCollectedResourcesThisRound, "后继真实采集未完成");
                Trace.Add(new { stage = "后继首个合法采集已提交", player = selection.AnsweringPlayerId,
                    pendingCollectors = State.EffectRuntime.InteractionRequests.Count(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection"), State.Phase });
                Recover("缪尔赛思后继首个采集提交恢复");
            }
            private string StoreFacility()
            {
                string closure = "operator.closure"; Start(closure, "tactic");
                var request = Open(); string held = request.CandidateIds.First(id => id != "choice.skip"); Answer(held);
                if (Open()?.MinSelections == 0) Answer(NormalizedValue.CreateArray(new List<NormalizedValue>()));
                var zone = State.ItemZones.Zones.Single(z => z.ParentId == Card(closure).ItemId && z.LocalZoneId == "orders");
                Need(Item(held).ZoneId == zone.ZoneId, "未产生正式存储设施"); return held;
            }
            internal void Jessica()
            {
                // 原正式可见设施库存先由可露希尔的真实计谋存到牌上，再交杰西卡免费建设。
                string held = StoreFacility(); string storage = Item(held).ZoneId;
                State.FindPlayer(1).UsedCharacterThisRound = State.FindPlayer(1).UsedCharacterThisTurn = State.FindPlayer(1).CharacterCardLockedThisTurn = false;
                var city = State.ItemZones.Items.Single(i => i.TypeId == "core.mobile_city" && i.OwnerSubject.PlayerId == 1);
                var location = map.Map.Locations.First(l => l.CanDockCity && !l.IsRedZone);
                var parent = State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location.LocationId);
                Place(city, State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.city_dock"));
                const string definition = "infected.character.jessica_the_liberated";
                var resources = JsonConvert.SerializeObject(State.FindPlayer(1).Resources);
                Start(definition, "tactic"); Answer("continue");
                var build = Open(); Need(build != null && build.CandidateIds.Contains(held), "杰西卡候选未包含牌上存储设施");
                Recover("杰西卡待选择存储设施恢复"); Answer(held);
                // 免费建设选牌后只需选城市面板位置，不得再扣设施费用。
                var place = Open(); Need(place != null, "免费建设缺少放置续接"); Answer(place.CandidateIds.First());
                int guard = 0;
                while (Open() != null && State.FacilityCharacterBindings.All(b => b.CharacterItemId != Card(definition).ItemId) && guard++ < 20)
                    Answer(Open().CandidateIds.Contains("decline") ? "decline" : Open().CandidateIds.First());
                var binding = State.FacilityCharacterBindings.Single(b => b.Active && b.CharacterItemId == Card(definition).ItemId);
                Need(binding.FacilityItemId == held && Item(held).ZoneId != storage, "绑定未指向建设的实际存储设施");
                Need(State.ItemZones.Zones.Single(z => z.ZoneId == Item(held).ZoneId).TemplateId == "core.zone.city_facility_cell", "存储设施未进实际城市格");
                Need(JsonConvert.SerializeObject(State.FindPlayer(1).Resources) == resources, "免费建设错误扣费");
                Recover("杰西卡存储设施建成并绑定恢复");
                Trace.Add(new { stage = "存储设施建成与绑定", held, storage, binding.BindingId, binding.CharacterItemId, binding.FacilityItemId });
                int cost = FacilityCardDatabase.Get(State, Item(held).DefinitionId).GoldVoucherCost + 2;
                int gold = State.FindPlayer(1).Resources.GoldVoucher, score = State.FindPlayer(1).Score;
                CleanupOption(Card(definition).ItemId); Recover("杰西卡绑定存储设施结算付款待答恢复");
                Answer(Open().CandidateIds.First(id => id != "decline"));
                Need(State.FindPlayer(1).Resources.GoldVoucher == gold - cost && State.FindPlayer(1).Score == score + 2, "绑定存储设施没有按卡面费用加2结算一次");
                Need(State.FacilityCharacterBindings.Single(b => b.FacilityItemId == held).Active == false && Card(definition).ZoneId == Zone("discard", 1).ZoneId,
                    "结算未解除实际存储设施绑定并将杰西卡回弃牌");
                Recover("杰西卡存储设施后继结算完成恢复");
            }
            internal void TinStrategy()
            {
                State.FindPlayer(1).Resources.GoldVoucher = 15; int score = State.FindPlayer(1).Score, pure = State.FindPlayer(1).Resources.PureOriginium;
                Start("tin-man", "strategy"); Need(State.FindPlayer(1).Score == score + 1, "锡人未先得分");
                Recover("锡人第一独立条件待答恢复"); Answer(NormalizedValue.CreateBoolean(false));
                Need(State.FindPlayer(1).Resources.GoldVoucher == 15, "第一放弃仍扣款");
                var second = Open(); Need(second != null && second.Presentation.Prompt.Arguments.Single(a => a.Name == "cost").Value.IntegerValue == 15, "放弃第一条件封锁第二条件");
                Recover("锡人第二独立条件待答恢复"); Answer(second.CandidateIds.First(id => id != "decline"));
                Need(State.FindPlayer(1).Resources.GoldVoucher == 0 && State.FindPlayer(1).Resources.PureOriginium == pure + 1, "第二条件未独立支付15并获1至纯源石");
                Need(State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == EffectTypeIds.Condition) == 2, "锡人不是两个独立条件");
            }
            internal void TinNoMove()
            {
                var hand = State.ItemZones.Items.Where(i => i.ZoneId == Zone("hand", 1).ZoneId && i.DefinitionId != "tin-man").Take(2).ToArray();
                Need(hand.Length == 2, "隔离初态缺弃牌"); foreach (var item in hand) Place(item, Zone("discard", 1)); Valid();
                int gold = State.FindPlayer(1).Resources.GoldVoucher;
                Start("tin-man", "tactic");
                Need(hand.All(i => Item(i.ItemId).ZoneId == Zone("hand", 1).ZoneId), "锡人未把真实弃牌全部回手");
                var first = Open(); Need(first != null && first.CandidateIds.Contains("move-influence") && first.CandidateIds.Contains("gain-gold"), "无影响力时被预先删除合法计谋选项");
                Recover("锡人无影响力仍可选移动分支恢复"); Answer("move-influence");
                var next = Open(); Need(next != null && next.CandidateIds.Contains("gain-gold"), "无目标移动封锁后续弃牌奖励"); Answer("gain-gold");
                Need(State.FindPlayer(1).Resources.GoldVoucher == gold + 5, "后续5金券未结算");
                Need(State.ItemZones.Items.Count(i => hand.Any(h => h.ItemId == i.ItemId) && i.ZoneId == Zone("hand", 1).ZoneId) == 2, "回手实例数量错误");
                Recover("锡人无目标分支及后续奖励完成恢复");
            }
        }
    }
}
