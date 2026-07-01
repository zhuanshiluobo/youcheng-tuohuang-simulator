using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace YC.Presentation
{
    public enum UiDiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public static class UiDiagnosticLog
    {
        private const int MaxEntries = 512;
        private const string DirectoryName = "YCDiagnostics";
        private const string FilePrefix = "ui-diagnostic-";
        private const string FileExtension = ".json";

        private static readonly List<UiDiagnosticLogEntry> Entries = new List<UiDiagnosticLogEntry>();

        public static IReadOnlyList<UiDiagnosticLogEntry> CurrentEntries => Entries;

        public static void Clear()
        {
            Entries.Clear();
        }

        public static void Record(
            string category,
            string message,
            UnityEngine.Object context = null,
            string data = null,
            UiDiagnosticSeverity severity = UiDiagnosticSeverity.Info)
        {
            if (string.IsNullOrEmpty(category))
            {
                category = "General";
            }

            if (string.IsNullOrEmpty(message))
            {
                message = "<empty>";
            }

            if (Entries.Count >= MaxEntries)
            {
                Entries.RemoveAt(0);
            }

            Entries.Add(new UiDiagnosticLogEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                Frame = Time.frameCount,
                Severity = severity.ToString(),
                Category = category,
                Message = message,
                ContextName = context == null ? string.Empty : context.name,
                ContextType = context == null ? string.Empty : context.GetType().Name,
                HierarchyPath = GetHierarchyPath(context),
                Data = data ?? string.Empty
            });
        }

        public static string Export(string reason = null)
        {
            var directory = GetLogDirectory();
            Directory.CreateDirectory(directory);

            var path = Path.Combine(
                directory,
                FilePrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss") + FileExtension);

            var snapshot = new UiDiagnosticLogSnapshot
            {
                SchemaVersion = 1,
                ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                Reason = reason ?? string.Empty,
                UnityVersion = UnityEngine.Application.unityVersion,
                ApplicationVersion = UnityEngine.Application.version,
                Platform = UnityEngine.Application.platform.ToString(),
                PersistentDataPath = UnityEngine.Application.persistentDataPath,
                EntryCount = Entries.Count,
                Entries = Entries.ToArray()
            };

            File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
            return path;
        }

        public static string GetLogDirectory()
        {
            return Path.Combine(UnityEngine.Application.persistentDataPath, DirectoryName);
        }

        public static string GetLatestLogPath()
        {
            var directory = GetLogDirectory();
            if (!Directory.Exists(directory))
            {
                return string.Empty;
            }

            var files = Directory.GetFiles(directory, FilePrefix + "*" + FileExtension);
            if (files == null || files.Length == 0)
            {
                return string.Empty;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files[files.Length - 1];
        }

        public static bool TryRead(string path, out string content)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                content = string.Empty;
                return false;
            }

            content = File.ReadAllText(path);
            return true;
        }

        private static string GetHierarchyPath(UnityEngine.Object context)
        {
            var component = context as Component;
            if (component == null)
            {
                var gameObject = context as GameObject;
                if (gameObject == null)
                {
                    return string.Empty;
                }

                return GetTransformPath(gameObject.transform);
            }

            return GetTransformPath(component.transform);
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var names = new List<string>();
            var current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }
    }

    [Serializable]
    public sealed class UiDiagnosticLogSnapshot
    {
        public int SchemaVersion;
        public string ExportedAt;
        public string Reason;
        public string UnityVersion;
        public string ApplicationVersion;
        public string Platform;
        public string PersistentDataPath;
        public int EntryCount;
        public UiDiagnosticLogEntry[] Entries;
    }

    [Serializable]
    public sealed class UiDiagnosticLogEntry
    {
        public string Timestamp;
        public int Frame;
        public string Severity;
        public string Category;
        public string Message;
        public string ContextName;
        public string ContextType;
        public string HierarchyPath;
        public string Data;
    }
}
