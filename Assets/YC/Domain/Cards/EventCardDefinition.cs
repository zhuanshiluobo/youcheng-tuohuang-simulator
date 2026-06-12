using System;
using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    [Serializable]
    public sealed class EventCardDefinition
    {
        public string CardId = string.Empty;
        public EventColor Color;
        public ResourceType ResourceType;
        public int ResourceAmount = 1;
        public List<string> ChoiceDescriptions = new List<string>();
        public List<ResourceSet> ChoiceRewards = new List<ResourceSet>();
    }
}
