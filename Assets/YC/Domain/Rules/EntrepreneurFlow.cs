using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Rules
{
    /// <summary>原准备树的可恢复子流程。卡面正文仅调用冻结的来源处理器。</summary>
    public static class EntrepreneurFlow
    {
        public const string SetupTypeId = "setup.entrepreneur.prepare", PlayerTypeId = "setup.entrepreneur.player";
        public const string PreviewTypeId = "setup.entrepreneur.preview", FaceTypeId = "round.entrepreneur.select", EntryTypeId = "effect.entrepreneur.entry";
        public const string ChooseInteraction = "setup.entrepreneur.choose", PreviewInteraction = "setup.enterprise.preview", FaceInteraction = "round.entrepreneur.choose";
        public static void Register(EffectRegistry registry)
        {
            foreach (var pair in new Dictionary<string, Func<EffectExecutionContext, EffectStepResult>> {
                { SetupTypeId, Setup }, { PlayerTypeId, PlayerSetup }, { PreviewTypeId, Preview }, { FaceTypeId, SelectFace }, { EntryTypeId, Entry } })
                if (!registry.TryGet(pair.Key, out _)) registry.Register(new EffectRegistration(pair.Key, pair.Value, EffectExecutorKind.IntrinsicFlow, EntrepreneurContent.Version));
        }
        public static bool Enabled(GameState state) => state.RuleConfiguration?.RuleModeId == "entrepreneur";
        public static EffectSpec SetupSpec(int player) => EffectSpec.Create(SetupTypeId, playerId: player);
        public static EffectSpec FaceSpec(int player) => EffectSpec.Create(FaceTypeId, playerId: player);
        private static string Key(string category, params string[] ids) => StableIdFactory.Create(category, ids);
        private static bool Terminal(EffectNodeRuntimeState n) => n.Status == EffectNodeStatus.Completed || n.Status == EffectNodeStatus.Failed;
        private static List<string> Ids(NormalizedValue v) => v?.Items?.Select(x => x.Kind == NormalizedValueKind.StableReference ? x.ReferenceId : x.StringValue).ToList() ?? new List<string>();
        private static EffectStepResult Choose(EffectExecutionContext c, string stage, string type, IEnumerable<string> ids, NormalizedValue parameters = null)
        {
            var request = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = c.Node.PlayerId, Visibility = "owner", PromptKey = type,
                AnswerSchema = "candidate_ids", MinSelections = 1, MaxSelections = 1, AllowDecline = false, PromptParameters = parameters ?? Object() };
            request.CandidateIds.AddRange(ids);
            return EffectStepResult.Continue(stage).AddInteraction(PrepareInteraction(c, request));
        }
        private static InteractionRequest Answered(EffectExecutionContext c) => c.State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.OwnerEffectId == c.Node.EffectId && r.AnsweringPlayerId == c.Node.PlayerId && r.Status == "answered");
        private static EffectStepResult Preview(EffectExecutionContext c)
        {
            c.Node.Visibility = GameStateVisibilityPolicy.Owner;
            if (c.Node.FlowStage == "") return Choose(c, "viewing", PreviewInteraction, new[] { "continue" });
            if (Answer(c) != "continue") throw new InvalidOperationException("entrepreneur_preview_answer_invalid");
            return EffectStepResult.Completed();
        }
        private static EffectStepResult Setup(EffectExecutionContext c)
        {
            if (!Enabled(c.State)) throw new InvalidOperationException("entrepreneur_mode_required");
            if (c.Node.FlowStage == "")
            {
                if (c.State.EntrepreneurSetup != null) throw new InvalidOperationException("entrepreneur_setup_duplicate");
                EntrepreneurContent.ValidateDefinitions(c.State.ContentDefinitions.Entrepreneurs);
                var parent = c.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == c.Node.ParentEffectId && n.EffectTypeId == ExpansionSetupFlow.PreparationTypeId);
                var order = c.State.EffectRuntime.PlayerOrderSnapshot.ToList(); int draw = order.Count == 2 ? 4 : 2;
                var candidates = Ids(Value(parent.ContinuationData, "candidateEnterpriseItemIds"));
                if (order.Count < 2 || order.Count > 4 || candidates.Count != draw * order.Count) throw new InvalidOperationException("entrepreneur_assignment_invalid");
                c.State.EntrepreneurSetup = new EntrepreneurSetupState { OwnerEffectId = c.Node.EffectId, ContentVersion = c.State.ContentPackHash,
                    PlayerOrder = order.AsEnumerable().Reverse().ToList(), RemainingEntrepreneurs = c.State.ContentDefinitions.Entrepreneurs.Select(d => d.DefinitionId).OrderBy(id => id, StringComparer.Ordinal).ToList(),
                    EnterpriseAssignments = order.Select((p, i) => new EnterpriseCandidateAssignment { PlayerId = p, CandidateItemIds = candidates.Skip(i * draw).Take(draw).ToList() }).ToList() };
                return EffectStepResult.Continue("previews").AddIndependentChildren("enterprise_candidate_previews", order.Select(p => EffectSpec.Create(PreviewTypeId, playerId: p)).ToList());
            }
            var setup = c.State.EntrepreneurSetup;
            if (c.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) throw new InvalidOperationException("entrepreneur_setup_child_failed");
            if (c.ChildNodes.Any(n => n.Status != EffectNodeStatus.Completed)) return EffectStepResult.NoProgress("等待企业家设置。");
            if (c.Node.FlowStage == "player") setup.CurrentIndex++;
            if (setup.CurrentIndex < setup.PlayerOrder.Count)
            {
                setup.Stage = "choosing";
                return EffectStepResult.Continue("player").AddChild(EffectSpec.Create(PlayerTypeId, playerId: setup.PlayerOrder[setup.CurrentIndex]));
            }
            var unselected = c.State.ContentDefinitions.Entrepreneurs.Where(d => setup.RemainingEntrepreneurs.Contains(d.DefinitionId)).ToList();
            var unusedIds = unselected.SelectMany(d => new[] { d.DefinitionId }.Concat(d.RoundGroups.SelectMany(g => g.Candidates).Select(f => f.EntityDefinitionId))
                .Concat(d.Components.Where(x => x.SourceRole == "reserve").Select(x => x.DefinitionId))).ToHashSet();
            foreach (var item in c.State.ItemZones.Items.Where(i => unusedIds.Contains(i.DefinitionId) && i.ZoneId == FindZone(c.State, "reserve").ZoneId).ToList())
                Move(c, item, FindZone(c.State, "retired").ZoneId);
            setup.Stage = "complete";
            return EffectStepResult.Completed();
        }
        private static EffectStepResult PlayerSetup(EffectExecutionContext c)
        {
            var setup = c.State.EntrepreneurSetup;
            if (setup == null || setup.Stage != "choosing" || setup.PlayerOrder[setup.CurrentIndex] != c.Node.PlayerId || c.Node.ParentEffectId != setup.OwnerEffectId)
                throw new InvalidOperationException("entrepreneur_setup_authority_invalid");
            if (c.Node.FlowStage == "") return Choose(c, "choosing", ChooseInteraction, setup.RemainingEntrepreneurs);
            var player = setup.Players.SingleOrDefault(p => p.PlayerId == c.Node.PlayerId);
            if (c.Node.FlowStage == "choosing")
            {
                var chosen = Answer(c);
                if (!setup.RemainingEntrepreneurs.Contains(chosen) || player != null || Answered(c)?.InteractionTypeId != ChooseInteraction) throw new InvalidOperationException("entrepreneur_choice_invalid");
                var definition = c.State.ContentDefinitions.Entrepreneurs.Single(d => d.DefinitionId == chosen);
                var item = Reserve(c.State, chosen, 1).Single();
                player = new EntrepreneurPlayerSetup { PlayerId = c.Node.PlayerId, DefinitionId = chosen, AbilityItemId = item.ItemId, OwnerEffectId = c.Node.EffectId };
                Assign(c, item, FindZone(c.State, "entrepreneur_active", c.Node.PlayerId).ZoneId); player.AllocatedItemIds.Add(item.ItemId);
                foreach (var group in definition.RoundGroups.OrderBy(g => g.RoundNumber))
                {
                    var entities = group.Candidates.Select(f => f.EntityDefinitionId).Distinct().ToDictionary(id => id, id => Reserve(c.State, id, 1).Single());
                    foreach (var entity in entities.Values) { Assign(c, entity, FindZone(c.State, "entrepreneur_pending", c.Node.PlayerId).ZoneId); player.AllocatedItemIds.Add(entity.ItemId); }
                    player.RoundGroups.Add(new RoundChoiceState { GroupId = group.GroupId, RoundNumber = group.RoundNumber,
                        Candidates = group.Candidates.OrderBy(f => f.DisplayOrder).Select(f => new RoundCandidateState { CandidateId = f.CandidateId, EntityItemId = entities[f.EntityDefinitionId].ItemId, DefinitionId = f.EntityDefinitionId, FaceId = f.FaceId, DisplayOrder = f.DisplayOrder }).ToList() });
                }
                foreach (var component in definition.Components)
                {
                    var destination = component.DestinationRole == "ability_card" ? c.State.ItemZones.Zones.Single(z => z.ParentId == item.ItemId && z.LocalZoneId == component.CarrierLocalZoneId).ZoneId : FindZone(c.State, component.DestinationRole, c.Node.PlayerId).ZoneId;
                    List<ItemInstanceState> items;
                    if (component.SourceRole == "supply")
                        items = PlayerAreaPolicy.Supply(c.State, c.Node.PlayerId, component.TypeId).Where(i => i.DefinitionId == component.DefinitionId).Take(component.Quantity).Select(i => c.State.ItemZones.Items.Single(x => x.ItemId == i.ItemId)).ToList();
                    else
                    {
                        var quota = c.State.SetupRecipe.Entries.SingleOrDefault(e => e.EntryId == component.EntryId && e.Condition.Kind == "entrepreneur_selected" && e.Condition.AbilityId == chosen);
                        if (quota == null || quota.DefinitionId != component.DefinitionId || quota.TypeId != component.TypeId || quota.Quantity != component.Quantity || quota.PerPlayer)
                            throw new InvalidOperationException("entrepreneur_component_recipe_mismatch:" + component.EntryId);
                        items = Enumerable.Range(0, quota.Quantity).Select(index => new ItemZoneStateFactory().AddItem(c.State, quota.DefinitionId, quota.TypeId, c.Node.EffectId + "/" + quota.EntryId, index, destination, ItemSubjectRef.Player(c.Node.PlayerId))).ToList();
                    }
                    if (items.Count != component.Quantity) throw new InvalidOperationException("entrepreneur_setup_insufficient_components:" + component.EntryId);
                    foreach (var componentItem in items)
                    {
                        if (component.SourceRole == "supply") Move(c, componentItem, destination);
                        if (component.DestinationRole == "supply" && componentItem.PhysicalKind != ItemPhysicalKind.Token) componentItem.OriginalSupplyZoneId = destination;
                        player.AllocatedItemIds.Add(componentItem.ItemId);
                    }
                }
                setup.Players.Add(player); setup.RemainingEntrepreneurs.Remove(chosen);
                player.SetupEntries = Entries(c.Node.EffectId, definition.SetupHandlerRefs);
                var step = EffectStepResult.Continue("initializing");
                foreach (var track in c.State.ContentDefinitions.UsageTracks.Where(t => t.DefinitionId == chosen)) step.AddChild(UsageTracks.CreateInitialization(c.Node.PlayerId, item.ItemId, track.TrackId));
                return step;
            }
            if (c.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) throw new InvalidOperationException("entrepreneur_required_setup_failed");
            if (c.ChildNodes.Any(n => n.Status != EffectNodeStatus.Completed)) return EffectStepResult.NoProgress("等待必做设置。");
            var next = Schedule(c, player.SetupEntries, player.AbilityItemId, true);
            if (next != null) return next;
            player.Stage = "complete"; player.SetupReceiptId = Key("entrepreneur-setup", c.Node.EffectId, player.DefinitionId);
            return EffectStepResult.Completed();
        }
        private static List<EntrepreneurImmediateEntry> Entries(string owner, IEnumerable<string> handlers) => handlers.Select((h, i) => new EntrepreneurImmediateEntry {
            OwnerEffectId = owner, EntryId = Key("entrepreneur-entry", owner, h, i.ToString(CultureInfo.InvariantCulture)), HandlerRef = h }).ToList();
        private static EffectStepResult SelectFace(EffectExecutionContext c)
        {
            var setup = c.State.EntrepreneurSetup;
            if (!Enabled(c.State) || setup?.Stage != "complete" || !new[] { 1, 3, 5, 7 }.Contains(c.State.Round)) throw new InvalidOperationException("entrepreneur_face_stage_invalid");
            var parent = c.State.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == c.Node.ParentEffectId);
            if (parent?.EffectTypeId != ExpansionSetupFlow.RoundPreparationTypeId || parent.FlowStage != "awaiting_entrepreneur_face" ||
                c.State.EffectRuntime.PlayerOrderSnapshot[Number(parent.ContinuationData, "nextIndex")] != c.Node.PlayerId)
                throw new InvalidOperationException("entrepreneur_face_authority_invalid");
            var player = setup.Players.Single(p => p.PlayerId == c.Node.PlayerId);
            var group = player.RoundGroups.Single(g => g.RoundNumber == c.State.Round);
            var definition = c.State.ContentDefinitions.Entrepreneurs.Single(d => d.DefinitionId == player.DefinitionId).RoundGroups.Single(g => g.GroupId == group.GroupId);
            if (c.Node.FlowStage == "")
            {
                if (group.SelectedCandidateId.Length > 0) throw new InvalidOperationException("entrepreneur_face_already_selected");
                if (definition.Candidates.Any(f => !c.Registry.AbilityDeclarations.Any(d => d.DefinitionId == f.EntityDefinitionId && d.ActiveFaceId == f.FaceId && d.AuthorizationPolicy == "active_selection"))) throw new InvalidOperationException("entrepreneur_face_rules_missing");
                group.OwnerEffectId = c.Node.EffectId;
                return Choose(c, "selecting", FaceInteraction, group.Candidates.Select(f => f.CandidateId), Object("abilitySourceCandidates", NormalizedValue.CreateArray(group.Candidates.Select(f =>
                    Object("candidateId", NormalizedValue.CreateString(f.CandidateId), "itemId", NormalizedValue.CreateString(f.EntityItemId), "definitionId", NormalizedValue.CreateString(f.DefinitionId), "faceId", NormalizedValue.CreateString(f.FaceId))).ToList())));
            }
            if (c.Node.FlowStage == "selecting")
            {
                var selected = group.Candidates.SingleOrDefault(f => f.CandidateId == Answer(c)); var request = Answered(c);
                if (selected == null || request?.InteractionTypeId != FaceInteraction || group.OwnerEffectId != c.Node.EffectId || group.SelectedCandidateId.Length > 0) throw new InvalidOperationException("entrepreneur_face_answer_invalid");
                var item = c.State.ItemZones.Items.Single(i => i.ItemId == selected.EntityItemId);
                if (item.ZoneId != FindZone(c.State, "entrepreneur_pending", c.Node.PlayerId).ZoneId) throw new InvalidOperationException("entrepreneur_face_entity_changed");
                AbilitySourceSelection.CommitSelectedFace(c, item.ItemId, selected.FaceId, request.GetStableInteractionId(), selected.CandidateId);
                Move(c, item, FindZone(c.State, "entrepreneur_active", c.Node.PlayerId).ZoneId);
                group.SelectedCandidateId = selected.CandidateId; group.ActiveFaceId = selected.FaceId; group.RequestId = request.GetStableInteractionId();
                foreach (var unused in group.Candidates.Where(f => f.EntityItemId != item.ItemId).Select(f => f.EntityItemId).Distinct())
                    Move(c, c.State.ItemZones.Items.Single(i => i.ItemId == unused), FindZone(c.State, definition.UnselectedEntityDestination).ZoneId);
                var shared = c.Registry.QuerySources(c.State, c.Node.PlayerId).Where(s => s.ItemId != player.AbilityItemId).ToList();
                AbilitySourceSelection.GrantSharedRules(c, player.AbilityItemId, shared.Where(s => player.RoundGroups.Any(g => g.Candidates.Any(f => f.EntityItemId == s.ItemId) && g.SelectedCandidateId.Length > 0)));
                group.ImmediateEntries = Entries(c.Node.EffectId, definition.Candidates.Single(f => f.CandidateId == selected.CandidateId).ImmediateHandlerRefs);
                var step = EffectStepResult.Continue("initializing");
                foreach (var track in c.State.ContentDefinitions.UsageTracks.Where(t => t.DefinitionId == selected.DefinitionId && c.Registry.AbilityDeclarations.Any(d => d.DefinitionId == t.DefinitionId && d.ActiveFaceId == selected.FaceId && d.Actions.Any(a => a.UsageTrackId == t.TrackId)))) step.AddChild(UsageTracks.CreateInitialization(c.Node.PlayerId, item.ItemId, track.TrackId));
                return step;
            }
            if (c.Node.FlowStage == "initializing")
            {
                if (c.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) throw new InvalidOperationException("entrepreneur_face_initialization_failed");
                if (c.ChildNodes.Any(n => !Terminal(n))) return EffectStepResult.NoProgress("等待使用轨道初始化。");
            }
            var result = Schedule(c, group.ImmediateEntries, group.Candidates.Single(f => f.CandidateId == group.SelectedCandidateId).EntityItemId, false);
            return result ?? EffectStepResult.Completed();
        }
        private static EffectStepResult Schedule(EffectExecutionContext c, List<EntrepreneurImmediateEntry> entries, string item, bool required)
        {
            foreach (var entry in entries)
            {
                var node = c.ChildNodes.SingleOrDefault(n => Text(n.NormalizedArguments, "entryId") == entry.EntryId);
                if (node != null)
                {
                    entry.EffectRootId = node.EffectId;
                    if (node.Status == EffectNodeStatus.Faulted || required && node.Status == EffectNodeStatus.Failed) throw new InvalidOperationException("entrepreneur_entry_failed");
                    if (!Terminal(node)) return EffectStepResult.NoProgress("等待独立效果。");
                    entry.Status = node.Status == EffectNodeStatus.Completed ? "completed" : "failed";
                    entry.ReceiptId = Key("entrepreneur-entry-receipt", entry.EntryId, node.Status.ToString());
                    continue;
                }
                if (entry.Status != "pending") throw new InvalidOperationException("entrepreneur_entry_receipt_missing");
                var sources = c.Registry.QuerySources(c.State, c.Node.PlayerId).Where(s => s.ItemId == item && c.Registry.AbilityDeclarations.Any(d => d.AbilityId == s.AbilityId && d.Kind == "setup" && d.HandlerRef == entry.HandlerRef)).ToList();
                if (sources.Count != 1 || c.Registry.HandlerHash(entry.HandlerRef).Length == 0) throw new InvalidOperationException("entrepreneur_handler_source_missing_or_ambiguous:" + entry.HandlerRef);
                var source = sources[0];
                entry.SourceKey = source.SourceKey; entry.Status = "scheduled";
                var spec = EffectSpec.Create(EntryTypeId, Object("entryId", NormalizedValue.CreateString(entry.EntryId), "handlerRef", NormalizedValue.CreateString(entry.HandlerRef), "sourceKey", NormalizedValue.CreateString(source.SourceKey)), c.Node.PlayerId);
                spec.StableKey = entry.EntryId;
                return EffectStepResult.Continue("entries").AddChild(spec);
            }
            return null;
        }
        private static EffectStepResult Entry(EffectExecutionContext c)
        {
            if (c.Node.FlowStage == "")
            {
                var source = c.Registry.QuerySources(c.State, c.Node.PlayerId).SingleOrDefault(s => s.SourceKey == Text(c.Node.NormalizedArguments, "sourceKey"));
                if (source == null || !c.Registry.AbilityDeclarations.Any(d => d.AbilityId == source.AbilityId && d.Kind == "setup" && d.HandlerRef == Text(c.Node.NormalizedArguments, "handlerRef"))) throw new InvalidOperationException("entrepreneur_entry_source_invalid");
                var entries = c.State.EntrepreneurSetup.Players.SelectMany(p => p.SetupEntries.Concat(p.RoundGroups.SelectMany(g => g.ImmediateEntries)));
                var entry = entries.SingleOrDefault(e => e.EntryId == Text(c.Node.NormalizedArguments, "entryId") && e.OwnerEffectId == c.Node.ParentEffectId && e.HandlerRef == Text(c.Node.NormalizedArguments, "handlerRef") && e.SourceKey == source.SourceKey);
                if (entry == null) throw new InvalidOperationException("entrepreneur_entry_authority_invalid");
                entry.EffectRootId = c.Node.EffectId;
                var body = c.Registry.ConstructAbilityBody(entry.HandlerRef, new AbilityActionCallContext { State = c.State, OwnerNode = c.Node, SourceRef = source, Arguments = Object() });
                var step = EffectStepResult.Continue("body"); foreach (var spec in body) step.AddChild(spec); return step;
            }
            if (c.ChildNodes.Any(n => n.Status == EffectNodeStatus.Faulted)) throw new InvalidOperationException("entrepreneur_body_faulted");
            if (c.ChildNodes.Any(n => !Terminal(n))) return EffectStepResult.NoProgress("等待独立效果正文。");
            return c.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed) ? EffectStepResult.Failed("entrepreneur_body_failed") : EffectStepResult.Completed();
        }
        private static List<ItemInstanceState> Reserve(GameState state, string definition, int count)
        {
            var items = state.ItemZones.Items.Where(i => i.DefinitionId == definition && i.ZoneId == FindZone(state, "reserve").ZoneId).OrderBy(i => i.ItemId, StringComparer.Ordinal).Take(count).ToList();
            if (items.Count != count) throw new InvalidOperationException("entrepreneur_reserve_insufficient:" + definition); return items;
        }
        private static void Assign(EffectExecutionContext c, ItemInstanceState item, string zone)
        {
            if (item.OwnerSubject.Kind == ItemSubjectKind.Player || item.ZoneId != FindZone(c.State, "reserve").ZoneId) throw new InvalidOperationException("entrepreneur_allocation_duplicate");
            item.OwnerSubject = ItemSubjectRef.Player(c.Node.PlayerId); item.ControllerSubject = ItemSubjectRef.Player(c.Node.PlayerId);
            foreach (var child in c.State.ItemZones.Zones.Where(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == item.ItemId)) child.OwnerPlayerId = c.Node.PlayerId;
            Move(c, item, zone);
        }
        private static void Move(EffectExecutionContext c, ItemInstanceState item, string zone)
        {
            var from = new ItemPositionRef { ZoneId = item.ZoneId, SlotId = item.SlotId };
            ItemZoneStateMigrator.SetPosition(c.State, item, zone);
            c.Node.PositionChanges.Add(new ItemPositionChange { ItemId = item.ItemId, From = from, To = new ItemPositionRef { ZoneId = zone, SlotId = item.SlotId } });
        }
    }
}
