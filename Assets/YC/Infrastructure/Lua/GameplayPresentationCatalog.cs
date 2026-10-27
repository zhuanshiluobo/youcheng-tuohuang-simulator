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
    public sealed partial class GameplayPresentationCatalog : IGameplayTextCatalogSnapshot
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
            foreach (var pair in resources) copy.resources.Add(pair.Key, pair.Value);
            foreach (var pair in resourceSources) copy.resourceSources.Add(pair.Key, pair.Value);
            copy.themeAssetKeys.UnionWith(themeAssetKeys);
            foreach (var pair in templateSources) copy.templateSources.Add(pair.Key, pair.Value);
            foreach (var pair in iconSources) copy.iconSources.Add(pair.Key, pair.Value);
            foreach (var pair in iconOwners) copy.iconOwners.Add(pair.Key, pair.Value);
            copy.strictTemplates.UnionWith(strictTemplates); copy.themeIcons.UnionWith(themeIcons);
            copy.localeChosen = localeChosen; copy.HasExtensions = HasExtensions;
            return copy;
        }
        internal IEnumerable<string> DefaultScriptPaths => effects.Values.Where(e => e != null && !string.IsNullOrEmpty(e.PresentationScript)).Select(e => e.PresentationScript).Distinct(StringComparer.Ordinal);
        internal static GameplayPresentationCatalog Load(JToken declaration, string packId, Func<string, JObject> readJson, Func<string, byte[]> readArtwork)
        {
            var catalog = new GameplayPresentationCatalog();
            if (declaration == null) return catalog; // 旧包继续显式旧字段适配。
            var manifest = declaration as JObject;
            Require(manifest != null && Version(manifest, 1) && manifest["texts"] is JArray && manifest["icons"]?.Type == JTokenType.String && manifest["defaults"]?.Type == JTokenType.String, "presentation_manifest_invalid");
            Require(!manifest.Properties().Any(p => !new[] { "schemaVersion", "texts", "icons", "defaults" }.Contains(p.Name)), "presentation_manifest_unknown_field");
            catalog.SchemaVersion = 1;
            Require(((JArray)manifest["texts"]).All(t => t.Type == JTokenType.String), "presentation_text_paths_invalid");
            var paths = ((JArray)manifest["texts"]).Values<string>().ToList();
            Require(paths.Count > 0 && paths.All(p => !string.IsNullOrWhiteSpace(p)) && paths.Distinct(StringComparer.Ordinal).Count() == paths.Count, "presentation_text_paths_invalid");
            foreach (string path in paths) catalog.AddTextDocument(readJson(path), packId, path, readArtwork);
            string iconPath = (string)manifest["icons"];
            JObject iconDocument = readJson(iconPath);
            Require(Version(iconDocument, 1) && iconDocument["icons"] is JArray, "presentation_icons_schema_invalid:" + iconPath);
            Fields(iconDocument, iconPath, "schemaVersion", "assets", "icons");
            catalog.AddAssetsAndIcons(iconDocument, packId, packId + "/" + iconPath, readArtwork, packId == "core");
            JObject defaults = readJson((string)manifest["defaults"]);
            Fields(defaults, (string)manifest["defaults"], "schemaVersion", "effectTypes", "interactions", "outcomes");
            Require(Version(defaults, 1) && defaults["effectTypes"] is JObject && defaults["interactions"] is JObject && defaults["outcomes"] is JObject, "presentation_defaults_schema_invalid");
            foreach (var entry in ((JObject)defaults["effectTypes"]).Properties()) catalog.effects.Add(entry.Name, ReadDto<EffectPresentationDefinition>(entry.Value));
            foreach (var entry in ((JObject)defaults["interactions"]).Properties()) catalog.interactions.Add(entry.Name, ReadDto<GameplayInteractionPresentation>(entry.Value));
            foreach (var entry in ((JObject)defaults["outcomes"]).Properties()) catalog.outcomes.Add(entry.Name, ReadDto<GameplayTextRef>(entry.Value));
            return catalog;
        }
        public void ValidateReferences(object declaration)
        {
            if (declaration == null) return;
            var compact = declaration as GameplayCandidateIntegerLabel;
            if (compact != null)
            {
                GameplayPresentationNormalizer.Validate(compact);
                // 只检查一次已有模板签名，不按候选数量展开Text.Ref。
                ValidateReferences(new GameplayTextRef(compact.Key) { Arguments = compact.Arguments.Select(a => new GameplayTextArgument {
                    Name = a.Name, Kind = "integer", Value = NormalizedValue.CreateInteger(0) }).ToList() });
                return;
            }
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
            catch (Exception error) when (error is ArgumentException || error is OverflowException || error is InvalidCastException || error is FormatException)
            { throw new InvalidDataException("presentation_dto_invalid:" + typeof(T).Name + ":" + error.Message, error); }
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
