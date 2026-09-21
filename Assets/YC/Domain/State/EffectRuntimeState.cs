using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YC.Domain.State
{
    public enum EffectRuntimeStatus
    {
        Active,
        PausedFault
    }

    public enum EffectNodeStatus
    {
        Created,
        Ready,
        Running,
        Blocked,
        Completed,
        Failed,
        Faulted
    }

    public enum EffectPendingOutcome
    {
        None,
        Completed,
        Failed
    }

    public enum RuleEventResponseKind
    {
        None,
        Effects,
        CandidatePatches
    }

    public enum NormalizedValueKind
    {
        Null,
        Boolean,
        Integer,
        String,
        StableReference,
        Array,
        Object
    }

    public enum RuleJournalEntryKind
    {
        Commit,
        Command,
        DomainState,
        EffectNode,
        Interaction,
        RuleEvent,
        DispatchReceipt,
        Fault,
        CandidateResolution
    }

    [Serializable]
    public sealed class NormalizedValueEntry
    {
        public string Name = string.Empty;
        public NormalizedValue Value = NormalizedValue.CreateNull();

        public NormalizedValueEntry Clone()
        {
            return new NormalizedValueEntry
            {
                Name = Name,
                Value = Value == null ? null : Value.Clone()
            };
        }
    }

    [Serializable]
    public sealed class NormalizedValue
    {
        public const int MaxDepth = 8;
        public const int MaxElements = 128;
        public const int MaxStringBytes = 1024;
        public const long MinInteger = -1000000000L;
        public const long MaxInteger = 1000000000L;

        public NormalizedValueKind Kind = NormalizedValueKind.Null;
        public bool BooleanValue;
        public long IntegerValue;
        public string StringValue = string.Empty;
        public string ReferenceType = string.Empty;
        public string ReferenceId = string.Empty;
        public List<NormalizedValue> Items = new List<NormalizedValue>();
        public List<NormalizedValueEntry> Properties = new List<NormalizedValueEntry>();

        public static NormalizedValue CreateNull()
        {
            return new NormalizedValue { Kind = NormalizedValueKind.Null };
        }

        public static NormalizedValue CreateBoolean(bool value)
        {
            return new NormalizedValue { Kind = NormalizedValueKind.Boolean, BooleanValue = value };
        }

        public static NormalizedValue CreateInteger(long value)
        {
            if (value < MinInteger || value > MaxInteger)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "归一化整数超出允许范围。");
            }

            return new NormalizedValue { Kind = NormalizedValueKind.Integer, IntegerValue = value };
        }

        public static NormalizedValue CreateString(string value)
        {
            ValidateString(value, nameof(value));
            return new NormalizedValue { Kind = NormalizedValueKind.String, StringValue = value };
        }

        public static NormalizedValue CreateStableReference(string referenceType, string referenceId)
        {
            ValidateString(referenceType, nameof(referenceType));
            ValidateString(referenceId, nameof(referenceId));
            if (string.IsNullOrEmpty(referenceType) || string.IsNullOrEmpty(referenceId))
            {
                throw new ArgumentException("稳定引用必须包含非空类型和 ID。");
            }

            return new NormalizedValue
            {
                Kind = NormalizedValueKind.StableReference,
                ReferenceType = referenceType,
                ReferenceId = referenceId
            };
        }

        public static NormalizedValue CreateArray(IList<NormalizedValue> values)
        {
            return new NormalizedValue
            {
                Kind = NormalizedValueKind.Array,
                Items = values == null ? new List<NormalizedValue>() : new List<NormalizedValue>(values)
            };
        }

        public static NormalizedValue CreateObject(IList<NormalizedValueEntry> entries)
        {
            List<NormalizedValueEntry> sorted = entries == null
                ? new List<NormalizedValueEntry>()
                : new List<NormalizedValueEntry>(entries);
            sorted.Sort((left, right) => StringComparer.Ordinal.Compare(
                left == null ? string.Empty : left.Name,
                right == null ? string.Empty : right.Name));

            return new NormalizedValue
            {
                Kind = NormalizedValueKind.Object,
                Properties = sorted
            };
        }

        public NormalizedValue Clone()
        {
            NormalizedValue clone = new NormalizedValue
            {
                Kind = Kind,
                BooleanValue = BooleanValue,
                IntegerValue = IntegerValue,
                StringValue = StringValue,
                ReferenceType = ReferenceType,
                ReferenceId = ReferenceId,
                Items = Items == null ? null : new List<NormalizedValue>(),
                Properties = Properties == null ? null : new List<NormalizedValueEntry>()
            };

            if (Items != null)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    clone.Items.Add(Items[i] == null ? null : Items[i].Clone());
                }
            }

            if (Properties != null)
            {
                for (int i = 0; i < Properties.Count; i++)
                {
                    clone.Properties.Add(Properties[i] == null ? null : Properties[i].Clone());
                }
            }

            return clone;
        }

        public bool IsValid()
        {
            string reason;
            return TryValidate(out reason);
        }

        public bool TryValidate(out string reason)
        {
            int elementCount = 0;
            return TryValidate(0, ref elementCount, out reason);
        }

        public string ToDeterministicString()
        {
            string reason;
            if (!TryValidate(out reason))
            {
                throw new InvalidOperationException("归一化值无效：" + reason);
            }

            StringBuilder builder = new StringBuilder();
            AppendCanonical(builder);
            return builder.ToString();
        }

        private bool TryValidate(int depth, ref int elementCount, out string reason)
        {
            if (depth > MaxDepth)
            {
                reason = "归一化值嵌套深度超过限制。";
                return false;
            }

            elementCount++;
            if (elementCount > MaxElements)
            {
                reason = "归一化值元素数量超过限制。";
                return false;
            }

            switch (Kind)
            {
                case NormalizedValueKind.Null:
                    reason = string.Empty;
                    return true;

                case NormalizedValueKind.Boolean:
                    reason = string.Empty;
                    return true;

                case NormalizedValueKind.Integer:
                    if (IntegerValue < MinInteger || IntegerValue > MaxInteger)
                    {
                        reason = "归一化整数超出允许范围。";
                        return false;
                    }

                    reason = string.Empty;
                    return true;

                case NormalizedValueKind.String:
                    return TryValidateString(StringValue, out reason);

                case NormalizedValueKind.StableReference:
                    if (!TryValidateString(ReferenceType, out reason) ||
                        !TryValidateString(ReferenceId, out reason) ||
                        string.IsNullOrEmpty(ReferenceType) ||
                        string.IsNullOrEmpty(ReferenceId))
                    {
                        reason = "稳定引用必须包含非空类型和 ID。";
                        return false;
                    }

                    reason = string.Empty;
                    return true;

                case NormalizedValueKind.Array:
                    if (Items == null || Items.Count > MaxElements)
                    {
                        reason = "归一化数组为空引用或元素数量超过限制。";
                        return false;
                    }

                    NormalizedValueKind? itemKind = null;
                    for (int i = 0; i < Items.Count; i++)
                    {
                        if (Items[i] == null)
                        {
                            reason = "归一化数组不能包含空元素。";
                            return false;
                        }

                        if (itemKind.HasValue && itemKind.Value != Items[i].Kind)
                        {
                            reason = "归一化数组元素必须保持同一 kind。";
                            return false;
                        }

                        itemKind = Items[i].Kind;
                        if (!Items[i].TryValidate(depth + 1, ref elementCount, out reason)) return false;
                    }

                    reason = string.Empty;
                    return true;

                case NormalizedValueKind.Object:
                    if (Properties == null || Properties.Count > MaxElements)
                    {
                        reason = "归一化对象为空引用或属性数量超过限制。";
                        return false;
                    }

                    string previousName = null;
                    for (int i = 0; i < Properties.Count; i++)
                    {
                        NormalizedValueEntry entry = Properties[i];
                        if (entry == null || !TryValidatePropertyName(entry.Name, out reason) || entry.Value == null)
                        {
                            reason = "归一化对象包含无效属性。";
                            return false;
                        }

                        if (previousName != null && StringComparer.Ordinal.Compare(previousName, entry.Name) >= 0)
                        {
                            reason = "归一化对象属性必须按 Ordinal 名称严格排序且唯一。";
                            return false;
                        }

                        previousName = entry.Name;
                        if (!entry.Value.TryValidate(depth + 1, ref elementCount, out reason)) return false;
                    }

                    reason = string.Empty;
                    return true;

                default:
                    reason = "未知归一化值 kind。";
                    return false;
            }
        }

        private void AppendCanonical(StringBuilder builder)
        {
            switch (Kind)
            {
                case NormalizedValueKind.Null:
                    builder.Append('N');
                    break;
                case NormalizedValueKind.Boolean:
                    builder.Append(BooleanValue ? "B1" : "B0");
                    break;
                case NormalizedValueKind.Integer:
                    builder.Append('I').Append(IntegerValue.ToString(CultureInfo.InvariantCulture)).Append(';');
                    break;
                case NormalizedValueKind.String:
                    AppendLengthPrefixed(builder, 'S', StringValue);
                    break;
                case NormalizedValueKind.StableReference:
                    AppendLengthPrefixed(builder, 'T', ReferenceType);
                    AppendLengthPrefixed(builder, 'R', ReferenceId);
                    break;
                case NormalizedValueKind.Array:
                    builder.Append('A').Append(Items.Count).Append('[');
                    for (int i = 0; i < Items.Count; i++) Items[i].AppendCanonical(builder);
                    builder.Append(']');
                    break;
                case NormalizedValueKind.Object:
                    builder.Append('O').Append(Properties.Count).Append('{');
                    for (int i = 0; i < Properties.Count; i++)
                    {
                        AppendLengthPrefixed(builder, 'K', Properties[i].Name);
                        Properties[i].Value.AppendCanonical(builder);
                    }

                    builder.Append('}');
                    break;
            }
        }

        private static void AppendLengthPrefixed(StringBuilder builder, char tag, string value)
        {
            builder.Append(tag)
                .Append(Encoding.UTF8.GetByteCount(value))
                .Append(':')
                .Append(value);
        }

        private static void ValidateString(string value, string parameterName)
        {
            string reason;
            if (!TryValidateString(value, out reason))
            {
                throw new ArgumentException(reason, parameterName);
            }
        }

        private static bool TryValidateString(string value, out string reason)
        {
            if (value == null)
            {
                reason = "字符串不能为 null。";
                return false;
            }

            if (Encoding.UTF8.GetByteCount(value) > MaxStringBytes)
            {
                reason = "字符串超出长度限制。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool TryValidatePropertyName(string value, out string reason)
        {
            if (string.IsNullOrEmpty(value))
            {
                reason = "对象属性名不能为空。";
                return false;
            }

            if (Encoding.UTF8.GetByteCount(value) > 128)
            {
                reason = "对象属性名超出长度限制。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    [Serializable]
    public sealed class TimingHandlerBinding
    {
        public string BindingId = string.Empty;
        public string SourceEffectId = string.Empty;
        public string AbilityId = string.Empty;
        public string HandlerSlot = string.Empty;
        public string DefinitionVersion = string.Empty;
        public string ContentHash = string.Empty;
        public int RoundNumber;
        public int ScopedPlayerId;
        public string Status = "registered";
        public string AttachedEffectId = string.Empty;
    }

    [Serializable]
    public sealed class EffectNodeRuntimeState
    {
        public string EffectId = string.Empty;
        public string RoundId = string.Empty;
        public string ParentEffectId = string.Empty;
        public string SourceId = string.Empty;
        public string EffectTypeId = string.Empty;
        public string DefinitionVersion = string.Empty;
        public int PlayerId = -1;
        public string Visibility = string.Empty;
        public EffectNodeStatus Status = EffectNodeStatus.Created;
        public EffectPendingOutcome PendingOutcome = EffectPendingOutcome.None;
        public NormalizedValue NormalizedArguments = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        public NormalizedValue NormalizedResult = NormalizedValue.CreateNull();
        public string FailureReason = string.Empty;
        public List<string> ChildEffectIds = new List<string>();
        public List<string> BlockerIds = new List<string>();
        public string CompletedEventId = string.Empty;
        public string ContinuationHandlerId = string.Empty;
        public string ContinuationBindingId = string.Empty;
        public string ContinuationContentId = string.Empty;
        public string ContinuationContentInstanceId = string.Empty;
        public string ContinuationAbilityId = string.Empty;
        public string ContinuationDefinitionVersion = string.Empty;
        public string ContinuationContentHash = string.Empty;
        public string FlowStage = string.Empty;
        public List<TimingHandlerBinding> TimingBindings = new List<TimingHandlerBinding>();
        public List<EffectSpecRuntimeState> NestedEffects = new List<EffectSpecRuntimeState>();
        public List<int> NestedGroupSizes = new List<int>();
        public bool AllowDecline;
        public int DecisionPlayerId = -1;
        public string DeclinePromptKey = string.Empty;
        public string UnavailablePolicy = string.Empty;
        public int LastCommitSequence;
    }

    [Serializable]
    public sealed class EffectSpecRuntimeState
    {
        public string EffectTypeId = string.Empty;
        public string DefinitionVersion = string.Empty;
        public string SourceId = string.Empty;
        public int PlayerId = -1;
        public string Visibility = string.Empty;
        public NormalizedValue NormalizedArguments = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        public string StableKey = string.Empty;
        public bool AllowDecline;
        public int DecisionPlayerId = -1;
        public string DeclinePromptKey = string.Empty;
        public string UnavailablePolicy = string.Empty;
        public string CompletionHandlerId = string.Empty;
        public string ContinuationContentId = string.Empty;
        public string ContinuationContentInstanceId = string.Empty;
        public string ContinuationAbilityId = string.Empty;
        public string ContinuationDefinitionVersion = string.Empty;
        public string ContinuationContentHash = string.Empty;
        public List<EffectSpecRuntimeState> NestedEffects = new List<EffectSpecRuntimeState>();
        public List<int> NestedGroupSizes = new List<int>();

        public EffectSpecRuntimeState Clone()
        {
            var clone = new EffectSpecRuntimeState
            {
                EffectTypeId = EffectTypeId,
                DefinitionVersion = DefinitionVersion,
                SourceId = SourceId,
                PlayerId = PlayerId,
                Visibility = Visibility,
                NormalizedArguments = NormalizedArguments == null ? null : NormalizedArguments.Clone(),
                StableKey = StableKey,
                AllowDecline = AllowDecline,
                DecisionPlayerId = DecisionPlayerId,
                DeclinePromptKey = DeclinePromptKey,
                UnavailablePolicy = UnavailablePolicy,
                CompletionHandlerId = CompletionHandlerId,
                ContinuationContentId = ContinuationContentId,
                ContinuationContentInstanceId = ContinuationContentInstanceId,
                ContinuationAbilityId = ContinuationAbilityId,
                ContinuationDefinitionVersion = ContinuationDefinitionVersion,
                ContinuationContentHash = ContinuationContentHash,
                NestedEffects = NestedEffects == null ? null : new List<EffectSpecRuntimeState>(),
                NestedGroupSizes = NestedGroupSizes == null ? null : new List<int>(NestedGroupSizes)
            };

            if (clone.NestedEffects != null)
            {
                for (int i = 0; i < NestedEffects.Count; i++)
                {
                    clone.NestedEffects.Add(NestedEffects[i] == null ? null : NestedEffects[i].Clone());
                }
            }

            return clone;
        }
    }

    [Serializable]
    public sealed class EffectBlockerRuntimeState
    {
        public string BlockerId = string.Empty;
        public string OwnerEffectId = string.Empty;
        public string TargetEffectId = string.Empty;
        public string InteractionRequestId = string.Empty;
        public string BlockerKind = string.Empty;
        public string Reason = string.Empty;
        public bool IsResolved;
    }

    [Serializable]
    public sealed class MainlineNodeRuntimeState
    {
        public string NodeId = string.Empty;
        public string RoundExecutionId = string.Empty;
        public string NodeTypeId = string.Empty;
        public string PreviousNodeId = string.Empty;
        public string NextNodeId = string.Empty;
        public int MainlineIndex;
        public int RoundNumber;
        public int StartPlayerId = -1;
        public int ActionRound;
        public int PlayerId = -1;
        public int ActiveTaskPlayerId = -1;
        public EffectNodeStatus Status = EffectNodeStatus.Created;
        public string ExecutionEffectId = string.Empty;
        public bool CompletionRequested;
        public int CompletionInteractionOrdinal;
        public List<int> CompletedPlayerIds = new List<int>();
        public List<string> PublishedEventKeys = new List<string>();
        // 统一盖放节点的顺序子任务由执行器创建后固化在主链节点上。
        // 该列表是恢复时的索引，不承载第二份可变规则状态。
        public List<string> OrderedChildEffectIds = new List<string>();
        public int LastCommitSequence;
    }

    [Serializable]
    public sealed class InteractionRequest
    {
        public const string MainlineCompletionInteractionPrefix = "round.mainline.complete:";

        // RequestId 保留为迁移期字段；InteractionId 是对外合同名称，二者在创建时保持一致。
        public string InteractionId = string.Empty;
        public string RequestId = string.Empty;
        public string SourceNodeId = string.Empty;
        public string InteractionTypeId = string.Empty;
        public string OwnerEffectId = string.Empty;
        public int AnsweringPlayerId = -1;
        public string Visibility = string.Empty;
        public string PromptKey = string.Empty;
        public NormalizedValue PromptParameters = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        public string CandidateSetId = string.Empty;
        public int CandidateSetVersion;
        public string CandidateResolutionId = string.Empty;
        public List<string> CandidateIds = new List<string>();
        public int MinSelections;
        public int MaxSelections;
        public bool AllowDecline;
        public string AnswerSchema = string.Empty;
        public string Status = "open";
        public NormalizedValue NormalizedAnswer = NormalizedValue.CreateNull();
        public int StateRevision;
        public int AnsweredAtRevision;

        public string GetStableInteractionId()
        {
            return string.IsNullOrEmpty(InteractionId) ? RequestId : InteractionId;
        }

        public bool IsInternalMainlineCompletion()
        {
            return !string.IsNullOrEmpty(InteractionTypeId) &&
                   InteractionTypeId.StartsWith(MainlineCompletionInteractionPrefix, StringComparison.Ordinal);
        }

        public bool IsInternalMainlineInteraction()
        {
            return IsInternalMainlineCompletion() ||
                   string.Equals(InteractionTypeId, "collection.task", StringComparison.Ordinal);
        }

        // 对外合同使用 kind；持久化仍只有 InteractionTypeId 一个权威字段。
        public string Kind
        {
            get { return InteractionTypeId; }
            set { InteractionTypeId = value ?? string.Empty; }
        }
    }

    [Serializable]
    public sealed class RuleEvent
    {
        public string EventId = string.Empty;
        public string EventType = string.Empty;
        public string SourceEffectId = string.Empty;
        public string OwnerNodeId = string.Empty;
        public string RouteKey = string.Empty;
        public string TargetEntityId = string.Empty;
        public int PlayerId = -1;
        public NormalizedValue Payload = NormalizedValue.CreateNull();
        // 只在 Host 和事件所属玩家的视图中合并；公共 Payload 必须已经是遮蔽后的投影。
        public NormalizedValue HostOnlyPayload = NormalizedValue.CreateNull();
        public RuleEventResponseKind ResponseKind = RuleEventResponseKind.None;
        public int StateRevision;
        public int CommitSequence;
        public string DefinitionVersion = string.Empty;
        public string Visibility = string.Empty;
    }

    [Serializable]
    public sealed class DispatchReceipt
    {
        public string ReceiptId = string.Empty;
        public string EventId = string.Empty;
        public string SubscriptionId = string.Empty;
        public string HandlerId = string.Empty;
        public string ContentId = string.Empty;
        public string DefinitionVersion = string.Empty;
        public int RouteTier;
        public int Priority;
        public string ContentInstanceId = string.Empty;
        public string AbilityId = string.Empty;
        public string InvocationKey = string.Empty;
        public List<string> AttachedEffectIds = new List<string>();
        public int CommitSequence;
        public bool Succeeded;
    }

    public enum ContinuationBindingStatus
    {
        Registered,
        Attached,
        Completed,
        Cancelled
    }

    [Serializable]
    public sealed class ContinuationBinding
    {
        public string BindingId = string.Empty;
        public string SourceEffectId = string.Empty;
        public string ContentId = string.Empty;
        public string ContentInstanceId = string.Empty;
        public string AbilityId = string.Empty;
        public string HandlerId = string.Empty;
        public string DefinitionVersion = string.Empty;
        public string ContentHash = string.Empty;
        public string CompletionEventId = string.Empty;
        public ContinuationBindingStatus Status = ContinuationBindingStatus.Registered;

        public ContinuationBinding Clone()
        {
            return new ContinuationBinding
            {
                BindingId = BindingId,
                SourceEffectId = SourceEffectId,
                ContentId = ContentId,
                ContentInstanceId = ContentInstanceId,
                AbilityId = AbilityId,
                HandlerId = HandlerId,
                DefinitionVersion = DefinitionVersion,
                ContentHash = ContentHash,
                CompletionEventId = CompletionEventId,
                Status = Status
            };
        }
    }

    [Serializable]
    public sealed class RuleJournalEntry
    {
        public int CommitSequence;
        public int StateRevision;
        public string CommitId = string.Empty;
        public string CommandId = string.Empty;
        public RuleJournalEntryKind Kind = RuleJournalEntryKind.Commit;
        public string EntityId = string.Empty;
        public string Detail = string.Empty;
        public string ContentId = string.Empty;
        public string ContentVersion = string.Empty;
        public string ContentHash = string.Empty;
        public NormalizedValue Payload = NormalizedValue.CreateNull();
    }

    [Serializable]
    public sealed class EffectDiagnosticRuntimeState
    {
        public string Code = string.Empty;
        public string Message = string.Empty;
        public string NodeId = string.Empty;
        public string EventId = string.Empty;
        public int StateRevision;
        public int CommitSequence;
    }

    [Serializable]
    public sealed class RuleCommit
    {
        public string CommitId = string.Empty;
        public string CommandId = string.Empty;
        public int StateRevision;
        public int FirstCommitSequence;
        public int LastCommitSequence;
        public List<RuleJournalEntry> Entries = new List<RuleJournalEntry>();

        public static RuleCommit Commit(GameState state, string commandId, params RuleJournalEntry[] entries)
        {
            return Apply(state, commandId, entries);
        }

        public static RuleCommit Create(GameState state, string commandId, params RuleJournalEntry[] entries)
        {
            return Apply(state, commandId, entries);
        }

        public static RuleCommit Apply(GameState state, string commandId, params RuleJournalEntry[] entries)
        {
            return ApplyInternal(state, commandId, false, entries);
        }

        public static RuleCommit ApplyFault(GameState state, string commandId, params RuleJournalEntry[] entries)
        {
            return ApplyInternal(state, commandId, true, entries);
        }

        private static RuleCommit ApplyInternal(
            GameState state,
            string commandId,
            bool allowPausedFault,
            params RuleJournalEntry[] entries)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.EffectRuntime == null)
            {
                state.EffectRuntime = new EffectRuntimeState();
            }

            EffectRuntimeState runtime = state.EffectRuntime;
            ValidateCommitPreconditions(runtime, allowPausedFault);

            List<RuleJournalEntry> sourceEntries = new List<RuleJournalEntry>();
            if (entries != null)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i] == null)
                    {
                        throw new ArgumentException("RuleCommit 不能包含空日志项。", nameof(entries));
                    }

                    string payloadReason;
                    if (entries[i].Payload != null &&
                        !entries[i].Payload.TryValidate(out payloadReason))
                    {
                        throw new ArgumentException(
                            "RuleCommit 包含非法归一化 payload：" + payloadReason,
                            nameof(entries));
                    }

                    sourceEntries.Add(entries[i]);
                }
            }

            if (sourceEntries.Count == 0)
            {
                sourceEntries.Add(new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.Commit,
                    EntityId = commandId ?? string.Empty,
                    Detail = "rule_commit"
                });
            }

            int nextRevision = checked(runtime.StateRevision + 1);
            int firstSequence = runtime.NextCommitSequence;
            int lastSequence = checked(firstSequence + sourceEntries.Count - 1);
            string normalizedCommandId = commandId ?? string.Empty;
            string commitId = StableIdFactory.Create(
                "commit",
                state.GameId ?? string.Empty,
                normalizedCommandId,
                nextRevision.ToString(CultureInfo.InvariantCulture),
                firstSequence.ToString(CultureInfo.InvariantCulture));

            List<RuleJournalEntry> committedEntries = new List<RuleJournalEntry>(sourceEntries.Count);
            for (int i = 0; i < sourceEntries.Count; i++)
            {
                RuleJournalEntry source = sourceEntries[i];
                RuleJournalEntry committed = new RuleJournalEntry
                {
                    CommitSequence = firstSequence + i,
                    StateRevision = nextRevision,
                    CommitId = commitId,
                    CommandId = string.IsNullOrEmpty(source.CommandId) ? normalizedCommandId : source.CommandId,
                    Kind = source.Kind,
                    EntityId = source.EntityId,
                    Detail = source.Detail,
                    ContentId = source.ContentId,
                    ContentVersion = source.ContentVersion,
                    ContentHash = source.ContentHash,
                    Payload = source.Payload == null ? null : source.Payload.Clone()
                };
                committedEntries.Add(committed);
            }

            runtime.StateRevision = nextRevision;
            runtime.NextCommitSequence = checked(lastSequence + 1);
            runtime.Journal.AddRange(committedEntries);

            RuleCommit result = new RuleCommit
            {
                CommitId = commitId,
                CommandId = normalizedCommandId,
                StateRevision = nextRevision,
                FirstCommitSequence = firstSequence,
                LastCommitSequence = lastSequence,
                Entries = new List<RuleJournalEntry>()
            };
            for (int i = 0; i < committedEntries.Count; i++)
            {
                result.Entries.Add(committedEntries[i].Clone());
            }

            return result;
        }

        private static void ValidateCommitPreconditions(EffectRuntimeState runtime, bool allowPausedFault)
        {
            if (runtime.SchemaVersion <= 0 ||
                (runtime.Status != EffectRuntimeStatus.Active &&
                 (!allowPausedFault || runtime.Status != EffectRuntimeStatus.PausedFault)))
            {
                throw new InvalidOperationException("运行状态不可提交：版本无效或已暂停于故障。");
            }

            string reason;
            if (!runtime.TryValidate(out reason))
            {
                throw new InvalidOperationException("运行状态当前不可提交：" + reason);
            }
        }

        public RuleCommitEntrySnapshot Snapshot()
        {
            return new RuleCommitEntrySnapshot
            {
                CommitId = CommitId,
                CommandId = CommandId,
                StateRevision = StateRevision,
                FirstCommitSequence = FirstCommitSequence,
                LastCommitSequence = LastCommitSequence,
                EntryCount = Entries == null ? 0 : Entries.Count
            };
        }
    }

    [Serializable]
    public sealed class RuleCommitEntrySnapshot
    {
        public string CommitId = string.Empty;
        public string CommandId = string.Empty;
        public int StateRevision;
        public int FirstCommitSequence;
        public int LastCommitSequence;
        public int EntryCount;
    }

    [Serializable]
    public sealed class EffectRuntimeState
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion = CurrentSchemaVersion;
        public EffectRuntimeStatus Status = EffectRuntimeStatus.Active;
        public int StateRevision;
        public int NextCommitSequence = 1;
        public string CurrentRoundExecutionId = string.Empty;
        public int CurrentRoundNumber;
        public int CurrentRoundStartPlayerId = -1;
        public int PendingNextRoundStartPlayerId = -1;
        public string FirstMainNodeId = string.Empty;
        public List<int> PlayerOrderSnapshot = new List<int>();
        public string ActiveMainNodeId = string.Empty;
        public List<EffectNodeRuntimeState> EffectNodes = new List<EffectNodeRuntimeState>();
        public List<MainlineNodeRuntimeState> MainNodes = new List<MainlineNodeRuntimeState>();
        public List<EffectBlockerRuntimeState> Blockers = new List<EffectBlockerRuntimeState>();
        public List<InteractionRequest> InteractionRequests = new List<InteractionRequest>();
        public List<CandidateResolutionRecord> CandidateResolutions = new List<CandidateResolutionRecord>();
        public List<RuleEvent> RuleEvents = new List<RuleEvent>();
        public List<DispatchReceipt> DispatchReceipts = new List<DispatchReceipt>();
        public List<ContinuationBinding> ContinuationBindings = new List<ContinuationBinding>();
        public List<RuleJournalEntry> Journal = new List<RuleJournalEntry>();
        public string LastFaultCode = string.Empty;
        public string LastFaultMessage = string.Empty;
        public int StepCount;
        public List<EffectDiagnosticRuntimeState> Diagnostics = new List<EffectDiagnosticRuntimeState>();

        public bool IsValid()
        {
            string reason;
            return TryValidate(out reason);
        }

        public bool TryValidate(out string reason)
        {
            if (SchemaVersion <= 0 || SchemaVersion > CurrentSchemaVersion)
            {
                reason = "运行状态 schemaVersion 不受支持。";
                return false;
            }

            if (Status != EffectRuntimeStatus.Active && Status != EffectRuntimeStatus.PausedFault)
            {
                reason = "运行状态 status 不受支持。";
                return false;
            }

            if (StateRevision < 0 || NextCommitSequence < 1 || StepCount < 0)
            {
                reason = "运行状态 revision、nextCommitSequence 或 stepCount 无效。";
                return false;
            }

            if (EffectNodes == null || MainNodes == null || Blockers == null ||
                InteractionRequests == null || CandidateResolutions == null || RuleEvents == null ||
                DispatchReceipts == null || ContinuationBindings == null || Journal == null || Diagnostics == null)
            {
                reason = "运行状态集合不能为 null。";
                return false;
            }

            int expectedSequence = 1;
            int previousRevision = 0;
            string previousCommitId = string.Empty;
            for (int i = 0; i < Journal.Count; i++)
            {
                RuleJournalEntry entry = Journal[i];
                if (entry == null || entry.CommitSequence != expectedSequence ||
                    entry.StateRevision <= 0 ||
                    entry.StateRevision < previousRevision ||
                    entry.StateRevision > previousRevision + 1 ||
                    string.IsNullOrEmpty(entry.CommitId))
                {
                    reason = "运行日志序号或 revision 不连续。";
                    return false;
                }

                if (entry.StateRevision == previousRevision &&
                    !string.Equals(entry.CommitId, previousCommitId, StringComparison.Ordinal))
                {
                    reason = "同一 revision 的运行日志必须属于同一 commit。";
                    return false;
                }

                if (entry.Payload != null && !entry.Payload.IsValid())
                {
                    reason = "运行日志包含非法归一化 payload。";
                    return false;
                }

                previousRevision = entry.StateRevision;
                previousCommitId = entry.CommitId;
                expectedSequence++;
            }

            if (NextCommitSequence != expectedSequence ||
                (Journal.Count == 0 && StateRevision != 0) ||
                (Journal.Count > 0 && StateRevision != previousRevision))
            {
                reason = "nextCommitSequence 或空日志与 stateRevision 不一致。";
                return false;
            }

            HashSet<string> nodeIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState node = EffectNodes[i];
                if (node == null || string.IsNullOrEmpty(node.EffectId) || !nodeIds.Add(node.EffectId))
                {
                    reason = "Effect 节点为空或 ID 重复。";
                    return false;
                }

                if (node != null && ((node.NormalizedArguments != null && !node.NormalizedArguments.IsValid()) ||
                                     (node.NormalizedResult != null && !node.NormalizedResult.IsValid()) ||
                                     node.ChildEffectIds == null || node.BlockerIds == null ||
                                     node.NestedEffects == null || node.NestedGroupSizes == null ||
                                     !TryValidateNestedSpecs(node.NestedEffects, node.NestedGroupSizes, out reason)))
                {
                    reason = "Effect 节点包含非法归一化参数或结果。";
                    return false;
                }
            }

            HashSet<string> mainNodeIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < MainNodes.Count; i++)
            {
                MainlineNodeRuntimeState node = MainNodes[i];
                if (node == null || string.IsNullOrEmpty(node.NodeId) || !mainNodeIds.Add(node.NodeId) ||
                    string.IsNullOrEmpty(node.NodeTypeId) || node.CompletedPlayerIds == null ||
                    node.PublishedEventKeys == null || node.OrderedChildEffectIds == null)
                {
                    reason = "主链节点为空、ID 重复或运行集合缺失。";
                    return false;
                }

                for (int j = 0; j < node.OrderedChildEffectIds.Count; j++)
                {
                    if (string.IsNullOrEmpty(node.OrderedChildEffectIds[j]) ||
                        !EffectNodes.Exists(candidate => candidate != null && candidate.EffectId == node.OrderedChildEffectIds[j]))
                    {
                        reason = "主链节点的有序子任务引用不存在。";
                        return false;
                    }
                }

            }

            for (int i = 0; i < MainNodes.Count; i++)
            {
                MainlineNodeRuntimeState node = MainNodes[i];
                if ((!string.IsNullOrEmpty(node.PreviousNodeId) &&
                     !MainNodes.Exists(candidate => candidate != null && candidate.NodeId == node.PreviousNodeId)) ||
                    (!string.IsNullOrEmpty(node.NextNodeId) &&
                     !MainNodes.Exists(candidate => candidate != null && candidate.NodeId == node.NextNodeId)))
                {
                    reason = "主链节点链接指向不存在的节点。";
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(ActiveMainNodeId) &&
                !MainNodes.Exists(node => node != null && node.NodeId == ActiveMainNodeId))
            {
                reason = "活动主链节点不存在。";
                return false;
            }

            HashSet<string> blockerIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Blockers.Count; i++)
            {
                EffectBlockerRuntimeState blocker = Blockers[i];
                if (blocker == null || string.IsNullOrEmpty(blocker.BlockerId) ||
                    !blockerIds.Add(blocker.BlockerId) || string.IsNullOrEmpty(blocker.OwnerEffectId) ||
                    !nodeIds.Contains(blocker.OwnerEffectId) ||
                    (!string.IsNullOrEmpty(blocker.TargetEffectId) && !nodeIds.Contains(blocker.TargetEffectId)) ||
                    (!string.IsNullOrEmpty(blocker.InteractionRequestId) &&
                     !InteractionRequests.Exists(request => request != null &&
                         request.GetStableInteractionId() == blocker.InteractionRequestId)))
                {
                    reason = "Effect blocker 引用了不存在的节点/交互或包含重复 ID。";
                    return false;
                }
            }

            for (int i = 0; i < EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState node = EffectNodes[i];
                if (node == null) continue;
                for (int j = 0; j < node.BlockerIds.Count; j++)
                {
                    if (string.IsNullOrEmpty(node.BlockerIds[j]) || !blockerIds.Contains(node.BlockerIds[j]))
                    {
                        reason = "Effect 节点引用了不存在的 blocker。";
                        return false;
                    }
                }
            }

            for (int i = 0; i < RuleEvents.Count; i++)
            {
                RuleEvent ruleEvent = RuleEvents[i];
                if (ruleEvent == null ||
                    (ruleEvent.Payload != null && !ruleEvent.Payload.IsValid()) ||
                    (ruleEvent.HostOnlyPayload != null && !ruleEvent.HostOnlyPayload.IsValid()))
                {
                    reason = "RuleEvent 为空或 payload 非法。";
                    return false;
                }
            }

            for (int i = 0; i < InteractionRequests.Count; i++)
            {
                InteractionRequest request = InteractionRequests[i];
                if (request == null || string.IsNullOrEmpty(request.GetStableInteractionId()) ||
                    (request.Status != "open" && request.Status != "answered" &&
                     request.Status != "cancelled" && request.Status != "invalidated") ||
                    request.StateRevision < 0 || request.AnsweredAtRevision < 0 ||
                    request.MinSelections < 0 || request.MaxSelections < request.MinSelections ||
                    request.CandidateIds == null || !HasUniqueIds(request.CandidateIds) ||
                    (request.PromptParameters != null && !request.PromptParameters.IsValid()) ||
                    (request.NormalizedAnswer != null && !request.NormalizedAnswer.IsValid()))
                {
                    reason = "InteractionRequest 为空或答案非法。";
                    return false;
                }
            }

            HashSet<string> interactionIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < InteractionRequests.Count; i++)
            {
                InteractionRequest request = InteractionRequests[i];
                if (request != null && !interactionIds.Add(request.GetStableInteractionId()))
                {
                    reason = "InteractionRequest ID 重复。";
                    return false;
                }
            }

            for (int i = 0; i < CandidateResolutions.Count; i++)
            {
                CandidateResolutionRecord record = CandidateResolutions[i];
                if (record == null)
                {
                    reason = "CandidateResolutionRecord 非法。";
                    return false;
                }

                if (!record.TryValidate(out reason))
                {
                    if (string.IsNullOrEmpty(reason)) reason = "CandidateResolutionRecord 非法。";
                    return false;
                }
            }

            HashSet<string> eventIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < RuleEvents.Count; i++)
            {
                if (RuleEvents[i] != null &&
                    (!string.IsNullOrEmpty(RuleEvents[i].EventId) && !eventIds.Add(RuleEvents[i].EventId)))
                {
                    reason = "RuleEvent ID 重复。";
                    return false;
                }
            }

            HashSet<string> receiptKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < DispatchReceipts.Count; i++)
            {
                DispatchReceipt receipt = DispatchReceipts[i];
                if (receipt == null || string.IsNullOrEmpty(receipt.EventId) ||
                    string.IsNullOrEmpty(receipt.SubscriptionId) ||
                    !receiptKeys.Add(receipt.EventId + "\u001f" + receipt.SubscriptionId) ||
                    receipt.AttachedEffectIds == null)
                {
                    reason = "分发收据为空、缺少键或重复。";
                    return false;
                }
            }

            HashSet<string> bindingIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ContinuationBindings.Count; i++)
            {
                ContinuationBinding binding = ContinuationBindings[i];
                if (binding == null || string.IsNullOrEmpty(binding.BindingId) ||
                    !bindingIds.Add(binding.BindingId) || string.IsNullOrEmpty(binding.SourceEffectId) ||
                    string.IsNullOrEmpty(binding.HandlerId) || string.IsNullOrEmpty(binding.DefinitionVersion) ||
                    (binding.Status != ContinuationBindingStatus.Registered &&
                     binding.Status != ContinuationBindingStatus.Attached &&
                     binding.Status != ContinuationBindingStatus.Completed &&
                     binding.Status != ContinuationBindingStatus.Cancelled))
                {
                    reason = "continuation binding 为空、重复或缺少稳定标识。";
                    return false;
                }

                if (!nodeIds.Contains(binding.SourceEffectId))
                {
                    reason = "continuation binding 指向不存在的 Effect 节点。";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static bool HasUniqueIds(IList<string> ids)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (string.IsNullOrEmpty(ids[i])) return false;
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(ids[i], ids[j], StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }

        private static bool TryValidateNestedSpecs(
            IList<EffectSpecRuntimeState> specs,
            IList<int> groupSizes,
            out string reason)
        {
            reason = string.Empty;
            long expectedCount = 0;
            if (specs == null || groupSizes == null)
            {
                reason = "Effect 嵌套描述集合不能为 null。";
                return false;
            }

            for (int i = 0; i < groupSizes.Count; i++)
            {
                if (groupSizes[i] < 0)
                {
                    reason = "Effect 嵌套组大小不能为负数。";
                    return false;
                }

                expectedCount += groupSizes[i];
                if (expectedCount > int.MaxValue)
                {
                    reason = "Effect 嵌套描述数量超过整数范围。";
                    return false;
                }
            }

            if (expectedCount != specs.Count)
            {
                reason = "Effect 嵌套组大小与描述数量不一致。";
                return false;
            }

            for (int i = 0; i < specs.Count; i++)
            {
                EffectSpecRuntimeState spec = specs[i];
                if (spec == null || string.IsNullOrEmpty(spec.EffectTypeId) ||
                    spec.NormalizedArguments == null || !spec.NormalizedArguments.IsValid() ||
                    !TryValidateNestedSpecs(spec.NestedEffects, spec.NestedGroupSizes, out reason))
                {
                    if (string.IsNullOrEmpty(reason)) reason = "Effect 嵌套描述无效。";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }
    }

    internal static class RuleJournalEntryExtensions
    {
        public static RuleJournalEntry Clone(this RuleJournalEntry source)
        {
            if (source == null) return null;
            return new RuleJournalEntry
            {
                CommitSequence = source.CommitSequence,
                StateRevision = source.StateRevision,
                CommitId = source.CommitId,
                CommandId = source.CommandId,
                Kind = source.Kind,
                EntityId = source.EntityId,
                Detail = source.Detail,
                ContentId = source.ContentId,
                ContentVersion = source.ContentVersion,
                ContentHash = source.ContentHash,
                Payload = source.Payload == null ? null : source.Payload.Clone()
            };
        }
    }
}
