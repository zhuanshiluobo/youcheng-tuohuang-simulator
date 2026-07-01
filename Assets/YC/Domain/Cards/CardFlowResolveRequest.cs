using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public sealed class CardFlowResolveRequest
    {
        public int PlayerId;
        public string SessionId = string.Empty;
        public int OptionIndex = -1;
        public List<StringKeyValuePair> Arguments = new List<StringKeyValuePair>();
    }
}
