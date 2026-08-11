using UnityEngine;

namespace YC.Presentation
{
    public sealed class UiVisualAssetLibrary : ScriptableObject
    {
        [SerializeField] private Sprite triangleUp;
        [SerializeField] private Sprite triangleDown;

        public Sprite TriangleUp => triangleUp;
        public Sprite TriangleDown => triangleDown;

        public bool TryValidateConfiguration(out string reason)
        {
            if (triangleUp == null || triangleDown == null)
            {
                reason = "Shared UI visual library requires persistent up/down triangle sprites.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
