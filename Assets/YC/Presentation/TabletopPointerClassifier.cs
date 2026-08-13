using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>Distinguishes flat HUD hits from interactive UI rendered on the tabletop.</summary>
    public static class TabletopPointerClassifier
    {
        public static bool IsBlockedByFlatHud(Vector2 screenPosition)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return false;
            }

            var pointer = new PointerEventData(eventSystem) { position = screenPosition };
            var hits = new List<RaycastResult>();
            eventSystem.RaycastAll(pointer, hits);
            return IsBlockedByTopUiHit(hits);
        }

        private static bool IsBlockedByTopUiHit(IList<RaycastResult> hits)
        {
            for (var i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                if (hit.gameObject == null || !(hit.module is GraphicRaycaster))
                {
                    continue;
                }

                return hit.gameObject.GetComponentInParent<TabletopCanvasLayout>() == null;
            }

            return false;
        }
    }
}
