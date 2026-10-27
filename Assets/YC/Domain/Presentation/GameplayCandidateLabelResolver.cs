using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.State;
using YC.Domain.Interactions;

namespace YC.Domain.Presentation
{
    // 只接收同一可见请求的获准ID，不读Host、资源或业务定义，不展开/修改持久Options。
    public static class GameplayCandidateLabelResolver
    {
        public static GameplayTextRef Resolve(InteractionRequestProjection request, string candidateId)
        {
            if (request?.VisibleToViewer != true || request.Presentation == null || request.CandidateIds == null || candidateId == null || !request.CandidateIds.Contains(candidateId, StringComparer.Ordinal)) return null;
            var presentation = request.Presentation;
            var explicitLabel = presentation.Options?.Find(o => o.CandidateId == candidateId)?.Label;
            if (explicitLabel != null) return explicitLabel.Clone();
            var compact = presentation.CandidateIntegerLabel;
            if (compact == null) return null;
            GameplayPresentationNormalizer.Validate(compact);
            if (!long.TryParse(candidateId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer) ||
                integer < NormalizedValue.MinInteger || integer > NormalizedValue.MaxInteger)
                throw new ArgumentException("整数候选标签需要范围内的整数候选ID。");
            var label = new GameplayTextRef(compact.Key);
            foreach (var binding in compact.Arguments)
            {
                long value;
                try { value = checked(integer * binding.Multiplier); }
                catch (OverflowException) { throw new ArgumentException("整数候选标签乘积越界。"); }
                if (value < NormalizedValue.MinInteger || value > NormalizedValue.MaxInteger) throw new ArgumentException("整数候选标签乘积越界。");
                label.Arguments.Add(new GameplayTextArgument { Name = binding.Name, Kind = "integer", Value = NormalizedValue.CreateInteger(value) });
            }
            return label;
        }
    }
}
