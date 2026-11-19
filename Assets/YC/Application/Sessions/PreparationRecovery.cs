using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using YC.Domain.Rules;

namespace YC.Application.Sessions
{
    public static partial class HostRecoveryService
    {
        // 新合同的零值和空列表必须来自归档；不能由 DTO 初始化器补齐。
        private static bool HasRawPreparationFields(JObject state)
        {
            var plan = (state?["SetupRecipe"] as JObject)?["Preparation"];
            var runtime = state?["PreparationRuntime"];
            if (plan == null || plan.Type == JTokenType.Null)
                return runtime == null || runtime.Type == JTokenType.Null;

            bool Fields(JToken token, Type type, params string[] nullable)
            {
                if (token is not JObject value) return false;
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    var entry = value[field.Name];
                    if (entry == null || entry.Type == JTokenType.Null && !nullable.Contains(field.Name)) return false;
                    if (entry.Type == JTokenType.Null) continue;
                    if (field.FieldType == typeof(string) && entry.Type != JTokenType.String ||
                        field.FieldType == typeof(bool) && entry.Type != JTokenType.Boolean ||
                        field.FieldType == typeof(int) && entry.Type != JTokenType.Integer ||
                        field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>) && entry is not JArray ||
                        field.FieldType.IsClass && field.FieldType != typeof(string) && !field.FieldType.IsGenericType && entry is not JObject) return false;
                }
                return true;
            }
            bool Strings(JToken token) => token is JArray values && values.All(value => value.Type == JTokenType.String);
            bool Pool(JToken token) => Fields(token, typeof(PreparationPoolPolicy)) &&
                Strings(token["CandidateDefinitionIds"]) && Strings(token["RequiredDefinitionIds"]);
            bool Enterprise(JToken token) => Fields(token, typeof(PreparationEnterprisePlan)) && Pool(token["Pool"]) &&
                (token["PlayerCounts"] as JArray).All(item => Fields(item, typeof(PreparationEnterpriseCountRule))) &&
                (token["Fixed"] as JArray).All(item => Fields(item, typeof(PreparationFixedEnterprise)));
            bool Step(JToken token)
            {
                if (!Fields(token, typeof(PreparationStepDeclaration), "Completion") ||
                    token["Arguments"] is not JObject arguments || !HasRawNormalizedFields(arguments, 0)) return false;
                return token["Completion"].Type == JTokenType.Null || Fields(token["Completion"], typeof(PreparationCompletionDeclaration));
            }
            if (!Fields(plan, typeof(PreparationFlowPlan), "Enterprise", "CityStyles") ||
                !(plan["BeforeDistribution"] as JArray).All(Step) ||
                !(plan["BetweenDistributionAndConfirmation"] as JArray).All(Step) ||
                !(plan["AfterConfirmation"] as JArray).All(Step) ||
                !(plan["DistributionInputs"] as JArray).All(item => Fields(item, typeof(PreparationOutputBinding)))) return false;
            if (plan["Enterprise"].Type != JTokenType.Null)
            {
                if (!Enterprise(plan["Enterprise"])) return false;
            }
            if (plan["CityStyles"].Type != JTokenType.Null &&
                (!Fields(plan["CityStyles"], typeof(PreparationCityStylePlan)) ||
                 !Pool(plan["CityStyles"]["LevelOne"]) || !Pool(plan["CityStyles"]["LevelTwo"]))) return false;
            if (runtime == null || runtime.Type == JTokenType.Null) return true;
            if (!Fields(runtime, typeof(PreparationRuntimeState), "Distribution", "EffectiveEnterprise")) return false;
            if (runtime["EffectiveEnterprise"].Type != JTokenType.Null && !Enterprise(runtime["EffectiveEnterprise"])) return false;
            var distribution = runtime["Distribution"];
            if (distribution.Type == JTokenType.Null) return true;
            if (!Fields(distribution, typeof(PreparationEnterpriseDistributionRecord)) ||
                !(distribution["PlayerOrder"] as JArray).All(item => item.Type == JTokenType.Integer) ||
                !Strings(distribution["UnassignedItemIds"]) || !Strings(distribution["ExtraItemIds"]) ||
                !(distribution["Automatic"] as JArray).All(item => Fields(item, typeof(PreparationAutomaticEnterprise)))) return false;
            return (distribution["Assignments"] as JArray).All(item => Fields(item, typeof(PreparationEnterpriseAssignment)) &&
                Strings(item["CandidateItemIds"]) && Strings(item["RequiredItemIds"]) && Strings(item["SelectedItemIds"]));
        }
    }
}
