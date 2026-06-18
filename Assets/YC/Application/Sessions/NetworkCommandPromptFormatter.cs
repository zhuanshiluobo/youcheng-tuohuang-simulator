using YC.Domain.Commands;

namespace YC.Application.Sessions
{
    public static class NetworkCommandPromptFormatter
    {
        public static string BuildRejectedCommandPrompt(RejectedGameCommandDto rejected)
        {
            if (rejected == null || string.IsNullOrEmpty(rejected.Reason))
            {
                return "Host rejected the command.";
            }

            return "Host rejected the command: " + rejected.Reason;
        }
    }
}
