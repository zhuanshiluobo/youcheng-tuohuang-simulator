using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // 移动接口之外的业务编排。候选直接承载真实返还效果，选择由标准Choice节点拥有。
    public static class CityStyleMarkerReturnChoice
    {
        public const string TypeId="effect.flow.choose_city_style_marker_return",Version="1.0.0";
        public static string ValidateArguments(NormalizedValue args)=>args?.Kind!=NormalizedValueKind.Object||!args.IsValid()||
            args.Properties.Any(p=>!new[]{"kind","effectTypeId","apiVersion","player"}.Contains(p.Name))||
            Value(args,"apiVersion")?.Kind!=NormalizedValueKind.Integer||Number(args,"apiVersion")!=1||
            !int.TryParse(Text(args,"player"),NumberStyles.None,CultureInfo.InvariantCulture,out int player)||player<1?"city_style_return_choice_arguments_invalid":"";
        public static string ValidateSavedVersion(string version,bool runtime=false)=>version==Version||!runtime&&string.IsNullOrEmpty(version)?"":"city_style_return_choice_version_unsupported";
        public static void Register(EffectRegistry registry)
        {
            if(!registry.TryGet(TypeId,out _))registry.Register(new EffectRegistration(TypeId,Execute,EffectExecutorKind.IntrinsicFlow,Version){Validator=spec=>ValidateArguments(spec.NormalizedArguments)});
        }
        private sealed class Target { public string Id,Zone,Slot,Style; }
        private static EffectSpec Build(int player,List<Target> targets)
        {
            var options=new List<string>();var branches=new List<List<EffectSpec>>();var labels=new List<GameplayOptionPresentation>();
            if(targets.Count>16)
            {
                // 每个节点最多16项；分组仍是真实Choice效果，不截断名单或提高任何预算。
                int chunk=16;while((targets.Count+chunk-1)/chunk>16)chunk=checked(chunk*16);
                for(int first=0;first<targets.Count;first+=chunk)
                {
                    var subset=targets.Skip(first).Take(chunk).ToList();string id="group:"+subset[0].Id;
                    options.Add(id);branches.Add(new List<EffectSpec>{Build(player,subset)});
                    labels.Add(new GameplayOptionPresentation{CandidateId=id,Label=new GameplayTextRef("core:city_style.return_marker.group",new GameplayTextArgument{Name="count",Kind="integer",Value=NormalizedValue.CreateInteger(subset.Count)})});
                }
            }
            else foreach(var target in targets)
            {
                var source=Object("zoneId",NormalizedValue.CreateString(target.Zone));
                if(target.Slot.Length>0){source.Properties.Add(new NormalizedValueEntry{Name="slotId",Value=NormalizedValue.CreateString(target.Slot)});source.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));}
                var move=EffectSpec.Create(ContentLifecycleEffectExecutor.ReturnMarkerTypeId,Object("apiVersion",NormalizedValue.CreateInteger(2),"player",NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),
                    "selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{target.Id}),"expectedSource",source)),player);
                options.Add(target.Id);branches.Add(new List<EffectSpec>{move});
                labels.Add(new GameplayOptionPresentation{CandidateId=target.Id,Label=new GameplayTextRef("core:city_style.return_marker.item",
                    new GameplayTextArgument{Name="style",Kind="item",Value=NormalizedValue.CreateStableReference("item",target.Style)},
                    new GameplayTextArgument{Name="marker",Kind="item",Value=NormalizedValue.CreateStableReference("item",target.Id)})});
            }
            var spec=EffectSpec.Create(LuaDomainEffectTypeIds.Choice,Object("player",NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),"options",Strings(options),
                "interactionType",NormalizedValue.CreateString("effect.choice.execute"),"promptKey",NormalizedValue.CreateString("core:interaction.city_style.return_marker.prompt")),player);
            spec.NestedEffectGroups=branches;spec.Presentation=new EffectPresentationDefinition{Interaction=new GameplayInteractionPresentation{
                Prompt=new GameplayTextRef("core:interaction.city_style.return_marker.prompt"),Options=labels}};
            return spec;
        }
        private static List<EffectNodeRuntimeState> OwnChildren(GameState s,EffectNodeRuntimeState n)=>n.ChildEffectIds.Where(id=>!s.EffectRuntime.DispatchReceipts.Any(r=>r.Succeeded&&r.AttachedEffectIds.Contains(id))).Select(id=>s.EffectRuntime.EffectNodes.Single(x=>x.EffectId==id)).ToList();
        private static NormalizedValue Projection(GameState s,EffectNodeRuntimeState n)
        {
            var returns=AbilityActionExecutor.Descendants(s,n.EffectId).Where(c=>c.EffectTypeId==ContentLifecycleEffectExecutor.ReturnMarkerTypeId&&c.Status==EffectNodeStatus.Completed&&Flag(c.NormalizedResult,"changed")).ToList();
            if(returns.Count!=1)throw new InvalidOperationException("city_style_return_choice_committed_proof_missing");
            return returns[0].NormalizedResult.Clone();
        }
        private static EffectStepResult Execute(EffectExecutionContext c)
        {
            string error=ValidateArguments(c.Node.NormalizedArguments);if(error.Length>0)return EffectStepResult.Failed(error);
            if(Text(c.Node.NormalizedArguments,"player")!=c.Node.PlayerId.ToString(CultureInfo.InvariantCulture))return EffectStepResult.Failed("transfer_permission_denied");
            if(c.Node.FlowStage=="")
            {
                var targets=CityStyleMarkerReturn.Candidates(c.State,c.Node.PlayerId).Select(id=>{
                    var item=c.State.ItemZones.Items.Single(i=>i.ItemId==id);var zone=c.State.ItemZones.Zones.Single(z=>z.ZoneId==item.ZoneId);
                    return new Target{Id=id,Zone=item.ZoneId,Slot=item.SlotId,Style=zone.ParentId};}).ToList();
                var choice=Build(c.Node.PlayerId,targets);choice.StableKey=c.Node.EffectId+":marker-choice";
                return EffectStepResult.Continue("choosing").AddChild(choice);
            }
            if(!TryValidate(c.State,c.Node))throw new InvalidOperationException("city_style_return_choice_state_invalid");
            var child=OwnChildren(c.State,c.Node).Single();
            if(child.Status==EffectNodeStatus.Failed)return EffectStepResult.Failed(child.FailureReason).WithFlowStage("finished");
            if(child.Status!=EffectNodeStatus.Completed)return EffectStepResult.NoProgress("等待样式标记的实际返还完成。");
            return EffectStepResult.Completed(Projection(c.State,c.Node)).WithFlowStage("finished");
        }
        private static void ReadTargets(GameState s,EffectSpecRuntimeState spec,int player,List<Target> targets)
        {
            if(spec.EffectTypeId==LuaDomainEffectTypeIds.Choice)
            { foreach(var nested in spec.NestedEffects)ReadTargets(s,nested,player,targets);return; }
            if(spec.EffectTypeId!=ContentLifecycleEffectExecutor.ReturnMarkerTypeId||ContentLifecycleEffectExecutor.ValidateArguments(spec.EffectTypeId,spec.NormalizedArguments).Length>0||spec.PlayerId!=player)throw new InvalidOperationException();
            var selector=Value(spec.NormalizedArguments,"selector");string id=Value(selector,"itemIds").Items[0].StringValue;var source=Value(selector,"expectedSource");string zoneId=Text(source,"zoneId");
            if(!CityStyleMarkerReturn.EligibleSource(s,player,id,zoneId))throw new InvalidOperationException();
            targets.Add(new Target{Id=id,Zone=zoneId,Slot=Text(source,"slotId"),Style=s.ItemZones.Zones.Single(z=>z.ZoneId==zoneId).ParentId});
        }
        private static object Shape(EffectSpecRuntimeState spec)=>new{spec.EffectTypeId,spec.PlayerId,spec.NormalizedArguments,spec.Presentation,spec.NestedGroupSizes,Children=spec.NestedEffects.Select(Shape).ToList()};
        public static bool TryValidate(GameState s,EffectNodeRuntimeState n)
        {
            if(n.EffectTypeId!=TypeId)return true;
            try
            {
                if(ValidateArguments(n.NormalizedArguments).Length>0||ValidateSavedVersion(n.DefinitionVersion,true).Length>0||Text(n.NormalizedArguments,"player")!=n.PlayerId.ToString(CultureInfo.InvariantCulture)||n.PositionChanges.Count!=0||s.EffectRuntime.InteractionRequests.Any(r=>r.OwnerEffectId==n.EffectId))return false;
                var own=OwnChildren(s,n);if(n.FlowStage=="")return own.Count==0&&n.Status!=EffectNodeStatus.Completed;
                if(!new[]{"choosing","finished"}.Contains(n.FlowStage)||own.Count!=1)return false;var child=own[0];
                if(child.EffectTypeId!=LuaDomainEffectTypeIds.Choice||child.PlayerId!=n.PlayerId||child.ParentEffectId!=n.EffectId||child.EffectId!=StableIdFactory.Create("effect",n.EffectId,"child","child_effect",n.ChildEffectIds.IndexOf(child.EffectId).ToString(CultureInfo.InvariantCulture),child.EffectTypeId,n.EffectId+":marker-choice"))return false;
                var actual=new EffectSpecRuntimeState{EffectTypeId=child.EffectTypeId,PlayerId=child.PlayerId,NormalizedArguments=child.NormalizedArguments,Presentation=child.Presentation,NestedGroupSizes=child.NestedGroupSizes,NestedEffects=child.NestedEffects};
                var targets=new List<Target>();ReadTargets(s,actual,n.PlayerId,targets);
                if(targets.Select(t=>t.Id).Distinct().Count()!=targets.Count||!targets.Select(t=>t.Id).SequenceEqual(targets.Select(t=>t.Id).OrderBy(id=>id,StringComparer.Ordinal)))return false;
                if(ItemTransferService.MovementV4Digest(Shape(actual))!=ItemTransferService.MovementV4Digest(Shape(Build(n.PlayerId,targets).ToPersistentDefinition())))return false;
                if(n.FlowStage!="finished")return true;
                if(n.Status==EffectNodeStatus.Failed||n.PendingOutcome==EffectPendingOutcome.Failed)return child.Status==EffectNodeStatus.Failed&&n.FailureReason==child.FailureReason;
                return child.Status==EffectNodeStatus.Completed&&ItemTransferService.MovementV4Digest(n.NormalizedResult)==ItemTransferService.MovementV4Digest(Projection(s,n));
            }
            catch(InvalidOperationException){return false;}
        }
    }
}
