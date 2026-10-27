using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    /// <summary>本次授权调用的纯来源快照；完整集合留在VM读取侧，分页不进入状态广播。</summary>
    public static class SourceFactsLuaCatalog
    {
        public const string Version = "source_facts.v1.6.0.0";
        public const int PageSize = 4;
        private sealed class HostCall
        {
            internal EffectRegistry Registry;
            internal AbilityActionCallContext Call;
            internal GameState State;
            internal AbilitySourceRef Source;
            internal EffectNodeRuntimeState Owner;
            internal AbilityActionDeclaration Action;
            internal bool Preview;
            internal string HandlerId, HandlerHash, HandlerVersion;
        }
        // 只由私有目录入口登记本次上下文对象；SourceKey、事件 payload 和同名 owner 都不能制造此身份。
        private static readonly ConditionalWeakTable<EffectEventHandlerContext, HostCall> HostCalls = new ConditionalWeakTable<EffectEventHandlerContext, HostCall>();
        internal static void BindHostCall(EffectEventHandlerContext context, AbilityActionCallContext call, LuaHandlerDefinition handler, EffectRegistry registry)
        {
            HostCalls.Add(context, new HostCall { Registry = registry, HandlerId = handler.Script.HandlerId, HandlerHash = handler.Script.ContentHash,
                HandlerVersion = handler.Script.DefinitionVersion,
                Call = call, State = call.State, Source = GameStateCloneService.DeepClone(call.SourceRef), Owner = call.OwnerNode, Action = call.Action,
                Preview = registry.TryGetSourceFactsPreviewEvent(call, out _) });
        }

        public static LuaSnapshotValue ForSettlement(GameState state, EffectRegistry registry, SettlementWindowContext window,
            SettlementEntryRuntimeState entry)
        {
            if (state?.EffectRuntime == null || registry == null || window == null || entry == null || window.Kind != SettlementEntryService.PlayerCleanup ||
                entry.EntryKind != SettlementEntryService.CleanupOrder || window.StateRevision != state.EffectRuntime.StateRevision)
                return LuaSnapshotValue.Null;
            var current = registry.QuerySources(state, window.PlayerId).SingleOrDefault(s => s.ItemId == entry.SourceItemId && s.AbilityId == entry.AbilityId);
            if (current == null || !registry.HasAbilitySourceKind(current.AbilityId, "cleanup") ||
                !string.IsNullOrEmpty(entry.SourceDefinitionId) && entry.SourceDefinitionId != current.DefinitionId)
                return LuaSnapshotValue.Null;
            var original = entry.CleanupSource?.SourceRef;
            if (entry.CleanupSource != null && original == null) return LuaSnapshotValue.Null;
            if (original != null && (original.SourceKey != current.SourceKey || original.ItemId != current.ItemId ||
                original.DefinitionId != current.DefinitionId || original.AbilityId != current.AbilityId ||
                original.AuthorizedPlayerId != current.AuthorizedPlayerId || original.DeclarationVersion != current.DeclarationVersion ||
                original.AuthorizationRef != current.AuthorizationRef || original.EffectiveZoneId != current.EffectiveZoneId))
                return LuaSnapshotValue.Null;
            return Create(state, registry, current);
        }

        public static LuaSnapshotValue Create(GameState state, EffectRegistry registry, AbilitySourceRef owner,bool includeCallableActions=true,string executionHandlerHash="",string executionOwnerId="")
        {
            if (executionHandlerHash.Length > 0 && (state?.EffectRuntime == null || registry == null || owner == null || executionOwnerId.Length == 0 ||
                !registry.TryResolveAbilityExecutionHandler(state, executionOwnerId, owner.AbilityId, executionHandlerHash, out var executing, out _) || !Same(executing, owner))) return LuaSnapshotValue.Null;
            return CreateCurrent(state, registry, owner, includeCallableActions, executionHandlerHash, executionOwnerId);
        }
        private static LuaSnapshotValue CreateCurrent(GameState state, EffectRegistry registry, AbilitySourceRef owner, bool includeCallableActions,
            string executionHandlerHash = "", string executionOwnerId = "")
        {
            if (state?.EffectRuntime == null || registry == null || owner == null || state.ContentPackHash != registry.ContentPackHash)
                return LuaSnapshotValue.Null;
            // 原key不解析、不重算；来源及grant来自同一次完整资格解析。
            var all = registry.QuerySources(state, owner.AuthorizedPlayerId).OrderBy(s => s.SourceKey, StringComparer.Ordinal).ToList();
            var current = all.SingleOrDefault(s => Same(s, owner));
            if (current == null) return LuaSnapshotValue.Null;
            var byKey = all.ToDictionary(s => s.SourceKey, StringComparer.Ordinal);
            var grants = current.GrantingSourceRefs.Select(key => byKey.TryGetValue(key, out var source) ? source : null).ToList();
            if (grants.Any(s => s == null || s.AuthorizedPlayerId != current.AuthorizedPlayerId))
                throw new InvalidOperationException("source_facts_grant_invalid");
            var callable=new List<LuaSnapshotValue>();
            var sourceOperations = registry.GetAbilitySourceOperations(current.AbilityId);
            string callerHash=executionHandlerHash.Length>0?executionHandlerHash:registry.GetAbilitySourceHandlerHash(current.AbilityId);
            if(includeCallableActions) foreach(string policy in registry.ActionCallPoliciesFor(current.AbilityId,callerHash))
                foreach(var option in registry.PreviewAuthorizedActions(state,current,callerHash,policy,executionOwnerId))
                    callable.Add(O(E("optionId",S(option.OptionId)),E("itemId",S(option.SourceRef.ItemId)),E("sourceKey",S(option.SourceRef.SourceKey)),
                        E("actionId",S(option.ActionId)),E("callPolicyRef",S(policy)),E("enabled",LuaSnapshotValue.Boolean(option.Enabled)),E("disabledReason",S(option.DisabledReason)),
                        E("trackBindingId",S(option.TrackBindingId)),E("factsDigest",S(option.RequestRef.FactsDigest)),E("handlerHash",S(option.VersionRef.HandlerHash))));
            var payable=new HashSet<string>(StringComparer.Ordinal);
            var transferPermissions=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
            // 保留payableMarkerCount查询字段兼容旧脚本；这里只报告获授权token库存，不决定费用或选择。
            foreach(var permission in sourceOperations.Where(p=>p.StartsWith("carrier.transfer.",StringComparison.Ordinal) && CardMarkerCarrier.ValidPermission(p)))
            {
                var parts=permission.Split(':');
                foreach(var card in state.ItemZones.Items.Where(i=>i.DefinitionId==parts[1]))
                {
                    var zone=registry.QueryAuthorizedCarrierZone(state,current,current.AuthorizedPlayerId,card.ItemId,parts[2],parts[0].Substring("carrier.".Length),out var types,out _);
                    if(zone!=null) foreach(var item in state.ItemZones.Items.Where(i=>i.ZoneId==zone.ZoneId && CardMarkerCarrier.AcceptsTransfer(state,zone,i,current.AuthorizedPlayerId,types)))
                    {
                        payable.Add(item.ItemId);
                        if(!transferPermissions.TryGetValue(item.ItemId,out var operations)) transferPermissions[item.ItemId]=operations=new HashSet<string>(StringComparer.Ordinal);
                        operations.Add(parts[0].Substring("carrier.".Length));
                    }
                }
            }
            return O(E("protocolVersion", N(1)), E("stateRevision", N(state.EffectRuntime.StateRevision)),
                E("callableActions",A(callable.OrderBy(v=>v.Get("optionId").StringValue,StringComparer.Ordinal))),E("payableMarkerCount",N(payable.Count)),
                E("transferableTokenCount",N(payable.Count)),E("transferableTokens",A(payable.OrderBy(id=>id,StringComparer.Ordinal).Select(id=> {
                    var item=state.ItemZones.Items.Single(i=>i.ItemId==id); var zone=state.ItemZones.Zones.Single(z=>z.ZoneId==item.ZoneId);
                    return O(E("itemId",S(id)),E("typeId",S(item.TypeId)),E("physicalKind",S(item.PhysicalKind.ToString())),
                        E("zoneId",S(item.ZoneId)),E("carrierItemId",S(zone.ParentId)),E("localZoneId",S(zone.LocalZoneId)),
                        E("originalSupplyZoneId",S(item.OriginalSupplyZoneId)),E("ownerPlayerId",S(item.OwnerSubject.Kind==ItemSubjectKind.Player?Player(item.OwnerSubject.PlayerId):"")),
                        E("ownerKind",S(item.OwnerSubject.Kind.ToString())),E("ownerCompanyId",S(item.OwnerSubject.CompanyId)),
                        E("controllerKind",S(item.ControllerSubject.Kind.ToString())),E("controllerPlayerId",S(item.ControllerSubject.Kind==ItemSubjectKind.Player?Player(item.ControllerSubject.PlayerId):"")),
                        E("controllerCompanyId",S(item.ControllerSubject.CompanyId)),
                        E("ownerSubject",S(LuaQuerySnapshotBuilder.Subject(item.OwnerSubject))),E("controller",S(LuaQuerySnapshotBuilder.Subject(item.ControllerSubject))),
                        E("localState",Snapshot(item.LocalState)),E("hasOpenDeparture",LuaSnapshotValue.Boolean(state.EffectRuntime.InfluenceDepartures.Any(d=>d.ItemId==id && d.Status=="open"))),
                        E("localStateDigest",S(ItemTransferService.LocalStateDigest(item))),E("operations",A(transferPermissions[id].OrderBy(p=>p,StringComparer.Ordinal).Select(S))));
                }))),
                E("contentHash", S(state.ContentPackHash)), E("authorizedPlayerId", S(Player(current.AuthorizedPlayerId))),
                E("ruleOwnerSourceKey", S(current.SourceKey)), E("ruleOwnerItemId", S(current.RuleOwnerItemId)),
                E("sources", A(all.Select(s => Fact(state, registry, s)))), E("grants", A(grants.Select(s => Fact(state, registry, s)))));
        }

        internal static LuaSnapshotValue ForInvocation(GameState state, EffectRegistry registry, LuaHandlerDefinition handler,
            EffectEventHandlerContext context, int player)
        {
            if (context == null || state?.EffectRuntime == null || registry == null || !ReferenceEquals(context.State, state)) return LuaSnapshotValue.Null;
            if (HostCalls.TryGetValue(context, out var host))
            {
                if (!ReferenceEquals(host.Registry, registry) || host.HandlerId != handler.Script.HandlerId || host.HandlerHash != handler.Script.ContentHash ||
                    host.HandlerVersion != handler.Script.DefinitionVersion || host.HandlerVersion != registry.GetAbilityHandlerVersion(host.HandlerId) ||
                    !ReferenceEquals(host.State, state) || !ReferenceEquals(host.Call.State, state) || !ReferenceEquals(host.Owner, host.Call.OwnerNode) ||
                    !ReferenceEquals(host.Action, host.Call.Action) || host.Call.SourceRef == null || !Same(host.Source, host.Call.SourceRef) ||
                    host.Preview && !registry.TryGetSourceFactsPreviewEvent(host.Call, out _) ||
                    host.Call.SourceRef.AbilityId != handler.AbilityId || host.Call.SourceRef.AuthorizedPlayerId != player ||
                    !registry.TryResolveSourceFactsCall(host.Call, host.HandlerId, host.HandlerHash, out var current, out bool callable)) return LuaSnapshotValue.Null;
                return CreateCurrent(state, registry, current, callable, host.HandlerHash, host.Call.OwnerNode?.EffectId ?? "");
            }
            if (handler.Role == EffectHandlerRole.Observer && handler.EventType == context.Event.EventType &&
                handler.Script.DefinitionVersion == context.Registration?.DefinitionVersion &&
                CharacterLifecycleService.TryResolvePersistentSourceFacts(registry, context, handler.AbilityId, handler.Script.ContentHash, out var subscription) &&
                subscription.AuthorizedPlayerId == player)
                return CreateCurrent(state, registry, subscription, false);
            // 真实 continuation 继续使用执行收据；纯查询不再从任意 payload/SourceKey 推导授权。
            if (context.OwnerNode != null && CharacterLifecycleService.PersistentSourceFactsRoot(state, context.OwnerNode) != null)
                return CharacterLifecycleService.TryResolvePersistentSourceFactsContinuation(registry, context, handler.Script.HandlerId, handler.Script.ContentHash, out var persistent) &&
                    persistent.AbilityId == handler.AbilityId && persistent.AuthorizedPlayerId == player
                    ? CreateCurrent(state, registry, persistent, false) : LuaSnapshotValue.Null;
            if (context.OwnerNode != null && ReferenceEquals(context.OwnerNode, state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == context.OwnerNode.EffectId)) &&
                handler.Role == EffectHandlerRole.Continuation && context.OwnerNode.ContinuationHandlerId == handler.Script.HandlerId &&
                handler.Script.DefinitionVersion == context.Registration?.DefinitionVersion &&
                context.OwnerNode.ContinuationContentHash == handler.Script.ContentHash &&
                registry.TryGetContinuation(handler.Script.HandlerId, context.Event.EventType, out var continuation) && ReferenceEquals(continuation, context.Registration) &&
                state.EffectRuntime.RuleEvents.Any(e => ReferenceEquals(e, context.Event)) &&
                context.Registration?.HandlerId == handler.Script.HandlerId && context.Registration.ContentHash == handler.Script.ContentHash &&
                registry.TryResolveAbilityExecutionHandler(state, context.OwnerNode.EffectId, handler.AbilityId, handler.Script.ContentHash, out var source, out _) &&
                source.AuthorizedPlayerId == player)
                return Create(state, registry, source, true, handler.Script.ContentHash, context.OwnerNode.EffectId);
            return LuaSnapshotValue.Null;
        }

        private static bool Same(AbilitySourceRef a, AbilitySourceRef b) => a.SourceKey == b.SourceKey && a.ItemId == b.ItemId &&
            a.DefinitionId == b.DefinitionId && a.AbilityId == b.AbilityId && a.ActiveFaceId == b.ActiveFaceId &&
            a.DeclarationVersion == b.DeclarationVersion && a.AuthorizedPlayerId == b.AuthorizedPlayerId &&
            a.AuthorizationRef == b.AuthorizationRef && a.EffectiveZoneId == b.EffectiveZoneId && a.RuleOwnerItemId == b.RuleOwnerItemId &&
            b.GrantingSourceRefs != null && a.GrantingSourceRefs.SequenceEqual(b.GrantingSourceRefs);

        private static LuaSnapshotValue Fact(GameState state, EffectRegistry registry, AbilitySourceRef source)
        {
            var item = state.ItemZones.Items.Single(i => i.ItemId == source.ItemId);
            var parent = state.EntrepreneurSetup?.Players.SingleOrDefault(p => p.AbilityItemId == item.ItemId ||
                p.RoundGroups.Any(g => g.Candidates.Any(c => c.EntityItemId == item.ItemId)));
            var receipt = Value(item.LocalState, "sourceSelectionReceipt");
            var selection = receipt == null ? LuaSnapshotValue.Null : O(E("protocolVersion", N(Number(receipt, "protocolVersion"))),
                E("requestId", S(Text(receipt, "requestId"))), E("ownerEffectId", S(Text(receipt, "ownerEffectId"))),
                E("candidateId", S(Text(receipt, "candidateId"))), E("faceId", S(Text(receipt, "faceId"))));
            return O(E("sourceKey", S(source.SourceKey)), E("itemId", S(source.ItemId)), E("definitionId", S(source.DefinitionId)),
                E("abilityId", S(source.AbilityId)), E("activeFaceId", S(source.ActiveFaceId)),
                E("declarationVersion", S(source.DeclarationVersion)), E("handlerHash", S(registry.GetAbilitySourceHandlerHash(source.AbilityId))),
                E("authorizedPlayerId", S(Player(source.AuthorizedPlayerId))), E("authorizationRef", S(source.AuthorizationRef)),
                E("effectiveZoneId", S(source.EffectiveZoneId)), E("ruleOwnerItemId", S(source.RuleOwnerItemId)),
                E("abilityOwnerItemId", S(parent?.AbilityItemId ?? "")), E("abilityOwnerDefinitionId", S(parent?.DefinitionId ?? "")),
                E("selectionReceipt", selection), E("grantingSourceRefs", A(source.GrantingSourceRefs.Select(S))));
        }
        private static string Player(int id) => "p" + id.ToString(CultureInfo.InvariantCulture);
        private static LuaSnapshotValue Snapshot(NormalizedValue value)
        {
            if (value == null || value.Kind == NormalizedValueKind.Null) return LuaSnapshotValue.Null;
            switch (value.Kind)
            {
                case NormalizedValueKind.Boolean: return LuaSnapshotValue.Boolean(value.BooleanValue);
                case NormalizedValueKind.Integer: return LuaSnapshotValue.Integer(value.IntegerValue);
                case NormalizedValueKind.String: return S(value.StringValue);
                case NormalizedValueKind.StableReference: return S(value.ReferenceId);
                case NormalizedValueKind.Array: return A(value.Items.Select(Snapshot));
                case NormalizedValueKind.Object: return O(value.Properties.Select(p=>E(p.Name,Snapshot(p.Value))).ToArray());
                default: throw new InvalidOperationException("token_local_state_invalid");
            }
        }
        private static LuaSnapshotEntry E(string n, LuaSnapshotValue v) => new LuaSnapshotEntry(n, v);
        private static LuaSnapshotValue O(params LuaSnapshotEntry[] entries) => LuaSnapshotValue.Object(entries);
        private static LuaSnapshotValue A(IEnumerable<LuaSnapshotValue> items) => LuaSnapshotValue.Array(items.ToList());
        private static LuaSnapshotValue S(string value) => LuaSnapshotValue.String(value);
        private static LuaSnapshotValue N(int value) => LuaSnapshotValue.Integer(value);
    }
}
