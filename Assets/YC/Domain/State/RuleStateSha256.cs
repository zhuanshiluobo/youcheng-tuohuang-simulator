using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace YC.Domain.State
{
    // 只替换SHA-256计算后端，规范输入字节及摘要版本完全保持。
    // 每次计算独占句柄；不缓存输入、摘要或对局引用，其他平台保留原实现。
    internal static class RuleStateSha256
    {
        internal static SHA256 Create()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                try { return new WindowsSha256(); }
                catch (Exception error) when (error is DllNotFoundException || error is EntryPointNotFoundException ||
                    error is PlatformNotSupportedException || error is CryptographicException) { }
#endif
            return SHA256.Create();
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private sealed class WindowsSha256 : SHA256
        {
            private readonly AlgorithmHandle algorithm;
            private HashHandle hash;
            private bool disposed;

            internal WindowsSha256()
            {
                HashSizeValue = 256;
                Check(BCryptOpenAlgorithmProvider(out var provider, "SHA256", null, 0));
                algorithm = new AlgorithmHandle(provider);
                try { Initialize(); }
                catch { algorithm.Dispose(); throw; }
            }

            public override void Initialize()
            {
                if (disposed) throw new ObjectDisposedException(nameof(WindowsSha256));
                hash?.Dispose(); hash = null;
                // Windows 7起NULL/0让BCrypt拥有哈希对象存储，DestroyHash一并释放。
                // https://learn.microsoft.com/windows/win32/api/bcrypt/nf-bcrypt-bcryptcreatehash
                Check(BCryptCreateHash(algorithm, out var value, IntPtr.Zero, 0, IntPtr.Zero, 0, 0));
                hash = new HashHandle(value);
            }

            protected override void HashCore(byte[] array, int offset, int count)
            {
                using var diagnostic=GameStateCloneService.MeasureDiagnostic("rule.digest.sha256");
                if (disposed) throw new ObjectDisposedException(nameof(WindowsSha256));
                if (count == 0) return;
                // 仅在同步HashData期间固定输入；支持CryptoStream的非零偏移，绝不复制整份输入。
                var pinned = GCHandle.Alloc(array, GCHandleType.Pinned);
                try { Check(BCryptHashData(hash, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), count, 0)); }
                finally { pinned.Free(); }
            }

            protected override byte[] HashFinal()
            {
                if (disposed) throw new ObjectDisposedException(nameof(WindowsSha256));
                var result = new byte[32];
                Check(BCryptFinishHash(hash, result, result.Length, 0));
                return result;
            }

            protected override void Dispose(bool disposing)
            {
                if (!disposed)
                {
                    disposed = true;
                    hash?.Dispose(); algorithm?.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private static void Check(int status)
        { if (status < 0) throw new CryptographicException("BCrypt SHA256失败：0x" + unchecked((uint)status).ToString("x8")); }

        private sealed class AlgorithmHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal AlgorithmHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() => BCryptCloseAlgorithmProvider(handle, 0) >= 0;
        }

        private sealed class HashHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal HashHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() => BCryptDestroyHash(handle) >= 0;
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, int flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptCreateHash(AlgorithmHandle algorithm, out IntPtr hash, IntPtr buffer, int bufferSize, IntPtr secret, int secretSize, int flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptHashData(HashHandle hash, IntPtr input, int count, int flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptFinishHash(HashHandle hash, [Out] byte[] output, int count, int flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyHash(IntPtr hash);
#endif
    }
}
