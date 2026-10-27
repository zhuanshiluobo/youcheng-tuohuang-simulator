using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Enterprises;

namespace YC.Infrastructure.Lua
{
    /// <summary>供组合根注入的基础内容注册入口；Application 无需反射或引用本程序集。</summary>
    public static class LuaContentCatalog
    {
        /// <summary>009 传入本局有效定义；不遍历安装目录，也不自动启用测试或正式扩展。</summary>
        public static EnterpriseDefinitions CreateEnterpriseDefinitions(EffectRegistry registry, IEnumerable<ExternalContentDefinition> activeDefinitions, Func<string, string> getScript, ExternalContentPack pack = null)
        {
            var definitions = new EnterpriseDefinitions();
            var dispatcher = new LuaEffectEventDispatcher(registry, compiler: new LuaEffectSpecCompiler(presentationPack: pack ?? ExternalContentPack.Current));
            foreach (var content in activeDefinitions.Where(value => value.Enabled && (value.ContentType == "enterprise" || value.ContentType == "department")))
            {
                if (content.ContentType == "enterprise") definitions.Register(new EnterpriseDefinition { DefinitionId = content.DefinitionId, RuntimeId = content.RuntimeId, DisplayNameKey = content.DisplayName,
                    DepartmentRewardPolicy = (string)content.Data["departmentRewardPolicy"] ?? "", Presentation = content.Presentation,
                    Levels = content.Data["levels"]?.Select(level => new EnterpriseLevelDefinition { Level = (int)level["level"],
                        Kind = (string)level["kind"], AbilityId = (string)level["abilityId"],
                        Description = GameplayPresentationCatalog.ReadDto<YC.Domain.Presentation.GameplayTextRef>(level["description"]) }).ToList() });
                else definitions.Register(new DepartmentDefinition { DefinitionId = content.DefinitionId, EnterpriseRuntimeId = (string)content.Data["enterpriseRuntimeId"], DisplayNameKey = content.DisplayName,
                    Presentation = content.Presentation, Rewards = content.Data["rewards"]?.Select(reward => new DepartmentRewardDefinition {
                        Level = (int)reward["level"], AbilityId = (string)reward["abilityId"],
                        Description = GameplayPresentationCatalog.ReadDto<YC.Domain.Presentation.GameplayTextRef>(reward["description"]) }).ToList() });
                foreach (var ability in content.Abilities)
                {
                    string source = getScript(ability.Script);
                    var script = new LuaScriptDefinition(content.DefinitionId, ability.AbilityId, ability.SubscriptionId, ability.Version, source, LuaContentHasher.ComputeSha256(source));
                    var handler = new LuaHandlerDefinition(ability.SubscriptionId, "EnterpriseSpecialEffectActivated", "", script, EffectHandlerRole.Primary, abilityId: ability.AbilityId,
                        completionHandlerId: ability.CompletionHandlerId);
                    if (!string.IsNullOrEmpty(ability.CompletionHandlerId)) dispatcher.RegisterContinuation(new LuaHandlerDefinition(ability.CompletionHandlerId, "EffectCompleted", "",
                        new LuaScriptDefinition(content.DefinitionId, ability.AbilityId, ability.CompletionHandlerId, ability.Version, source, LuaContentHasher.ComputeSha256(source)),
                        EffectHandlerRole.Continuation, abilityId: ability.AbilityId));
                    definitions.BindAbility(ability.AbilityId, context => dispatcher.Invoke(handler, context));
                }
            }
            definitions.Freeze();
            return definitions;
        }

        public static void Register(EffectRegistry registry)
        {
            if (registry.LaunchConfiguration != null)
            {
                var catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Current, registry.LaunchConfiguration);
                registry.LaunchConfiguration.ContentPackHash = catalog.ContentPackHash;
                catalog.Register(registry);
                return;
            }
            PlayerEntranceLuaCatalog.Register(registry);
            EventCardLuaCatalog.Register(registry);
            CharacterCardLuaCatalog.Register(registry);
            FacilityLuaCatalog.Register(registry);
            CityStyleLuaCatalog.Register(registry);
            ContentLifecycleLuaCatalog.Register(registry, ExternalContentPack.Current);
            AbilitySourceLuaCatalog.Register(registry, ExternalContentPack.Current);
            BindGameplayPresenter(registry, ExternalContentPack.Current);
        }
        public static void Register(EffectRegistry registry, ActiveContentCatalog catalog)
        {
            BindGameplayPresenter(registry, catalog.Pack);
            PlayerEntranceLuaCatalog.Register(registry);
            EventCardLuaCatalog.Register(registry, catalog.Pack);
            CharacterCardLuaCatalog.Register(registry, catalog.Pack);
            FacilityLuaCatalog.Register(registry, catalog.Pack);
            CityStyleLuaCatalog.Register(registry, catalog.Pack);
            ContentLifecycleLuaCatalog.Register(registry, catalog.Pack);
            AbilitySourceLuaCatalog.Register(registry, catalog.Pack);
            EnterpriseEffectExecutor.Register(registry, CreateEnterpriseDefinitions(registry, catalog.Definitions, catalog.Pack.GetScript, catalog.Pack));
            YC.Domain.Rules.ExpansionSetupFlow.Register(registry);
        }
        private static void BindGameplayPresenter(EffectRegistry registry, ExternalContentPack pack)
        {
            registry.GameplayPresenter = (string path, YC.Domain.Presentation.GameplayPresentationContext context,
                out YC.Domain.Presentation.GameplayPresentationResult result, out string diagnostic) =>
            {
                bool succeeded = pack.TryPresent(path, context, out result, out var code, out diagnostic);
                if (!succeeded) diagnostic = code + ": " + diagnostic;
                return succeeded;
            };
        }
    }
}
