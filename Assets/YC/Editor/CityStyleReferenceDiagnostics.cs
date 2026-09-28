using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace YC.Presentation.Editor
{
    internal static class CityStyleReferenceDiagnostics
    {
        [MenuItem("YC/诊断/读取城市样式引用")]
        private static void ReadReferences()
        {
            // 只读已导入对象，不进入运行、不重建、不保存任何 UI 资产或场景。
            AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CityStyleDeclarationPreviewDialog.prefab");
            var report = new StringBuilder();
            foreach (var view in Resources.FindObjectsOfTypeAll<CityStyleDeclarationPreviewView>())
            {
                report.AppendLine(view.name + " | " + AssetDatabase.GetAssetPath(view));
                var serialized = new SerializedObject(view);
                foreach (var field in new[] { "optionsContent", "optionTemplate", "listPage", "detailPage", "detailGesture" })
                {
                    var property = serialized.FindProperty(field);
                    var value = property.objectReferenceValue;
                    report.AppendLine(field + " = " + (value == null ? "NULL" : value.name) + " | " + property.objectReferenceInstanceIDValue);
                }
            }
            File.WriteAllText("Logs/CityStyleReferenceDiagnostics.txt", report.ToString());
        }
    }
}
