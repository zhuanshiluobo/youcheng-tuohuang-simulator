namespace YC.Infrastructure.Multiplayer
{
    // Unity 2022 即使拒绝退出仍发送此消息；由 MirrorNetworkRuntime 在 Application.quitting 统一清理。
    public sealed class QuitDeferredNetworkManager : Mirror.NetworkManager
    {
        public override void OnApplicationQuit() { }
    }
}
