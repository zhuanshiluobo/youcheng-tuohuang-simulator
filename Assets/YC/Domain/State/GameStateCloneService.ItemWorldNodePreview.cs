using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;

namespace YC.Domain.State
{
    public sealed partial class EffectRuntimeState
    {
        internal EffectRuntimeState CreateItemWorldNodePreview(string nodeId)
        {
            var node=EffectNodes.Single(n=>n.EffectId==nodeId);
            var copy=(EffectRuntimeState)MemberwiseClone();
            copy.EffectNodes=new List<EffectNodeRuntimeState>(EffectNodes);
            copy.EffectNodes[EffectNodes.IndexOf(node)]=GameStateCloneService.DeepClone(node);
            return copy;
        }
    }
    public static partial class GameStateCloneService
    {
        [ThreadStatic] private static bool requireNativeItemWorldReferences,fullItemWorldNodePreviews,verifyItemWorldNodePreviews;
        [ThreadStatic] private static string rejectedItemWorldPreviewType;
        public static IDisposable UseFullItemWorldNodePreviewsForDiagnostics() => new ItemWorldNodePreviewScope(true,false);
        public static IDisposable VerifyItemWorldNodePreviewsForDiagnostics() => new ItemWorldNodePreviewScope(false,true);
        private sealed class ItemWorldNodePreviewScope : IDisposable
        {
            private readonly bool previousFull=fullItemWorldNodePreviews,previousVerify=verifyItemWorldNodePreviews;
            private bool closed;
            internal ItemWorldNodePreviewScope(bool full,bool verify) { fullItemWorldNodePreviews=previousFull||full;verifyItemWorldNodePreviews=previousVerify||verify; }
            public void Dispose() { if(closed)return;closed=true;fullItemWorldNodePreviews=previousFull;verifyItemWorldNodePreviews=previousVerify; }
        }
        private static bool IsNativeItemWorldReference(Type type)
        {
            bool accepted=type.IsValueType || type==typeof(string) || type.IsArray && type.GetArrayRank()==1 ||
                type.IsGenericType && type.GetGenericTypeDefinition()==typeof(List<>) ||
                !typeof(IList).IsAssignableFrom(type) && type.Assembly==typeof(GameState).Assembly && type.IsSerializable;
            if(!accepted)rejectedItemWorldPreviewType=type.FullName;
            return accepted;
        }
        private static bool IsItemWorldPreviewField(string name) => name=="ItemZones" || name=="CharacterUses" || name=="CharacterCoverSlots" ||
            name=="FacilityCharacterBindings" || name=="Players" || name=="Map" || name=="Decks";

        // 只证明借用部分是不会执行扩展枚举/回调的原生持久数据；七个写集完整脱离。
        // 临时严格标记仅约束本次引用形状遍历，finally先复原，再运行任何克隆/规则/计时回调。
        // 不影响公开Clone/Copy、正常引用证明或回调合同，不跨提交缓存资格。
        private static bool HasItemWorldNodePreviewShape(GameState source)
        {
            var seen=reusableUniquenessReferences ?? new HashSet<object>(ReferenceEqualityComparer.Instance);
            reusableUniquenessReferences=null;bool previous=requireNativeItemWorldReferences;bool native=true;
            rejectedItemWorldPreviewType=null;
            try
            {
                requireNativeItemWorldReferences=true;
                foreach(var field in StateCopyMetadata.For(typeof(GameState)).ReferenceFields)
                    if(!IsItemWorldPreviewField(field.Name) && !VisitPublicationShape(field.GetValue(source),seen)) { native=false;break; }
            }
            finally
            {
                requireNativeItemWorldReferences=previous;
                int count=seen.Count;seen.Clear();
                if(count<=MaximumRetainedUniquenessReferences && reusableUniquenessReferences==null)reusableUniquenessReferences=seen;
            }
            return native && HasUniqueMutableReferences(source);
        }
        internal static GameState CreateItemWorldNodePreview(GameState source,string nodeId,out GameState reference) =>
            CreateItemWorldNodePreviewForPublication(source,nodeId,out reference,out _);
        internal static GameState CreateItemWorldNodePreviewForPublication(GameState source,string nodeId,out GameState reference,out bool narrow)
        {
            rejectedItemWorldPreviewType=null;
            reference=verifyItemWorldNodePreviews?DeepClone(source):null;
            narrow=!fullItemWorldNodePreviews && ItemTransferService.NativeItemWorldNodePreviewsEnabled && HasItemWorldNodePreviewShape(source);
            if(!narrow)
            {
                using var fallback=MeasureDiagnostic("transfer.node_preview.fallback",rejectedItemWorldPreviewType??"alias_or_mode");
                return DeepClone(source);
            }
            using var sample=MeasureDiagnostic("transfer.node_preview");
            var preview=source.CreateItemTransferPreview();
            preview.EffectRuntime=source.EffectRuntime.CreateItemWorldNodePreview(nodeId);
            return preview;
        }
        internal static void VerifyItemWorldNodePreviewSource(GameState source,GameState reference)
        {
            if(reference!=null && !AreEquivalent(source,reference))throw new InvalidOperationException("节点物品预演改写了原始状态或借用历史。");
        }
        internal static void VerifyItemWorldNodePreviewResult(GameState actual,GameState reference,bool actualValid,bool expectedValid)
        {
            if(actualValid!=expectedValid || !AreEquivalent(actual,reference))throw new InvalidOperationException("节点物品预演与原完整克隆结果不一致。");
        }
    }
}
