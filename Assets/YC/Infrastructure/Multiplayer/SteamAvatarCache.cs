using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>仅用于本机展示；不参与对局状态、存档或联机命令。</summary>
    public static class SteamAvatarCache
    {
        private sealed class Entry
        {
            public int ImageHandle;
            public Texture2D Texture;
        }
        private static readonly Dictionary<ulong, Entry> entries = new Dictionary<ulong, Entry>();
        private static Callback<AvatarImageLoaded_t> avatarLoaded;
        private static Callback<PersonaStateChange_t> personaChanged;
        public static event Action<ulong> Changed;

        public static Texture2D Get(ulong steamId)
        {
            if (steamId == 0 || !SteamBootstrap.IsInitialized) return null;
            if (entries.TryGetValue(steamId, out var cached)) return cached.Texture;
            avatarLoaded = avatarLoaded ?? Callback<AvatarImageLoaded_t>.Create(OnAvatarLoaded);
            personaChanged = personaChanged ?? Callback<PersonaStateChange_t>.Create(OnPersonaChanged);
            var entry = new Entry();
            entries.Add(steamId, entry);
            var user = new CSteamID(steamId);
            if (!SteamFriends.RequestUserInformation(user, false))
                Load(steamId, SteamFriends.GetLargeFriendAvatar(user));
            return entry.Texture;
        }
        private static void OnAvatarLoaded(AvatarImageLoaded_t callback)
        {
            if (SteamBootstrap.IsInitialized && entries.ContainsKey(callback.m_steamID.m_SteamID))
                Load(callback.m_steamID.m_SteamID, callback.m_iImage);
        }
        private static void OnPersonaChanged(PersonaStateChange_t callback)
        {
            if (!SteamBootstrap.IsInitialized || !entries.TryGetValue(callback.m_ulSteamID, out var entry)) return;
            if (entry.Texture == null || (callback.m_nChangeFlags & EPersonaChange.k_EPersonaChangeAvatar) != 0)
                Load(callback.m_ulSteamID, SteamFriends.GetLargeFriendAvatar(new CSteamID(callback.m_ulSteamID)));
        }
        private static void Load(ulong steamId, int handle)
        {
            if (handle < 0 || !entries.TryGetValue(steamId, out var entry)) return;
            if (handle == entry.ImageHandle && (handle == 0 || entry.Texture != null)) return;
            Texture2D texture = null;
            if (handle > 0)
            {
                if (!SteamUtils.GetImageSize(handle, out uint width, out uint height) || width == 0 || height == 0 || width > 1024 || height > 1024) return;
                var pixels = new byte[checked((int)(width * height * 4))];
                if (!SteamUtils.GetImageRGBA(handle, pixels, pixels.Length)) return;
                // Steam 按自上而下的行序返回像素；Unity 原始纹理以左下角为起点。
                int rowSize = checked((int)width * 4);
                var flipped = new byte[pixels.Length];
                for (int row = 0; row < (int)height; row++)
                    Buffer.BlockCopy(pixels, row * rowSize, flipped, ((int)height - 1 - row) * rowSize, rowSize);
                texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false)
                { name = "SteamAvatar", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                texture.LoadRawTextureData(flipped);
                texture.Apply(false, true);
            }
            var previous = entry.Texture;
            entry.ImageHandle = handle;
            entry.Texture = texture;
            Changed?.Invoke(steamId);
            Release(previous);
        }
        public static void Clear()
        {
            avatarLoaded?.Dispose(); avatarLoaded = null;
            personaChanged?.Dispose(); personaChanged = null;
            var previous = new List<KeyValuePair<ulong, Entry>>(entries);
            entries.Clear();
            foreach (var pair in previous)
            {
                Changed?.Invoke(pair.Key);
                Release(pair.Value.Texture);
            }
        }
        private static void Release(Texture2D texture)
        {
            if (texture == null) return;
            if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
