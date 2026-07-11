using System.Collections.Generic;
using YC.Domain.Maps;

namespace YC.Presentation
{
    public sealed class ExplorePathChoice
    {
        public readonly MapPath Path;
        public readonly string Label;

        public ExplorePathChoice(MapPath path, string label)
        {
            Path = path;
            Label = label ?? string.Empty;
        }
    }

    public sealed class ExplorePaymentChoice
    {
        public readonly string RouteId;
        public readonly List<int> RecipientPlayerIds;

        public ExplorePaymentChoice(string routeId, List<int> recipientPlayerIds)
        {
            RouteId = routeId ?? string.Empty;
            RecipientPlayerIds = recipientPlayerIds ?? new List<int>();
        }
    }
}
