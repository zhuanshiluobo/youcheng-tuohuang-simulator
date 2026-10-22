using System;
using System.Collections.Generic;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public enum EventResponseAttachmentKind { RequiredChild, DeferredPlayerResponse }
    public enum DeferredResponseStatus { Pending, Stored, Returned }
    public enum DeferredDependencyKind { CardMarkerConsumption, CarrierExit, ExactItemUse, SupplyRequirement, FinalizeGame }

    public sealed class DeferredEventResponseDescriptor
    {
        public string StableResponseKey = string.Empty;
        public string SourceEventId = string.Empty;
        public string SourceDepartureEffectId = string.Empty;
        public string SourceCardInstanceId = string.Empty;
        public string AbilityId = string.Empty;
        public int AnsweringPlayerId = -1;
        public string ReservedItemId = string.Empty;
        public string OriginalSupplyZoneId = string.Empty;
        public string DestinationHandlerId = string.Empty;
    }

    public sealed class DeferredDependencyQuery
    {
        public string OwnerEffectId = string.Empty;
        public DeferredDependencyKind DependencyKind;
        public string SourceCardInstanceId = string.Empty;
        public List<string> ItemIds = new List<string>();
        public string OriginalSupplyZoneId = string.Empty;
        public string RequiredTypeId = string.Empty;
        public int RequiredCount;
        public bool ConsumesAll;
    }

    public sealed class DeferredResponseDependency
    {
        public string DeferredResponseId = string.Empty;
        public string ResponseRootEffectId = string.Empty;
        public string Reason = string.Empty;
    }

    public sealed class DeferredDestinationResult
    {
        private DeferredDestinationResult(DeferredResponseStatus status, string zoneId, string diagnostic)
        { Status = status; DestinationZoneId = zoneId ?? string.Empty; Diagnostic = diagnostic ?? string.Empty; }
        public DeferredResponseStatus Status { get; }
        public string DestinationZoneId { get; }
        public string Diagnostic { get; }
        public bool Completed => Status != DeferredResponseStatus.Pending && !string.IsNullOrEmpty(DestinationZoneId) && string.IsNullOrEmpty(Diagnostic);
        public static DeferredDestinationResult Stored(string zoneId) => new DeferredDestinationResult(DeferredResponseStatus.Stored, zoneId, string.Empty);
        public static DeferredDestinationResult Returned(string zoneId) => new DeferredDestinationResult(DeferredResponseStatus.Returned, zoneId, string.Empty);
        public static DeferredDestinationResult Unresolved(string diagnostic) => new DeferredDestinationResult(DeferredResponseStatus.Pending, string.Empty, diagnostic);
    }

    public sealed class DeferredResponseDestinationRegistration
    {
        public delegate bool ValidationHandler(GameState state, DeferredResponseState response, out string diagnostic);
        public DeferredResponseDestinationRegistration(string destinationHandlerId, string abilityId,
            Func<EffectEventHandlerContext, EffectSpec, DeferredEventResponseDescriptor> describeDeferredResponse,
            Func<GameState, EffectNodeRuntimeState, DeferredResponseState, ItemTransferResult> adoptPendingDestination,
            Func<EffectExecutionContext, DeferredResponseState, DeferredDestinationResult> completeDeferredDestination,
            ValidationHandler tryValidateDeferredDestination)
        {
            if (string.IsNullOrEmpty(destinationHandlerId) || string.IsNullOrEmpty(abilityId)) throw new ArgumentException("旁路去向登记必须有稳定处理器和能力身份。");
            DestinationHandlerId = destinationHandlerId; AbilityId = abilityId;
            DescribeDeferredResponse = describeDeferredResponse ?? throw new ArgumentNullException(nameof(describeDeferredResponse));
            AdoptPendingDestination = adoptPendingDestination ?? throw new ArgumentNullException(nameof(adoptPendingDestination));
            CompleteDeferredDestination = completeDeferredDestination ?? throw new ArgumentNullException(nameof(completeDeferredDestination));
            TryValidateDeferredDestination = tryValidateDeferredDestination ?? throw new ArgumentNullException(nameof(tryValidateDeferredDestination));
        }
        public string DestinationHandlerId { get; }
        public string AbilityId { get; }
        public Func<EffectEventHandlerContext, EffectSpec, DeferredEventResponseDescriptor> DescribeDeferredResponse { get; }
        public Func<GameState, EffectNodeRuntimeState, DeferredResponseState, ItemTransferResult> AdoptPendingDestination { get; }
        public Func<EffectExecutionContext, DeferredResponseState, DeferredDestinationResult> CompleteDeferredDestination { get; }
        public ValidationHandler TryValidateDeferredDestination { get; }
    }
}

namespace YC.Domain.State
{
    [Serializable]
    public sealed class DeferredResponseState
    {
        public string DeferredResponseId = string.Empty;
        public string ResponseRootEffectId = string.Empty;
        public string SourceEventId = string.Empty;
        public string SourceDepartureEffectId = string.Empty;
        public string SourceCardInstanceId = string.Empty;
        public string AbilityId = string.Empty;
        public int AnsweringPlayerId = -1;
        public string OriginMainOperationWindowId = string.Empty;
        public int MainWindowPlayerIdAtCreation = -1;
        public string ReservedItemId = string.Empty;
        public string ReservationZoneId = string.Empty;
        public string OriginalSupplyZoneId = string.Empty;
        public string DestinationHandlerId = string.Empty;
        public YC.Domain.Effects.DeferredResponseStatus Status;
        public string ResolvedDestinationZoneId = string.Empty;
    }
}
