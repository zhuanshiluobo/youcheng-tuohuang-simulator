using System;
using System.Collections.Generic;
using System.Reflection;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;

namespace YC.Tests.EditMode
{
    // 关闭 Domain Reload 时，场景必须加载生产目录；离开后恢复纯 EditMode 的兼容目录。
    internal sealed class SceneRuntimeCatalogScope : IDisposable
    {
        private readonly List<KeyValuePair<FieldInfo, object>> fields = new List<KeyValuePair<FieldInfo, object>>();
        private readonly Dictionary<string, FacilityCardDefinition> facilities;
        private readonly Dictionary<string, FacilityCardDefinition> previousFacilities;
        public SceneRuntimeCatalogScope()
        {
            var types = new[] { typeof(FacilityCardDatabase), typeof(CharacterCardDatabase), typeof(EventCardDatabase), typeof(CityStyleDatabase), typeof(SpecialActionDatabase) };
            foreach (var type in types)
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                    if (!field.IsInitOnly && !field.IsLiteral) fields.Add(new KeyValuePair<FieldInfo, object>(field, field.GetValue(null)));
            facilities = (Dictionary<string, FacilityCardDefinition>)typeof(FacilityCardDatabase)
                .GetField("Definitions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            previousFacilities = new Dictionary<string, FacilityCardDefinition>(facilities);
            facilities.Clear();
            typeof(FacilityCardDatabase).GetField("injectedDefinitions", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
            foreach (var type in types)
                if (type != typeof(FacilityCardDatabase)) type.GetMethod("ResetForTests", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }
        public void Dispose()
        {
            facilities.Clear();
            foreach (var entry in previousFacilities) facilities.Add(entry.Key, entry.Value);
            foreach (var entry in fields) entry.Key.SetValue(null, entry.Value);
        }
    }
}
