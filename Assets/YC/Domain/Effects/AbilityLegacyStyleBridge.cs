using System;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    /// <summary>把原有受授权样式原语转接到同一调用帧；不公开客户端跳费开关。</summary>
    public static class AbilityLegacyStyleBridge
    {
        public const string PolicyRef = "host.legacy_style.v1", AuthorizationPrefix = "legacy-style-effect:";
        public static AbilityActionCallPolicy Policy => new AbilityActionCallPolicy { PolicyRef = PolicyRef, OmitMainAction = true };
        public static EffectStepResult ExecuteActivation(EffectExecutionContext context)
        {
            if (!context.Registry.HasAbilityCatalog) return EffectStepResult.Failed("ability_catalog_required");
            if (context.Node.FlowStage.Length == 0) return EffectStepResult.Continue("unified_action").AddChild(Create(context.Node.PlayerId,
                Text(context.Node.NormalizedArguments, "specialActionId"), Text(context.Node.NormalizedArguments, "declarationMarkerId")));
            if (context.Node.FlowStage != "unified_action") throw new InvalidOperationException("legacy_style_bridge_stage_invalid");
            var child = context.State.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == context.Node.EffectId && IsBridge(n));
            return child.Status == EffectNodeStatus.Failed ? EffectStepResult.Failed(child.FailureReason) : EffectStepResult.Completed();
        }
        public static bool IsBridge(EffectNodeRuntimeState node) => node.EffectTypeId == AbilityActionExecutor.DirectTypeId && Flag(node.NormalizedArguments, "hostLegacyStyleGrant");
        public static EffectSpec Create(int player, string actionId, string markerId) => EffectSpec.Create(AbilityActionExecutor.DirectTypeId,
            Object("hostLegacyStyleGrant", NormalizedValue.CreateBoolean(true), "actionId", NormalizedValue.CreateString(actionId), "markerId", NormalizedValue.CreateString(markerId), "callPolicyRef", NormalizedValue.CreateString(PolicyRef)), player);
        private static bool ValidateParent(GameState state, EffectNodeRuntimeState activation, int player, string actionId, string markerId)
        {
            if (activation?.EffectTypeId != CityStyleSpecialActionEffectTypeIds.Activate || activation.PlayerId != player ||
                Text(activation.NormalizedArguments, "specialActionId") != actionId || Text(activation.NormalizedArguments, "declarationMarkerId") != markerId) return false;
            string mode = Text(activation.NormalizedArguments, "activationSource");
            var origin = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == activation.ParentEffectId && n.PlayerId == player);
            bool content = origin?.EffectTypeId == ContentTransactionEffectExecutor.Prefix + "activate_style" && !string.IsNullOrEmpty(origin.ContinuationContentHash) &&
                Text(origin.NormalizedArguments, "specialActionId") == actionId && Text(origin.NormalizedArguments, "declarationMarkerId") == markerId && Text(origin.NormalizedArguments, "activationSource") == mode;
            return (mode == "public_style" || mode == "immediate") && (content || mode == "immediate" && origin?.EffectTypeId == CharacterContentEffectExecutor.Prefix + "immediate_special");
        }
        public static AbilityActionOption QueryOption(EffectExecutionContext context)
        {
            var node = context.Node; var state = context.State; var registry = context.Registry;
            string actionId = Text(node.NormalizedArguments, "actionId"), markerId = Text(node.NormalizedArguments, "markerId");
            var activation = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == node.ParentEffectId);
            if (!IsBridge(node) || !ValidateParent(state, activation, node.PlayerId, actionId, markerId)) return null;
            var declaration = registry.AbilityDeclarations.SingleOrDefault(d => d.Actions.Any(a => a.ActionId == actionId));
            var action = declaration?.Actions.Single(a => a.ActionId == actionId);
            if (action?.DirectCallPolicy != PolicyRef) return null;
            var item = state.ItemZones.Items.SingleOrDefault(i => i.DefinitionId == declaration.DefinitionId && state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.city_style_display"));
            if (item == null) return null;
            bool publicSource = Text(activation.NormalizedArguments, "activationSource") == "public_style";
            if (publicSource && state.ContentDefinitions?.CityStyles.SingleOrDefault(s => s.CityStyleId == item.DefinitionId)?.Level != 1) return null;
            if (!publicSource && !registry.QuerySources(state, node.PlayerId).Any(s => s.ItemId == item.ItemId && s.AbilityId == declaration.AbilityId)) return null;
            var source = new AbilitySourceRef { ItemId = item.ItemId, DefinitionId = item.DefinitionId, AbilityId = declaration.AbilityId, DeclarationVersion = declaration.DeclarationVersion,
                AuthorizedPlayerId = node.PlayerId, AuthorizationRef = AuthorizationPrefix + activation.EffectId, EffectiveZoneId = item.ZoneId, RuleOwnerItemId = item.ItemId };
            source.SourceKey = StableIdFactory.Create("ability-source", item.ItemId, source.AbilityId, "", source.DeclarationVersion, node.PlayerId.ToString(CultureInfo.InvariantCulture));
            string binding = ""; string reason = registry.UsageProvider(action.UsageTrackId)?.Validate(state, source, action, out binding) ?? "usage_track_unavailable";
            string option = StableIdFactory.Create("ability-action", source.SourceKey, actionId);
            return new AbilityActionOption { OptionId = option, ActionId = actionId, SourceRef = source, TrackBindingId = binding, EntryKind = action.EntryKind,
                Enabled = reason.Length == 0, DisabledReason = reason, AuthorizationRef = source.AuthorizationRef,
                VersionRef = new AbilityVersionRef { ContentPackHash = registry.ContentPackHash, DefinitionVersion = source.DeclarationVersion, HandlerHash = registry.HandlerHash(action.BodyHandlerRef), ExecutorVersion = AbilityActionExecutor.Version },
                RequestRef = new AbilityActionRequestRef { InteractionId = StableIdFactory.Create("style-authorized-request", activation.EffectId, option), OwnerEffectId = activation.EffectId,
                    AnsweringPlayerId = node.PlayerId, WindowId = AbilitySourceService.WindowId(state, node.PlayerId), FactsDigest = AbilityActionExecutor.Digest(activation.NormalizedArguments), AllowedAnswers = new System.Collections.Generic.List<string> { option } } };
        }
        public static bool SourceCurrent(GameState state, AbilityActionInvocation invocation)
            => ValidateReceipt(state, invocation) && state.ItemZones.Items.Any(i => i.ItemId == invocation.SourceRef.ItemId && i.ZoneId == invocation.SourceRef.EffectiveZoneId);
        public static bool ValidateReceipt(GameState state, AbilityActionInvocation invocation)
        {
            var source = invocation.SourceRef;
            var node = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == invocation.OwnerEffectId);
            var activation = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == invocation.ParentEffectId);
            return node != null && IsBridge(node) && source.AuthorizationRef == AuthorizationPrefix + invocation.ParentEffectId &&
                ValidateParent(state, activation, source.AuthorizedPlayerId, invocation.ActionId, Text(node.NormalizedArguments, "markerId")) &&
                state.ItemZones.Items.Any(i => i.ItemId == source.ItemId && i.DefinitionId == source.DefinitionId) &&
                state.ItemZones.Zones.Any(z => z.ZoneId == source.EffectiveZoneId && z.TemplateId == "core.zone.city_style_display");
        }
        public static bool IsPublicGrant(GameState state, AbilitySourceRef source) => source.AuthorizationRef.StartsWith(AuthorizationPrefix, StringComparison.Ordinal) &&
            state.EffectRuntime.EffectNodes.Any(n => n.EffectId == source.AuthorizationRef.Substring(AuthorizationPrefix.Length) && Text(n.NormalizedArguments, "activationSource") == "public_style");
        public static string GrantedMarker(GameState state, AbilitySourceRef source) => source.AuthorizationRef.StartsWith(AuthorizationPrefix, StringComparison.Ordinal)
            ? Text(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == source.AuthorizationRef.Substring(AuthorizationPrefix.Length)).NormalizedArguments, "declarationMarkerId") : "";
    }
}
