using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Presentation;

namespace YC.Application.Presentation
{
    /// <summary>仅组合动作展示文档；不读取规则、候选或推进动作。</summary>
    public static class AbilityActionTextDocuments
    {
        public static GameplayTextDocument Name(AbilityActionOption option, Func<GameplayTextRef, GameplayTextDocument> resolve, string suffix = "") =>
            Join(new[] { option.DisplayNameRef }, resolve, suffix);

        public static GameplayTextDocument Details(AbilityActionOption option, Func<GameplayTextRef, GameplayTextDocument> resolve, string suffix = "") =>
            Join(new[] { option.DescriptionRef, option.EntryCostDescriptionRef, option.EntryAfterEffectsDescriptionRef }, resolve, suffix);

        public static string PlainDetails(AbilityActionOption option) => string.Join("\n",
            new[] { option.Description, option.EntryCostDescription, option.EntryAfterEffectsDescription }.Where(s => !string.IsNullOrEmpty(s)));

        private static GameplayTextDocument Join(IEnumerable<GameplayTextRef> references, Func<GameplayTextRef, GameplayTextDocument> resolve, string suffix)
        {
            if (resolve == null) return null;
            var documents = references.Where(r => r != null).Select(resolve).Where(d => d != null).ToList();
            if (documents.Count == 0) return null;
            var runs = new List<GameplayTextRun>();
            foreach (var document in documents)
            {
                if (runs.Count > 0) runs.Add(new LineBreak());
                runs.AddRange(document.Runs);
            }
            if (!string.IsNullOrEmpty(suffix)) runs.Add(new TextRun(suffix));
            string identity = string.Concat(documents.Select(d => d.Identity.Length + ":" + d.Identity)) + suffix.Length + ":" + suffix;
            return new GameplayTextDocument(runs, identity);
        }
    }
}
