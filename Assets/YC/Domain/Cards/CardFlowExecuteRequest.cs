using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public sealed class CardFlowExecuteRequest
    {
        public int PlayerId;
        public string TargetId = string.Empty;
        public string SourceCommandId = string.Empty;
        public int OptionIndex = -1;
        public List<StringKeyValuePair> Arguments = new List<StringKeyValuePair>();
    }
}
