using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;

namespace YC.Infrastructure.Multiplayer
{
    public interface INetworkCommandTransport
    {
        event Action<ConfirmedGameCommandDto> ConfirmedCommandApplied;
        event Action<InitialGameStateDto> InitialStateApplied;
        event Action<RejectedGameCommandDto> CommandRejected;
        void Initialize(GameSession session, LaunchMode launchMode, int playerId, IList<PlayerSeat> seats);
        CommandResult SubmitOrSend(GameCommand command, out bool appliedLocally);
        void Shutdown();
    }
}
