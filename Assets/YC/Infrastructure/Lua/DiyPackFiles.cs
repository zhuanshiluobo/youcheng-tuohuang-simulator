using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MoonSharp.Interpreter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YC.Infrastructure.Lua
{
    /// <summary>发布、安装和游戏读取共用的 v1 资源限制。</summary>
    public static class DiyPackLimits
    {
        public const int MaximumFiles = 4096;
        public const long MaximumFileBytes = 64L * 1024 * 1024;
        public const long MaximumTotalBytes = 512L * 1024 * 1024;
        public const int MaximumLuaBytes = 32 * 1024;
        public const int MaximumImageDimension = 8192;
        public const long MaximumImagePixels = 32L * 1024 * 1024;
    }

    public static class DiyPackPaths
    {
        private static readonly HashSet<string> Devices = new HashSet<string>(new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }, StringComparer.OrdinalIgnoreCase);
        public static string ValidateRelative(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\\') >= 0 || relative[0] == '/' || Path.IsPathRooted(relative) || relative.Length > 512)
                throw new InvalidDataException("包内路径必须是使用 / 的相对路径：" + relative);
            foreach (string part in relative.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.Any(c => c < 32 || "<>:\"|?*".IndexOf(c) >= 0) || Devices.Contains(part.Split('.')[0]))
                    throw new InvalidDataException("包内路径包含无效或保留名称：" + relative);
            }
            return relative;
        }

        internal static void RejectLinksToRoot(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("内容路径不能包含目录联接或符号链接：" + current);
        }
    }

    /// <summary>冻结完整文件树；读取后不再访问磁盘，身份不包含绝对路径或时间。</summary>
    public sealed class DiyFrozenFiles
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly Dictionary<string, byte[]> files;
        public string ContentHash { get; }
        public long TotalBytes { get; }
        public IReadOnlyList<string> Paths { get; }
        private DiyFrozenFiles(Dictionary<string, byte[]> files, long bytes)
        {
            this.files = files; TotalBytes = bytes;
            Paths = Array.AsReadOnly(files.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            using (var input = new MemoryStream())
            using (var writer = new BinaryWriter(input, Encoding.UTF8, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("nomadcity.diy.files.v1\0"));
                foreach (string path in Paths)
                {
                    byte[] name = Encoding.UTF8.GetBytes(path), data = files[path];
                    writer.Write(name.Length); writer.Write(name); writer.Write((long)data.Length);
                    using (var hash = SHA256.Create()) writer.Write(hash.ComputeHash(data));
                }
                writer.Flush(); input.Position = 0;
                using (var hash = SHA256.Create()) ContentHash = Hex(hash.ComputeHash(input));
            }
        }

        public static DiyFrozenFiles Capture(string directory)
        {
            string root = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            DiyPackPaths.RejectLinksToRoot(root);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            void Walk(string current, string prefix)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(current).OrderBy(x => x, StringComparer.Ordinal))
                {
                    string relative = prefix + Path.GetFileName(path);
                    DiyPackPaths.ValidateRelative(relative);
                    if (!seen.Add(relative)) throw new InvalidDataException("包内路径大小写冲突：" + relative);
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("包内不能包含目录联接或符号链接：" + relative);
                    if ((attributes & FileAttributes.Directory) != 0) { Walk(path, relative + "/"); continue; }
                    if (files.Count >= DiyPackLimits.MaximumFiles) throw new InvalidDataException("包文件数量超过上限：" + relative);
                    long length = new FileInfo(path).Length;
                    if (length < 0 || length > DiyPackLimits.MaximumFileBytes || total + length > DiyPackLimits.MaximumTotalBytes)
                        throw new InvalidDataException("包文件或总大小超过上限：" + relative);
                    byte[] bytes = File.ReadAllBytes(path);
                    if (bytes.LongLength != length) throw new InvalidDataException("读取期间文件大小发生变化：" + relative);
                    // 再检查文件和祖先，防止常见的扫描期间路径替换；不会跟随已发现的链接。
                    DiyPackPaths.RejectLinksToRoot(path);
                    total += bytes.LongLength; files.Add(relative, bytes);
                }
            }
            Walk(root, "");
            return new DiyFrozenFiles(files, total);
        }
        public bool Contains(string path) => path != null && files.ContainsKey(DiyPackPaths.ValidateRelative(path));
        public byte[] ReadBytes(string path)
        {
            path = DiyPackPaths.ValidateRelative(path);
            if (!files.TryGetValue(path, out var bytes)) throw new InvalidDataException("包内文件不存在或大小写不一致：" + path);
            return (byte[])bytes.Clone();
        }
        public string ReadText(string path)
        {
            try { return Utf8.GetString(ReadBytes(path)).TrimStart('\uFEFF'); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("文件不是有效 UTF-8：" + path, error); }
        }
        public JObject ReadJson(string path)
        {
            try { return JObject.Parse(ReadText(path), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
            catch (JsonException error) { throw new InvalidDataException("JSON 无效或存在重复字段：" + path + "：" + error.Message, error); }
        }
        internal static string Hex(byte[] hash) => string.Concat(hash.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    public sealed class DiyAppendEntry
    {
        public string EntryId { get; internal set; }
        public string DefinitionId { get; internal set; }
        public int Quantity { get; internal set; }
        public string InitialZoneRole { get; internal set; }
        public IReadOnlyList<string> ApplicableMapIds { get; internal set; }
    }

    public sealed class DiyAppendRecipe
    {
        public string RecipeId { get; private set; }
        public string ExpansionId { get; private set; }
        public IReadOnlyList<string> ApplicableModes { get; private set; }
        public IReadOnlyList<DiyAppendEntry> Entries { get; private set; }
        internal static DiyAppendRecipe Parse(JObject value, DiyPackManifest manifest, string source)
        {
            DiyPackManifest.Fields(value, source, "schemaVersion", "recipeId", "expansionId", "applicableModes", "entries");
            if (DiyPackManifest.Integer(value, "schemaVersion", source) != 1) throw DiyPackManifest.Error(source, "追加配方版本不支持");
            string id = DiyPackManifest.Text(value, "recipeId", source), expansion = DiyPackManifest.Text(value, "expansionId", source);
            DiyIdentifiers.RequireOwned(id, manifest.PackId, source + ":recipeId");
            if (!manifest.Expansions.Any(x => x.ExpansionId == expansion)) throw DiyPackManifest.Error(source + ":expansionId", "必须属于本包声明的扩展");
            if (!(value["entries"] is JArray)) throw DiyPackManifest.Error(source + ":entries", "必须是数组");
            var entries = DiyPackManifest.Objects(value, "entries", source).Select((entry, index) =>
            {
                string at = source + ":entries[" + index + "]";
                DiyPackManifest.Fields(entry, at, "entryId", "definitionId", "quantity", "initialZoneRole", "applicableMapIds");
                string entryId = DiyPackManifest.Text(entry, "entryId", at), definitionId = DiyPackManifest.Text(entry, "definitionId", at);
                DiyIdentifiers.RequireOwned(entryId, manifest.PackId, at + ":entryId");
                if (!DiyIdentifiers.ValidIdentity(definitionId)) throw DiyPackManifest.Error(at + ":definitionId", "定义身份无效");
                int quantity = DiyPackManifest.Integer(entry, "quantity", at);
                string zone = DiyPackManifest.Text(entry, "initialZoneRole", at);
                if (quantity < 1 || quantity > 128 || !new[] { "event_green", "event_red", "event_yellow" }.Contains(zone))
                    throw DiyPackManifest.Error(at, "事件数量或牌堆颜色无效");
                var maps = DiyPackManifest.Strings(entry, "applicableMapIds", at);
                if (maps.Any(id => id != "map-three-players" && id != "map-four-players"))
                    throw DiyPackManifest.Error(at + ":applicableMapIds", "存在未知地图");
                return new DiyAppendEntry { EntryId = entryId, DefinitionId = definitionId, Quantity = quantity, InitialZoneRole = zone,
                    ApplicableMapIds = Array.AsReadOnly(maps) };
            }).ToArray();
            DiyPackManifest.Unique(entries.Select(x => x.EntryId), source + ":entries");
            return new DiyAppendRecipe { RecipeId = id, ExpansionId = expansion,
                ApplicableModes = Array.AsReadOnly(DiyPackManifest.Strings(value, "applicableModes", source)), Entries = Array.AsReadOnly(entries) };
        }
    }

    public sealed class DiyDefinitionDocument
    {
        public string PackId { get; internal set; }
        public string PackVersion { get; internal set; }
        public string RelativePath { get; internal set; }
        public string DefinitionId { get; internal set; }
        private JObject source;
        internal void SetSource(JObject value) { source = (JObject)value.DeepClone(); }
        public JObject Source => (JObject)source.DeepClone();
    }

    /// <summary>包内第一层检查；跨包引用、版本依赖及最终活动装配由后续组合阶段检查。</summary>
    public sealed class DiyPackSnapshot
    {
        public DiyFrozenFiles Files { get; private set; }
        public DiyPackManifest Manifest { get; private set; }
        public IReadOnlyList<DiyAppendRecipe> Recipes { get; private set; }
        public IReadOnlyList<DiyDefinitionDocument> Definitions { get; private set; }
        public string ContentHash => Files.ContentHash;

        /// <summary>宿主对冻结图片做真实解码；发现器仍不执行包内程序。</summary>
        public void ValidateImages(Action<byte[], string, int, int> decode)
        {
            if (decode == null) throw new ArgumentNullException(nameof(decode));
            foreach (string path in Files.Paths.Where(p => new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(p).ToLowerInvariant())))
            {
                byte[] bytes = Files.ReadBytes(path);
                var expected = DiyImageHeader.Validate(bytes, path);
                try { decode(bytes, path, expected.Width, expected.Height); }
                catch (Exception error) { throw new InvalidDataException(Manifest.PackId + "/" + path + ": 图片无法完整解码：" + error.Message, error); }
            }
        }

        public static DiyPackSnapshot Load(string root)
        {
            var files = DiyFrozenFiles.Capture(root);
            var manifest = DiyPackManifest.Parse(files.ReadJson("pack.json"));
            foreach (string path in files.Paths.Where(x => x.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = files.ReadBytes(path);
                if (bytes.Length > DiyPackLimits.MaximumLuaBytes) throw new InvalidDataException(manifest.PackId + "/" + path + ": Lua 文件超过 32 KiB");
                try { new Script(CoreModules.None).LoadString(files.ReadText(path), null, path); }
                catch (SyntaxErrorException error) { throw new InvalidDataException(manifest.PackId + "/" + path + ": Lua 编译失败：" + error.Message, error); }
            }
            foreach (var binding in manifest.RuleModes.SelectMany(x => x.RuleBindings).Concat(manifest.Expansions.SelectMany(x => x.RuleBindings)))
            {
                EventChoiceContract.ValidateReferencedScript(binding.Script, manifest.PackId + "/pack.json", "ruleBindings[" + binding.BindingId + "]", files.ReadText);
                if (!string.IsNullOrEmpty(binding.CompletionScript))
                    EventChoiceContract.ValidateReferencedScript(binding.CompletionScript, manifest.PackId + "/pack.json", "ruleBindings[" + binding.BindingId + "].completion", files.ReadText);
            }
            foreach (var step in manifest.RuleModes.Where(mode => mode.Preparation != null)
                .SelectMany(mode => mode.Preparation.UnresolvedSteps).Where(step => step.SourcePackId == manifest.PackId))
            {
                if (step.SourcePackVersion != manifest.Version)
                    throw new InvalidDataException(manifest.PackId + ": 准备步骤来源版本与本包不一致：" + step.StepId);
                if (step.Kind != "lua") continue;
                EventChoiceContract.ValidateReferencedScript(step.ScriptPath, manifest.PackId + "/pack.json", "preparation[" + step.StepId + "]", files.ReadText);
                if (step.Completion != null)
                    EventChoiceContract.ValidateReferencedScript(step.Completion.ScriptPath, manifest.PackId + "/pack.json", "preparation[" + step.StepId + "].completion", files.ReadText);
            }
            // 配方来源可以属于传递依赖；此处不能在依赖闭包尚未装配时判缺失。
            var definitions = new HashSet<string>(StringComparer.Ordinal);
            var documents = new List<DiyDefinitionDocument>();
            foreach (string path in manifest.Definitions)
            {
                var definition = files.ReadJson(path);
                DiyPackManifest.Fields(definition, path, "definitionId", "contentType", "version", "displayName", "expansionId", "replaces", "enabled",
                    "applicableModes", "requiredCapabilities", "requiredMechanisms", "artwork", "artworkSpriteId", "tags", "playerMarkerZones", "abilities", "abilitySources",
                    "abilityHandlers", "usageTracks", "cardZones", "data", "presentation", "itemTypeId", "cubeAppearance", "dataTemplate");
                string id = DiyPackManifest.Text(definition, "definitionId", path), type = DiyPackManifest.Text(definition, "contentType", path);
                DiyIdentifiers.RequireOwned(id, manifest.PackId, path + ":definitionId");
                if (!definitions.Add(id)) throw DiyPackManifest.Error(path, "重复定义 ID：" + id);
                if (!new[] { "character", "facility", "event", "city_style", "enterprise", "department", "component", "contract", "award", "enterprise_board", "entrepreneur_ability", "entrepreneur_round" }.Contains(type))
                    throw DiyPackManifest.Error(path + ":contentType", "未知内容类型：" + type);
                string expansion = DiyPackManifest.Text(definition, "expansionId", path);
                DiyPackManifest.Strings(definition, "requiredMechanisms", path);
                if (!manifest.Expansions.Any(x => x.ExpansionId == expansion)) throw DiyPackManifest.Error(path + ":expansionId", "定义必须属于本包声明的扩展");
                DiyPackManifest.Text(definition, "version", path); DiyPackManifest.Text(definition, "displayName", path);
                if (definition.Property("replaces") == null || definition["replaces"].Type != JTokenType.Null && !(definition["replaces"] is JObject))
                    throw DiyPackManifest.Error(path + ":replaces", "必须明确声明替换目标或 null");
                if (definition["replaces"] is JObject replacement)
                {
                    DiyPackManifest.Fields(replacement, path + ":replaces", "expansionId", "contentType", "definitionId");
                    DiyPackManifest.Text(replacement, "expansionId", path + ":replaces");
                    if (DiyPackManifest.Text(replacement, "contentType", path + ":replaces") != type || DiyPackManifest.Text(replacement, "definitionId", path + ":replaces") == id)
                        throw DiyPackManifest.Error(path + ":replaces", "替换类型不一致或目标是自身");
                }
                if (definition["artwork"]?.Type == JTokenType.String && !string.IsNullOrEmpty((string)definition["artwork"]))
                    DiyImageHeader.Validate(files.ReadBytes((string)definition["artwork"]), (string)definition["artwork"]);
                if (type == "event")
                {
                    DiyPackManifest.Text(definition, "artwork", path);
                    if (!(definition["abilities"] is JArray)) throw DiyPackManifest.Error(path + ":abilities", "必须是数组");
                    if (!(definition["data"] is JObject data)) throw DiyPackManifest.Error(path + ":data", "事件 data 必须是对象");
                    if (data["cardId"] != null && (string)data["cardId"] != id) throw DiyPackManifest.Error(path + ":data.cardId", "必须与定义 ID 一致");
                    var rawHandlers = DiyPackManifest.Objects(definition, "abilityHandlers", path);
                    var handlers = rawHandlers.Select(h => DiyPackManifest.Text(h, "handlerId", path)).ToArray();
                    DiyPackManifest.Unique(handlers, path + ":abilityHandlers");
                    EventChoiceContract.Parse(data, manifest.PackId + "/" + path, files.ReadText, handlers);
                    foreach (var handler in rawHandlers)
                    {
                        DiyPackManifest.Fields(handler, path + ":abilityHandlers", "handlerId", "script", "version", "completionHandlerId");
                        DiyPackManifest.Text(handler, "version", path + ":abilityHandlers");
                        EventChoiceContract.ValidateReferencedScript(DiyPackManifest.Text(handler, "script", path + ":abilityHandlers"),
                            manifest.PackId + "/" + path, "abilityHandlers", files.ReadText);
                    }
                }
                var document = new DiyDefinitionDocument { PackId = manifest.PackId, PackVersion = manifest.Version, RelativePath = path, DefinitionId = id };
                document.SetSource(definition); documents.Add(document);
            }
            var recipes = manifest.SetupRecipes.Select(path => DiyAppendRecipe.Parse(files.ReadJson(path), manifest, path)).ToArray();
            DiyPackManifest.Unique(recipes.Select(x => x.RecipeId), manifest.PackId + ":setupRecipes");
            return new DiyPackSnapshot { Files = files, Manifest = manifest, Recipes = Array.AsReadOnly(recipes), Definitions = documents.AsReadOnly() };
        }
    }

    internal static class DiyImageHeader
    {
        internal static (int Width, int Height) Validate(byte[] bytes, string path)
        {
            int width = 0, height = 0;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".png" && bytes.Length >= 24 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                bytes[12] == 'I' && bytes[13] == 'H' && bytes[14] == 'D' && bytes[15] == 'R')
            { width = Read32(bytes, 16); height = Read32(bytes, 20); }
            else if ((extension == ".jpg" || extension == ".jpeg") && bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
            {
                int i = 2;
                while (i + 3 < bytes.Length)
                {
                    if (bytes[i++] != 0xff) break;
                    while (i < bytes.Length && bytes[i] == 0xff) i++;
                    if (i >= bytes.Length) break;
                    int marker = bytes[i++];
                    if (marker == 0xd9 || marker == 0xda) break;
                    if (marker == 0x01 || marker >= 0xd0 && marker <= 0xd7) continue;
                    if (i + 1 >= bytes.Length) break;
                    int size = (bytes[i] << 8) | bytes[i + 1];
                    if (size < 2 || i + size > bytes.Length) break;
                    if ((marker >= 0xc0 && marker <= 0xc3 || marker >= 0xc5 && marker <= 0xc7 || marker >= 0xc9 && marker <= 0xcb || marker >= 0xcd && marker <= 0xcf) && size >= 7)
                    { height = (bytes[i + 3] << 8) | bytes[i + 4]; width = (bytes[i + 5] << 8) | bytes[i + 6]; break; }
                    i += size;
                }
            }
            if (width <= 0 || height <= 0 || width > DiyPackLimits.MaximumImageDimension || height > DiyPackLimits.MaximumImageDimension || (long)width * height > DiyPackLimits.MaximumImagePixels)
                throw new InvalidDataException("图片格式或尺寸无效、超过限制：" + path);
            return (width, height);
        }
        private static int Read32(byte[] bytes, int index)
        {
            uint number = ((uint)bytes[index] << 24) | ((uint)bytes[index + 1] << 16) | ((uint)bytes[index + 2] << 8) | bytes[index + 3];
            return number > int.MaxValue ? 0 : (int)number;
        }
    }
}
