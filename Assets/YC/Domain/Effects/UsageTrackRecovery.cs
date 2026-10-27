using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class UsageTrackRecovery
    {
        public static bool HasRawFields(JObject state)
        {
            if ((int?)state["EntitySchemaVersion"] != GameProtocolVersions.Entity) return true;
            if ((int?)state["ItemZones"]?["NamingVersion"] != PlayerAreaPolicy.NamingVersion || (int?)state["EffectRuntime"]?["UsageTrackProtocolVersion"] != 1) return false;
            var runtime = state["EffectRuntime"];
            foreach (var pair in new[] { new { Name = "UsageTrackBindings", Type = typeof(UsageTrackBinding) }, new { Name = "UsageGroupReceipts", Type = typeof(UsageGroupReceipt) }, new { Name = "UsageTrackResets", Type = typeof(UsageTrackResetProgress) } })
            {
                if (runtime[pair.Name] is not JArray values) return false;
                foreach (var value in values)
                    if (value is not JObject obj || pair.Type.GetFields().Any(f => obj[f.Name] == null || obj[f.Name].Type == JTokenType.Null)) return false;
            }
            return true;
        }
        public static bool Validate(GameState state, EffectRegistry registry, out string reason)
        {
            reason = "usage_tracks.v1:invalid"; var runtime = state?.EffectRuntime;
            if (runtime?.UsageTrackProtocolVersion != 1 || runtime.UsageTrackBindings == null || runtime.UsageGroupReceipts == null || runtime.UsageTrackResets == null) return false;
            if (state.ContentDefinitions != null && state.ContentDefinitions.UsageTracks == null) return false;
            if (runtime.UsageTrackBindings.Any(b => b == null) || runtime.UsageTrackBindings.Select(b => b.BindingId).Distinct().Count() != runtime.UsageTrackBindings.Count) return false;
            if (registry != null && runtime.UsageTrackBindings.Any(b => registry.UsageProvider(b.TrackId) is not DeclaredUsageProvider provider || !provider.Matches(UsageTracks.Declaration(state, b.TrackId)))) return false;
            foreach (var b in runtime.UsageTrackBindings)
            {
                var declaration = UsageTracks.Declaration(state, b.TrackId);
                var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == b.SourceItemId);
                var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == b.ZoneId);
                var marker = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == b.MarkerItemId);
                var initializer = runtime.EffectNodes.SingleOrDefault(n => n.EffectId == b.InitializationEffectId);
                if (b.ProtocolVersion != 1 || b.InitializedRoundNumber < 0 || b.InitializedRoundNumber > state.Round || b.BindingId != UsageTracks.BindingId(b.SourceItemId, b.TrackId, b.PlayerId) || declaration?.DefinitionId != item?.DefinitionId ||
                    declaration == null || b.DefinitionVersion != declaration.Version || state.FindPlayer(b.PlayerId) == null || initializer == null || initializer.PlayerId != b.PlayerId ||
                    !new[] { UsageTracks.InitializeTypeId, CityStyleDeclarationEffectExecutor.TypeId }.Contains(initializer.EffectTypeId) ||
                    b.InitializationReceiptId != StableIdFactory.Create("usage-initialized", initializer.EffectId, b.BindingId) ||
                    zone?.ParentId != b.SourceItemId || zone.ParentKind != ZoneParentKind.Item || zone.OwnerPlayerId != b.PlayerId || !zone.LegalSlotIds.SequenceEqual(declaration.Slots) || zone.Configuration == null || !zone.Configuration.Slots.SequenceEqual(declaration.Slots) ||
                    marker?.TypeId != "core.player_marker" || marker.OwnerSubject.Kind != ItemSubjectKind.Player || marker.OwnerSubject.PlayerId != b.PlayerId ||
                    !initializer.PositionChanges.Any(c => c.ItemId == b.MarkerItemId && c.To.ZoneId == b.ZoneId && c.To.SlotId == declaration.InitialSlotId)) return false;
                if (declaration.InitializationPolicy == "public_declaration" && state.FindPlayer(b.PlayerId).DeclaredCityStyles.FirstOrDefault(d => d.CityStyleItemId == b.SourceItemId)?.DeclarationId != b.AuthorizationRef) return false;
                if (declaration.InitializationPolicy == "owner_supply_or_retrieve" && (string.IsNullOrEmpty(b.AuthorizationRef) || ItemTransferEffectExecutor.Text(initializer.NormalizedArguments, "sourceItemId") != b.SourceItemId || ItemTransferEffectExecutor.Text(initializer.NormalizedArguments, "trackId") != b.TrackId)) return false;
            }
            if (runtime.UsageGroupReceipts.Any(r => r == null) || runtime.UsageGroupReceipts.GroupBy(r => r.RoundNumber + ":" + r.GroupKey).Any(g => g.Count() != 1)) return false;
            foreach (var receipt in runtime.UsageGroupReceipts)
            {
                var invocation = runtime.AbilityActionInvocations.SingleOrDefault(i => i.InvocationId == receipt.InvocationId);
                var declaration = state.ContentDefinitions?.AbilitySources.SelectMany(d => d.Actions).SingleOrDefault(a => a.ActionId == invocation?.ActionId);
                if (invocation == null || !invocation.ActivationCommitted || invocation.OwnerEffectId != receipt.OwnerEffectId || invocation.TrackBindingId != receipt.BindingId || receipt.RoundNumber <= 0 || receipt.RoundNumber > state.Round ||
                    declaration == null || receipt.PlayerId != invocation.SourceRef.AuthorizedPlayerId || receipt.GroupKey != UsageTracks.GroupKey(invocation.SourceRef.ItemId, declaration.UsageTrackId, receipt.PlayerId)) return false;
            }
            if (runtime.UsageTrackResets.Any(r => r == null) || runtime.UsageTrackResets.Select(r => r.RoundId).Distinct().Count() != runtime.UsageTrackResets.Count) return false;
            foreach (var reset in runtime.UsageTrackResets)
            {
                var owner = runtime.EffectNodes.SingleOrDefault(n => n.EffectId == reset.OwnerEffectId && n.EffectTypeId == UsageTracks.ResetTypeId);
                var boundary = runtime.MainNodes.SingleOrDefault(n => n.ExecutionEffectId == owner?.ParentEffectId && n.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded);
                if (reset.ProtocolVersion != 1 || boundary == null || boundary.RoundExecutionId != reset.RoundId || boundary.RoundNumber != reset.RoundNumber ||
                    !new[] { "waiting", "resetting", "completed" }.Contains(reset.Phase) || reset.ParticipantIds == null || reset.ParticipantIds.Count != state.Players.Count || !new HashSet<int>(reset.ParticipantIds).SetEquals(state.Players.Select(p => p.PlayerId)) ||
                    reset.RequiredObligationIds == null || reset.RequiredObligationIds.Any(id => !runtime.EffectNodes.Any(n => n.EffectId == id)) || reset.RequiredObligationIds.Distinct().Count() != reset.RequiredObligationIds.Count ||
                    reset.TargetBindingIds == null || reset.TargetBindingIds.Distinct().Count() != reset.TargetBindingIds.Count || reset.TargetBindingIds.Any(id => !runtime.UsageTrackBindings.Any(b => b.BindingId == id)) ||
                    reset.ProcessedBindingIds == null || reset.ProcessedBindingIds.Distinct().Count() != reset.ProcessedBindingIds.Count || reset.ProcessedBindingIds.Any(id => !reset.TargetBindingIds.Contains(id)) ||
                    reset.Phase == "completed" && reset.ProcessedBindingIds.Count != reset.TargetBindingIds.Count) return false;
                if (reset.Phase != "waiting" && !new HashSet<string>(reset.TargetBindingIds).SetEquals(runtime.UsageTrackBindings.Where(b => b.InitializedRoundNumber <= reset.RoundNumber).Select(b => b.BindingId))) return false;
            }
            reason = ""; return true;
        }
    }
}
