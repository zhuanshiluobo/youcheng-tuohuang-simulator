using System;
using System.Collections.Generic;
using System.Linq;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    public enum SimulationCheckpointReceiveStatus { Buffered, Installed, AlreadyInstalled, Rejected, Stopped }
    public sealed class SimulationCheckpointChannelResult
    {
        public SimulationCheckpointReceiveStatus Status { get; internal set; }
        public SimulationCheckpointEnvelope Envelope { get; internal set; }
        public InitialSimulationCheckpointInstallResult InstallResult { get; internal set; }
        public string FaultCode { get; internal set; } = string.Empty;
    }

    /// <summary>由Mirror在已绑定连接上调用。只管理初始运输，不授予操作窗口或业务确认。</summary>
    public sealed class SimulationCheckpointChannel
    {
        private readonly SimulationCheckpointEnvelope identity;
        private readonly InitialSimulationCheckpointInstaller installer;
        private readonly Func<string> createHostArchive;
        private readonly HashSet<int> players;
        private readonly SimulationCheckpointTransfer.Assembler assembler;
        private readonly Dictionary<int, string> peers = new Dictionary<int, string>();
        private readonly HashSet<int> installedPeers = new HashSet<int>();
        private SimulationCheckpointEncodedTransfer hostTransfer;
        private bool hostAttempted, stopped;
        private string hostFault, clientToken;
        private SimulationCheckpointEnvelope installedEnvelope;
        private InitialSimulationCheckpointInstallResult installResult;
        public SimulationCheckpointChannel(SimulationCheckpointEnvelope identity, InitialSimulationCheckpointInstaller installer,
            Func<string> createHostArchive, IEnumerable<int> authorizedPlayerIds)
        {
            if (identity == null || !identity.HasValidIdentity() || (installer == null) == (createHostArchive == null))
                throw new ArgumentException("checkpoint_channel_configuration_invalid");
            if (!string.Equals(InitialSimulationCheckpointInstaller.ArchiveDigestVersion, SimulationCheckpointEnvelope.Utf8ArchiveDigestVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("checkpoint_archive_digest_version_mismatch");
            players = authorizedPlayerIds == null ? new HashSet<int>() : new HashSet<int>(authorizedPlayerIds);
            if (players.Count == 0 || players.Count > 256 || players.Any(player => player <= 0)) throw new ArgumentException("checkpoint_channel_players_invalid");
            this.identity = identity.Snapshot(); this.installer = installer; this.createHostArchive = createHostArchive;
            assembler = new SimulationCheckpointTransfer.Assembler(identity);
        }
        public SimulationCheckpointEnvelope Identity => identity.Snapshot();
        public bool IsInstalled => installedEnvelope != null;
        public bool IsStopped => stopped;
        public int BufferedBytes => assembler.BufferedBytes;
        public bool IsPeerInstalled(int playerId) => installedPeers.Contains(playerId);

        public bool TryPrepareHostArchive(out string faultCode)
        {
            faultCode = string.Empty;
            if (stopped || createHostArchive == null) { faultCode = "checkpoint_channel_stopped_or_wrong_role"; return false; }
            if (!hostAttempted)
            {
                hostAttempted = true;
                try { hostTransfer = SimulationCheckpointTransfer.Encode(identity, createHostArchive()); }
                catch (Exception exception) when (!(exception is OutOfMemoryException)) { hostFault = "checkpoint_host_archive_failed:" + exception.Message; }
            }
            if (hostTransfer == null) { faultCode = hostFault; return false; }
            return true;
        }

        public void BindClientConnection(string connectionToken)
        {
            if (stopped || installer == null || !ValidToken(connectionToken)) throw new ArgumentException("checkpoint_client_binding_invalid");
            clientToken = connectionToken; assembler.Reset();
        }

        public bool TryCreateHostTransfer(int verifiedPlayerId, string connectionToken, SimulationCheckpointEnvelope request,
            out SimulationCheckpointEncodedTransfer transfer, out string faultCode)
        {
            transfer = null; faultCode = string.Empty;
            if (stopped || createHostArchive == null) { faultCode = "checkpoint_channel_stopped_or_wrong_role"; return false; }
            if (!players.Contains(verifiedPlayerId) || !ValidToken(connectionToken) || request == null || !request.HasValidIdentity() ||
                !SimulationCheckpointEnvelope.SameIdentity(identity, request) || request.EncodedByteCount != 0 || request.DecodedByteCount != 0)
            { faultCode = "checkpoint_request_identity_invalid"; return false; }
            if (!TryPrepareHostArchive(out faultCode)) return false;
            if (!peers.TryGetValue(verifiedPlayerId, out var prior) || prior != connectionToken) installedPeers.Remove(verifiedPlayerId);
            peers[verifiedPlayerId] = connectionToken; transfer = hostTransfer; return true;
        }

        public SimulationCheckpointChannelResult ReceivePart(string verifiedConnectionToken, SimulationCheckpointEnvelope envelope, int offset, byte[] payload)
        {
            if (stopped) return Reply(SimulationCheckpointReceiveStatus.Stopped, "checkpoint_channel_stopped");
            if (installer == null || clientToken == null || verifiedConnectionToken != clientToken)
                return Reply(SimulationCheckpointReceiveStatus.Rejected, "checkpoint_host_binding_mismatch");
            if (installedEnvelope != null && !SimulationCheckpointEnvelope.SamePayload(installedEnvelope, envelope))
            { assembler.Reset(); return Reply(SimulationCheckpointReceiveStatus.Rejected, "checkpoint_installed_conflict"); }
            if (!assembler.TryAppend(envelope, offset, payload, out var json, out var fault)) return Reply(SimulationCheckpointReceiveStatus.Rejected, fault);
            if (json == null) return Reply(SimulationCheckpointReceiveStatus.Buffered, string.Empty);
            if (installedEnvelope != null) return Reply(SimulationCheckpointReceiveStatus.AlreadyInstalled, string.Empty);
            if (!installer.TryInstall(envelope.CheckpointId, json, out var result))
            {
                Stop();
                var rejection = Reply(SimulationCheckpointReceiveStatus.Rejected, result.FaultCode); rejection.InstallResult = result; return rejection;
            }
            if (result.ArchiveDigest != envelope.ArchiveDigest) throw new InvalidOperationException("checkpoint_installer_digest_mismatch");
            installedEnvelope = envelope.Snapshot(); installResult = result;
            return Reply(SimulationCheckpointReceiveStatus.Installed, string.Empty);
        }

        public bool TryConfirmInstalled(int verifiedPlayerId, string connectionToken, SimulationCheckpointEnvelope envelope,
            out bool alreadyConfirmed, out string faultCode)
        {
            alreadyConfirmed = false; faultCode = string.Empty;
            if (stopped || hostTransfer == null || !peers.TryGetValue(verifiedPlayerId, out var token) || token != connectionToken ||
                !SimulationCheckpointEnvelope.SamePayload(hostTransfer.Envelope, envelope))
            { faultCode = "checkpoint_installed_confirmation_invalid"; return false; }
            alreadyConfirmed = !installedPeers.Add(verifiedPlayerId); return true;
        }
        public void ResetReceive() => assembler.Reset();
        public void ForgetPeer(int playerId) { peers.Remove(playerId); installedPeers.Remove(playerId); }
        public void Stop()
        { stopped = true; assembler.Reset(); clientToken = null; hostTransfer = null; peers.Clear(); installedPeers.Clear(); }
        private SimulationCheckpointChannelResult Reply(SimulationCheckpointReceiveStatus status, string fault) =>
            new SimulationCheckpointChannelResult { Status = status, FaultCode = fault, Envelope = installedEnvelope?.Snapshot(), InstallResult = installResult };
        private static bool ValidToken(string token) => !string.IsNullOrWhiteSpace(token) && token.Length <= 64 && !token.Any(char.IsControl);
    }
}
