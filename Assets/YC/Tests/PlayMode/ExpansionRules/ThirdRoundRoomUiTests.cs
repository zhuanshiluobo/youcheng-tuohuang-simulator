using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    [Timeout(180000)]
    public sealed class ThirdRoundRoomUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private Component room;

        [UnityTearDown]
        public IEnumerator PreserveEndState()
        {
            if (room != null && room.gameObject.activeInHierarchy) yield return f.Capture("房间验收终态_包含失败现场", room);
        }

        [UnityTest]
        public IEnumerator StartScene_ExpansionScrollbarStaysInsideAndAcceptsRealDrag()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return SceneManager.LoadSceneAsync("StartScene"); yield return null;
            yield return f.Resolution(Sizes[0]);
            var controller = Active("StartMenuController").Single(); var view = Read(controller, "view");
            yield return f.Click(ButtonObject(view, "StartGameButton"), "实际主界面创建本地房间");
            room = (Component)Read(view, "RoomPanel");
            Assert.That(room.gameObject.activeInHierarchy, Is.True);
            var scroll = room.GetComponentsInChildren<ScrollRect>(true).Single(s => s.name == "RoomExpansionScroll");
            var bar = scroll.verticalScrollbar;
            Assert.That(bar, Is.Not.Null); Assert.That(bar.name, Is.EqualTo("RoomExpansionScrollScrollbar"));
            foreach (var size in Sizes)
            {
                yield return f.Resolution(size);
                var naturalOverflow = scroll.content.rect.height > scroll.viewport.rect.height + 1;
                var bounds = ScreenRect((RectTransform)scroll.transform);
                var track = ScreenRect((RectTransform)bar.transform); var handle = ScreenRect(bar.handleRect);
                Contained(track, bounds, "滚动条超出实际扩展滚动区域");
                Contained(handle, track, "滑块超出轨道");
                ThirdRoundUiAcceptanceChecks.FramesAndBackgrounds(room, false);
                yield return f.Capture("房间扩展边界_拖拽前", room);
                f.Record("房间自然内容与AutoHide", new { naturalOverflow, active = bar.gameObject.activeInHierarchy,
                    scroll.verticalScrollbarVisibility, content = Geometry(scroll.content), viewport = Geometry(scroll.viewport) });
                GameObject filler = null;
                var last = scroll.content.Cast<Transform>().Last(t => t.gameObject.activeSelf) as RectTransform;
                if (!naturalOverflow)
                {
                    // 仅加长测试运行时实例；原真实末项仍在末尾，不创建假扩展或保存资产。
                    filler = new GameObject("受控长内容占位_不属于正式房间数据", typeof(RectTransform), typeof(LayoutElement));
                    filler.transform.SetParent(scroll.content, false); filler.transform.SetSiblingIndex(last.GetSiblingIndex());
                    filler.GetComponent<LayoutElement>().preferredHeight = scroll.viewport.rect.height * 2;
                    yield return Settle();
                    Assert.That(scroll.content.rect.height, Is.GreaterThan(scroll.viewport.rect.height + 1), "测试占位未生成溢出，属于夹具条件未满足");
                }
                track = ScreenRect((RectTransform)bar.transform); handle = ScreenRect(bar.handleRect);
                Assert.That(bar.gameObject.activeInHierarchy && bar.IsInteractable(), Is.True);
                var previous = bar.value; var previousPosition = scroll.content.anchoredPosition;
                var positions = new System.Collections.Generic.List<float>();
                foreach (var endY in new[] { track.yMax - 2, track.center.y, track.yMin + 2 })
                {
                    yield return DragTo(bar, new Vector2(track.center.x, endY));
                    positions.Add(bar.value);
                    Contained(ScreenRect(bar.handleRect), track, "顶/中/底滑块超出轨道");
                }
                Assert.That(positions.Max() - positions.Min(), Is.GreaterThan(.5f), "溢出列表拖拽未跨越顶/中/底");
                Contained(ScreenRect((RectTransform)bar.transform), ScreenRect((RectTransform)scroll.transform), "拖拽后滚动条超界");
                Contained(ScreenRect(last), ScreenRect(scroll.viewport), "真实末项未能滚动到视口内");
                f.Record("实际房间滚动条拖拽", new { naturalOverflow, controlledContent = filler != null, previous, after = bar.value, positions,
                    previousPosition = new[] { previousPosition.x, previousPosition.y },
                    afterPosition = new[] { scroll.content.anchoredPosition.x, scroll.content.anchoredPosition.y }, bar = Geometry((RectTransform)bar.transform),
                    parent = Geometry((RectTransform)scroll.transform),
                    note = naturalOverflow ? "自然溢出内容已检查位移" : "长内容仅为测试运行时占位；不声称正式自然列表发生溢出" });
                yield return f.Capture("房间扩展边界_拖拽后", room);
                if (filler != null) { Object.Destroy(filler); yield return Settle(); }
            }
        }

        private static IEnumerator DragTo(Scrollbar bar, Vector2 end)
        {
            var start = ScreenRect(bar.handleRect).center; var hit = Hit(start);
            var handler = ExecuteEvents.GetEventHandler<IDragHandler>(hit.gameObject);
            Assert.That(handler, Is.SameAs(bar.gameObject), "滑块拖拽被其他控件遮挡");
            var data = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left,
                position = start, pressPosition = start, pointerCurrentRaycast = hit, pointerPressRaycast = hit,
                pointerDrag = handler, useDragThreshold = false };
            ExecuteEvents.Execute(handler, data, ExecuteEvents.initializePotentialDrag);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.beginDragHandler);
            for (var step = 1; step <= 5; step++)
            {
                var next = Vector2.Lerp(start, end, step / 5f); data.delta = next - data.position; data.position = next;
                ExecuteEvents.Execute(handler, data, ExecuteEvents.dragHandler); yield return null;
            }
            ExecuteEvents.Execute(handler, data, ExecuteEvents.endDragHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
            yield return Settle();
        }

        private static void Contained(Rect inner, Rect outer, string reason)
        {
            Assert.That(inner.xMin, Is.GreaterThanOrEqualTo(outer.xMin - 1), reason);
            Assert.That(inner.xMax, Is.LessThanOrEqualTo(outer.xMax + 1), reason);
            Assert.That(inner.yMin, Is.GreaterThanOrEqualTo(outer.yMin - 1), reason);
            Assert.That(inner.yMax, Is.LessThanOrEqualTo(outer.yMax + 1), reason);
        }
    }
}
