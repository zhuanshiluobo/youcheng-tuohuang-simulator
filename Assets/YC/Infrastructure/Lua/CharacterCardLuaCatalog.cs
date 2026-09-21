using System;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;

namespace YC.Infrastructure.Lua
{
    /// <summary>只注册外部目录；角色效果正文与贴图不再内嵌于 C#。</summary>
    public static class CharacterCardLuaCatalog
    {
        public const string ResourceContentVersion = "1.3.0";
        public const string ConditionContentVersion = "1.3.0";
        public const string SubscriptionId = "characters.elysium.strategy";
        public const string ContentInstanceId = "characters.core";
        public const string ContentId = "content.characters.elysium.strategy";
        public const string SourcePath = "Assets/StreamingAssets/Content/core/lua/characters/elysium.strategy.lua";
        public static string ScriptSource => ExternalContentPack.Current.GetScript("lua/characters/elysium.strategy.lua");
        public static LuaScriptDefinition CreateElysiumStrategyDefinition() => new LuaScriptDefinition(ContentId,
            CharacterAbilityCatalog.ElysiumStrategyAbilityId, SubscriptionId, ResourceContentVersion, ScriptSource, LuaContentHasher.ComputeSha256(ScriptSource));

        public static void Register(EffectRegistry registry) => Register(registry, ExternalContentPack.Current);
        public static void Register(EffectRegistry registry, ExternalContentPack pack)
        {
            if (registry == null || pack == null) throw new ArgumentNullException();
            if (!string.IsNullOrEmpty(registry.ContentPackHash) && registry.ContentPackHash != pack.ContentHash)
                throw new InvalidOperationException("同一 Registry 不能混用不同内容包快照。");
            registry.ContentPackHash = pack.ContentHash;
            var dispatcher = new LuaEffectEventDispatcher(registry);
            foreach (var card in pack.ActiveDefinitions.Where(d => d.ContentType == "character"))
            foreach (var ability in card.Abilities)
            {
                registry.CharacterActivationSubscriptions[ability.AbilityId] = ability.SubscriptionId;
                if (registry.HasEventHandler(ability.SubscriptionId)) continue;
                string source = pack.GetScript(ability.Script);
                string hash = LuaContentHasher.ComputeSha256(source);
                var script = new LuaScriptDefinition("content." + ability.SubscriptionId, ability.AbilityId,
                    ability.SubscriptionId, ability.Version, source, hash);
                if (!string.IsNullOrEmpty(ability.CompletionHandlerId))
                    dispatcher.RegisterContinuation(new LuaHandlerDefinition(ability.CompletionHandlerId, "EffectCompleted", "",
                        new LuaScriptDefinition(script.ContentId, ability.AbilityId, ability.CompletionHandlerId, ability.Version, source, hash),
                        EffectHandlerRole.Continuation, 0, ContentInstanceId, ability.AbilityId));
                dispatcher.Register(new LuaHandlerDefinition(ability.SubscriptionId,
                    CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType, ability.AbilityId,
                    script, EffectHandlerRole.Primary, 0, ContentInstanceId, ability.AbilityId, ability.CompletionHandlerId));
                if (!string.IsNullOrEmpty(ability.CleanupScript))
                {
                    string cleanupSource = pack.GetScript(ability.CleanupScript);
                    string cleanupId = ability.SubscriptionId + ".cleanup";
                    var cleanupScript = new LuaScriptDefinition("content." + cleanupId, ability.AbilityId, cleanupId,
                        ability.Version, cleanupSource, LuaContentHasher.ComputeSha256(cleanupSource));
                    var handler = new LuaHandlerDefinition(cleanupId, "PlayerCleanupStarted", "*", cleanupScript,
                        EffectHandlerRole.Observer, 0, ContentInstanceId, ability.AbilityId);
                    TimingHandlerRegistry.RegisterPlayerCleanupAfterResolveOnce(registry,
                        CharacterAbilityEffectExecutor.ActivationEffectTypeId, ability.AbilityId, cleanupScript.DefinitionVersion,
                        cleanupScript.ContentHash, context => dispatcher.Invoke(handler, context));
                }
            }
        }
    }
}
