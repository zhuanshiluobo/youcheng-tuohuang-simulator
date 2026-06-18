using System;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    [Serializable]
    public sealed class InitialGameStateDto
    {
        public int NextConfirmedSequence = 1;
        public GameState State = new GameState();
    }
}
