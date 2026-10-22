using System;
using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable]
    public abstract class RoomConfigurationRequest
    {
        public string CommandId = string.Empty;
        public string RoomId = string.Empty;
        public int RequestingPlayerId;
        public int ExpectedConfigurationRevision;
    }
    [Serializable]
    public sealed class ChangeRoomConfigurationRequest : RoomConfigurationRequest
    {
        public string MapId = string.Empty;
        public string RuleModeId = string.Empty;
        public List<string> EnabledExpansionIds = new List<string>();
    }
    [Serializable]
    public sealed class SetRoomReadyRequest : RoomConfigurationRequest { public bool IsUserReady; }
    [Serializable]
    public sealed class SetRoomSeatStatusRequest : RoomConfigurationRequest
    { public int SeatIndex; public string Status = "open"; }
    [Serializable]
    public sealed class RequestRoomSeatSwapRequest : RoomConfigurationRequest { public int TargetSeatIndex; }
    [Serializable]
    public sealed class ConfirmRoomSeatSwapRequest : RoomConfigurationRequest
    { public string SwapRequestId = string.Empty; public bool Accepted; }
    [Serializable]
    public sealed class StartConfiguredGameRequest : RoomConfigurationRequest { }
    // 仅由本机房间适配器处理，不进入联机消息协议。
    public sealed class AddLocalRoomPlayerRequest : RoomConfigurationRequest
    { public int SeatIndex; public string PlayerName; }
    public sealed class RemoveLocalRoomPlayerRequest : RoomConfigurationRequest
    { public int SeatIndex; }
    [Serializable]
    public sealed class RoomConfigurationResult
    {
        public bool Success;
        public string ReasonCode = string.Empty;
        public RoomState Snapshot;
    }

    /// <summary>生产内容组合根提供实际指纹和能力校验；未提供时拒绝启动。</summary>
    public interface IRoomContentCapabilityProvider
    {
        string GetContentPackHash(GameRuleConfiguration configuration);
        bool TryValidate(GameRuleConfiguration configuration, out string reasonCode);
    }

    public static class RoomServiceAvailability
    {
        public const string PasswordReasonCode = "service_unavailable";
        public const string ArtificialPlayerReasonCode = "service_unavailable";
        public const string InvitationCodeReasonCode = "service_unavailable";
    }
}
