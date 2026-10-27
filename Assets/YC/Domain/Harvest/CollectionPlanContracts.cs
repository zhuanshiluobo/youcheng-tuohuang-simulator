using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Domain.Harvest
{
    public sealed class CollectionQueryContext
    {
        public int PlayerId { get; }
        public string Purpose { get; }
        public string SourceEffectId { get; }
        public string SelectionSessionId { get; }
        public string CityStartLocationId { get; }
        public string ContentVersion { get; }
        public IReadOnlyList<string> LegalLocationIds { get; }
        public CollectionQueryContext(int playerId, string purpose, string sourceEffectId,
            string selectionSessionId, string cityStartLocationId, string contentVersion, IReadOnlyList<string> legalLocationIds)
        {
            PlayerId = playerId; Purpose = purpose ?? ""; SourceEffectId = sourceEffectId ?? "";
            SelectionSessionId = selectionSessionId ?? ""; CityStartLocationId = cityStartLocationId ?? "";
            ContentVersion = contentVersion ?? "";
            LegalLocationIds = Array.AsReadOnly((legalLocationIds ?? throw new ArgumentNullException(nameof(legalLocationIds))).ToArray());
        }
    }

    // 对外返回副本；保存的是来源调用及其事实依据，不在C#重演卡面条件。
    public sealed class FreeLocationEvidence
    {
        public string LocationId { get; }
        public string Kind { get; }
        public string BasisDigest { get; }
        private readonly AbilitySourceRef source;
        private readonly AbilityVersionRef version;
        public AbilitySourceRef SourceRef => source == null ? null : GameStateCloneService.DeepClone(source);
        public AbilityVersionRef VersionRef => version == null ? null : GameStateCloneService.DeepClone(version);
        public FreeLocationEvidence(string locationId, string kind, string basisDigest,
            AbilitySourceRef sourceRef = null, AbilityVersionRef versionRef = null)
        {
            LocationId = locationId ?? ""; Kind = kind ?? ""; BasisDigest = basisDigest ?? "";
            source = sourceRef == null ? null : GameStateCloneService.DeepClone(sourceRef);
            version = versionRef == null ? null : GameStateCloneService.DeepClone(versionRef);
        }
        public string Identity => LocationId + "\n" + Kind + "\n" + (source?.SourceKey ?? "");
    }

    public sealed class CollectionQuerySource
    {
        private readonly AbilitySourceRef source;
        private readonly AbilityVersionRef version;
        public AbilitySourceRef SourceRef => GameStateCloneService.DeepClone(source);
        public AbilityVersionRef VersionRef => GameStateCloneService.DeepClone(version);
        public CollectionQuerySource(AbilitySourceRef sourceRef, AbilityVersionRef versionRef)
        {
            source = GameStateCloneService.DeepClone(sourceRef ?? throw new ArgumentNullException(nameof(sourceRef)));
            version = GameStateCloneService.DeepClone(versionRef ?? throw new ArgumentNullException(nameof(versionRef)));
        }
    }

    public sealed class CollectionFreeLocationResult
    {
        public IReadOnlyList<string> LegalLocationIds { get; }
        public IReadOnlyList<string> FreeLocationIds { get; }
        public IReadOnlyList<FreeLocationEvidence> Evidence { get; }
        public IReadOnlyList<CollectionQuerySource> QuerySources { get; }
        public string BasisDigest { get; }
        public string ResultDigest { get; }
        internal CollectionFreeLocationResult(IReadOnlyList<string> legal, IReadOnlyList<string> free,
            IReadOnlyList<FreeLocationEvidence> evidence, string basisDigest, string resultDigest, IReadOnlyList<CollectionQuerySource> querySources)
        {
            LegalLocationIds = Array.AsReadOnly(legal.ToArray());
            FreeLocationIds = Array.AsReadOnly(free.ToArray());
            Evidence = Array.AsReadOnly(evidence.ToArray());
            QuerySources = Array.AsReadOnly(querySources.ToArray());
            BasisDigest = basisDigest; ResultDigest = resultDigest;
        }
    }

    public sealed class CollectionPartition
    {
        public IReadOnlyList<string> SelectedFreeLocationIds { get; }
        public IReadOnlyList<string> RouteRequiredLocationIds { get; }
        public CollectionFreeLocationResult FreeLocations { get; }
        internal CollectionPartition(IReadOnlyList<string> free, IReadOnlyList<string> route, CollectionFreeLocationResult facts)
        {
            SelectedFreeLocationIds = Array.AsReadOnly(free.ToArray());
            RouteRequiredLocationIds = Array.AsReadOnly(route.ToArray()); FreeLocations = facts;
        }
    }
}
