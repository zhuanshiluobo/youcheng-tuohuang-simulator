using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;

namespace YC.Presentation
{
    public sealed class WindowCloseInputHandler : MonoBehaviour
    {
        private readonly List<RaycastResult> raycastResults = new List<RaycastResult>();
        private UnityAction closeAction;
        private bool closeRequested;

        public void Configure(UnityAction configuredCloseAction)
        {
            closeAction = configuredCloseAction;
            closeRequested = false;
        }

        public void RequestClose()
        {
            if (closeRequested)
            {
                return;
            }

            closeRequested = true;
            closeAction?.Invoke();
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(1) && IsPointerOverThisWindow())
            {
                RequestClose();
            }
        }

        private bool IsPointerOverThisWindow()
        {
            if (EventSystem.current == null)
            {
                return true;
            }

            raycastResults.Clear();
            EventSystem.current.RaycastAll(
                new PointerEventData(EventSystem.current) { position = Input.mousePosition },
                raycastResults);
            for (var i = 0; i < raycastResults.Count; i++)
            {
                var hit = raycastResults[i].gameObject;
                if (hit == null)
                {
                    continue;
                }

                var hitTransform = hit.transform;
                return hitTransform == transform || hitTransform.IsChildOf(transform);
            }

            return false;
        }
    }
}
