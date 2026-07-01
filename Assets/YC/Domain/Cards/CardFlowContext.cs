using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public sealed class CardFlowContext
    {
        public string SessionId = string.Empty;
        public string ScenarioId = string.Empty;
        public string ChoiceType = string.Empty;
        public string PoolId = string.Empty;
        public string CardId = string.Empty;
        public int PlayerId;
        public string TargetId = string.Empty;
        public string SourceCommandId = string.Empty;
        public List<StringKeyValuePair> ContextData = new List<StringKeyValuePair>();
    }
}
