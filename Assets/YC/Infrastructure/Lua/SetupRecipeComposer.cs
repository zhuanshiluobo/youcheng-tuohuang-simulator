using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    /// <summary>按模式和扩展声明装配 core 配方；参数覆盖必须有明确的优先关系。</summary>
    internal static class SetupRecipeComposer
    {
        private sealed class Policy
        {
            internal string Id, Role;
            internal JObject Source;
            internal string[] Overrides, Supersedes, After, Requires, UnconditionalZones;
            internal JObject MinimumDraw;
        }

        internal static GameSetupRecipe Compose(ExternalContentPack pack, JObject manifest, GameRuleConfiguration configuration, IReadOnlyList<string> mechanisms, out JToken identity)
        {
            var modeIds = configuration.ActiveRuleModeIds;
            var modes = (manifest["ruleModes"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(x => modeIds.Contains((string)x["modeId"]))
                .OrderBy(x => (string)x["modeId"] == configuration.RuleModeId ? 0 : 1)
                .ThenBy(x => (string)x["modeId"], StringComparer.Ordinal).ToArray();
            if (modes.Length != modeIds.Count) throw new InvalidOperationException("rule_mode_declaration_invalid:" + configuration.RuleModeId);
            var expansions = (manifest["expansionDependencies"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(x => configuration.EnabledExpansionIds.Contains((string)x["expansionId"])).ToArray();
            var references = new List<(string PackId, string RecipeId)>();
            void AddReferences(JObject owner, bool required)
            {
                if (owner["setupRecipeRefs"] == null && !required) return;
                if (!(owner["setupRecipeRefs"] is JArray array) || required && array.Count == 0)
                    throw new InvalidOperationException("setup_recipe_references_invalid");
                foreach (var token in array)
                {
                    if (!(token is JObject item) || item.Properties().Any(p => p.Name != "packId" && p.Name != "recipeId") ||
                        item["packId"]?.Type != JTokenType.String || item["recipeId"]?.Type != JTokenType.String ||
                        !DiyIdentifiers.ValidPackId((string)item["packId"]) || !DiyIdentifiers.ValidIdentity((string)item["recipeId"]))
                        throw new InvalidOperationException("setup_recipe_reference_invalid");
                    var reference = ((string)item["packId"], (string)item["recipeId"]);
                    if (!references.Contains(reference)) references.Add(reference);
                }
            }
            foreach (var mode in modes) AddReferences(mode, true);
            foreach (var expansion in expansions) AddReferences(expansion, false);
            foreach (var reference in references.Where(x => x.PackId != "core"))
            {
                if (pack.DiyComposition == null) throw new InvalidOperationException("setup_recipe_source_missing:" + reference.PackId);
                var source = pack.DiyComposition.GetPack(reference.PackId);
                var addition = source.Recipes.SingleOrDefault(x => x.RecipeId == reference.RecipeId);
                if (addition == null || !configuration.EnabledExpansionIds.Contains(addition.ExpansionId) ||
                    addition.ApplicableModes.Count > 0 && !modeIds.Any(addition.ApplicableModes.Contains))
                    throw new InvalidOperationException("diy_mode_recipe_not_applicable:" + reference.RecipeId);
            }

            if (!(manifest["setupRecipeComposition"] is JArray declarations)) throw new InvalidOperationException("setup_recipe_composition_missing");
            var policies = new Dictionary<string, Policy>(StringComparer.Ordinal);
            string[] Strings(JObject source, string name)
            {
                if (source[name] == null) return Array.Empty<string>();
                if (!(source[name] is JArray values) || values.Any(x => x.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)x)))
                    throw new InvalidOperationException("setup_recipe_composition_invalid:" + name);
                var result = values.Values<string>().ToArray();
                if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) throw new InvalidOperationException("setup_recipe_composition_duplicate:" + name);
                return result;
            }
            foreach (var token in declarations)
            {
                if (!(token is JObject item) || item.Properties().Any(p => !new[] { "recipeId", "role", "overrides", "supersedes", "after", "requiresExpansions", "unconditionalZones", "minimumOperatorDrawByPlayerCount" }.Contains(p.Name)) ||
                    item["recipeId"]?.Type != JTokenType.String || item["role"]?.Type != JTokenType.String)
                    throw new InvalidOperationException("setup_recipe_composition_invalid");
                string id = (string)item["recipeId"], role = (string)item["role"];
                if (!DiyIdentifiers.ValidIdentity(id) || !new[] { "base", "contribution" }.Contains(role) || policies.ContainsKey(id))
                    throw new InvalidOperationException("setup_recipe_composition_invalid:" + id);
                var policy = new Policy { Id = id, Role = role, Source = item, Overrides = Strings(item, "overrides"),
                    Supersedes = Strings(item, "supersedes"), After = Strings(item, "after"), Requires = Strings(item, "requiresExpansions"),
                    UnconditionalZones = Strings(item, "unconditionalZones"), MinimumDraw = item["minimumOperatorDrawByPlayerCount"] as JObject };
                if (item["minimumOperatorDrawByPlayerCount"] != null && policy.MinimumDraw == null ||
                    policy.MinimumDraw != null && policy.MinimumDraw.Properties().Any(p => !new[] { "1", "2", "3", "4" }.Contains(p.Name) ||
                        p.Value.Type != JTokenType.Integer || (int)p.Value < 1 || (int)p.Value > 128) ||
                    role == "base" && (policy.Overrides.Length > 0 || policy.Supersedes.Length > 0 || policy.UnconditionalZones.Length > 0) ||
                    role == "contribution" && policy.MinimumDraw != null)
                    throw new InvalidOperationException("setup_recipe_composition_invalid:" + id);
                policies.Add(id, policy);
            }
            foreach (var policy in policies.Values)
                if (policy.After.Concat(policy.Supersedes).Any(id => id == policy.Id || !policies.ContainsKey(id)))
                    throw new InvalidOperationException("setup_recipe_composition_order_invalid:" + policy.Id);

            var available = pack.SetupRecipes;
            if (available.Any(x => x == null || string.IsNullOrWhiteSpace(x.RecipeId)) || available.Select(x => x.RecipeId).Distinct(StringComparer.Ordinal).Count() != available.Count)
                throw new InvalidOperationException("setup_recipe_identity_duplicate");
            var selected = new List<Policy>();
            foreach (string id in references.Where(x => x.PackId == "core").Select(x => x.RecipeId))
            {
                if (!policies.TryGetValue(id, out var policy)) throw new InvalidOperationException("setup_recipe_missing:" + id);
                var recipe = available.SingleOrDefault(x => x.RecipeId == id);
                if (recipe == null || recipe.ApplicableModes == null || recipe.RequiredMechanisms == null ||
                    recipe.ApplicableModes.Count > 0 && !modeIds.Any(recipe.ApplicableModes.Contains) ||
                    recipe.RequiredMechanisms.Any(id => !mechanisms.Contains(id)))
                    throw new InvalidOperationException("setup_recipe_not_applicable:" + id);
                if (policy.Requires.Any(x => !configuration.EnabledExpansionIds.Contains(x))) throw new InvalidOperationException("setup_recipe_dependency_missing:" + id);
                if (RuleModeReadiness.Missing(recipe.RequiredCapabilities).Length > 0) throw new InvalidOperationException("recipe_capability_missing:" + id);
                selected.Add(policy);
            }
            if (selected.Count(x => x.Role == "base") != 1) throw new InvalidOperationException("setup_recipe_base_invalid");
            var basePolicy = selected.Single(x => x.Role == "base");
            var result = GameStateCloneService.DeepClone(available.Single(x => x.RecipeId == basePolicy.Id));
            result.Mechanisms = mechanisms.ToList();
            var pending = selected.Where(x => x.Role == "contribution").ToList();
            var ordered = new List<Policy>();
            while (pending.Count > 0)
            {
                var next = pending.FirstOrDefault(x => x.After.Concat(x.Supersedes).All(id => pending.All(p => p.Id != id)));
                if (next == null) throw new InvalidOperationException("setup_recipe_composition_cycle");
                ordered.Add(next); pending.Remove(next);
            }
            bool Supersedes(Policy source, string target, HashSet<string> visited)
            {
                if (!visited.Add(source.Id)) return false;
                return source.Supersedes.Contains(target) || source.Supersedes.Any(id => Supersedes(policies[id], target, visited));
            }
            var assigned = new Dictionary<string, List<(Policy Owner, object Value)>>(StringComparer.Ordinal);
            foreach (var policy in ordered)
            {
                var addition = available.Single(x => x.RecipeId == policy.Id);
                result.Entries.AddRange(GameStateCloneService.DeepClone(addition.Entries));
                foreach (string name in policy.Overrides)
                {
                    object value = ReadParameter(addition, name);
                    if (!assigned.TryGetValue(name, out var previous)) assigned.Add(name, previous = new List<(Policy, object)>());
                    if (previous.Any(x => !Equals(x.Value, value) && !Supersedes(policy, x.Owner.Id, new HashSet<string>(StringComparer.Ordinal))))
                        throw new InvalidOperationException("setup_recipe_parameter_conflict:" + name);
                    previous.Add((policy, value));
                    WriteParameter(result, name, value);
                }
                foreach (var entry in result.Entries.Where(e => policy.UnconditionalZones.Contains(e.InitialZoneRole))) entry.Condition = new SetupRecipeCondition();
            }
            string countKey = configuration.PlayerCount.ToString(CultureInfo.InvariantCulture);
            if (basePolicy.MinimumDraw != null && basePolicy.MinimumDraw[countKey] != null)
                result.OperatorDrawCount = Math.Max(result.OperatorDrawCount, (int)basePolicy.MinimumDraw[countKey]);
            var plans = new List<(string Id, int? Order, PreparationFlowPlan Plan)>();
            foreach (var mode in modes)
            {
                if (mode["preparation"] == null) continue;
                if (!(mode["preparation"] is JObject rawPreparation)) throw new InvalidOperationException("preparation_declaration_invalid");
                string modeId = (string)mode["modeId"];
                var preparation = DiyPreparationDeclaration.Parse(rawPreparation, modeId, "ruleMode.preparation");
                int? order = mode["preparationOrder"] != null ? DiyPackManifest.Integer(mode, "preparationOrder", modeId) : preparation.HasSteps ? 0 : (int?)null;
                var resolved = preparation.Resolve((sourceId, version, relative) => {
                    if (sourceId == "core")
                    {
                        if ((string)manifest["version"] != version) throw new InvalidOperationException("preparation_source_version_mismatch:" + sourceId);
                    }
                    else
                    {
                        if (pack.DiyComposition == null) throw new InvalidOperationException("preparation_source_missing:" + sourceId);
                        var source = pack.DiyComposition.GetPack(sourceId);
                        if (source.Manifest.Version != version || !source.Files.Contains(relative))
                            throw new InvalidOperationException("preparation_source_missing:" + sourceId + "/" + relative);
                    }
                    string mounted = PreparationScriptPath(sourceId, version, relative);
                    pack.LoadScript(mounted);
                    return pack.GetScript(mounted);
                });
                plans.Add((modeId, order, resolved));
            }
            var duplicateOrder = plans.Where(item => item.Order.HasValue).GroupBy(item => item.Order.Value).FirstOrDefault(group => group.Count() > 1);
            if (duplicateOrder != null) throw new InvalidOperationException("准备阶段序号冲突：" + string.Join(" / ", duplicateOrder.Select(item => item.Id)) + "（" + duplicateOrder.Key + "）");
            if (plans.Count == 1)
            {
                result.Preparation = plans[0].Plan;
                result.Preparation.ModeId = configuration.RuleModeId;
            }
            else if (plans.Count > 1)
            {
                var combined = new PreparationFlowPlan { ModeId = configuration.RuleModeId };
                bool prefixSteps = plans.Count(item => item.Plan.BeforeDistribution.Count + item.Plan.BetweenDistributionAndConfirmation.Count + item.Plan.AfterConfirmation.Count > 0) > 1;
                foreach (var item in plans.OrderBy(item => item.Order ?? int.MaxValue).ThenBy(item => item.Id, StringComparer.Ordinal))
                {
                    var plan = item.Plan;
                    if (plan.Enterprise != null)
                    {
                        if (combined.Enterprise != null && !JToken.DeepEquals(JToken.FromObject(combined.Enterprise), JToken.FromObject(plan.Enterprise)))
                            throw new InvalidOperationException("准备阶段企业分发规则冲突：" + item.Id);
                        combined.Enterprise = GameStateCloneService.DeepClone(plan.Enterprise);
                    }
                    if (plan.CityStyles != null)
                    {
                        if (combined.CityStyles != null && !JToken.DeepEquals(JToken.FromObject(combined.CityStyles), JToken.FromObject(plan.CityStyles)))
                            throw new InvalidOperationException("准备阶段城市样式规则冲突：" + item.Id);
                        combined.CityStyles = GameStateCloneService.DeepClone(plan.CityStyles);
                    }
                    void AddSteps(IEnumerable<PreparationStepDeclaration> source, List<PreparationStepDeclaration> destination)
                    {
                        foreach (var original in source)
                        {
                            var step = GameStateCloneService.DeepClone(original);
                            if (prefixSteps) step.StepId = StableIdFactory.Create("preparation-step", item.Id, step.StepId);
                            destination.Add(step);
                        }
                    }
                    AddSteps(plan.BeforeDistribution, combined.BeforeDistribution);
                    AddSteps(plan.BetweenDistributionAndConfirmation, combined.BetweenDistributionAndConfirmation);
                    AddSteps(plan.AfterConfirmation, combined.AfterConfirmation);
                    foreach (var original in plan.DistributionInputs)
                    {
                        var binding = GameStateCloneService.DeepClone(original);
                        if (prefixSteps) binding.StepId = StableIdFactory.Create("preparation-step", item.Id, binding.StepId);
                        combined.DistributionInputs.Add(binding);
                    }
                }
                combined.Validate();
                result.Preparation = combined;
            }
            if (result.Preparation != null && (result.Preparation.Enterprise != null) != result.EnterpriseEntrance)
                throw new InvalidOperationException("preparation_enterprise_mechanism_mismatch");
            var mechanismDeclarations = (manifest["mechanismDeclarations"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(x => mechanisms.Contains((string)x["mechanismId"]))
                .OrderBy(x => (string)x["mechanismId"], StringComparer.Ordinal).Select(x => x.DeepClone());
            identity = new JObject { ["mode"] = modes[0].DeepClone(), ["mechanisms"] = new JArray(mechanismDeclarations),
                ["expansions"] = new JArray(expansions.Select(x => x.DeepClone())),
                ["policies"] = new JArray(new[] { basePolicy }.Concat(ordered).Select(x => x.Source.DeepClone())) };
            if (modes.Length > 1) ((JObject)identity)["additionalModes"] = new JArray(modes.Skip(1).Select(mode => mode.DeepClone()));
            return result;
        }

        internal static string PreparationScriptPath(string packId, string version, string relative)
        {
            DiyPackPaths.ValidateRelative(relative);
            if (!relative.StartsWith("lua/", StringComparison.Ordinal)) throw new InvalidOperationException("preparation_script_path_invalid");
            return packId == "core" ? relative : "lua/.diy/" + packId + "/" + version + "/" + relative.Substring(4);
        }

        private static object ReadParameter(GameSetupRecipe recipe, string name) => name switch
        {
            "recipeId" => recipe.RecipeId,
            "initialBasicCharacterCount" => recipe.InitialBasicCharacterCount,
            "basicCharacterKeepCount" => recipe.BasicCharacterKeepCount,
            "maxRetainedBasicCharacterCount" => recipe.MaxRetainedBasicCharacterCount,
            "cityStyleLevelOneDrawCount" => recipe.CityStyleLevelOneDrawCount,
            "cityStyleLevelTwoDrawCount" => recipe.CityStyleLevelTwoDrawCount,
            "enterpriseEntrance" => recipe.EnterpriseEntrance,
            "operatorDrawCount" => recipe.OperatorDrawCount,
            _ => throw new InvalidOperationException("setup_recipe_parameter_unknown:" + name)
        };
        private static void WriteParameter(GameSetupRecipe recipe, string name, object value)
        {
            switch (name)
            {
                case "recipeId": recipe.RecipeId = (string)value; break;
                case "initialBasicCharacterCount": recipe.InitialBasicCharacterCount = (int)value; break;
                case "basicCharacterKeepCount": recipe.BasicCharacterKeepCount = (int)value; break;
                case "maxRetainedBasicCharacterCount": recipe.MaxRetainedBasicCharacterCount = (int)value; break;
                case "cityStyleLevelOneDrawCount": recipe.CityStyleLevelOneDrawCount = (int)value; break;
                case "cityStyleLevelTwoDrawCount": recipe.CityStyleLevelTwoDrawCount = (int)value; break;
                case "enterpriseEntrance": recipe.EnterpriseEntrance = (bool)value; break;
                case "operatorDrawCount": recipe.OperatorDrawCount = (int)value; break;
                default: throw new InvalidOperationException("setup_recipe_parameter_unknown:" + name);
            }
        }
    }
}
