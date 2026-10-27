namespace YC.Domain.State
{
    /// <summary>同一正式构建的持久化和房间边界；局部原语协议另由各执行器维护。</summary>
    public static class GameProtocolVersions
    {
        public const int Host = 5;
        public const int Journal = 5;
        public const int Runtime = 4;
        public const int Entity = 2;
        public const int Room = 6;
        public const string Identity = "yc.game-protocol.5.5.4.2.6";
    }
}
