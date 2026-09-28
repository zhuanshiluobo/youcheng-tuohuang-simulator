using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation.Editor
{
    /// <summary>读取正式资产和两张场景的有效覆盖；不保存或生成任何 UI。</summary>
    public static class GameplayUiIntegrityAudit
    {
        [Serializable] private class Report
        {
            public List<string> sources = new List<string>();
            public List<string> errors = new List<string>();
            public List<LayoutRecord> layouts = new List<LayoutRecord>();
        }
        [Serializable] private class LayoutRecord
        {
            public string source, path, driver;
            public long fileID;
            public Vector2 minimum, preferred, flexible;
            public string configuration;
        }
        public static void Run()
        {
            var report = new Report();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] {
                "Assets/YC/Presentation/Prefabs/Gameplay", "Assets/YC/Presentation/Prefabs/FinalScore",
                "Assets/YC/Presentation/Effects/Prefabs" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                Check(AssetDatabase.LoadAssetAtPath<GameObject>(path), path, report);
            }
            foreach (var name in new[] { "SampleScene", "ThreePlayerScene" })
            {
                var path = "Assets/Scenes/" + name + ".unity";
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                foreach (var root in scene.GetRootGameObjects()) Check(root, path, report);
            }
            var target = Environment.GetEnvironmentVariable("YC_UI_AUDIT_OUTPUT") ??
                "prompt/UI换新/执行记录/局内UI基础修正/最终实际布局与引用.json";
            File.WriteAllText(target, JsonUtility.ToJson(report, true));
            if (report.errors.Count > 0) throw new InvalidOperationException(string.Join("\n", report.errors));
            Debug.Log("局内 UI 源资产及实际场景覆盖引用核验通过，布局记录 " + report.layouts.Count);
            EditorApplication.Exit(0);
        }
        private static void Check(GameObject root, string source, Report report)
        {
            if (root == null) { report.errors.Add(source + " 资产为空"); return; }
            if (!report.sources.Contains(source)) report.sources.Add(source);
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var path = AnimationUtility.CalculateTransformPath(transform, root.transform);
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) > 0)
                    report.errors.Add(source + "/" + path + " Missing Script");
                foreach (var component in transform.GetComponents<Component>())
                {
                    if (component == null) continue;
                    var serialized = new SerializedObject(component);
                    var property = serialized.GetIterator();
                    while (property.NextVisible(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference &&
                            property.objectReferenceValue == null && property.objectReferenceInstanceIDValue != 0)
                            report.errors.Add(source + "/" + path + "." + property.propertyPath + " Missing Reference");
                    if (!(component is ILayoutController)) continue;
                    var rect = transform as RectTransform;
                    if (rect == null) continue;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component, out string assetGuid, out long id);
                    report.layouts.Add(new LayoutRecord { source = source, path = path, fileID = id,
                        driver = component.GetType().Name,
                        minimum = new Vector2(LayoutUtility.GetMinWidth(rect), LayoutUtility.GetMinHeight(rect)),
                        preferred = new Vector2(LayoutUtility.GetPreferredWidth(rect), LayoutUtility.GetPreferredHeight(rect)),
                        flexible = new Vector2(LayoutUtility.GetFlexibleWidth(rect), LayoutUtility.GetFlexibleHeight(rect)),
                        configuration = EditorJsonUtility.ToJson(component) });
                }
            }
        }
    }
}
