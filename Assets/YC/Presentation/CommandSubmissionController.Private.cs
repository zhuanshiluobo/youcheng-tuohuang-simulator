using System;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using UnityEngine;

namespace YC.Presentation
{
    internal sealed partial class CommandSubmissionController
    {
        private bool usesPrivateTransport;
        private GameContentDefinitions privateContentDefinitions;
        private string privateContentHash;
        private bool PrivateBindingIsCurrent => !disposed && !simulationStopped && UsesPrivateTransport && OwnsSimulationTransport &&
            simulationTransport.UsesPrivateTransport && launchContext != null && !launchContext.IsLeavingGameplay &&
            launchContext.GameId == simulationGameId && launchContext.LocalPlayerId == localPlayerId &&
            launchContext.Mode == simulationLaunchMode && GameSessionBootstrapper.IsNetworkLaunch(launchContext);

        // 加载时可以先创建界面，再收到并安装本人视图；不能用房主权威状态替代空的玩家投影。
        internal bool AwaitingInitialPrivateProjection => PrivateBindingIsCurrent && simulationProjectionView == null &&
            simulationProjectionState == null && string.IsNullOrEmpty(simulationTransport.PrivateFault);

        private void InitializePrivate(EffectRegistry registry)
        {
            usesPrivateTransport = true;
            try
            {
                var resume = launchContext.ResumeInfo;
                if (resume != null && !resume.IsPrivate || launchContext.Mode == LaunchMode.Host && launchContext.RestoreData != null &&
                    (resume == null || !resume.MatchesPrivateSave(launchContext.RestoreData)))
                    throw new InvalidOperationException("private_resume_identity_mismatch");
                simulationGameId = session.State.GameId; simulationEpoch = resume?.SimulationEpoch ?? 1;
                simulationMembershipVersion = resume?.MembershipVersion ?? 1; simulationLaunchMode = launchContext.Mode;
                simulationRegistry = registry;
                privateContentHash = session.State.ContentPackHash;
                privateContentDefinitions = GameStateCloneService.DeepClone(session.State.ContentDefinitions);
                if (privateContentDefinitions == null || registry.ContentPackHash != privateContentHash)
                    throw new InvalidOperationException("private_content_definitions_missing");
                simulationProjectionState = null; simulationProjectionView = null;
                launchContext.OnlineSessionNotice += OnOnlineSessionNotice; sessionNoticeSubscribed = true;
                if (launchContext.TryConsumeOnlineSessionNotice(out var notice)) setPrompt(notice);
                var transport = MirrorCommandTransport.Ensure();
                simulationTransport = transport; commandTransport = transport; SimulationTransportOwners[transport] = this;
                transport.PaymentSourcePageAvailable += OnPaymentSourcePageAvailable;
                transport.RegionControlPreviewAvailable += OnPaymentSourcePageAvailable;
                transport.PrivateStatusChanged += OnPrivateStatusChanged;
                transport.PrivateCommandCompleted += OnPrivateCommandCompleted;
                transport.PrivateTransportFaulted += OnPrivateTransportFaulted;
                transport.InitializePrivateTransport(session, registry, launchContext.Mode, localPlayerId, launchContext.Players,
                    simulationEpoch, simulationMembershipVersion, InstallPrivateView);
            }
            catch (Exception error)
            {
                InitializationFailure = error.Message; Dispose(); Debug.LogException(error, logContext);
                setPrompt("联网同步初始化失败，请返回房间重试。");
            }
        }

        private bool InstallPrivateView(GameStateView view)
        {
            if (!PrivateBindingIsCurrent || view == null) return false;
            var projected = PlayerUiReadStateFactory.CreateFromPrivateView(view, privateContentDefinitions,
                localPlayerId, simulationGameId, privateContentHash);
            if (projected == null) return false;
            simulationProjectionState = projected; simulationProjectionView = view;
            refreshFromState?.Invoke();
            return true;
        }

        private void OnPrivateStatusChanged()
        { if (PrivateBindingIsCurrent) refreshFromState?.Invoke(); }

        private void OnPrivateCommandCompleted(PrivateCommandResult result)
        {
            if (!PrivateBindingIsCurrent || result == null || result.GameId != simulationGameId || result.PlayerId != localPlayerId ||
                result.SessionEpoch != simulationEpoch || result.MembershipVersion != simulationMembershipVersion) return;
            bool accepted = result.Status == PrivateCommandStatus.Committed || result.Status == PrivateCommandStatus.Duplicate;
            if (ResolveSubmitted(result.CommandId, result.PlayerId, result.GameId, accepted, result.ErrorCode, !accepted))
            {
                cityStyleCommandResolved?.Invoke(result.CommandId, accepted);
                commandSettled?.Invoke(result.CommandId);
            }
            refreshFromState?.Invoke();
            if (!accepted) setPrompt(string.IsNullOrEmpty(result.Reason) || result.Reason.StartsWith("private_", StringComparison.Ordinal)
                ? "当前操作状态已变化，请重新选择。" : result.Reason);
        }

        private void OnPrivateTransportFaulted(string reason)
        {
            if (!PrivateBindingIsCurrent) return;
            // 未收到准确结果时不能把可能已提交的命令标成失败或换 ID 重发。
            simulationStopped = true;
            refreshFromState?.Invoke();
            setPrompt("联机同步发生故障，请返回房间处理。");
        }

        private void UnbindPrivateSubscriptions()
        {
            if (ReferenceEquals(simulationTransport, null)) return;
            simulationTransport.PrivateStatusChanged -= OnPrivateStatusChanged;
            simulationTransport.PrivateCommandCompleted -= OnPrivateCommandCompleted;
            simulationTransport.PrivateTransportFaulted -= OnPrivateTransportFaulted;
            privateContentDefinitions = null; privateContentHash = null;
        }
    }
}
