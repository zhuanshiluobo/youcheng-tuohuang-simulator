using UnityEditor;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class UiDiagnosticLogMenu
    {
        [MenuItem("YC/Dev/UI Diagnostics/Export Snapshot")]
        public static void ExportSnapshot()
        {
            var path = UiDiagnosticLog.Export("manual-editor-export");
            Debug.Log("UI diagnostic log exported: " + path);
        }

        [MenuItem("YC/Dev/UI Diagnostics/Read Latest Snapshot")]
        public static void ReadLatestSnapshot()
        {
            var path = UiDiagnosticLog.GetLatestLogPath();
            string content;
            if (!UiDiagnosticLog.TryRead(path, out content))
            {
                Debug.LogWarning("No UI diagnostic log found. Directory: " + UiDiagnosticLog.GetLogDirectory());
                return;
            }

            Debug.Log("UI diagnostic log: " + path + "\n" + content);
        }

        [MenuItem("YC/Dev/UI Diagnostics/Open Log Folder")]
        public static void OpenLogFolder()
        {
            var directory = UiDiagnosticLog.GetLogDirectory();
            System.IO.Directory.CreateDirectory(directory);
            EditorUtility.RevealInFinder(directory);
        }

        [MenuItem("YC/Dev/UI Diagnostics/Clear Runtime Buffer")]
        public static void ClearRuntimeBuffer()
        {
            UiDiagnosticLog.Clear();
            Debug.Log("UI diagnostic runtime buffer cleared.");
        }
    }
}
