using System;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.SpecialActions;

namespace YC.Infrastructure.Lua
{
    /// <summary>
    /// 城市样式内容目录。稳定 ID 只用于 Event 路由和内容登记；脚本本身只选择行为族，
    /// 不直接修改 GameState，也不携带旧的 PendingSpecialAction 会话。
    /// </summary>
    public static class CityStyleLuaCatalog
    {
        public const string DefinitionVersion = "1.0.0";
        public const string ContentInstanceId = "city-styles.core";
        public const string CleanupSubscriptionId = "city-styles.player-cleanup";

        private const string CleanupSource = @"
return function(ctx)
    return { Effect.OperatePlayerMarker({ operation = ""round_cleanup"", executingPlayer = ctx.playerId }) }
end";

        public static void Register(EffectRegistry registry) => Register(registry, ExternalContentPack.Current);

        public static void Register(EffectRegistry registry, ExternalContentPack pack)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (!registry.HasEventHandler(CleanupSubscriptionId))
            {
                new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition(
                    CleanupSubscriptionId,
                    CityStyleSpecialActionEventTypeIds.CleanupStarted,
                    "*",
                    Definition("content.city-styles.cleanup", "city_styles.cleanup", CleanupSubscriptionId, CleanupSource),
                    EffectHandlerRole.Observer,
                    0,
                    ContentInstanceId,
                    "city_styles.cleanup"));
            }

            foreach (var content in pack.ActiveDefinitions)
            {
                if (content.ContentType != "city_style" || !(content.Data["specialAction"] is Newtonsoft.Json.Linq.JObject)) continue;
                string actionId = (string)pack.CreateCityStyles().First(d => d.CityStyleId == content.RuntimeId).SpecialActionId;
                string subscription = (string)content.Data["luaSubscriptionId"] ?? "city-styles." + content.RuntimeId;
                string ability = (string)content.Data["luaAbilityId"] ?? "city_styles." + content.RuntimeId;
                string completion = (string)content.Data["luaCompletionHandlerId"] ?? "";
                string version = (string)content.Data["luaVersion"] ?? content.Version;
                RegisterAction(registry, subscription, actionId, ability, pack.GetContentScript("city_style", content.RuntimeId), completion, version);
            }
        }

        private static void RegisterAction(EffectRegistry registry, string subscriptionId, string routeKey, string abilityId, string source, string continuation, string version)
        {
            if (registry.HasEventHandler(subscriptionId)) return;
            var dispatcher = new LuaEffectEventDispatcher(registry);
            if (continuation.Length > 0)
                dispatcher.RegisterContinuation(new LuaHandlerDefinition(continuation, "EffectCompleted", "",
                    Definition("content." + subscriptionId, abilityId, continuation, source, version),
                    EffectHandlerRole.Continuation, 0, ContentInstanceId, abilityId));
            dispatcher.Register(new LuaHandlerDefinition(
                subscriptionId,
                CityStyleSpecialActionEventTypeIds.Activated,
                routeKey,
                Definition("content." + subscriptionId, abilityId, subscriptionId, source, version),
                EffectHandlerRole.Primary,
                0,
                ContentInstanceId,
                abilityId, continuation));
        }

        private static LuaScriptDefinition Definition(string contentId, string abilityId, string handlerId, string source, string version = DefinitionVersion)
        {
            return new LuaScriptDefinition(
                contentId,
                abilityId,
                handlerId,
                version,
                source,
                LuaContentHasher.ComputeSha256(source));
        }
    }
}
