using UnityEngine;

namespace YC.Presentation
{
    public sealed class ResourceCounterVisualLibrary : ScriptableObject
    {
        [SerializeField] private Sprite largeGear;
        [SerializeField] private Sprite smallGear;
        [SerializeField] private Sprite numberedGear;
        [SerializeField] private Sprite dialCover;
        [SerializeField] private Sprite readoutRing;
        [SerializeField] private Sprite rivet;
        [SerializeField] private Sprite banknote;

        public Sprite LargeGear => largeGear;
        public Sprite SmallGear => smallGear;
        public Sprite NumberedGear => numberedGear;
        public Sprite DialCover => dialCover;
        public Sprite ReadoutRing => readoutRing;
        public Sprite Rivet => rivet;
        public Sprite Banknote => banknote;

        public bool TryValidateConfiguration(out string reason)
        {
            if (largeGear == null || smallGear == null || numberedGear == null || dialCover == null ||
                readoutRing == null || rivet == null || banknote == null)
            {
                reason = "资源卡板视觉库缺少齿轮、数字齿轮、铆钉或金券 Sprite。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
