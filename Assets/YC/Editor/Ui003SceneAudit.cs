using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation.Editor
{
    /// <summary>只读检查两张正式对局场景的 HUD 来源与常驻栏接线。</summary>
    public static class Ui003SceneAudit
    {
        public static void Run()
        {
            const string prefabPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var sourceView = source == null ? null : source.GetComponent<GameplayInteractionHudView>();
            var reason = string.Empty;
            if (sourceView == null || !sourceView.TryValidateConfiguration(out reason))
                throw new InvalidOperationException("共享 HUD 源预制体无效：" + reason);
            var lines = new List<string> { "UI-003 场景来源与输入审计", "HUD 源：" + prefabPath };

            foreach (var path in new[] { "Assets/Scenes/SampleScene.unity", "Assets/Scenes/ThreePlayerScene.unity" })
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                GameplayInteractionHudView sceneView = null;
                var eventSystems = 0;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (sceneView == null) sceneView = root.GetComponentInChildren<GameplayInteractionHudView>(true);
                    eventSystems += root.GetComponentsInChildren<EventSystem>(true).Length;
                }
                if (sceneView == null || !sceneView.TryValidateConfiguration(out reason))
                    throw new InvalidOperationException(path + " 场景 HUD 无效：" + reason);
                var origin = PrefabUtility.GetCorrespondingObjectFromSource(sceneView);
                if (origin != sourceView || eventSystems != 1)
                    throw new InvalidOperationException(path + " 未使用共享 HUD，或 EventSystem 不唯一。");
                var frame = sceneView.Frame;
                if (frame.EndActionButton != sceneView.ActionPanelView.EndRoundButton ||
                    frame.BarCanvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null ||
                    frame.TopBar.parent != frame.BottomBar.parent)
                    throw new InvalidOperationException(path + " 常驻栏输入或唯一结束行动入口未接好。");
                Debug.Log("UI003 AUDIT OK " + path + " source=" + prefabPath +
                          " bars=" + frame.BarCanvas.sortingOrder +
                          " content=" + frame.ContentRect.name +
                          " eventSystems=" + eventSystems);
                lines.Add(path + " | HUD 共享源匹配 | EventSystem=" + eventSystems +
                          " | 顶底栏排序=" + frame.BarCanvas.sortingOrder +
                          " | 结束行动入口唯一 | 常驻栏有 GraphicRaycaster");
            }
            var output = Path.GetFullPath("prompt/UI换新/执行记录/证据/UI003-场景来源审计.txt");
            File.WriteAllLines(output, lines);
            Debug.Log("UI003 AUDIT REPORT " + output);
        }
    }
}
