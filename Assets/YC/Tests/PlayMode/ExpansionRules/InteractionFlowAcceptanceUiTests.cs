using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
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
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 每方法只开一次正式房间；通过现有实例、正式转移/盖牌/使用 Effect 建立受控起点。
    // 待验输入走场景中的 UI 和原 Session。没有内容包替换、界面重建或资产保存。
    [Timeout(600000)]
    public sealed class InteractionFlowAcceptanceUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private EffectRegistry effects;
        private int player;
        private IList<IGameCommandHandler> handlers;
        private IGameCommandHandler[] originals;
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private object Expansion => Read(f.Hud, "ExpansionUi");
        private Component BuildPage => (Component)Read(f.Gameplay, "facilityBuildPage");
        private BuildFacilitySelectionController BuildSelection => (BuildFacilitySelectionController)Read(
            Read(Read(f.Gameplay, "turnActionPresenter"), "BuildInteraction"), "selection");
        private Button EndButton => (Button)Read(f.Frame, "EndActionButton");

        [UnityTearDown]
        public IEnumerator Release()
        {
            if (handlers != null && originals != null)
                for (int i = 0; i < originals.Length; i++) handlers[i] = originals[i];
            if (f?.Session != null)
            {
                Directory.CreateDirectory(f.DirectoryPath);
                File.WriteAllText(Path.Combine(f.DirectoryPath, "终态.json"), JsonConvert.SerializeObject(f.State));
                File.WriteAllText(Path.Combine(f.DirectoryPath, "实际命令.json"), JsonConvert.SerializeObject(commands, Formatting.Indented));
                if (f.Gameplay != null) yield return f.Capture("联合验收终态或失败现场", (Component)f.Hud);
            }
            handlers = null; originals = null;
        }

        [UnityTest]
        public IEnumerator CardGesturesAndSourceDetailsPreservePendingDraft()
        {
            yield return Start();
            string dorothy = PrepareUse("operator.dorothy", "tactic");
            AnswerPreparation(null, "0");
            string closure = PrepareUse("operator.closure", "tactic");
            AnswerPreparation(f.Request.CandidateIds[0]);
            if (f.Request != null) AnswerPreparation(null, null, true);
            Assert.That(Item(dorothy).ZoneId, Is.EqualTo(FindZone(f.State, "persistent", player).ZoneId));
            Assert.That(Item(closure).ZoneId, Is.EqualTo(FindZone(f.State, "persistent", player).ZoneId));
            yield return Sync();
            // 明细与建设分别从各自正常入口验收；建设页没有可用的收起入口，不穿透遮罩。
            var modules = Read(f.Hud, "MainModules");
            if (!Rows().Any(r => r.gameObject.activeInHierarchy))
                yield return Click(ButtonObject(modules, "localDetailToggle"), "展开本人明细");
            string state = JsonConvert.SerializeObject(f.State); int submitted = commands.Count;
            foreach (string source in new[] { dorothy, closure })
            {
                var entry = ((IEnumerable)Read(Expansion, "DetailEntries")).Cast<PlayerDetailEntry>().Single(e => e.SourceItemId == source);
                var row = Rows().Single(r => (string)Read(r, "ItemId") == entry.EntryId);
                Assert.That(((Text)Read(row, "DescriptionText")).text, Is.EqualTo(entry.EffectText));
                yield return Click(ButtonObject(row, "bodyButton"), "明细正文直接查看来源 " + Item(source).DefinitionId);
                var viewer = Active("CardViewer").Single();
                Assert.That(Read(viewer, "Mode").ToString(), Is.EqualTo("Inspect"));
                Assert.That(Read(viewer, "DisplayedSprite"), Is.SameAs(Call(Read(f.Registry, "CardVisualCatalog"),
                    "GetCharacterFront", Item(source).DefinitionId)));
                Assert.That(((IEnumerable)Read(Expansion, "InspectedItemIds")).Cast<string>(), Is.EqualTo(new[] { source }));
                Assert.That(Active("EffectDialogShellView"), Is.Empty, "正文直接进入来源卡，不能打开内部区域列表");
                yield return f.Capture("来源卡面_" + Item(source).DefinitionId, viewer);
                yield return Click(viewer.gameObject, "关闭来源卡面返回本人明细");
                Assert.That(Rows().Any(r => (string)Read(r, "ItemId") == entry.EntryId && r.gameObject.activeInHierarchy), Is.True);
                Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(state));
            }
            Assert.That(commands.Count, Is.EqualTo(submitted));
            yield return f.Capture("来源卡返回完整明细", (Component)f.Hud);
            yield return Click(HotseatUiInputDriver.Marker("action.build"), "打开真实建设草稿");
            var snapshot = new BuildFacilityOptionQueryService(new BuildFacilityService(), effects.FacilityBuildCandidates)
                .QueryCandidates(f.State, player);
            string id = snapshot.FinalItemIds[0];
            yield return ImmediateCardClick(id);
            Assert.That(Active("CardViewer"), Is.Empty);
            yield return ImmediateCardClick(id);
            Assert.That(Active("CardViewer"), Is.Empty, "连续两次左键不得转为查看");
            string draft = Draft(); var page = BuildPage;
            var scroll = page.GetComponentInChildren<ScrollRect>();
            Vector2 scrollBefore = scroll == null ? Vector2.zero : scroll.normalizedPosition;
            yield return PointerPreview(HotseatUiInputDriver.Marker("build.candidate." + id), false);
            Assert.That(Draft(), Is.EqualTo(draft));
            yield return PointerPreview(HotseatUiInputDriver.Marker("build.candidate." + id), true);
            yield return DragWithoutSelection(HotseatUiInputDriver.Marker("build.candidate." + id));
            Assert.That(BuildPage, Is.SameAs(page)); Assert.That(page.gameObject.activeInHierarchy, Is.True);
            Assert.That(Draft(), Is.EqualTo(draft));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(state));
            Assert.That(commands.Count, Is.EqualTo(submitted));
            if (scroll != null) Assert.That(Vector2.Distance(scroll.normalizedPosition, scrollBefore), Is.LessThan(.001f));
            f.Record("正常入口分段验收", new { note = "明细在HUD直接查看来源并返回；建设草稿分别经卡牌手势查看器返回。未声称建设模态可收起或可直接访问底层明细。" });
            yield return f.Capture("卡牌查看后保留建设草稿", page);
        }

        [UnityTest]
        public IEnumerator StoredFacilityBuildUsesInstanceAndBottomBarStop()
        {
            yield return Start();
            string closure = PrepareUse("operator.closure", "tactic");
            yield return Sync();
            var first = f.Request;
            Assert.That(first.CandidateIds, Does.Not.Contain("choice.skip"));
            string held = first.CandidateIds[0];
            string originalSlot = Item(held).ZoneId;
            yield return ChooseCard(held);
            Assert.That(Item(held).ZoneId, Is.EqualTo(CardZone(closure, "orders")));
            string refill = f.State.ItemZones.Items.Single(i => i.ZoneId == originalSlot).ItemId;
            Assert.That(refill, Is.Not.EqualTo(held));
            Assert.That(f.Request, Is.Not.Null, "一次取得后应仍可选择第二张");
            Assert.That(f.Request.CandidateIds, Does.Not.Contain("choice.skip"));
            yield return Stop("停止可露希尔剩余取得");
            Assert.That(Item(held).ZoneId, Is.EqualTo(CardZone(closure, "orders")));
            Assert.That(Item(refill).ZoneId, Is.EqualTo(originalSlot));
            // 同名实例对照为受控目录夹具：在另一真实供应槽授予同定义实例，登记额外实体边界。
            string twin = PreparePublicTwin(held);
            FundBuild(); yield return Sync();
            var query = new BuildFacilityOptionQueryService(new BuildFacilityService(), effects.FacilityBuildCandidates);
            var snapshot = query.QueryCandidates(f.State, player);
            Assert.That(snapshot.IsDefaultSupplySet, Is.False);
            Assert.That(snapshot.FinalItemIds, Does.Contain(held).And.Contain(twin));
            var option = query.Query(f.State, player, snapshot).Single(o => o.FacilityId == held);
            Assert.That(option.CanBuild, Is.True, option.Reason);
            Assert.That(option.SourceZoneId, Is.EqualTo(CardZone(closure, "orders")));
            var publicBefore = PublicSupply(); int gold = f.State.FindPlayer(player).Resources.GoldVoucher;
            int price = FacilityCardDatabase.Get(f.State, Item(held).DefinitionId).GoldVoucherCost;
            yield return Click(HotseatUiInputDriver.Marker("action.build"), "普通建设打开含牌下来源的候选");
            Assert.That(((Text)Read(BuildPage, "candidatesHeading")).text, Is.EqualTo(Read(BuildPage, "alternateHeading")));
            yield return Click(HotseatUiInputDriver.Marker("build.candidate." + held), "选择真实牌下实例");
            yield return f.Capture("普通建设_可露希尔真实实例候选", BuildPage);
            int before = commands.Count;
            yield return Click(HotseatUiInputDriver.Marker("build.payment.gold"), "普通建设支付正常金券费用");
            Assert.That(commands.Skip(before).Single().Kind, Is.EqualTo(GameCommandKind.BuildFacility));
            Assert.That(commands.Last().TargetId, Is.EqualTo(held));
            Assert.That(commands.Last().Parameters[BuildFacilityCommandHandler.ExpectedSourceZoneIdParameter], Is.EqualTo(option.SourceZoneId));
            Assert.That(f.State.FindPlayer(player).Resources.GoldVoucher, Is.EqualTo(gold - price));
            Assert.That(Item(held).ZoneId, Is.Not.EqualTo(option.SourceZoneId));
            Assert.That(Item(closure).ZoneId, Is.EqualTo(FindZone(f.State, "discard", player).ZoneId));
            Assert.That(PublicSupply(), Is.EqualTo(publicBefore), "牌下建设不得拿公共同名实例或额外补槽");
            string archive = SaveArchive("普通建设已支付待落位");
            yield return Restore(archive);
            var placementPage = Active("FacilityBuildDialogView").Single();
            var slot = ((Button[])Read(placementPage, "citySlots")).First(b => b.IsInteractable());
            yield return Click(slot.gameObject, "选城市合法空格");
            yield return Click(ButtonObject(placementPage, "confirm"), "确认牌下设施落位");
            Assert.That(f.State.Map.Facilities.Any(p => p.ContentInstanceId == held && p.PlayerId == player), Is.True);
            Assert.That(f.State.Map.Facilities.Any(p => p.ContentInstanceId == twin), Is.False);
            Assert.That(f.State.FindPlayer(player).Resources.GoldVoucher, Is.EqualTo(gold - price));
            Assert.That(PublicSupply(), Is.EqualTo(publicBefore));
            yield return f.Capture("可露希尔设施建成与空载退场", (Component)f.Hud);
        }

        [UnityTest]
        public IEnumerator QuantityAndCleanupUseAllocationAndSettlementBar()
        {
            yield return Start();
            string dorothy = PrepareUse("operator.dorothy", "tactic");
            yield return Sync();
            Assert.That(f.Request.CandidateIds, Is.Empty);
            Assert.That(Number(f.Request.PromptParameters, "maxAmount"), Is.EqualTo(4));
            string emptyBranch = SaveArchive("多萝西装载数量待选");
            yield return Stop("装载零枚直接停止");
            Assert.That(Item(dorothy).ZoneId, Is.EqualTo(FindZone(f.State, "persistent", player).ZoneId));
            Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == CardZone(dorothy, "markers")), Is.Zero);
            yield return Restore(emptyBranch);
            yield return Quantity(2);
            Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == CardZone(dorothy, "markers")), Is.EqualTo(2));
            foreach (var other in f.State.Players.Where(p => p.PlayerId != player).ToArray())
                Run(ResourceEffectSpecFactory.Gain(other.PlayerId, ResourceType.Iron, 3), "异客资源借取前置库存");
            string passenger = PrepareUse("operator.passenger", "strategy");
            yield return Sync();
            Assert.That(f.Request.PromptKey, Is.EqualTo("effect.resource.choose_type"));
            var candidates = f.Request.CandidateIds.ToArray();
            var rows = Active("EffectDialogResourceRowView");
            Assert.That(rows.Length, Is.EqualTo(candidates.Length));
            Assert.That(rows.Select(r => ((Text)Read(r, "Label")).text).Any(t => t.Contains("|") || t.StartsWith("player:")), Is.False);
            int index = Array.IndexOf(candidates, "iron"); Assert.That(index, Is.GreaterThanOrEqualTo(0));
            yield return Click(ButtonObject(rows.Single(r => r.name == "Resource Allocation Row " + index), "IncreaseButton"), "选择异客借取资源");
            yield return ConfirmAllocation();
            Assert.That(f.Request.PromptKey, Is.EqualTo("effect.player.choose_target"));
            int target = int.Parse(f.Request.CandidateIds[0].Substring("player:".Length), CultureInfo.InvariantCulture);
            var page = Active("PlayerSelectionPageView").Single();
            var playerOption = ((IEnumerable)Read(page, "Options")).Cast<Component>().Single(o =>
                (int)Read(Read(o, "Option"), "Id") == target);
            yield return Click(ButtonObject(playerOption, "Button"), "异客选择标准玩家候选");
            yield return f.Capture("异客标准玩家选择页", page);
            yield return Click(ButtonObject(page, "ConfirmButton"), "确认借取对象");
            int ownScore = f.State.FindPlayer(player).Score, targetScore = f.State.FindPlayer(target).Score;
            yield return Quantity(0);
            Assert.That(f.State.FindPlayer(player).Score, Is.EqualTo(ownScore + 1));
            Assert.That(f.State.FindPlayer(target).Score, Is.EqualTo(targetScore + 1));
            Assert.That(f.State.CharacterUses.Single(u => u.CardInstanceId == passenger).Finished, Is.True);

            RegisterMandatoryCleanup();
            yield return AdvanceToOwnCleanup();
            Assert.That(f.Request.PromptKey, Is.EqualTo("acceptance.cleanup.required"));
            Assert.That(EndButton.IsInteractable(), Is.False, "强制子式未完成不得直接结束收尾");
            Assert.That(CharacterSettlementOptionQuery.GetOptions(f.State, player, ""), Is.Empty);
            yield return ChooseOrdinaryCandidate("required");
            Assert.That(f.Request.InteractionTypeId, Is.EqualTo(CharacterContentEffectExecutor.CleanupInteractionTypeId));
            Assert.That(Active("EffectDialogOptionRowView"), Is.Empty, "收尾不应再有激活/放弃弹窗");
            var offer = CharacterSettlementOptionQuery.GetOptions(f.State, player, "").Single(o => o.SourceItemId == dorothy);
            Assert.That(offer.IsEnabled, Is.True);
            Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == CardZone(dorothy, "markers")), Is.EqualTo(2), "展示结算项不得自动支付");
            string cleanup = SaveArchive("多萝西收尾待点击");
            var settlement = ((IDictionary)Read(Expansion, "settlementRows"))[offer.OptionId];
            yield return f.Capture("多萝西底栏可选结算", (Component)f.Hud);
            yield return Click(ButtonObject(settlement, "StatusButton"), "点击底栏执行多萝西收尾");
            Assert.That(commands.Last().Kind, Is.EqualTo(GameCommandKind.InvokeSettlementOption));
            Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == CardZone(dorothy, "markers")), Is.Zero);
            Assert.That(Item(dorothy).ZoneId, Is.EqualTo(FindZone(f.State, "persistent", player).ZoneId));
            Assert.That(f.Request, Is.Not.Null, "支付后应要求选择真实影响力位置");
            string slot = f.Request.CandidateIds.First();
            yield return MapAnswer(slot, slot);
            Assert.That(f.State.ItemZones.Items.Any(i => i.SlotId == slot && i.OwnerSubject.PlayerId == player), Is.True);
            yield return Restore(cleanup);
            yield return Stop("收尾恢复后直接结束不执行可选项");
            Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == CardZone(dorothy, "markers")), Is.EqualTo(2));
            Assert.That(Item(dorothy).ZoneId, Is.EqualTo(FindZone(f.State, "persistent", player).ZoneId));
            yield return f.Capture("可选收尾停止保留空牌规则与未付标记", (Component)f.Hud);
        }

        [UnityTest]
        public IEnumerator DispatchMovesSequentiallyAndStopsOnlyRemainingChild()
        {
            yield return Start();
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId)));
            string source = influence.GetLegalPlacementSlotIds(f.State, player).First();
            var placement = Run(InfluenceEffectSpecFactory.PlaceInfluence(player, source), "放置调度前置真实影响力");
            string item = Value(placement.NormalizedResult, "influenceId").ReferenceId;
            Assert.That(influence.GetLegalMovementSlotIds(f.State, item).Count, Is.GreaterThanOrEqualTo(2));
            yield return Sync();
            int budget = f.State.FindPlayer(player).RemainingMainActionsThisTurn;
            int completed = f.State.FindPlayer(player).CompletedMainActionsThisTurn;
            int before = commands.Count;
            yield return Click(HotseatUiInputDriver.Marker("action.dispatch"), "实际调度入口");
            var start = commands.Skip(before).Single();
            Assert.That(start.Kind, Is.EqualTo(GameCommandKind.DispatchInfluence));
            Assert.That(start.SourceId, Is.Null.Or.Empty); Assert.That(start.TargetId, Is.Null.Or.Empty); Assert.That(start.OptionIds, Is.Empty);
            var parent = f.State.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == InfluenceDispatchActionEffectExecutor.TypeId);
            string parentId = parent.EffectId;
            Assert.That(parent.ChildEffectIds.Count, Is.EqualTo(2));
            Assert.That(f.Request.InteractionTypeId, Is.EqualTo(InfluenceDispatchActionEffectExecutor.SourceInteractionTypeId));
            yield return MapAnswer(source, item);
            string first = f.Request.CandidateIds.First(id => id != source);
            yield return MapAnswer(first, first);
            Assert.That(Item(item).SlotId, Is.EqualTo(first));
            Assert.That(f.Request.CandidateIds, Does.Contain(item), "第二次允许同一真实实例");
            Assert.That(f.State.FindPlayer(player).RemainingMainActionsThisTurn, Is.EqualTo(budget));
            string archive = SaveArchive("调度第一移动完成第二来源待选");
            yield return MapAnswer(first, item);
            string second = f.Request.CandidateIds.First(id => id != first);
            yield return MapAnswer(second, second);
            Assert.That(Item(item).SlotId, Is.EqualTo(second));
            AssertDispatchComplete(parentId, budget, completed, false);
            yield return f.Capture("同一影响力实例连续两次移动", (Component)f.Hud);
            yield return Restore(archive);
            Assert.That(Item(item).SlotId, Is.EqualTo(first));
            yield return Stop("跳过调度第二移动");
            Assert.That(Item(item).SlotId, Is.EqualTo(first));
            AssertDispatchComplete(parentId, budget, completed, true);
            yield return f.Capture("停止第二移动保留第一移动", (Component)f.Hud);
        }

        private IEnumerator Start()
        {
            commands.Clear(); f = new GameplayPageAcceptanceFixture();
            yield return f.Start(3, Noop, mapId: "map-four-players");
            player = (int)Read(f.Gameplay, "localPlayerId");
            handlers = (IList<IGameCommandHandler>)Read(f.Session, "commandHandlers");
            effects = (EffectRegistry)Read(handlers.OfType<AnswerInteractionCommandHandler>().Single(), "registry");
            originals = handlers.ToArray();
            for (int i = 0; i < originals.Length; i++) handlers[i] = new Observer(originals[i], (command, result) =>
            {
                commands.Add(JsonConvert.DeserializeObject<GameCommand>(JsonConvert.SerializeObject(command)));
                f.Record("原Session命令结果", new { command, result.Succeeded, reason = result.Validation?.Reason });
                Assert.That(result.Succeeded, Is.True, command.Kind + ":" + result.Validation?.Reason);
            });
            f.Record("联合验收边界", new { f.State.ContentPackHash, player,
                note = "正式三人企业房间使用四人地图。非待验开局响应经正式Submit；角色取得和额外盖牌为明确受控目录起点，使用原实例和正式Effect；不代表自然抽取覆盖。待验操作走实际UI。" });
            SaveArchive("当前版本正式房间开局_尚无受控前置");
        }

        private static IEnumerator Noop(Component _) { yield break; }
        private ItemInstanceState Item(string id) => f.State.ItemZones.Items.Single(i => i.ItemId == id);
        private string CardZone(string id, string local) => f.State.ItemZones.Zones.Single(z => z.ParentId == id && z.LocalZoneId == local).ZoneId;
        private Component[] Rows() => ((IEnumerable)Read(Expansion, "Rows")).Cast<Component>().ToArray();
        private Dictionary<string, string> PublicSupply() => f.State.ItemZones.Items.Where(i => f.State.ItemZones.Zones.Any(z =>
            z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).ToDictionary(i => i.ItemId, i => i.ZoneId);
        private string Draft() => JsonConvert.SerializeObject(new { BuildSelection.Phase, BuildSelection.FacilityId,
            BuildSelection.PlayerId, BuildSelection.PaymentMode, BuildSelection.CityBoardSlotIndex,
            BuildSelection.IsSupplySource, BuildSelection.IsActive, selected = Read(f.Gameplay, "selectedBuildFacilityId") });

        private EffectNodeRuntimeState Run(EffectSpec spec, string boundary)
        {
            var engine = new EffectTreeExecutor(f.State, effects);
            string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            var node = engine.GetNode(id);
            Assert.That(node.Status, Is.Not.EqualTo(EffectNodeStatus.Failed), boundary + ":" + node.FailureReason);
            Assert.That(new ItemZoneQuery().TryValidate(f.State, out string reason), Is.True, reason);
            f.Record("受控正式Effect前置", new { boundary, spec, id, node.Status,
                note = "只制备所述起点，不能计入实际UI输入次数" });
            return node;
        }

        private string PrepareUse(string definition, string mode)
        {
            Assert.That(f.Request, Is.Null, "制备角色来源时不能盖过未完成请求");
            var card = f.State.ItemZones.Items.FirstOrDefault(i => i.DefinitionId == definition &&
                (i.ControllerSubject.Kind != ItemSubjectKind.Player || i.ControllerSubject.PlayerId == player));
            Assert.That(card, Is.Not.Null, "当前正式目录没有可用于受控取牌的实例：" + definition);
            string id = card.ItemId;
            Assert.That(effects.CharacterActivationSubscriptions.ContainsKey(definition + "." + mode), Is.True,
                "本局正式registry必须注册该能力");
            string hand = FindZone(f.State, "hand", player).ZoneId;
            if (card.ZoneId != hand) Run(MoveSpec(player, new[] { id }, card.ZoneId, hand, "unordered", 1,
                "core.character_card_transfer"), "从正式牌池取得既有干员实例 " + definition);
            const string cover = "acceptance.character.additional_cover";
            if (!effects.TryGet(cover, out _)) effects.Register(cover, context => CharacterLifecycleService.Cover(
                context, Text(context.Node.NormalizedArguments, "cardId"), true));
            Run(EffectSpec.Create(cover, Object("cardId", NormalizedValue.CreateString(id)), player),
                "受控额外盖牌槽；跳过取得资格，不重置使用标记 " + definition);
            Run(CharacterLifecycleService.CreateUseSpec(player, id, mode), "真实角色生命周期及Lua " + definition + "/" + mode);
            Assert.That(f.Request, Is.Not.Null, "角色应停在本轮待验的真实请求");
            return id;
        }

        private void AnswerPreparation(string candidate, string value = null, bool decline = false)
        {
            var request = f.Request; Assert.That(request, Is.Not.Null);
            var command = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId),
                request.AnsweringPlayerId, candidate == null ? null : new[] { candidate }, decline);
            if (value != null) command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = value;
            SubmitPreparation(command, "非待验角色前置回答 " + request.PromptKey);
        }

        private void SubmitPreparation(GameCommand command, string note)
        {
            if (string.IsNullOrEmpty(command.CommandId)) command.CommandId = Guid.NewGuid().ToString("N");
            var result = f.Session.Submit(command);
            Assert.That(result.Succeeded, Is.True, note + ":" + result.Validation?.Reason);
            f.Record("非待验正式协议辅助", new { note, command, boundary = "此项不是鼠标输入" });
        }

        private void FundBuild()
        {
            foreach (var resource in new[] { ResourceType.GoldVoucher, ResourceType.Originium, ResourceType.OriginiumShard, ResourceType.Iron })
                Run(ResourceEffectSpecFactory.Gain(player, resource, resource == ResourceType.GoldVoucher ? 80 : 20), "建设付费前置库存");
        }

        private string PreparePublicTwin(string held)
        {
            string definition = Item(held).DefinitionId;
            var existing = f.State.ItemZones.Items.FirstOrDefault(i => i.DefinitionId == definition && PublicSupply().ContainsKey(i.ItemId));
            if (existing != null) return existing.ItemId;
            var displaced = f.State.ItemZones.Items.First(i => PublicSupply().ContainsKey(i.ItemId));
            string slot = displaced.ZoneId;
            Run(MoveSpec(player, new[] { displaced.ItemId }, slot, FindZone(f.State, "facility_deck").ZoneId,
                "bottom", 1, "core.facility_card_transfer"), "同定义实例对照前置腾出一个公共供应槽");
            const string grant = "acceptance.facility.public_twin";
            effects.Register(grant, context => new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId,
                "public_twin", Text(context.Node.NormalizedArguments, "definition"), "core.facility_card",
                Text(context.Node.NormalizedArguments, "zone"), new ItemSubjectRef(), out string id, out string reason)
                ? EffectStepResult.Completed(Object("itemId", NormalizedValue.CreateString(id))) : EffectStepResult.Failed(reason));
            var result = Run(EffectSpec.Create(grant, Object("definition", NormalizedValue.CreateString(definition),
                "zone", NormalizedValue.CreateString(slot)), player), "目录对照额外授予同定义实体；不作为正式配方数量证据");
            return Text(result.NormalizedResult, "itemId");
        }

        private IEnumerator Sync()
        {
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Settle();
        }

        private IEnumerator Click(GameObject target, string label)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            yield return f.Click(target, label);
            f.Record("实际输入小样本耗时", new { label,
                throughUiSettleMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency,
                note = "含自然布局帧的单次输入耗时；不冒充规则提交耗时或P95" });
        }

        private PointerEventData Pointer(GameObject target, PointerEventData.InputButton button, out GameObject handler)
        {
            Assert.That(HotseatUiInputDriver.TryPoint(target, out var point, out _), Is.True, "手势目标必须实际可见");
            var hit = Hit(point);
            handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            Assert.That(handler, Is.EqualTo(ExecuteEvents.GetEventHandler<IPointerClickHandler>(target)), "手势射线被拦截");
            return new PointerEventData(EventSystem.current) { pointerId = -1, button = button, position = point,
                pressPosition = point, eligibleForClick = true, clickCount = 1, clickTime = Time.unscaledTime,
                pointerCurrentRaycast = hit, pointerPressRaycast = hit, pointerPress = handler };
        }

        private IEnumerator ImmediateCardClick(string id)
        {
            var target = HotseatUiInputDriver.Marker("build.candidate." + id);
            var data = Pointer(target, PointerEventData.InputButton.Left, out var handler);
            int callbacks = 0, before = commands.Count;
            var button = handler.GetComponent<Button>();
            UnityEngine.Events.UnityAction observe = () => callbacks++;
            button.onClick.AddListener(observe);
            try
            {
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
                yield return null;
                int releaseFrame = Time.frameCount;
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
                Assert.That(callbacks, Is.EqualTo(1), "点击派发返回前必须同步选中一次");
                Assert.That(Time.frameCount, Is.EqualTo(releaseFrame));
                Assert.That((string)Read(f.Gameplay, "selectedBuildFacilityId"), Is.EqualTo(id));
                Assert.That(commands.Count, Is.EqualTo(before));
                f.Record("同帧选牌", new { id, callbacks, releaseFrame, returnedFrame = Time.frameCount });
            }
            finally { if (button != null) button.onClick.RemoveListener(observe); }
            yield return Settle();
        }

        private IEnumerator PointerPreview(GameObject target, bool held)
        {
            var data = Pointer(target, held ? PointerEventData.InputButton.Left : PointerEventData.InputButton.Right, out var handler);
            int callbacks = 0;
            var button = handler.GetComponent<Button>();
            UnityEngine.Events.UnityAction observe = () => callbacks++;
            button.onClick.AddListener(observe);
            var focus = EventSystem.current.currentSelectedGameObject;
            try
            {
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
                if (held) yield return Wait(() => Active("CardViewer").Length == 1, "长按未打开卡面", 2);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
                yield return Settle();
                Assert.That(callbacks, Is.Zero, "右键或长按释放不得误选");
                var viewer = Active("CardViewer").Single();
                Assert.That(Read(viewer, "Mode").ToString(), Is.EqualTo("Inspect"));
                yield return f.Capture(held ? "长按查看卡面" : "右键查看卡面", viewer);
                yield return Click(viewer.gameObject, "查看器返回原建设草稿");
                if (focus != null && focus.activeInHierarchy) Assert.That(EventSystem.current.currentSelectedGameObject, Is.EqualTo(focus));
            }
            finally { if (button != null) button.onClick.RemoveListener(observe); }
        }

        private IEnumerator DragWithoutSelection(GameObject target)
        {
            var data = Pointer(target, PointerEventData.InputButton.Left, out var handler);
            int callbacks = 0;
            var button = handler.GetComponent<Button>();
            UnityEngine.Events.UnityAction observe = () => callbacks++;
            button.onClick.AddListener(observe);
            try
            {
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.initializePotentialDrag);
                data.position += Vector2.right * (EventSystem.current.pixelDragThreshold + 2);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.beginDragHandler);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
                Assert.That(callbacks, Is.Zero); Assert.That(Active("CardViewer"), Is.Empty);
            }
            finally { if (button != null) button.onClick.RemoveListener(observe); }
            yield return Settle();
        }

        private IEnumerator ChooseCard(string id)
        {
            var request = f.Request; int index = request.CandidateIds.IndexOf(id);
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            var row = Active("FacilityEffectCardView").Single(r => r.name == "Character Effect Option " + index);
            yield return Click(ButtonObject(row, "Button"), "选取真实设施 " + id);
            yield return ConfirmSelection();
            Assert.That(commands.Last().OptionIds, Is.EqualTo(new[] { id }));
        }

        private IEnumerator ChooseOrdinaryCandidate(string id)
        {
            int index = f.Request.CandidateIds.IndexOf(id); Assert.That(index, Is.GreaterThanOrEqualTo(0));
            var row = Active("EffectDialogOptionRowView").Single(r => r.name == "Character Effect Option " + index);
            yield return Click(ButtonObject(row, "Button"), "选择强制响应");
            yield return ConfirmSelection();
        }

        private IEnumerator ConfirmSelection()
        {
            var button = Active("EffectDialogShellView").SelectMany(s => s.GetComponentsInChildren<Button>())
                .Single(b => b.name == "Confirm Selection" || b.transform.parent.name == "Confirm Selection");
            yield return Click(button.gameObject, "确认当前权威候选");
        }

        private IEnumerator ConfirmAllocation()
        {
            var button = Active("EffectDialogShellView").SelectMany(s => s.GetComponentsInChildren<Button>())
                .Single(b => b.name == "Confirm" || b.transform.parent.name == "Confirm");
            yield return Click(button.gameObject, "确认数量或资源分配");
        }

        private IEnumerator Quantity(int amount)
        {
            var request = f.Request;
            Assert.That(request.InteractionTypeId, Is.EqualTo(ContentPrimitiveEffectExecutor.QuantityInteractionTypeId));
            Assert.That(request.CandidateIds, Is.Empty);
            var row = Active("EffectDialogResourceRowView").Single();
            Assert.That(Active("EffectDialogOptionRowView"), Is.Empty, "数量应复用单行分配，不是1至N候选列表");
            for (int i = 0; i < amount; i++) yield return Click(ButtonObject(row, "IncreaseButton"), "增加本次数量");
            if (Read(row, "ValueInput") is InputField input) Assert.That(input.text, Is.EqualTo(amount.ToString(CultureInfo.InvariantCulture)));
            yield return f.Capture("单行数量_" + amount, Active("EffectDialogShellView").Single());
            yield return ConfirmAllocation();
            Assert.That(commands.Last().OptionIds, Is.Empty);
            Assert.That(commands.Last().Parameters[AnswerInteractionCommandHandler.AnswerValueParameter], Is.EqualTo(amount.ToString(CultureInfo.InvariantCulture)));
        }

        private IEnumerator Stop(string label)
        {
            var request = f.Request; Assert.That(request, Is.Not.Null); Assert.That(request.AllowDecline, Is.True);
            int before = commands.Count; Assert.That(EndButton.IsInteractable(), Is.True);
            yield return Click(EndButton.gameObject, label);
            var command = commands.Skip(before).Single();
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction), "停止当前子式不得发EndAction跨过行动窗口");
            Assert.That(command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter], Is.EqualTo(request.GetStableInteractionId()));
            Assert.That(command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter], Is.EqualTo("false"));
        }

        private IEnumerator MapAnswer(string slot, string candidate)
        {
            var request = f.Request; int before = commands.Count;
            Assert.That(request.CandidateIds, Does.Contain(candidate));
            yield return Click(EffectTargetUiAcceptance.MapTarget("InfluenceSlot", slot).gameObject, "地图选取 " + slot);
            // 保留生产入口的预选/确认规则；只有同一请求仍待答时才发第二次独立输入。
            if (f.Request?.GetStableInteractionId() == request.GetStableInteractionId())
                yield return Click(EffectTargetUiAcceptance.MapTarget("InfluenceSlot", slot).gameObject, "地图确认 " + slot);
            var command = commands.Skip(before).Single();
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(command.OptionIds, Is.EqualTo(new[] { candidate }));
        }

        private string SaveArchive(string label)
        {
            string archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State));
            Directory.CreateDirectory(f.DirectoryPath);
            File.WriteAllText(Path.Combine(f.DirectoryPath, label + ".archive.json"), archive);
            return archive;
        }

        private IEnumerator Restore(string archive)
        {
            var result = HostRecoveryService.RecoverJson(archive, effects);
            Assert.That(result.Succeeded, Is.True, result.FaultCode + ":" + result.Diagnostic);
            f.Session.ReplaceState(result.State);
            yield return Sync();
            f.Record("本批同输入正式恢复", new { result.Succeeded, result.Diagnostic, f.State.ContentPackHash });
        }

        private void AssertDispatchComplete(string id, int budget, int completed, bool skipped)
        {
            var parent = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(parent.Status, Is.EqualTo(EffectNodeStatus.Completed));
            var moves = parent.ChildEffectIds.Select(child => f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == child)).ToArray();
            Assert.That(moves.All(n => n.Status == EffectNodeStatus.Completed && !n.AllowDecline), Is.True);
            if (skipped) Assert.That(Text(moves[1].NormalizedResult, "outcome"), Is.EqualTo("skipped"));
            Assert.That(f.State.FindPlayer(player).RemainingMainActionsThisTurn, Is.EqualTo(budget - 1));
            Assert.That(f.State.FindPlayer(player).CompletedMainActionsThisTurn, Is.EqualTo(completed + 1));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.InteractionTypeId == "action.decline_effect"), Is.False);
        }

        private void RegisterMandatoryCleanup()
        {
            const string type = "acceptance.cleanup.required";
            effects.Register(new EffectRegistration(type, context => context.Node.FlowStage.Length > 0 ? EffectStepResult.Completed() :
                EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = "lua.choice", PromptKey = type, AnsweringPlayerId = player,
                    AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "required" } })));
            effects.RegisterEventHandler(new EffectEventHandlerRegistration(type, "PlayerCleanupStarted", "*", type,
                context => context.Event.PlayerId == player ? new List<EffectSpec> { EffectSpec.Create(type, playerId: player) } : new List<EffectSpec>(),
                EffectHandlerRole.Observer));
            f.Record("受控强制收尾响应", new { type, note = "专用无收益强制响应只用于验证强制先于可选；多萝西可选项仍来自原生角色Lua" });
        }

        private IEnumerator AdvanceToOwnCleanup()
        {
            var service = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId)));
            for (int guard = 0; guard < 90; guard++)
            {
                if (f.Request?.PromptKey == "acceptance.cleanup.required") { yield return Sync(); yield break; }
                var request = f.Request;
                if (request != null)
                {
                    string candidate = request.InteractionTypeId == "route_resource.selection"
                        ? request.CandidateIds.Single(id => id.EndsWith(".commit", StringComparison.Ordinal)) : request.CandidateIds.FirstOrDefault();
                    if (request.AllowDecline) AnswerPreparation(null, null, true);
                    else if (request.CandidateIds.Count == 0) AnswerPreparation(null, "true");
                    else
                    {
                        var command = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId),
                            request.AnsweringPlayerId, request.InteractionTypeId == "route_resource.selection" ? new[] { candidate } :
                                request.CandidateIds.Take(Math.Max(1, request.MinSelections)).ToArray());
                        SubmitPreparation(command, "非待验回合推进响应 " + request.PromptKey);
                    }
                }
                else
                {
                    int actor = f.State.CurrentPlayerId;
                    bool action = (f.State.Phase == GamePhase.ActionRound1 || f.State.Phase == GamePhase.ActionRound2) &&
                        f.State.FindPlayer(actor).RemainingMainActionsThisTurn > 0;
                    SubmitPreparation(new GameCommand { Kind = action ? GameCommandKind.DeployInfluence : GameCommandKind.EndAction,
                        PlayerId = actor, TargetId = action ? service.GetLegalPlacementSlotIds(f.State, actor).First() : "" },
                        "经正式主行动、结束和采集推进到当前玩家收尾");
                }
                yield return Sync();
            }
            Assert.Fail("正式协议推进未到达本人的强制收尾响应");
        }

        private sealed class Observer : IGameCommandHandler
        {
            private readonly IGameCommandHandler inner;
            private readonly Action<GameCommand, CommandResult> observe;
            internal Observer(IGameCommandHandler inner, Action<GameCommand, CommandResult> observe)
            { this.inner = inner; this.observe = observe; }
            public bool CanHandle(GameCommand command) => inner.CanHandle(command);
            public CommandResult Handle(GameState state, GameCommand command)
            { var result = inner.Handle(state, command); observe(command, result); return result; }
        }
    }
}
