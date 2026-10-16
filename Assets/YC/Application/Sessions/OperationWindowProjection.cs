using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public static class OperationWindowProjection
    {
        public static OperationWindowDescriptorDto ToDescriptor(GameState state, OperationWindowState window,
            long simulationEpoch, int membershipVersion, long openedAtGlobalSequence, long baseWindowSequence,
            IReadOnlyList<string> requiredReplicaIds, string prefixDigest, string windowContractDigest)
        {
            if (state?.EffectRuntime == null || window == null || window.Lifecycle != OperationWindowLifecycle.Open ||
                !state.EffectRuntime.OperationWindows.Any(w => w.OperationWindowId == window.OperationWindowId && w.WindowGeneration == window.WindowGeneration && w.Lifecycle == OperationWindowLifecycle.Open) ||
                simulationEpoch < 1 || membershipVersion < 1 || openedAtGlobalSequence < 0 || baseWindowSequence < 0 || requiredReplicaIds == null ||
                requiredReplicaIds.Count == 0 || requiredReplicaIds.Any(string.IsNullOrEmpty) || requiredReplicaIds.Distinct(StringComparer.Ordinal).Count() != requiredReplicaIds.Count)
                throw new ArgumentException("operation_window_projection_invalid");
            var actual = state.EffectRuntime.OperationWindows.Single(w => w.OperationWindowId == window.OperationWindowId);
            return new OperationWindowDescriptorDto {
                GameId = state.GameId, SimulationEpoch = simulationEpoch, MembershipVersion = membershipVersion,
                OperationWindowId = actual.OperationWindowId, WindowGeneration = actual.WindowGeneration,
                OpenedAtGlobalSequence = openedAtGlobalSequence, BaseWindowSequence = baseWindowSequence,
                AuthorizedPlayerIds = new List<int>(actual.AuthorizedPlayerIds), RequiredReplicaIds = requiredReplicaIds.ToList(),
                PrefixDigest = prefixDigest ?? string.Empty, WindowContractDigest = windowContractDigest ?? string.Empty
            };
        }
    }
}
