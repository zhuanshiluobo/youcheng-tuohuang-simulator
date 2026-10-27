using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;

namespace YC.Presentation
{
    /// <summary>公开定义的卡图与只读顺序；不读取规则运行状态，不创建卡牌。</summary>
    internal sealed class EntrepreneurCardPresentation
    {
        internal string Id, DefinitionId, FaceId, Label;
        internal Sprite Artwork => ExternalContentRuntime.GetDeclaredCardArtwork(DefinitionId, FaceId);
        private static ExternalContentPack cachedPack;
        private static Dictionary<string, EntrepreneurDefinition> definitions;
        internal static EntrepreneurDefinition Definition(string id)
        {
            var pack = ExternalContentRuntime.Pack;
            if (cachedPack != pack) { cachedPack = pack; definitions = EntrepreneurContentCatalog.Read(pack.Definitions).ToDictionary(d => d.DefinitionId); }
            return id != null && definitions.TryGetValue(id, out var definition) ? definition : null;
        }
        internal static EntrepreneurCardPresentation Ability(string id)
        {
            var definition = Definition(id);
            return definition == null ? null : new EntrepreneurCardPresentation { Id = id, DefinitionId = id, FaceId = "", Label = definition.DisplayName };
        }
        internal static EntrepreneurCardPresentation Candidate(RoundCandidateDefinition candidate) =>
            new EntrepreneurCardPresentation { Id = candidate.CandidateId, DefinitionId = candidate.EntityDefinitionId,
                FaceId = candidate.FaceId, Label = candidate.CandidateId };
        internal static IReadOnlyList<EntrepreneurCardPresentation> Deck(string id, string group = null)
        {
            var definition = Definition(id); var result = new List<EntrepreneurCardPresentation>();
            if (definition == null) return result;
            if (group == null) result.Add(Ability(id));
            foreach (var round in definition.RoundGroups.Where(g => group == null || g.GroupId == group).OrderBy(g => g.RoundNumber))
                result.AddRange(round.Candidates.OrderBy(c => c.DisplayOrder).Select(Candidate));
            return result;
        }
    }

    [Serializable]
    public sealed class EntrepreneurUiCopy
    {
        public string PreviewTitle, ChooseTitle, FaceTitle, DeckTitle, ContinueLabel, OwnEnterpriseLabel;
        public string BrowseHint, SelectionHint, MissingPreviewFormat, MissingEnterpriseCandidateFormat;
    }
}
