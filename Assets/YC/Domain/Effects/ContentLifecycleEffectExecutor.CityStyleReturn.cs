using System;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static partial class ContentLifecycleEffectExecutor
    {
        public const string ReturnMarkerDefinitionVersion = "4.0.0";
        public static string ValidateSavedReturnMarkerVersion(string type, string version, bool runtimeNode = false) =>
            type != ReturnMarkerTypeId || version == ReturnMarkerDefinitionVersion || !runtimeNode && string.IsNullOrEmpty(version) ? "" : "city_style_marker_return_version_unsupported";
        public const string RetiredReturnSelection="city_style_marker_selection_contract_retired";
        private static string ValidateExplicitReturnArguments(NormalizedValue args)
        {
            if(Number(args,"apiVersion")==1)return RetiredReturnSelection;
            if(args?.Kind!=NormalizedValueKind.Object||!args.IsValid()||Number(args,"apiVersion")!=2||args.Properties.Any(p=>!new[]{"apiVersion","player","selector","kind","effectTypeId"}.Contains(p.Name))||
                !int.TryParse(Text(args,"player"),NumberStyles.None,CultureInfo.InvariantCulture,out int player)||player<1)return "lifecycle_arguments_invalid";
            return ValidateExplicitMovementTarget(Value(args,"selector"));
        }
        private static EffectSpec ReturnMarkerStep(EffectNodeRuntimeState node)=>EffectSpec.Create(TypeId,Object("apiVersion",NormalizedValue.CreateInteger(4),
            "player",NormalizedValue.CreateString(node.PlayerId.ToString(CultureInfo.InvariantCulture)),"operation",NormalizedValue.CreateString("return_city_style_marker"),
            "selector",Value(node.NormalizedArguments,"selector").Clone()),node.PlayerId);
        private static NormalizedValue ReturnMarkerProjection(EffectNodeRuntimeState child)
        {
            var receipt = child.CommonItemMovementV4.Receipt;
            return Object("changed", NormalizedValue.CreateBoolean(receipt.Result.Changed), "markerItemId", NormalizedValue.CreateString(receipt.Result.MovedItemIds.Single()));
        }
        private static EffectStepResult ReturnMarker(EffectExecutionContext context)
        {
            var node = context.Node;
            if (node.FlowStage == "")
            {
                var child = ReturnMarkerStep(node); child.StableKey = node.EffectId + ":city-style-return";
                return EffectStepResult.Continue("returning_common").AddChild(child);
            }
            if (node.FlowStage != "returning_common" || !ValidateReturnMarkerWrapper(context.State, node)) throw new InvalidOperationException("city_style_return_stage_invalid");
            var completed = context.ChildNodes.Single(n => n.EffectTypeId == TypeId);
            if (completed.Status == EffectNodeStatus.Failed) return EffectStepResult.Failed(completed.FailureReason).WithFlowStage("finished");
            if (completed.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待样式标记返还及其响应。");
            if (!ItemMovementV4.TryValidateState(context.State, completed, out var reason)) throw new InvalidOperationException(reason);
            return EffectStepResult.Completed(ReturnMarkerProjection(completed)).WithFlowStage("finished");
        }
        private static bool ValidateReturnMarkerWrapper(GameState state, EffectNodeRuntimeState node)
        {
            if (ValidateSavedReturnMarkerVersion(node.EffectTypeId, node.DefinitionVersion, true).Length > 0 || node.PositionChanges.Count != 0) return false;
            var automatic = node.ChildEffectIds.Where(id => !state.EffectRuntime.DispatchReceipts.Any(d => d.Succeeded && d.AttachedEffectIds.Contains(id))).ToList();
            if (node.FlowStage == "") return automatic.Count == 0 && node.Status != EffectNodeStatus.Completed;
            if(state.EffectRuntime.InteractionRequests.Any(r=>r.OwnerEffectId==node.EffectId))return false;
            if (!new[] { "returning_common", "finished" }.Contains(node.FlowStage) || automatic.Count != 1) return false;
            var child = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == automatic[0]);
            if (child == null || child.ParentEffectId != node.EffectId || child.PlayerId != node.PlayerId || child.EffectTypeId != TypeId ||
                child.EffectId != StableIdFactory.Create("effect", node.EffectId, "child", "child_effect", node.ChildEffectIds.IndexOf(child.EffectId).ToString(CultureInfo.InvariantCulture), TypeId, node.EffectId + ":city-style-return") ||
                ItemTransferService.MovementV4Digest(child.NormalizedArguments) != ItemTransferService.MovementV4Digest(ReturnMarkerStep(node).NormalizedArguments)) return false;
            if (node.FlowStage != "finished") return true;
            if (node.PendingOutcome == EffectPendingOutcome.Failed || node.Status == EffectNodeStatus.Failed) return child.Status == EffectNodeStatus.Failed && node.FailureReason == child.FailureReason;
            return child.Status == EffectNodeStatus.Completed && child.CommonItemMovementV4?.Receipt?.Result.MovedCount == 1 &&
                ItemTransferService.MovementV4Digest(node.NormalizedResult) == ItemTransferService.MovementV4Digest(ReturnMarkerProjection(child));
        }
    }
}
