namespace YC.Domain.Influence
{
    public sealed class InfluenceMoveRequest
    {
        public InfluenceMoveRequest(string sourceSlotId, string targetSlotId)
        {
            SourceSlotId = sourceSlotId ?? string.Empty;
            TargetSlotId = targetSlotId ?? string.Empty;
        }

        public string SourceSlotId { get; private set; }
        public string TargetSlotId { get; private set; }
    }
}
