using System;
using System.Collections.Generic;
using System.Text;

namespace YC.Infrastructure.Multiplayer
{
    // 可靠有序连接上的分片。先按观察者投影，再编码；任何未完整组装的数据都不能应用到会话。
    public static class GameStateViewTransfer
    {
        public const int ChunkBytes = 8 * 1024;
        public const int MaximumBytes = 8 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new ArgumentException("state_view_empty");
            var bytes = Utf8.GetBytes(json);
            if (bytes.Length > MaximumBytes) throw new ArgumentException("state_view_too_large");
            return bytes;
        }

        public static IEnumerable<ArraySegment<byte>> Split(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) throw new ArgumentException("state_view_invalid_size");
            for (int offset = 0; offset < bytes.Length; offset += ChunkBytes)
                yield return new ArraySegment<byte>(bytes, offset, Math.Min(ChunkBytes, bytes.Length - offset));
        }

        public sealed class Assembler
        {
            private byte[] buffer;
            private long transferId;
            private int received;
            private bool initial;
            public void Reset() { buffer = null; received = 0; transferId = 0; }

            public bool TryAppend(long id, bool isInitial, int total, int offset, byte[] part, out string json)
            {
                json = null;
                if (id <= 0 || total <= 0 || total > MaximumBytes || part == null || part.Length == 0 ||
                    part.Length > ChunkBytes || offset < 0 || offset > total - part.Length) { Reset(); return false; }
                if (buffer == null)
                {
                    if (offset != 0) return false;
                    buffer = new byte[total]; transferId = id; initial = isInitial;
                }
                if (id != transferId || isInitial != initial || total != buffer.Length || offset != received)
                { Reset(); return false; }
                Buffer.BlockCopy(part, 0, buffer, received, part.Length); received += part.Length;
                if (received == buffer.Length)
                {
                    try { json = Utf8.GetString(buffer); }
                    catch (DecoderFallbackException) { Reset(); return false; }
                    Reset();
                }
                return true;
            }
        }
    }
}
