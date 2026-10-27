using System;
using System.Linq;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        public const string ImmediateSourceAuthorityRevision = "immediate_source_authority.r5.1";

        // 只在实际调用/续链时按当前玩家的立即收据核验，不做每帧全局状态重验。
        private bool TryResolveImmediateExecutionSource(GameState state, EffectNodeRuntimeState node, string invocationOwnerId,
            out AbilitySourceRef source, out AbilityVersionRef version)
        {
            source = null; version = null;
            if (state?.EffectRuntime == null || state.EffectRuntime.Status != EffectRuntimeStatus.Active ||
                state.ContentPackHash != ContentPackHash || !EntrepreneurFlow.Enabled(state) || node == null ||
                node.EffectTypeId != EntrepreneurFlow.EntryTypeId ||
                !ReferenceEquals(node, state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == node.EffectId)) ||
                node.Status != EffectNodeStatus.Running && node.Status != EffectNodeStatus.Blocked ||
                node.FlowStage != "" && node.FlowStage != "body" ||
                !TryGet(EntrepreneurFlow.EntryTypeId, out var registration) || node.DefinitionVersion != registration.DefinitionVersion)
                return false;
            string handler = Text(node.NormalizedArguments, "handlerRef"), key = Text(node.NormalizedArguments, "sourceKey");
            var current = QuerySources(state, node.PlayerId).SingleOrDefault(s => s.SourceKey == key);
            if (current == null || current.AuthorizedPlayerId != node.PlayerId) return false;
            var declaration = SourceDeclaration(current);
            var frozenSource = state.ContentDefinitions?.AbilitySources.SingleOrDefault(d => d.AbilityId == current.AbilityId && d.DefinitionId == current.DefinitionId);
            if (declaration.Kind != "setup" || declaration.HandlerRef != handler || HandlerHash(handler).Length == 0 ||
                GetAbilityHandlerVersion(handler).Length == 0 || frozenSource == null || frozenSource.Kind != declaration.Kind ||
                frozenSource.HandlerRef != handler || frozenSource.DeclarationVersion != declaration.DeclarationVersion || frozenSource.ActiveFaceId != declaration.ActiveFaceId)
                return false;
            var setup = state.EntrepreneurSetup;
            if (setup == null || setup.ProtocolVersion != 1 || setup.SetupProtocolVersion != 3 || setup.ContentVersion != ContentPackHash) return false;
            var player = setup.Players.SingleOrDefault(p => p.PlayerId == node.PlayerId);
            var definition = state.ContentDefinitions.Entrepreneurs.SingleOrDefault(d => d.DefinitionId == player?.DefinitionId);
            var owner = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == node.ParentEffectId && n.PlayerId == node.PlayerId);
            if (player == null || definition == null || owner == null || owner.FlowStage != "entries" ||
                !owner.ChildEffectIds.Contains(node.EffectId) || !TryGet(owner.EffectTypeId, out var ownerRegistration) ||
                owner.DefinitionVersion != ownerRegistration.DefinitionVersion || owner.Status != EffectNodeStatus.Running && owner.Status != EffectNodeStatus.Blocked)
                return false;
            System.Collections.Generic.List<EntrepreneurImmediateEntry> entries;
            System.Collections.Generic.List<string> handlers;
            if (owner.EffectTypeId == EntrepreneurFlow.PlayerTypeId && player.OwnerEffectId == owner.EffectId && current.ItemId == player.AbilityItemId)
            {
                entries = player.SetupEntries; handlers = definition.SetupHandlerRefs;
            }
            else if (owner.EffectTypeId == EntrepreneurFlow.FaceTypeId)
            {
                var group = player.RoundGroups.SingleOrDefault(g => g.OwnerEffectId == owner.EffectId && g.RoundNumber == state.Round);
                var selected = group?.Candidates.SingleOrDefault(c => c.CandidateId == group.SelectedCandidateId && c.FaceId == group.ActiveFaceId && c.EntityItemId == current.ItemId && c.DefinitionId == current.DefinitionId);
                var frozenGroup = definition.RoundGroups.SingleOrDefault(g => g.GroupId == group?.GroupId && g.RoundNumber == group.RoundNumber);
                var face = frozenGroup?.Candidates.SingleOrDefault(c => c.CandidateId == selected?.CandidateId && c.FaceId == selected.FaceId && c.EntityDefinitionId == selected.DefinitionId);
                var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == current.ItemId);
                if (selected == null || face == null || item == null || group.ActiveFaceId != current.ActiveFaceId ||
                    !AbilitySourceSelection.HasSelectedFaceReceipt(state, item, selected.FaceId, node.PlayerId) ||
                    Text(Value(item.LocalState, "sourceSelectionReceipt"), "requestId") != group.RequestId ||
                    Text(Value(item.LocalState, "sourceSelectionReceipt"), "ownerEffectId") != owner.EffectId)
                    return false;
                entries = group.ImmediateEntries; handlers = face.ImmediateHandlerRefs;
            }
            else return false;
            if (entries == null || handlers == null || entries.Count != handlers.Count) return false;
            var entry = entries.SingleOrDefault(e => e.EntryId == Text(node.NormalizedArguments, "entryId"));
            int index = entry == null ? -1 : entries.IndexOf(entry);
            if (index < 0 || handlers[index] != handler || entry.HandlerRef != handler || entry.SourceKey != key ||
                entry.OwnerEffectId != owner.EffectId || entry.EffectRootId != node.EffectId || entry.Status != "scheduled" || entry.ReceiptId.Length != 0 ||
                entry.EntryId != StableIdFactory.Create("entrepreneur-entry", owner.EffectId, handler, index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                return false;
            // 续链只能沿内核实际登记的子树到达该仍在执行的入口；不能靠ParentEffectId单向挂接借用来源。
            var descendant = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == invocationOwnerId);
            var visited = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            while (descendant != null && descendant.EffectId != node.EffectId && visited.Add(descendant.EffectId))
            {
                var parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == descendant.ParentEffectId);
                if (parent == null || !parent.ChildEffectIds.Contains(descendant.EffectId)) return false;
                descendant = parent;
            }
            if (!ReferenceEquals(descendant, node)) return false;
            source = GameStateCloneService.DeepClone(current);
            version = new AbilityVersionRef { ContentPackHash = ContentPackHash, DefinitionVersion = declaration.DeclarationVersion,
                HandlerHash = HandlerHash(handler), ExecutorVersion = EntrepreneurContent.Version };
            return true;
        }
    }
}
