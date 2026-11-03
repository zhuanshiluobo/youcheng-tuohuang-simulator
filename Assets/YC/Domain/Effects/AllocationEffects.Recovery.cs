using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static partial class AllocationEffects
    {
        public static string ValidateSavedSpec(EffectSpecRuntimeState spec)
        {
            if (spec.EffectTypeId!=TypeId) return spec.AllocationOptions==null ? "" : "allocation_options_on_other_effect";
            if (!string.IsNullOrEmpty(spec.DefinitionVersion) && spec.DefinitionVersion!=ExecutorVersion) return "allocation_executor_version_invalid";
            try { ValidateSpec(EffectRegistry.FromRuntimeSpec(spec));return ""; } catch(Exception e) when(e is ArgumentException || e is InvalidOperationException) { return "allocation_saved_spec_invalid:"+e.Message; }
        }
        public static bool ValidateNode(GameState game,EffectNodeRuntimeState n,out string reason)
        {
            reason="allocation_state_invalid";
            if(n.EffectTypeId!=TypeId) return n.AllocationOptions==null && n.AllocationV1==null;
            if(n.DefinitionVersion!=ExecutorVersion || ValidateSavedSpec(ToSpec(n)).Length>0)return false;
            var s=n.AllocationV1;var requests=game.EffectRuntime.InteractionRequests.Where(r=>r.OwnerEffectId==n.EffectId && (r.InteractionTypeId==InteractionType || r.AnswerSchema==AnswerSchema)).ToList();
            if(s==null) return n.ChildEffectIds.Count==0 && requests.Count==0 && n.NormalizedResult.Kind==NormalizedValueKind.Null && n.FlowStage=="" && n.Status!=EffectNodeStatus.Completed;
            if(s.Version!=1 || s.OwnerEffectId!=n.EffectId || s.DecisionPlayerId!=n.PlayerId || game.FindPlayer(s.DecisionPlayerId)==null || s.ContentIdentity!=ContentIdentity(n) ||
                s.DeclarationDigest!=DeclarationDigest(n) || Digest(s.Options)!=Digest(n.AllocationOptions) || s.CandidateSetVersion!=1 || s.CandidateSetId!=StableIdFactory.Create("allocation_candidates",n.EffectId,DeclarationDigest(n)) ||
                s.InteractionId!=StableIdFactory.Create("interaction",n.EffectId,InteractionType,"0") || s.Sequence==null || s.Sequence.Count>99 || s.AllocatedCounts==null || s.Executions==null || s.NextOrdinal<1 || requests.Count!=1)return false;
            var q=requests[0];
            if(q.GetStableInteractionId()!=s.InteractionId || q.SourceNodeId!=n.EffectId || q.AnsweringPlayerId!=s.DecisionPlayerId || q.CandidateSetId!=s.CandidateSetId || q.CandidateSetVersion!=1 ||
                q.InteractionTypeId!=InteractionType || q.AnswerSchema!=AnswerSchema || q.AllowDecline || !q.CandidateIds.SequenceEqual(s.Options.Select(o=>o.Id)) ||
                I(q.PromptParameters,"totalExecutions")!=I(n.NormalizedArguments,"totalExecutions") || V(q.PromptParameters,"allowEndAction")?.Kind!=NormalizedValueKind.Boolean || V(q.PromptParameters,"allowEndAction").BooleanValue!=V(n.NormalizedArguments,"allowEndAction").BooleanValue ||
                Digest(V(q.PromptParameters,"rowMaximums"))!=Digest(NormalizedValue.CreateObject(s.Options.Select(o=>new NormalizedValueEntry{Name=o.Id,Value=NormalizedValue.CreateInteger(o.MaxExecutions)}).ToList())) ||
                Digest(q.Presentation?.QuantityRows)!=Digest(s.Options.Select(o=>new YC.Domain.Presentation.GameplayQuantityRowPresentation{RowId=o.Id,Label=o.Text.Clone()}).ToList()))return false;
            if(!s.Sealed) return q.Status=="open" && s.Sequence.Count==0 && s.AllocatedCounts.Count==0 && s.SubmitKind=="" && s.NextOrdinal==1 && s.Executions.Count==0 && s.Result==null && n.ChildEffectIds.Count==0 && n.NormalizedResult.Kind==NormalizedValueKind.Null;
            if(q.Status!="answered" || q.NormalizedAnswer==null || !SequenceEqualAnswer(n,q.NormalizedAnswer) || s.Sequence.Count>I(n.NormalizedArguments,"totalExecutions") ||
                s.SubmitKind!="confirm" && s.SubmitKind!="end_action" || s.SubmitKind=="confirm" && s.Sequence.Count!=I(n.NormalizedArguments,"totalExecutions") || s.SubmitKind=="end_action" && !V(n.NormalizedArguments,"allowEndAction").BooleanValue ||
                s.Sequence.Any(id=>!s.Options.Any(o=>o.Id==id)) || s.AllocatedCounts.Count!=s.Options.Count || s.Options.Any(o=>s.Sequence.Count(id=>id==o.Id)>o.MaxExecutions) ||
                Digest(s.AllocatedCounts)!=Digest(s.Options.Select(o=>new AllocationCountV1{OptionId=o.Id,Count=s.Sequence.Count(id=>id==o.Id)}).ToList()) || s.Executions.Count>s.Sequence.Count)return false;
            var rootIds=new HashSet<string>(StringComparer.Ordinal);
            int completed=0,failed=0;string failure="";
            for(int i=0;i<s.Executions.Count;i++)
            {
                var e=s.Executions[i];
                if(e==null || e.Ordinal!=i+1 || e.OptionId!=s.Sequence[i] || e.Roots==null || e.Roots.Count<1 || !new[]{"in_flight","completed","failed"}.Contains(e.State) || (i<s.Executions.Count-1 && e.State!="completed"))return false;
                int option=s.Options.FindIndex(o=>o.Id==e.OptionId),offset=n.NestedGroupSizes.Take(option).Sum(),roots=n.NestedGroupSizes[option];
                if(e.Roots.Count>roots || e.State=="completed" && e.Roots.Count!=roots)return false;
                for(int j=0;j<e.Roots.Count;j++)
                {
                    var r=e.Roots[j];var actual=game.EffectRuntime.EffectNodes.Find(c=>c.EffectId==r?.EffectId);var spec=n.NestedEffects[offset+j];
                    int childIndex=n.ChildEffectIds.IndexOf(r?.EffectId);
                    if(r==null || !rootIds.Add(r.EffectId) || actual==null || childIndex<0 || actual.ParentEffectId!=n.EffectId || r.NormalizedResult==null || !r.NormalizedResult.IsValid() ||
                        r.EffectId!=StableIdFactory.Create("effect",n.EffectId,"child","child_effect",childIndex.ToString(CultureInfo.InvariantCulture),spec.EffectTypeId,RootKey(n.EffectId,e.Ordinal,e.OptionId,j)) ||
                        actual.EffectTypeId!=spec.EffectTypeId || actual.PlayerId!=spec.PlayerId || actual.SourceId!=spec.SourceId || Digest(actual.NormalizedArguments)!=Digest(spec.NormalizedArguments) ||
                        Digest(actual.NestedEffects)!=Digest(spec.NestedEffects) || Digest(actual.NestedGroupSizes)!=Digest(spec.NestedGroupSizes) || Digest(actual.Presentation)!=Digest(spec.Presentation) ||
                        actual.ContinuationHandlerId!=spec.CompletionHandlerId || actual.ContinuationContentHash!=spec.ContinuationContentHash || actual.ContinuationDefinitionVersion!=(string.IsNullOrEmpty(spec.ContinuationDefinitionVersion)?actual.DefinitionVersion:spec.ContinuationDefinitionVersion) ||
                        actual.ContinuationContentId!=spec.ContinuationContentId || actual.ContinuationAbilityId!=spec.ContinuationAbilityId)return false;
                    if(r.State=="in_flight")
                    {
                        if(e.State!="in_flight" || i!=s.Executions.Count-1 || j!=e.Roots.Count-1 || r.FailureReason!="" || r.NormalizedResult.Kind!=NormalizedValueKind.Null)return false;
                    }
                    else if(r.State!=actual.Status.ToString() || r.State!="Completed" && r.State!="Failed" || r.FailureReason!=actual.FailureReason || Digest(r.NormalizedResult)!=Digest(actual.NormalizedResult) ||
                        r.State=="Failed" && (e.State!="failed" || j!=e.Roots.Count-1))return false;
                }
                if(e.State=="completed") { if(e.Roots.Any(r=>r.State!="Completed"))return false;completed++; }
                if(e.State=="failed") { if(i!=s.Executions.Count-1 || e.Roots.Last().State!="Failed")return false;failed=e.Ordinal;failure=e.Roots.Last().FailureReason; }
            }
            // 完成事件挂接的合法续接由原派发收据证明，不把它误作分配业务根。
            var continuations=new HashSet<string>(StringComparer.Ordinal);
            foreach(var receipt in game.EffectRuntime.DispatchReceipts.Where(d=>d.Succeeded))
            {
                var completedEvent=game.EffectRuntime.RuleEvents.Find(e=>e.EventId==receipt.EventId && e.EventType=="EffectCompleted");
                var source=game.EffectRuntime.EffectNodes.Find(c=>c.EffectId==completedEvent?.SourceEffectId);
                if(source==null || source.ContinuationScopeRootEffectId!=n.EffectId || source.CompletedEventId!=receipt.EventId)continue;
                if(!game.EffectRuntime.ContinuationBindings.Any(b=>b.BindingId==source.ContinuationBindingId && b.SourceEffectId==source.EffectId && b.CompletionEventId==receipt.EventId &&
                    b.HandlerId==receipt.HandlerId && b.ContentId==receipt.ContentId && b.ContentHash==source.ContinuationContentHash && b.DefinitionVersion==receipt.DefinitionVersion))continue;
                foreach(string id in receipt.AttachedEffectIds)
                    if(game.EffectRuntime.EffectNodes.Any(c=>c.EffectId==id && c.ParentEffectId==n.EffectId))continuations.Add(id);
            }
            if(n.ChildEffectIds.Any(id=>!rootIds.Contains(id) && !continuations.Contains(id)))return false;
            if(s.NextOrdinal!=completed+1)return false;
            if(s.Result==null) return failed==0 && n.NormalizedResult.Kind==NormalizedValueKind.Null && n.Status!=EffectNodeStatus.Completed;
            var result=s.Result;var copy=GameStateCloneService.DeepClone(result);copy.ResultDigest="";
            if(result.Version!="allocation_result_v1" || result.OwnerEffectId!=n.EffectId || result.ContentIdentity!=s.ContentIdentity || result.ResultDigest!=Digest(copy) ||
                Digest(result.Sequence)!=Digest(s.Sequence) || Digest(result.AllocatedCounts)!=Digest(s.AllocatedCounts) || Digest(result.Executions)!=Digest(s.Executions) || result.SubmitKind!=s.SubmitKind ||
                result.AllocatedCount!=s.Sequence.Count || result.AttemptedCount!=s.Executions.Count || result.CompletedCount!=completed || result.FailedOrdinal!=failed || result.FailureReason!=failure || result.UnexecutedFrom!=s.Executions.Count+1 ||
                result.Outcome!=(failed>0?"stopped_on_failure":"completed") || failed==0 && completed!=s.Sequence.Count || Digest(n.NormalizedResult)!=Digest(Summary(result)))return false;
            reason="";return true;
        }
        private static bool SequenceEqualAnswer(EffectNodeRuntimeState n,NormalizedValue a) => a.Kind==NormalizedValueKind.Object && a.Properties.Count==4 &&
            S(a,"candidateSetId")==n.AllocationV1.CandidateSetId && V(a,"candidateSetVersion")?.Kind==NormalizedValueKind.Integer && I(a,"candidateSetVersion")==1 &&
            S(a,"submitKind")==n.AllocationV1.SubmitKind && V(a,"sequence")?.Kind==NormalizedValueKind.Array && V(a,"sequence").Items.All(v=>v.Kind==NormalizedValueKind.String) && V(a,"sequence").Items.Select(v=>v.StringValue).SequenceEqual(n.AllocationV1.Sequence);

        public static string ValidateRaw(JObject node,bool runtime)
        {
            if(node.Value<string>("EffectTypeId")!=TypeId) return (node["AllocationOptions"]==null || node["AllocationOptions"].Type==JTokenType.Null) && (node["AllocationV1"]==null || node["AllocationV1"].Type==JTokenType.Null) ? "" : "allocation_state_on_other_effect";
            if(!RawShape(node["AllocationOptions"],typeof(List<AllocationOptionV1>)) || runtime && node.Property("AllocationV1")==null || runtime && node["AllocationV1"].Type!=JTokenType.Null && !RawShape(node["AllocationV1"],typeof(AllocationStateV1)))return "allocation_raw_shape_invalid";
            if(runtime && node.Value<string>("DefinitionVersion")!=ExecutorVersion || !runtime && !string.IsNullOrEmpty(node.Value<string>("DefinitionVersion")) && node.Value<string>("DefinitionVersion")!=ExecutorVersion)return "allocation_executor_version_invalid";
            return "";
        }
        private static bool RawShape(JToken token,Type type)
        {
            if(token==null)return false;
            if(type==typeof(string))return token.Type==JTokenType.String;
            if(type==typeof(bool))return token.Type==JTokenType.Boolean;
            if(type==typeof(int))return token.Type==JTokenType.Integer && token.Value<long>()>=int.MinValue && token.Value<long>()<=int.MaxValue;
            if(type==typeof(long))return token.Type==JTokenType.Integer;
            if(type.IsEnum)return token.Type==JTokenType.Integer && Enum.IsDefined(type,token.Value<int>());
            if(type.IsGenericType && type.GetGenericTypeDefinition()==typeof(List<>))return token is JArray array && array.All(t=>RawShape(t,type.GetGenericArguments()[0]));
            if(token is not JObject obj)return false;
            var fields=type.GetFields(BindingFlags.Public|BindingFlags.Instance);
            return obj.Properties().Count()==fields.Length && fields.All(f=>obj.Property(f.Name)!=null && (type==typeof(AllocationStateV1) && f.Name=="Result" && obj[f.Name].Type==JTokenType.Null || RawShape(obj[f.Name],f.FieldType)));
        }
    }
}
