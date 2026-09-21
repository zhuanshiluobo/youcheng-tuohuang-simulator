using YC.Domain.Effects;

namespace YC.Infrastructure.Lua
{
    /// <summary>供组合根注入的基础内容注册入口；Application 无需反射或引用本程序集。</summary>
    public static class LuaContentCatalog
    {
        public static void Register(EffectRegistry registry)
        {
            PlayerEntranceLuaCatalog.Register(registry);
            EventCardLuaCatalog.Register(registry);
            CharacterCardLuaCatalog.Register(registry);
            FacilityLuaCatalog.Register(registry);
            CityStyleLuaCatalog.Register(registry);
        }
    }
}
