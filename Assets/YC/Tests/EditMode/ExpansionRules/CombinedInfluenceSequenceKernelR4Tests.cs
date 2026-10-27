using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 正式Lua、正式注册/提交/Host恢复的内核隔离测试；人工初态不计正常游戏或页面验收。
    public sealed class CombinedInfluenceSequenceKernelR4Tests
    {
        private IsolatedTestArtifacts artifacts;
        private Fixture current;

        [SetUp]
        public void BeginEvidence() => artifacts = IsolatedTestArtifacts.BeginCase();

        [TearDown]
        public void PreserveEvidence()
        {
            try
            {
                if (current?.State != null)
                    artifacts.WriteLabeledText("组合选择内核完整现场", JsonConvert.SerializeObject(new {
                        scope = "隔离初态及真实内核执行，不计正常对局", current.Source, current.RootId,
                        current.ActionEventIndex, current.ActionNodeIndex, state = current.State
                    }));
            }
            finally { artifacts.Finish(); }
        }

        private static IEnumerable<TestCaseData> Sequences()
        {
            foreach (string source in new[] { "defense", "blacksteel", "hellagur" })
            {
                int count = source == "hellagur" ? 3 : 2;
                for (int mask = 0; mask < (1 << count); mask++)
                {
                    var operations = Enumerable.Range(0, count)
                        .Select(index => (mask & (1 << (count - index - 1))) == 0 ? "place" : "remove");
                    string sequence = string.Join("_", operations);
                    yield return new TestCaseData(source, sequence).SetName("组合内核_" + source + "_" + sequence);
                }
            }
        }

        [TestCaseSource(nameof(Sequences))]
        public void FormalSourceOffersOneCombinedChoiceAndExecutesTheSelectedOrder(string source, string sequence)
        {
            var f = Create(source);
            int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            f.Start();
            if (source == "defense") f.ConfirmDefensePayment();
            f.AssertCombinedRequest();
            f.Answer(sequence);
            string lastPlaced = null;
            foreach (string operation in sequence.Split('_'))
                lastPlaced = f.PerformStep(operation, operation == "remove" ? lastPlaced : null);
            f.AssertFinished(sequence);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - (source == "defense" ? 12 : 0)));
        }

        [Test]
        public void RemoveThenPlaceWaitsForRequiredResponseAndUsesTheReturnedLastMarker()
        {
            // 只有一枚仍可用的真实标记，开始已在地图；其余在明确的隔离初态中退盒。
            var f = Create("blacksteel", onlyOneAvailableMarker: true);
            string marker = f.SeededIds.Single();
            Assert.That(f.SupplyCount, Is.Zero, "先移除是后续放置获得库存的必要条件。");
            const string gateType = "qy009.r4.required_response";
            f.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("qy009.r4.removed.gate",
                InfluenceEventTypeIds.InfluenceRemoved, "", "qy009.r4.removed.gate", context =>
                    new List<EffectSpec> { EffectSpec.Create(LuaDomainEffectTypeIds.Choice,
                        Object("player", NormalizedValue.CreateString("1"),
                            "options", Strings(new[] { "response_done" }),
                            "minSelections", NormalizedValue.CreateInteger(1), "maxSelections", NormalizedValue.CreateInteger(1),
                            "interactionType", NormalizedValue.CreateString(gateType)), 1) }, EffectHandlerRole.Observer));
            // 此测试专用观察者只产生真实必需子响应；不改状态、不替代任何正式卡面来源/授权。
            f.Start(); f.AssertCombinedRequest(); f.Answer("remove_place");
            f.PerformStep("remove", marker);
            Assert.That(f.Open.InteractionTypeId, Is.EqualTo(gateType));
            Assert.That(f.ActionEvents.Select(e => e.EventType), Is.EqualTo(new[] { InfluenceEventTypeIds.InfluenceRemoved }));
            Assert.That(f.State.EffectRuntime.EffectNodes.Skip(f.ActionNodeIndex)
                .Any(n => n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence), Is.False,
                "第二步实际Place不得在前步及其必需响应结束前挂接。");
            f.Answer("response_done");
            Assert.That(f.SupplyCount, Is.EqualTo(1));
            f.PerformStep("place");
            Assert.That(f.State.Map.Influences.Any(i => i.InfluenceId == marker), Is.True,
                "原本无供应，后步须使用真实移除/响应结清后返回的同一实例。");
            f.AssertFinished("remove_place");
        }

        [Test]
        public void DecliningDefensePaymentCreatesNoCombinationOrInfluenceEffects()
        {
            var f = Create("defense");
            int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            f.Start();
            Assert.That(f.Open.CandidateIds, Does.Contain("decline"));
            f.Answer("decline");
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            string rhine = f.State.ItemZones.Items.Single(i => i.DefinitionId == "enterprise.rhine_lab").ItemId;
            Assert.That(new EnterpriseStateQuery(f.Registry.EnterpriseDefinitions).GetLevel(f.State, 1, rhine), Is.EqualTo(4),
                "奖励付款放弃只阻止右式，不回退此前已经完成的合作升级。");
            Assert.That(f.ActionEvents, Is.Empty);
            Assert.That(f.CombinedRequests, Is.Empty);
            Assert.That(f.State.EffectRuntime.EffectNodes.Skip(f.ActionNodeIndex)
                .Any(n => n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence ||
                    n.EffectTypeId == ContentPrimitiveEffectExecutor.SelectInfluence), Is.False);
            var condition = f.State.EffectRuntime.EffectNodes.Skip(f.ActionNodeIndex).Single(n => n.EffectTypeId == EffectTypeIds.Condition);
            Assert.That(Text(condition.NormalizedResult, "outcome"), Is.EqualTo("condition_not_met"),
                "主动放弃应保原条件未满足结果，不将它改成技术成功或执行右式。");
        }

        [TestCase("defense", "remove_place")]
        [TestCase("blacksteel", "place_remove")]
        [TestCase("hellagur", "place_remove_place")]
        public void HostRecoveryAfterFirstStepDoesNotAskAgainOrRepeatSettledWork(string source, string sequence)
        {
            var f = Create(source);
            int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            f.Start();
            if (source == "defense") f.ConfirmDefensePayment();
            f.AssertCombinedRequest(); f.Answer(sequence);
            var operations = sequence.Split('_');
            string firstPlaced = f.PerformStep(operations[0]);
            string targetRequest = f.Open.GetStableInteractionId();
            Assert.That(f.ActionEvents.Count, Is.EqualTo(1));
            f.Recover("组合已答且第一步实际完成_下一目标待答");
            Assert.That(f.Open.GetStableInteractionId(), Is.EqualTo(targetRequest));
            Assert.That(f.ActionEvents.Count, Is.EqualTo(1));
            Assert.That(f.CombinedRequests.Count, Is.EqualTo(1));
            Assert.That(f.CombinedRequests.Single().Status, Is.EqualTo("answered"));
            string lastPlaced = firstPlaced;
            foreach (string operation in operations.Skip(1))
                lastPlaced = f.PerformStep(operation, operation == "remove" ? lastPlaced : null);
            f.AssertFinished(sequence);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - (source == "defense" ? 12 : 0)));
            var eventIds = f.ActionEvents.Select(e => e.EventId).ToArray();
            f.Recover("所选组合全部结清"); f.Pump();
            Assert.That(f.ActionEvents.Select(e => e.EventId), Is.EqualTo(eventIds));
            Assert.That(f.CombinedRequests.Count, Is.EqualTo(1));
            Assert.That(f.Open, Is.Null);
            if (source == "defense")
                Assert.That(f.State.EffectRuntime.EffectNodes.Skip(f.ActionNodeIndex)
                    .Count(n => n.EffectTypeId == ResourceEffectTypeIds.Pay && n.Status == EffectNodeStatus.Completed), Is.EqualTo(1));
        }

        [Test]
        public void HellagurKeepsItsRequiredTwoOwnRemovalsAfterStrategyAndSourceExit()
        {
            var f = Create("hellagur");
            f.Start(); f.AssertCombinedRequest(); f.Answer("place_place_place");
            f.PerformStep("place"); f.PerformStep("place"); f.PerformStep("place");
            f.AssertFinished("place_place_place");
            Assert.That(f.Hellagur.ZoneId, Is.EqualTo(FindZone(f.State, "discard", 1).ZoneId));
            f.StartCleanup();
            var window = SettlementHostProtocol.FindActiveWindow(f.State);
            Assert.That(window, Is.Not.Null);
            var entry = window.Entries.Single(e => e.AbilityId == "infected.character.hellagur.strategy");
            Assert.That(entry.WholeEffectPolicy, Is.EqualTo(SettlementWholeEffectPolicy.Required));
            f.Recover("赫拉格来源已离场_收尾义务待执行");
            var option = f.Registry.GetSettlementOptions(f.State, 1)
                .Single(o => o.TargetEffectId == entry.TargetEffectId && o.IsEnabled);
            Assert.That(f.Engine.TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision,
                out _, out string diagnostic), Is.True, diagnostic);
            f.Pump();
            int before = f.State.Map.Influences.Count(i => i.OwnerSubject.SubjectType == "player" && i.OwnerSubject.PlayerId == 1);
            f.PerformStep("remove"); f.PerformStep("remove");
            Assert.That(f.State.Map.Influences.Count(i => i.OwnerSubject.SubjectType == "player" && i.OwnerSubject.PlayerId == 1), Is.EqualTo(before - 2));
            var target = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == entry.TargetEffectId);
            Assert.That(target.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.CombinedRequests.Count, Is.EqualTo(1), "收尾义务不得重新打开策略组合。");
        }

        private Fixture Create(string source, bool onlyOneAvailableMarker = false)
        {
            current = new Fixture(artifacts, source);
            current.Initialize(onlyOneAvailableMarker);
            return current;
        }

        private sealed class Fixture
        {
            private readonly IsolatedTestArtifacts artifacts;
            private ActiveContentCatalog catalog;
            private bool firstFault;
            internal readonly string Source;
            internal GameState State;
            internal EffectRegistry Registry;
            internal string RootId;
            internal int ActionEventIndex, ActionNodeIndex;
            internal readonly List<string> SeededIds = new List<string>();
            internal EffectTreeExecutor Engine => new EffectTreeExecutor(State, Registry);
            internal ItemInstanceState Hellagur => State.ItemZones.Items.Single(i => i.DefinitionId == "infected.character.hellagur");
            internal InteractionRequest Open => State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open" && !r.IsInternalMainlineInteraction());
            internal int SupplyCount => State.ItemZones.Items.Count(i => i.ZoneId == FindZone(State, "supply", 1).ZoneId && i.TypeId == "core.player_marker");
            internal List<RuleEvent> ActionEvents => State.EffectRuntime.RuleEvents.Skip(ActionEventIndex)
                .Where(e => e.EventType == InfluenceEventTypeIds.InfluencePlaced || e.EventType == InfluenceEventTypeIds.InfluenceRemoved).ToList();
            internal List<InteractionRequest> CombinedRequests => State.EffectRuntime.InteractionRequests
                .Where(r => r.CandidateIds.Contains("place_place") || r.CandidateIds.Contains("place_place_place")).ToList();

            internal Fixture(IsolatedTestArtifacts artifacts, string source) { this.artifacts = artifacts; Source = source; }

            internal void Initialize(bool onlyOne)
            {
                var configuration = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3,
                    EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" },
                    PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential };
                // 在统一测试启动时读取017最终整合内容；本轮不复制/覆盖基础texts或pack。
                catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core")), configuration);
                configuration.ContentPackHash = catalog.ContentPackHash;
                State = catalog.SetupRecipe.Assemble(new GameState { GameId = "qy009.r4.isolated", MapId = configuration.MapId,
                    RuleConfiguration = configuration, ContentPackHash = catalog.ContentPackHash, ContentDefinitions = catalog.ContentDefinitions,
                    Round = 1, MaxRounds = 8, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1,
                    LaunchPlayerOrder = new List<int> { 1, 2, 3 }, Players = Enumerable.Range(1, 3)
                        .Select(id => new PlayerState { PlayerId = id, Name = "隔离玩家" + id, Resources = new ResourceSet { GoldVoucher = 30 } }).ToList() },
                    StaticMapDefinitions.CreateThreePlayerMap(), 17);
                Registry = NewRegistry();
                State.FindPlayer(1).Resources.GoldVoucher = 30;
                State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2, 3 };
                var map = StaticMapDefinitions.CreateThreePlayerMap();
                foreach (var location in map.Locations.Where(l => !l.IsRedZone))
                    if (!State.Map.OpenLocationIds.Contains(location.LocationId)) State.Map.OpenLocationIds.Add(location.LocationId);
                var locationZone = State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == "A-03");
                var dock = State.ItemZones.Zones.Single(z => z.ParentId == locationZone.ZoneId && z.TemplateId == "core.zone.city_dock");
                InitialPosition(State.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == 1), dock.ZoneId);
                PrepareBoard("enterprise.rhine_lab", 3); PrepareBoard("enterprise.blacksteel", 4);
                var rhine = Item("enterprise.rhine_lab"); var defense = Item("enterprise.department.defense");
                InitialPosition(defense, State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.department_display").ZoneId);
                var relation = State.ItemZones.Relations.SingleOrDefault(r => r.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation && r.SourceItemId == rhine.ItemId);
                if (relation == null) State.ItemZones.Relations.Add(new ItemRelationState { RelationId = StableIdFactory.Create("qy009.r4.department", State.GameId),
                    RelationTypeId = EnterpriseStateQuery.ActiveDepartmentRelation, SourceItemId = rhine.ItemId, TargetItemId = defense.ItemId });
                else relation.TargetItemId = defense.ItemId;
                if (onlyOne)
                {
                    string retired = State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.box_retired").ZoneId;
                    foreach (var item in Supply().Skip(1).ToArray()) InitialPosition(item, retired);
                }
                var citySlots = Enumerable.Range(0, map.Locations.Single(l => l.LocationId == "A-03").InfluenceSlotCount)
                    .Select(index => InfluenceService.GetLocationSlotId("A-03", index));
                var routes = map.Routes.Where(route => route.FromLocationId == "A-03" || route.ToLocationId == "A-03" || route.CoveredLocationIds.Contains("A-03"));
                var slots = citySlots.Concat(routes.SelectMany(route => Enumerable.Range(0, route.InfluenceSlotCount)
                    .Select(index => InfluenceService.GetRouteSlotId(route.RouteId, index)))).Take(onlyOne ? 1 : 3).ToArray();
                Assert.That(slots.Length, Is.EqualTo(onlyOne ? 1 : 3), "隔离地图必须提供初态合法槽。");
                foreach (string slot in slots)
                {
                    Assert.That(InfluenceSlotReference.TryParse(new MapQueryService(map), slot, out var parsed, out string diagnostic), Is.True, diagnostic);
                    var parent = State.ItemZones.Zones.Single(z => z.TemplateId == (parsed.Kind == InfluenceSlotKind.Location ? "core.zone.map_location" : "core.zone.map_route") &&
                        z.LocalZoneId == (parsed.Kind == InfluenceSlotKind.Location ? parsed.LocationId : parsed.RouteId));
                    var zone = State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.influence_slots");
                    var marker = Supply().First(); InitialPosition(marker, zone.ZoneId, slot); SeededIds.Add(marker.ItemId);
                }
                Valid();
                var enterprises = new EnterpriseStateQuery(Registry.EnterpriseDefinitions);
                Assert.That(State.FindPlayer(1).CityLocationId, Is.EqualTo("A-03"), "隔离城市初态缺失。");
                Assert.That(SeededIds.All(id => State.Map.Influences.Any(i => i.InfluenceId == id && i.OwnerSubject.PlayerId == 1)), Is.True,
                    "隔离在场标记未进入真实地图投影。");
                Assert.That(enterprises.GetLevel(State, 1, rhine.ItemId), Is.EqualTo(3), "隔离莱茵合作级不足，不能据此判断4级奖励失败。");
                Assert.That(enterprises.GetActiveDepartmentItemId(State, rhine.ItemId), Is.EqualTo(defense.ItemId), "隔离防卫科授权关系缺失。");
                Assert.That(enterprises.GetSpecialCandidates(State, 1, 1, 0, new[] { Item("enterprise.blacksteel").ItemId })
                    .Any(c => c.SpecialId == "enterprise.blacksteel.level_4.special"), Is.True, "隔离黑钢4级特效未解锁。");
                artifacts.WriteLabeledText("隔离输入与正式内容身份", JsonConvert.SerializeObject(new {
                    source = Source, catalog.ContentPackHash, seed = 17, initialOwnMarkers = SeededIds,
                    initialSupply = SupplyCount, initialGold = State.FindPlayer(1).Resources.GoldVoucher,
                    note = "正式配方后人工设置城市/合作级/科室关系/金券和已在场标记；仅隔离初态，不计正常菜单开局。单库存例其余原标记在初态退盒，无运行中注入。"
                }));
            }

            private EffectRegistry NewRegistry()
            {
                var registry = new EffectRegistry();
                GenericLuaEffectExecutor.Register(registry); RepeatEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry);
                ItemTransferEffectExecutor.Register(registry); DeckShuffleEffectExecutor.Register(registry);
                ContentPrimitiveEffectExecutor.Register(registry); CharacterContentEffectExecutor.Register(registry);
                InfluenceEffectExecutor.Register(registry, new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateThreePlayerMap())));
                CityStyleSpecialActionEffectExecutor.RegisterCleanup(registry); CharacterCoverEffectExecutor.Register(registry);
                catalog.Register(registry);
                _ = new RoundExecutionService(registry);
                return registry;
            }

            internal void Start()
            {
                ActionEventIndex = State.EffectRuntime.RuleEvents.Count; ActionNodeIndex = State.EffectRuntime.EffectNodes.Count;
                if (Source == "defense") RootId = Engine.CreateRoot(EnterpriseEffectExecutor.UpgradeSpec(1, Item("enterprise.rhine_lab").ItemId, 1));
                else if (Source == "blacksteel") RootId = Engine.CreateRoot(EnterpriseSpecialSelectionExecutor.ActivateSpec(1, 1,
                    Item("enterprise.blacksteel").ItemId, "enterprise.blacksteel.level_4.special"));
                else
                {
                    var card = Hellagur;
                    Engine.CreateRoot(MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(State, "hand", 1).ZoneId, "unordered", 1, "core.character_card_transfer")); Pump();
                    Engine.CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Pump(); Answer(card.ItemId);
                    Assert.That(Registry.QuerySources(State, 1, "quick")
                        .Any(s => s.ItemId == card.ItemId && s.AbilityId == "infected.character.hellagur.use"), Is.True,
                        "隔离角色使用来源未获授权；保留此初态缺口，不伪造策略根来掩盖。");
                    RootId = Engine.CreateRoot(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "strategy"));
                }
                Pump();
            }

            internal void AssertCombinedRequest()
            {
                Assert.That(Open, Is.Not.Null, "正式来源没有生成组合；保留现场定位授权/付款/来源问题，不能假造请求。");
                int count = Source == "hellagur" ? 3 : 2;
                var expected = Enumerable.Range(0, 1 << count).Select(mask => string.Join("_", Enumerable.Range(0, count)
                    .Select(i => (mask & (1 << (count - i - 1))) == 0 ? "place" : "remove"))).ToArray();
                Assert.That(Open.InteractionTypeId, Is.EqualTo("effect.choice.execute"));
                Assert.That(Open.CandidateIds, Is.EqualTo(expected));
                Assert.That(Open.CandidateIds.Distinct().Count(), Is.EqualTo(expected.Length));
                Assert.That(Open.AnsweringPlayerId, Is.EqualTo(1));
                Assert.That(Open.MinSelections, Is.EqualTo(1)); Assert.That(Open.MaxSelections, Is.EqualTo(1));
                Assert.That(CombinedRequests.Count, Is.EqualTo(1));
            }

            internal void ConfirmDefensePayment()
            {
                int before = State.FindPlayer(1).Resources.GoldVoucher;
                for (int attempt = 0; attempt < 3 && CombinedRequests.Count == 0; attempt++)
                {
                    Assert.That(Open, Is.Not.Null, "防卫科付款链未产生待答，也没有组合。");
                    Assert.That(Open.CandidateIds, Does.Contain("continue"), "仅处理原执行/放弃，不猜测其它请求。");
                    Answer("continue");
                }
                Assert.That(State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before - 12));
                Assert.That(CombinedRequests.Count, Is.EqualTo(1));
            }

            internal string PerformStep(string operation, string preferredRemoval = null)
            {
                int before = ActionEvents.Count;
                Assert.That(Open, Is.Not.Null, "该步没有原目标请求；保留授权或候选缺失现场。");
                Assert.That(Open.CandidateIds.Any(id => id == "place" || id == "remove" || id == "place_place"), Is.False,
                    "组合已选，不能再弹同一放/移选择。");
                var owner = State.EffectRuntime.EffectNodes.Single(n => n.EffectId == Open.OwnerEffectId);
                if (operation == "place")
                {
                    Assert.That(owner.EffectTypeId, Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence));
                    Answer(Open.CandidateIds.First());
                }
                else
                {
                    Assert.That(owner.EffectTypeId, Is.EqualTo(ContentPrimitiveEffectExecutor.SelectInfluence));
                    string candidate = preferredRemoval ?? Open.CandidateIds.First(id => State.Map.Influences.Any(i => i.InfluenceId == id && i.OwnerSubject.PlayerId == 1));
                    Assert.That(Open.CandidateIds, Does.Contain(candidate), "放置后移除须能看见刚实际放下的实例。");
                    Answer(candidate);
                }
                Assert.That(ActionEvents.Count, Is.EqualTo(before + 1), "每步必须真实执行一次，不能只改Effect树或跳过目标。");
                Assert.That(ActionEvents.Last().EventType, Is.EqualTo(operation == "place" ? InfluenceEventTypeIds.InfluencePlaced : InfluenceEventTypeIds.InfluenceRemoved));
                Assert.That(CombinedRequests.Count, Is.EqualTo(1));
                return operation == "place" ? State.Map.Influences.Single(i => i.InfluenceId == EventInfluenceId(ActionEvents.Last())).InfluenceId : null;
            }

            internal void AssertFinished(string sequence)
            {
                Assert.That(ActionEvents.Select(e => e.EventType), Is.EqualTo(sequence.Split('_')
                    .Select(op => op == "place" ? InfluenceEventTypeIds.InfluencePlaced : InfluenceEventTypeIds.InfluenceRemoved)));
                Assert.That(State.EffectRuntime.EffectNodes.Single(n => n.EffectId == RootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(State.EffectRuntime.EffectNodes.Skip(ActionNodeIndex).Where(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted), Is.Empty);
                Assert.That(CombinedRequests.Count, Is.EqualTo(1)); Assert.That(CombinedRequests.Single().Status, Is.EqualTo("answered"));
                Assert.That(Open, Is.Null);
            }

            internal void Answer(string candidate)
            {
                var request = Open; Assert.That(request, Is.Not.Null);
                var projection = InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId);
                var result = new AnswerInteractionCommandHandler(Registry).Handle(State,
                    EffectInteractionCommands.Answer(projection, request.AnsweringPlayerId, new[] { candidate }));
                Assert.That(result.Succeeded, Is.True, "正式应答入口拒绝：" + result.Validation.Reason);
                Pump();
            }

            internal void Pump()
            {
                var report = Engine.RunUntilQuiescent();
                if (report.Faulted && !firstFault) { firstFault = true; artifacts.WriteLabeledText("首次真实内核故障", JsonConvert.SerializeObject(State)); }
                Assert.That(report.Faulted, Is.False, State.EffectRuntime.LastFaultMessage); Valid();
            }

            internal void Recover(string label)
            {
                Registry = NewRegistry();
                var recovered = artifacts.Recover(State, label, Registry);
                Assert.That(recovered.Succeeded, Is.True, recovered.FaultCode + ":" + recovered.Diagnostic);
                State = recovered.State; Pump();
            }

            internal void StartCleanup()
            {
                State.Phase = GamePhase.Cleanup;
                _ = new RoundExecutionService(Registry);
                var chain = new RoundMainChainBuilder().Build(State.GameId, "", State.Round, new[] { 1, 2, 3 }, 1,
                    false, State.RuleConfiguration.PlayerStageSchedulingMode);
                var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == 1);
                foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
                State.ActionRound = main.ActionRound;
                var runtime = State.EffectRuntime;
                runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = main.NodeId; runtime.FirstMainNodeId = chain.FirstNodeId;
                runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = State.Round; runtime.CurrentRoundStartPlayerId = 1;
                main.ExecutionEffectId = Engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                    Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
                main.Status = EffectNodeStatus.Running; Pump();
            }

            private ItemInstanceState Item(string definition) => State.ItemZones.Items.Single(i => i.DefinitionId == definition);
            private IEnumerable<ItemInstanceState> Supply() => State.ItemZones.Items.Where(i => i.ZoneId == FindZone(State, "supply", 1).ZoneId && i.TypeId == "core.player_marker");
            private void Valid() => Assert.That(new ItemZoneQuery().TryValidate(State, out string diagnostic), Is.True, "隔离初态/真实位置无效：" + diagnostic);
            private static string EventInfluenceId(RuleEvent e)
            {
                var value = Value(e.Payload, "influenceRefAfterPlacement");
                Assert.That(value?.Kind, Is.EqualTo(NormalizedValueKind.StableReference));
                Assert.That(value.ReferenceType, Is.EqualTo("influence"));
                Assert.That(value.ReferenceId, Is.EqualTo(e.TargetEntityId));
                return value.ReferenceId;
            }

            private void PrepareBoard(string definition, int level)
            {
                var board = Item(definition);
                // 配方仅创建实例；此隔离主例要求公开在场企业，必须在执行前明确摆放。
                InitialPosition(board, State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.enterprise_display").ZoneId);
                foreach (int playerId in State.Players.Select(player => player.PlayerId).ToArray())
                {
                string local = "cooperation_" + playerId;
                var zone = State.ItemZones.Zones.SingleOrDefault(z => z.ParentId == board.ItemId && z.LocalZoneId == local);
                if (zone == null)
                {
                    var declaration = new CardZoneDefinition { LocalZoneId = local, ZoneTemplateId = "core.zone.board_marker_track",
                        AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                        CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track, VisibilityPolicyId = ItemZoneVisibility.Public,
                        EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                    Assert.That(new ItemZoneStateFactory().TryCreateCardZone(State, board.ItemId, declaration, out string zoneId, out string diagnostic), Is.True, diagnostic);
                    zone = State.ItemZones.Zones.Single(z => z.ZoneId == zoneId);
                }
                var marker = State.ItemZones.Items.SingleOrDefault(i => i.ZoneId == zone.ZoneId) ?? State.ItemZones.Items.First(i =>
                    i.ZoneId == FindZone(State, "supply", playerId).ZoneId && i.TypeId == "core.player_marker");
                InitialPosition(marker, zone.ZoneId, "level_" + (playerId == 1 ? level : 0));
                }
            }

            private void InitialPosition(ItemInstanceState item, string zoneId, string slotId = "")
            {
                // 仅Initialize调用：明确构造隔离初态，不在待答/运行过程中修补状态。
                foreach (var order in State.ItemZones.Orders) if (order.ItemIds.Remove(item.ItemId)) order.OrderRevision++;
                item.ZoneId = zoneId; item.SlotId = slotId;
                var destination = State.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zoneId);
                if (destination != null) { destination.ItemIds.Add(item.ItemId); destination.OrderRevision++; }
            }
        }
    }
}
