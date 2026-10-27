using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Presentation;

namespace YC.Infrastructure.Lua
{
    public sealed partial class GameplayPresentationCatalog : IGameplayIconResourceCatalog, IGameplayTextSourceCatalog
    {
        public const string ExtensionVersion = "presentation_sources.v1.1.0.0";
        private readonly Dictionary<string, GameplayIconResource> resources = new Dictionary<string, GameplayIconResource>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> resourceSources = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> themeAssetKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> templateSources = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> strictTemplates = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> iconSources = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> iconOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> themeIcons = new HashSet<string>(StringComparer.Ordinal);
        private bool localeChosen;
        public bool HasExtensions { get; private set; }
        public bool TryGetIconResource(string key, out GameplayIconResource resource)
        { resource = null; return key != null && resources.TryGetValue(key, out resource); }
        public bool TryGetTemplateSource(string key, out string source)
        { source = null; return key != null && templateSources.TryGetValue(key, out source); }
        public bool UsesStrictTemplateSyntax(string key) => key != null && strictTemplates.Contains(key);
        public IEnumerable<GameplayIconResource> IconResources => resources.Values.ToArray();

        private static void Fields(JObject value, string source, params string[] allowed)
        {
            Require(value != null, "presentation_object_required:" + source);
            string unknown = string.Join(",", value.Properties().Where(p => !allowed.Contains(p.Name)).Select(p => p.Name));
            Require(unknown.Length == 0, "presentation_unknown_field:" + source + ":" + unknown);
        }
        private static bool Version(JObject value, int expected) => value?["schemaVersion"]?.Type == JTokenType.Integer &&
            value["schemaVersion"].ToString(Newtonsoft.Json.Formatting.None) == expected.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private void AddTextDocument(JObject document, string packId, string path, Func<string, byte[]> readArtwork)
        {
            string source = packId + "/" + path;
            Require((Version(document, 1) || Version(document, 2)) && document["locale"]?.Type == JTokenType.String &&
                !string.IsNullOrWhiteSpace((string)document["locale"]) && document["messages"] is JObject, "presentation_text_schema_invalid:" + source);
            bool extended = Version(document, 2);
            Fields(document, source, extended ? new[] { "schemaVersion", "locale", "messages", "assets", "icons" } : new[] { "schemaVersion", "locale", "messages" });
            Require(!localeChosen || Locale == (string)document["locale"], "presentation_locale_mismatch:" + source);
            Locale = (string)document["locale"]; localeChosen = true;
            foreach (var message in ((JObject)document["messages"]).Properties())
            {
                string item = source + ":" + message.Name;
                Require(IsOwnedKey(message.Name, packId) && !texts.ContainsKey(message.Name) && message.Value is JObject &&
                    message.Value["parameters"] is JObject && message.Value["template"]?.Type == JTokenType.String, "presentation_template_duplicate_or_invalid:" + item);
                Fields((JObject)message.Value, item, "parameters", "template");
                var template = new GameplayTextTemplateDefinition { Key = message.Name, Template = (string)message.Value["template"] };
                foreach (var parameter in ((JObject)message.Value["parameters"]).Properties())
                {
                    Require(parameter.Value.Type == JTokenType.String && GameplayPresentationNormalizer.IsArgumentKind((string)parameter.Value), "presentation_parameter_invalid:" + item + ":" + parameter.Name);
                    template.Parameters.Add(new GameplayTextParameterDefinition { Name = parameter.Name, Kind = (string)parameter.Value });
                }
                try { GameplayPresentationNormalizer.Validate(template); }
                catch (ArgumentException error) { throw new InvalidDataException("presentation_template_invalid:" + item + ":" + error.Message, error); }
                texts.Add(template.Key, template); templateSources.Add(template.Key, source);
                if (extended) strictTemplates.Add(template.Key);
            }
            if (extended)
            {
                HasExtensions = true;
                AddAssetsAndIcons(document, packId, source, readArtwork, false);
            }
        }
        private void AddAssetsAndIcons(JObject document, string packId, string source, Func<string, byte[]> readArtwork, bool coreTheme)
        {
            Require(document["assets"] == null || document["assets"] is JObject, "presentation_assets_schema_invalid:" + source);
            Require(document["icons"] == null || document["icons"] is JArray, "presentation_icons_schema_invalid:" + source);
            if (document["assets"] is JObject assets)
                foreach (var asset in assets.Properties())
                {
                    Require(IsOwnedKey(asset.Name, packId) && asset.Value.Type == JTokenType.String && !assetPaths.ContainsKey(asset.Name), "presentation_asset_duplicate_or_foreign:" + source + ":" + asset.Name);
                    Require(!themeAssetKeys.Contains(asset.Name), "presentation_theme_asset_shadow:" + source + ":" + asset.Name);
                    string path = (string)asset.Value;
                    Require(new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(path ?? "").ToLowerInvariant()), "presentation_asset_format_unsupported:" + source + ":" + asset.Name);
                    byte[] bytes;
                    try { bytes = readArtwork(path); }
                    catch (Exception error) when (error is InvalidDataException || error is IOException || error is ArgumentException) { throw new InvalidDataException("presentation_asset_read_failed:" + source + ":" + asset.Name + ":" + error.Message, error); }
                    assetPaths.Add(asset.Name, path); resources.Add(asset.Name, new GameplayIconResource(packId, asset.Name, path, bytes));
                    resourceSources.Add(asset.Name, source);
                }
            foreach (var token in (JArray)document["icons"] ?? new JArray())
            {
                GameplayIconDefinition icon;
                try { icon = ReadDto<GameplayIconDefinition>(token); }
                catch (InvalidDataException error) { throw new InvalidDataException("presentation_icon_invalid:" + source + ":" + (token as JObject)?["id"] + ":" + error.Message, error); }
                Require(icon != null && IsOwnedKey(icon.Id, packId) && !icons.ContainsKey(icon.Id), "presentation_icon_duplicate_or_foreign:" + source + ":" + icon?.Id);
                foreach (string key in new[] { icon.AssetKey, icon.Dark, icon.Light, icon.Muted }.Where(k => !string.IsNullOrEmpty(k)))
                {
                    Require(GameplayPresentationNormalizer.IsKey(key), "presentation_asset_key_invalid:" + source + ":" + icon.Id);
                    if (coreTheme)
                    {
                        // 文案文件先读；已有主题的资源键即使未列入assets，也属于基础目录。
                        Require(!resourceSources.TryGetValue(key, out var origin) || origin == source,
                            "presentation_theme_asset_shadow:" + origin + ":" + icon.Id + ":" + key);
                        themeAssetKeys.Add(key);
                    }
                }
                icons.Add(icon.Id, icon);
                iconSources.Add(icon.Id, source); iconOwners.Add(icon.Id, packId);
                if (coreTheme) themeIcons.Add(icon.Id);
            }
        }
        internal void AddExternalTexts(string packId, string declaredLocale, IEnumerable<string> paths, Func<string, JObject> readJson, Func<string, byte[]> readArtwork)
        {
            HasExtensions = true;
            if (SchemaVersion == 0) SchemaVersion = 1;
            Require(!localeChosen || Locale == declaredLocale, "presentation_locale_mismatch:" + packId + "/presentation.json:locale");
            foreach (string path in paths)
            {
                var document = readJson(path);
                Require(document["locale"]?.Type == JTokenType.String && (string)document["locale"] == declaredLocale, "presentation_locale_mismatch:" + packId + "/" + path);
                AddTextDocument(document, packId, path, readArtwork);
            }
        }
        internal void ValidateAllReferences()
        {
            foreach (var icon in icons.Values)
            {
                foreach (string key in new[] { icon.AssetKey, icon.Dark, icon.Light, icon.Muted }.Where(k => !string.IsNullOrEmpty(k)))
                    Require(themeIcons.Contains(icon.Id) || resources.TryGetValue(key, out var resource) && resource.OwnerPackId == iconOwners[icon.Id],
                        "presentation_owned_icon_asset_missing:" + iconSources[icon.Id] + ":" + icon.Id + ":" + key);
                try { ValidateReferences(icon.AltText); }
                catch (InvalidDataException error) { throw new InvalidDataException(iconSources[icon.Id] + ":" + icon.Id + ":" + error.Message, error); }
                if (!string.IsNullOrEmpty(icon.ValueAltTextKey))
                    Require(texts.TryGetValue(icon.ValueAltTextKey, out var value) && value.Parameters.Count == 1 && value.Parameters[0].Name == "value" && value.Parameters[0].Kind == "integer", "presentation_value_alt_invalid:" + iconSources[icon.Id] + ":" + icon.Id);
            }
            foreach (var value in effects.Values) ValidateReferences(value);
            foreach (var value in interactions.Values) ValidateReferences(value);
            foreach (var value in outcomes.Values) ValidateReferences(value);
        }
    }
}
