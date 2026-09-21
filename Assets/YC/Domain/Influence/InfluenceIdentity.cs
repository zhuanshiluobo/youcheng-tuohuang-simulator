using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.State;

namespace YC.Domain.Influence
{
    /// <summary>
    /// 影响力实例的唯一身份和来源归一化入口。旧快照没有 influenceId 时在首次经过规则入口时补齐，
    /// 不把槽位 ID 当作实例 ID，因此移除后重新放置会得到新的实例身份。
    /// </summary>
    public static class InfluenceIdentity
    {
        public const string PlayerSupplySourceKind = "player_supply";

        public static void Ensure(GameState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Map == null) state.Map = new MapRuntimeState();
            if (state.Map.Influences == null) state.Map.Influences = new List<InfluencePlacement>();

            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence == null) continue;

                if (string.IsNullOrEmpty(influence.InfluenceId) || used.Contains(influence.InfluenceId))
                {
                    var ordinal = 0;
                    var candidate = CreateLegacyId(state, influence, i, ordinal);
                    while (used.Contains(candidate))
                    {
                        ordinal += 1;
                        candidate = CreateLegacyId(state, influence, i, ordinal);
                    }

                    influence.InfluenceId = candidate;
                }

                used.Add(influence.InfluenceId);
                NormalizeOwnerAndSource(influence);
            }
        }

        public static string CreateNewId(GameState state, int playerId, string slotId, string sourceId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Map == null) state.Map = new MapRuntimeState();
            if (state.Map.Influences == null) state.Map.Influences = new List<InfluencePlacement>();

            var sequence = state.Map.NextInfluenceInstanceSequence;
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                if (state.Map.Influences[i] != null && !string.IsNullOrEmpty(state.Map.Influences[i].InfluenceId))
                {
                    used.Add(state.Map.Influences[i].InfluenceId);
                }
            }

            var id = CreateId(state, playerId, slotId, sourceId, sequence);
            while (used.Contains(id))
            {
                sequence += 1;
                id = CreateId(state, playerId, slotId, sourceId, sequence);
            }

            state.Map.NextInfluenceInstanceSequence = sequence + 1;
            return id;
        }

        public static string GetStableId(GameState state, InfluencePlacement influence)
        {
            if (state == null || state.Map == null || state.Map.Influences == null || influence == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(influence.InfluenceId)) return influence.InfluenceId;
            var index = state.Map.Influences.IndexOf(influence);
            return index < 0 ? string.Empty : CreateLegacyId(state, influence, index, 0);
        }

        public static RuleSubjectReference CreatePlayerOwner(int playerId)
        {
            return RuleSubjectReference.ForPlayer(playerId);
        }

        public static InfluenceSourceReference CreatePlayerSupplySource(int playerId, string sourceId = "")
        {
            return new InfluenceSourceReference
            {
                Kind = PlayerSupplySourceKind,
                SourceId = string.IsNullOrEmpty(sourceId) ? "player_supply" : sourceId,
                Subject = RuleSubjectReference.ForPlayer(playerId)
            };
        }

        public static InfluenceSourceReference CloneSource(InfluenceSourceReference source)
        {
            if (source == null) return new InfluenceSourceReference();
            return new InfluenceSourceReference
            {
                Kind = source.Kind ?? string.Empty,
                SourceId = source.SourceId ?? string.Empty,
                Subject = CloneSubject(source.Subject)
            };
        }

        public static RuleSubjectReference CloneSubject(RuleSubjectReference subject)
        {
            if (subject == null) return new RuleSubjectReference();
            return new RuleSubjectReference
            {
                SubjectType = subject.SubjectType ?? string.Empty,
                InstanceId = subject.InstanceId ?? string.Empty,
                DefinitionId = subject.DefinitionId ?? string.Empty,
                PlayerId = subject.PlayerId
            };
        }

        private static void NormalizeOwnerAndSource(InfluencePlacement influence)
        {
            if (influence.OwnerSubject == null || string.IsNullOrEmpty(influence.OwnerSubject.SubjectType))
            {
                influence.OwnerSubject = RuleSubjectReference.ForPlayer(influence.PlayerId);
            }

            if (influence.Source == null || string.IsNullOrEmpty(influence.Source.Kind))
            {
                influence.Source = CreatePlayerSupplySource(influence.PlayerId, "legacy");
            }
        }

        private static string CreateLegacyId(GameState state, InfluencePlacement influence, int index, int ordinal)
        {
            return StableIdFactory.Create(
                "influence",
                state.GameId ?? string.Empty,
                "legacy",
                index.ToString(CultureInfo.InvariantCulture),
                influence == null ? string.Empty : influence.PlayerId.ToString(CultureInfo.InvariantCulture),
                influence == null ? string.Empty : influence.SlotId ?? string.Empty,
                ordinal.ToString(CultureInfo.InvariantCulture));
        }

        private static string CreateId(GameState state, int playerId, string slotId, string sourceId, int sequence)
        {
            return StableIdFactory.Create(
                "influence",
                state.GameId ?? string.Empty,
                playerId.ToString(CultureInfo.InvariantCulture),
                slotId ?? string.Empty,
                sourceId ?? string.Empty,
                sequence.ToString(CultureInfo.InvariantCulture));
        }
    }
}
