using System;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        [ThreadStatic] private static bool fullBuiltinFlowPublication;
        internal static bool BuiltinFlowPublicationProofsEnabled => !fullBuiltinFlowPublication;
        public static IDisposable UseFullBuiltinFlowPublicationForDiagnostics() => new FullBuiltinFlowPublicationScope();
        private sealed class FullBuiltinFlowPublicationScope : IDisposable
        {
            private readonly bool previous = fullBuiltinFlowPublication;
            private bool closed;
            internal FullBuiltinFlowPublicationScope() { fullBuiltinFlowPublication = true; }
            public void Dispose() { if (closed) return; closed = true; fullBuiltinFlowPublication = previous; }
        }
        // 仅原生Condition/Allocation工厂的固定执行函数使用独立引用和日志前缀合同。
        // Condition只读节点并返回独立结果/子描述；Allocation只写当前节点的独立分配记录。
        // 登记自定义执行器、普通NoRetain回调以及未知Validator不会获得这些合同。
    }
}
