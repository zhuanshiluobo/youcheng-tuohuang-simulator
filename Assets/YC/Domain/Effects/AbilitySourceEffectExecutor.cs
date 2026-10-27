using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class AbilitySourceEffectExecutor
    {
        public const string TypeId = "effect.ability.source.resolve";
        public static void Register(EffectRegistry registry)
        { if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, "1.0.0")); }
        public static EffectSpec Create(AbilitySourceRef source, RuleEvent trigger, string handler, bool cleanup, int round) => EffectSpec.Create(TypeId,
            Object("sourceKey", NormalizedValue.CreateString(source.SourceKey), "itemId", NormalizedValue.CreateString(source.ItemId), "abilityId", NormalizedValue.CreateString(source.AbilityId),
                "authorizationRef", NormalizedValue.CreateString(source.AuthorizationRef), "handlerRef", NormalizedValue.CreateString(handler),
                "definitionId", NormalizedValue.CreateString(source.DefinitionId), "activeFaceId", NormalizedValue.CreateString(source.ActiveFaceId),
                "declarationVersion", NormalizedValue.CreateString(source.DeclarationVersion), "effectiveZoneId", NormalizedValue.CreateString(source.EffectiveZoneId),
                "grantingSourceRefs", Strings(source.GrantingSourceRefs), "triggerEventId", NormalizedValue.CreateString(trigger.EventId),
                "scopeKey", NormalizedValue.CreateString(cleanup ? "cleanup:" + round : "event:" + trigger.EventId),
                "triggerPayload", trigger.Payload?.Clone() ?? Object()), source.AuthorizedPlayerId);
        public static bool IsConsumed(GameState state, AbilitySourceRef source, string scope)
        {
            if (state.EffectRuntime.AbilitySourceUses.Any(r => r.SourceKey == source.SourceKey && r.ScopeKey == scope)) return true;
            // 兼容证据只读取实际旧收据，不从当前库存推断取得／使用历史。
            var acquisition = ContentLifecycleEffectExecutor.Acquisition(state, source.ItemId);
            return scope.StartsWith("cleanup:", StringComparison.Ordinal) && acquisition != null &&
                Text(acquisition.ContinuationData, "lastAwardCleanupKey") == scope.Substring("cleanup:".Length);
        }
        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var state = context.State; var node = context.Node;
            if (node.FlowStage == "body")
            {
                var completed = state.EffectRuntime.AbilitySourceUses.Single(r => r.OwnerEffectId == node.EffectId);
                completed.Outcome = "completed"; return EffectStepResult.Completed(node.RequiredOutcome==null ? null : YC.Domain.Facilities.FacilityBuildJournal.WrappedResult(state,node));
            }
            if (node.FlowStage.Length > 0) throw new InvalidOperationException("ability_source_phase_invalid");
            var declaration = context.Registry.AbilityDeclarations.SingleOrDefault(d => d.AbilityId == Text(node.NormalizedArguments, "abilityId"));
            if (declaration == null || declaration.HandlerRef != Text(node.NormalizedArguments, "handlerRef")) return EffectStepResult.Failed("ability_source_handler_invalid");
            var source = context.Registry.QuerySources(state, node.PlayerId).SingleOrDefault(s => s.SourceKey == Text(node.NormalizedArguments, "sourceKey"));
            // 已挂链义务有原来源证明；实际离场只撤销后续来源发现。
            if (source == null)
            {
                var item = state.ItemZones.Items.Find(i => i.ItemId == Text(node.NormalizedArguments, "itemId") && i.DefinitionId == declaration.DefinitionId);
                if (item == null || !state.EffectRuntime.DispatchReceipts.Any(r => r.AttachedEffectIds.Contains(node.EffectId) && r.EventId == Text(node.NormalizedArguments, "triggerEventId"))) return EffectStepResult.Failed("ability_source_receipt_missing");
                source = new AbilitySourceRef { SourceKey = Text(node.NormalizedArguments, "sourceKey"), ItemId = item.ItemId, DefinitionId = item.DefinitionId,
                    AbilityId = declaration.AbilityId, AuthorizedPlayerId = node.PlayerId, AuthorizationRef = Text(node.NormalizedArguments, "authorizationRef"), DeclarationVersion = declaration.DeclarationVersion,
                    ActiveFaceId = Text(node.NormalizedArguments, "activeFaceId"), EffectiveZoneId = Text(node.NormalizedArguments, "effectiveZoneId"), RuleOwnerItemId = item.ItemId,
                    GrantingSourceRefs = Value(node.NormalizedArguments, "grantingSourceRefs")?.Items.Select(v => v.StringValue).ToList() ?? new List<string>() };
            }
            string scope = Text(node.NormalizedArguments, "scopeKey");
            if (IsConsumed(state, source, scope)) return EffectStepResult.Completed();
            var receipt = new AbilitySourceUseReceipt { ReceiptId = StableIdFactory.Create("source-use", source.SourceKey, scope), SourceKey = source.SourceKey,
                ItemId = source.ItemId, AbilityId = source.AbilityId, ScopeKey = scope, OwnerEffectId = node.EffectId, PlayerId = node.PlayerId, Outcome = "accepted",
                SourceRef = GameStateCloneService.DeepClone(source), TriggerEventId = Text(node.NormalizedArguments, "triggerEventId"),
                VersionRef = new AbilityVersionRef { ContentPackHash = context.Registry.ContentPackHash, DefinitionVersion = declaration.DeclarationVersion, HandlerHash = context.Registry.HandlerHash(declaration.HandlerRef) } };
            state.EffectRuntime.AbilitySourceUses.Add(receipt);
            state.EffectRuntime.AbilityActionProtocolVersion = 1;
            var acquisition = ContentLifecycleEffectExecutor.Acquisition(state, source.ItemId);
            if (acquisition != null && scope.StartsWith("cleanup:", StringComparison.Ordinal)) Set(acquisition, "lastAwardCleanupKey", NormalizedValue.CreateString(scope.Substring("cleanup:".Length)));
            var body = context.Registry.ConstructAbilityBody(declaration.HandlerRef, new AbilityActionCallContext { State = state, OwnerNode = node, SourceRef = source, Arguments = Object() });
            var step = EffectStepResult.Continue("body"); foreach (var child in body) step.AddChild(child); return step;
        }
        public static void ObserveTerminal(GameState state, EffectNodeRuntimeState node, EffectRegistry registry)
        {
            if (node.EffectTypeId != TypeId || node.Status != EffectNodeStatus.Completed && node.Status != EffectNodeStatus.Failed ||
                state.EffectRuntime.AbilitySourceUses.Any(r => r.OwnerEffectId == node.EffectId) || !Text(node.NormalizedArguments, "scopeKey").StartsWith("cleanup:", StringComparison.Ordinal)) return;
            var declaration = registry.AbilityDeclarations.Single(d => d.AbilityId == Text(node.NormalizedArguments, "abilityId"));
            var args = node.NormalizedArguments;
            var source = new AbilitySourceRef { SourceKey = Text(args, "sourceKey"), ItemId = Text(args, "itemId"), DefinitionId = Text(args, "definitionId"), AbilityId = declaration.AbilityId,
                DeclarationVersion = declaration.DeclarationVersion, ActiveFaceId = Text(args, "activeFaceId"), AuthorizedPlayerId = node.PlayerId, AuthorizationRef = Text(args, "authorizationRef"),
                EffectiveZoneId = Text(args, "effectiveZoneId"), RuleOwnerItemId = Text(args, "itemId"), GrantingSourceRefs = Value(args, "grantingSourceRefs").Items.Select(v => v.StringValue).ToList() };
            string scope = Text(args, "scopeKey");
            state.EffectRuntime.AbilitySourceUses.Add(new AbilitySourceUseReceipt { ReceiptId = StableIdFactory.Create("source-use", source.SourceKey, scope), SourceKey = source.SourceKey,
                ItemId = source.ItemId, AbilityId = source.AbilityId, ScopeKey = scope, OwnerEffectId = node.EffectId, PlayerId = node.PlayerId, Outcome = "declined", SourceRef = source,
                TriggerEventId = Text(args, "triggerEventId"), VersionRef = new AbilityVersionRef { ContentPackHash = registry.ContentPackHash, DefinitionVersion = declaration.DeclarationVersion, HandlerHash = registry.HandlerHash(declaration.HandlerRef) } });
            var acquisition = ContentLifecycleEffectExecutor.Acquisition(state, source.ItemId);
            if (acquisition != null) Set(acquisition, "lastAwardCleanupKey", NormalizedValue.CreateString(scope.Substring("cleanup:".Length)));
        }
    }
}
