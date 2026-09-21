using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YC.Domain.State
{
    /// <summary>
    /// 生成跨进程、跨平台且不依赖本地化文本的稳定规则 ID。
    /// 输入协议为：版本字符串、kind、组件数量，以及每个组件的
    /// null 标记、UTF-8 字节长度（大端）和 UTF-8 字节。kind 同时作为
    /// 输出命名空间，完整 SHA-256 以小写十六进制输出。
    /// </summary>
    public static class StableIdFactory
    {
        private const string ProtocolVersion = "nmc.stable-id.v1";

        public static string Create(string kind, params string[] components)
        {
            ValidateKind(kind);

            byte[] input = EncodeCanonicalInput(kind, components);
            byte[] hash;
            using (SHA256 sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(input);
            }

            return kind + "_" + ToLowerHex(hash);
        }

        public static byte[] EncodeCanonicalInput(string kind, string[] components)
        {
            ValidateKind(kind);

            using (MemoryStream stream = new MemoryStream())
            {
                WriteUInt32BigEndian(stream, 1u + (uint)(components == null ? 0 : components.Length));
                WriteComponent(stream, ProtocolVersion);
                WriteComponent(stream, kind);

                if (components != null)
                {
                    for (int i = 0; i < components.Length; i++)
                    {
                        WriteComponent(stream, components[i]);
                    }
                }

                return stream.ToArray();
            }
        }

        public static string ComputeSha256(byte[] input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            using (SHA256 sha256 = SHA256.Create())
            {
                return ToLowerHex(sha256.ComputeHash(input));
            }
        }

        private static void ValidateKind(string kind)
        {
            if (string.IsNullOrEmpty(kind))
            {
                throw new ArgumentException("Stable ID 类型不能为空。", nameof(kind));
            }

            for (int i = 0; i < kind.Length; i++)
            {
                if (char.IsControl(kind[i]) || char.IsWhiteSpace(kind[i]))
                {
                    throw new ArgumentException("Stable ID 类型不能包含空白或控制字符。", nameof(kind));
                }
            }
        }

        private static void WriteComponent(Stream stream, string value)
        {
            if (value == null)
            {
                stream.WriteByte(0);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            stream.WriteByte(1);
            WriteUInt32BigEndian(stream, (uint)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteUInt32BigEndian(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static string ToLowerHex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
