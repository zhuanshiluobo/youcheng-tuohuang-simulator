using System;
using System.Collections.Generic;

namespace YC.Presentation.Workflows
{
    public sealed class InteractionRouter
    {
        private readonly Action<string> showPrompt;
        private readonly List<Registration> registrations = new List<Registration>();
        private readonly HashSet<string> registeredIds =
            new HashSet<string>(StringComparer.Ordinal);

        public InteractionRouter(Action<string> showPrompt)
        {
            this.showPrompt = showPrompt ??
                              throw new ArgumentNullException(nameof(showPrompt));
        }

        public void Register(IInteraction interaction)
        {
            if (interaction == null)
            {
                throw new ArgumentNullException(nameof(interaction));
            }

            for (var i = 0; i < registrations.Count; i++)
            {
                if (ReferenceEquals(registrations[i].Interaction, interaction))
                {
                    throw new InvalidOperationException(
                        "The same interaction instance cannot be registered more than once.");
                }
            }

            var id = interaction.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    "An interaction must have a non-empty Id.",
                    nameof(interaction));
            }

            if (registeredIds.Contains(id))
            {
                throw new InvalidOperationException(
                    "An interaction with Id '" + id + "' is already registered.");
            }

            var registration = new Registration(
                interaction,
                id,
                interaction.Priority);
            var insertIndex = registrations.Count;
            for (var i = 0; i < registrations.Count; i++)
            {
                if (registration.Priority <= registrations[i].Priority)
                {
                    continue;
                }

                insertIndex = i;
                break;
            }

            registrations.Insert(insertIndex, registration);
            registeredIds.Add(id);
        }

        public InteractionResult OnLocationClicked(string locationId)
        {
            return Route(interaction => interaction.OnLocationClicked(locationId));
        }

        public InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            return Route(interaction => interaction.OnInfluenceSlotClicked(slotId));
        }

        public InteractionResult OnMobileCityClicked()
        {
            return Route(interaction => interaction.OnMobileCityClicked());
        }

        public InteractionResult OnEscape()
        {
            return Route(interaction => interaction.OnEscape());
        }

        public InteractionPresentation BuildActivePresentation()
        {
            for (var i = 0; i < registrations.Count; i++)
            {
                var registration = registrations[i];
                if (!registration.Interaction.IsActive)
                {
                    continue;
                }

                var presentation = registration.Interaction.BuildPresentation();
                if (presentation == null)
                {
                    throw new InvalidOperationException(
                        "Interaction '" + registration.Id +
                        "' returned a null presentation.");
                }

                if (presentation.IsEmpty)
                {
                    continue;
                }

                return presentation;
            }

            return InteractionPresentation.Empty;
        }

        public void CancelAll()
        {
            List<Exception> failures = null;
            try
            {
                for (var i = 0; i < registrations.Count; i++)
                {
                    try
                    {
                        registrations[i].Interaction.Cancel();
                    }
                    catch (Exception exception)
                    {
                        if (failures == null)
                        {
                            failures = new List<Exception>();
                        }

                        failures.Add(exception);
                    }
                }
            }
            finally
            {
                registrations.Clear();
                registeredIds.Clear();
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more interactions failed while being cancelled.",
                    failures);
            }
        }

        public void NotifyCommandSettled(string commandId)
        {
            List<Exception> failures = null;
            for (var i = 0; i < registrations.Count; i++)
            {
                try
                {
                    registrations[i].Interaction.NotifyCommandSettled(commandId);
                }
                catch (Exception exception)
                {
                    if (failures == null)
                    {
                        failures = new List<Exception>();
                    }

                    failures.Add(exception);
                }
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more interactions failed while handling command settlement.",
                    failures);
            }
        }

        private InteractionResult Route(Func<IInteraction, InteractionResult> dispatch)
        {
            for (var i = 0; i < registrations.Count; i++)
            {
                var registration = registrations[i];
                if (!registration.Interaction.IsActive)
                {
                    continue;
                }

                var result = dispatch(registration.Interaction);
                if (result == null)
                {
                    throw new InvalidOperationException(
                        "Interaction '" + registration.Id +
                        "' returned a null result.");
                }

                switch (result.Kind)
                {
                    case InteractionResultKind.Passthrough:
                        continue;
                    case InteractionResultKind.Consumed:
                        return result;
                    case InteractionResultKind.RejectedWithPrompt:
                        showPrompt(result.PromptText);
                        return result;
                    default:
                        throw new InvalidOperationException(
                            "Interaction '" + registration.Id +
                            "' returned an unsupported result kind.");
                }
            }

            return InteractionResult.Passthrough;
        }

        private sealed class Registration
        {
            public Registration(
                IInteraction interaction,
                string id,
                InteractionPriority priority)
            {
                Interaction = interaction;
                Id = id;
                Priority = priority;
            }

            public IInteraction Interaction { get; private set; }

            public string Id { get; private set; }

            public InteractionPriority Priority { get; private set; }
        }
    }
}
