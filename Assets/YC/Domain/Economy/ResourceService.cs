using System;
using YC.Domain.State;

namespace YC.Domain.Economy
{
    public sealed class ResourceService
    {
        public bool TryPayToSupply(PlayerState player, ResourceSet cost)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (cost == null) throw new ArgumentNullException(nameof(cost));
            return player.Resources.TryPay(cost);
        }

        public bool TryPayToPlayer(PlayerState payer, PlayerState receiver, ResourceSet cost)
        {
            if (payer == null) throw new ArgumentNullException(nameof(payer));
            if (receiver == null) throw new ArgumentNullException(nameof(receiver));
            if (cost == null) throw new ArgumentNullException(nameof(cost));

            if (!payer.Resources.TryPay(cost)) return false;
            receiver.Resources.Add(cost);
            return true;
        }

        public void GrantFromSupply(PlayerState player, ResourceSet reward)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (reward == null) throw new ArgumentNullException(nameof(reward));
            player.Resources.Add(reward);
        }

        public void AddScore(PlayerState player, int points)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (points < 0) throw new ArgumentOutOfRangeException(nameof(points));
            player.Score += points;
        }
    }
}
