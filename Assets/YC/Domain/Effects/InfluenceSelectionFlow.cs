using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // 基础操作内部的选择流程；没有独立选择节点或旧类型兼容执行入口。
    public static class InfluenceSelectionFlow
    {
        public const string ProtocolVersion = "influence.selection.v3.1";
        public const string InteractionSemantic = "influence_selection_v3";
        public static readonly string[] Fields = { "kind", "effectTypeId", "apiVersion", "targetMode", "executingPlayer",
            "subjectFilter", "excludedSubjectKeys", "selectionScope", "candidateIds", "distinctPolicy", "count", "requiredCount",
            "requireCompleteSelection", "allowDecline", "promptKey", "nextPromptKey", "interactionType" };
        public static string Operation(string type) => type == InfluenceEffectTypeIds.RemoveInfluence ? "remove" :
            type == InfluenceEffectTypeIds.MoveInfluence ? "move" : type == InfluenceEffectTypeIds.ReplaceInfluence ? "replace" : "";
        public static bool IsSelection(string type, NormalizedValue args) => Operation(type).Length > 0 &&
            (Value(args, "targetMode") != null || Number(args, "apiVersion") == 3);
        private static string Scope(EffectNodeRuntimeState node) => Text(node.NormalizedArguments, "selectionScope");
        private static List<string> ReadStrings(NormalizedValue args, string key) => Value(args,key)?.Items?.Select(v=>v.StringValue).ToList() ?? new List<string>();
        private static bool StringArray(NormalizedValue v) => v?.Kind == NormalizedValueKind.Array && v.Items != null &&
            v.Items.All(x=>x?.Kind == NormalizedValueKind.String && !string.IsNullOrWhiteSpace(x.StringValue)) &&
            v.Items.Select(x=>x.StringValue).Distinct(StringComparer.Ordinal).Count()==v.Items.Count;
        public static string ValidateArguments(string type, NormalizedValue args)
        {
            if (Operation(type).Length==0 || args?.Kind!=NormalizedValueKind.Object || args.Properties==null ||
                args.Properties.Any(p=>p==null || p.Value==null || !Fields.Contains(p.Name)) ||
                args.Properties.Select(p=>p.Name).Distinct().Count()!=args.Properties.Count ||
                Value(args,"apiVersion")?.Kind!=NormalizedValueKind.Integer || Value(args,"apiVersion").IntegerValue!=3 ||
                Text(args,"targetMode")!="select") return "influence_selection_arguments_invalid";
            if(Value(args,"kind")!=null && Text(args,"kind")!="effect_spec" || Value(args,"effectTypeId")!=null && Text(args,"effectTypeId")!=type)return "influence_selection_arguments_invalid";
            var player=Value(args,"executingPlayer");
            if (player?.Kind!=NormalizedValueKind.StableReference || player.ReferenceType!="player" ||
                !int.TryParse(player.ReferenceId,NumberStyles.None,CultureInfo.InvariantCulture,out int id) || id<1) return "invalid_player";
            foreach(string field in new[]{"subjectFilter","selectionScope","distinctPolicy","promptKey","nextPromptKey","interactionType"})
                if(Value(args,field)!=null && Value(args,field).Kind!=NormalizedValueKind.String) return "influence_selection_arguments_invalid";
            if(!new[]{"self","player","opponent","any"}.Contains(Text(args,"subjectFilter","self")) ||
                !new[]{"","resource_points","routes"}.Contains(Text(args,"selectionScope")) ||
                !new[]{"none","instance","subject"}.Contains(Text(args,"distinctPolicy","none"))) return "influence_selection_arguments_invalid";
            foreach(string field in new[]{"count","requiredCount"})
                if(Value(args,field)!=null && Value(args,field).Kind!=NormalizedValueKind.Integer) return "influence_selection_arguments_invalid";
            long rawCount=Value(args,"count")?.IntegerValue??1,rawRequired=Value(args,"requiredCount")?.IntegerValue??0;
            if(rawCount<1 || rawCount>16 || rawRequired<0 || rawRequired>rawCount)return "influence_selection_arguments_invalid";
            int count=(int)rawCount,required=(int)rawRequired;
            if(count<1 || count>16 || required<0 || required>count) return "influence_selection_arguments_invalid";
            foreach(string field in new[]{"requireCompleteSelection","allowDecline"})
                if(Value(args,field)!=null && Value(args,field).Kind!=NormalizedValueKind.Boolean) return "influence_selection_arguments_invalid";
            if(Flag(args,"requireCompleteSelection") && (Operation(type)=="move" || required!=count)) return "complete_selection_arguments_invalid";
            var excluded=Value(args,"excludedSubjectKeys");
            if(excluded!=null && (!StringArray(excluded) || excluded.Items.Any(v=>!IsSubjectKey(v.StringValue)))) return "excluded_subject_keys_rejected";
            var allowed=Value(args,"candidateIds");
            if(allowed!=null && (!StringArray(allowed) || Operation(type)!="remove" || count!=1 || required!=1 || !Flag(args,"requireCompleteSelection"))) return "influence_selection_arguments_invalid";
            if(Operation(type)=="move" && Value(args,"interactionType")!=null && !new[]{"","lua.choice","character.ability.choice.move"}.Contains(Text(args,"interactionType"))) return "influence_selection_interaction_type_rejected";
            return "";
        }
        private static bool IsSubjectKey(string key) => key=="company:stonehoove_security" ||
            key.StartsWith("player:",StringComparison.Ordinal) && int.TryParse(key.Substring(7),NumberStyles.None,CultureInfo.InvariantCulture,out int player) && player>0;
        public static string ValidateSpec(EffectSpec spec)
        {
            string reason=ValidateArguments(spec.EffectTypeId,spec.NormalizedArguments);
            if(reason.Length>0)return reason;
            if(Value(spec.NormalizedArguments,"executingPlayer").ReferenceId!=spec.PlayerId.ToString(CultureInfo.InvariantCulture) || spec.AllowDecline!=Flag(spec.NormalizedArguments,"allowDecline"))return "influence_selection_executor_mismatch";
            return spec.Children.Count>0 || spec.NestedEffectGroups.Count>0 ? "influence_selection_body_rejected" : "";
        }
        public static string ValidateSavedSpec(EffectSpecRuntimeState spec)
        {
            string reason=ValidateArguments(spec.EffectTypeId,spec.NormalizedArguments);if(reason.Length>0)return reason;
            if(Value(spec.NormalizedArguments,"executingPlayer").ReferenceId!=spec.PlayerId.ToString(CultureInfo.InvariantCulture) || spec.AllowDecline!=Flag(spec.NormalizedArguments,"allowDecline"))return "influence_selection_executor_mismatch";
            return spec.NestedEffects.Count>0 || spec.NestedGroupSizes.Count>0 ? "influence_selection_body_rejected" : "";
        }
        private static EffectSpec ConstrainChild(EffectSpec child, NormalizedValue args, string filter)
        {
            child.NormalizedArguments.Properties.Add(new NormalizedValueEntry {Name="subjectFilter",Value=NormalizedValue.CreateString(filter)});
            if(Value(args,"excludedSubjectKeys")!=null) child.NormalizedArguments.Properties.Add(new NormalizedValueEntry {Name="excludedSubjectKeys",Value=Value(args,"excludedSubjectKeys").Clone()});
            child.NormalizedArguments.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));return child;
        }
        private static bool DistinctSubjects(GameState state, NormalizedValue args, IEnumerable<string> ids)
        {
            if(!(Text(args,"distinctPolicy")=="subject"))return true;
            var keys=ids.Select(id=>new ItemZoneQuery().FindItem(state,id)).Select(i=>i==null?"":InfluenceSubjectQuery.GetSubjectKey(i.OwnerSubject)).ToList();
            return keys.All(k=>k.Length>0) && keys.Distinct().Count()==keys.Count;
        }
        private static int AvailableCount(GameState state,List<string> ids,NormalizedValue args) =>
            (Text(args,"distinctPolicy")=="subject") ? ids.Select(id=>new ItemZoneQuery().FindItem(state,id))
                .Where(i=>i!=null).Select(i=>InfluenceSubjectQuery.GetSubjectKey(i.OwnerSubject)).Distinct().Count() : ids.Count;
        private static bool IsEventChild(GameState state,string id) => state.EffectRuntime.DispatchReceipts.Any(r=>r.Succeeded && r.AttachedEffectIds.Contains(id));
        internal static List<string> ApplyParentScope(EffectExecutionContext context, List<string> ids)
        {
            var parent=context.State.EffectRuntime.EffectNodes.Find(n=>n.EffectId==context.Node.ParentEffectId);
            if(parent==null || IsEventChild(context.State,context.Node.EffectId) || !IsSelection(parent.EffectTypeId,parent.NormalizedArguments))return ids;
            var service=context.Registry.InfluenceService ?? new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId)));
            FilterByScope(context.State,service,ids,Scope(parent));return ids;
        }
        internal static bool ParentDistinctSubjects(EffectExecutionContext context,IEnumerable<string> targets)
        {
            var parent=context.State.EffectRuntime.EffectNodes.Find(n=>n.EffectId==context.Node.ParentEffectId);
            return parent==null || IsEventChild(context.State,context.Node.EffectId) || !IsSelection(parent.EffectTypeId,parent.NormalizedArguments) ||
                DistinctSubjects(context.State,parent.NormalizedArguments,targets);
        }
        public static bool TryValidateAnswer(GameState state, InteractionRequest request, NormalizedValue answer, out string reason)
        {
            reason="";var node=state.EffectRuntime.EffectNodes.Find(n=>n.EffectId==request.OwnerEffectId);
            if(node==null || !IsSelection(node.EffectTypeId,node.NormalizedArguments) || request.InteractionTypeId=="action.decline_effect")return true;
            if(!Flag(node.NormalizedArguments,"requireCompleteSelection"))return true;
            var values=answer.Kind==NormalizedValueKind.Array?answer.Items:new List<NormalizedValue>{answer};
            var selected=values.Select(ReadId).ToList();
            if(!DistinctSubjects(state,node.NormalizedArguments,selected)) { reason="influence_selection_subject_not_distinct";return false; }
            return true;
        }
        private static string ReadId(NormalizedValue value) => value?.Kind==NormalizedValueKind.StableReference?value.ReferenceId:value?.Kind==NormalizedValueKind.String?value.StringValue:"";
        private static string ArgumentsDigest(EffectNodeRuntimeState node)
        {
            using(var hash=System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(ProtocolVersion+"|"+node.EffectTypeId+"|"+node.PlayerId.ToString(CultureInfo.InvariantCulture)+"|"+node.SourceId+"|"+node.NormalizedArguments.ToDeterministicString()))).Replace("-","").ToLowerInvariant();
        }
        public static bool TryValidateNode(GameState state, EffectNodeRuntimeState node, out string reason)
        {
            reason="";
            if(!IsSelection(node.EffectTypeId,node.NormalizedArguments))
            {
                if(Operation(node.EffectTypeId).Length>0 && (Value(node.ContinuationData,"selectionProtocolVersion")!=null ||
                    state.EffectRuntime.InteractionRequests.Any(r=>r.OwnerEffectId==node.EffectId && Text(r.PromptParameters,"interactionSemantic")==InteractionSemantic)))
                { reason="influence_selection_state_mode_missing";return false; }
                return true;
            }
            reason=ValidateArguments(node.EffectTypeId,node.NormalizedArguments);if(reason.Length>0)return false;
            reason="influence_selection_state_invalid";
            var args=node.NormalizedArguments;int count=Number(args,"count",1),index=Number(node.ContinuationData,"nextIndex");
            bool initialized=Value(node.ContinuationData,"selectionProtocolVersion")!=null;
            if(initialized)
            {
                if(Value(node.ContinuationData,"selectionProtocolVersion").Kind!=NormalizedValueKind.Integer || Value(node.ContinuationData,"selectionProtocolVersion").IntegerValue!=1 ||
                    Text(node.ContinuationData,"selectionArgumentsDigest")!=ArgumentsDigest(node) ||
                    new[]{"nextIndex","selectedItemIds","processedSubjectKeys"}.Any(k=>Value(node.ContinuationData,k)==null))return false;
            }
            else if(!new[]{"","awaiting_decline","optional_accepted","decline_accepted"}.Contains(node.FlowStage))return false;
            if(Value(args,"executingPlayer").ReferenceId!=node.PlayerId.ToString(CultureInfo.InvariantCulture) ||
                node.AllowDecline!=Flag(args,"allowDecline") || node.DecisionPlayerId!=node.PlayerId ||
                node.NestedEffects.Count!=0 || node.NestedGroupSizes.Count!=0 || node.DefinitionVersion!=InfluenceEffectExecutor.DefinitionVersion ||
                !new[]{"","awaiting","applied","choose_complete","applied_strict","applied_complete","awaiting_decline","optional_accepted","decline_accepted","selection_terminal"}.Contains(node.FlowStage))return false;
            foreach(string key in new[]{"selectedItemIds","processedSubjectKeys"})
                if(Value(node.ContinuationData,key)!=null && !StringArray(Value(node.ContinuationData,key)))return false;
            foreach(string key in new[]{"nextIndex","completedCount"})
                if(Value(node.ContinuationData,key)!=null && (Value(node.ContinuationData,key).Kind!=NormalizedValueKind.Integer || Value(node.ContinuationData,key).IntegerValue<0 || Value(node.ContinuationData,key).IntegerValue>count))return false;
            var selected=ReadStrings(node.ContinuationData,"selectedItemIds");if(selected.Count>count)return false;
            var children=node.ChildEffectIds.Where(id=>!IsEventChild(state,id)).Select(id=>state.EffectRuntime.EffectNodes.Find(n=>n.EffectId==id)).ToList();
            if(children.Any(n=>n==null || n.EffectTypeId!=node.EffectTypeId || IsSelection(n.EffectTypeId,n.NormalizedArguments)))return false;
            var requests=state.EffectRuntime.InteractionRequests.Where(r=>r.OwnerEffectId==node.EffectId && r.InteractionTypeId!="action.decline_effect").ToList();
            foreach(var r in requests)
                if(r.AnsweringPlayerId!=node.PlayerId || Text(r.PromptParameters,"interactionSemantic")!=InteractionSemantic ||
                    Text(r.PromptParameters,"ownerEffectTypeId")!=node.EffectTypeId || Text(r.PromptParameters,"influenceOperation")!=Operation(node.EffectTypeId) ||
                    Value(r.PromptParameters,"selectionApiVersion")?.Kind!=NormalizedValueKind.Integer || Value(r.PromptParameters,"selectionApiVersion").IntegerValue!=3 || r.CandidateSetVersion<1 || string.IsNullOrEmpty(r.CandidateResolutionId) ||
                    r.MinSelections!=(Flag(args,"requireCompleteSelection")?count:1) || r.MaxSelections!=r.MinSelections ||
                    r.AnswerSchema!=(Flag(args,"requireCompleteSelection") && Value(args,"candidateIds")==null?"candidate_id_array":"candidate_id"))return false;
            var answered=requests.Where(r=>r.Status=="answered").ToList();bool complete=Flag(args,"requireCompleteSelection");
            if(complete && Operation(node.EffectTypeId)=="remove")
            {
                if(index!=0 || children.Count>1 || children.Count==1 && node.FlowStage!="applied_strict" && node.FlowStage!="selection_terminal")return false;
            }
            else
            {
                int pending=new[]{"applied","applied_complete"}.Contains(node.FlowStage)?1:0;
                if(node.FlowStage=="selection_terminal")
                {
                    if(node.Status==EffectNodeStatus.Completed || node.PendingOutcome==EffectPendingOutcome.Completed) { if(children.Count!=index)return false; }
                    else if(children.Count!=index && children.Count!=index+1)return false;
                }
                else if(children.Count!=index+pending)return false;
            }
            if(children.Count>0 && answered.Count==0)return false;
            if(!complete && (answered.Count<children.Count || answered.Count>children.Count+1))return false;
            var actualIds=new List<string>();
            for(int i=0;i<children.Count;i++)
            {
                var child=children[i];EffectSpec expected;
                var values=answered[complete?0:i].NormalizedAnswer;
                var ids=(values.Kind==NormalizedValueKind.Array?values.Items:new List<NormalizedValue>{values}).Select(ReadId).ToList();
                if(ids.Count!=(complete?count:1) || ids.Any(string.IsNullOrEmpty) || ids.Distinct().Count()!=ids.Count)return false;
                if(complete && Operation(node.EffectTypeId)=="remove")
                {
                    if(!selected.SequenceEqual(ids))return false;
                    expected=InfluenceEffectExecutor.StrictRemove(node.PlayerId,ids,count,node.SourceId);
                }
                else
                {
                    string target;
                    if(complete) { if(selected.Count!=count || !selected.SequenceEqual(ids))return false;target=selected[i]; }
                    else target=ids.SingleOrDefault()??"";
                    if(Operation(node.EffectTypeId)=="move")
                    {
                        var parts=target.Split('|');if(parts.Length!=3 || parts[0]!="move")return false;
                        expected=InfluenceEffectSpecFactory.MoveInfluence(node.PlayerId,parts[1],parts[2]);actualIds.Add(parts[1]);
                    }
                    else
                    {
                        expected=Operation(node.EffectTypeId)=="replace"?InfluenceEffectSpecFactory.ReplaceInfluence(node.PlayerId,target,node.SourceId):InfluenceEffectSpecFactory.RemoveInfluence(node.PlayerId,target,node.SourceId);actualIds.Add(target);
                    }
                }
                expected=ConstrainChild(expected,args,Text(args,"subjectFilter","self"));
                if(child.ParentEffectId!=node.EffectId || child.PlayerId!=node.PlayerId || child.AllowDecline ||
                    child.NormalizedArguments.ToDeterministicString()!=expected.NormalizedArguments.ToDeterministicString())return false;
            }
            if(!complete)
            {
                if(!selected.SequenceEqual(actualIds.Distinct(StringComparer.Ordinal)))return false;
                var processed=Text(args,"distinctPolicy")=="subject"?children.Take(index).SelectMany(c=>ReadStrings(c.NormalizedResult,"originalSubjectKeys")).Distinct(StringComparer.Ordinal).ToList():new List<string>();
                if(!ReadStrings(node.ContinuationData,"processedSubjectKeys").SequenceEqual(processed))return false;
            }
            if(node.Status==EffectNodeStatus.Completed && (Value(node.NormalizedResult,"completedCount")?.Kind!=NormalizedValueKind.Integer || Value(node.NormalizedResult,"completedCount").IntegerValue!=(complete?count:index)))return false;
            reason="";return true;
        }
        internal static EffectStepResult Execute(EffectExecutionContext context, InfluenceService influenceService)
        {
            using var diagnostic=GameStateCloneService.MeasureDiagnostic("influence.selection",context.Node.FlowStage);
            if (!IsSelection(context.Node.EffectTypeId, context.Node.NormalizedArguments)) return EffectStepResult.Failed("influence_selection_arguments_invalid");
            if(Value(context.Node.ContinuationData,"selectionProtocolVersion")==null && context.Node.FlowStage=="")
            {
                Set(context.Node,"selectionProtocolVersion",NormalizedValue.CreateInteger(1));Set(context.Node,"selectionArgumentsDigest",NormalizedValue.CreateString(ArgumentsDigest(context.Node)));
                Set(context.Node,"nextIndex",NormalizedValue.CreateInteger(0));Set(context.Node,"selectedItemIds",Strings(Array.Empty<string>()));Set(context.Node,"processedSubjectKeys",Strings(Array.Empty<string>()));
            }
            var result=ExecuteCore(context,influenceService);
            if(!result.HasPendingOutcome)return result;
            var children=context.ChildNodes.Where(n=>!IsEventChild(context.State,n.EffectId)).ToList();var childIds=children.Select(c=>c.EffectId).ToList();
            int removed=context.State.EffectRuntime.InfluenceDepartures.Count(d=>childIds.Contains(d.SourceEffectId));
            if(result.PendingOutcome==EffectPendingOutcome.Failed)
                result=EffectStepResult.Failed(result.FailureReason,Object("completedCount",NormalizedValue.CreateInteger(Number(context.Node.ContinuationData,"nextIndex")),
                    "requestedCount",NormalizedValue.CreateInteger(Number(context.Node.NormalizedArguments,"count",1)),
                    "actualRemovedCount",NormalizedValue.CreateInteger(removed),"changed",NormalizedValue.CreateBoolean(removed>0 || children.Any(c=>Flag(c.NormalizedResult,"changed")))));
            else
            {
                result.NormalizedResult.Properties.Add(new NormalizedValueEntry{Name="requestedCount",Value=NormalizedValue.CreateInteger(Number(context.Node.NormalizedArguments,"count",1))});
                result.NormalizedResult.Properties.Add(new NormalizedValueEntry{Name="actualRemovedCount",Value=NormalizedValue.CreateInteger(removed)});
                result.NormalizedResult.Properties.Add(new NormalizedValueEntry{Name="operationEffectIds",Value=Strings(childIds)});
                var subjects=children.SelectMany(c=>ReadStrings(c.NormalizedResult,"originalSubjectKeys")).Distinct(StringComparer.Ordinal).ToList();
                result.NormalizedResult.Properties.RemoveAll(p=>p.Name=="originalSubjectKeys");
                result.NormalizedResult.Properties.Add(new NormalizedValueEntry{Name="originalSubjectKeys",Value=Strings(subjects)});
                result.NormalizedResult.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));
            }
            return result.WithFlowStage("selection_terminal");
        }
        private static EffectStepResult ExecuteCore(EffectExecutionContext context, InfluenceService influenceService)
        {
            var node = context.Node;
            var args = node.NormalizedArguments;
            string operation = Operation(node.EffectTypeId), filter = Text(args, "subjectFilter", "self");
            if (!TryValidateNode(context.State, node, out string invalid)) throw new InvalidOperationException(invalid);
            int count = Number(args, "count", 1), required = Number(args, "requiredCount", 0);
            if (!new[] { "move", "replace", "remove" }.Contains(operation) || !new[] { "self", "player", "opponent", "any" }.Contains(filter) || count < 1 || count > 16 || required < 0 || required > count)
                return EffectStepResult.Failed("influence_selection_arguments_invalid");
            var allowedIds = Value(args, "candidateIds");
            if (allowedIds != null && (operation != "remove" || count != 1 || required != 1 || !Flag(args, "requireCompleteSelection") ||
                    allowedIds.Kind != NormalizedValueKind.Array ||
                    allowedIds.Items.Any(v => v.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(v.StringValue)) ||
                    allowedIds.Items.Select(v => v.StringValue).Distinct(StringComparer.Ordinal).Count() != allowedIds.Items.Count))
                return EffectStepResult.Failed("influence_selection_arguments_invalid");
            var service = influenceService ?? context.Registry.InfluenceService;
            var map = service?.MapQuery ?? new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId));
            service = service ?? new InfluenceService(map);
            string scope = Scope(node);
            if (!new[] { "", "resource_points", "routes" }.Contains(scope)) return EffectStepResult.Failed("influence_scope_invalid");
            if (Flag(args, "requireCompleteSelection")) return CompleteInfluenceSelection(context, service, operation, filter, count, required, scope);
            int index = ItemTransferEffectExecutor.Number(node.ContinuationData, "nextIndex");
            var processed = ItemTransferEffectExecutor.Value(node.ContinuationData, "processedSubjectKeys")?.Items?.Select(value => value.StringValue).ToList() ?? new List<string>();
            var selected = ItemTransferEffectExecutor.Value(node.ContinuationData, "selectedItemIds")?.Items?.Select(value => value.StringValue).ToList() ?? new List<string>();
            var excluded = ItemTransferEffectExecutor.Value(args, "excludedSubjectKeys")?.Items?.Select(value => value.StringValue).ToList() ?? new List<string>();
            excluded.AddRange(processed);
            if (node.FlowStage == "applied")
            {
                var child = context.ChildNodes.Last();
                if (child.Status == EffectNodeStatus.Failed) return EffectStepResult.Failed(child.FailureReason);
                if (Text(args, "distinctPolicy") == "subject")
                    foreach (var key in ItemTransferEffectExecutor.Value(child.NormalizedResult, "originalSubjectKeys")?.Items ?? new List<NormalizedValue>())
                        if (!processed.Contains(key.StringValue)) processed.Add(key.StringValue);
                index++;
                ItemTransferEffectExecutor.Set(node, "nextIndex", NormalizedValue.CreateInteger(index));
                ItemTransferEffectExecutor.Set(node, "processedSubjectKeys", ItemTransferEffectExecutor.Strings(processed));
                excluded.AddRange(processed);
            }
            else if (node.FlowStage != "" && node.FlowStage != "awaiting") return EffectStepResult.Failed("invalid_flow_stage");
            var ids = ItemZoneLegacyProjection.IsActive(context.State)
                ? new InfluenceSubjectQuery().GetCandidateItemIds(context.State, node.PlayerId, filter, excluded).ToList()
                : context.State.Map.Influences.Where(item => InfluenceSubjectQuery.Matches(LegacySubjectKey(item), node.PlayerId, filter) && !excluded.Contains(LegacySubjectKey(item)))
                    .Select(item => InfluenceIdentity.GetStableId(context.State, item)).ToList();
            FilterByScope(context.State, service, ids, scope);
            if (Text(args, "distinctPolicy") == "instance") ids.RemoveAll(selected.Contains);
            var candidates = new List<string>();
            bool entityMovement = operation == "move" && ids.Count > 0 && ItemZoneLegacyProjection.IsActive(context.State);
            var movementSlots = entityMovement ? map.Map.Locations.SelectMany(location => Enumerable.Range(0,Math.Max(0,location.InfluenceSlotCount))
                .Select(slot => InfluenceService.GetLocationSlotId(location.LocationId,slot)))
                .Concat(map.Map.Routes.SelectMany(route => Enumerable.Range(0,Math.Max(0,route.InfluenceSlotCount))
                    .Select(slot => InfluenceService.GetRouteSlotId(route.RouteId,slot)))).ToArray() : null;
            foreach (var id in ids)
            {
                if (operation != "move") { candidates.Add(id); continue; }
                if(entityMovement)
                {
                    foreach(var slot in service.GetLegalMovementSlotsInOrder(context.State,id,movementSlots))candidates.Add("move|"+id+"|"+slot);
                    continue;
                }
                var placement = service.FindInfluence(context.State, id);
                foreach (var location in map.Map.Locations)
                    for (int slot = 0; slot < location.InfluenceSlotCount; slot++) AddMoveCandidate(candidates, context, service, id, InfluenceService.GetLocationSlotId(location.LocationId, slot), placement);
                foreach (var route in map.Map.Routes)
                    for (int slot = 0; slot < route.InfluenceSlotCount; slot++) AddMoveCandidate(candidates, context, service, id, InfluenceService.GetRouteSlotId(route.RouteId, slot), placement);
            }
            candidates.Sort(StringComparer.Ordinal);
            if (index >= count || candidates.Count == 0)
                return index < required ? EffectStepResult.Failed("insufficient_items") : EffectStepResult.Completed(ItemTransferEffectExecutor.Object(
                    "changed", NormalizedValue.CreateBoolean(index > 0), "completedCount", NormalizedValue.CreateInteger(index),
                    "reasonCode", NormalizedValue.CreateString(index == 0 ? "no_candidates" : "limit_reached"), "originalSubjectKeys", ItemTransferEffectExecutor.Strings(processed)));
            if (node.FlowStage == "awaiting")
            {
                string answer = Answer(context);
                if (candidates.Contains(answer))
                {
                    string id = answer;
                    EffectSpec child;
                    if (operation == "move")
                    {
                        var parts = answer.Split('|'); id = parts[1];
                        child = InfluenceEffectSpecFactory.MoveInfluence(node.PlayerId, id, parts[2]);
                    }
                    else child = operation == "replace" ? InfluenceEffectSpecFactory.ReplaceInfluence(node.PlayerId, id, node.SourceId) : InfluenceEffectSpecFactory.RemoveInfluence(node.PlayerId, id, node.SourceId);
                    if (!selected.Contains(id)) selected.Add(id);
                    ItemTransferEffectExecutor.Set(node, "selectedItemIds", ItemTransferEffectExecutor.Strings(selected));
                    // 旧基础状态结果缺主体数组，仍从操作前的真实实例保存原主体。
                    if (!ItemZoneLegacyProjection.IsActive(context.State) && (Text(args, "distinctPolicy") == "subject"))
                    {
                        string key = LegacySubjectKey(service.FindInfluence(context.State, id)); if (!processed.Contains(key)) processed.Add(key);
                        ItemTransferEffectExecutor.Set(node, "processedSubjectKeys", ItemTransferEffectExecutor.Strings(processed));
                    }
                    return EffectStepResult.Continue("applied").AddChild(ConstrainChild(child, node.NormalizedArguments, filter));
                }
            }
            string prompt = index == 0 ? Text(args, "promptKey") : Text(args, "nextPromptKey", Text(args, "promptKey"));
            var interaction = InfluenceSelectionInteraction(node, operation, prompt, 1, "candidate_id");
            interaction.CandidateIds.AddRange(candidates);
            return EffectStepResult.Continue("awaiting").AddInteraction(ItemTransferEffectExecutor.PrepareInteraction(context, interaction));
        }
        private static bool InScope(GameState state, InfluencePlacement placement, string scope)
        {
            if (scope.Length == 0) return true;
            if (placement == null) return false;
            if (scope == "routes") return placement.RouteId.Length > 0;
            return state.Map.ResourceTokens.Any(token => placement.LocationId == token.LocationId);
        }
        private static void FilterByScope(GameState state, InfluenceService service, List<string> ids, string scope)
        {
            // 无范围约束时不需要查位置；有约束时仅在这次同步过滤内固定投影。
            if (scope.Length == 0) return;
            var placements = state.Map.Influences.ToArray();
            ids.RemoveAll(id => !InScope(state, service.FindInfluence(state, id, placements), scope));
        }
        private static EffectStepResult CompleteInfluenceSelection(EffectExecutionContext context, InfluenceService service, string operation, string filter, int count, int required, string scope)
        {
            if (operation == "move" || required != count) return EffectStepResult.Failed("complete_selection_arguments_invalid");
            var node = context.Node;
            var selected = Value(node.ContinuationData, "selectedItemIds")?.Items.Select(v => v.StringValue).ToList() ?? new List<string>();
            int index = Number(node.ContinuationData, "nextIndex", 0), completed = Number(node.ContinuationData, "completedCount", 0);
            var candidates = new InfluenceSubjectQuery().GetCandidateItemIds(context.State, node.PlayerId, filter, ReadStrings(node.NormalizedArguments, "excludedSubjectKeys")).OrderBy(id => id, StringComparer.Ordinal).ToList();
            FilterByScope(context.State, service, candidates, scope);
            var allowedIds = Value(node.NormalizedArguments, "candidateIds");
            if (allowedIds != null) candidates.RemoveAll(id => !allowedIds.Items.Any(v => v.StringValue == id));
            if (node.FlowStage == "")
            { if (AvailableCount(context.State, candidates, node.NormalizedArguments) < count) return EffectStepResult.Failed("insufficient_items"); }
            else if (node.FlowStage == "choose_complete")
            {
                var answer = context.GetLatestInteractionAnswer();
                var values = answer?.Kind == NormalizedValueKind.Array ? answer.Items : new List<NormalizedValue> { answer };
                selected = values.Select(v => v?.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v?.StringValue ?? "").ToList();
                if (selected.Count != count || selected.Distinct().Count() != count || selected.Any(id => !candidates.Contains(id)) || !DistinctSubjects(context.State, node.NormalizedArguments, selected))
                { if (AvailableCount(context.State, candidates, node.NormalizedArguments) < count) return EffectStepResult.Failed("insufficient_items"); selected.Clear(); }
                else Set(node, "selectedItemIds", Strings(selected));
            }
            else if (node.FlowStage == "applied_strict")
            {
                var child = context.ChildNodes.Last();
                if (child.Status != EffectNodeStatus.Completed || Number(child.NormalizedResult, "actualRemovedCount") != count) return EffectStepResult.Failed("incomplete_influence_payment");
                var result = Object("completedCount", NormalizedValue.CreateInteger(count), "changed", NormalizedValue.CreateBoolean(true));
                if (allowedIds != null)
                {
                    var departures = context.State.EffectRuntime.InfluenceDepartures.Where(d => d.SourceEffectId == child.EffectId).OrderBy(d => d.OperationIndex).ToList();
                    if (departures.Count != count || departures.Any(d => !selected.Contains(d.ItemId) || d.Status == "open"))
                        return EffectStepResult.Failed("incomplete_influence_payment");
                    var origins = departures.Select(d => {
                        var zone = context.State.ItemZones.Zones.Single(z => z.ZoneId == d.OriginalPosition.ZoneId);
                        var parent = context.State.ItemZones.Zones.Single(z => z.ZoneId == zone.ParentId);
                        return Object("influenceId", NormalizedValue.CreateString(d.ItemId), "departureId", NormalizedValue.CreateString(d.DepartureId),
                            "originalPosition", Object("zoneId", NormalizedValue.CreateString(d.OriginalPosition.ZoneId), "slotId", NormalizedValue.CreateString(d.OriginalPosition.SlotId)),
                            "locationId", NormalizedValue.CreateString(parent.TemplateId == "core.zone.map_location" ? parent.LocalZoneId : ""));
                    }).ToList();
                    result.Properties.Add(new NormalizedValueEntry { Name = "removedInfluences", Value = NormalizedValue.CreateArray(origins) });
                    result.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
                }
                return EffectStepResult.Completed(result);
            }
            else if (node.FlowStage == "applied_complete")
            {
                var child = context.ChildNodes.Last();
                if (child.Status != EffectNodeStatus.Completed || Value(child.NormalizedResult, operation == "remove" ? "removedItemIds" : "replacedItemIds")?.Items.Count != 1) return EffectStepResult.Failed("incomplete_influence_payment");
                index++; completed++; Set(node, "nextIndex", NormalizedValue.CreateInteger(index)); Set(node, "completedCount", NormalizedValue.CreateInteger(completed));
            }
            else return EffectStepResult.Failed("invalid_flow_stage");
            if (selected.Count == 0)
            {
                var request = InfluenceSelectionInteraction(node, operation, Text(node.NormalizedArguments, "promptKey"), count, allowedIds != null ? "candidate_id" : "candidate_id_array");
                request.CandidateIds.AddRange(candidates); return EffectStepResult.Continue("choose_complete").AddInteraction(PrepareInteraction(context, request));
            }
            if (operation == "remove" && index == 0) return EffectStepResult.Continue("applied_strict").AddChild(ConstrainChild(InfluenceEffectExecutor.StrictRemove(node.PlayerId, selected, count, node.SourceId), node.NormalizedArguments, filter));
            if (index == count) return EffectStepResult.Completed(Object("completedCount", NormalizedValue.CreateInteger(completed), "changed", NormalizedValue.CreateBoolean(completed == count)));
            if (!candidates.Contains(selected[index])) return EffectStepResult.Failed("incomplete_influence_payment");
            return EffectStepResult.Continue("applied_complete").AddChild(ConstrainChild(operation == "remove" ? InfluenceEffectSpecFactory.RemoveInfluence(node.PlayerId, selected[index], node.SourceId) : InfluenceEffectSpecFactory.ReplaceInfluence(node.PlayerId, selected[index], node.SourceId), node.NormalizedArguments, filter));
        }
        private static EffectInteractionSpec InfluenceSelectionInteraction(EffectNodeRuntimeState node, string operation, string prompt, int count, string schema)
        {
            string type = Text(node.NormalizedArguments, "interactionType", "effect.influence." + operation + ".target");
            if (type.Length == 0) type = "effect.influence." + operation + ".target";
            if (Value(node.NormalizedArguments, "candidateIds") != null) type = "effect.influence.remove.target";
            if (operation == "move") type = "character.ability.choice.move";
            if (prompt.Length == 0 || prompt == "effect.influence.choose") prompt = "effect.influence." + operation + ".choose_target";
            var request = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = node.PlayerId,
                Visibility = "owner", PromptKey = prompt, MinSelections = count, MaxSelections = count, AnswerSchema = schema };
            request.PromptParameters = Object("interactionSemantic", NormalizedValue.CreateString(InteractionSemantic),
                    "influenceOperation", NormalizedValue.CreateString(operation), "selectionApiVersion", NormalizedValue.CreateInteger(3),
                    "ownerEffectTypeId", NormalizedValue.CreateString(node.EffectTypeId));
            if (node.Presentation?.Interaction == null)
                request.Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:interaction.map.influence." + operation) };
            return request;
        }
        private static string LegacySubjectKey(InfluencePlacement placement) => placement?.OwnerSubject?.SubjectType == "company"
            ? "company:" + (string.IsNullOrEmpty(placement.OwnerSubject.DefinitionId) ? placement.OwnerSubject.InstanceId : placement.OwnerSubject.DefinitionId)
            : placement == null || placement.PlayerId < 1 ? "" : "player:" + placement.PlayerId.ToString(CultureInfo.InvariantCulture);
        private static void AddMoveCandidate(List<string> candidates, EffectExecutionContext context, InfluenceService service, string itemId, string slotId, InfluencePlacement placement)
        {
            bool legal = ItemZoneLegacyProjection.IsActive(context.State)
                ? service.ValidateItemPlacement(context.State, new ItemZoneQuery().FindItem(context.State, itemId).OwnerSubject, slotId).Succeeded
                : placement?.PlayerId == context.Node.PlayerId && service.CanMove(context.State, context.Node.PlayerId, itemId, slotId).IsValid;
            if (legal) candidates.Add("move|" + itemId + "|" + slotId);
        }
    }
}
