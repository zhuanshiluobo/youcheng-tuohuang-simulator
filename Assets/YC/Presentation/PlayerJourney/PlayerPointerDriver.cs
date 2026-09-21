#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.PlayerJourney
{
    public sealed class PlayerPointerDriver
    {
        public static Vector2 Position(GameObject target)
        {
            var rect = target.transform as RectTransform;
            if (rect != null)
            {
                var canvas = target.GetComponentInParent<Canvas>();
                var camera = canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null : canvas == null ? Camera.main : canvas.worldCamera;
                var center = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
                foreach (var x in new[] { 0.5f, 0.15f, 0.85f })
                    foreach (var y in new[] { 0.5f, 0.85f, 0.15f })
                    {
                        var pos = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(new Vector2(
                            Mathf.Lerp(rect.rect.xMin, rect.rect.xMax, x), Mathf.Lerp(rect.rect.yMin, rect.rect.yMax, y))));
                        var hit = Hit(pos);
                        if (hit != null && (hit == target || hit.transform.IsChildOf(target.transform))) return pos;
                    }
                return center;
            }
            return Camera.main.WorldToScreenPoint(target.transform.position);
        }

        public static GameObject Hit(Vector2 position)
        {
            if (EventSystem.current == null) return null;
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = position }, hits);
            return hits.Count == 0 ? null : hits[0].gameObject;
        }

        public static bool Reachable(GameObject target)
        {
            if (target == null || !target.activeInHierarchy) return false;
            var selectable = target.GetComponent<Selectable>();
            if (selectable != null && !selectable.IsInteractable()) return false;
            foreach (var group in target.GetComponentsInParent<CanvasGroup>())
                if (group.alpha < 0.01f || !group.blocksRaycasts) return false;
            var pos = Position(target);
            if (pos.x < 0 || pos.y < 0 || pos.x > Screen.width || pos.y > Screen.height) return false;
            var hit = Hit(pos);
            return hit != null && (hit == target || hit.transform.IsChildOf(target.transform));
        }

        private static PointerEventData Down(GameObject target)
        {
            if (!Reachable(target)) throw new InvalidOperationException("INPUT_BLOCKED: " + target.name);
            var pos = Position(target);
            var data = new PointerEventData(EventSystem.current)
            {
                pointerId = -1, button = PointerEventData.InputButton.Left, position = pos,
                pressPosition = pos, eligibleForClick = true, clickCount = 1, clickTime = Time.unscaledTime,
                pointerCurrentRaycast = new RaycastResult { gameObject = Hit(pos), screenPosition = pos }
            };
            data.pointerPressRaycast = data.pointerCurrentRaycast;
            var hit = data.pointerCurrentRaycast.gameObject;
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerEnterHandler);
            data.pointerPress = ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerDownHandler)
                ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
            data.rawPointerPress = hit;
            return data;
        }

        public IEnumerator Click(GameObject target)
        {
            var data = Down(target);
            yield return null;
            ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
            var release = ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(data.position));
            if (release != data.pointerPress) throw new InvalidOperationException("INPUT_BLOCKED: 抬起时目标已改变");
            ExecuteEvents.Execute(release, data, ExecuteEvents.pointerClickHandler);
        }

        public IEnumerator Drag(GameObject source, GameObject target)
        {
            var end = Position(target);
            var data = Down(source);
            data.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(data.rawPointerPress);
            if (data.pointerDrag == null) throw new InvalidOperationException("INPUT_BLOCKED: 目标不可拖动");
            ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.initializePotentialDrag);
            yield return null;
            data.dragging = true;
            ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.beginDragHandler);
            var start = data.position;
            for (var i = 1; i <= 15; i++)
            {
                var next = Vector2.Lerp(start, end, i / 15f);
                data.delta = next - data.position; data.position = next;
                data.pointerCurrentRaycast = new RaycastResult { gameObject = Hit(next), screenPosition = next };
                ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.dragHandler);
                yield return null;
            }
            if (!Reachable(target)) throw new InvalidOperationException("INPUT_BLOCKED: 放置区不可达");
            ExecuteEvents.ExecuteHierarchy(Hit(end), data, ExecuteEvents.dropHandler);
            ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.endDragHandler);
            ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
            data.dragging = false;
        }
    }
}
#endif
