using System;

namespace YC.Presentation.Workflows
{
    public enum InteractionResultKind
    {
        Passthrough,
        Consumed,
        RejectedWithPrompt
    }

    public sealed class InteractionResult
    {
        private static readonly InteractionResult PassthroughResult =
            new InteractionResult(InteractionResultKind.Passthrough, string.Empty);

        private static readonly InteractionResult ConsumedResult =
            new InteractionResult(InteractionResultKind.Consumed, string.Empty);

        private InteractionResult(InteractionResultKind kind, string promptText)
        {
            Kind = kind;
            PromptText = promptText;
        }

        public static InteractionResult Passthrough
        {
            get { return PassthroughResult; }
        }

        public static InteractionResult Consumed
        {
            get { return ConsumedResult; }
        }

        public InteractionResultKind Kind { get; private set; }

        public string PromptText { get; private set; }

        public static InteractionResult RejectedWithPrompt(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException(
                    "A rejected interaction result requires a non-empty prompt.",
                    nameof(reason));
            }

            return new InteractionResult(InteractionResultKind.RejectedWithPrompt, reason);
        }
    }
}
