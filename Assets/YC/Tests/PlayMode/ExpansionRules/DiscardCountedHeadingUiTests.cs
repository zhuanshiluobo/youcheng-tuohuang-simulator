using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    [Timeout(600000)]
    public sealed class DiscardCountedHeadingUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private EffectRegistry registry;
        private object hand;
        private Font wide;
        private static IEnumerator Noop(Component _) { yield break; }
        [UnityTest] public IEnumerator SampleScene_CountsAndBothCachedHeadingsKeepFontRoles() => Verify(4);
        [UnityTest] public IEnumerator ThreePlayerScene_CountsAndBothCachedHeadingsKeepFontRoles() => Verify(3);

        private IEnumerator Verify(int players)
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(players, Noop);
            hand = Read(f.Hud, "CharacterHandPanel");
            registry = (EffectRegistry)Read(((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>()
                .OfType<AnswerInteractionCommandHandler>().Single(), "registry");
#if UNITY_EDITOR
            wide = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>("Assets/YC/Presentation/CommonUi/Fonts/Novecento wide Normal Regular.ttf");
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(UnityEditor.AssetDatabase.GetAssetPath(wide)), Is.EqualTo("0a6c131175d7afb4daca1a87a679f835"));
#else
            wide = Resources.FindObjectsOfTypeAll<Font>().Single(font => font.name == "Novecento wide Normal Regular");
#endif
            Assert.That(wide, Is.Not.Null);
            CheckHudNumbers();
            int player = (int)Read(f.Gameplay, "localPlayerId");
            var discard = FindZone(f.State, "discard", player).ZoneId;
            var originalHand = FindZone(f.State, "hand", player).ZoneId;
            Assert.That(f.State.ItemZones.Items.Any(i => i.ZoneId == discard), Is.False, "空弃牌前置必须来自正式开局");
            yield return OpenAndCheck(0, false);
            var emptyShell = CurrentShell();
            yield return CloseBackground(emptyShell);
            yield return OrdinaryHeadingOnSameCachedShell(false, emptyShell);

            var cards = f.State.ItemZones.Items.Where(i => i.ZoneId == originalHand)
                .Where(i => Call(Read(hand, "cardVisualCatalog"), "GetCharacterFront", i.DefinitionId) != null).Take(2).ToArray();
            Assert.That(cards.Length, Is.EqualTo(2), "非空分支只能取正式真实卡，不能填占位图");
            Move(cards[0].ItemId, originalHand, discard, player);
            yield return OpenAndCheck(1, true);
            var pickerShell = CurrentShell();
            Move(cards[1].ItemId, originalHand, discard, player);
            yield return Settle(); CheckCounted(pickerShell, 2);
            Assert.That(CurrentShell(), Is.SameAs(pickerShell), "数量变化不应另造显示壳");
            yield return f.Capture("非空弃牌数量更新", pickerShell);
            RefreshFonts(); yield return Settle(); CheckCounted(pickerShell, 2); CheckHudNumbers();
            yield return CloseBackground(pickerShell);
            yield return OrdinaryHeadingOnSameCachedShell(true, pickerShell);

            var covered = (Component)Read(Read(f.Hud, "MainModules"), "coveredPile");
            var coverButton = covered.GetComponentsInChildren<Button>().FirstOrDefault(b => b.IsInteractable());
            Assert.That(coverButton, Is.Not.Null, "盖牌浏览必须有实际牌背入口");
            yield return f.Click(coverButton.gameObject, "实际盖牌牌背浏览");
            var model = Read(hand, "currentViewModel");
            var coveredCount = ((IEnumerable)Read(model, "CoveredCards")).Cast<object>().Count();
            var coveredShell = CurrentShell(); CheckCounted(coveredShell, coveredCount);
            Assert.That(coveredShell, Is.SameAs(pickerShell), "盖牌与弃牌应复用本手牌面板的卡牌壳");
            yield return f.Capture("盖牌共享混排标题", coveredShell);
            yield return CloseBackground(coveredShell);
            Move(cards[0].ItemId, discard, originalHand, player);
            Move(cards[1].ItemId, discard, originalHand, player);
            yield return OpenAndCheck(0, false);
            Assert.That(CurrentShell(), Is.SameAs(emptyShell));

            // 仅推进一个正式行动窗口，检验切玩家时关闭旧私有页面；不是八回合回归。
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId)));
            string slot = influence.GetLegalPlacementSlotIds(f.State, player).First();
            foreach (var command in new[] {
                new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.DeployInfluence, PlayerId = player, TargetId = slot },
                new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.EndAction, PlayerId = player } })
            {
                var result = f.Session.Submit(command);
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
                Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
            }
            yield return Wait(() => (int)Read(f.Gameplay, "localPlayerId") != player, "正式行动结束后没有切换热座视角");
            Assert.That((bool)Read(hand, "IsDiscardPreviewOpen"), Is.False, "旧玩家弃牌窗口泄漏到下一玩家");
            int nextPlayer = (int)Read(f.Gameplay, "localPlayerId");
            int nextCount = f.State.ItemZones.Items.Count(i => i.ZoneId == FindZone(f.State, "discard", nextPlayer).ZoneId);
            yield return OpenAndCheck(nextCount, nextCount > 0);
            yield return f.Capture("切玩家后新计数与字体", CurrentShell());
            yield return CloseBackground(CurrentShell());
            f.Record("混排标题覆盖边界", new { players, player, nextPlayer,
                note = "数量前置用正式 MoveSpec；普通标题同壳用组件入口；页面打开／盖牌／关闭均为真实指针，切玩家用正式 DeployInfluence 与 EndAction" });
        }

        private void CheckHudNumbers()
        {
            var count = (Text)Read(Read(f.Hud, "MainModules"), "DiscardCountText");
            Assert.That(count.font, Is.SameAs(wide));
            var headers = (GameObject)Read(Read(f.Hud, "ExpansionUi"), "cooperationLevelHeaders");
            var levels = headers.GetComponentsInChildren<Text>(true).Where(t => t.name.StartsWith("Cooperation Level ", StringComparison.Ordinal)).ToArray();
            Assert.That(levels.Length, Is.EqualTo(6));
            foreach (var text in levels) Assert.That(text.font, Is.SameAs(wide), text.name);
            f.Record("实际 HUD 七处数字字体", new { count = count.font.name, levels = levels.Select(t => new { t.name, font = t.font.name }).ToArray() });
        }

        private Component CurrentShell() => (Component)Read(Read(hand, "discardPage"), "view");
        private IEnumerator OpenAndCheck(int count, bool picker)
        {
            using (var submits = new RuleSubmitCounter())
            {
                yield return f.Click(ButtonObject(Read(f.Hud, "MainModules"), "discardPreviewButton"), "实际弃牌列表入口");
                Assert.That(submits.Calls, Is.Zero);
            }
            var shell = CurrentShell();
            Assert.That((bool)Read(shell, "IsCardPicker"), Is.EqualTo(picker));
            CheckCounted(shell, count);
            yield return f.Capture(picker ? "真实非空弃牌混排标题" : "真实空弃牌混排标题", shell);
        }

        private void CheckCounted(Component shell, int count)
        {
            var title = (Text)Read(shell, "TitleText");
            var root = (RectTransform)Read(shell, "countedTitleRoot");
            var prefix = (Text)Read(shell, "countedTitlePrefix");
            var number = (Text)Read(shell, "countedTitleNumber");
            var suffix = (Text)Read(shell, "countedTitleSuffix");
            Assert.That(root.gameObject.activeInHierarchy, Is.True);
            Assert.That(title.enabled, Is.False, "原标题不得与三段重复绘制");
            Assert.That(title.gameObject.activeInHierarchy, Is.True, "不能停用原标题父对象连带隐藏三段");
            Assert.That(number.text, Is.EqualTo(count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(number.font, Is.SameAs(wide));
            Assert.That(prefix.font, Is.SameAs(title.font)); Assert.That(suffix.font, Is.SameAs(title.font));
            Assert.That(prefix.text + number.text + suffix.text, Is.EqualTo(title.text), "分段必须沿用资产格式，不得改写正文");
            foreach (var text in new[] { prefix, number, suffix })
            {
                Assert.That(text.enabled && text.gameObject.activeInHierarchy, Is.True);
                text.font.RequestCharactersInTexture(text.text, text.fontSize, text.fontStyle);
                foreach (char character in text.text.Where(c => !char.IsWhiteSpace(c)))
                    Assert.That(text.font.HasCharacter(character), Is.True, "缺少实际标题字形：" + character);
                Assert.That(text.GetComponentsInParent<CanvasGroup>().All(g => g.alpha > 0), Is.True, "标题祖先透明度导致文字不可见");
            }
        }

        private IEnumerator OrdinaryHeadingOnSameCachedShell(bool picker, Component expected)
        {
            var cache = Read(hand, "discardPage");
            var profile = Read(f.Registry, "EffectDialogLayoutProfile");
            Call(cache, "PreparePage", (RectTransform)((Component)hand).transform, "Acceptance Plain Selection", "Acceptance Plain Panel",
                Read(profile, "SelectionPanelSize"), Read(profile, "CharacterPanelPosition"), true, false, true, picker);
            var shell = CurrentShell(); Assert.That(shell, Is.SameAs(expected));
            string title = (string)Read(profile, "SelectionEmptyText");
            Call(shell, "ConfigureHeading", title, string.Empty, 70f, "Title", "Description", 27);
            yield return Settle();
            Assert.That(((Text)Read(shell, "TitleText")).enabled, Is.True);
            Assert.That(((Text)Read(shell, "TitleText")).text, Is.EqualTo(title));
            Assert.That(((RectTransform)Read(shell, "countedTitleRoot")).gameObject.activeSelf, Is.False);
            foreach (var field in new[] { "countedTitlePrefix", "countedTitleNumber", "countedTitleSuffix" })
                Assert.That(((Text)Read(shell, field)).text, Is.Empty);
            yield return f.Capture("同一缓存壳恢复普通标题_" + picker, shell);
            Call(cache, "Hide"); yield return Settle();
            f.Record("同壳普通标题组件补证", new { picker, instance = shell.GetInstanceID(), note = "复用实际运行实例与正式 PreparePage／ConfigureHeading，不冒称另一业务请求的鼠标入口" });
        }

        private void Move(string id, string source, string target, int player)
        {
            Assert.That(f.Request, Is.Null);
            var engine = new EffectTreeExecutor(f.State, registry);
            engine.CreateRoot(MoveSpec(player, new[] { id }, source, target, "unordered", 1, "core.character_card_transfer"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            f.Record("弃牌数量受控前置", new { id, source, target, note = "正式 MoveSpec 搬运本局真实卡；不改 ViewModel 或直接写牌位置" });
        }

        private static void RefreshFonts() => T("FontUtility").GetMethod("RefreshManagedTextRenderers", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { true });

        private IEnumerator CloseBackground(Component shell)
        {
            var overlay = (Image)Read(shell, "OverlayImage");
            var panel = ScreenRect((RectTransform)Read(shell, "Panel")); var area = ScreenRect(overlay.rectTransform);
            var points = new[] { new Vector2(area.xMin + 2, area.center.y), new Vector2(area.xMax - 2, area.center.y),
                new Vector2(area.center.x, area.yMin + 2), new Vector2(area.center.x, area.yMax - 2) };
            var point = points.FirstOrDefault(p => !panel.Contains(p) && ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(p).gameObject) ==
                ExecuteEvents.GetEventHandler<IPointerClickHandler>(overlay.gameObject));
            Assert.That(point, Is.Not.EqualTo(Vector2.zero), "实际背景关闭区域不可达");
            yield return f.Click(overlay.gameObject, "实际背景关闭列表", point);
            Assert.That((bool)Read(hand, "IsDiscardPreviewOpen"), Is.False);
        }
    }
}
