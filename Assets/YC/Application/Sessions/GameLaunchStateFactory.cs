using System;
using System.Collections.Generic;
using YC.Domain.CityStyles;
using YC.Domain.Cards;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public static class GameLaunchStateFactory
    {
        public static bool IsNetworkLaunch(LaunchMode mode)
        {
            return mode != LaunchMode.Local;
        }

        public static bool ContainsPlayer(IList<PlayerSeat> players, int playerId)
        {
            if (players == null || playerId <= 0)
            {
                return false;
            }

            for (var i = 0; i < players.Count; i++)
            {
                if (players[i].PlayerId == playerId)
                {
                    return true;
                }
            }

            return false;
        }

        public static int ResolveHostLocalPlayerId(
            int localPlayerId,
            int hostPlayerId,
            bool isHost,
            IList<PlayerSeat> players)
        {
            if (ContainsPlayer(players, localPlayerId))
            {
                return localPlayerId;
            }

            if (isHost && ContainsPlayer(players, hostPlayerId))
            {
                return hostPlayerId;
            }

            return localPlayerId;
        }

        public static GameState CreateInitialState(
            LaunchMode mode,
            int localPlayerId,
            IList<PlayerSeat> players,
            string mapId,
            int eventDeckSeed)
        {
            return CreateInitialState(mode, localPlayerId, players, new GameRuleConfiguration
            {
                MapId = mapId, PlayerCount = players == null || players.Count == 0 ? 1 : players.Count
            }, eventDeckSeed);
        }

        public static GameState CreateInitialState(
            LaunchMode mode, int localPlayerId, IList<PlayerSeat> players,
            GameRuleConfiguration configuration, int eventDeckSeed)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (!configuration.TryValidate(out var reason)) throw new InvalidOperationException(reason);
            return CreateInitialState(mode, localPlayerId, players, configuration, eventDeckSeed,
                GameSetupRecipe.CreateRegisteredStandard(configuration.MapId));
        }

        public static GameState CreateInitialState(
            LaunchMode mode, int localPlayerId, IList<PlayerSeat> players,
            GameRuleConfiguration configuration, int eventDeckSeed, GameSetupRecipe recipe, GameContentDefinitions definitions = null)
            => CreateInitialState(mode, localPlayerId, players, configuration, eventDeckSeed, recipe, definitions, null);

        public static GameState CreateInitialState(
            LaunchMode mode, int localPlayerId, IList<PlayerSeat> players,
            GameRuleConfiguration configuration, int eventDeckSeed, GameSetupRecipe recipe,
            GameContentDefinitions definitions, IReadOnlyList<int> launchPlayerOrder)
            => CreateInitialState(mode, localPlayerId, players, configuration, eventDeckSeed, recipe, definitions,
                launchPlayerOrder, Guid.NewGuid().ToString("N"), null);

        public static GameState CreateInitialState(LaunchMode mode, int localPlayerId, IList<PlayerSeat> players,
            GameRuleConfiguration configuration, int eventDeckSeed, GameSetupRecipe recipe,
            GameContentDefinitions definitions, IReadOnlyList<int> launchPlayerOrder, string gameId, GameplayRandomState initialRandomState)
        {
            if (string.IsNullOrWhiteSpace(gameId)) throw new InvalidOperationException("random_game_id_missing");
            if (initialRandomState == null)
            {
                if (mode == LaunchMode.Client) throw new InvalidOperationException("random_initialization_required");
                initialRandomState = GameplayRandomRuntime.Create(gameId);
            }
            if (!GameplayRandomRuntime.TryValidate(initialRandomState, gameId, eventDeckSeed, out var randomReason))
                throw new InvalidOperationException(randomReason);
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (!configuration.TryValidate(out var reason)) throw new InvalidOperationException(reason);
            var actualCount = players == null || players.Count == 0 ? 1 : players.Count;
            if (configuration.PlayerCount != actualCount) throw new InvalidOperationException("unsupported_player_count");
            if (recipe == null || configuration.EnabledExpansionIds.Contains("infected") && recipe.RecipeId != "infected" ||
                configuration.EnabledExpansionIds.Contains("enterprise") && !recipe.EnterpriseEntrance)
                throw new InvalidOperationException("content_not_ready");
            var identities = new HashSet<int>();
            if (players != null)
                foreach (var player in players)
                    if (player == null || player.PlayerId <= 0 || !identities.Add(player.PlayerId))
                        throw new InvalidOperationException("room_protocol_mismatch");
            var startPlayerId = GetStartPlayerId(mode, players);
            var state = new GameState
            {
                GameId = gameId,
                RandomRuntime = initialRandomState.Clone(),
                UseSeatTurnOrder = IsNetworkLaunch(mode),
                MapId = configuration.MapId,
                RuleConfiguration = configuration.Clone(),
                ContentPackHash = configuration.ContentPackHash,
                EventDeckSeed = eventDeckSeed
            };
            state.ContentDefinitions = definitions == null ? GameContentDefinitions.CreateRegisteredStandard() : GameStateCloneService.DeepClone(definitions);

            AddPlayers(state, localPlayerId, players);
            if (launchPlayerOrder != null && !TurnOrderService.IsValidPlayerOrder(launchPlayerOrder, state.Players.ConvertAll(player => player.PlayerId)))
                throw new InvalidOperationException("launch_player_order_invalid");
            state.LaunchPlayerOrder = launchPlayerOrder == null
                ? new List<int>(new TurnOrderService().GetLaunchPlayerOrder(state)) : new List<int>(launchPlayerOrder);
            if (launchPlayerOrder != null) startPlayerId = state.LaunchPlayerOrder[0];
            new RoundExecutionProjector().ProjectEntrance(state, startPlayerId, startPlayerId);
            // 正式门禁读取本次活动配方；尚未装配不能拿默认基础配方判断扩展入口。
            state.SetupRecipe = GameStateCloneService.DeepClone(recipe);
            RuleModeReadiness.ValidateReady(state);
            if (mode == LaunchMode.Client) return state;
            return recipe.Assemble(state, YC.Domain.Maps.StaticMapDefinitions.Resolve(configuration.MapId), eventDeckSeed);
        }

        private static int GetStartPlayerId(LaunchMode mode, IList<PlayerSeat> players)
        {
            if (players != null && players.Count > 0)
            {
                return players[0].PlayerId;
            }

            return 1;
        }

        private static void AddPlayers(GameState state, int localPlayerId, IList<PlayerSeat> players)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (players != null && players.Count > 0)
            {
                for (var i = 0; i < players.Count; i++)
                {
                    var seat = players[i];
                    state.Players.Add(new PlayerState
                    {
                        PlayerId = seat.PlayerId,
                        Name = string.IsNullOrEmpty(seat.PlayerName) ? "玩家 " + seat.PlayerId : seat.PlayerName,
                        Color = seat.Color
                    });
                }

                return;
            }

            state.Players.Add(new PlayerState
            {
                PlayerId = localPlayerId,
                Name = "Player " + localPlayerId,
                Color = YC.Domain.Rules.PlayerColor.Blue
            });
        }
    }
}
