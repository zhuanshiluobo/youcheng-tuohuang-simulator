using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    [Serializable]
    public sealed class GameplayRandomStreamState
    {
        public string StreamId = string.Empty;
        public ulong State, WordsConsumed, RequestsConsumed;
    }

    [Serializable]
    public sealed class GameplayRandomState
    {
        public string AlgorithmVersion = GameplayRandomRuntime.AlgorithmVersion;
        public string GameId = string.Empty;
        public List<GameplayRandomStreamState> Streams = new List<GameplayRandomStreamState>();
        public GameplayRandomState Clone()
        {
            var copy = new GameplayRandomState { AlgorithmVersion = AlgorithmVersion, GameId = GameId };
            if (Streams == null) { copy.Streams = null; return copy; }
            foreach (var stream in Streams) copy.Streams.Add(stream == null ? null : new GameplayRandomStreamState
            { StreamId = stream.StreamId, State = stream.State, WordsConsumed = stream.WordsConsumed, RequestsConsumed = stream.RequestsConsumed });
            return copy;
        }
    }

    /// <summary>整数运算、用途流和游标属于规则工作状态；失败工作副本不会消耗已提交游标。</summary>
    public static class GameplayRandomRuntime
    {
        public const string AlgorithmVersion = "yc.splitmix64-fnv1a-utf16.v1";
        public const string LaunchOrder = "launch.order.v1";
        public const string SetupDecks = "setup.decks.v1";
        public const string SetupStyles = "setup.styles.v1";
        public const string EnterprisePreparation = "enterprise.prepare.v1";
        public const string DeckShuffle = "effects.deck-shuffle.v1";
        public const string Dice = "effects.dice.v1";
        public const string EventInitialization = "events.initialize.v1";
        private const ulong Increment = 0x9E3779B97F4A7C15UL;
        public static GameplayRandomState Create(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId)) throw new InvalidOperationException("random_game_id_missing");
            return new GameplayRandomState { GameId = gameId };
        }
        // 仅显式 seed 的复现接口使用；正式入口采用已冻结的局事实。
        public static GameState CreateExplicitSeedState(int seed) => new GameState
        { GameId = "explicit-seed", EventDeckSeed = seed, RandomRuntime = Create("explicit-seed") };

        public static bool TryValidate(GameplayRandomState runtime, string gameId, int seed, out string reason)
        {
            reason = "random_state_invalid";
            if (runtime == null || runtime.AlgorithmVersion != AlgorithmVersion || string.IsNullOrWhiteSpace(gameId) ||
                runtime.GameId != gameId || runtime.Streams == null) return false;
            string previous = null;
            foreach (var stream in runtime.Streams)
            {
                if (stream == null || string.IsNullOrWhiteSpace(stream.StreamId) || stream.StreamId.Length > 128 ||
                    previous != null && StringComparer.Ordinal.Compare(previous, stream.StreamId) >= 0 ||
                    stream.RequestsConsumed > stream.WordsConsumed ||
                    stream.State != unchecked(InitialState(seed, stream.StreamId) + Increment * stream.WordsConsumed)) return false;
                previous = stream.StreamId;
            }
            reason = string.Empty;
            return true;
        }

        public static int Next(GameState state, string streamId, int maxExclusive)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return Next(state.RandomRuntime, state.GameId, state.EventDeckSeed, streamId, maxExclusive);
        }
        public static bool IsContinuation(GameplayRandomState previous, GameplayRandomState current, int seed)
        {
            if (previous == null || current == null || !TryValidate(previous, previous.GameId, seed, out _) ||
                !TryValidate(current, previous.GameId, seed, out _)) return false;
            foreach (var old in previous.Streams)
            {
                var next = current.Streams.Find(stream => stream.StreamId == old.StreamId);
                if (next == null || next.WordsConsumed < old.WordsConsumed || next.RequestsConsumed < old.RequestsConsumed) return false;
            }
            return true;
        }
        public static int Next(GameplayRandomState runtime, string gameId, int seed, string streamId, int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            if (string.IsNullOrWhiteSpace(streamId) || streamId.Length > 128) throw new ArgumentException("random_stream_invalid", nameof(streamId));
            if (!TryValidate(runtime, gameId, seed, out var reason)) throw new InvalidOperationException(reason);
            var stream = runtime.Streams.Find(value => value.StreamId == streamId);
            if (stream == null)
            {
                stream = new GameplayRandomStreamState { StreamId = streamId, State = InitialState(seed, streamId) };
                runtime.Streams.Add(stream);
                runtime.Streams.Sort((left, right) => StringComparer.Ordinal.Compare(left.StreamId, right.StreamId));
            }
            if (stream.RequestsConsumed == ulong.MaxValue) throw new InvalidOperationException("random_cursor_exhausted");
            ulong bound = (uint)maxExclusive, threshold = unchecked(0UL - (uint)maxExclusive) % (uint)maxExclusive;
            ulong value;
            do { value = NextWord(stream); } while (value < threshold);
            stream.RequestsConsumed++;
            return (int)(value % bound);
        }
        public static void Shuffle<T>(IList<T> values, GameState state, string streamId)
        {
            for (int index = values.Count - 1; index > 0; index--)
            {
                int other = Next(state, streamId, index + 1);
                T value = values[index]; values[index] = values[other]; values[other] = value;
            }
        }
        private static ulong InitialState(int seed, string streamId)
        {
            unchecked
            {
                // FNV-1a 对 UTF-16 的两个小端字节扩展；不使用运行时字符串 hash。
                ulong hash = 14695981039346656037UL;
                foreach (char value in streamId)
                { hash = (hash ^ (byte)value) * 1099511628211UL; hash = (hash ^ (byte)(value >> 8)) * 1099511628211UL; }
                return hash ^ (uint)seed;
            }
        }
        private static ulong NextWord(GameplayRandomStreamState stream)
        {
            if (stream.WordsConsumed == ulong.MaxValue) throw new InvalidOperationException("random_cursor_exhausted");
            unchecked
            {
                stream.State += Increment;
                stream.WordsConsumed++;
                ulong value = stream.State;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
