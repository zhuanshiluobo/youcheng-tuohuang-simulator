using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // 只提交已经确定的目标；选择、费用、奖励、次数和后继由正式Lua编排。
    public static class ContentTransactionEffectExecutor
    {
        public const string Prefix = "effect.content.transaction.";
        public static readonly Dictionary<string, string> Constructors = new Dictionary<string, string> {
            { "RecycleCharacterCover", "recycle_cover" }, { "BindCharacterToFacility", "bind_facility" },
            { "CreateMapToken", "create_token" },
            { "ActivateCityStyleSpecial", "activate_style" }
        };
        public static bool IsType(string type) => type != null && type.StartsWith(Prefix, StringComparison.Ordinal) && Constructors.Values.Contains(type.Substring(Prefix.Length));
        public static string[] Fields(string type)
        {
            var fields = new List<string> { "apiVersion", "player" };
            switch (type.Substring(Prefix.Length))
            {
                case "recycle_cover": fields.AddRange(new[] { "coveredItemIds", "sourceItemId" }); break;
                case "bind_facility": fields.AddRange(new[] { "characterItemId", "facilityItemId" }); break;
                case "create_token": fields.AddRange(new[] { "definitionId", "sourceItemId", "destinationLocationId", "operationProfileId" }); break;
                case "activate_style": fields.AddRange(new[] { "specialActionId", "declarationMarkerId", "activationSource" }); break;
            }
            return fields.ToArray();
        }
        public static string ValidateArguments(string type, NormalizedValue args)
        {
            if (!IsType(type) || args?.Kind != NormalizedValueKind.Object || Number(args, "apiVersion") != 1 ||
                CharacterLifecycleService.ParsePlayer(Text(args, "player")) < 1 || args.Properties.Any(p => !Fields(type).Concat(new[] { "kind", "effectTypeId" }).Contains(p.Name)))
                return "content_transaction_arguments_invalid";
            foreach (string field in Fields(type).Except(new[] { "apiVersion", "player" }))
            {
                var value = Value(args, field);
                if (field == "coveredItemIds")
                {
                    if (value?.Kind != NormalizedValueKind.Array || value.Items.Count > 16 || value.Items.Any(v => v.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(v.StringValue)) || value.Items.Select(v => v.StringValue).Distinct().Count() != value.Items.Count)
                        return "content_transaction_items_invalid";
                }
                else if (field == "levels") { if (value?.Kind != NormalizedValueKind.Integer || value.IntegerValue < 1 || value.IntegerValue > 16) return "content_transaction_levels_invalid"; }
                else if (value?.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(value.StringValue)) return "content_transaction_arguments_incomplete";
            }
            return "";
        }
        public static void Register(EffectRegistry registry)
        {
            foreach (string suffix in Constructors.Values)
            {
                string type = Prefix + suffix;
                if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, Execute,
                    suffix == "activate_style" ? EffectExecutorKind.IntrinsicFlow : EffectExecutorKind.Atomic, "1") {
                    Validator = spec => ValidateArguments(type, spec.NormalizedArguments) });
            }
        }
        public static bool TryValidateRuntime(GameState state, out string reason, EffectRegistry registry = null)
        {
            reason = "content_transaction_state_invalid";
            if (state?.EffectRuntime?.EffectNodes == null) return false;
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => IsType(n.EffectTypeId)))
            {
                if (ValidateArguments(node.EffectTypeId,node.NormalizedArguments).Length > 0 ||
                    Text(node.NormalizedArguments,"player") != node.PlayerId.ToString(CultureInfo.InvariantCulture) || state.FindPlayer(node.PlayerId) == null) return false;
                string stage = CharacterLifecycleService.GetResumableFlowStage(node);
                string allowed = node.EffectTypeId == Prefix + "activate_style" ? "activated" : "";
                if (stage.Length > 0 && stage != allowed) return false;
                if (!IsPending(node)) continue;
                try { BoundSource(new EffectExecutionContext(state,node,registry)); }
                catch (InvalidOperationException) { return false; }
            }
            reason = ""; return true;
        }
        private static bool IsPending(EffectNodeRuntimeState node) => node.Status != EffectNodeStatus.Completed && node.Status != EffectNodeStatus.Failed && node.Status != EffectNodeStatus.Faulted;
        public static string BoundSource(EffectExecutionContext context)
        {
            var root = context.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == context.Node.ContinuationScopeRootEffectId);
            if (root == null)
                for (var n = context.Node; n != null; n = context.State.EffectRuntime.EffectNodes.Find(p => p.EffectId == n.ParentEffectId))
                    if (n.ContinuationContext != null) { root = n; break; }
            // 真实分支不再制造选项值回调；公开样式可直接沿正式角色激活及其已验证收据证明来源。
            if (root?.ContinuationContext == null && context.Node.EffectTypeId == Prefix + "activate_style")
            {
                var seen = new HashSet<string>();
                for (var activation = context.Node; activation != null && seen.Add(activation.EffectId);
                    activation = context.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == activation.ParentEffectId))
                {
                    if (activation.EffectTypeId != CharacterAbilityEffectExecutor.ActivationEffectTypeId || activation.PlayerId != context.Node.PlayerId) continue;
                    string ability = Text(activation.NormalizedArguments, "abilityId"), mode = Text(activation.NormalizedArguments, "mode");
                    var use = context.State.CharacterUses.SingleOrDefault(u => u.UseId == Text(activation.NormalizedArguments, "useId") &&
                        u.OwnerEffectId == activation.ParentEffectId && u.ExecutingPlayerId == context.Node.PlayerId);
                    if (use == null || use.Finished || Value(activation.NormalizedArguments, "cardInstanceId")?.ReferenceId != use.CardInstanceId ||
                        !use.ModeOrder.Contains(mode) || !use.Modes.Any(m => m.Mode == mode && m.AbilityId == ability)) continue;
                    string eventId = StableIdFactory.Create("event", activation.EffectId, CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType);
                    if (!context.State.EffectRuntime.RuleEvents.Any(e => e.EventId == eventId && e.SourceEffectId == activation.EffectId &&
                        e.OwnerNodeId == activation.EffectId && e.RouteKey == ability && e.PlayerId == context.Node.PlayerId) ||
                        !context.State.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.EventId == eventId && r.AbilityId == ability &&
                            r.RouteTier == (int)EffectRouteTier.Targeted && r.AttachedEffectIds.Count > 0)) continue;
                    if (context.Registry != null)
                    {
                        if (!context.Registry.CharacterActivationSubscriptions.TryGetValue(ability, out string subscription)) continue;
                        var primary = context.Registry.FindEventHandler(subscription);
                        if (primary == null || !context.Registry.TryGetCharacterReceiptScope(context.State, context.Node, ability,
                            primary.ContentHash, primary.DefinitionVersion, context.Node.PlayerId, out _)) continue;
                    }
                    else if (!CharacterLifecycleService.GetModeOwnedEffects(context.State, use, activation).Contains(context.Node.EffectId)) continue;
                    return use.CardInstanceId;
                }
            }
            if (root?.ContinuationContext == null || string.IsNullOrEmpty(root.ContinuationContentHash) || root.PlayerId != context.Node.PlayerId)
                throw new InvalidOperationException("content_transaction_source_binding_required");
            string source = Text(root.ContinuationContext,"source");
            bool proven = false;
            for (var n = root; n != null; n = context.State.EffectRuntime.EffectNodes.Find(p => p.EffectId == n.ParentEffectId))
            {
                var card = Value(n.NormalizedArguments,"cardInstanceId");
                string id = card?.Kind == NormalizedValueKind.StableReference ? card.ReferenceId : card?.StringValue;
                if ((id == source || n.SourceId == "character-card:" + source || n.Settlement?.SourceItemId == source) && context.State.CharacterUses.Any(u => u.CardInstanceId == source && u.Modes.Any(m => m.AbilityId == root.ContinuationAbilityId && (m.Activated || n.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId && Text(n.NormalizedArguments,"abilityId") == root.ContinuationAbilityId)))) proven = true;
                if (Text(n.ContinuationData,"selectedEnterpriseItemId") == source && Text(n.ContinuationData,"selectedSpecialId") == root.ContinuationAbilityId &&
                    n.FrozenEnterpriseSpecials.Any(f => f.Candidate.EnterpriseItemId == source && f.Candidate.SpecialId == root.ContinuationAbilityId)) proven = true;
            }
            if (!proven) throw new InvalidOperationException("content_transaction_source_binding_required");
            return source;
        }
        private static EffectStepResult Execute(EffectExecutionContext c)
        {
            string error = ValidateArguments(c.Node.EffectTypeId, c.Node.NormalizedArguments);
            if (error.Length > 0 || Text(c.Node.NormalizedArguments, "player") != c.Node.PlayerId.ToString(CultureInfo.InvariantCulture))
                return EffectStepResult.Failed(error.Length > 0 ? error : "invalid_player");
            CharacterLifecycleService.ResumeDeferredDependencies(c);
            var args = c.Node.NormalizedArguments;
            string source = BoundSource(c), kind = c.Node.EffectTypeId.Substring(Prefix.Length);
            if (kind == "activate_style") return ActivateStyle(c);
            if (kind == "recycle_cover")
            {
                var ids = Value(args, "coveredItemIds").Items.Select(v => v.StringValue).ToList();
                if (Text(args, "sourceItemId") != source || ids.Any(id => !c.State.CharacterCoverSlots.Any(s => s.PlayerId == c.Node.PlayerId && s.CardInstanceId == id && s.Reusable && s.SourceEffectId == "character-card:" + source)))
                    return EffectStepResult.Failed("character_cover_source_invalid");
                return CharacterLifecycleService.Recycle(c, c.Node.PlayerId, false, ids);
            }
            if (kind == "bind_facility")
            {
                if (Text(args, "characterItemId") != source) return EffectStepResult.Failed("character_binding_source_invalid");
                var result = CharacterLifecycleService.BindFacility(c, source, Text(args, "facilityItemId"));
                if (result.PendingOutcome == EffectPendingOutcome.Failed) return result;
                return EffectStepResult.Completed(Object("bindingId", NormalizedValue.CreateString(c.State.FacilityCharacterBindings.Single(b => b.Active && b.CharacterItemId == source).BindingId)));
            }
            if (kind == "create_token")
            {
                string definition = Text(args, "definitionId"), location = Text(args, "destinationLocationId");
                var component = c.State.ContentDefinitions.Components.Find(d => d.DefinitionId == definition);
                if (Text(args, "sourceItemId") != source || component == null || Text(args, "operationProfileId") != "core.adjacent_location_token_move" ||
                    !c.State.Map.OpenLocationIds.Contains(location) || !c.State.Map.ResourceTokens.Any(t => t.LocationId == location)) return EffectStepResult.Failed("map_token_target_invalid");
                var existing = c.State.ItemZones.Items.Find(i => i.DefinitionId == definition && c.State.ItemZones.Relations.Any(r => r.RelationTypeId == "core.persistent_token_source" && r.SourceItemId == i.ItemId && r.TargetItemId == source));
                if (existing != null) return EffectStepResult.Completed(Object("tokenItemId", NormalizedValue.CreateString(existing.ItemId)));
                if (!new ItemZoneStateFactory().TryCreateGrantedItem(c.State, c.Node.EffectId, "persistent_token", definition, component.TypeId,
                    YC.Domain.Influence.InfluenceLockRules.TokenZone(c.State, location), ItemSubjectRef.Player(c.Node.PlayerId), out string id, out error)) return EffectStepResult.Failed(error);
                c.State.ItemZones.Relations.Add(new ItemRelationState { RelationId = StableIdFactory.Create("relation", id, source), RelationTypeId = "core.persistent_token_source", SourceItemId = id, TargetItemId = source });
                return EffectStepResult.Completed(Object("tokenItemId", NormalizedValue.CreateString(id)));
            }
            return EffectStepResult.Failed("content_transaction_arguments_invalid");
        }
        private static EffectStepResult ActivateStyle(EffectExecutionContext c)
        {
            if (c.Node.FlowStage == "activated") return c.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed) ? EffectStepResult.Failed("special_action_failed") : EffectStepResult.Completed();
            if (c.Node.FlowStage.Length > 0) return EffectStepResult.Failed("special_action_stage_invalid");
            var args = c.Node.NormalizedArguments;
            if (!new[] { "public_style", "immediate" }.Contains(Text(args, "activationSource"))) return EffectStepResult.Failed("special_action_source_invalid");
            var spec = CityStyleSpecialActionEffectSpecFactory.Activate(c.Node.PlayerId, Text(args, "specialActionId"), Text(args, "declarationMarkerId"));
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "activationSource", Value = NormalizedValue.CreateString(Text(args, "activationSource")) });
            spec.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return EffectStepResult.Continue("activated").AddChild(spec);
        }
    }
}
