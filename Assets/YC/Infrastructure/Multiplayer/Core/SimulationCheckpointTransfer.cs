using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class SimulationCheckpointPart
    {
        public SimulationCheckpointEnvelope Envelope { get; }
        public int Offset { get; }
        public byte[] Payload { get; }
        internal SimulationCheckpointPart(SimulationCheckpointEnvelope envelope, int offset, byte[] payload)
        { Envelope = envelope; Offset = offset; Payload = payload; }
    }

    /// <summary>保存私有编码字节；信封、分片和原始字节的读出均为副本。</summary>
    public sealed class SimulationCheckpointEncodedTransfer
    {
        private readonly SimulationCheckpointEnvelope envelope;
        private readonly byte[] bytes;
        internal SimulationCheckpointEncodedTransfer(SimulationCheckpointEnvelope envelope, byte[] bytes)
        { this.envelope = envelope.Snapshot(); this.bytes = bytes; }
        public SimulationCheckpointEnvelope Envelope => envelope.Snapshot();
        public byte[] CopyEncodedBytes() => (byte[])bytes.Clone();
        public IEnumerable<SimulationCheckpointPart> Split()
        {
            for (int offset = 0; offset < bytes.Length; offset += SimulationCheckpointTransfer.ChunkBytes)
            {
                var part = new byte[Math.Min(SimulationCheckpointTransfer.ChunkBytes, bytes.Length - offset)];
                Buffer.BlockCopy(bytes, offset, part, 0, part.Length);
                yield return new SimulationCheckpointPart(envelope.Snapshot(), offset, part);
            }
        }
    }

    public static class SimulationCheckpointTransfer
    {
        public const int ChunkBytes = 8 * 1024;
        public const int MaximumEncodedBytes = 8 * 1024 * 1024;
        public const int MaximumDecodedBytes = 64 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static SimulationCheckpointEncodedTransfer Encode(SimulationCheckpointEnvelope identity, string archiveJson)
        {
            if (identity == null || !identity.HasValidIdentity()) throw new ArgumentException("checkpoint_identity_invalid");
            if (string.IsNullOrEmpty(archiveJson)) throw new ArgumentException("checkpoint_archive_empty");
            int count = Utf8.GetByteCount(archiveJson);
            if (count > MaximumDecodedBytes) throw new ArgumentException("checkpoint_decoded_limit");
            var raw = Utf8.GetBytes(archiveJson);
            var envelope = identity.Snapshot(); envelope.DecodedByteCount = count;
            envelope.ArchiveDigest = Hash(raw);
            if (!string.IsNullOrEmpty(identity.ArchiveDigest) && identity.ArchiveDigest != envelope.ArchiveDigest)
                throw new ArgumentException("checkpoint_archive_digest_conflict");
            byte[] encoded;
            using (var output = new LimitedEncodedStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(raw, 0, raw.Length);
                encoded = output.ToArray();
            }
            envelope.EncodedByteCount = encoded.Length; envelope.EncodedDigest = Hash(encoded);
            if (!envelope.HasValidPayload()) throw new ArgumentException("checkpoint_encoded_limit");
            return new SimulationCheckpointEncodedTransfer(envelope, encoded);
        }

        public sealed class Assembler
        {
            private readonly SimulationCheckpointEnvelope expected;
            private SimulationCheckpointEnvelope envelope;
            private byte[] buffer;
            private int received;
            public Assembler(SimulationCheckpointEnvelope expectedIdentity)
            {
                if (expectedIdentity == null || !expectedIdentity.HasValidIdentity()) throw new ArgumentException("checkpoint_identity_invalid");
                expected = expectedIdentity.Snapshot();
            }
            public int BufferedBytes => received;
            public void Reset() { buffer = null; envelope = null; received = 0; }

            public bool TryAppend(SimulationCheckpointEnvelope incoming, int offset, byte[] payload, out string archiveJson, out string faultCode)
            {
                archiveJson = null; faultCode = string.Empty;
                if (incoming == null || !incoming.HasValidPayload()) return Reject("checkpoint_envelope_invalid", out faultCode);
                if (!SimulationCheckpointEnvelope.SameIdentity(expected, incoming) ||
                    !string.IsNullOrEmpty(expected.ArchiveDigest) && expected.ArchiveDigest != incoming.ArchiveDigest)
                    return Reject("checkpoint_identity_mismatch", out faultCode);
                if (payload == null || payload.Length == 0 || payload.Length > ChunkBytes || offset < 0 ||
                    offset > incoming.EncodedByteCount - payload.Length)
                    return Reject("checkpoint_chunk_invalid", out faultCode);
                if (buffer == null)
                {
                    if (offset != 0) return Reject("checkpoint_chunk_gap", out faultCode);
                    envelope = incoming.Snapshot(); buffer = new byte[envelope.EncodedByteCount];
                }
                if (!SimulationCheckpointEnvelope.SamePayload(envelope, incoming) || offset != received)
                    return Reject("checkpoint_chunk_conflict", out faultCode);
                Buffer.BlockCopy(payload, 0, buffer, received, payload.Length); received += payload.Length;
                if (received < buffer.Length) return true;
                try
                {
                    if (Hash(buffer) != envelope.EncodedDigest) return Reject("checkpoint_encoded_digest_mismatch", out faultCode);
                    if (buffer[0] != 0x1f || buffer[1] != 0x8b || buffer[2] != 8 || buffer[3] != 0)
                        return Reject("checkpoint_gzip_invalid", out faultCode);
                    byte[] raw;
                    using (var input = new MemoryStream(buffer, false))
                    using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                    using (var output = new MemoryStream())
                    {
                        var block = new byte[32 * 1024]; int read;
                        while ((read = gzip.Read(block, 0, block.Length)) != 0)
                        {
                            // 按真实读取增量核限额，绝不按不可信解码长度预分配内存。
                            if (output.Length + read > MaximumDecodedBytes) return Reject("checkpoint_decoded_limit", out faultCode);
                            if (output.Length + read > envelope.DecodedByteCount) return Reject("checkpoint_decoded_length_mismatch", out faultCode);
                            output.Write(block, 0, read);
                        }
                        raw = output.ToArray();
                    }
                    if (raw.Length != envelope.DecodedByteCount) return Reject("checkpoint_decoded_length_mismatch", out faultCode);
                    // 显式核gzip尾部，拒绝某些运行时会容忍的截断CRC/ISIZE。
                    if (ReadUInt32(buffer, buffer.Length - 8) != Crc32(raw) || ReadUInt32(buffer, buffer.Length - 4) != (uint)raw.Length)
                        return Reject("checkpoint_gzip_truncated_or_invalid", out faultCode);
                    if (Hash(raw) != envelope.ArchiveDigest) return Reject("checkpoint_archive_digest_mismatch", out faultCode);
                    archiveJson = Utf8.GetString(raw); Reset(); return true;
                }
                catch (InvalidDataException) { return Reject("checkpoint_gzip_invalid", out faultCode); }
                catch (IOException) { return Reject("checkpoint_gzip_invalid", out faultCode); }
                catch (DecoderFallbackException) { return Reject("checkpoint_utf8_invalid", out faultCode); }
            }

            private bool Reject(string code, out string faultCode) { faultCode = code; Reset(); return false; }
        }

        private sealed class LimitedEncodedStream : Stream
        {
            private readonly MemoryStream buffer = new MemoryStream();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => buffer.Length;
            public override long Position { get => buffer.Position; set => throw new NotSupportedException(); }
            public byte[] ToArray() => buffer.ToArray();
            public override void Write(byte[] bytes, int offset, int count)
            {
                if (Position + count > MaximumEncodedBytes) throw new ArgumentException("checkpoint_encoded_limit");
                buffer.Write(bytes, offset, count);
            }
            public override void WriteByte(byte value)
            { if (Position >= MaximumEncodedBytes) throw new ArgumentException("checkpoint_encoded_limit"); buffer.WriteByte(value); }
            public override void Flush() => buffer.Flush();
            public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) buffer.Dispose(); base.Dispose(disposing); }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(bytes); var text = new StringBuilder(64);
                foreach (byte value in digest) text.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
        private static uint ReadUInt32(byte[] bytes, int offset) => (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        private static uint Crc32(byte[] bytes)
        { uint crc = uint.MaxValue; foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8); return ~crc; }
        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            { uint value = index; for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1; table[index] = value; }
            return table;
        }
    }
}
