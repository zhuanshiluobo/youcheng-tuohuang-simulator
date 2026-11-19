using System;
using System.Linq;
using System.Net;
using UnityEngine;

namespace YC.Infrastructure.Multiplayer
{
    public static class LocalMirrorTestMode
    {
        public const string CommandLineArgument = "--yc-local-multiplayer-test";
        public const ushort MirrorPort = 7777;
        // 开放正式准备能力后，隔离测试身份仍须独立验证，不能再以能力已启用作为证明。
        public static bool IsPrivatePreparationFixture
        {
            get
            {
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
                try
                {
                    var args = Environment.GetCommandLineArgs();
                    if (!args.Contains("--yc-private-preparation-fixture") || !args.Contains(CommandLineArgument)) return false;
                    var build = new System.IO.DirectoryInfo(System.IO.Path.GetFullPath(Environment.CurrentDirectory));
                    if (build.Parent == null || !string.Equals(build.Parent.Name, "Builds", StringComparison.OrdinalIgnoreCase) || build.Parent.Parent == null) return false;
                    string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(build.Parent.Parent.FullName, "TestResults")) + System.IO.Path.DirectorySeparatorChar;
                    foreach (string prefix in new[] { "--yc-test-content-root=", "--yc-performance-save-root=", "--yc-network-validation-output=" })
                    {
                        var matches = args.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                        if (matches.Length != 1) return false;
                        string path = matches[0].Substring(prefix.Length);
                        if (!System.IO.Path.IsPathRooted(path) || !System.IO.Directory.Exists(path) ||
                            !System.IO.Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    return true;
                }
                catch (Exception error) when (!(error is OutOfMemoryException)) { return false; }
#else
                return false;
#endif
            }
        }
        public static ushort RuntimeMirrorPort
        {
            get
            {
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
                // 只供显式隔离的私密联机夹具并存；正式端口及预制体校验不变。
                if (IsPrivatePreparationFixture)
                {
                    const string prefix = "--yc-private-fixture-port=";
                    var matches = Array.FindAll(Environment.GetCommandLineArgs(), argument => argument.StartsWith(prefix, StringComparison.Ordinal));
                    if (matches.Length > 1) throw new InvalidOperationException("隔离联机夹具端口参数重复。");
                    if (matches.Length == 1)
                    {
                        if (!ushort.TryParse(matches[0].Substring(prefix.Length), out ushort port) || port < 7800)
                            throw new InvalidOperationException("隔离联机夹具端口无效。");
                        return port;
                    }
                }
#endif
                return MirrorPort;
            }
        }
        private const string EditorPreferenceKey = "YC.LocalMirrorMultiplayerTest.Enabled";

        public static bool IsEnabled
        {
            get
            {
#if UNITY_EDITOR
                if (UnityEditor.EditorPrefs.GetBool(EditorPreferenceKey, false)) return true;
#endif
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                var args = Environment.GetCommandLineArgs();
                for (var i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], CommandLineArgument, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
#endif
                return false;
            }
        }

        public static string PlayerName
        {
            get
            {
                const string prefix = "--yc-local-player-name=";
                var args = Environment.GetCommandLineArgs();
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var value = args[i].Substring(prefix.Length).Trim();
                        if (!string.IsNullOrEmpty(value)) return value;
                    }
                }

                return UnityEngine.Application.isEditor
                    ? "Editor Player"
                    : "Local Player " + System.Diagnostics.Process.GetCurrentProcess().Id;
            }
        }

        public static string GetMirrorHost(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId)) throw new ArgumentException("本地房间号不能为空。", nameof(roomId));
            var separator = roomId.LastIndexOf(':');
            var host = separator > 0 ? roomId.Substring(0, separator) : roomId;
            if (string.IsNullOrWhiteSpace(host)) return IPAddress.Loopback.ToString();
            return host.Trim();
        }

#if UNITY_EDITOR
        public static void SetEditorEnabled(bool enabled) => UnityEditor.EditorPrefs.SetBool(EditorPreferenceKey, enabled);
#endif
    }
}
