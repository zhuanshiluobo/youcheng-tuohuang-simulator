using System;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    /// <summary>冷启动续局沿用确认点预检、原子安装和日志续接；必须匹配房间已冻结的保存点标识。</summary>
    public sealed class SavedSimulationCheckpointInstaller : ISimulationCheckpointInstaller
    {
        private readonly SimulationConfirmedRecoveryInstaller installer;
        private readonly SimulationRecoveryNoticeDto expected;
        private string installedDigest;
        private readonly GameSession session;
        public DeterministicSimulationCoordinator Coordinator { get; private set; }

        public SavedSimulationCheckpointInstaller(GameSession session, EffectRegistry registry, GameStateViewer viewer,
            DeterministicSimulationSessionOptions options, string checkpointId, string metadataDigest)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            if (options == null || options.IsHost || !InitialSimulationCheckpointInstaller.IsInitialSkeleton(session.State))
                throw new ArgumentException("saved_checkpoint_requires_cold_client");
            installer = new SimulationConfirmedRecoveryInstaller(session, registry, viewer, options);
            expected = new SimulationRecoveryNoticeDto { GameId = options.GameId,
                SimulationEpoch = options.SimulationEpoch, SourceSimulationEpoch = options.SimulationEpoch - 1,
                MembershipVersion = options.MembershipVersion, CheckpointId = checkpointId,
                ConfirmedGlobalSequence = options.CheckpointGlobalSequence, MetadataDigest = metadataDigest,
                Phase = SimulationRecoveryPhase.Prepare, ResumeAllowed = true };
        }
        public bool TryInstall(string checkpointId, string json, out InitialSimulationCheckpointInstallResult result)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 64 * 1024 * 1024 || installedDigest == null && !InitialSimulationCheckpointInstaller.IsInitialSkeleton(session.State))
            { result = new InitialSimulationCheckpointInstallResult(false, false, checkpointId, "", "saved_checkpoint_requires_cold_client", "只允许在续局冷启动时安装存档。"); return false; }
            string digest;
            using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false, true).GetBytes(json))).Replace("-", "").ToLowerInvariant();
            if (checkpointId != expected.CheckpointId || installedDigest != null && installedDigest != digest)
            {
                result = new InitialSimulationCheckpointInstallResult(false, false, checkpointId, digest, "saved_checkpoint_identity_mismatch", "续局检查点与房间已确认存档不一致。");
                return false;
            }
            if (installedDigest != null)
            { result = new InitialSimulationCheckpointInstallResult(true, true, checkpointId, digest, "", ""); return true; }
            if (!installer.TryPrepare(expected, json, out var prepared, out var recovery) || !installer.TryCommit(prepared, out recovery))
            { result = new InitialSimulationCheckpointInstallResult(false, false, checkpointId, digest, recovery.ErrorCode, recovery.Reason); return false; }
            Coordinator = prepared.Coordinator; installedDigest = digest;
            result = new InitialSimulationCheckpointInstallResult(true, false, checkpointId, digest, "", ""); return true;
        }
    }
}
