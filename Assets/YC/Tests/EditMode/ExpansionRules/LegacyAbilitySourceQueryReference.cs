using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
namespace YC.Tests.EditMode.ExpansionRules
{
    // 固定修改前QuerySources主体；ResolveAuthorization本次未改，保留同一授权规则实现。
    internal static class LegacyAbilitySourceQueryReference
    {
        public static IReadOnlyList<AbilitySourceRef> QuerySources(GameState state, IEnumerable<AbilitySourceDeclaration> declarations,
            int playerId = -1, string kind = "", string eventType = "", string queryKind = "", bool includeCharacterActivation = false)
        {
            using var diagnostic = GameStateCloneService.MeasureDiagnostic("source.query");
            var result = new List<AbilitySourceRef>();
            if (state?.ItemZones == null || declarations == null) return result;
            foreach (var declaration in declarations.OrderBy(d => d.AbilityId, StringComparer.Ordinal))
            {
                foreach (var item in state.ItemZones.Items.Where(i => i.DefinitionId == declaration.DefinitionId).OrderBy(i => i.ItemId, StringComparer.Ordinal))
                {
                    var zone = state.ItemZones.Zones.Find(z => z.ZoneId == item.ZoneId);
                    if (zone == null || !declaration.EffectiveZones.Contains(zone.TemplateId)) continue;
                    if (item.TypeId == "entrepreneur.ability_card" || item.TypeId == "entrepreneur.round_card")
                    {
                        if (zone.TemplateId != "core.zone.entrepreneur_active" || item.TypeId == "entrepreneur.round_card" &&
                            !AbilitySourceSelection.HasSelectedFaceReceipt(state, item, Text(item.LocalState, "activeFaceId"), item.ControllerSubject.PlayerId)) continue;
                    }
                    string face = Text(item.LocalState, "activeFaceId");
                    if (declaration.ActiveFaceId.Length > 0 && face != declaration.ActiveFaceId) continue;
                    foreach (var player in state.Players.Where(p => playerId < 0 || p.PlayerId == playerId).OrderBy(p => p.PlayerId))
                    {
                        string authorization = ResolveAuthorization(state, declaration, item, zone, player.PlayerId,includeCharacterActivation);
                        if (authorization.Length == 0) continue;
                        var source = new AbilitySourceRef { ItemId = item.ItemId, DefinitionId = item.DefinitionId, AbilityId = declaration.AbilityId,
                            ActiveFaceId = declaration.ActiveFaceId.Length > 0 ? face : "", DeclarationVersion = declaration.DeclarationVersion,
                            AuthorizedPlayerId = player.PlayerId, AuthorizationRef = authorization, EffectiveZoneId = zone.ZoneId, RuleOwnerItemId = item.ItemId };
                        source.SourceKey = StableIdFactory.Create("ability-source", source.ItemId, source.AbilityId, source.ActiveFaceId,
                            source.DeclarationVersion, player.PlayerId.ToString(CultureInfo.InvariantCulture));
                        var grants = Value(item.LocalState, "grantingSourceRefs");
                        if (grants?.Kind == NormalizedValueKind.Array) source.GrantingSourceRefs = grants.Items.Select(v => v.StringValue).OrderBy(v => v, StringComparer.Ordinal).ToList();
                        result.Add(source);
                    }
                }
            }
            foreach (var source in result) source.GrantingSourceRefs = source.GrantingSourceRefs.Where(key => result.Any(s => s.SourceKey == key && s.AuthorizedPlayerId == source.AuthorizedPlayerId)).ToList();
            var byAbility = declarations.ToDictionary(d => d.AbilityId, StringComparer.Ordinal);
            return result.Where(source => (kind.Length == 0 || byAbility[source.AbilityId].Kind == kind) &&
                (eventType.Length == 0 || byAbility[source.AbilityId].ObservedEvents.Contains(eventType)) && (queryKind.Length == 0 || byAbility[source.AbilityId].QueryKinds.Contains(queryKind))).ToList();
        }
        private static string ResolveAuthorization(GameState s, AbilitySourceDeclaration d, ItemInstanceState i, ZoneInstanceState z, int p, bool activation)
        {
            try { return (string)typeof(AbilitySourceService).GetMethod("ResolveAuthorization",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{s,d,i,z,p,activation}); }
            catch(TargetInvocationException e) when(e.InnerException!=null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
    }
}
