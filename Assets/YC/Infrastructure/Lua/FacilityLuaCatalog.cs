using System;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;

namespace YC.Infrastructure.Lua
{
    /// <summary>
    /// 设施入口内容目录。每个行为族只注册一个版本化 Lua handler，Event 的 routeKey
    /// 使用 effectId，TargetEntityId 使用实际建设实例的 contentInstanceId。
    /// Lua 只返回通用 EffectSpec，不读取或修改 GameState。
    /// </summary>
    public static class FacilityLuaCatalog
    {
        public const string DefinitionVersion = FacilityContentRegistration.DefinitionVersion;
        public const string CleanupSubscriptionId = "lua:facility:cleanup";

        public static void Register(EffectRegistry registry) => Register(registry, ExternalContentPack.Current);

        public static void Register(EffectRegistry registry, ExternalContentPack pack)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            foreach (FacilityContentRegistration registration in pack.CreateFacilities().Select(FacilityContentRegistrationCatalog.Create))
            {
                if (!registration.HasEntryEffect || registry.HasEventHandler(registration.SubscriptionId)) continue;
                string source = pack.GetFacilityScript(registration.EffectId);
                var dispatcher = new LuaEffectEventDispatcher(registry);
                var content = pack.ActiveDefinitions.First(d => d.ContentType == "facility" && (string)d.Data["effectId"] == registration.EffectId);
                string continuation = (string)content.Data["completionHandlerId"] ?? "";
                if (continuation.Length > 0)
                    dispatcher.RegisterContinuation(new LuaHandlerDefinition(continuation, "EffectCompleted", "",
                        Definition(registration, source, continuation), EffectHandlerRole.Continuation, 0,
                        "facility-definition:" + registration.EffectId, "facility.entry." + registration.EffectId));
                dispatcher.Register(new LuaHandlerDefinition(
                    registration.SubscriptionId,
                    registration.EventType,
                    registration.RouteKey,
                    Definition(registration, source),
                    EffectHandlerRole.Primary,
                    0,
                    "facility-definition:" + registration.EffectId,
                    "facility.entry." + registration.EffectId, continuation));
            }
        }

        private static LuaScriptDefinition Definition(FacilityContentRegistration registration, string source, string handlerId = null)
        {
            return new LuaScriptDefinition(
                "content.facility." + registration.EffectId,
                "facility.entry." + registration.EffectId,
                handlerId ?? registration.HandlerId,
                DefinitionVersion,
                source,
                LuaContentHasher.ComputeSha256(source));
        }

    }
}
