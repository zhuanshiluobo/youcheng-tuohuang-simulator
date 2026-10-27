using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class CleanupObligationRecord
    {
        public int ProtocolVersion=1,PlayerId,DueRound;
        public bool BodyFrozen;
        public string ObligationId="",ScheduleEffectId="",OriginKind="",OriginId="",OriginOwnerEffectId="",OriginAuthorization="";
        public string RegistrationDigest="";
        public string HandlerRef="",HandlerVersion="",HandlerHash="",Status="pending",DueEventId="",InvokeEffectId="",CompletionReceiptId="";
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef OriginVersion;
        public NormalizedValue Context;
        public List<string> BodyEffectIds=new List<string>();
    }
    // 已成立义务以原真实调用收据为资格；不依赖未来仍能发现该来源。
    public static partial class CleanupObligation
    {
        public const string Version="1.2.0",ScheduleType="effect.cleanup.obligation.schedule",InvokeType="effect.cleanup.obligation.invoke";
        public const string SettlementLocalId=InvokeType+".required";
        public static readonly string[] Fields={"apiVersion","player","handlerRef","dueRound","context"};
        public static string ValidateArguments(NormalizedValue a)
        {
            return a?.Kind!=NormalizedValueKind.Object || !a.IsValid() || a.Properties.Any(p=>!Fields.Concat(new[]{"kind","effectTypeId"}).Contains(p.Name)) ||
                Value(a,"apiVersion")?.Kind!=NormalizedValueKind.Integer || Number(a,"apiVersion")!=1 || Value(a,"handlerRef")?.Kind!=NormalizedValueKind.String || Text(a,"handlerRef").Length==0 ||
                Value(a,"dueRound")?.Kind!=NormalizedValueKind.Integer || Number(a,"dueRound")<1 || Value(a,"context")?.Kind!=NormalizedValueKind.Object ? "cleanup_obligation_arguments_invalid" : "";
        }
        public static void Register(EffectRegistry registry)
        {
            if(registry.TryGet(ScheduleType,out _)) return;
            registry.Register(new EffectRegistration(ScheduleType,Schedule,EffectExecutorKind.IntrinsicFlow,Version));
            registry.Register(new EffectRegistration(InvokeType,Invoke,EffectExecutorKind.IntrinsicFlow,Version));
            registry.RegisterSettlementSourceContract(new SettlementSourceContract(InvokeType,InvokeType,SettlementLocalId,
                SettlementEntryService.CleanupOrder,"",SettlementWholeEffectPolicy.Required));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("cleanup.obligation.due","PlayerCleanupStarted","","cleanup.obligation.due",c=>
                c.State.EffectRuntime.CleanupObligations.Where(r=>r.Status=="pending" && r.PlayerId==c.Event.PlayerId && r.DueRound==c.State.Round &&
                    !c.State.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==InvokeType && Text(n.NormalizedArguments,"obligationId")==r.ObligationId))
                .OrderBy(r=>r.ObligationId,StringComparer.Ordinal).Select(r=>InvokeSpec(c.State,r,c.Event,c.State.Round)).ToList(),EffectHandlerRole.Observer));
        }
        internal static EffectSpec InvokeSpec(GameState state,CleanupObligationRecord r,RuleEvent e,int round)
        {
            var s=EffectSpec.Create(InvokeType,Object("obligationId",NormalizedValue.CreateString(r.ObligationId),"triggerEventId",NormalizedValue.CreateString(e.EventId),"dueRound",NormalizedValue.CreateInteger(round)),r.PlayerId);
            s.StableKey=r.ObligationId;s.AllowDecline=false;
            s.Settlement=SettlementDeclaration(state,r);return s;
        }
        private static bool ActiveOrigin(GameState state,EffectNodeRuntimeState node,out string kind,out string id,out string owner,out AbilitySourceRef source,out AbilityVersionRef version)
        {
            kind=id=owner="";source=null;version=null;var visited=new HashSet<string>();bool found=false;
            node=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==node.ParentEffectId);
            while(node!=null && visited.Add(node.EffectId))
            {
                if(node.EffectTypeId==InvokeType) return false;
                if(node.Status==EffectNodeStatus.Completed || node.Status==EffectNodeStatus.Failed || node.Status==EffectNodeStatus.Faulted) return false;
                var action=state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(r=>r.OwnerEffectId==node.EffectId && r.ActivationCommitted && r.Phase!="completed" && r.Phase!="faulted");
                var use=state.EffectRuntime.AbilitySourceUses.SingleOrDefault(r=>r.OwnerEffectId==node.EffectId && r.Outcome=="accepted");
                var cleanup=CleanupSourceService.Invocations(state).SingleOrDefault(r=>r.TargetEffectId==node.EffectId && r.Status=="executing");
                if(!found && (action!=null || use!=null || cleanup!=null))
                {
                    source=action?.SourceRef ?? use?.SourceRef ?? cleanup.SourceRef;version=action?.VersionRef ?? use?.VersionRef ?? cleanup.VersionRef;
                    kind=action!=null?"action":use!=null?"source":"cleanup";id=action?.InvocationId ?? use?.ReceiptId ?? cleanup.InvocationId;owner=node.EffectId;found=true;
                }
                node=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==node.ParentEffectId);
            }
            return found && node==null;
        }
        private static string OriginProof(CleanupObligationRecord r)=>StableIdFactory.Create("cleanup-obligation-origin",r.OriginKind,r.OriginId,r.OriginOwnerEffectId,
            CleanupSourceService.SourceProofDigest(r.SourceRef),r.OriginVersion.ContentPackHash,r.OriginVersion.DefinitionVersion,r.OriginVersion.HandlerHash,r.OriginVersion.ExecutorVersion);
        private static string RegistrationProof(CleanupObligationRecord r)=>StableIdFactory.Create("cleanup-obligation-registration",r.ObligationId,r.ScheduleEffectId,
            r.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),r.DueRound.ToString(System.Globalization.CultureInfo.InvariantCulture),r.HandlerRef,r.HandlerVersion,r.HandlerHash,r.Context.ToDeterministicString(),r.OriginAuthorization);
        internal static RuleEvent CurrentCleanup(GameState state,EffectNodeRuntimeState node,int player)=>node==null?null:state.EffectRuntime.RuleEvents.SingleOrDefault(e=>
            e.EventType=="PlayerCleanupStarted" && e.PlayerId==player && EffectExecutionBinding.Within(state,node.EffectId,e.OwnerNodeId));
        private static EffectStepResult Schedule(EffectExecutionContext c)
        {
            if(ValidateArguments(c.Node.NormalizedArguments).Length>0) throw new InvalidOperationException("cleanup_obligation_arguments_invalid");
            var old=c.State.EffectRuntime.CleanupObligations.SingleOrDefault(r=>r.ScheduleEffectId==c.Node.EffectId);
            if(old!=null) return EffectStepResult.Completed(Object("obligationId",NormalizedValue.CreateString(old.ObligationId),"registrationDigest",NormalizedValue.CreateString(old.RegistrationDigest)));
            if(!ActiveOrigin(c.State,c.Node,out var kind,out var id,out var owner,out var source,out var version) || source.AuthorizedPlayerId!=c.Node.PlayerId ||
                !c.Registry.QuerySources(c.State,c.Node.PlayerId).Any(s=>CleanupSourceService.SourceProofDigest(s)==CleanupSourceService.SourceProofDigest(source)))
                throw new InvalidOperationException("cleanup_obligation_origin_unauthorized");
            var d=c.Registry.AbilityDeclarations.Single(x=>x.AbilityId==source.AbilityId && x.DefinitionId==source.DefinitionId);
            string handler=Text(c.Node.NormalizedArguments,"handlerRef");int due=Number(c.Node.NormalizedArguments,"dueRound");
            if(!d.AllowedOperations.Contains("cleanup.defer:"+handler) || c.Registry.GetAbilityHandlerHash(handler).Length==0 || due<c.State.Round || due>c.State.MaxRounds)
                throw new InvalidOperationException("cleanup_obligation_permission_or_due_invalid");
            var now=CurrentCleanup(c.State,c.Node,c.Node.PlayerId);
            if(due==c.State.Round && now==null && c.State.EffectRuntime.RuleEvents.Any(e=>e.EventType=="PlayerCleanupStarted" && e.PlayerId==c.Node.PlayerId &&
                c.State.EffectRuntime.MainNodes.Any(n=>n.ExecutionEffectId==e.OwnerNodeId && n.RoundNumber==c.State.Round)))
                throw new InvalidOperationException("cleanup_obligation_due_already_passed");
            var r=new CleanupObligationRecord { ObligationId=StableIdFactory.Create("cleanup-obligation",c.State.GameId,c.Node.EffectId),ScheduleEffectId=c.Node.EffectId,
                PlayerId=c.Node.PlayerId,DueRound=due,OriginKind=kind,OriginId=id,OriginOwnerEffectId=owner,SourceRef=GameStateCloneService.DeepClone(source),
                OriginVersion=GameStateCloneService.DeepClone(version),HandlerRef=handler,HandlerVersion=c.Registry.GetAbilityHandlerVersion(handler),HandlerHash=c.Registry.GetAbilityHandlerHash(handler),
                Context=Value(c.Node.NormalizedArguments,"context").Clone() };
            SettlementDeclaration(c.State,r);
            r.OriginAuthorization=OriginProof(r);r.RegistrationDigest=RegistrationProof(r);c.State.EffectRuntime.CleanupObligations.Add(r);c.State.EffectRuntime.CleanupObligationProtocolVersion=1;
            var step=EffectStepResult.Completed(Object("obligationId",NormalizedValue.CreateString(r.ObligationId),"registrationDigest",NormalizedValue.CreateString(r.RegistrationDigest)));
            // 同收尾的新义务由工作事务挂到原窗口，不能成为当前正文等待自己的子节点。
            return step;
        }
        private static EffectStepResult Invoke(EffectExecutionContext c)
        {
            var r=c.State.EffectRuntime.CleanupObligations.SingleOrDefault(o=>o.ObligationId==Text(c.Node.NormalizedArguments,"obligationId"));
            if(r==null || r.PlayerId!=c.Node.PlayerId || r.Status=="completed" || !ValidateInvocation(c.State,r,c.Node)) throw new InvalidOperationException("cleanup_obligation_invoke_invalid");
            if(c.Node.FlowStage.Length==0)
            {
                if(r.Status!="pending" || r.InvokeEffectId.Length>0) throw new InvalidOperationException("cleanup_obligation_duplicate");
                r.InvokeEffectId=c.Node.EffectId;r.DueEventId=Text(c.Node.NormalizedArguments,"triggerEventId");r.Status="executing";
                var body=c.Registry.ConstructAbilityBody(r.HandlerRef,new AbilityActionCallContext { State=c.State,OwnerNode=c.Node,SourceRef=r.SourceRef,Arguments=r.Context.Clone() });
                var step=EffectStepResult.Continue("body");foreach(var child in body) step.AddChild(child);return step;
            }
            if(c.Node.FlowStage!="body") throw new InvalidOperationException("cleanup_obligation_stage_invalid");
            var nodes=c.State.EffectRuntime.EffectNodes.Where(n=>n.EffectId!=c.Node.EffectId && EffectExecutionBinding.Within(c.State,n.EffectId,c.Node.EffectId)).ToList();
            var expected=new HashSet<string>(nodes.Where(n=>n.EffectTypeId==EffectTypeIds.Condition && Text(n.NormalizedResult,"outcome")=="condition_not_met").Select(n=>Text(n.NormalizedResult,"failedChildId")));
            if(nodes.Any(n=>!CleanupSourceService.Terminal(n) || n.Status==EffectNodeStatus.Faulted || n.Status==EffectNodeStatus.Failed && !expected.Contains(n.EffectId) && n.EffectTypeId!=EffectTypeIds.Condition))
                throw new InvalidOperationException("cleanup_obligation_body_unfulfilled");
            if(c.TryAttachDeferredResponseDependenciesOnWorking(CleanupSourceDependencies.Instance.GetPendingResponses(c.State,c.Node.EffectId))) return EffectStepResult.Continue("body");
            if(CleanupSourceDependencies.Instance.HasPendingForInvocation(c.State,c.Node.EffectId,out string reason)) throw new InvalidOperationException(reason);
            if(!r.BodyFrozen) throw new InvalidOperationException("cleanup_obligation_body_not_frozen");
            return EffectStepResult.Completed(Object("obligationId",NormalizedValue.CreateString(r.ObligationId)));
        }
        public static void RecordBodyAssembly(GameState state,EffectNodeRuntimeState node)
        {
            if(node.EffectTypeId!=InvokeType || node.FlowStage!="body") return;
            var r=state.EffectRuntime.CleanupObligations.Single(x=>x.InvokeEffectId==node.EffectId);
            if(!r.BodyFrozen){r.BodyEffectIds=node.ChildEffectIds.ToList();r.BodyFrozen=true;}
        }
        public static void ObserveTerminal(GameState state,EffectNodeRuntimeState node)
        {
            if(node.EffectTypeId!=InvokeType) return;
            var r=state.EffectRuntime.CleanupObligations.Single(x=>x.InvokeEffectId==node.EffectId);
            if(node.Status!=EffectNodeStatus.Completed) throw new InvalidOperationException("cleanup_obligation_terminal_unfulfilled");
            r.Status="completed";r.CompletionReceiptId=StableIdFactory.Create("cleanup-obligation-complete",r.ObligationId,node.EffectId,node.CompletedEventId);
        }
        internal static bool ValidateInvocation(GameState state,CleanupObligationRecord r,EffectNodeRuntimeState n,bool attaching=false)
        {
            var e=state.EffectRuntime.RuleEvents.SingleOrDefault(x=>x.EventId==Text(n.NormalizedArguments,"triggerEventId"));
            return n.NormalizedArguments?.Kind==NormalizedValueKind.Object && n.NormalizedArguments.Properties.Count==3 &&
                n.NormalizedArguments.Properties.All(p=>new[]{"obligationId","triggerEventId","dueRound"}.Contains(p.Name)) &&
                Value(n.NormalizedArguments,"dueRound")?.Kind==NormalizedValueKind.Integer && Number(n.NormalizedArguments,"dueRound")==r.DueRound &&
                !state.EffectRuntime.MainNodes.Any(m=>m.ExecutionEffectId==e?.OwnerNodeId && m.RoundNumber!=r.DueRound) &&
                e?.EventType=="PlayerCleanupStarted" && e.PlayerId==r.PlayerId &&
                (n.ParentEffectId==e.OwnerNodeId && (attaching || state.EffectRuntime.DispatchReceipts.Any(d=>d.Succeeded && d.EventId==e.EventId && d.HandlerId=="cleanup.obligation.due" && d.AttachedEffectIds.Contains(n.EffectId)) ||
                    CurrentCleanup(state,state.EffectRuntime.EffectNodes.SingleOrDefault(s=>s.EffectId==r.ScheduleEffectId),r.PlayerId)?.EventId==e.EventId) ||
                 n.ParentEffectId==r.ScheduleEffectId && EffectExecutionBinding.Within(state,r.ScheduleEffectId,e.OwnerNodeId));
        }
        public static bool HasRawFields(JObject state)
        {
            var rt=state?["EffectRuntime"] as JObject;if(rt==null) return false;
            bool evidence=(rt["EffectNodes"] as JArray)?.Any(n=>(string)n["EffectTypeId"]==ScheduleType || (string)n["EffectTypeId"]==InvokeType)==true;
            if(rt["CleanupObligationProtocolVersion"]==null && rt["CleanupObligations"]==null) return !evidence;
            if(rt["CleanupObligationProtocolVersion"]?.Type!=JTokenType.Integer || rt["CleanupObligations"] is not JArray records) return false;
            foreach(var row in records)
            {
                if(row is not JObject o || typeof(CleanupObligationRecord).GetFields().Any(f=>o[f.Name]==null || o[f.Name].Type==JTokenType.Null)) return false;
                foreach(var f in typeof(CleanupObligationRecord).GetFields())
                    if(f.FieldType==typeof(string) && o[f.Name].Type!=JTokenType.String || f.FieldType==typeof(int) && o[f.Name].Type!=JTokenType.Integer) return false;
                if(o["BodyFrozen"]?.Type!=JTokenType.Boolean) return false;
                if(o["SourceRef"] is not JObject s || typeof(AbilitySourceRef).GetFields().Any(f=>s[f.Name]==null ||
                    f.FieldType==typeof(string) && s[f.Name].Type!=JTokenType.String ||
                    f.FieldType==typeof(int) && s[f.Name].Type!=JTokenType.Integer ||
                    f.FieldType==typeof(List<string>) && (s[f.Name] is not JArray refs || refs.Any(x=>x.Type!=JTokenType.String))) ||
                    o["OriginVersion"] is not JObject v || typeof(AbilityVersionRef).GetFields().Any(f=>v[f.Name]?.Type!=JTokenType.String) ||
                    o["Context"] is not JObject || o["BodyEffectIds"] is not JArray ids || ids.Any(x=>x.Type!=JTokenType.String)) return false;
            }
            return true;
        }
        public static bool TryValidate(GameState state,EffectRegistry registry,out string reason)
        {
            reason="cleanup_obligation_state_invalid";var rt=state.EffectRuntime;
            if(rt.CleanupObligations==null || rt.CleanupObligationProtocolVersion<0 || rt.CleanupObligationProtocolVersion>1 ||
                rt.CleanupObligationProtocolVersion==0 && (rt.CleanupObligations.Count>0 || rt.EffectNodes.Any(n=>n.EffectTypeId==InvokeType || n.EffectTypeId==ScheduleType && (n.Status==EffectNodeStatus.Completed || n.PendingOutcome==EffectPendingOutcome.Completed))) ||
                rt.CleanupObligations.Any(r=>r==null) || rt.CleanupObligations.Select(r=>r.ObligationId).Distinct().Count()!=rt.CleanupObligations.Count ||
                rt.CleanupObligations.Select(r=>r.ScheduleEffectId).Distinct().Count()!=rt.CleanupObligations.Count) return false;
            foreach(var r in rt.CleanupObligations)
            {
                var schedule=rt.EffectNodes.SingleOrDefault(n=>n.EffectId==r.ScheduleEffectId);
                var declaration=state.ContentDefinitions.AbilitySources.SingleOrDefault(d=>d.AbilityId==r.SourceRef?.AbilityId && d.DefinitionId==r.SourceRef?.DefinitionId);
                if(r.ProtocolVersion!=1 || r.SourceRef==null || r.SourceRef.GrantingSourceRefs==null || r.OriginVersion==null || r.OriginVersion.ContentPackHash!=state.ContentPackHash || r.OriginVersion.ContractVersion!="QY-COMMON/1.0.1" || r.OriginVersion.ExecutorVersion!=(r.OriginKind=="action"?AbilityActionExecutor.Version:r.OriginKind=="cleanup"?CleanupSourceService.Version:r.OriginKind=="source"?"1.0.0":"") ||
                    schedule?.EffectTypeId!=ScheduleType || schedule.PlayerId!=r.PlayerId || r.SourceRef.AuthorizedPlayerId!=r.PlayerId ||
                    ValidateArguments(schedule.NormalizedArguments).Length>0 || Text(schedule.NormalizedArguments,"handlerRef")!=r.HandlerRef || Number(schedule.NormalizedArguments,"dueRound")!=r.DueRound ||
                    r.Context?.Kind!=NormalizedValueKind.Object || !r.Context.IsValid() || Value(schedule.NormalizedArguments,"context").ToDeterministicString()!=r.Context.ToDeterministicString() ||
                    r.ObligationId!=StableIdFactory.Create("cleanup-obligation",state.GameId,r.ScheduleEffectId) || r.DueRound<1 || r.DueRound>state.MaxRounds ||
                    declaration==null || declaration.DeclarationVersion!=r.SourceRef.DeclarationVersion || !declaration.AllowedOperations.Contains("cleanup.defer:"+r.HandlerRef) ||
                    r.OriginAuthorization!=OriginProof(r) || r.RegistrationDigest!=RegistrationProof(r) || Text(schedule.NormalizedResult,"registrationDigest")!=r.RegistrationDigest || string.IsNullOrEmpty(r.HandlerVersion) || string.IsNullOrEmpty(r.HandlerHash) || !OriginMatches(state,r) ||
                    !EffectExecutionBinding.Within(state,r.ScheduleEffectId,r.OriginOwnerEffectId) || r.BodyEffectIds==null ||
                    registry!=null && (registry.GetAbilityHandlerHash(r.HandlerRef)!=r.HandlerHash || registry.GetAbilityHandlerVersion(r.HandlerRef)!=r.HandlerVersion)) return false;
                var invokes=rt.EffectNodes.Where(n=>n.EffectTypeId==InvokeType && Text(n.NormalizedArguments,"obligationId")==r.ObligationId).ToList();
                if(invokes.Count>1 || invokes.Any(n=>!ValidateInvocation(state,r,n))) return false;
                if(r.Status=="pending") { if(r.InvokeEffectId.Length>0 || r.DueEventId.Length>0 || r.CompletionReceiptId.Length>0 || r.BodyFrozen || r.BodyEffectIds.Count>0 || invokes.Any(n=>n.FlowStage.Length>0)) return false; }
                else
                {
                    var invoke=invokes.SingleOrDefault();
                    if(!r.BodyFrozen || invoke==null || !invoke.ChildEffectIds.Take(r.BodyEffectIds.Count).SequenceEqual(r.BodyEffectIds) || r.BodyEffectIds.Distinct().Count()!=r.BodyEffectIds.Count || invoke.ChildEffectIds.Skip(r.BodyEffectIds.Count).Any(id=>!rt.DispatchReceipts.Any(d=>d.Succeeded && d.AttachedEffectIds.Contains(id))) || invoke.EffectId!=r.InvokeEffectId || r.DueEventId!=Text(invoke.NormalizedArguments,"triggerEventId") || invoke.FlowStage!="body") return false;
                    if(r.Status=="completed") { if(invoke.Status!=EffectNodeStatus.Completed || r.CompletionReceiptId!=StableIdFactory.Create("cleanup-obligation-complete",r.ObligationId,invoke.EffectId,invoke.CompletedEventId)) return false; }
                    else if(r.Status!="executing" || r.CompletionReceiptId.Length>0) return false;
                }
            }
            foreach(var schedule in rt.EffectNodes.Where(n=>n.EffectTypeId==ScheduleType &&
                (n.Status==EffectNodeStatus.Completed || n.PendingOutcome==EffectPendingOutcome.Completed)))
            {
                var registered=rt.CleanupObligations.SingleOrDefault(r=>r.ScheduleEffectId==schedule.EffectId);
                if(registered==null || Text(schedule.NormalizedResult,"obligationId")!=registered.ObligationId ||
                    Text(schedule.NormalizedResult,"registrationDigest")!=registered.RegistrationDigest) return false;
            }
            if(rt.EffectNodes.Any(n=>n.EffectTypeId==InvokeType && !rt.CleanupObligations.Any(r=>r.ObligationId==Text(n.NormalizedArguments,"obligationId")))) return false;
            reason="";return true;
        }
        private static bool OriginMatches(GameState state,CleanupObligationRecord r)
        {
            AbilitySourceRef s=null;AbilityVersionRef v=null;
            if(r.OriginKind=="source") { var x=state.EffectRuntime.AbilitySourceUses.SingleOrDefault(a=>a.ReceiptId==r.OriginId && a.OwnerEffectId==r.OriginOwnerEffectId);s=x?.SourceRef;v=x?.VersionRef; }
            else if(r.OriginKind=="action") { var x=state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(a=>a.InvocationId==r.OriginId && a.OwnerEffectId==r.OriginOwnerEffectId && a.ActivationCommitted);s=x?.SourceRef;v=x?.VersionRef; }
            else if(r.OriginKind=="cleanup") { var x=CleanupSourceService.Invocations(state).SingleOrDefault(a=>a.InvocationId==r.OriginId && a.TargetEffectId==r.OriginOwnerEffectId);s=x?.SourceRef;v=x?.VersionRef; }
            return s!=null && v!=null && CleanupSourceService.SourceProofDigest(s)==CleanupSourceService.SourceProofDigest(r.SourceRef) &&
                v.ContractVersion==r.OriginVersion.ContractVersion && v.ContentPackHash==r.OriginVersion.ContentPackHash && v.DefinitionVersion==r.OriginVersion.DefinitionVersion && v.HandlerHash==r.OriginVersion.HandlerHash && v.ExecutorVersion==r.OriginVersion.ExecutorVersion;
        }
    }
}
