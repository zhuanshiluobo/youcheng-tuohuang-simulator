using System;
using System.Collections.Generic;

namespace YC.Application.Sessions
{
    /// <summary>仅承载规则层明确登记的窗口事实；协议层不推断窗口或响应挂载。</summary>
    [Serializable]
    public sealed class OperationWindowDescriptorDto
    {
        public int ProtocolVersion = OperationCommandProtocol.Version;
        public string DigestVersion = OperationCommandCanonicalizer.DigestVersion;
        public string GameId = string.Empty;
        public long SimulationEpoch;
        public int MembershipVersion;
        public string OperationWindowId = string.Empty;
        public int WindowGeneration;
        public long OpenedAtGlobalSequence;
        public long BaseWindowSequence;
        public List<int> AuthorizedPlayerIds = new List<int>();
        public List<string> RequiredReplicaIds = new List<string>();
        public string PrefixDigest = string.Empty;
        public string WindowContractDigest = string.Empty;
    }

    [Serializable]
    public sealed class OperationWindowCommandProgressDto
    {
        public string OperationWindowId = string.Empty;
        public int WindowGeneration;
        public long LastWindowCommandSequence;
        public long CommandCount;
        public string PrefixDigest = string.Empty;
    }
}
