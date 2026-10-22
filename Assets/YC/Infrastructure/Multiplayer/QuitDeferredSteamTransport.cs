namespace YC.Infrastructure.Multiplayer
{
    // 与本地传输一致，避免可取消的退出消息提前释放 Steam 连接。
    public sealed class QuitDeferredSteamTransport : Mirror.FizzySteam.FizzySteamworks
    {
        public override void OnApplicationQuit() { }
    }
}
