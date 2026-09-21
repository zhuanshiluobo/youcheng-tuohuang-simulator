using System;
using System.Collections.Generic;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    /// <summary>Host 本地持久化快照；该 DTO 严禁进入 Mirror/客户端消息。</summary>
    [Serializable]
    public sealed class HostSnapshotDto
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion = CurrentSchemaVersion;
        public int StateRevision;
        public int LastCommitSequence;
        public string ContentHash = string.Empty;
        public string ActiveMainNodeId = string.Empty;
        public List<string> ActiveBlockerIds = new List<string>();
        public GameState State = new GameState();
    }

    /// <summary>
    /// Host-only append journal。StateAfter 让每条已落盘记录成为可恢复 checkpoint；它不
    /// 是网络增量 DTO，也不允许投影给任何客户端。
    /// </summary>
    [Serializable]
    public sealed class HostJournalEntryDto
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
        public string ReceiptId = string.Empty;
        public string EventId = string.Empty;
        public string SubscriptionId = string.Empty;
        public string HandlerId = string.Empty;
        public GameState StateAfter;
    }

    [Serializable]
    public sealed class HostJournalDto
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion = CurrentSchemaVersion;
        public List<HostJournalEntryDto> Entries = new List<HostJournalEntryDto>();
    }

    [Serializable]
    public sealed class HostSessionArchiveDto
    {
        public HostSnapshotDto Snapshot = new HostSnapshotDto();
        public HostJournalDto Journal = new HostJournalDto();
    }

    public sealed class HostRecoveryResult
    {
        internal HostRecoveryResult(bool succeeded, GameState state, string faultCode, string diagnostic)
        {
            Succeeded = succeeded;
            State = state;
            FaultCode = faultCode ?? string.Empty;
            Diagnostic = diagnostic ?? string.Empty;
        }

        public bool Succeeded { get; }
        public GameState State { get; }
        public string FaultCode { get; }
        public string Diagnostic { get; }
        public bool PausedFault { get { return !Succeeded; } }
    }

    /// <summary>
    /// Host snapshot + append journal 的唯一恢复入口。它先做结构/连续性/内容版本检查，
    /// 再选择最后一个已落盘 StateAfter；任何不确定性都返回 paused_fault，不猜测补段。
    /// </summary>
    public static class HostRecoveryService
    {
        public const string SnapshotInvalid = "snapshot_invalid";
        public const string JournalGap = "journal_gap";
        public const string JournalConflict = "journal_conflict";
        public const string ContentHashMismatch = "content_hash_mismatch";
        public const string UnknownHandler = "unknown_handler";
        public const string ActiveNodeMissing = "active_node_missing";
        public const string BlockerReferenceMissing = "blocker_reference_missing";
        public const string ReceiptConflict = "receipt_conflict";

        public static HostSessionArchiveDto CreateArchive(GameState state, string contentHash = "")
        {
            HostSnapshotDto snapshot = CreateSnapshot(state, contentHash);
            return new HostSessionArchiveDto
            {
                Snapshot = snapshot,
                Journal = new HostJournalDto()
            };
        }

        public static HostSnapshotDto CreateSnapshot(GameState state, string contentHash = "")
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            string diagnostic;
            if (!TryValidateHostState(state, null, out diagnostic))
                throw new InvalidOperationException("无法创建 Host snapshot：" + diagnostic);

            EffectRuntimeState runtime = state.EffectRuntime;
            var snapshot = new HostSnapshotDto
            {
                StateRevision = runtime.StateRevision,
                LastCommitSequence = GetLastCommitSequence(runtime),
                ContentHash = contentHash ?? string.Empty,
                ActiveMainNodeId = runtime.ActiveMainNodeId ?? string.Empty,
                State = GameStateCloneService.DeepClone(state)
            };
            for (int i = 0; i < runtime.Blockers.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers[i];
                if (blocker != null && !blocker.IsResolved && !string.IsNullOrEmpty(blocker.BlockerId))
                    snapshot.ActiveBlockerIds.Add(blocker.BlockerId);
            }
            return snapshot;
        }

        /// <summary>把 snapshot 之后的新 runtime journal 记录追加到 Host journal。</summary>
        public static void AppendJournal(
            HostSessionArchiveDto archive,
            GameState committedState,
            string contentHash = "")
        {
            if (archive == null) throw new ArgumentNullException(nameof(archive));
            if (archive.Snapshot == null) throw new InvalidOperationException("Host archive 缺少 snapshot。");
            if (archive.Journal == null) archive.Journal = new HostJournalDto();
            if (committedState == null || committedState.EffectRuntime == null)
                throw new ArgumentNullException(nameof(committedState));

            string expectedHash = archive.Snapshot.ContentHash ?? string.Empty;
            string actualHash = string.IsNullOrEmpty(contentHash) ? expectedHash : contentHash;
            if (!string.IsNullOrEmpty(expectedHash) && !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                throw new InvalidOperationException(ContentHashMismatch + "：本次提交内容哈希与 snapshot 不一致。");

            string diagnostic;
            if (!TryValidateHostState(committedState, null, out diagnostic))
                throw new InvalidOperationException("无法追加 Host journal：" + diagnostic);

            if (!TryValidateJournalShape(archive.Snapshot, archive.Journal, out diagnostic))
                throw new InvalidOperationException("无法追加 Host journal：" + diagnostic);

            int lastSequence = archive.Snapshot.LastCommitSequence;
            for (int i = 0; i < archive.Journal.Entries.Count; i++)
            {
                HostJournalEntryDto existing = archive.Journal.Entries[i];
                if (existing.CommitSequence > lastSequence) lastSequence = existing.CommitSequence;
            }

            IList<RuleJournalEntry> source = committedState.EffectRuntime.Journal;
            for (int i = 0; i < source.Count; i++)
            {
                RuleJournalEntry entry = source[i];
                if (entry == null || entry.CommitSequence <= lastSequence) continue;
                if (entry.CommitSequence != lastSequence + 1)
                    throw new InvalidOperationException(JournalGap + "：追加 journal 缺少连续 commit 序号。");
                if (!string.IsNullOrEmpty(entry.ContentHash) &&
                    !string.Equals(entry.ContentHash, actualHash, StringComparison.Ordinal))
                    throw new InvalidOperationException(ContentHashMismatch + "：journal 条目的内容哈希与 snapshot 不一致。");

                archive.Journal.Entries.Add(CreateJournalEntry(entry, committedState, actualHash));
                lastSequence = entry.CommitSequence;
            }
        }

        public static HostRecoveryResult Recover(
            HostSessionArchiveDto archive,
            EffectRegistry registry = null)
        {
            if (archive == null)
                return Fault(null, SnapshotInvalid, "Host archive 为空。");
            if (archive.Snapshot == null)
                return Fault(null, SnapshotInvalid, "Host archive 缺少 snapshot。");

            string diagnostic;
            if (!TryValidateSnapshot(archive.Snapshot, registry, out diagnostic))
                return Fault(archive.Snapshot.State, SnapshotInvalid, diagnostic);
            if (registry == null && ContainsHandlerState(archive.Snapshot.State))
                return Fault(archive.Snapshot.State, UnknownHandler, "恢复包含 Effect/continuation，但没有提供 handler registry。");

            HostJournalDto journal = archive.Journal ?? new HostJournalDto();
            if (journal.SchemaVersion <= 0 || journal.SchemaVersion > HostJournalDto.CurrentSchemaVersion)
                return Fault(archive.Snapshot.State, SnapshotInvalid, "Host journal schemaVersion 不受支持。");

            GameState recovered;
            try
            {
                recovered = GameStateCloneService.DeepClone(archive.Snapshot.State);
            }
            catch (Exception exception)
            {
                return Fault(archive.Snapshot.State, SnapshotInvalid, "snapshot 克隆失败：" + exception.Message);
            }

            int expectedSequence = archive.Snapshot.LastCommitSequence + 1;
            int previousRevision = archive.Snapshot.StateRevision;
            string previousCommitId = string.Empty;
            HostJournalEntryDto previous = null;
            for (int i = 0; i < journal.Entries.Count; i++)
            {
                HostJournalEntryDto entry = journal.Entries[i];
                if (entry == null)
                    return Fault(recovered, JournalConflict, "Host journal 包含空记录。");
                if (entry.CommitSequence < expectedSequence)
                    return Fault(recovered, JournalConflict, "Host journal 包含重复或倒退的 commit 序号 " + entry.CommitSequence + "。");
                if (entry.CommitSequence > expectedSequence)
                    return Fault(recovered, JournalGap, "期望 commit 序号 " + expectedSequence + "，实际为 " + entry.CommitSequence + "。");
                if (string.IsNullOrEmpty(entry.CommitId) ||
                    (previous != null && entry.StateRevision == previousRevision &&
                     !string.Equals(entry.CommitId, previousCommitId, StringComparison.Ordinal)))
                    return Fault(recovered, JournalConflict, "同一 revision 的 journal 记录必须属于同一 commit。");
                if (entry.StateRevision < previousRevision || entry.StateRevision > previousRevision + 1)
                    return Fault(recovered, JournalGap, "journal stateRevision 不连续。");
                if (!string.Equals(archive.Snapshot.ContentHash ?? string.Empty, entry.ContentHash ?? string.Empty, StringComparison.Ordinal))
                    return Fault(recovered, ContentHashMismatch, "journal 内容哈希与 snapshot 不一致。");
                if (entry.StateAfter == null)
                    return Fault(recovered, JournalConflict, "journal 记录缺少已提交状态 checkpoint。");

                if (registry == null && ContainsHandlerState(entry.StateAfter))
                    return Fault(recovered, UnknownHandler, "恢复包含 Effect/continuation，但没有提供 handler registry。");

                if (!TryValidateJournalState(entry, registry, out diagnostic))
                    return Fault(recovered, JournalConflict, diagnostic);

                recovered = GameStateCloneService.DeepClone(entry.StateAfter);
                previousRevision = entry.StateRevision;
                previousCommitId = entry.CommitId;
                previous = entry;
                expectedSequence++;
            }

            if (!TryValidateHostState(recovered, registry, out diagnostic))
                return Fault(recovered, SnapshotInvalid, diagnostic);

            EffectRuntimeState runtime = recovered.EffectRuntime;
            int recoveredLastSequence = GetLastCommitSequence(runtime);
            if (runtime.StateRevision != previousRevision || recoveredLastSequence != expectedSequence - 1)
                return Fault(recovered, JournalConflict, "恢复后的 runtime revision/commit 序号与 journal 不一致。");

            return new HostRecoveryResult(true, recovered, string.Empty, string.Empty);
        }

        public static bool HasReceipt(GameState state, string eventId, string subscriptionId)
        {
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.DispatchReceipts == null)
                return false;
            for (int i = 0; i < state.EffectRuntime.DispatchReceipts.Count; i++)
            {
                DispatchReceipt receipt = state.EffectRuntime.DispatchReceipts[i];
                if (receipt != null && receipt.EventId == eventId && receipt.SubscriptionId == subscriptionId)
                    return true;
            }
            return false;
        }

        public static bool HasReceipt(GameState state, string receiptId)
        {
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.DispatchReceipts == null)
                return false;
            return state.EffectRuntime.DispatchReceipts.Exists(item => item != null && item.ReceiptId == receiptId);
        }

        private static HostJournalEntryDto CreateJournalEntry(
            RuleJournalEntry source,
            GameState committedState,
            string contentHash)
        {
            var result = new HostJournalEntryDto
            {
                CommitSequence = source.CommitSequence,
                StateRevision = source.StateRevision,
                CommitId = source.CommitId ?? string.Empty,
                CommandId = source.CommandId ?? string.Empty,
                Kind = source.Kind,
                EntityId = source.EntityId ?? string.Empty,
                Detail = source.Detail ?? string.Empty,
                ContentId = source.ContentId ?? string.Empty,
                ContentVersion = source.ContentVersion ?? string.Empty,
                ContentHash = string.IsNullOrEmpty(source.ContentHash) ? contentHash : source.ContentHash,
                StateAfter = GameStateCloneService.DeepClone(committedState)
            };
            if (source.Kind == RuleJournalEntryKind.DispatchReceipt)
            {
                result.ReceiptId = source.EntityId ?? string.Empty;
                DispatchReceipt receipt = committedState.EffectRuntime.DispatchReceipts.Find(
                    item => item != null && item.ReceiptId == result.ReceiptId);
                if (receipt != null)
                {
                    result.EventId = receipt.EventId ?? string.Empty;
                    result.SubscriptionId = receipt.SubscriptionId ?? string.Empty;
                    result.HandlerId = receipt.HandlerId ?? string.Empty;
                    if (string.IsNullOrEmpty(result.ContentId)) result.ContentId = receipt.ContentId ?? string.Empty;
                    if (string.IsNullOrEmpty(result.ContentVersion)) result.ContentVersion = receipt.DefinitionVersion ?? string.Empty;
                }
            }
            return result;
        }

        private static bool TryValidateSnapshot(HostSnapshotDto snapshot, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (snapshot.SchemaVersion <= 0 || snapshot.SchemaVersion > HostSnapshotDto.CurrentSchemaVersion ||
                snapshot.State == null || snapshot.State.EffectRuntime == null || snapshot.StateRevision < 0 ||
                snapshot.LastCommitSequence < 0)
            {
                diagnostic = "snapshot schema、revision 或状态为空。";
                return false;
            }

            EffectRuntimeState runtime = snapshot.State.EffectRuntime;
            if (runtime.StateRevision != snapshot.StateRevision ||
                GetLastCommitSequence(runtime) != snapshot.LastCommitSequence ||
                runtime.NextCommitSequence != snapshot.LastCommitSequence + 1 ||
                !string.Equals(runtime.ActiveMainNodeId ?? string.Empty, snapshot.ActiveMainNodeId ?? string.Empty, StringComparison.Ordinal))
            {
                diagnostic = "snapshot revision、journal 序号或 active node 不一致。";
                return false;
            }

            HashSet<string> activeBlockers = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < runtime.Blockers.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers[i];
                if (blocker != null && !blocker.IsResolved) activeBlockers.Add(blocker.BlockerId ?? string.Empty);
            }
            if (snapshot.ActiveBlockerIds == null || snapshot.ActiveBlockerIds.Count != activeBlockers.Count)
            {
                diagnostic = "snapshot active blocker 引用不一致。";
                return false;
            }
            for (int i = 0; i < snapshot.ActiveBlockerIds.Count; i++)
            {
                if (!activeBlockers.Contains(snapshot.ActiveBlockerIds[i]))
                {
                    diagnostic = "snapshot 引用了未知 active blocker。";
                    return false;
                }
            }

            return TryValidateHostState(snapshot.State, registry, out diagnostic);
        }

        private static bool TryValidateJournalState(HostJournalEntryDto entry, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (entry.StateAfter == null || entry.StateAfter.EffectRuntime == null)
            {
                diagnostic = "journal checkpoint 缺少 EffectRuntime。";
                return false;
            }
            EffectRuntimeState runtime = entry.StateAfter.EffectRuntime;
            if (runtime.StateRevision != entry.StateRevision ||
                !runtime.Journal.Exists(item => item != null && item.CommitSequence == entry.CommitSequence &&
                    item.CommitId == entry.CommitId && item.EntityId == entry.EntityId))
            {
                diagnostic = "journal checkpoint 与记录的 commit/entity 不一致。";
                return false;
            }

            if (entry.Kind == RuleJournalEntryKind.DispatchReceipt)
            {
                if (string.IsNullOrEmpty(entry.ReceiptId))
                {
                    diagnostic = ReceiptConflict + "：分发收据 journal 缺少 receiptId。";
                    return false;
                }

                DispatchReceipt receipt = runtime.DispatchReceipts.Find(
                    item => item != null && item.ReceiptId == entry.ReceiptId);
                if (receipt == null ||
                    !string.Equals(receipt.EventId ?? string.Empty, entry.EventId ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(receipt.SubscriptionId ?? string.Empty, entry.SubscriptionId ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(receipt.HandlerId ?? string.Empty, entry.HandlerId ?? string.Empty, StringComparison.Ordinal))
                {
                    diagnostic = ReceiptConflict + "：分发收据身份与 checkpoint 不一致。";
                    return false;
                }
            }

            return TryValidateHostState(entry.StateAfter, registry, out diagnostic);
        }

        private static bool TryValidateJournalShape(
            HostSnapshotDto snapshot,
            HostJournalDto journal,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            if (snapshot == null || journal == null || journal.Entries == null)
            {
                diagnostic = JournalConflict + "：Host journal 集合为空。";
                return false;
            }

            int expectedSequence = snapshot.LastCommitSequence + 1;
            int previousRevision = snapshot.StateRevision;
            string previousCommitId = string.Empty;
            for (int i = 0; i < journal.Entries.Count; i++)
            {
                HostJournalEntryDto entry = journal.Entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.CommitId))
                {
                    diagnostic = JournalConflict + "：Host journal 包含空记录或缺少 commitId。";
                    return false;
                }
                if (entry.CommitSequence < expectedSequence)
                {
                    diagnostic = JournalConflict + "：Host journal 包含重复或倒退的 commit 序号。";
                    return false;
                }
                if (entry.CommitSequence > expectedSequence)
                {
                    diagnostic = JournalGap + "：Host journal 缺少连续 commit 序号。";
                    return false;
                }
                if (entry.StateRevision < previousRevision || entry.StateRevision > previousRevision + 1)
                {
                    diagnostic = JournalGap + "：Host journal stateRevision 不连续。";
                    return false;
                }
                if (entry.StateRevision == previousRevision &&
                    !string.IsNullOrEmpty(previousCommitId) &&
                    !string.Equals(entry.CommitId, previousCommitId, StringComparison.Ordinal))
                {
                    diagnostic = JournalConflict + "：同一 revision 的 journal 记录必须属于同一 commit。";
                    return false;
                }
                if (!string.Equals(snapshot.ContentHash ?? string.Empty, entry.ContentHash ?? string.Empty, StringComparison.Ordinal))
                {
                    diagnostic = ContentHashMismatch + "：journal 内容哈希与 snapshot 不一致。";
                    return false;
                }

                previousRevision = entry.StateRevision;
                previousCommitId = entry.CommitId;
                expectedSequence++;
            }

            return true;
        }

        private static bool TryValidateHostState(GameState state, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (state == null || state.EffectRuntime == null)
            {
                diagnostic = "Host 状态缺少 EffectRuntime。";
                return false;
            }
            if (!state.EffectRuntime.TryValidate(out diagnostic)) return false;

            EffectRuntimeState runtime = state.EffectRuntime;
            if (!string.IsNullOrEmpty(runtime.FirstMainNodeId) &&
                !runtime.MainNodes.Exists(item => item != null && item.NodeId == runtime.FirstMainNodeId))
            {
                diagnostic = ActiveNodeMissing + "：firstMainNodeId 不存在。";
                return false;
            }
            if (!string.IsNullOrEmpty(runtime.ActiveMainNodeId) &&
                !runtime.MainNodes.Exists(item => item != null && item.NodeId == runtime.ActiveMainNodeId))
            {
                diagnostic = ActiveNodeMissing + "：activeMainNodeId 不存在。";
                return false;
            }

            HashSet<string> nodeIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState node = runtime.EffectNodes[i];
                if (node == null || string.IsNullOrEmpty(node.EffectId) || !nodeIds.Add(node.EffectId))
                {
                    diagnostic = "Effect 节点为空或 ID 重复。";
                    return false;
                }
                if (registry != null && !registry.TryGet(node.EffectTypeId, out _))
                {
                    diagnostic = UnknownHandler + "：未知 Effect 类型 " + node.EffectTypeId;
                    return false;
                }
                for (int j = 0; j < node.ChildEffectIds.Count; j++)
                {
                    if (!nodeIds.Contains(node.ChildEffectIds[j]) &&
                        !runtime.EffectNodes.Exists(item => item != null && item.EffectId == node.ChildEffectIds[j]))
                    {
                        diagnostic = BlockerReferenceMissing + "：子节点不存在。";
                        return false;
                    }
                }
                if (!string.IsNullOrEmpty(node.ContinuationHandlerId))
                {
                    if (registry == null) continue;
                    EffectEventHandlerRegistration registration;
                    if (!registry.TryGetContinuation(node.ContinuationHandlerId, "EffectCompleted", out registration))
                    {
                        diagnostic = UnknownHandler + "：" + node.ContinuationHandlerId;
                        return false;
                    }
                    if (!string.Equals(registration.DefinitionVersion, node.ContinuationDefinitionVersion ?? string.Empty, StringComparison.Ordinal) ||
                        !string.Equals(registration.ContentHash, node.ContinuationContentHash ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostic = ContentHashMismatch + "：continuation handler " + node.ContinuationHandlerId;
                        return false;
                    }
                }
            }

            HashSet<string> blockerIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < runtime.Blockers.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers[i];
                if (blocker == null || string.IsNullOrEmpty(blocker.BlockerId) || !blockerIds.Add(blocker.BlockerId) ||
                    !nodeIds.Contains(blocker.OwnerEffectId) ||
                    (!string.IsNullOrEmpty(blocker.TargetEffectId) && !nodeIds.Contains(blocker.TargetEffectId)) ||
                    (!string.IsNullOrEmpty(blocker.InteractionRequestId) &&
                     !runtime.InteractionRequests.Exists(item => item != null && item.GetStableInteractionId() == blocker.InteractionRequestId)))
                {
                    diagnostic = BlockerReferenceMissing + "：blocker owner/target/interaction 不存在。";
                    return false;
                }
            }
            return true;
        }

        private static bool ContainsHandlerState(GameState state)
        {
            return state != null && state.EffectRuntime != null &&
                   state.EffectRuntime.EffectNodes != null && state.EffectRuntime.EffectNodes.Count > 0;
        }

        private static int GetLastCommitSequence(EffectRuntimeState runtime)
        {
            if (runtime == null || runtime.Journal == null || runtime.Journal.Count == 0) return 0;
            return runtime.Journal[runtime.Journal.Count - 1] == null
                ? 0 : runtime.Journal[runtime.Journal.Count - 1].CommitSequence;
        }

        private static HostRecoveryResult Fault(GameState source, string faultCode, string diagnostic)
        {
            GameState faulted;
            try
            {
                faulted = source == null ? new GameState() : GameStateCloneService.DeepClone(source);
            }
            catch
            {
                faulted = new GameState();
            }
            if (faulted.EffectRuntime == null) faulted.EffectRuntime = new EffectRuntimeState();
            if (faulted.EffectRuntime.Diagnostics == null) faulted.EffectRuntime.Diagnostics = new List<EffectDiagnosticRuntimeState>();
            faulted.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
            faulted.EffectRuntime.LastFaultCode = faultCode ?? SnapshotInvalid;
            faulted.EffectRuntime.LastFaultMessage = diagnostic ?? string.Empty;
            faulted.EffectRuntime.Diagnostics.Add(new EffectDiagnosticRuntimeState
            {
                Code = faulted.EffectRuntime.LastFaultCode,
                Message = faulted.EffectRuntime.LastFaultMessage
            });
            return new HostRecoveryResult(false, faulted, faulted.EffectRuntime.LastFaultCode, faulted.EffectRuntime.LastFaultMessage);
        }
    }
}
