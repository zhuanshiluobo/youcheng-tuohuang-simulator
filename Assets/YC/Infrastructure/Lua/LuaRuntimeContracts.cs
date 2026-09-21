using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public enum LuaInvocationStatus
    {
        Success,
        Faulted,
    }

    public enum LuaFailureCode
    {
        None,
        InvalidDefinition,
        ScriptTooLarge,
        ContentHashMismatch,
        VersionMismatch,
        ScriptError,
        ForbiddenApi,
        ReadOnlySnapshot,
        ForbiddenStateAccess,
        InstructionBudgetExceeded,
        TimeBudgetExceeded,
        InvalidHandler,
        InvalidReturnShape,
        ReturnLimitExceeded,
        TableDepthExceeded,
        TableEntryLimitExceeded,
        InvalidEffectSpec,
        UnexpectedField,
        MissingField,
        InvalidFieldType,
        UnknownEffectType,
        OutOfBounds,
        InvalidTarget,
    }

    public sealed class LuaExecutionBudget
    {
        public LuaExecutionBudget(
            long maxInstructions,
            int maxMilliseconds,
            int maxTableDepth,
            int maxTableEntries,
            int maxEffectSpecs,
            int maxScriptLength)
        {
            if (maxInstructions <= 0) throw new ArgumentOutOfRangeException(nameof(maxInstructions));
            if (maxMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(maxMilliseconds));
            if (maxTableDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maxTableDepth));
            if (maxTableEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxTableEntries));
            if (maxEffectSpecs <= 0) throw new ArgumentOutOfRangeException(nameof(maxEffectSpecs));
            if (maxScriptLength <= 0) throw new ArgumentOutOfRangeException(nameof(maxScriptLength));

            MaxInstructions = maxInstructions;
            MaxMilliseconds = maxMilliseconds;
            MaxTableDepth = maxTableDepth;
            MaxTableEntries = maxTableEntries;
            MaxEffectSpecs = maxEffectSpecs;
            MaxScriptLength = maxScriptLength;
        }

        public long MaxInstructions { get; }
        public int MaxMilliseconds { get; }
        public int MaxTableDepth { get; }
        public int MaxTableEntries { get; }
        public int MaxEffectSpecs { get; }
        public int MaxScriptLength { get; }

        public static LuaExecutionBudget Default { get; } = new LuaExecutionBudget(
            maxInstructions: 10000,
            maxMilliseconds: 100,
            maxTableDepth: 8,
            maxTableEntries: 128,
            maxEffectSpecs: 16,
            maxScriptLength: 32 * 1024);
    }

    public sealed class LuaScriptDefinition
    {
        public LuaScriptDefinition(
            string contentId,
            string abilityId,
            string handlerId,
            string definitionVersion,
            string source,
            string contentHash)
        {
            ContentId = contentId;
            AbilityId = abilityId;
            HandlerId = handlerId;
            DefinitionVersion = definitionVersion;
            Source = source;
            ContentHash = contentHash;
        }

        public string ContentId { get; }
        public string AbilityId { get; }
        public string HandlerId { get; }
        public string DefinitionVersion { get; }
        public string Source { get; }
        public string ContentHash { get; }
    }

    public enum LuaSnapshotValueKind
    {
        Null,
        Boolean,
        Integer,
        String,
        Array,
        Object
    }

    [Serializable]
    public sealed class LuaSnapshotEntry
    {
        public LuaSnapshotEntry(string name, LuaSnapshotValue value)
        {
            Name = name ?? string.Empty;
            Value = value ?? LuaSnapshotValue.Null;
        }

        public string Name { get; }
        public LuaSnapshotValue Value { get; }
    }

    /// <summary>
    /// Host 输入 Lua 的封闭快照值。它只包含标量、数组和有序对象，不携带领域引用、委托或运行时对象。
    /// </summary>
    [Serializable]
    public sealed class LuaSnapshotValue
    {
        private LuaSnapshotValue(LuaSnapshotValueKind kind)
        {
            Kind = kind;
            Items = new List<LuaSnapshotValue>();
            Properties = new List<LuaSnapshotEntry>();
        }

        public static LuaSnapshotValue Null { get; } = new LuaSnapshotValue(LuaSnapshotValueKind.Null);
        public LuaSnapshotValueKind Kind { get; }
        public bool BooleanValue { get; private set; }
        public long IntegerValue { get; private set; }
        public string StringValue { get; private set; }
        public List<LuaSnapshotValue> Items { get; }
        public List<LuaSnapshotEntry> Properties { get; }

        public static LuaSnapshotValue Boolean(bool value)
        {
            return new LuaSnapshotValue(LuaSnapshotValueKind.Boolean) { BooleanValue = value };
        }

        public static LuaSnapshotValue Integer(long value)
        {
            return new LuaSnapshotValue(LuaSnapshotValueKind.Integer) { IntegerValue = value };
        }

        public static LuaSnapshotValue String(string value)
        {
            return new LuaSnapshotValue(LuaSnapshotValueKind.String) { StringValue = value ?? string.Empty };
        }

        public static LuaSnapshotValue Array(IList<LuaSnapshotValue> values)
        {
            var result = new LuaSnapshotValue(LuaSnapshotValueKind.Array);
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++) result.Items.Add(values[i] ?? Null);
            }

            return result;
        }

        public static LuaSnapshotValue Object(IList<LuaSnapshotEntry> entries)
        {
            var result = new LuaSnapshotValue(LuaSnapshotValueKind.Object);
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i] != null) result.Properties.Add(entries[i]);
                }
            }

            result.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            return result;
        }

        public LuaSnapshotValue Get(string name)
        {
            for (int i = 0; i < Properties.Count; i++)
            {
                if (string.Equals(Properties[i].Name, name, StringComparison.Ordinal)) return Properties[i].Value;
            }

            return Null;
        }
    }

    [Serializable]
    public sealed class LuaPlayerSnapshot
    {
        public LuaPlayerSnapshot(string playerId, int score, int goldVoucherCount, string cityLocationId = "",
            int originium = 0, int originiumShard = 0, int iron = 0, int pureOriginium = 0)
        {
            PlayerId = playerId ?? string.Empty;
            Score = score;
            GoldVoucherCount = goldVoucherCount;
            CityLocationId = cityLocationId ?? string.Empty;
            Originium = originium;
            OriginiumShard = originiumShard;
            Iron = iron;
            PureOriginium = pureOriginium;
            HandCardIds = new List<string>();
            DiscardCardIds = new List<string>();
        }

        public string PlayerId { get; }
        public int Score { get; }
        public int GoldVoucherCount { get; }
        public string CityLocationId { get; }
        public int Originium { get; }
        public int OriginiumShard { get; }
        public int Iron { get; }
        public int PureOriginium { get; }
        public List<string> HandCardIds { get; }
        public List<string> DiscardCardIds { get; }
    }

    /// <summary>
    /// 五类 Host façade 共用的只读输入快照。没有传入扩展快照时，宿主仍提供最小标量合同。
    /// </summary>
    [Serializable]
    public sealed class LuaFacadeSnapshot
    {
        public string GameId { get; set; } = string.Empty;
        public string PhaseId { get; set; } = string.Empty;
        public int Round { get; set; }
        public int MaxRounds { get; set; }
        public int ActionRound { get; set; }
        public string StartPlayerId { get; set; } = string.Empty;
        public string CurrentPlayerId { get; set; } = string.Empty;
        public string ContentVersion { get; set; } = string.Empty;
        public List<string> TurnOrder { get; } = new List<string>();
        public List<string> EnabledDlcIds { get; } = new List<string>();
        public List<LuaPlayerSnapshot> Players { get; } = new List<LuaPlayerSnapshot>();
        public LuaSnapshotValue GlobalMap { get; set; } = LuaSnapshotValue.Object(null);
        public LuaSnapshotValue GlobalContent { get; set; } = LuaSnapshotValue.Object(null);
        public LuaSnapshotValue EventPayload { get; set; } = LuaSnapshotValue.Null;

        public static LuaFacadeSnapshot FromInvocation(
            string playerId,
            int playerScore,
            int goldVoucherCount,
            int playerCount,
            string definitionVersion)
        {
            var snapshot = new LuaFacadeSnapshot
            {
                PhaseId = string.Empty,
                MaxRounds = 0,
                StartPlayerId = "p1",
                CurrentPlayerId = playerId ?? string.Empty,
                ContentVersion = definitionVersion ?? string.Empty
            };
            int count = playerCount < 1 ? 1 : playerCount;
            for (int i = 0; i < count; i++)
            {
                string currentId = "p" + (i + 1).ToString(CultureInfo.InvariantCulture);
                snapshot.TurnOrder.Add(currentId);
                snapshot.Players.Add(new LuaPlayerSnapshot(
                    currentId,
                    currentId == playerId ? playerScore : 0,
                    currentId == playerId ? goldVoucherCount : 0));
            }

            return snapshot;
        }
    }

    public sealed class LuaInvocationContext
    {
        public LuaInvocationContext(
            string eventId,
            string eventType,
            string playerId,
            int stateRevision,
            string definitionVersion,
            int playerCount,
            int playerScore,
            int goldVoucherCount,
            LuaFacadeSnapshot facadeSnapshot = null)
        {
            EventId = eventId;
            EventType = eventType;
            PlayerId = playerId;
            StateRevision = stateRevision;
            DefinitionVersion = definitionVersion;
            PlayerCount = playerCount;
            PlayerScore = playerScore;
            GoldVoucherCount = goldVoucherCount;
            FacadeSnapshot = facadeSnapshot ?? LuaFacadeSnapshot.FromInvocation(
                playerId,
                playerScore,
                goldVoucherCount,
                playerCount,
                definitionVersion);
        }

        public string EventId { get; }
        public string EventType { get; }
        public string PlayerId { get; }
        public int StateRevision { get; }
        public string DefinitionVersion { get; }
        public int PlayerCount { get; }
        public int PlayerScore { get; }
        public int GoldVoucherCount { get; }
        public LuaFacadeSnapshot FacadeSnapshot { get; }
        public bool AllowOtherPlayerReads { get; set; }
        // Event 分发器在调用前设置响应合同。Lua 不能自行切换返回形状。
        public string ResponseKind { get; set; } = "effects";
    }

    public sealed class LuaEffectSpec
    {
        public LuaEffectSpec(
            string effectTypeId,
            string recipientPlayerId,
            string resourceTypeId,
            int amount,
            string reasonId)
        {
            EffectTypeId = effectTypeId;
            RecipientPlayerId = recipientPlayerId;
            ResourceTypeId = resourceTypeId;
            Amount = amount;
            ReasonId = reasonId;
            LocationIds = new List<string>();
            HasIsOpen = false;
        }

        private LuaEffectSpec(string effectTypeId, NormalizedValue arguments, IReadOnlyList<LuaEffectSpec>[] nestedGroups)
        {
            EffectTypeId = effectTypeId ?? string.Empty;
            RecipientPlayerId = string.Empty;
            ResourceTypeId = string.Empty;
            Amount = 0;
            ReasonId = string.Empty;
            LocationIds = new List<string>();
            HasIsOpen = false;
            RawArguments = arguments == null ? NormalizedValue.CreateNull() : arguments.Clone();
            NestedEffectGroups = nestedGroups;
        }

        public static LuaEffectSpec FromArguments(string effectTypeId, NormalizedValue arguments)
        {
            return new LuaEffectSpec(effectTypeId, arguments, null);
        }

        public static LuaEffectSpec FromEffectGroups(string effectTypeId, NormalizedValue arguments, IReadOnlyList<LuaEffectSpec>[] groups)
        {
            return new LuaEffectSpec(effectTypeId, arguments, groups);
        }

        public static LuaEffectSpec FromNestedArguments(
            string effectTypeId,
            NormalizedValue arguments,
            IReadOnlyList<LuaEffectSpec> leftEffects,
            IReadOnlyList<LuaEffectSpec> rightEffects)
        {
            return new LuaEffectSpec(effectTypeId, arguments, new[] { leftEffects, rightEffects });
        }

        public LuaEffectSpec(
            string effectTypeId,
            IReadOnlyList<string> locationIds,
            bool isOpen,
            string reasonId)
        {
            EffectTypeId = effectTypeId;
            RecipientPlayerId = string.Empty;
            ResourceTypeId = string.Empty;
            Amount = 0;
            ReasonId = reasonId;
            LocationIds = locationIds == null ? new List<string>() : new List<string>(locationIds);
            IsOpen = isOpen;
            HasIsOpen = true;
        }

        public LuaEffectSpec(
            string effectTypeId,
            string abilityId,
            string cardInstanceId,
            string mode)
            : this(effectTypeId, abilityId, cardInstanceId, mode, string.Empty)
        {
        }

        public LuaEffectSpec(
            string effectTypeId,
            string abilityId,
            string cardInstanceId,
            string mode,
            string effectOrder)
        {
            EffectTypeId = effectTypeId;
            RecipientPlayerId = string.Empty;
            ResourceTypeId = string.Empty;
            Amount = 0;
            ReasonId = string.Empty;
            LocationIds = new List<string>();
            AbilityId = abilityId ?? string.Empty;
            CardInstanceId = cardInstanceId ?? string.Empty;
            Mode = mode ?? string.Empty;
            EffectOrder = effectOrder ?? string.Empty;
            HasIsOpen = false;
        }

        public LuaEffectSpec(
            string effectTypeId,
            string executingPlayerId,
            string targetInfluenceId,
            string targetSlotId,
            string sourceId,
            string ownerSubjectId,
            string replacementSourceId,
            string replacementOwnerId,
            string causeKind,
            string placementFailurePolicy,
            string destination,
            string reasonId)
        {
            EffectTypeId = effectTypeId;
            RecipientPlayerId = string.Empty;
            ResourceTypeId = string.Empty;
            Amount = 0;
            ReasonId = reasonId ?? string.Empty;
            LocationIds = new List<string>();
            ExecutingPlayerId = executingPlayerId ?? string.Empty;
            TargetInfluenceId = targetInfluenceId ?? string.Empty;
            TargetSlotId = targetSlotId ?? string.Empty;
            CandidateIds = new List<string>();
            InfluenceSourceId = sourceId ?? string.Empty;
            OwnerSubjectId = ownerSubjectId ?? string.Empty;
            ReplacementSourceId = replacementSourceId ?? string.Empty;
            ReplacementOwnerId = replacementOwnerId ?? string.Empty;
            CauseKind = causeKind ?? string.Empty;
            PlacementFailurePolicy = placementFailurePolicy ?? string.Empty;
            Destination = destination ?? string.Empty;
            HasIsOpen = false;
        }

        public LuaEffectSpec(
            string effectTypeId,
            string executingPlayerId,
            string targetInfluenceId,
            string targetSlotId,
            string sourceId,
            string ownerSubjectId,
            string replacementSourceId,
            string replacementOwnerId,
            string causeKind,
            string placementFailurePolicy,
            string destination,
            string reasonId,
            IReadOnlyList<string> candidateIds)
            : this(effectTypeId, executingPlayerId, targetInfluenceId, targetSlotId, sourceId,
                ownerSubjectId, replacementSourceId, replacementOwnerId, causeKind,
                placementFailurePolicy, destination, reasonId)
        {
            HasCandidateScope = candidateIds != null;
            CandidateIds = candidateIds == null
                ? new List<string>()
                : new List<string>(candidateIds);
        }

        public LuaEffectSpec(
            string effectTypeId,
            string operation,
            string executingPlayerId,
            IReadOnlyList<string> candidateIds,
            string candidateSetId,
            int candidateSetVersion,
            int minSelections,
            int maxSelections,
            string promptKey,
            int amount,
            string resourceTypeId,
            string markerId)
        {
            EffectTypeId = effectTypeId;
            RecipientPlayerId = string.Empty;
            ResourceTypeId = resourceTypeId ?? string.Empty;
            Amount = amount;
            ReasonId = string.Empty;
            LocationIds = new List<string>();
            Operation = operation ?? string.Empty;
            ExecutingPlayerId = executingPlayerId ?? string.Empty;
            CandidateIds = candidateIds == null ? new List<string>() : new List<string>(candidateIds);
            CandidateSetId = candidateSetId ?? string.Empty;
            CandidateSetVersion = candidateSetVersion;
            MinSelections = minSelections;
            MaxSelections = maxSelections;
            PromptKey = promptKey ?? string.Empty;
            MarkerId = markerId ?? string.Empty;
            HasIsOpen = false;
        }

        public LuaEffectSpec(
            string effectTypeId,
            string operation,
            string executingPlayerId,
            IReadOnlyList<string> candidateIds,
            string candidateSetId,
            int candidateSetVersion,
            int minSelections,
            int maxSelections,
            string promptKey,
            int amount,
            string resourceTypeId,
            string markerId,
            string facilityId,
            string facilityInstanceId,
            int sourceSlotIndex)
            : this(effectTypeId, operation, executingPlayerId, candidateIds, candidateSetId,
                candidateSetVersion, minSelections, maxSelections, promptKey, amount,
                resourceTypeId, markerId)
        {
            FacilityId = facilityId ?? string.Empty;
            FacilityInstanceId = facilityInstanceId ?? string.Empty;
            SourceSlotIndex = sourceSlotIndex;
        }

        public string EffectTypeId { get; }
        public string RecipientPlayerId { get; }
        public string ResourceTypeId { get; }
        public int Amount { get; }
        public string ReasonId { get; }
        public IReadOnlyList<string> LocationIds { get; }
        public bool IsOpen { get; }
        public bool HasIsOpen { get; }
        public string AbilityId { get; }
        public string CardInstanceId { get; }
        public string Mode { get; }
        public string EffectOrder { get; }
        public string ExecutingPlayerId { get; }
        public string TargetInfluenceId { get; }
        public string TargetSlotId { get; }
        public string InfluenceSourceId { get; }
        public string OwnerSubjectId { get; }
        public string ReplacementSourceId { get; }
        public string ReplacementOwnerId { get; }
        public string CauseKind { get; }
        public string PlacementFailurePolicy { get; }
        public string Destination { get; }
        public string Operation { get; }
        public string CandidateSetId { get; }
        public int CandidateSetVersion { get; }
        public bool HasCandidateScope { get; }
        public IReadOnlyList<string> CandidateIds { get; }
        public int MinSelections { get; }
        public int MaxSelections { get; }
        public string PromptKey { get; }
        public string MarkerId { get; }
        public string FacilityId { get; }
        public string FacilityInstanceId { get; }
        public int SourceSlotIndex { get; }
        public NormalizedValue RawArguments { get; }
        public IReadOnlyList<LuaEffectSpec>[] NestedEffectGroups { get; }
    }

    public sealed class LuaCandidatePatch
    {
        public LuaCandidatePatch(string operation, IReadOnlyList<string> ids, string reasonCode)
        {
            Operation = operation ?? string.Empty;
            Ids = ids == null ? new List<string>().AsReadOnly() : new List<string>(ids).AsReadOnly();
            ReasonCode = reasonCode ?? string.Empty;
        }

        public string Operation { get; }
        public IReadOnlyList<string> Ids { get; }
        public string ReasonCode { get; }
    }

    public sealed class LuaInvocationResult
    {
        private LuaInvocationResult(
            LuaInvocationStatus status,
            LuaFailureCode failureCode,
            string diagnostic,
            string contentId,
            string handlerId,
            string definitionVersion,
            string contentHash,
            IReadOnlyList<LuaEffectSpec> effects,
            IReadOnlyList<LuaCandidatePatch> candidatePatches)
        {
            Status = status;
            FailureCode = failureCode;
            Diagnostic = diagnostic;
            ContentId = contentId;
            HandlerId = handlerId;
            DefinitionVersion = definitionVersion;
            ContentHash = contentHash;
            Effects = effects;
            CandidatePatches = candidatePatches;
        }

        public LuaInvocationStatus Status { get; }
        public LuaFailureCode FailureCode { get; }
        public string Diagnostic { get; }
        public string ContentId { get; }
        public string HandlerId { get; }
        public string DefinitionVersion { get; }
        public string ContentHash { get; }
        public IReadOnlyList<LuaEffectSpec> Effects { get; }
        public IReadOnlyList<LuaCandidatePatch> CandidatePatches { get; }

        public bool IsSuccess => Status == LuaInvocationStatus.Success;

        internal static LuaInvocationResult Success(
            LuaScriptDefinition definition,
            IReadOnlyList<LuaEffectSpec> effects)
        {
            return Success(definition, effects, Array.Empty<LuaCandidatePatch>());
        }

        internal static LuaInvocationResult Success(
            LuaScriptDefinition definition,
            IReadOnlyList<LuaEffectSpec> effects,
            IReadOnlyList<LuaCandidatePatch> candidatePatches)
        {
            return new LuaInvocationResult(
                LuaInvocationStatus.Success,
                LuaFailureCode.None,
                string.Empty,
                definition.ContentId,
                definition.HandlerId,
                definition.DefinitionVersion,
                definition.ContentHash,
                effects,
                candidatePatches);
        }

        internal static LuaInvocationResult Failure(
            LuaScriptDefinition definition,
            LuaFailureCode failureCode,
            string diagnostic)
        {
            return new LuaInvocationResult(
                LuaInvocationStatus.Faulted,
                failureCode,
                diagnostic,
                definition == null ? string.Empty : definition.ContentId,
                definition == null ? string.Empty : definition.HandlerId,
                definition == null ? string.Empty : definition.DefinitionVersion,
                definition == null ? string.Empty : definition.ContentHash,
                Array.Empty<LuaEffectSpec>(),
                Array.Empty<LuaCandidatePatch>());
        }
    }

    public static class LuaContentHasher
    {
        public static string ComputeSha256(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(source));
                StringBuilder builder = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
