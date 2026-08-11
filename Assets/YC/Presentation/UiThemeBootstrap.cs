using System;
using UnityEngine;

namespace YC.Presentation
{
    [DefaultExecutionOrder(-30000)]
    public sealed class UiThemeBootstrap : MonoBehaviour
    {
        [SerializeField] private UiThemeCatalog themeCatalog;

        public UiThemeCatalog Catalog => themeCatalog;

        private void Awake()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[UiThemeBootstrap] " + reason, this);
                throw new InvalidOperationException("UiThemeBootstrap 初始化失败：" + reason);
            }

            UiTheme.Initialize(themeCatalog);
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (themeCatalog == null)
            {
                reason = "缺少 UiThemeCatalog 引用。";
                return false;
            }

            return themeCatalog.TryValidateConfiguration(out reason);
        }
    }
}
