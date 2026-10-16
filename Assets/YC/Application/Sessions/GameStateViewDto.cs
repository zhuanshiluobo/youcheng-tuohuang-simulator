using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    /// <summary>
    /// 网络初始同步 DTO。它故意没有 GameState 字段；Host 先按连接对应的 viewer 投影，
    /// 再由 Mirror 序列化此 DTO。
    /// </summary>
    [Serializable]
    public sealed class InitialGameStateViewDto
    {
        public int ProtocolVersion = GameStateView.CurrentSchemaVersion;
        public int NextConfirmedSequence = 1;
        public GameStateView View = new GameStateView();
        public IReadOnlyList<SettlementOptionView> SettlementOptions => View.CharacterSettlementOptions.Select(SettlementOptionView.FromOption).ToList();
    }

    /// <summary>网络确认广播 DTO。它与 Host journal/snapshot DTO 完全分离。</summary>
    [Serializable]
    public sealed class ConfirmedGameStateViewDto
    {
        public int ProtocolVersion = GameStateView.CurrentSchemaVersion;
        public int Sequence;
        public GameCommandDto Command = new GameCommandDto();
        public GameStateView View = new GameStateView();
        public IReadOnlyList<SettlementOptionView> SettlementOptions => View.CharacterSettlementOptions.Select(SettlementOptionView.FromOption).ToList();
    }
}
