using System;
using System.Collections.Generic;

namespace YC.Domain.State
{
    public enum OperationWindowLifecycle { Open, Closed }

    [Serializable]
    public sealed class OperationWindowState
    {
        public string OperationWindowId = string.Empty;
        public int WindowGeneration = 1;
        public string WindowKind = string.Empty;
        public string SourceMainNodeId = string.Empty;
        public string SourceEffectId = string.Empty;
        public string SourceSchedulingGroupId = string.Empty;
        public string ParentOperationWindowId = string.Empty;
        public List<int> AuthorizedPlayerIds = new List<int>();
        public List<int> AvailablePlayerIds = new List<int>();
        public OperationWindowLifecycle Lifecycle;
        public long OpenedOrdinal;
        public int OpenedAtStateRevision;
        public int ClosedAtStateRevision;
    }

    /// <summary>一次真实规则命令的因果快照；不含网络身份和本机查看者。</summary>
    [Serializable]
    public sealed class OperationCommandContext
    {
        public string OperationWindowId = string.Empty;
        public int WindowGeneration;
        public string WindowKind = string.Empty;
        public string SourceMainNodeId = string.Empty;
        public string SourceEffectId = string.Empty;
        public int ActingPlayerId = -1;
        public string SourceCommandId = string.Empty;
        public string OriginMainOperationWindowId = string.Empty;
        public int MainWindowPlayerIdAtCreation = -1;
        // 保存固定窗口合同的脱离副本，使关闭索引退休后仍可恢复校验历史因果。
        public OperationWindowState WindowFacts;
        public OperationCommandContext Clone()
        {
            var copy = (OperationCommandContext)MemberwiseClone();
            copy.WindowFacts = GameStateCloneService.DeepClone(WindowFacts);
            return copy;
        }
    }
}
