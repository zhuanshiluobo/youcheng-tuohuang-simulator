using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
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
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 两个组合方法使用正式房间及实际场景。目录取牌、额外盖牌、直接建设 Effect 为受控前置；
    // 待验分支、地图和资料入口走 EventSystem，不修改生产资产，不替换 Host State 或查看权限。
    [Timeout(600000)]
    public sealed class MercenaryAndViewedPilesUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private EffectRegistry effects;
        private IList<IGameCommandHandler> handlers;
        private IGameCommandHandler[] originals;
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private int player;
        private string buildRoot;
        private string facilityItem;
        private string preparedInfluenceSlot;
        private object Modules => Read(f.Hud, "MainModules");
        private Component Hand => (Component)Read(f.Gameplay, "characterHandPanel");
        private Component City => (Component)Read(f.Gameplay, "buildInfoPanel");
        private object Catalog => Read(f.Registry, "CardVisualCatalog");
        private Component HandPile => (Component)Read(Read(Hand, "View"), "HandPile");
        private bool DiscardPreviewOpen => (bool)Read(Hand, "IsDiscardPreviewOpen");
        private GameStateView Visible => (GameStateView)Call(f.Gameplay, "VisibleSaleState");
        private Text CityTitle => (Text)Read(Modules, "cityTitle");
        private Component Covered => (Component)Read(Modules, "coveredPile");
        private Component Discard => (Component)Read(Modules, "discardPile");

        [UnityTearDown]
        public IEnumerator Release()
        {
            if (handlers != null && originals != null)
                for (var i = 0; i < originals.Length; i++) handlers[i] = originals[i];
            if (f?.Session != null)
            {
                Directory.CreateDirectory(f.DirectoryPath);
                File.WriteAllText(Path.Combine(f.DirectoryPath, "实际命令.json"), JsonConvert.SerializeObject(commands, Formatting.Indented));
                File.WriteAllText(Path.Combine(f.DirectoryPath, "终态.archive.json"),
                    HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State)));
                if (f.Gameplay != null) yield return f.Capture("当前场景终态或失败现场", (Component)f.Hud);
            }
            handlers = null; originals = null;
        }

        [UnityTest]
        public IEnumerator MercenaryEntryMapsRealInfluenceInstanceAndReplacesOnce()
        {
            yield return Start("map-four-players", "SampleScene");
            PrepareMercenaryEntry();
            yield return Sync();
            Assert.That(f.Request.PromptKey, Is.EqualTo("facility.entry.choose_influence_branch"));
            int before = commands.Count;
            yield return ChooseBranch("replace-influence");
            Assert.That(commands.Skip(before).Single().OptionIds, Is.EqualTo(new[] { "replace-influence" }));
            var request = f.Request;
            AssertReplacementRequest(request);
            var projected = Visible;
            var mappings = request.CandidateIds.Select(id => new
            {
                candidate = id,
                influence = projected.Map.Influences.Single(i => i.InfluenceId == id)
            }).ToArray();
            Assert.That(mappings, Is.Not.Empty);
            foreach (var mapping in mappings)
            {
                Assert.That(mapping.candidate, Is.Not.EqualTo(mapping.influence.SlotId));
                var target = EffectTargetUiAcceptance.MapTarget("InfluenceSlot", mapping.influence.SlotId);
                Assert.That((bool)Read(target, "IsInteractive"), Is.True);
                Assert.That(((SpriteRenderer)Read(target, "Image")).enabled, Is.True);
            }
            Assert.That(Active("EffectDialogShellView"), Is.Empty, "正式替换请求应由地图承接");
            var selected = mappings.Single(m => m.influence.SlotId == preparedInfluenceSlot);
            f.Record("真实实例到地图槽位映射", new { facilityItem, buildRoot,
                request.InteractionTypeId, request.PromptKey, request.AllowDecline,
                mappings = mappings.Select(m => new { m.candidate, m.influence.SlotId, m.influence.PlayerId }).ToArray() });
            yield return f.Capture("佣兵替换真实影响力高亮", (Component)f.Hud);
            int eventCount = f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceReplaced);
            before = commands.Count;
            yield return f.Click(EffectTargetUiAcceptance.MapTarget("InfluenceSlot", selected.influence.SlotId).gameObject, "实际地图选择替换目标");
            if (f.Request?.GetStableInteractionId() == request.GetStableInteractionId())
                yield return f.Click(EffectTargetUiAcceptance.MapTarget("InfluenceSlot", selected.influence.SlotId).gameObject, "实际地图确认替换目标");
            var command = commands.Skip(before).Single();
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(command.PlayerId, Is.EqualTo(player));
            Assert.That(command.OptionIds, Is.EqualTo(new[] { selected.candidate }), "提交必须保留正式 ItemId，不能提交 SlotId");
            Assert.That(f.Request, Is.Null);
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == buildRoot).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.Map.Influences.Single(i => i.SlotId == selected.influence.SlotId).PlayerId, Is.EqualTo(player));
            Assert.That(f.State.Map.Influences.Any(i => i.InfluenceId == selected.candidate), Is.False);
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceReplaced), Is.EqualTo(eventCount + 1));
            Assert.That(new ItemZoneQuery().TryValidate(f.State, out var reason), Is.True, reason);
            yield return f.Capture("佣兵替换仅完成一次", (Component)f.Hud);
        }

        [UnityTest]
        public IEnumerator PlayerPileInspectionPreservesViewerAndPendingChoice()
        {
            yield return Start("map-three-players", "ThreePlayerScene");
            var opponents = f.State.Players.Where(p => p.PlayerId != player).Select(p => p.PlayerId).ToArray();
            int first = opponents[0], second = opponents[1];
            PreparePiles(first, second);
            PrepareMercenaryEntry();
            var preparation = f.Request;
            var result = f.Session.Submit(EffectInteractionCommands.Answer(
                InteractionRequestProjector.ProjectForPlayer(preparation, player), player, new[] { "replace-influence" }));
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            f.Record("非待验分支前置", new { note = "第二方法只验查看行为；替换分支由正式协议建立，第一方法另验真实界面分支。" });
            yield return Sync();
            AssertReplacementRequest(f.Request);
            string pending = JsonConvert.SerializeObject(f.Request);
            string state = JsonConvert.SerializeObject(f.State);
            int submitted = commands.Count;
            var session = f.Session;
            var ownFont = CityTitle.font;
            bool ownRichText = CityTitle.supportRichText;
            string ownTitle = CityTitle.text;
            var ownHand = Keys(HandPile);
            var ownCovered = Keys(Covered);
            var ownDiscard = Keys(Discard);
            string resources = LocalResources();
            CheckOwnCity();

            yield return Inspect(first);
            CheckInspection(first);
            Assert.That(BackGroups(first, "core.zone.character_hand").Keys,
                Does.Contain("operator_back").And.Contain("flowing_shape_back")
                    .And.Contain("character_back_" + (int)f.State.FindPlayer(player).Color));
            Assert.That(BackGroups(first, "core.zone.character_covered").Keys, Does.Contain("operator_back"));
            yield return f.Capture("甲城市及基础干员流形三类牌背", (Component)f.Hud);
            yield return ProbeHiddenPile(HandPile, "甲手牌");
            yield return ProbeHiddenPile(Covered, "甲盖放");
            CheckUnchanged(state, pending, submitted, session, resources);
            yield return OpenPublicDiscard(first);
            yield return f.Capture("甲公开弃牌正面查看", Active("CardViewer").Single());
            yield return ClosePublicDiscard();
            CheckUnchanged(state, pending, submitted, session, resources);

            yield return Inspect(second);
            CheckInspection(second);
            Assert.That(Active("CardViewer"), Is.Empty);
            yield return f.Capture("乙四区计数同步", (Component)f.Hud);
            CheckUnchanged(state, pending, submitted, session, resources);
            yield return OpenPublicDiscard(second);
            // 有明确边界的一次正式投影更新：只移动乙的一张真实手牌到公开弃牌，不回答本人待选。
            string moved = f.State.ItemZones.Items.First(i => i.ZoneId == FindZone(f.State, "hand", second).ZoneId).ItemId;
            MoveCard(moved, second, "discard", "查看期间乙牌区的受控正式更新");
            yield return Sync();
            CheckInspection(second);
            Assert.That(Active("CardViewer"), Is.Empty, "更新后旧详情不得继续显示或经旧返回回调复活");
            Assert.That(DiscardPreviewOpen, Is.False);
            Assert.That(JsonConvert.SerializeObject(f.Request), Is.EqualTo(pending));
            Assert.That(commands.Count, Is.EqualTo(submitted));
            state = JsonConvert.SerializeObject(f.State);
            yield return f.Capture("乙投影更新保留查看目标", (Component)f.Hud);
            yield return ReturnThroughCity();
            CheckOwnCity();
            Assert.That(Keys(HandPile), Is.EqualTo(ownHand));
            Assert.That(Keys(Covered), Is.EqualTo(ownCovered));
            Assert.That(Keys(Discard), Is.EqualTo(ownDiscard));
            Assert.That(CityTitle.font, Is.SameAs(ownFont));
            Assert.That(CityTitle.supportRichText, Is.EqualTo(ownRichText));
            Assert.That(CityTitle.text, Is.EqualTo(ownTitle), "返回只恢复本次场景原文案，不锁定固定文案");
            CheckUnchanged(state, pending, submitted, session, resources);
            yield return f.Capture("城市返回本人四区与原待选", (Component)f.Hud);
        }

        private IEnumerator Start(string map, string scene)
        {
            commands.Clear();
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(3, Noop, mapId: map);
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo(scene));
            player = (int)Read(f.Gameplay, "localPlayerId");
            handlers = (IList<IGameCommandHandler>)Read(f.Session, "commandHandlers");
            effects = (EffectRegistry)Read(handlers.OfType<AnswerInteractionCommandHandler>().Single(), "registry");
            originals = handlers.ToArray();
            for (var i = 0; i < originals.Length; i++) handlers[i] = new Observer(originals[i], (command, result) =>
            {
                commands.Add(JsonConvert.DeserializeObject<GameCommand>(JsonConvert.SerializeObject(command)));
                f.Record("原Session命令", new { command, result.Succeeded, reason = result.Validation?.Reason });
                Assert.That(result.Succeeded, Is.True, command.Kind + ":" + result.Validation?.Reason);
            });
            Directory.CreateDirectory(f.DirectoryPath);
            File.WriteAllText(Path.Combine(f.DirectoryPath, "正式开局.archive.json"),
                HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State)));
            f.Record("当前场景来源及边界", new { scene, map, player, f.State.ContentPackHash,
                hud = HotseatUiInputDriver.PathOf(((Component)f.Hud).gameObject),
                note = "固定一个尺寸，每方法一个实际场景；本地原Session的授权投影，不作为远端网络验收。" });
#if UNITY_EDITOR
            var hud = (Component)f.Hud;
            var source = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(hud);
            var modifications = UnityEditor.PrefabUtility.GetPropertyModifications(hud.gameObject);
            f.Record("实际加载场景与HUD源及覆盖", new
            {
                scenePath = SceneManager.GetActiveScene().path,
                sourcePath = UnityEditor.AssetDatabase.GetAssetPath(source),
                overrides = modifications?.Select(m => new { m.propertyPath, m.value,
                    referencePath = UnityEditor.AssetDatabase.GetAssetPath(m.objectReference) }).ToArray()
            });
#endif
        }

        private static IEnumerator Noop(Component _) { yield break; }
        private ItemInstanceState Item(string id) => f.State.ItemZones.Items.Single(i => i.ItemId == id);
        private static string[] Keys(Component pile) => ((IEnumerable)Read(pile, "regionKeys")).Cast<string>().ToArray();
        private static Component[] Slots(Component pile) => ((IEnumerable)Read(pile, "slots")).Cast<Component>().ToArray();
        private string LocalResources() => JsonConvert.SerializeObject(new
        {
            values = ((Text[])Read(Modules, "localResourceValues")).Select(t => t.text).ToArray(),
            voucher = ((Text)Read(Modules, "localVoucherValue")).text,
            score = ((Text)Read(Modules, "localScoreValue")).text
        });

        private EffectNodeRuntimeState Run(EffectSpec spec, string boundary)
        {
            var engine = new EffectTreeExecutor(f.State, effects);
            string root = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            var node = engine.GetNode(root);
            Assert.That(node.Status, Is.Not.EqualTo(EffectNodeStatus.Failed), boundary + ":" + node.FailureReason);
            Assert.That(new ItemZoneQuery().TryValidate(f.State, out var reason), Is.True, reason);
            f.Record("受控正式Effect", new { boundary, root, spec, node.Status,
                note = "此操作不计入真实鼠标输入或自然取得覆盖。" });
            return node;
        }

        private void PrepareMercenaryEntry()
        {
            Assert.That(f.Request, Is.Null);
            int opponent = f.State.Players.First(p => p.PlayerId != player).PlayerId;
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId)));
            var replacementSlots = new HashSet<string>(influence.GetLegalPlacementSlotIds(f.State, player));
            string slot = influence.GetLegalPlacementSlotIds(f.State, opponent).First(id => replacementSlots.Contains(id) &&
                Active("MapButtonView").Any(t => Read(t, "TargetKind").ToString() == "InfluenceSlot" && (string)Read(t, "TargetId") == id));
            preparedInfluenceSlot = slot;
            Run(InfluenceEffectSpecFactory.PlaceInfluence(opponent, slot), "佣兵目标的真实对手影响力");
            Run(ResourceEffectSpecFactory.Gain(player, ResourceType.GoldVoucher, 30), "佣兵正式建设付费库存");
            var card = f.State.ItemZones.Items.First(i => i.DefinitionId == "building_034" &&
                f.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId &&
                    (z.LocalZoneId == "facility_deck" || z.TemplateId == "core.zone.facility_supply_slot")));
            facilityItem = card.ItemId;
            int citySlot = Enumerable.Range(0, BuildFacilityService.CityBoardSlotCount).First(index =>
                BuildFacilityService.CreateForEffectTree().ValidatePaidPlacement(f.State, player, card.DefinitionId, index).IsValid);
            buildRoot = Run(FacilityBuildEffectSpecFactory.Build(player, card.ItemId, citySlot,
                BuildFacilityService.PaymentModeGold, false, "acceptance.mercenary.entry", false, card.ZoneId),
                "正式付费建设既有佣兵实例并进入真实入场Lua；取牌和落位为受控前置").EffectId;
            Assert.That(f.State.Map.Facilities.Any(p => p.ContentInstanceId == facilityItem && p.PlayerId == player), Is.True);
            Assert.That(f.Request.PromptKey, Is.EqualTo("facility.entry.choose_influence_branch"));
        }

        private static void AssertReplacementRequest(InteractionRequest request)
        {
            Assert.That(request, Is.Not.Null);
            Assert.That(request.InteractionTypeId, Is.EqualTo("lua.choice"));
            Assert.That(request.PromptKey, Is.EqualTo("effect.influence.replace.choose_target"));
            Assert.That(request.AnswerSchema, Is.EqualTo("candidate_id"));
            Assert.That(request.AllowDecline, Is.False);
        }

        private IEnumerator ChooseBranch(string id)
        {
            int index = f.Request.CandidateIds.IndexOf(id);
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            var row = Active("EffectDialogOptionRowView").Single(r => r.name == "Character Effect Option " + index);
            yield return f.Click(ButtonObject(row, "Button"), "佣兵入场选择替换分支");
            var confirm = Active("EffectDialogShellView").SelectMany(s => s.GetComponentsInChildren<Button>())
                .Single(b => b.name == "Confirm Selection" || b.transform.parent.name == "Confirm Selection");
            yield return f.Click(confirm.gameObject, "确认佣兵替换分支");
        }

        private void PreparePiles(int first, int second)
        {
            string ownBasic = f.State.ItemZones.Items.First(i => i.ZoneId == FindZone(f.State, "hand", player).ZoneId &&
                i.TypeId == "core.basic_character_card").ItemId;
            string otherBasic = f.State.ItemZones.Items.First(i => i.ZoneId == FindZone(f.State, "hand", first).ZoneId &&
                i.TypeId == "core.basic_character_card").ItemId;
            Run(MoveSpec(player, new[] { ownBasic }, Item(ownBasic).ZoneId, FindZone(f.State, "retired").ZoneId,
                "unordered", 1, "core.character_card_transfer"), "交换基础牌的受控暂退前置，避免超过五张保留上限");
            Run(MoveSpec(first, new[] { otherBasic }, Item(otherBasic).ZoneId, FindZone(f.State, "hand", player).ZoneId,
                "unordered", 1, "core.character_card_transfer"), "对手基础牌转交本人，释放对手保留名额");
            Run(MoveSpec(first, new[] { ownBasic }, Item(ownBasic).ZoneId, FindZone(f.State, "hand", first).ZoneId,
                "unordered", 1, "core.character_card_transfer"), "转交本人基础牌，保留原归属颜色背");
            string handOperator = TakeOperator(first, "operator.dorothy");
            string coveredOperator = TakeOperator(first, "operator.closure");
            string flowingShape = PrepareFlowingShape(first);
            const string cover = "acceptance.character.additional_cover";
            effects.Register(cover, context => CharacterLifecycleService.Cover(context, Text(context.Node.NormalizedArguments, "cardId"), true));
            Run(EffectSpec.Create(cover, Object("cardId", NormalizedValue.CreateString(coveredOperator)), first), "甲额外盖放真实干员");
            foreach (int owner in new[] { first, second })
            {
                string discard = f.State.ItemZones.Items.First(i => i.ZoneId == FindZone(f.State, "hand", owner).ZoneId &&
                    i.ItemId != ownBasic && i.ItemId != handOperator && i.ItemId != flowingShape).ItemId;
                MoveCard(discard, owner, "discard", "建立公开弃牌查看起点");
            }
            f.Record("混合牌背前置", new { first, second, ownBasic, handOperator, coveredOperator, flowingShape,
                originalOwner = Item(ownBasic).OwnerSubject.PlayerId,
                note = "实例ID只在测试端前置日志中记录，不向他人手牌及盖放UI提供。" });
        }

        private string PrepareFlowingShape(int owner)
        {
            const string grant = "acceptance.character.flowing_shape";
            const string definition = "infected.character.flowing_shape_alpha";
            effects.Register(grant, context => new ItemZoneStateFactory().TryCreateGrantedItem(
                context.State, context.Node.EffectId, "flowing_shape", definition, "core.generated_character_card",
                FindZone(context.State, "hand", context.Node.PlayerId).ZoneId, ItemSubjectRef.Player(context.Node.PlayerId),
                out string id, out string reason)
                ? EffectStepResult.Completed(Object("itemId", NormalizedValue.CreateString(id)))
                : EffectStepResult.Failed(reason));
            var node = Run(EffectSpec.Create(grant, Object(), owner),
                "三类实体牌背对照：正式授予接口额外建立一张流形；不改LocalState选背，不代表感染者组合或自然生成能力验收");
            return Text(node.NormalizedResult, "itemId");
        }

        private string TakeOperator(int owner, string definition)
        {
            var item = f.State.ItemZones.Items.First(i => i.DefinitionId == definition &&
                (i.ControllerSubject.Kind != ItemSubjectKind.Player || i.ControllerSubject.PlayerId == owner));
            if (item.ZoneId != FindZone(f.State, "hand", owner).ZoneId) MoveCard(item.ItemId, owner, "hand", "从正式目录取得既有干员实例");
            return item.ItemId;
        }

        private void MoveCard(string id, int owner, string zone, string boundary) =>
            Run(MoveSpec(owner, new[] { id }, Item(id).ZoneId, FindZone(f.State, zone, owner).ZoneId,
                "unordered", 1, "core.character_card_transfer"), boundary);

        private IEnumerator Sync()
        {
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Settle();
        }

        private IEnumerator Inspect(int id)
        {
            var ids = (int[])Read(Modules, "opponentIds");
            int index = Array.IndexOf(ids, id);
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            yield return f.Click(((Button[])Read(Modules, "opponentPortraits"))[index].gameObject, "实际头像切换玩家 " + id);
        }

        private Dictionary<string, int> BackGroups(int id, string template) => Visible.ItemZones.Zones
            .Where(z => z.Zone.OwnerPlayerId == id && z.Zone.TemplateId == template).SelectMany(z => z.CardBackGroups)
            .GroupBy(g => g.BackVisualKey).ToDictionary(g => g.Key, g => g.Sum(v => v.Count));

        private void CheckInspection(int id)
        {
            var visible = Visible;
            Assert.That(visible.ViewerPlayerId, Is.EqualTo(player));
            Assert.That((int)Read(f.Gameplay, "displayedCityPlayerId"), Is.EqualTo(id));
            Assert.That((int)Read(City, "displayedCityPlayerId"), Is.EqualTo(id));
            Assert.That((bool)Read(City, "IsOwnCityDisplayed"), Is.False);
            CheckHiddenPile(HandPile, id, "core.zone.character_hand");
            CheckHiddenPile(Covered, id, "core.zone.character_covered");
            int covered = BackGroups(id, "core.zone.character_covered").Values.Sum();
            var discardZones = visible.ItemZones.Zones.Where(z => z.Zone.OwnerPlayerId == id && z.Zone.TemplateId == "core.zone.character_discard").ToArray();
            var discardIds = visible.ItemZones.Items.Where(i => discardZones.Any(z => z.Zone.ZoneId == i.ZoneId)).Select(i => i.ItemId).ToArray();
            Assert.That(Keys(Discard), Is.EquivalentTo(discardIds));
            Assert.That(discardIds.Length, Is.EqualTo(discardZones.Sum(z => z.ItemCount)));
            Assert.That(((Text)Read(Modules, "coveredCountText")).text, Is.EqualTo(string.Format((string)Read(Modules, "coveredCountFormat"), covered)));
            Assert.That(((Text)Read(Modules, "discardCountText")).text, Is.EqualTo(string.Format((string)Read(Modules, "discardCountFormat"), discardIds.Length)));
            foreach (var binding in ((IEnumerable)Read(City, "cityBoardSlotBindings")).Cast<object>())
            {
                int slot = (int)Read(binding, "SlotIndex");
                var facility = visible.Map.Facilities.SingleOrDefault(p => p.PlayerId == id && p.CityBoardSlotIndex == slot);
                Assert.That((int)Read(binding, "PlayerId"), Is.EqualTo(id));
                Assert.That((string)Read(binding, "ContentInstanceId"), Is.EqualTo(facility?.ContentInstanceId ?? string.Empty));
                Assert.That(((Behaviour)Read(Read(binding, "Slot"), "DropTarget")).enabled, Is.False);
            }
            var expectedFont = (Font)Read(Modules, "viewedCityTitleFont");
            Assert.That(expectedFont, Is.Not.Null);
            Assert.That(CityTitle.font, Is.SameAs(expectedFont));
            Assert.That(CityTitle.fontStyle, Is.EqualTo(FontStyle.Normal));
            Assert.That(CityTitle.supportRichText, Is.False);
#if UNITY_EDITOR
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(UnityEditor.AssetDatabase.GetAssetPath(expectedFont)),
                Is.EqualTo("43e9d69dcca63724db85852acd46004d"), "本轮明确指定的普通字体引用");
#endif
            Assert.That(CityTitle.text, Does.Contain(visible.Players.Single(p => p.PlayerId == id).Name));
            f.Record("四区域当前投影", new { id, viewer = visible.ViewerPlayerId, visible.StateRevision,
                hand = Keys(HandPile).Length, covered, discarded = discardIds.Length,
                handBacks = BackGroups(id, "core.zone.character_hand"), coveredBacks = BackGroups(id, "core.zone.character_covered"),
                titleFont = CityTitle.font.name, source = f.Session.View == null ? "Host授权投影" : "session.View" });
        }

        private void CheckHiddenPile(Component pile, int owner, string template)
        {
            var zones = Visible.ItemZones.Zones.Where(z => z.Zone.OwnerPlayerId == owner && z.Zone.TemplateId == template).ToArray();
            var groups = BackGroups(owner, template);
            var slots = Slots(pile);
            Assert.That(slots.Length, Is.EqualTo(zones.Sum(z => z.ItemCount)));
            var expected = groups.SelectMany(g => Enumerable.Repeat((Texture)Call(Catalog, "GetCharacterBack", g.Key), g.Value)).ToArray();
            Assert.That(expected.All(texture => texture != null), Is.True);
            Assert.That(slots.Select(s => ((RawImage)Read(s, "Artwork")).texture), Is.EquivalentTo(expected));
            var hidden = f.State.ItemZones.Items.Where(i => zones.Any(z => z.Zone.ZoneId == i.ZoneId)).Select(i => i.ItemId).ToArray();
            Assert.That(Keys(pile).Intersect(hidden), Is.Empty);
            Assert.That(Visible.ItemZones.Items.Any(i => hidden.Contains(i.ItemId)), Is.False);
            foreach (var slot in slots)
            {
                Assert.That(((Button)Read(slot, "button")).IsInteractable(), Is.False);
                Assert.That(Read(slot, "clicked"), Is.Null);
                Assert.That(((Graphic)Read(slot, "hitSurface")).raycastTarget, Is.False);
                foreach (var node in slot.GetComponentsInChildren<Transform>(true))
                    Assert.That(hidden.Any(id => node.name.Contains(id)), Is.False);
                foreach (var marker in slot.GetComponentsInChildren<Component>(true).Where(c =>
                    c != null && c.GetType().FullName == "YC.PlayerJourney.PlayerAutomationId"))
                    Assert.That(hidden.Any(id => ((string)Read(marker, "Id")).Contains(id)), Is.False);
            }
        }

        private void CheckOwnCity()
        {
            Assert.That((int)Read(f.Gameplay, "displayedCityPlayerId"), Is.EqualTo(player));
            Assert.That((bool)Read(City, "IsOwnCityDisplayed"), Is.True);
            Assert.That(((GameObject)Read(Modules, "viewedCityReturnHint")).activeInHierarchy, Is.False);
        }

        private void CheckUnchanged(string state, string pending, int submitted, GameSession session, string resources)
        {
            Assert.That((int)Read(f.Gameplay, "localPlayerId"), Is.EqualTo(player));
            Assert.That(Visible.ViewerPlayerId, Is.EqualTo(player));
            Assert.That(f.Session, Is.SameAs(session));
            Assert.That(JsonConvert.SerializeObject(f.Request), Is.EqualTo(pending));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(state));
            Assert.That(commands.Count, Is.EqualTo(submitted), "查看输入不能产生本人规则命令");
            Assert.That(LocalResources(), Is.EqualTo(resources));
        }

        private IEnumerator OpenPublicDiscard(int owner)
        {
            yield return f.Click(ButtonObject(Modules, "discardPreviewButton"), "打开目标玩家公开弃牌");
            Assert.That(DiscardPreviewOpen, Is.True);
            var zoneIds = Visible.ItemZones.Zones.Where(z => z.Zone.OwnerPlayerId == owner && z.Zone.TemplateId == "core.zone.character_discard")
                .Select(z => z.Zone.ZoneId).ToArray();
            var card = Visible.ItemZones.Items.First(i => zoneIds.Contains(i.ZoneId));
            yield return InspectReadOnlyCard(HotseatUiInputDriver.Marker("selection.card." + card.ItemId));
            var viewer = Active("CardViewer").Single();
            Assert.That(Read(viewer, "DisplayedSprite"), Is.SameAs(Call(Catalog, "GetCharacterFront", card.DefinitionId)));
            Assert.That(Read(viewer, "Mode").ToString(), Is.EqualTo("Inspect"));
        }

        private IEnumerator InspectReadOnlyCard(GameObject target)
        {
            Assert.That(HotseatUiInputDriver.TryPoint(target, out var point, out var hit), Is.True);
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            Assert.That(handler, Is.SameAs(ExecuteEvents.GetEventHandler<IPointerClickHandler>(target)));
            var data = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left,
                position = point, pressPosition = point, eligibleForClick = true, clickCount = 1,
                clickTime = Time.unscaledTime, pointerCurrentRaycast = hit, pointerPressRaycast = hit };
            f.Record("只读卡面实际单击", new { target = HotseatUiInputDriver.PathOf(target),
                hit = HotseatUiInputDriver.PathOf(hit.gameObject), point = new[] { point.x, point.y },
                note = "只读 CardPicker 单击走 preview，不要求普通 Button.onClick；成功条件为真实查看器打开。" });
            data.pointerPress = ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerDownHandler) ?? handler;
            yield return null;
            Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(point).gameObject), Is.SameAs(handler));
            ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
            yield return Wait(() => Active("CardViewer").Length == 1, "只读卡面单击未打开真实查看器");
            yield return Settle();
        }

        private IEnumerator ClosePublicDiscard()
        {
            yield return f.Click(Active("CardViewer").Single().gameObject, "关闭公开弃牌卡面");
            Assert.That(DiscardPreviewOpen, Is.True);
            yield return f.Click(Active("EffectDialogShellView").Single().gameObject, "点击公开弃牌页背景返回");
            Assert.That(Active("CardViewer"), Is.Empty);
            Assert.That(DiscardPreviewOpen, Is.False);
        }

        private IEnumerator ProbeHiddenPile(Component pile, string label)
        {
            Assert.That(Slots(pile), Is.Not.Empty);
            // 牌面不接射线；从实际可见牌面范围采样，向真实最上层命中派发，禁止直接调用禁用按钮。
            var slot = Slots(pile).Last();
            var rect = ScreenRect((RectTransform)Read(slot, "Root"));
            var viewport = ScreenRect((RectTransform)Read(pile, "viewport"));
            var point = new Vector2(Mathf.Clamp(rect.center.x, viewport.xMin + 1, viewport.xMax - 1),
                Mathf.Clamp(rect.center.y, viewport.yMin + 1, viewport.yMax - 1));
            foreach (var gesture in new[] { "左键", "右键", "长按", "拖动" })
            {
                var hit = Hit(point);
                var button = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject)?.GetComponent<Button>();
                Assert.That(button == null || !button.IsInteractable(), Is.True, label + "采样点意外命中其他活动按钮");
                var data = new PointerEventData(EventSystem.current) { pointerId = -1, position = point, pressPosition = point,
                    button = gesture == "右键" ? PointerEventData.InputButton.Right : PointerEventData.InputButton.Left,
                    eligibleForClick = true, clickCount = 1, pointerCurrentRaycast = hit, pointerPressRaycast = hit };
                f.Record("隐藏牌实际手势", new { label, gesture, hit = HotseatUiInputDriver.PathOf(hit.gameObject), point = new[] { point.x, point.y } });
                var pressed = ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerDownHandler);
                if (gesture == "长按")
                {
                    float until = Time.realtimeSinceStartup + .85f;
                    while (Time.realtimeSinceStartup < until) yield return null;
                }
                else yield return null;
                if (gesture == "拖动")
                {
                    var drag = ExecuteEvents.GetEventHandler<IDragHandler>(hit.gameObject);
                    data.position += new Vector2(EventSystem.current.pixelDragThreshold + 2, 0);
                    data.dragging = true; data.eligibleForClick = false;
                    ExecuteEvents.Execute(drag, data, ExecuteEvents.beginDragHandler);
                    ExecuteEvents.Execute(drag, data, ExecuteEvents.dragHandler);
                    ExecuteEvents.Execute(drag, data, ExecuteEvents.endDragHandler);
                }
                ExecuteEvents.Execute(pressed, data, ExecuteEvents.pointerUpHandler);
                if (data.eligibleForClick) ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerClickHandler);
                yield return Settle();
                Assert.That(Active("CardViewer"), Is.Empty, label + gesture + "不能打开隐藏正面");
                Assert.That(DiscardPreviewOpen, Is.False);
            }
        }

        private IEnumerator ReturnThroughCity()
        {
            var surface = (Component)Read(Modules, "cityInspectionInput");
            var rect = ScreenRect((RectTransform)surface.transform);
            Vector2 point = default; RaycastResult ray = default; bool found = false;
            foreach (float y in new[] { .5f, .1f, .9f, .025f, .975f })
            {
                foreach (float x in new[] { .5f, .1f, .9f, .025f, .975f })
                {
                    var candidate = new Vector2(Mathf.Lerp(rect.xMin, rect.xMax, x), Mathf.Lerp(rect.yMin, rect.yMax, y));
                    var hits = new List<RaycastResult>();
                    EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = candidate }, hits);
                    if (hits.Count == 0 || ExecuteEvents.GetEventHandler<IPointerDownHandler>(hits[0].gameObject) != surface.gameObject) continue;
                    point = candidate; ray = hits[0]; found = true; break;
                }
                if (found) break;
            }
            Assert.That(found, Is.True, "当前场景城市返回表面不可达");
            var data = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left,
                position = point, pressPosition = point, pointerCurrentRaycast = ray, pointerPressRaycast = ray, eligibleForClick = true };
            ExecuteEvents.Execute(surface.gameObject, data, ExecuteEvents.pointerDownHandler);
            yield return null;
            data.pointerCurrentRaycast = Hit(point);
            Assert.That(ExecuteEvents.GetEventHandler<IPointerDownHandler>(data.pointerCurrentRaycast.gameObject), Is.SameAs(surface.gameObject));
            ExecuteEvents.Execute(surface.gameObject, data, ExecuteEvents.pointerUpHandler);
            Assert.That(data.eligibleForClick, Is.False, "城市返回须消费同次点击");
            Assert.That((bool)T("CityBoardInspectionInput").GetProperty("ReturnedThisFrame").GetValue(null), Is.True);
            // 抬起仅交还按下的原对象；不向返回后显露的城市按钮再次派发。
            yield return Settle();
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
