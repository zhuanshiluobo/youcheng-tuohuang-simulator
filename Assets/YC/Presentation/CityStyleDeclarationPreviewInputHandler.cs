using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    public sealed class CityStyleDeclarationPreviewInputHandler : MonoBehaviour
    {
        private static int escapeConsumedFrame = -1;

        private Action clearSelectionRequested;
        private Action closeRequested;
        private Action<int> pageChangeRequested;

        public static bool WasEscapeConsumedThisFrame()
        {
            return escapeConsumedFrame == Time.frameCount;
        }

        public static bool HasOpenDialog()
        {
            var handlers = FindObjectsOfType<CityStyleDeclarationPreviewInputHandler>();
            for (var i = 0; i < handlers.Length; i++)
            {
                if (handlers[i] != null && handlers[i].isActiveAndEnabled)
                {
                    return true;
                }
            }

            return false;
        }

        public void Configure(
            Action configuredClearSelectionRequested,
            Action configuredCloseRequested,
            Action<int> configuredPageChangeRequested)
        {
            clearSelectionRequested = configuredClearSelectionRequested;
            closeRequested = configuredCloseRequested;
            pageChangeRequested = configuredPageChangeRequested;
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(1))
            {
                HandleRightClick(RaycastPointerTarget());
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                escapeConsumedFrame = Time.frameCount;
                closeRequested?.Invoke();
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                pageChangeRequested?.Invoke(-1);
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                pageChangeRequested?.Invoke(1);
            }
        }

        private void HandleRightClick(GameObject pointerTarget)
        {
            if (IsDeclarationSlot(pointerTarget))
            {
                clearSelectionRequested?.Invoke();
                return;
            }

            closeRequested?.Invoke();
        }

        private static GameObject RaycastPointerTarget()
        {
            if (EventSystem.current == null)
            {
                return null;
            }

            var pointer = new PointerEventData(EventSystem.current)
            {
                position = Input.mousePosition
            };
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, results);
            return results.Count > 0 ? results[0].gameObject : null;
        }

        private static bool IsDeclarationSlot(GameObject pointerTarget)
        {
            return pointerTarget != null &&
                   pointerTarget.GetComponentInParent<CityStyleDeclarationSlotPointerHandler>() != null;
        }
    }
}
