using System;

namespace YC.Editor
{
    /// <summary>开始页与局内设置使用独立 Prefab；只读检查必须跟随实际场景来源。</summary>
    internal static class GameSettingsAssetSources
    {
        internal const string SharedPrefabPath =
            "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";
        internal const string InGamePrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/InGame/InGameSettingsMenu.prefab";
        internal static readonly string[] PrefabPaths = { SharedPrefabPath, InGamePrefabPath };

        internal static string ForScene(string scenePath)
        {
            switch ((scenePath ?? string.Empty).Replace('\\', '/'))
            {
                case "Assets/Scenes/StartScene.unity":
                    return SharedPrefabPath;
                case "Assets/Scenes/SampleScene.unity":
                case "Assets/Scenes/ThreePlayerScene.unity":
                    return InGamePrefabPath;
                default:
                    throw new InvalidOperationException("未配置设置资产来源的场景：" + scenePath);
            }
        }
    }
}
