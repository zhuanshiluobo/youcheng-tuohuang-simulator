using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    // 仅在编辑器手动导入配置，不接触场景、预制体或界面生成器。
    public static class CollectionExcelImporter
    {
        public const string WorkbookPath = "ConfigTables/收藏室配置.xlsx";
        public const string CatalogPath = "Assets/YC/Presentation/Collection/CollectionCatalog.asset";
        private const string ItemsFolder = "Assets/YC/Presentation/Collection/Items";
        private static readonly string[] Headers = { "内部编号", "稀有度", "中文", "英文", "描述文本", "启用", "默认展示", "获取条件文本", "模型路径", "聚光灯颜色" };

        [MenuItem("游城/收藏室/导入项目配置表")]
        private static void ImportProjectWorkbook() => ImportWithDialog(Path.GetFullPath(WorkbookPath));

        [MenuItem("游城/收藏室/选择外部配置表并导入")]
        private static void ImportExternalWorkbook()
        {
            string path = EditorUtility.OpenFilePanel("选择收藏室配置表", Path.GetFullPath("ConfigTables"), "xlsx");
            if (!string.IsNullOrEmpty(path)) ImportWithDialog(path);
        }

        [MenuItem("游城/收藏室/定位项目配置表")]
        private static void RevealWorkbook() => EditorUtility.RevealInFinder(Path.GetFullPath(WorkbookPath));

        private static void ImportWithDialog(string path)
        {
            try
            {
                string result = ImportWorkbook(path);
                Debug.Log(result);
                EditorUtility.DisplayDialog("收藏室配置导入完成", result, "确定");
            }
            catch (Exception ex)
            {
                Debug.LogError("收藏室配置导入失败：" + ex.Message);
                EditorUtility.DisplayDialog("收藏室配置导入失败", ex.Message, "确定");
            }
        }

        public static string ImportWorkbook(string path)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请先停止运行，再导入收藏室配置。");
            if (EditorApplication.isCompiling)
                throw new InvalidOperationException("请等待脚本编译完成后再导入。");
            var rows = ReadWorkbook(path);
            var catalog = AssetDatabase.LoadAssetAtPath<CollectionCatalog>(CatalogPath);
            if (catalog == null || !AssetDatabase.IsValidFolder(ItemsFolder))
                throw new InvalidOperationException("找不到现有收藏室目录或藏品配置文件夹，请检查收藏室资产。");

            // 按稳定 ID 复用资产，保留 GUID、外部引用和表格未管理的图标。
            var existing = AssetDatabase.FindAssets("t:CollectionItemDefinition", new[] { ItemsFolder })
                .Select(guid => AssetDatabase.LoadAssetAtPath<CollectionItemDefinition>(AssetDatabase.GUIDToAssetPath(guid)))
                .Concat(catalog.Items).Where(item => item != null).Distinct().ToList();
            var byId = new Dictionary<string, CollectionItemDefinition>(StringComparer.Ordinal);
            foreach (var item in existing)
            {
                if (string.IsNullOrWhiteSpace(item.ItemId)) continue;
                if (byId.ContainsKey(item.ItemId))
                    throw new InvalidOperationException("现有配置存在重复内部编号：" + item.ItemId + "，请先在 Inspector 中修正。");
                byId.Add(item.ItemId, item);
            }
            // 所有表格数据及资源引用在第一次写入前完成校验。
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.PrefabPath)) continue; // 允许资料先行，界面已有“暂无模型”状态。
                if (!row.PrefabPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                    !row.PrefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    throw RowError(row.Line, "模型预制体路径必须是 Assets/ 开头的 .prefab 项目路径。");
                row.Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(row.PrefabPath);
                if (row.Prefab == null || !PrefabUtility.IsPartOfPrefabAsset(row.Prefab))
                    throw RowError(row.Line, "找不到模型预制体：" + row.PrefabPath);
            }

            int created = 0;
            foreach (var row in rows)
            {
                CollectionItemDefinition item;
                if (!byId.TryGetValue(row.Id, out item))
                {
                    item = ScriptableObject.CreateInstance<CollectionItemDefinition>();
                    AssetDatabase.CreateAsset(item, AssetDatabase.GenerateUniqueAssetPath(ItemsFolder + "/" + row.Id + ".asset"));
                    byId.Add(row.Id, item);
                    created++;
                }
                var serialized = new SerializedObject(item);
                SetText(serialized, "itemId", row.Id);
                SetText(serialized, "displayName", row.Name);
                SetText(serialized, "englishName", row.EnglishName);
                SetText(serialized, "category", row.Rarity);
                SetText(serialized, "displayNumber", row.Id);
                SetText(serialized, "description", row.Description);
                SetText(serialized, "acquisitionRequirementText", row.AcquisitionRequirementText);
                serialized.FindProperty("defaultDisplay").boolValue = row.DefaultDisplay;
                serialized.FindProperty("overrideSpotlightColor").boolValue = row.HasSpotlightColor;
                serialized.FindProperty("spotlightColor").colorValue = row.SpotlightColor;
                serialized.FindProperty("displayPrefab").objectReferenceValue = row.Prefab;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssetIfDirty(item);
            }
            var enabled = rows.Where(row => row.Enabled).OrderBy(row => row.Line).ToList();
            var settings = new SerializedObject(catalog);
            var items = settings.FindProperty("items");
            items.arraySize = enabled.Count;
            for (int i = 0; i < enabled.Count; i++) items.GetArrayElementAtIndex(i).objectReferenceValue = byId[enabled[i].Id];
            settings.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(catalog);
            return string.Format("读取 {0} 行：新增 {1} 项，更新 {2} 项，目录启用 {3} 项。\n未列出或禁用的藏品不进入目录，已有资产不会被删除。\n未修改场景、模型预制体或聚光灯区。", rows.Count, created, rows.Count - created, enabled.Count);
        }

        private static void SetText(SerializedObject target, string field, string value) => target.FindProperty(field).stringValue = value;
        private static FormatException RowError(int row, string message) => new FormatException("藏品工作表第 " + row + " 行：" + message);

        public sealed class ItemRow
        {
            public int Line;
            public string Id, Name, EnglishName, Rarity, Description, AcquisitionRequirementText, PrefabPath;
            public bool Enabled, DefaultDisplay, HasSpotlightColor;
            public Color SpotlightColor = Color.white;
            public GameObject Prefab;
        }

        // 只读取本配置表需要的 Open XML 单元格，不引入运行时 Excel 依赖。
        public static List<ItemRow> ReadWorkbook(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("只支持 .xlsx，请在 Excel 中另存为 Excel 工作簿；不支持 .xls 或 .xlsm。");
            if (!File.Exists(path)) throw new FileNotFoundException("找不到配置表：" + path);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
            {
                var workbook = ReadXml(zip, "xl/workbook.xml");
                XNamespace ns = workbook.Root.Name.Namespace;
                var sheet = workbook.Descendants(ns + "sheet").FirstOrDefault(x => (string)x.Attribute("name") == "藏品");
                if (sheet == null) throw new FormatException("找不到名为“藏品”的工作表，请使用项目配置表的表头。");
                var relationId = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id");
                var relations = ReadXml(zip, "xl/_rels/workbook.xml.rels");
                var relation = relations.Root.Elements().FirstOrDefault(x => (string)x.Attribute("Id") == relationId?.Value);
                if (relation == null || (string)relation.Attribute("TargetMode") == "External")
                    throw new FormatException("藏品工作表引用无效，请重新另存为 .xlsx。");
                string sheetPath = ResolvePart((string)relation.Attribute("Target"));
                var shared = new List<string>();
                var sharedRelation = relations.Root.Elements().FirstOrDefault(x => ((string)x.Attribute("Type") ?? "").EndsWith("/sharedStrings", StringComparison.Ordinal));
                if (sharedRelation != null)
                {
                    var strings = ReadXml(zip, ResolvePart((string)sharedRelation.Attribute("Target")));
                    shared.AddRange(strings.Root.Elements().Select(ReadString));
                }
                var worksheet = ReadXml(zip, sheetPath);
                ns = worksheet.Root.Name.Namespace;
                var data = worksheet.Root.Element(ns + "sheetData");
                if (data == null) throw new FormatException("藏品工作表没有数据。");
                var headerRow = data.Elements(ns + "row").FirstOrDefault(r => (string)r.Attribute("r") == "1");
                if (headerRow == null) throw new FormatException("请在藏品工作表第 1 行放置表头。");
                var headers = ReadCells(headerRow, shared);
                var columns = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string header in Headers)
                {
                    var matches = headers.Where(pair => pair.Value.Trim() == header).ToList();
                    if (matches.Count != 1) throw new FormatException("藏品工作表第 1 行必须且只能包含一个表头：“" + header + "”。");
                    columns.Add(header, matches[0].Key);
                }
                var result = new List<ItemRow>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var xmlRow in data.Elements(ns + "row").Where(r => (string)r.Attribute("r") != "1"))
                {
                    int line = (int?)xmlRow.Attribute("r") ?? 0;
                    var cells = ReadCells(xmlRow, shared);
                    Func<string, string> get = key => cells.ContainsKey(columns[key]) ? cells[columns[key]].Trim() : "";
                    if (Headers.All(key => get(key).Length == 0)) continue;
                    var row = new ItemRow { Line = line, Id = get("内部编号"), Name = get("中文"), EnglishName = get("英文"), Rarity = get("稀有度"), Description = get("描述文本"), AcquisitionRequirementText = get("获取条件文本"), PrefabPath = get("模型路径").Replace('\\', '/') };
                    if (!Regex.IsMatch(row.Id, "^[0-9]+$"))
                        throw RowError(line, "内部编号请填写不重复的数字文本，例如 01、02；请将 Excel 单元格设为文本以保留前导零。");
                    if (!ids.Add(row.Id)) throw RowError(line, "内部编号重复：" + row.Id);
                    if (row.Name.Length == 0) throw RowError(line, "中文名称不能为空。");
                    row.HasSpotlightColor = CollectionRarityPalette.TryGetSpotlightColor(row.Rarity, out row.SpotlightColor);
                    if (!row.HasSpotlightColor)
                        throw RowError(line, "稀有度必填，且只能填写：常见、普通、精良、罕见、稀有。");
                    row.Enabled = ParseBool(get("启用"), line, "启用");
                    row.DefaultDisplay = ParseBool(get("默认展示"), line, "默认展示");
                    // 表格灯色列用于展示标准色；导入及运行时均以稀有度绑定结果为准。
                    result.Add(row);
                }
                if (result.Count == 0) throw new FormatException("配置表没有藏品数据。为避免误清空目录，空表不导入；需要空目录时请将已有行的启用设为“否”。");
                return result;
            }
        }

        private static bool ParseBool(string value, int line, string column)
        {
            switch (value.ToLowerInvariant())
            {
                case "是": case "true": case "1": return true;
                case "否": case "false": case "0": return false;
                default: throw RowError(line, column + "请填写“是”或“否”。");
            }
        }

        private static Dictionary<int, string> ReadCells(XElement row, List<string> shared)
        {
            var cells = new Dictionary<int, string>();
            XNamespace ns = row.Name.Namespace;
            int line = (int?)row.Attribute("r") ?? 0;
            foreach (var cell in row.Elements(ns + "c"))
            {
                string address = (string)cell.Attribute("r") ?? "";
                int column = 0;
                foreach (char ch in address.TakeWhile(c => c >= 'A' && c <= 'Z')) column = column * 26 + ch - 'A' + 1;
                if (column == 0) throw RowError(line, "无效单元格地址：" + address);
                if (cell.Element(ns + "f") != null) throw RowError(line, address + " 不支持公式，请粘贴为值后导入。");
                string type = (string)cell.Attribute("t");
                string value = (string)cell.Element(ns + "v") ?? "";
                if (type == "e") throw RowError(line, address + " 含 Excel 错误值。");
                if (type == "s")
                {
                    int index;
                    if (!int.TryParse(value, out index) || index < 0 || index >= shared.Count) throw RowError(line, address + " 的共享文本索引无效。");
                    value = shared[index];
                }
                else if (type == "inlineStr") value = ReadString(cell.Element(ns + "is"));
                cells[column] = value;
            }
            return cells;
        }

        private static string ReadString(XElement element)
        {
            if (element == null) return "";
            XNamespace ns = element.Name.Namespace;
            return string.Concat(element.Elements().Where(e => e.Name == ns + "t" || e.Name == ns + "r")
                .Select(e => e.Name == ns + "t" ? e.Value : string.Concat(e.Elements(ns + "t").Select(t => t.Value))));
        }

        private static string ResolvePart(string target)
        {
            if (string.IsNullOrEmpty(target)) throw new FormatException("工作簿缺少内部文件引用。");
            return Uri.UnescapeDataString(new Uri(new Uri("http://xlsx.local/xl/workbook.xml"), target).AbsolutePath.TrimStart('/'));
        }

        private static XDocument ReadXml(ZipArchive zip, string path)
        {
            var part = zip.GetEntry(path);
            if (part == null) throw new FormatException("工作簿缺少内部文件：" + path);
            using (var stream = part.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                return XDocument.Load(reader);
        }
    }
}
