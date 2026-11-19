using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class AllocationOptionV1
    {
        public string Id = "";
        public int MaxExecutions;
        public GameplayTextRef Text;
    }
    [Serializable] public sealed class AllocationCountV1 { public string OptionId = ""; public int Count; }
    [Serializable] public sealed class AllocationRootReceiptV1
    {
        public string EffectId = "", State = "", FailureReason = "";
        public NormalizedValue NormalizedResult = NormalizedValue.CreateNull();
    }
    [Serializable] public sealed class AllocationExecutionV1
    {
        public int Ordinal;
        public string OptionId = "", State = "in_flight";
        public List<AllocationRootReceiptV1> Roots = new List<AllocationRootReceiptV1>();
    }
    [Serializable] public sealed class AllocationResultV1
    {
        public string Version = "allocation_result_v1", OwnerEffectId = "", ContentIdentity = "", ResultDigest = "";
        public List<string> Sequence = new List<string>();
        public List<AllocationCountV1> AllocatedCounts = new List<AllocationCountV1>();
        public string SubmitKind = "";
        public int AllocatedCount, AttemptedCount, CompletedCount;
        public List<AllocationExecutionV1> Executions = new List<AllocationExecutionV1>();
        public int FailedOrdinal;
        public string FailureReason = "";
        public int UnexecutedFrom;
        public string Outcome = "";
    }
    [Serializable] public sealed class AllocationStateV1
    {
        public int Version = 1;
        public string OwnerEffectId = "", InteractionId = "", CandidateSetId = "", ContentIdentity = "", DeclarationDigest = "";
        public int CandidateSetVersion = 1, DecisionPlayerId = -1;
        public List<AllocationOptionV1> Options = new List<AllocationOptionV1>();
        public bool Sealed;
        public List<string> Sequence = new List<string>();
        public List<AllocationCountV1> AllocatedCounts = new List<AllocationCountV1>();
        public string SubmitKind = "";
        public int NextOrdinal = 1;
        public List<AllocationExecutionV1> Executions = new List<AllocationExecutionV1>();
        public AllocationResultV1 Result;
    }

    /// <summary>API-37：队列只存一份；逐根挂接、等待整个子树及完成续接，再记录收据。</summary>
    public static partial class AllocationEffects
    {
        public const string TypeId = "effect.flow.allocate_effects";
        public const string InteractionType = "effect.effects.allocate";
        public const string AnswerSchema = "allocation_sequence_v1";
        public const string ExecutorVersion = "1.0.0";
        internal static NormalizedValue V(NormalizedValue v, string k) => ItemTransferEffectExecutor.Value(v, k);
        public static string S(NormalizedValue v, string k) => ItemTransferEffectExecutor.Text(v, k);
        internal static int I(NormalizedValue v, string k) => (int)(V(v,k)?.IntegerValue ?? -1);
        public static string Digest(object value)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)))).Replace("-", "").ToLowerInvariant();
        }
        public static List<AllocationOptionV1> CloneOptions(List<AllocationOptionV1> v) => GameStateCloneService.DeepClone(v);
        public static string ValidateArguments(NormalizedValue a, List<AllocationOptionV1> options, int groupCount)
        {
            if (a?.Kind != NormalizedValueKind.Object || !a.IsValid() || a.Properties.Any(p => !new[] { "apiVersion", "player", "totalExecutions", "allowEndAction", "kind", "effectTypeId" }.Contains(p.Name)) ||
                V(a,"apiVersion")?.Kind != NormalizedValueKind.Integer || I(a,"apiVersion") != 1 ||
                V(a,"totalExecutions")?.Kind != NormalizedValueKind.Integer || I(a,"totalExecutions") < 1 || I(a,"totalExecutions") > 99 ||
                V(a,"allowEndAction")?.Kind != NormalizedValueKind.Boolean || V(a,"player")?.Kind != NormalizedValueKind.String ||
                !int.TryParse(S(a,"player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 0)
                return "allocation_arguments_invalid";
            if (options == null || options.Count < 1 || options.Count > 16 || groupCount != options.Count) return "allocation_options_invalid";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options)
            {
                if (option == null || string.IsNullOrEmpty(option.Id) || option.Id.Length > 64 || option.Id.Any(c => !(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || "_.:-".Contains(c))) || !ids.Add(option.Id) || option.MaxExecutions < 0 || option.MaxExecutions > 99 || option.Text == null)
                    return "allocation_option_invalid";
                try { GameplayPresentationNormalizer.ToNormalized(option.Text); } catch (ArgumentException) { return "allocation_text_invalid"; }
            }
            if (!V(a,"allowEndAction").BooleanValue && options.Sum(o => o.MaxExecutions) < I(a,"totalExecutions")) return "allocation_capacity_insufficient";
            return "";
        }
        public static void ValidateSpec(EffectSpec spec, EffectRegistry registry = null, EffectRuntimeLimits limits = null)
        {
            if (spec.EffectTypeId != TypeId)
            {
                if (spec.AllocationOptions != null) throw new ArgumentException("allocation_options_on_other_effect");
                return;
            }
            string reason = ValidateArguments(spec.NormalizedArguments, spec.AllocationOptions, spec.NestedEffectGroups?.Count ?? 0);
            if (reason.Length > 0) throw new ArgumentException(reason);
            if (spec.PlayerId != IPlayer(spec.NormalizedArguments) || spec.DecisionPlayerId >= 0 && spec.DecisionPlayerId != spec.PlayerId) throw new ArgumentException("allocation_player_unauthorized");
            int count = 0, depth = 0, optionIndex=0;
            var costs=new List<(int cost,int cap)>();
            foreach (var group in spec.NestedEffectGroups)
            {
                if (group == null || group.Count < 1 || group.Count > 16) throw new ArgumentException("allocation_roots_invalid");
                int before=count;
                foreach (var child in group) Inspect(child, 1, registry, ref count, ref depth);
                costs.Add((count-before,spec.AllocationOptions[optionIndex++].MaxExecutions));
            }
            var budget = limits ?? EffectRuntimeLimits.Default;
            int executionNodes=0,remaining=I(spec.NormalizedArguments,"totalExecutions");
            foreach(var cost in costs.OrderByDescending(v=>v.cost)) {int executions=Math.Min(remaining,cost.cap);executionNodes+=executions*cost.cost;remaining-=executions;}
            if (count + 1 > budget.MaxNodeCount || depth + 1 > budget.MaxTreeDepth || executionNodes+1>budget.MaxNodeCount || executionNodes+1>budget.MaxEventCount) throw new ArgumentException("allocation_declaration_budget_exceeded");
        }
        private static void Inspect(EffectSpec spec, int level, EffectRegistry registry, ref int count, ref int depth)
        {
            if (spec == null || level > 16 || ++count > EffectRuntimeLimits.Default.MaxNodeCount || spec.EffectTypeId == TypeId || spec.EffectTypeId == LuaDomainEffectTypeIds.Repeat || spec.AllocationOptions != null) throw new ArgumentException("allocation_nested_effect_invalid");
            if(spec.Children!=null && spec.Children.Count>0)throw new ArgumentException("allocation_nonpersistent_children_invalid");
            depth = Math.Max(depth,level);
            string retired = ContentPrimitiveEffectExecutor.RetiredMarkerContractReason(spec.EffectTypeId,spec.NormalizedArguments);
            if (retired.Length > 0) throw new ArgumentException(retired);
            if(spec.NormalizedArguments==null || !spec.NormalizedArguments.IsValid() || !RequiredEffectOutcome.IsValid(spec.RequiredOutcome) || !EffectContinuationContext.IsValid(spec.ContinuationContext) ||
                !GameplayPresentationStateValidator.TryValidateSpecMetadata(spec.Presentation,spec.Settlement,out _))throw new ArgumentException("allocation_nested_metadata_invalid");
            if (registry != null) registry.Get(spec.EffectTypeId).Validate(spec);
            if(spec.NestedEffectGroups==null)throw new ArgumentException("allocation_nested_groups_missing");
            foreach (var group in spec.NestedEffectGroups)
            {
                if(group==null || group.Count>16)throw new ArgumentException("allocation_nested_roots_invalid");
                foreach (var child in group) Inspect(child,level+1,registry,ref count,ref depth);
            }
        }
        private static int IPlayer(NormalizedValue a) => int.Parse(S(a,"player"), CultureInfo.InvariantCulture);
        public static string ContentIdentity(EffectNodeRuntimeState n) => Digest(new object[] { n.EffectId,n.SourceId,n.PlayerId,n.ContinuationContentId,n.ContinuationContentInstanceId,n.ContinuationAbilityId,n.ContinuationDefinitionVersion,n.ContinuationContentHash,n.ExecutionBinding,n.PersistentSourceFacts,n.ContinuationHandlerId,n.ContinuationContext });
        public static string DeclarationDigest(EffectNodeRuntimeState n) => Digest(new object[] { n.NormalizedArguments,n.AllocationOptions,n.NestedGroupSizes,n.NestedEffects });
        public static void Register(EffectRegistry registry)
        {
            if(!registry.TryGet(TypeId,out _))registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, ExecutorVersion) { ExecutionDoesNotRetainState = true,
                ExecutionPreservesUniqueReferences = EffectRegistry.BuiltinFlowPublicationProofsEnabled,
                ExecutionPreservesJournalPrefix = EffectRegistry.BuiltinFlowPublicationProofsEnabled,
                Validator = spec => { ValidateSpec(spec,registry);return ""; } });
        }
        public static bool IsConsumedFailure(EffectNodeRuntimeState owner,EffectNodeRuntimeState child) => owner.EffectTypeId==TypeId && owner.AllocationV1?.Result?.Outcome=="stopped_on_failure" && owner.AllocationV1.Executions.Any(e=>e.State=="failed" && e.Roots.Any(r=>r.EffectId==child.EffectId && r.State=="Failed"));

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var n = context.Node;
            if (n.AllocationV1 == null)
            {
                ValidateSpec(EffectRegistry.FromRuntimeSpec(ToSpec(n)), context.Registry, context.Limits);
                if (context.State.FindPlayer(n.PlayerId) == null) throw new ArgumentException("allocation_player_missing");
                ValidateRemainingBudget(context);
                var s = new AllocationStateV1 { OwnerEffectId=n.EffectId, DecisionPlayerId=n.PlayerId, ContentIdentity=ContentIdentity(n), DeclarationDigest=DeclarationDigest(n), Options=CloneOptions(n.AllocationOptions),
                    InteractionId=StableIdFactory.Create("interaction",n.EffectId,InteractionType,"0"), CandidateSetId=StableIdFactory.Create("allocation_candidates",n.EffectId,DeclarationDigest(n)) };
                n.AllocationV1=s;
                var request = new EffectInteractionSpec { InteractionTypeId=InteractionType, AnswerSchema=AnswerSchema, AnsweringPlayerId=n.PlayerId, Visibility=GameStateVisibilityPolicy.Owner, PromptKey="core:interaction.effects.allocate.prompt",
                    CandidateSetId=s.CandidateSetId, CandidateSetVersion=1, MinSelections=0, MaxSelections=99,
                    PromptParameters=ItemTransferEffectExecutor.Object("totalExecutions",NormalizedValue.CreateInteger(I(n.NormalizedArguments,"totalExecutions")),"allowEndAction",V(n.NormalizedArguments,"allowEndAction").Clone(),"rowMaximums",NormalizedValue.CreateObject(s.Options.Select(o=>new NormalizedValueEntry{Name=o.Id,Value=NormalizedValue.CreateInteger(o.MaxExecutions)}).ToList())),
                    Presentation=new GameplayInteractionPresentation { Prompt=new GameplayTextRef("core:interaction.effects.allocate.prompt"), QuantityRows=s.Options.Select(o=>new GameplayQuantityRowPresentation{RowId=o.Id,Label=o.Text.Clone()}).ToList() } };
                request.CandidateIds.AddRange(s.Options.Select(o=>o.Id));
                return EffectStepResult.Continue("allocation_waiting").AddInteraction(request);
            }
            var state=n.AllocationV1;
            if (!state.Sealed) return EffectStepResult.NoProgress("等待有序次数分配。");
            if (state.Result != null) return EffectStepResult.Completed(Summary(state.Result));
            var current=state.Executions.LastOrDefault();
            if (current != null && current.State=="in_flight")
            {
                var root=current.Roots.Last();
                var actual=context.State.EffectRuntime.EffectNodes.SingleOrDefault(c=>c.EffectId==root.EffectId);
                if (actual==null) throw new ArgumentException("allocation_root_missing");
                if (actual.Status==EffectNodeStatus.Faulted) throw new ArgumentException("allocation_child_faulted:"+actual.FailureReason);
                if (actual.Status!=EffectNodeStatus.Completed && actual.Status!=EffectNodeStatus.Failed) return EffectStepResult.NoProgress("等待分配子树结束。");
                root.State=actual.Status.ToString(); root.FailureReason=actual.FailureReason; root.NormalizedResult=actual.NormalizedResult.Clone();
                if (actual.Status==EffectNodeStatus.Failed) { current.State="failed"; return Finish(n,"stopped_on_failure",current.Ordinal,actual.FailureReason); }
                int option=state.Options.FindIndex(o=>o.Id==current.OptionId);
                if (current.Roots.Count<n.NestedGroupSizes[option]) return AddRoot(context,current,option,current.Roots.Count);
                current.State="completed"; state.NextOrdinal++;
            }
            if (state.NextOrdinal>state.Sequence.Count) return Finish(n,"completed",0,"");
            current=new AllocationExecutionV1 { Ordinal=state.NextOrdinal,OptionId=state.Sequence[state.NextOrdinal-1] };
            state.Executions.Add(current);
            return AddRoot(context,current,state.Options.FindIndex(o=>o.Id==current.OptionId),0);
        }
        private static EffectStepResult AddRoot(EffectExecutionContext c, AllocationExecutionV1 execution, int option, int index)
        {
            int offset=c.Node.NestedGroupSizes.Take(option).Sum();
            var spec=EffectRegistry.FromRuntimeSpec(c.Node.NestedEffects[offset+index]);
            spec.StableKey=RootKey(c.Node.EffectId,execution.Ordinal,execution.OptionId,index);
            string id=StableIdFactory.Create("effect",c.Node.EffectId,"child","child_effect",c.Node.ChildEffectIds.Count.ToString(CultureInfo.InvariantCulture),spec.EffectTypeId,spec.StableKey);
            execution.Roots.Add(new AllocationRootReceiptV1 { EffectId=id, State="in_flight" });
            return EffectStepResult.Continue("allocation_executing").AddChildren(new[]{spec});
        }
        internal static string RootKey(string owner,int ordinal,string option,int index) => owner+":allocation:"+ordinal.ToString(CultureInfo.InvariantCulture)+":"+option+":"+index.ToString(CultureInfo.InvariantCulture);
        private static int DeclarationNodes(EffectSpecRuntimeState spec) => 1+spec.NestedEffects.Sum(DeclarationNodes);
        private static void ValidateRemainingBudget(EffectExecutionContext c)
        {
            int offset=0,remaining=I(c.Node.NormalizedArguments,"totalExecutions"),nodes=0;
            var costs=new List<(int cost,int cap)>();
            for(int i=0;i<c.Node.AllocationOptions.Count;i++)
            {
                int size=c.Node.NestedGroupSizes[i];
                costs.Add((c.Node.NestedEffects.Skip(offset).Take(size).Sum(DeclarationNodes),c.Node.AllocationOptions[i].MaxExecutions));offset+=size;
            }
            foreach(var cost in costs.OrderByDescending(v=>v.cost)) {int count=Math.Min(remaining,cost.cap);nodes+=count*cost.cost;remaining-=count;}
            if((long)c.State.EffectRuntime.EffectNodes.Count+nodes>c.Limits.MaxNodeCount || (long)c.State.EffectRuntime.RuleEvents.Count+nodes>c.Limits.MaxEventCount)
                throw new ArgumentException("allocation_execution_budget_exceeded");
        }
        private static EffectStepResult Finish(EffectNodeRuntimeState n,string outcome,int failed,string reason)
        {
            var s=n.AllocationV1;
            var r=new AllocationResultV1 { OwnerEffectId=n.EffectId,ContentIdentity=s.ContentIdentity,Sequence=new List<string>(s.Sequence),AllocatedCounts=GameStateCloneService.DeepClone(s.AllocatedCounts),SubmitKind=s.SubmitKind,
                AllocatedCount=s.Sequence.Count,AttemptedCount=s.Executions.Count,CompletedCount=s.Executions.Count(e=>e.State=="completed"),Executions=GameStateCloneService.DeepClone(s.Executions),FailedOrdinal=failed,FailureReason=reason,
                UnexecutedFrom=s.Executions.Count+1,Outcome=outcome };
            r.ResultDigest=Digest(r);s.Result=r;
            return EffectStepResult.Completed(Summary(r)).WithFlowStage("allocation_finished");
        }
        public static NormalizedValue Summary(AllocationResultV1 r) => ItemTransferEffectExecutor.Object("version",NormalizedValue.CreateString(r.Version),"resultRef",NormalizedValue.CreateString(r.OwnerEffectId),"resultDigest",NormalizedValue.CreateString(r.ResultDigest),"submitKind",NormalizedValue.CreateString(r.SubmitKind),"allocatedCount",NormalizedValue.CreateInteger(r.AllocatedCount),"attemptedCount",NormalizedValue.CreateInteger(r.AttemptedCount),"completedCount",NormalizedValue.CreateInteger(r.CompletedCount),"failedOrdinal",NormalizedValue.CreateInteger(r.FailedOrdinal),"failureReason",NormalizedValue.CreateString(r.FailureReason),"outcome",NormalizedValue.CreateString(r.Outcome));

        public static bool ValidateAnswer(GameState game,InteractionRequest request,NormalizedValue answer,out string reason)
        {
            reason="allocation_answer_invalid";
            var n=game.EffectRuntime.EffectNodes.Find(e=>e.EffectId==request.OwnerEffectId); var s=n?.AllocationV1;
            if(n==null || !game.EffectRuntime.InteractionRequests.Any(actual=>ReferenceEquals(actual,request)) || !ValidateNode(game,n,out reason))return false;
            if (n?.EffectTypeId!=TypeId || s==null || s.Sealed || request.Status!="open" || request.InteractionTypeId!=InteractionType || request.AnswerSchema!=AnswerSchema ||
                s.InteractionId!=request.GetStableInteractionId() || request.SourceNodeId!=n.EffectId || request.AnsweringPlayerId!=s.DecisionPlayerId || n.PlayerId!=s.DecisionPlayerId ||
                s.ContentIdentity!=ContentIdentity(n) || s.DeclarationDigest!=DeclarationDigest(n) || Digest(s.Options)!=Digest(n.AllocationOptions) || !request.CandidateIds.SequenceEqual(s.Options.Select(o=>o.Id)) ||
                request.CandidateSetId!=s.CandidateSetId || request.CandidateSetVersion!=s.CandidateSetVersion || answer?.Kind!=NormalizedValueKind.Object || !answer.IsValid() ||
                answer.Properties.Count!=4 || answer.Properties.Any(p=>!new[]{"sequence","submitKind","candidateSetId","candidateSetVersion"}.Contains(p.Name)) ||
                S(answer,"candidateSetId")!=s.CandidateSetId || V(answer,"candidateSetVersion")?.Kind!=NormalizedValueKind.Integer || I(answer,"candidateSetVersion")!=s.CandidateSetVersion ||
                V(answer,"sequence")?.Kind!=NormalizedValueKind.Array || V(answer,"sequence").Items.Any(v=>v.Kind!=NormalizedValueKind.String)) return false;
            var seq=V(answer,"sequence").Items.Select(v=>v.StringValue).ToList(); string kind=S(answer,"submitKind"); int total=I(n.NormalizedArguments,"totalExecutions");
            if (seq.Count>total || kind!="confirm" && kind!="end_action" || kind=="confirm" && seq.Count!=total || kind=="end_action" && !V(n.NormalizedArguments,"allowEndAction").BooleanValue ||
                seq.Any(id=>!s.Options.Any(o=>o.Id==id)) || s.Options.Any(o=>seq.Count(id=>id==o.Id)>o.MaxExecutions)) return false;
            reason="";return true;
        }
        public static void Seal(EffectNodeRuntimeState n,NormalizedValue answer)
        {
            var s=n.AllocationV1;s.Sequence=V(answer,"sequence").Items.Select(v=>v.StringValue).ToList();s.SubmitKind=S(answer,"submitKind");
            s.AllocatedCounts=s.Options.Select(o=>new AllocationCountV1{OptionId=o.Id,Count=s.Sequence.Count(id=>id==o.Id)}).ToList();s.Sealed=true;
        }
        public static void BindRequestRows(EffectNodeRuntimeState owner,InteractionRequest request)
        {
            if(owner.EffectTypeId!=TypeId || request.InteractionTypeId!=InteractionType)return;
            if(request.Presentation==null)request.Presentation=new GameplayInteractionPresentation();
            request.Presentation.QuantityRows=owner.AllocationOptions.Select(o=>new GameplayQuantityRowPresentation{RowId=o.Id,Label=o.Text.Clone()}).ToList();
        }
        internal static EffectSpecRuntimeState ToSpec(EffectNodeRuntimeState n) => new EffectSpecRuntimeState { EffectTypeId=n.EffectTypeId, DefinitionVersion=n.DefinitionVersion, PlayerId=n.PlayerId, DecisionPlayerId=n.PlayerId, NormalizedArguments=n.NormalizedArguments,AllocationOptions=n.AllocationOptions,NestedEffects=n.NestedEffects,NestedGroupSizes=n.NestedGroupSizes };
    }
}
