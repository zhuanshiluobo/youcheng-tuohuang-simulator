using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public sealed class TurnOrderService
    {
        public IReadOnlyList<int> GetTurnOrder(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var order = new List<int>();
            if (state.Players.Count == 0)
            {
                return order;
            }

            var startIndex = 0;
            for (var i = 0; i < state.Players.Count; i++)
            {
                if (state.Players[i].PlayerId == state.StartPlayerId)
                {
                    startIndex = i;
                    break;
                }
            }

            for (var offset = 0; offset < state.Players.Count; offset++)
            {
                var index = (startIndex + offset) % state.Players.Count;
                order.Add(state.Players[index].PlayerId);
            }

            return order;
        }
    }
}
