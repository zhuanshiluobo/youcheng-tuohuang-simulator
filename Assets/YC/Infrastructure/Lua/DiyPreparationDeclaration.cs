using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    /// <summary>只读取模式声明；不执行 Lua。脚本身份在依赖闭包确定后由冻结文件计算。</summary>
    public sealed class DiyPreparationDeclaration
    {
        public const string Capability = "diy.preparation_flow.v1";
        private readonly PreparationFlowPlan draft;
        private DiyPreparationDeclaration(PreparationFlowPlan plan) { draft = plan; }

        internal bool HasSteps => draft.BeforeDistribution.Count != 0 ||
            draft.BetweenDistributionAndConfirmation.Count != 0 || draft.AfterConfirmation.Count != 0;

        internal IEnumerable<PreparationStepDeclaration> UnresolvedSteps =>
            draft.BeforeDistribution.Concat(draft.BetweenDistributionAndConfirmation).Concat(draft.AfterConfirmation)
                .Select(GameStateCloneService.DeepClone).ToArray();

        public static DiyPreparationDeclaration Parse(JObject value, string modeId, string source)
        {
            if (value == null) throw DiyPackManifest.Error(source, "准备阶段必须是对象");
            DiyPackManifest.Fields(value, source, "version", "beforeDistribution", "betweenDistributionAndConfirmation",
                "afterConfirmation", "distributionInputs", "enterprise", "cityStyles");
            if (DiyPackManifest.Integer(value, "version", source) != PreparationFlowPlan.CurrentVersion)
                throw DiyPackManifest.Error(source + ":version", "准备阶段版本不支持");
            var plan = new PreparationFlowPlan { ModeId = modeId };
            plan.BeforeDistribution = ReadSteps(value, "beforeDistribution", source);
            plan.BetweenDistributionAndConfirmation = ReadSteps(value, "betweenDistributionAndConfirmation", source);
            plan.AfterConfirmation = ReadSteps(value, "afterConfirmation", source);
            plan.DistributionInputs = DiyPackManifest.Objects(value, "distributionInputs", source).Select((item, i) => {
                string at = source + ":distributionInputs[" + i + "]";
                DiyPackManifest.Fields(item, at, "stepId", "effectTypeId", "effectStableKey", "resultField", "playerScope", "target");
                return new PreparationOutputBinding { StepId = DiyPackManifest.Text(item, "stepId", at),
                    EffectTypeId = DiyPackManifest.Text(item, "effectTypeId", at), EffectStableKey = DiyPackManifest.Text(item, "effectStableKey", at),
                    ResultField = DiyPackManifest.Text(item, "resultField", at), PlayerScope = DiyPackManifest.Text(item, "playerScope", at),
                    Target = DiyPackManifest.Text(item, "target", at) };
            }).ToList();
            if (value["enterprise"] != null)
            {
                if (!(value["enterprise"] is JObject enterprise)) throw DiyPackManifest.Error(source + ":enterprise", "必须是对象");
                plan.Enterprise = ReadEnterprise(enterprise, source + ":enterprise");
            }
            if (value["cityStyles"] != null)
            {
                if (!(value["cityStyles"] is JObject styles)) throw DiyPackManifest.Error(source + ":cityStyles", "必须是对象");
                DiyPackManifest.Fields(styles, source + ":cityStyles", "levelOne", "levelTwo");
                plan.CityStyles = new PreparationCityStylePlan {
                    LevelOne = ReadPool(styles["levelOne"] as JObject, source + ":cityStyles.levelOne"),
                    LevelTwo = ReadPool(styles["levelTwo"] as JObject, source + ":cityStyles.levelTwo") };
            }
            try { plan.Validate(); }
            catch (InvalidOperationException error) { throw new InvalidDataException(source + ": 准备阶段声明无效：" + error.Message, error); }
            return new DiyPreparationDeclaration(plan);
        }

        /// <summary>解析器收到的版本和源码必须来自所选模式的真实依赖闭包。</summary>
        public PreparationFlowPlan Resolve(Func<string, string, string, string> readFrozenScript)
        {
            if (readFrozenScript == null) throw new ArgumentNullException(nameof(readFrozenScript));
            var plan = GameStateCloneService.DeepClone(draft);
            foreach (var step in plan.BeforeDistribution.Concat(plan.BetweenDistributionAndConfirmation).Concat(plan.AfterConfirmation))
            {
                if (step.Kind != "lua") continue;
                step.ScriptHash = LuaContentHasher.ComputeSha256(readFrozenScript(step.SourcePackId, step.SourcePackVersion, step.ScriptPath));
                if (step.Completion != null)
                    step.Completion.ScriptHash = LuaContentHasher.ComputeSha256(readFrozenScript(step.SourcePackId, step.SourcePackVersion, step.Completion.ScriptPath));
            }
            plan.Validate();
            return plan;
        }

        private static List<PreparationStepDeclaration> ReadSteps(JObject parent, string name, string source) =>
            DiyPackManifest.Objects(parent, name, source).Select((item, i) => ReadStep(item, source + ":" + name + "[" + i + "]")).ToList();

        private static PreparationStepDeclaration ReadStep(JObject item, string at)
        {
            DiyPackManifest.Fields(item, at, "stepId", "sourcePackId", "sourcePackVersion", "kind", "handlerId", "handlerVersion", "execution", "script", "completion", "arguments");
            var step = new PreparationStepDeclaration {
                StepId = DiyPackManifest.Text(item, "stepId", at), SourcePackId = DiyPackManifest.Text(item, "sourcePackId", at),
                SourcePackVersion = DiyPackManifest.Text(item, "sourcePackVersion", at), Kind = DiyPackManifest.Text(item, "kind", at),
                HandlerId = DiyPackManifest.Text(item, "handlerId", at), HandlerVersion = DiyPackManifest.Text(item, "handlerVersion", at),
                Execution = DiyPackManifest.Text(item, "execution", at)
            };
            if (item["arguments"] != null)
            {
                if (!(item["arguments"] is JObject)) throw DiyPackManifest.Error(at + ":arguments", "必须是对象");
                step.Arguments = Normalize(item["arguments"], at + ":arguments");
            }
            if (step.Kind == "lua")
            {
                step.ScriptPath = ScriptPath(item, "script", at);
                // 用固定临时值验证数据结构；该值不会进入真实已解析的配方。
                step.ScriptHash = new string('0', 64);
                if (item["completion"] != null)
                {
                    if (!(item["completion"] is JObject completion)) throw DiyPackManifest.Error(at + ":completion", "必须是对象");
                    DiyPackManifest.Fields(completion, at + ":completion", "handlerId", "handlerVersion", "script");
                    step.Completion = new PreparationCompletionDeclaration { HandlerId = DiyPackManifest.Text(completion, "handlerId", at),
                        HandlerVersion = DiyPackManifest.Text(completion, "handlerVersion", at), ScriptPath = ScriptPath(completion, "script", at + ":completion"),
                        ScriptHash = new string('0', 64) };
                }
            }
            else if (item["script"] != null || item["completion"] != null)
                throw DiyPackManifest.Error(at, "非 Lua 步骤不能声明脚本或续接");
            return step;
        }

        private static string ScriptPath(JObject item, string field, string source)
        {
            string path = DiyPackManifest.Text(item, field, source);
            DiyPackPaths.ValidateRelative(path);
            if (!path.StartsWith("lua/", StringComparison.Ordinal) || !path.EndsWith(".lua", StringComparison.Ordinal))
                throw DiyPackManifest.Error(source + ":" + field, "必须是包内 lua/ 目录的 Lua 文件");
            return path;
        }

        private static PreparationEnterprisePlan ReadEnterprise(JObject item, string source)
        {
            DiyPackManifest.Fields(item, source, "pool", "playerCounts", "fixed");
            var plan = new PreparationEnterprisePlan { Pool = ReadPool(item["pool"] as JObject, source + ":pool") };
            plan.PlayerCounts = DiyPackManifest.Objects(item, "playerCounts", source).Select((rule, i) => {
                string at = source + ":playerCounts[" + i + "]";
                DiyPackManifest.Fields(rule, at, "playerCount", "draw", "keep", "extraRandom", "finalCount");
                return new PreparationEnterpriseCountRule { PlayerCount = DiyPackManifest.Integer(rule, "playerCount", at),
                    Draw = DiyPackManifest.Integer(rule, "draw", at), Keep = DiyPackManifest.Integer(rule, "keep", at),
                    ExtraRandom = OptionalInteger(rule, "extraRandom", at), FinalCount = DiyPackManifest.Integer(rule, "finalCount", at) };
            }).ToList();
            plan.Fixed = DiyPackManifest.Objects(item, "fixed", source).Select((fixedItem, i) => {
                string at = source + ":fixed[" + i + "]";
                DiyPackManifest.Fields(fixedItem, at, "definitionId", "ownerOrderIndex", "requireConfirmation");
                int owner = -1;
                if (fixedItem["ownerOrderIndex"] != null && (fixedItem["ownerOrderIndex"].Type != JTokenType.Integer ||
                    !int.TryParse(fixedItem["ownerOrderIndex"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out owner)))
                    throw DiyPackManifest.Error(at + ":ownerOrderIndex", "必须是有效整数");
                if (fixedItem["requireConfirmation"] != null && fixedItem["requireConfirmation"].Type != JTokenType.Boolean)
                    throw DiyPackManifest.Error(at + ":requireConfirmation", "必须是布尔值");
                return new PreparationFixedEnterprise { DefinitionId = DiyPackManifest.Text(fixedItem, "definitionId", at), OwnerOrderIndex = owner,
                    RequireConfirmation = (bool?)fixedItem["requireConfirmation"] ?? false };
            }).ToList();
            return plan;
        }

        private static PreparationPoolPolicy ReadPool(JObject item, string source)
        {
            if (item == null) throw DiyPackManifest.Error(source, "必须是对象");
            DiyPackManifest.Fields(item, source, "kind", "candidateDefinitionIds", "requiredDefinitionIds", "count");
            var pool = new PreparationPoolPolicy { Kind = DiyPackManifest.Text(item, "kind", source),
                CandidateDefinitionIds = DiyPackManifest.Strings(item, "candidateDefinitionIds", source).ToList(),
                RequiredDefinitionIds = DiyPackManifest.Strings(item, "requiredDefinitionIds", source).ToList() };
            pool.Count = item["count"] == null && pool.Kind == "fixed" ? pool.CandidateDefinitionIds.Count : OptionalInteger(item, "count", source);
            return pool;
        }

        private static int OptionalInteger(JObject item, string name, string source) =>
            item[name] == null ? 0 : DiyPackManifest.Integer(item, name, source);

        private static NormalizedValue Normalize(JToken value, string source, int depth = 0)
        {
            if (depth > NormalizedValue.MaxDepth) throw DiyPackManifest.Error(source, "参数层级过深");
            NormalizedValue result;
            switch (value.Type)
            {
                case JTokenType.Null: result = NormalizedValue.CreateNull(); break;
                case JTokenType.Boolean: result = NormalizedValue.CreateBoolean((bool)value); break;
                case JTokenType.Integer:
                    if (!long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ||
                        number < NormalizedValue.MinInteger || number > NormalizedValue.MaxInteger)
                        throw DiyPackManifest.Error(source, "整数超出归一化范围");
                    result = NormalizedValue.CreateInteger(number); break;
                case JTokenType.String:
                    try { result = NormalizedValue.CreateString((string)value); }
                    catch (ArgumentException error) { throw new InvalidDataException(source + ": 字符串无效", error); }
                    break;
                case JTokenType.Array:
                    if (((JArray)value).Count > NormalizedValue.MaxElements) throw DiyPackManifest.Error(source, "数组过长");
                    result = NormalizedValue.CreateArray(((JArray)value).Select((item, i) => Normalize(item, source + "[" + i + "]", depth + 1)).ToList()); break;
                case JTokenType.Object:
                    if (((JObject)value).Count > NormalizedValue.MaxElements) throw DiyPackManifest.Error(source, "对象字段过多");
                    result = NormalizedValue.CreateObject(((JObject)value).Properties().Select(property => new NormalizedValueEntry {
                        Name = property.Name, Value = Normalize(property.Value, source + "." + property.Name, depth + 1) }).ToList()); break;
                default: throw DiyPackManifest.Error(source, "只支持空值、布尔值、整数、字符串、数组和对象");
            }
            if (!result.TryValidate(out string reason)) throw DiyPackManifest.Error(source, "参数无效：" + reason);
            return result;
        }
    }
}
