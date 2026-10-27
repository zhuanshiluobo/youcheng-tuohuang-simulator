using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 实际场景输入；真实 Lua 的受控调用与正常房间起点分别记证，不宣称感染者整局已开放。
    [Timeout(600000)]
    public sealed class EffectTargetAndRoadUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private EffectRegistry registry;
        private static IEnumerator Noop(Component _) { yield break; }
        [UnityTest] public IEnumerator SampleScene_MountainRoadUsesRouteInputsAndRestoresOneRenderer() => Roads(4, false);
        [UnityTest] public IEnumerator ThreePlayerScene_MountainAndRoadAwardUseSameRouteInputsAndRestore() => Roads(3, true);

        private IEnumerator Start(int players)
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(players, Noop, "enterprise.fussen_bank", "mountain");
            registry = (EffectRegistry)Read(((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>()
                .OfType<AnswerInteractionCommandHandler>().Single(), "registry");
        }

        private IEnumerator Roads(int players, bool award)
        {
            yield return Start(players);
            int player = f.State.CurrentPlayerId;
            var map = StaticMapDefinitions.Resolve(f.State.MapId);
            var seededRoute = map.Routes.First(r => r.InfluenceSlotCount > 0 &&
                !f.State.Map.Influences.Any(i => i.RouteId == r.RouteId)).RouteId;
            var placement = new EffectTreeExecutor(f.State, registry);
            placement.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(player, InfluenceService.GetRouteSlotId(seededRoute, 0)));
            Assert.That(placement.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Assert.That(f.State.Map.Influences.Any(i => i.SlotId == InfluenceService.GetRouteSlotId(seededRoute, 0)), Is.True);
            f.Record("第零槽已有真实影响力前置", new { seededRoute, note = "通过真实 PlaceInfluence 生成，后续山清理并覆盖槽；绘制不能寻找首空槽" });
            EffectTargetUiAcceptance.InvokeLua(f, registry, "characters/mountain.strategy.lua", Object(), player);
            Assert.That(f.Request.InteractionTypeId, Is.EqualTo("effect.route.choose"));
            string route = seededRoute;
            Assert.That(f.Request.CandidateIds, Does.Contain(route));
            yield return EffectTargetUiAcceptance.Restore(f, registry, "山待选航道");
            yield return EffectTargetUiAcceptance.AnswerRoute(f, route, true);
            Assert.That(f.Request, Is.Null);
            var firstRoad = f.State.ItemZones.Items.Single(i => i.TypeId == "core.road_token").ItemId;
            EffectTargetUiAcceptance.CheckRoads(f);
            yield return f.Capture("实际地图公路_首槽与正式素材", (Component)Read(f.Gameplay, "mapViewBinding"));
            yield return EffectTargetUiAcceptance.Restore(f, registry, "山公路已落盘");
            Assert.That(f.State.ItemZones.Items.Any(i => i.ItemId == firstRoad), Is.True);
            EffectTargetUiAcceptance.CheckRoads(f);
            yield return f.Resolution(Sizes[1]);
            EffectTargetUiAcceptance.CheckRoads(f);
            yield return f.Capture("窗口变化后的公路", (Component)Read(f.Gameplay, "mapViewBinding"));

            if (award)
            {
                var source = f.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_rewards" && z.OwnerPlayerId == player);
                const string grant = "acceptance.road_award.grant";
                registry.Register(grant, context =>
                {
                    if (!new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, "road_award",
                        "infected.award.roads", "core.award_card", source.ZoneId, ItemSubjectRef.Player(player), out string created, out string error))
                        return EffectStepResult.Failed(error);
                    return EffectStepResult.Completed(Object("itemId", NormalizedValue.CreateString(created)));
                });
                var grantEngine = new EffectTreeExecutor(f.State, registry);
                var grantId = grantEngine.CreateRoot(EffectSpec.Create(grant, playerId: player));
                Assert.That(grantEngine.RunUntilQuiescent().Faulted, Is.False);
                var grantNode = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == grantId);
                Assert.That(grantNode.Status, Is.EqualTo(EffectNodeStatus.Completed));
                string awardId = Text(grantNode.NormalizedResult, "itemId");
                f.Record("受控真实目录授勋起点", new { awardId, source.ZoneId, note = "使用正式类型／定义／获得后 Lua；不冒称通过未开放感染者房间或支付授勋获得流程" });
                var scores = f.State.Players.Select(p => p.Score).ToArray();
                EffectTargetUiAcceptance.InvokeLua(f, registry, "awards/infected.award.roads.acquired.lua",
                    Object("rewardItemId", NormalizedValue.CreateString(awardId)), player);
                Assert.That(f.Request.CandidateIds, Does.Not.Contain(route));
                var firstAwardRoute = f.Request.CandidateIds.First();
                yield return EffectTargetUiAcceptance.AnswerRoute(f, firstAwardRoute);
                EffectTargetUiAcceptance.CheckRoads(f);
                yield return EffectTargetUiAcceptance.Restore(f, registry, "道路授勋第一条已完成");
                Assert.That(f.Request.InteractionTypeId, Is.EqualTo("effect.route.choose"));
                Assert.That(f.Request.CandidateIds, Does.Not.Contain(firstAwardRoute));
                Assert.That(f.Request.CandidateIds, Does.Not.Contain(route));
                yield return EffectTargetUiAcceptance.AnswerRoute(f, f.Request.CandidateIds.First());
                Assert.That(f.State.Players.Select(p => p.Score), Is.EqualTo(scores));
                Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == awardId).ZoneId, Is.EqualTo(FindZone(f.State, "retired").ZoneId));
                Assert.That(f.State.ItemZones.Items.Count(i => i.TypeId == "core.road_token"), Is.EqualTo(3));
                EffectTargetUiAcceptance.CheckRoads(f);
            }
            Assert.That(f.Request, Is.Null);
            yield return CheckProjectedRoadRemoval(firstRoad);
            yield return f.Capture("最终真实公路实体与显示", (Component)Read(f.Gameplay, "mapViewBinding"));
        }

        private IEnumerator CheckProjectedRoadRemoval(string itemId)
        {
            var presenter = Read(f.Gameplay, "mapView");
            var visible = GameStateViewProjector.ProjectForPlayer(f.State, f.State.CurrentPlayerId).ItemZones;
            var removed = GameStateCloneService.DeepClone(visible);
            Assert.That(removed.Items.RemoveAll(item => item.ItemId == itemId), Is.EqualTo(1));
            var beforeCount = ((IDictionary)Read(presenter, "roadRenderers")).Count;
            Call(presenter, "RefreshRoadDisplay", removed); yield return Settle();
            var after = (IDictionary)Read(presenter, "roadRenderers");
            Assert.That(after.Contains(itemId), Is.False);
            Assert.That(after.Count, Is.EqualTo(beforeCount - 1));
            Assert.That(f.State.ItemZones.Items.Any(item => item.ItemId == itemId), Is.True, "只读投影补证不得修改权威物品");
            Call(presenter, "RefreshRoadDisplay", visible); yield return Settle();
            EffectTargetUiAcceptance.CheckRoads(f);
            f.Record("实际地图渲染器撤图与恢复补证", new { itemId, beforeCount,
                note = "复制正式公开投影后去掉一个公路条目，真实渲染器应撤图；重新提供原投影恢复唯一对象。权威状态未移动／删除公路，不冒称存在正式公路移除命令。" });
        }

        [UnityTest]
        public IEnumerator SampleScene_CaveRuinsPaysIronBeforeActualEnterprisePageAndKeepsFussenName()
        {
            yield return Start(4);
            int player = f.State.CurrentPlayerId;
            if (f.State.FindPlayer(player).Resources.Iron == 0)
            {
                var preparation = new EffectTreeExecutor(f.State, registry);
                preparation.CreateRoot(ResourceEffectSpecFactory.Gain(player, ResourceType.Iron, 1));
                Assert.That(preparation.RunUntilQuiescent().Faulted, Is.False);
                f.Record("洞穴遗迹付款受控前置", new { player, note = "随机正式开局无铁时，由真实 Gain Effect 先授予 1 铁，只制造付款样本；不是事件提前发奖励，不直接覆写库存" });
            }
            int iron = f.State.FindPlayer(player).Resources.Iron;
            EffectTargetUiAcceptance.InvokeLua(f, registry, "events/enterprise.event.cave_ruins.resolve.lua",
                Object("optionIndex", NormalizedValue.CreateInteger(2)), player);
            Assert.That(f.Request.InteractionTypeId, Is.EqualTo("enterprise.upgrade"));
            Assert.That(f.State.FindPlayer(player).Resources.Iron, Is.EqualTo(iron - 1));
            var id = f.Request.CandidateIds.First(candidate => f.State.ItemZones.Items.Any(i => i.ItemId == candidate && i.DefinitionId == "enterprise.fussen_bank"));
            int level = new EnterpriseStateQuery().GetLevel(f.State, player, id);
            Assert.That(Call(Read(f.Hud, "ExpansionUi"), "Name", "enterprise.fussen_bank"), Is.EqualTo("富森银行"));
            yield return EffectTargetUiAcceptance.Restore(f, registry, "洞穴遗迹已付铁待企业");
            yield return EffectTargetUiAcceptance.AnswerEnterprise(f, id);
            Assert.That(new EnterpriseStateQuery().GetLevel(f.State, player, id), Is.EqualTo(Math.Min(5, level + 1)));
            Assert.That(f.State.FindPlayer(player).Resources.Iron, Is.EqualTo(iron - 1));
        }

        [UnityTest]
        public IEnumerator SampleScene_RedZonePreparationUsesActualEnterprisePageForEachAnsweringPlayer()
        {
            yield return Start(4);
            int originalRound = f.State.Round;
            var order = f.State.EffectRuntime.PlayerOrderSnapshot.ToArray();
            Assert.That(order.Length, Is.EqualTo(4));
            // 本例只验真实 PrepareRound 请求的接线。同步主链的回合投影输入，避免提交时又投影回第一回合；
            // 保留当前链和全部实际资产，不宣称自然推进或经历前三回合。
            var roundRuntime = f.State.EffectRuntime;
            foreach (var main in roundRuntime.MainNodes.Where(n => n.RoundExecutionId == roundRuntime.CurrentRoundExecutionId))
                main.RoundNumber = 4;
            roundRuntime.CurrentRoundNumber = 4;
            Assert.That(new RoundExecutionProjector().Project(f.State), Is.True);
            Assert.That(f.State.Round, Is.EqualTo(4));
            var engine = new EffectTreeExecutor(f.State, registry);
            var root = engine.CreateRoot(ExpansionSetupFlow.RoundPreparationSpec(f.State.StartPlayerId));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
            f.Record("红区正式准备流受控前置", new { originalRound, f.State.Round, root, order,
                note = "保留正式房间、企业和玩家顺序，受控把当前主链节点与 CurrentRoundNumber 同步为四人红区回合，再由原投影器生成 State.Round 并调用真实 RoundPreparationSpec；不是自然推进或完整多回合经历" });
            var answered = new System.Collections.Generic.List<int>();
            for (int guard = 0; answered.Count < order.Length && guard < 32; guard++)
            {
                var request = f.Request; Assert.That(request, Is.Not.Null);
                if (request.InteractionTypeId == "round.enterprise.upgrade")
                {
                    var candidate = request.CandidateIds.First(); int player = request.AnsweringPlayerId;
                    Assert.That(player, Is.EqualTo(order[answered.Count]));
                    int level = new EnterpriseStateQuery().GetLevel(f.State, player, candidate);
                    File.WriteAllText(Path.Combine(f.DirectoryPath, "红区玩家" + player + "确认前.json"), Newtonsoft.Json.JsonConvert.SerializeObject(f.State, Newtonsoft.Json.Formatting.Indented));
                    yield return EffectTargetUiAcceptance.AnswerEnterprise(f, candidate);
                    File.WriteAllText(Path.Combine(f.DirectoryPath, "红区玩家" + player + "确认后.json"), Newtonsoft.Json.JsonConvert.SerializeObject(f.State, Newtonsoft.Json.Formatting.Indented));
                    Assert.That(f.State.Round, Is.EqualTo(4), "测试前置的主链回合投影必须在正式提交后保持一致");
                    Assert.That(new EnterpriseStateQuery().GetLevel(f.State, player, candidate), Is.EqualTo(Math.Min(5, level + 1)));
                    answered.Add(player);
                }
                else
                {
                    var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
                    command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
                    command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString();
                    command.OptionIds.Add(request.CandidateIds.First());
                    var result = f.Session.Submit(command);
                    Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
                    f.Record("红区升级奖励正式协议辅助", new { request.InteractionTypeId, request.AnsweringPlayerId });
                    Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
                }
            }
            Assert.That(answered, Is.EqualTo(order));
        }
    }

    // 仅本轮三个测试源共用的输入／存档助手，不修改既有共享夹具。
    internal static class EffectTargetUiAcceptance
    {
        internal static IEnumerator AnswerEnterprise(GameplayPageAcceptanceFixture f, string candidate)
        {
            var request = f.Request;
            Assert.That(request.CandidateIds, Does.Contain(candidate));
            yield return Wait(() => f.RequestPage() != null && (int)Read(f.Gameplay, "localPlayerId") == request.AnsweringPlayerId,
                "企业权威请求未绑定回答玩家的实际页面");
            var page = f.RequestPage();
            using (var count = new RuleSubmitCounter())
            {
                yield return f.Click(Target(page, candidate), "实际企业选择 " + candidate);
                Assert.That(count.Calls, Is.Zero);
                yield return f.Capture("企业效果确认前", page);
                yield return f.Click(ButtonObject(page, "confirm"), "实际企业效果确认");
                Assert.That(count.Calls, Is.EqualTo(1));
            }
            Assert.That(request.Status, Is.Not.EqualTo("open"));
            f.Record("共用企业页正式回答", new { request.InteractionTypeId, request.PromptKey, request.StateRevision,
                request.AnsweringPlayerId, candidate, request.CandidateIds });
        }

        internal static Component MapTarget(string kind, string id) => Active("MapButtonView").Single(t =>
            Read(t, "TargetKind").ToString() == kind && (string)Read(t, "TargetId") == id);

        internal static IEnumerator AnswerRoute(GameplayPageAcceptanceFixture f, string route, bool restorePreselection = false)
        {
            var request = f.Request;
            Assert.That(request.InteractionTypeId, Is.EqualTo("effect.route.choose"));
            Assert.That(request.CandidateIds, Does.Contain(route));
            yield return Wait(() => (int)Read(f.Gameplay, "localPlayerId") == request.AnsweringPlayerId,
                "航道输入未切到请求所属玩家");
            var target = MapTarget("Route", route);
            using (var count = new RuleSubmitCounter())
            {
                yield return f.Click(target.gameObject, "航道首次独立输入只预选");
                Assert.That(count.Calls, Is.Zero);
                Assert.That(request.Status, Is.EqualTo("open"));
                // 第一次与第二次输入之间经过自然帧和截图，不能以同一事件冒充二次确认。
                yield return f.Capture("航道预选未提交", (Component)Read(f.Gameplay, "mapViewBinding"));
                if (restorePreselection)
                {
                    var registry = (EffectRegistry)Read(((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>()
                        .OfType<AnswerInteractionCommandHandler>().Single(), "registry");
                    yield return Restore(f, registry, "仅航道预选后恢复");
                    request = f.Request;
                    yield return f.Click(MapTarget("Route", route).gameObject, "恢复后首次输入重新预选");
                    Assert.That(count.Calls, Is.Zero, "恢复不应继承旧地图预选");
                }
                yield return f.Click(MapTarget("Route", route).gameObject, "航道第二次独立输入正式确认");
                Assert.That(count.Calls, Is.EqualTo(1));
            }
            Assert.That(request.Status, Is.Not.EqualTo("open"));
            f.Record("共用航道二次确认", new { route, request.InteractionTypeId, request.StateRevision,
                request.CandidateSetId, request.CandidateSetVersion, request.AnsweringPlayerId });
        }

        internal static IEnumerator Restore(GameplayPageAcceptanceFixture f, EffectRegistry registry, string label)
        {
            var json = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State));
            Directory.CreateDirectory(f.DirectoryPath);
            File.WriteAllText(Path.Combine(f.DirectoryPath, label + ".archive.json"), json);
            var result = HostRecoveryService.RecoverJson(json, registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            f.Session.ReplaceState(result.State);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Settle();
            f.Record("正式 Host 存档恢复", new { label, result.Succeeded, result.Diagnostic });
        }

        internal static void CheckRoads(GameplayPageAcceptanceFixture f)
        {
            var renderers = (IDictionary)Read(Read(f.Gameplay, "mapView"), "roadRenderers");
            var roads = f.State.ItemZones.Items.Where(i => f.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.route_road")).ToArray();
            Assert.That(renderers.Count, Is.EqualTo(roads.Length));
            var view = Read(f.Gameplay, "mapViewBinding");
            foreach (var road in roads)
            {
                Assert.That(road.PhysicalKind, Is.EqualTo(ItemPhysicalKind.Token));
                var zone = f.State.ItemZones.Zones.Single(z => z.ZoneId == road.ZoneId);
                var route = f.State.ItemZones.Zones.Single(z => z.ZoneId == zone.ParentId);
                Assert.That(route.TemplateId, Is.EqualTo("core.zone.map_route"));
                Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId), Is.EqualTo(1));
                var renderer = (SpriteRenderer)renderers[road.ItemId];
                Assert.That(renderer, Is.Not.Null); Assert.That(renderer.gameObject.activeInHierarchy, Is.True);
                Assert.That(renderer.sprite, Is.Not.Null);
                var anchor = MapTarget("InfluenceSlot", InfluenceService.GetRouteSlotId(route.LocalZoneId, 0));
                var anchorImage = (SpriteRenderer)Read(anchor, "Image");
                var height = anchorImage.transform.TransformVector(Vector3.up * anchorImage.sprite.bounds.size.y).magnitude * (float)Read(view, "RoadTokenHeightMultiplier");
                Assert.That(renderer.bounds.size.y, Is.EqualTo(height).Within(Mathf.Max(0.0001f, height * 0.001f)));
                Assert.That(renderer.bounds.size.x / renderer.bounds.size.y, Is.EqualTo(942f / 325f).Within(0.002f));
                Assert.That(Vector2.Distance(renderer.transform.position, anchor.transform.position), Is.LessThan(Mathf.Max(0.0001f, height * 0.001f)));
                Assert.That(renderer.GetComponentsInChildren<Collider2D>(true), Is.Empty);
                Assert.That(renderer.GetComponentsInChildren<Collider>(true), Is.Empty);
                f.Record("真实公路与唯一显示", new { road.ItemId, road.DefinitionId, road.ZoneId, route = route.LocalZoneId,
                    renderer = renderer.GetInstanceID(), sprite = renderer.sprite.name, height, renderer.sortingLayerID, renderer.sortingOrder });
            }
        }

        internal static void InvokeLua(GameplayPageAcceptanceFixture f, EffectRegistry registry, string relative, NormalizedValue payload, int player)
        {
            Assert.That(new[] { "characters/mountain.strategy.lua", "events/enterprise.event.cave_ruins.resolve.lua", "awards/infected.award.roads.acquired.lua" }, Does.Contain(relative));
            Assert.That(f.Request, Is.Null, "受控真实目录入口不能插入未结束交互");
            var path = Path.Combine(UnityEngine.Application.dataPath, "StreamingAssets/Content/core/lua", relative);
            var source = File.ReadAllText(path); var ability = Path.GetFileNameWithoutExtension(path);
            const string type = "acceptance.content.invoke";
            if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, context => EffectStepResult.Completed().AddEvent(new EffectEventRequest {
                EventType = "ContentAbilityInvoked", RouteKey = Text(context.Node.NormalizedArguments, "abilityId"), PlayerId = context.Node.PlayerId,
                Payload = Value(context.Node.NormalizedArguments, "payload").Clone() })));
            string subscription = "acceptance.content." + ability;
            if (!registry.HasEventHandler(subscription)) new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition(subscription,
                "ContentAbilityInvoked", ability, new LuaScriptDefinition(ability, ability, subscription, "1.0.0", source, LuaContentHasher.ComputeSha256(source)),
                EffectHandlerRole.Primary, 0, "", ability));
            var engine = new EffectTreeExecutor(f.State, registry);
            engine.CreateRoot(EffectSpec.Create(type, Object("abilityId", NormalizedValue.CreateString(ability), "payload", payload), player));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            f.Record("受控真实 Lua 入口", new { path, ability, player, sha256 = LuaContentHasher.ComputeSha256(source),
                note = "仅跳过来源获得／事件抽取，效果与候选运行真实源码；目标选择随后走实际 UI" });
        }
    }
}
