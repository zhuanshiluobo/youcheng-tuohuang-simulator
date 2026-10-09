using System;
using System.Collections.Generic;

namespace YC.Infrastructure.Multiplayer
{
    public readonly struct SnapshotChunkRange
    {
        public int Offset { get; }
        public int Count { get; }

        public SnapshotChunkRange(int offset, int count)
        {
            Offset = offset;
            Count = count;
        }
    }

    /// <summary>限制每个连接尚未确认的快照字节，并从累计确认位置按序重试。</summary>
    public sealed class SnapshotSendWindow
    {
        public const int MaxInFlightBytes = 64 * 1024;

        private readonly int _chunkBytes;
        private int _nextOffset;
        private int _highestSubmittedOffset;
        private float _nextRetryAt;
        private float _retryDelay = 1f;

        public int TotalBytes { get; }
        public int AcknowledgedBytes { get; private set; }
        public bool IsComplete => AcknowledgedBytes == TotalBytes;

        public SnapshotSendWindow(int totalBytes, int chunkBytes)
        {
            if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
            if (chunkBytes <= 0 || chunkBytes > MaxInFlightBytes)
                throw new ArgumentOutOfRangeException(nameof(chunkBytes));

            TotalBytes = totalBytes;
            _chunkBytes = chunkBytes;
        }

        public List<SnapshotChunkRange> GetChunks(float now)
        {
            var chunks = new List<SnapshotChunkRange>();
            if (IsComplete) return chunks;

            var hadOutstandingBytes = _highestSubmittedOffset > AcknowledgedBytes;
            if (hadOutstandingBytes && now >= _nextRetryAt)
            {
                _nextOffset = AcknowledgedBytes;
                _retryDelay = Math.Min(8f, _retryDelay * 2f);
                _nextRetryAt = now + _retryDelay;
            }

            var windowEnd = AcknowledgedBytes + Math.Min(MaxInFlightBytes,
                TotalBytes - AcknowledgedBytes);
            while (_nextOffset < windowEnd)
            {
                var count = Math.Min(_chunkBytes, TotalBytes - _nextOffset);
                // 非整块窗口尾端留给下一次确认，只有快照末块允许缩短。
                if (count > windowEnd - _nextOffset) break;

                chunks.Add(new SnapshotChunkRange(_nextOffset, count));
                _nextOffset += count;
            }

            if (chunks.Count > 0)
            {
                _highestSubmittedOffset = Math.Max(_highestSubmittedOffset, _nextOffset);
                if (!hadOutstandingBytes) _nextRetryAt = now + _retryDelay;
            }

            return chunks;
        }

        public bool TryAcknowledge(int nextOffset, float now)
        {
            if (nextOffset < 0 || nextOffset > _highestSubmittedOffset ||
                (nextOffset != TotalBytes && nextOffset % _chunkBytes != 0))
                return false;

            // 丢失确认后的重发会产生重复确认；它们不能掩盖没有进展的超时。
            if (nextOffset <= AcknowledgedBytes) return true;

            AcknowledgedBytes = nextOffset;
            _nextOffset = Math.Max(_nextOffset, nextOffset);
            _retryDelay = 1f;
            _nextRetryAt = now + _retryDelay;
            return true;
        }
    }
}
