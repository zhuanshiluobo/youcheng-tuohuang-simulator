using System;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    // API-31 / 4.13.3：玩家标记移动与系统轮末收尾分离。
    public static class PlayerMarkerOperation
    {
        public const string TypeId=CityStyleSpecialActionEffectTypeIds.OperatePlayerMarker;
        public const string DefinitionVersion="2.0.0";
        public const string CleanupHostOnlyReason="player_marker_round_cleanup_host_only";
        public static bool IsLegacy(NormalizedValue args)=>Value(args,"apiVersion")==null||Text(args,"operation")=="round_cleanup"||Text(args,"mode")=="round_cleanup";
        public static string ValidateArguments(NormalizedValue args)
        {
            if(IsLegacy(args))return CleanupHostOnlyReason;
            var fields=new[]{"kind","effectTypeId","apiVersion","player","operation","selector","destination","insertion","requiredCount","carrierPolicy"};
            if(args?.Kind!=NormalizedValueKind.Object||args.Properties.Any(p=>!fields.Contains(p.Name))||Value(args,"apiVersion")?.Kind!=NormalizedValueKind.Integer||Value(args,"apiVersion").IntegerValue!=1||Text(args,"operation")!="transfer"||Text(Value(args,"selector"),"mode")!="explicit")return "player_marker_operation_arguments_invalid";
            return ItemMovementV4.ValidateArguments(CommonArguments(args));
        }
        public static NormalizedValue CommonArguments(NormalizedValue args)
        {
            var value=args.Clone();value.Properties.RemoveAll(p=>p.Name=="kind"||p.Name=="effectTypeId");
            value.Properties.Single(p=>p.Name=="apiVersion").Value=NormalizedValue.CreateInteger(4);
            value.Properties.Add(new NormalizedValueEntry{Name="operationProfileId",Value=NormalizedValue.CreateString("core.player_marker_storage")});
            value.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));return value;
        }
        public static void Register(EffectRegistry registry)
        {
            ItemTransferEffectExecutor.Register(registry);
            if(!registry.TryGet(TypeId,out _))registry.Register(new EffectRegistration(TypeId,Execute,EffectExecutorKind.IntrinsicFlow,DefinitionVersion){Validator=spec=>ValidateArguments(spec.NormalizedArguments)});
        }
        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            string error=ValidateArguments(context.Node.NormalizedArguments);if(error.Length>0)return EffectStepResult.Failed(error);
            if(context.Node.FlowStage.Length==0)
            {
                if(Text(context.Node.NormalizedArguments,"player")!=context.Node.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture))return EffectStepResult.Failed("transfer_permission_denied");
                foreach(var id in Value(Value(context.Node.NormalizedArguments,"selector"),"itemIds").Items)
                {
                    var item=context.State.ItemZones.Items.SingleOrDefault(i=>i.ItemId==id.StringValue);
                    if(item==null)return EffectStepResult.Failed("unknown_item");
                    if(!ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(item.TypeId,"core.player_marker"))return EffectStepResult.Failed("player_marker_type_rejected");
                }
            }
            return SpecializedItemMovement.ExecuteWrapper(context);
        }
        public static string ValidateSavedVersion(string type,NormalizedValue args,string version,bool runtimeNode=false)
        {
            if(type!=TypeId)return "";if(IsLegacy(args))return CleanupHostOnlyReason;
            return version==DefinitionVersion||!runtimeNode&&string.IsNullOrEmpty(version)?"":"player_marker_operation_version_unsupported";
        }
    }
}
