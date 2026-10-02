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
        private readonly MapInteractionConfirmationController mapConfirmation = new MapInteractionConfirmationController();
        public bool HasPendingMapConfirmation => mapConfirmation.HasPending;
        public void ClearMapConfirmation() => mapConfirmation.Clear();

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

        public InteractionResult OnLocationClicked(string locationId, int inputFrame = -1)
        {
            return Route(interaction => RouteMapTarget(interaction, WorkflowHighlightTargetKind.Location,
                locationId, inputFrame, () => interaction.OnLocationClicked(locationId)));
        }

        public InteractionResult OnInfluenceSlotClicked(string slotId, int inputFrame = -1)
        {
            return Route(interaction => RouteMapTarget(interaction, WorkflowHighlightTargetKind.InfluenceSlot,
                slotId, inputFrame, () => interaction.OnInfluenceSlotClicked(slotId)));
        }

        public InteractionResult OnRouteClicked(string routeId, int inputFrame = -1)
        {
            return Route(interaction => interaction is IRouteInteraction route
                ? RouteMapTarget(interaction, WorkflowHighlightTargetKind.Route, routeId, inputFrame,
                    () => route.OnRouteClicked(routeId))
                : interaction.Priority >= InteractionPriority.ActiveAction
                    ? InteractionResult.Consumed : InteractionResult.Passthrough);
        }

        public InteractionResult OnMobileCityClicked()
        {
            return Route(interaction => interaction.OnMobileCityClicked());
        }

        public InteractionResult OnEscape()
        {
            if (HasPendingMapConfirmation)
            {
                ClearMapConfirmation();
                return InteractionResult.Consumed;
            }
            return Route(interaction => interaction.OnEscape());
        }

        // 提示仲裁只读查询；不执行过期交互清理，避免清理回调重入提示入口。
        public string GetPendingPrompt()
        {
            foreach (var registration in registrations)
            {
                if (registration.Priority != InteractionPriority.PendingResolution || !registration.Interaction.IsActive) continue;
                var presentation = registration.Interaction.BuildPresentation();
                if (presentation != null && !string.IsNullOrEmpty(presentation.PromptText)) return presentation.PromptText;
            }
            return string.Empty;
        }

        public InteractionPresentation BuildActivePresentation()
        {
            // 待结算弹窗的清理不能被 Busy 提前返回截断。
            // 普通动作由各自工作流结束，不能在此延迟 Cancel，误清新动作高亮。
            for (var i = 0; i < registrations.Count; i++)
            {
                var registration = registrations[i];
                var active = registration.Interaction.IsActive;
                if (registration.Priority == InteractionPriority.PendingResolution &&
                    registration.WasActive && !active)
                    registration.Interaction.Cancel();
                registration.WasActive = active;
            }

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

                return WithMapConfirmation(registration.Interaction, presentation);
            }

            ClearMapConfirmation();
            return InteractionPresentation.Empty;
        }

        public void CancelAll()
        {
            ClearMapConfirmation();
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
            ClearMapConfirmation();
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

        private InteractionResult RouteMapTarget(IInteraction interaction, WorkflowHighlightTargetKind kind,
            string targetId, int frame, Func<InteractionResult> dispatch)
        {
            if (!(interaction is IMapConfirmationScope scope)) return dispatch();
            var presentation = interaction.BuildPresentation();
            if (string.IsNullOrEmpty(scope.MapConfirmationScope) || presentation == null) return InteractionResult.Consumed;
            var legal = false;
            foreach (var highlight in presentation.Highlights)
                if (highlight.TargetKind == kind && highlight.TargetId == targetId && highlight.IsInteractive)
                { legal = true; break; }
            if (!legal) { ClearMapConfirmation(); return InteractionResult.Consumed; }
            Action ignored;
            if (!mapConfirmation.Request(scope.MapConfirmationScope, kind + ":" + targetId,
                    kind == WorkflowHighlightTargetKind.Location ? targetId : string.Empty,
                    kind == WorkflowHighlightTargetKind.InfluenceSlot ? targetId : string.Empty,
                    null, out ignored, frame))
            {
                if (!string.IsNullOrEmpty(presentation.PromptText)) showPrompt(presentation.PromptText);
                return InteractionResult.Consumed;
            }
            return dispatch();
        }

        private InteractionPresentation WithMapConfirmation(IInteraction interaction, InteractionPresentation presentation)
        {
            if (!mapConfirmation.HasPending) return presentation;
            if (!(interaction is IMapConfirmationScope scope) || mapConfirmation.ActionKey != scope.MapConfirmationScope)
            { ClearMapConfirmation(); return presentation; }
            var targets = new List<WorkflowHighlight>();
            var valid = false;
            foreach (var target in presentation.Highlights)
            {
                var pending = target.IsInteractive && mapConfirmation.TargetId == target.TargetKind + ":" + target.TargetId;
                valid |= pending;
                targets.Add(new WorkflowHighlight(target.TargetKind, target.TargetId, target.Semantic,
                    pending ? WorkflowHighlightState.PendingConfirmation : target.State, target.IsInteractive));
            }
            if (!valid) ClearMapConfirmation();
            return new InteractionPresentation(targets, presentation.PromptText, presentation.PanelMode, presentation.ReplacesHighlights);
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

            public bool WasActive { get; set; }

            public string Id { get; private set; }

            public InteractionPriority Priority { get; private set; }
        }
    }
}
