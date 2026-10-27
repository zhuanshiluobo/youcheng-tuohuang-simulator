using System;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Movement;

namespace YC.Infrastructure.Lua
{
    public static class CityDestinationLuaCatalog
    {
        public const string Version = "1.0.0";
        public static void Register(EffectRegistry registry, ExternalContentPack pack)
        {
            if (registry == null || pack == null) throw new ArgumentNullException();
            registry.EnsureCollectionQueryContract();
            foreach (var entry in pack.ActiveDefinitions.SelectMany(d => d.AbilitySources
                .Where(s => s.Kind == "ruleQuery" && s.QueryKinds.Contains(YC.Domain.Harvest.CollectionFreeLocationQuery.QueryKind))
                .Select(s => new { Definition = d, Source = s })))
            {
                var h = entry.Definition.AbilityHandlers.Single(x => x.HandlerId == entry.Source.HandlerRef);
                registry.RegisterCollectionQueryHandlerVersion(h.HandlerId, LuaContentHasher.ComputeSha256(pack.GetScript(h.Script)));
            }
            var dispatcher = new LuaEffectEventDispatcher(registry);
            foreach (var group in pack.ActiveDefinitions.SelectMany(d => d.AbilitySources
                .Where(s => s.Kind == "ruleQuery" && s.QueryKinds.Contains(CityDestinationQueryService.QueryKind))
                .Select(s => new { Definition = d, Source = s })).GroupBy(x => x.Source.HandlerRef, StringComparer.Ordinal))
            {
                var entry = group.First();
                var handler = entry.Definition.AbilityHandlers.Single(h => h.HandlerId == entry.Source.HandlerRef);
                string script = pack.GetScript(handler.Script);
                var definition = new LuaHandlerDefinition(handler.HandlerId, "CandidateSetBuilding", CityDestinationQueryService.CandidateKind,
                    new LuaScriptDefinition(entry.Definition.DefinitionId, entry.Source.AbilityId, handler.HandlerId,
                        handler.Version, script, LuaContentHasher.ComputeSha256(script)), responseKind: "candidatePatches");
                registry.RegisterCityDestinationPolicyProvider(handler.HandlerId, definition.Script.ContentHash,
                    (state, draft, source) => dispatcher.InvokeAbilityCandidatePatches(definition, state, draft, source));
            }
        }
    }
}
