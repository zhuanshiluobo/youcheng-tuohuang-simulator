using System;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Infrastructure.Multiplayer;
using UnityEngine;

namespace YC.Presentation
{
    internal sealed class CommandSubmissionController : IDisposable
    {
        private readonly GameSession session;
        private readonly GameLaunchContext launchContext;
        private readonly int localPlayerId;
        private readonly UnityEngine.Object logContext;
        private readonly Action refreshFromState;
        private readonly Action<string> setPrompt;
        private INetworkCommandTransport commandTransport;
        private bool sessionNoticeSubscribed;

        public CommandSubmissionController(
            GameSession session,
            GameLaunchContext launchContext,
            int localPlayerId,
            UnityEngine.Object logContext,
            Action refreshFromState,
            Action<string> setPrompt)
        {
            this.session = session;
            this.launchContext = launchContext;
            this.localPlayerId = localPlayerId;
            this.logContext = logContext;
            this.refreshFromState = refreshFromState;
            this.setPrompt = setPrompt;
        }

        public void Initialize()
        {
            if (launchContext == null || launchContext.Mode == LaunchMode.Local)
            {
                return;
            }

            launchContext.OnlineSessionNotice -= OnOnlineSessionNotice;
            launchContext.OnlineSessionNotice += OnOnlineSessionNotice;
            sessionNoticeSubscribed = true;
            if (launchContext.TryConsumeOnlineSessionNotice(out var notice))
                setPrompt(notice);

            try
            {
                commandTransport = NetworkCommandTransportProvider.Ensure();
                commandTransport.InitialStateApplied += OnInitialNetworkStateApplied;
                commandTransport.ConfirmedCommandApplied += OnConfirmedNetworkCommandApplied;
                commandTransport.CommandRejected += OnNetworkCommandRejected;
                commandTransport.Initialize(session, launchContext.Mode, localPlayerId, launchContext.Players);
            }
            catch (Exception ex)
            {
                Dispose();
                Debug.LogException(ex, logContext);
                setPrompt("联网同步初始化失败，请返回房间重试。");
            }
        }

        public CommandResult Submit(GameCommand command, out bool appliedLocally)
        {
            if (commandTransport != null)
            {
                return commandTransport.SubmitOrSend(command, out appliedLocally);
            }

            if (GameSessionBootstrapper.IsNetworkLaunch(launchContext))
            {
                appliedLocally = false;
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "\u8054\u7f51\u540c\u6b65\u672a\u521d\u59cb\u5316"));
            }

            var result = session.Submit(command);
            appliedLocally = result.Succeeded;
            return result;
        }

        public void Dispose()
        {
            if (sessionNoticeSubscribed && launchContext != null)
            {
                launchContext.OnlineSessionNotice -= OnOnlineSessionNotice;
                sessionNoticeSubscribed = false;
            }

            if (commandTransport == null)
            {
                return;
            }

            commandTransport.InitialStateApplied -= OnInitialNetworkStateApplied;
            commandTransport.ConfirmedCommandApplied -= OnConfirmedNetworkCommandApplied;
            commandTransport.CommandRejected -= OnNetworkCommandRejected;
            commandTransport.Shutdown();
            commandTransport = null;
        }

        private void OnOnlineSessionNotice(string message)
        {
            launchContext?.TryConsumeOnlineSessionNotice(out _);
            setPrompt(message);
        }

        private void OnConfirmedNetworkCommandApplied(ConfirmedGameCommandDto confirmed)
        {
            refreshFromState();
        }

        private void OnInitialNetworkStateApplied(InitialGameStateDto snapshot)
        {
            refreshFromState();
        }

        private void OnNetworkCommandRejected(RejectedGameCommandDto rejected)
        {
            setPrompt(NetworkCommandPromptFormatter.BuildRejectedCommandPrompt(rejected));
        }
    }
}
