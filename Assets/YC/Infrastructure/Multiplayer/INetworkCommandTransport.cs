using YC.Domain.Commands;

namespace YC.Infrastructure.Multiplayer
{
    public interface INetworkCommandTransport
    {
        CommandResult SubmitOrSend(GameCommand command, out bool appliedLocally);
        void Shutdown();
    }
}
