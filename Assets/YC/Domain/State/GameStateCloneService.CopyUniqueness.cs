using System;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        [ThreadStatic] private static UniqueCopyTargetScope uniqueCopyTarget;
        [ThreadStatic] private static bool copyingUniqueTarget;

        // 与纯调度发布使用同一份Run入口证明。处理器只取得独占工作图；
        // CopyTo逐字段保留目标引用、克隆新增分支，不会给无别名的目标引入共享引用。
        // 因而整次复制中每个可变目标最多到达一次，不会出现重复的(target,source)对。
        // 源新增分支仍完整深克隆并检查环；无证明的根继续原引用对路径。
        internal static IDisposable BeginUniqueCopyTarget(GameState state, bool provenUnique)
            => new UniqueCopyTargetScope(provenUnique ? state : null);

        private sealed class UniqueCopyTargetScope : IDisposable
        {
            internal GameState State;
            private readonly UniqueCopyTargetScope previous;
            private bool closed;
            internal UniqueCopyTargetScope(GameState state)
            { State = state; previous = uniqueCopyTarget; uniqueCopyTarget = this; }
            public void Dispose()
            {
                if (closed) return;
                closed = true; State = null;
                if (ReferenceEquals(uniqueCopyTarget, this))
                {
                    var restored = previous;
                    while (restored != null && restored.closed) restored = restored.previous;
                    uniqueCopyTarget = restored;
                }
            }
        }

        private readonly struct CopyTargetOperation : IDisposable
        {
            private readonly bool previous;
            internal readonly bool Enabled;
            internal CopyTargetOperation(GameState target)
            {
                previous = copyingUniqueTarget;
                Enabled = uniqueCopyTarget != null && ReferenceEquals(uniqueCopyTarget.State, target);
                copyingUniqueTarget = Enabled;
            }
            public void Dispose() { copyingUniqueTarget = previous; }
        }
    }
}
