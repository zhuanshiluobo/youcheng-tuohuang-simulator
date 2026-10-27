using System;
using System.Linq;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    /// <summary>供冻结宿主接Global.Scoring；未进入终局返回不可用标记，不计算或持久化任何分数。</summary>
    public static class FinalScoringLuaProjection
    {
        public static LuaSnapshotValue Build(GameState state, EffectNodeRuntimeState owner, int authorizedPlayerId)
        {
            var session = state?.FinalScoring?.Session;
            if (session == null) return LuaSnapshotValue.Null;
            if (owner == null || state.FindPlayer(authorizedPlayerId) == null || !FinalScoringSessionService.Within(state, owner.EffectId, session.OwnerEffectId) || session.Control == null) throw new InvalidOperationException("final_scoring_query_context_invalid");
            var control = session.Control;
            var influences = control.InfluenceFacts.Select(i => O(E("itemId", S(i.ItemId)), E("typeId", S(i.TypeId)), E("regionId", S(i.RegionId)), E("position", S(i.Position)), E("subjectKind", S(i.SubjectKind)), E("playerId", N(i.PlayerId)), E("companyId", S(i.CompanyId))));
            var sources = control.Sources.Select(s => O(E("sourceKey", S(s.Source.SourceKey)), E("itemId", S(s.Source.ItemId)), E("abilityId", S(s.Source.AbilityId)), E("playerId", N(s.Source.AuthorizedPlayerId)), E("declarationVersion", S(s.Version.DefinitionVersion)), E("handlerHash", S(s.Version.HandlerHash))));
            return O(E("sessionId", S(session.SessionId)), E("snapshotId", S(control.SnapshotId)), E("basisDigest", S(control.BasisDigest)), E("mapId", S(control.MapId)), E("contentHash", S(control.ContentHash)),
                E("protocolVersion", N(control.ProtocolVersion)), E("executionVersion", S(session.ExecutionVersion)), E("rulesDigest", S(control.RulesDigest)), E("sources", A(sources)), E("regions", A(control.Regions.Select(Region))), E("physicalInfluences", A(influences)));
        }
        private static LuaSnapshotValue Region(FinalRegionControlSnapshot r)
        {
            var claims = r.ControlClaims.Select(c => O(E("playerId", N(c.PlayerId)), E("sourceKey", S(c.SourceKey)), E("ruleId", S(c.RuleId))));
            return O(E("regionId", S(r.RegionId)), E("baseValue", N(r.BaseScoreValue)), E("finalValue", N(r.ScoreValue)), E("controllerPlayerId", N(r.ControllerPlayerId ?? -1)), E("controlReason", S(r.ControlReason)),
                E("physicalCounts", Counts(r.PhysicalCounts)), E("cityCounts", Counts(r.CityCounts)), E("effectiveCounts", Counts(r.InfluenceCounts)), E("isTie", LuaSnapshotValue.Boolean(r.IsTie)), E("positiveHighest", A(r.PositiveHighestPlayerIds.Select(N))),
                E("controlClaims", A(claims)), E("influenceModifiers", Modifiers(r.InfluenceModifiers)), E("valueModifiers", Modifiers(r.ValueModifiers)));
        }
        private static LuaSnapshotValue Modifiers(System.Collections.Generic.IEnumerable<ScoringModifierRecord> records) => A(records.Select(r => O(E("kind", S(r.Kind)), E("playerId", N(r.PlayerId)), E("delta", N(r.Delta)), E("sourceKey", S(r.Source.SourceKey)), E("abilityId", S(r.Source.AbilityId)), E("handlerHash", S(r.Version.HandlerHash)))));
        private static LuaSnapshotValue Counts(System.Collections.Generic.Dictionary<int, int> values) => A(values.OrderBy(p => p.Key).Select(p => O(E("playerId", N(p.Key)), E("count", N(p.Value)))));
        private static LuaSnapshotEntry E(string name, LuaSnapshotValue value) => new LuaSnapshotEntry(name, value);
        private static LuaSnapshotValue O(params LuaSnapshotEntry[] entries) => LuaSnapshotValue.Object(entries);
        private static LuaSnapshotValue A(System.Collections.Generic.IEnumerable<LuaSnapshotValue> values) => LuaSnapshotValue.Array(values.ToList());
        private static LuaSnapshotValue S(string value) => LuaSnapshotValue.String(value);
        private static LuaSnapshotValue N(int value) => LuaSnapshotValue.Integer(value);
    }
}
