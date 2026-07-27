using System;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    /// <summary>
    /// Owns the single hotseat rule for deciding which player is controlled locally.
    /// Networked contexts keep their configured local player unchanged.
    /// </summary>
    public sealed class LocalPlayerResolver
    {
        private readonly TurnOrderService turnOrderService = new TurnOrderService();

        public int Resolve(
            GameState state,
            int configuredLocalPlayerId,
            bool controlsCurrentPlayerLocally)
        {
            if (!controlsCurrentPlayerLocally || state == null || state.CurrentPlayerId <= 0)
            {
                return configuredLocalPlayerId;
            }

            if (state.Phase != GamePhase.ResourceCollection)
            {
                return state.CurrentPlayerId;
            }

            var order = turnOrderService.GetTurnOrder(state);
            for (var i = 0; i < order.Count; i++)
            {
                var player = state.FindPlayer(order[i]);
                if (player != null && !player.HasCollectedResourcesThisRound)
                {
                    return player.PlayerId;
                }
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player != null && !player.HasCollectedResourcesThisRound)
                {
                    return player.PlayerId;
                }
            }

            return configuredLocalPlayerId;
        }

        public int ResolveAndApply(IWritableGameplayContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var resolvedPlayerId = Resolve(
                context.CurrentState,
                context.LocalPlayerId,
                context.ControlsCurrentPlayerLocally);
            if (resolvedPlayerId > 0 && resolvedPlayerId != context.LocalPlayerId)
            {
                context.SetLocalPlayerId(resolvedPlayerId);
            }

            return resolvedPlayerId;
        }
    }
}
