using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    /// <summary>只排序可见显示副本；不改变实体、牌堆或请求的权威顺序。</summary>
    public sealed class CharacterCardDisplayOrder
    {
        private readonly Dictionary<string, int> ranks = new Dictionary<string, int>(StringComparer.Ordinal);

        public CharacterCardDisplayOrder(GameContentDefinitions definitions)
        {
            foreach (var id in CharacterCardDatabase.CanonicalDisplayOrder) Add(id);
            if (definitions?.Characters != null)
                foreach (var card in definitions.Characters) Add(card?.TemplateId);
        }

        private void Add(string id)
        {
            if (!string.IsNullOrEmpty(id) && !ranks.ContainsKey(id)) ranks.Add(id, ranks.Count);
        }

        public List<T> Sort<T>(IEnumerable<T> source, Func<T, string> definitionId, Func<T, string> instanceId)
        {
            return source.Select(value => new { Value = value, Definition = definitionId(value), Instance = instanceId(value) })
                .OrderBy(value => value.Definition != null && ranks.TryGetValue(value.Definition, out var rank) ? rank : int.MaxValue)
                .ThenBy(value => value.Definition, StringComparer.Ordinal)
                .ThenBy(value => value.Instance, StringComparer.Ordinal)
                .Select(value => value.Value).ToList();
        }

        public static string ResolveDefinition(GameState state, string id)
        {
            var definition = GameContentDefinitions.ResolveDefinitionId(state, id);
            // 兼容旧玩家牌 ID；真实实例已在上一行解析，不拆解其不透明 ID。
            if (state?.ContentDefinitions?.Characters.Any(card => card.TemplateId == definition) == true) return definition;
            return CharacterCardDatabase.IsInitialized ? CharacterCardDatabase.Get(definition)?.TemplateId ?? definition : definition;
        }

        public static InteractionRequestProjection ForDisplay(GameState state, InteractionRequestProjection request,
            Func<string, string> definitionId)
        {
            if (request == null || !request.VisibleToViewer || request.CandidateIds == null || request.CandidateIds.Count < 2)
                return request;
            var order = new CharacterCardDisplayOrder(state?.ContentDefinitions);
            var definitions = request.CandidateIds.ToDictionary(id => id, id => definitionId(id) ?? string.Empty, StringComparer.Ordinal);
            // 资源、玩家、设施以及混合候选保持原语义顺序。
            if (definitions.Values.Any(id => !order.ranks.ContainsKey(id))) return request;
            var display = GameStateCloneService.DeepClone(request);
            display.CandidateIds = order.Sort(request.CandidateIds, id => definitions[id], id => id);
            var definitionValues = display.PromptParameters?.Properties.Find(value => value.Name == "candidateDefinitionIds")?.Value;
            if (definitionValues?.Kind == NormalizedValueKind.Array && definitionValues.Items.Count == request.CandidateIds.Count)
            {
                var originalValues = definitionValues.Items.ToArray();
                definitionValues.Items = display.CandidateIds.Select(id => originalValues[request.CandidateIds.IndexOf(id)]).ToList();
            }
            return display;
        }
    }
}
