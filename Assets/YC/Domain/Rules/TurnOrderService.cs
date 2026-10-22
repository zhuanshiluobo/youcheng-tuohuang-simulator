using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public sealed class TurnOrderService
    {
        public IReadOnlyList<int> GetLaunchPlayerOrder(GameState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var players = state.Players.Select(player => player.PlayerId).ToList();
            if (state.LaunchPlayerOrder != null && state.LaunchPlayerOrder.Count > 0)
                return CopyValidatedOrder(state.LaunchPlayerOrder, players);
            if (state.UseSeatTurnOrder) players.Sort();
            return players.AsReadOnly();
        }

        public IReadOnlyList<int> GetLaunchPlayerOrder(GameStateView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            var players = view.Players.Select(player => player.PlayerId).ToList();
            return view.LaunchPlayerOrder != null && view.LaunchPlayerOrder.Count > 0
                ? CopyValidatedOrder(view.LaunchPlayerOrder, players) : players.AsReadOnly();
        }

        public static bool IsValidPlayerOrder(IReadOnlyList<int> order, IEnumerable<int> playerIds)
        {
            if (order == null || playerIds == null) return false;
            var players = playerIds.ToList();
            return order.Count == players.Count && players.Distinct().Count() == players.Count &&
                order.Distinct().Count() == order.Count && new HashSet<int>(players).SetEquals(order);
        }

        private static IReadOnlyList<int> CopyValidatedOrder(List<int> order, List<int> players)
        {
            if (!IsValidPlayerOrder(order, players)) throw new InvalidOperationException("launch_player_order_invalid");
            return new List<int>(order).AsReadOnly();
        }

        public IReadOnlyList<int> GetTurnOrder(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return GetTurnOrder(state, state.StartPlayerId);
        }

        public IReadOnlyList<int> GetTurnOrder(GameState state, int startPlayerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return RotateOrder(new List<int>(GetLaunchPlayerOrder(state)), startPlayerId).AsReadOnly();
        }

        private static List<int> RotateOrder(List<int> order, int startPlayerId)
        {
            var startIndex = 0;
            for (var i = 0; i < order.Count; i++)
            {
                if (order[i] == startPlayerId)
                {
                    startIndex = i;
                    break;
                }
            }

            var rotated = new List<int>();
            for (var offset = 0; offset < order.Count; offset++)
            {
                rotated.Add(order[(startIndex + offset) % order.Count]);
            }

            return rotated;
        }
    }
}
