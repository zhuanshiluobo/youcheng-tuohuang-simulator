using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable]
    public sealed class StateViewHistoryDelta
    {
        public bool Reset;
        public int BaseEventSequence;
        public int BaseLogSequence;
        public int EventSequence;
        public int LogSequence;
    }

    /// <summary>
    /// 只跟踪已经按权限投影的历史。每个接收者使用独立的发送实例，客户端使用独立的接收实例。
    /// </summary>
    public sealed class StateViewHistoryTracker
    {
        private bool initialized;
        private string gameId = string.Empty;
        private GameStateViewerRole viewerRole;
        private int viewerPlayerId;
        private int eventSequence;
        private int logSequence;
        private List<RuleEventView> events = new List<RuleEventView>();
        private List<GameLogView> logs = new List<GameLogView>();

        public void Reset()
        {
            initialized = false;
            gameId = string.Empty;
            viewerRole = GameStateViewerRole.Unknown;
            viewerPlayerId = -1;
            eventSequence = 0;
            logSequence = 0;
            events.Clear();
            logs.Clear();
        }

        /// <summary>把完整投影中的历史替换为尚未发送的条目，首次或回退时自动发送全量历史。</summary>
        public StateViewHistoryDelta CreateDelta(GameStateView view, bool reset)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            int nextEventSequence;
            int nextLogSequence;
            if (!TryGetLastSequence(view.Events, item => item.Sequence, 0, out nextEventSequence) ||
                !TryGetLastSequence(view.Logs, item => item.Sequence, 0, out nextLogSequence))
                throw new ArgumentException("投影历史的序号必须为正数且递增。", nameof(view));

            reset = reset || !MatchesIdentity(view) || nextEventSequence < eventSequence ||
                    nextLogSequence < logSequence;
            var delta = new StateViewHistoryDelta
            {
                Reset = reset,
                BaseEventSequence = reset ? 0 : eventSequence,
                BaseLogSequence = reset ? 0 : logSequence,
                EventSequence = nextEventSequence,
                LogSequence = nextLogSequence
            };
            view.Events = After(view.Events, delta.BaseEventSequence, item => item.Sequence);
            view.Logs = After(view.Logs, delta.BaseLogSequence, item => item.Sequence);
            Remember(view, nextEventSequence, nextLogSequence);
            return delta;
        }

        /// <summary>连续基序号验证通过后恢复完整历史；失败时不修改视图或已收到的历史。</summary>
        public bool TryRestore(GameStateView view, StateViewHistoryDelta delta)
        {
            if (view == null || delta == null || delta.BaseEventSequence < 0 ||
                delta.BaseLogSequence < 0 || delta.EventSequence < delta.BaseEventSequence ||
                delta.LogSequence < delta.BaseLogSequence) return false;

            if (delta.Reset)
            {
                if (delta.BaseEventSequence != 0 || delta.BaseLogSequence != 0) return false;
            }
            else if (!MatchesIdentity(view) || delta.BaseEventSequence != eventSequence ||
                     delta.BaseLogSequence != logSequence)
            {
                return false;
            }

            int restoredEventSequence;
            int restoredLogSequence;
            if (!TryGetLastSequence(view.Events, item => item.Sequence,
                    delta.BaseEventSequence, out restoredEventSequence) ||
                !TryGetLastSequence(view.Logs, item => item.Sequence,
                    delta.BaseLogSequence, out restoredLogSequence) ||
                restoredEventSequence != delta.EventSequence || restoredLogSequence != delta.LogSequence)
                return false;

            var restoredEvents = delta.Reset ? new List<RuleEventView>() : new List<RuleEventView>(events);
            var restoredLogs = delta.Reset ? new List<GameLogView>() : new List<GameLogView>(logs);
            if (view.Events != null) restoredEvents.AddRange(view.Events);
            if (view.Logs != null) restoredLogs.AddRange(view.Logs);
            events = restoredEvents;
            logs = restoredLogs;
            // 不把缓存列表交给 UI，后续调用也不改动之前交付的视图列表。
            view.Events = new List<RuleEventView>(events);
            view.Logs = new List<GameLogView>(logs);
            Remember(view, restoredEventSequence, restoredLogSequence);
            return true;
        }

        private bool MatchesIdentity(GameStateView view)
        {
            return initialized && string.Equals(gameId, view.GameId ?? string.Empty, StringComparison.Ordinal) &&
                   viewerRole == view.ViewerRole && viewerPlayerId == view.ViewerPlayerId;
        }

        private void Remember(GameStateView view, int nextEventSequence, int nextLogSequence)
        {
            initialized = true;
            gameId = view.GameId ?? string.Empty;
            viewerRole = view.ViewerRole;
            viewerPlayerId = view.ViewerPlayerId;
            eventSequence = nextEventSequence;
            logSequence = nextLogSequence;
        }

        private static List<T> After<T>(List<T> source, int sequence, Func<T, int> getSequence)
        {
            var result = new List<T>();
            if (source != null)
                for (int i = 0; i < source.Count; i++)
                    if (getSequence(source[i]) > sequence) result.Add(source[i]);
            return result;
        }

        private static bool TryGetLastSequence<T>(List<T> source, Func<T, int> getSequence,
            int baseSequence, out int lastSequence) where T : class
        {
            lastSequence = baseSequence;
            if (source == null) return true;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] == null) return false;
                int sequence = getSequence(source[i]);
                // 私有记录被投影过滤后可留有空洞，不能要求 sequence == lastSequence + 1。
                if (sequence <= lastSequence) return false;
                lastSequence = sequence;
            }
            return true;
        }
    }
}
