using System.Collections.Generic;
using NUnit.Framework;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class SnapshotSendWindowTests
    {
        private const int ChunkBytes = 16 * 1024;

        [Test]
        public void GetChunks_StreamsSnapshotBeyondSteamBufferWithinAcknowledgedWindow()
        {
            var sender = new SnapshotSendWindow(768 * 1024 + 137, ChunkBytes);
            var receivedBytes = 0;
            var now = 0f;
            while (!sender.IsComplete)
            {
                var chunks = sender.GetChunks(now);
                Assert.IsNotEmpty(chunks);
                Assert.LessOrEqual(ByteCount(chunks), SnapshotSendWindow.MaxInFlightBytes);
                foreach (var chunk in chunks)
                {
                    Assert.AreEqual(receivedBytes, chunk.Offset);
                    receivedBytes += chunk.Count;
                }

                Assert.LessOrEqual(receivedBytes - sender.AcknowledgedBytes,
                    SnapshotSendWindow.MaxInFlightBytes);
                Assert.IsTrue(sender.TryAcknowledge(receivedBytes, now));
                now += 0.01f;
            }

            Assert.AreEqual(sender.TotalBytes, receivedBytes);
            Assert.AreEqual(sender.TotalBytes, sender.AcknowledgedBytes);
            Assert.IsEmpty(sender.GetChunks(now + 10f));
        }

        [Test]
        public void GetChunks_RetriesLostChunkFromLastContiguousAcknowledgement()
        {
            var sender = new SnapshotSendWindow(768 * 1024, ChunkBytes);
            Assert.AreEqual(64 * 1024, ByteCount(sender.GetChunks(0f)));
            // 前两块收到，第三块丢失；后续块不能推进累计确认。
            Assert.IsTrue(sender.TryAcknowledge(32 * 1024, 0.1f));
            var refill = sender.GetChunks(0.2f);
            Assert.AreEqual(64 * 1024, refill[0].Offset);
            Assert.AreEqual(32 * 1024, ByteCount(refill));
            Assert.IsEmpty(sender.GetChunks(1f));

            var retry = sender.GetChunks(1.11f);
            Assert.AreEqual(32 * 1024, retry[0].Offset);
            Assert.AreEqual(64 * 1024, ByteCount(retry));
            Assert.IsTrue(sender.TryAcknowledge(96 * 1024, 1.12f));
            var next = sender.GetChunks(1.13f);
            Assert.AreEqual(96 * 1024, next[0].Offset);
            Assert.AreEqual(64 * 1024, ByteCount(next));
        }

        [Test]
        public void GetChunks_BacksOffLostAcknowledgementsAndResetsOnProgress()
        {
            var sender = new SnapshotSendWindow(768 * 1024, ChunkBytes);
            sender.GetChunks(0f);
            foreach (var retryAt in new[] { 1f, 3f, 7f, 15f, 23f })
            {
                Assert.IsEmpty(sender.GetChunks(retryAt - 0.01f));
                var retry = sender.GetChunks(retryAt);
                Assert.AreEqual(0, retry[0].Offset);
                Assert.AreEqual(64 * 1024, ByteCount(retry));
            }

            Assert.IsTrue(sender.TryAcknowledge(ChunkBytes, 23.1f));
            sender.GetChunks(23.2f);
            Assert.IsEmpty(sender.GetChunks(24f));
            Assert.AreEqual(ChunkBytes, sender.GetChunks(24.11f)[0].Offset);
        }

        [Test]
        public void TryAcknowledge_RejectsInvalidOffsetsAndDuplicateAckDoesNotDelayRetry()
        {
            var sender = new SnapshotSendWindow(768 * 1024 + 137, ChunkBytes);
            Assert.IsFalse(sender.TryAcknowledge(ChunkBytes, 0f));
            sender.GetChunks(0f);
            Assert.IsFalse(sender.TryAcknowledge(-1, 0.1f));
            Assert.IsFalse(sender.TryAcknowledge(1, 0.1f));
            Assert.IsFalse(sender.TryAcknowledge(80 * 1024, 0.1f));
            Assert.IsFalse(sender.TryAcknowledge(sender.TotalBytes, 0.1f));
            Assert.IsTrue(sender.TryAcknowledge(ChunkBytes, 0.1f));
            sender.GetChunks(0.2f);

            Assert.IsTrue(sender.TryAcknowledge(0, 0.9f));
            Assert.IsTrue(sender.TryAcknowledge(ChunkBytes, 1f));
            Assert.IsEmpty(sender.GetChunks(1f));
            var retry = sender.GetChunks(1.11f);
            Assert.AreEqual(ChunkBytes, retry[0].Offset);
            Assert.AreEqual(64 * 1024, ByteCount(retry));
            Assert.AreEqual(ChunkBytes, sender.AcknowledgedBytes);
        }

        private static int ByteCount(List<SnapshotChunkRange> chunks)
        {
            var bytes = 0;
            foreach (var chunk in chunks) bytes += chunk.Count;
            return bytes;
        }
    }
}
