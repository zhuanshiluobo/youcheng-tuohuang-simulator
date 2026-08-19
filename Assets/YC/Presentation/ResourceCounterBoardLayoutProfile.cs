using UnityEngine;

namespace YC.Presentation
{
    [CreateAssetMenu(
        menuName = "YC/Presentation/Resource Counter Board Layout Profile",
        fileName = "ResourceCounterBoardLayoutProfile")]
    public sealed class ResourceCounterBoardLayoutProfile : ScriptableObject
    {
        [SerializeField] private SecondaryRectLayout rootLayout;
        [SerializeField] private Vector2 gearCounterSize = new Vector2(134f, 132f);
        [SerializeField] private Vector2 auxiliaryCounterSize = new Vector2(212f, 50f);
        [SerializeField] private float gearAnimationDuration = 0.46f;
        [SerializeField] private float auxiliaryAnimationDuration = 0.32f;
        [SerializeField] private float degreesPerStep = 36f;

        public SecondaryRectLayout RootLayout => rootLayout;
        public Vector2 GearCounterSize => gearCounterSize;
        public Vector2 AuxiliaryCounterSize => auxiliaryCounterSize;
        public float GearAnimationDuration => gearAnimationDuration;
        public float AuxiliaryAnimationDuration => auxiliaryAnimationDuration;
        public float DegreesPerStep => degreesPerStep;

        public bool TryValidateConfiguration(out string reason)
        {
            if (!rootLayout.TryValidate(true, out reason) ||
                !SecondaryLayoutProfileValidation.IsFinite(gearCounterSize) ||
                !SecondaryLayoutProfileValidation.IsFinite(auxiliaryCounterSize) ||
                gearCounterSize.x <= 0f || gearCounterSize.y <= 0f ||
                auxiliaryCounterSize.x <= 0f || auxiliaryCounterSize.y <= 0f ||
                gearAnimationDuration <= 0f || auxiliaryAnimationDuration <= 0f ||
                degreesPerStep <= 0f)
            {
                reason = "资源卡板布局 Profile 包含无效尺寸或动画参数。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            SecondaryRectLayout configuredRootLayout,
            Vector2 configuredGearCounterSize,
            Vector2 configuredAuxiliaryCounterSize,
            float configuredGearAnimationDuration,
            float configuredAuxiliaryAnimationDuration,
            float configuredDegreesPerStep)
        {
            rootLayout = configuredRootLayout;
            gearCounterSize = configuredGearCounterSize;
            auxiliaryCounterSize = configuredAuxiliaryCounterSize;
            gearAnimationDuration = configuredGearAnimationDuration;
            auxiliaryAnimationDuration = configuredAuxiliaryAnimationDuration;
            degreesPerStep = configuredDegreesPerStep;
        }
#endif
    }
}
