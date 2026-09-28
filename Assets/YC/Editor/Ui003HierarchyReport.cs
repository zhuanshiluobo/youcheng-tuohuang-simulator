using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YC.Presentation.Editor
{
    /// <summary>只读记录实际场景与共享 HUD 的可见对象来源、父子关系和矩形。</summary>
    public static class Ui003HierarchyReport
    {
        public static void Run()
        {
            var report = new StringBuilder();
            foreach (var path in new[] { "Assets/Scenes/SampleScene.unity", "Assets/Scenes/ThreePlayerScene.unity" })
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                report.AppendLine(path);
                foreach (var root in scene.GetRootGameObjects()) Write(root.transform, report, 0, 5);
            }
            var output = Path.GetFullPath("Temp/ui003-hierarchy.txt");
            File.WriteAllText(output, report.ToString());
            Debug.Log("UI003 HIERARCHY " + output);
        }

        private static void Write(Transform transform, StringBuilder report, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(transform.gameObject);
            var sourcePath = source == null ? string.Empty : AssetDatabase.GetAssetPath(source);
            report.Append(' ', depth * 2).Append(transform.name);
            var rect = transform as RectTransform;
            if (rect != null)
                report.Append(" rect=").Append(rect.anchorMin).Append('/').Append(rect.anchorMax)
                    .Append(" pos=").Append(rect.anchoredPosition).Append(" size=").Append(rect.sizeDelta);
            var canvas = transform.GetComponent<Canvas>();
            if (canvas != null) report.Append(" canvas=").Append(canvas.sortingOrder);
            var camera = transform.GetComponent<Camera>();
            if (camera != null) report.Append(" camera=").Append(camera.rect);
            if (!string.IsNullOrEmpty(sourcePath)) report.Append(" source=").Append(sourcePath);
            report.AppendLine();
            for (var i = 0; i < transform.childCount; i++) Write(transform.GetChild(i), report, depth + 1, maxDepth);
        }
    }
}
