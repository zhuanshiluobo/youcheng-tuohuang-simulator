using System;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>原包与压缩字节摘要分开；初始和确认前缀恢复使用明确不同身份。</summary>
    [Serializable]
    public sealed class SimulationCheckpointEnvelope
    {
        public const int CurrentProtocolVersion = 1;
        public const string InitialSimulation = "InitialSimulation";
        public const string SavedSimulation = "SavedSimulation";
        public const int SavedProtocolVersion = 3;
        public const string ConfirmedSimulationRecovery = "ConfirmedSimulationRecovery";
        public const int RecoveryProtocolVersion = 2;
        public const int WindowInitialProtocolVersion = 4;
        public const int WindowSavedProtocolVersion = 5;
        public const int WindowRecoveryProtocolVersion = 6;
        public const string RecoveryDigestVersion = "yc.confirmed-checkpoint.sha256-utf8.v1";
        public const string GzipVersion = "gzip.v1";
        public const string Utf8ArchiveDigestVersion = "yc.initial-checkpoint.sha256-utf8.v1";
        public const string GzipDigestVersion = "yc.simulation-checkpoint.gzip.sha256.v1";

        public int ProtocolVersion = CurrentProtocolVersion;
        // 线上会话能力；缺字段的旧信封保持0，不能靠内容hash推断新请求语义。
        public int RequestContractVersion;
        public int WindowExecutionVersion;
        public string Purpose = InitialSimulation;
        public string GameId = string.Empty;
        public long SimulationEpoch;
        public long SourceSimulationEpoch;
        public int MembershipVersion;
        public string CheckpointId = string.Empty;
        public long GlobalCommandSequence;
        public string ContentPackHash = string.Empty;
        public string ArchiveDigestVersion = Utf8ArchiveDigestVersion;
        public string ArchiveDigest = string.Empty;
        public string Compression = GzipVersion;
        public int EncodedByteCount;
        public int DecodedByteCount;
        public string EncodedDigestVersion = GzipDigestVersion;
        public string EncodedDigest = string.Empty;

        public SimulationCheckpointEnvelope Snapshot() => (SimulationCheckpointEnvelope)MemberwiseClone();

        public static bool SameIdentity(SimulationCheckpointEnvelope left, SimulationCheckpointEnvelope right) =>
            left != null && right != null && left.ProtocolVersion == right.ProtocolVersion && left.Purpose == right.Purpose &&
            left.RequestContractVersion == right.RequestContractVersion &&
            left.WindowExecutionVersion == right.WindowExecutionVersion &&
            left.GameId == right.GameId && left.SimulationEpoch == right.SimulationEpoch && left.MembershipVersion == right.MembershipVersion &&
            left.SourceSimulationEpoch == right.SourceSimulationEpoch &&
            left.CheckpointId == right.CheckpointId && left.GlobalCommandSequence == right.GlobalCommandSequence &&
            left.ContentPackHash == right.ContentPackHash && left.ArchiveDigestVersion == right.ArchiveDigestVersion &&
            left.Compression == right.Compression && left.EncodedDigestVersion == right.EncodedDigestVersion;

        public static bool SamePayload(SimulationCheckpointEnvelope left, SimulationCheckpointEnvelope right) =>
            SameIdentity(left, right) && left.ArchiveDigest == right.ArchiveDigest && left.EncodedDigest == right.EncodedDigest &&
            left.EncodedByteCount == right.EncodedByteCount && left.DecodedByteCount == right.DecodedByteCount;

        public bool HasValidIdentity() =>
            ((ProtocolVersion == CurrentProtocolVersion && WindowExecutionVersion == 0 || ProtocolVersion == WindowInitialProtocolVersion && WindowExecutionVersion == WindowExecutionGrant.Version) && Purpose == InitialSimulation && SourceSimulationEpoch == 0 && ArchiveDigestVersion == Utf8ArchiveDigestVersion ||
             (ProtocolVersion == SavedProtocolVersion && WindowExecutionVersion == 0 || ProtocolVersion == WindowSavedProtocolVersion && WindowExecutionVersion == WindowExecutionGrant.Version) && Purpose == SavedSimulation && SourceSimulationEpoch > 0 &&
             SourceSimulationEpoch < long.MaxValue && SimulationEpoch == SourceSimulationEpoch + 1 && ArchiveDigestVersion == Utf8ArchiveDigestVersion ||
             (ProtocolVersion == RecoveryProtocolVersion && WindowExecutionVersion == 0 || ProtocolVersion == WindowRecoveryProtocolVersion && WindowExecutionVersion == WindowExecutionGrant.Version) && Purpose == ConfirmedSimulationRecovery && SourceSimulationEpoch > 0 &&
             SourceSimulationEpoch < long.MaxValue && SimulationEpoch == SourceSimulationEpoch + 1 && ArchiveDigestVersion == RecoveryDigestVersion) &&
            (RequestContractVersion == 0 || RequestContractVersion == OperationCommandProtocol.RequestContractVersion) &&
            (WindowExecutionVersion == 0 || WindowExecutionVersion == WindowExecutionGrant.Version) &&
            ValidText(GameId, 128) && SimulationEpoch > 0 && MembershipVersion > 0 && ValidText(CheckpointId, 128) &&
            GlobalCommandSequence >= 0 && GlobalCommandSequence < long.MaxValue && ValidText(ContentPackHash, 128) &&
            Compression == GzipVersion && EncodedDigestVersion == GzipDigestVersion;

        public bool HasValidPayload() => HasValidIdentity() && EncodedByteCount >= 18 && EncodedByteCount <= SimulationCheckpointTransfer.MaximumEncodedBytes &&
            DecodedByteCount > 0 && DecodedByteCount <= SimulationCheckpointTransfer.MaximumDecodedBytes &&
            ValidDigest(ArchiveDigest) && ValidDigest(EncodedDigest);

        private static bool ValidText(string text, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength) return false;
            foreach (char value in text) if (char.IsControl(value)) return false;
            try { new System.Text.UTF8Encoding(false, true).GetByteCount(text); return true; }
            catch (System.Text.EncoderFallbackException) { return false; }
        }

        private static bool ValidDigest(string digest)
        {
            if (digest == null || digest.Length != 64) return false;
            foreach (char value in digest) if (!(value >= '0' && value <= '9') && !(value >= 'a' && value <= 'f')) return false;
            return true;
        }
    }
}
