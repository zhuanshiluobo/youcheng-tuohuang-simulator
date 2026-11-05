using System;
using YC.Domain.State;

namespace YC.Domain.Items
{
    public sealed partial class ItemTransferService
    {
        [ThreadStatic] private static bool fullStructuralPreviews, verifyStructuralPreviews, fullStructuralPublication;
        public static IDisposable UseFullStructuralPreviewsForDiagnostics() => new StructuralPreviewScope(true, false);
        public static IDisposable VerifyStructuralPreviewsForDiagnostics() => new StructuralPreviewScope(false, true);
        private sealed class StructuralPreviewScope : IDisposable
        {
            private readonly bool previousFull = fullStructuralPreviews, previousVerify = verifyStructuralPreviews;
            private bool closed;
            internal StructuralPreviewScope(bool full, bool verify) { fullStructuralPreviews = full; verifyStructuralPreviews = verify; }
            public void Dispose()
            {
                if (closed) return;
                closed = true; fullStructuralPreviews = previousFull; verifyStructuralPreviews = previousVerify;
            }
        }
        public static IDisposable UseFullStructuralPublicationForDiagnostics() => new FullStructuralPublicationScope();
        private sealed class FullStructuralPublicationScope : IDisposable
        {
            private readonly bool previous = fullStructuralPublication;
            private bool closed;
            internal FullStructuralPublicationScope() { fullStructuralPublication = true; }
            public void Dispose() { if (closed) return; closed = true; fullStructuralPublication = previous; }
        }
        internal static bool NativeItemWorldNodePreviewsEnabled => !fullStructuralPreviews && !fullStructuralPublication;
        internal static void PublishStructuralPreview(GameState target, GameState preview, bool narrow)
        {
            if (narrow && !fullStructuralPublication) GameStateCloneService.CopyItemWorldPreviewTo(target, preview, verifyStructuralPreviews);
            else GameStateCloneService.CopyTo(target, preview);
        }

        // 只用于封闭Apply、ApplyMarkerPlan及实体工厂写集；结构查询不会执行规则回调。
        // 工厂成功时只发布已脱离的物品世界写集；有别名源和诊断原路径仍完整CopyTo。
        // 写集沿既有CreateItemTransferPreview完整脱离；有别名或不支持的图仍保留原完整克隆。
        internal static GameState StructuralPreview(GameState source, string caller, out GameState reference) =>
            StructuralPreview(source, caller, out reference, out _);
        internal static GameState StructuralPreview(GameState source, string caller, out GameState reference, out bool narrow)
        {
            narrow = !fullStructuralPreviews && GameStateCloneService.HasUniqueMutableReferences(source);
            reference = verifyStructuralPreviews ? GameStateCloneService.DeepClone(source) : null;
            if (!narrow) return GameStateCloneService.DeepClone(source);
            using var sample = GameStateCloneService.MeasureDiagnostic("transfer.structural_preview");
            using var detail = GameStateCloneService.MeasureDiagnostic("transfer.structural_preview", caller);
            return source.CreateItemTransferPreview();
        }
        internal static bool ValidateStructuralPreview(ItemZoneQuery query,GameState preview,GameState reference,out string reason)
        {
            bool valid=query.TryValidate(preview,out reason);
            if(reference!=null)
            {
                VerifyStructuralResult(preview,reference);
                bool expected=query.TryValidate(reference,out var expectedReason);
                if(valid!=expected || reason!=expectedReason)throw new InvalidOperationException("实体工厂结构预检与原完整克隆不一致。");
            }
            return valid;
        }
        internal static void VerifyStructuralSource(GameState source, GameState reference)
        {
            if (reference != null && !GameStateCloneService.AreEquivalent(source, reference))
                throw new InvalidOperationException("结构预检改写了原始状态或借用的历史字段。");
        }
        internal static void VerifyStructuralResult(GameState preview, GameState reference)
        {
            if (reference != null && !GameStateCloneService.AreEquivalent(preview, reference))
                throw new InvalidOperationException("结构预检与原完整克隆的全部字段不一致。");
        }
    }
}
