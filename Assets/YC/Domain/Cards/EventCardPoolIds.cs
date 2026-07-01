using YC.Domain.Rules;

namespace YC.Domain.CardFlows
{
    public static class EventCardPoolIds
    {
        public const string EventGreen = "event_green";
        public const string EventYellow = "event_yellow";
        public const string EventRed = "event_red";

        public static string FromColor(EventColor color)
        {
            switch (color)
            {
                case EventColor.Green:
                    return EventGreen;
                case EventColor.Yellow:
                    return EventYellow;
                case EventColor.Red:
                    return EventRed;
                default:
                    return EventGreen;
            }
        }
    }
}
