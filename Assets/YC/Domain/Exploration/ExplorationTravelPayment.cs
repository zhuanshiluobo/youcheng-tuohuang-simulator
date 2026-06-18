using System;

namespace YC.Domain.Exploration
{
    [Serializable]
    public sealed class ExplorationTravelPayment
    {
        public string RouteId = string.Empty;
        public int Amount;
        public int ReceiverPlayerId = -1;

        public bool PaidToSupply
        {
            get { return ReceiverPlayerId < 0; }
        }
    }
}
