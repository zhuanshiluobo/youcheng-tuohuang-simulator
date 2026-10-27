using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Domain.Harvest
{
    public static class CollectionPlanService
    {
        public const int ProtocolVersion = 2;
        public const string Version = "1.0.0";
        public static CollectionQueryContext Context(GameState state, RouteSelectionSession session, IReadOnlyList<string> legal)
            => new CollectionQueryContext(session.PlayerId, session.Purpose == RouteSelectionPurpose.ExtraHarvest ? "extra" : "normal",
                session.OwnerEffectId, session.SessionId, session.StartLocationId, state.ContentPackHash, legal);

        public static CollectionPartition Partition(IReadOnlyList<string> selected, CollectionFreeLocationResult facts)
        {
            if (selected == null || facts == null || selected.Distinct(StringComparer.Ordinal).Count() != selected.Count ||
                selected.Any(x => !facts.LegalLocationIds.Contains(x)))
                throw new ArgumentException("collection_plan_selection_invalid");
            var free = new HashSet<string>(facts.FreeLocationIds, StringComparer.Ordinal);
            return new CollectionPartition(selected.Where(free.Contains).ToArray(), selected.Where(x => !free.Contains(x)).ToArray(), facts);
        }

        public static ValidationResult ValidateFacts(RouteSelectionSession session, CollectionFreeLocationResult facts)
        {
            if (session == null || facts == null || session.SchemaVersion != ProtocolVersion ||
                session.Purpose == RouteSelectionPurpose.Exploration) return Invalid("collection_plan_protocol_invalid");
            foreach (var proof in facts.Evidence.Where(e => e.Kind == "city"))
                if (proof.LocationId != session.StartLocationId || proof.SourceRef != null || proof.VersionRef != null)
                    return Invalid("collection_plan_city_evidence_invalid");
            foreach (var entry in facts.QuerySources)
            {
                var source=entry.SourceRef;var version=entry.VersionRef;
                if (source.AuthorizedPlayerId != session.PlayerId || string.IsNullOrEmpty(source.SourceKey) ||
                    string.IsNullOrEmpty(source.ItemId) || string.IsNullOrEmpty(source.AbilityId) ||
                    string.IsNullOrEmpty(source.AuthorizationRef) || source.GrantingSourceRefs == null ||
                    source.SourceKey != StableIdFactory.Create("ability-source",source.ItemId,source.AbilityId,source.ActiveFaceId,
                        source.DeclarationVersion,source.AuthorizedPlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
                    string.IsNullOrEmpty(source.EffectiveZoneId) || version.DefinitionVersion != source.DeclarationVersion ||
                    string.IsNullOrEmpty(version.ContentPackHash) || version.ContentPackHash.Length != 64 ||
                    string.IsNullOrEmpty(version.HandlerHash) || version.HandlerHash.Length != 64 ||
                    version.ContractVersion != "QY-COMMON/1.0.1" || version.ExecutorVersion != CollectionFreeLocationQuery.Version)
                    return Invalid("collection_plan_source_evidence_invalid");
            }
            return ValidationResult.Success;
        }

        public static ValidationResult ValidateSaved(RouteSelectionSession session, RouteSelectionPreview preview)
        {
            if (preview?.Collection == null) return Invalid("collection_plan_protocol_invalid");
            var part=preview.Collection;var valid=ValidateFacts(session,part.FreeLocations);
            if (!valid.IsValid) return valid;
            if (part.SelectedFreeLocationIds.Intersect(part.RouteRequiredLocationIds,StringComparer.Ordinal).Any() ||
                !new HashSet<string>(part.SelectedFreeLocationIds.Concat(part.RouteRequiredLocationIds),StringComparer.Ordinal).SetEquals(preview.SelectedLocationIds) ||
                preview.SelectedLocationIds.Distinct(StringComparer.Ordinal).Count()!=preview.SelectedLocationIds.Count ||
                preview.SelectedLocationIds.Any(x=>!part.FreeLocations.LegalLocationIds.Contains(x)||!session.SelectableLocationUpperBound.Contains(x)) ||
                !part.SelectedFreeLocationIds.SequenceEqual(preview.SelectedLocationIds.Where(x=>part.FreeLocations.FreeLocationIds.Contains(x))) ||
                !part.RouteRequiredLocationIds.SequenceEqual(preview.SelectedLocationIds.Where(x=>!part.FreeLocations.FreeLocationIds.Contains(x))))
                return Invalid("collection_plan_partition_invalid");
            return ValidationResult.Success;
        }

        public static ValidationResult ValidateCurrent(GameState state,IMapQueryService map,RouteSelectionSession session,
            IReadOnlyList<string> legal,CollectionFreeLocationResult facts)
        {
            try
            {
                var valid=ValidateFacts(session,facts);
                if (!valid.IsValid) return valid;
                return CollectionFreeLocationQuery.MatchesCurrentFacts(state,Context(state,session,legal),map,facts)
                    ? ValidationResult.Success : Invalid("collection_free_facts_changed");
            }
            catch (InvalidOperationException) { return Invalid("collection_free_facts_changed"); }
        }
        private static ValidationResult Invalid(string code)=>ValidationResult.Failure(CommandErrorCode.InvalidTarget,code);
    }
}
