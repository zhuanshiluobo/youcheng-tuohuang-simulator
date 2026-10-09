using System;
using System.Text;
using NUnit.Framework;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class SnapshotChunkBufferTests
    {
        [Test]
        public void TryAppend_ReassemblesUtf8SnapshotBeyondMirrorStringLimit()
        {
            var text = new StringBuilder("{\"日志\":\"");
            for (var i = 0; i < 9000; i++) text.Append("游城🚀");
            text.Append("\"}");
            var expectedText = text.ToString();
            var expectedBytes = Encoding.UTF8.GetBytes(expectedText);
            Assert.Greater(expectedBytes.Length, 65534);

            var receiver = new SnapshotChunkBuffer();
            byte[] completed = null;
            // 故意在多字节字符中间分块，完整重组后才解码 UTF-8。
            for (var offset = 0; offset < expectedBytes.Length; offset += 32767)
            {
                var count = Math.Min(32767, expectedBytes.Length - offset);
                Assert.IsTrue(receiver.TryAppend(42, false, expectedBytes.Length, offset,
                    Slice(expectedBytes, offset, count), out completed));
                if (offset + count < expectedBytes.Length)
                {
                    Assert.IsNull(completed);
                    Assert.IsTrue(receiver.IsReceiving);
                }
            }

            CollectionAssert.AreEqual(expectedBytes, completed);
            Assert.AreEqual(expectedText, Encoding.UTF8.GetString(completed));
            Assert.IsFalse(receiver.IsReceiving);
        }

        [TestCase(2, false, 6, 2)] // 混入另一传输
        [TestCase(2, false, 6, 0)] // 另一传输的首块
        [TestCase(1, true, 6, 2)]  // 混入另一快照类型
        [TestCase(1, false, 7, 2)] // 总长变化
        [TestCase(1, false, 6, 1)] // 重复块跨越已收边界
        public void TryAppend_RejectsBrokenSequenceAndReleasesBuffer(
            int transferId, bool isInitial, int totalBytes, int offset)
        {
            var receiver = new SnapshotChunkBuffer();
            Assert.IsTrue(receiver.TryAppend(1, false, 6, 0, new byte[] { 1, 2 }, out _));

            Assert.IsFalse(receiver.TryAppend(transferId, isInitial, totalBytes, offset,
                new byte[] { 3, 4 }, out var completed));

            Assert.IsNull(completed);
            Assert.IsFalse(receiver.IsReceiving);
        }

        [TestCase(0, 0)]
        [TestCase(-1, 0)]
        [TestCase(SnapshotChunkBuffer.MaxSnapshotBytes + 1, 0)]
        [TestCase(int.MaxValue, 0)]
        [TestCase(2, -1)]
        [TestCase(2, 2)]
        public void TryAppend_RejectsInvalidLengthOrOffset(int totalBytes, int offset)
        {
            var receiver = new SnapshotChunkBuffer();
            Assert.IsFalse(receiver.TryAppend(1, false, totalBytes, offset,
                new byte[] { 1 }, out var completed));
            Assert.IsNull(completed);
            Assert.IsFalse(receiver.IsReceiving);
        }

        [Test]
        public void TryAppend_RejectsEmptyOrNullPayload()
        {
            var receiver = new SnapshotChunkBuffer();
            Assert.IsTrue(receiver.TryAppend(1, false, 2, 0, new byte[] { 1 }, out _));
            Assert.IsFalse(receiver.TryAppend(1, false, 2, 1, Array.Empty<byte>(), out _));
            Assert.IsFalse(receiver.IsReceiving);
            Assert.IsFalse(receiver.TryAppend(1, false, 2, 0, null, out _));
        }

        [Test]
        public void Reset_AllowsRetransmissionAfterPartialSnapshot()
        {
            var receiver = new SnapshotChunkBuffer();
            Assert.IsTrue(receiver.TryAppend(1, true, 4, 0, new byte[] { 1, 2 }, out _));
            receiver.Reset();
            Assert.IsFalse(receiver.IsReceiving);

            var bytes = new byte[] { 1, 2, 3, 4 };
            Assert.IsTrue(receiver.TryAppend(2, true, bytes.Length, 0, bytes, out var completed));
            CollectionAssert.AreEqual(bytes, completed);
            Assert.IsFalse(receiver.IsReceiving);
        }

        [Test]
        public void TryAppend_ReassemblesSnapshotBeyondSteamBufferAfterGapAndRetries()
        {
            const int chunkBytes = 16 * 1024;
            const int missingOffset = 2 * chunkBytes;
            var expectedBytes = new byte[768 * 1024 + 17];
            for (var i = 0; i < expectedBytes.Length; i++) expectedBytes[i] = (byte)(i * 31);
            Assert.Greater(expectedBytes.Length, 512 * 1024);

            var receiver = new SnapshotChunkBuffer();
            // 首块发送失败时，后续块不会分配缓冲，也不会推进累计确认。
            Assert.IsTrue(receiver.TryAppend(42, true, expectedBytes.Length, chunkBytes,
                Slice(expectedBytes, chunkBytes, chunkBytes), out var completed));
            Assert.IsNull(completed);
            Assert.IsFalse(receiver.IsReceiving);
            Assert.AreEqual(0, receiver.ReceivedBytes);
            Assert.AreEqual(0, receiver.TotalBytes);

            Assert.IsTrue(receiver.TryAppend(42, true, expectedBytes.Length, 0,
                Slice(expectedBytes, 0, chunkBytes), out completed));
            // 确认消息丢失可能引发首块重传。
            Assert.IsTrue(receiver.TryAppend(42, true, expectedBytes.Length, 0,
                Slice(expectedBytes, 0, chunkBytes), out completed));
            Assert.IsNull(completed);
            Assert.AreEqual(42, receiver.TransferId);
            Assert.IsTrue(receiver.IsInitial);
            Assert.AreEqual(expectedBytes.Length, receiver.TotalBytes);
            Assert.AreEqual(chunkBytes, receiver.ReceivedBytes);

            // 模拟发送缓冲满时丢掉一块；缺口后的块都保持原累计进度。
            for (var offset = chunkBytes; offset < expectedBytes.Length; offset += chunkBytes)
            {
                if (offset == missingOffset) continue;
                var count = Math.Min(chunkBytes, expectedBytes.Length - offset);
                Assert.IsTrue(receiver.TryAppend(42, true, expectedBytes.Length, offset,
                    Slice(expectedBytes, offset, count), out completed));
                Assert.IsNull(completed);
                Assert.IsTrue(receiver.IsReceiving);
                Assert.AreEqual(missingOffset, receiver.ReceivedBytes);
            }

            // 从未确认区域按序重试；其中一块因延迟确认而重复发送。
            for (var offset = missingOffset - chunkBytes; offset < expectedBytes.Length;
                offset += chunkBytes)
            {
                var count = Math.Min(chunkBytes, expectedBytes.Length - offset);
                Assert.IsTrue(receiver.TryAppend(42, true, expectedBytes.Length, offset,
                    Slice(expectedBytes, offset, count), out completed));
            }

            CollectionAssert.AreEqual(expectedBytes, completed);
            Assert.IsFalse(receiver.IsReceiving);
            Assert.AreEqual(0, receiver.ReceivedBytes);
            Assert.AreEqual(0, receiver.TotalBytes);
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }
    }
}
