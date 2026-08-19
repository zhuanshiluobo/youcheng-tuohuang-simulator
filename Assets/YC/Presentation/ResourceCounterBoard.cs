using System;
using UnityEngine;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class ResourceCounterBoard : MonoBehaviour
    {
        private sealed class GearAnimation
        {
            public ResourceGearCounterView View;
            public float StartValue;
            public float CurrentValue;
            public int TargetValue;
            public float StartMainAngle;
            public float TargetMainAngle;
            public float StartIdlerAngle;
            public float TargetIdlerAngle;
            public float Elapsed;
            public bool Active;
        }

        private sealed class AuxiliaryAnimation
        {
            public ResourceAuxiliaryCounterView View;
            public float Elapsed;
            public bool Active;
        }

        [SerializeField] private ResourceCounterBoardView view;
        [SerializeField] private ResourceCounterBoardLayoutProfile layoutProfile;

        private GearAnimation[] gearAnimations;
        private AuxiliaryAnimation[] auxiliaryAnimations;
        private bool hasRendered;

        public ResourceCounterBoardView View => view;
        public ResourceCounterBoardLayoutProfile LayoutProfile => layoutProfile;
        public bool HasRendered => hasRendered;

        private void Awake()
        {
            EnsureAnimationState();
        }

        private void Update()
        {
            AdvanceAnimations(Time.unscaledDeltaTime);
        }

        public void Render(ResourceSet resources, bool animate)
        {
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[ResourceCounterBoard] 配置无效：" + reason, this);
                return;
            }

            EnsureAnimationState();
            var shouldAnimate = hasRendered && animate;
            for (var i = 0; i < gearAnimations.Length; i++)
            {
                SetGearTarget(gearAnimations[i], resources.Get(gearAnimations[i].View.ResourceType), shouldAnimate);
            }

            for (var i = 0; i < auxiliaryAnimations.Length; i++)
            {
                SetAuxiliaryTarget(
                    auxiliaryAnimations[i],
                    resources.Get(auxiliaryAnimations[i].View.ResourceType),
                    shouldAnimate);
            }

            hasRendered = true;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;
            if (view == null || layoutProfile == null || !layoutProfile.TryValidateConfiguration(out reason))
            {
                reason = string.IsNullOrEmpty(reason) ? "资源卡板缺少 View 或布局 Profile。" : reason;
                return false;
            }

            if (!view.IsBoundTo(this))
            {
                reason = "资源卡板 Controller/View 未双向绑定。";
                return false;
            }

            return view.TryValidateConfiguration(out reason);
        }

        internal void AdvanceAnimations(float unscaledDeltaTime)
        {
            if (unscaledDeltaTime <= 0f || gearAnimations == null || auxiliaryAnimations == null)
            {
                return;
            }

            for (var i = 0; i < gearAnimations.Length; i++)
            {
                AdvanceGear(gearAnimations[i], unscaledDeltaTime);
            }

            for (var i = 0; i < auxiliaryAnimations.Length; i++)
            {
                AdvanceAuxiliary(auxiliaryAnimations[i], unscaledDeltaTime);
            }
        }

        private void EnsureAnimationState()
        {
            if (view == null || view.GearCounters == null || view.AuxiliaryCounters == null)
            {
                return;
            }

            if (gearAnimations == null || gearAnimations.Length != view.GearCounters.Length)
            {
                gearAnimations = new GearAnimation[view.GearCounters.Length];
                for (var i = 0; i < gearAnimations.Length; i++)
                {
                    gearAnimations[i] = new GearAnimation { View = view.GearCounters[i] };
                }
            }

            if (auxiliaryAnimations == null || auxiliaryAnimations.Length != view.AuxiliaryCounters.Length)
            {
                auxiliaryAnimations = new AuxiliaryAnimation[view.AuxiliaryCounters.Length];
                for (var i = 0; i < auxiliaryAnimations.Length; i++)
                {
                    auxiliaryAnimations[i] = new AuxiliaryAnimation { View = view.AuxiliaryCounters[i] };
                }
            }
        }

        private void SetGearTarget(GearAnimation animation, int target, bool animate)
        {
            if (animation.View == null) return;
            if (!animate)
            {
                animation.Active = false;
                animation.StartValue = target;
                animation.CurrentValue = target;
                animation.TargetValue = target;
                SetDisplayedValue(animation.View, target);
                SetZRotation(animation.View.MainGear, -TensDigit(target) * layoutProfile.DegreesPerStep);
                SetZRotation(animation.View.IdlerGear, -OnesDigit(target) * layoutProfile.DegreesPerStep);
                return;
            }

            var currentRounded = Mathf.RoundToInt(animation.CurrentValue);
            if (currentRounded == target && !animation.Active)
            {
                SetDisplayedValue(animation.View, target);
                return;
            }

            animation.StartValue = animation.CurrentValue;
            animation.TargetValue = target;
            animation.StartMainAngle = NormalizeSigned(animation.View.MainGear.localEulerAngles.z);
            animation.StartIdlerAngle = NormalizeSigned(animation.View.IdlerGear.localEulerAngles.z);
            var delta = target - currentRounded;
            var tensDelta = target / 10 - currentRounded / 10;
            animation.TargetMainAngle = tensDelta == 0
                ? animation.StartMainAngle
                : BuildDirectionalDigitTarget(
                    animation.StartMainAngle,
                    TensDigit(target),
                    tensDelta,
                    layoutProfile.DegreesPerStep);
            animation.TargetIdlerAngle = BuildDirectionalDigitTarget(
                animation.StartIdlerAngle,
                OnesDigit(target),
                delta,
                layoutProfile.DegreesPerStep);
            animation.Elapsed = 0f;
            animation.Active = true;
        }

        private void SetAuxiliaryTarget(AuxiliaryAnimation animation, int target, bool animate)
        {
            if (animation.View == null) return;
            var changed = animation.View.AmountText.text != target.ToString();
            animation.View.AmountText.text = target.ToString();
            if (!animate || !changed)
            {
                animation.Active = false;
                animation.View.ContentRoot.localScale = Vector3.one;
                SetFlashAlpha(animation.View, 0f);
                return;
            }

            animation.Elapsed = 0f;
            animation.Active = true;
        }

        private void AdvanceGear(GearAnimation animation, float deltaTime)
        {
            if (!animation.Active || animation.View == null) return;
            animation.Elapsed += deltaTime;
            var progress = Mathf.Clamp01(animation.Elapsed / layoutProfile.GearAnimationDuration);
            var eased = progress * progress * (3f - 2f * progress);
            animation.CurrentValue = Mathf.Lerp(animation.StartValue, animation.TargetValue, eased);
            SetDisplayedValue(animation.View, Mathf.RoundToInt(animation.CurrentValue));
            SetZRotation(animation.View.MainGear, Mathf.Lerp(animation.StartMainAngle, animation.TargetMainAngle, eased));
            SetZRotation(animation.View.IdlerGear, Mathf.Lerp(animation.StartIdlerAngle, animation.TargetIdlerAngle, eased));
            if (progress < 1f) return;

            animation.Active = false;
            animation.CurrentValue = animation.TargetValue;
            SetDisplayedValue(animation.View, animation.TargetValue);
            SetZRotation(animation.View.MainGear, animation.TargetMainAngle);
            SetZRotation(animation.View.IdlerGear, animation.TargetIdlerAngle);
        }

        private void AdvanceAuxiliary(AuxiliaryAnimation animation, float deltaTime)
        {
            if (!animation.Active || animation.View == null) return;
            animation.Elapsed += deltaTime;
            var progress = Mathf.Clamp01(animation.Elapsed / layoutProfile.AuxiliaryAnimationDuration);
            var pulse = Mathf.Sin(progress * Mathf.PI);
            animation.View.ContentRoot.localScale = Vector3.one * (1f + pulse * 0.08f);
            SetFlashAlpha(animation.View, pulse * 0.28f);
            if (progress < 1f) return;

            animation.Active = false;
            animation.View.ContentRoot.localScale = Vector3.one;
            SetFlashAlpha(animation.View, 0f);
        }

        private static void SetFlashAlpha(ResourceAuxiliaryCounterView counter, float alpha)
        {
            var color = counter.FlashOverlay.color;
            color.a = alpha;
            counter.FlashOverlay.color = color;
        }

        private static void SetZRotation(RectTransform target, float angle)
        {
            target.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        private static float NormalizeSigned(float angle)
        {
            return angle > 180f ? angle - 360f : angle;
        }

        private static int TensDigit(int value)
        {
            return Mathf.Abs(value / 10) % 10;
        }

        private static int OnesDigit(int value)
        {
            return Mathf.Abs(value) % 10;
        }

        private static void SetDisplayedValue(ResourceGearCounterView counter, int value)
        {
            counter.TensDigitText.text = TensDigit(value).ToString();
            counter.OnesDigitText.text = OnesDigit(value).ToString();
        }

        private static float BuildDirectionalDigitTarget(
            float startAngle,
            int targetDigit,
            int signedStepCount,
            float degreesPerStep)
        {
            if (signedStepCount == 0) return startAngle;
            var targetAngle = -targetDigit * degreesPerStep;
            if (signedStepCount > 0)
            {
                while (targetAngle >= startAngle - 0.001f) targetAngle -= 360f;
                targetAngle -= 360f * Mathf.Clamp(Mathf.Abs(signedStepCount) / 10 - 1, 0, 1);
            }
            else
            {
                while (targetAngle <= startAngle + 0.001f) targetAngle += 360f;
                targetAngle += 360f * Mathf.Clamp(Mathf.Abs(signedStepCount) / 10 - 1, 0, 1);
            }
            return targetAngle;
        }
    }
}
