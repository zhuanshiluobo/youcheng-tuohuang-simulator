namespace YC.Infrastructure.Multiplayer
{
    public static class SteamIdentityAddress
    {
        public static bool TryParse(string address, out ulong steamId)
        {
            steamId = 0;
            if (string.IsNullOrWhiteSpace(address)) return false;
            var value = address.Trim();
            if (value.StartsWith("steam://", System.StringComparison.OrdinalIgnoreCase))
                value = value.Substring("steam://".Length);
            else if (value.StartsWith("steam:", System.StringComparison.OrdinalIgnoreCase))
                value = value.Substring("steam:".Length);
            else if (value.IndexOf(':') >= 0 || value.IndexOf('/') >= 0)
                return false;
            return ulong.TryParse(value, out steamId) && steamId > 0;
        }
    }
}
