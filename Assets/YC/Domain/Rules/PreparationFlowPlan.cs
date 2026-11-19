using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    /// <summary>模式冻结的准备合同。这里只验证声明；执行器和能力尚需分别注册。</summary>
    [Serializable]
    public sealed class PreparationFlowPlan
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public string ModeId = string.Empty;
        public List<PreparationStepDeclaration> BeforeDistribution = new List<PreparationStepDeclaration>();
        public List<PreparationStepDeclaration> BetweenDistributionAndConfirmation = new List<PreparationStepDeclaration>();
        public List<PreparationStepDeclaration> AfterConfirmation = new List<PreparationStepDeclaration>();
        public List<PreparationOutputBinding> DistributionInputs = new List<PreparationOutputBinding>();
        public PreparationEnterprisePlan Enterprise;
        public PreparationCityStylePlan CityStyles;

        public void Validate()
        {
            if (Version != CurrentVersion || !PreparationContract.IsIdentity(ModeId) ||
                BeforeDistribution == null || BetweenDistributionAndConfirmation == null || AfterConfirmation == null ||
                DistributionInputs == null)
                throw new InvalidOperationException("preparation_plan_invalid");

            var all = BeforeDistribution.Concat(BetweenDistributionAndConfirmation).Concat(AfterConfirmation).ToArray();
            if (all.Length > 128 || all.Any(step => step == null))
                throw new InvalidOperationException("preparation_steps_invalid");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in all)
            {
                step.Validate();
                if (!ids.Add(step.StepId)) throw new InvalidOperationException("preparation_step_duplicate:" + step.StepId);
            }
            if (Enterprise == null && BetweenDistributionAndConfirmation.Count != 0)
                throw new InvalidOperationException("preparation_middle_requires_enterprise");

            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in DistributionInputs)
            {
                if (binding == null) throw new InvalidOperationException("preparation_output_binding_invalid");
                binding.Validate();
                var step = BeforeDistribution.SingleOrDefault(value => value.StepId == binding.StepId);
                if (step == null) throw new InvalidOperationException("preparation_output_step_not_before:" + binding.StepId);
                if (!targets.Add(binding.Target)) throw new InvalidOperationException("preparation_output_target_duplicate:" + binding.Target);
                if (binding.PlayerScope == "each" && step.Execution == "once" ||
                    binding.PlayerScope == "once" && step.Execution != "once")
                    throw new InvalidOperationException("preparation_output_scope_mismatch:" + binding.StepId);
                if (binding.PlayerScope == "each" && (binding.Target == "candidateCount" || binding.Target == "extraRandomCount"))
                    throw new InvalidOperationException("preparation_output_count_scope_invalid:" + binding.StepId);
            }
            if (DistributionInputs.Count > 0 && Enterprise == null)
                throw new InvalidOperationException("preparation_output_requires_enterprise");
            Enterprise?.Validate();
            CityStyles?.Validate();
        }
    }

    [Serializable]
    public sealed class PreparationStepDeclaration
    {
        public string StepId = string.Empty;
        public string SourcePackId = string.Empty;
        public string SourcePackVersion = string.Empty;
        public string Kind = string.Empty; // builtin / lua
        public string HandlerId = string.Empty;
        public string HandlerVersion = string.Empty;
        public string Execution = string.Empty; // once / eachInOrder / eachIndependent
        public string ScriptPath = string.Empty;
        public string ScriptHash = string.Empty;
        public PreparationCompletionDeclaration Completion;
        public NormalizedValue Arguments = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());

        public void Validate()
        {
            string at = string.IsNullOrEmpty(StepId) ? "<empty>" : StepId;
            if (!PreparationContract.IsIdentity(StepId) || !PreparationContract.IsPackId(SourcePackId) ||
                !PreparationContract.IsVersion(SourcePackVersion) || !PreparationContract.IsIdentity(HandlerId) ||
                !PreparationContract.IsVersion(HandlerVersion) ||
                !new[] { "once", "eachInOrder", "eachIndependent" }.Contains(Execution) ||
                Arguments == null || Arguments.Kind != NormalizedValueKind.Object || !Arguments.TryValidate(out _))
                throw new InvalidOperationException("preparation_step_invalid:" + at);
            if (Kind == "builtin")
            {
                if (!string.IsNullOrEmpty(ScriptPath) || !string.IsNullOrEmpty(ScriptHash) || Completion != null)
                    throw new InvalidOperationException("preparation_builtin_invalid:" + at);
            }
            else if (Kind == "lua")
            {
                if (!PreparationContract.IsScriptPath(ScriptPath) || !PreparationContract.IsSha256(ScriptHash))
                    throw new InvalidOperationException("preparation_lua_invalid:" + at);
                Completion?.Validate(at);
            }
            else throw new InvalidOperationException("preparation_step_kind_invalid:" + at);
        }
    }

    [Serializable]
    public sealed class PreparationCompletionDeclaration
    {
        public string HandlerId = string.Empty;
        public string HandlerVersion = string.Empty;
        public string ScriptPath = string.Empty;
        public string ScriptHash = string.Empty;
        internal void Validate(string at)
        {
            if (!PreparationContract.IsIdentity(HandlerId) || !PreparationContract.IsVersion(HandlerVersion) ||
                !PreparationContract.IsScriptPath(ScriptPath) || !PreparationContract.IsSha256(ScriptHash))
                throw new InvalidOperationException("preparation_completion_invalid:" + at);
        }
    }

    /// <summary>从前置步骤的真实效果结果读取有限输入；不把普通 Lua memory 当作可信配方。</summary>
    [Serializable]
    public sealed class PreparationOutputBinding
    {
        public string StepId = string.Empty;
        public string EffectTypeId = string.Empty;
        public string EffectStableKey = string.Empty;
        public string ResultField = string.Empty;
        public string PlayerScope = string.Empty; // once / each
        public string Target = string.Empty;
        internal void Validate()
        {
            if (!PreparationContract.IsIdentity(StepId) || !PreparationContract.IsIdentity(EffectTypeId) ||
                !PreparationContract.IsStableKey(EffectStableKey) || !PreparationContract.IsIdentity(ResultField) ||
                !new[] { "once", "each" }.Contains(PlayerScope) ||
                !new[] { "candidateDefinitionIds", "requiredDefinitionIds", "candidateCount", "extraRandomCount" }.Contains(Target))
                throw new InvalidOperationException("preparation_output_binding_invalid:" + StepId);
        }
    }

    [Serializable]
    public sealed class PreparationPoolPolicy
    {
        public string Kind = "all"; // all / fixed / requiredRandom
        public List<string> CandidateDefinitionIds = new List<string>();
        public List<string> RequiredDefinitionIds = new List<string>();
        public int Count;

        internal void Validate(string at)
        {
            if (CandidateDefinitionIds == null || RequiredDefinitionIds == null ||
                CandidateDefinitionIds.Any(id => !PreparationContract.IsIdentity(id)) ||
                RequiredDefinitionIds.Any(id => !PreparationContract.IsIdentity(id)) ||
                CandidateDefinitionIds.Distinct(StringComparer.Ordinal).Count() != CandidateDefinitionIds.Count ||
                RequiredDefinitionIds.Distinct(StringComparer.Ordinal).Count() != RequiredDefinitionIds.Count ||
                CandidateDefinitionIds.Count > 256 || RequiredDefinitionIds.Count > 256)
                throw new InvalidOperationException("preparation_pool_invalid:" + at);
            switch (Kind)
            {
                case "all":
                    if (Count != 0 || CandidateDefinitionIds.Count != 0 || RequiredDefinitionIds.Count != 0)
                        throw new InvalidOperationException("preparation_pool_all_invalid:" + at);
                    break;
                case "fixed":
                    if (CandidateDefinitionIds.Count == 0 || Count != CandidateDefinitionIds.Count || RequiredDefinitionIds.Count != 0)
                        throw new InvalidOperationException("preparation_pool_fixed_invalid:" + at);
                    break;
                case "requiredRandom":
                    if (Count < 0 || Count > 256 || RequiredDefinitionIds.Count > Count ||
                        CandidateDefinitionIds.Count > 0 && (Count > CandidateDefinitionIds.Count ||
                            RequiredDefinitionIds.Any(id => !CandidateDefinitionIds.Contains(id))))
                        throw new InvalidOperationException("preparation_pool_random_invalid:" + at);
                    break;
                default: throw new InvalidOperationException("preparation_pool_kind_invalid:" + at);
            }
        }
    }

    [Serializable]
    public sealed class PreparationEnterprisePlan
    {
        public PreparationPoolPolicy Pool = new PreparationPoolPolicy();
        public List<PreparationEnterpriseCountRule> PlayerCounts = new List<PreparationEnterpriseCountRule>();
        public List<PreparationFixedEnterprise> Fixed = new List<PreparationFixedEnterprise>();

        internal void Validate()
        {
            if (Pool == null || PlayerCounts == null || Fixed == null || PlayerCounts.Count == 0 ||
                PlayerCounts.Any(rule => rule == null) || Fixed.Any(item => item == null))
                throw new InvalidOperationException("preparation_enterprise_invalid");
            Pool.Validate("enterprise");
            if (PlayerCounts.Select(rule => rule.PlayerCount).Distinct().Count() != PlayerCounts.Count ||
                Fixed.Select(item => item.DefinitionId).Distinct(StringComparer.Ordinal).Count() != Fixed.Count)
                throw new InvalidOperationException("preparation_enterprise_duplicate");
            foreach (var item in Fixed)
                if (!PreparationContract.IsIdentity(item.DefinitionId) || item.OwnerOrderIndex < -1 ||
                    item.OwnerOrderIndex > 3 || item.RequireConfirmation && item.OwnerOrderIndex < 0)
                    throw new InvalidOperationException("preparation_enterprise_fixed_invalid:" + item.DefinitionId);
            if (Pool.Kind == "fixed" || Pool.Kind == "requiredRandom" && Pool.CandidateDefinitionIds.Count > 0)
                foreach (var item in Fixed)
                    if (!Pool.CandidateDefinitionIds.Contains(item.DefinitionId))
                        throw new InvalidOperationException("preparation_enterprise_fixed_outside_pool:" + item.DefinitionId);
            foreach (var rule in PlayerCounts)
            {
                int automatic = Fixed.Count(item => !item.RequireConfirmation);
                if (rule.PlayerCount < 2 || rule.PlayerCount > 4 || rule.Draw < 1 || rule.Draw > 64 ||
                    rule.Keep < 1 || rule.Keep > rule.Draw || rule.ExtraRandom < 0 || rule.ExtraRandom > 64 ||
                    rule.FinalCount != rule.PlayerCount * rule.Keep + rule.ExtraRandom + automatic ||
                    rule.FinalCount > 64 || Fixed.Any(item => item.OwnerOrderIndex >= rule.PlayerCount) ||
                    Pool.Kind != "all" && Pool.Count < rule.Draw * rule.PlayerCount + rule.ExtraRandom + automatic ||
                    Enumerable.Range(0, rule.PlayerCount).Any(index => Fixed.Count(item =>
                        item.RequireConfirmation && item.OwnerOrderIndex == index) > rule.Keep))
                    throw new InvalidOperationException("preparation_enterprise_count_invalid:" + rule.PlayerCount);
            }
        }
    }

    [Serializable]
    public sealed class PreparationEnterpriseCountRule
    {
        public int PlayerCount;
        public int Draw;
        public int Keep;
        public int ExtraRandom;
        public int FinalCount;
    }

    [Serializable]
    public sealed class PreparationFixedEnterprise
    {
        public string DefinitionId = string.Empty;
        // -1 表示无初始所有者；非负值对应冻结玩家顺序的索引。
        public int OwnerOrderIndex = -1;
        public bool RequireConfirmation;
    }

    [Serializable]
    public sealed class PreparationCityStylePlan
    {
        public PreparationPoolPolicy LevelOne = new PreparationPoolPolicy();
        public PreparationPoolPolicy LevelTwo = new PreparationPoolPolicy();
        internal void Validate()
        {
            if (LevelOne == null || LevelTwo == null) throw new InvalidOperationException("preparation_city_styles_invalid");
            LevelOne.Validate("city_style_level_one");
            LevelTwo.Validate("city_style_level_two");
        }
    }

    internal static class PreparationContract
    {
        internal static bool IsIdentity(string value) => IsIdentity(value, 192, false);
        internal static bool IsPackId(string value) => IsIdentity(value, 128, true);
        private static bool IsIdentity(string value, int max, bool lowerOnly)
        {
            if (string.IsNullOrEmpty(value) || value.Length > max) return false;
            bool previousSeparator = true;
            foreach (char c in value)
            {
                bool letter = c >= 'a' && c <= 'z' || !lowerOnly && c >= 'A' && c <= 'Z';
                bool digit = c >= '0' && c <= '9';
                if (letter || digit) { previousSeparator = false; continue; }
                if ((c != '.' && c != '_' && c != '-') || previousSeparator) return false;
                previousSeparator = true;
            }
            return !previousSeparator;
        }
        internal static bool IsVersion(string value)
        {
            var parts = (value ?? string.Empty).Split('.');
            return parts.Length == 3 && parts.All(part => part.Length > 0 &&
                (part.Length == 1 || part[0] != '0') && part.All(c => c >= '0' && c <= '9') &&
                int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        }
        internal static bool IsSha256(string value) => value != null && value.Length == 64 &&
            value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        internal static bool IsStableKey(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
            value.All(c => c >= 33 && c <= 126);
        internal static bool IsScriptPath(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 512 || !value.EndsWith(".lua", StringComparison.Ordinal) ||
                !value.StartsWith("lua/", StringComparison.Ordinal) || value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0 ||
                value.Any(c => c < 32 || "<>\"|?*".IndexOf(c) >= 0)) return false;
            var parts = value.Split('/');
            return parts.All(part => part.Length > 0 && part != "." && part != ".." &&
                !part.EndsWith(".", StringComparison.Ordinal) && !part.EndsWith(" ", StringComparison.Ordinal));
        }
    }
}
