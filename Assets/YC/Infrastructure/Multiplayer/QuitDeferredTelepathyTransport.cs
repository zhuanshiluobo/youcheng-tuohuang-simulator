namespace YC.Infrastructure.Multiplayer
{
    // 保留保存期间的连接，实际退出时由 MirrorNetworkRuntime 关闭传输。
    public sealed class QuitDeferredTelepathyTransport : Mirror.TelepathyTransport
    {
        public override void OnApplicationQuit() { }
    }
}
