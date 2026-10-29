using UnityEngine;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;

namespace YC.Presentation
{
    public sealed class SteamAvatarView : MonoBehaviour
    {
        [SerializeField] private RawImage avatar;
        [SerializeField] private GameObject emptyAvatar;
        private ulong steamId;
        private Texture suppliedAvatar;
        public bool IsConfigured => avatar != null;

        public void BindPlayer(int playerId, Texture fallback = null)
        {
            var launch = GameLaunchContext.Instance;
            Bind(launch != null && launch.Mode != LaunchMode.Local
                ? launch.Players.Find(player => player.PlayerId == playerId)?.SteamId ?? 0 : 0, fallback);
        }
        public void Bind(ulong id, Texture fallback = null)
        {
            steamId = id;
            suppliedAvatar = fallback;
            Refresh();
        }
        private void OnEnable()
        {
            SteamAvatarCache.Changed += OnAvatarChanged;
            Refresh();
        }
        private void OnDisable() => SteamAvatarCache.Changed -= OnAvatarChanged;
        private void OnAvatarChanged(ulong id) { if (id == steamId) Refresh(); }
        private void Refresh()
        {
            if (avatar == null) return;
            var texture = suppliedAvatar != null ? suppliedAvatar : SteamAvatarCache.Get(steamId);
            avatar.texture = texture;
            avatar.gameObject.SetActive(texture != null);
            if (emptyAvatar != null) emptyAvatar.SetActive(texture == null);
            if (texture == null) return;
            var aspect = (float)texture.width / Mathf.Max(1, texture.height);
            avatar.uvRect = aspect > 1 ? new Rect((1 - 1 / aspect) / 2, 0, 1 / aspect, 1)
                : new Rect(0, (1 - aspect) / 2, 1, aspect);
        }
    }
}
