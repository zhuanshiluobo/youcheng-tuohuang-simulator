using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YC.Infrastructure.Lua
{
    /// <summary>仅显式登记的显示包；没有玩法定义、目录扫描或覆盖基础键。</summary>
    public sealed class ExternalPresentationSourceInfo
    {
        public string PackId { get; internal set; }
        public string Version { get; internal set; }
        public string Locale { get; internal set; }
        public string ContentHash { get; internal set; }
    }
    internal sealed class PresentationFileStore
    {
        private readonly string root;
        private readonly SortedDictionary<string, byte[]> files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        internal PresentationFileStore(string root) { this.root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root))); CheckAncestors(this.root); }
        internal static void CheckAncestors(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("presentation_reparse_point:" + current);
        }
        internal static string Resolve(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":") || relative.Contains("\\") ||
                relative.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal) || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    System.Text.RegularExpressions.Regex.IsMatch(p, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
                throw new InvalidDataException("presentation_path_invalid:" + root + "/" + relative);
            root = Path.GetFullPath(root);
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("presentation_path_escape:" + relative);
            CheckAncestors(path); return path;
        }
        internal byte[] Read(string relative)
        {
            string path = Resolve(root, relative);
            if (files.TryGetValue(relative, out var cached)) return cached;
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("presentation_file_missing_or_oversized:" + path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var memory = new MemoryStream())
            { stream.CopyTo(memory); cached = memory.ToArray(); }
            CheckAncestors(path);
            if (cached.Length > 16 * 1024 * 1024) throw new InvalidDataException("presentation_file_oversized:" + path);
            files.Add(relative, cached); return cached;
        }
        internal JObject Json(string relative)
        {
            try { return JObject.Parse(Encoding.UTF8.GetString(Read(relative)), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
            catch (JsonException error) { throw new InvalidDataException("presentation_json_invalid:" + root + "/" + relative + ":" + error.Message, error); }
        }
        internal byte[] Artwork(string relative)
        {
            if (!new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(relative ?? "").ToLowerInvariant())) throw new InvalidDataException("presentation_asset_format_unsupported:" + relative);
            return Read(relative);
        }
        internal string Fingerprint() => string.Concat(files.Select(p => p.Key + ":" + Convert.ToBase64String(p.Value) + "\n"));
    }
    public sealed partial class ExternalContentPack
    {
        /// <summary>正式显式清单入口；sources 内目录均相对 registryRoot，不扫描磁盘。</summary>
        public static ExternalContentPack LoadRegistered(string coreRoot, string registryRoot)
        {
            registryRoot = Path.GetFullPath(registryRoot ?? throw new ArgumentNullException(nameof(registryRoot)));
            PresentationFileStore.CheckAncestors(registryRoot);
            string path = PresentationFileStore.Resolve(registryRoot, "sources.json");
            if (!File.Exists(path)) return Load(coreRoot);
            var store = new PresentationFileStore(registryRoot);
            var registry = store.Json("sources.json");
            if (registry["schemaVersion"]?.Type != JTokenType.Integer || registry["schemaVersion"].ToString(Formatting.None) != "1" ||
                registry["sources"] is not JArray sources || sources.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t)) ||
                sources.Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count ||
                registry.Properties().Any(p => p.Name != "schemaVersion" && p.Name != "sources"))
                throw new InvalidDataException("presentation_registration_invalid:" + path);
            var roots = sources.Values<string>().Select(relative => PresentationFileStore.Resolve(registryRoot, relative)).ToArray();
            return LoadCombined(coreRoot, roots, "registration:" + LuaContentHasher.ComputeSha256(store.Fingerprint()) + "\n");
        }
        private readonly List<ExternalPresentationSourceInfo> presentationSources = new List<ExternalPresentationSourceInfo>();
        public IReadOnlyList<ExternalPresentationSourceInfo> PresentationSources => presentationSources.Select(p => new ExternalPresentationSourceInfo {
            PackId = p.PackId, Version = p.Version, Locale = p.Locale, ContentHash = p.ContentHash }).ToList().AsReadOnly();
        private string presentationSourceInput = "";
        private void LoadPresentationSources(IEnumerable<string> roots)
        {
            var sources = new List<(PresentationFileStore Store, JObject Manifest)>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string root in roots ?? Array.Empty<string>())
            {
                var store = new PresentationFileStore(root); var declaration = store.Json("presentation.json");
                string id = declaration["packId"]?.Type == JTokenType.String ? (string)declaration["packId"] : null;
                if (declaration["schemaVersion"]?.Type != JTokenType.Integer || declaration["schemaVersion"].ToString(Formatting.None) != "1" ||
                    string.IsNullOrWhiteSpace(id) || id.Contains(":") || !YC.Domain.Presentation.GameplayPresentationNormalizer.IsKey(id + ":probe") || id == PackId || !ids.Add(id) ||
                    declaration["version"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)declaration["version"]) ||
                    declaration["locale"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)declaration["locale"]) ||
                    declaration["texts"] is not JArray texts || texts.Count == 0 || texts.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t)) ||
                    texts.Values<string>().Distinct(StringComparer.Ordinal).Count() != texts.Count ||
                    declaration.Properties().Any(p => !new[] { "schemaVersion", "packId", "version", "locale", "texts" }.Contains(p.Name)))
                    throw new InvalidDataException("presentation_source_manifest_invalid_or_duplicate:" + root + "/presentation.json:" + id);
                sources.Add((store, declaration));
            }
            var identity = new StringBuilder();
            foreach (var source in sources.OrderBy(s => (string)s.Manifest["packId"], StringComparer.Ordinal))
            {
                string id = (string)source.Manifest["packId"], locale = (string)source.Manifest["locale"];
                Presentation.AddExternalTexts(id, locale, ((JArray)source.Manifest["texts"]).Values<string>(), source.Store.Json, source.Store.Artwork);
                string input = source.Store.Fingerprint(), hash = LuaContentHasher.ComputeSha256(input);
                presentationSources.Add(new ExternalPresentationSourceInfo { PackId = id, Version = (string)source.Manifest["version"], Locale = locale, ContentHash = hash });
                identity.Append(id).Append(':').Append(hash).Append('\n');
            }
            presentationSourceInput = identity.ToString();
            Presentation.ValidateAllReferences();
        }
        private string PresentationExtensionIdentity() => Presentation.HasExtensions || presentationSourceInput.Length > 0 ? "\n" + GameplayPresentationCatalog.ExtensionVersion + "\n" + presentationSourceInput : "";
        private void CopyPresentationSourcesTo(ExternalContentPack destination)
        {
            destination.presentationSourceInput = presentationSourceInput;
            destination.presentationSources.AddRange(PresentationSources);
        }
    }
}
