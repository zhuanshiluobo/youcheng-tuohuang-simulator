using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    /// <summary>内容包唯一显示目录。只读克隆输出；不查询对局，不解析布局或文字标签。</summary>
    public sealed class GameplayPresentationCatalog : IGameplayTextCatalogSnapshot
    {
        private readonly Dictionary<string, GameplayTextTemplateDefinition> texts = new Dictionary<string, GameplayTextTemplateDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameplayIconDefinition> icons = new Dictionary<string, GameplayIconDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, EffectPresentationDefinition> effects = new Dictionary<string, EffectPresentationDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameplayInteractionPresentation> interactions = new Dictionary<string, GameplayInteractionPresentation>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameplayTextRef> outcomes = new Dictionary<string, GameplayTextRef>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> assetPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        public string ContentHash { get; private set; } = string.Empty;
        public string Locale { get; private set; } = "zh-CN";
        public int SchemaVersion { get; private set; }
        public IReadOnlyList<GameplayTextTemplateDefinition> Texts => texts.Values.OrderBy(t => t.Key, StringComparer.Ordinal).Select(GameplayPresentationNormalizer.Clone).ToList().AsReadOnly();
        public IReadOnlyList<GameplayIconDefinition> Icons => icons.Values.OrderBy(i => i.Id, StringComparer.Ordinal).Select(GameplayPresentationNormalizer.Clone).ToList().AsReadOnly();
        public IReadOnlyDictionary<string, string> AssetPaths => new ReadOnlyDictionary<string, string>(assetPaths);
        public bool TryGetTemplate(string key, out GameplayTextTemplateDefinition definition)
        { GameplayTextTemplateDefinition value; bool found = key != null && texts.TryGetValue(key, out value); definition = found ? GameplayPresentationNormalizer.Clone(texts[key]) : null; return found; }
        public bool TryGetIcon(string key, out GameplayIconDefinition definition)
        { GameplayIconDefinition value; bool found = key != null && icons.TryGetValue(key, out value); definition = found ? GameplayPresentationNormalizer.Clone(icons[key]) : null; return found; }
        public bool TryGetInteractionDefault(string typeId, out GameplayInteractionPresentation definition)
        { GameplayInteractionPresentation value; bool found = typeId != null && interactions.TryGetValue(typeId, out value); definition = found ? GameplayPresentationNormalizer.Clone(interactions[typeId]) : null; return found; }
        public bool TryGetOutcome(string outcomeCode, out GameplayTextRef reference)
        { GameplayTextRef value; bool found = outcomeCode != null && outcomes.TryGetValue(outcomeCode, out value); reference = found ? GameplayPresentationNormalizer.Clone(outcomes[outcomeCode]) : null; return found; }
        public EffectPresentationDefinition ResolveEffect(string typeId, EffectPresentationDefinition explicitDefinition = null, AbilityPresentationDefinition ability = null)
        {
            EffectPresentationDefinition value = explicitDefinition ?? ability?.Effect;
            if (value == null && typeId != null) effects.TryGetValue(typeId, out value);
            ValidateReferences(value); return GameplayPresentationNormalizer.Clone(value);
        }
        internal void BindContentHash(string hash) { ContentHash = hash ?? string.Empty; }
        internal GameplayPresentationCatalog WithContentHash(string hash)
        {
            var copy = new GameplayPresentationCatalog { ContentHash = hash ?? string.Empty, Locale = Locale, SchemaVersion = SchemaVersion };
            foreach (var pair in texts) copy.texts.Add(pair.Key, pair.Value);
            foreach (var pair in icons) copy.icons.Add(pair.Key, pair.Value);
            foreach (var pair in effects) copy.effects.Add(pair.Key, pair.Value);
            foreach (var pair in interactions) copy.interactions.Add(pair.Key, pair.Value);
            foreach (var pair in outcomes) copy.outcomes.Add(pair.Key, pair.Value);
            foreach (var pair in assetPaths) copy.assetPaths.Add(pair.Key, pair.Value);
            return copy;
        }
        internal IEnumerable<string> DefaultScriptPaths => effects.Values.Where(e => e != null && !string.IsNullOrEmpty(e.PresentationScript)).Select(e => e.PresentationScript).Distinct(StringComparer.Ordinal);
        internal static GameplayPresentationCatalog Load(JToken declaration, string packId, Func<string, JObject> readJson, Action<string> readArtwork)
        {
            var catalog = new GameplayPresentationCatalog();
            if (declaration == null) return catalog; // 旧包继续显式旧字段适配。
            var manifest = declaration as JObject;
            Require(manifest != null && (int?)manifest["schemaVersion"] == 1 && manifest["texts"] is JArray && manifest["icons"]?.Type == JTokenType.String && manifest["defaults"]?.Type == JTokenType.String, "presentation_manifest_invalid");
            Require(!manifest.Properties().Any(p => !new[] { "schemaVersion", "texts", "icons", "defaults" }.Contains(p.Name)), "presentation_manifest_unknown_field");
            catalog.SchemaVersion = 1;
            var paths = ((JArray)manifest["texts"]).Values<string>().ToList();
            Require(paths.Count > 0 && paths.All(p => !string.IsNullOrWhiteSpace(p)) && paths.Distinct(StringComparer.Ordinal).Count() == paths.Count, "presentation_text_paths_invalid");
            foreach (string path in paths)
            {
                JObject document = readJson(path);
                Require((int?)document["schemaVersion"] == 1 && document["locale"]?.Type == JTokenType.String && document["messages"] is JObject, "presentation_text_schema_invalid:" + path);
                if (catalog.texts.Count > 0) Require(catalog.Locale == (string)document["locale"], "presentation_locale_mismatch");
                catalog.Locale = (string)document["locale"];
                foreach (var message in ((JObject)document["messages"]).Properties())
                {
                    Require(IsOwnedKey(message.Name, packId) && !catalog.texts.ContainsKey(message.Name) && message.Value is JObject && message.Value["parameters"] is JObject && message.Value["template"]?.Type == JTokenType.String, "presentation_template_invalid:" + message.Name);
                    var template = new GameplayTextTemplateDefinition { Key = message.Name, Template = (string)message.Value["template"] };
                    foreach (var parameter in ((JObject)message.Value["parameters"]).Properties())
                    { Require(parameter.Value.Type == JTokenType.String && GameplayPresentationNormalizer.IsArgumentKind((string)parameter.Value), "presentation_parameter_invalid:" + message.Name); template.Parameters.Add(new GameplayTextParameterDefinition { Name = parameter.Name, Kind = (string)parameter.Value }); }
                    GameplayPresentationNormalizer.Validate(template); catalog.texts.Add(template.Key, template);
                }
            }
            JObject iconDocument = readJson((string)manifest["icons"]);
            Require((int?)iconDocument["schemaVersion"] == 1 && iconDocument["icons"] is JArray, "presentation_icons_schema_invalid");
            if (iconDocument["assets"] is JObject assets)
                foreach (var asset in assets.Properties())
                {
                    Require(IsOwnedKey(asset.Name, packId) && asset.Value.Type == JTokenType.String, "presentation_asset_invalid");
                    string path = (string)asset.Value;
                    Require(!string.IsNullOrWhiteSpace(path) && !path.Contains(":") && !path.Contains("\\") && new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(Path.GetExtension(path).ToLowerInvariant()), "presentation_asset_path_invalid");
                    readArtwork(path); catalog.assetPaths.Add(asset.Name, path);
                }
            foreach (var token in (JArray)iconDocument["icons"])
            {
                var icon = ReadDto<GameplayIconDefinition>(token);
                Require(icon != null && IsOwnedKey(icon.Id, packId) && !catalog.icons.ContainsKey(icon.Id), "presentation_icon_duplicate_or_foreign");
                foreach (string asset in new[] { icon.AssetKey, icon.Dark, icon.Light, icon.Muted }.Where(k => !string.IsNullOrEmpty(k))) Require(GameplayPresentationNormalizer.IsKey(asset), "presentation_asset_key_invalid");
                // 官方主题键由 UI 已批准目录解析；DIY 包必须提供包内文件，不得盗用主题用途。
                if (packId != "core") foreach (string asset in new[] { icon.AssetKey, icon.Dark, icon.Light, icon.Muted }.Where(k => !string.IsNullOrEmpty(k))) Require(catalog.assetPaths.ContainsKey(asset), "presentation_diy_asset_missing");
                catalog.icons.Add(icon.Id, icon);
            }
            JObject defaults = readJson((string)manifest["defaults"]);
            Require((int?)defaults["schemaVersion"] == 1 && defaults["effectTypes"] is JObject && defaults["interactions"] is JObject && defaults["outcomes"] is JObject, "presentation_defaults_schema_invalid");
            foreach (var entry in ((JObject)defaults["effectTypes"]).Properties()) catalog.effects.Add(entry.Name, ReadDto<EffectPresentationDefinition>(entry.Value));
            foreach (var entry in ((JObject)defaults["interactions"]).Properties()) catalog.interactions.Add(entry.Name, ReadDto<GameplayInteractionPresentation>(entry.Value));
            foreach (var entry in ((JObject)defaults["outcomes"]).Properties()) catalog.outcomes.Add(entry.Name, ReadDto<GameplayTextRef>(entry.Value));
            foreach (var icon in catalog.icons.Values)
            {
                catalog.ValidateReferences(icon.AltText);
                if (!string.IsNullOrEmpty(icon.ValueAltTextKey))
                { GameplayTextTemplateDefinition value; Require(catalog.texts.TryGetValue(icon.ValueAltTextKey, out value) && value.Parameters.Count == 1 && value.Parameters[0].Name == "value" && value.Parameters[0].Kind == "integer", "presentation_value_alt_invalid:" + icon.Id); }
            }
            foreach (var value in catalog.effects.Values) catalog.ValidateReferences(value);
            foreach (var value in catalog.interactions.Values) catalog.ValidateReferences(value);
            foreach (var value in catalog.outcomes.Values) catalog.ValidateReferences(value);
            return catalog;
        }
        public void ValidateReferences(object declaration)
        {
            if (declaration == null) return;
            var reference = declaration as GameplayTextRef;
            if (reference != null)
            {
                GameplayPresentationNormalizer.Validate(reference);
                GameplayTextTemplateDefinition template;
                Require(texts.TryGetValue(reference.Key, out template), "presentation_text_missing:" + reference.Key);
                Require(template.Parameters.Count == reference.Arguments.Count && template.Parameters.All(p => reference.Arguments.Any(a => a.Name == p.Name && a.Kind == p.Kind)), "presentation_argument_signature_mismatch:" + reference.Key);
                return;
            }
            if (declaration is string || declaration is NormalizedValue || declaration.GetType().IsValueType) return;
            var list = declaration as IEnumerable;
            if (list != null) { foreach (var item in list) ValidateReferences(item); return; }
            foreach (var field in declaration.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.Name == "IconKey")
                { string key = (string)field.GetValue(declaration); Require(string.IsNullOrEmpty(key) || icons.ContainsKey(key), "presentation_icon_missing:" + key); }
                else ValidateReferences(field.GetValue(declaration));
            }
        }
        public static T ReadDto<T>(JToken token) where T : class, new()
        {
            try { return GameplayPresentationNormalizer.FromNormalized<T>(JsonValue(token)); }
            catch (ArgumentException error) { throw new InvalidDataException("presentation_dto_invalid:" + typeof(T).Name + ":" + error.Message, error); }
        }
        internal void ValidateEnterpriseDescriptions(ExternalContentDefinition definition)
        {
            if (definition.ContentType != "enterprise" && definition.ContentType != "department") return;
            bool department = definition.ContentType == "department";
            var levels = definition.Data[department ? "rewards" : "levels"] as JArray;
            bool detail = !string.IsNullOrEmpty(definition.Presentation?.DetailScript);
            Require(!detail || levels != null, "presentation_enterprise_levels_missing:" + definition.DefinitionId);
            if (levels == null) return;
            foreach (var level in levels)
            {
                var reference = ReadDto<GameplayTextRef>(level["description"]);
                ValidateReferences(reference);
                bool described = (int?)level["level"] > 0 && (string)level["kind"] != "department_reward";
                Require(!detail || !described || reference != null, "presentation_enterprise_description_missing:" + definition.DefinitionId);
                var ability = definition.Abilities.Find(value => value.AbilityId == (string)level["abilityId"]);
                if (detail && reference != null && ability != null)
                    Require(ability.Presentation?.Description != null &&
                        Newtonsoft.Json.Linq.JToken.DeepEquals(
                            Newtonsoft.Json.Linq.JToken.FromObject(GameplayPresentationNormalizer.ToNormalized(reference)),
                            Newtonsoft.Json.Linq.JToken.FromObject(GameplayPresentationNormalizer.ToNormalized(ability.Presentation.Description))),
                        "presentation_enterprise_ability_description_mismatch:" + ability.AbilityId);
            }
        }
        private static NormalizedValue JsonValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return NormalizedValue.CreateNull();
            if (token.Type == JTokenType.String) return NormalizedValue.CreateString((string)token);
            if (token.Type == JTokenType.Integer) return NormalizedValue.CreateInteger((long)token);
            if (token.Type == JTokenType.Boolean) return NormalizedValue.CreateBoolean((bool)token);
            if (token is JArray array) return NormalizedValue.CreateArray(array.Select(JsonValue).ToList());
            if (token is JObject obj)
            {
                if ((string)obj["kind"] == "stable_reference" && obj.Count == 3) return NormalizedValue.CreateStableReference((string)obj["referenceType"], (string)obj["referenceId"]);
                return NormalizedValue.CreateObject(obj.Properties().Select(p => new NormalizedValueEntry { Name = p.Name, Value = JsonValue(p.Value) }).ToList());
            }
            throw new InvalidDataException("显示 JSON 不允许浮点数或其他非归一化值。");
        }
        private static bool IsOwnedKey(string key, string packId) => GameplayPresentationNormalizer.IsKey(key) && key.StartsWith(packId + ":", StringComparison.Ordinal);
        private static void Require(bool condition, string diagnostic) { if (!condition) throw new InvalidDataException(diagnostic); }
    }
}
