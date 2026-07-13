using System;
using System.Net;
using UnityEngine;

namespace YC.Infrastructure.Multiplayer
{
    public static class LocalMirrorTestMode
    {
        public const string CommandLineArgument = "--yc-local-multiplayer-test";
        public const ushort MirrorPort = 7777;
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
