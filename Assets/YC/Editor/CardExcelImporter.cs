using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MoonSharp.Interpreter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace YC.Editor
{
    /// <summary>手动将卡牌资料表写回现有 JSON；不生成界面，不修改 Lua 文件。</summary>
    public static class CardExcelImporter
    {
        public const string WorkbookPath = "ConfigTables/卡牌资料.xlsx";
        private const string ContentRoot = "Assets/StreamingAssets/Content/core";

        [MenuItem("游城/卡牌/导入项目资料表")]
        private static void ImportProjectWorkbook() { ImportWithDialog(Path.GetFullPath(WorkbookPath)); }

        [MenuItem("游城/卡牌/选择外部资料表并导入")]
        private static void ImportExternalWorkbook()
        {
            string path = EditorUtility.OpenFilePanel("选择卡牌资料表", Path.GetFullPath("ConfigTables"), "xlsx");
            if (!string.IsNullOrEmpty(path)) ImportWithDialog(path);
        }

        [MenuItem("游城/卡牌/定位项目资料表")]
        private static void RevealWorkbook() { EditorUtility.RevealInFinder(Path.GetFullPath(WorkbookPath)); }

        [MenuItem("游城/卡牌/打开效果脚本目录")]
        private static void RevealScripts() { EditorUtility.RevealInFinder(Path.GetFullPath(ContentRoot + "/lua")); }

        private static void ImportWithDialog(string path)
        {
            try
            {
                string result = ImportWorkbook(path);
                AssetDatabase.Refresh();
                Debug.Log(result);
                EditorUtility.DisplayDialog("卡牌资料导入完成", result, "确定");
            }
            catch (Exception ex)
            {
                Debug.LogError("卡牌资料导入失败：" + ex.Message);
                EditorUtility.DisplayDialog("卡牌资料导入失败", ex.Message, "确定");
            }
        }

        public static string ImportWorkbook(string path)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请先停止运行，再导入卡牌资料。");
            if (EditorApplication.isCompiling)
                throw new InvalidOperationException("请等待脚本编译完成后再导入。");
            string root = Path.GetFullPath(ContentRoot);
            var documents = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var originals = new Dictionary<string, JObject>(StringComparer.Ordinal);
            Func<string, JObject> read = relative =>
            {
                if (!documents.ContainsKey(relative))
                {
                    var json = JObject.Parse(File.ReadAllText(ResolvePath(root, relative)));
                    documents.Add(relative, json);
                    originals.Add(relative, (JObject)json.DeepClone());
                }
                return documents[relative];
            };
            var manifest = read("pack.json");
            var cards = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (string relative in manifest["definitions"].Values<string>())
            {
                var card = read(relative);
                cards.Add((string)card["definitionId"], card);
            }
            string inventoryPath = (string)manifest["facilityInventory"];
            var inventory = read(inventoryPath);
            var groups = ((JArray)inventory["groups"]).OfType<JObject>()
                .ToDictionary(group => (string)group["idPrefix"], StringComparer.Ordinal);
            var facilityImages = new Dictionary<string, FacilityImage>(StringComparer.Ordinal);
            foreach (var pair in groups)
            {
                foreach (JObject variant in pair.Value["variants"])
                {
                    var instances = variant["instances"] as JArray ?? new JArray();
                    int count = (int)variant["count"];
                    for (int i = 0; i < count; i++)
                    {
                        // 将自动编号也作为可维护的实体卡面，保留原来的编号顺序。
                        var instance = i < instances.Count ? (JObject)instances[i] : null;
                        string id = instance == null ? pair.Key + "_" + (string)variant["color"] + "_" + (i + 1).ToString("000", CultureInfo.InvariantCulture) : (string)instance["id"];
                        facilityImages.Add(id, new FacilityImage { Group = pair.Key, Variant = variant, Index = i });
                    }
                }
            }
            int rows = 0;
            using (var workbook = new WorkbookReader(path))
            {
                foreach (var row in workbook.ReadSheet("角色牌", "内部编号", "名称", "说明", "卡面图片", "策略名称", "策略说明", "策略脚本", "策略版本", "策略收尾脚本", "计谋名称", "计谋说明", "计谋脚本", "计谋版本", "计谋收尾脚本"))
                {
                    var card = FindCard(cards, row, "character");
                    UpdateName(card, row);
                    UpdateArtwork(card, row, root);
                    var data = (JObject)card["data"];
                    SetText(data, "description", row.Get("说明"));
                    UpdateAbility(card, data, row, "策略", "strategy", root);
                    UpdateAbility(card, data, row, "计谋", "tactic", root);
                    rows++;
                }
                foreach (var row in workbook.ReadSheet("事件牌", "内部编号", "名称", "描述文本", "卡面图片"))
                {
                    var card = FindCard(cards, row, "event");
                    UpdateName(card, row);
                    UpdateArtwork(card, row, root);
                    SetText((JObject)card["data"], "description", row.Get("描述文本"));
                    rows++;
                }
                foreach (var row in workbook.ReadSheet("事件选项", "内部编号", "选项序号", "选项说明"))
                {
                    var card = FindCard(cards, row, "event");
                    var choices = (JArray)card["data"]["choiceDescriptions"];
                    int index;
                    if (!int.TryParse(row.Get("选项序号"), NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 1 || index > choices.Count)
                        throw row.Error("选项序号必须对应已有选项，从 1 开始。");
                    string text = row.Required("选项说明");
                    choices[index - 1] = text;
                    rows++;
                }
                foreach (var row in workbook.ReadSheet("设施牌", "内部编号", "名称", "描述文本", "效果说明", "效果脚本"))
                {
                    JObject group;
                    if (!groups.TryGetValue(row.Required("内部编号"), out group))
                        throw row.Error("找不到已有设施类型，请保持内部编号不变。");
                    var definition = (JObject)group["definition"];
                    var effective = new JObject();
                    string template = (string)definition["dataTemplate"];
                    if (!string.IsNullOrEmpty(template)) effective = (JObject)read(template)["data"].DeepClone();
                    var data = definition["data"] as JObject;
                    if (data == null) { data = new JObject(); definition["data"] = data; }
                    effective.Merge(data, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Merge });
                    definition["displayName"] = row.Required("名称");
                    SetOverride(data, effective, "name", row.Get("名称"));
                    SetOverride(data, effective, "description", row.Get("描述文本"));
                    SetOverride(data, effective, "effectText", row.Get("效果说明"));
                    string script = row.Required("效果脚本");
                    ValidateScript(root, script, row);
                    SetOverride(data, effective, "effectScript", script);
                    rows++;
                }
                foreach (var row in workbook.ReadSheet("设施卡面", "内部编号", "设施类型", "名称", "颜色", "卡面图片"))
                {
                    FacilityImage target;
                    if (!facilityImages.TryGetValue(row.Required("内部编号"), out target))
                        throw row.Error("找不到已有设施实体，请保持内部编号不变。");
                    var variant = target.Variant;
                    if (row.Get("设施类型") != target.Group || row.Get("颜色") != (string)variant["color"])
                        throw row.Error("设施类型和颜色是定位资料，请保持不变。");
                    string image = row.Required("卡面图片");
                    ValidateArtwork(root, image, row);
                    int index = target.Index;
                    var instances = variant["instances"] as JArray ?? new JArray();
                    var instance = index < instances.Count ? (JObject)instances[index] : null;
                    string previous = instance == null || instance["artwork"] == null ? (string)variant["artwork"] : (string)instance["artwork"];
                    if (previous != image)
                    {
                        if (variant["instances"] == null) variant["instances"] = instances;
                        while (instances.Count <= index)
                            instances.Add(new JObject { ["id"] = target.Group + "_" + (string)variant["color"] + "_" + (instances.Count + 1).ToString("000", CultureInfo.InvariantCulture) });
                        instances[index]["artwork"] = image;
                    }
                    rows++;
                }
                foreach (var row in workbook.ReadSheet("城市样式牌", "内部编号", "名称", "宣告说明", "特殊行动说明", "卡面图片", "效果脚本", "脚本版本"))
                {
                    var card = FindCard(cards, row, "city_style");
                    UpdateName(card, row);
                    UpdateArtwork(card, row, root);
                    var data = (JObject)card["data"];
                    SetText(data, "description", row.Get("宣告说明"));
                    var specialAction = data["specialAction"] as JObject;
                    if (specialAction != null)
                    {
                        SetText(specialAction, "description", row.Get("特殊行动说明"));
                        SetText(specialAction, "name", row.Get("名称"));
                    }
                    else if (row.Get("特殊行动说明").Length > 0)
                        throw row.Error("此城市样式没有特殊行动，请保持特殊行动说明为空。");
                    if (specialAction != null)
                    {
                        string script = row.Required("效果脚本");
                        ValidateScript(root, script, row);
                        SetText(data, "effectScript", script);
                        SetText(data, "luaVersion", row.Required("脚本版本"));
                    }
                    else if (row.Get("效果脚本").Length > 0 || row.Get("脚本版本").Length > 0)
                        throw row.Error("此城市样式没有脚本执行入口，请保持效果脚本和版本为空。");
                    rows++;
                }
            }
            // 校验全部行后才写回，未纳入表格的字段和未列出的卡牌保持原值。
            int updated = 0;
            foreach (var pair in documents)
            {
                if (JToken.DeepEquals(pair.Value, originals[pair.Key])) continue;
                File.WriteAllText(ResolvePath(root, pair.Key), pair.Value.ToString(Newtonsoft.Json.Formatting.Indented) + "\n", new UTF8Encoding(false));
                updated++;
            }
            return string.Format("读取 {0} 行，更新 {1} 个 JSON 文件。\n效果脚本由独立 Lua 文件维护；未修改脚本、场景或预制体。\n重新进入游戏后加载新资料，图片上的文字需另行修改卡面图片。", rows, updated);
        }

        private static JObject FindCard(Dictionary<string, JObject> cards, Row row, string type)
        {
            JObject card;
            if (!cards.TryGetValue(row.Required("内部编号"), out card) || (string)card["contentType"] != type)
                throw row.Error("找不到对应类型的已有卡牌，请保持内部编号不变。");
            return card;
        }
        private static void UpdateName(JObject card, Row row)
        {
            string name = row.Required("名称");
            card["displayName"] = name;
            SetText((JObject)card["data"], "name", name);
        }
        private static void UpdateArtwork(JObject card, Row row, string root)
        {
            string image = row.Required("卡面图片");
            ValidateArtwork(root, image, row);
            card["artwork"] = image;
        }
        private static void UpdateAbility(JObject card, JObject data, Row row, string label, string mode, string root)
        {
            var ability = card["abilities"].OfType<JObject>().Single(value => (string)value["mode"] == mode);
            string script = row.Required(label + "脚本");
            ValidateScript(root, script, row);
            string cleanup = row.Get(label + "收尾脚本");
            if (cleanup.Length > 0) ValidateScript(root, cleanup, row);
            SetText(ability, "script", script);
            SetText(ability, "version", row.Required(label + "版本"));
            SetText(ability, "cleanupScript", cleanup);
            SetText(data, mode + "Name", row.Get(label + "名称"));
            SetText(data, mode + "Text", row.Get(label + "说明"));
        }
        private static void SetText(JObject target, string key, string value)
        {
            // 空的新增说明不产生多余字段；已有字段可以显式清空。
            if (target[key] != null || value.Length > 0) target[key] = value;
        }
        private static void SetOverride(JObject target, JObject effective, string key, string value)
        {
            if (((string)effective[key] ?? "") != value) target[key] = value;
        }
        private static string ResolvePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                throw new FormatException("请填写内容包内的相对路径：" + relative);
            string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("路径不能离开当前内容包：" + relative);
            if (!File.Exists(path)) throw new FileNotFoundException("找不到内容文件：" + relative);
            return path;
        }
        private static void ValidateArtwork(string root, string relative, Row row)
        {
            string ext = Path.GetExtension(relative).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") throw row.Error("卡面图片必须为 PNG 或 JPEG。");
            ResolvePath(root, relative);
        }
        private static void ValidateScript(string root, string relative, Row row)
        {
            if (Path.GetExtension(relative) != ".lua") throw row.Error("效果脚本必须为 .lua 文件。");
            string script = File.ReadAllText(ResolvePath(root, relative));
            try { new Script(CoreModules.None).LoadString(script, null, relative); }
            catch (Exception ex) { throw row.Error("Lua 语法错误：" + relative + "，" + ex.Message); }
        }

        private sealed class FacilityImage
        {
            public string Group;
            public JObject Variant;
            public int Index;
        }

        private sealed class Row
        {
            public string Sheet;
            public int Line;
            public Dictionary<string, string> Cells;
            public string Get(string name) { return Cells[name].Trim(); }
            public string Required(string name)
            {
                string value = Get(name);
                if (value.Length == 0) throw Error(name + "不能为空。");
                return value;
            }
            public FormatException Error(string message) { return new FormatException(Sheet + "第 " + Line + " 行：" + message); }
        }

        private sealed class WorkbookReader : IDisposable
        {
            private readonly ZipArchive zip;
            private readonly Dictionary<string, string> sheets = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly List<string> shared = new List<string>();
            public WorkbookReader(string path)
            {
                if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("只支持 .xlsx，请将工作簿另存为 Excel 工作簿。");
                zip = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), ZipArchiveMode.Read);
                try
                {
                    var workbook = ReadXml("xl/workbook.xml");
                    var relations = ReadXml("xl/_rels/workbook.xml.rels");
                    XNamespace ns = workbook.Root.Name.Namespace;
                    foreach (var sheet in workbook.Descendants(ns + "sheet"))
                    {
                        string id = sheet.Attributes().Single(a => a.Name.LocalName == "id").Value;
                        var relation = relations.Root.Elements().Single(value => (string)value.Attribute("Id") == id);
                        if ((string)relation.Attribute("TargetMode") == "External") throw new FormatException("不支持外部工作表引用。");
                        sheets.Add((string)sheet.Attribute("name"), ResolvePart((string)relation.Attribute("Target")));
                    }
                    var strings = relations.Root.Elements().FirstOrDefault(value => ((string)value.Attribute("Type") ?? "").EndsWith("/sharedStrings", StringComparison.Ordinal));
                    if (strings != null) shared.AddRange(ReadXml(ResolvePart((string)strings.Attribute("Target"))).Root.Elements().Select(ReadString));
                }
                catch { zip.Dispose(); throw; }
            }
            public List<Row> ReadSheet(string name, params string[] headers)
            {
                string part;
                if (!sheets.TryGetValue(name, out part)) throw new FormatException("找不到工作表：" + name);
                var xml = ReadXml(part);
                XNamespace ns = xml.Root.Name.Namespace;
                var data = xml.Root.Element(ns + "sheetData");
                var first = data == null ? null : data.Elements(ns + "row").FirstOrDefault(row => (int?)row.Attribute("r") == 1);
                if (first == null) throw new FormatException(name + "第 1 行必须为表头。");
                var labels = ReadCells(first);
                var columns = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string header in headers)
                {
                    var matches = labels.Where(pair => pair.Value.Trim() == header).ToList();
                    if (matches.Count != 1) throw new FormatException(name + "必须且只能包含一个表头：" + header);
                    columns.Add(header, matches[0].Key);
                }
                var result = new List<Row>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var element in data.Elements(ns + "row").Where(row => (int?)row.Attribute("r") != 1))
                {
                    var cells = ReadCells(element);
                    var row = new Row { Sheet = name, Line = (int?)element.Attribute("r") ?? 0, Cells = new Dictionary<string, string>(StringComparer.Ordinal) };
                    foreach (var pair in columns) row.Cells.Add(pair.Key, cells.ContainsKey(pair.Value) ? cells[pair.Value] : "");
                    if (headers.All(header => row.Get(header).Length == 0)) continue;
                    string id = row.Required("内部编号");
                    if (name == "事件选项") id += ":" + row.Get("选项序号");
                    if (!ids.Add(id)) throw row.Error("内部编号或选项重复：" + id);
                    result.Add(row);
                }
                if (result.Count == 0) throw new FormatException(name + "没有资料，请保留现有数据行。");
                return result;
            }
            private Dictionary<int, string> ReadCells(XElement row)
            {
                var cells = new Dictionary<int, string>();
                XNamespace ns = row.Name.Namespace;
                foreach (var cell in row.Elements(ns + "c"))
                {
                    string address = (string)cell.Attribute("r") ?? "";
                    int column = 0;
                    foreach (char ch in address.TakeWhile(ch => ch >= 'A' && ch <= 'Z')) column = column * 26 + ch - 'A' + 1;
                    if (column == 0) throw new FormatException("无效单元格地址：" + address);
                    if (cell.Element(ns + "f") != null) throw new FormatException("不支持公式，请将 " + address + " 粘贴为值。");
                    string type = (string)cell.Attribute("t");
                    string value = (string)cell.Element(ns + "v") ?? "";
                    if (type == "e") throw new FormatException(address + " 含 Excel 错误值。");
                    if (type == "s")
                    {
                        int index;
                        if (!int.TryParse(value, out index) || index < 0 || index >= shared.Count) throw new FormatException(address + " 文本索引无效。");
                        value = shared[index];
                    }
                    else if (type == "inlineStr") value = ReadString(cell.Element(ns + "is"));
                    cells.Add(column, value);
                }
                return cells;
            }
            private static string ReadString(XElement element)
            {
                if (element == null) return "";
                XNamespace ns = element.Name.Namespace;
                return string.Concat(element.Elements().Where(value => value.Name == ns + "t" || value.Name == ns + "r")
                    .Select(value => value.Name == ns + "t" ? value.Value : string.Concat(value.Elements(ns + "t").Select(text => text.Value))));
            }
            private static string ResolvePart(string target)
            {
                return Uri.UnescapeDataString(new Uri(new Uri("http://xlsx.local/xl/workbook.xml"), target).AbsolutePath.TrimStart('/'));
            }
            private XDocument ReadXml(string path)
            {
                var entry = zip.GetEntry(path);
                if (entry == null) throw new FormatException("工作簿缺少文件：" + path);
                using (var stream = entry.Open())
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    return XDocument.Load(reader);
            }
            public void Dispose() { zip.Dispose(); }
        }
    }
}
