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

        public static void Register(EffectRegistry registry) => Register(registry, ExternalContentPack.Current);

        public static void Register(EffectRegistry registry, ExternalContentPack pack)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            foreach (var content in pack.ActiveDefinitions)
            {
                if (content.ContentType != "city_style" || !(content.Data["specialAction"] is Newtonsoft.Json.Linq.JObject)) continue;
                string actionId = (string)pack.CreateCityStyles().First(d => d.CityStyleId == (pack.UseDefinitionIds ? content.DefinitionId : content.RuntimeId)).SpecialActionId;
                string subscription = (string)content.Data["luaSubscriptionId"] ?? "city-styles." + content.RuntimeId;
                string ability = (string)content.Data["luaAbilityId"] ?? "city_styles." + content.RuntimeId;
                string completion = (string)content.Data["luaCompletionHandlerId"] ?? "";
                string version = (string)content.Data["luaVersion"] ?? content.Version;
                RegisterAction(registry, subscription, actionId, ability, pack.GetContentScript("city_style", content.RuntimeId), completion, version, pack.GetScriptEntryId((string)content.Data["effectScript"]));
            }
        }

        private static void RegisterAction(EffectRegistry registry, string subscriptionId, string routeKey, string abilityId, string source, string continuation, string version, string entryId)
        {
            if (registry.HasEventHandler(subscriptionId)) return;
            var dispatcher = new LuaEffectEventDispatcher(registry);
            if (continuation.Length > 0)
                dispatcher.RegisterContinuation(new LuaHandlerDefinition(continuation, "EffectCompleted", "",
                    Definition("content." + subscriptionId, abilityId, continuation, source, version, entryId),
                    EffectHandlerRole.Continuation, 0, ContentInstanceId, abilityId));
            dispatcher.Register(new LuaHandlerDefinition(
                subscriptionId,
                CityStyleSpecialActionEventTypeIds.Activated,
                routeKey,
                Definition("content." + subscriptionId, abilityId, subscriptionId, source, version, entryId),
                EffectHandlerRole.Primary,
                0,
                ContentInstanceId,
                abilityId, continuation));
        }

        private static LuaScriptDefinition Definition(string contentId, string abilityId, string handlerId, string source, string version, string entryId)
        {
            return new LuaScriptDefinition(
                contentId,
                abilityId,
                handlerId,
                version,
                source,
                LuaContentHasher.ComputeSha256(source), entryId);
        }
    }
}
