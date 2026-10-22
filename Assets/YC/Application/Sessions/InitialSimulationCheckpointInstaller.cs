using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public interface ISimulationCheckpointInstaller
    {
        bool TryInstall(string checkpointId, string archiveJson, out InitialSimulationCheckpointInstallResult result);
    }
    public sealed class InitialSimulationCheckpointInstallResult
    {
        internal InitialSimulationCheckpointInstallResult(bool succeeded, bool alreadyInstalled, string checkpointId,
            string archiveDigest, string faultCode, string diagnostic)
        {
            Succeeded = succeeded; AlreadyInstalled = alreadyInstalled;
            CheckpointId = checkpointId ?? string.Empty; ArchiveDigest = archiveDigest ?? string.Empty;
            FaultCode = faultCode ?? string.Empty; Diagnostic = diagnostic ?? string.Empty;
        }
        public bool Succeeded { get; }
        public bool AlreadyInstalled { get; }
        public string CheckpointId { get; }
        public string ArchiveDigest { get; }
        public string FaultCode { get; }
        public string Diagnostic { get; }
    }

    /// <summary>只为已接受START、未执行入口的客户端安装一次完整检查点；不是运行中恢复入口。</summary>
    public sealed class InitialSimulationCheckpointInstaller : ISimulationCheckpointInstaller
    {
        public const string ArchiveDigestVersion = "yc.initial-checkpoint.sha256-utf8.v1";
        private readonly GameSession session;
        private readonly EffectRegistry registry;
        private readonly GameStateViewer viewer;
        private readonly GameState initialState;
        private readonly bool validInitialState;
        private readonly object gate = new object();
        private string installedId, installedDigest;

        public InitialSimulationCheckpointInstaller(GameSession session, EffectRegistry registry, GameStateViewer viewer)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.viewer = viewer;
            initialState = GameStateCloneService.DeepClone(session.State);
            validInitialState = IsInitialSkeleton(initialState) &&
                (viewer.Role != GameStateViewerRole.Player || initialState.FindPlayer(viewer.PlayerId) != null);
        }

        public bool TryInstall(string checkpointId, string archiveJson, out InitialSimulationCheckpointInstallResult result)
        {
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(checkpointId) || checkpointId.Length > 128 || checkpointId.Any(char.IsControl))
                    return Fail(checkpointId, "", "checkpoint_id_invalid", "检查点ID无效。", out result);
                if (string.IsNullOrEmpty(archiveJson)) return Fail(checkpointId, "", "checkpoint_archive_missing", "检查点档案为空。", out result);
                string digest;
                try
                {
                    using (var sha = SHA256.Create())
                        digest = BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false, true).GetBytes(archiveJson))).Replace("-", "").ToLowerInvariant();
                }
                catch (EncoderFallbackException exception)
                { return Fail(checkpointId, "", "checkpoint_encoding_invalid", exception.Message, out result); }

                if (session.State.GameId != initialState.GameId || session.State.EventDeckSeed != initialState.EventDeckSeed)
                    return Fail(checkpointId, digest, "checkpoint_game_mismatch", "目标会话已切换对局。", out result);
                if (installedId != null)
                {
                    if (checkpointId != installedId || digest != installedDigest)
                        return Fail(checkpointId, digest, "checkpoint_conflict", "初始检查点已经安装，拒绝替换ID或载荷。", out result);
                    result = new InitialSimulationCheckpointInstallResult(true, true, checkpointId, digest, "", "");
                    return true;
                }
                if (!validInitialState || !GameStateCloneService.AreEquivalent(session.State, initialState))
                    return Fail(checkpointId, digest, "initial_session_changed", "目标必须保持已接受START、尚未执行规则的初始骨架。", out result);

                GameState prepared;
                try
                {
                    var recovery = HostRecoveryService.RecoverJson(archiveJson, registry);
                    if (!recovery.Succeeded)
                        return Fail(checkpointId, digest, recovery.FaultCode, recovery.Diagnostic, out result);
                    var restored = recovery.State;
                    if (restored == null || restored.GameId != initialState.GameId || restored.EventDeckSeed != initialState.EventDeckSeed ||
                        restored.MapId != initialState.MapId || restored.LaunchPlayerOrder == null ||
                        !restored.LaunchPlayerOrder.SequenceEqual(initialState.LaunchPlayerOrder))
                        return Fail(checkpointId, digest, "checkpoint_start_mismatch", "检查点与已接受的局身份、种子、地图或开局顺序不一致。", out result);
                    if (restored.ContentPackHash != initialState.ContentPackHash || restored.RuleConfiguration == null ||
                        JsonConvert.SerializeObject(restored.RuleConfiguration) != JsonConvert.SerializeObject(initialState.RuleConfiguration))
                        return Fail(checkpointId, digest, "checkpoint_configuration_mismatch", "检查点内容或规则版本与START不一致。", out result);
                    if (!GameplayRandomRuntime.IsContinuation(initialState.RandomRuntime, restored.RandomRuntime, initialState.EventDeckSeed))
                        return Fail(checkpointId, digest, "checkpoint_random_discontinuity", "检查点随机状态不能续接START游标。", out result);

                    // 在独立对象图上预检现有复制与真实Registry投影查询，错误不触碰目标会话。
                    prepared = GameStateCloneService.DeepClone(initialState);
                    GameStateCloneService.CopyTo(prepared, restored);
                    GameStateViewProjector.Project(prepared, viewer, viewer.Role == GameStateViewerRole.Player
                        ? registry.GetSettlementOptions(prepared, viewer.PlayerId) : null);
                }
                catch (Exception exception) when (!(exception is OutOfMemoryException))
                { return Fail(checkpointId, digest, "checkpoint_preparation_failed", exception.Message, out result); }

                session.ReplaceState(prepared);
                session.RefreshLocalProjection(viewer);
                installedId = checkpointId; installedDigest = digest;
                result = new InitialSimulationCheckpointInstallResult(true, false, checkpointId, digest, "", "");
                return true;
            }
        }

        internal static bool IsInitialSkeleton(GameState state)
        {
            var runtime = state?.EffectRuntime;
            return state != null && state.UseSeatTurnOrder && state.Phase == GamePhase.Entrance && state.Round == 0 &&
                state.RuleConfiguration != null && state.RuleConfiguration.TryValidate(out _) && !string.IsNullOrEmpty(state.ContentPackHash) &&
                state.LaunchPlayerOrder != null && TurnOrderService.IsValidPlayerOrder(state.LaunchPlayerOrder, state.Players.Select(player => player.PlayerId)) &&
                GameplayRandomRuntime.TryValidate(state.RandomRuntime, state.GameId, state.EventDeckSeed, out _) &&
                state.ItemZones != null && state.ItemZones.Items.Count == 0 && state.ItemZones.Zones.Count == 0 &&
                runtime != null && runtime.StateRevision == 0 && runtime.Journal.Count == 0 && runtime.RuleEvents.Count == 0 &&
                runtime.EffectNodes.Count == 0 && runtime.InteractionRequests.Count == 0 && runtime.DispatchReceipts.Count == 0 &&
                state.PendingChoice == null && state.PendingCardSession == null && state.PendingCharacterEffect == null && state.PendingSpecialAction == null;
        }
        private static bool Fail(string id, string digest, string code, string diagnostic, out InitialSimulationCheckpointInstallResult result)
        {
            result = new InitialSimulationCheckpointInstallResult(false, false, id, digest, code, diagnostic);
            return false;
        }
    }
}
