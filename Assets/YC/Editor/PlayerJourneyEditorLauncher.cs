#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

namespace YC.Editor
{
    public static class PlayerJourneyEditorLauncher
    {
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/StartScene.unity");
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            ConfigureResolution();
            EditorApplication.EnterPlaymode();
        }

        // 仅设置启动环境。Unity 2022 的 GameView 分辨率接口非公开；玩家驱动程序集
        // 不引用此启动器，也不允许反射或访问任何游戏对象的业务数据。
        private static void ConfigureResolution()
        {
            var assembly = typeof(EditorApplication).Assembly;
            var sizesType = assembly.GetType("UnityEditor.GameViewSizes");
            var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            var sizes = singleton.GetProperty("instance").GetValue(null);
            var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType");
            var group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { System.Enum.Parse(groupType, "Standalone") });
            var sizeType = assembly.GetType("UnityEditor.GameViewSizeType");
            var size = System.Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"),
                System.Enum.Parse(sizeType, "FixedResolution"), 1920, 1080, "玩家黑盒 1920×1080");
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            var index = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null) - 1;
            var viewType = assembly.GetType("UnityEditor.GameView");
            var view = EditorWindow.GetWindow(viewType);
            viewType.GetProperty("selectedSizeIndex", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).SetValue(view, index);
        }
    }
}
#endif
