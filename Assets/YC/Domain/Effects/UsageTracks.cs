using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    /// <summary>轨道位置只有ItemZones一份事实。调用使用002唯一激活步骤；初始化与全体复位是独立可恢复Effect。</summary>
    public static class UsageTracks
    {
        public const string Version = "1.0.0", InitializeTypeId = "effect.usage_track.initialize", ResetTypeId = "effect.usage_track.reset_all";
        public static string BindingId(string item, string track, int player) => StableIdFactory.Create("usage-binding", item, track, player.ToString(CultureInfo.InvariantCulture));
        public static string GroupKey(string item, string track, int player) => StableIdFactory.Create("usage-group", item, track, player.ToString(CultureInfo.InvariantCulture));
        public static UsageTrackDeclaration Declaration(GameState state, string track) => state.ContentDefinitions?.UsageTracks?.SingleOrDefault(d => d.TrackId == track);
        public static void Register(EffectRegistry registry, IEnumerable<UsageTrackDeclaration> declarations = null)
        {
            if (!registry.TryGet(InitializeTypeId, out _)) registry.Register(InitializeTypeId, Initialize, EffectExecutorKind.IntrinsicFlow);
            if (!registry.TryGet(ResetTypeId, out _)) registry.Register(ResetTypeId, Reset, EffectExecutorKind.IntrinsicFlow);
            foreach (var declaration in declarations ?? registry.ContentDefinitions?.UsageTracks ?? new List<UsageTrackDeclaration>())
            {
                var existing = registry.UsageProvider(declaration.TrackId);
                if (existing == null) registry.RegisterActionUsageProvider(declaration.TrackId, new DeclaredUsageProvider(declaration));
                else if (existing is not DeclaredUsageProvider declared || !declared.Matches(declaration)) throw new InvalidOperationException("usage_track_global_id_conflict");
            }
        }
        public static EffectSpec CreateInitialization(int player, string sourceItem, string track) => EffectSpec.Create(InitializeTypeId,
            Object("sourceItemId", NormalizedValue.CreateString(sourceItem), "trackId", NormalizedValue.CreateString(track)), player);
        public static EffectSpec CreateReset(int player) => EffectSpec.Create(ResetTypeId, playerId: player);
        public static UsageTrackBinding Bind(EffectExecutionContext context, UsageTrackDeclaration declaration, string itemId, string authorization, string zoneId, string markerId)
        {
            string id = BindingId(itemId, declaration.TrackId, context.Node.PlayerId);
            if (context.State.EffectRuntime.UsageTrackBindings.Any(b => b.BindingId == id)) throw new InvalidOperationException("usage_track_already_initialized");
            var marker = context.State.ItemZones.Items.Single(i => i.ItemId == markerId);
            var zone = context.State.ItemZones.Zones.Single(z => z.ZoneId == zoneId);
            if (zone.ParentKind != ZoneParentKind.Item || zone.ParentId != itemId || zone.OwnerPlayerId != context.Node.PlayerId || marker.ZoneId != zoneId ||
                marker.TypeId != "core.player_marker" || marker.OwnerSubject.Kind != ItemSubjectKind.Player || marker.OwnerSubject.PlayerId != context.Node.PlayerId || !declaration.Slots.Contains(marker.SlotId)) throw new InvalidOperationException("usage_track_binding_invalid");
            var result = new UsageTrackBinding { BindingId = id, SourceItemId = itemId, TrackId = declaration.TrackId, DefinitionVersion = declaration.Version, PlayerId = context.Node.PlayerId,
                AuthorizationRef = authorization, ZoneId = zoneId, MarkerItemId = markerId, InitializationEffectId = context.Node.EffectId, InitializedRoundNumber = context.State.Round,
                InitializationReceiptId = StableIdFactory.Create("usage-initialized", context.Node.EffectId, id) };
            context.State.EffectRuntime.UsageTrackBindings.Add(result); return result;
        }
        public static void Move(EffectExecutionContext context, UsageTrackBinding binding, string slot)
        {
            var marker = context.State.ItemZones.Items.Single(i => i.ItemId == binding.MarkerItemId);
            new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = context.Node.PlayerId, ItemIds = new List<string> { marker.ItemId },
                ExpectedSource = new ItemPositionRef { ZoneId = binding.ZoneId, SlotId = marker.SlotId }, Destination = new ZoneRef { ZoneId = binding.ZoneId }, DestinationSlotId = slot,
                InsertionKind = "slot", RequiredCount = 1, OperationProfileId = "core.player_marker_storage", CarrierPolicyId = "preserve" });
        }
        private static EffectStepResult Initialize(EffectExecutionContext context)
        {
            string itemId = Text(context.Node.NormalizedArguments, "sourceItemId"), track = Text(context.Node.NormalizedArguments, "trackId");
            var declaration = Declaration(context.State, track);
            var item = context.State.ItemZones.Items.SingleOrDefault(i => i.ItemId == itemId);
            if (declaration == null || declaration.DefinitionId != item?.DefinitionId || declaration.InitializationPolicy != "owner_supply_or_retrieve") return EffectStepResult.Failed("usage_initialization_not_permitted");
            var existing = context.State.EffectRuntime.UsageTrackBindings.SingleOrDefault(b => b.SourceItemId == itemId && b.TrackId == track);
            if (existing != null) return existing.InitializationEffectId == context.Node.EffectId ? EffectStepResult.Completed() : EffectStepResult.Failed("usage_track_already_initialized");
            if (context.State.EffectRuntime.EffectNodes.TakeWhile(n => n.EffectId != context.Node.EffectId).Any(n => n.EffectTypeId == InitializeTypeId && Text(n.NormalizedArguments, "sourceItemId") == itemId && Text(n.NormalizedArguments, "trackId") == track &&
                n.Status != EffectNodeStatus.Completed && n.Status != EffectNodeStatus.Failed)) return EffectStepResult.Failed("usage_initialization_in_progress");
            var source = context.Registry.QuerySources(context.State, context.Node.PlayerId).FirstOrDefault(s => s.ItemId == itemId && context.Registry.SourceDeclaration(s).Actions.Any(a => a.UsageTrackId == track));
            if (source == null || item.OwnerSubject.Kind != ItemSubjectKind.Player || item.OwnerSubject.PlayerId != context.Node.PlayerId) return EffectStepResult.Failed("usage_source_not_active");
            if (context.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) throw new InvalidOperationException("usage_retrieval_failed");
            var marker = PlayerAreaPolicy.Supply(context.State, context.Node.PlayerId, "core.player_marker").FirstOrDefault();
            if (marker == null)
            {
                // 只对此声明开放；使用正式移除请求，完整执行离场、收容及后续响应后再复验真实供应。
                var candidates = context.State.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.Kind == ItemSubjectKind.Player && i.OwnerSubject.PlayerId == context.Node.PlayerId &&
                    new ItemZoneQuery().IsInfluence(context.State, i.ItemId)).OrderBy(i => i.ItemId, StringComparer.Ordinal).Select(i => i.ItemId).ToList();
                if (candidates.Count == 0) throw new InvalidOperationException("usage_marker_supply_exhausted");
                return EffectStepResult.Continue("retrieving").AddChild(EffectSpec.Create(InfluenceEffectTypeIds.RemoveInfluence,
                    Object("executingPlayer", NormalizedValue.CreateStableReference("player", context.Node.PlayerId.ToString(CultureInfo.InvariantCulture)),
                        "candidateScope", NormalizedValue.CreateArray(candidates.Select(id => NormalizedValue.CreateStableReference("influence", id)).ToList()),
                        "destination", NormalizedValue.CreateString(YC.Domain.Influence.InfluenceIdentity.PlayerSupplySourceKind), "reasonId", NormalizedValue.CreateString("usage_track.initialization")), context.Node.PlayerId));
            }
            var zoneDeclaration = context.State.SetupRecipe?.CardZoneDeclarations.SingleOrDefault(d => d.DefinitionId == item.DefinitionId)?.CardZones.SingleOrDefault(z => z.LocalZoneId == declaration.CardZoneLocalId);
            var zone = context.State.ItemZones.Zones.SingleOrDefault(z => z.ParentId == itemId && z.LocalZoneId == declaration.CardZoneLocalId);
            if (zone == null && (zoneDeclaration == null || !new ItemZoneStateFactory().TryCreateCardZone(context.State, itemId, zoneDeclaration, out _, out _))) throw new InvalidOperationException("usage_zone_missing");
            zone = context.State.ItemZones.Zones.Single(z => z.ParentId == itemId && z.LocalZoneId == declaration.CardZoneLocalId);
            new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = context.Node.PlayerId, ItemIds = new List<string> { marker.ItemId },
                ExpectedSource = new ItemPositionRef { ZoneId = marker.ZoneId }, Destination = new ZoneRef { ZoneId = zone.ZoneId }, DestinationSlotId = declaration.InitialSlotId,
                InsertionKind = "slot", RequiredCount = 1, OperationProfileId = "core.player_marker_storage", CarrierPolicyId = "preserve" });
            Bind(context, declaration, itemId, source.AuthorizationRef, zone.ZoneId, marker.ItemId);
            return EffectStepResult.Completed();
        }
        public static bool BarrierReady(GameState state, UsageTrackResetProgress progress, out string reason)
        {
            reason = "usage_reset_waiting_for_all_cleanup";
            var runtime = state.EffectRuntime;
            foreach (int player in progress.ParticipantIds)
            {
                var cleanup = runtime.MainNodes.SingleOrDefault(n => n.RoundExecutionId == progress.RoundId && n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == player);
                if (cleanup == null || cleanup.Status != EffectNodeStatus.Completed) return false;
                if (!progress.RequiredObligationIds.Contains(cleanup.ExecutionEffectId)) progress.RequiredObligationIds.Add(cleanup.ExecutionEffectId);
            }
            if(runtime.CleanupObligations.Any(r=>r.DueRound<=progress.RoundNumber && progress.ParticipantIds.Contains(r.PlayerId) && r.Status!="completed")) return false;
            var roots = new HashSet<string>(progress.RequiredObligationIds);
            bool IsObligation(EffectNodeRuntimeState n)
            {
                var visited = new HashSet<string>();
                while (n != null && visited.Add(n.EffectId)) { if (roots.Contains(n.EffectId)) return true; n = runtime.EffectNodes.Find(p => p.EffectId == n.ParentEffectId); }
                return false;
            }
            foreach (var node in runtime.EffectNodes.Where(IsObligation).ToList()) if (!progress.RequiredObligationIds.Contains(node.EffectId)) progress.RequiredObligationIds.Add(node.EffectId);
            if (progress.RequiredObligationIds.Any(id => !runtime.EffectNodes.Any(n => n.EffectId == id && (n.Status == EffectNodeStatus.Completed || n.Status == EffectNodeStatus.Failed)))) return false;
            if (runtime.InfluenceDepartureBatches.Any(b => !b.Sealed) || runtime.DeferredResponses.Any(r => r.Status == DeferredResponseStatus.Pending || r.ProtocolVersion == 1 && r.Status == DeferredResponseStatus.Queued) || runtime.InteractionRequests.Any(r => r.Status == "open" && progress.RequiredObligationIds.Contains(r.OwnerEffectId))) return false;
            if (runtime.AbilityActionInvocations.Any(i => i.Phase != "completed" && progress.ParticipantIds.Contains(i.SourceRef.AuthorizedPlayerId))) return false;
            reason = ""; return true;
        }
        private static EffectStepResult Reset(EffectExecutionContext context)
        {
            var runtime = context.State.EffectRuntime;
            var roundEnd = runtime.MainNodes.SingleOrDefault(n => n.ExecutionEffectId == context.Node.ParentEffectId && n.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded);
            if (roundEnd == null) return EffectStepResult.Failed("usage_reset_boundary_required");
            var progress = runtime.UsageTrackResets.SingleOrDefault(r => r.RoundId == roundEnd.RoundExecutionId);
            if (progress == null)
            {
                progress = new UsageTrackResetProgress { RoundId = roundEnd.RoundExecutionId, RoundNumber = roundEnd.RoundNumber, OwnerEffectId = context.Node.EffectId,
                    ParticipantIds = runtime.PlayerOrderSnapshot.ToList() };
                runtime.UsageTrackResets.Add(progress);
            }
            if (progress.OwnerEffectId != context.Node.EffectId) return EffectStepResult.Failed("usage_reset_duplicate_boundary");
            if (!BarrierReady(context.State, progress, out var reason)) return EffectStepResult.NoProgress(reason);
            if (progress.Phase == "completed") return EffectStepResult.Completed();
            if (progress.Phase == "waiting") progress.TargetBindingIds = runtime.UsageTrackBindings.Select(b => b.BindingId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            progress.Phase = "resetting";
            var next = runtime.UsageTrackBindings.OrderBy(b => b.BindingId, StringComparer.Ordinal).FirstOrDefault(b => progress.TargetBindingIds.Contains(b.BindingId) && !progress.ProcessedBindingIds.Contains(b.BindingId));
            if (next != null)
            {
                var declaration = Declaration(context.State, next.TrackId) ?? throw new InvalidOperationException("usage_reset_definition_missing");
                var marker = context.State.ItemZones.Items.Single(i => i.ItemId == next.MarkerItemId);
                var transition = marker.ZoneId == next.ZoneId ? declaration.ResetTransitions.SingleOrDefault(t => t.FromSlotId == marker.SlotId) : null;
                if (transition != null)
                {
                    // 同一统一节点的玩家子式保留正常转移的所有者校验。
                    return EffectStepResult.Continue("resetting").AddChild(EffectSpec.Create(ResetBindingTypeId, Object("bindingId", NormalizedValue.CreateString(next.BindingId)), next.PlayerId));
                }
                progress.ProcessedBindingIds.Add(next.BindingId); return EffectStepResult.Continue("resetting");
            }
            foreach (var player in context.State.Players) player.UsedSpecialActionIdsThisRound.Clear();
            progress.Phase = "completed"; return EffectStepResult.Completed();
        }
        public const string ResetBindingTypeId = "effect.usage_track.reset_binding";
        public static void RegisterResetBinding(EffectRegistry registry)
        {
            if (registry.TryGet(ResetBindingTypeId, out _)) return;
            registry.Register(ResetBindingTypeId, context =>
            {
                var progress = context.State.EffectRuntime.UsageTrackResets.SingleOrDefault(r => r.OwnerEffectId == context.Node.ParentEffectId);
                if (progress == null || !BarrierReady(context.State, progress, out _)) throw new InvalidOperationException("usage_reset_boundary_required");
                var binding = context.State.EffectRuntime.UsageTrackBindings.Single(b => b.BindingId == Text(context.Node.NormalizedArguments, "bindingId") && b.PlayerId == context.Node.PlayerId);
                if (progress.ProcessedBindingIds.Contains(binding.BindingId)) return EffectStepResult.Completed();
                var marker = context.State.ItemZones.Items.Single(i => i.ItemId == binding.MarkerItemId);
                var transition = Declaration(context.State, binding.TrackId).ResetTransitions.SingleOrDefault(t => t.FromSlotId == marker.SlotId);
                if (marker.ZoneId != binding.ZoneId || transition == null) throw new InvalidOperationException("usage_reset_position_changed");
                Move(context, binding, transition.ToSlotId); progress.ProcessedBindingIds.Add(binding.BindingId); return EffectStepResult.Completed();
            });
        }
        public static IReadOnlyList<UsageTrackView> Project(GameState state, int player = -1) => state.EffectRuntime.UsageTrackBindings.Where(b => player < 0 || b.PlayerId == player).Select(b =>
        {
            var marker = state.ItemZones.Items.Single(i => i.ItemId == b.MarkerItemId);
            return new UsageTrackView { Binding = GameStateCloneService.DeepClone(b), SlotId = marker.ZoneId == b.ZoneId ? marker.SlotId : "", DisabledReason = marker.ZoneId == b.ZoneId ? "" : "usage_marker_departed" };
        }).ToList();
        public static bool AllowsTrackTransfer(EffectExecutionContext context, ItemTransferRequest request)
        {
            bool IsTrack(string zoneId)
            {
                var zone = context.State.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == zoneId);
                if (zone?.ParentKind != ZoneParentKind.Item || zone.TemplateId != "core.zone.board_marker_track") return false;
                var carrier = context.State.ItemZones.Items.Single(i => i.ItemId == zone.ParentId);
                return context.State.ContentDefinitions?.UsageTracks.Any(d => d.DefinitionId == carrier.DefinitionId &&
                    (zone.LocalZoneId == d.CardZoneLocalId || context.State.EffectRuntime.UsageTrackBindings.Any(b => b.ZoneId == zone.ZoneId) || zone.LocalZoneId.StartsWith("declaration_", StringComparison.Ordinal))) == true;
            }
            if (!IsTrack(request.ExpectedSource?.ZoneId) && !IsTrack(request.Destination?.ZoneId)) return true;
            return new[] { InitializeTypeId, ResetBindingTypeId, AbilityActionExecutor.TypeId, AbilityActionExecutor.DirectTypeId, CityStyleDeclarationEffectExecutor.TypeId,
                ContentLifecycleEffectExecutor.ReturnMarkerTypeId }.Contains(context.Node.EffectTypeId);
        }
    }
    public sealed class DeclaredUsageProvider : IAbilityActionUsageProvider
    {
        private readonly UsageTrackDeclaration declaration;
        public DeclaredUsageProvider(UsageTrackDeclaration value) { declaration = GameStateCloneService.DeepClone(value); }
        public bool Matches(UsageTrackDeclaration value) => Newtonsoft.Json.JsonConvert.SerializeObject(declaration) == Newtonsoft.Json.JsonConvert.SerializeObject(value);
        public string DefinitionVersion => UsageTracks.Version + "/" + declaration.Version;
        public string Validate(GameState state, AbilitySourceRef source, AbilityActionDeclaration action, out string bindingId)
        {
            string group = UsageTracks.GroupKey(source.ItemId, declaration.TrackId, source.AuthorizedPlayerId);
            bool publicGrant = AbilityLegacyStyleBridge.IsPublicGrant(state, source);
            var binding = state.EffectRuntime.UsageTrackBindings.SingleOrDefault(b => b.SourceItemId == source.ItemId && b.TrackId == declaration.TrackId && b.PlayerId == source.AuthorizedPlayerId);
            bindingId = publicGrant ? group : binding?.BindingId ?? "";
            if (source.DefinitionId != declaration.DefinitionId || action.UsageTrackId != declaration.TrackId) return "usage_track_definition_mismatch";
            if (state.EffectRuntime.UsageGroupReceipts.Any(r => r.RoundNumber == state.Round && r.GroupKey == group)) return "style_action_used_this_round";
            if (publicGrant) return "";
            if (binding == null) return "usage_track_not_initialized";
            if (binding.DefinitionVersion != declaration.Version) return "usage_track_version_mismatch";
            var marker = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == binding.MarkerItemId);
            if (marker == null || marker.ZoneId != binding.ZoneId || marker.OwnerSubject.Kind != ItemSubjectKind.Player || marker.OwnerSubject.PlayerId != binding.PlayerId) return "usage_marker_departed";
            var card = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == binding.SourceItemId);
            if (declaration.InitializationPolicy == "owner_supply_or_retrieve" && (card?.OwnerSubject.Kind != ItemSubjectKind.Player || card.OwnerSubject.PlayerId != binding.PlayerId)) return "usage_owner_changed";
            if (declaration.InitializationPolicy == "public_declaration" && !state.FindPlayer(binding.PlayerId).DeclaredCityStyles.Any(d => d.DeclarationId == binding.AuthorizationRef && d.InfluenceMarkerId == binding.MarkerItemId && d.CityStyleItemId == binding.SourceItemId && d.UnlockedSpecialActionId == action.ActionId)) return "style_authorization_missing";
            return declaration.ActivationTransitions.Any(t => t.FromSlotId == marker.SlotId) ? "" : "style_track_exhausted";
        }
        public void Activate(EffectExecutionContext context, AbilityActionInvocation invocation, AbilityActionDeclaration action)
        {
            string reason = Validate(context.State, invocation.SourceRef, action, out string bindingId);
            if (reason.Length != 0 || bindingId != invocation.TrackBindingId) throw new InvalidOperationException(reason.Length > 0 ? reason : "usage_binding_changed");
            var source = invocation.SourceRef;
            if (!AbilityLegacyStyleBridge.IsPublicGrant(context.State, source))
            {
                var binding = context.State.EffectRuntime.UsageTrackBindings.Single(b => b.BindingId == bindingId);
                var marker = context.State.ItemZones.Items.Single(i => i.ItemId == binding.MarkerItemId);
                UsageTracks.Move(context, binding, declaration.ActivationTransitions.Single(t => t.FromSlotId == marker.SlotId).ToSlotId);
            }
            context.State.EffectRuntime.UsageGroupReceipts.Add(new UsageGroupReceipt { GroupKey = UsageTracks.GroupKey(source.ItemId, declaration.TrackId, source.AuthorizedPlayerId), RoundNumber = context.State.Round,
                PlayerId = source.AuthorizedPlayerId, BindingId = bindingId, InvocationId = invocation.InvocationId, OwnerEffectId = context.Node.EffectId });
            if (declaration.InitializationPolicy == "public_declaration")
            {
                var used = context.State.FindPlayer(source.AuthorizedPlayerId).UsedSpecialActionIdsThisRound;
                if (!used.Contains(action.ActionId)) used.Add(action.ActionId);
            }
        }
    }
}
