using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    // 定向只读导入诊断；不加载场景、不激活克隆、不保存或重建任何资产。
    public sealed class GameplayPageAssetImportDiagnosticsTests
    {
        private const string Root = "Assets/YC/Presentation/Prefabs/Gameplay/";
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<string> issues = new List<string>();

        [Test]
        public void ImportedPageTemplatesExposeReferencesWithoutRunningHudAwake()
        {
            issues.Clear();
            var report = new JObject
            {
                ["utc"] = DateTime.UtcNow.ToString("O"),
                ["unityVersion"] = UnityEngine.Application.unityVersion,
                ["scope"] = "仅LoadAssetAtPath读取资产及inactive宿主下的六个组件克隆；不运行HUD Awake、不保存资产"
            };
            var assets = new JArray();
            var clones = new JArray();
            report["assets"] = assets;
            report["inactiveClones"] = clones;
            var host = new GameObject("页面导入诊断禁用宿主");
            host.SetActive(false);
            try
            {
                foreach (var relative in new[]
                {
                    "CityStyles/CityStyleStatusRow.prefab", "CityStyles/CityStyleActiveStatusRow.prefab",
                    "CityStyles/CityStyleRewardStatusRow.prefab", "CityStyles/CityStyleLimitedStatusRow.prefab",
                    "CityStyles/CityStyleCardItem.prefab", "Dialogs/Enterprise/DepartmentCandidate.prefab"
                })
                {
                    var path = Root + relative;
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    assets.Add(InspectPrefab(asset, path));
                    if (asset == null) continue;
                    var clone = Object.Instantiate(asset, host.transform, false);
                    clones.Add(InspectPrefab(clone, path + "（禁用宿主克隆）"));
                }
                var entries = new JArray();
                report["consumerEntries"] = entries;
                foreach (var entry in new[]
                {
                    new[] { "GameplayInteractionHud.prefab", "BuildInfoPanelView", "cityStyleRowTemplates" },
                    new[] { "Dialogs/CityStyleDeclarationPreviewDialog.prefab", "CityStyleDeclarationPreviewView", "optionTemplate" },
                    new[] { "Dialogs/Enterprise/DepartmentSelectionDialog.prefab", "EnterpriseSelectionDialogView", "boardTemplate" },
                    new[] { "Dialogs/Enterprise/EnterpriseSelectionDialog.prefab", "EnterpriseSelectionDialogView", "boardTemplate" }
                })
                {
                    var path = Root + entry[0];
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var view = asset == null ? null : asset.GetComponentInChildren(T(entry[1]), true);
                    var info = new JObject { ["path"] = path, ["view"] = Identity(view) };
                    entries.Add(info);
                    if (view == null) { issues.Add(path + "：缺少" + entry[1]); continue; }
                    info["references"] = References(view);
                    var value = Read(view, entry[2]);
                    if (value is Array array)
                    {
                        info["templateCount"] = array.Length;
                        info["templates"] = new JArray(array.Cast<object>().Select(v => InspectRow(v as Component, path)));
                        if (array.Length != 3) issues.Add(path + "：状态行数量不是3");
                    }
                    else info["template"] = Identity(value as Object);
                    var args = new object[] { null };
                    var valid = (bool)view.GetType().GetMethod("TryValidateConfiguration", Members).Invoke(view, args);
                    info["valid"] = valid;
                    info["reason"] = args[0]?.ToString();
                    if (!valid) issues.Add(path + "：" + args[0]);
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
                report["issues"] = new JArray(issues);
                var directory = Environment.GetEnvironmentVariable("YC_PAGE_IMPORT_DIAGNOSTICS");
                if (string.IsNullOrEmpty(directory)) directory = Path.GetFullPath("Logs/GameplayUiRework/import-diagnostics");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "imported-references.json");
                File.WriteAllText(path, report.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
                TestContext.Out.WriteLine("实际导入引用诊断：" + path);
            }
            Assert.That(issues, Is.Empty, "实际导入引用存在问题，完整记录已先写入JSON：\n" + string.Join("\n", issues));
        }

        private JObject InspectPrefab(GameObject value, string context)
        {
            var result = new JObject { ["context"] = context, ["root"] = Identity(value) };
            if (value == null) { issues.Add(context + "：LoadAssetAtPath为空"); return result; }
            var components = value.GetComponentsInChildren<Component>(true);
            result["missingScriptCount"] = components.Count(c => c == null);
            if (components.Any(c => c == null)) issues.Add(context + "：存在丢失脚本");
            var records = new JArray();
            result["components"] = records;
            foreach (var component in components.Where(c => c != null && c.GetType().Namespace == "YC.Presentation"))
            {
                records.Add(new JObject { ["component"] = Identity(component), ["references"] = References(component) });
                switch (component.GetType().Name)
                {
                    case "CityStyleStatusRowView":
                        result["row"] = InspectRow(component, context);
                        break;
                    case "BuildInfoItemView":
                        var args = new[] { Read(component, "Kind"), (object)null };
                        var valid = (bool)component.GetType().GetMethod("TryValidateAs", Members).Invoke(component, args);
                        result["itemValidation"] = new JObject { ["valid"] = valid, ["reason"] = args[1]?.ToString() };
                        if (!valid) issues.Add(context + "：" + args[1]);
                        break;
                    case "CityStyleCardStateView":
                        RequireReferences(component, context, "stateFrame", "availableFrame", "selectedFrame", "unavailableFrame", "checkIcon");
                        break;
                    case "EnterpriseBoardItemView":
                        RequireReferences(component, context, "boardImage", "boardAspect", "targetRoot", "targetTemplate");
                        // 第三轮候选Footer及固定板交互入口已经授权移除，不能继续作为必填引用。
                        var standardMarkers = Read(component, "playerLevelMarkers") as Array;
                        if (standardMarkers != null && standardMarkers.Length > 0) RequireArray(component, context, "playerLevelMarkers");
                        else RequireArray(component, context, "playerLevels");
                        RequireArray(component, context, "rewardLabels");
                        break;
                }
            }
            return result;
        }

        private JObject InspectRow(Component row, string context)
        {
            var result = new JObject { ["component"] = Identity(row), ["null"] = row == null };
            if (row == null) { issues.Add(context + "：状态行直接引用为空"); return result; }
            var hasName = (bool)Read(row, "HasNameText");
            result["hasNameText"] = hasName;
            result["activeSelf"] = row.gameObject.activeSelf;
            result["trackLayout"] = Read(row, "TrackLayout")?.ToString();
            result["nameText"] = Identity(Read(row, "nameText") as Object);
            result["references"] = References(row);
            if (!hasName || row.gameObject.activeSelf) issues.Add(context + "：HasNameText=" + hasName + "，activeSelf=" + row.gameObject.activeSelf);
            var slots = Read(row, "slots") as Array;
            var slotRecords = new JArray();
            result["slots"] = slotRecords;
            if (slots == null || slots.Length == 0) issues.Add(context + "：slots为空");
            if (slots != null) foreach (var slot in slots)
            {
                var entry = new JObject { ["null"] = slot == null };
                slotRecords.Add(entry);
                if (slot == null) { issues.Add(context + "：slot元素为空"); continue; }
                entry["area"] = Read(slot, "area")?.ToString();
                foreach (var field in new[] { "blocks", "counts" })
                {
                    var array = Read(slot, field) as Array;
                    entry[field] = array == null ? JValue.CreateNull() : new JArray(array.Cast<object>().Select(v => Identity(v as Object)));
                    if (array == null || array.Length != 4 || array.Cast<object>().Any(v => v as Object == null))
                        issues.Add(context + "：slot " + entry["area"] + "." + field + "引用不完整");
                }
            }
            return result;
        }

        private void RequireReferences(Component component, string context, params string[] fields)
        {
            foreach (var field in fields)
                if (Read(component, field) as Object == null) issues.Add(context + "：" + component.GetType().Name + "." + field + "为空");
        }

        private void RequireArray(Component component, string context, string field)
        {
            var values = Read(component, field) as Array;
            if (values == null || values.Cast<object>().Any(v => v as Object == null)) issues.Add(context + "：" + field + "包含空引用");
        }

        private static JArray References(Component component)
        {
            var result = new JArray();
            using (var serialized = new SerializedObject(component))
            {
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference)
                        result.Add(new JObject { ["field"] = property.propertyPath,
                            ["instanceId"] = property.objectReferenceInstanceIDValue,
                            ["value"] = Identity(property.objectReferenceValue) });
            }
            return result;
        }

        private static JObject Identity(Object value, bool includeSource = true)
        {
            var result = new JObject { ["null"] = value == null };
            if (value == null) return result;
            result["name"] = value.name;
            result["type"] = value.GetType().FullName;
            result["instanceId"] = value.GetInstanceID();
            result["assetPath"] = AssetDatabase.GetAssetPath(value);
            result["hasAssetIdentity"] = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId);
            result["guid"] = guid;
            result["localFileId"] = localId;
            var gameObject = value as GameObject ?? (value as Component)?.gameObject;
            if (gameObject != null)
            {
                result["activeSelf"] = gameObject.activeSelf;
                result["activeInHierarchy"] = gameObject.activeInHierarchy;
                result["hierarchy"] = string.Join("/", gameObject.GetComponentsInParent<Transform>(true).Reverse().Select(t => t.name));
            }
            if (includeSource) result["correspondingSource"] = Identity(PrefabUtility.GetCorrespondingObjectFromSource(value), false);
            return result;
        }

        private static Type T(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("YC.Presentation." + name)).First(t => t != null);
        private static object Read(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value) ??
            value.GetType().GetProperty(name, Members)?.GetValue(value);
    }
}
