using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Presentation.Workflows;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 第三轮明确指定的组件/交互约束；不把其他静态文字、尺寸或装饰锁成规范。
    internal static class ThirdRoundUiAcceptanceChecks
    {
        internal static void PlayerLevels(Component board, EnterpriseBoardProjection source, GameplayPageAcceptanceFixture f)
        {
            var colors = (int[])Read(board, "playerColorOrder");
            var markers = Read(board, "playerLevelMarkers") as Array;
            if (markers == null || markers.Length == 0)
            {
                var texts = (Text[])Read(board, "playerLevels");
                for (var i = 0; i < texts.Length; i++)
                {
                    var at = Array.IndexOf(source.PlayerColors ?? Array.Empty<int>(), colors[i]);
                    var visible = at >= 0 && source.PlayerLevels[at] >= 0;
                    Assert.That(texts[i].gameObject.activeInHierarchy, Is.EqualTo(visible));
                    if (visible) Assert.That(texts[i].text, Is.EqualTo(source.PlayerLevels[at].ToString()));
                }
                return;
            }
            Assert.That(markers.Length, Is.EqualTo(4), "固定莱茵应绑定四个标准玩家标记槽");
            Assert.That(colors.Length, Is.EqualTo(markers.Length));
            var records = new List<object>();
            for (var i = 0; i < markers.Length; i++)
            {
                var marker = (Component)markers.GetValue(i);
                Assert.That(marker, Is.Not.Null);
                var at = Array.IndexOf(source.PlayerColors ?? Array.Empty<int>(), colors[i]);
                var visible = at >= 0 && source.PlayerLevels[at] >= 0;
                Assert.That(marker.gameObject.activeInHierarchy, Is.EqualTo(visible));
                var text = (Text)Read(marker, "NumberText"); var face = (Image)Read(marker, "Face");
                var args = new object[] { null };
                Assert.That((bool)marker.GetType().GetMethod("TryValidateConfiguration").Invoke(marker, args), Is.True, "标准标记引用缺失：" + args[0]);
                if (visible)
                {
                    var expectedColor = new[] { "Red", "Blue", "Green", "Yellow" }[colors[i]];
                    Assert.That(Read(marker, "ColorPreset").ToString(), Is.EqualTo(expectedColor));
                    Assert.That((bool)Read(marker, "ShowNumber"), Is.True);
                    Assert.That(text.text, Is.EqualTo(source.PlayerLevels[at].ToString()));
                    Assert.That(face.sprite, Is.Not.Null);
                    Assert.That(face.raycastTarget, Is.False);
                }
#if UNITY_EDITOR
                // Registry用Object.Instantiate创建的运行时克隆没有PrefabUtility源链接。
                // 源嵌套归属由EditMode合同验证；运行时校验实际标准目录及其当前数字贴图。
                var standard = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/YC/Presentation/Prefabs/Gameplay/Pieces/InfluenceMarker2DNumbered.prefab").GetComponent(T("InfluenceMarker2DView"));
                Assert.That(Read(marker, "Catalog"), Is.SameAs(Read(standard, "Catalog")));
                if (visible)
                {
                    var entry = Call(Read(marker, "Catalog"), "Find", Read(marker, "ColorPreset"));
                    Assert.That(face.sprite, Is.SameAs(Read(entry, "numbered")));
                }
#endif
                records.Add(new { color = colors[i], visible, text = text.text, actualColor = Read(marker, "ColorPreset").ToString(), geometry = Geometry((RectTransform)marker.transform) });
            }
            f.Record("标准玩家标记与真实等级映射", new { source.PlayerIds, source.PlayerColors, source.PlayerLevels, slots = records });
        }

        internal static void Department(Component page, GameplayPageAcceptanceFixture f)
        {
            var draft = (EnterpriseSelectionDraft)Read(page, "Draft"); var p = draft.Projection;
            var fixedBoard = (Component)Read(page, "fixedCompany");
            Assert.That(fixedBoard, Is.Not.Null);
            Assert.That((Array)Read(fixedBoard, "playerLevelMarkers"), Has.Length.EqualTo(4));
            PlayerLevels(fixedBoard, p.FixedCompany, f);
            Assert.That((bool)Read(fixedBoard, "interactionEnabled"), Is.False);
            Assert.That(((IDictionary)Read(fixedBoard, "targets")).Count, Is.Zero);
            Assert.That(Read(fixedBoard, "detailsButton") as Button, Is.Null);
            Assert.That(Read(fixedBoard, "boardPreviewButton") as Button, Is.Null);
            Assert.That(((RawImage)Read(fixedBoard, "boardImage")).raycastTarget, Is.False);
            var overlay = (Component)Read(fixedBoard, "departmentOverlay");
            Assert.That(((RawImage)Read(overlay, "artwork")).raycastTarget, Is.False);
            Assert.That(((Button)Read(overlay, "previewButton")).IsInteractable(), Is.False);
            var items = (IDictionary)Read(page, "items");
            Assert.That(items.Contains(p.CurrentDepartmentId ?? string.Empty), Is.False, "当前科室仍占候选显示位置");
            var scroll = (ScrollRect)Read(page, "candidateScroll"); var viewport = ScreenRect(scroll.viewport);
            foreach (DictionaryEntry entry in items)
            {
                var board = (Component)entry.Value;
                Assert.That(board.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Board Footer"), Is.False);
                Assert.That(Read(board, "detailsButton") as Button, Is.Null);
                var art = ((RawImage)Read(board, "boardImage")).rectTransform; var rect = ScreenRect(art);
                Assert.That(Mathf.Abs((viewport.yMax - rect.yMax) - (rect.yMin - viewport.yMin)), Is.LessThanOrEqualTo(2f), "科室原板上下留白不对称");
            }
            var details = page.GetComponentsInChildren<RectTransform>(true).Single(t => t.name == "Selection Details");
            Assert.That(details.GetComponent<Image>(), Is.Null, "科室Selection Details仍带贴图组件");
            foreach (var name in new[] { "Description", "Detail Title", "Detail Artwork" })
                Assert.That(details.GetComponentsInChildren<Transform>(true).Any(t => t.name == name), Is.False, name + "仍占底部空间");
            Assert.That(details.GetComponentsInChildren<Text>(true).Count(t => t.name == "Selection Summary"), Is.EqualTo(1));
            FramesAndBackgrounds(page);
            f.Record("第三轮科室实际显示集合", new { p.Mode, p.ReadOnly, p.CurrentDepartmentId,
                visibleIds = items.Keys.Cast<string>().ToArray(), sourceIds = p.Boards.Select(b => b.Id).ToArray(),
                viewport = Geometry(scroll.viewport), summary = Geometry(details) });
        }

        internal static void FramesAndBackgrounds(Component root, bool checkEnterpriseFrames = true)
        {
            foreach (var image in root.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite != null && image.sprite.name == "module-bg")
                    Assert.That(image.GetComponent<Outline>(), Is.Null, "module-bg仍有Outline：" + HotseatUiInputDriver.PathOf(image.gameObject));
                if (checkEnterpriseFrames && image.GetComponentInParent(T("EnterpriseSelectionDialogView")) != null &&
                    (image.name == "Frame" || image.name == "State Frame"))
                    Assert.That(image.pixelsPerUnitMultiplier, Is.EqualTo(1f), "授权范围frame倍率未改为1：" + image.name);
            }
        }

        internal static IEnumerator FixedBoardRejectsGestures(Component page, GameplayPageAcceptanceFixture f)
        {
            var draft = (EnterpriseSelectionDraft)Read(page, "Draft"); var before = draft.SelectedIds.ToArray();
            var board = (Component)Read(page, "fixedCompany");
            var rects = new List<RectTransform> { ((RawImage)Read(board, "boardImage")).rectTransform };
            var overlay = (Component)Read(board, "departmentOverlay");
            if (overlay.gameObject.activeInHierarchy) rects.Add(((RawImage)Read(overlay, "artwork")).rectTransform);
            using (var count = new RuleSubmitCounter())
            {
                foreach (var rect in rects)
                foreach (var gesture in new[] { "右键", "双击", "长按" })
                {
                    var area = ScreenRect(rect); var point = rects.IndexOf(rect) == 0 ? new Vector2(area.xMin + area.width * .12f, area.yMax - area.height * .06f) : area.center;
                    var hit = Hit(point); var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
                    Assert.That(handler == null || !handler.transform.IsChildOf(board.transform), Is.True, "固定板仍接收点击手势");
                    var data = new PointerEventData(EventSystem.current) { position = point, pressPosition = point,
                        pointerCurrentRaycast = hit, pointerPressRaycast = hit, eligibleForClick = true,
                        button = gesture == "右键" ? PointerEventData.InputButton.Right : PointerEventData.InputButton.Left, clickCount = 1 };
                    if (handler != null)
                    {
                        ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
                        if (gesture == "长按") yield return new WaitForSecondsRealtime(.7f); else yield return null;
                        ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
                        ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
                        if (gesture == "双击")
                        {
                            yield return null; data.clickCount = 2;
                            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
                            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
                            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
                        }
                    }
                    yield return Settle();
                    Assert.That(page.gameObject.activeInHierarchy, Is.True);
                    Assert.That(((GameObject)Read(page, "detailPage")).activeInHierarchy, Is.False);
                    Assert.That(draft.SelectedIds, Is.EqualTo(before));
                    f.Record("固定莱茵手势无交互", new { gesture, point = new[] { point.x, point.y }, hit = HotseatUiInputDriver.PathOf(hit.gameObject), handler = handler == null ? null : HotseatUiInputDriver.PathOf(handler) });
                }
                Assert.That(count.Calls, Is.Zero);
            }
        }

        internal static IEnumerator CityStyleOrder(GameplayPageAcceptanceFixture f)
        {
            var supply = f.State.Decks.CityStyleSupply.ToArray();
            var expected = supply.OrderBy(id => CityStyleDatabase.Get(f.State, id).Level == 2 ? 1 : 0).ToArray();
            yield return f.Click(HotseatUiInputDriver.Marker("action.tab.2"), "检查HUD城市样式排序");
            var rows = ((Component)f.Hud).GetComponentsInChildren(T("CityStyleStatusRowView"), false).Cast<Component>()
                .Where(row => !string.IsNullOrEmpty((string)Read(row, "Id"))).ToArray();
            Assert.That(rows.Select(row => (string)Read(row, "Id")), Is.EqualTo(expected), "二级样式未稳定排列在其余样式之后");
            f.Record("真实HUD样式顺序", new { supply, expected,
                rows = rows.Select(row => new { id = (string)Read(row, "Id"), level = CityStyleDatabase.Get(f.State, (string)Read(row, "Id")).Level,
                    sibling = row.transform.GetSiblingIndex(), geometry = Geometry((RectTransform)row.transform) }).ToArray() });
            yield return f.Capture("HUD二级样式最后", (Component)f.Hud);
        }
    }
}
