namespace YC.Domain.Effects
{
    // API-34 / 4.14.1：仅保留旧类型诊断；实际基础操作继续自动创建InfluenceDepartureBatch状态。
    public static class RetiredDepartureBatchContract
    {
        public const string TypeId = "effect.influence.departure_batch";
        public const string Reason = "departure_batch_api_retired";
    }
}
