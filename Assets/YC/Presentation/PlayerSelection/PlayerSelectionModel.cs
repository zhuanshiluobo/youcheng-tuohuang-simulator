using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YC.Domain.Rules;

namespace YC.Presentation
{
    public enum PlayerSelectionMode { Single, Multiple }

    [Serializable]
    public sealed class PlayerSelectionOption
    {
        public int Id;
        public string Name;
        public PlayerColor Color;
        public Texture Avatar;
        public bool IsSelf;
        public bool Eligible = true;
        public string DisabledReason;
    }

    public sealed class PlayerSelectionConfig
    {
        public string RequestId;
        public int Revision;
        public string Title;
        public string Description;
        public PlayerSelectionMode Mode;
        public int Min = 1, Max = 1;
        public bool AllowCancel = true;
        public PlayerSelectionOption[] Players = Array.Empty<PlayerSelectionOption>();
        public int[] SelectedIds = Array.Empty<int>();
    }

    /// <summary>仅保存界面草稿；稳定玩家 ID 与规则提供的可选性决定选择集。</summary>
    public sealed class PlayerSelectionDraft
    {
        private readonly List<int> selected = new List<int>();
        public PlayerSelectionConfig Config { get; private set; }
        public IReadOnlyList<int> SelectedIds => selected.AsReadOnly();
        public bool CanConfirm => Config != null && selected.Count >= Config.Min && selected.Count <= Config.Max;
        public bool HasEnoughPlayers => Config != null && Config.Players.Count(p => p.Eligible) >= Config.Min;

        public void Refresh(PlayerSelectionConfig config)
        {
            if (config == null || config.Players == null || config.Players.Length > 4 ||
                config.Players.Any(p => p == null || p.Id < 0) ||
                config.Players.Select(p => p.Id).Distinct().Count() != config.Players.Length ||
                config.Min < 0 || config.Max < config.Min || config.Max > 4 ||
                (config.Mode != PlayerSelectionMode.Single && config.Mode != PlayerSelectionMode.Multiple) ||
                (config.Mode == PlayerSelectionMode.Single && config.Max != 1))
                throw new ArgumentException("玩家列表或选择数量配置无效。", nameof(config));
            var newRequest = Config == null || Config.RequestId != config.RequestId || Config.Mode != config.Mode;
            Config = config;
            if (newRequest)
            {
                selected.Clear();
                selected.AddRange((config.SelectedIds ?? Array.Empty<int>()).Distinct()
                    .Where(id => config.Players.Any(p => p.Id == id && p.Eligible)).Take(config.Max));
            }
            selected.RemoveAll(id => !config.Players.Any(p => p.Id == id && p.Eligible));
            if (selected.Count > config.Max) selected.RemoveRange(config.Max, selected.Count - config.Max);
        }

        // 返回 false 时保留草稿；上限与禁用提示由页面的可序列化文案提供。
        public bool Toggle(int id)
        {
            var player = Config?.Players.FirstOrDefault(p => p.Id == id);
            if (player == null || !player.Eligible) return false;
            if (Config.Mode == PlayerSelectionMode.Single)
            {
                if (selected.Contains(id)) return false;
                selected.Clear(); selected.Add(id); return true;
            }
            if (selected.Remove(id)) return true;
            if (selected.Count >= Config.Max) return false;
            selected.Add(id); return true;
        }

        public int[] Result() => selected.ToArray();
    }
}
