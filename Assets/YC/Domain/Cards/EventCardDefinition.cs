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
        public string Name = string.Empty;
        public string Description = string.Empty;
        public EventColor Color;
        public ResourceType ResourceType;
        public int ResourceAmount = 1;
        public ResourceType RepresentativeResourceType;
        public int RepresentativeResourceAmount = 1;
        public List<string> ChoiceDescriptions = new List<string>();
        public List<ResourceSet> ChoiceRewards = new List<ResourceSet>();
        public List<string> ChoicePendingEffects = new List<string>();
    }
}
