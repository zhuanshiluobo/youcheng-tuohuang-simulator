using System;

namespace YC.Infrastructure.Multiplayer
{
    public static class LocalMirrorIdentity
    {
        private const ulong SimulatedIdentityBase = 90000000000000000UL;

        public static ulong ForPlayer(int playerId)
        {
            if (playerId < 1)
                throw new ArgumentOutOfRangeException(nameof(playerId));

            return SimulatedIdentityBase + (ulong)playerId;
        }
    }
}
