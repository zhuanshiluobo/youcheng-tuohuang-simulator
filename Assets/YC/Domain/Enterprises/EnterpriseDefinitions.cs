using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Domain.Presentation;

namespace YC.Domain.Enterprises
{
    [Serializable]
    public sealed class EnterpriseLevelDefinition
    {
        public int Level;
        public string Kind = "empty", AbilityId = string.Empty;
        public GameplayTextRef Description;
    }

    [Serializable]
    public sealed class EnterpriseDefinition
    {
        public string DefinitionId = string.Empty, RuntimeId = string.Empty, DisplayNameKey = string.Empty;
        public string DepartmentRewardPolicy = string.Empty;
        public ContentPresentationDefinition Presentation;
        public List<EnterpriseLevelDefinition> Levels = new List<EnterpriseLevelDefinition>();
    }

    [Serializable]
    public sealed class DepartmentRewardDefinition
    {
        public int Level;
        public string AbilityId = string.Empty;
        public GameplayTextRef Description;
    }

    [Serializable]
    public sealed class DepartmentDefinition
    {
        public string DefinitionId = string.Empty, EnterpriseRuntimeId = string.Empty, DisplayNameKey = string.Empty;
        public ContentPresentationDefinition Presentation;
        public List<DepartmentRewardDefinition> Rewards = new List<DepartmentRewardDefinition>();
    }

    [Serializable]
    public sealed class EnterpriseSpecialCandidate
    {
        public string CandidateId = string.Empty, EnterpriseItemId = string.Empty, SpecialId = string.Empty;
        public int CurrentLevel, EffectiveLevelCeiling;
        public string DisplayNameKey = string.Empty;
    }

    // 节点持有的历史快照，不是第二份可写等级。大型冻结集合不塞进 NormalizedValue。
    [Serializable]
    public sealed class FrozenEnterpriseSpecial
    {
        public EnterpriseSpecialCandidate Candidate = new EnterpriseSpecialCandidate();
        public string EnterpriseDefinitionId = string.Empty;
        public int UnlockedByPlayerId = -1;
    }

    [Serializable]
    public sealed class EnterpriseArrivalReceipt
    {
        public string EventId = string.Empty, EnterpriseItemId = string.Empty, DepartmentItemId = string.Empty;
        public string RewardAbilityId = string.Empty;
        public int PlayerId = -1, OldLevel, NewLevel;
    }

    /// <summary>每局隔离的不可变内容目录。只保存定义与返回 Spec 的能力绑定，不保存玩家等级。</summary>
    public sealed class EnterpriseDefinitions
    {
        private readonly Dictionary<string, EnterpriseDefinition> enterprises = new Dictionary<string, EnterpriseDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, DepartmentDefinition> departments = new Dictionary<string, DepartmentDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<EffectEventHandlerContext, IList<EffectSpec>>> abilities = new Dictionary<string, Func<EffectEventHandlerContext, IList<EffectSpec>>>(StringComparer.Ordinal);
        private bool frozen;

        public void Register(EnterpriseDefinition definition)
        {
            RequireMutable();
            if (definition == null || string.IsNullOrWhiteSpace(definition.DefinitionId) || string.IsNullOrWhiteSpace(definition.RuntimeId) || definition.Levels == null ||
                definition.Levels.Count != 6 || definition.Levels.Any(level => level == null || level.Level < 0 || level.Level > 5 ||
                    !new[] { "empty", "reward", "special", "department_reward" }.Contains(level.Kind) ||
                    (level.Kind == "empty" || level.Kind == "department_reward" ? !string.IsNullOrEmpty(level.AbilityId) : string.IsNullOrWhiteSpace(level.AbilityId))) ||
                definition.Levels.Select(level => level.Level).Distinct().Count() != 6 || definition.Levels.Single(level => level.Level == 0).Kind != "empty" ||
                definition.Levels.Where(level => level.Kind == "special").Select(level => level.AbilityId).Distinct().Count() != definition.Levels.Count(level => level.Kind == "special") ||
                (definition.DepartmentRewardPolicy != "" && definition.DepartmentRewardPolicy != "active_department") ||
                definition.Levels.Any(level => level.Kind == "department_reward" && (definition.DepartmentRewardPolicy != "active_department" || level.Level < 2)))
                throw new InvalidOperationException("enterprise_definition_invalid");
            if (enterprises.ContainsKey(definition.DefinitionId)) throw new InvalidOperationException("enterprise_definition_duplicate");
            enterprises.Add(definition.DefinitionId, GameStateCloneService.DeepClone(definition));
        }

        public void Register(DepartmentDefinition definition)
        {
            RequireMutable();
            if (definition == null || string.IsNullOrWhiteSpace(definition.DefinitionId) || string.IsNullOrWhiteSpace(definition.EnterpriseRuntimeId) || definition.Rewards == null ||
                definition.Rewards.Count != 4 || definition.Rewards.Any(reward => reward == null || reward.Level < 2 || reward.Level > 5 || string.IsNullOrWhiteSpace(reward.AbilityId)) ||
                definition.Rewards.Select(reward => reward.Level).Distinct().Count() != 4) throw new InvalidOperationException("department_definition_invalid");
            if (departments.ContainsKey(definition.DefinitionId)) throw new InvalidOperationException("department_definition_duplicate");
            departments.Add(definition.DefinitionId, GameStateCloneService.DeepClone(definition));
        }

        public void BindAbility(string abilityId, Func<EffectEventHandlerContext, IList<EffectSpec>> factory)
        {
            RequireMutable();
            if (string.IsNullOrWhiteSpace(abilityId) || factory == null || abilities.ContainsKey(abilityId)) throw new InvalidOperationException("enterprise_ability_binding_invalid");
            abilities.Add(abilityId, factory);
        }
        public void Freeze() { frozen = true; }
        public EnterpriseDefinition FindEnterprise(string definitionId) => definitionId != null && enterprises.TryGetValue(definitionId, out var value) ? GameStateCloneService.DeepClone(value) : null;
        public DepartmentDefinition FindDepartment(string definitionId) => definitionId != null && departments.TryGetValue(definitionId, out var value) ? GameStateCloneService.DeepClone(value) : null;
        internal IList<EffectSpec> ResolveAbility(string abilityId, EffectEventHandlerContext context)
        {
            if (string.IsNullOrEmpty(abilityId)) return Array.Empty<EffectSpec>();
            if (!abilities.TryGetValue(abilityId, out var factory)) return new[] { EnterpriseEffectExecutor.IncompleteSpec(context.Event.PlayerId, abilityId) };
            var specs = factory(context);
            if (specs == null || specs.Count == 0) return new[] { EnterpriseEffectExecutor.IncompleteSpec(context.Event.PlayerId, abilityId) };
            if (specs.Any(spec => spec == null)) throw new InvalidOperationException("enterprise_ability_spec_invalid");
            return specs;
        }
        private void RequireMutable() { if (frozen) throw new InvalidOperationException("enterprise_definitions_frozen"); }
    }
}
