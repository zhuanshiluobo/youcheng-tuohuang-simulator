using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Effects;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Enterprises;
using YC.Domain.State;
using YC.Presentation.Workflows;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;
using Object = UnityEngine.Object;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 第三轮迁移：叠层整体查看、只读科室导航与固定板禁用输入；不重跑完整对局。
    [Timeout(600000)]
    public sealed class EnterpriseDepartmentOverlayUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private EnterpriseSelectionProjection initialDepartment;
        private GameStateView initialView;
        private readonly List<Component> ownedPages = new List<Component>();
        private readonly Dictionary<string, float[]> buildHeaderInsets = new Dictionary<string, float[]>();
        private object Expansion => Read(f.Hud, "ExpansionUi");
        private static TData Clone<TData>(TData value) => JsonConvert.DeserializeObject<TData>(JsonConvert.SerializeObject(value));
        private static EnterpriseSelectionDraft Draft(Component page) => (EnterpriseSelectionDraft)Read(page, "Draft");
        private static Component Overlay(Component board) => (Component)Read(board, "departmentOverlay");
        private static RawImage Artwork(Component overlay) => (RawImage)Read(overlay, "artwork");
        private static GameObject OverlayButton(Component overlay) => ButtonObject(overlay, "previewButton");
        private static RectTransform Original(Component board) => ((RawImage)Read(board, "boardImage")).rectTransform;
        private static Component Board(Component page, string id) => (Component)((IDictionary)Read(page, "items"))[id];
        private static Vector2 Point(RectTransform rect, float x, float y)
        {
            var bounds = ScreenRect(rect);
            return new Vector2(Mathf.Lerp(bounds.xMin, bounds.xMax, x), Mathf.Lerp(bounds.yMin, bounds.yMax, y));
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            // 即使首个断言失败，也保留实际窗口与射线遮挡的终态画面再退出。
            var remaining = Active("EnterpriseSelectionDialogView").FirstOrDefault();
            if (f != null && remaining != null) yield return f.Capture("验收终态_包含失败现场", remaining);
            foreach (var page in ownedPages)
                if (page != null) { Call(page, "ClearPresentation"); Object.Destroy(page.gameObject); }
            ownedPages.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator SampleScene_PublicGameBoxPilesAndEnterpriseOverlaysUseActualInputs()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            var registry = (EffectRegistry)Read(((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>()
                .OfType<AnswerInteractionCommandHandler>().Single(), "registry");
            int player = (int)Read(f.Gameplay, "localPlayerId");
            var query = new EnterpriseStateQuery(registry.EnterpriseDefinitions);
            var publicEnterprises = query.GetEnterpriseItemIds(f.State).ToArray();
            string rhine = f.State.ItemZones.Items.Single(i => publicEnterprises.Contains(i.ItemId) && i.DefinitionId == "enterprise.rhine_lab").ItemId;
            foreach (var mode in new[] { EnterpriseSelectionMode.Company, EnterpriseSelectionMode.Effect })
            {
                var args = ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(1),
                    "player", NormalizedValue.CreateString(player.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    "enterpriseItemIds", ItemTransferEffectExecutor.Strings(mode == EnterpriseSelectionMode.Company ? publicEnterprises : new[] { rhine }));
                args.Properties.Add(new NormalizedValueEntry { Name = mode == EnterpriseSelectionMode.Company ? "levels" : "unlockedByPlayer",
                    Value = mode == EnterpriseSelectionMode.Company ? NormalizedValue.CreateInteger(1) :
                        NormalizedValue.CreateString(player.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
                args.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                string root = RunSmallUiPreparation(registry, EffectSpec.Create(mode == EnterpriseSelectionMode.Company ?
                    LuaDomainEffectTypeIds.UpgradeEnterprise : LuaDomainEffectTypeIds.ActivateEnterpriseSpecial, args, player));
                yield return Wait(() => f.RequestPage() != null, "正式企业选择请求没有显示实际页面");
                var page = f.RequestPage(); var projection = Draft(page).Projection;
                Assert.That(projection.Mode, Is.EqualTo(mode));
                var target = projection.Targets.First(t => t.BoardId == rhine && t.Available);
                var overlay = Overlay(Board(page, rhine));
                Assert.That(overlay.gameObject.activeInHierarchy && Artwork(overlay).texture != null, Is.True);
                Assert.That(Artwork(overlay).raycastTarget, Is.False, "选择模式科室图不得截获企业／特效输入");
                Assert.That(((Button)Read(overlay, "previewButton")).enabled, Is.False);
                var button = Target(page, target.Id);
                yield return ScrollSmallTargetIntoView(page, button);
                Vector2 point = SmallOverlayInputPoint(page, button, overlay, mode == EnterpriseSelectionMode.Company);
                yield return f.Capture("小修_" + mode + "正式莱茵科室合成", page);
                yield return f.Click(button, "小修_选择原" + mode + "候选", point);
                Assert.That(Draft(page).SelectedIds, Is.EqualTo(new[] { target.Id }));
                Assert.That(target.Id, Is.Not.EqualTo(projection.Boards.Single(b => b.Id == rhine).ActiveDepartment.ItemId));
                if (mode == EnterpriseSelectionMode.Company)
                {
                    yield return f.Click(Target(page, target.Id), "小修_查看企业和科室合成详情", point, PointerEventData.InputButton.Right);
                    Assert.That(((GameObject)Read(page, "detailPage")).activeInHierarchy, Is.True);
                    var enlarged = (Component)Read(page, "enlargedDepartmentOverlay");
                    Assert.That(enlarged.gameObject.activeInHierarchy, Is.True);
                    Assert.That(Artwork(enlarged).texture, Is.SameAs(Artwork(overlay).texture));
                    yield return f.Capture("小修_企业详情保留科室合成", page);
                    yield return f.Click((GameObject)Read(page, "detailsReturnSurface"), "小修_单击详情返回原企业选择");
                    Assert.That(((GameObject)Read(page, "detailPage")).activeInHierarchy, Is.False);
                    Assert.That(Draft(page).SelectedIds, Is.EqualTo(new[] { target.Id }));
                }
                var request = f.Request;
                using (var submits = new RuleSubmitCounter())
                {
                    yield return f.Click(ButtonObject(page, "confirm"), "小修_确认原" + mode + "目标一次");
                    Assert.That(submits.Calls, Is.EqualTo(1));
                }
                f.Record("小修叠层原目标提交", new { mode = mode.ToString(), target.Id, target.CompanyId, target.EffectId,
                    requestId = request.GetStableInteractionId(), root,
                    note = "正式领域根受控触发真实请求；选择和确认走实际 EventSystem，不声称办事处付费入口" });
                yield return DrainSmallUiResponses();
                Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            }

            // 简单位移、字体和文案只留当前实际画面，不在这里锁死可编辑样式。
            yield return f.Capture("小修_实际HUD数字与四企业标志", (Component)f.Hud);
            var modules = Read(f.Hud, "MainModules");
            f.Record("小修HUD当前数字字体", new { discard = ((Text)Read(modules, "DiscardCountText")).font.name,
                cooperation = ((Component)Expansion).GetComponentsInChildren<Text>(true)
                    .Where(t => int.TryParse(t.text, out _)).Select(t => new { t.name, t.text, font = t.font?.name }).ToArray() });
            string handZone = ItemTransferEffectExecutor.FindZone(f.State, "hand", player).ZoneId;
            string discard = ItemTransferEffectExecutor.FindZone(f.State, "discard", player).ZoneId;
            string retired = ItemTransferEffectExecutor.FindZone(f.State, "retired").ZoneId;
            var character = f.State.ItemZones.Items.First(i => i.ZoneId == handZone &&
                Call(Read(f.Registry, "CardVisualCatalog"), "GetCharacterFront", i.DefinitionId) != null);
            string characterId = character.ItemId;
            RunSmallUiPreparation(registry, ItemTransferEffectExecutor.MoveSpec(player, new[] { characterId }, handZone, discard, "unordered", 1, "core.character_card_transfer"));
            yield return f.Click(ButtonObject(modules, "discardPreviewButton"), "小修_实际非空弃牌列表");
            var discardShell = (Component)Read(Read(Read(f.Hud, "CharacterHandPanel"), "discardPage"), "view");
            Assert.That((bool)Read(discardShell, "IsCardPicker"), Is.True);
            yield return f.Capture("小修_弃牌标题与下移滚动条", discardShell);
            yield return CloseSmallListBackground(discardShell);
            RunSmallUiPreparation(registry, ItemTransferEffectExecutor.MoveSpec(player, new[] { characterId }, discard, retired, "unordered", 1, "core.character_card_transfer"));

            if (!GameStateViewProjector.ProjectForPlayer(f.State, player).GameBox.Cards.Any(c => c.Kind == GameBoxCardKind.Event))
            {
                var location = StaticMapDefinitions.Resolve(f.State.MapId).Locations.First(l =>
                    !f.State.Map.ResourceTokens.Any(t => t.LocationId == l.LocationId));
                string eventRoot = RunSmallUiPreparation(registry, EventCardEffectSpecFactory.Resolve(player, location.EventColor, location.LocationId));
                yield return DrainSmallUiResponses();
                Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == eventRoot).Status, Is.EqualTo(EffectNodeStatus.Completed));
            }
            PutExistingPublicEnterpriseInBox(publicEnterprises.First(id => id != rhine), player, retired);
            yield return Settle();
            var information = ((Component)f.Hud).GetComponent(T("GameplayInformationPages"));
            Assert.That(information, Is.Not.Null);
            yield return f.Click(ButtonObject(information, "GameBoxButton"), "小修_实际游戏盒入口");
            var box = (Component)Read(information, "GameBoxPage");
            var visible = GameStateViewProjector.ProjectForPlayer(f.State, player);
            CheckSmallBoxVisibility(player, visible.GameBox.Cards.ToArray());
            Assert.That(visible.GameBox.Cards.Select(c => c.Kind).Distinct(), Is.EquivalentTo(new[] {
                GameBoxCardKind.Character, GameBoxCardKind.Event, GameBoxCardKind.EnterpriseCooperation }));
            yield return f.Capture("小修_公开与本人私密合并游戏盒三类牌堆", box);
            object browser = null;
            foreach (var kind in new[] { GameBoxCardKind.Character, GameBoxCardKind.Event, GameBoxCardKind.EnterpriseCooperation })
            {
                using (var submits = new RuleSubmitCounter())
                {
                    var cards = ((IEnumerable)Call(box, "Cards", kind)).Cast<GameBoxCardState>().ToArray();
                    var expected = GameStateViewProjector.ProjectForPlayer(f.State, player).GameBox.Cards.Where(c => c.Kind == kind).ToArray();
                    Assert.That(JsonConvert.SerializeObject(cards), Is.EqualTo(JsonConvert.SerializeObject(expected)));
                    Assert.That(cards, Is.Not.Empty);
                    var images = ((IEnumerable)Call(box, "Images", kind)).Cast<Sprite>().ToArray();
                    Assert.That(images, Has.Length.EqualTo(cards.Length)); Assert.That(images.All(s => s != null), Is.True);
                    var pile = Read(box, kind == GameBoxCardKind.Character ? "CharacterPile" : kind == GameBoxCardKind.Event ? "EventPile" : "EnterprisePile");
                    Assert.That(Read(pile, "pile"), Is.Not.Null, "三类均需使用实际 CardPileView");
                    yield return f.Click(ButtonObject(pile, "InspectButton"), "小修_浏览游戏盒" + kind);
                    browser = Read(information, "browser");
                    var shell = (Component)Read(browser, "view");
                    Assert.That((bool)Read(shell, "IsCardPicker"), Is.True);
                    var cardButtons = shell.GetComponentsInChildren(T("CardPickerCardButton"), false).Cast<Button>().ToArray();
                    Assert.That(cardButtons.Length, Is.EqualTo(cards.Length));
                    if (kind != GameBoxCardKind.Character)
                        Assert.That(cardButtons.All(b => !b.interactable), Is.True, "事件与企业只读浏览不得变成牌选择");
                    foreach (var card in cards)
                    {
                        var listed = HotseatUiInputDriver.Marker("selection.card." + card.InstanceId);
                        Assert.That(listed.transform.IsChildOf(shell.transform), Is.True);
                        if (kind == GameBoxCardKind.Character)
                        {
                            var front = (Sprite)Call(Read(f.Registry, "CardVisualCatalog"), "GetCharacterFront", card.CardId);
                            Assert.That(images[Array.IndexOf(cards, card)], Is.SameAs(front), "本人私密与公开角色必须同样显示真实正面");
                            var cardView = listed.GetComponentInParent(T("FacilityEffectCardView"));
                            var selection = (Image)Read(cardView, "SelectionImage");
                            Assert.That(selection == null || !selection.enabled, Is.True, "角色预览入口不得出现选择状态");
                            var artwork = (RawImage)Read(cardView, "CardImage");
                            Assert.That(artwork.texture, Is.SameAs(front.texture));
                        }
                    }
                    yield return f.Capture("小修_游戏盒" + kind + "真实实例列表", shell);
                    if (kind == GameBoxCardKind.Character)
                    {
                        // 复用真实开局私密基本牌；本次公开回盒角色在切观察者后从同一列表查看。
                        var privateCard = cards.First(c => (int)Read(c, "PrivateForPlayerId") == player);
                        yield return InspectSmallBoxCharacter(shell, privateCard, true);
                        yield return CloseSmallListBackground(shell);
                        Assert.That(submits.Calls, Is.Zero, "角色双面浏览不得提交规则命令");
                        continue;
                    }
                    var cardButton = HotseatUiInputDriver.Marker("selection.card." + cards[0].InstanceId);
                    float scroll = (float)Read(shell, "ScrollPosition");
                    yield return f.Click(cardButton, "小修_查看游戏盒" + kind + "真实卡面",
                        ScreenRect((RectTransform)cardButton.transform).center, PointerEventData.InputButton.Right);
                    var viewer = Active("CardViewer").Single();
                    Assert.That(Read(viewer, "Mode").ToString(), Is.EqualTo("Inspect"));
                    Assert.That(Read(viewer, "DisplayedSprite"), Is.SameAs(images[0]));
                    Assert.That(((RawImage)Read(viewer, "GameBoxBackImage")).gameObject.activeInHierarchy, Is.False,
                        "事件与企业的原Inspect不得显示游戏盒角色背面");
                    f.Record("游戏盒实际实例与详情", new { kind = kind.ToString(), cards[0].InstanceId, cards[0].CardId,
                        cards[0].FaceUp, sprite = images[0].name, listScroll = scroll });
                    yield return f.Click(viewer.gameObject, "小修_返回游戏盒原列表", ScreenRect((RectTransform)Read(viewer, "Window")).center);
                    Assert.That(shell.gameObject.activeInHierarchy, Is.True);
                    Assert.That((float)Read(shell, "ScrollPosition"), Is.EqualTo(scroll).Within(.001f));
                    yield return CloseSmallListBackground(shell);
                    Assert.That(submits.Calls, Is.Zero, "游戏盒浏览不得提交规则命令");
                }
            }
            // 仅推进一次实际玩家窗口；主要行动收敛后打开详情，再核对正式热座切换。
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId)));
            var deploy = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.DeployInfluence,
                PlayerId = player, TargetId = influence.GetLegalPlacementSlotIds(f.State, player).First() };
            var deployed = f.Session.Submit(deploy); Assert.That(deployed.Succeeded, Is.True, deployed.Validation?.Reason);
            Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
            yield return DrainSmallUiResponses();
            // 三类列表均已返回同一游戏盒；直接沿现有可见页面继续，不点击被其遮挡的 HUD 入口。
            Assert.That(box.gameObject.activeInHierarchy, Is.True, "主要行动前置收敛后应仍使用已打开的游戏盒");
            yield return f.Click(ButtonObject(Read(box, "CharacterPile"), "InspectButton"), "小修_切观察者前浏览角色");
            browser = Read(information, "browser");
            var oldPrivateCard = ((IEnumerable)Call(box, "Cards", GameBoxCardKind.Character)).Cast<GameBoxCardState>()
                .First(c => (int)Read(c, "PrivateForPlayerId") == player);
            yield return InspectSmallBoxCharacter((Component)Read(browser, "view"), oldPrivateCard, false);
            var previousViewer = Active("CardViewer").Single();
            yield return f.Capture("小修_切观察者前本人角色双面详情", previousViewer);
            var end = f.Session.Submit(new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.EndAction, PlayerId = player });
            Assert.That(end.Succeeded, Is.True, end.Validation?.Reason);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Wait(() => (int)Read(f.Gameplay, "localPlayerId") != player, "正式窗口结束后没有切换热座观察者");
            yield return Settle();
            Assert.That(Active("CardViewer"), Is.Empty, "切观察者后仍显示旧游戏盒详情");
            Assert.That((bool)Read(browser, "IsVisible"), Is.False);
            Assert.That(Read(previousViewer, "DisplayedSprite"), Is.Null);
            Assert.That(((RawImage)Read(previousViewer, "GameBoxBackImage")).texture, Is.Null, "旧私密角色背面必须清空");
            yield return f.Click(ButtonObject(information, "GameBoxButton"), "小修_切观察者后重开游戏盒");
            int next = (int)Read(f.Gameplay, "localPlayerId");
            var all = new[] { GameBoxCardKind.Character, GameBoxCardKind.Event, GameBoxCardKind.EnterpriseCooperation }
                .SelectMany(kind => ((IEnumerable)Call(box, "Cards", kind)).Cast<GameBoxCardState>()).ToArray();
            Assert.That(JsonConvert.SerializeObject(all), Is.EqualTo(JsonConvert.SerializeObject(GameStateViewProjector.ProjectForPlayer(f.State, next).GameBox.Cards)));
            CheckSmallBoxVisibility(next, all);
            Assert.That(all.Any(c => c.InstanceId == oldPrivateCard.InstanceId), Is.False, "切观察者后不得保留上一玩家私密实例");
            Assert.That(all.Any(c => c.InstanceId == characterId), Is.True, "上一玩家公开回盒的角色仍须对新观察者可见");
            yield return f.Capture("小修_正式切观察者后的游戏盒", box);
            yield return f.Click(ButtonObject(Read(box, "CharacterPile"), "InspectButton"), "小修_新观察者查看合并角色列表");
            var nextShell = (Component)Read(Read(information, "browser"), "view");
            var nextCards = all.Where(c => c.Kind == GameBoxCardKind.Character).ToArray();
            Assert.That(nextShell.GetComponentsInChildren(T("CardPickerCardButton"), false).Length, Is.EqualTo(nextCards.Length));
            foreach (var card in nextCards)
                Assert.That(HotseatUiInputDriver.Marker("selection.card." + card.InstanceId).transform.IsChildOf(nextShell.transform), Is.True);
            yield return f.Capture("小修_新观察者仅公开与本人私密角色列表", nextShell);
            using (var submits = new RuleSubmitCounter())
            {
                yield return InspectSmallBoxCharacter(nextShell, nextCards.Single(c => c.InstanceId == characterId), true);
                Assert.That(submits.Calls, Is.Zero, "查看他人公开回盒角色双面不得提交规则命令");
            }
            yield return CloseSmallListBackground(nextShell);
            f.Record("游戏盒窄验边界", new { player, next, characterId,
                note = "三类真实实例实际浏览；公开与本人私密合并，角色实际单击双面并返回原列表；企业使用已审公开位置夹具，角色正式移牌、事件正式公开；不代表企业自然回盒或完整对局验证" });
        }

        private void CheckSmallBoxVisibility(int player, GameBoxCardState[] cards)
        {
            var types = ItemTypeRegistry.CreateBuiltIn();
            bool Supported(ItemInstanceState item) => types.IsAssignableTo(item.TypeId, "core.character_card") ||
                types.IsAssignableTo(item.TypeId, "core.event_card") ||
                item.TypeId == "core.board_card" && f.State.ContentDefinitions.EnterpriseDefinitionIds.Contains(item.DefinitionId);
            var retired = ItemTransferEffectExecutor.FindZone(f.State, "retired").ZoneId;
            var secret = ItemTransferEffectExecutor.FindZone(f.State, "secret_retired").ZoneId;
            var publicItems = f.State.ItemZones.Items.Where(i => i.ZoneId == retired && Supported(i)).ToArray();
            var ownPrivate = f.State.ItemZones.Items.Where(i => i.ZoneId == secret && Supported(i) &&
                i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId == player).ToArray();
            var otherPrivate = f.State.ItemZones.Items.Where(i => i.ZoneId == secret && Supported(i) &&
                i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId != player).ToArray();
            Assert.That(publicItems, Is.Not.Empty);
            Assert.That(ownPrivate, Is.Not.Empty, "复用正式开局裁掉的本人基本角色，不补造私密牌");
            Assert.That(otherPrivate, Is.Not.Empty);
            Assert.That(cards.Select(c => c.InstanceId), Is.EquivalentTo(publicItems.Concat(ownPrivate).Select(i => i.ItemId)),
                "游戏盒必须恰为所有公开回盒与本人私密的并集");
            Assert.That(cards.Any(c => otherPrivate.Any(i => i.ItemId == c.InstanceId)), Is.False);
            foreach (var item in publicItems.Concat(ownPrivate))
            {
                var card = cards.Single(c => c.InstanceId == item.ItemId);
                bool isPrivate = item.ZoneId == secret;
                Assert.That(card.CardId, Is.EqualTo(item.DefinitionId));
                Assert.That(card.FaceUp, Is.EqualTo(!isPrivate));
                Assert.That((int)Read(card, "PrivateForPlayerId"), Is.EqualTo(isPrivate ? player : -1));
                if (card.Kind != GameBoxCardKind.Character) continue;
                if (types.IsAssignableTo(item.TypeId, "core.basic_character_card"))
                {
                    var originalOwner = f.State.Players.Single(p => p.PlayerId == item.OwnerSubject.PlayerId);
                    Assert.That(card.BackColor, Is.EqualTo(originalOwner.Color));
                    Assert.That(card.BackVisualKey, Is.EqualTo("character_back_" + ((int)originalOwner.Color).ToString()));
                }
                else if (types.IsAssignableTo(item.TypeId, "core.operator_card"))
                    Assert.That(card.BackVisualKey, Is.EqualTo("operator_back"));
            }
            f.Record("游戏盒公开与本人私密集合", new { player,
                publicIds = publicItems.Select(i => i.ItemId).ToArray(), ownPrivateIds = ownPrivate.Select(i => i.ItemId).ToArray(),
                excludedOtherPrivateCount = otherPrivate.Length, displayedIds = cards.Select(c => c.InstanceId).ToArray() });
        }

        private IEnumerator InspectSmallBoxCharacter(Component shell, GameBoxCardState card, bool returnToList)
        {
            var target = HotseatUiInputDriver.Marker("selection.card." + card.InstanceId);
            var scroll = (ScrollRect)Read(shell, "OptionScroll");
            for (int step = 0; step < 48 && !HotseatUiInputDriver.TryPoint(target, out _, out _); step++)
            {
                var center = ScreenRect(scroll.viewport).center; var hit = Hit(center);
                var handler = ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject);
                Assert.That(handler, Is.SameAs(scroll.gameObject));
                var rect = ScreenRect((RectTransform)target.transform);
                ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current) { position = center,
                    scrollDelta = new Vector2(rect.center.x > center.x ? -1 : 1, 0), pointerCurrentRaycast = hit }, ExecuteEvents.scrollHandler);
                yield return null; yield return null;
            }
            Assert.That(HotseatUiInputDriver.TryPoint(target, out var point, out _), Is.True);
            float position = (float)Read(shell, "ScrollPosition");
            var ray = Hit(point); var actual = ExecuteEvents.GetEventHandler<IPointerClickHandler>(ray.gameObject);
            Assert.That(actual, Is.SameAs(ExecuteEvents.GetEventHandler<IPointerClickHandler>(target)));
            var data = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left,
                position = point, pressPosition = point, clickCount = 1, eligibleForClick = true,
                pointerCurrentRaycast = ray, pointerPressRaycast = ray };
            ExecuteEvents.ExecuteHierarchy(ray.gameObject, data, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(actual, data, ExecuteEvents.pointerDownHandler); yield return null;
            Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(point).gameObject), Is.SameAs(actual));
            ExecuteEvents.Execute(actual, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(actual, data, ExecuteEvents.pointerClickHandler);
            Assert.That(Active("CardViewer").Length, Is.EqualTo(1), "一次左键释放必须立即打开，不等待选牌或第二击");
            var viewer = Active("CardViewer").Single();
            Assert.That(Read(viewer, "Mode").ToString(), Is.EqualTo("GameBoxCharacterInspect"));
            Assert.That(Read(viewer, "CardId"), Is.EqualTo(card.InstanceId));
            var front = (Sprite)Call(Read(f.Registry, "CardVisualCatalog"), "GetCharacterFront", card.CardId);
            var back = (Texture2D)T("ExternalContentRuntime").GetMethod("GetSharedArtwork").Invoke(null, new object[] { card.BackVisualKey });
            Assert.That(front, Is.Not.Null); Assert.That(back, Is.Not.Null);
            Assert.That(Read(viewer, "DisplayedSprite"), Is.SameAs(front));
            Assert.That(((RawImage)Read(viewer, "CardImage")).texture, Is.SameAs(front.texture));
            var backImage = (RawImage)Read(viewer, "GameBoxBackImage");
            Assert.That(backImage.gameObject.activeInHierarchy, Is.True);
            Assert.That(backImage.texture, Is.SameAs(back));
            Assert.That(((Button)Read(viewer, "PlotButton")).interactable || ((Button)Read(viewer, "StrategyButton")).interactable, Is.False);
            f.Record("游戏盒角色单击双面", new { card.InstanceId, card.CardId, card.FaceUp, card.BackVisualKey,
                privateForPlayerId = (int)Read(card, "PrivateForPlayerId"), front = front.name, back = back.name,
                listScroll = position, hit = HotseatUiInputDriver.PathOf(ray.gameObject), handler = HotseatUiInputDriver.PathOf(actual) });
            yield return f.Capture("小修_游戏盒角色单击双面_" + card.CardId, viewer);
            if (!returnToList) yield break;
            yield return new WaitForSecondsRealtime((float)Read(viewer, "gameBoxOpeningClickGuardSeconds") + .05f);
            yield return f.Click(viewer.gameObject, "小修_双面详情单击返回原列表", ScreenRect((RectTransform)Read(viewer, "Window")).center);
            Assert.That(Active("CardViewer"), Is.Empty);
            Assert.That(shell.gameObject.activeInHierarchy, Is.True);
            Assert.That((float)Read(shell, "ScrollPosition"), Is.EqualTo(position).Within(.001f));
        }

        private string RunSmallUiPreparation(EffectRegistry registry, EffectSpec spec)
        {
            Assert.That(f.Request, Is.Null, "受控前置不得跨过未回答请求");
            var engine = new EffectTreeExecutor(f.State, registry); string root = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            var node = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root);
            Assert.That(node.Status, Is.Not.EqualTo(EffectNodeStatus.Failed), node.FailureReason);
            f.Record("小修真实领域受控前置", new { root, node.EffectTypeId, node.NormalizedArguments, node.PositionChanges });
            Call(f.Gameplay, "SynchronizeInteractionFromState"); return root;
        }

        private IEnumerator DrainSmallUiResponses()
        {
            for (int step = 0; step < 20 && f.Request != null; step++)
            {
                var request = f.Request;
                IEnumerable<string> choices = request.CandidateIds;
                if (request.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId)
                {
                    var owner = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
                    var card = EventCardDatabase.Get(f.State, ItemTransferEffectExecutor.Text(owner.NormalizedResult, "cardId"));
                    choices = choices.Where(id => EventCardOptionIdFactory.TryRead(id, card.CardId, card.ChoiceDescriptions.Count, out int index) &&
                        card.ChoicePendingEffects[index].All(effect => effect.Kind != EventEffectKind.PlaceInfluence));
                }
                var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.AnswerInteraction,
                    PlayerId = request.AnsweringPlayerId };
                command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
                command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.OptionIds.AddRange(choices.Take(Math.Max(1, request.MinSelections)));
                Assert.That(command.OptionIds, Is.Not.Empty, "仅选实际合法候选；不补造事件／科室答案");
                var result = f.Session.Submit(command); Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
                f.Record("小修非待验响应正式辅助提交", new { request.InteractionTypeId, request.PromptKey, command.OptionIds });
                Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
            }
            Assert.That(f.Request, Is.Null);
            Assert.That(f.State.EffectRuntime.Status, Is.Not.EqualTo(EffectRuntimeStatus.PausedFault));
        }

        private void PutExistingPublicEnterpriseInBox(string id, int player, string retired)
        {
            Assert.That(f.Request, Is.Null);
            var visible = GameStateViewProjector.ProjectForPlayer(f.State, player);
            var item = visible.ItemZones.Items.Single(i => i.ItemId == id);
            Assert.That(f.State.ContentDefinitions.EnterpriseDefinitionIds, Does.Contain(item.DefinitionId));
            Assert.That(f.State.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId).TemplateId, Is.EqualTo("core.zone.enterprise_display"));
            var preview = GameStateCloneService.DeepClone(f.State);
            var existing = preview.ItemZones.Items.Single(i => i.ItemId == id);
            // 企业没有通用转移许可；已审夹具只移动原公开实例，并保留其身份及附属区域。
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Invoke(null, new object[] { preview, existing, retired });
            Assert.That(new ItemZoneQuery().TryValidate(preview, out string reason), Is.True, reason);
            Assert.That(existing.DefinitionId, Is.EqualTo(item.DefinitionId));
            GameStateCloneService.CopyTo(f.State, preview);
            Assert.That(GameStateViewProjector.ProjectForPlayer(f.State, player).GameBox.Cards.Any(c =>
                c.Kind == GameBoxCardKind.EnterpriseCooperation && c.InstanceId == id && c.CardId == item.DefinitionId && c.FaceUp), Is.True);
            f.Record("企业原公开实例位置夹具", new { id, item.DefinitionId, source = item.ZoneId, destination = retired,
                note = "经区域完整性校验后使用正式 ProjectForPlayer；不是自然回盒，不创建实例、不修改 GameBox DTO、不读取隐藏 reserve 身份" });
            Call(f.Gameplay, "SynchronizeInteractionFromState");
        }

        private IEnumerator ScrollSmallTargetIntoView(Component page, GameObject target)
        {
            var scroll = (ScrollRect)Read(page, "candidateScroll");
            for (int step = 0; step < 48 && !HotseatUiInputDriver.TryPoint(target, out _, out _); step++)
            {
                var center = ScreenRect(scroll.viewport).center; var hit = Hit(center);
                var handler = ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject);
                Assert.That(handler, Is.SameAs(scroll.gameObject));
                var targetRect = ScreenRect((RectTransform)target.transform);
                ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current) { position = center,
                    scrollDelta = new Vector2(targetRect.center.x > center.x ? -1 : 1, 0), pointerCurrentRaycast = hit }, ExecuteEvents.scrollHandler);
                yield return null; yield return null;
            }
            Assert.That(HotseatUiInputDriver.TryPoint(target, out _, out _), Is.True);
        }

        private Vector2 SmallOverlayInputPoint(Component page, GameObject target, Component overlay, bool requireOverlap)
        {
            var targetRect = ScreenRect((RectTransform)target.transform);
            var artRect = ScreenRect(Artwork(overlay).rectTransform);
            var viewport = ScreenRect(((ScrollRect)Read(page, "candidateScroll")).viewport);
            var intersection = Rect.MinMaxRect(Mathf.Max(targetRect.xMin, artRect.xMin, viewport.xMin),
                Mathf.Max(targetRect.yMin, artRect.yMin, viewport.yMin), Mathf.Min(targetRect.xMax, artRect.xMax, viewport.xMax),
                Mathf.Min(targetRect.yMax, artRect.yMax, viewport.yMax));
            if (intersection.width > 0 && intersection.height > 0)
                foreach (float x in new[] { .5f, .2f, .8f }) foreach (float y in new[] { .5f, .2f, .8f })
                {
                    var point = new Vector2(Mathf.Lerp(intersection.xMin, intersection.xMax, x), Mathf.Lerp(intersection.yMin, intersection.yMax, y));
                    if (ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(point).gameObject) == ExecuteEvents.GetEventHandler<IPointerClickHandler>(target)) return point;
                }
            Assert.That(requireOverlap, Is.False, "Company 模式须实际从科室覆盖处命中原企业目标");
            Assert.That(HotseatUiInputDriver.TryPoint(target, out var fallback, out _), Is.True);
            f.Record("特效与科室覆盖范围", new {
                targetRect = new { targetRect.x, targetRect.y, targetRect.width, targetRect.height },
                artRect = new { artRect.x, artRect.y, artRect.width, artRect.height },
                note = "本次特效目标与科室图无可命中重叠，实际点击原特效目标，并检查科室图无射线与预览处理器" });
            return fallback;
        }

        private IEnumerator CloseSmallListBackground(Component shell)
        {
            var overlay = (Image)Read(shell, "OverlayImage");
            var panel = ScreenRect((RectTransform)Read(shell, "Panel")); var area = ScreenRect(overlay.rectTransform);
            var points = new[] { new Vector2(area.xMin + 2, area.center.y), new Vector2(area.xMax - 2, area.center.y),
                new Vector2(area.center.x, area.yMin + 2), new Vector2(area.center.x, area.yMax - 2) };
            var point = points.FirstOrDefault(p => !panel.Contains(p) && ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(p).gameObject) ==
                ExecuteEvents.GetEventHandler<IPointerClickHandler>(overlay.gameObject));
            Assert.That(point, Is.Not.EqualTo(Vector2.zero), "原列表背景关闭区域不可达");
            yield return f.Click(overlay.gameObject, "小修_实际背景返回", point);
            Assert.That(shell.gameObject.activeInHierarchy, Is.False);
        }

        [UnityTest]
        public IEnumerator RhodesFirstSpecialUsesFormalEnterpriseUpgradePageAndSingleArrival()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle(), preferredEnterpriseDefinition: "enterprise.rhodes_island");
            var answer = ((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>().OfType<AnswerInteractionCommandHandler>().Single();
            var registry = (EffectRegistry)Read(answer, "registry");
            var query = new EnterpriseStateQuery(registry.EnterpriseDefinitions);
            var publicIds = query.GetEnterpriseItemIds(f.State).ToArray();
            var rhodes = f.State.ItemZones.Items.Single(item => publicIds.Contains(item.ItemId) && item.DefinitionId == "enterprise.rhodes_island");
            var ability = registry.EnterpriseDefinitions.FindEnterprise(rhodes.DefinitionId).Levels.Single(level => level.Level == 1 && level.Kind == "special").AbilityId;
            int player = f.State.CurrentPlayerId;
            int unlockedBy = f.State.Players.First(p => query.GetLevel(f.State, p.PlayerId, rhodes.ItemId) >= 1).PlayerId;
            var oldLevels = publicIds.ToDictionary(id => id, id => query.GetLevel(f.State, player, id));
            var beforeNodes = new HashSet<string>(f.State.EffectRuntime.EffectNodes.Select(node => node.EffectId));
            // 受控触发真实已解锁能力；不伪造选择请求、不替换 Lua、不改等级/资源。
            // UI 验收从能力触发后的正式企业页开始，不冒充支付/建造办事处的全链入口。
            var engine = new EffectTreeExecutor(f.State, registry);
            var rootId = engine.CreateRoot(EnterpriseSpecialSelectionExecutor.ActivateSpec(player, unlockedBy, rhodes.ItemId, ability));
            var run = engine.RunUntilQuiescent();
            f.Record("罗德岛真实能力受控触发", new { rootId, rhodes.ItemId, ability, player, unlockedBy,
                scope = "真实新局正式注册能力→真实选择页面；能力根由夹具提交，非办事处建造或付费入口", run.Faulted });
            Assert.That(run.Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Wait(() => f.Request?.InteractionTypeId == "enterprise.upgrade" && f.RequestPage() != null,
                "罗德岛一级真实 Lua 必须交给正式 enterprise.upgrade 企业页，不能退回普通 Choice");
            var request = f.Request;
            var page = f.RequestPage();
            var projection = Draft(page).Projection;
            Assert.That(request.CandidateIds, Is.EquivalentTo(publicIds.Where(id => id != rhodes.ItemId)));
            Assert.That(projection.Mode, Is.EqualTo(EnterpriseSelectionMode.Company));
            Assert.That(projection.Context, Is.Null.Or.Empty, "升级不能沿用开局企业上下文");
            Assert.That(projection.Targets.Select(target => target.Id), Is.EquivalentTo(request.CandidateIds));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.InteractionTypeId == "lua.choice"), Is.False);
            var chosen = request.CandidateIds.OrderByDescending(id => oldLevels[id]).First(id => oldLevels[id] < 5);
            f.Record("罗德岛正式升级页候选", new { requestId = request.GetStableInteractionId(), request.InteractionTypeId,
                request.CandidateIds, rhodes.ItemId, ability, chosen, oldLevels });
            yield return f.Capture("罗德岛一级正式企业升级页", page);
            yield return f.Click(Target(page, chosen), "罗德岛一级_选择真实企业板");
            var confirm = (Button)Read(page, "confirm");
            using (var submits = new RuleSubmitCounter())
            {
                yield return f.Click(confirm.gameObject, "罗德岛一级_确认企业升级");
                Assert.That(submits.Calls, Is.EqualTo(1));
            }
            yield return Wait(() => f.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId).Status == EffectNodeStatus.Completed,
                "真实升级及其到达效果没有完成；保留请求和 Effect 现场，不跳过后续奖励");
            Assert.That(query.GetLevel(f.State, player, chosen), Is.EqualTo(oldLevels[chosen] + 1));
            foreach (var id in publicIds.Where(id => id != chosen)) Assert.That(query.GetLevel(f.State, player, id), Is.EqualTo(oldLevels[id]));
            var nodes = f.State.EffectRuntime.EffectNodes.Where(node => !beforeNodes.Contains(node.EffectId)).ToArray();
            var upgrade = nodes.Single(node => node.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise);
            Assert.That(upgrade.EnterpriseArrivalReceipts.Count, Is.EqualTo(1), "同一升级只产生一次真实到达");
            var snapshot = JsonConvert.SerializeObject(f.State);
            using (var submits = new RuleSubmitCounter())
            {
                confirm.onClick.Invoke(); // 明确是过期回调补证，不是假称隐藏按钮的真实点击。
                yield return Settle();
                Assert.That(submits.Calls, Is.Zero);
            }
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(snapshot), "过期回调不得再次升级或领取到达奖励");
            f.Record("罗德岛升级完成与防重复", new { rootId, chosen, before = oldLevels[chosen], after = query.GetLevel(f.State, player, chosen),
                upgrade.EnterpriseArrivalReceipts, completedNodes = nodes.Select(node => new { node.EffectId, node.EffectTypeId, node.FlowStage, status = node.Status.ToString() }).ToArray() });
        }

        [UnityTest]
        public IEnumerator FormalEnterpriseEventArchivesRestoreVisiblePageAndAnswerExactlyOnce()
        {
            foreach (var definitionId in new[] { "enterprise.event.collection_platform", "enterprise.event.cave_ruins" })
            {
                f = new GameplayPageAcceptanceFixture();
                var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("YC_ENTERPRISE_EVENT_ARCHIVES") ??
                    "Logs/EnterpriseEventDiagnostics/20261008_readonly/build");
                var archivePath = Path.Combine(directory, definitionId + ".archive.json");
                var evidencePath = Path.Combine(directory, definitionId + ".evidence.json");
                Assert.That(File.Exists(archivePath) && File.Exists(evidencePath), Is.True, "必须使用已交接的正式企业待答存档");
                var json = File.ReadAllText(archivePath); var archive = JObject.Parse(json);
                var origin = JObject.Parse(File.ReadAllText(evidencePath));
                Assert.That((int)origin["OriginalInstanceCount"], Is.Zero);
                Assert.That((int)origin["EnterpriseRecipeCount"], Is.EqualTo(1));
                Assert.That((string)origin["Node"]["FlowStage"], Is.EqualTo("awaiting_option"));
                var config = archive["Snapshot"]["State"]["RuleConfiguration"].ToObject<GameRuleConfiguration>();
                Assert.That(config.PlayerCount, Is.EqualTo(4));
                Assert.That(config.EnabledExpansionIds, Does.Contain("enterprise"));
                var context = T("GameLaunchContext").GetMethod("Ensure").Invoke(null, null);
                var seats = Enumerable.Range(1, 4).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "存档验收玩家" + id }).ToList();
                Call(context, "Configure", LaunchMode.Local, 1, "", seats, config);
                yield return UnityEngine.SceneManagement.SceneManager.LoadSceneAsync("SampleScene");
                yield return Wait(() => Active("MobileCityInteractionController").Length == 1, "存档实际场景未就绪");
                f.Gameplay = Active("MobileCityInteractionController").Single();
                f.Session = (GameSession)Read(f.Gameplay, "session");
                var handler = ((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>().OfType<AnswerInteractionCommandHandler>().Single();
                var registry = (EffectRegistry)Read(handler, "registry");
                var recovered = HostRecoveryService.RecoverJson(json, registry);
                f.Record("正式企业事件存档恢复边界", new { archivePath, evidencePath, definitionId,
                    archiveContentHash = (string)archive["Snapshot"]["ContentHash"], registry.ContentPackHash,
                    recovered.Succeeded, recovered.Diagnostic,
                    scope = "正式四人企业awaiting_option存档→实际事件页→正式回答；未重演探索选地点、选路或预留付款" });
                Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
                f.Session.ReplaceState(recovered.State);
                Call(f.Gameplay, "SynchronizeInteractionFromState");
                yield return f.Resolution(Sizes[0]);
                var request = f.Request;
                Assert.That(request, Is.Not.Null);
                Assert.That(request.InteractionTypeId, Is.EqualTo(EventCardEffectExecutor.OptionInteractionTypeId));
                Assert.That(request.GetStableInteractionId(), Is.EqualTo((string)origin["ActualRequest"]["InteractionId"]));
                Assert.That(EventCardDatabase.Get(f.State, definitionId), Is.Not.Null, "本局冻结目录必须包含替换事件");
                var router = Read(f.Gameplay, "interactionRequestRouter");
                var renderer = Read(router, "activeRenderer");
                f.Record("恢复后事件UI接管断点", new { request, renderer = renderer?.GetType().FullName,
                    nodes = f.State.EffectRuntime.EffectNodes.Select(n => new { n.EffectId, n.ParentEffectId, n.EffectTypeId, n.FlowStage, status = n.Status.ToString() }).ToArray(),
                    activePages = Active("EventChoiceDialogView").Select(p => HotseatUiInputDriver.PathOf(p.gameObject)).ToArray() });
                // 先验页面出现；不能只看到规则请求便算 UI 通过。
                yield return Wait(() => Active("EventChoiceDialogView").Length == 1, "正式企业替换事件仍未出现实际事件页");
                renderer = Read(router, "activeRenderer");
                Assert.That(renderer?.GetType().Name, Is.EqualTo("EventCardInteractionUiCoordinator"));
                var page = Active("EventChoiceDialogView").Single();
                var optionIndex = definitionId.EndsWith("collection_platform", StringComparison.Ordinal) ? 2 : 0;
                var option = HotseatUiInputDriver.Marker("event.option." + optionIndex);
                Assert.That(option, Is.Not.Null);
                Assert.That(HotseatUiInputDriver.TryPoint(option, out _, out _), Is.True, "事件奖励必须实际可射线命中");
                yield return f.Capture(definitionId + "_正式存档事件页已出现", page);
                var player = f.State.FindPlayer(request.AnsweringPlayerId);
                var before = Clone(player.Resources);
                // 在开放时保留旧回答协议，完成后再次送交 Host；不能用已关闭的请求构造新回答。
                var duplicate = EffectInteractionCommands.Answer(
                    YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId),
                    request.AnsweringPlayerId, new[] { definitionId + ":option:" + optionIndex });
                using (var count = new RuleSubmitCounter())
                {
                    yield return f.Click(option, "正式替换事件_选择实际奖励按钮");
                    Assert.That(count.Calls, Is.EqualTo(1));
                }
                yield return Wait(() => f.Request == null && f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId).Status == EffectNodeStatus.Completed,
                    "事件回答未完成正式请求和奖励");
                var after = f.State.FindPlayer(request.AnsweringPlayerId).Resources;
                Assert.That(after.Originium, Is.EqualTo(before.Originium + (optionIndex == 0 ? 5 : 0)));
                Assert.That(after.Iron, Is.EqualTo(before.Iron + (optionIndex == 2 ? 4 : 0)));
                Assert.That(after.GoldVoucher, Is.EqualTo(before.GoldVoucher + (optionIndex == 2 ? 5 : 0)));
                var resources = JsonConvert.SerializeObject(f.State.Players.Select(p => p.Resources));
                var effects = JsonConvert.SerializeObject(f.State.EffectRuntime.EffectNodes);
                Assert.That(f.Session.Submit(duplicate).Succeeded, Is.False, "旧请求回答必须被正式Host拒绝");
                Assert.That(JsonConvert.SerializeObject(f.State.Players.Select(p => p.Resources)), Is.EqualTo(resources), "旧回答不得重复奖励");
                Assert.That(JsonConvert.SerializeObject(f.State.EffectRuntime.EffectNodes), Is.EqualTo(effects), "旧回答不得再次执行效果或到达收据");
                f.Record("正式企业事件存档UI完成", new { definitionId, optionIndex, before, after,
                    requestId = request.GetStableInteractionId(), scope = "仅目录显示断点与该奖励回答；完整探索整链待第二批" });
                yield return f.Capture(definitionId + "_正式奖励后", f.Gameplay);
            }
        }

        [UnityTest]
        public IEnumerator SampleScene_EnterpriseLayoutAndDepartmentGlyphsMatchActualBounds()
        {
            f = new GameplayPageAcceptanceFixture();
            var effectOnly = Environment.GetEnvironmentVariable("YC_ENTERPRISE_LAYOUT_SCOPE") == "Effect";
            var headerOnly = Environment.GetEnvironmentVariable("YC_ENTERPRISE_LAYOUT_SCOPE") == "Header";
            f.Record("布局窄验实际范围", new { effectOnly, headerOnly, note = "Header集中验证当前建设活跃标题、企业标题/关联布局及科室换框；不重复数字矩阵、角色或性能" });
            yield return f.Start(4, headerOnly ? ObserveDepartmentFrame :
                effectOnly ? (Func<Component, IEnumerator>)(_ => Settle()) : ObserveDepartmentGlyphBounds,
                firstEnterpriseCheck: effectOnly || headerOnly ? null : (Func<Component, IEnumerator>)ObserveEnterpriseInitialBounds);
            if (headerOnly) yield return ObserveBuildHeaders();
            var revision = f.State.EffectRuntime.StateRevision;
            EnterpriseSelectionProjection publicProjection = null;
            using (var submits = new RuleSubmitCounter())
            {
                if (effectOnly)
                {
                    yield return f.Click(ButtonObject(Expansion, "cooperationOpenButton"), "Effect续验_读取同局正式公开投影");
                    var page = Active("EnterpriseSelectionDialogView").Single();
                    publicProjection = Clone(Draft(page).Projection);
                    yield return f.Click(ButtonObject(page, "closeButton"), "Effect续验_关闭投影来源页");
                }
                foreach (var size in effectOnly ? Array.Empty<Vector2Int>() : Sizes)
                {
                    yield return f.Resolution(size);
                    yield return f.Click(ButtonObject(Expansion, "cooperationOpenButton"), "布局窄验_正式合作板入口");
                    var page = Active("EnterpriseSelectionDialogView").Single();
                    publicProjection = Clone(Draft(page).Projection);
                    Assert.That(((IDictionary)Read(page, "items")).Count, Is.EqualTo(4));
                    yield return f.Capture("合作板四企业与叠层_边界断言前", page);
                    if (headerOnly) CheckEnterpriseHeader(page, "正式Browse");
                    CheckEnterpriseBounds(page, false, "正式Browse");
                    var rhine = publicProjection.Boards.Single(b => b.ActiveDepartment != null);
                    var board = Board(page, rhine.Id); var overlay = Overlay(board);
                    CheckOverlayGeometry(page, Original(board), overlay, ((ScrollRect)Read(page, "candidateScroll")).viewport, rhine.ActiveDepartment);
                    yield return f.Click(OverlayButton(overlay), "布局窄验_叠层单击查看整体", Point(Artwork(overlay).rectTransform, .5f, .5f));
                    Assert.That(Draft(page).InspectedId, Is.EqualTo(rhine.Id));
                    CheckEnterpriseBounds(page, false, "正式Browse查看整体后");
                    // 只抽查一次右键进入和返回，保留原来的全手势矩阵但本批不执行。
                    if (size == Sizes[0])
                    {
                        Component Department() => Active("EnterpriseSelectionDialogView").SingleOrDefault(p => Draft(p).Projection?.Mode == EnterpriseSelectionMode.BrowseDepartment);
                        yield return PreviewAt(page, OverlayButton(overlay), Point(Artwork(overlay).rectTransform, .5f, .5f), "右键", "布局窄验_叠层只读导航", () => Department() != null);
                        var department = Department();
                        ThirdRoundUiAcceptanceChecks.PlayerLevels((Component)Read(department, "fixedCompany"), Draft(department).Projection.FixedCompany, f);
                        yield return f.Click(ButtonObject(department, "browseReturnButton"), "布局窄验_返回原合作板");
                        Assert.That(Active("EnterpriseSelectionDialogView").Single(), Is.SameAs(page));
                        Assert.That(Draft(page).InspectedId, Is.EqualTo(rhine.Id));
                        CheckEnterpriseBounds(page, false, "正式Browse返回后");
                        if (headerOnly)
                        {
                            CheckEnterpriseHeader(page, "正式Browse返回后");
                            yield return f.Capture("标题窄验_详情返回合作板", page);
                        }
                    }
                    yield return f.Click(ButtonObject(page, "closeButton"), "布局窄验_关闭合作板");
                }

                // 真实局的四企业/当前科室投影，补足Effect表现模式；不伪造规则请求或修改GameState。
                var input = Clone(publicProjection);
                input.RequestId = "controlled-enterprise-effect-layout"; input.Mode = EnterpriseSelectionMode.Effect;
                input.ReadOnly = false; input.SubmissionAvailable = input.CanCancel = true;
                var definitions = (EnterpriseDefinitions)Read(Expansion, "Definitions");
                var companies = input.Boards.Where(b => f.State.ItemZones.Items.Any(i => i.ItemId == b.Id && definitions.FindEnterprise(i.DefinitionId) != null)).ToArray();
                Assert.That(companies.Length, Is.EqualTo(4));
                input.Boards = companies;
                input.Targets = companies.Select(b =>
                {
                    var item = f.State.ItemZones.Items.Single(i => i.ItemId == b.Id);
                    var special = definitions.FindEnterprise(item.DefinitionId).Levels.First(l => l.Kind == "special");
                    var area = (Rect)Call(Expansion, "LevelArea", special.Level);
                    return new EnterpriseSelectionTarget { Id = b.Id + ":" + special.AbilityId, BoardId = b.Id, CompanyId = b.Id,
                        EffectId = special.AbilityId, Tier = special.Level, Label = b.Label, Description = b.Summary, Available = true,
                        X = area.x, Y = area.y, Width = area.width, Height = area.height };
                }).ToArray();
                var effect = NewPage(false); int callbacks = 0;
                Call(effect, "BindSubmission", (Func<EnterpriseSelectionProjection>)(() => input),
                    (Action<EnterpriseSelectionIntent>)(_ => callbacks++), (Action)(() => Call(effect, "ClearPresentation")));
                foreach (var size in Sizes)
                {
                    yield return f.Resolution(size);
                    Call(effect, "Present", input, true); yield return Settle();
                    yield return f.Capture("受控Effect四企业_边界断言前", effect);
                    if (headerOnly) CheckEnterpriseHeader(effect, "受控Effect");
                    CheckEnterpriseBounds(effect, true, "受控Effect");
                    var target = input.Targets.OrderByDescending(t => (t.Description ?? "").Length).First();
                    yield return f.Click(Target(effect, target.Id), "受控Effect_实际特效区域选择");
                    Assert.That(Draft(effect).SelectedId, Is.EqualTo(target.Id));
                    CheckEnterpriseBounds(effect, true, "受控Effect选择后");
                    // 长文压力仅扩展只读表现副本，明确不冒充一条正式能力的正文或结算。
                    input.Targets.Single(t => t.Id == target.Id).Description = string.Join("\n", Enumerable.Repeat(target.Description, 8));
                    input.Revision++;
                    Call(effect, "Present", input, true); yield return Settle();
                    var scroll = (ScrollRect)Read(effect, "descriptionScroll");
                    Assert.That(scroll.content.rect.height, Is.GreaterThan(scroll.viewport.rect.height + 1), "受控长文未形成实际内部溢出");
                    var frame = DetailsFrame(effect); var before = ScreenRect(frame);
                    var candidates = ((ScrollRect)Read(effect, "candidateScroll")).content.anchoredPosition;
                    f.Record("受控Effect长文边界", new { note = "同一局正式企业投影与能力ID，正文重复仅作UI长文压力；不提交规则", input.Mode,
                        frame = Geometry(frame), viewport = Geometry(scroll.viewport), content = Geometry(scroll.content) });
                    CheckEnterpriseBounds(effect, true, "受控Effect长文顶部");
                    yield return f.Capture("受控Effect长文_内部滚动前", effect);
                    yield return CheckFullDescription(effect, input.Targets.Single(t => t.Id == target.Id).Description);
                    var after = ScreenRect(frame);
                    Assert.That(Vector2.Distance(before.min, after.min) + Vector2.Distance(before.max, after.max), Is.LessThanOrEqualTo(1));
                    Assert.That(((ScrollRect)Read(effect, "candidateScroll")).content.anchoredPosition, Is.EqualTo(candidates));
                    CheckEnterpriseBounds(effect, true, "受控Effect长文末段");
                    if (headerOnly) CheckEnterpriseHeader(effect, "受控Effect长文末段");
                    Call(effect, "ClearPresentation"); yield return Settle();
                    // 下一分辨率恢复原正式正文，防止累乘长度。
                    foreach (var t in input.Targets) t.Description = companies.Single(b => b.Id == t.CompanyId).Summary;
                    input.Revision++;
                }
                Assert.That(callbacks, Is.Zero); Assert.That(submits.Calls, Is.Zero);
                Assert.That(f.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
                f.Record("布局窄验零提交终态", new { callbacks, ruleSubmitCalls = submits.Calls, revision,
                    finalRevision = f.State.EffectRuntime.StateRevision });
            }
        }

        private static RectTransform OuterFrame(Component page) =>
            page.GetComponentsInChildren<RectTransform>(false).Single(r => r.name == "Frame" || r.name == "Window Frame");

        private float[] RecordHeaderInsets(RectTransform frame, Text title, string label)
        {
            var world = new Vector3[4]; title.rectTransform.GetWorldCorners(world);
            var local = world.Select(frame.InverseTransformPoint).ToArray();
            var left = local.Min(v => v.x) - frame.rect.xMin;
            var right = frame.rect.xMax - local.Max(v => v.x);
            var top = frame.rect.yMax - local.Max(v => v.y);
            var ratio = top / frame.rect.height;
            f.Record("活跃标题相对外框", new { label, title.text, left, right, top, topRatio = ratio,
                frame = Geometry(frame), title = Geometry(title.rectTransform), title.preferredHeight,
                note = "在各自Frame局部逻辑坐标测量；不以窗口绝对屏幕位置或旧inactive标题作参照" });
            Assert.That(left, Is.EqualTo(43.5f).Within(.1f));
            Assert.That(right, Is.EqualTo(52.2f).Within(.1f));
            Assert.That(ratio, Is.EqualTo(.025f).Within(.0001f));
            Assert.That(title.preferredHeight, Is.LessThanOrEqualTo(title.rectTransform.rect.height + 1));
            AssertContained(ScreenRect(title.rectTransform), ScreenRect(frame), "活跃标题超出外框");
            return new[] { left, right, ratio };
        }

        private IEnumerator ObserveBuildHeaders()
        {
            foreach (var size in Sizes)
            {
                yield return f.Resolution(size);
                var build = (Button)Read(Read(f.Hud, "ActionPanelView"), "BuildButton");
                Assert.That(build.IsInteractable(), Is.True);
                yield return f.Click(build.gameObject, "标题参照_实际建设入口");
                yield return Wait(() => Active("FacilityBuildDialogView").Any(), "实际建设页未打开");
                var page = Active("FacilityBuildDialogView").Single();
                yield return f.Capture("建设页活跃标题与外框_实际入口", page);
                var title = page.GetComponentsInChildren<Text>(false).Single(t => t.name == "Title" && t.transform.parent.name == "Expanded Content");
#if UNITY_EDITOR
                const string sourcePath = "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/FacilityBuildDialog.prefab";
                var source = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                var sourceTitle = source.GetComponentsInChildren<Text>(true).Single(t => t.name == "Title" && t.gameObject.activeSelf && t.transform.parent.name == "Expanded Content");
                Assert.That(UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sourceTitle.gameObject, out string guid, out long fileId), Is.True);
                Assert.That(fileId, Is.EqualTo(7162000000000000001L));
                f.Record("建设活跃标题源对象", new { sourcePath, guid, fileId, runtimeTitle = Geometry(title.rectTransform) });
#endif
                buildHeaderInsets[Screen.width + "x" + Screen.height] = RecordHeaderInsets(OuterFrame(page), title, "实际建设页活跃标题");
                yield return f.Click(ButtonObject(page, "cancel"), "标题参照_关闭建设页");
                yield return Wait(() => !page.gameObject.activeInHierarchy, "建设页关闭未完成");
            }
            yield return f.Resolution(Sizes[0]);
        }

        private void CheckEnterpriseHeader(Component page, string label)
        {
            var frame = OuterFrame(page); var title = (Text)Read(page, "title"); var context = (Text)Read(page, "context");
            var actual = RecordHeaderInsets(frame, title, label);
            var reference = buildHeaderInsets[Screen.width + "x" + Screen.height];
            for (var i = 0; i < actual.Length; i++) Assert.That(actual[i], Is.EqualTo(reference[i]).Within(.001f));
            var titleBounds = ScreenRect(title.rectTransform); var contextBounds = ScreenRect(context.rectTransform);
            var bodyBounds = ScreenRect((RectTransform)Read(page, "body"));
            var close = (RectTransform)ButtonObject(page, "closeButton").transform; var closeBounds = ScreenRect(close);
            var frameBounds = ScreenRect(frame);
            f.Record("企业标题关联布局", new { label, context.text, context.preferredHeight,
                titleContextGapPixels = titleBounds.yMin - contextBounds.yMax,
                contextBodyGapPixels = contextBounds.yMin - bodyBounds.yMax,
                context = Geometry(context.rectTransform), body = Geometry((RectTransform)Read(page, "body")), close = Geometry(close) });
            Assert.That(titleBounds.yMin - contextBounds.yMax, Is.GreaterThanOrEqualTo(-1));
            Assert.That(contextBounds.yMin - bodyBounds.yMax, Is.GreaterThanOrEqualTo(-1));
            Assert.That(context.preferredHeight, Is.LessThanOrEqualTo(context.rectTransform.rect.height + 1));
            AssertContained(contextBounds, frameBounds, "说明超出外框");
            AssertContained(closeBounds, frameBounds, "关闭按钮超出外框");
            Assert.That(closeBounds.center.x, Is.GreaterThan(frameBounds.center.x));
            Assert.That(closeBounds.center.y, Is.GreaterThan(frameBounds.center.y));
        }

        private IEnumerator ObserveDepartmentFrame(Component page)
        {
            foreach (var size in Sizes)
            {
                yield return f.Resolution(size);
                yield return f.Capture("科室新外框与正式等级_实际开局", page);
                var frame = OuterFrame(page); var image = frame.GetComponent<Image>();
                f.Record("科室外层Frame实际来源", new { geometry = Geometry(frame), type = image.type.ToString(), image.pixelsPerUnitMultiplier });
#if UNITY_EDITOR
                Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(UnityEditor.AssetDatabase.GetAssetPath(image.sprite)),
                    Is.EqualTo("b4ca9e75c1cae354aa1b2ce17d12d467"));
#endif
                Assert.That(image.type, Is.EqualTo(Image.Type.Sliced));
                Assert.That(image.pixelsPerUnitMultiplier, Is.EqualTo(1));
                ThirdRoundUiAcceptanceChecks.PlayerLevels((Component)Read(page, "fixedCompany"), Draft(page).Projection.FixedCompany, f);
            }
            yield return f.Resolution(Sizes[0]);
        }

        private IEnumerator ObserveEnterpriseInitialBounds(Component page)
        {
            foreach (var size in Sizes)
            {
                yield return f.Resolution(size);
                yield return f.Capture("正式企业初选_Footer与板图边界", page);
                CheckEnterpriseBounds(page, true, "正式初选");
            }
            yield return f.Resolution(Sizes[0]);
        }

        private static RectTransform DetailsFrame(Component page) => page.GetComponentsInChildren<RectTransform>(true).Single(t => t.name == "Details Frame");
        private static float[] BoundsArray(Rect value) => new[] { value.xMin, value.yMin, value.xMax, value.yMax };

        private void CheckEnterpriseBounds(Component page, bool footerVisible, string label)
        {
            var scroll = (ScrollRect)Read(page, "candidateScroll"); var viewport = ScreenRect(scroll.viewport);
            var details = DetailsFrame(page); var right = ScreenRect(details);
            var footer = (GameObject)Read(page, "footer"); var footerRect = ScreenRect((RectTransform)footer.transform);
            var boards = ((IDictionary)Read(page, "items")).Values.Cast<Component>().ToArray();
            f.Record("企业板图与详情实际边界", new { label, footerVisible = footer.activeInHierarchy,
                viewport = Geometry(scroll.viewport), detailsFrame = Geometry(details), footer = Geometry((RectTransform)footer.transform),
                boards = boards.Select(b => new { original = Geometry(Original(b)),
                    topDeltaPixels = ScreenRect(Original(b)).yMax - right.yMax, bottomDeltaPixels = ScreenRect(Original(b)).yMin - right.yMin,
                    overlayActive = Overlay(b).gameObject.activeInHierarchy,
                    overlay = Overlay(b).gameObject.activeInHierarchy ? Geometry(Artwork(Overlay(b)).rectTransform) : null }).ToArray() });
            Assert.That(footer.activeInHierarchy, Is.EqualTo(footerVisible), label + "Footer显隐错误");
            Assert.That(boards, Is.Not.Empty);
            Assert.That(scroll.horizontalScrollbar == null || !scroll.horizontalScrollbar.gameObject.activeInHierarchy, Is.True, "企业横条仍显示");
            foreach (var board in boards)
            {
                var rect = Original(board); var bounds = ScreenRect(rect);
                AssertContained(bounds, viewport, label + "板图被Viewport裁切");
                Assert.That(Mathf.Abs(bounds.yMax - right.yMax), Is.LessThanOrEqualTo(1), label + "详情顶边未与板图对齐");
                Assert.That(Mathf.Abs(bounds.yMin - right.yMin), Is.LessThanOrEqualTo(1), label + "详情底边未与板图对齐");
                var areas = new List<RectTransform> { rect };
                if (Overlay(board).gameObject.activeInHierarchy) areas.Add(Artwork(Overlay(board)).rectTransform);
                foreach (var area in areas)
                {
                    var actual = ScreenRect(area); AssertContained(actual, viewport, label + "原图/叠层裁切");
                    foreach (var mask in area.GetComponentsInParent<RectMask2D>().Where(m => m.isActiveAndEnabled))
                        AssertContained(actual, ScreenRect((RectTransform)mask.transform), label + "被祖先RectMask2D裁切");
                    foreach (var mask in area.GetComponentsInParent<Mask>().Where(m => m.isActiveAndEnabled))
                        AssertContained(actual, ScreenRect((RectTransform)mask.transform), label + "被祖先Mask裁切");
                    if (footerVisible) Assert.That(actual.yMin, Is.GreaterThanOrEqualTo(footerRect.yMax - 1), label + "Footer遮挡板图");
                }
            }
        }

        private IEnumerator ObserveDepartmentGlyphBounds(Component page)
        {
            var source = Clone(Draft(page).Projection); var board = (Component)Read(page, "fixedCompany");
            foreach (var size in Sizes)
            {
                yield return f.Resolution(size);
                ThirdRoundUiAcceptanceChecks.PlayerLevels(board, source.FixedCompany, f);
                var markers = ((Array)Read(board, "playerLevelMarkers")).Cast<Component>().ToArray();
                var originalValues = markers.Select(m => int.Parse(((Text)Read(m, "NumberText")).text)).ToArray();
                var values = new[] { 0, 1, 5, 0 };
                for (var i = 0; i < markers.Length; i++) Call(markers[i], "SetCount", values[i], 1f);
                yield return Settle();
                yield return f.Capture("科室数字0_1_5_受控字形边界", page);
                for (var i = 0; i < markers.Length; i++)
                {
                    var marker = markers[i]; var text = (Text)Read(marker, "NumberText"); var face = (Image)Read(marker, "Face");
                    var mesh = text.canvasRenderer.GetMesh();
                    var canvas = text.canvas; var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    var vertices = mesh.vertices.Select(v => RectTransformUtility.WorldToScreenPoint(camera, text.transform.TransformPoint(v))).ToArray();
                    f.Record("标准数字最终渲染字形", new { value = values[i], text.text, text.fontSize, text.resizeTextForBestFit,
                        face = Geometry(face.rectTransform), number = Geometry(text.rectTransform),
                        vertices = vertices.Select(v => new[] { v.x, v.y }).ToArray(),
                        note = "同一正式科室页实例仅SetCount显示0/1/5；采CanvasRenderer最终网格含已有文字效果，不改字号、尺寸或规则状态" });
                    Assert.That(vertices, Is.Not.Empty, "数字未生成可见字形");
                    var bounds = Rect.MinMaxRect(vertices.Min(v => v.x), vertices.Min(v => v.y), vertices.Max(v => v.x), vertices.Max(v => v.y));
                    AssertContained(bounds, ScreenRect(face.rectTransform), "数字字形越出标准方块");
                    Assert.That(bounds.width, Is.GreaterThan(0)); Assert.That(bounds.height, Is.GreaterThan(0));
                    var square = ScreenRect(face.rectTransform);
                    Assert.That(Mathf.Abs(square.width - square.height), Is.LessThanOrEqualTo(1), "标准方块发生非等比拉伸");
                    Assert.That(text.text, Is.EqualTo(values[i].ToString()));
                }
                // 只恢复组件数字；正式请求页由Coordinator以effectPage=true管理，不能用信息页Present替换其生命周期。
                for (var i = 0; i < markers.Length; i++) Call(markers[i], "SetCount", originalValues[i], 1f);
                yield return Settle();
                ThirdRoundUiAcceptanceChecks.PlayerLevels(board, source.FixedCompany, f);
                yield return f.Capture("科室数字恢复正式等级绑定", page);
            }
            yield return f.Resolution(Sizes[0]);
        }

        [UnityTest]
        public IEnumerator SampleScene_PublicEnterprisesComposeTheRealActiveDepartment()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, ObserveInitialDepartment);
            var view = (GameStateView)Call(Expansion, "VisibleView");
            var realRhine = view.ItemZones.Items.Single(item => item.DefinitionId == "enterprise.rhine_lab");
            var relation = view.ItemZones.Relations.Single(r => r.SourceItemId == realRhine.ItemId &&
                r.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation);
            var realDepartment = view.ItemZones.Items.Single(item => item.ItemId == relation.TargetItemId);
            var revision = f.State.EffectRuntime.StateRevision;
            foreach (var size in Sizes)
            {
                yield return f.Resolution(size);
                using (var submits = new RuleSubmitCounter())
                {
                    yield return f.Click(ButtonObject(Expansion, "cooperationOpenButton"), "叠层_正式合作浏览入口");
                    var page = Active("EnterpriseSelectionDialogView").Single();
                    var projection = Draft(page).Projection;
                    var rhine = projection.Boards.Single(b => b.Id == realRhine.ItemId);
                    Assert.That(rhine.ActiveDepartment, Is.Not.Null);
                    Assert.That(rhine.ActiveDepartment.ItemId, Is.EqualTo(relation.TargetItemId));
                    Assert.That(projection.Boards.Any(b => b.Id == realDepartment.ItemId), Is.True,
                        "渲染合板不能删除原投影中的科室稳定ID");
                    Assert.That(projection.Targets.Any(t => t.BoardId == realDepartment.ItemId), Is.True);
                    var expectedBoardIds = projection.Boards.Where(b => b.Id != realDepartment.ItemId).Select(b => b.Id).ToArray();
                    Assert.That(expectedBoardIds.Length, Is.EqualTo(4));
                    Assert.That(((IDictionary)Read(page, "items")).Keys.Cast<string>(), Is.EquivalentTo(expectedBoardIds));
                    var scroll = (ScrollRect)Read(page, "candidateScroll");
                    Assert.That(scroll.horizontalScrollbar, Is.Not.Null);
                    Assert.That(scroll.horizontalScrollbar.gameObject.activeInHierarchy, Is.False, "公开四企业仍出现横滚条");
                    Assert.That(scroll.content.rect.width, Is.LessThanOrEqualTo(scroll.viewport.rect.width + 1f));
                    foreach (var id in expectedBoardIds)
                    {
                        var item = Board(page, id);
                        AssertContained(ScreenRect(Original(item)), ScreenRect(scroll.viewport), "四企业原板被裁切：" + id);
                        CheckLevels(item, projection.Boards.Single(b => b.Id == id));
                    }
                    var board = Board(page, rhine.Id);
                    var overlay = Overlay(board);
                    Assert.That(page.GetComponentsInChildren(T("EnterpriseDepartmentOverlayView"), false).Length,
                        Is.EqualTo(1), "公开浏览应只有一个激活的科室叠层");
                    CheckOverlayGeometry(page, Original(board), overlay, scroll.viewport, rhine.ActiveDepartment);
                    f.Record("正式叠层关系与视觉单元", new { relation.SourceItemId, relation.TargetItemId,
                        realDepartment.DefinitionId, visualBoardIds = expectedBoardIds,
                        projectionBoardIds = projection.Boards.Select(b => b.Id).ToArray(),
                        note = "当前正式ActiveDepartmentRelation，不按候选或中文名称猜当前科室" });
                    yield return f.Capture("正式四企业与当前科室叠层", page);
                    var selected = Draft(page).SelectedIds.ToArray();
                    yield return f.Click(OverlayButton(overlay), "叠层内部单击_查看莱茵整体", Point(Artwork(overlay).rectTransform, .5f, .5f));
                    Assert.That(Draft(page).InspectedId, Is.EqualTo(rhine.Id));
                    yield return CheckFullDescription(page, rhine.Summary);
                    Assert.That(rhine.Summary, Does.Contain(rhine.ActiveDepartment.Summary));
                    var firstLevel = (string)Call(Expansion, "Description", realRhine.DefinitionId + ".level_1");
                    Assert.That(rhine.Summary, Does.Contain(firstLevel));
                    Assert.That(rhine.Summary.IndexOf(firstLevel, StringComparison.Ordinal),
                        Is.LessThan(rhine.Summary.IndexOf(rhine.ActiveDepartment.Summary, StringComparison.Ordinal)), "莱茵1级和科室2—5级段落顺序错误");
                    Assert.That(page.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Detail Artwork"), Is.False);
                    ThirdRoundUiAcceptanceChecks.FramesAndBackgrounds(page);
                    Assert.That(Draft(page).SelectedIds, Is.EqualTo(selected));
                    yield return BrowseDepartmentMatrix(page, rhine.Id, true);
                    yield return BrowseDepartmentMatrix(page, rhine.Id, false);
                    Assert.That(submits.Calls, Is.Zero);
                    Assert.That(f.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
                    yield return f.Click(ButtonObject(page, "closeButton"), "叠层_关闭公开浏览");
                    Assert.That(page.gameObject.activeInHierarchy, Is.False);
                }
            }
            yield return ThirdRoundUiAcceptanceChecks.CityStyleOrder(f);
            f.Record("正式叠层链完成", new { players = 4, ruleRevision = revision,
                note = "自动断言完成；叠板两分辨率截图仍须人工检查，不作性能结论" });
        }

        [UnityTest]
        public IEnumerator SampleScene_ControlledRelationsPreserveDraftAndOtherSelectionModes()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, ObserveInitialDepartment);
            var view = Clone((GameStateView)Call(Expansion, "VisibleView"));
            var rhineItem = view.ItemZones.Items.Single(item => item.DefinitionId == "enterprise.rhine_lab");
            EnterpriseBoardProjection Project(GameStateView source) =>
                (EnterpriseBoardProjection)Call(Expansion, "ProjectBoard", source, rhineItem.ItemId, rhineItem.DefinitionId);
            var current = Project(view);
            Assert.That(current.ActiveDepartment, Is.Not.Null);
            var otherId = initialDepartment.Targets.Select(t => t.DepartmentId).First(id => id != current.ActiveDepartment.ItemId);
            var switchedView = Clone(view);
            var switchedRelation = switchedView.ItemZones.Relations.Single(r => r.SourceItemId == rhineItem.ItemId &&
                r.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation);
            var oldItem = switchedView.ItemZones.Items.Single(item => item.ItemId == switchedRelation.TargetItemId);
            var nextItem = switchedView.ItemZones.Items.FirstOrDefault(item => item.ItemId == otherId);
            if (nextItem == null)
            {
                nextItem = Clone(initialView.ItemZones.Items.Single(item => item.ItemId == otherId));
                switchedView.ItemZones.Items.Add(nextItem);
            }
            // 只改只读View副本来表达宿主已确认切换后的关系，绝不写真实State／Session。
            nextItem.ZoneId = oldItem.ZoneId;
            switchedRelation.TargetItemId = otherId;
            var switched = Project(switchedView);
            Assert.That(switched.ActiveDepartment?.ItemId, Is.EqualTo(otherId));
            f.Record("受控关系补证边界", new { current = current.ActiveDepartment, next = switched.ActiveDepartment,
                note = "实际Registry和正式科室实例/定义；后继View关系受控，不声称走完正式切换规则，不改真实GameState" });
            var page = NewPage(true);
            var projection = Clone(initialDepartment);
            projection.RequestId = "controlled-active-department";
            projection.Mode = EnterpriseSelectionMode.SwitchDepartment;
            projection.CanCancel = projection.SubmissionAvailable = true;
            projection.FixedCompany = current;
            projection.CurrentDepartmentId = current.ActiveDepartment.ItemId;
            int callbacks = 0;
            Call(page, "BindSubmission", (Func<EnterpriseSelectionProjection>)(() => projection),
                (Action<EnterpriseSelectionIntent>)(_ => callbacks++), (Action)(() => Call(page, "ClearPresentation")));
            var revision = f.State.EffectRuntime.StateRevision;
            using (var submits = new RuleSubmitCounter())
            {
                foreach (var size in Sizes)
                {
                    yield return f.Resolution(size);
                    projection.FixedCompany = Clone(current); projection.CurrentDepartmentId = current.ActiveDepartment.ItemId;
                    projection.Revision++;
                    Call(page, "Present", projection, false); yield return Settle();
                    var fixedBoard = (Component)Read(page, "fixedCompany");
                    var overlay = Overlay(fixedBoard);
                    CheckOverlayGeometry(page, Original(fixedBoard), overlay, null, current.ActiveDepartment);
                    CheckLevels(fixedBoard, current);
                    yield return f.Click(Target(page, otherId), "受控_未确认的另一科室候选");
                    Assert.That(Draft(page).SelectedIds, Is.EqualTo(new[] { otherId }));
                    Assert.That(Draft(page).Projection.FixedCompany.ActiveDepartment.ItemId, Is.EqualTo(current.ActiveDepartment.ItemId));
                    CheckTexture(page, Artwork(overlay), current.ActiveDepartment.VisualKey);
                    yield return f.Capture("受控科室候选不替换正式叠层", page);
                    ThirdRoundUiAcceptanceChecks.Department(page, f);
                    yield return ThirdRoundUiAcceptanceChecks.FixedBoardRejectsGestures(page, f);
                    Assert.That(Draft(page).SelectedIds, Is.EqualTo(new[] { otherId }));
                    var candidate = Board(page, otherId);
                    yield return PreviewMatrix(page, Target(page, otherId), Point(Original(candidate), .5f, .5f),
                        projection.Boards.Single(b => b.Id == otherId).VisualKey, null, "受控切换候选三手势");
                    projection.FixedCompany = Clone(switched); projection.CurrentDepartmentId = otherId; projection.Revision++;
                    Call(page, "Present", projection, false); yield return Settle();
                    CheckTexture(page, Artwork(overlay), switched.ActiveDepartment.VisualKey);
                    Assert.That(Draft(page).SelectedIds, Is.Empty, "切换后的当前科室不能残留为可提交候选");
                    ThirdRoundUiAcceptanceChecks.Department(page, f);
                    yield return f.Capture("受控已确认关系更新叠层", page);
                }
                Assert.That(callbacks, Is.Zero); Assert.That(submits.Calls, Is.Zero);
                Call(page, "ClearPresentation"); yield return Settle();
                yield return CheckInvalidAndRemovedRelations(view, current, Project);
                yield return CheckThreePlayerColors(view, Project);
                yield return CheckCompanyAndEffectRegression(view);
                Assert.That(submits.Calls, Is.Zero, "组件补证不能进入正式规则提交");
                Assert.That(f.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
            }
            f.Record("受控叠层补证完成", new { actualPlayers = 4,
                note = "切换/错误关系/三人颜色为View副本补证，Company/Effect为组件输入；未启动随机三人或八轮矩阵" });
        }

        private IEnumerator ObserveInitialDepartment(Component page)
        {
            initialDepartment = Clone(Draft(page).Projection);
            initialView = Clone((GameStateView)Call(Expansion, "VisibleView"));
            Assert.That(initialDepartment.FixedCompany, Is.Not.Null);
            Assert.That(initialDepartment.FixedCompany.ActiveDepartment, Is.Null);
            Assert.That(string.IsNullOrEmpty(initialDepartment.CurrentDepartmentId), Is.True);
            var fixedBoard = (Component)Read(page, "fixedCompany");
            Assert.That(Overlay(fixedBoard).gameObject.activeInHierarchy, Is.False, "正式开局尚无当前科室却出现叠层");
            foreach (var size in Sizes)
            {
                yield return f.Resolution(size);
                ThirdRoundUiAcceptanceChecks.Department(page, f);
                yield return f.Capture("正式科室开局无当前叠层", page);
            }
        }

        private Component NewPage(bool department)
        {
            var page = (Component)Call(f.Registry, "InstantiateEnterpriseSelection", f.Host, department);
            ownedPages.Add(page); return page;
        }

        private void CheckOverlayGeometry(Component page, RectTransform original, Component overlay,
            RectTransform viewport, EnterpriseDepartmentProjection expected)
        {
            Assert.That(overlay, Is.Not.Null); Assert.That(overlay.gameObject.activeInHierarchy, Is.True);
            var raw = Artwork(overlay); CheckTexture(page, raw, expected.VisualKey);
            var corners = new Vector3[4]; raw.rectTransform.GetWorldCorners(corners);
            var normalized = corners.Select(c => original.InverseTransformPoint(c)).Select(c => new Vector2(
                (c.x - original.rect.xMin) / original.rect.width, (c.y - original.rect.yMin) / original.rect.height)).ToArray();
            var requested = new[] { new Vector2(164f / 650, 442f / 1850), new Vector2(164f / 650, 1442f / 1850),
                new Vector2(680f / 650, 1442f / 1850), new Vector2(680f / 650, 442f / 1850) };
            f.Record("叠层归一化世界四角", new { expected.ItemId, expected.VisualKey,
                board = Geometry(original), overlay = Geometry(raw.rectTransform),
                normalized = normalized.Select(v => new[] { v.x, v.y }).ToArray(),
                note = "用户明确指定的叠板几何；包含原图右伸30像素，不用关闭遮罩或放大板面掩盖裁切" });
            for (var i = 0; i < 4; i++) Assert.That(Vector2.Distance(normalized[i], requested[i]), Is.LessThan(.0005f));
            var bounds = ScreenRect(raw.rectTransform);
            AssertContained(bounds, ScreenRect((RectTransform)Read(page, "panel")), "叠层超出主Panel");
            if (viewport != null) AssertContained(bounds, ScreenRect(viewport), "叠层右伸30px被候选视口裁切");
            foreach (var mask in raw.GetComponentsInParent<RectMask2D>().Where(m => m.isActiveAndEnabled))
                AssertContained(bounds, ScreenRect((RectTransform)mask.transform), "叠层被RectMask2D裁切");
            foreach (var mask in raw.GetComponentsInParent<Mask>().Where(m => m.isActiveAndEnabled))
                AssertContained(bounds, ScreenRect((RectTransform)mask.transform), "叠层被Mask裁切");
            Assert.That(bounds.width / bounds.height, Is.EqualTo(516f / 1000).Within(.002f));
        }

        private static void AssertContained(Rect inner, Rect outer, string reason)
        {
            Assert.That(inner.xMin, Is.GreaterThanOrEqualTo(outer.xMin - 1), reason);
            Assert.That(inner.xMax, Is.LessThanOrEqualTo(outer.xMax + 1), reason);
            Assert.That(inner.yMin, Is.GreaterThanOrEqualTo(outer.yMin - 1), reason);
            Assert.That(inner.yMax, Is.LessThanOrEqualTo(outer.yMax + 1), reason);
        }

        private static void CheckTexture(Component page, RawImage actual, string visualKey)
        {
            var art = Call(Read(page, "catalog"), "Find", visualKey);
            Assert.That(art, Is.Not.Null);
            Assert.That(actual.texture, Is.SameAs(Read(art, "texture")));
            Assert.That(actual.uvRect, Is.EqualTo((Rect)Read(art, "uv")));
            Assert.That(actual.enabled, Is.True);
        }

        private void CheckLevels(Component board, EnterpriseBoardProjection expected)
        {
            ThirdRoundUiAcceptanceChecks.PlayerLevels(board, expected, f);
        }

        private IEnumerator BrowseDepartmentMatrix(Component parent, string enterpriseId, bool fromOverlay)
        {
            foreach (var gesture in new[] { "右键", "双击", "长按" })
            {
                var other = ((IDictionary)Read(parent, "items")).Keys.Cast<string>().First(id => id != enterpriseId);
                yield return f.Click(Target(parent, other), "导航前查看另一家企业");
                var board = Board(parent, enterpriseId); var overlay = Overlay(board);
                var target = fromOverlay ? OverlayButton(overlay) : Target(parent, enterpriseId);
                var point = fromOverlay ? Point(Artwork(overlay).rectTransform, .5f, .5f) : Point(Original(board), .12f, .94f);
                var inspected = Draft(parent).InspectedId; var selected = Draft(parent).SelectedIds.ToArray();
                var candidates = (ScrollRect)Read(parent, "candidateScroll"); var description = (ScrollRect)Read(parent, "descriptionScroll");
                var candidatePosition = candidates.content.anchoredPosition; var descriptionPosition = description.content.anchoredPosition;
                var descriptionText = ((Text)Read(parent, "description")).text;
                Component Current() => Active("EnterpriseSelectionDialogView").SingleOrDefault(p =>
                    Draft(p).Projection?.Mode.ToString() == "BrowseDepartment");
                yield return PreviewAt(parent, target, point, gesture, "公开莱茵进入只读科室", () => Current() != null);
                var page = Current();
                Assert.That(parent.gameObject.activeInHierarchy, Is.False);
                Assert.That(Draft(page).Projection.ReadOnly, Is.True);
                Assert.That(Draft(page).Projection.SubmissionAvailable, Is.False);
                Assert.That(Draft(page).CanConfirm, Is.False);
                Assert.That(((Button)Read(page, "confirm")).gameObject.activeInHierarchy, Is.False);
                Assert.That(((Button)Read(page, "cancel")).gameObject.activeInHierarchy, Is.False);
                ThirdRoundUiAcceptanceChecks.Department(page, f);
                if (gesture == "右键")
                {
                    yield return f.Capture(fromOverlay ? "叠层进入只读科室" : "本体进入只读科室", page);
                    yield return ThirdRoundUiAcceptanceChecks.FixedBoardRejectsGestures(page, f);
                    var candidate = ((IDictionary)Read(page, "items")).Values.Cast<Component>().First();
                    // ID取实际显示字典，避免依赖可编辑名称。
                    var id = ((IDictionary)Read(page, "items")).Keys.Cast<string>().First(k => ReferenceEquals(Board(page, k), candidate));
                    yield return PreviewMatrix(page, Target(page, id), Point(Original(candidate), .5f, .5f),
                        Draft(page).Projection.Boards.Single(b => b.Id == id).VisualKey, null, "只读科室候选");
                }
                yield return f.Click(ButtonObject(page, "browseReturnButton"), "返回企业合作板");
                Assert.That(Active("EnterpriseSelectionDialogView").Single(), Is.SameAs(parent), "返回时必须复用原公开页实例");
                Assert.That(Draft(parent).InspectedId, Is.EqualTo(inspected));
                Assert.That(((Text)Read(parent, "description")).text, Is.EqualTo(descriptionText), "莱茵预览不应偷偷替换进入前右栏");
                Assert.That(Draft(parent).SelectedIds, Is.EqualTo(selected));
                Assert.That(Vector2.Distance(candidates.content.anchoredPosition, candidatePosition), Is.LessThan(.1f));
                Assert.That(Vector2.Distance(description.content.anchoredPosition, descriptionPosition), Is.LessThan(.1f));
                f.Record("只读科室双层返回保持父页", new { fromOverlay, gesture, enterpriseId, inspected, selected,
                    candidatePosition = new[] { candidatePosition.x, candidatePosition.y }, descriptionPosition = new[] { descriptionPosition.x, descriptionPosition.y } });
            }
        }

        private IEnumerator PreviewMatrix(Component page, GameObject target, Vector2 exactPoint, string visualKey,
            EnterpriseDepartmentProjection composite, string label)
        {
            foreach (var gesture in new[] { "右键", "双击", "长按" })
            {
                var selection = Draft(page).SelectedIds.ToArray();
                var scroll = (ScrollRect)Read(page, "candidateScroll"); var position = scroll.content.anchoredPosition;
                using (var submits = new RuleSubmitCounter())
                {
                    yield return PreviewAt(page, target, exactPoint, gesture, label);
                    CheckTexture(page, (RawImage)Read(page, "enlargedImage"), visualKey);
                    var enlarged = (Component)Read(page, "enlargedDepartmentOverlay");
                    Assert.That(enlarged.gameObject.activeInHierarchy, Is.EqualTo(composite != null));
                    if (composite != null)
                    {
                        CheckOverlayGeometry(page, ((RawImage)Read(page, "enlargedImage")).rectTransform, enlarged, null, composite);
                        Assert.That(Artwork(enlarged).raycastTarget, Is.False, "放大叠层不应截获单击返回");
                    }
                    if (gesture == "右键") yield return f.Capture(label + "详情", page);
                    var focus = Read(page, "savedFocus") as GameObject;
                    yield return f.Click((GameObject)Read(page, "detailsReturnSurface"), label + "单击屏幕返回");
                    Assert.That(((GameObject)Read(page, "detailPage")).activeInHierarchy, Is.False);
                    Assert.That(Draft(page).SelectedIds, Is.EqualTo(selection));
                    Assert.That(Vector2.Distance(scroll.content.anchoredPosition, position), Is.LessThan(.1f));
                    if (focus != null && focus.activeInHierarchy) Assert.That(EventSystem.current.currentSelectedGameObject, Is.SameAs(focus));
                    Assert.That(submits.Calls, Is.Zero, label + gesture + "误提交规则");
                    f.Record("叠层手势返回通过", new { label, gesture, selection, ruleSubmitCalls = submits.Calls });
                }
            }
        }

        private IEnumerator PreviewAt(Component page, GameObject target, Vector2 point, string gesture, string label, Func<bool> opened = null)
        {
            var hit = Hit(point);
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            Assert.That(handler, Is.SameAs(ExecuteEvents.GetEventHandler<IPointerClickHandler>(target)),
                label + gesture + "固定点被整板目标或其他控件吞掉");
            f.Record("叠层固定点实际手势命中", new { label, gesture, point = new[] { point.x, point.y },
                target = HotseatUiInputDriver.PathOf(target), hit = HotseatUiInputDriver.PathOf(hit.gameObject),
                handler = HotseatUiInputDriver.PathOf(handler) });
            var data = new PointerEventData(EventSystem.current) { pointerId = gesture == "右键" ? -2 : -1,
                position = point, pressPosition = point, button = gesture == "右键" ? PointerEventData.InputButton.Right : PointerEventData.InputButton.Left,
                eligibleForClick = true, clickCount = 1, pointerCurrentRaycast = hit, pointerPressRaycast = hit };
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
            if (gesture == "长按") yield return new WaitForSecondsRealtime((float)Read(handler.GetComponent<Button>(), "longPressSeconds") + .08f);
            else yield return null;
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
            if (gesture == "双击")
            {
                // 两次真实按下/释放，间隔远小于生产配置的双击窗口。
                yield return new WaitForSecondsRealtime(.04f);
                Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(point).gameObject), Is.SameAs(handler));
                data.clickCount = 2;
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler); yield return null;
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
            }
            yield return Wait(opened ?? (() => ((GameObject)Read(page, "detailPage")).activeInHierarchy), label + gesture + "未打开目标页面", 3);
            if (opened != null) { yield return Settle(); yield break; }
            var openedFrame = (int)Read(page, "detailsOpenedFrame"); yield return Settle();
            Assert.That((int)Read(page, "detailsOpenedFrame"), Is.EqualTo(openedFrame), "单次手势重复打开详情");
        }

        private IEnumerator CheckFullDescription(Component page, string expected)
        {
            Assert.That(expected, Is.Not.Null.And.Not.Empty);
            var description = (Text)Read(page, "description");
            Assert.That(description.text, Does.Contain(expected), "叠层单击未显示当前科室完整奖励正文");
            var scroll = (ScrollRect)Read(page, "descriptionScroll");
            var before = scroll.content.anchoredPosition;
            var wheelBudget = Mathf.CeilToInt(Mathf.Max(0, scroll.content.rect.height - scroll.viewport.rect.height) /
                Mathf.Max(1, scroll.scrollSensitivity)) + 8;
            for (var i = 0; i < wheelBudget && scroll.content.rect.height > scroll.viewport.rect.height + 1 && scroll.verticalNormalizedPosition > .001f; i++)
            {
                var point = ScreenRect(scroll.viewport).center; var hit = Hit(point);
                var handler = ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject);
                Assert.That(handler, Is.SameAs(scroll.gameObject), "科室正文滚轮被其他区截获");
                ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current) { position = point,
                    scrollDelta = new Vector2(0, -1), pointerCurrentRaycast = hit }, ExecuteEvents.scrollHandler);
                yield return null; yield return null;
            }
            yield return Settle();
            f.Record("正文滚动终点诊断", new { wheelBudget, scroll.verticalNormalizedPosition,
                viewport = Geometry(scroll.viewport), content = Geometry(scroll.content) });
            if (scroll.content.rect.height > scroll.viewport.rect.height + 1)
                Assert.That(scroll.verticalNormalizedPosition, Is.LessThanOrEqualTo(.001f), "正文滚轮未到达底部");
            string Compact(string value) => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
            var segments = description.GetComponentsInChildren<Text>(false)
                .Where(t => t != description && t.enabled && !string.IsNullOrWhiteSpace(t.text)).ToArray();
            var lastParagraph = description.text.Split('\n').Last(s => !string.IsNullOrWhiteSpace(s));
            Assert.That(Compact(string.Concat(segments.Select(t => t.text))), Does.EndWith(Compact(lastParagraph)));
            var lastSegments = new List<Text>(); var remaining = Compact(lastParagraph).Length;
            for (var i = segments.Length - 1; i >= 0 && remaining > 0; i--)
            { lastSegments.Add(segments[i]); remaining -= Compact(segments[i].text).Length; }
            Assert.That(lastSegments, Is.Not.Empty);
            foreach (var segment in lastSegments)
                AssertContained(ScreenRect(segment.rectTransform), ScreenRect(scroll.viewport), "科室奖励正文末段不可达");
            f.Record("当前科室完整正文可达", new { expected, lastParagraph,
                before = new[] { before.x, before.y }, after = new[] { scroll.content.anchoredPosition.x, scroll.content.anchoredPosition.y },
                tail = lastSegments.AsEnumerable().Reverse().Select(t => new { t.text, geometry = Geometry(t.rectTransform) }).ToArray(),
                viewport = Geometry(scroll.viewport) });
            yield return f.Capture("当前科室正文末段", page);
        }

        private IEnumerator CheckInvalidAndRemovedRelations(GameStateView view, EnterpriseBoardProjection current,
            Func<GameStateView, EnterpriseBoardProjection> project)
        {
            var invalidViews = new List<GameStateView>();
            var missing = Clone(view);
            missing.ItemZones.Items.RemoveAll(item => item.ItemId == current.ActiveDepartment.ItemId); invalidViews.Add(missing);
            var wrong = Clone(view);
            wrong.ItemZones.Relations.Single(r => r.SourceItemId == current.Id && r.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation)
                .TargetItemId = current.Id; invalidViews.Add(wrong);
            var removed = Clone(view);
            removed.ItemZones.Relations.RemoveAll(r => r.SourceItemId == current.Id && r.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation);
            invalidViews.Add(removed);
            foreach (var bad in invalidViews) Assert.That(project(bad).ActiveDepartment, Is.Null, "错误或陈旧关系仍投影为有效当前科室");
            yield return f.Click(ButtonObject(Expansion, "cooperationOpenButton"), "陈旧关系补证_取得正式公开投影");
            var page = Active("EnterpriseSelectionDialogView").Single();
            var original = Clone(Draft(page).Projection);
            var overlay = Overlay(Board(page, current.Id));
            yield return f.Click(OverlayButton(overlay), "移除前查看莱茵整体");
            var next = Clone(original); next.Revision++;
            next.Boards = next.Boards.Where(b => b.Id != current.ActiveDepartment.ItemId).ToArray();
            next.Targets = next.Targets.Where(t => t.BoardId != current.ActiveDepartment.ItemId).ToArray();
            var index = Array.FindIndex(next.Boards, b => b.Id == current.Id); next.Boards[index] = project(removed);
            f.Record("受控移除关系", new { removedItemId = current.ActiveDepartment.ItemId,
                note = "仅给当前窗口注入View派生表现副本，真实Session关系保留" });
            Call(page, "Present", next, false); yield return Settle();
            Assert.That(((GameObject)Read(page, "detailPage")).activeInHierarchy, Is.False, "已移除科室详情仍残留");
            Assert.That(Overlay(Board(page, current.Id)).gameObject.activeInHierarchy, Is.False);
            Assert.That(((Component)Read(page, "enlargedDepartmentOverlay")).gameObject.activeInHierarchy, Is.False);
            Assert.That(Draft(page).Projection.Boards.Any(b => b.ActiveDepartment != null), Is.False);
            yield return f.Capture("受控移除当前科室无旧叠层", page);
            yield return f.Click(ButtonObject(page, "closeButton"), "关闭陈旧关系补证页");
        }

        private IEnumerator CheckThreePlayerColors(GameStateView view, Func<GameStateView, EnterpriseBoardProjection> project)
        {
            var three = Clone(view); three.Players.RemoveAt(three.Players.Count - 1);
            var projected = project(three);
            var page = NewPage(true); var input = Clone(initialDepartment);
            input.RequestId = "controlled-three-player-colors"; input.FixedCompany = projected;
            input.CurrentDepartmentId = projected.ActiveDepartment.ItemId;
            input.Mode = EnterpriseSelectionMode.SwitchDepartment; input.CanCancel = true;
            f.Record("三人颜色补证边界", new { note = "四人真实View副本移除一名玩家，仅验证颜色槽显隐，不声称三人正式开局", projected.PlayerIds });
            Call(page, "Present", input, false); yield return Settle();
            CheckLevels((Component)Read(page, "fixedCompany"), projected);
            Assert.That(projected.PlayerIds.Length, Is.EqualTo(3));
            yield return f.Capture("受控三人等级与当前叠层", page);
            Call(page, "ClearPresentation"); yield return Settle();
        }

        private IEnumerator CheckCompanyAndEffectRegression(GameStateView view)
        {
            var definitions = (EnterpriseDefinitions)Read(Expansion, "Definitions");
            var sources = f.State.ItemZones.Items.Where(item => definitions.FindEnterprise(item.DefinitionId)?.Levels.Any(l => l.Kind == "special") == true)
                .OrderByDescending(item => item.DefinitionId == "enterprise.rhine_lab").Take(5).ToArray();
            Assert.That(sources.Length, Is.EqualTo(5));
            var page = NewPage(false);
            foreach (var mode in new[] { EnterpriseSelectionMode.Company, EnterpriseSelectionMode.Effect })
            {
                var input = new EnterpriseSelectionProjection { RequestId = "controlled-original-mode-" + mode,
                    Mode = mode, CanCancel = true, SubmissionAvailable = true, Revision = 1,
                    Boards = sources.Select(item => (EnterpriseBoardProjection)Call(Expansion, "ProjectBoard", view, item.ItemId, item.DefinitionId)).ToArray() };
                input.Targets = sources.Select(item =>
                {
                    var special = definitions.FindEnterprise(item.DefinitionId).Levels.First(l => l.Kind == "special");
                    var area = (Rect)Call(Expansion, "LevelArea", special.Level);
                    return new EnterpriseSelectionTarget { Id = mode == EnterpriseSelectionMode.Company ? item.ItemId : item.ItemId + ":" + special.AbilityId,
                        BoardId = item.ItemId, CompanyId = item.ItemId, EffectId = special.AbilityId, Tier = special.Level,
                        Available = true, X = area.x, Y = area.y, Width = area.width, Height = area.height };
                }).ToArray();
                int callbacks = 0; EnterpriseSelectionIntent sent = null;
                Call(page, "BindSubmission", (Func<EnterpriseSelectionProjection>)(() => input),
                    (Action<EnterpriseSelectionIntent>)(intent => { callbacks++; sent = intent; }), (Action)(() => Call(page, "ClearPresentation")));
                Call(page, "Present", input, false); yield return Settle();
                f.Record("原Company与Effect组件回归边界", new { mode = mode.ToString(), input.Targets,
                    note = "真实实例和正式Kind=special定义构造五板组件输入，只验原命中/滚轮/提交意图，不声称费用或正式规则请求覆盖" });
                Assert.That(page.GetComponentsInChildren(T("EnterpriseDepartmentOverlayView"), false), Is.Empty,
                    "本轮叠层侵入原Company/Effect候选命中区");
                yield return f.Click(Target(page, input.Targets[0].Id), "原模式首项选择");
                var scroll = (ScrollRect)Read(page, "candidateScroll");
                Assert.That(scroll.content.rect.width, Is.GreaterThan(scroll.viewport.rect.width));
                Assert.That(scroll.horizontalScrollbar.gameObject.activeInHierarchy, Is.True, "关闭浏览横条误伤原多候选模式");
                var before = scroll.content.anchoredPosition; var last = Target(page, input.Targets.Last().Id);
                for (var i = 0; i < 80 && !HotseatUiInputDriver.TryPoint(last, out _, out _); i++)
                {
                    var point = ScreenRect(scroll.viewport).center; var hit = Hit(point);
                    var handler = ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject);
                    Assert.That(handler, Is.SameAs(scroll.gameObject));
                    ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current) { position = point,
                        scrollDelta = new Vector2(-1, 0), pointerCurrentRaycast = hit }, ExecuteEvents.scrollHandler);
                    yield return null; yield return null;
                }
                Assert.That(Vector2.Distance(before, scroll.content.anchoredPosition), Is.GreaterThan(1));
                Assert.That(HotseatUiInputDriver.TryPoint(last, out var lastPoint, out _), Is.True);
                yield return f.Click(last, "原模式独立滚轮后末项命中", lastPoint);
                Assert.That(Draft(page).SelectedIds, Is.EqualTo(new[] { input.Targets.Last().Id }));
                yield return f.Capture("原" + mode + "五板横滚与选择", page);
                yield return f.Click(ButtonObject(page, "confirm"), "原模式组件确认一次");
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(sent.Mode, Is.EqualTo(mode));
                Assert.That(sent.TargetId, Is.EqualTo(input.Targets.Last().Id));
                if (mode == EnterpriseSelectionMode.Effect) Assert.That(sent.EffectId, Is.EqualTo(input.Targets.Last().EffectId));
                Call(page, "ClearPresentation"); yield return Settle();
            }
        }
    }
}
