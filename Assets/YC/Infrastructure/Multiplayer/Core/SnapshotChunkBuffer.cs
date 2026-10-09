using System;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>按连续字节重组快照，允许累计确认后的重复块和缺口重试。</summary>
    public sealed class SnapshotChunkBuffer
    {
        public const int MaxSnapshotBytes = 64 * 1024 * 1024;

        private byte[] _buffer;
        private int _transferId;
        private bool _isInitial;
        private int _receivedBytes;

        public bool IsReceiving => _buffer != null;
        public int TransferId => _transferId;
        public bool IsInitial => _isInitial;
        public int TotalBytes => _buffer?.Length ?? 0;
        public int ReceivedBytes => _receivedBytes;

        public void Reset()
        {
            _buffer = null;
            _transferId = 0;
            _isInitial = false;
            _receivedBytes = 0;
        }

        public bool TryAppend(int transferId, bool isInitial, int totalBytes, int offset,
            byte[] payload, out byte[] completed)
        {
            completed = null;
            if (totalBytes <= 0 || totalBytes > MaxSnapshotBytes || payload == null ||
                payload.Length == 0 || payload.Length > totalBytes || offset < 0 ||
                offset > totalBytes - payload.Length)
            {
                Reset();
                return false;
            }

            if (!IsReceiving)
            {
                if (offset != 0)
                {
                    // 首块可能发送失败；保持零进度，让调用方确认并重试首块。
                    return true;
                }

                _buffer = new byte[totalBytes];
                _transferId = transferId;
                _isInitial = isInitial;
            }
            else if (_transferId != transferId || _isInitial != isInitial ||
                _buffer.Length != totalBytes)
            {
                // 尚未完成的传输不能被另一快照悄悄替换。
                Reset();
                return false;
            }

            if (offset > _receivedBytes)
            {
                // 丢弃缺口后面的块，累计确认仍指向缺失块的起点。
                return true;
            }

            if (offset < _receivedBytes)
            {
                if (offset + payload.Length <= _receivedBytes) return true;
                // 重传块只能覆盖完整的已收区间，不能跨越当前接收边界。
                Reset();
                return false;
            }

            Buffer.BlockCopy(payload, 0, _buffer, offset, payload.Length);
            _receivedBytes += payload.Length;
            if (_receivedBytes == totalBytes)
            {
                completed = _buffer;
                Reset();
            }

            return true;
        }
    }
}
