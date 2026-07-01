using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public static class RedZoneAccessRule
    {
        public static bool IsClosed(GameState state, GameMapDefinition map, MapLocationDefinition location)
        {
            if (location == null || !location.IsRedZone)
            {
                return false;
            }

            return state.Round < GetOpenRound(map, state.Players.Count);
        }

        public static int GetOpenRound(GameMapDefinition map, int playerCount)
        {
            var rulesPlayerCount = ResolveRulesPlayerCount(map, playerCount);
            if (rulesPlayerCount <= 2)
            {
                return 6;
            }

            return rulesPlayerCount == 3 ? 5 : 4;
        }

        private static int ResolveRulesPlayerCount(GameMapDefinition map, int playerCount)
        {
            if (map == null)
            {
                return playerCount;
            }

            if (map.MinPlayers > 0 && playerCount > 0 && playerCount < map.MinPlayers)
            {
                return map.MinPlayers;
            }

            if (map.MaxPlayers > 0 && playerCount > map.MaxPlayers)
            {
                return map.MaxPlayers;
            }

            if (playerCount > 0)
            {
                return playerCount;
            }

            return map.MaxPlayers > 0 ? map.MaxPlayers : map.MinPlayers;
        }
    }
}
