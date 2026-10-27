using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed partial class LuaEffectEventDispatcher
    {
        public IReadOnlyList<CandidatePatch> InvokeAbilityCandidatePatches(LuaHandlerDefinition handler,
            GameState state, CandidateSetDraft draft, AbilitySourceRef source)
        {
            if (handler == null || state?.EffectRuntime == null || draft == null || source == null ||
                !registry.QuerySources(state, new AbilityQueryContext { QueryId = draft.CandidateSetId,
                    QueryKind = YC.Domain.Movement.CityDestinationQueryService.QueryKind, FactsDigest = "source-validation",
                    MapId = state.MapId, ContentVersion = state.ContentPackHash }).Any(x => x.SourceKey == source.SourceKey &&
                        x.AuthorizationRef == source.AuthorizationRef))
                throw new InvalidOperationException("city_destination_source_inactive");
            var bound = new LuaHandlerDefinition(handler.SubscriptionId, "CandidateSetBuilding", draft.CandidateKind,
                new LuaScriptDefinition(handler.Script.ContentId, source.AbilityId, handler.Script.HandlerId,
                    handler.Script.DefinitionVersion, handler.Script.Source, handler.Script.ContentHash),
                handler.Role, handler.Priority, source.ItemId, source.AbilityId, responseKind: "candidatePatches");
            var ruleEvent = new RuleEvent { EventId = StableIdFactory.Create("city-candidate-query", draft.CandidateSetId, source.SourceKey),
                EventType = "CandidateSetBuilding", PlayerId = source.AuthorizedPlayerId, SourceEffectId = draft.SourceEffectId,
                OwnerNodeId = source.SourceKey, RouteKey = draft.CandidateKind, Payload = NormalizedValue.CreateObject(new List<NormalizedValueEntry>()) };
            var owner = new EffectNodeRuntimeState { EffectId = source.SourceKey, PlayerId = source.AuthorizedPlayerId };
            var invocation = CreateInvocationContext(bound, new EffectEventHandlerContext(state, ruleEvent, owner, null));
            var payload = CreateCandidatePayload(draft).Properties.ToList();
            payload.Add(new LuaSnapshotEntry("sourceItemId", LuaSnapshotValue.String(source.ItemId)));
            payload.Add(new LuaSnapshotEntry("authorizedPlayerId", LuaSnapshotValue.Integer(source.AuthorizedPlayerId)));
            payload.Add(new LuaSnapshotEntry("sourceKey", LuaSnapshotValue.String(source.SourceKey)));
            invocation.FacadeSnapshot.EventPayload = LuaSnapshotValue.Object(payload);
            var returned = host.Invoke(bound.Script, invocation);
            if (!returned.IsSuccess) throw new InvalidOperationException("city_destination_lua_invalid:" + returned.Diagnostic);
            return returned.CandidatePatches.Select(p => {
                if (!Enum.TryParse(p.Operation, true, out CandidatePatchOperation operation))
                    throw new InvalidOperationException("city_destination_patch_invalid");
                return new CandidatePatch { Operation = operation, CandidateIds = new List<string>(p.Ids), ReasonCode = p.ReasonCode };
            }).ToArray();
        }
    }
}
