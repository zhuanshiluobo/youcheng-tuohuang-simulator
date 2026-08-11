using UnityEngine;

namespace YC.Presentation
{
    [DefaultExecutionOrder(-10000)]
    public sealed class FontRefreshDriver : MonoBehaviour
    {
        private const float HealthCheckSeconds = 30f;

        [SerializeField] private Font cjkFont;
        [SerializeField] private Font latinFont;

        private bool subscribed;
        private float nextHealthCheckAt;

        public Font CjkFont => cjkFont;
        public Font LatinFont => latinFont;

        private void Awake()
        {
            ConfigureAndSubscribe();
        }

        private void OnEnable()
        {
            ConfigureAndSubscribe();
        }

        private void OnDisable()
        {
            UnsubscribeAndRelease();
        }

        private void OnDestroy()
        {
            UnsubscribeAndRelease();
        }

        private void Update()
        {
            FontUtility.FlushPendingManagedTextRefresh();
            if (Time.unscaledTime < nextHealthCheckAt)
            {
                return;
            }

            nextHealthCheckAt = Time.unscaledTime + HealthCheckSeconds;
            if (FontUtility.HasInvalidManagedFontTexture())
            {
                FontUtility.RecreateManagedFonts();
                FontUtility.FlushPendingManagedTextRefresh();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && FontUtility.IsConfigured)
            {
                FontUtility.RecreateManagedFonts();
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!pauseStatus && FontUtility.IsConfigured)
            {
                FontUtility.RecreateManagedFonts();
            }
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (cjkFont == null || latinFont == null)
            {
                reason = "FontRefreshDriver requires serialized CJK and Latin font assets.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private void ConfigureAndSubscribe()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                enabled = false;
                Debug.LogError(reason, this);
                return;
            }

            FontUtility.Configure(cjkFont, latinFont, this);
            if (!subscribed)
            {
                Font.textureRebuilt += FontUtility.OnFontTextureRebuilt;
                subscribed = true;
            }

            nextHealthCheckAt = Time.unscaledTime + HealthCheckSeconds;
        }

        private void UnsubscribeAndRelease()
        {
            if (subscribed)
            {
                Font.textureRebuilt -= FontUtility.OnFontTextureRebuilt;
                subscribed = false;
            }

            FontUtility.Release(this);
        }
    }
}
