using UnityEditor;
using UnityEngine;
using YC.Infrastructure.Multiplayer;

namespace YC.Editor
{
    public static class LocalMirrorTestModeMenu
    {
        private const string MenuPath = "YC/联机测试/启用 Mirror 本地测试模式";

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("请先停止 Play Mode，再切换 Mirror 本地测试模式。");
                return;
            }

            LocalMirrorTestMode.SetEditorEnabled(!LocalMirrorTestMode.IsEnabled);
            Menu.SetChecked(MenuPath, LocalMirrorTestMode.IsEnabled);
            Debug.Log("Mirror 本地测试模式已" + (LocalMirrorTestMode.IsEnabled ? "启用" : "关闭") + "。");
        }

        [MenuItem(MenuPath, true)]
        private static bool Validate()
        {
            Menu.SetChecked(MenuPath, LocalMirrorTestMode.IsEnabled);
            return true;
        }
    }
}
