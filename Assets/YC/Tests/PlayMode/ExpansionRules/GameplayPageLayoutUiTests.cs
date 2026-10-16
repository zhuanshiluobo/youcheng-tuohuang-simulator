using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Presentation.Workflows;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using Object = UnityEngine.Object;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 两条场景链覆盖第二轮页面问题；组件多候选补证单独标记，不替代实际科室开局。
    [Timeout(600000)]
    public sealed class GameplayPageLayoutUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private EnterpriseSelectionProjection realDepartment;
        private readonly List<GameObject> temporaryPages = new List<GameObject>();
        private static readonly Dictionary<string, float> ApprovedMultipliers = new Dictionary<string, float>
        {
            { "aa1b97a5365d1bc43a2347ecf9652512", 2.5f },
            { "b4ca9e75c1cae354aa1b2ce17d12d467", 1f },
            { "60da9888730f47940a4996898a80b18c", 2.5f },
            { "f74fdea60ee0c6d45806b6411416ba4a", 2.5f },
            { "fb5d9b0fc274099428281871dd10c6a8", 1f },
            { "d914be6582d14b14785a040bfc72f931", 1f },
            { "2d606f464c58709488217ca9ee5fe94e", 1f },
            { "3ec57d85cf4f03b4db6735e85da99df3", 1f }
        };

        [UnityTest] public IEnumerator ThreePlayerScene_SecondPageReviewUsesActualEntries() => Verify(3);
        [UnityTest] public IEnumerator SampleScene_SecondPageReviewUsesActualEntries() => Verify(4);

        [UnityTest]
        public IEnumerator SampleScene_EnterpriseDescriptionReachesLastParagraph()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, page => Settle(), "enterprise.rhodes_island");
            yield return f.Resolution(Sizes[0]);
            var revision = f.State.EffectRuntime.StateRevision;
            using (var counter = new RuleSubmitCounter())
            {
                yield return f.Click(ButtonObject(Read(f.Hud, "ExpansionUi"), "cooperationOpenButton"),
                    "长文补证_正式企业浏览入口");
                var page = Active("EnterpriseSelectionDialogView").Single();
                var draft = (EnterpriseSelectionDraft)Read(page, "Draft");
                Assert.That(draft.Projection.ReadOnly, Is.True);
                var selection = draft.SelectedIds.ToArray();
                var scroll = (ScrollRect)Read(page, "descriptionScroll");
                var description = (Text)Read(page, "description");
                EnterpriseSelectionTarget chosen = null;
                // 只选当前实际公开的企业；优先罗德岛，不足以溢出时再选真实最长内容。
                foreach (var target in draft.Projection.Targets
                    .OrderByDescending(t => f.State.ItemZones.Items.Any(item =>
                        item.ItemId == t.CompanyId && item.DefinitionId == "enterprise.rhodes_island"))
                    .ThenByDescending(t => (t.Description ?? string.Empty).Length))
                {
                    yield return f.Click(Target(page, target.Id), "长文补证_查看真实企业" + target.Label);
                    if (scroll.content.rect.height > scroll.viewport.rect.height + 1f)
                    { chosen = target; break; }
                }
                Assert.That(chosen, Is.Not.Null, "当前公开企业正文均未溢出，不能声称验证了滚动到底");
                yield return ScrollEnterpriseDescription(scroll, 1);
                Assert.That(scroll.verticalNormalizedPosition, Is.GreaterThanOrEqualTo(.999f), "正文未到顶部");
                var before = scroll.content.anchoredPosition;
                var candidateBefore = ((ScrollRect)Read(page, "candidateScroll")).content.anchoredPosition;
                yield return f.Capture("企业长文_滚动前", page);
                var lastParagraph = description.text.Split('\n').Last(s => !string.IsNullOrWhiteSpace(s)).Trim();
                string Compact(string value) => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
                var segments = description.GetComponentsInChildren<Text>(false)
                    .Where(t => t != description && t.enabled && !string.IsNullOrWhiteSpace(t.text)).ToArray();
                Assert.That(Compact(string.Concat(segments.Select(t => t.text))),
                    Does.EndWith(Compact(lastParagraph)), "渲染分段未包含真实正文末段");
                var lastSegments = new List<Text>();
                var remaining = Compact(lastParagraph).Length;
                for (var i = segments.Length - 1; i >= 0 && remaining > 0; i--)
                { lastSegments.Add(segments[i]); remaining -= Compact(segments[i].text).Length; }
                Assert.That(lastSegments, Is.Not.Empty);
                var firstTail = ScreenRect(lastSegments[0].rectTransform);
                var viewportBefore = ScreenRect(scroll.viewport);
                Assert.That(firstTail.yMin, Is.LessThan(viewportBefore.yMin), "末段起初已可见，未覆盖长文遮罩风险");
                yield return ScrollEnterpriseDescription(scroll, -1);
                yield return Settle();
                var after = scroll.content.anchoredPosition;
                var viewport = ScreenRect(scroll.viewport);
                f.Record("企业长文末段滚轮证据", new { chosen.Id, chosen.CompanyId, chosen.Label,
                    definitionId = f.State.ItemZones.Items.Single(item => item.ItemId == chosen.CompanyId).DefinitionId,
                    description = description.text, lastParagraph,
                    before = new[] { before.x, before.y }, after = new[] { after.x, after.y },
                    normalizedPosition = scroll.verticalNormalizedPosition,
                    viewport = Geometry(scroll.viewport), content = Geometry(scroll.content),
                    lastParagraphSegments = lastSegments.AsEnumerable().Reverse().Select(t => new { t.text,
                        geometry = Geometry(t.rectTransform) }).ToArray(),
                    ruleSubmitCalls = counter.Calls, selectionBefore = selection, selectionAfter = draft.SelectedIds });
                yield return f.Capture("企业长文_末段实际可见", page);
                Assert.That(Vector2.Distance(before, after), Is.GreaterThan(1f), "独立滚轮没有移动正文Content");
                Assert.That(scroll.verticalNormalizedPosition, Is.LessThanOrEqualTo(.001f), "正文未滚至底部");
                foreach (var segment in lastSegments)
                {
                    var rect = ScreenRect(segment.rectTransform);
                    Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(viewport.xMin - 1f));
                    Assert.That(rect.xMax, Is.LessThanOrEqualTo(viewport.xMax + 1f));
                    Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(viewport.yMin - 1f));
                    Assert.That(rect.yMax, Is.LessThanOrEqualTo(viewport.yMax + 1f));
                }
                Assert.That(((ScrollRect)Read(page, "candidateScroll")).content.anchoredPosition,
                    Is.EqualTo(candidateBefore), "右栏滚轮误移动了企业候选");
                Assert.That(draft.SelectedIds, Is.EqualTo(selection), "浏览或滚动误改候选选择");
                Assert.That(page.gameObject.activeInHierarchy, Is.True);
                yield return f.Click(ButtonObject(page, "closeButton"), "长文补证_顶部关闭");
                Assert.That(page.gameObject.activeInHierarchy, Is.False);
                Assert.That(f.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
                Assert.That(counter.Calls, Is.Zero, "企业浏览、滚轮与关闭不应提交规则");
                f.Record("企业长文补证完成", new { ruleSubmitCalls = counter.Calls, revision,
                    note = "正式四人新局，实际公开企业，独立正文滚轮，无伪文本或状态覆盖" });
            }
        }

        private IEnumerator ScrollEnterpriseDescription(ScrollRect scroll, int direction)
        {
            for (var i = 0; i < 100; i++)
            {
                if (direction > 0 && scroll.verticalNormalizedPosition >= .999f ||
                    direction < 0 && scroll.verticalNormalizedPosition <= .001f) yield break;
                var point = ScreenRect(scroll.viewport).center;
                var hit = Hit(point);
                var handler = ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject);
                f.Record("企业正文独立滚轮命中", new { direction, point = new[] { point.x, point.y },
                    hit = HotseatUiInputDriver.PathOf(hit.gameObject), handler = HotseatUiInputDriver.PathOf(handler) });
                Assert.That(handler, Is.EqualTo(scroll.gameObject), "正文滚轮被其他区域截获");
                ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current) { position = point,
                    scrollDelta = new Vector2(0, direction), pointerCurrentRaycast = hit }, ExecuteEvents.scrollHandler);
                yield return null; yield return null;
            }
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            foreach (var page in temporaryPages) if (page != null) Object.Destroy(page);
            temporaryPages.Clear();
            yield return null;
        }

        private IEnumerator Verify(int players)
        {
            realDepartment = null;
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(players, CheckRealDepartment);
            f.Record("实际Registry来源", new { enterprise = Asset(Read(f.Registry, "EnterpriseSelectionPrefab")),
                department = Asset(Read(f.Registry, "DepartmentSelectionPrefab")),
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path });
            foreach (var size in Sizes)
            {
                yield return f.Resolution(size);
                CheckFrames((Component)f.Hud);
                yield return CheckRealCityStyles();
                yield return CheckCooperationAndEnterprise();
                yield return CheckDiscard(size == Sizes[0] ? 10 : 1);
            }
            if (realDepartment != null) yield return CheckDepartmentComponents();
            else f.Record("本局科室组件补证未执行", new { players,
                note = "本局没有真实科室投影；四人链承担真实科室与2/3/5补证，本局不伪造" });
            f.Record("本轮功能链完成", new { players, note = "自动断言完成；实际画面仍须人工逐图检查，未作性能结论" });
        }

        private IEnumerator CheckRealDepartment(Component page)
        {
            realDepartment = JsonConvert.DeserializeObject<EnterpriseSelectionProjection>(JsonConvert.SerializeObject(
                ((EnterpriseSelectionDraft)Read(page, "Draft")).Projection));
            Assert.That(realDepartment.Targets.Length, Is.EqualTo(3));
            Assert.That(realDepartment.CanCancel, Is.False);
            foreach (var size in Sizes)
            {
                yield return f.Resolution(size);
                yield return f.Capture("实际科室3候选", page); // 先记几何、遮罩、图片，再作断言，失败仍有证据。
                CheckDepartmentPlayerLevels(page);
                AssertBounds(page);
                AssertSharedSizeInput(page);
                CheckBoardArtwork(page);
                Assert.That(((Button)Read(page, "cancel")).gameObject.activeInHierarchy, Is.False);
                CheckFrames(page);
            }
            var before = ((EnterpriseSelectionDraft)Read(page, "Draft")).SelectedIds.ToArray();
            using (var submitCounter = new RuleSubmitCounter())
            {
                var fixedCompany = (Component)Read(page, "fixedCompany");
                Assert.That(fixedCompany, Is.Not.Null, "科室页缺少左侧固定莱茵企业板");
                ThirdRoundUiAcceptanceChecks.Department(page, f);
                yield return ThirdRoundUiAcceptanceChecks.FixedBoardRejectsGestures(page, f);
                Assert.That(((EnterpriseSelectionDraft)Read(page, "Draft")).SelectedIds, Is.EqualTo(before));
                yield return f.Click(Target(page, realDepartment.Targets[0].Id), "真实科室右键查看原图", null,
                    PointerEventData.InputButton.Right);
                Assert.That(((GameObject)Read(page, "detailPage")).activeInHierarchy, Is.True);
                yield return f.Click((GameObject)Read(page, "detailsReturnSurface"), "科室详情单击屏幕返回");
                Assert.That(((GameObject)Read(page, "detailPage")).activeInHierarchy, Is.False);
                Assert.That(((EnterpriseSelectionDraft)Read(page, "Draft")).SelectedIds, Is.EqualTo(before));
                Assert.That(((EnterpriseSelectionDraft)Read(page, "Draft")).Projection.RequestId, Is.EqualTo(realDepartment.RequestId));
                Assert.That(submitCounter.Calls, Is.Zero, "科室或固定莱茵详情查看／返回不应提交规则");
            }
        }

        private IEnumerator CheckRealCityStyles()
        {
            var ids = f.State.Decks.CityStyleSupply.ToArray();
            Assert.That(ids.Length, Is.EqualTo(6), "本轮真实企业配方必须实际产生6张样式，不能用HUD假数据补足");
            yield return f.Click(HotseatUiInputDriver.Marker("action.tab.2"), "城市样式页签");
            var rows = ((Component)f.Hud).GetComponentsInChildren(T("CityStyleStatusRowView"), false).Cast<Component>()
                .Where(row => !string.IsNullOrEmpty((string)Read(row, "Id"))).ToArray();
            Assert.That(rows.Select(row => (string)Read(row, "Id")), Is.EquivalentTo(ids));
            var panelView = ((Component)f.Hud).GetComponentsInChildren(T("BuildInfoPanelView"), true).Cast<Component>().Single();
            foreach (var template in (Array)Read(panelView, "cityStyleRowTemplates"))
                Assert.That(Asset(template), Does.Contain("/CityStyles/"), "HUD状态行未接独立可复用Prefab");
            yield return f.Capture("实际HUD六样式", (Component)f.Hud);
            yield return f.Click(HotseatUiInputDriver.Marker("action.declare"), "正式宣告样式入口");
            var page = Active("CityStyleDeclarationPreviewView").Single();
            yield return f.Capture("实际样式列表_输入前", page);
            var cards = ((IEnumerable)Read(page, "options")).Cast<Component>().ToArray();
            Assert.That(cards.Select(c => c.name), Is.EqualTo(ids), "正式页面供应ID或顺序缺失");
            var runtimeTemplate = (Component)Read(page, "optionTemplate");
            var registeredPage = (Component)Read(f.Registry, "cityStyleDeclarationPreviewPrefab");
            var registeredTemplate = (Component)Read(registeredPage, "optionTemplate");
            Assert.That(Asset(registeredTemplate), Does.EndWith("/CityStyleCardItem.prefab"));
#if UNITY_EDITOR
            // Object.Instantiate产生的运行时窗口没有资产连接；用实际Registry源及克隆内对应层级核验。
            var templatePath = UnityEditor.AnimationUtility.CalculateTransformPath(registeredTemplate.transform, registeredPage.transform);
            Assert.That(runtimeTemplate.transform, Is.SameAs(page.transform.Find(templatePath)));
            Assert.That(runtimeTemplate.GetType(), Is.EqualTo(registeredTemplate.GetType()));
            Assert.That(runtimeTemplate.gameObject.activeSelf, Is.False);
            f.Record("样式卡模板来源与运行时对应", new { registryPage = Asset(registeredPage),
                independentTemplate = Asset(registeredTemplate), templatePath,
                runtimePath = HotseatUiInputDriver.PathOf(runtimeTemplate.gameObject),
                runtimeAssetPath = UnityEditor.AssetDatabase.GetAssetPath(runtimeTemplate),
                note = "实际Registry通过Object.Instantiate建立窗口，运行时克隆资产路径为空是正常行为" });
#endif
            var catalog = Read(Read(f.Hud, "CharacterHandPanel"), "cardVisualCatalog");
            var identities = new HashSet<string>();
            foreach (var card in cards)
            {
                var raw = (RawImage)Read(card, "RawImage");
                var expected = (Sprite)Call(catalog, "GetCityStyle", card.name);
                Assert.That(expected, Is.Not.Null, "真实样式卡面未注册：" + card.name);
                Assert.That(raw.texture, Is.SameAs(expected.texture));
                Assert.That(raw.gameObject.activeInHierarchy, Is.True);
                var sr = expected.textureRect;
                // 既有CardArtworkView将采样限制到首尾像素中心，避免采到相邻卡面。
                var uv = new Rect((sr.x + .5f) / expected.texture.width, (sr.y + .5f) / expected.texture.height,
                    (sr.width - 1f) / expected.texture.width, (sr.height - 1f) / expected.texture.height);
                f.Record("本局样式图像取样", new { id = card.name,
                    sourcePixels = new[] { sr.x, sr.y, sr.width, sr.height },
                    actualUv = new[] { raw.uvRect.x, raw.uvRect.y, raw.uvRect.width, raw.uvRect.height },
                    expectedUv = new[] { uv.x, uv.y, uv.width, uv.height } });
                Assert.That(raw.uvRect.x, Is.EqualTo(uv.x).Within(.0001f));
                Assert.That(raw.uvRect.y, Is.EqualTo(uv.y).Within(.0001f));
                Assert.That(raw.uvRect.width, Is.EqualTo(uv.width).Within(.0001f));
                Assert.That(raw.uvRect.height, Is.EqualTo(uv.height).Within(.0001f));
                identities.Add(expected.texture.GetInstanceID() + ":" + sr);
                f.Record("本局样式真实卡面", new { id = card.name, source = Asset(expected),
                    spriteRect = new[] { sr.x, sr.y, sr.width, sr.height }, card = Geometry(raw.rectTransform) });
                yield return f.Click(ButtonObject(card, "Button"), "浏览样式" + card.name);
            }
            Assert.That(identities.Count, Is.EqualTo(6), "六个ID不能共享同一张占位或错误切片");
            yield return f.Capture("真实企业六样式", page);
            AssertBounds(page); CheckFrames(page);
            yield return f.Click(ButtonObject(page, "closeButton"), "关闭城市样式页");
            Assert.That(page == null || !page.gameObject.activeInHierarchy, Is.True,
                "城市样式页关闭后仍可见；合法销毁和隐藏均视为关闭");
        }

        private void CheckDepartmentPlayerLevels(Component page)
        {
            var projection = ((EnterpriseSelectionDraft)Read(page, "Draft")).Projection;
            var board = projection.FixedCompany;
            Assert.That(board, Is.Not.Null);
            Assert.That(board.PlayerIds, Is.Not.Null);
            Assert.That(board.PlayerColors, Is.Not.Null);
            Assert.That(board.PlayerLevels, Is.Not.Null);
            Assert.That(board.PlayerColors.Length, Is.EqualTo(board.PlayerIds.Length));
            Assert.That(board.PlayerLevels.Length, Is.EqualTo(board.PlayerIds.Length));
            Assert.That(board.PlayerIds, Is.EquivalentTo(f.State.Players.Select(player => player.PlayerId)));
            Assert.That(board.PlayerColors.Distinct().Count(), Is.EqualTo(board.PlayerColors.Length));
            var fixedCompany = (Component)Read(page, "fixedCompany");
            ThirdRoundUiAcceptanceChecks.PlayerLevels(fixedCompany, board, f);
            foreach (var candidate in ((IDictionary)Read(page, "items")).Values.Cast<object>())
                Assert.That(((Text[])Read(candidate, "playerLevels")).Any(text => text.gameObject.activeInHierarchy),
                    Is.False, "科室候选重复显示了共享企业等级");
            var viewerOnly = (Text)Read(page, "cooperationLevel");
            Assert.That(viewerOnly == null || !viewerOnly.gameObject.activeInHierarchy, Is.True,
                "全体颜色槽之外仍重复显示本人合作等级");
        }

        private IEnumerator CheckCooperationAndEnterprise()
        {
            var expansion = Read(f.Hud, "ExpansionUi");
            var entry = (Button)Read(expansion, "cooperationOpenButton");
            Assert.That(entry, Is.Not.Null, "合作整区唯一入口缺失");
            for (var x = 0; x < 3; x++)
            for (var y = 0; y < 3; y++)
            {
                var rect = ScreenRect((RectTransform)entry.transform);
                var point = new Vector2(Mathf.Lerp(rect.xMin, rect.xMax, new[] { .04f, .5f, .96f }[x]),
                    Mathf.Lerp(rect.yMin, rect.yMax, new[] { .04f, .5f, .96f }[y]));
                yield return f.Click(entry.gameObject, "合作区域九点" + x + y, point);
                var page = Active("EnterpriseSelectionDialogView").Single();
                Assert.That(((EnterpriseSelectionDraft)Read(page, "Draft")).Projection.ReadOnly, Is.True);
                Assert.That(((Button)Read(page, "cancel")).gameObject.activeInHierarchy, Is.False, "浏览页仍显示底部关闭");
                Assert.That(((Button)Read(page, "confirm")).gameObject.activeInHierarchy, Is.False);
                if (x == 1 && y == 1)
                {
                    yield return f.Capture("真实企业浏览", page);
                    AssertBounds(page); AssertSharedSizeInput(page); CheckFrames(page); CheckBoardArtwork(page);
                    var candidates = ((ScrollRect)Read(page, "candidateScroll")).GetComponent<Image>();
                    var description = ((ScrollRect)Read(page, "descriptionScroll")).GetComponent<Image>();
                    Assert.That(candidates == null || candidates.color.a == 0, Is.True, "企业候选黑底仍可见");
                    Assert.That(description == null || description.color.a == 0, Is.True, "企业正文黑底仍可见");
                    Assert.That(page.GetComponentsInChildren<Image>(true).Any(i => i.name == "Details Frame" && i.sprite != null), Is.True);
                    Assert.That(page.GetComponentsInChildren(T("UiInlineTextLayout"), true), Is.Not.Empty, "企业右栏缺少显式换行布局");
                }
                yield return f.Click(ButtonObject(page, "closeButton"), "浏览页顶部关闭");
                Assert.That(page.gameObject.activeInHierarchy, Is.False);
            }
        }

        private IEnumerator CheckDiscard(int repetitions)
        {
            var hand = Read(f.Hud, "CharacterHandPanel");
            int player = (int)Read(f.Gameplay, "localPlayerId");
            var discardZone = FindZone(f.State, "discard", player);
            Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == discardZone.ZoneId), Is.Zero,
                "空态基线不成立；不能清空真实弃牌列表伪造空态");
            yield return DiscardCycles(hand, repetitions, true);
            var model = Read(hand, "currentViewModel");
            var candidates = ((IEnumerable)Read(model, "HandCards")).Cast<object>();
            var catalog = Read(hand, "cardVisualCatalog");
            var card = candidates.FirstOrDefault(c => Call(catalog, "GetCharacterFront", Call(model,
                "ResolveDefinitionId", (string)Read(c, "CardId"))) != null);
            Assert.That(card, Is.Not.Null, "非空弃牌必须使用本局已注册真实卡面");
            var id = (string)Read(card, "CardId");
            MoveForFixture(player, id, FindZone(f.State, "hand", player).ZoneId, discardZone.ZoneId);
            yield return Settle();
            yield return DiscardCycles(hand, repetitions, false);
            MoveForFixture(player, id, discardZone.ZoneId, FindZone(f.State, "hand", player).ZoneId);
            yield return Settle();
            yield return DiscardCycles(hand, 1, true, false); // 同一缓存壳空→非空→空。
        }

        private void MoveForFixture(int player, string id, string source, string target)
        {
            Assert.That(f.Request, Is.Null);
            var handlers = ((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>();
            var handler = handlers.OfType<AnswerInteractionCommandHandler>().Single();
            var registry = (EffectRegistry)Read(handler, "registry");
            var engine = new EffectTreeExecutor(f.State, registry);
            engine.CreateRoot(MoveSpec(player, new[] { id }, source, target, "unordered", 1, "core.character_card_transfer"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            f.Record("受控取态_正式原生角色转移", new { player, id, source, target,
                note = "仅制造本轮空非空分支；页面本身仍从HUD指针打开，未改视图模型或资产" });
        }

        private IEnumerator DiscardCycles(object hand, int repetitions, bool empty, bool capture = true)
        {
            int? nodes = null;
            for (var cycle = 0; cycle < repetitions; cycle++)
            {
                yield return f.Click(ButtonObject(Read(f.Hud, "MainModules"), "discardPreviewButton"), "弃牌正式入口");
                var shell = Read(hand, "discardPage"); var page = (Component)Read(shell, "view");
                bool picker = (bool)Read(page, "IsCardPicker");
                // 在任何布局断言之前写下空态/卡牌壳分支、Frame和Content的完整世界角点与遮罩链。
                if (cycle == 0 && capture) yield return f.Capture(empty ? "实际空弃牌" : "实际非空弃牌", page);
                var texts = page.GetComponentsInChildren<Transform>(true).Count(t => t.name == "Selection Empty");
                var total = page.GetComponentsInChildren<Transform>(true).Length;
                f.Record("弃牌重复开关", new { cycle = cycle + 1, empty, picker, texts, total,
                    panel = Geometry((RectTransform)Read(page, "Panel")), content = Geometry((RectTransform)Read(page, "OptionContent")) });
                Assert.That(texts, Is.EqualTo(empty ? 1 : 0));
                Assert.That(picker, Is.EqualTo(!empty), "有真实卡面与空态应分别验证实际不同壳层");
                if (nodes == null) nodes = total;
                Assert.That(total, Is.EqualTo(nodes.Value), "隐藏的动态节点随开关次数增长");
                if (!empty)
                {
                    var content = (RectTransform)Read(page, "OptionContent");
                    Assert.That(content.GetComponentsInChildren<Image>().Any(i => i.sprite != null) ||
                        content.GetComponentsInChildren<RawImage>().Any(i => i.texture != null), Is.True, "非空弃牌未显示真实图像");
                }
                AssertBounds(page); CheckFrames(page);
                yield return CloseBackground(page);
                Assert.That((bool)Read(hand, "IsDiscardPreviewOpen"), Is.False);
                yield return null; // Destroy队列必须跨帧清完。
            }
        }

        private IEnumerator CloseBackground(Component page)
        {
            var overlay = (Image)Read(page, "OverlayImage");
            var panel = ScreenRect((RectTransform)Read(page, "Panel"));
            var area = ScreenRect(overlay.rectTransform);
            var points = new[] { new Vector2(area.xMin + 2, area.center.y), new Vector2(area.xMax - 2, area.center.y),
                new Vector2(area.center.x, area.yMax - 2), new Vector2(area.center.x, area.yMin + 2) };
            var point = points.FirstOrDefault(p => !panel.Contains(p) &&
                ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(p).gameObject) ==
                ExecuteEvents.GetEventHandler<IPointerClickHandler>(overlay.gameObject));
            Assert.That(point, Is.Not.EqualTo(Vector2.zero), "弃牌页没有可达的背景关闭区域");
            yield return f.Click(overlay.gameObject, "弃牌背景关闭", point);
        }

        private IEnumerator CheckDepartmentComponents()
        {
            yield return f.Resolution(Sizes[1]);
            var page = (Component)Call(f.Registry, "InstantiateEnterpriseSelection", f.Host, true);
            temporaryPages.Add(page.gameObject);
            foreach (var count in new[] { 2, 3, 5 })
            {
                var p = JsonConvert.DeserializeObject<EnterpriseSelectionProjection>(JsonConvert.SerializeObject(realDepartment));
                p.RequestId = "component-department-" + count; p.Mode = EnterpriseSelectionMode.SwitchDepartment;
                p.CanCancel = true; p.SubmissionAvailable = true; p.Revision = count;
                p.Boards = Enumerable.Range(0, count).Select(i => new EnterpriseBoardProjection {
                    Id = "component-board-" + i, VisualKey = realDepartment.Boards[i % realDepartment.Boards.Length].VisualKey,
                    Label = "组件候选" + (i + 1) }).ToArray();
                p.Targets = p.Boards.Select((b, i) => new EnterpriseSelectionTarget { Id = b.Id, BoardId = b.Id,
                    DepartmentId = b.Id, Available = i != 0, UnavailableReason = i == 0 ? "组件补证不可选原因" : null }).ToArray();
                int submitted = 0;
                Call(page, "BindSubmission", (Func<EnterpriseSelectionProjection>)(() => p),
                    (Action<EnterpriseSelectionIntent>)(_ => submitted++), (Action)(() => Call(page, "ClearPresentation")));
                Call(page, "Present", p, false);
                yield return Settle();
                f.Record("科室组件补证边界", new { count, note = "实际Registry与科室资产；受控投影及重复原板，仅验2/3/5布局与交互，不计作真实规则候选" });
                if (count != 3) yield return f.Capture("组件科室" + count + "候选", page);
                AssertBounds(page); CheckBoardArtwork(page); CheckFrames(page);
                yield return f.Click(Target(page, p.Targets[0].Id), "查看不可选科室");
                Assert.That(((EnterpriseSelectionDraft)Read(page, "Draft")).SelectedIds, Is.Empty);
                Assert.That(((Button)Read(page, "confirm")).IsInteractable(), Is.False);
                if (count == 5)
                    yield return ScrollLastDepartmentWithoutClickRescue(page, p.Targets[count - 1].Id);
                yield return f.Click(Target(page, p.Targets[count - 1].Id), "滚至末项并选择可用科室");
                Assert.That(((EnterpriseSelectionDraft)Read(page, "Draft")).SelectedIds, Is.EqualTo(new[] { p.Targets[count - 1].Id }));
                yield return f.Click(ButtonObject(page, "confirm"), "组件科室确认");
                Assert.That(submitted, Is.EqualTo(1));
                Call(page, "ClearPresentation");
                yield return null;
            }
        }

        private IEnumerator ScrollLastDepartmentWithoutClickRescue(Component page, string lastId)
        {
            var scroll = (ScrollRect)Read(page, "CandidateScroll");
            Assert.That(scroll.content.rect.width, Is.GreaterThan(scroll.viewport.rect.width), "5候选未形成横滚区域");
            var start = scroll.content.anchoredPosition;
            // 独立滚轮链：移动断言与末项原始Raycast必须先通过，不能由后续Click自动滚动救援。
            for (var i = 0; i < 40; i++)
            {
                var point = ScreenRect(scroll.viewport).center;
                var hit = Hit(point);
                var handler = ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject);
                Assert.That(handler, Is.EqualTo(scroll.gameObject), "科室候选未接收真实滚轮");
                ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current) { position = point,
                    scrollDelta = new Vector2(-1, 0), pointerCurrentRaycast = hit }, ExecuteEvents.scrollHandler);
                yield return null; yield return null;
                if (Vector2.Distance(start, scroll.content.anchoredPosition) > 1 &&
                    HotseatUiInputDriver.TryPoint(Target(page, lastId), out _, out _)) break;
            }
            var finish = scroll.content.anchoredPosition;
            Assert.That(Vector2.Distance(start, finish), Is.GreaterThan(1), "滚轮未移动Content");
            Assert.That(HotseatUiInputDriver.TryPoint(Target(page, lastId), out var lastPoint, out var lastHit), Is.True,
                "独立滚轮未让末项实际可达，禁止Click自动救援");
            f.Record("科室5候选独立滚轮证据", new { before = new[] { start.x, start.y }, after = new[] { finish.x, finish.y },
                point = new[] { lastPoint.x, lastPoint.y }, hit = HotseatUiInputDriver.PathOf(lastHit.gameObject) });
        }

        private void AssertSharedSizeInput(Component page)
        {
            var reference = (Component)Read(f.Registry, "cityStyleDeclarationPreviewPrefab");
            // 窗口还包含内部警告框；按被驱动的主Panel选取，不能假定整棵树只有一个尺寸输入。
            var expected = reference.GetComponentsInChildren(T("UiWindowSizeInput"), true).Cast<Component>()
                .Single(input => Read(input, "panel") == Read(reference, "panel"));
            var actual = page.GetComponentsInChildren(T("UiWindowSizeInput"), true).Cast<Component>()
                .Single(input => Read(input, "panel") == Read(page, "panel"));
            var actualRect = ScreenRect((RectTransform)Read(page, "panel"));
            f.Record("主页面实际尺寸与配置输入", new { page = page.name,
                screenRect = new[] { actualRect.x, actualRect.y, actualRect.width, actualRect.height },
                input = HotseatUiInputDriver.PathOf(actual.gameObject),
                note = "配置断言仅补证；大小统一须再比较同场景同分辨率各页实际矩形和截图" });
            Assert.That((Vector2)Read(actual, "PreferredSize"), Is.EqualTo((Vector2)Read(expected, "PreferredSize")),
                "企业／科室未使用已批准的公共页面尺寸输入");
            Assert.That((Vector2)Read(actual, "PreferredOffset"), Is.EqualTo((Vector2)Read(expected, "PreferredOffset")));
        }

        private void AssertBounds(Component page)
        {
            var panel = (RectTransform)Read(page, "panel");
            var outer = ScreenRect(f.Host); var inner = ScreenRect(panel);
            Assert.That(inner.width, Is.GreaterThan(0)); Assert.That(inner.height, Is.GreaterThan(0));
            Assert.That(inner.xMin, Is.GreaterThanOrEqualTo(outer.xMin - 1));
            Assert.That(inner.yMin, Is.GreaterThanOrEqualTo(outer.yMin - 1));
            Assert.That(inner.xMax, Is.LessThanOrEqualTo(outer.xMax + 1));
            Assert.That(inner.yMax, Is.LessThanOrEqualTo(outer.yMax + 1));
            foreach (var frame in panel.Cast<Transform>().Where(t => t.name == "Frame" || t.name == "Window Frame").OfType<RectTransform>())
            {
                var rect = ScreenRect(frame);
                Assert.That(rect.xMin, Is.LessThanOrEqualTo(inner.xMin + 1));
                Assert.That(rect.yMin, Is.LessThanOrEqualTo(inner.yMin + 1));
                Assert.That(rect.xMax, Is.GreaterThanOrEqualTo(inner.xMax - 1));
                Assert.That(rect.yMax, Is.GreaterThanOrEqualTo(inner.yMax - 1));
            }
        }

        private static void CheckBoardArtwork(Component page)
        {
            var boards = page.GetComponentsInChildren(T("EnterpriseBoardItemView"), false).Cast<Component>().ToArray();
            Assert.That(boards, Is.Not.Empty);
            foreach (var board in boards)
            {
                var raw = (RawImage)Read(board, "BoardImage");
                Assert.That(raw.texture, Is.Not.Null, "原板不得使用空占位");
                var sourceRatio = raw.texture.width * raw.uvRect.width / (raw.texture.height * raw.uvRect.height);
                Assert.That(raw.rectTransform.rect.width / raw.rectTransform.rect.height, Is.EqualTo(sourceRatio).Within(.01f),
                    "企业和科室必须分别按原板比例显示");
            }
        }

        private static void CheckFrames(Component page)
        {
#if UNITY_EDITOR
            foreach (var image in page.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite == null) continue;
                var guid = UnityEditor.AssetDatabase.AssetPathToGUID(UnityEditor.AssetDatabase.GetAssetPath(image.sprite));
                if (!ApprovedMultipliers.TryGetValue(guid, out var expected)) continue;
                // 第三轮只修改实际企业/科室页及其picker variant；其他页面沿原资产规范。
                if (image.GetComponentInParent(T("EnterpriseSelectionDialogView")) != null &&
                    (guid == "aa1b97a5365d1bc43a2347ecf9652512" || guid == "60da9888730f47940a4996898a80b18c")) expected = 1f;
                Assert.That(image.pixelsPerUnitMultiplier, Is.EqualTo(expected).Within(.0001f), image.name + " 同GUID倍率不一致");
                Assert.That(image.sprite.pixelsPerUnit, Is.EqualTo(100));
                Assert.That(image.raycastTarget, Is.False, "装饰边框不应拦截输入：" + image.name);
            }
#endif
        }

        private static string Asset(object value)
        {
#if UNITY_EDITOR
            var resource = value as Object;
            if (resource is Component || resource is GameObject)
            {
                var nestedSource = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(resource);
                if (nestedSource != null) resource = nestedSource;
            }
            return UnityEditor.AssetDatabase.GetAssetPath(resource);
#else
            return value?.GetType().Name ?? "";
#endif
        }
    }
}
