using System;
using System.Security.Cryptography;

namespace YC.Domain.Rules
{
    /// <summary>仅由主机验证后的新局创建入口调用；局身份绑定和重复请求门闩由调用方保存。</summary>
    public static class GameplayRandomSeed
    {
        /// <summary>生成新局种子；显式熵源供复现测试使用，不保存进程共享的随机序列。</summary>
        public static int CreateMatchSeed(Func<int> entropySource = null)
        {
            if (entropySource != null) return entropySource();
            var bytes = new byte[sizeof(int)];
            using (var source = RandomNumberGenerator.Create()) source.GetBytes(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }
    }
}
