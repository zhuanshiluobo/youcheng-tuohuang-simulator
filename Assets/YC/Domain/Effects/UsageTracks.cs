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
    public static partial class UsageTracks
    {
        public const string Version = "2.0.0", InitializeTypeId = "effect.usage_track.initialize", ResetTypeId = "effect.usage_track.reset_all";
        public static string BindingId(string item, string track, int player) => StableIdFactory.Create("usage-binding", item, track, player.ToString(CultureInfo.InvariantCulture));
        public static string GroupKey(string item, string track, int player) => StableIdFactory.Create("usage-group", item, track, player.ToString(CultureInfo.InvariantCulture));
        public static UsageTrackDeclaration Declaration(GameState state, string track) => state.ContentDefinitions?.UsageTracks?.SingleOrDefault(d => d.TrackId == track);
        public static ItemInstanceState CurrentMarker(GameState state, UsageTrackBinding binding)
        {
            if (binding == null) return null;
            var declaration = Declaration(state, binding.TrackId);
            var zones = new HashSet<string>(state.ItemZones.Zones.Where(z =>
                PlayerMarkerTrackPolicy.IsUsageArea(z, binding.SourceItemId, declaration, binding.PlayerId)).Select(z => z.ZoneId), StringComparer.Ordinal);
            var markers = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.Kind == ItemSubjectKind.Player &&
                i.OwnerSubject.PlayerId == binding.PlayerId && zones.Contains(i.ZoneId)).ToList();
            if (markers.Count > 1) throw new InvalidOperationException("player_marker_track_duplicate");
            return markers.SingleOrDefault();
        }
        public static bool HasCurrentMarker(GameState state, string itemId) =>
            state.EffectRuntime.UsageTrackBindings.Any(b => CurrentMarker(state, b)?.ItemId == itemId);
        public static ItemInstanceState DeclarationMarker(GameState state, CityStyleDeclarationState declaration)
        {
            if (declaration == null) return null;
            var binding = state.EffectRuntime.UsageTrackBindings.SingleOrDefault(b => b.SourceItemId == declaration.CityStyleItemId &&
                state.Players.Any(p => p.PlayerId == b.PlayerId && p.DeclaredCityStyles.FirstOrDefault(d => d.CityStyleItemId == declaration.CityStyleItemId)?.DeclarationId == declaration.DeclarationId));
            return binding == null ? declaration.MarkerArea.Length == 0 ? null : state.ItemZones.Items.Find(i => i.ItemId == declaration.InfluenceMarkerId) : CurrentMarker(state, binding);
        }
        public static string PlacementAuthorization(string bindingId) => "marker-placement:" + bindingId;
        public static bool IsPlacementBinding(UsageTrackBinding binding) => binding?.ProtocolVersion == 2 && binding.AuthorizationRef == PlacementAuthorization(binding.BindingId);
        public static void RegisterDirectPlacements(EffectExecutionContext context, IEnumerable<MarkerMoveItemFactV4> moved)
        {
            foreach (var fact in moved.Where(f => f.From.ZoneId != f.To.ZoneId || f.From.SlotId != f.To.SlotId))
            {
                var zone = context.State.ItemZones.Zones.Single(z => z.ZoneId == fact.To.ZoneId);
                if (zone.ParentKind != ZoneParentKind.Item || zone.TemplateId != "core.zone.board_marker_track") continue;
                var carrier = context.State.ItemZones.Items.Single(i => i.ItemId == zone.ParentId);
                foreach (var declaration in context.State.ContentDefinitions?.UsageTracks.Where(d => d.DefinitionId == carrier.DefinitionId &&
                    PlayerMarkerTrackPolicy.IsUsageArea(zone, carrier.ItemId, d, fact.Owner.PlayerId)) ?? Enumerable.Empty<UsageTrackDeclaration>())
                {
                    if (context.Registry.UsageProvider(declaration.TrackId) is not DeclaredUsageProvider || !declaration.Slots.Contains(fact.To.SlotId)) continue;
                    string id = BindingId(carrier.ItemId, declaration.TrackId, fact.Owner.PlayerId);
                    if (context.State.EffectRuntime.UsageTrackBindings.Any(b => b.BindingId == id)) continue;
                    if (declaration.InitializationPolicy == "owner_supply_or_retrieve" &&
                        (carrier.OwnerSubject.Kind != ItemSubjectKind.Player || carrier.OwnerSubject.PlayerId != fact.Owner.PlayerId)) continue;
                    context.State.EffectRuntime.UsageTrackBindings.Add(new UsageTrackBinding { ProtocolVersion = 2, BindingId = id, SourceItemId = carrier.ItemId,
                        TrackId = declaration.TrackId, DefinitionVersion = declaration.Version, PlayerId = fact.Owner.PlayerId, ZoneId = zone.ZoneId, MarkerItemId = fact.ItemId,
                        AuthorizationRef = PlacementAuthorization(id), InitializationEffectId = context.Node.EffectId, InitializedRoundNumber = context.State.Round,
                        InitializationReceiptId = StableIdFactory.Create("usage-initialized", context.Node.EffectId, id) });
                }
            }
        }
        public static void Register(EffectRegistry registry, IEnumerable<UsageTrackDeclaration> declarations = null)
        {
            if (!registry.TryGet(InitializeTypeId, out _)) registry.Register(InitializeTypeId, Initialize, EffectExecutorKind.IntrinsicFlow);
            if (!registry.TryGet(ResetTypeId, out _))
            {
                registry.RegisterWithIndependentStateWrites(ResetTypeId, Reset, EffectExecutorKind.IntrinsicFlow);
                registry.Get(ResetTypeId).ExecutionPreservesJournalPrefix = true;
            }
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
            var marker = CurrentMarker(context.State, binding) ?? throw new InvalidOperationException("usage_marker_departed");
            new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = context.Node.PlayerId, ItemIds = new List<string> { marker.ItemId },
                ExpectedSource = new ItemPositionRef { ZoneId = marker.ZoneId, SlotId = marker.SlotId }, Destination = new ZoneRef { ZoneId = marker.ZoneId }, DestinationSlotId = slot,
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
            if (!CleanupObligationsComplete(runtime, progress)) return false;
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
                var marker = CurrentMarker(context.State, next);
                var transition = marker == null ? null : declaration.ResetTransitions.SingleOrDefault(t => t.FromSlotId == marker.SlotId);
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
            registry.RegisterWithIndependentStateWrites(ResetBindingTypeId, context =>
            {
                var progress = context.State.EffectRuntime.UsageTrackResets.SingleOrDefault(r => r.OwnerEffectId == context.Node.ParentEffectId);
                if (progress == null || !BarrierReady(context.State, progress, out _)) throw new InvalidOperationException("usage_reset_boundary_required");
                var binding = context.State.EffectRuntime.UsageTrackBindings.Single(b => b.BindingId == Text(context.Node.NormalizedArguments, "bindingId") && b.PlayerId == context.Node.PlayerId);
                if (progress.ProcessedBindingIds.Contains(binding.BindingId)) return EffectStepResult.Completed();
                var marker = CurrentMarker(context.State, binding);
                var transition = marker == null ? null : Declaration(context.State, binding.TrackId).ResetTransitions.SingleOrDefault(t => t.FromSlotId == marker.SlotId);
                // 已移出或已显式换状态时不拉回旧实例，也不将合法零成员变成回合故障。
                if (transition != null) Move(context, binding, transition.ToSlotId);
                progress.ProcessedBindingIds.Add(binding.BindingId); return EffectStepResult.Completed();
            });
            registry.Get(ResetBindingTypeId).ExecutionPreservesJournalPrefix = true;
        }
        public static IReadOnlyList<UsageTrackView> Project(GameState state, int player = -1) => state.EffectRuntime.UsageTrackBindings.Where(b => player < 0 || b.PlayerId == player).Select(b =>
        {
            var marker = CurrentMarker(state, b);
            return new UsageTrackView { Binding = GameStateCloneService.DeepClone(b), MarkerItemId = marker?.ItemId ?? "", ZoneId = marker?.ZoneId ?? "",
                SlotId = marker?.SlotId ?? "", DisabledReason = marker == null ? "usage_marker_departed" : "" };
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
            return new[] { InitializeTypeId, ResetBindingTypeId, AbilityActionExecutor.TypeId, AbilityActionExecutor.DirectTypeId, CityStyleDeclarationEffectExecutor.TypeId }.Contains(context.Node.EffectTypeId);
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
            var marker = UsageTracks.CurrentMarker(state, binding);
            if (marker == null) return "usage_marker_departed";
            var card = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == binding.SourceItemId);
            if (declaration.InitializationPolicy == "owner_supply_or_retrieve" && (card?.OwnerSubject.Kind != ItemSubjectKind.Player || card.OwnerSubject.PlayerId != binding.PlayerId)) return "usage_owner_changed";
            if (declaration.InitializationPolicy == "public_declaration" && !UsageTracks.IsPlacementBinding(binding) && !state.FindPlayer(binding.PlayerId).DeclaredCityStyles.Any(d => d.DeclarationId == binding.AuthorizationRef && d.InfluenceMarkerId == binding.MarkerItemId && d.CityStyleItemId == binding.SourceItemId && d.UnlockedSpecialActionId == action.ActionId)) return "style_authorization_missing";
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
                var marker = UsageTracks.CurrentMarker(context.State, binding) ?? throw new InvalidOperationException("usage_marker_departed");
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
