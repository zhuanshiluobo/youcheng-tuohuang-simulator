namespace YC.Infrastructure.Multiplayer
{
    public static class OnlineRoomServiceProvider
    {
        private static IOnlineRoomService active;
        private static IRoomContentCapabilityProvider contentCapabilities;
        public static IRoomContentCapabilityProvider ContentCapabilities =>
            contentCapabilities ?? (contentCapabilities = new BasicRoomContentCapabilityProvider());

        public static IOnlineRoomService GetOrCreate()
        {
            var wantsLocal = LocalMirrorTestMode.IsEnabled;
            if (active != null && (active is LocalMirrorRoomService) == wantsLocal) return active;

            active?.Dispose();
            active = wantsLocal
                ? (IOnlineRoomService)new LocalMirrorRoomService()
                : new RoomSessionService(ContentCapabilities, () => new SteamRoomService());
            return active;
        }

        public static IOnlineRoomService GetActive() => active;

        public static void ShutdownActive() => active?.Shutdown();

        public static void DisposeActive()
        {
            active?.Dispose();
            active = null;
        }
    }
}
